using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SliceArc.Core.Shell;

/// <summary>An archive type this app can open on double-click, with its UI label.</summary>
public sealed record ArchiveType(string Extension, string Description);

/// <summary>Outcome of asking Windows to make this app the default handler.</summary>
public enum AssociationStatus
{
    /// <summary>Windows applied it; double-click now opens this app.</summary>
    Bound,

    /// <summary>
    /// Registered and requested, but Windows will not let a program change a
    /// user's default silently. The system "default apps" page was opened so the
    /// user can confirm; the caller should say so instead of claiming success.
    /// </summary>
    NeedsConfirm,
}

public sealed record AssociationOutcome(string Extension, AssociationStatus Status, string? CurrentDefault);

/// <summary>
/// Registers this app as an "Open with" candidate for the archive extensions and
/// asks Windows to make it the default.
///
/// Everything is written under HKEY_CURRENT_USER, so no elevation is needed and
/// <see cref="Unregister"/> is a complete rollback.
///
/// Windows 10/11 deliberately block programs from silently taking over a file
/// association: the <c>SetAppAsDefault</c> call below is only a request. This
/// class therefore registers the ProgIds + capabilities (so the app shows up in
/// "open with" and in Settings &gt; Default apps), fires the request, and then
/// verifies the result. Anything Windows refuses is reported as
/// <see cref="AssociationStatus.NeedsConfirm"/> and the system page is opened -
/// it never fakes success and never writes UserChoice (whose hash protection
/// exists precisely to stop this).
/// </summary>
public static class FileAssociationRegistrar
{
    /// <summary>Value name under <c>RegisteredApplications</c>; also the ProgId root.</summary>
    public const string AppRegName = "SliceArc";

    private const string ClassesRoot = @"Software\Classes";
    private const string CapabilitiesPath = @"Software\SliceArc\Capabilities";
    private const string OpenWithProgids = "OpenWithProgids";

    /// <summary>
    /// Formats offered in the first-run guide, in the order they are shown. Only
    /// single-file, double-clickable types are listed: a non-first volume such as
    /// .z01 / .r00 cannot be opened on its own, so associating it would only
    /// mislead. (.part1.rar is really just .rar, so it needs no entry.)
    /// </summary>
    public static IReadOnlyList<ArchiveType> SupportedTypes { get; } = new[]
    {
        new ArchiveType(".7z", "7-Zip 压缩包"),
        new ArchiveType(".zip", "ZIP 压缩包"),
        new ArchiveType(".rar", "RAR 压缩包"),
        new ArchiveType(".tar", "TAR 归档"),
        new ArchiveType(".gz", "GZIP 压缩文件"),
        new ArchiveType(".tgz", "TAR.GZ 归档"),
        new ArchiveType(".bz2", "BZIP2 压缩文件"),
        new ArchiveType(".tbz", "TAR.BZ2 归档"),
        new ArchiveType(".xz", "XZ 压缩文件"),
        new ArchiveType(".zst", "Zstandard 压缩文件"),
        new ArchiveType(".lz", "LZMA 压缩文件"),
        new ArchiveType(".lzh", "LZH 压缩包"),
        new ArchiveType(".cab", "CAB 压缩包"),
        new ArchiveType(".iso", "ISO 光盘映像"),
        new ArchiveType(".wim", "WIM 映像"),
        new ArchiveType(".001", "分卷压缩包（首卷）"),
    };

    // =====================================================================
    // Registration (no defaults touched)
    // =====================================================================

