using System.Runtime.InteropServices;
using SliceArc.Core.Extraction;

namespace SliceArc.Core.DragDrop;

/// <summary>One virtual file offered to the shell during a drag.</summary>
/// <param name="RelativeName">
/// Name the shell should create, relative to the drop target. May contain
/// backslashes for a directory tree ("sub\file.bin"); the shell creates the
/// intermediate folders, which is how Windows' own Compressed Folders drag-out
/// reproduces a tree.
/// </param>
/// <param name="ArchivePath">Path of the member inside the archive.</param>
/// <param name="Size">Uncompressed size, reported to the shell up front.</param>
/// <param name="Modified">Optional last-write time carried in the descriptor.</param>
public sealed record VirtualFileEntry(
    string RelativeName,
    string ArchivePath,
    long Size,
    DateTime? Modified)
{
    public bool IsDirectory { get; init; }
}

/// <summary>
/// IDataObject that serves archive members as if they were files on disk.
///
/// The shell asks for CFSTR_FILEDESCRIPTORW (a FILEGROUPDESCRIPTORW describing
/// the drop), then for CFSTR_FILECONTENTS per index, and copies from the IStream
/// we hand back straight into the destination file it just created. We never
/// write a byte ourselves, so a 40 GB member costs a bounded buffer and nothing
/// else - no %TEMP%, no scratch copy on the archive's drive, nothing.
///
/// One background decode per requested member. A solid archive therefore decodes
/// its block once per dragged file; that is the price of not buffering the whole
/// block, and it is the right trade for the normal case (drag one file out).
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class VirtualFileDataObject : IOleDataObject, IDisposable
{
    /// <summary>
    /// The BCL-flavoured view of this object, so in-process consumers can use the
    /// same object through System.Runtime.InteropServices.ComTypes.IDataObject.
    /// </summary>
    public System.Runtime.InteropServices.ComTypes.IDataObject AsBclDataObject() => new BclDataObject(this);

    private readonly string _archivePath;
    private readonly IReadOnlyList<VirtualFileEntry> _files;
    private readonly Action<EntryStreamProgress>? _progress;

    // Everything the native side holds a pointer to must stay rooted here: the
    // CCW's own reference is not enough to keep the managed object alive.
    private readonly List<object> _live = new();
    private readonly List<ArchiveEntryReader> _readers = new();
    private readonly object _gate = new();

    private Timer? _releaseTimer;
    private bool _disposed;

    /// <summary>Raised once the object has released every decode, i.e. the copy is over.</summary>
    public event Action? Finished;

    /// <summary>True while the shell may still be pulling bytes (decode running or
    /// a reader that has been handed out but not finished). MainWindow blocks the
    /// window close on this, borrowed from 7-Zip's k_SourceFlags_WaitFinish logic:
    /// killing the process mid-copy truncates the file the user is watching appear.</summary>
    public bool TransferInFlight
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return false;
                return _dragLoopActive || (_readers.Count > 0 && _readers.Any(r => !r.IsFinished));
            }
        }
    }

    private bool _dragLoopActive;

    /// <summary>Set by <see cref="DragOut"/> around the OLE loop so the object knows
    /// the drop target is still deciding even before the first stream is requested.</summary>
    internal void SetDragLoopActive(bool active)
    {
        lock (_gate) _dragLoopActive = active;
    }

    // ---- CF_HDROP compatibility fallback (learned from 7-Zip PanelDrag.cpp and Bandizip) ----
    // 7-Zip drags archive contents out with a *real* temp directory and CF_HDROP only.
    // Many targets (Sticky Notes, some upload dialogs, older apps) refuse virtual
    // FileGroupDescriptor data and ask CF_HDROP (format id 15). We keep the zero-temp
    // stream path as primary for Explorer, but answer CF_HDROP by extracting the
    // dragged items to a temp folder on demand - only when a target actually asks.
    private string? _hdropRoot;
    private IReadOnlyList<string>? _hdropPaths;
    private bool _usedTempFallback;
    private const long MaxFallbackBytes = 2L * 1024 * 1024 * 1024;
    // CF_HDROP stays OUT of EnumFormatEtc on purpose: if we advertise it, Explorer
    // prefers the real-path (temp-extracting) form and we lose the zero-temp property.
    // It is answered only when a target asks for it directly.
    private const ushort CF_HDROP = 15;

    public VirtualFileDataObject(
        string archivePath,
        IReadOnlyList<VirtualFileEntry> files,
        Action<EntryStreamProgress>? progress = null)
    {
        _archivePath = Path.GetFullPath(archivePath);
        _files = files;
        _progress = progress;
        TotalBytes = files.Sum(f => f.Size);
    }

    public int FileCount => _files.Count;

    public long TotalBytes { get; }

    public IReadOnlyList<VirtualFileEntry> Files => _files;

    // =====================================================================
    // Descriptor
    // =====================================================================

    /// <summary>Clipboard format id for CFSTR_FILEDESCRIPTORW.</summary>
    public static ushort FileDescriptorFormat => Formats.Descriptor;

    /// <summary>Clipboard format id for CFSTR_FILECONTENTS.</summary>
    public static ushort FileContentsFormat => Formats.Contents;

    /// <summary>Clipboard format id for CFSTR_PREFERREDDROPEFFECT.</summary>
    public static ushort PreferredDropEffectFormat => Formats.PreferredEffect;

    /// <summary>
    /// FILEGROUPDESCRIPTORW bytes, exactly as the shell receives them. Public so
    /// the layout can be asserted without going through COM.
    /// </summary>
    public static byte[] BuildDescriptorBytes(IReadOnlyList<VirtualFileEntry> files)
    {
        var buffer = new byte[Ole32.FileGroupDescriptorHeaderSize + Ole32.FileDescriptorSize * files.Count];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            var entry = files[i];
            var offset = Ole32.FileGroupDescriptorHeaderSize + Ole32.FileDescriptorSize * i;

            var flags = Ole32.FD_ATTRIBUTES | Ole32.FD_FILESIZE;
            if (entry.Modified is not null) flags |= Ole32.FD_WRITESTIME;

            WriteUInt32(buffer, offset, flags);
            // Field offsets are the ones ShlObj_core.h _FILEDESCRIPTORW actually has,
            // cross-checked with the CLR's own layout engine at Pack=4:
            //   0 dwFlags | 4 clsid | 20 sizl | 28 pointl | 36 dwFileAttributes
            //   40 ftCreation | 48 ftLastAccess | 56 ftLastWrite
            //   64 nFileSizeHigh | 68 nFileSizeLow | 72 cFileName
            // The 8-byte pointl was the one we skipped. With it missing the shell read
            // cFileName at 72 (we wrote 64), so it saw an empty name, and read the name
            // bytes as nFileSizeHigh/Low - it rejected the drop without ever asking for
            // FileContents, which is exactly the "drag does nothing" symptom.
            WriteUInt32(buffer, offset + 36,
                entry.IsDirectory ? Ole32.FILE_ATTRIBUTE_DIRECTORY : Ole32.FILE_ATTRIBUTE_NORMAL);

            if (entry.Modified is { } modified)
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + 56, 8), modified.ToFileTimeUtc());

            WriteUInt32(buffer, offset + 64, (uint)(entry.Size >> 32));
            WriteUInt32(buffer, offset + 68, (uint)(entry.Size & 0xFFFFFFFF));

            var name = entry.RelativeName.Replace('/', '\\');
            var characters = Math.Min(name.Length, 259);   // 260 WCHARs including the terminator
            for (var c = 0; c < characters; c++) WriteUInt16(buffer, offset + 72 + c * 2, name[c]);
        }

        return buffer;
    }

    // =====================================================================
    // IOleDataObject
    // =====================================================================

    public int GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        var hr = GetDataInner(ref format, out medium);
        ProbeLog?.Invoke($"GetData cf={NameOf(format.cfFormat)} lindex={format.lindex} -> 0x{hr:X8}" +
                        (hr == Ole32.S_OK ? $" medium={medium.tymed:X} {MediumInfo(in medium)}" : ""));
        return hr;
    }

    private static string MediumInfo(in STGMEDIUM m) => m.tymed switch
    {
        Ole32.TYMED_HGLOBAL => $"hglobal 大小={GlobalSizeOf(m.unionmember)}",
        Ole32.TYMED_ISTREAM => "IStream",
        _ => "medium",
    };

    [DllImport("ole32.dll")]
    private static extern int ReleaseStgMedium(ref STGMEDIUM pmedium);

    private static long GlobalSizeOf(IntPtr h) => h == IntPtr.Zero ? -1 : GlobalSize(h).ToInt64();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr h);

    private int GetDataInner(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.dwAspect != Ole32.DVASPECT_CONTENT) return Ole32.DV_E_DVASPECT;

        // Same "tymed == 0 means any medium" convention QueryGetData answers.
        var tymed = format.tymed == 0 ? AcceptedMedium(format.cfFormat) : format.tymed;
        ProbeLog?.Invoke($"GetData 请求 cf={NameOf(format.cfFormat)} tymed=0x{format.tymed:X} lindex={format.lindex} aspect={format.dwAspect}");

        if (format.cfFormat == Formats.Descriptor && (tymed & Ole32.TYMED_HGLOBAL) != 0)
        {
            medium = GlobalMedium(BuildDescriptorBytes(_files));
            return Ole32.S_OK;
        }

        if (format.cfFormat == Formats.PreferredEffect && (tymed & Ole32.TYMED_HGLOBAL) != 0)
        {
            medium = GlobalMedium(BitConverter.GetBytes(Ole32.DROPEFFECT_COPY));
            return Ole32.S_OK;
        }

        if (format.cfFormat == Formats.Contents && (tymed & Ole32.TYMED_ISTREAM) != 0)
        {
            if (format.lindex < 0 || format.lindex >= _files.Count) return Ole32.DV_E_LINDEX;

            var stream = CreateStream(format.lindex);
            if (stream is null) return Ole32.STG_E_READFAULT;
            _streamsRequested = true;

            medium = new STGMEDIUM
            {
                tymed = Ole32.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IOleStream)),
                pUnkForRelease = IntPtr.Zero,
            };
            return Ole32.S_OK;
        }

        // A target that only understands real paths (Sticky Notes, older apps, some
        // upload pickers) asks for CF_HDROP. Extract the dragged items to a temp
        // folder once and hand back that file list. This is 7-Zip/Bandizip's model;
        // we keep it off the common Explorer path so the normal drag stays zero-temp.
        if (format.cfFormat == CF_HDROP && (tymed & Ole32.TYMED_HGLOBAL) != 0)
        {
            var paths = EnsureTempExtracted();
            if (paths is null) return Ole32.STG_E_READFAULT;
            medium = BuildHDropMedium(paths);
            return Ole32.S_OK;
        }

        return Ole32.DV_E_FORMATETC;
    }

    public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Ole32.E_NOTIMPL;

    public int QueryGetData(ref FORMATETC format)
    {
        var answer = AnswerQuery(ref format);
        ProbeLog?.Invoke($"QueryGetData cf={NameOf(format.cfFormat)} tymed=0x{format.tymed:X} lindex={format.lindex} -> 0x{answer:X8}");
        return answer;
    }

    /// <summary>
    /// Records what the drop target actually asked for. Without this, "the drag did
    /// nothing" has no evidence behind it: the shell's probes are the only witness.
    /// Capped because a shell can ask dozens of times per drag.
    /// </summary>
    public static Action<string>? ProbeLog { get; set; }

    private int AnswerQuery(ref FORMATETC format)
    {
        if (format.dwAspect != Ole32.DVASPECT_CONTENT) return Ole32.DV_E_DVASPECT;

        // tymed == 0 means "any medium will do" - the convention most shell and
        // third-party targets use when probing. Answering DV_E_FORMATETC here makes
        // Explorer conclude there is nothing to copy: the drag shows no cursor and
        // DoDragDrop returns effect=0, i.e. "nothing happened". Mask against what we
        // actually offer instead of requiring an exact match.
        var tymed = format.tymed == 0 ? AcceptedMedium(format.cfFormat) : format.tymed;
        if (tymed == 0) return Ole32.DV_E_FORMATETC;

        if (format.cfFormat == Formats.Descriptor && (tymed & Ole32.TYMED_HGLOBAL) != 0)
            return Ole32.S_OK;

        if (format.cfFormat == Formats.PreferredEffect && (tymed & Ole32.TYMED_HGLOBAL) != 0)
            return Ole32.S_OK;

        if (format.cfFormat == Formats.Contents && (tymed & Ole32.TYMED_ISTREAM) != 0)
            // Must agree with GetData: claiming a stream exists at lindex=-1 and then
            // answering DV_E_LINDEX makes the shell treat the whole object as broken.
            return format.lindex >= 0 && format.lindex < _files.Count ? Ole32.S_OK : Ole32.DV_E_LINDEX;

        // CF_HDROP is advertised on purpose: it is the one shape Explorer, Desktop,
        // Sticky Notes and every upload picker handle the same way, and its copy runs
        // inside the drop instead of racing the release timer afterwards.
        if (format.cfFormat == CF_HDROP && (tymed & Ole32.TYMED_HGLOBAL) != 0)
            return Ole32.S_OK;

        return Ole32.DV_E_FORMATETC;
    }

    /// <summary>The mediums this object can serve for a given clipboard format.</summary>
    private static uint AcceptedMedium(ushort cfFormat) => cfFormat switch
    {
        var cf when cf == Formats.Descriptor => Ole32.TYMED_HGLOBAL,
        var cf when cf == Formats.PreferredEffect => Ole32.TYMED_HGLOBAL,
        var cf when cf == Formats.Contents => Ole32.TYMED_ISTREAM,
        var cf when cf == CF_HDROP => Ole32.TYMED_HGLOBAL,
        _ => 0,
    };

    /// <summary>Human-readable clipboard format name for the probe log.</summary>
    private static string NameOf(ushort cf) => cf switch
    {
        var x when x == Formats.Descriptor => "FileGroupDescriptorW",
        var x when x == Formats.Contents => "FileContents",
        var x when x == Formats.PreferredEffect => "PreferredDropEffect",
        var x when x == CF_HDROP => "CF_HDROP",
        _ => $"cf#{cf}",
    };

    /// <summary>
    /// Materialise every dragged entry into a temp folder ahead of the OLE drag loop.
    ///
    /// This is the shape 7-Zip, WinRAR and Bandizip all use, and the reason is not
    /// laziness: a CF_HDROP copy is performed by the *target* from real paths, so the
    /// files must already exist when the drop happens. Extracting lazily inside
    /// GetData blocks the drag loop on a multi-gigabyte decode with no progress and no
    /// way to cancel. Doing it up front keeps the UI responsive and lets us report
    /// per-entry progress.
    ///
    /// Returns the temp root (already populated) or null when the payload is too large
    /// or extraction failed; callers then fall back or report.
    /// </summary>
    public string? PrepareForDrag()
    {
        if (TotalBytes > MaxFallbackBytes)
        {
            ProbeLog?.Invoke($"预解压放弃：{TotalBytes} 字节超过 {MaxFallbackBytes} 上限");
            return null;
        }

        if (EnsureTempExtracted() is { } paths)
        {
            ProbeLog?.Invoke($"预解压完成：{paths.Count} 项，根目录 {_hdropRoot}");
            return _hdropRoot;
        }
        return null;
    }

    /// <summary>
    /// Extract the dragged items into a throwaway temp folder once, and remember it.
    /// Only runs when a target asked for CF_HDROP, so Explorer's normal zero-temp copy
    /// never pays this. 7-Zip creates such a temp folder up front ("7zE"); we create it
    /// on demand instead, which keeps the common path temp-free while staying compatible
    /// with targets that only understand real paths.
    /// </summary>
    private IReadOnlyList<string>? EnsureTempExtracted()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_hdropPaths is not null) return _hdropPaths;
        }

        if (TotalBytes > MaxFallbackBytes)
        {
            ProbeLog?.Invoke($"CF_HDROP 回退放弃：{TotalBytes} 字节超过 {MaxFallbackBytes} 上限");
            return null;
        }

        var root = Path.Combine(Path.GetTempPath(), "SliceArc-drag-" + Guid.NewGuid().ToString("N"));
        var paths = new List<string>();
        try
        {
            Directory.CreateDirectory(root);

            // Directory entries first, so an empty folder still materialises and the
            // hierarchy exists before files are written into it.
            foreach (var dir in _files.Where(f => f.IsDirectory))
            {
                var dirPath = Path.Combine(root, dir.RelativeName.Replace('/', '\\'));
                Directory.CreateDirectory(dirPath);
            }

            foreach (var file in _files.Where(f => !f.IsDirectory))
            {
                var target = Path.Combine(root, file.RelativeName.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var reader = new ArchiveEntryReader(_archivePath, file.ArchivePath, file.Size, _progress);
                var buffer = new byte[1 << 16];
                using (var fs = File.Create(target))
                {
                    while (true)
                    {
                        var n = reader.Read(buffer);
                        if (n <= 0) break;
                        fs.Write(buffer, 0, n);
                    }
                }
            }

            // Hand the shell the TOP-LEVEL items, not every leaf. A folder dropped as a
            // folder makes Explorer create that folder and recurse into it; handing over
            // the leaves alone reproduces the files but drops the folder level, which is
            // the "拖文件夹只解出里面的东西" symptom.
            var tops = _files
                .Select(f => f.RelativeName.Replace('/', '\\').TrimEnd('\\'))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !n.Contains('\\'))
                .ToList();

            foreach (var top in tops)
            {
                var full = Path.Combine(root, top);
                if (Directory.Exists(full) || File.Exists(full)) paths.Add(full);
            }
        }
        catch (Exception ex)
        {
            ProbeLog?.Invoke($"CF_HDROP 回退失败: {ex.Message}");
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            return null;
        }

        lock (_gate)
        {
            if (_disposed) return null;
            _usedTempFallback = true;
            _hdropRoot = root;
            _hdropPaths = paths;
            ProbeLog?.Invoke($"CF_HDROP 回退：已解压 {paths.Count} 项到 {root}");
            return paths;
        }
    }

    /// <summary>CF_HDROP payload: DROPFILES header then the double-null WCHAR list.</summary>
    private static STGMEDIUM BuildHDropMedium(IReadOnlyList<string> paths)
    {
        const int headerSize = 20; // pFiles, pt.x, pt.y, fNC, fWide
        var chars = new List<char>();
        foreach (var p in paths)
        {
            chars.AddRange(p.ToCharArray());
            chars.Add('\0');
        }
        chars.Add('\0');

        var byteLen = headerSize + chars.Count * 2;
        var handle = Ole32.GlobalAlloc(Ole32.GMEM_MOVEABLE, (UIntPtr)byteLen);
        var ptr = Ole32.GlobalLock(handle);
        try
        {
            Span<byte> header = stackalloc byte[headerSize];
            BitConverter.TryWriteBytes(header[0..4], headerSize); // pFiles: list starts right after
            BitConverter.TryWriteBytes(header[16..20], 1);        // fWide = TRUE
            for (var i = 0; i < headerSize; i++) Marshal.WriteByte(ptr, i, header[i]);
            for (var i = 0; i < chars.Count; i++)
                Marshal.WriteInt16(ptr, headerSize + i * 2, chars[i]);
        }
        finally
        {
            Ole32.GlobalUnlock(handle);
        }

        return new STGMEDIUM
        {
            tymed = Ole32.TYMED_HGLOBAL,
            unionmember = handle,
            pUnkForRelease = IntPtr.Zero,
        };
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        return Ole32.E_NOTIMPL;
    }

    /// <summary>
    /// The BCL view of this data object, for in-process consumers.
    ///
    /// The shell talks to us through the OLE COM interface, but an in-process
    /// consumer (WPF's own drop path, or a test host) gets the *managed* object back
    /// from Marshal.GetObjectForIUnknown and then casts it to
    /// <see cref="System.Runtime.InteropServices.ComTypes.IDataObject"/>. Without
    /// that interface the cast throws InvalidCastException and the drop looks like it
    /// delivered nothing - AGENTS 7.16 records this exact trap. A separate adapter
    /// keeps the two FORMATETC struct types from colliding in one class.
    /// </summary>
    internal sealed class BclDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private readonly VirtualFileDataObject _owner;
        public BclDataObject(VirtualFileDataObject owner) => _owner = owner;

        void System.Runtime.InteropServices.ComTypes.IDataObject.GetData(
            ref System.Runtime.InteropServices.ComTypes.FORMATETC format,
            out System.Runtime.InteropServices.ComTypes.STGMEDIUM medium)
        {
            var f = ToOle(format);
            var hr = _owner.GetDataInner(ref f, out var m);
            if (hr != Ole32.S_OK) Marshal.ThrowExceptionForHR(hr);
            medium = new System.Runtime.InteropServices.ComTypes.STGMEDIUM
            {
                tymed = (System.Runtime.InteropServices.ComTypes.TYMED)m.tymed,
                unionmember = m.unionmember,
                pUnkForRelease = m.pUnkForRelease,
            };
        }

        void System.Runtime.InteropServices.ComTypes.IDataObject.GetDataHere(
            ref System.Runtime.InteropServices.ComTypes.FORMATETC format,
            ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium)
        {
            var f = ToOle(format);
            var m = ToOle(medium);
            Marshal.ThrowExceptionForHR(_owner.GetDataHere(ref f, ref m));
        }

        int System.Runtime.InteropServices.ComTypes.IDataObject.QueryGetData(
            ref System.Runtime.InteropServices.ComTypes.FORMATETC format)
        {
            var f = ToOle(format);
            return _owner.AnswerQuery(ref f);
        }

        int System.Runtime.InteropServices.ComTypes.IDataObject.DAdvise(
            ref System.Runtime.InteropServices.ComTypes.FORMATETC format,
            System.Runtime.InteropServices.ComTypes.ADVF advf,
            System.Runtime.InteropServices.ComTypes.IAdviseSink advSink, out int connection)
        {
            connection = 0;
            return Ole32.OLE_E_ADVISENOTSUPPORTED;
        }

        void System.Runtime.InteropServices.ComTypes.IDataObject.DUnadvise(int connection)
            => Marshal.ThrowExceptionForHR(Ole32.OLE_E_ADVISENOTSUPPORTED);

        int System.Runtime.InteropServices.ComTypes.IDataObject.EnumDAdvise(
            out System.Runtime.InteropServices.ComTypes.IEnumSTATDATA enumAdvise)
        {
            enumAdvise = null;
            return Ole32.OLE_E_ADVISENOTSUPPORTED;
        }

        void System.Runtime.InteropServices.ComTypes.IDataObject.SetData(
            ref System.Runtime.InteropServices.ComTypes.FORMATETC format,
            ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium, bool release)
            => Marshal.ThrowExceptionForHR(Ole32.E_NOTIMPL);

        int System.Runtime.InteropServices.ComTypes.IDataObject.GetCanonicalFormatEtc(
            ref System.Runtime.InteropServices.ComTypes.FORMATETC formatIn,
            out System.Runtime.InteropServices.ComTypes.FORMATETC formatOut)
        {
            formatOut = formatIn;
            return Ole32.E_NOTIMPL;
        }

        System.Runtime.InteropServices.ComTypes.IEnumFORMATETC
            System.Runtime.InteropServices.ComTypes.IDataObject.EnumFormatEtc(
                System.Runtime.InteropServices.ComTypes.DATADIR direction)
            => new BclFormatEnumerator(Formats.Enumerate());

        private static FORMATETC ToOle(System.Runtime.InteropServices.ComTypes.FORMATETC f) => new()
        {
            cfFormat = (ushort)f.cfFormat,
            ptd = f.ptd,
            dwAspect = (uint)f.dwAspect,
            lindex = f.lindex,
            tymed = (uint)f.tymed,
        };

        private static STGMEDIUM ToOle(System.Runtime.InteropServices.ComTypes.STGMEDIUM m) => new()
        {
            tymed = (uint)m.tymed,
            unionmember = m.unionmember,
            // BCL holds the release sink as object; the OLE struct wants the raw
            // interface pointer (null when nobody owns it).
            pUnkForRelease = m.pUnkForRelease is null
                ? IntPtr.Zero
                : Marshal.GetIUnknownForObject(m.pUnkForRelease),
        };
    }

    public int SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => Ole32.E_NOTIMPL;

    public int EnumFormatEtc(uint direction, out IntPtr enumFormatEtc)
    {
        enumFormatEtc = IntPtr.Zero;
        if (direction != Ole32.DATADIR_GET) return Ole32.E_NOTIMPL;

        var enumerator = new FormatEnumerator(Formats.Enumerate(), Root);
        _live.Add(enumerator);
        enumFormatEtc = Marshal.GetComInterfaceForObject(enumerator, typeof(IEnumFORMATETC));
        return Ole32.S_OK;
    }

    public int DAdvise(ref FORMATETC format, uint advf, IntPtr advSink, out uint connection)
    {
        connection = 0;
        return Ole32.OLE_E_ADVISENOTSUPPORTED;
    }

    public int DUnadvise(uint connection) => Ole32.OLE_E_ADVISENOTSUPPORTED;

    public int EnumDAdvise(out IntPtr enumAdvise)
    {
        enumAdvise = IntPtr.Zero;
        return Ole32.OLE_E_ADVISENOTSUPPORTED;
    }

    // =====================================================================
    // Streams
    // =====================================================================

    private VirtualFileStream? CreateStream(int index)
    {
        var file = _files[index];
        var reader = new ArchiveEntryReader(_archivePath, file.ArchivePath, file.Size, _progress);
        reader.Start();

        var stream = new VirtualFileStream(reader, file.RelativeName);

        lock (_gate)
        {
            if (_disposed) return null;
            _readers.Add(reader);
            _live.Add(stream);
        }

        return stream;
    }

    private void Root(object target)
    {
        lock (_gate) _live.Add(target);
    }

    private static STGMEDIUM GlobalMedium(byte[] payload)
    {
        var handle = Ole32.GlobalAlloc(Ole32.GMEM_MOVEABLE, (UIntPtr)(uint)payload.Length);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException("无法为拖拽描述符分配全局内存。");

        var pointer = Ole32.GlobalLock(handle);
        try
        {
            Marshal.Copy(payload, 0, pointer, payload.Length);
        }
        finally
        {
            Ole32.GlobalUnlock(handle);
        }

        return new STGMEDIUM
        {
            tymed = Ole32.TYMED_HGLOBAL,
            unionmember = handle,
            pUnkForRelease = IntPtr.Zero,
        };
    }

    private static void WriteUInt16(byte[] buffer, int offset, char value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), (ushort)value);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);

    /// <summary>
    /// Called once the OLE drag loop has returned.
    ///
    /// The object must NOT be disposed at that moment: for a successful drop the
    /// shell keeps reading the streams while it writes the destination files, and
    /// those reads keep arriving after DoDragDrop returns. So the object releases
    /// itself as soon as no decode is running any more, with a hard deadline as a
    /// backstop for a target that never asks for anything.
    /// </summary>
    public void MarkDragFinished(bool cancelled) =>
        ReleaseWhenIdle(cancelled ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(10), cancelled);

    private void ReleaseWhenIdle(TimeSpan grace, bool cancelled)
    {
        var deadline = DateTime.UtcNow + grace;

        lock (_gate)
        {
            if (_disposed) return;
            _releaseTimer ??= new Timer(_ =>
            {
                bool idle;
                lock (_gate)
                {
                    if (_disposed) return;
                    // A successful drop means the shell may start pulling AFTER
                    // DoDragDrop returned (its copy runs on its own thread). While the
                    // readers list is still empty we cannot conclude "idle" - the
                    // earlier behaviour disposed instantly and Explorer's late pull got
                    // STG_E_READFAULT: effect=1, zero-byte result.
                    //
                    // With CF_HDROP as the primary shape there is no stream to watch:
                    // Explorer copies from the temp folder on its own schedule, so the
                    // temp folder must survive until the deadline regardless. Never
                    // report idle in that mode.
                    var streamIdle = _streamsRequested && _readers.Count > 0 &&
                                     _readers.All(reader => reader.IsFinished);
                    idle = cancelled || (streamIdle && !_usedTempFallback);
                }

                if (idle || DateTime.UtcNow >= deadline) Dispose();
            }, null, 500, 500);
        }
    }

    private bool _streamsRequested;

    /// <summary>Cancel every in-flight decode and drop the native roots.</summary>
    public void Dispose()
    {
        string? tempRoot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _releaseTimer?.Dispose();
            _releaseTimer = null;

            foreach (var reader in _readers) reader.Dispose();
            _readers.Clear();
            _live.Clear();
            tempRoot = _hdropRoot;
        }

        // The temp folder backs a CF_HDROP fallback: it must live until the target
        // finished copying (Dispose only runs once the release timer sees no stream in
        // flight), so deleting here is the safe point, mirroring 7-Zip's own cleanup.
        if (tempRoot is not null)
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch (Exception ex) { ProbeLog?.Invoke($"临时目录清理失败 {tempRoot}: {ex.Message}"); }
        }

        Finished?.Invoke();
    }

    /// <summary>The clipboard format ids, resolved once per process.</summary>
    private static class Formats
    {
        public static readonly ushort Descriptor = (ushort)Ole32.RegisterClipboardFormat("FileGroupDescriptorW");
        public static readonly ushort Contents = (ushort)Ole32.RegisterClipboardFormat("FileContents");
        public static readonly ushort PreferredEffect = (ushort)Ole32.RegisterClipboardFormat("Preferred DropEffect");

        /// <summary>
        /// What the drop target is offered, in priority order.
        ///
        /// CF_HDROP leads because that is the only shape every Windows drop target
        /// really honours: 7-Zip, WinRAR and Bandizip all hand the shell a plain file
        /// list and let it copy from real paths. Advertising the virtual
        /// FileContents/IStream shape first made Explorer accept the drop (effect=1)
        /// and then produce nothing on disk, because its copy engine reads those
        /// streams after DoDragDrop has already returned and the decoders were gone.
        /// The virtual shape is still answered when a target asks for it directly.
        /// </summary>
        public static FORMATETC[] Enumerate() => new[]
        {
            new FORMATETC { cfFormat = CF_HDROP, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL },
            new FORMATETC { cfFormat = Descriptor, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL },
            new FORMATETC { cfFormat = Contents, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_ISTREAM },
            new FORMATETC { cfFormat = PreferredEffect, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL },
        };
    }

    /// <summary>
    /// The BCL flavour of the same format list, for in-process consumers that cast
    /// the object to <see cref="System.Runtime.InteropServices.ComTypes.IDataObject"/>
    /// instead of going through the OLE vtable.
    /// </summary>
    private sealed class BclFormatEnumerator : System.Runtime.InteropServices.ComTypes.IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _index;

        public BclFormatEnumerator(FORMATETC[] formats) => _formats = formats;

        public int Next(int celt, System.Runtime.InteropServices.ComTypes.FORMATETC[] rgelt, int[] pceltFetched)
        {
            var n = 0;
            while (n < celt && _index < _formats.Length)
            {
                var f = _formats[_index++];
                rgelt[n++] = new System.Runtime.InteropServices.ComTypes.FORMATETC
                {
                    cfFormat = (short)f.cfFormat,
                    dwAspect = (System.Runtime.InteropServices.ComTypes.DVASPECT)f.dwAspect,
                    lindex = f.lindex,
                    tymed = (System.Runtime.InteropServices.ComTypes.TYMED)f.tymed,
                    ptd = f.ptd,
                };
            }
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = n;
            return n == celt ? 0 : 1; // S_OK / S_FALSE
        }

        public int Skip(int celt)
        {
            _index = Math.Min(_index + celt, _formats.Length);
            return _index < _formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public void Clone(out System.Runtime.InteropServices.ComTypes.IEnumFORMATETC newEnum)
            => newEnum = new BclFormatEnumerator(_formats) { _index = _index };
    }
}

