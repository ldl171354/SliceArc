using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SliceArc.Core.SevenZip;

namespace SliceArc.Core.Compression;

/// <summary>
/// The compression pipeline: enumerate the caller's sources into a flat item
/// list, instantiate the right 7z.dll handler (7z/zip/tar/gz/bz2/xz/zst/wim),
/// configure it via <see cref="ISetProperties"/>, then drive
/// <see cref="IOutArchive.UpdateItems"/> from a CCW of this object.
///
/// Mirrors <see cref="SliceArc.Core.Extraction.ExtractionEngine"/> in shape: a
/// single-threaded COM worker that callers run on a pool thread, with progress
/// throttled to roughly 12 Hz and a cooperative cancellation token. The same
/// flat-declaration / IntPtr-ONLY rule from the read side applies here too -
/// IArchiveUpdateCallback derives from IProgress, and the vtable order is
/// load-bearing (see SevenZipInterfaces.cs).
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class CompressionEngine : IArchiveUpdateCallback2, ICryptoGetTextPassword2, IDisposable
{
    private const int ProgressIntervalMs = 80;

    private readonly CompressionOptions _options;
    private readonly List<PendingItem> _items = new();
    private readonly List<string> _errors = new();
    private readonly List<string> _volumePaths = new();
    private readonly Stopwatch _progressClock = Stopwatch.StartNew();

    // ---- run-scoped state ----
    private long _bytesTotal;
    private long _bytesDone;
    private long _bytesWritten;
    private int _itemsAdded;
    private FileStream? _currentIn;
    private CompressionInputSource? _currentSource;
    private long _currentItemSize;
    private int _currentVolumeIndex = -1;
    private string? _currentPath;
    private bool _cancelled;
    private bool _disposed;
    private IOutArchive? _archive;
    private ISequentialOutStream? _primaryOut;
    private FileStream? _primaryStream;
    private List<object> _liveStreams = new();
    private Action<CompressionProgress>? _progress;

    public CompressionEngine(CompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    // =====================================================================
    // Public entry
    // =====================================================================

    public CompressionResult Compress(Action<CompressionProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _progress = progress;

        BuildItemList();
        if (_items.Count == 0)
            return new CompressionResult(0, 0, 0, Array.Empty<string>(),
                new[] { "未找到任何要压缩的文件。" });

        // Single-stream formats (gz/bz2/xz/zst) can only hold one file. If we
        // have multiple items (directory or multiple files), auto-tar first
        // so the result is a proper .tar.gz / .tar.bz2 / .tar.xz / .tar.zst.
        if (IsSingleStreamFormat(_options.Format) && _items.Count > 1)
            return CompressWithAutoTar(progress);

        _bytesTotal = _items.Where(i => !i.IsDirectory).Sum(i => i.Size);
        PushProgress();

        var format = ResolveFormat(_options.Format);
        _archive = SevenZipLibrary.CreateOutArchive(format.Clsid);

        try
        {
            ConfigureHandler(_archive);
            OpenPrimaryOutput();

            var callbackPtr = Marshal.GetComInterfaceForObject(this, typeof(IArchiveUpdateCallback2));
            // Pass IOutStream (which begins with the ISequentialOutStream Write slot) so handlers
            // that need Seek/SetSize can call through the same pointer without an extra QI.
            var outPtr = Marshal.GetComInterfaceForObject(_primaryOut!, typeof(IOutStream));
            int hr;
            try
            {
                hr = _archive.UpdateItems(outPtr, (uint)_items.Count, callbackPtr);
            }
            finally
            {
                Marshal.Release(callbackPtr);
                Marshal.Release(outPtr);
            }

            CloseCurrentInput(SevenZipNative.OperationResult.Ok);
            ClosePrimaryOutput(hr);

            if (!_cancelled && hr != SevenZipNative.HResult.S_OK)
                _errors.Add($"IOutArchive::UpdateItems 返回 HRESULT=0x{hr:X8}。");
        }
        finally
        {
            DisposeLiveStreams();
        }

        PushProgress();
        return new CompressionResult(_itemsAdded, _bytesDone, _bytesWritten,
            _volumePaths.Count > 0 ? _volumePaths.ToArray() : new[] { _options.OutputPath },
            _errors.ToArray())
        { Cancelled = _cancelled };
    }

    // =====================================================================
    // Auto-tar for single-stream formats
    // =====================================================================

    private static bool IsSingleStreamFormat(CompressionFormat f) =>
        f is CompressionFormat.GZip or CompressionFormat.BZip2
            or CompressionFormat.Xz or CompressionFormat.Zstd;

    /// <summary>
    /// For gz/bz2/xz/zst with multiple items: first tar everything into a temp
    /// file, then compress the tar with the chosen single-stream format. The
    /// output is renamed to .tar.gz / .tar.bz2 / .tar.xz / .tar.zst so it round-trips
    /// cleanly through the extract side.
    /// </summary>
    private CompressionResult CompressWithAutoTar(Action<CompressionProgress>? progress)
    {
        // Determine the final output path — insert .tar before the final extension.
        var ext = _options.Format.Extension();
        var outPath = _options.OutputPath;
        if (!outPath.EndsWith(".tar." + ext, StringComparison.OrdinalIgnoreCase))
        {
            if (outPath.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
                outPath = outPath.Substring(0, outPath.Length - ext.Length - 1) + ".tar." + ext;
            else
                outPath += ".tar." + ext;
        }

        // Create the temp tar next to the output with a clean name, so the
        // filename stored inside the gz/bz2/xz is "out.tar" (not a GUID).
        var baseName = Path.GetFileNameWithoutExtension(outPath);
        if (baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            baseName = baseName.Substring(0, baseName.Length - 4);
        var dir = Path.GetDirectoryName(outPath);
        if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
        var tarPath = Path.Combine(dir, baseName + ".tar");

        try
        {
            // Step 1: create a tar of the original sources.
            var tarOptions = _options with { Format = CompressionFormat.Tar, OutputPath = tarPath, VolumeSize = 0 };
            using var tarEngine = new CompressionEngine(tarOptions);
            var tarResult = tarEngine.Compress(progress);
            if (!tarResult.Ok)
                return tarResult;

            // Step 2: compress the tar as a single file with the chosen format.
            var compOptions = _options with
            {
                Sources = new[] { tarPath },
                OutputPath = outPath,
                VolumeSize = 0,   // single-stream formats don't support our volume splitting
            };
            using var compEngine = new CompressionEngine(compOptions);
            return compEngine.Compress(progress);
        }
        finally
        {
            try { if (File.Exists(tarPath)) File.Delete(tarPath); } catch { /* temp */ }
        }
    }

    // =====================================================================
    // Item enumeration
    // =====================================================================

    /// <summary>
    /// Walk every source into the flat item list 7z.dll will be driven with.
    /// The listing logic lives in <see cref="CompressionSourceWalker"/> so the
    /// add-to-existing engine names entries identically.
    /// </summary>
    private void BuildItemList()
    {
        _items.Clear();
        _items.AddRange(CompressionSourceWalker.Build(_options.Sources, _errors));
    }

    // =====================================================================
    // Handler configuration
    // =====================================================================

    private static ArchiveFormat ResolveFormat(CompressionFormat format)
    {
        foreach (var f in SevenZipLibrary.Formats)
        {
            if (!f.Name.Equals(format.HandlerName(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!SevenZipLibrary.SupportsWrite(f)) continue;
            return f;
        }

        // Say what IS possible: 7-Zip 26.04 can read .zst but cannot write it, so
        // "not supported" without a list leaves the user guessing.
        var writable = Enum.GetValues<CompressionFormat>()
            .Where(f => f.CanCreate())
            .Select(f => f.DisplayName());

        throw new InvalidOperationException(
            $"7z.dll 没有可写入的 {format.DisplayName()} 处理器（已枚举 {SevenZipLibrary.Formats.Count} 个 handler）。" +
            $"可创建的格式：{string.Join(" / ", writable)}。");
    }

    /// <summary>
    /// Set the property bag the handler reads before UpdateItems. Delegates to
    /// <see cref="CompressionHandlerConfig"/> so the create-new and
    /// add-to-existing pipelines configure the handler identically.
    /// </summary>
    private void ConfigureHandler(IOutArchive archive)
        => CompressionHandlerConfig.Apply(
            archive, _options.Format.HandlerName(), _options.Level, _options.Solid,
            _options.Method, _options.Threads, _options.EncryptHeaders, _options.Password, _errors);

    /// <summary>
    /// Called by the handler (7z and zip alike) through the update callback when
    /// it wants a password. We answer from the options; a null flag + an empty
    /// BSTR means "no encryption", which is what the handler expects for an
    /// unencrypted archive.
    /// </summary>
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
    // Output stream wiring
    // =====================================================================

    private void OpenPrimaryOutput()
    {
        var dir = Path.GetDirectoryName(_options.OutputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (_options.VolumeSize > 0)
        {
            // Volume mode: name volumes .001, .002, .003, ... so VolumeDetector
            // recognises them as a NumericSuffix set and the extract side can
            // reassemble them transparently.
            var firstVol = _options.OutputPath + ".001";
            _primaryStream = new FileStream(firstVol, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 1 << 20);
            var volSink = new VolumeOutputSink(
                _primaryStream, firstVol, _options.VolumeSize,
                OpenNextVolume, OnBytesWritten);
            _volumePaths.Add(firstVol);
            _primaryOut = volSink;
        }
        else
        {
            _primaryStream = new FileStream(_options.OutputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 1 << 20);
            _primaryOut = new CompressionOutputSink(_primaryStream, OnBytesWritten);
        }
        _liveStreams.Add(_primaryOut);
    }

    private (Stream, string) OpenNextVolume(int index)
    {
        // index is 0-based: 0 = second volume (.002), 1 = third (.003), ...
        var suffix = "." + (index + 2).ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        var path = _options.OutputPath + suffix;
        var d = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 1 << 20);
        _volumePaths.Add(path);
        return (stream, path);
    }

    private void ClosePrimaryOutput(int hr)
    {
        // In volume mode the VolumeOutputSink owns the streams; just flush.
        // In normal mode we dispose _primaryStream here.
        try
        {
            if (_primaryOut is IDisposable dispOut)
            {
                try { _primaryStream?.Flush(); } catch { }
                dispOut.Dispose();
            }
            else
            {
                _primaryStream?.Flush();
                _primaryStream?.Dispose();
            }
            _primaryStream = null;
            _primaryOut = null;
        }
        catch (Exception ex)
        {
            _errors.Add($"关闭输出流失败: {ex.Message}");
        }

        // Volume mode: if the primary file (volume 0) ended up empty (all data
        // went to .001, .002, ...), delete the empty stub.
        if (_volumePaths.Count > 0 && File.Exists(_options.OutputPath))
        {
            var primaryInfo = new FileInfo(_options.OutputPath);
            if (primaryInfo.Length == 0)
            {
                try { File.Delete(_options.OutputPath); } catch { }
                _volumePaths.Remove(_options.OutputPath);
            }
        }
    }

    private bool OnBytesWritten(int count)
    {
        _bytesWritten += count;
        return !IsCancelled;
    }

    private void OnBytesRead(int count)
    {
        // 7z.dll pulls bytes from our input source; we count them for progress.
        // Writes to the output stream are the other half of the picture.
        _bytesDone += count;
    }

    // =====================================================================
    // IProgress slots
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
    // IArchiveUpdateCallback slots
    // =====================================================================

    private void LogExc(string where, Exception ex)
    {
        var msg = $"[CB] {where}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";
        _errors.Add(msg);
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "cb-except.log"), msg + Environment.NewLine); } catch { }
    }

    public int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive)
    {
        try
        {
        if (newData != IntPtr.Zero) Marshal.WriteInt32(newData, 1);
        if (newProps != IntPtr.Zero) Marshal.WriteInt32(newProps, 1);
        if (indexInArchive != IntPtr.Zero) Marshal.WriteInt32(indexInArchive, -1);
        return SevenZipNative.HResult.S_OK;
        }
        catch (Exception ex) { LogExc("GetUpdateItemInfo", ex); return SevenZipNative.HResult.E_FAIL; }
    }

    public int GetProperty(uint index, uint propId, IntPtr value)
    {
        try
        {
        if (index >= (uint)_items.Count) return SevenZipNative.HResult.E_INVALIDARG;
        if (value == IntPtr.Zero) return SevenZipNative.HResult.E_POINTER;
        var item = _items[(int)index];

        // Clear any existing PROPVARIANT 7z.dll handed us (it should be VT_EMPTY,
        // but be safe). Layout: vt(2) r1(2) r2(2) r3(2) union(8) = 16 bytes.
        ClearPropVariant(value);

        switch (propId)
        {
            case SevenZipNative.PropId.Path:
                Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.Bstr);
                Marshal.WriteIntPtr(value, 8, Marshal.StringToBSTR(item.ArchivePath.Replace('\\', '/')));
                break;
            case SevenZipNative.PropId.IsDir:
                Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.Bool);
                Marshal.WriteInt16(value, 8, item.IsDirectory ? (short)1 : (short)0);
                break;
            case SevenZipNative.PropId.Size:
                Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.UI8);
                Marshal.WriteInt64(value, 8, item.Size);
                break;
            case SevenZipNative.PropId.MTime:
                Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.FileTime);
                Marshal.WriteInt64(value, 8, item.Modified.ToFileTime());
                break;
            case SevenZipNative.PropId.Attrib:
                Marshal.WriteInt16(value, 0, (short)SevenZipNative.VarType.UI4);
                Marshal.WriteInt32(value, 8, (int)item.Attributes);
                break;
            default:
                // Leave as VT_EMPTY (already cleared above).
                break;
        }

        return SevenZipNative.HResult.S_OK;
        }
        catch (Exception ex) { LogExc($"GetProperty(idx={index}, prop={propId})", ex); return SevenZipNative.HResult.E_FAIL; }
    }

    /// <summary>
    /// Free any allocation in a 16-byte PROPVARIANT at <paramref name="ptr"/>
    /// and set it to VT_EMPTY. Mirrors PropVariant::Clear but works on raw
    /// unmanaged memory so we never overrun the 16-byte buffer 7z.dll gave us.
    /// </summary>
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
        // Zero out all 16 bytes so the union arm is clean.
        Marshal.WriteInt64(ptr, 0, 0);
        Marshal.WriteInt64(ptr, 8, 0);
    }

    public int GetStream(uint index, IntPtr inStream)
    {
        try
        {
        if (inStream != IntPtr.Zero) Marshal.WriteIntPtr(inStream, IntPtr.Zero);
        if (IsCancelled) return SevenZipNative.HResult.E_ABORT;
        if (index >= (uint)_items.Count) return SevenZipNative.HResult.E_INVALIDARG;

        var item = _items[(int)index];
        _currentPath = item.ArchivePath;

        if (item.IsDirectory)
        {
            return SevenZipNative.HResult.S_OK;
        }

        // Size the read buffer to the member: a flat 1 MB buffer is right for big
        // files but wasteful for the small ones a game install is full of, and
        // every input stream stays alive (see CloseCurrentInput) until the run
        // ends, so those buffers used to add up to tens of GB.
        var bufferSize = item.Size <= 0 ? 1 : (int)Math.Min(item.Size, 1 << 20);
        _currentIn = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: false);
        _currentItemSize = item.Size;
        _currentSource = new CompressionInputSource(_currentIn, OnBytesRead);
        _liveStreams.Add(_currentSource);
        var ptr = Marshal.GetComInterfaceForObject(_currentSource, typeof(IInStream));
        if (inStream != IntPtr.Zero) Marshal.WriteIntPtr(inStream, ptr);

        return SevenZipNative.HResult.S_OK;
        }
        catch (Exception ex) { LogExc($"GetStream(idx={index})", ex); return SevenZipNative.HResult.E_FAIL; }
    }

    public int SetOperationResult(int operationResult)
    {
        try
        {
        CloseCurrentInput(operationResult);

        if (operationResult == SevenZipNative.OperationResult.Ok)
        {
            _itemsAdded++;
        }
        else
        {
            _errors.Add($"{_currentPath ?? "未知文件"}: {SevenZipNative.OperationResult.Describe(operationResult)}");
        }

        PushProgress();
        return IsCancelled ? SevenZipNative.HResult.E_ABORT : SevenZipNative.HResult.S_OK;
        }
        catch (Exception ex) { LogExc("SetOperationResult", ex); return SevenZipNative.HResult.E_FAIL; }
    }

    // =====================================================================
    // IArchiveUpdateCallback2 slots (volume splitting)
    // =====================================================================

    public int GetVolumeSize(uint index, IntPtr size)
    {
        // Volume splitting is handled at the output stream level (VolumeOutputSink).
        // Return S_FALSE so the handler doesn't try its own volume mode.
        return SevenZipNative.HResult.S_FALSE;
    }

    public int GetVolumeStream(uint index, IntPtr volumeStream)
    {
        if (volumeStream != IntPtr.Zero) Marshal.WriteIntPtr(volumeStream, IntPtr.Zero);
        if (IsCancelled) return SevenZipNative.HResult.E_ABORT;

        // 7z.dll names volumes as base.001, base.002, ... We use the same scheme:
        // our primary OutputPath is the *first* volume (index 0), so for index N
        // we open OutputPath + ".00(N+1)".
        var n = (int)index + 1;
        var suffix = "." + n.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        var volumePath = _options.OutputPath + suffix;

        try
        {
            var dir = Path.GetDirectoryName(volumePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var stream = new FileStream(volumePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 1 << 20);
            var sink = new CompressionOutputSink(stream, OnBytesWritten);
            _liveStreams.Add(sink);
            _volumePaths.Add(volumePath);
            _currentVolumeIndex = (int)index;

            var ptr = Marshal.GetComInterfaceForObject(sink, typeof(ISequentialOutStream));
            if (volumeStream != IntPtr.Zero) Marshal.WriteIntPtr(volumeStream, ptr);
        }
        catch (Exception ex)
        {
            _errors.Add($"创建分卷 {volumePath} 失败: {ex.Message}");
            return SevenZipNative.HResult.E_FAIL;
        }

        return SevenZipNative.HResult.S_OK;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private void CloseCurrentInput(int opRes)
    {
        // Do NOT dispose the FileStream here. The ZIP handler keeps a pipeline of
        // input streams alive (it compresses several members in parallel) and
        // re-reads them after SetOperationResult returns, so disposing early
        // makes UpdateItems fail with ObjectDisposedException (0x80131622). All
        // streams stay alive in _liveStreams until Compress returns; they are
        // cheap now because each buffer is sized to its member, not a flat 1 MB.
        _currentSource = null;
        _currentIn = null;
    }

    private void DisposeLiveStreams()
    {
        foreach (var s in _liveStreams)
        {
            if (s is IDisposable d) try { d.Dispose(); } catch { }
        }
        _liveStreams.Clear();
    }

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

    private void PushProgress()
    {
        if (_progress is null) return;
        _progress(new CompressionProgress(
            _itemsAdded,
            _items.Count,
            _bytesDone,
            _bytesTotal,
            _bytesWritten,
            Math.Max(0, _currentVolumeIndex),
            Math.Max(1, _volumePaths.Count),
            _currentPath));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseCurrentInput(SevenZipNative.OperationResult.Ok);
        DisposeLiveStreams();

        try { _primaryStream?.Dispose(); } catch { /* already gone */ }
        _primaryStream = null;

        if (_archive is not null)
        {
            try { SevenZipLibrary.ReleaseOutArchive(_archive); } catch { /* disposer */ }
            _archive = null;
        }
    }
}

/// <summary>
/// IInStream over a .NET FileStream. Same flat-COM contract as VolumeAwareStream
/// on the extract side: 7z.dll pulls bytes through Read and may Seek back for
/// CRC recomputation / solid layout. IntPtr out parameters throughout - never
/// `out` across the boundary (NOTES-7z-com-interop.md 坑 2).
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class CompressionInputSource : IInStream, ISequentialInStream, IDisposable
{
    private readonly Stream _source;
    private readonly Action<int>? _onRead;

    public CompressionInputSource(Stream source, Action<int>? onRead = null)
    {
        _source = source;
        _onRead = onRead;
    }

    public void Dispose()
    {
        try { _source.Dispose(); } catch { /* idempotent */ }
    }

    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
        if (size == 0 || data == IntPtr.Zero) return SevenZipNative.HResult.S_OK;

        // Decode straight into the buffer 7z.dll handed us: the old path
        // allocated a managed array on every call and copied every input byte
        // through it. The extract side already works this way.
        var length = (int)Math.Min(size, 1 << 20);
        int read;
        unsafe { read = _source.Read(new Span<byte>((void*)data, length)); }
        if (read > 0) _onRead?.Invoke(read);
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, read);
        return SevenZipNative.HResult.S_OK;
    }

    /// <summary>
    /// ISequentialInStream.Read — the ZIP handler QIs for this interface on the
    /// input stream (it doesn't need Seek). Uses <c>out uint</c> instead of
    /// IntPtr for processedSize because the CLR always passes a valid pointer
    /// for <c>out</c> params, so we don't need NULL handling.
    /// </summary>
    int ISequentialInStream.Read(IntPtr data, uint size, out uint processedSize)
    {
        processedSize = 0;
        if (size == 0 || data == IntPtr.Zero) return SevenZipNative.HResult.S_OK;

        var length = (int)Math.Min(size, 1 << 20);
        int read;
        unsafe { read = _source.Read(new Span<byte>((void*)data, length)); }
        if (read > 0) _onRead?.Invoke(read);
        processedSize = (uint)read;
        return SevenZipNative.HResult.S_OK;
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        if (!_source.CanSeek) return SevenZipNative.HResult.E_FAIL;

        var origin = seekOrigin switch
        {
            SevenZipNative.SeekOrigin7z.Begin => SeekOrigin.Begin,
            SevenZipNative.SeekOrigin7z.Current => SeekOrigin.Current,
            SevenZipNative.SeekOrigin7z.End => SeekOrigin.End,
            _ => SeekOrigin.Current,
        };

        try
        {
            var pos = _source.Seek(offset, origin);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, pos);
            return SevenZipNative.HResult.S_OK;
        }
        catch (Exception)
        {
            return SevenZipNative.HResult.E_FAIL;
        }
    }
}

