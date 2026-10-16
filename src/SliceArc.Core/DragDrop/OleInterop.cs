using System.Runtime.InteropServices;

namespace SliceArc.Core.DragDrop;

// =========================================================================
// The OLE drag-and-drop surface, declared by hand.
//
// Same rules as the 7-Zip interfaces in SliceArc.Core.SevenZip: everything is
// flat (no interface inheritance), every out parameter is an IntPtr, and every
// slot carries [PreserveSig] so a non-zero HRESULT crosses the boundary instead
// of turning into an exception before the shell sees it.
//
// The interfaces we *implement* are plain (no [ComImport]); the CLR builds the
// CCW vtable from the declaration order, which is load-bearing.
// =========================================================================

/// <summary>FORMATETC (objidl.h). 32 bytes on x64, 20 on x86 - the CLR pads it identically.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FORMATETC
{
    public ushort cfFormat;
    public IntPtr ptd;
    public uint dwAspect;
    public int lindex;
    public uint tymed;
}

/// <summary>STGMEDIUM (objidl.h): 24 bytes on x64, 12 on x86.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct STGMEDIUM
{
    public uint tymed;
    public IntPtr unionmember;
    public IntPtr pUnkForRelease;
}

/// <summary>
/// STATSTG (objidl.h). Declared as a struct so the CLR computes the layout and
/// writes the whole thing - the shell reads fields we never set (notably
/// pwcsName), so a partially filled structure would hand it garbage.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct STATSTG
{
    public IntPtr pwcsName;
    public uint type;
    public ulong cbSize;
    public long mtime;
    public long ctime;
    public long atime;
    public uint grfMode;
    public uint grfLocksSupported;
    public Guid clsid;
    public uint grfStateBits;
    public uint reserved;
}

/// <summary>IDataObject (0000010E-...). We implement this to serve virtual files.</summary>
[ComVisible(true)]
[Guid("0000010E-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleDataObject
{
    [PreserveSig]
    int GetData(ref FORMATETC format, out STGMEDIUM medium);

    [PreserveSig]
    int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium);

    [PreserveSig]
    int QueryGetData(ref FORMATETC format);

    [PreserveSig]
    int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut);

    [PreserveSig]
    int SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release);

    [PreserveSig]
    int EnumFormatEtc(uint direction, out IntPtr enumFormatEtc);

    [PreserveSig]
    int DAdvise(ref FORMATETC format, uint advf, IntPtr advSink, out uint connection);

    [PreserveSig]
    int DUnadvise(uint connection);

    [PreserveSig]
    int EnumDAdvise(out IntPtr enumAdvise);
}

/// <summary>
/// IStream (0000000C-...). Flattened: the real vtable is
/// [QueryInterface, AddRef, Release, Read, Write, Seek, SetSize, CopyTo, Commit,
/// Revert, LockRegion, UnlockRegion, Stat, Clone] - ISequentialStream's two
/// slots come first, which is why Read/Write sit at positions 3 and 4.
/// </summary>
[ComVisible(true)]
[Guid("0000000C-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleStream
{
    [PreserveSig]
    int Read(IntPtr pv, int cb, IntPtr pcbRead);

    [PreserveSig]
    int Write(IntPtr pv, int cb, IntPtr pcbWritten);

    [PreserveSig]
    int Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition);

    [PreserveSig]
    int SetSize(long libNewSize);

    [PreserveSig]
    int CopyTo(IntPtr pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten);

    [PreserveSig]
    int Commit(int grfCommitFlags);

    [PreserveSig]
    int Revert();

    [PreserveSig]
    int LockRegion(long libOffset, long cb, int dwLockType);

    [PreserveSig]
    int UnlockRegion(long libOffset, long cb, int dwLockType);

    [PreserveSig]
    int Stat(IntPtr pstatstg, int grfStatFlag);

    [PreserveSig]
    int Clone(IntPtr ppstm);
}

/// <summary>
/// IEnumFORMATETC (00000103-...). <c>rgelt</c> stays an IntPtr: a managed array
/// parameter would be marshaled as a SAFEARRAY and the shell would walk off into
/// freed memory (the same trap as IShellFolder::GetUIObjectOf).
/// </summary>
[ComVisible(true)]
[Guid("00000103-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumFORMATETC
{
    [PreserveSig]
    int Next(uint celt, IntPtr rgelt, IntPtr pceltFetched);

    [PreserveSig]
    int Skip(uint celt);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IntPtr ppenum);
}

/// <summary>IDropSource (00000121-...): cursor feedback and Esc-to-cancel.</summary>
[ComVisible(true)]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);

    [PreserveSig]
    int GiveFeedback(uint dwEffect);
}

/// <summary>ole32 / user32 / kernel32 entry points plus the enum values we use.</summary>
internal static class Ole32
{
    public const int S_OK = 0;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int DV_E_LINDEX = unchecked((int)0x80040068);
    public const int DV_E_DVASPECT = unchecked((int)0x8004006B);
    public const int STG_E_INVALIDPOINTER = unchecked((int)0x80030009);
    public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
    public const int STG_E_READFAULT = unchecked((int)0x8003001E);
    public const int DRAGDROP_S_DROP = 0x00040100;
    public const int DRAGDROP_S_CANCEL = 0x00040101;
    public const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;

    public const uint DVASPECT_CONTENT = 1;
    public const uint TYMED_HGLOBAL = 1;
    public const uint TYMED_ISTREAM = 4;
    public const uint DATADIR_GET = 1;
    public const uint DROPEFFECT_COPY = 1;
    public const uint DROPEFFECT_MOVE = 2;
    public const uint MK_LBUTTON = 0x0001;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint STATFLAG_DEFAULT = 0;
    public const uint STATFLAG_NONAME = 1;
    public const uint STGTY_STREAM = 2;
    public const int STREAM_SEEK_SET = 0;
    public const int STREAM_SEEK_CUR = 1;
    public const int STREAM_SEEK_END = 2;

    public const uint FD_ATTRIBUTES = 0x00000004;
    public const uint FD_WRITESTIME = 0x00000020;
    public const uint FD_FILESIZE = 0x00000040;

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    /// <summary>
    /// Bytes of one FILEDESCRIPTORW. The shell's copy engine reads cFileName at 72:
    /// 4 dwFlags + 16 clsid + 8 sizl + 8 pointl + 4 attributes + 24 filetimes
    /// + 8 filesize = 72, plus 260 WCHARs = 592. The 8-byte pointl between sizl and
    /// dwFileAttributes is easy to miss - leaving it out makes the entry 584 and
    /// shifts every later field, so the shell sees an empty name and refuses the
    /// drop (verified against ShlObj_core.h _FILEDESCRIPTORW).
    /// </summary>
    public const int FileDescriptorSize = 592;

    /// <summary>Bytes of the FILEGROUPDESCRIPTORW header (the item count).</summary>
    public const int FileGroupDescriptorHeaderSize = 4;

    [DllImport("ole32.dll")]
    public static extern int DoDragDrop(IntPtr pDataObj, IntPtr pDropSource, uint dwOKEffects, out uint pdwEffect);

    [DllImport("ole32.dll")]
    public static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern UIntPtr GlobalSize(IntPtr hMem);
}
