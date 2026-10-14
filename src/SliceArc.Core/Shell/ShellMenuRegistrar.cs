using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SliceArc.Core.Shell;

/// <summary>How the Explorer context menu entry should look and behave.</summary>
public sealed record ShellMenuOptions
{
    /// <summary>Absolute path to the executable the verbs should launch.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Top-level menu caption.</summary>
    public string MenuText { get; init; } = "极限解压";

    /// <summary>Icon reference; defaults to the executable's own icon.</summary>
    public string? IconReference { get; init; }

    /// <summary>
    /// Hide the entry on files whose extension is not a known archive. When
    /// false the verb appears on every file (useful as a fallback if the shell
    /// ignores <c>AppliesTo</c>).
    /// </summary>
    public bool OnlyArchiveExtensions { get; init; } = true;

    /// <summary>Also add verbs for folders and folder backgrounds.</summary>
    public bool IncludeFolderVerbs { get; init; } = true;
}

/// <summary>
/// Installs the native right-click entries under <c>HKCU\Software\Classes</c>:
/// <c>*\shell</c> (files), <c>Directory\shell</c> (folders) and
/// <c>Directory\Background\shell</c> (folder background).
///
/// Everything lives under HKEY_CURRENT_USER, so no elevation is ever required
/// and <see cref="Unregister"/> is a complete rollback.
///
/// The verbs are FLAT - one top-level entry per action, each with its own
/// <c>command</c>. That is deliberate, and it is the outcome of an actual
/// failure:
///
///   1. A cascade built with <c>SubCommands = ""</c> plus a nested
///      <c>shell</c> subkey produced a dead item: the submenu opened empty and
///      clicking the parent did nothing. <c>SubCommands</c> is not "use whatever
///      is nested" - its value is the *list of verbs to display*, so an empty
///      string means "show nothing".
///
///   2. <c>ExtendedSubCommandsKey</c> (the documented elevation-free cascade)
///      was tried next, both with the verbs nested under <c>*\ContextMenus\</c>
///      and in a dedicated top-level key. Both still produced an empty submenu
///      on this machine, which the probe caught as a regression (section 8.3).
///
///   3. Note that 7-Zip's own submenu is not a counter-example: it is an
///      IContextMenu shell extension, not static verbs, so there is no registry
///      shape to copy from it.
///
/// Flat verbs have no second level that can fail to resolve, and they are
/// one click faster. The captions share a "SliceArc：" prefix so they still
/// read as one group in the menu.
/// </summary>
public static class ShellMenuRegistrar
{
    public const string VerbKey = "SliceArc";

    /// <summary>HKCU-relative root every verb is written under.</summary>
    public const string ClassesRoot = @"Software\Classes";

    private const string AppliesToValue = "AppliesTo";
    private const string ExtendedSubCommandsValue = "ExtendedSubCommandsKey";

    /// <summary>Extensions offered in the <c>AppliesTo</c> condition.</summary>
    private static readonly string[] ArchiveExtensions =
    {
        ".7z", ".zip", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst", ".lz", ".lzh",
        ".cab", ".iso", ".wim", ".tgz", ".tbz", ".001", ".z01", ".r00", ".r01", ".part1",
    };

    // =====================================================================
    // Public API
    // =====================================================================

    public static bool IsRegistered() => IsRegistered(ClassesRoot);

    public static void Register(ShellMenuOptions options) => Register(options, ClassesRoot);

    public static void Unregister() => Unregister(ClassesRoot);

    /// <summary>Read the whole tree back; used by the UI and by the probe.</summary>
    public static IReadOnlyList<string> Inspect() => Inspect(ClassesRoot);

    /// <summary>Path of the executable the verbs currently point at, or null.</summary>
    public static string? RegisteredTarget() => RegisteredTarget(ClassesRoot);

    // =====================================================================
    // Test-friendly overloads (the probe points these at a scratch hive path)
    // =====================================================================

    internal static bool IsRegistered(string basePath)
    {
        return VerbNames(basePath, FileVerbPath).Any();
    }

