using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SliceArc.ShellExt;

/// <summary>Stable GUIDs for every command's canonical name (and the root's CLSID).</summary>
internal static class CommandGuids
{
    public const string RootClsid = "7A1CEA11-5A1C-4C00-A000-000000000001";

    public static readonly Guid Root = new(RootClsid);
    public static readonly Guid ExtractHere = new("7A1CEA11-5A1C-4C00-A000-000000000002");
    public static readonly Guid ExtractSubfolder = new("7A1CEA11-5A1C-4C00-A000-000000000003");
    public static readonly Guid ExtractDeleteSpent = new("7A1CEA11-5A1C-4C00-A000-000000000004");
    public static readonly Guid TestArchive = new("7A1CEA11-5A1C-4C00-A000-000000000005");
    public static readonly Guid Open = new("7A1CEA11-5A1C-4C00-A000-000000000006");
    public static readonly Guid CompressZip = new("7A1CEA11-5A1C-4C00-A000-000000000007");
    public static readonly Guid Compress7z = new("7A1CEA11-5A1C-4C00-A000-000000000008");
    public static readonly Guid CompressSplit = new("7A1CEA11-5A1C-4C00-A000-000000000009");
}

/// <summary>Turn the IShellItemArray Explorer hands us into plain file system paths.</summary>
internal static class ShellItemPaths
{
    private const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);
    private const int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);

    public static List<string> FromArray(IntPtr psiItemArray)
    {
        var paths = new List<string>();
        if (psiItemArray == IntPtr.Zero) return paths;

        var arr = Marshal.GetObjectForIUnknown(psiItemArray) as IShellItemArray;
        if (arr is null) return paths;

        try
        {
            if (arr.GetCount(out var count) != HResults.S_OK) return paths;
            for (uint i = 0; i < count; i++)
            {
                if (arr.GetItemAt(i, out var item) != HResults.S_OK || item is null) continue;
                try
                {
                    var p = DisplayName(item, SIGDN_FILESYSPATH) ?? DisplayName(item, SIGDN_DESKTOPABSOLUTEPARSING);
                    // Relative names ("test.zip") are useless downstream: the app
                    // runs with its own working directory, not the folder shown.
                    if (!string.IsNullOrEmpty(p) && Path.IsPathRooted(p)) paths.Add(p);
                }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        finally { Marshal.ReleaseComObject(arr); }

        return paths;
    }

    private static string? DisplayName(IShellItem item, int sigdnName)
    {
        if (item.GetDisplayName(sigdnName, out var p) != HResults.S_OK || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }
}

/// <summary>Where SliceArc.App.exe lives relative to this extension (same folder in dist\portable).</summary>
internal static class AppLocator
{
    private static string? _exePath;

    public static string ExePath => _exePath ??= Resolve();

    private static string Resolve()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        // The extension is published next to the exe; prefer the same folder and
        // only fall back to the parent (old dist\shellext\ layout).
        var here = Path.Combine(dir, "SliceArc.App.exe");
        if (File.Exists(here)) return here;
        return Path.GetFullPath(Path.Combine(dir, "..", "SliceArc.App.exe"));
    }

    public static string IconReference => ExePath + ",0";
}

/// <summary>
/// Archive guess for GetState visibility. The logic itself lives in the file
/// shared with SliceArc.Core (<see cref="SliceArc.Shared.ArchiveNameHeuristics"/>)
/// so the modern menu and the classic menu's AppliesTo cannot drift apart.
/// </summary>
internal static class ArchiveGuessing
{
    public static bool IsArchive(string path) => SliceArc.Shared.ArchiveNameHeuristics.LooksLikeArchive(path);
}

/// <summary>Shared plumbing for a leaf verb: title, icon, visibility, launch.</summary>
public abstract class ExplorerCommandBase : IExplorerCommand
{
    public abstract Guid CanonicalGuid { get; }
    public abstract string Title { get; }
    public abstract string ArgumentsFor(string path);

    /// <summary>Dynamic visibility, evaluated per call against the current selection.</summary>
    public virtual bool IsVisible(IReadOnlyList<string> paths) => true;

    protected static bool AnyArchive(IReadOnlyList<string> paths) => paths.Any(ArchiveGuessing.IsArchive);

    public int GetTitle(IntPtr psiItemArray, out IntPtr ppszName)
    {
        ppszName = Marshal.StringToCoTaskMemUni(Title);
        return HResults.S_OK;
    }

    public int GetIcon(IntPtr psiItemArray, out IntPtr ppszIcon)
    {
        ppszIcon = Marshal.StringToCoTaskMemUni(AppLocator.IconReference);
        return HResults.S_OK;
    }

    public int GetToolTip(IntPtr psiItemArray, out IntPtr ppszInfotip)
    {
        ppszInfotip = Marshal.StringToCoTaskMemUni(Title);
        return HResults.S_OK;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = CanonicalGuid;
        return HResults.S_OK;
    }

    public int GetState(IntPtr psiItemArray, bool fOkToBeSlow, out int pCmdState)
    {
        try
        {
            pCmdState = IsVisible(ShellItemPaths.FromArray(psiItemArray))
                ? ExpCmdState.Enabled
                : ExpCmdState.Hidden;
        }
        catch { pCmdState = ExpCmdState.Enabled; }
        return HResults.S_OK;
    }

    public int Invoke(IntPtr psiItemArray, IntPtr pbc)
    {
        try
        {
            // One process per item, exactly what Explorer does for classic verbs.
            foreach (var path in ShellItemPaths.FromArray(psiItemArray))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppLocator.ExePath,
                    Arguments = ArgumentsFor(path),
                    WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
                    UseShellExecute = false,
                });
            }
            return HResults.S_OK;
        }
        catch { return HResults.E_FAIL; }
    }

    public virtual int GetFlags(out int pFlags)
    {
        pFlags = ExpCmdFlags.Default;
        return HResults.S_OK;
    }

    public virtual int EnumSubCommands(out IEnumExplorerCommand? ppEnum)
    {
        ppEnum = null;
        return HResults.E_NOTIMPL;
    }

    protected static string Quote(string s) => "\"" + s + "\"";
}