/// <summary>
/// ISequentialOutStream AND IOutStream over a FileStream. 7z.dll QIs for
/// IOutStream when it needs to Seek (rewrite headers, jump back for solid
/// optimisation); we implement both interfaces on the same object so the
/// handler gets a single CCW that satisfies whatever it asks for.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class CompressionOutputSink : ISequentialOutStream, IOutStream, IDisposable
{
    private readonly Stream _target;
    private readonly Func<int, bool> _onWrite;

    public CompressionOutputSink(Stream target, Func<int, bool> onWrite)
    {
        _target = target;
        _onWrite = onWrite;
    }

    public void Dispose()
    {
        try { _target.Dispose(); } catch { /* idempotent */ }
    }

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
        if (size == 0 || data == IntPtr.Zero) return SevenZipNative.HResult.S_OK;

        // Write straight from the encoder's buffer instead of allocating and
        // copying a managed array on every call.
        var length = (int)size;
        unsafe { _target.Write(new ReadOnlySpan<byte>((void*)data, length)); }

        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, length);
        return _onWrite(length) ? SevenZipNative.HResult.S_OK : SevenZipNative.HResult.E_ABORT;
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        if (!_target.CanSeek) return SevenZipNative.HResult.E_FAIL;

        var origin = seekOrigin switch
        {
            SevenZipNative.SeekOrigin7z.Begin => SeekOrigin.Begin,
            SevenZipNative.SeekOrigin7z.Current => SeekOrigin.Current,
            SevenZipNative.SeekOrigin7z.End => SeekOrigin.End,
            _ => SeekOrigin.Current,
        };

        try
        {
            var pos = _target.Seek(offset, origin);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, pos);
            return SevenZipNative.HResult.S_OK;
        }
        catch (Exception)
        {
            return SevenZipNative.HResult.E_FAIL;
        }
    }

    public int SetSize(long newSize)
    {
        if (!_target.CanSeek) return SevenZipNative.HResult.E_FAIL;
        try
        {
            _target.SetLength(newSize);
            return SevenZipNative.HResult.S_OK;
        }
        catch (Exception)
        {
            return SevenZipNative.HResult.E_FAIL;
        }
    }
}

