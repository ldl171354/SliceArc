using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SliceArc.Core.SevenZip;
using SliceArc.Core.Volumes;

namespace SliceArc.Core.Extraction;

/// <summary>
/// The productised extraction pipeline: open an archive (single file or volume
/// set), enumerate it, extract a selection, optionally verify without writing,
/// and optionally reclaim each volume the moment the decoder is provably done
/// with it.
///
/// Everything the probe proved out lives here now; the probe only asserts.
///
/// Threading: 7z.dll drives this object synchronously on the thread that calls
/// <see cref="Extract"/>, so the callback state below needs no locking. Callers
/// that want a responsive UI must run Extract on a worker thread and marshal
/// the progress callback themselves.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractionEngine : IArchiveExtractCallback, ICryptoGetTextPassword, IArchiveOpenCallback, IArchiveOpenVolumeCallback, IDisposable
{
    /// <summary>7-Zip's own clients pass a non-NULL search limit here; some handlers are picky.</summary>
    private const long MaxCheckStartPosition = 1L << 23;

    /// <summary>How often the byte-level progress callback is allowed to fire.</summary>
    private const int ProgressIntervalMs = 80;

    /// <summary>
    /// How often a long single-member extraction re-checks volume consumption.
    /// Without this, a 40 GB file inside a 50-volume set would only reclaim disk
    /// at the very end, which defeats the point of the whole product.
    /// </summary>
    private const int SweepIntervalMs = 500;

    private readonly string _inputPath;
    private readonly List<ArchiveEntry> _entries = new();
    private readonly List<object> _liveStreams = new();
    private readonly List<string> _errors = new();
    private readonly List<string> _deletedNames = new();
    private readonly Stopwatch _progressClock = Stopwatch.StartNew();
    private readonly Stopwatch _sweepClock = Stopwatch.StartNew();

    private VolumeAwareStream? _stream;
    private IInArchive? _archive;
    private VolumeGroup? _group;
    private ArchiveFormat? _format;

    // ---- volume-callback state (RAR-style sets only) ----
    //
    // RAR volumes are separate archives, so instead of merging them we hand the
    // decoder volume 1 and answer "give me <name>" from IArchiveOpenVolumeCallback.
    // Each served volume gets its own tracking stream so "delete as it is spent"
    // keeps working for this layout too.
    private readonly List<VolumeAwareStream> _volumeStreams = new();
    private readonly Dictionary<int, VolumeAwareStream> _volumeStreamByIndex = new();
    private readonly Dictionary<string, int> _volumeIndexByName = new(StringComparer.OrdinalIgnoreCase);
    private string _archiveDirectory = string.Empty;
    private bool _volumeCallbackArmed;
    private int _highestVolumeServed = -1;
    private bool _extractionStarted;
    private bool _fullExtractionSucceeded;

    // ---- state that lives only for the duration of one Extract() call ----
    private ExtractionOptions? _options;
    private HashSet<uint>? _selection;
    private bool[] _deletedVolumes = Array.Empty<bool>();
    private Action<ExtractionProgress>? _progress;
    private CancellationToken _ct;
    private string _outDir = string.Empty;
    private Stream? _current;
    private string? _currentPath;
    private ArchiveEntry? _currentEntry;
    private ArchiveEntry? _currentTestEntry;
    private DateTime? _currentTimestamp;
    private bool _currentOwned;
    private int _currentAskMode = -1;
    private bool _currentIsDir;
    private bool _currentEncrypted;
    private long _bytesWritten;
    private long _bytesDone;
    /// <summary>Value of <see cref="_bytesDone"/> when the current member started; the delta is that member's progress.</summary>
    private long _fileByteStart;
    /// <summary>Uncompressed size of the current member, 0 when unknown (directory or skipped).</summary>
    private long _currentFileTotal;
    private long _bytesFreed;
    private long _bytesTotal;
    private int _extracted;
    private int _failed;
    private int _tested;
    private int _skipped;
    private int _directories;
    private int _entriesTotal;
    private string? _lastEntry;
    private string? _failureReason;
    private string? _failureHint;

    /// <summary>
    /// Paths passed via IncludePaths that no entry matched, filled by SelectEntries.
    /// An empty selection that the caller asked for is a user-facing mistake, not a
    /// quiet success.
    /// </summary>
    private List<string>? _unmatchedIncludePaths;
    private bool _cancelled;
    private bool _bombTripped;
    private bool _passwordRequested;
    private bool _passwordAsked;
    private bool _openPhase;
    private bool _wrongPassword;
    private bool _disposed;

    public ExtractionEngine(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        _inputPath = Path.GetFullPath(inputPath);
    }

    /// <summary>Path the engine was constructed with (absolute).</summary>
    public string InputPath => _inputPath;

    /// <summary>The volume set, once <see cref="Open"/> has resolved it.</summary>
    public VolumeGroup? Group => _group;

    /// <summary>
    /// Password for this session. It has to be in place *before* <see cref="Open"/>:
    /// an archive with an encrypted header needs it while 7z.dll is still being
    /// probed for a handler, and at that point there is no
    /// <see cref="ExtractionOptions"/> in play yet. <see cref="Extract"/> adopts
    /// <see cref="ExtractionOptions.Password"/> when this is left null, so callers
    /// that pass the password only to Extract still work.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The handler 7z.dll accepted for this archive.</summary>
    public ArchiveFormat? Format => _format;

    /// <summary>Every member, in archive order. Indices are positions in this list.</summary>
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    public bool IsOpen => _archive is not null && _stream is not null;

    public int VolumeCount => _group?.Volumes.Count ?? 0;

    public bool IsMultiVolume => _group?.IsMultiVolume ?? false;

    /// <summary>Logical size of the whole archive, across all volumes.</summary>
    public long TotalArchiveBytes => _group?.TotalBytes ?? _stream?.TotalBytes ?? 0;

    // =====================================================================
    // Open
    // =====================================================================

    /// <summary>
    /// Resolve the volume set (if any), pick a handler and read the item list.
    /// Safe to call repeatedly.
    /// </summary>
    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen) return;

        _passwordRequested = false;
        _passwordAsked = false;
        _openPhase = true;
        try
        {
            OpenCore();
        }
        finally
        {
            _openPhase = false;
        }
    }

    private void OpenCore()
    {
        var group = VolumeDetector.DetectGroup(_inputPath) ?? SingleFileGroup(_inputPath);

        // A name that ends in three or more digits (a.7z.001, blob.002) declares itself a
        // member of a sliced volume set. If the detector could not find the siblings, the
        // set is incomplete: decoding the lone slice "succeeds" with a truncated entry and
        // no CRC complaint, which is how a user ends up with a corrupt file and a green
        // result. Refuse and ask for the rest of the set.
        if (group.Volumes.Count == 1 && LooksLikeSlicedVolume(group.Volumes[0].Path))
        {
            throw new InvalidDataException(
                $"「{Path.GetFileName(_inputPath)}」看起来是一个分卷（文件名以多位数字结尾），但同目录下找不到它的其余分卷。" +
                "请把整套分卷放在同一个文件夹里再解压；也可以先用 --test 校验。");
        }

        _archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(_inputPath)) ?? Environment.CurrentDirectory;
        IndexVolumesByName(group);

        // Arm the volume callback for everything EXCEPT sliced multi-volume sets.
        // A sliced set is already complete inside one merged stream, so letting
        // the Split/Zip handler also hunt for sibling volumes would change a path
        // that works today for no gain. Every other shape - a RAR set, or a lone
        // .part01.exe whose siblings the detector could not name - needs it.
        _volumeCallbackArmed = !(group.IsMultiVolume && !group.IsIndependentVolumes);

        VolumeAwareStream stream;
        if (group.IsIndependentVolumes)
        {
            // RAR: volume 1 only, and the handler asks for the rest by name.
            // Concatenating these decodes as corrupt data - measured, not assumed.
            stream = OpenVolumeStream(0, group.Volumes[0].Path);
        }
        else
        {
            // Every other volume style - .7z.001 slicing and PKZIP's .z01/.../.zip
            // alike - goes through one merged stream. The handler only ever sees a
            // plain single-stream archive that happens to span several files, and
            // the per-volume accounting keeps working because the merge happens
            // below it.
            stream = new VolumeAwareStream(group);
        }

        // The pointer handed to Open() is typed IArchiveOpenCallback* by 7z.dll,
        // which calls SetTotal/SetCompleted straight off it - no QueryInterface.
        // So it has to be an IArchiveOpenCallback vtable for real: when we passed
        // the ICryptoGetTextPassword pointer instead, the handler's SetTotal ran
        // CryptoGetTextPassword and its SetCompleted read past the end of that
        // one-method interface into garbage, which is an access violation
        // (Fatal error 0x80131506) the moment a handler reports open progress -
        // e.g. while scanning the central directory of a zip with thousands of
        // members. This object still answers the password QI, because it
        // implements ICryptoGetTextPassword too - and now also the volume QI,
        // because it implements IArchiveOpenVolumeCallback.
        var openCallback = Marshal.GetComInterfaceForObject(this, typeof(IArchiveOpenCallback));
        try
        {
            var (archive, format) = OpenArchive(stream, group, openCallback);
            _group = group;
            _stream = stream;
            _archive = archive;
            _format = format;
            ReadEntries();
        }
        catch (Exception ex) when (_passwordAsked)
        {
            // A handler asked us for a password while probing, then every
            // candidate turned the stream down anyway: the header is encrypted
            // and the one we had (if any) did not fit. That is a prompt-and-retry,
            // not a corrupt file.
            stream.Dispose();
            throw new PasswordRequiredException(
                $"「{Path.GetFileName(_inputPath)}」的文件名/文件头已加密，需要口令才能打开。", ex)
            {
                WasSupplied = !string.IsNullOrEmpty(Password),
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        finally
        {
            Marshal.Release(openCallback);
        }
    }

    private static VolumeGroup SingleFileGroup(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到归档文件。", path);
        var length = new FileInfo(path).Length;
        return new VolumeGroup(
            Path.GetFileName(path),
            VolumeStyle.None,
            new[] { new VolumeInfo(path, 0, length, 0) },
            true);
    }

    /// <summary>
    /// True when the file name ends in three or more digits — the naming a sliced
    /// volume set uses (.7z.001, .zip.002, blob.003). A lone file with such a name is
    /// one missing sibling away from a corrupt extraction.
    /// </summary>
    private static bool LooksLikeSlicedVolume(string path)
    {
        var name = Path.GetFileName(path);
        var end = name.Length;
        while (end > 0 && !char.IsDigit(name[end - 1])) end--;
        var digits = 0;
        var scan = end;
        while (scan > 0 && char.IsDigit(name[scan - 1])) { scan--; digits++; }
        return digits >= 3 && (scan == 0 || name[scan - 1] == '.');
    }

    /// <summary>
    /// Try the extension-hinted handler first, then every handler 7z.dll exposes.
    ///
    /// The fallback matters more than it looks: volume styles whose base name has
    /// no extension left (<c>foo.part1.rar</c> -> <c>foo</c>) cannot be hinted, and
    /// mis-named archives exist in the wild.
    /// </summary>
    private static (IInArchive Archive, ArchiveFormat Format) OpenArchive(VolumeAwareStream stream, VolumeGroup group, IntPtr openCallback)
    {
        var candidates = new List<ArchiveFormat>();
        var hint = SevenZipLibrary.FindByExtension(HintName(group));
        if (hint is not null) candidates.Add(hint);
        foreach (var f in SevenZipLibrary.Formats)
        {
            if (!candidates.Contains(f)) candidates.Add(f);
        }

        var maxCheck = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(maxCheck, MaxCheckStartPosition);
        var streamPtr = Marshal.GetComInterfaceForObject(stream, typeof(IInStream));
        try
        {
            foreach (var format in candidates)
            {
                var isHint = ReferenceEquals(format, hint);

                // A rejected handler may still have read the whole stream. That
                // coverage would make every volume look "spent" - and deleting
                // them on that basis would destroy the archive - so the slate is
                // wiped before each attempt.
                stream.ResetCoverage();
                stream.Seek(0, SevenZipNative.SeekOrigin7z.Begin, IntPtr.Zero);

                IInArchive archive;
                try
                {
                    archive = SevenZipLibrary.CreateInArchive(format.Clsid);
                }
                catch (Exception)
                {
                    continue;
                }

                var hr = SevenZipNative.HResult.E_FAIL;
                uint count = 0;
                try
                {
                    hr = archive.Open(streamPtr, maxCheck, openCallback);
                    if (hr == 0) archive.GetNumberOfItems(out count);
                }
                catch (Exception)
                {
                    // Handler refused the stream; try the next one.
                }

                // A hinted handler is trusted outright; anything else must show
                // at least one item before we believe it.
                if (hr == 0 && (isHint || count > 0))
                {
                    return (archive, format);
                }

                try { archive.Close(); } catch { /* handler already gave up */ }
                SevenZipLibrary.ReleaseInArchive(archive);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(maxCheck);
            Marshal.Release(streamPtr);
        }

        throw new InvalidDataException(
            $"无法打开「{Path.GetFileName(group.BaseName)}」：它可能已损坏、不完整，或是本程序不支持的格式。" +
            "如果这是加密压缩包，请用 --password 提供口令后重试；也可以先用 --test 做一次只读校验。");
    }

    /// <summary>Give the handler a name that still carries the real extension.</summary>
    private static string HintName(VolumeGroup group) => group.Style switch
    {
        VolumeStyle.RarNewStyle or VolumeStyle.RarOldStyle => group.BaseName + ".rar",
        VolumeStyle.ZipSplit => group.BaseName + ".zip",
        _ => group.BaseName,
    };

    private void ReadEntries()
    {
        _entries.Clear();
        if (_archive!.GetNumberOfItems(out var count) != 0)
            throw new InvalidDataException("读取压缩包内容失败：文件已损坏或格式不受支持。");

        for (uint i = 0; i < count; i++)
        {
            var path = GetString(i, SevenZipNative.PropId.Path) ?? $"item{i}";
            _entries.Add(new ArchiveEntry(
                i,
                path,
                GetUInt64(i, SevenZipNative.PropId.IsDir) != 0,
                unchecked((long)GetUInt64(i, SevenZipNative.PropId.Size)),
                unchecked((long)GetUInt64(i, SevenZipNative.PropId.PackSize)),
                GetUInt64(i, SevenZipNative.PropId.Solid) != 0,
                GetUInt64(i, SevenZipNative.PropId.Encrypted) != 0,
                unchecked((long)GetUInt64(i, SevenZipNative.PropId.Offset)))
            {
                Modified = ReadFileTime(i, SevenZipNative.PropId.MTime),
                Crc = unchecked((uint)GetUInt64(i, SevenZipNative.PropId.CRC)),
                Attributes = unchecked((uint)GetUInt64(i, SevenZipNative.PropId.Attrib)),
            });
        }
    }

    // =====================================================================
    // Extract / test
    // =====================================================================

    /// <summary>Decode every member and verify its CRC, writing nothing.</summary>
    public ExtractionResult Test(
        IReadOnlyList<string>? includeGlobs = null,
        Action<ExtractionProgress>? progress = null)
        => Extract(
            new ExtractionOptions
            {
                OutputDirectory = Path.GetDirectoryName(_inputPath) ?? _inputPath,
                IncludeGlobs = includeGlobs,
                TestOnly = true,
            },
            progress);

    /// <summary>
    /// Run the extraction. <paramref name="progress"/> fires on the calling
    /// thread, throttled to roughly 12 Hz, plus once per completed member and
    /// once per deleted volume.
    /// </summary>
    public ExtractionResult Extract(ExtractionOptions options, Action<ExtractionProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        Open();

        _options = options;
        _progress = progress;
        _ct = options.CancellationToken;
        _errors.Clear();
        _deletedNames.Clear();
        _liveStreams.Clear();
        _passwordRequested = false;
        _wrongPassword = false;
        Password ??= options.Password;
        _deletedVolumes = new bool[_group!.Volumes.Count];
        _bytesWritten = _bytesDone = _bytesFreed = _bytesTotal = 0;
        _fileByteStart = 0;
        _currentFileTotal = 0;
        _extracted = _failed = _tested = _skipped = _directories = 0;
        _cancelled = _bombTripped = false;
        _current = null;
        _currentPath = null;
        _currentEntry = null;
        _currentTestEntry = null;
        _currentTimestamp = null;
        _currentOwned = false;
        _currentAskMode = -1;
        _currentIsDir = false;
        _lastEntry = null;
        _failureReason = null;
        _failureHint = null;

        _outDir = Path.GetFullPath(options.OutputDirectory);
        // With an OutputFactory nothing is written to disk, so the directory is
        // not touched either - that is what keeps a drag-out temp-file free.
        if (!options.TestOnly && options.OutputFactory is null) Directory.CreateDirectory(_outDir);

        // A gapped sliced set (.7z.001 + .003 + .004) merges into a stream with a hole.
        // The decoder either fails late with a confusing code or - measured on this exact
        // shape - writes nothing and reports success. Neither is acceptable: say which
        // volumes are missing and stop before anything is written. The user's fix is to
        // supply the missing volumes, so the message points there.
        // (RAR sets are exempt: the handler fetches volumes by name and already fails
        // loudly on a hole.)
        if (_group.IsMultiVolume && !_group.IsComplete && !_group.IsIndependentVolumes)
        {
            var (have, missing) = DescribeVolumeGaps(_group);
            RecordFailure(
                "卷册不完整，缺少分卷",
                $"这个压缩包按序号应是连续的分卷，现在只有第 {have} 卷，缺第 {missing} 卷。" +
                "请补齐缺失的分卷后重试；若分卷本身已损坏，请重新获取。");
            _errors.Add($"卷册不完整：现有第 {have} 卷，缺第 {missing} 卷。");
            return BuildResult();
        }

        var selected = SelectEntries(options);

        // Paths the caller explicitly asked for but the archive does not contain must be
        // reported: an empty selection with no word of it is how "the preview showed
        // nothing" and "the drag copied zero files" both looked like successes.
        if (_unmatchedIncludePaths is { Count: > 0 } && selected.Count == 0)
        {
            var shown = string.Join(", ", _unmatchedIncludePaths.Take(3));
            RecordFailure(
                "压缩包里没有这些条目",
                $"要解压的条目（{shown}{(_unmatchedIncludePaths.Count > 3 ? " 等" : "")}）在压缩包里找不到。" +
                "可能是路径大小写或分隔符不一致，或文件在子文件夹里。请在列表里确认后重试。");
            _errors.Add($"未找到条目：{shown}");
            return BuildResult();
        }

        if (selected.Count == 0)
        {
            return BuildResult();
        }

        // Arm tracking for the extraction phase. Sliced sets have one merged
        // stream; a RAR set has one per volume, and they were all opened while the
        // handler enumerated the archive.
        if (_group!.IsIndependentVolumes)
        {
            foreach (var volume in _volumeStreams) volume.BeginExtractionTracking();
        }
        else
        {
            _stream!.BeginExtractionTracking();
        }

        _extractionStarted = true;
        _progressClock.Restart();
        _sweepClock.Restart();
        PushProgress();

        var indices = selected.ToArray();
        var callbackPtr = Marshal.GetComInterfaceForObject(this, typeof(IArchiveExtractCallback));
        int hr;
        try
        {
            hr = _archive!.Extract(indices, (uint)indices.Length, options.TestOnly ? 1 : 0, callbackPtr);
        }
        catch
        {
            // A decoder exception must not also cost the space that was already
            // consumed. For RAR sets the final sweep is the ONLY reclaim point, so
            // skipping it on the way out would leave every volume on disk. The
            // coverage rule - not the "whole run succeeded" shortcut - decides
            // here, so a half-read archive keeps everything it may still need.
            _fullExtractionSucceeded = false;
            SweepSpentVolumes(final: true);
            throw;
        }
        finally
        {
            Marshal.Release(callbackPtr);
        }

        CloseCurrentFile(SevenZipNative.OperationResult.Ok);

        // "Every member was asked for and none failed" is what makes the final
        // sweep safe to treat the whole set as spent, including volumes the
        // decoder never read to the end. A partial selection must not: the
        // volumes it skipped still hold data the user has not got yet.
        _fullExtractionSucceeded = selected.Count == _entries.Count
                                   && _failed == 0
                                   && _errors.Count == 0
                                   && !_cancelled;

        SweepSpentVolumes(final: true);
        PushProgress();

        if (_bombTripped)
        {
            RecordFailure("解压体积超过安全上限",
                "压缩包解压后的体积过大，已自动中止以免占满磁盘。若确认内容可信，请分次解压。");
            _errors.Add($"解压体积超过上限 {options.MaxTotalBytes:N0} B，已中止（zip 炸弹防护）。");
        }
        else if (_passwordRequested && string.IsNullOrEmpty(Password))
        {
            // The payload is encrypted and no password was given, so our callback
            // aborted the run. That is a prompt-and-retry, not a corrupt archive.
            RecordFailure("压缩包已加密，需要口令", "请输入正确的口令后重试。");
            _errors.Add($"「{Path.GetFileName(_inputPath)}」已加密，需要口令才能解压。");
        }
        else if (!_cancelled && hr != SevenZipNative.HResult.S_OK)
        {
            RecordFailure("解压中断",
                "压缩包可能已损坏或不完整，也可能是本程序不支持的格式；请确认文件来源可靠后重试。");
            _errors.Add("解压中断：压缩包可能已损坏、不完整，或格式不受支持。");
        }

        return BuildResult();
    }

    /// <summary>Which members this run touches, in ascending index order.</summary>
    private List<uint> SelectEntries(ExtractionOptions options)
    {
        var selected = new List<uint>();
        _bytesTotal = 0;
        _entriesTotal = 0;

        var globs = options.IncludeGlobs?.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        var requested = options.IncludePaths is { Count: > 0 }
            ? options.IncludePaths.Select(p => p.Replace('\\', '/')).ToList()
            : null;
        var paths = requested is not null
            ? new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase)
            : null;
        var filtered = paths is not null || globs is { Length: > 0 };

        foreach (var entry in _entries)
        {
            var take = !filtered
                || (paths is not null ? paths.Contains(entry.NormalizedPath) : Matches(entry, globs!));
            if (!take) continue;

            selected.Add(entry.Index);
            if (entry.IsDirectory) continue;
            _bytesTotal += entry.Size;
            _entriesTotal++;
            if (paths is not null) paths.Remove(entry.NormalizedPath);   // matched
        }

        // What the caller asked for and the archive does not have. Reported by the
        // caller-visible failure path instead of dissolving into a silent empty run.
        _unmatchedIncludePaths = paths is { Count: > 0 } ? paths.ToList() : null;

        _selection = new HashSet<uint>(selected);
        return selected;
    }

    private static bool Matches(ArchiveEntry entry, string[] globs)
    {
        foreach (var glob in globs)
        {
            if (GlobMatcher.IsMatch(glob, entry.Name)) return true;
            if (GlobMatcher.IsMatch(glob, entry.NormalizedPath)) return true;
        }
        return false;
    }

    private ExtractionResult BuildResult() => new(
        _extracted,
        _failed,
        _bytesWritten,
        _bytesFreed,
        _deletedNames.ToArray(),
        _errors.ToArray())
    {
        Tested = _tested,
        Skipped = _skipped,
        Directories = _directories,
        Cancelled = _cancelled,
        FailureReason = _failureReason,
        FailureHint = _failureHint,
        NeedsPassword = _passwordRequested,
        WrongPassword = _wrongPassword,
    };

    /// <summary>
    /// Remember the cause of the first failure so the result can lead with it.
    /// Later failures are only appended to the list - the first one is almost
    /// always the one worth acting on, and a 500-file CRC cascade would
    /// otherwise bury it.
    /// </summary>
    private void RecordFailure(int opRes)
    {
        // The 7z and zip handlers report DataError - not WrongPassword - when the
        // AES key is wrong, so a password has to be inferred from "an encrypted
        // member failed and we did supply one". 7z.exe prints "Wrong password?"
        // in exactly this situation.
        var badPassword = SevenZipNative.OperationResult.ImpliesPassword(opRes)
            || (!string.IsNullOrEmpty(Password) && _currentEncrypted
                && (opRes == SevenZipNative.OperationResult.DataError
                    || opRes == SevenZipNative.OperationResult.CrcError));
        if (badPassword) _wrongPassword = true;
        if (_failureReason is not null) return;
        if (badPassword)
        {
            // "Needs a password" and "the password was wrong" are different problems for
            // the user, so they must not share one sentence. Which one it is comes from
            // whether we actually sent a password down to the handler.
            var supplied = !string.IsNullOrEmpty(Password);
            _failureReason = supplied
                ? "口令不正确"
                : SevenZipNative.OperationResult.Describe(SevenZipNative.OperationResult.WrongPassword);
            _failureHint = supplied
                ? "该压缩包已加密，刚才的口令不对。请重新输入口令后重试。"
                : "该压缩包已加密，需要一个正确的口令。请输入口令后重试。";
            return;
        }
        // A file that is not an archive at all (a text file renamed .zip, a half-finished
        // download) reaches 7z.dll as garbage and comes back as "unexpected end of data",
        // which sends the user hunting for volumes that never existed. Sniff the header
        // and say what it actually is.
        if ((opRes is SevenZipNative.OperationResult.IsNotArc
                   or SevenZipNative.OperationResult.UnexpectedEnd
                   or SevenZipNative.OperationResult.HeadersError)
            && ArchiveSignature.Sniff(_inputPath) == ArchiveSignature.Kind.PlainData)
        {
            _failureReason = "这不是压缩包，或格式不受支持";
            _failureHint = "文件内容不像任何已知的压缩包格式（可能被改名、下载不完整，或本身就不是压缩包）。请核对来源后重新获取。";
            return;
        }
        _failureReason = SevenZipNative.OperationResult.Describe(opRes);
        _failureHint = SevenZipNative.OperationResult.Hint(opRes);
    }

    /// <summary>
    /// Which volume ordinals are present and which are missing, for the message.
    /// Reads the trailing number of each volume file name, so it works for .7z.001,
    /// .zip.001 and .part01.exe alike. RAR old-style first volume (name.rar) has no
    /// number and is simply reported as present without one.
    /// </summary>
    private static (string Have, string Missing) DescribeVolumeGaps(VolumeGroup group)
    {
        var nums = new List<int>();
        foreach (var v in group.Volumes)
        {
            var name = Path.GetFileName(v.Path);
            var end = name.Length;
            while (end > 0 && !char.IsDigit(name[end - 1])) end--;
            var begin = end;
            while (begin > 0 && char.IsDigit(name[begin - 1])) begin--;
            if (begin < end && int.TryParse(name[begin..end], out var n)) nums.Add(n);
        }
        if (nums.Count == 0) return ("?", "?");
        nums.Sort();
        var have = string.Join(", ", nums);
        var missing = Enumerable.Range(nums[0], nums[^1] - nums[0] + 1).Except(nums).ToList();
        return (have, missing.Count > 0 ? string.Join(", ", missing) : "无");
    }

    /// <summary>
    /// What to print next to a file name when its entry failed. Once a wrong password is
    /// established the raw code is actively misleading - DataError reads "数据损坏", but
    /// the bytes are fine, we simply cannot decrypt them.
    /// </summary>
    private string DescribeForUser(int opRes) =>
        _wrongPassword ? "口令不正确" : SevenZipNative.OperationResult.Describe(opRes);

    /// <summary>Same as <see cref="RecordFailure(int)"/> for failures that never
    /// reached a 7-Zip operation result (I/O, permissions, path safety).</summary>
    private void RecordFailure(string reason, string hint)
    {
        if (_failureReason is not null) return;
        _failureReason = reason;
        _failureHint = hint;
    }

    // =====================================================================
    // ICryptoGetTextPassword
    // =====================================================================

    /// <summary>
    /// The 7z and zip handlers QueryInterface the callback they were handed -
    /// the extract callback while decoding, and the open callback while reading
    /// an encrypted header - for this interface.
    ///
    /// While <em>probing</em> (<see cref="_openPhase"/>) an empty password is
    /// answered with S_OK: the ZIP handler asks unconditionally, and answering
    /// E_ABORT there makes it reject a perfectly ordinary archive. An encrypted
    /// header still fails to decrypt on its own, which is what lets the open
    /// path tell "wrong/absent password" from "not this format".
    ///
    /// While <em>decoding</em> a missing password answers E_ABORT so the handler
    /// reports WrongPassword instead of writing garbage, and it is also our
    /// signal (see <see cref="_passwordRequested"/>) that the caller should be
    /// prompted.
    /// </summary>
    public int CryptoGetTextPassword(out IntPtr password)
    {
        password = IntPtr.Zero;
        _passwordAsked = true;

        var value = Password;
        if (string.IsNullOrEmpty(value))
        {
            if (!_openPhase)
            {
                _passwordRequested = true;
                return SevenZipNative.HResult.E_ABORT;
            }
            password = Marshal.StringToBSTR(string.Empty);
            return SevenZipNative.HResult.S_OK;
        }

        password = Marshal.StringToBSTR(value);
        return SevenZipNative.HResult.S_OK;
    }

    // =====================================================================
    // IArchiveOpenCallback
    // =====================================================================

    /// <summary>
    /// 7-Zip calls these straight off the pointer it was handed in Open(), with
    /// no QueryInterface, which is why they are explicit: the names collide with
    /// the IArchiveExtractCallback slots above but the signatures (two pointers,
    /// not one) are what 7-Zip actually emits. Answering S_OK keeps an archive
    /// with a large central directory openable; the progress numbers are not
    /// needed here, so they are ignored, and E_ABORT is reserved for a
    /// cancellation that arrives while the handler is still probing.
    /// </summary>
    int IArchiveOpenCallback.SetTotal(IntPtr files, IntPtr bytes) => SevenZipNative.HResult.S_OK;

    int IArchiveOpenCallback.SetCompleted(IntPtr files, IntPtr bytes) =>
        IsCancelled ? SevenZipNative.HResult.E_ABORT : SevenZipNative.HResult.S_OK;

    // =====================================================================
    // IArchiveOpenVolumeCallback - how a RAR set gets stitched together
    // =====================================================================

    /// <summary>
    /// Answers <c>kpidName</c> with the archive's FILE NAME (extension included).
    ///
    /// The handler feeds that string to <c>CVolumeName::InitName</c>, which strips
    /// the trailing digits and re-increments them, mapping .rar -> .rar and
    /// .exe -> .rar. So <c>Build.part01.exe</c> yields <c>Build.part02.rar</c>,
    /// which is exactly how a self-extracting RAR set is named on disk.
    /// </summary>
    int IArchiveOpenVolumeCallback.GetProperty(uint propId, IntPtr value)
    {
        if (value == IntPtr.Zero) return SevenZipNative.HResult.E_INVALIDARG;
        if (propId != SevenZipNative.PropId.Name) return SevenZipNative.HResult.S_FALSE;

        // The BSTR is owned by the caller's PROPVARIANT from here on; 7-Zip frees
        // it with PropVariantClear.
        var bstr = Marshal.StringToBSTR(Path.GetFileName(_inputPath));
        Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.Bstr);
        Marshal.WriteIntPtr(value, 8, bstr);
        return SevenZipNative.HResult.S_OK;
    }

    /// <summary>
    /// Opens the volume the handler asked for and hands back a raw IInStream.
    /// Returns S_FALSE (with a NULL stream) when the name does not exist - that is
    /// how the handler learns the set has ended.
    /// </summary>
    int IArchiveOpenVolumeCallback.GetStream(string name, IntPtr outStream)
    {
        if (outStream != IntPtr.Zero) Marshal.WriteIntPtr(outStream, IntPtr.Zero);
        if (!_volumeCallbackArmed || string.IsNullOrEmpty(name)) return SevenZipNative.HResult.S_FALSE;

        // 7-Zip builds a bare file name here (its own client prepends the folder),
        // so resolve it against the archive's directory.
        var path = Path.IsPathRooted(name) ? name : Path.Combine(_archiveDirectory, name);
        if (!File.Exists(path)) return SevenZipNative.HResult.S_FALSE;

        var volumeIndex = IndexOfVolume(path);
        var volume = _volumeStreamByIndex.TryGetValue(volumeIndex, out var known)
            ? known
            : OpenVolumeStream(volumeIndex, path);

        if (_extractionStarted) volume.BeginExtractionTracking();

        var pointer = Marshal.GetComInterfaceForObject(volume, typeof(IInStream));
        if (outStream != IntPtr.Zero) Marshal.WriteIntPtr(outStream, pointer);
        return SevenZipNative.HResult.S_OK;
    }

    /// <summary>Map every known volume file name to its index, for the name lookup above.</summary>
    private void IndexVolumesByName(VolumeGroup group)
    {
        _volumeIndexByName.Clear();
        for (var i = 0; i < group.Volumes.Count; i++)
        {
            var fileName = Path.GetFileName(group.Volumes[i].Path);
            if (!string.IsNullOrEmpty(fileName)) _volumeIndexByName[fileName] = i;
        }
    }

    private int IndexOfVolume(string path)
    {
        var fileName = Path.GetFileName(path);
        return _volumeIndexByName.TryGetValue(fileName, out var index) ? index : -1;
    }

    /// <summary>
    /// Wrap one physical volume in its own tracking stream. <paramref name="index"/>
    /// is the position in the detected set, or -1 for a sibling the detector never
    /// named (served, but never auto-deleted).
    /// </summary>
    private VolumeAwareStream OpenVolumeStream(int index, string path)
    {
        if (index >= 0 && _volumeStreamByIndex.TryGetValue(index, out var existing)) return existing;

        var length = new FileInfo(path).Length;
        var group = new VolumeGroup(
            Path.GetFileName(path), VolumeStyle.None, new[] { new VolumeInfo(path, 0, length, 0) }, true);
        var stream = new VolumeAwareStream(group);

        _volumeStreams.Add(stream);
        if (index >= 0)
        {
            _volumeStreamByIndex[index] = stream;
            if (index > _highestVolumeServed) _highestVolumeServed = index;
        }

        return stream;
    }

    private bool IsDeletionSafeFor(int index)
    {
        if (!_group!.IsIndependentVolumes) return _stream!.IsDeletionSafe;
        return _volumeStreamByIndex.TryGetValue(index, out var volume) && volume.IsDeletionSafe;
    }

    /// <summary>
    /// Per-volume reclamation state, for tests and diagnostics. "Why was this
    /// volume not deleted" is otherwise invisible from outside the engine.
    /// </summary>
    public IReadOnlyList<VolumeReclaimState> VolumeStates()
    {
        var states = new List<VolumeReclaimState>();
        if (_group is null) return states;

        for (var i = 0; i < _group.Volumes.Count; i++)
        {
            var volume = _group.Volumes[i];
            var served = _volumeStreamByIndex.ContainsKey(i);
            var coveredEnd = _group.IsIndependentVolumes
                ? (_volumeStreamByIndex.TryGetValue(i, out var own) ? own.CoveredEnd : 0)
                : _stream?.CoveredEnd ?? 0;
            var deletionSafe = _group.IsIndependentVolumes
                ? (_volumeStreamByIndex.TryGetValue(i, out var ownStream) && ownStream.IsDeletionSafe)
                : _stream?.IsDeletionSafe ?? false;

            states.Add(new VolumeReclaimState(
                i, volume.FileName, volume.Length, served, IsVolumeSpent(i), deletionSafe, coveredEnd));
        }

        return states;
    }

    /// <summary>
    /// Per-volume deletion judgement. Sliced sets ask the merged stream; RAR sets
    /// ask the volume's own stream, and additionally require that the handler has
    /// already moved on to a later volume - once it has, it never comes back.
    /// </summary>
    private bool IsVolumeSpent(int index)
    {
        if (!_group!.IsIndependentVolumes) return _stream!.IsVolumeSpent(index);
        if (!_volumeStreamByIndex.TryGetValue(index, out var volume)) return false;
        if (index >= _highestVolumeServed) return false;

        // Coverage, not byte-exactness. A RAR volume ends with a structural
        // end-of-archive record that the decoder never bothers to read (measured:
        // it stops ~76 bytes short), so demanding [0, length) would mean a RAR set
        // never reclaims anything. The data region is fully read by then, and the
        // "handler already moved past this volume" guard above covers the rest.
        var length = _group.Volumes[index].Length;
        return volume.CoveredEnd >= length - IndependentVolumeTailSlack;
    }

    /// <summary>
    /// Structural tail a RAR volume may legitimately stay unread. Well above the
    /// real record size (tens of bytes) and far below any data region, so it can
    /// only ever absorb layout metadata.
    /// </summary>
    private const long IndependentVolumeTailSlack = 4096;

    private void ReleaseVolumeHandle(int index)
    {
        if (!_group!.IsIndependentVolumes)
        {
            _stream!.ReleaseVolumeHandle(index);
            return;
        }

        if (_volumeStreamByIndex.TryGetValue(index, out var volume)) volume.ReleaseVolumeHandle(0);
    }

    // =====================================================================
    // IArchiveExtractCallback
    // =====================================================================

    /// <inheritdoc />
    public int SetTotal(ulong total) => SevenZipNative.HResult.S_OK;

    /// <inheritdoc />
    public int SetCompleted(IntPtr completeValue)
    {
        if (IsCancelled) return SevenZipNative.HResult.E_ABORT;

        // In test mode nothing reaches the output sink, so the decoder's own
        // counter is the only progress signal available. In extract mode the
        // sink is authoritative: 7z.dll's "completed" value is per decoding
        // block and can run past the member sizes we are reporting against.
        if (_options?.TestOnly == true && completeValue != IntPtr.Zero)
        {
            var done = Marshal.ReadInt64(completeValue);
            if (done > _bytesDone) _bytesDone = done;
        }

        if (_progressClock.ElapsedMilliseconds >= ProgressIntervalMs)
        {
            _progressClock.Restart();
            PushProgress();
        }

        if (_options is { DeleteSpentVolumes: true } && _sweepClock.ElapsedMilliseconds >= SweepIntervalMs)
        {
            _sweepClock.Restart();
            SweepSpentVolumes();
        }

        return SevenZipNative.HResult.S_OK;
    }

    /// <inheritdoc />
    public int GetStream(uint index, IntPtr outStream, int askExtractMode)
    {
        if (outStream != IntPtr.Zero) Marshal.WriteIntPtr(outStream, IntPtr.Zero);

        if (IsCancelled) return SevenZipNative.HResult.E_ABORT;

        _current = null;
        _currentPath = null;
        _currentEntry = null;
        _currentTimestamp = null;
        _currentOwned = false;
        _currentIsDir = false;
        _currentEncrypted = false;

        // A member starts here: remember where the byte counter stood so the
        // per-file progress bar can be derived from the running total, and drop
        // any size carried over from the previous member.
        _fileByteStart = _bytesDone;
        _currentFileTotal = 0;

        // kSkip means "not in the requested set"; we also answer NULL for items
        // we choose not to write, which the handler turns into kSkip for us.
        if (askExtractMode == SevenZipNative.AskMode.Skip) return SevenZipNative.HResult.S_OK;
        if (_selection is not null && !_selection.Contains(index)) return SevenZipNative.HResult.S_OK;
        if (index >= _entries.Count) return SevenZipNative.HResult.S_OK;

        var entry = _entries[(int)index];
        _lastEntry = entry.NormalizedPath;
        _currentEncrypted = entry.IsEncrypted;
        if (!entry.IsDirectory) _currentFileTotal = entry.Size;

        // Verification pass: decode and CRC, but touch no filesystem at all.
        if (_options?.TestOnly == true || askExtractMode == SevenZipNative.AskMode.Test)
        {
            _currentTestEntry = entry;
            return SevenZipNative.HResult.S_OK;
        }

        if (entry.IsDirectory)
        {
            if (_options?.OutputFactory is not null) return SevenZipNative.HResult.S_OK;
            var dirTarget = ResolveSafe(_outDir, entry.Path);
            if (dirTarget is null)
            {
                _failed++;
                RecordFailure("压缩包内含危险路径",
                    "压缩包里有指向目标文件夹之外的路径，已拒绝写入。该文件可能被刻意构造，请谨慎处理。");
                _errors.Add($"拒绝越界路径：{entry.Path}");
                return SevenZipNative.HResult.S_OK;
            }

            try
            {
                Directory.CreateDirectory(dirTarget);
                _currentIsDir = true;
            }
            catch (Exception ex)
            {
                _failed++;
                RecordFailure("无法创建文件夹",
                    "目标文件夹无法创建：可能没有写入权限，或路径已被同名文件占用、磁盘已满。");
                _errors.Add($"创建目录 {entry.NormalizedPath} 失败: {ex.Message}");
            }
            return SevenZipNative.HResult.S_OK;
        }

        // Streamed destination (drag-out): no path resolution, no overwrite
        // policy, no partial file to clean up - just hand 7z.dll a sink.
        if (_options?.OutputFactory is { } factory)
        {
            _currentEntry = entry;
            try
            {
                var destination = factory(entry);
                _current = destination;
                _currentOwned = false;
                var factorySink = new ExtractionOutputSink(destination, AcceptBytes);
                _liveStreams.Add(factorySink);
                var factoryPtr = Marshal.GetComInterfaceForObject(factorySink, typeof(ISequentialOutStream));
                if (outStream != IntPtr.Zero) Marshal.WriteIntPtr(outStream, factoryPtr);
            }
            catch (Exception ex)
            {
                _failed++;
                RecordFailure("无法写出数据",
                    "数据无法写出到目标位置：可能没有写入权限、磁盘空间不足，或连接已中断。");
                _errors.Add($"{entry.NormalizedPath}: {ex.Message}");
                _current = null;
                _currentEntry = null;
            }
            return SevenZipNative.HResult.S_OK;
        }

        var target = ResolveSafe(_outDir, entry.Path);
        if (target is null)
        {
            _failed++;
            RecordFailure("压缩包内含危险路径",
                "压缩包里有指向目标文件夹之外的路径，已拒绝写入。该文件可能被刻意构造，请谨慎处理。");
            _errors.Add($"拒绝越界路径：{entry.Path}");
            return SevenZipNative.HResult.S_OK;
        }

        try
        {
            if (!_options!.Overwrite && File.Exists(target))
            {
                _skipped++;
                return SevenZipNative.HResult.S_OK;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // 1 MB buffer: the default 4 KB made every decoded chunk a chain of
            // small writes, and 64 KB still cost one syscall per chunk. A buffer
            // larger than the decoder's output chunks lets FileStream coalesce
            // them into full-size writes.
            _current = File.Create(target, 1 << 20, FileOptions.SequentialScan);
            _currentPath = target;
            _currentEntry = entry;
            _currentTimestamp = entry.Modified;
            _currentOwned = true;

            // The CCW's only other reference lives on the native side, so the
            // managed sink must be rooted here until the item is finished.
            var sink = new ExtractionOutputSink(_current, AcceptBytes);
            _liveStreams.Add(sink);
            var ptr = Marshal.GetComInterfaceForObject(sink, typeof(ISequentialOutStream));
            if (outStream != IntPtr.Zero) Marshal.WriteIntPtr(outStream, ptr);
        }
        catch (Exception ex)
        {
            _failed++;
            RecordFailure("无法写入文件",
                "目标文件写不进去：可能正被其他程序占用，或没有写入权限；也可能是磁盘空间不足。");
            _errors.Add($"{entry.NormalizedPath}: {ex.Message}");
            _current?.Dispose();
            _current = null;
            _currentPath = null;
            _currentEntry = null;
            _currentOwned = false;
        }

        return SevenZipNative.HResult.S_OK;
    }

    /// <inheritdoc />
    public int PrepareOperation(int askExtractMode)
    {
        _currentAskMode = askExtractMode;
        return SevenZipNative.HResult.S_OK;
    }

    /// <inheritdoc />
    public int SetOperationResult(int opRes)
    {
        var mode = _currentAskMode;
        var wroteFile = _current is not null;
        var testEntry = _currentTestEntry;
        _currentTestEntry = null;
        CloseCurrentFile(opRes);

        if (wroteFile)
        {
            if (opRes == SevenZipNative.OperationResult.Ok) _extracted++;
            else
            {
                _failed++;
                RecordFailure(opRes);
                _errors.Add($"{_lastEntry ?? "未知条目"}: {DescribeForUser(opRes)}");
            }
        }
        else if (mode == SevenZipNative.AskMode.Test)
        {
            var passed = opRes == SevenZipNative.OperationResult.Ok;
            if (passed) _tested++;
            else
            {
                _failed++;
                RecordFailure(opRes);
                _errors.Add($"{_lastEntry ?? "未知条目"}: {DescribeForUser(opRes)}");
            }

            if (testEntry is { IsDirectory: false })
                _options?.OnMemberVerified?.Invoke(testEntry, passed);
        }
        else if (_currentIsDir && opRes == SevenZipNative.OperationResult.Ok)
        {
            _directories++;
        }
        else if (opRes != SevenZipNative.OperationResult.Ok)
        {
            // The decoder flagged something even though we wrote nothing.
            _failed++;
            RecordFailure(opRes);
            _errors.Add($"{_lastEntry ?? "未知条目"}: {SevenZipNative.OperationResult.Describe(opRes)}");
        }

        _currentIsDir = false;
        _currentAskMode = -1;

        if (_options is { DeleteSpentVolumes: true }) SweepSpentVolumes();
        PushProgressThrottled();

        return IsCancelled ? SevenZipNative.HResult.E_ABORT : SevenZipNative.HResult.S_OK;
    }

    /// <summary>
    /// Push progress no more often than <see cref="ProgressIntervalMs"/>.
    ///
    /// Every member used to push unconditionally on top of the 12 Hz byte-level
    /// pushes, so a many-member archive produced ~40 updates/s. Each one costs a
    /// layout+render of the progress dialog on the UI thread, which competes with
    /// 7z.dll's decoder for cores; the final state is pushed unconditionally by
    /// <see cref="Extract"/> once the run is over, so nothing is lost.
    /// </summary>
    private void PushProgressThrottled()
    {
        if (_progressClock.ElapsedMilliseconds < ProgressIntervalMs) return;
        _progressClock.Restart();
        PushProgress();
    }

    // =====================================================================
    // Output accounting
    // =====================================================================

    /// <summary>Called by the output sink for every chunk 7z.dll hands us.</summary>
    private bool AcceptBytes(int count)
    {
        _bytesWritten += count;
        _bytesDone += count;

        var limit = _options?.MaxTotalBytes ?? 0;
        if (limit > 0 && _bytesWritten > limit)
        {
            _bombTripped = true;
            return false;
        }
        return true;
    }

    private void CloseCurrentFile(int opRes)
    {
        var stream = _current;
        var path = _currentPath;
        var entry = _currentEntry;
        var stamp = _currentTimestamp;
        var owned = _currentOwned;

        _current = null;
        _currentPath = null;
        _currentEntry = null;
        _currentTimestamp = null;
        _currentOwned = false;

        if (stream is null) return;

        try
        {
            stream.Dispose();
        }
        catch (Exception ex)
        {
            RecordFailure("无法关闭输出文件",
                "写入的数据未能完整保存：磁盘可能已满，或设备已断开连接。");
            _errors.Add($"关闭 {NameOf(entry, path)} 失败: {ex.Message}");
        }

        if (opRes != SevenZipNative.OperationResult.Ok)
        {
            // The failure itself is reported by SetOperationResult; reporting it
            // again here would put the same line in the list twice.
            // Only a file we created ourselves is worth deleting; a streamed
            // destination (drag-out) owns nothing on disk.
            if (owned) TryDelete(path);
            return;
        }

        if (owned && stamp is { } time && path is not null)
        {
            try { File.SetLastWriteTime(path, time); } catch { /* cosmetic only */ }
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* keep the partial file */ }
    }

    private static string NameOf(ArchiveEntry? entry, string? path) =>
        entry?.NormalizedPath
        ?? (string.IsNullOrEmpty(path) ? "(unknown)" : Path.GetFileName(path));

    // =====================================================================
    // Volume reclamation
    // =====================================================================

    /// <summary>
    /// Delete every volume the decoder is provably finished with.
    ///
    /// The judgement itself lives in <see cref="VolumeAwareStream.IsVolumeSpent"/>
    /// (a merged coverage range from offset 0), not here: this method only acts
    /// on it, honours the keep-last fuse, and closes the OS handle first - on
    /// Windows the space is not reclaimed while a handle is still open.
    ///
    /// Mid-flight sweeps (<paramref name="final"/> = false) additionally require
    /// the volume's stream to have seen no backward seeks, so a handler that
    /// still revisits earlier bytes keeps its volume. The final sweep runs after
    /// Extract() has returned - the decoder issues no further volume reads at
    /// that point - so full coverage alone is sufficient (this is what lets a
    /// ZipSplit .z01 go: the ZIP handler back-seeks into it once for the local
    /// header, then never returns).
    /// </summary>
    private void SweepSpentVolumes(bool final = false)
    {
        if (_options is not { DeleteSpentVolumes: true }) return;
        if (!_group!.IsComplete) return;

        // Independently-archived volumes (RAR) are reclaimed only once the
        // decoder is finished with the whole set.
        //
        // It keeps one stream per volume and revisits them - each member's header
        // lives wherever it was written, and the set has a trailer - so a volume
        // deleted mid-flight can be read again. That read fails, 7z.dll reports
        // the archive as truncated, and the run ends in "已损坏、不完整" even
        // though every member was written correctly; the abort then leaves the
        // volumes it had not reached yet on disk. Sliced sets carry no such risk:
        // they are a single strictly-forward merged stream, which is what the
        // merge below exists for.
        if (!final && _group.IsIndependentVolumes) return;

        var keepLast = Math.Max(0, _options.KeepLastVolumes);
        var limit = _group.Volumes.Count - keepLast;

        // After a fully successful run nothing will read the archive again, so
        // every volume of a fully-extracted set is spent - including the ones the
        // decoder never read to the very end. That is the normal case: it stops
        // ~76 bytes short of each RAR volume's trailer, and a volume holding only
        // the archive terminator is never read at all. Requiring coverage there
        // would mean "delete as consumed" silently cleans only part of the set.
        var allSpent = final && _fullExtractionSucceeded;

        for (var i = 0; i < limit; i++)
        {
            if (_deletedVolumes[i]) continue;

            // Two layouts, one question. Sliced sets ask the merged stream, which
            // tracks every volume; RAR sets ask each volume's own stream and only
            // once the handler has moved on to a later one.
            if (!allSpent)
            {
                if (!IsVolumeSpent(i)) continue;
                if (!final && !IsDeletionSafeFor(i)) continue;
            }

            var volume = _group.Volumes[i];
            try
            {
                ReleaseVolumeHandle(i);
                var size = new FileInfo(volume.Path).Length;
                File.Delete(volume.Path);
                _deletedVolumes[i] = true;
                _bytesFreed += size;
                _deletedNames.Add(volume.FileName);
                _progressClock.Restart();
                PushProgress();
            }
            catch (Exception ex)
            {
                // A volume that refuses to go is not a decoding failure: every
                // member extracted fine and the archive is simply still there.
                // Recording it as the run's failure made the completion dialog
                // lead with archive-corruption advice, which sent the user
                // looking for a problem that did not exist.
                _errors.Add($"分卷 {volume.FileName} 未能删除（可能正被占用）：{ex.Message}；解压结果不受影响，可稍后手动删除。");
            }
        }
    }

    // =====================================================================
    // Progress
    // =====================================================================

    private void PushProgress()
    {
        if (_progress is null || _stream is null) return;

        var done = _options?.TestOnly == true ? _tested + _failed : _extracted + _failed;

        // A merged stream knows exactly which volume the decoder is inside. For a
        // RAR set the volumes are separate streams, so report how far through the
        // set the handler has asked us to go.
        var currentVolume = _group!.IsIndependentVolumes
            ? Math.Max(1, _highestVolumeServed + 1)
            : _stream.Group.VolumeIndexAt(_stream.Position) + 1;

        _progress(new ExtractionProgress(
            done,
            _entriesTotal,
            _bytesDone,
            _bytesTotal,
            currentVolume,
            _group.Volumes.Count,
            _bytesFreed,
            _deletedNames.Count,
            _lastEntry)
        {
            CurrentFileBytes = Math.Max(0, _bytesDone - _fileByteStart),
            CurrentFileTotal = _currentFileTotal,
        });
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private bool IsCancelled
    {
        get
        {
            if (_cancelled) return true;
            if (!_ct.IsCancellationRequested) return false;
            _cancelled = true;
            return true;
        }
    }

    /// <summary>
    /// Join an archive member path onto the output directory and prove the
    /// result cannot escape it. Rejects rooted names, drive/ADS colons and
    /// anything that resolves above the root.
    /// </summary>
    private static string? ResolveSafe(string outDir, string entryPath)
    {
        var rel = entryPath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0) return null;
        if (rel.Contains(':')) return null;

        string full;
        try { full = Path.GetFullPath(Path.Combine(outDir, rel)); }
        catch { return null; }

        var root = outDir.EndsWith(Path.DirectorySeparatorChar)
            ? outDir
            : outDir + Path.DirectorySeparatorChar;

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private string? GetString(uint index, uint propId)
    {
        var pv = PropVariant.Create();
        try
        {
            if (_archive!.GetProperty(index, propId, ref pv) != 0) return null;
            return pv.AsString();
        }
        finally { pv.Clear(); }
    }

    private ulong GetUInt64(uint index, uint propId)
    {
        var pv = PropVariant.Create();
        try
        {
            if (_archive!.GetProperty(index, propId, ref pv) != 0) return 0;
            return pv.AsUInt64();
        }
        finally { pv.Clear(); }
    }

    private DateTime? ReadFileTime(uint index, uint propId)
    {
        var pv = PropVariant.Create();
        try
        {
            if (_archive!.GetProperty(index, propId, ref pv) != 0) return null;
            if (pv.VarType != SevenZipNative.VarType.FileTime) return null;
            var ft = pv.FileTime;
            if (ft <= 0) return null;
            return DateTime.FromFileTime(ft);
        }
        catch { return null; }
        finally { pv.Clear(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseCurrentFile(SevenZipNative.OperationResult.Ok);

        if (_archive is not null)
        {
            try { _archive.Close(); } catch { /* nothing left to close */ }
            SevenZipLibrary.ReleaseInArchive(_archive);
            _archive = null;
        }

        // Only after the archive is released may the callback streams go away:
        // 7z.dll can hold raw pointers to them until Close() returns.
        foreach (var item in _liveStreams)
        {
            if (item is IDisposable d) { try { d.Dispose(); } catch { /* idempotent */ } }
        }
        _liveStreams.Clear();

        // A RAR set keeps one stream per volume; they are all ours to release.
        foreach (var volume in _volumeStreams) volume.Dispose();
        _volumeStreams.Clear();
        _volumeStreamByIndex.Clear();

        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>
/// The write side of one extracted member: 7z.dll pushes decoded bytes at us
/// through <see cref="ISequentialOutStream.Write"/>, we forward them to the
/// destination stream and report the running total back to the engine (which
/// is what trips the decompression-bomb fuse).
///
/// One instance is created per member and rooted in the engine's live-stream
/// list until the member is finished, because the only other reference 7z.dll
/// holds is a raw pointer on the native side.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed unsafe class ExtractionOutputSink : ISequentialOutStream, IDisposable
{
    private readonly Stream _target;
    private readonly Func<int, bool> _onWrite;

    public ExtractionOutputSink(Stream target, Func<int, bool> onWrite)
    {
        _target = target;
        _onWrite = onWrite;
    }

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
        if (size == 0 || data == IntPtr.Zero) return SevenZipNative.HResult.S_OK;

        // Write straight from 7z.dll's buffer instead of allocating a managed
        // array and copying on every call - the decoder emits these in 64 KB
        // chunks, and on a large archive that is a lot of garbage to produce.
        var length = (int)size;
        try
        {
            _target.Write(new ReadOnlySpan<byte>((void*)data, length));
        }
        catch (Exception)
        {
            return SevenZipNative.HResult.E_FAIL;
        }

        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, length);

        // A false here means the bomb fuse tripped; abort the extraction.
        return _onWrite(length) ? SevenZipNative.HResult.S_OK : SevenZipNative.HResult.E_ABORT;
    }

    public void Dispose()
    {
        try { _target.Dispose(); } catch { /* idempotent */ }
    }
}

/// <summary>
/// Simple wildcard matcher for <c>--include</c> style member filters: <c>*</c>
/// matches any run of characters (including path separators), <c>?</c> matches
/// exactly one. Comparison is case-insensitive and treats <c>/</c> and <c>\</c>
/// as the same separator, so a filter written with either slash works on an
/// archive recorded with the other.
/// </summary>
internal static class GlobMatcher
{
    public static bool IsMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern)) return string.IsNullOrEmpty(input);
        if (input is null) return false;

        int p = 0, i = 0;
        int starP = -1, starI = 0;

        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], input[i])))
            {
                p++;
                i++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                // Remember where the star was so a mismatch later can backtrack
                // and let this one swallow one more character.
                starP = p++;
                starI = i;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                i = ++starI;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool SameChar(char a, char b)
    {
        if (a == b) return true;
        if ((a == '/' || a == '\\') && (b == '/' || b == '\\')) return true;
        return char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
    }
}
