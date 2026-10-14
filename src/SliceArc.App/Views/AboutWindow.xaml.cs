using System.Diagnostics;
using System.Reflection;
using System.Windows;
using SliceArc.App.Services;

namespace SliceArc.App.Views;

/// <summary>About box: product name, version (from the assembly), credit, update check.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null
            ? "版本未知"
            : $"版本 {version.Major}.{version.Minor}.{version.Build}";
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Manual check: shows the result right here instead of only a banner.</summary>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateResultText.Text = "正在检查…";
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var info = await UpdateChecker.CheckAsync();
            UpdateResultText.Text = info is null
                ? $"已是最新版本（{UpdateChecker.CurrentVersion()}），或检查源暂时不可达。"
                : $"发现新版本 {info.LatestVersion}（当前 {info.CurrentVersion}）—— 点击「前往下载」去获取。";
            GoButton.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnGoClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(UpdateChecker.DownloadUrl) { UseShellExecute = true }); }
        catch { /* the about box has nothing better to do than ignore */ }
    }
}
