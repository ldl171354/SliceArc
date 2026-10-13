using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SliceArc.Core.Shell;

/// <summary>
/// Registers the Windows 11 modern context menu: a sparse MSIX package that
/// gives the portable app a package identity, so Explorer will host the
/// IExplorerCommand implemented by SliceArc.ShellExt.comhost.dll.
///
/// Two files ship next to SliceArc.App.exe:
///   SliceArc.ContextMenu.msix  - manifest+logo, packed and signed at build time
///                                  (no Windows SDK needed on the user's machine)
///   slicearc-sparse.cer        - the signing certificate's PUBLIC key
///
/// Registration = trust the public certificate, then Add-AppxPackage with
/// -ExternalLocation pointing at the app folder. Machine-level certificate
/// trust requires one UAC elevation (packages with runFullTrust are not
/// accepted from CurrentUser trust); everything else runs non-elevated.
///
/// Every failure is a graceful skip: the classic registry menu keeps working.
/// </summary>
public static class ShellPackageRegistrar
{
    public const string PackageName = "SliceArc.ContextMenu";
    private const string CertSubject = "CN=SliceArc";
    private const string MsixFileName = "SliceArc.ContextMenu.msix";
    private const string CertFileName = "slicearc-sparse.cer";

    private static readonly string[] RequiredFiles =
    {
        "SliceArc.App.exe",
        "SliceArc.ShellExt.comhost.dll",
        "SliceArc.ShellExt.dll",
        "SliceArc.ShellExt.deps.json",
        "SliceArc.ShellExt.runtimeconfig.json",
        MsixFileName,
        CertFileName,
    };

    public sealed class ModernMenuResult
    {
        public bool Ok { get; init; }
        public string Reason { get; init; } = string.Empty;
    }

    // =====================================================================
    // Prerequisites
    // =====================================================================

