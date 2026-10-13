using System.Collections.Generic;
using System.Windows;

namespace SliceArc.App.Views;

public enum ResultKind { Success, Info, Warning, Error }

/// <summary>
/// The themed replacement for every completion / failure MessageBox. A flat
/// state badge, a title, one summary line and (optionally) an error list -
/// everything follows the active palette, unlike the native grey MessageBox.
/// </summary>
public partial class ResultWindow : Window
{
    public ResultWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
    }

    /// <summary>
    /// One-call helper: centres on the owner when there is one, otherwise on
    /// the screen (the headless right-click path has no owner window).
    /// </summary>
    /// <param name="reason">One-line cause, shown in an accented block. Null to omit.</param>
    /// <param name="hint">What the user can do about it. Null to omit.</param>
    public static void Show(Window? owner, ResultKind kind, string title, string summary,
        IReadOnlyList<string>? errors = null, string? reason = null, string? hint = null)
    {
        var window = new ResultWindow
        {
            WindowStartupLocation = owner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        if (owner is not null) window.Owner = owner;

        window.Configure(kind, title, summary, errors, reason, hint);
        window.ShowDialog();
    }

    private void Configure(ResultKind kind, string title, string summary,
        IReadOnlyList<string>? errors, string? reason, string? hint)
    {
        Title = $"超速解压缩 — {title}";
        TitleText.Text = title;
        SummaryText.Text = summary;

        if (kind == ResultKind.Warning || kind == ResultKind.Error)
        {
            BadgeGlyph.Text = kind == ResultKind.Error ? "\xE783" : "\xE7BA"; // error / warning
            Badge.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "B.Danger");
            Badge.Opacity = 0.14;
            BadgeGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "B.Danger");
        }
        else
        {
            // Success and Info both stay on the palette accent: one accent colour
            // is the app's whole visual language.
            BadgeGlyph.Text = kind == ResultKind.Success ? "\xE73E" : "\xE946"; // check / info
            Badge.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "B.AccentSoft");
            BadgeGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "B.Accent");
        }

        // The cause and the suggested fix are the headline; the per-file list
        // below is detail for whoever wants to dig.
        var hasReason = !string.IsNullOrWhiteSpace(reason);
        var hasHint = !string.IsNullOrWhiteSpace(hint);
        if (hasReason || hasHint)
        {
            if (hasReason) ReasonText.Text = "原因：" + reason;
            else ReasonText.Visibility = Visibility.Collapsed;
            if (hasHint) HintText.Text = "建议：" + hint;
            else HintText.Visibility = Visibility.Collapsed;
            AdviceBox.Visibility = Visibility.Visible;
            MinWidth = 480;
        }

        if (errors is { Count: > 0 })
        {
            var shown = new List<string>(errors.Count);
            foreach (var err in errors) shown.Add("• " + err);
            ErrorList.ItemsSource = shown;
            ErrorBox.Visibility = Visibility.Visible;
            MinWidth = 480;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