    internal static void Register(ShellMenuOptions options, string basePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
            throw new ArgumentException("可执行文件路径不能为空。", nameof(options));

        var exe = Path.GetFullPath(options.ExecutablePath);
        var icon = options.IconReference ?? Quote(exe) + ",0";
        var appliesTo = options.OnlyArchiveExtensions ? BuildAppliesTo() : null;

        // Flat verbs, one command each - see the class remark. A cascade looks
        // nicer on paper but every HKCU-only form of it produced a dead item on
        // this machine, and a menu entry that does nothing is worse than eight
        // entries that work.
        //
        // Extraction verbs inherit the group AppliesTo (archive extensions only);
        // compression verbs clear it so they show on any file, the way Bandizip
        // does. The 20-series is compression, the 01..04-series is extraction.
        //
        // No --quiet anywhere: extraction ends with a confirmation box + sound,
        // compression opens the parameter dialog first (Bandizip-style). --quiet
        // stays available for scripts on the command line.
        WriteVerbs(basePath, FileVerbPath,
            new[]
            {
                Leaf(VerbKey + "01_here", "极限解压：解压到当前文件夹", icon,
                    $"{Quote(exe)} --extract \"%1\" --here", appliesTo),
                Leaf(VerbKey + "02_sub", "极限解压：解压到「归档名」文件夹", icon,
                    $"{Quote(exe)} --extract \"%1\" --to-subfolder", appliesTo),
                Leaf(VerbKey + "03_here_delete", "极限解压：解压并删除已用完的分卷", icon,
                    $"{Quote(exe)} --extract \"%1\" --here --delete-spent", appliesTo),
                Leaf(VerbKey + "04_test", "极限解压：测试压缩包完整性", icon,
                    $"{Quote(exe)} --extract \"%1\" --test", appliesTo),
                Leaf(VerbKey + "20_zip", "极限解压：压缩到 ZIP…", icon,
                    $"{Quote(exe)} --compress --add \"%1\" --format zip", null),
                Leaf(VerbKey + "21_7z", "极限解压：压缩到 7z…", icon,
                    $"{Quote(exe)} --compress --add \"%1\" --format 7z", null),
                Leaf(VerbKey + "22_split", "极限解压：压缩并分卷…（7z · 50 MB）", icon,
                    $"{Quote(exe)} --compress --add \"%1\" --format 7z --split-volume 52428800", null),
                Leaf(VerbKey + "10_open", "用极限解压打开", icon, $"{Quote(exe)} \"%1\"", null),
            });

        if (options.IncludeFolderVerbs)
        {
            WriteVerbs(basePath, FolderVerbPath,
                new[]
                {
                    Leaf(VerbKey + "01_all", "极限解压：解压此文件夹中的全部压缩包", icon,
                        $"{Quote(exe)} --extract-all \"%1\" --here", null),
                    Leaf(VerbKey + "20_zip", "极限解压：压缩此文件夹到 ZIP…", icon,
                        $"{Quote(exe)} --compress --add \"%1\" --format zip", null),
                    Leaf(VerbKey + "21_7z", "极限解压：压缩此文件夹到 7z…", icon,
                        $"{Quote(exe)} --compress --add \"%1\" --format 7z", null),
                    Leaf(VerbKey + "22_split", "极限解压：压缩此文件夹并分卷…（7z · 50 MB）", icon,
                        $"{Quote(exe)} --compress --add \"%1\" --format 7z --split-volume 52428800", null),
                    Leaf(VerbKey + "10_open", "用极限解压打开", icon, $"{Quote(exe)} \"%1\"", null),
                });

            WriteVerbs(basePath, BackgroundVerbPath,
                new[]
                {
                    Leaf(VerbKey + "01_all", "极限解压：解压此文件夹中的全部压缩包", icon,
                        $"{Quote(exe)} --extract-all \"%V\" --here", null),
                    Leaf(VerbKey + "20_zip", "极限解压：压缩此文件夹到 ZIP…", icon,
                        $"{Quote(exe)} --compress --add \"%V\" --format zip", null),
                    Leaf(VerbKey + "21_7z", "极限解压：压缩此文件夹到 7z…", icon,
                        $"{Quote(exe)} --compress --add \"%V\" --format 7z", null),
                    Leaf(VerbKey + "22_split", "极限解压：压缩此文件夹并分卷…（7z · 50 MB）", icon,
                        $"{Quote(exe)} --compress --add \"%V\" --format 7z --split-volume 52428800", null),
                    Leaf(VerbKey + "10_open", "用极限解压打开此处", icon, $"{Quote(exe)} \"%V\"", null),
                });
        }

        NotifyShell();
    }

