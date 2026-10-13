using System.Diagnostics;
using System.Runtime.InteropServices;
using SliceArc.Core.SevenZip;
using SliceArc.Core.Volumes;

namespace SliceArc.Core.Compression;

/// <summary>
/// The "add to an existing archive" pipeline: open the target archive with its
/// 7z.dll handler, drive <see cref="IOutArchive.UpdateItems"/> with a plan that
/// keeps the old items and appends/replaces the new ones, write the rebuilt
/// archive to a temp file, then swap it over the original.
///
/// The decisive 7-Zip contract (verified against the sources under .refs) is
/// that the SAME handler instance must both hold the opened old archive and
/// serve the update. 7-Zip does exactly this in
/// CPP/7zip/UI/Common/Update.cpp (arc->Archive is QI'd to IOutArchive, then
/// UpdateItems is called on it, L364-376 + L853), and handlers copy unchanged
/// items' bytes straight out of their own opened archive - the update callback
/// only supplies data for items whose newData flag is set
/// (CPP/7zip/Archive/Zip/ZipUpdate.cpp L684). It also only asks the callback for
/// properties when newProps is set (7zHandlerOut.cpp L488, ZipHandlerOut.cpp
/// L146), so kept items need nothing from us at all.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ArchiveUpdateEngine
    : IArchiveUpdateCallback2, ICryptoGetTextPassword, ICryptoGetTextPassword2,
      IArchiveOpenCallback, IDisposable
{
    private const int ProgressIntervalMs = 80;
    private const long MaxCheckStartPosition = 1L << 23;

    /// <summary>Handlers whose archive is one stream holding a single file - it can never hold more than one item.</summary>
    private static readonly HashSet<string> SingleStreamHandlers =
        new(StringComparer.OrdinalIgnoreCase) { "gzip", "bzip2", "xz", "zstd" };

    private readonly ArchiveUpdateOptions _options;
    private readonly List<string> _errors = new();
    private readonly List<object> _liveStreams = new();
    private readonly Stopwatch _progressClock = Stopwatch.StartNew();
    private readonly List<PlanItem> _plan = new();

    private IInArchive? _inArchive;
    private IOutArchive? _outArchive;
    private string _handlerName = string.Empty;
    private FileStream? _oldStream;
    private CompressionInputSource? _oldSource;

    private string? _tempPath;
    private CompressionOutputSink? _tempSink;

    private CompressionInputSource? _currentSource;

    private Action<CompressionProgress>? _progress;
    private long _bytesTotal;
    private long _bytesDone;
    private long _bytesWritten;
    private int _itemsDone;
    private int _added;
    private int _updated;
    private int _kept;
    private string? _currentPath;
    private bool _cancelled;
    private bool _cancelledReported;
    private bool _disposed;
    private bool _openPhase;
    private bool _passwordAsked;

    public ArchiveUpdateEngine(ArchiveUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Add every source to the archive, replacing same-named entries in place.
    /// Returns a result; failures also land in <see cref="ArchiveUpdateResult.Errors"/>
    /// so a batch caller can report them without a try/catch. Structural
    /// problems (unknown / read-only / volume-set archive) throw
    /// <see cref="UnsupportedArchiveUpdateException"/> instead.
    /// </summary>
    public ArchiveUpdateResult Update(Action<CompressionProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _progress = progress;

        var archivePath = Path.GetFullPath(_options.ArchivePath);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("找不到要更新的压缩包。", archivePath);

        var format = ResolveUpdateFormat(archivePath);
        _handlerName = format.Name;

        EnsureSingleFile(archivePath);

        _inArchive = SevenZipLibrary.CreateInArchive(format.Clsid);
        try
        {
            // Same CLSID, same object: the cast performs a QueryInterface for
            // IOutArchive on the very instance that will hold the opened old
            // archive. That shared identity is what makes 7-Zip's update work.
            _outArchive = (IOutArchive)_inArchive;
        }
        catch (Exception ex)
        {
            throw new UnsupportedArchiveUpdateException(
                $"「{format.Name}」格式不支持写入，无法添加文件。",
                "该格式只能读取。可以先用本软件解压，再重新打包成 7z 或 zip。", ex);
        }

        try
        {
            OpenOldArchive(archivePath);

            // A data-encrypted archive (no header encryption) opens without a
            // password, but rebuilding it without one would store the new items
            // as plaintext right next to the protected ones - a silent downgrade.
            // Require the password whenever any existing item is encrypted.
            if (string.IsNullOrEmpty(_options.Password) && OldArchiveHasEncryptedItems())
                throw new UnsupportedArchiveUpdateException(
                    "这个压缩包已加密，需要口令才能打开并更新。",
                    "请提供正确的口令后重试。", passwordRequired: true);

            BuildPlan();

            _bytesTotal = 0;
            foreach (var p in _plan)
                if (p.Source is { IsDirectory: false }) _bytesTotal += p.Source.Size;

            PushProgress();
            RunUpdate(archivePath);
        }
        finally
        {
            DisposeLiveStreams();
            ReleaseArchives();
            CleanupTemp();
        }

        return new ArchiveUpdateResult(_added, _updated, _kept, _bytesWritten, _errors.ToArray())
        {
            Cancelled = _cancelled,
        };
    }

    // =====================================================================
    // Format / open
    // =====================================================================

    private static ArchiveFormat ResolveUpdateFormat(string archivePath)
    {
        var format = SevenZipLibrary.FindByExtension(archivePath);
        if (format is null)
            throw new UnsupportedArchiveUpdateException(
                "无法识别的压缩包格式，不能向其中添加文件。",
                "请确认这是一个受支持的压缩包（例如 .7z / .zip / .tar）。");

        if (!SevenZipLibrary.SupportsWrite(format))
            throw new UnsupportedArchiveUpdateException(
                $"「{format.Name}」格式只能读取，不能添加文件。",
                "可以先用本软件解压，再重新打包成 7z 或 zip 后添加。");

        if (SingleStreamHandlers.Contains(format.Name))
            throw new UnsupportedArchiveUpdateException(
                $"「{format.Name}」是单文件压缩包，内部只能存放一个文件，无法添加。",
                "可以先用本软件解压，再重新打包成 7z 或 zip。");

        return format;
    }

    private static void EnsureSingleFile(string archivePath)
    {
        var group = VolumeDetector.DetectGroup(archivePath);
        if (group is not null && group.IsMultiVolume)
            throw new UnsupportedArchiveUpdateException(
                "分卷压缩包暂不支持添加文件。",
                "请先合并为一个压缩包，或解压后重新打包，再执行添加。");
    }

    private void OpenOldArchive(string archivePath)
    {
        _oldStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: false);
        _oldSource = new CompressionInputSource(_oldStream, null);
        _liveStreams.Add(_oldSource);

        var streamPtr = Marshal.GetComInterfaceForObject(_oldSource, typeof(IInStream));
        var maxCheck = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(maxCheck, MaxCheckStartPosition);
        var openCb = Marshal.GetComInterfaceForObject(this, typeof(IArchiveOpenCallback));

        int hr;
        _openPhase = true;
        try { hr = _inArchive!.Open(streamPtr, maxCheck, openCb); }
        finally
        {
            _openPhase = false;
            Marshal.FreeHGlobal(maxCheck);
            Marshal.Release(streamPtr);
            Marshal.Release(openCb);
        }

        if (hr != SevenZipNative.HResult.S_OK)
        {
            // The handler asked for a password and we had none to give.
            if (_passwordAsked && string.IsNullOrEmpty(_options.Password))
                throw new UnsupportedArchiveUpdateException(
                    "这个压缩包已加密，需要口令才能打开并更新。",
                    "请提供正确的口令后重试。", passwordRequired: true);

            throw new InvalidDataException(
                $"无法打开压缩包（错误码 0x{hr:X8}）。文件可能已损坏，或格式与扩展名不符。");
        }
    }

    // =====================================================================
    // Plan
    // =====================================================================

    /// <summary>One slot of the new archive: either the bytes we supply, or a copy of an old item.</summary>
    private sealed class PlanItem
    {
        public PendingItem? Source;   // null = keep the old item verbatim
        public int IndexInArchive;    // -1 = brand-new item
    }

    private void BuildPlan()
    {
        _inArchive!.GetNumberOfItems(out var oldCount);
        var oldPaths = new string[oldCount];
        for (uint i = 0; i < oldCount; i++)
            oldPaths[i] = Normalize(GetString(i, SevenZipNative.PropId.Path) ?? string.Empty);

        var newItems = CompressionSourceWalker.Build(_options.Sources, _errors);
        var newToOld = new int[newItems.Count];
        var oldMatched = new bool[oldCount];
        for (var i = 0; i < newToOld.Length; i++) newToOld[i] = -1;

        // Same-named entries replace in place (the 7z.exe "a" convention).
        // Compare case-insensitively because the archives come from a
        // case-insensitive filesystem.
        for (var i = 0; i < newItems.Count; i++)
        {
            var path = Normalize(newItems[i].ArchivePath);
            for (uint j = 0; j < oldCount; j++)
            {
                if (oldMatched[j]) continue;
                if (!string.Equals(oldPaths[j], path, StringComparison.OrdinalIgnoreCase)) continue;
                newToOld[i] = (int)j;
                oldMatched[j] = true;
                _updated++;
                break;
            }
        }

        _plan.Clear();

        // Old items first, in their original order, so unchanged entries keep
        // their position and only the replaced ones change.
        for (uint j = 0; j < oldCount; j++)
        {
            var replacedBy = -1;
            for (var i = 0; i < newToOld.Length; i++)
                if (newToOld[i] == (int)j) { replacedBy = i; break; }

            if (replacedBy >= 0)
                _plan.Add(new PlanItem { Source = newItems[replacedBy], IndexInArchive = (int)j });
            else
            {
                _plan.Add(new PlanItem { Source = null, IndexInArchive = (int)j });
                _kept++;
            }
        }

        // Brand-new items go on the end.
        for (var i = 0; i < newItems.Count; i++)
        {
            if (newToOld[i] >= 0) continue;
            _plan.Add(new PlanItem { Source = newItems[i], IndexInArchive = -1 });
            _added++;
        }
    }

    private string? GetString(uint index, uint propId)
    {
        var pv = PropVariant.Create();
        try
        {
            if (_inArchive!.GetProperty(index, propId, ref pv) != 0) return null;
            return pv.AsString();
        }
        finally { pv.Clear(); }
    }

    /// <summary>True if any existing entry is encrypted (kpidEncrypted).</summary>
    private bool OldArchiveHasEncryptedItems()
    {
        _inArchive!.GetNumberOfItems(out var count);
        for (uint i = 0; i < count; i++)
        {
            var pv = PropVariant.Create();
            try
            {
                if (_inArchive.GetProperty(i, SevenZipNative.PropId.Encrypted, ref pv) == 0 &&
                    pv.AsUInt64() != 0)
                    return true;
            }
            finally { pv.Clear(); }
        }

        return false;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    // =====================================================================
    // Update
    // =====================================================================

    private void RunUpdate(string archivePath)
    {
        _tempPath = archivePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var tempStream = new FileStream(_tempPath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1 << 20, useAsync: false);
        _tempSink = new CompressionOutputSink(tempStream, OnBytesWritten);
        _liveStreams.Add(_tempSink);

        CompressionHandlerConfig.Apply(
            _outArchive!, _handlerName, _options.Level, solid: false,
            _options.Method, _options.Threads, _options.EncryptHeaders, _options.Password, _errors);

        var outPtr = Marshal.GetComInterfaceForObject(_tempSink, typeof(IOutStream));
        var cbPtr = Marshal.GetComInterfaceForObject(this, typeof(IArchiveUpdateCallback2));

        int hr;
        try { hr = _outArchive!.UpdateItems(outPtr, (uint)_plan.Count, cbPtr); }
        finally
        {
            Marshal.Release(cbPtr);
            Marshal.Release(outPtr);
        }

        // Flush and close the rebuilt archive before moving it over the original.
        try { _tempSink.Dispose(); } catch { /* idempotent */ }
        _tempSink = null;

        if (hr != SevenZipNative.HResult.S_OK)
        {
            _errors.Add(_cancelled
                ? "已取消，压缩包未做任何修改。"
                : $"更新压缩包失败（错误码 0x{hr:X8}），原压缩包未改动。");
            return;
        }

        // The handler is done reading the old archive: close it (and its stream)
        // so the swap below is not blocked by a lingering file handle.
        ReleaseArchiveForSwap();

        try
        {
            File.Move(_tempPath, archivePath, overwrite: true);
            _tempPath = null;   // moved; nothing to clean up
        }
        catch (Exception ex)
        {
            _errors.Add($"替换原压缩包失败：{ex.Message}");
        }
    }

    private void ReleaseArchiveForSwap()
    {
        try { _inArchive?.Close(); } catch { /* nothing left to close */ }
        if (_oldSource is not null) { try { _oldSource.Dispose(); } catch { /* idempotent */ } }
    }

    private void CleanupTemp()
    {
        if (_tempPath is null) return;
        try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { /* leave it for the user */ }
    }

    private void ReleaseArchives()
    {
        if (_inArchive is not null)
        {
            try { _inArchive.Close(); } catch { /* already closed */ }
            SevenZipLibrary.ReleaseInArchive(_inArchive);
            _inArchive = null;
        }
        _outArchive = null;   // same RCW as _inArchive; never released twice
    }

    private void DisposeLiveStreams()
    {
        foreach (var s in _liveStreams)
            if (s is IDisposable d) { try { d.Dispose(); } catch { /* idempotent */ } }
        _liveStreams.Clear();
    }

    // =====================================================================
    // IArchiveUpdateCallback2 — IProgress slots
    // =====================================================================

    public int SetTotal(ulong total) => SevenZipNative.HResult.S_OK;

    public int SetCompleted(IntPtr completeValue)
    {
        if (IsCancelled) return SevenZipNative.HResult.E_ABORT;
        if (_progressClock.ElapsedMilliseconds < ProgressIntervalMs) return SevenZipNative.HResult.S_OK;
        _progressClock.Restart();
        PushProgress();
        return SevenZipNative.HResult.S_OK;
    }

    // =====================================================================
    // IArchiveUpdateCallback2 — item slots
    // =====================================================================

    public int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive)
    {
        if (index >= (uint)_plan.Count) return SevenZipNative.HResult.E_INVALIDARG;
        var item = _plan[(int)index];

        // We supply bytes+props for the sources we were given; the handler
        // copies everything else out of the archive it opened itself.
        var isSource = item.Source is not null;
        if (newData != IntPtr.Zero) Marshal.WriteInt32(newData, isSource ? 1 : 0);
        if (newProps != IntPtr.Zero) Marshal.WriteInt32(newProps, isSource ? 1 : 0);
        if (indexInArchive != IntPtr.Zero) Marshal.WriteInt32(indexInArchive, item.IndexInArchive);

        return SevenZipNative.HResult.S_OK;
    }

    public int GetProperty(uint index, uint propId, IntPtr value)
    {
        try
        {
            if (index >= (uint)_plan.Count) return SevenZipNative.HResult.E_INVALIDARG;
            if (value == IntPtr.Zero) return SevenZipNative.HResult.E_POINTER;

            ClearPropVariant(value);

            var source = _plan[(int)index].Source;
            // Kept items are never queried (the handler only calls GetProperty
            // when newProps == 1). Leaving VT_EMPTY is the correct answer.
            if (source is null) return SevenZipNative.HResult.S_OK;

            switch (propId)
            {
                case SevenZipNative.PropId.Path:
                    Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.Bstr);
                    Marshal.WriteIntPtr(value, 8, Marshal.StringToBSTR(source.ArchivePath.Replace('\\', '/')));
                    break;
                case SevenZipNative.PropId.IsDir:
                    Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.Bool);
                    Marshal.WriteInt16(value, 8, source.IsDirectory ? (short)1 : (short)0);
                    break;
                case SevenZipNative.PropId.Size:
                    Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.UI8);
                    Marshal.WriteInt64(value, 8, source.Size);
                    break;
                case SevenZipNative.PropId.MTime:
                    Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.FileTime);
                    Marshal.WriteInt64(value, 8, source.Modified.ToFileTime());
                    break;
                case SevenZipNative.PropId.Attrib:
                    Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.UI4);
                    Marshal.WriteInt32(value, 8, (int)source.Attributes);
                    break;
            }

            return SevenZipNative.HResult.S_OK;
        }
        catch (Exception ex) { return Fail($"GetProperty({index})", ex); }
    }

    public int GetStream(uint index, IntPtr inStream)
    {
        try
        {
            if (inStream != IntPtr.Zero) Marshal.WriteIntPtr(inStream, IntPtr.Zero);
            if (IsCancelled) return SevenZipNative.HResult.E_ABORT;
            if (index >= (uint)_plan.Count) return SevenZipNative.HResult.E_INVALIDARG;

            var source = _plan[(int)index].Source;
            // Only new-data items reach here. Mirror 7-Zip's own callback, which
            // fails for kept items (UpdateCallback.cpp GetStream2).
            if (source is null) return SevenZipNative.HResult.E_FAIL;

            _currentPath = source.ArchivePath;
            if (source.IsDirectory) return SevenZipNative.HResult.S_OK;

            var bufferSize = source.Size <= 0 ? 1 : (int)Math.Min(source.Size, 1 << 20);
            var fileStream = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize, useAsync: false);
            _currentSource = new CompressionInputSource(fileStream, OnBytesRead);
            _liveStreams.Add(_currentSource);

            var ptr = Marshal.GetComInterfaceForObject(_currentSource, typeof(IInStream));
            if (inStream != IntPtr.Zero) Marshal.WriteIntPtr(inStream, ptr);
            return SevenZipNative.HResult.S_OK;
        }
        catch (Exception ex) { return Fail($"GetStream({index})", ex); }
    }

    public int SetOperationResult(int operationResult)
    {
        _currentSource = null;

        if (operationResult == SevenZipNative.OperationResult.Ok)
        {
            _itemsDone++;
        }
        else if (_currentPath is not null)
        {
            _errors.Add($"{_currentPath}: {SevenZipNative.OperationResult.Describe(operationResult)}");
        }

        PushProgress();
        return IsCancelled ? SevenZipNative.HResult.E_ABORT : SevenZipNative.HResult.S_OK;
    }

    public int GetVolumeSize(uint index, IntPtr size) => SevenZipNative.HResult.S_FALSE;

    public int GetVolumeStream(uint index, IntPtr volumeStream)
    {
        if (volumeStream != IntPtr.Zero) Marshal.WriteIntPtr(volumeStream, IntPtr.Zero);
        return SevenZipNative.HResult.S_FALSE;
    }

    // =====================================================================
    // Crypto callbacks
    // =====================================================================

    /// <summary>
    /// The open path uses ICryptoGetTextPassword. During the handler's probing
    /// phase an empty password is reported as an empty BSTR (not E_ABORT) so an
    /// unencrypted archive is never misdiagnosed as password-protected; after
    /// the probe, "no password" is E_ABORT, the standard refusal.
    /// </summary>
    public int CryptoGetTextPassword(out IntPtr password)
    {
        password = IntPtr.Zero;
        _passwordAsked = true;

        var value = _options.Password;
        if (string.IsNullOrEmpty(value))
        {
            if (!_openPhase) return SevenZipNative.HResult.E_ABORT;
            password = Marshal.StringToBSTR(string.Empty);
            return SevenZipNative.HResult.S_OK;
        }

        password = Marshal.StringToBSTR(value);
        return SevenZipNative.HResult.S_OK;
    }

    /// <summary>The write path uses ICryptoGetTextPassword2; flag 0 = "no encryption wanted".</summary>
    public int CryptoGetTextPassword2(IntPtr passwordIsDefined, out IntPtr password)
    {
        password = IntPtr.Zero;
        var value = _options.Password;
        if (string.IsNullOrEmpty(value))
        {
            if (passwordIsDefined != IntPtr.Zero) Marshal.WriteInt32(passwordIsDefined, 0);
            return SevenZipNative.HResult.S_OK;
        }

        if (passwordIsDefined != IntPtr.Zero) Marshal.WriteInt32(passwordIsDefined, 1);
        password = Marshal.StringToBSTR(value);
        return SevenZipNative.HResult.S_OK;
    }

    // =====================================================================
    // IArchiveOpenCallback
    // =====================================================================

    int IArchiveOpenCallback.SetTotal(IntPtr files, IntPtr bytes) => SevenZipNative.HResult.S_OK;

    int IArchiveOpenCallback.SetCompleted(IntPtr files, IntPtr bytes) =>
        IsCancelled ? SevenZipNative.HResult.E_ABORT : SevenZipNative.HResult.S_OK;

    // =====================================================================
    // Helpers
    // =====================================================================

    private bool IsCancelled
    {
        get
        {
            if (_cancelled) return true;
            if (!_options.CancellationToken.IsCancellationRequested) return false;
            _cancelled = true;
            return true;
        }
    }

    private bool OnBytesWritten(int count)
    {
        _bytesWritten += count;
        return !IsCancelled;
    }

    private void OnBytesRead(int count)
    {
        if (count > 0) _bytesDone += count;
    }

    private void PushProgress()
    {
        if (_progress is null) return;
        _progress(new CompressionProgress(
            _itemsDone, _plan.Count, _bytesDone, _bytesTotal, _bytesWritten, 1, 1, _currentPath));
    }

    private int Fail(string where, Exception ex)
    {
        if (!_cancelledReported || !_cancelled)
            _errors.Add($"{where}: {ex.Message}");
        _cancelledReported = true;
        return SevenZipNative.HResult.E_FAIL;
    }

    private static void ClearPropVariant(IntPtr ptr)
    {
        var vt = (ushort)Marshal.ReadInt16(ptr);
        if (vt == SevenZipNative.VarType.Bstr)
        {
            var p = Marshal.ReadIntPtr(ptr, 8);
            if (p != IntPtr.Zero) Marshal.FreeBSTR(p);
        }
        else if (vt == SevenZipNative.VarType.LPWStr)
        {
            var p = Marshal.ReadIntPtr(ptr, 8);
            if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
        Marshal.WriteInt64(ptr, 0, 0);
        Marshal.WriteInt64(ptr, 8, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeLiveStreams();
        ReleaseArchives();
        CleanupTemp();
    }
}