    /// <summary>
    /// Register the ProgIds, the "open with" entries and the capabilities block
    /// for exactly <paramref name="types"/>, and drop this app from any supported
    /// extension not in the list. Idempotent; it never changes a default
    /// association - that only happens in <see cref="Bind"/>.
    ///
    /// <paramref name="types"/> is treated as the complete desired set, not a
    /// merge: that is what keeps the promise made in the first-run guide
    /// ("only the formats you tick are touched").
    /// </summary>
    public static void Register(string exePath, string appName, string appDescription,
        IReadOnlyList<ArchiveType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("可执行文件路径不能为空。", nameof(exePath));

        var exe = Path.GetFullPath(exePath);
        var exeName = Path.GetFileName(exe);
        var icon = Quote(exe) + ",0";
        var command = Quote(exe) + " \"%1\"";
        var wanted = new HashSet<string>(types.Select(t => t.Extension), StringComparer.OrdinalIgnoreCase);

        // Capabilities block: what Settings > Default apps reads to list us.
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesPath, true)
                          ?? throw new InvalidOperationException("无法创建程序能力注册表项。"))
        {
            caps.SetValue("ApplicationName", appName, RegistryValueKind.String);
            caps.SetValue("ApplicationDescription", appDescription, RegistryValueKind.String);
            using var fa = caps.CreateSubKey("FileAssociations", true)
                           ?? throw new InvalidOperationException("无法创建文件关联注册表项。");
            ClearValues(fa);
            foreach (var type in types) fa.SetValue(type.Extension, ProgIdFor(type.Extension), RegistryValueKind.String);
        }

        using (var regApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", true))
            regApps?.SetValue(AppRegName, CapabilitiesPath, RegistryValueKind.String);

        // Applications\<exe>: makes the app a valid "open with" target and lets
        // Explorer offer it in the OpenWith list.
        using (var app = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"\Applications\" + exeName, true))
        {
            if (app is not null)
            {
                app.SetValue("FriendlyAppName", appName, RegistryValueKind.String);
                using (var ico = app.CreateSubKey("DefaultIcon", true))
                    ico?.SetValue(null, icon, RegistryValueKind.String);
                using (var cmd = app.CreateSubKey(@"shell\open\command", true))
                    cmd?.SetValue(null, command, RegistryValueKind.String);
                using var supported = app.CreateSubKey("SupportedTypes", true);
                if (supported is not null)
                {
                    ClearValues(supported);
                    foreach (var type in types) supported.SetValue(type.Extension, string.Empty, RegistryValueKind.String);
                }
            }
        }

        foreach (var type in SupportedTypes)
        {
            var progId = ProgIdFor(type.Extension);
            if (wanted.Contains(type.Extension)) WriteType(type, progId, icon, command);
            else RemoveType(type.Extension, progId);
        }

        NotifyShell();
    }

    private static void WriteType(ArchiveType type, string progId, string icon, string command)
    {
        using (var prog = Registry.CurrentUser.CreateSubKey(ClassesRoot + "\\" + progId, true))
        {
            if (prog is not null)
            {
                prog.SetValue(null, type.Description, RegistryValueKind.String);
                using (var ico = prog.CreateSubKey("DefaultIcon", true))
                    ico?.SetValue(null, icon, RegistryValueKind.String);
                using (var cmd = prog.CreateSubKey(@"shell\open\command", true))
                    cmd?.SetValue(null, command, RegistryValueKind.String);
            }
        }

        // Advertise the ProgId as an alternative handler for the extension.
        using var extKey = Registry.CurrentUser.CreateSubKey(ClassesRoot + "\\" + type.Extension, true);
        using var owp = extKey?.CreateSubKey(OpenWithProgids, true);
        owp?.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private static void RemoveType(string extension, string progId)
    {
        try
        {
            using var extKey = Registry.CurrentUser.OpenSubKey(ClassesRoot + "\\" + extension, true);
            using var owp = extKey?.OpenSubKey(OpenWithProgids, true);
            owp?.DeleteValue(progId, throwOnMissingValue: false);
        }
        catch
        {
            // A locked OpenWithProgids key is not worth failing the whole sync.
        }

        TryDeleteTree(ClassesRoot + "\\" + progId);
    }

    private static void ClearValues(RegistryKey key)
    {
        foreach (var name in key.GetValueNames()) key.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>Remove every key <see cref="Register"/> wrote. Defaults are untouched.</summary>
    public static void Unregister(IReadOnlyList<ArchiveType>? types = null)
    {
        types ??= SupportedTypes;

        foreach (var type in types)
        {
            var progId = ProgIdFor(type.Extension);
            TryDeleteTree(ClassesRoot + "\\" + progId);

            try
            {
                using var extKey = Registry.CurrentUser.OpenSubKey(ClassesRoot + "\\" + type.Extension, true);
                using var owp = extKey?.OpenSubKey(OpenWithProgids, true);
                owp?.DeleteValue(progId, throwOnMissingValue: false);
            }
            catch
            {
                // A locked OpenWithProgids key is not worth failing the whole unregister.
            }
        }

        var exeName = Environment.ProcessPath is { } exe ? Path.GetFileName(exe) : "SliceArc.App.exe";
        TryDeleteTree(ClassesRoot + @"\Applications\" + exeName);

        try
        {
            using var regApps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true);
            regApps?.DeleteValue(AppRegName, throwOnMissingValue: false);
        }
        catch
        {
            // best-effort
        }

        TryDeleteTree(@"Software\SliceArc");
        NotifyShell();
    }

    // =====================================================================
    // Default binding
    // =====================================================================

    /// <summary>
    /// Ask Windows to make this app the default handler for each extension and
    /// report what actually stuck. <see cref="Register"/> must have run first.
    /// </summary>
    public static IReadOnlyList<AssociationOutcome> Bind(IEnumerable<string> extensions)
    {
        var wanted = extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var registration = TryCreateRegistration();
        var outcomes = new List<AssociationOutcome>(wanted.Count);
        var needsConfirm = false;

        foreach (var ext in wanted)
        {
            var current = QueryCurrentDefaultProgId(ext);
            if (IsOurs(current))
            {
                outcomes.Add(new AssociationOutcome(ext, AssociationStatus.Bound, current));
                continue;
            }

            if (registration is not null)
            {
                try { registration.SetAppAsDefault(AppRegName, ext, AssociationType.FileExtension); }
                catch { /* verified below */ }
            }

            var after = QueryCurrentDefaultProgId(ext);
            if (IsOurs(after))
            {
                outcomes.Add(new AssociationOutcome(ext, AssociationStatus.Bound, after));
            }
            else
            {
                needsConfirm = true;
                outcomes.Add(new AssociationOutcome(ext, AssociationStatus.NeedsConfirm, after));
            }
        }

        if (needsConfirm) LaunchAdvancedUi();
        NotifyShell();
        return outcomes;
    }

    /// <summary>The ProgId Windows currently opens <paramref name="extension"/> with, or null.</summary>
    public static string? QueryCurrentDefaultProgId(string extension)
    {
        try
        {
            var registration = TryCreateRegistration();
            if (registration is not null &&
                registration.QueryCurrentDefault(extension, AssociationType.FileExtension,
                    AssociationLevel.Effective, out var assoc) == 0 &&
                !string.IsNullOrWhiteSpace(assoc))
            {
                return assoc;
            }
        }
        catch
        {
            // Fall through to the registry probe.
        }

        // UserChoice is where Explorer records the effective handler; reading it
        // is reliable even when the COM query is unavailable.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\UserChoice");
            return key?.GetValue("ProgId") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when this app is already the default handler for the extension.</summary>
    public static bool IsDefault(string extension) => IsOurs(QueryCurrentDefaultProgId(extension));

    /// <summary>True when at least one of our ProgIds is registered.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var prog = Registry.CurrentUser.OpenSubKey(ClassesRoot + "\\" + ProgIdFor(".7z"));
            return prog is not null;
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    // Internals
    // =====================================================================

    private static bool IsOurs(string? progId) =>
        progId is not null && progId.StartsWith(AppRegName, StringComparison.OrdinalIgnoreCase);

    private static string ProgIdFor(string extension) => AppRegName + extension;

    private static string Quote(string value) => "\"" + value + "\"";

    private static void TryDeleteTree(string path)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
        catch { /* best-effort cleanup */ }
    }

    private static IApplicationAssociationRegistration? TryCreateRegistration()
    {
        try { return (IApplicationAssociationRegistration)new ApplicationAssociationRegistration(); }
        catch { return null; }
    }

    /// <summary>
    /// The only reliable path when Windows refuses the silent request: hand the
    /// user to the system page. Tries the dedicated COM UI first, then the
    /// Settings deep link.
    /// </summary>
    private static void LaunchAdvancedUi()
    {
        try
        {
            var ui = (IApplicationAssociationRegistrationUI)new ApplicationAssociationRegistrationUI();
            if (ui.LaunchAdvancedAssociationUI(AppRegName) == 0) return;
        }
        catch
        {
            // Fall through to the Settings URI.
        }

        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppRegName}")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // No Settings host (or blocked): the caller still reports the status.
        }
    }

    // =====================================================================
    // Shell notification
    // =====================================================================

    private const uint ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShell()
    {
        try { SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { /* a notification is never a hard requirement */ }
    }

    // =====================================================================
    // COM plumbing
    // =====================================================================

    private enum AssociationType
    {
        FileExtension = 0,
        UrlProtocol = 1,
        StartMenuClient = 2,
        MimeType = 3,
    }

    private enum AssociationLevel
    {
        Machine = 0,
        Effective = 1,
        User = 2,
    }

    [ComImport, Guid("591209c7-767b-42b2-9fba-44ee4615f2c7")]
    private class ApplicationAssociationRegistration
    {
    }

    [ComImport, Guid("4e530b0a-e611-4c77-a3ac-9031d022281b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistration
    {
        [PreserveSig]
        int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string pszQuery,
            AssociationType atQueryType, AssociationLevel alQueryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] out string ppszAssociation);

        [PreserveSig]
        int QueryAppIsDefault([MarshalAs(UnmanagedType.LPWStr)] string pszQuery,
            AssociationType atQueryType, AssociationLevel alQueryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName,
            [MarshalAs(UnmanagedType.Bool)] out bool pfDefault);

        [PreserveSig]
        int QueryAppIsDefaultAll(AssociationLevel alQueryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName,
            [MarshalAs(UnmanagedType.Bool)] out bool pfDefault);

        [PreserveSig]
        int SetAppAsDefault([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName,
            [MarshalAs(UnmanagedType.LPWStr)] string pszSet, AssociationType atSetType);

        [PreserveSig]
        int SetAppAsDefaultAll([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName);

        [PreserveSig]
        int ClearUserAssociations();

        [PreserveSig]
        int RemoveAppFromDefault([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName);
    }

    [ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1")]
    private class ApplicationAssociationRegistrationUI
    {
    }

    [ComImport, Guid("1f76a169-f994-40ac-8fc8-0959e8874710"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistrationUI
    {
        [PreserveSig]
        int LaunchAdvancedAssociationUI([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegName);
    }
}