/// <summary>
/// The IStream the shell copies from. Read pulls decoded bytes out of an
/// <see cref="ArchiveEntryReader"/>; Seek is supported because the drop target is
/// entitled to it, but the normal copy loop only reads forward.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class VirtualFileStream : IOleStream, IDisposable
{
    private const int MaxChunk = 1 << 20;

    private readonly ArchiveEntryReader _reader;
    private byte[] _scratch = new byte[64 * 1024];
    private bool _disposed;

    public VirtualFileStream(ArchiveEntryReader reader, string name)
    {
        _reader = reader;
        Name = name;
    }

    public string Name { get; }

    public int Read(IntPtr pv, int cb, IntPtr pcbRead)
    {
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, 0);
        if (_disposed) return Ole32.STG_E_READFAULT;
        if (cb <= 0 || pv == IntPtr.Zero) return Ole32.S_OK;

        var want = Math.Min(cb, MaxChunk);
        if (_scratch.Length < want) _scratch = new byte[want];

        int read;
        try
        {
            read = _reader.Read(_scratch.AsSpan(0, want));
        }
        catch (Exception ex)
        {
            // Surfacing a failure here is what makes the shell abort the copy
            // and tell the user, instead of writing a silently truncated file.
            System.Diagnostics.Debug.WriteLine($"[SliceArc] 拖拽读取失败: {ex.Message}");
            return Ole32.STG_E_READFAULT;
        }

        if (read > 0) Marshal.Copy(_scratch, 0, pv, read);
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
        return Ole32.S_OK;
    }

    public int Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var target = dwOrigin switch
        {
            Ole32.STREAM_SEEK_SET => dlibMove,
            Ole32.STREAM_SEEK_CUR => _reader.Position + dlibMove,
            Ole32.STREAM_SEEK_END => _reader.Length + dlibMove,
            _ => long.MinValue,
        };

        if (target == long.MinValue || target < 0) return Ole32.STG_E_INVALIDFUNCTION;

        try
        {
            _reader.Seek(target);
        }
        catch (Exception)
        {
            return Ole32.STG_E_INVALIDFUNCTION;
        }

        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _reader.Position);
        return Ole32.S_OK;
    }

    public int Stat(IntPtr pstatstg, int grfStatFlag)
    {
        if (pstatstg == IntPtr.Zero) return Ole32.STG_E_INVALIDPOINTER;

        // The whole structure is written, not just the two fields we know about:
        // the shell reads pwcsName and would otherwise walk into garbage.
        // STATFLAG_DEFAULT means the caller wants the stream name and frees it with
        // CoTaskMemFree. S_OK with a NULL name is a contract violation the shell
        // copy engine takes badly.
        var namePtr = IntPtr.Zero;
        if ((grfStatFlag & (int)Ole32.STATFLAG_NONAME) == 0 && Name.Length > 0)
            namePtr = Marshal.StringToCoTaskMemUni(Name);

        var stat = new STATSTG
        {
            pwcsName = namePtr,
            type = Ole32.STGTY_STREAM,
            cbSize = (ulong)_reader.Length,
        };
        Marshal.StructureToPtr(stat, pstatstg, false);
        return Ole32.S_OK;
    }

    public int Write(IntPtr pv, int cb, IntPtr pcbWritten) => Ole32.E_NOTIMPL;

    public int SetSize(long libNewSize) => Ole32.E_NOTIMPL;

    /// <summary>
    /// The shell copy engine uses CopyTo; refusing it aborts the copy. Implemented
    /// on top of the same reader as Read, so the byte accounting stays honest.
    /// </summary>
    public int CopyTo(IntPtr pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        if (pstm == IntPtr.Zero) return Ole32.STG_E_INVALIDPOINTER;
        if (_disposed) return Ole32.STG_E_READFAULT;

        var target = (System.Runtime.InteropServices.ComTypes.IStream)
            Marshal.GetObjectForIUnknown(pstm);

        var want = cb < 0 ? long.MaxValue : cb;
        var buffer = new byte[Math.Min(1 << 20, Math.Max(1, (long)want))];
        long moved = 0;
        try
        {
            while (moved < want)
            {
                int take = _reader.Read(buffer.AsSpan(0, buffer.Length));
                if (take <= 0) break;
                target.Write(buffer, take, IntPtr.Zero);
                moved += take;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SliceArc] 拖拽 CopyTo 失败: {ex.Message}");
            return Ole32.STG_E_READFAULT;
        }

        if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, moved);
        if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, moved);
        return Ole32.S_OK;
    }

    public int Commit(int grfCommitFlags) => Ole32.E_NOTIMPL;

    public int Revert() => Ole32.E_NOTIMPL;

    public int LockRegion(long libOffset, long cb, int dwLockType) => Ole32.E_NOTIMPL;

    public int UnlockRegion(long libOffset, long cb, int dwLockType) => Ole32.E_NOTIMPL;

    public int Clone(IntPtr ppstm) => Ole32.E_NOTIMPL;

    public void Dispose() => _disposed = true;
}