// ---------------------------------------------------------------------
// The eight leaf verbs, mirroring the classic registry menu.
// ---------------------------------------------------------------------

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000002")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractHereCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.ExtractHere;
    public override string Title => "解压到当前文件夹";
    public override bool IsVisible(IReadOnlyList<string> p) => AnyArchive(p);
    public override string ArgumentsFor(string path) => $"--extract {Quote(path)} --here";
}

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000003")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractSubfolderCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.ExtractSubfolder;
    public override string Title => "解压到「归档名」文件夹";
    public override bool IsVisible(IReadOnlyList<string> p) => AnyArchive(p);
    public override string ArgumentsFor(string path) => $"--extract {Quote(path)} --to-subfolder";
}

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000004")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExtractDeleteSpentCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.ExtractDeleteSpent;
    public override string Title => "解压并删除已用完的分卷";
    public override bool IsVisible(IReadOnlyList<string> p) => AnyArchive(p);
    public override string ArgumentsFor(string path) => $"--extract {Quote(path)} --here --delete-spent";
}

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000005")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class TestArchiveCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.TestArchive;
    public override string Title => "测试压缩包完整性";
    public override bool IsVisible(IReadOnlyList<string> p) => AnyArchive(p);
    public override string ArgumentsFor(string path) => $"--extract {Quote(path)} --test";
}

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000006")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class OpenCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.Open;
    public override string Title => "用极限解压打开";
    public override string ArgumentsFor(string path) => Quote(path);
}

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000007")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class CompressZipCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.CompressZip;
    public override string Title => "压缩到 ZIP…";
    public override string ArgumentsFor(string path) => $"--compress --add {Quote(path)} --format zip";
}

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000008")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class Compress7zCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.Compress7z;
    public override string Title => "压缩到 7z…";
    public override string ArgumentsFor(string path) => $"--compress --add {Quote(path)} --format 7z";
}