    /// <summary>
    /// Null when the modern menu can be registered from <paramref name="appDir"/>;
    /// otherwise a human-readable reason it has to be skipped.
    /// </summary>
    public static string? FindPrerequisiteProblem(string appDir)
    {
        foreach (var name in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(appDir, name)))
                return $"缺少文件 {name}";
        }

        if (!HasNet8DesktopRuntime())
            return "未安装 .NET 8 桌面运行时（现代菜单的 COM 宿主需要它）";

        return null;
    }

    /// <summary>
    /// The COM host is framework-dependent (COM hosting cannot be self-contained),
    /// so a Microsoft.NETCore.App 8 runtime must exist machine-wide. hostfxr looks
    /// in DOTNET_ROOT and the default install location.
    /// </summary>
    private static bool HasNet8DesktopRuntime()
    {
        var roots = new List<string>();
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot)) roots.Add(dotnetRoot);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));

        foreach (var root in roots)
        {
            var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            try
            {
                if (Directory.Exists(shared) &&
                    Directory.EnumerateDirectories(shared).Any(d =>
                    {
                        var n = Path.GetFileName(d);
                        return n.StartsWith("8.", StringComparison.Ordinal);
                    }))
                    return true;
            }
            catch { /* keep looking */ }
        }
        return false;
    }

    // =====================================================================
    // Register / unregister
    // =====================================================================

    public static ModernMenuResult Register(string appDir, Action<string>? log = null)
    {
        var problem = FindPrerequisiteProblem(appDir);
        if (problem is not null) return new ModernMenuResult { Reason = problem };

        try
        {
            var certPath = Path.Combine(appDir, CertFileName);
            var msixPath = Path.Combine(appDir, MsixFileName);

            var trustProblem = EnsureCertificateTrusted(certPath, log);
            if (trustProblem is not null) return new ModernMenuResult { Reason = trustProblem };

            // Re-registering from a new location only works reliably after the
            // old registration is gone.
            Unregister(null);

            log?.Invoke("注册现代右键菜单包…");
            var (exit, output) = RunPowerShell(
                $"Add-AppxPackage -Path '{EscapePs(msixPath)}' -ExternalLocation '{EscapePs(appDir)}' -ForceUpdateFromAnyVersion -ErrorAction Stop");
            if (exit != 0)
                return new ModernMenuResult { Reason = "Add-AppxPackage 失败: " + Tail(output) };

            // Deployment finalizes asynchronously right after the cmdlet
            // returns; give the query a few chances before declaring failure.
            string? version = null;
            for (var i = 0; i < 20 && version is null; i++)
            {
                if (i > 0) Thread.Sleep(500);
                version = InstalledVersion();
            }
            return version is null
                ? new ModernMenuResult { Reason = "注册后查询不到包" }
                : new ModernMenuResult { Ok = true, Reason = $"已注册 v{version}" };
        }
        catch (Exception ex)
        {
            return new ModernMenuResult { Reason = ex.Message };
        }
    }

    /// <summary>Best-effort removal of the sparse package. True when it is gone.</summary>
    public static bool Unregister(Action<string>? log = null)
    {
        try
        {
            RunPowerShell($"Get-AppxPackage -Name {PackageName} | Remove-AppxPackage -ErrorAction SilentlyContinue");
            // Removal finalizes asynchronously; poll until the package is gone.
            for (var i = 0; i < 10; i++)
            {
                if (InstalledVersion() is null) return true;
                Thread.Sleep(500);
            }
            log?.Invoke("移除现代菜单包超时");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke("移除现代菜单包异常: " + ex.Message);
            return false;
        }
    }

    /// <summary>Version string of the installed sparse package, or null when absent.</summary>
    public static string? InstalledVersion()
    {
        try
        {
            // powershell.exe is Windows PowerShell 5.1: no ?? operator.
            var (exit, output) = RunPowerShell(
                $"$p = Get-AppxPackage -Name {PackageName} -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                "if ($p) { $p.Version.ToString() }");
            var v = output.Trim();
            return exit == 0 && v.Length > 0 && char.IsDigit(v[0]) ? v : null;
        }
        catch { return null; }
    }

    // =====================================================================
    // Certificate trust
    // =====================================================================

    /// <summary>
    /// Trust the shipped public certificate. CurrentUser trust never needs
    /// elevation; the package manager additionally demands machine trust for
    /// full-trust packages, which costs one UAC prompt the first time.
    /// </summary>
    private static string? EnsureCertificateTrusted(string certPath, Action<string>? log)
    {
        var cert = new X509Certificate2(certPath);

        try
        {
            using var userStore = new X509Store(StoreName.TrustedPeople, StoreLocation.CurrentUser, OpenFlags.ReadWrite);
            if (!userStore.Certificates.Cast<X509Certificate2>().Any(c => c.Thumbprint == cert.Thumbprint))
                userStore.Add(cert);
        }
        catch (Exception ex)
        {
            log?.Invoke("用户级证书信任失败（忽略）: " + ex.Message);
        }

        if (IsTrustedInMachineStore(cert.Thumbprint!)) return null;

        log?.Invoke("需要一次提权：把签名证书加入本机受信任列表…");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -WindowStyle Hidden -EncodedCommand " + EncodedCommand(
                    $"Import-Certificate -FilePath '{EscapePs(certPath)}' -CertStoreLocation Cert:\\LocalMachine\\TrustedPeople | Out-Null"),
                Verb = "runas",
                UseShellExecute = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "无法启动提权进程";
            proc.WaitForExit(120_000);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "用户取消了提权，证书未被信任";
        }

        return IsTrustedInMachineStore(cert.Thumbprint!)
            ? null
            : "证书导入本机受信任列表失败";
    }

    private static bool IsTrustedInMachineStore(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine, OpenFlags.ReadOnly);
            return store.Certificates.Cast<X509Certificate2>().Any(c => c.Thumbprint == thumbprint);
        }
        catch { return false; }
    }

    // =====================================================================
    // PowerShell plumbing
    // =====================================================================

    private static string EscapePs(string s) => s.Replace("'", "''");

    private static string Tail(string s)
    {
        var t = s.Trim();
        return t.Length <= 300 ? t : "…" + t[^300..];
    }

    /// <summary>EncodedCommand avoids every nested-quoting trap.</summary>
    private static string EncodedCommand(string command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    private static (int ExitCode, string Output) RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + EncodedCommand(command),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.ExitCode, stdout + stderr);
    }
}