/// <summary>IEnumFORMATETC over the three formats a virtual file drop offers.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class FormatEnumerator : IEnumFORMATETC
{
    private readonly FORMATETC[] _formats;
    private readonly Action<object>? _root;
    private int _index;

    public FormatEnumerator(FORMATETC[] formats, Action<object>? root = null, int index = 0)
    {
        _formats = formats;
        _root = root;
        _index = index;
    }

    public int Next(uint celt, IntPtr rgelt, IntPtr pceltFetched)
    {
        if (rgelt == IntPtr.Zero) return Ole32.STG_E_INVALIDPOINTER;

        var size = Marshal.SizeOf<FORMATETC>();
        var written = 0;
        while (written < celt && _index < _formats.Length)
        {
            Marshal.StructureToPtr(_formats[_index], rgelt + written * size, false);
            _index++;
            written++;
        }

        if (pceltFetched != IntPtr.Zero) Marshal.WriteInt32(pceltFetched, written);
        return written == celt ? Ole32.S_OK : 1;   // S_FALSE when fewer remain
    }

    public int Skip(uint celt)
    {
        _index = (int)Math.Min(_formats.Length, _index + (int)celt);
        return Ole32.S_OK;
    }

    public int Reset()
    {
        _index = 0;
        return Ole32.S_OK;
    }

    public int Clone(out IntPtr ppenum)
    {
        var clone = new FormatEnumerator(_formats, _root, _index);
        _root?.Invoke(clone);
        ppenum = Marshal.GetComInterfaceForObject(clone, typeof(IEnumFORMATETC));
        return Ole32.S_OK;
    }
}

