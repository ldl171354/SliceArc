using System.Reflection;
using System.Windows;

namespace SliceArc.App.Views;

/// <summary>About box: product name, version (from the assembly), credit.</summary>
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
}
