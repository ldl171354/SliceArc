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
            WriteUInt32(buffer, offset + 28,
                entry.IsDirectory ? Ole32.FILE_ATTRIBUTE_DIRECTORY : Ole32.FILE_ATTRIBUTE_NORMAL);

            if (entry.Modified is { } modified)
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + 48, 8), modified.ToFileTimeUtc());

            WriteUInt32(buffer, offset + 56, (uint)(entry.Size >> 32));
            WriteUInt32(buffer, offset + 60, (uint)(entry.Size & 0xFFFFFFFF));

            var name = entry.RelativeName.Replace('/', '\\');
            var characters = Math.Min(name.Length, 259);   // 260 WCHARs including the terminator
            for (var c = 0; c < characters; c++) WriteUInt16(buffer, offset + 64 + c * 2, name[c]);
        }

        return buffer;
    }

    // =====================================================================
    // IOleDataObject
    // =====================================================================

    public int GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.dwAspect != Ole32.DVASPECT_CONTENT) return Ole32.DV_E_DVASPECT;

        if (format.cfFormat == Formats.Descriptor && (format.tymed & Ole32.TYMED_HGLOBAL) != 0)
        {
            medium = GlobalMedium(BuildDescriptorBytes(_files));
            return Ole32.S_OK;
        }

        if (format.cfFormat == Formats.PreferredEffect && (format.tymed & Ole32.TYMED_HGLOBAL) != 0)
        {
            medium = GlobalMedium(BitConverter.GetBytes(Ole32.DROPEFFECT_COPY));
            return Ole32.S_OK;
        }

        if (format.cfFormat == Formats.Contents && (format.tymed & Ole32.TYMED_ISTREAM) != 0)
        {
            if (format.lindex < 0 || format.lindex >= _files.Count) return Ole32.DV_E_LINDEX;

            var stream = CreateStream(format.lindex);
            if (stream is null) return Ole32.STG_E_READFAULT;

            medium = new STGMEDIUM
            {
                tymed = Ole32.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IOleStream)),
                pUnkForRelease = IntPtr.Zero,
            };
            return Ole32.S_OK;
        }

        return Ole32.DV_E_FORMATETC;
    }

    public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Ole32.E_NOTIMPL;

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.dwAspect != Ole32.DVASPECT_CONTENT) return Ole32.DV_E_DVASPECT;

        if (format.cfFormat == Formats.Descriptor && (format.tymed & Ole32.TYMED_HGLOBAL) != 0)
            return Ole32.S_OK;

        if (format.cfFormat == Formats.PreferredEffect && (format.tymed & Ole32.TYMED_HGLOBAL) != 0)
            return Ole32.S_OK;

        if (format.cfFormat == Formats.Contents && (format.tymed & Ole32.TYMED_ISTREAM) != 0)
            return format.lindex >= -1 && format.lindex < _files.Count ? Ole32.S_OK : Ole32.DV_E_LINDEX;

        return Ole32.DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        return Ole32.E_NOTIMPL;
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
        ReleaseWhenIdle(cancelled ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(10));

    private void ReleaseWhenIdle(TimeSpan grace)
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
                    idle = _readers.Count == 0 || _readers.All(reader => reader.IsFinished);
                }

                if (idle || DateTime.UtcNow >= deadline) Dispose();
            }, null, 500, 500);
        }
    }

    /// <summary>Cancel every in-flight decode and drop the native roots.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _releaseTimer?.Dispose();
            _releaseTimer = null;

            foreach (var reader in _readers) reader.Dispose();
            _readers.Clear();
            _live.Clear();
        }

        Finished?.Invoke();
    }

    /// <summary>The clipboard format ids, resolved once per process.</summary>
    private static class Formats
    {
        public static readonly ushort Descriptor = (ushort)Ole32.RegisterClipboardFormat("FileGroupDescriptorW");
        public static readonly ushort Contents = (ushort)Ole32.RegisterClipboardFormat("FileContents");
        public static readonly ushort PreferredEffect = (ushort)Ole32.RegisterClipboardFormat("Preferred DropEffect");

        public static FORMATETC[] Enumerate() => new[]
        {
            new FORMATETC { cfFormat = Descriptor, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL },
            new FORMATETC { cfFormat = Contents, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_ISTREAM },
            new FORMATETC { cfFormat = PreferredEffect, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL },
        };
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
        var stat = new STATSTG
        {
            pwcsName = IntPtr.Zero,
            type = Ole32.STGTY_STREAM,
            cbSize = (ulong)_reader.Length,
        };
        Marshal.StructureToPtr(stat, pstatstg, false);
        return Ole32.S_OK;
    }

    public int Write(IntPtr pv, int cb, IntPtr pcbWritten) => Ole32.E_NOTIMPL;

    public int SetSize(long libNewSize) => Ole32.E_NOTIMPL;

    public int CopyTo(IntPtr pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => Ole32.E_NOTIMPL;

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
        // both harmless here.
        Ole32.OleInitialize(IntPtr.Zero);

        var source = new SliceArcDropSource();
        var dataPointer = Marshal.GetComInterfaceForObject(dataObject, typeof(IOleDataObject));
        var sourcePointer = Marshal.GetComInterfaceForObject(source, typeof(IDropSource));
        try
        {
            var hr = Ole32.DoDragDrop(dataPointer, sourcePointer, Ole32.DROPEFFECT_COPY, out var effect);
            if (hr == Ole32.DRAGDROP_S_DROP) return effect;
            if (hr == Ole32.DRAGDROP_S_CANCEL) return 0;
            throw new InvalidOperationException($"DoDragDrop 失败，HRESULT=0x{hr:X8}。");
        }
        finally
        {
            Marshal.Release(dataPointer);
            Marshal.Release(sourcePointer);
        }
    }
}
