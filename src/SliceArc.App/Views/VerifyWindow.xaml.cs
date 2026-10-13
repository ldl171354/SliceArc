using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SliceArc.App.Models;
using SliceArc.App.Services;
using SliceArc.Core.Extraction;

namespace SliceArc.App.Views;

/// <summary>
/// The archive-check tool: put any number of archives (or folders of archives)
/// in, run a full decode-and-CRC pass over each one, and get a per-file verdict.
/// Nothing is written to disk - it is the same verification path the "测试"
/// button uses, just aimed at a whole batch and with the results laid out.
/// </summary>
public partial class VerifyWindow : Window
{
    private readonly ObservableCollection<VerifyRow> _rows = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _busy;

    // Live member results arrive on the worker thread; they are batched here and
    // flushed to the grid on the UI thread, so a 50 000-member archive does not
    // post 50 000 dispatcher work items.
    private readonly object _gate = new();
    private VerifyRow? _activeRow;
    private int _pendingPassed;
    private int _pendingFailed;
    private readonly List<VerifyIssue> _pendingIssues = new();
    private DateTime _lastFlush = DateTime.MinValue;
    private bool _flushQueued;
    private DateTime _lastProgressFlush = DateTime.MinValue;

    public VerifyWindow(string? initialPath = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        ArchiveGrid.ItemsSource = _rows;

        if (!string.IsNullOrWhiteSpace(initialPath))
            AddPaths(new[] { initialPath! });
    }