/// <summary>
/// Volume-splitting output sink: wraps multiple FileStreams, opening a new
/// volume file when the current one reaches <c>VolumeSize</c> bytes. Implements
/// IOutStream (Write + Seek + SetSize) so the handler can seek back across
/// volume boundaries — the 7z handler rewrites the signature header at offset 0
/// after writing the entire archive, which lands in volume .001 even if the
/// current write position is in .005. The Seek implementation here jumps back
/// to the correct volume rather than only seeking within the current one.
///
/// This mirrors 7-Zip's COutStreamWithVolume (CPP/7zip/UI/Common/UpdateProduce.cpp):
/// the handler writes to a single logical stream, and this class transparently
/// splits the output across physical files (.001, .002, ...).
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class VolumeOutputSink : ISequentialOutStream, IOutStream, IDisposable
{
    private readonly long _volumeSize;
    private readonly Func<int, (Stream Stream, string Path)> _openVolume;
    private readonly Func<int, bool> _onWrite;
    private readonly List<Stream> _volumes = new();
    private readonly List<long> _volumeStartOffsets = new();
    private int _currentIndex = 0;
    private long _volumePosition = 0;
    private long _maxPosition = 0;  // highest global position reached (for End seek)

    public VolumeOutputSink(Stream firstVolume, string firstPath, long volumeSize,
        Func<int, (Stream, string)> openVolume, Func<int, bool> onWrite)
    {
        _volumes.Add(firstVolume);
        _volumeStartOffsets.Add(0);
        _volumeSize = volumeSize;
        _openVolume = openVolume;
        _onWrite = onWrite;
    }

    private Stream Current => _volumes[_currentIndex];

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
        if (size == 0 || data == IntPtr.Zero) return SevenZipNative.HResult.S_OK;

        var written = 0;
        while (written < size)
        {
            var remaining = _volumeSize - _volumePosition;
            if (remaining <= 0)
            {
                // Current volume is full — move to the next one.
                // If the next volume already exists in _volumes (we're writing
                // after a backward seek that crossed into a previous volume),
                // reuse it; otherwise open a fresh volume file.
                _currentIndex++;
                _volumePosition = 0;
                if (_currentIndex >= _volumes.Count)
                {
                    var (stream, _) = _openVolume(_currentIndex - 1);
                    _volumes.Add(stream);
                    _volumeStartOffsets.Add(_volumeStartOffsets[_currentIndex - 1] + _volumeSize);
                }
                remaining = _volumeSize;
            }

            var toWrite = (int)Math.Min(size - written, (uint)remaining);
            unsafe { Current.Write(new ReadOnlySpan<byte>((byte*)data + written, toWrite)); }
            _volumePosition += toWrite;
            written += toWrite;

            var globalPos = _volumeStartOffsets[_currentIndex] + _volumePosition;
            if (globalPos > _maxPosition) _maxPosition = globalPos;
        }

        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, (int)size);
        return _onWrite((int)size) ? SevenZipNative.HResult.S_OK : SevenZipNative.HResult.E_ABORT;
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        if (!Current.CanSeek) return SevenZipNative.HResult.E_FAIL;

        long target;
        switch (seekOrigin)
        {
            case SevenZipNative.SeekOrigin7z.Begin:
                target = offset;
                break;
            case SevenZipNative.SeekOrigin7z.Current:
                target = _volumeStartOffsets[_currentIndex] + _volumePosition + offset;
                break;
            case SevenZipNative.SeekOrigin7z.End:
                target = _maxPosition + offset;
                break;
            default:
                return SevenZipNative.HResult.E_INVALIDARG;
        }

        if (target < 0) return SevenZipNative.HResult.E_INVALIDARG;

        // Find which volume contains target. Volumes are contiguous:
        //   vol[i] covers [volumeStartOffsets[i], volumeStartOffsets[i] + _volumeSize)
        // except the last one which only extends to _maxPosition.
        int volIdx = -1;
        for (var i = _volumes.Count - 1; i >= 0; i--)
        {
            if (target >= _volumeStartOffsets[i]) { volIdx = i; break; }
        }
        if (volIdx < 0) return SevenZipNative.HResult.E_INVALIDARG;

        // Don't let the handler seek beyond what we've actually written.
        if (target > _maxPosition) return SevenZipNative.HResult.E_INVALIDARG;

        var localOffset = target - _volumeStartOffsets[volIdx];
        try
        {
            _volumes[volIdx].Seek(localOffset, SeekOrigin.Begin);
            _currentIndex = volIdx;
            _volumePosition = localOffset;
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, target);
            return SevenZipNative.HResult.S_OK;
        }
        catch (Exception)
        {
            return SevenZipNative.HResult.E_FAIL;
        }
    }

    public int SetSize(long newSize)
    {
        if (!Current.CanSeek) return SevenZipNative.HResult.E_FAIL;
        // The 7z handler calls SetSize with the total stream length after the
        // final write — only truncate the LAST volume, since the others are
        // already at their final size.
        try
        {
            // Compute which volume the new size ends in.
            if (newSize < 0) return SevenZipNative.HResult.E_INVALIDARG;
            if (newSize == _maxPosition) return SevenZipNative.HResult.S_OK;

            // If the new size is less than current max, truncate the appropriate volume.
            // We don't expect SetSize to extend — the handler writes first, then may
            // call SetSize to trim the trailing volume.
            int volIdx = -1;
            for (var i = _volumes.Count - 1; i >= 0; i--)
            {
                if (newSize > _volumeStartOffsets[i]) { volIdx = i; break; }
            }
            if (volIdx < 0) return SevenZipNative.HResult.E_INVALIDARG;

            var localLen = newSize - _volumeStartOffsets[volIdx];
            _volumes[volIdx].SetLength(localLen);
            _maxPosition = newSize;
            return SevenZipNative.HResult.S_OK;
        }
        catch (Exception)
        {
            return SevenZipNative.HResult.E_FAIL;
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < _volumes.Count; i++)
        {
            try { _volumes[i].Dispose(); } catch { /* idempotent */ }
        }
        _volumes.Clear();
    }
}
