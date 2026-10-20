using System.Diagnostics;
using System.IO;
using System.Windows;
using SliceArc.App.Cli;
using SliceArc.App.Services;
using SliceArc.App.Views;
using SliceArc.Core.Compression;
using SliceArc.Core.Extraction;
using SliceArc.Core.Shell;

namespace SliceArc.App;

/// <summary>
/// Entry point. The same executable is both the GUI and the worker the Explorer
/// context menu launches, so every mode is selected from the command line here.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// True for the silent worker modes the context menu uses: an unexpected
    /// failure there is logged, never turned into a modal dialog nobody is
    /// waiting for.
    /// </summary>
    private bool _silentFailures;

    /// <summary>
    /// Delete SliceArc temp folders older than a day. Both the "open externally"
    /// copies (SliceArc-open-) and the CF_HDROP drag fallback (SliceArc-drag-) are
    /// normally removed when their window closes; this catches the leftovers from a
    /// kill or crash so %TEMP% never accumulates them. A day is far longer than any
    /// real drag/open session, so a live instance's in-flight copy is never touched.
    /// </summary>
    private static void SweepStaleTempFolders()
    {
        try
        {
            var temp = Path.GetTempPath();
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var prefix in new[] { "SliceArc-open-", "SliceArc-drag-" })
            {
                foreach (var dir in Directory.GetDirectories(temp, prefix + "*"))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) continue;
                        Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                        // A locked leftover just survives to the next launch; a sweep
                        // must never be able to stop the app from starting.
                    }
                }
            }
        }
        catch
        {
            // %TEMP% unreadable is not a reason to fail startup.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.AttachParentConsole();

        // Best-effort sweep of stale temp folders ("open externally" copies and the
        // CF_HDROP drag fallback). A normal close deletes them, but a killed process
        // or a crash leaves them behind, and %TEMP% should not silt up over months.
        // Anything older than a day is safe to drop: a live drag/open session is
        // measured in minutes, so this never touches another instance's in-flight copy.
        SweepStaleTempFolders();

        var cli = CommandLineOptions.Parse(e.Args);
        _silentFailures = cli.Quiet && cli.Mode != CliMode.Window;
        AppLog.Info($"启动 mode={cli.Mode} target={cli.Target ?? "(none)"} quiet={cli.Quiet}");

        // A command line we cannot honour is reported, never guessed around. The user
        // asked for something we cannot do (a format we cannot write, or a name that
        // disagrees with --format), so this is the error surface, not a warning.
        if (cli.ArgumentError is { Length: > 0 } argError)
        {
            AppLog.Error(argError);
            AppLog.Out(argError);
            if (!cli.Quiet)
            {
                ResultWindow.Show(null, ResultKind.Error, "命令行参数有误", argError,
                    null, null, "修正参数后重试。运行 --help 可查看全部支持的参数。");
            }
            Shutdown(2);
            return;
        }

        // Headless modes open several windows in sequence (e.g. compress:
        // parameter dialog, then the progress window). OnLastWindowClose would
        // kill the process the moment the first dialog closes, so these modes
        // must shut down explicitly - every code path below ends in Shutdown().
        if (cli.Mode != CliMode.Window) ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var savedTheme = AppSettings.Load().Theme;

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("未处理的 UI 异常", args.Exception);
            if (!_silentFailures)
            {
                ResultWindow.Show(null, ResultKind.Error, "出错", args.Exception.Message,
                    null, null, FailureAdvice.For(args.Exception));
            }
            args.Handled = true;
            Shutdown(1);
        };

        try { ApplyTheme(savedTheme); }
        catch (Exception ex) { AppLog.Error($"主题 {savedTheme} 加载失败，回退浅色", ex); }

        switch (cli.Mode)
        {
            case CliMode.Help:
                AppLog.Out(CommandLineOptions.Usage);
                Shutdown(0);
                break;

            case CliMode.Register:
                ApplyShellMenu(cli, install: true);
                break;

            case CliMode.Unregister:
                ApplyShellMenu(cli, install: false);
                break;

            case CliMode.ShellStatus:
                ReportShellStatus();
                break;

            case CliMode.ExtractAll:
                RunExtractAll(cli);
                break;

            case CliMode.Extract:
                RunExtract(cli);
                break;

            case CliMode.Compress:
                RunCompress(cli);
                break;

            case CliMode.AddToArchive:
                RunAddToArchive(cli);
                break;

            default:
                var main = new MainWindow(cli.Target);
                main.Show();
                ShowWelcomeIfNeeded(main);
                break;
        }
    }

    /// <summary>
    /// The one-time first-run guide (features + extension picker). GUI mode only:
    /// a headless right-click run must never pop a window nobody asked for.
    /// </summary>
    private static void ShowWelcomeIfNeeded(Window owner)
    {
        try
        {
            if (AppSettings.Load().WelcomeShown) return;
            new WelcomeWindow { Owner = owner }.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLog.Error("首次引导显示失败", ex);
        }
    }

    // =====================================================================
    // Shell menu
    // =====================================================================

    private void ApplyShellMenu(CommandLineOptions cli, bool install)
    {
        try
        {
            if (install)
            {
                var exe = Environment.ProcessPath
                          ?? throw new InvalidOperationException("无法确定自身可执行文件路径。");
                var appDir = Path.GetDirectoryName(exe)!;
                var result = ShellMenuRegistrar.Install(
                    new ShellMenuOptions
                    {
                        ExecutablePath = exe,
                        OnlyArchiveExtensions = !cli.AllFiles,
                    },
                    appDir,
                    AppLog.Out);

                AppLog.Out(result.ModernMenu
                    ? $"已注册 Win11 现代右键菜单（一级菜单是「{Brand.Name}」的二级菜单）：{result.Detail}"
                    : $"已注册经典右键菜单（平铺）-> {exe}：现代菜单不可用 - {result.Detail}");

                // File associations are deliberately NOT touched here: which types
                // to take over is the user's explicit choice in the first-run
                // guide / Settings > 文件关联. Registering them all now would
                // silently grab every unassociated archive extension.
                Shutdown(0);
                return;
            }

            // Uninstall. The current-user part never needs elevation; the sparse
            // package and the machine-trusted certificate do, which is why this
            // asks for exactly one elevation - and says so if it is refused.
            var removal = ShellMenuRegistrar.RemoveAll(allowElevation: true, AppLog.Out);
            FileAssociationRegistrar.Unregister();

            if (removal.Ok)
            {
                AppLog.Out("已移除右键菜单、现代菜单包、本机证书与文件关联：" + removal.Detail);
                Shutdown(0);
                return;
            }

            var summary = "右键菜单与文件关联已移除，但系统级组件未能清理干净：" + removal.Detail;
            AppLog.Error(summary);
            if (!_silentFailures)
                ResultWindow.Show(null, ResultKind.Warning, "卸载未完全清理", summary, null, null,
                    $"可在「设置 → 应用 → 已安装的应用」中卸载「{Brand.Name} 右键菜单」，" +
                    "或在管理员 PowerShell 中执行：Get-AppxPackage SliceArc.ContextMenu | Remove-AppxPackage");
            Shutdown(1);
        }
        catch (Exception ex)
        {
            AppLog.Error("右键菜单操作失败", ex);
            ResultWindow.Show(null, ResultKind.Error, "右键菜单操作失败", ex.Message,
                null, null, FailureAdvice.For(ex));
            Shutdown(1);
        }
    }

    private void ReportShellStatus()
    {
        try
        {
            var registered = ShellMenuRegistrar.IsRegistered();
            AppLog.Out(registered
                ? $"经典右键菜单：已注册 -> {ShellMenuRegistrar.RegisteredTarget() ?? "(未知)"}"
                : "经典右键菜单：未注册");

            foreach (var line in ShellMenuRegistrar.Inspect()) AppLog.Out("  " + line);

            var modernVersion = ShellPackageRegistrar.InstalledVersion();
            AppLog.Out(modernVersion is not null
                ? $"现代右键菜单：已注册 v{modernVersion}"
                : "现代右键菜单：未注册");
            AppLog.Out(ShellMenuRegistrar.IsMenuInstalled()
                ? "右键菜单总状态：已启用"
                : "右键菜单总状态：未启用");
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                var problem = ShellPackageRegistrar.FindPrerequisiteProblem(Path.GetDirectoryName(exePath)!);
                if (problem is not null) AppLog.Out("  现代菜单不可用原因: " + problem);
            }
            Shutdown(0);
        }
        catch (Exception ex)
        {
            AppLog.Error("读取右键菜单状态失败", ex);
            Shutdown(1);
        }
    }

    // =====================================================================
    // Headless extraction (the verbs on the context menu land here)
    // =====================================================================

    private void RunExtract(CommandLineOptions cli)
    {
        var target = cli.Target;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            Fail($"找不到要解压的文件：{target ?? "(未指定)"}",
                "文件可能已被移动、重命名或删除，请确认位置后重试。");
            return;
        }

        var archivePath = Path.GetFullPath(target);
        var options = ExtractionRunner.BuildOptions(cli, archivePath);
        AppLog.Info($"解压 {archivePath} -> {options.OutputDirectory}");

        var run = RunWork(cli, cli.TestOnly ? "正在校验" : "正在解压", archivePath, cli.TestOnly, showFileBar: true,
            (progress, token) => (object)ExtractionRunner.Run(archivePath, options, progress, token));

        // An encrypted archive reports NeedsPassword / WrongPassword instead of
        // failing outright. Prompt and retry, at most three times; in --quiet
        // mode a dialog would hang a script, so the failure is just reported.
        for (var attempt = 0; !cli.Quiet && attempt < 3; attempt++)
        {
            if (run.Failure is not null || run.Result is not ExtractionResult probe ||
                (!probe.NeedsPassword && !probe.WrongPassword))
                break;

            var password = PasswordWindow.Prompt(null, Path.GetFileName(archivePath), retry: probe.WrongPassword);
            if (password is null) break;

            options = options with { Password = password };
            AppLog.Info($"使用口令重试解压 {archivePath}（第 {attempt + 1} 次）");
            run = RunWork(cli, cli.TestOnly ? "正在校验" : "正在解压", archivePath, cli.TestOnly, showFileBar: true,
                (progress, token) => (object)ExtractionRunner.Run(archivePath, options, progress, token));
        }

        // Auto-untar: if extracting a .tar.gz/.tar.bz2/.tar.xz produced a single
        // .tar file, extract it too and delete the intermediate .tar.
        if (run.Failure is null && run.Result is ExtractionResult result && result.Ok && !cli.TestOnly)
            TryAutoUntar(options.OutputDirectory, cli);

        Finish(run, cli);
    }

    /// <summary>
    /// Run a long operation and hand back its outcome.
    ///
    /// With a UI (<c>--quiet</c> absent, i.e. everything the Explorer verbs do)
    /// that is the progress dialog the user can cancel; in quiet mode - the
    /// scripted path - the work runs with no window at all, which also saves the
    /// dialog's own cost: the WPF window and its repaints compete with 7z.dll's
    /// decoder threads for the same cores (measured ~0.2 s wall / ~0.9 s CPU on a
    /// 600 MB archive, on an 8-thread machine).
    /// </summary>
    private static RunOutcome RunWork(
        CommandLineOptions cli,
        string headline,
        string target,
        bool testOnly,
        bool showFileBar,
        Func<Action<ExtractionProgress>, CancellationToken, object> work)
    {
        if (!cli.Quiet)
        {
            var window = new ProgressWindow(headline, target, testOnly, showFileBar, work);
            window.ShowDialog();
            return new RunOutcome(window.Result, window.Failure, window.Elapsed);
        }

        var clock = Stopwatch.StartNew();
        try
        {
            return new RunOutcome(work(static _ => { }, CancellationToken.None), null, clock.Elapsed);
        }
        catch (Exception ex)
        {
            AppLog.Error("任务失败", ex);
            return new RunOutcome(null, ex, clock.Elapsed);
        }
    }

    /// <summary>What a run produced, whether or not a progress dialog was shown.</summary>
    private sealed record RunOutcome(object? Result, Exception? Failure, TimeSpan Elapsed);

    /// <summary>
    /// After extracting a compressed tar (e.g. .tar.gz), the output is a single
    /// .tar file. This method detects that case, extracts the .tar to the same
    /// directory, and deletes the intermediate .tar. Best-effort: on failure the
    /// .tar is left in place so the user can extract it manually.
    /// </summary>
    private void TryAutoUntar(string outputDir, CommandLineOptions cli)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(outputDir, "*", SearchOption.TopDirectoryOnly);
            if (entries.Length != 1) return;
            if (!IsTarFile(entries[0]) && !entries[0].EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                return;

            var tarPath = entries[0];
            AppLog.Info($"自动解包 tar: {tarPath} -> {outputDir}");

            var tarOptions = new ExtractionOptions
            {
                OutputDirectory = outputDir,
                Overwrite = cli.Overwrite,
                Password = cli.Password,
            };
            var tarResult = ExtractionRunner.Run(tarPath, tarOptions, null, default);
            if (tarResult.Ok)
            {
                try { File.Delete(tarPath); } catch { /* best-effort */ }
                AppLog.Info("自动解包 tar 完成");
            }
            else
            {
                AppLog.Error($"自动解包 tar 失败: {string.Join("; ", tarResult.Errors)}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"自动解包 tar 异常: {ex.Message}");
        }
    }

    /// <summary>Detect a tar file by the "ustar" magic at offset 257.</summary>
    private static bool IsTarFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 262) return false;
            fs.Seek(257, SeekOrigin.Begin);
            var buf = new byte[5];
            return fs.Read(buf, 0, 5) == 5 && System.Text.Encoding.ASCII.GetString(buf) == "ustar";
        }
        catch { return false; }
    }

    private void RunExtractAll(CommandLineOptions cli)
    {
        var folder = cli.Target;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Fail($"找不到文件夹：{folder ?? "(未指定)"}",
                "请确认文件夹路径是否正确，或该文件夹是否已被移动、删除。");
            return;
        }

        var directory = Path.GetFullPath(folder);
        var candidates = ArchiveScanner.Scan(directory);
        if (candidates.Count == 0)
        {
            Fail($"这个文件夹里没有找到压缩包：{directory}",
                "请把压缩包放进该文件夹，或直接对着压缩包本身使用右键菜单解压。");
            return;
        }

        AppLog.Info($"批量解压 {candidates.Count} 个压缩包于 {directory}");

        var run = RunWork(cli, cli.TestOnly ? "正在批量校验" : $"正在解压 {candidates.Count} 个压缩包", directory,
            cli.TestOnly, showFileBar: true,
            (progress, token) => (object)RunAll(candidates, cli, progress, token));

        Finish(run, cli);
    }

    private static ExtractionResult RunAll(
        IReadOnlyList<ArchiveCandidate> candidates,
        CommandLineOptions cli,
        Action<ExtractionProgress> progress,
        CancellationToken token)
    {
        var extracted = 0;
        var failed = 0;
        long written = 0;
        long freed = 0;
        var deleted = new List<string>();
        var errors = new List<string>();

        foreach (var candidate in candidates)
        {
            if (token.IsCancellationRequested) break;

            try
            {
                var options = ExtractionRunner.BuildOptions(cli, candidate.FirstVolumePath);
                var result = ExtractionRunner.Run(candidate.FirstVolumePath, options, progress, token);

                extracted += result.Extracted;
                failed += result.Failed;
                written += result.BytesWritten;
                freed += result.BytesFreed;
                deleted.AddRange(result.DeletedVolumes);
                errors.AddRange(result.Errors.Select(err => $"{candidate.DisplayName}: {err}"));
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{candidate.DisplayName}: {ex.Message}");
                AppLog.Error($"解压 {candidate.FirstVolumePath} 失败", ex);
            }
        }

        return new ExtractionResult(extracted, failed, written, freed, deleted, errors);
    }

    // =====================================================================
    // Headless compression (the right-click "compress" verbs land here)
    // =====================================================================

    private void RunCompress(CommandLineOptions cli)
    {
        var sources = cli.AddSources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFullPath(s.Trim('"')))
            .ToList();

        if (sources.Count == 0)
        {
            // Fall back to the bare target if no --add was supplied.
            if (string.IsNullOrWhiteSpace(cli.Target) && string.IsNullOrWhiteSpace(cli.OutputArchive))
            {
                Fail("没有指定要压缩的源（用 --add <文件或文件夹>，可重复）",
                    "用法示例：SliceArc --compress --add \"D:\\资料\" --add \"D:\\照片\"");
                return;
            }
            var fallback = !string.IsNullOrWhiteSpace(cli.Target) ? cli.Target : cli.OutputArchive;
            sources.Add(Path.GetFullPath(fallback!.Trim('"')));
        }

        // Validate at least one source exists on disk before we open a window.
        var missing = sources.FirstOrDefault(s => !File.Exists(s) && !Directory.Exists(s));
        if (missing is not null)
        {
            Fail($"找不到源：{missing}", "该文件或文件夹不存在，请确认路径是否正确。");
            return;
        }

        CompressionOptions options;
        string outputPath;

        if (!cli.Quiet)
        {
            // Bandizip-style: the right-click verb opens the parameter dialog
            // (format / level / volume size / password) and compression only
            // starts after the user confirms. --quiet keeps the old fire-and-forget
            // behaviour for scripts.
            // The「压缩并分卷」verbs pass --split-volume 0, meaning "ask me": open the
            // dialog with a split size already selected instead of landing on 不分卷.
            var requireSplit = cli.VolumeSizeExplicit && cli.VolumeSize == 0;
            // Only override the remembered defaults when the caller actually named a
            // format/level; cli.Format is pre-filled with 7z, which is not a user choice
            // and would otherwise shadow whatever the user set in 设置.
            var dialog = new CompressWindow(
                sources,
                cli.FormatExplicit ? cli.Format : null,
                cli.LevelExplicit ? cli.Level : null,
                cli.VolumeSize,
                requireSplit);
            if (dialog.ShowDialog() != true || dialog.Options is null)
            {
                AppLog.Info("压缩已在参数对话框取消");
                Shutdown(0);
                return;
            }
            options = dialog.Options;
            outputPath = options.OutputPath;
        }
        else
        {
            outputPath = ResolveCompressOutput(cli, sources);
            options = new CompressionOptions
            {
                Sources = sources,
                OutputPath = outputPath,
                Format = cli.Format,
                Level = cli.Level,
                Method = cli.Method,
                VolumeSize = cli.VolumeSize,
                Password = cli.Password,
                EncryptHeaders = cli.EncryptHeaders,
                Solid = !cli.NoSolid,
                Threads = cli.Threads,
            };
        }

        AppLog.Info($"压缩 {options.Sources.Count} 项 -> {outputPath} (format={options.Format} level={options.Level} vol={(options.VolumeSize > 0 ? options.VolumeSize.ToString() : "none")})");

        var run = RunWork(cli, $"正在压缩到 {Path.GetFileName(outputPath)}", outputPath, testOnly: false, showFileBar: false,
            (progress, token) => RunCompressWorker(options, ProgressAdapter.ForCompression(progress), token));

        FinishCompress(run, cli, outputPath);
    }

    private static string ResolveCompressOutput(CommandLineOptions cli, IReadOnlyList<string> sources)
    {
        // Explicit --out / first arg to --compress wins.
        if (!string.IsNullOrWhiteSpace(cli.OutputArchive))
            return Path.GetFullPath(cli.OutputArchive!.Trim('"'));
        if (!string.IsNullOrWhiteSpace(cli.OutputDirectory))
            return Path.GetFullPath(cli.OutputDirectory!.Trim('"'));

        // Default: name the archive after the (first) source, next to it.
        var first = sources[0];
        var dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(first))
                  ?? Environment.CurrentDirectory;
        var name = Directory.Exists(first)
            ? new DirectoryInfo(first).Name
            : Path.GetFileNameWithoutExtension(first);
        if (string.IsNullOrWhiteSpace(name)) name = "archive";
        var ext = cli.Format.Extension();
        return Path.Combine(dir, $"{name}.{ext}");
    }

    private static CompressionResult RunCompressWorker(
        CompressionOptions options,
        Action<CompressionProgress> progress,
        CancellationToken token)
    {
        using var engine = new CompressionEngine(options with { CancellationToken = token });
        return engine.Compress(progress);
    }

    private void FinishCompress(RunOutcome run, CommandLineOptions cli, string outputPath)
    {
        if (run.Failure is not null)
        {
            Fail(run.Failure);
            return;
        }

        var result = run.Result as CompressionResult;
        if (result is null)
        {
            Shutdown(0);
            return;
        }

        var summary = DescribeCompress(result, outputPath) + $" · 耗时 {ProgressText.FormatElapsed(run.Elapsed)}";
        AppLog.Out(summary);
        CompletionNotice.Play(AppSettings.Load(), result.Ok);

        if (!result.Ok)
        {
            foreach (var err in result.Errors)
                AppLog.Error("压缩失败: " + err);
            if (!_silentFailures)
                ResultWindow.Show(null, ResultKind.Warning, "压缩完成（有错误）", summary, ClipErrors(result.Errors));
            Shutdown(1);
            return;
        }

        if (!cli.Quiet && AppSettings.Load().ShowCompletionDialog)
            ResultWindow.Show(null, ResultKind.Success, "压缩完成", summary);
        Shutdown(0);
    }

    // =====================================================================
    // Headless "add to an existing archive"
    // =====================================================================

    private void RunAddToArchive(CommandLineOptions cli)
    {
        var archivePath = cli.AddToArchivePath;
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            Fail($"找不到要更新的压缩包：{archivePath ?? "(未指定)"}",
                "用法：SliceArc --add-to \"D:\\资料.7z\" --add \"D:\\新文件\"");
            return;
        }

        var sources = cli.AddSources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFullPath(s.Trim('"')))
            .ToList();

        if (sources.Count == 0)
        {
            Fail("没有指定要添加的内容（用 --add <文件或文件夹>，可重复）",
                "用法：SliceArc --add-to \"D:\\资料.7z\" --add \"D:\\新文件\"");
            return;
        }

        var missing = sources.FirstOrDefault(s => !File.Exists(s) && !Directory.Exists(s));
        if (missing is not null)
        {
            Fail($"找不到源：{missing}", "该文件或文件夹不存在，请确认路径是否正确。");
            return;
        }

        var options = new ArchiveUpdateOptions
        {
            ArchivePath = Path.GetFullPath(archivePath.Trim('"')),
            Sources = sources,
            Level = cli.Level,
            Method = cli.Method,
            Threads = cli.Threads,
            EncryptHeaders = cli.EncryptHeaders,
            Password = cli.Password,
        };

        AppLog.Info($"向 {options.ArchivePath} 添加 {sources.Count} 项");

        var run = RunWork(cli, $"正在添加到 {Path.GetFileName(options.ArchivePath)}", options.ArchivePath,
            testOnly: false, showFileBar: false,
            (progress, token) => RunAddToWorker(options, ProgressAdapter.ForCompression(progress), token));

        FinishAddTo(run, cli, options.ArchivePath);
    }

    private static ArchiveUpdateResult RunAddToWorker(
        ArchiveUpdateOptions options,
        Action<CompressionProgress> progress,
        CancellationToken token)
    {
        using var engine = new ArchiveUpdateEngine(options with { CancellationToken = token });
        return engine.Update(progress);
    }

    private void FinishAddTo(RunOutcome run, CommandLineOptions cli, string archivePath)
    {
        if (run.Failure is UnsupportedArchiveUpdateException unsupported)
        {
            Fail(unsupported.Message, unsupported.Hint);
            return;
        }
        if (run.Failure is not null)
        {
            Fail(run.Failure);
            return;
        }

        var result = run.Result as ArchiveUpdateResult;
        if (result is null)
        {
            Shutdown(0);
            return;
        }

        var summary = DescribeUpdate(result) + $" · 耗时 {ProgressText.FormatElapsed(run.Elapsed)}";
        AppLog.Out(summary);
        CompletionNotice.Play(AppSettings.Load(), result.Ok);

        if (!result.Ok)
        {
            foreach (var err in result.Errors)
                AppLog.Error("添加失败: " + err);
            if (!_silentFailures)
                ResultWindow.Show(null, ResultKind.Warning, "添加完成（有错误）", summary, ClipErrors(result.Errors));
            Shutdown(1);
            return;
        }

        if (!cli.Quiet && AppSettings.Load().ShowCompletionDialog)
            ResultWindow.Show(null, ResultKind.Success, "已添加到压缩包", summary);
        Shutdown(0);
    }

    private static string DescribeUpdate(ArchiveUpdateResult result)
    {
        var parts = new List<string>();
        if (result.ItemsAdded > 0) parts.Add($"新增 {result.ItemsAdded} 项");
        if (result.ItemsUpdated > 0) parts.Add($"更新 {result.ItemsUpdated} 项");
        if (result.ItemsKept > 0) parts.Add($"保留 {result.ItemsKept} 项");
        if (result.BytesWritten > 0) parts.Add($"输出 {result.BytesWritten / 1024.0 / 1024.0:0.#} MB");
        if (result.Cancelled) parts.Add("已取消");
        if (parts.Count == 0) parts.Add("没有变化");
        return string.Join(" · ", parts);
    }

    /// <summary>First 10 errors for the dialog; the overflow note points at the log.</summary>
    private static List<string> ClipErrors(IReadOnlyCollection<string> errors)
    {
        var shown = errors.Take(10).ToList();
        if (errors.Count > 10) shown.Add($"… 其余 {errors.Count - 10} 条见 SliceArc.log");
        return shown;
    }

    private static string DescribeCompress(CompressionResult result, string outputPath)
    {
        var parts = new List<string> { $"压缩 {result.ItemsAdded} 项" };
        if (result.BytesRead > 0) parts.Add($"输入 {result.BytesRead / 1024.0 / 1024.0:0.#} MB");
        if (result.BytesWritten > 0) parts.Add($"输出 {result.BytesWritten / 1024.0 / 1024.0:0.#} MB");
        if (result.VolumePaths.Count > 1) parts.Add($"分卷 {result.VolumePaths.Count} 个");
        if (result.Cancelled) parts.Add("已取消");
        return string.Join(" · ", parts);
    }

    private void Finish(RunOutcome run, CommandLineOptions cli)
    {
        if (run.Failure is not null)
        {
            Fail(run.Failure);
            return;
        }

        var result = run.Result as ExtractionResult;
        if (result is null)
        {
            Shutdown(0);
            return;
        }

        var summary = Describe(result, cli.TestOnly) + $" · 耗时 {ProgressText.FormatElapsed(run.Elapsed)}";
        AppLog.Out(summary);
        if (result.FailureReason is not null) AppLog.Out("  原因：" + result.FailureReason);
        if (result.FailureHint is not null) AppLog.Out("  建议：" + result.FailureHint);
        foreach (var err in result.Errors) AppLog.Out("  ! " + err);
        CompletionNotice.Play(AppSettings.Load(), result.Ok);

        if (!result.Ok)
        {
            if (!_silentFailures)
                ResultWindow.Show(null, ResultKind.Warning, cli.TestOnly ? "校验完成（有错误）" : "解压完成（有错误）",
                    summary, ClipErrors(result.Errors), result.FailureReason, result.FailureHint);
            Shutdown(1);
            return;
        }

        if (!cli.Quiet && AppSettings.Load().ShowCompletionDialog)
            ResultWindow.Show(null, ResultKind.Success, cli.TestOnly ? "校验完成" : "解压完成", summary);
        Shutdown(0);
    }

    private static string Describe(ExtractionResult result, bool testOnly)
    {
        var parts = new List<string>
        {
            testOnly ? $"校验 {result.Tested} 项通过" : $"解压 {result.Extracted} 项",
        };
        if (result.Skipped > 0) parts.Add($"跳过 {result.Skipped} 项");
        if (result.Failed > 0) parts.Add($"失败 {result.Failed} 项");
        if (result.BytesFreed > 0) parts.Add($"回收 {result.BytesFreed / 1024.0 / 1024.0:0.#} MB（{result.DeletedVolumes.Count} 卷）");
        if (result.Cancelled) parts.Add("已取消");
        return string.Join(" · ", parts);
    }

    private void Fail(string message, string? hint = null)
    {
        AppLog.Error(message);
        if (!_silentFailures)
            ResultWindow.Show(null, ResultKind.Error, "操作失败", message, null, null, hint);
        Shutdown(1);
    }

    /// <summary>Headless failure carrying the same actionable advice the UI shows.</summary>
    private void Fail(Exception ex) => Fail(ex.Message, FailureAdvice.For(ex));

    // =====================================================================
    // Theme switching
    // =====================================================================

    private static readonly string[] ValidThemes = { "Light", "Bandizip", "Dark", "Soft" };

    /// <summary>Currently applied palette; native title-bar tint reads this.</summary>
    public static string CurrentTheme { get; private set; } = "Light";

    /// <summary>
    /// Swap the palette override dictionary at runtime. Minimal.xaml stays loaded
    /// (it holds the controls + the default Light palette); a second dictionary
    /// overrides the B.* brush keys when a non-Light theme is selected.
    /// </summary>
    public static void ApplyTheme(string? theme)
    {
        var t = ValidThemes.Contains(theme) ? theme! : "Light";
        CurrentTheme = t;
        var app = Current as App;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts
            .Where(d => d.Source is not null && d.Source.OriginalString.Contains("Palette."))
            .ToList();
        foreach (var d in existing) dicts.Remove(d);

        if (t == "Light") return;

        var source = new Uri($"pack://application:,,,/Themes/Palette.{t}.xaml");
        try
        {
            dicts.Add(new ResourceDictionary { Source = source });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"加载主题 {t} 失败: {ex.Message}");
        }
    }
}
