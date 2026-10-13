using System.Runtime.InteropServices;
using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using SliceArc.Core.DragDrop;

namespace SliceArc.DropProbe;

/// <summary>
/// A minimal data object offering a real CF_HDROP - the same clipboard format
/// Explorer uses when you drag files - built straight into an HGLOBAL.
///
/// The clipboard is deliberately not touched: writing file paths there would
/// clobber whatever the user had copied, and a drop does not need it.
///
/// It implements TWO data object interfaces, and both are required:
///
///   * <see cref="IOleDataObject"/> (the app's own declaration, IID 0000010E) is
///     what the CCW exposes, i.e. what a *cross-process* drop target sees.
///   * <see cref="ComTypes.IDataObject"/> is what WPF casts to *in-process*.
///     Inside one process the CLR identity map returns this very managed object
///     for its own CCW, so WPF never goes through COM and simply type-checks the
///     instance; without this interface DragEnter fails with E_NOINTERFACE.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class HdropDataObject : IOleDataObject, ComTypes.IDataObject
{
    /// <summary>CF_HDROP - the "file group" format the shell and WPF both read.</summary>
    private const ushort CfHDrop = 15;

    private const uint TymedHGlobal = 1;
    private const uint GmemMoveable = 0x0002;
    private const int DvEFormatetc = unchecked((int)0x80040064);
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);

    /// <summary>DROPFILES header size: DWORD + POINT + BOOL + BOOL.</summary>
    private const int DropFilesHeaderSize = 20;

    private readonly string[] _files;

    /// <summary>Diagnostic: shows exactly which methods the drop target calls.</summary>
    private static void Trace(string message) => Console.Error.WriteLine($"[data] {message}");

    public HdropDataObject(IReadOnlyList<string> files) => _files = files.ToArray();

    // =====================================================================
    // The app's IOleDataObject (what the CCW exposes across processes)
    // =====================================================================

    public int GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat != CfHDrop) return DvEFormatetc;
        if ((format.tymed & TymedHGlobal) == 0) return DvEFormatetc;

        medium = new STGMEDIUM
        {
            tymed = TymedHGlobal,
            unionmember = BuildDropFiles(),
            pUnkForRelease = IntPtr.Zero,
        };
        return 0;
    }

    public int QueryGetData(ref FORMATETC format) =>
        format.cfFormat == CfHDrop && (format.tymed & TymedHGlobal) != 0 ? 0 : DvEFormatetc;

    public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => ENotImpl;

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        return ENotImpl;
    }

    public int SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => ENotImpl;

    public int EnumFormatEtc(uint direction, out IntPtr enumFormatEtc)
    {
        enumFormatEtc = IntPtr.Zero;
        return ENotImpl;
    }

    public int DAdvise(ref FORMATETC format, uint advf, IntPtr advSink, out uint connection)
    {
        connection = 0;
        return OleEAdviseNotSupported;
    }

    public int DUnadvise(uint connection) => OleEAdviseNotSupported;

    public int EnumDAdvise(out IntPtr enumAdvise)
    {
        enumAdvise = IntPtr.Zero;
        return OleEAdviseNotSupported;
    }

    // =====================================================================
    // The BCL interface WPF type-checks in-process
    // =====================================================================

    void ComTypes.IDataObject.GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        Trace($"ComTypes.GetData cf={format.cfFormat} tymed={format.tymed}");
        if (format.cfFormat != (short)CfHDrop)
            throw new COMException("only CF_HDROP is offered", DvEFormatetc);

        medium = new ComTypes.STGMEDIUM
        {
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
            unionmember = BuildDropFiles(),
            pUnkForRelease = null,
        };
    }

    int ComTypes.IDataObject.QueryGetData(ref ComTypes.FORMATETC format)
    {
        Trace($"ComTypes.QueryGetData cf={format.cfFormat} tymed={format.tymed}");
        return format.cfFormat == (short)CfHDrop ? 0 : DvEFormatetc;
    }

    int ComTypes.IDataObject.GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)    {
        formatOut = default;
        return ENotImpl;
    }

    void ComTypes.IDataObject.GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium)
    {
        Trace($"ComTypes.GetDataHere cf={format.cfFormat} tymed={format.tymed}");
        throw new NotSupportedException();
    }

    void ComTypes.IDataObject.SetData(ref ComTypes.FORMATETC formatIn, ref ComTypes.STGMEDIUM medium, bool release) =>
        throw new NotSupportedException();

    ComTypes.IEnumFORMATETC ComTypes.IDataObject.EnumFormatEtc(ComTypes.DATADIR direction)
    {
        Trace($"ComTypes.EnumFormatEtc dir={direction}");
        if (direction != ComTypes.DATADIR.DATADIR_GET) return null!;

        // Must not be null: WPF builds its format list from this enumeration and
        // never asks for the data if the list comes back empty.
        return new SingleFormatEnumerator(new ComTypes.FORMATETC
        {
            cfFormat = (short)CfHDrop,
            ptd = IntPtr.Zero,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        });
    }

    int ComTypes.IDataObject.DAdvise(ref ComTypes.FORMATETC pFormatetc, ComTypes.ADVF advf,
        ComTypes.IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OleEAdviseNotSupported;
    }

    void ComTypes.IDataObject.DUnadvise(int connection)
    {
        // No advisory connections are offered.
    }

    int ComTypes.IDataObject.EnumDAdvise(out ComTypes.IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return OleEAdviseNotSupported;
    }

    // =====================================================================

    /// <summary>Build the DROPFILES structure: header, then a double-null-terminated wide list.</summary>
    private IntPtr BuildDropFiles()
    {
        var list = new StringBuilder();
        foreach (var file in _files) list.Append(file).Append('\0');
        list.Append('\0');

        var nameBytes = Encoding.Unicode.GetBytes(list.ToString());
        var size = DropFilesHeaderSize + nameBytes.Length;

        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)size);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc for CF_HDROP failed.");

        var pointer = GlobalLock(handle);
        try
        {
            Marshal.WriteInt32(pointer, 0, DropFilesHeaderSize);  // pFiles: offset of the list
            Marshal.WriteInt32(pointer, 4, 0);                    // pt.x
            Marshal.WriteInt32(pointer, 8, 0);                    // pt.y
            Marshal.WriteInt32(pointer, 12, 0);                   // fNC = FALSE
            Marshal.WriteInt32(pointer, 16, 1);                   // fWide = TRUE
            Marshal.Copy(nameBytes, 0, pointer + DropFilesHeaderSize, nameBytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>The one format this object offers, as an enumerator WPF can walk.</summary>
    private sealed class SingleFormatEnumerator : ComTypes.IEnumFORMATETC
    {
        private readonly ComTypes.FORMATETC[] _formats;
        private int _index;

        public SingleFormatEnumerator(params ComTypes.FORMATETC[] formats) => _formats = formats;

        public int Next(int celt, ComTypes.FORMATETC[] rgelt, int[] pceltFetched)
        {
            var written = 0;
            while (written < celt && _index < _formats.Length && written < rgelt.Length)
            {
                rgelt[written] = _formats[_index];
                written++;
                _index++;
            }

            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = written;
            return written == celt ? 0 : 1;   // S_FALSE when fewer remain
        }

        public int Skip(int celt)
        {
            _index = Math.Min(_formats.Length, _index + celt);
            return 0;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public void Clone(out ComTypes.IEnumFORMATETC newEnum)
        {
            newEnum = new SingleFormatEnumerator(_formats) { _index = _index };
        }
    }
}
