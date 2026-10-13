using System.Windows;

namespace SliceArc.App.Views;

/// <summary>
/// Asks for the password of an encrypted archive. Shown from the UI thread
/// before (or between) extraction attempts, so the engine itself never has to
/// know how a password is obtained - it just reports that it needs one.
/// </summary>
public partial class PasswordWindow : Window
{
    private PasswordWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>
    /// Prompt for a password. Returns null when the user cancels.
    /// </summary>
    /// <param name="owner">Window to centre on, or null for the headless path.</param>
    /// <param name="archiveName">Shown in the prompt so batch runs stay clear.</param>
    /// <param name="retry">True when a previous password was rejected.</param>
    public static string? Prompt(Window? owner, string archiveName, bool retry = false)
    {
        var window = new PasswordWindow
        {
            WindowStartupLocation = owner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
        };
        if (owner is not null) window.Owner = owner;

        window.SummaryText.Text = retry
            ? $"口令不正确。「{archiveName}」需要正确的口令才能解压。"
            : $"「{archiveName}」的内容已加密，请输入口令。";
        if (retry)
        {
            window.ErrorText.Text = "上一次输入的口令被拒绝，请重新输入。";
            window.ErrorText.Visibility = Visibility.Visible;
        }

        return window.ShowDialog() == true ? window.PasswordInput.Password : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordInput.Password))
        {
            ErrorText.Text = "口令不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