[ComVisible(true)]
[Guid("7A1CEA11-5A1C-4C00-A000-000000000009")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class CompressSplitCommand : ExplorerCommandBase
{
    public override Guid CanonicalGuid => CommandGuids.CompressSplit;
    public override string Title => "压缩并分卷…（7z · 50 MB）";
    public override string ArgumentsFor(string path) => $"--compress --add {Quote(path)} --format 7z --split-volume 52428800";
}

// ---------------------------------------------------------------------
// Root: the one registered COM class. Explorer asks it for the submenu.
// ---------------------------------------------------------------------

internal sealed class ChildCommandEnumerator : IEnumExplorerCommand
{
    private readonly IExplorerCommand[] _items;
    private int _pos;

    public ChildCommandEnumerator(IExplorerCommand[] items) => _items = items;

    public int Next(uint celt, out IExplorerCommand? pUICommand, out uint pceltFetched)
    {
        pUICommand = null;
        pceltFetched = 0;
        if (celt == 0 || _pos >= _items.Length) return HResults.S_FALSE;

        pUICommand = _items[_pos++];
        pceltFetched = 1;
        return pceltFetched == celt ? HResults.S_OK : HResults.S_FALSE;
    }

    public int Skip(uint celt)
    {
        var next = _pos + (int)celt;
        if (next > _items.Length) { _pos = _items.Length; return HResults.S_FALSE; }
        _pos = next;
        return HResults.S_OK;
    }

    public int Reset()
    {
        _pos = 0;
        return HResults.S_OK;
    }

    public int Clone(out IEnumExplorerCommand? ppenum)
    {
        ppenum = new ChildCommandEnumerator(_items) { _pos = _pos };
        return HResults.S_OK;
    }
}

/// <summary>
/// The only class the sparse package registers (CLSID in the manifest).
/// Explorer QIs it for IExplorerCommand, reads ECF_HASSUBCOMMANDS, then pulls
/// the leaf verbs out of EnumSubCommands.
/// </summary>
[ComVisible(true)]
[Guid(CommandGuids.RootClsid)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IExplorerCommand))]
public sealed class SliceArcRootCommand : IExplorerCommand
{
    private static readonly IExplorerCommand[] Children =
    {
        new ExtractHereCommand(),
        new ExtractSubfolderCommand(),
        new ExtractDeleteSpentCommand(),
        new TestArchiveCommand(),
        new OpenCommand(),
        new CompressZipCommand(),
        new Compress7zCommand(),
        new CompressSplitCommand(),
    };

    public int GetTitle(IntPtr psiItemArray, out IntPtr ppszName)
    {
        ppszName = Marshal.StringToCoTaskMemUni("极限解压");
        return HResults.S_OK;
    }

    public int GetIcon(IntPtr psiItemArray, out IntPtr ppszIcon)
    {
        ppszIcon = Marshal.StringToCoTaskMemUni(AppLocator.IconReference);
        return HResults.S_OK;
    }

    public int GetToolTip(IntPtr psiItemArray, out IntPtr ppszInfotip)
    {
        ppszInfotip = Marshal.StringToCoTaskMemUni("极限解压");
        return HResults.S_OK;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = CommandGuids.Root;
        return HResults.S_OK;
    }

    public int GetState(IntPtr psiItemArray, bool fOkToBeSlow, out int pCmdState)
    {
        pCmdState = ExpCmdState.Enabled;
        return HResults.S_OK;
    }

    /// <summary>Never called: ECF_HASSUBCOMMANDS makes Explorer open the submenu instead.</summary>
    public int Invoke(IntPtr psiItemArray, IntPtr pbc) => HResults.S_OK;

    public int GetFlags(out int pFlags)
    {
        pFlags = ExpCmdFlags.HasSubCommands;
        return HResults.S_OK;
    }

    public int EnumSubCommands(out IEnumExplorerCommand? ppEnum)
    {
        ppEnum = new ChildCommandEnumerator(Children);
        return HResults.S_OK;
    }
}
