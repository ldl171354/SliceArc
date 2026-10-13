using System.Diagnostics;
using System.Windows;
using SliceArc.App.Services;
using SliceArc.Core.Compression;
using SliceArc.Core.Extraction;

namespace SliceArc.App.Views;

/// <summary>
/// The compact progress dialog the right-click verbs open: one line of context,
/// one bar, one cancel button. Nothing to configure, nothing to confirm.
///
/// Typed as <c>object</c> on the result side so the same window serves both the
/// extraction worker (returns <see cref="ExtractionResult"/>) and the
/// compression worker (returns <see cref="CompressionResult"/>). The progress
/// callback is <see cref="ExtractionProgress"/>; the compress side adapts its
/// own <see cref="CompressionProgress"/> to that shape via <see cref="ProgressAdapter"/>.
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly Func<Action<ExtractionProgress>, CancellationToken, object> _work;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _testOnly;
    private readonly bool _showFileBar;

    /// <summary>Result of the run (ExtractionResult or CompressionResult); null when it threw.</summary>
    public object? Result { get; private set; }

    /// <summary>Exception thrown by the run, if any.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Wall-clock time the run took; read after ShowDialog returns.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public ProgressWindow(
        string headline,
        string target,
        bool testOnly,
        bool showFileBar,
        Func<Action<ExtractionProgress>, CancellationToken, object> work)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        _work = work;
        _testOnly = testOnly;
        _showFileBar = showFileBar;
        Headline.Text = headline;
        TargetText.Text = target;
        Title = headline;
        FilePanel.Visibility = showFileBar ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Result = await Task.Run(() => _work(OnProgress, _cts.Token));
        }
        catch (Exception ex)
        {
            Failure = ex;
            AppLog.Error("后台任务失败", ex);
        }
        finally
        {
            Close();
        }
    }

    private void OnProgress(ExtractionProgress progress) =>
        Dispatcher.BeginInvoke(new Action(() => Apply(progress)));

    private void Apply(ExtractionProgress progress)
    {
        Bar.Value = progress.Fraction;
        Percent.Text = $"{progress.Fraction * 100:0}%";

        if (_showFileBar)
        {
            FileText.Text = progress.CurrentEntry ?? string.Empty;
            if (progress.FileFraction is { } fileFraction)
            {
                FileBar.Value = fileFraction;
                FilePercent.Text = $"{fileFraction * 100:0}%";
            }
            else
            {
                // Unknown- or zero-size member (a directory, say): no bar to fill.
                FileBar.Value = 0;
                FilePercent.Text = string.Empty;
            }
        }

        Detail.Text = ProgressText.Describe(progress, _clock.Elapsed, _testOnly, includeEntry: !_showFileBar);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cts.IsCancellationRequested) return;
        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在取消…";
        _cts.Cancel();
    }
}

/// <summary>
/// Adapter that turns a CompressionProgress callback into an ExtractionProgress
/// callback. Field-for-field equivalent shape; the only difference is that
/// compression has no "bytes freed" notion, so BytesFreed stays 0.
/// </summary>
public static class ProgressAdapter
{
    public static Action<CompressionProgress> ForCompression(Action<ExtractionProgress> sink)
    {
        return cp => sink(new ExtractionProgress(
            cp.ItemsDone,
            cp.ItemsTotal,
            cp.BytesDone,
            cp.BytesTotal,
            cp.VolumeIndex + 1,
            Math.Max(1, cp.VolumeCount),
            0,
            0,
            cp.CurrentEntry));
    }
}

/// <summary>One-line progress description shared by the dialog and the main window.</summary>
public static class ProgressText
{
    public static string Describe(ExtractionProgress progress, TimeSpan elapsed, bool testOnly, bool includeEntry = true)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var speed = progress.BytesDone / seconds;

        var parts = new List<string>(6);
        // Omitted when the caller already shows the member name on its own line.
        if (includeEntry && !string.IsNullOrEmpty(progress.CurrentEntry)) parts.Add(progress.CurrentEntry);
        parts.Add($"{Format(progress.BytesDone)} / {Format(progress.BytesTotal)}");
        parts.Add($"{Format((long)speed)}/s");
        parts.Add(testOnly
            ? $"{progress.EntriesDone}/{progress.EntriesTotal} 项"
            : $"分卷 {progress.CurrentVolume}/{progress.VolumeCount}");

        // Estimated time remaining, from the byte-throughput average.
        if (progress.BytesTotal > 0 && progress.BytesDone > 0 && speed > 1)
        {
            var remaining = (progress.BytesTotal - progress.BytesDone) / speed;
            if (remaining > 0.5) parts.Add($"剩余约 {FormatElapsed(TimeSpan.FromSeconds(remaining))}");
        }
        parts.Add($"已用 {FormatElapsed(elapsed)}");

        if (progress.BytesFreed > 0)
            parts.Add($"已回收 {Format(progress.BytesFreed)}（{progress.VolumesDeleted} 卷）");

        return string.Join("  ·  ", parts);
    }

    /// <summary>mm:ss (h:mm:ss past an hour) for elapsed/ETA readouts.</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
