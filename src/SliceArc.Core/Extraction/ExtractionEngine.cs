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
public sealed class ExtractionEngine : IArchiveExtractCallback, ICryptoGetTextPassword, IArchiveOpenCallback, IDisposable
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

    /// <summary>Logical size of the whole archive stream, across all volumes.</summary>
    public long TotalArchiveBytes => _stream?.TotalBytes ?? 0;

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

        // Every volume style - .7z.001 slicing and PKZIP's .z01/.../.zip alike -
        // goes through one merged stream. The ZIP handler only ever sees a plain
        // single-stream archive that happens to span several files, and our
        // per-volume accounting keeps working because the merge happens below it.
        var stream = new VolumeAwareStream(group);
        // The pointer handed to Open() is typed IArchiveOpenCallback* by 7z.dll,
        // which calls SetTotal/SetCompleted straight off it - no QueryInterface.
        // So it has to be an IArchiveOpenCallback vtable for real: when we passed
        // the ICryptoGetTextPassword pointer instead, the handler's SetTotal ran
        // CryptoGetTextPassword and its SetCompleted read past the end of that
        // one-method interface into garbage, which is an access violation
        // (Fatal error 0x80131506) the moment a handler reports open progress -
        // e.g. while scanning the central directory of a zip with thousands of
        // members. This object still answers the password QI, because it
        // implements ICryptoGetTextPassword too.
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
            "如果它是加密压缩包，本程序暂不支持输入口令解压。");
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

        if (options.DeleteSpentVolumes && !_group.IsComplete)
        {
            // Missing volumes mean the byte layout is a guess; never delete on a guess.
            _errors.Add("分卷不完整，已自动禁用「解压后删除分卷」。");
        }

        var selected = SelectEntries(options);

        if (selected.Count == 0)
        {
            return BuildResult();
        }

        _stream!.BeginExtractionTracking();
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
        finally
        {
            Marshal.Release(callbackPtr);
        }

        CloseCurrentFile(SevenZipNative.OperationResult.Ok);
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
        var paths = options.IncludePaths is { Count: > 0 }
            ? new HashSet<string>(options.IncludePaths.Select(p => p.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
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
        }

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
            _failureReason = SevenZipNative.OperationResult.Describe(SevenZipNative.OperationResult.WrongPassword);
            _failureHint = SevenZipNative.OperationResult.Hint(SevenZipNative.OperationResult.WrongPassword);
            return;
        }
        _failureReason = SevenZipNative.OperationResult.Describe(opRes);
        _failureHint = SevenZipNative.OperationResult.Hint(opRes);
    }

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
                _errors.Add($"{_lastEntry ?? "未知条目"}: {SevenZipNative.OperationResult.Describe(opRes)}");
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
                _errors.Add($"{_lastEntry ?? "未知条目"}: {SevenZipNative.OperationResult.Describe(opRes)}");
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

        var keepLast = Math.Max(0, _options.KeepLastVolumes);
        var limit = _group.Volumes.Count - keepLast;

        for (var i = 0; i < limit; i++)
        {
            if (_deletedVolumes[i]) continue;

            // The stream tracks every volume of the set, so one call answers
            // "is this volume spent" for sliced archives and PKZIP .z01 sets alike.
            var tracker = _stream;
            var trackIndex = i;
            if (tracker is null) continue;
            if (!tracker.IsVolumeSpent(trackIndex)) continue;
            if (!final && !tracker.IsDeletionSafe) continue;

            var volume = _group.Volumes[i];
            try
            {
                tracker.ReleaseVolumeHandle(trackIndex);
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
                RecordFailure("无法回收分卷",
                    "某个分卷删除失败：它可能正被其他程序占用。解压结果不受影响，可稍后手动删除。");
                _errors.Add($"删除 {volume.FileName} 失败: {ex.Message}");
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
        _progress(new ExtractionProgress(
            done,
            _entriesTotal,
            _bytesDone,
            _bytesTotal,
            _stream.Group.VolumeIndexAt(_stream.Position) + 1,
            _stream.Group.Volumes.Count,
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