    // =====================================================================
    // Queue management
    // =====================================================================

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加要校验的压缩包",
            Multiselect = true,
            Filter = "压缩包|*.7z;*.zip;*.rar;*.tar;*.gz;*.bz2;*.xz;*.zst;*.cab;*.iso;*.lzh;*.001;*.z01;*.r00;*.part1.rar|所有文件|*.*",
            InitialDirectory = AppSettings.Load().LastDirectory ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "添加文件夹",
            InitialDirectory = AppSettings.Load().LastDirectory ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true) AddPaths(new[] { dialog.FolderName });
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _rows.Clear();
        _queued.Clear();
        Bar.Value = 0;
        DetailSummary.Text = string.Empty;
        StatusText.Text = "已清空，把压缩包拖进来开始吧";
    }

    /// <summary>Add files (as given) and folders (expanded to the archives inside).</summary>
    private void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string full;
            try { full = Path.GetFullPath(raw); }
            catch { continue; }

            if (Directory.Exists(full))
            {
                foreach (var candidate in ArchiveScanner.Scan(full))
                    if (AddOne(candidate.FirstVolumePath)) added++;
            }
            else if (File.Exists(full))
            {
                if (AddOne(full)) added++;
            }
        }

        UpdateIdleStatus(added);
    }

    private bool AddOne(string path)
    {
        if (!_queued.Add(path)) return false;
        _rows.Add(new VerifyRow(path));
        return true;
    }

    private void UpdateIdleStatus(int added)
    {
        if (_busy) return;
        StatusText.Text = added > 0
            ? $"已添加 {added} 个压缩包，共 {_rows.Count} 个待校验"
            : $"共 {_rows.Count} 个待校验";
    }

    // =====================================================================
    // Drag & drop
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
        AddPaths(paths);
    }

    // =====================================================================
    // The check run
    // =====================================================================

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var todo = _rows.Where(r => r.State != VerifyState.Passed).ToList();
        if (todo.Count == 0)
        {
            StatusText.Text = _rows.Count == 0 ? "还没有要校验的压缩包" : "全部已校验通过，无需重跑";
            return;
        }

        var password = PasswordInput.Password;
        if (password.Length == 0) password = null;

        _busy = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ClearButton.IsEnabled = false;

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        var clock = Stopwatch.StartNew();
        var passed = 0;
        var corrupt = 0;
        var locked = 0;

        foreach (var row in todo)
        {
            if (token.IsCancellationRequested) break;

            _activeRow = row;
            lock (_gate) { _pendingPassed = _pendingFailed = 0; _pendingIssues.Clear(); }
            row.State = VerifyState.Running;
            row.Status = "校验中…";
            row.Passed = 0;
            row.Failed = 0;
            row.Issues.Clear();
            ArchiveGrid.SelectedItem = row;
            Bar.Value = 0;

            var captured = row;
            ArchiveVerifyReport report;
            try
            {
                report = await Task.Run(() => ArchiveVerifier.Verify(
                    captured.Path,
                    password,
                    onMember: (entry, ok) => OnMember(entry, ok),
                    progress: OnProgress,
                    cancellationToken: token));
            }
            catch (Exception ex)
            {
                AppLog.Error($"校验 {captured.Path} 失败", ex);
                report = new ArchiveVerifyReport
                {
                    Path = captured.Path,
                    DisplayName = captured.Name,
                    Unverifiable = true,
                    Reason = ex.Message,
                    Hint = FailureAdvice.For(ex),
                };
            }

            ApplyReport(row, report);
            if (report.Ok) passed++;
            else if (report.NeedsPassword) locked++;
            else if (!report.Cancelled) corrupt++;

            if (report.Cancelled) break;
        }

        Flush(); // drain any final member results before settling the totals
        _activeRow = null;

        _busy = false;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        ClearButton.IsEnabled = true;
        _cts = null;
        cts.Dispose();

        Bar.Value = token.IsCancellationRequested ? 0 : 1;

        var parts = new List<string> { $"校验完成，用时 {ProgressText.FormatElapsed(clock.Elapsed)}" };
        parts.Add($"通过 {passed}");
        if (corrupt > 0) parts.Add($"损坏 {corrupt}");
        if (locked > 0) parts.Add($"需要口令 {locked}");
        parts.Add($"共 {todo.Count} 个");
        if (token.IsCancellationRequested) parts.Add("已取消");
        StatusText.Text = string.Join(" · ", parts);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        StopButton.IsEnabled = false;
        StatusText.Text = "正在停止…";
        _cts.Cancel();
    }

    private void ApplyReport(VerifyRow row, ArchiveVerifyReport report)
    {
        row.Report = report;
        row.Format = string.IsNullOrWhiteSpace(report.FormatName) || report.FormatName == "unknown"
            ? "-"
            : report.FormatName;
        row.EntryCount = report.EntryCount;
        row.Passed = report.TestedCount;
        row.Failed = report.FailedCount;

        row.Issues.Clear();
        foreach (var issue in report.Issues) row.Issues.Add(issue);

        if (report.Cancelled)
        {
            row.State = VerifyState.Cancelled;
            row.Status = "已取消";
        }
        else if (report.NeedsPassword)
        {
            row.State = VerifyState.NeedsPassword;
            row.Status = "需要口令";
        }
        else if (report.Unverifiable)
        {
            row.State = VerifyState.Unverifiable;
            row.Status = "无法校验";
        }
        else if (report.Ok)
        {
            row.State = VerifyState.Passed;
            row.Status = "校验通过";
        }
        else
        {
            row.State = VerifyState.Corrupt;
            row.Status = "发现损坏";
        }

        if (ReferenceEquals(ArchiveGrid.SelectedItem, row)) UpdateDetailSummary(row);
    }

    // =====================================================================
    // Worker-thread callbacks (batched onto the UI thread)
    // =====================================================================

    private void OnMember(ArchiveEntry entry, bool ok)
    {
        lock (_gate)
        {
            if (ok) _pendingPassed++;
            else
            {
                _pendingFailed++;
                _pendingIssues.Add(new VerifyIssue(entry.NormalizedPath, "CRC 校验失败或数据损坏", entry.Size));
            }

            if (_flushQueued || (DateTime.UtcNow - _lastFlush).TotalMilliseconds < 90) return;
            _flushQueued = true;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Flush));
    }

    private void Flush()
    {
        lock (_gate)
        {
            var row = _activeRow;
            if (row is not null)
            {
                row.Passed += _pendingPassed;
                row.Failed += _pendingFailed;
                foreach (var issue in _pendingIssues) row.Issues.Add(issue);
            }

            _pendingPassed = 0;
            _pendingFailed = 0;
            _pendingIssues.Clear();
            _lastFlush = DateTime.UtcNow;
            _flushQueued = false;
        }
    }

    private void OnProgress(ExtractionProgress progress)
    {
        if ((DateTime.UtcNow - _lastProgressFlush).TotalMilliseconds < 90) return;
        _lastProgressFlush = DateTime.UtcNow;
        var fraction = progress.Fraction;
        Dispatcher.BeginInvoke(new Action(() => Bar.Value = fraction));
    }

    // =====================================================================
    // Detail panel
    // =====================================================================

    private void OnGridSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ArchiveGrid.SelectedItem is VerifyRow row) UpdateDetailSummary(row);
        else DetailSummary.Text = string.Empty;
    }

    private void UpdateDetailSummary(VerifyRow row)
    {
        if (row.Issues.Count == 0)
        {
            DetailSummary.Text = row.State switch
            {
                VerifyState.Passed => $"{row.Name} · 全部 {row.Passed} 项通过",
                VerifyState.Running => $"{row.Name} · 已验证 {row.Passed} 项",
                _ => row.Report?.Reason ?? string.Empty,
            };
            return;
        }

        DetailSummary.Text = $"{row.Name} · 失败 {row.Issues.Count} 项"
                             + (row.Report?.Reason is { Length: > 0 } reason ? " · " + reason : string.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
