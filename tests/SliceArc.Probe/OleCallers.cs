using System.Runtime.InteropServices;
using SliceArc.Core.DragDrop;

namespace SliceArc.Probe;

/// <summary>
/// Calls a COM interface the way the shell does: by reading the vtable and
/// invoking the slot at a fixed index.
///
/// A managed round trip through <c>Marshal.GetObjectForIUnknown</c> cannot test
/// this, because the CLR recognises its own CCW and hands back the original
/// managed object - the vtable is never touched. Invoking the raw slot pointer
/// does touch it, so a wrong slot order or a wrong IID shows up here instead of
/// in Explorer.
/// </summary>
internal static class VTableCaller
{
    // ---- IDataObject (0000010E): 3 GetData, 4 GetDataHere, 5 QueryGetData, 8 EnumFormatEtc ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetDataDelegate(IntPtr self, ref FORMATETC format, out STGMEDIUM medium);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetDataHereDelegate(IntPtr self, ref FORMATETC format, ref STGMEDIUM medium);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int QueryGetDataDelegate(IntPtr self, ref FORMATETC format);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int EnumFormatEtcDelegate(IntPtr self, uint direction, out IntPtr ppenum);

    // ---- IStream (0000000C): 3 Read, 4 Write, 5 Seek, 12 Stat ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ReadDelegate(IntPtr self, IntPtr pv, int cb, IntPtr pcbRead);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int WriteDelegate(IntPtr self, IntPtr pv, int cb, IntPtr pcbWritten);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int SeekDelegate(IntPtr self, long dlibMove, int dwOrigin, IntPtr plibNewPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int StatDelegate(IntPtr self, IntPtr pstatstg, int grfStatFlag);

    // ---- IEnumFORMATETC (00000103): 3 Next ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int NextDelegate(IntPtr self, uint celt, IntPtr rgelt, IntPtr pceltFetched);

    /// <summary>Slot 0..2 are QueryInterface/AddRef/Release; the first method is slot 3.</summary>
    public static T Slot<T>(IntPtr comPointer, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comPointer);
        var address = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("ole32.dll")]
    internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