    internal static void Unregister(string basePath)
    {
        // "shell" is shared with every other handler, so only our own verb keys
        // are removed - never the shell key itself.
        foreach (var shellPath in MenuSuffixes())
        {
            foreach (var name in VerbNames(basePath, shellPath))
            {
                var path = KeyPath(basePath, shellPath) + "\\" + name;
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"移除 {path} 失败: {ex.Message}", ex);
                }
            }
        }

        NotifyShell();
    }

    internal static IReadOnlyList<string> Inspect(string basePath)
    {
        var lines = new List<string>();

        foreach (var shellPath in MenuSuffixes())
        {
            var root = KeyPath(basePath, shellPath);
            var names = VerbNames(basePath, shellPath).ToList();

            if (names.Count == 0)
            {
                lines.Add($"(未注册) {root}");
                continue;
            }

            foreach (var name in names)
            {
                using var verb = Registry.CurrentUser.OpenSubKey(root + "\\" + name);
                var caption = verb?.GetValue("MUIVerb") as string ?? name;
                var command = verb?.OpenSubKey("command")?.GetValue(null) as string ?? "(无命令)";
                var applies = verb?.GetValue(AppliesToValue) as string;
                lines.Add($"{root}\\{name} = {caption}  ->  {command}" +
                          (applies is null ? string.Empty : "   [AppliesTo]"));
            }
        }

        return lines;
    }

    internal static string? RegisteredTarget(string basePath)
    {
        foreach (var name in VerbNames(basePath, FileVerbPath))
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath(basePath, FileVerbPath) + "\\" + name + "\\command");
            var value = key?.GetValue(null) as string;
            if (string.IsNullOrEmpty(value)) continue;

            // First quoted token of the command line.
            var start = value.IndexOf('"');
            if (start < 0) continue;
            var end = value.IndexOf('"', start + 1);
            if (end > start) return value[(start + 1)..end];
        }
        return null;
    }

    // =====================================================================
    // Internals
    // =====================================================================

    /// <summary>File verbs live under <c>*\shell</c>.</summary>
    private const string FileVerbPath = @"*\shell";

    private const string FolderVerbPath = @"Directory\shell";

    private const string BackgroundVerbPath = @"Directory\Background\shell";

    /// <summary>Every key this installer owns, so Unregister is a clean rollback.</summary>
    private static IEnumerable<string> MenuSuffixes()
    {
        yield return FileVerbPath;
        yield return FolderVerbPath;
        yield return BackgroundVerbPath;
    }

    private static (string Name, string Caption, string Icon, string Command, string? AppliesTo) Leaf(
        string name, string caption, string icon, string command, string? appliesTo = null) =>
        (name, caption, icon, command, appliesTo);

    /// <summary>
    /// Writes plain top-level verbs: <c>...\shell\&lt;name&gt;\command</c>. Each
    /// entry carries its own command and its own <c>AppliesTo</c> condition, so
    /// there is no way for one to end up as a dead item the way a broken cascade
    /// does, and extraction verbs can be filtered to archive extensions while
    /// compression verbs show on every file.
    /// </summary>
    private static void WriteVerbs(
        string basePath,
        string shellPath,
        (string Name, string Caption, string Icon, string Command, string? AppliesTo)[] leaves)
    {
        using var shell = Registry.CurrentUser.CreateSubKey(KeyPath(basePath, shellPath), true)
                          ?? throw new InvalidOperationException($"无法创建菜单键 {shellPath}。");

        foreach (var leaf in leaves)
        {
            using var verb = shell.CreateSubKey(leaf.Name, true)
                             ?? throw new InvalidOperationException($"无法创建菜单项 {leaf.Name}。");

            verb.SetValue(null, leaf.Caption, RegistryValueKind.String);
            verb.SetValue("MUIVerb", leaf.Caption, RegistryValueKind.String);
            verb.SetValue("Icon", leaf.Icon, RegistryValueKind.String);

            if (leaf.AppliesTo is null) verb.DeleteValue(AppliesToValue, throwOnMissingValue: false);
            else verb.SetValue(AppliesToValue, leaf.AppliesTo, RegistryValueKind.String);

            using var cmd = verb.CreateSubKey("command", true)
                            ?? throw new InvalidOperationException($"无法创建菜单命令 {leaf.Name}\\command。");
            cmd.SetValue(null, leaf.Command, RegistryValueKind.String);
        }
    }

    private static IEnumerable<string> VerbNames(string basePath, string shellPath)
    {
        using var shell = Registry.CurrentUser.OpenSubKey(KeyPath(basePath, shellPath));
        if (shell is null) return Array.Empty<string>();
        return shell.GetSubKeyNames().Where(n => n.StartsWith(VerbKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);
    }

    /// <summary>
    /// The AQS condition that decides which files show the extract verbs.
    ///
    /// An extension list alone is not enough: the first volume of a RAR
    /// self-extracting set is <c>name.part01.exe</c>, whose extension is
    /// <c>.exe</c>, so every extract verb - including "delete each volume as it
    /// is spent" - stayed hidden for exactly the files that need it most.
    /// Listing <c>.exe</c> outright is not an option either: that would put
    /// extraction on every executable on the disk. Match the name pattern.
    ///
    /// The pattern has to use a wildcard, not <c>~</c>: measured on this machine,
    /// <c>System.FileName:~".part"</c> is silently ignored (the verb disappears)
    /// while <c>System.FileName:*.part*.exe</c> matches. Exact <c>=</c> works too,
    /// which is how the property name was verified.
    /// </summary>
    private static string BuildAppliesTo()
    {
        var byExtension = string.Join(" OR ", ArchiveExtensions.Select(e => $"System.FileExtension:=\"{e}\""));
        const string selfExtractingVolume = "System.FileName:*.part*.exe";

        return $"{byExtension} OR {selfExtractingVolume}";
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private static string KeyPath(string basePath, string suffix) => basePath + "\\" + suffix;

    // =====================================================================
    // Shell notification
    // =====================================================================

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Explorer caches association data; without this the new (or removed)
    /// submenu may not show up until the next logon or explorer restart.
    /// </summary>
    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Notification is a convenience, never a hard requirement.
        }
    }
}
