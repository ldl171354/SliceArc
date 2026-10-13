using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SliceArc.App.Services;

/// <summary>
/// Append-only log next to the executable. The right-click verbs launch this app
/// from Explorer, where there is no console to write to, so a file log is the
/// only way to find out why an extraction from the shell menu failed.
///
/// The file lives beside the exe on purpose: this machine's system drive is
/// nearly full and every SliceArc artefact is supposed to stay on D:.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();

    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "SliceArc.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            Write("ERROR", message);
            return;
        }
        var sb = new StringBuilder($"{message} :: {ex.GetType().Name}: {ex.Message}");
        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 5)
        {
            sb.Append($" <- {inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
            depth++;
        }
        if (ex is System.Windows.Markup.XamlParseException xp && xp.LineNumber > 0)
            sb.Append($" (line {xp.LineNumber}, pos {xp.LinePosition})");
        if (ex.StackTrace is string st)
            sb.Append(Environment.NewLine + st);
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Rotate();
                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }

        try
        {
            if (ConsoleAttached) Console.WriteLine($"[{level.Trim()}] {message}");
        }
        catch
        {
            // No console, no problem.
        }
    }

    private static void Rotate()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes) return;

        var backup = FilePath + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(FilePath, backup);
    }

    // =====================================================================
    // Console bridge: a WinExe started from an existing console has no stdout
    // until it explicitly attaches to the parent's.
    // =====================================================================

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    public static bool ConsoleAttached { get; private set; }

    public static void AttachParentConsole()
    {
        if (ConsoleAttached) return;
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;

            // A WinExe starts with no console, so .NET has already cached UTF-8
            // as Console.OutputEncoding. The console it just attached to is
            // usually a legacy code page (936 here), so the writers are built
            // with that code page instead - and the console's own code page is
            // deliberately left alone, because the parent process shares it.
            var encoding = ConsoleEncoding();
            var stdout = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true };
            Console.SetError(stderr);
            ConsoleAttached = true;
        }
        catch
        {
            ConsoleAttached = false;
        }
    }

    private static Encoding ConsoleEncoding()
    {
        try
        {
            var codePage = GetConsoleOutputCP();
            if (codePage == 0 || codePage == 65001) return Encoding.UTF8;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)codePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    public static void Out(string message)
    {
        Console.WriteLine(message);
        Info(message);
    }
}
