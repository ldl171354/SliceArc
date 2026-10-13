using System.Runtime.InteropServices;

namespace SliceArc.ShellExt;

// =========================================================================
// COM interfaces for the Windows 11 modern context menu (IExplorerCommand).
// Same discipline as the 7z interop: flat interfaces, method order = vtable
// order, [PreserveSig] so raw HRESULTs cross the boundary untouched.
//
//   IExplorerCommand      : what Explorer calls on us (root + each child verb)
//   IEnumExplorerCommand  : enumerates our child verbs for the submenu
//   IShellItemArray/Item  : what Explorer hands us (the current selection)
//
// Strings returned TO Explorer (GetTitle/GetIcon/GetToolTip) must be
// CoTaskMem-allocated LPWSTR - Explorer frees them with CoTaskMemFree, so a
// BSTR or a managed-string marshal would corrupt the heap. Hence IntPtr.
// =========================================================================

internal static class HResults
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_FAIL = unchecked((int)0x80004005);
}

/// <summary>EXPCMDSTATE from shobjidl_core.h.</summary>
internal static class ExpCmdState
{
    public const int Enabled = 0x0;
    public const int Disabled = 0x1;
    public const int Hidden = 0x2;
}

/// <summary>EXPCMDFLAGS from shobjidl_core.h.</summary>
internal static class ExpCmdFlags
{
    public const int Default = 0x0;
    public const int HasSubCommands = 0x1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

/// <summary>IExplorerCommand (a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9). We implement this.</summary>
[ComVisible(true)]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    /// <param name="psiItemArray">IShellItemArray* - the selection, owned by the caller.</param>
    /// <param name="ppszName">LPWSTR* we fill with a CoTaskMem string.</param>
    [PreserveSig]
    int GetTitle(IntPtr psiItemArray, out IntPtr ppszName);

    [PreserveSig]
    int GetIcon(IntPtr psiItemArray, out IntPtr ppszIcon);

    [PreserveSig]
    int GetToolTip(IntPtr psiItemArray, out IntPtr ppszInfotip);

    [PreserveSig]
    int GetCanonicalName(out Guid pguidCommandName);

    /// <param name="fOkToBeSlow">FALSE = don't do anything slow; visibility must be cheap.</param>
    /// <param name="pCmdState">EXPCMDSTATE we write.</param>
    [PreserveSig]
    int GetState(IntPtr psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out int pCmdState);

    /// <param name="pbc">IBindCtx*, usually NULL.</param>
    [PreserveSig]
    int Invoke(IntPtr psiItemArray, IntPtr pbc);

    /// <param name="pFlags">EXPCMDFLAGS we write.</param>
    [PreserveSig]
    int GetFlags(out int pFlags);

    [PreserveSig]
    int EnumSubCommands(out IEnumExplorerCommand? ppEnum);
}

/// <summary>IEnumExplorerCommand (a88826f8-186f-4987-aade-ea0cef8fbfe8). We implement this.</summary>
[ComVisible(true)]
[Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumExplorerCommand
{
    /// <summary>
    /// Explorer always calls Next(1, ...); the out parameter is a single slot.
    /// COM rule: S_OK when pceltFetched == celt, S_FALSE otherwise.
    /// </summary>
    [PreserveSig]
    int Next(uint celt, out IExplorerCommand? pUICommand, out uint pceltFetched);

    [PreserveSig]
    int Skip(uint celt);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IEnumExplorerCommand? ppenum);
}

/// <summary>IShellItemArray (b63ea76d-1f85-456f-a19c-48159efa858b). Explorer implements; we consume.</summary>
[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);

    [PreserveSig]
    int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);

    [PreserveSig]
    int GetAttributes(uint dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, out IShellItem ppsi);

    [PreserveSig]
    int EnumItems(out IntPtr ppenumShellItems);
}

/// <summary>IShellItem (43826d1e-e718-42ee-bc55-a1e261c37bfe). Explorer implements; we consume.</summary>
[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig]
    int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);

    [PreserveSig]
    int GetParent(out IShellItem ppsi);

    [PreserveSig]
    int GetDisplayName(int sigdnName, out IntPtr ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(IShellItem psi, uint hint, out int piOrder);
}