/// <summary>
/// IDropSource: the cursor half of the drag. Escape cancels, releasing the
/// mouse button drops, and the shell keeps its own default cursors.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SliceArcDropSource : IDropSource
{
    public int QueryContinueDrag(bool escapePressed, uint keyState)
    {
        if (escapePressed) return Ole32.DRAGDROP_S_CANCEL;
        return (keyState & Ole32.MK_LBUTTON) == 0 ? Ole32.DRAGDROP_S_DROP : Ole32.S_OK;
    }

    public int GiveFeedback(uint dwEffect) => Ole32.DRAGDROP_S_USEDEFAULTCURSORS;
}

/// <summary>Runs the OLE drag loop for a virtual file drop.</summary>
public static class DragOut
{
    /// <summary>Set by the host to receive a diagnostic trail of each drag.</summary>
    public static Action<string>? Diagnostics { get; set; }

    /// <summary>
    /// Blocking: returns the effect the target accepted (0 when the drag was
    /// cancelled or dropped somewhere that refused it). Must be called from an
    /// STA thread with a message pump - the WPF UI thread qualifies.
    /// </summary>
    public static uint DoDragDrop(VirtualFileDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);

        // DoDragDrop needs OLE initialised on this thread. WPF usually has it,
        // but a CLI-started process may not; S_FALSE and RPC_E_CHANGED_MODE are
        // both harmless here. A real failure here is what makes a drag silently
        // do nothing, so it is worth recording instead of swallowing.
        var oleHr = Ole32.OleInitialize(IntPtr.Zero);
        Diagnostics?.Invoke($"OleInitialize hr=0x{oleHr:X8}");

        var source = new SliceArcDropSource();
        var dataPointer = Marshal.GetComInterfaceForObject(dataObject, typeof(IOleDataObject));
        var sourcePointer = Marshal.GetComInterfaceForObject(source, typeof(IDropSource));
        try
        {
            dataObject.SetDragLoopActive(true);
            Diagnostics?.Invoke($"DoDragDrop 开始：{dataObject.FileCount} 个文件 / {dataObject.TotalBytes} 字节");
            var hr = Ole32.DoDragDrop(dataPointer, sourcePointer, Ole32.DROPEFFECT_COPY, out var effect);
            Diagnostics?.Invoke($"DoDragDrop 返回 hr=0x{hr:X8} effect={effect}");
            if (hr == Ole32.DRAGDROP_S_DROP) return effect;
            if (hr == Ole32.DRAGDROP_S_CANCEL) return 0;
            throw new InvalidOperationException($"DoDragDrop 失败，HRESULT=0x{hr:X8}。");
        }
        finally
        {
            dataObject.SetDragLoopActive(false);
            Marshal.Release(dataPointer);
            Marshal.Release(sourcePointer);
        }
    }
}
