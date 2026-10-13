using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using SliceArc.App.Models;
using SliceArc.App.Services;
using SliceArc.Core.Compression;
using SliceArc.Core.Extraction;
using SliceArc.Core.Shell;
using DragOut = SliceArc.Core.DragDrop.DragOut;
using VirtualFileDataObject = SliceArc.Core.DragDrop.VirtualFileDataObject;
using VirtualFileEntry = SliceArc.Core.DragDrop.VirtualFileEntry;

namespace SliceArc.App.Views;

/// <summary>
/// The whole UI: one toolbar, one address strip, one list, one status line.
/// The list is either the inside of an open archive or a folder on disk; the
/// window never keeps an <see cref="ExtractionEngine"/> alive between actions,
/// so browsing cannot lock a volume that a later run wants to delete.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<BrowserRow> _rows = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private ArchiveSummary? _archive;
    private string _folder = string.Empty;
    private string _entryPrefix = string.Empty;
    private string _sortMember = "Name";
    private bool _sortAscending = true;
    private string _filter = string.Empty;
    private bool _busy;
    private bool _shellUiSync;
    private bool _settingsUiSync;
    private bool _dragging;
    private Point _dragStart;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _runClock = Stopwatch.StartNew();

    /// <summary>
    /// Sticky per-run flag: once a member with a known size has been seen, the
    /// per-file bar stays up for the rest of the run so a stray directory item
    /// does not make the panel flicker.
    /// </summary>
    private bool _fileProgressShown;

    /// <summary>.z01 / .r00 / .part1.rar / .001 - the volume styles whose first part matters.</summary>
    [GeneratedRegex(@"\.(z|r|s)\d{2}$|\.part\d+\.rar$|\.\d{3,}$", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePartRegex();

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        EntryGrid.ItemsSource = _rows;

        // Never open larger than the desktop's working area: on a 160% scaled
        // display the logical desktop can be barely bigger than the defaults,
        // and a window that exactly fills the screen looks maximised.
        var work = SystemParameters.WorkArea;
        Width = Math.Clamp(_settings.WindowWidth, MinWidth, Math.Max(MinWidth, work.Width - 40));
        Height = Math.Clamp(_settings.WindowHeight, MinHeight, Math.Max(MinHeight, work.Height - 40));

        DeleteSpentCheck.IsChecked = _settings.DeleteSpentVolumes;
        KeepLastBox.SelectedIndex = Math.Clamp(_settings.KeepLastVolumes, 0, 3);

        ShowCompleteCheck.IsChecked = _settings.ShowCompletionDialog;
        PlaySoundCheck.IsChecked = _settings.PlaySoundOnComplete;
        _settingsUiSync = true;
        try
        {
            var soundIndex = Array.IndexOf(CompletionNotice.SoundNames, _settings.CompletionSound);
            SoundBox.SelectedIndex = soundIndex >= 0 ? soundIndex : 0;
        }
        finally { _settingsUiSync = false; }

        SyncShellMenuUi();
        SyncThemeUi();
        UpdateCommands();
        ShowEmptyHint("把压缩包或文件夹拖进来，或点左上角「打开」");
        StatusRight.Text = "就绪";

        if (!string.IsNullOrWhiteSpace(initialPath)) Load(initialPath!);
    }

    // =====================================================================
    // Loading
    // =====================================================================

    private void Load(string path)
    {
        try
        {
            if (Directory.Exists(path)) OpenFolder(path);
            else if (File.Exists(path)) OpenArchive(path);
            else ShowMessage("找不到路径", path);
        }
        catch (Exception ex)
        {
            Report("打开失败", ex);
        }
    }

    private void OpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            ShowMessage("找不到文件夹", full);
            return;
        }

        _archive = null;
        _folder = full;
        _entryPrefix = string.Empty;
        _settings.LastDirectory = full;

        Title = $"{Brand.Name} — {full}";

        RefreshList();
        StatusRight.Text = "文件夹视图";
    }

    private void OpenArchive(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            ShowMessage("找不到文件", full);
            return;
        }

        var summary = ReadArchive(full);
        _archive = summary;
        _folder = string.Empty;
        _entryPrefix = string.Empty;
        _settings.LastDirectory = Path.GetDirectoryName(full);

        Title = $"{Brand.Name} — {summary.FileName}";
        RefreshList();

        var bits = new List<string> { summary.FileName, summary.FormatName };
        if (summary.VolumeCount > 1) bits.Add($"{summary.VolumeCount} 卷");
        bits.Add(BrowserRow.FormatSize(summary.TotalBytes));
        if (!summary.Complete) bits.Add("⚠ 分卷不完整");
        StatusRight.Text = string.Join(" · ", bits);
    }

    /// <summary>
    /// Read the archive's metadata and throw the engine away again: the COM
    /// objects belong to the thread that created them, and the window has no
    /// business holding them across a background extraction.
    /// </summary>
    private static ArchiveSummary ReadArchive(string path)
    {
        using var engine = new ExtractionEngine(path);
        engine.Open();
        return new ArchiveSummary(
            engine.InputPath,
            engine.Entries.ToArray(),
            engine.VolumeCount,
            engine.TotalArchiveBytes,
            engine.Group?.IsComplete ?? true,
            engine.Format?.Name ?? "unknown");
    }

    // =====================================================================
    // List construction
    // =====================================================================

    private void RefreshList()
    {
        List<BrowserRow> rows;
        try
        {
            rows = _archive is not null ? BuildArchiveRows() : BuildFolderRows();
        }
        catch (Exception ex)
        {
            Report("读取列表失败", ex);
            return;
        }

        if (_filter.Length > 0)
        {
            rows = rows
                .Where(r => r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        rows.Sort(CompareRows);
        if (!_sortAscending) rows.Reverse();

        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);

        UpdateEmptyHint();
        UpdateCounts();
        UpdateCommands();
    }

    private List<BrowserRow> BuildArchiveRows()
    {
        var archive = _archive!;
        var rows = new List<BrowserRow>();
        var implicitDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            var path = entry.NormalizedPath;
            if (path.Length <= _entryPrefix.Length) continue;
            if (!path.StartsWith(_entryPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = path[_entryPrefix.Length..].TrimEnd('/');
            if (rest.Length == 0) continue;

            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var dirName = rest[..slash];
                if (implicitDirs.Add(dirName))
                {
                    rows.Add(new BrowserRow
                    {
                        Name = dirName,
                        Glyph = "\uE8B7",
                        EntryPath = _entryPrefix + dirName + "/",
                        IsDirectory = true,
                        TypeText = "文件夹",
                        Note = "隐式目录",
                    });
                }
                continue;
            }

            if (entry.IsDirectory)
            {
                rows.Add(new BrowserRow
                {
                    Name = rest,
                    Glyph = "\uE8B7",
                    EntryPath = path.TrimEnd('/') + "/",
                    IsDirectory = true,
                    Modified = entry.Modified,
                    TypeText = "文件夹",
                });
                continue;
            }

            rows.Add(new BrowserRow
            {
                Name = rest,
                Glyph = "\uE7C3",
                EntryPath = path,
                Size = entry.Size,
                PackedSize = entry.PackedSize,
                Modified = entry.Modified,
                TypeText = DescribeType(entry.Extension),
                Note = DescribeEntry(entry),
            });
        }

        return rows;
    }

    private List<BrowserRow> BuildFolderRows()
    {
        var rows = new List<BrowserRow>();
        if (!Directory.Exists(_folder)) return rows;

        foreach (var dir in Directory.EnumerateDirectories(_folder))
        {
            var info = new DirectoryInfo(dir);
            rows.Add(new BrowserRow
            {
                Name = info.Name,
                Glyph = "\uE8B7",
                FullPath = info.FullName,
                IsDirectory = true,
                Modified = info.LastWriteTime,
                TypeText = "文件夹",
            });
        }

        foreach (var file in Directory.EnumerateFiles(_folder))
        {
            var info = new FileInfo(file);
            var candidate = DescribeArchiveFile(info);
            var isTail = IsVolumeTail(info.Name, candidate);

            rows.Add(new BrowserRow
            {
                Name = info.Name,
                Glyph = candidate is not null && !isTail ? "\uE7B8" : "\uE7C3",
                FullPath = info.FullName,
                IsArchive = candidate is not null && !isTail,
                IsVolumeTail = isTail,
                Size = info.Length,
                Modified = info.LastWriteTime,
                VolumeCount = candidate?.VolumeCount ?? 1,
                TypeText = candidate is not null ? "压缩包" : DescribeType(info.Extension),
                Note = isTail
                    ? $"分卷（共 {candidate!.VolumeCount} 卷，请打开第 1 卷）"
                    : candidate is { IsMultiVolume: true } ? $"{candidate.VolumeCount} 卷"
                    : candidate is { IsComplete: false } ? "分卷不完整"
                    : string.Empty,
            });
        }

        return rows;
    }

    /// <summary>
    /// Only files that can plausibly be an archive are described, because the
    /// description enumerates the directory per candidate - doing that for every
    /// file would make large folders crawl.
    /// </summary>
    private static ArchiveCandidate? DescribeArchiveFile(FileInfo info)
    {
        var looksInteresting = ArchiveScanner.IsKnownArchiveFile(info.Name)
                               || VolumePartRegex().IsMatch(info.Name);
        return looksInteresting ? ArchiveScanner.Describe(info.FullName) : null;
    }

    private static bool IsVolumeTail(string fileName, ArchiveCandidate? candidate)
    {
        if (candidate is null) return false;
        if (!candidate.IsMultiVolume) return false;
        var first = Path.GetFileName(candidate.FirstVolumePath);
        return !string.Equals(first, fileName, StringComparison.OrdinalIgnoreCase);
    }

    private int CompareRows(BrowserRow a, BrowserRow b)
    {
        // Folders above files, always: that is what a file list is expected to do.
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;

        return _sortMember switch
        {
            "Size" => a.Size.CompareTo(b.Size),
            "PackedSize" => a.PackedSize.CompareTo(b.PackedSize),
            "Modified" => Nullable.Compare(a.Modified, b.Modified),
            "TypeText" => string.Compare(a.TypeText, b.TypeText, StringComparison.OrdinalIgnoreCase),
            "Note" => string.Compare(a.Note, b.Note, StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
    }

    private void UpdateEmptyHint()
    {
        if (_rows.Count > 0)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyHint.Visibility = Visibility.Visible;
        EmptyHintText.Text = _archive is not null
            ? "这个位置没有可显示的内容"
            : string.IsNullOrEmpty(_folder)
                ? "把压缩包或文件夹拖进来，或点左上角「打开」"
                : "这个文件夹是空的";
    }

    private void UpdateCounts()
    {
        var selected = EntryGrid.SelectedItems.Count;
        var bytes = _rows.Where(r => !r.IsDirectory).Sum(r => r.Size);

        var parts = new List<string> { $"{_rows.Count} 项" };
        if (selected > 0) parts.Add($"已选 {selected} 项");
        if (bytes > 0) parts.Add($"合计 {BrowserRow.FormatSize(bytes)}");
        if (_archive is { VolumeCount: > 1 }) parts.Add($"分卷 {_archive.VolumeCount}");
        StatusLeft.Text = string.Join(" · ", parts);
    }

    private void UpdateLocationText() { /* no address strip — path shown in Title only */ }

    private void UpdateCommands()
    {
        var hasArchive = _archive is not null;
        var idle = !_busy;
        var canCompress = idle && _archive is null && (_folder.Length > 0 || EntryGrid.SelectedItems.Count > 0);

        OpenButton.IsEnabled = idle;
        CompressButton.IsEnabled = canCompress;
        RefreshButton.IsEnabled = idle;
        UpButton.IsEnabled = idle && CanGoUp();
        ExtractButton.IsEnabled = idle && hasArchive;
        QuickExtractButton.IsEnabled = idle && hasArchive;
        TestButton.IsEnabled = idle && hasArchive;
        DeleteSpentCheck.IsEnabled = idle && hasArchive;
        KeepLastBox.IsEnabled = idle && hasArchive;
        CancelButton.IsEnabled = _busy && !_dragging;
    }

    private bool CanGoUp()
    {
        if (_archive is not null) return _entryPrefix.Length > 0;
        if (_folder.Length == 0) return false;
        return Directory.GetParent(_folder) is not null;
    }

    // =====================================================================
    // Extraction
    // =====================================================================

    private async Task RunExtractionAsync(bool testOnly, string? outputDirectory)
    {
        if (_busy) return;

        if (_archive is null)
        {
            ShowMessage("还没有打开压缩包", "先在列表里打开一个压缩包，或把 .7z.001 拖进来。");
            return;
        }

        var archivePath = _archive.Path;
        var selection = SelectedEntryPaths();

        var options = new ExtractionOptions
        {
            OutputDirectory = outputDirectory
                              ?? Path.GetDirectoryName(archivePath)
                              ?? Environment.CurrentDirectory,
            IncludePaths = selection.Count > 0 ? selection : null,
            DeleteSpentVolumes = !testOnly && DeleteSpentCheck.IsChecked == true,
            KeepLastVolumes = Math.Max(0, KeepLastBox.SelectedIndex),
            Overwrite = _settings.Overwrite,
            TestOnly = testOnly,
        };

        if (!testOnly)
        {
            try
            {
                Directory.CreateDirectory(options.OutputDirectory);
            }
            catch (Exception ex)
            {
                // Nothing has been written yet, so bail out before the progress
                // panel is even shown and tell the user why.
                Report("无法准备目标文件夹", ex);
                return;
            }
        }

        SetBusy(true);
        var cts = new CancellationTokenSource();
        _cts = cts;
        _runClock.Restart();
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressPercent.Text = "0%";
        ProgressDetail.Text = testOnly ? "正在校验…" : $"正在解压到 {options.OutputDirectory}";
        _fileProgressShown = false;
        FileProgressPanel.Visibility = Visibility.Collapsed;

        ExtractionResult? result = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                result = await Task.Run(() =>
                    ExtractionRunner.Run(archivePath, options, OnProgress, cts.Token));
            }
            catch (Exception ex)
            {
                Report("解压失败", ex);
                break;
            }
            finally
            {
                SetBusy(false);
                cts.Dispose();
                _cts = null;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }

            // Encrypted payload: ask for the password and run again, up to three
            // times. The progress panel is hidden while the prompt is up.
            if (result is null || attempt >= 3 ||
                (!result.NeedsPassword && !result.WrongPassword))
                break;

            var password = PasswordWindow.Prompt(this, Path.GetFileName(archivePath),
                retry: result.WrongPassword);
            if (password is null) break;

            options = options with { Password = password };

            SetBusy(true);
            cts = new CancellationTokenSource();
            _cts = cts;
            _runClock.Restart();
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            ProgressPercent.Text = "0%";
            ProgressDetail.Text = testOnly ? "正在校验…" : $"正在解压到 {options.OutputDirectory}";
            _fileProgressShown = false;
            FileProgressPanel.Visibility = Visibility.Collapsed;
        }

        if (result is null) return;

        var summary = DescribeResult(result, testOnly) + $" · 耗时 {ProgressText.FormatElapsed(_runClock.Elapsed)}";
        StatusRight.Text = summary;
        AppLog.Info($"{archivePath} -> {options.OutputDirectory}: {summary}");
        CompletionNotice.Play(_settings, result.Ok);

        if (!result.Ok) ShowIssues("解压结束，但有失败项", summary, result);
        else if (_settings.ShowCompletionDialog)
            ResultWindow.Show(this, ResultKind.Success, testOnly ? "校验完成" : "解压完成", summary);

        // Volumes are gone from disk now; a folder view has to be re-read.
        if (result.DeletedVolumes.Count > 0 && _archive is null) RefreshList();
        UpdateCounts();
    }

    private void OnProgress(ExtractionProgress progress) =>
        Dispatcher.BeginInvoke(new Action(() => ApplyProgress(progress)));

    private void ApplyProgress(ExtractionProgress progress)
    {
        ProgressBar.Value = progress.Fraction;
        ProgressPercent.Text = $"{progress.Fraction * 100:0}%";

        if (progress.CurrentFileTotal > 0) _fileProgressShown = true;
        FileProgressPanel.Visibility = _fileProgressShown ? Visibility.Visible : Visibility.Collapsed;

        if (progress.FileFraction is { } fileFraction)
        {
            FileProgressName.Text = progress.CurrentEntry ?? string.Empty;
            FileProgressBar.Value = fileFraction;
            FileProgressPercent.Text = $"{fileFraction * 100:0}%";
        }

        ProgressDetail.Text = ProgressText.Describe(progress, _runClock.Elapsed, false, includeEntry: !_fileProgressShown);
    }

    private static string DescribeResult(ExtractionResult result, bool testOnly)
    {
        var parts = new List<string>();
        if (testOnly)
        {
            parts.Add($"校验 {result.Tested} 项通过");
        }
        else
        {
            parts.Add($"解压 {result.Extracted} 项");
        }

        if (result.Directories > 0 && !testOnly) parts.Add($"{result.Directories} 个目录");
        if (result.Skipped > 0) parts.Add($"跳过 {result.Skipped} 项");
        if (result.Failed > 0) parts.Add($"失败 {result.Failed} 项");
        if (result.BytesFreed > 0)
            parts.Add($"回收 {BrowserRow.FormatSize(result.BytesFreed)}（{result.DeletedVolumes.Count} 卷）");
        if (result.Cancelled) parts.Add("已取消");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Archive paths for the current selection. Selecting a folder means
    /// "everything under it", which is what a user clicking a folder expects.
    /// </summary>
    private List<string> SelectedEntryPaths()
    {
        var result = new List<string>();
        if (_archive is null) return result;

        foreach (var item in EntryGrid.SelectedItems)
        {
            if (item is not BrowserRow row) continue;

            if (!row.IsDirectory)
            {
                if (row.EntryPath.Length > 0) result.Add(row.EntryPath);
                continue;
            }

            var prefix = row.EntryPath.TrimEnd('/') + "/";
            result.Add(prefix);
            foreach (var entry in _archive.Entries)
            {
                if (entry.NormalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(entry.NormalizedPath);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateCommands();
    }

    // =====================================================================
    // Compression
    // =====================================================================

    private void OnCompressClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_archive is not null) return;

        var initial = CollectCompressionSources();
        if (initial.Count == 0)
        {
            ShowMessage("没有可压缩的内容", "先打开一个文件夹或选中一些文件。");
            return;
        }

        var dialog = new CompressWindow(initial) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.Options is null) return;

        _ = RunCompressionAsync(dialog.Options);
    }

    private List<string> CollectCompressionSources()
    {
        var selected = EntryGrid.SelectedItems.Cast<BrowserRow>().ToList();
        var paths = new List<string>();

        foreach (var row in selected)
        {
            if (row.IsDirectory || row.FullPath.Length > 0)
            {
                var p = row.FullPath.Length > 0 ? row.FullPath : Path.Combine(_folder, row.Name);
                if (Directory.Exists(p) || File.Exists(p)) paths.Add(p);
            }
        }

        if (paths.Count > 0) return paths;
        if (_folder.Length > 0 && Directory.Exists(_folder)) return new List<string> { _folder };
        return new List<string>();
    }

    private async Task RunCompressionAsync(CompressionOptions options)
    {
        SetBusy(true);
        var cts = new CancellationTokenSource();
        _cts = cts;
        _runClock.Restart();
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressPercent.Text = "0%";
        ProgressDetail.Text = $"正在压缩到 {options.OutputPath}";
        FileProgressPanel.Visibility = Visibility.Collapsed;

        CompressionResult? result = null;
        try
        {
            result = await Task.Run(() =>
            {
                using var engine = new CompressionEngine(options with { CancellationToken = cts.Token });
                return engine.Compress(OnCompressProgress);
            });
        }
        catch (Exception ex)
        {
            Report("压缩失败", ex);
        }
        finally
        {
            SetBusy(false);
            cts.Dispose();
            _cts = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }

        if (result is null) return;

        var parts = new List<string> { $"压缩 {result.ItemsAdded} 项" };
        if (result.BytesRead > 0)
            parts.Add($"输入 {BrowserRow.FormatSize(result.BytesRead)}");
        if (result.BytesWritten > 0)
            parts.Add($"输出 {BrowserRow.FormatSize(result.BytesWritten)}");
        if (result.VolumePaths.Count > 1) parts.Add($"分卷 {result.VolumePaths.Count} 个");
        if (result.Cancelled) parts.Add("已取消");
        var summary = string.Join(" · ", parts) + $" · 耗时 {ProgressText.FormatElapsed(_runClock.Elapsed)}";

        StatusRight.Text = summary;
        AppLog.Info($"压缩完成 -> {options.OutputPath}: {summary}");
        CompletionNotice.Play(_settings, result.Ok);

        if (!result.Ok)
        {
            ResultWindow.Show(this, ResultKind.Warning, "压缩完成（有错误）", summary, result.Errors.Take(10).ToList());
        }
        else if (_settings.ShowCompletionDialog)
        {
            ResultWindow.Show(this, ResultKind.Success, "压缩完成", summary);
        }

        // Refresh folder view so the new archive appears in the list.
        if (_archive is null && _folder.Length > 0) RefreshList();
        UpdateCounts();
    }

    private void OnCompressProgress(CompressionProgress progress) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ProgressBar.Value = progress.Fraction;
            ProgressPercent.Text = $"{progress.Fraction * 100:0}%";
            var detail = !string.IsNullOrEmpty(progress.CurrentEntry)
                ? progress.CurrentEntry
                : "压缩中…";
            detail += $"  ·  {BrowserRow.FormatSize(progress.BytesDone)} / {BrowserRow.FormatSize(progress.BytesTotal)}";
            if (progress.VolumeCount > 1)
                detail += $"  ·  分卷 {progress.VolumeIndex + 1}/{progress.VolumeCount}";
            ProgressDetail.Text = detail;
        }));

    // =====================================================================
    // Settings popup + theme
    // =====================================================================

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var theme = btn.Tag as string ?? "Light";

        App.ApplyTheme(theme);
        _settings.Theme = theme;
        _settings.Save();
        SyncThemeUi();
        WindowTheme.Apply(this); // native chrome follows the palette immediately
        SettingsPopup.IsOpen = false;

        StatusRight.Text = $"主题已切换：{theme}";
    }

    /// <summary>Mark the active theme in the settings popup with the accent colour.</summary>
    private void SyncThemeUi()
    {
        foreach (var btn in new[] { ThemeLight, ThemeBandizip, ThemeDark, ThemeSoft })
        {
            var active = (btn.Tag as string) == _settings.Theme;
            btn.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
                active ? "B.Accent" : "B.Text");
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void OnCompletionToggled(object sender, RoutedEventArgs e)
    {
        if (_settingsUiSync) return;
        _settings.ShowCompletionDialog = ShowCompleteCheck.IsChecked == true;
        _settings.PlaySoundOnComplete = PlaySoundCheck.IsChecked == true;
        _settings.Save();

        if (sender == PlaySoundCheck && PlaySoundCheck.IsChecked == true)
            CompletionNotice.Play(_settings, success: true); // preview the chosen sound
    }

    private void OnSoundChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsUiSync) return;
        if (SoundBox.SelectedItem is ComboBoxItem item && item.Tag is string name)
        {
            _settings.CompletionSound = name;
            _settings.Save();
            if (PlaySoundCheck.IsChecked == true) CompletionNotice.Play(_settings, success: true);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OnAssociationsClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        new AssociationsWindow { Owner = this }.ShowDialog();
    }

    // =====================================================================
    // Drag out (the zero-temp-file path)
    // =====================================================================

    private void OnGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(EntryGrid);

    private void OnGridPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_busy || _dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var position = e.GetPosition(EntryGrid);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = (e.OriginalSource as FrameworkElement)?.DataContext as BrowserRow
                  ?? EntryGrid.SelectedItem as BrowserRow;
        if (row is null) return;

        // Dragging an unselected row drags that row, exactly like Explorer does.
        var selection = EntryGrid.SelectedItems.Cast<BrowserRow>().ToList();
        if (!selection.Contains(row)) selection = new List<BrowserRow> { row };

        _dragging = true;
        try
        {
            DragRowsOut(selection);
        }
        catch (Exception ex)
        {
            Report("拖拽失败", ex);
        }
        finally
        {
            _dragging = false;
        }
    }

    private void DragRowsOut(IReadOnlyList<BrowserRow> rows)
    {
        if (_archive is not null)
        {
            DragArchiveEntriesOut(rows);
            return;
        }

        // Folder view: these are real files, so the shell's own FileDrop is
        // already temp-free and there is no reason to reinvent it.
        var paths = rows.Where(r => r.FullPath.Length > 0).Select(r => r.FullPath).ToArray();
        if (paths.Length == 0) return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        var effect = DragDrop.DoDragDrop(EntryGrid, data, DragDropEffects.Copy);
        StatusRight.Text = effect == DragDropEffects.None
            ? "拖拽已取消"
            : $"已拖出 {paths.Length} 项";
    }

    private void DragArchiveEntriesOut(IReadOnlyList<BrowserRow> rows)
    {
        var files = BuildVirtualFiles(rows);
        if (files.Count == 0)
        {
            StatusRight.Text = "没有可拖出的文件";
            return;
        }

        var dataObject = new VirtualFileDataObject(_archive!.Path, files, OnDragProgress);
        dataObject.Finished += OnDragFinished;

        BeginDragFeedback(files);

        var effect = DragOut.DoDragDrop(dataObject);
        CancelButton.IsEnabled = true;

        if (effect == 0)
        {
            // Nothing was accepted, so tear the decodes down right away.
            dataObject.Dispose();
            ProgressPanel.Visibility = Visibility.Collapsed;
            StatusRight.Text = "拖拽已取消";
            return;
        }

        // The object outlives this call on purpose: after a successful drop the
        // shell keeps pulling bytes while it writes the destination files, and
        // it releases itself once the last decode is done.
        dataObject.MarkDragFinished(cancelled: false);
        StatusRight.Text =
            $"已拖出 {files.Count} 个文件（{BrowserRow.FormatSize(dataObject.TotalBytes)}），正在写入目标位置…";
    }

    /// <summary>
    /// Flatten the selection into the file list the shell will create. A selected
    /// folder becomes every file under it, named relative to the folder currently
    /// being browsed, so the drop reproduces the tree.
    /// </summary>
    private List<VirtualFileEntry> BuildVirtualFiles(IReadOnlyList<BrowserRow> rows)
    {
        var archive = _archive!;
        var files = new List<VirtualFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (!row.IsDirectory)
            {
                if (row.EntryPath.Length == 0) continue;
                var name = RelativeName(row.EntryPath);
                if (seen.Add(name))
                    files.Add(new VirtualFileEntry(name, row.EntryPath, row.Size, row.Modified));
                continue;
            }

            var prefix = row.EntryPath.TrimEnd('/') + "/";
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory) continue;
                if (!entry.NormalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var name = RelativeName(entry.NormalizedPath);
                if (seen.Add(name))
                    files.Add(new VirtualFileEntry(name, entry.NormalizedPath, entry.Size, entry.Modified));
            }
        }

        return files;
    }

    private string RelativeName(string entryPath)
    {
        var name = _entryPrefix.Length > 0 &&
                   entryPath.StartsWith(_entryPrefix, StringComparison.OrdinalIgnoreCase)
            ? entryPath[_entryPrefix.Length..]
            : entryPath;

        return name.Replace('/', '\\').TrimStart('\\');
    }

    private void OnDragProgress(EntryStreamProgress progress) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.Value = progress.Fraction;
            ProgressPercent.Text = $"{progress.Fraction * 100:0}%";
            ProgressDetail.Text =
                $"拖出 {Path.GetFileName(progress.EntryPath)} · " +
                $"{BrowserRow.FormatSize(progress.BytesRead)}/{BrowserRow.FormatSize(progress.TotalBytes)} · 零临时文件";
        }));

    private void OnDragFinished() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            StatusRight.Text = "拖拽完成（全程未产生临时文件）";
            UpdateCounts();
        }));

    private void BeginDragFeedback(IReadOnlyList<VirtualFileEntry> files)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressPercent.Text = "0%";
        ProgressDetail.Text = $"正在为拖拽解码 {files.Count} 个文件…";
        CancelButton.IsEnabled = false;
        StatusRight.Text = $"拖出 {files.Count} 个文件…（目标文件由资源管理器现场创建，不经过临时文件）";
    }

    // =====================================================================
    // Toolbar / context menu handlers
    // =====================================================================

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开压缩包",
            Filter = "压缩包|*.7z;*.zip;*.rar;*.tar;*.gz;*.bz2;*.xz;*.zst;*.cab;*.iso;*.lzh;*.001;*.z01;*.r00;*.part1.rar|所有文件|*.*",
            InitialDirectory = _settings.LastDirectory,
        };

        if (dialog.ShowDialog(this) == true) Load(dialog.FileName);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "打开文件夹",
            InitialDirectory = _settings.LastDirectory,
        };

        if (dialog.ShowDialog(this) == true) Load(dialog.FolderName);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_archive is not null)
        {
            var path = _archive.Path;
            try
            {
                OpenArchive(path);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"刷新归档失败: {ex.Message}");
            }
        }
        RefreshList();
    }

    // =====================================================================
    // Tools menu
    // =====================================================================

    /// <summary>The "工具" dropdown. Built in code so it stays in one place.</summary>
    private void OnToolsClick(object sender, RoutedEventArgs e)
    {
        if (ToolsButton.ContextMenu is null) BuildToolsMenu();
        var menu = ToolsButton.ContextMenu!;
        menu.PlacementTarget = ToolsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BuildToolsMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MakeToolItem("压缩包校验", OnVerifyToolClick));
        menu.Items.Add(MakeToolItem("批量重命名", OnRenameToolClick));

        ToolsButton.ContextMenu = menu;
    }

    private static MenuItem MakeToolItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header, Padding = new Thickness(10, 6, 18, 6) };
        item.Click += handler;
        return item;
    }

    private void OnVerifyToolClick(object sender, RoutedEventArgs e)
    {
        var initial = _archive?.Path
                      ?? (EntryGrid.SelectedItem as BrowserRow)?.FullPath;
        new VerifyWindow(initial) { Owner = this }.Show();
    }

    private void OnRenameToolClick(object sender, RoutedEventArgs e)
    {
        var initial = _archive is null && _folder.Length > 0 ? _folder : null;
        new RenameWindow(initial) { Owner = this }.ShowDialog();
    }

    private async void OnExtractToClick(object sender, RoutedEventArgs e)
    {
        if (_archive is null) return;

        var dialog = new OpenFolderDialog
        {
            Title = "解压到…",
            InitialDirectory = _settings.LastDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _settings.LastDirectory = dialog.FolderName;
            await RunExtractionAsync(testOnly: false, dialog.FolderName);
        }
    }

    private async void OnExtractHereClick(object sender, RoutedEventArgs e)
    {
        if (_archive is null) return;
        await RunExtractionAsync(testOnly: false, Path.GetDirectoryName(_archive.Path));
    }

    private async void OnQuickExtractClick(object sender, RoutedEventArgs e)
    {
        if (_archive is null) return;
        var folder = Path.GetDirectoryName(_archive.Path) ?? Environment.CurrentDirectory;
        var target = Path.Combine(folder, OutputNaming.FolderNameFor(_archive.Path));
        await RunExtractionAsync(testOnly: false, target);
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_archive is null) return;
        await RunExtractionAsync(testOnly: true, Path.GetDirectoryName(_archive.Path));
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        CancelButton.IsEnabled = false;
        ProgressDetail.Text = "正在取消…";
        _cts.Cancel();
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_archive is not null)
        {
            if (_entryPrefix.Length == 0) return;
            var trimmed = _entryPrefix.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            _entryPrefix = slash < 0 ? string.Empty : trimmed[..(slash + 1)];
            RefreshList();
            UpdateLocationText();
            return;
        }

        var parent = Directory.GetParent(_folder)?.FullName;
        if (parent is not null) OpenFolder(parent);
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as BrowserRow
                  ?? EntryGrid.SelectedItem as BrowserRow;
        if (row is null) return;

        if (_archive is not null)
        {
            if (!row.IsDirectory) return;
            _entryPrefix = row.EntryPath.TrimEnd('/') + "/";
            RefreshList();
            UpdateLocationText();
            return;
        }

        if (row.IsDirectory)
        {
            OpenFolder(row.FullPath);
        }
        else if (row.IsArchive)
        {
            try
            {
                OpenArchive(row.FullPath);
            }
            catch (Exception ex)
            {
                Report("打开失败", ex);
            }
        }
        else if (row.IsVolumeTail)
        {
            ShowMessage("这是分卷的后续部分", "请双击第一卷（例如 .7z.001 或 .part1.rar）。");
        }
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        var target = _archive?.Path
                     ?? (EntryGrid.SelectedItem as BrowserRow)?.FullPath
                     ?? _folder;

        if (string.IsNullOrEmpty(target)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Report("打开资源管理器失败", ex);
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (EntryGrid.SelectedItem is BrowserRow row)
        {
            var text = _archive is not null
                ? _archive.Path + " › " + row.EntryPath
                : row.FullPath;
            try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
            return;
        }

        var fallback = _archive?.Path ?? _folder;
        if (string.IsNullOrEmpty(fallback)) return;
        try { Clipboard.SetText(fallback); } catch { /* clipboard busy */ }
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCounts();

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        var member = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(member)) return;

        e.Handled = true;
        if (_sortMember == member) _sortAscending = !_sortAscending;
        else
        {
            _sortMember = member;
            _sortAscending = true;
        }

        foreach (var column in EntryGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = _sortAscending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        RefreshList();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        RefreshList();
    }

    private void OnDeleteSpentToggled(object sender, RoutedEventArgs e)
    {
        _settings.DeleteSpentVolumes = DeleteSpentCheck.IsChecked == true;
        if (_settings.DeleteSpentVolumes && _archive is { VolumeCount: 1 })
            StatusRight.Text = "单文件压缩包没有分卷可删，勾选无效";
    }

    private void OnKeepLastChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.KeepLastVolumes = Math.Max(0, KeepLastBox.SelectedIndex);
    }

    // =====================================================================
    // Shell integration
    // =====================================================================

    private void OnShellMenuToggled(object sender, RoutedEventArgs e)
    {
        if (_shellUiSync) return;

        try
        {
            if (ShellMenuCheck.IsChecked == true)
            {
                var exe = Environment.ProcessPath
                          ?? throw new InvalidOperationException("无法确定自身可执行文件路径。");
                ShellMenuRegistrar.Register(new ShellMenuOptions { ExecutablePath = exe });
                AppLog.Info($"已注册右键菜单 -> {exe}");
            }
            else
            {
                ShellMenuRegistrar.Unregister();
                AppLog.Info("已移除右键菜单");
            }
        }
        catch (Exception ex)
        {
            Report("右键菜单操作失败", ex);
        }

        SyncShellMenuUi();
    }

    private void SyncShellMenuUi()
    {
        _shellUiSync = true;
        try
        {
            var registered = ShellMenuRegistrar.IsRegistered();
            ShellMenuCheck.IsChecked = registered;

            var self = Environment.ProcessPath;
            var target = registered ? ShellMenuRegistrar.RegisteredTarget() : null;
            ShellMenuCheck.ToolTip = registered
                ? target is not null && self is not null &&
                  !string.Equals(target, self, StringComparison.OrdinalIgnoreCase)
                    ? $"当前指向 {target}；重新勾选可改为本程序（{self}）"
                    : "已安装（当前用户，无需管理员）"
                : "在当前用户注册表中安装 Explorer 右键菜单（无需管理员）";
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取右键菜单状态失败: {ex.Message}");
        }
        finally
        {
            _shellUiSync = false;
        }
    }

    // =====================================================================
    // Drag & drop, keyboard, lifetime
    // =====================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var path = paths[0];
        foreach (var candidate in paths)
        {
            if (File.Exists(candidate) &&
                (ArchiveScanner.IsKnownArchiveFile(candidate) || VolumePartRegex().IsMatch(candidate)))
            {
                path = candidate;
                break;
            }
        }

        Load(path);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                OnOpenClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F5:
                OnRefreshClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Back:
                OnUpClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Escape when _busy:
                OnCancelClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.Save();
        base.OnClosing(e);
    }

    // =====================================================================
    // Small helpers
    // =====================================================================

    private static string DescribeType(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return "文件";
        var bare = extension.TrimStart('.').ToUpperInvariant();
        return bare.Length == 0 ? "文件" : bare + " 文件";
    }

    private static string DescribeEntry(ArchiveEntry entry)
    {
        var bits = new List<string>(2);
        if (entry.IsSolid) bits.Add("固实");
        if (entry.IsEncrypted) bits.Add("加密");
        if (entry.Crc != 0) bits.Add($"CRC {entry.Crc:X8}");
        return string.Join(" · ", bits);
    }

    private void ShowEmptyHint(string text)
    {
        EmptyHint.Visibility = Visibility.Visible;
        EmptyHintText.Text = text;
    }

    private void ShowMessage(string title, string message) =>
        ResultWindow.Show(this, ResultKind.Info, title, message);

    private void Report(string title, Exception ex)
    {
        AppLog.Error(title, ex);
        ResultWindow.Show(this, ResultKind.Error, title, ex.Message, null, null, FailureAdvice.For(ex));
    }

    private void ShowIssues(string title, string summary, ExtractionResult result)
    {
        ResultWindow.Show(this, ResultKind.Warning, title, summary,
            result.Errors.Take(10).ToList(), result.FailureReason, result.FailureHint);
    }
}
