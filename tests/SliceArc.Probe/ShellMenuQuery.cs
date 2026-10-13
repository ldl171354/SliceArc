using System.Runtime.InteropServices;
using System.Text;

namespace SliceArc.Probe;

/// <summary>
/// Asks the shell itself which verbs it would show for a file.
///
/// This is the only honest way to verify a context menu entry: the registry can
/// be perfectly shaped and the entry still never appear, because <c>AppliesTo</c>
/// is evaluated by the shell during QueryContextMenu.
///
/// Read-only: it creates a popup menu, fills it, reads the labels and destroys it.
/// </summary>
internal static class ShellMenuQuery
{
    private const uint CmfNormal = 0x00000000;
    private const uint MfByPosition = 0x00000400;

    /// <summary>WM_INITMENUPOPUP - this is what makes a cascade's children materialise.</summary>
    private const uint WmInitMenuPopup = 0x0117;

    private static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidIContextMenu = new("000214E4-0000-0000-C000-000000000046");

    /// <summary>One entry the shell would render.</summary>
    internal sealed record MenuEntry(string Text, bool HasSubmenu, int Depth, List<MenuEntry>? Children);

    /// <summary>
    /// Every entry the shell would put on screen for <paramref name="path"/>.
    ///
    /// A cascading submenu is built lazily: QueryContextMenu only creates the
    /// submenu handle, and the children appear when the user opens it. Sending
    /// WM_INITMENUPOPUP to that handle makes the shell populate it on demand, so
    /// the probe can tell a working cascade from a broken one - without it both
    /// look identical (a submenu handle with nothing inside).
    /// </summary>
    public static IReadOnlyList<MenuEntry> Entries(string path)
    {
        var labels = new List<MenuEntry>();
        IntPtr pidlFull = IntPtr.Zero;
        IntPtr folderPtr = IntPtr.Zero;
        IntPtr menuPtr = IntPtr.Zero;
        var hmenu = IntPtr.Zero;

        try
        {
            CoInitializeEx(IntPtr.Zero, 0x2);   // COINIT_APARTMENTTHREADED; ignored if already initialised

            if (SHParseDisplayName(Path.GetFullPath(path), IntPtr.Zero, out pidlFull, 0, out _) != 0)
                return labels;

            var iidFolder = IidIShellFolder;
            if (SHBindToParent(pidlFull, ref iidFolder, out folderPtr, out var pidlChild) != 0)
                return labels;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            var iid = IidIContextMenu;
            if (folder.GetUIObjectOf(IntPtr.Zero, 1, new[] { pidlChild }, ref iid, IntPtr.Zero, out menuPtr) != 0)
                return labels;

            var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            hmenu = CreatePopupMenu();
            if (hmenu == IntPtr.Zero) return labels;

            if (contextMenu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CmfNormal) < 0) return labels;

            Collect(hmenu, labels, 0);
        }
        catch (Exception ex)
        {
            labels.Add(new MenuEntry($"(探测失败: {ex.GetType().Name}: {ex.Message})", false, 0, null));
        }
        finally
        {
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            if (pidlFull != IntPtr.Zero) CoTaskMemFree(pidlFull);
        }

        return labels;
    }

    private static void Collect(IntPtr hmenu, List<MenuEntry> labels, int depth)
    {
        if (depth > 3) return;

        var count = GetMenuItemCount(hmenu);
        var buffer = new StringBuilder(512);

        for (var i = 0; i < count; i++)
        {
            var sub = GetSubMenu(hmenu, i);

            buffer.Clear();
            var haveText = GetMenuStringW(hmenu, (uint)i, buffer, buffer.Capacity, MfByPosition) > 0;
            var text = haveText ? buffer.ToString().Replace("&", string.Empty) : string.Empty;

            List<MenuEntry>? children = null;
            if (sub != IntPtr.Zero)
            {
                children = new List<MenuEntry>();
                SendMessage(sub, WmInitMenuPopup, sub, IntPtr.Zero);
                Collect(sub, children, depth + 1);
            }

            if (text.Length > 0) labels.Add(new MenuEntry(text, sub != IntPtr.Zero, depth, children));
        }
    }

    // =====================================================================
    // Interop
    // =====================================================================

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig] int GetAttributesOf(uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

        // The array must be LPArray: without the attribute the default COM
        // marshaler hands the shell a SAFEARRAY and it walks off into garbage.
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig] int InvokeCommand(IntPtr pici);

        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uFlags, IntPtr reserved, IntPtr pszName, uint cchMax);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString,
        int cchMax, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
}
