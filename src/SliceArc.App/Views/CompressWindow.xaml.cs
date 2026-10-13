using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SliceArc.App.Services;
using SliceArc.Core.Compression;

namespace SliceArc.App.Views;

/// <summary>
/// Configuration dialog for the compress action. Collects sources, output path,
/// format, level, volume, password and switches, then builds a CompressionOptions
/// the caller runs through ProgressWindow.
/// </summary>
public partial class CompressWindow : Window
{
    private readonly List<string> _sources = new();

    /// <summary>The resolved options the caller reads after DialogResult == true.</summary>
    public CompressionOptions? Options { get; private set; }

    /// <param name="initialSources">Pre-fill from the main window's selection or folder.</param>
    /// <param name="presetFormat">Right-click verbs pre-select the format they advertise.</param>
    /// <param name="presetLevel">Pre-selected level (0-9); null keeps the UI default.</param>
    /// <param name="presetVolumeSize">Pre-filled split volume size in bytes; 0 = single file.</param>
    public CompressWindow(
        IReadOnlyList<string>? initialSources = null,
        CompressionFormat? presetFormat = null,
        int? presetLevel = null,
        long presetVolumeSize = 0)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        // Launched from the Explorer menu there is no owner window.
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;

        FormatBox.SelectedIndex = 0;
        if (presetFormat is { } fmt)
        {
            foreach (ComboBoxItem item in FormatBox.Items)
            {
                if (string.Equals(item.Tag as string, fmt.ToString(), StringComparison.Ordinal))
                {
                    FormatBox.SelectedItem = item;
                    break;
                }
            }
        }

        if (presetLevel is { } lvl) LevelBox.SelectedIndex = IndexFromLevel(lvl);

        if (presetVolumeSize > 0) VolumeBox.Text = FormatVolumeSize(presetVolumeSize);

        if (initialSources is { Count: > 0 })
        {
            _sources.AddRange(initialSources);
            SourcesBox.Text = string.Join(Environment.NewLine, _sources);
        }

        UpdateOutputDefault();
        Validate();
    }

    private static int IndexFromLevel(int level) => level switch
    {
        <= 0 => 0,
        1 => 1,
        <= 3 => 2,
        <= 5 => 3,
        <= 7 => 4,
        _ => 5,
    };

    /// <summary>52428800 -> "50MB" so the box reads the way Bandizip's does.</summary>
    private static string FormatVolumeSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var number = value % 1 == 0 ? ((long)value).ToString() : value.ToString("0.#");
        return number + units[unit];
    }

    // -----------------------------------------------------------------
    // Source management
    // -----------------------------------------------------------------

    private void OnSourcesChanged(object sender, TextChangedEventArgs e)
    {
        _sources.Clear();
        foreach (var line in SourcesBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().Trim('"');
            if (trimmed.Length > 0) _sources.Add(trimmed);
        }
        UpdateOutputDefault();
        Validate();
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加文件",
            Multiselect = true,
            InitialDirectory = AppSettings.Load().LastDirectory ?? string.Empty,
        };

        if (dialog.ShowDialog(this) != true) return;

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sources) existing.Add(s);
        foreach (var f in dialog.FileNames)
        {
            if (existing.Add(f)) _sources.Add(f);
        }

        SourcesBox.Text = string.Join(Environment.NewLine, _sources);
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "添加文件夹",
            InitialDirectory = AppSettings.Load().LastDirectory ?? string.Empty,
        };

        if (dialog.ShowDialog(this) != true) return;

        if (!_sources.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            _sources.Add(dialog.FolderName);

        SourcesBox.Text = string.Join(Environment.NewLine, _sources);
    }

    // -----------------------------------------------------------------
    // Output path
    // -----------------------------------------------------------------

    private void OnOutputChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        var ext = CurrentFormat().Extension();
        var dialog = new SaveFileDialog
        {
            Title = "压缩包保存到",
            Filter = $"{ext}|*.{ext}|所有文件|*.*",
            FileName = Path.GetFileName(OutputBox.Text.Trim()),
            InitialDirectory = Path.GetDirectoryName(OutputBox.Text.Trim()) ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
            OutputBox.Text = dialog.FileName;
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutputBox.Text.Trim().Length == 0)
        {
            UpdateOutputDefault();
            return;
        }
        // Keep the chosen path, swap only the extension for the new format.
        try { OutputBox.Text = Path.ChangeExtension(OutputBox.Text.Trim(), CurrentFormat().Extension()); }
        catch { /* leave the user's text alone */ }
    }

    private void UpdateOutputDefault()
    {
        if (_sources.Count == 0) return;
        if (OutputBox.Text.Trim().Length > 0) return;

        var first = _sources[0];
        // A folder source's archive goes NEXT TO it, not inside it.
        var dir = Directory.Exists(first)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(first)) ?? Environment.CurrentDirectory
            : Path.GetDirectoryName(first) ?? Environment.CurrentDirectory;
        var name = Directory.Exists(first)
            ? new DirectoryInfo(first).Name
            : Path.GetFileNameWithoutExtension(first);
        if (string.IsNullOrWhiteSpace(name)) name = "archive";
        var ext = CurrentFormat().Extension();
        OutputBox.Text = Path.Combine(dir, $"{name}.{ext}");
    }

    // -----------------------------------------------------------------
    // Validation + OK
    // -----------------------------------------------------------------

    private void Validate()
    {
        OkButton.IsEnabled = _sources.Count > 0 && OutputBox.Text.Trim().Length > 0;
    }

    private CompressionFormat CurrentFormat()
    {
        var tag = (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "SevenZip";
        return Enum.TryParse<CompressionFormat>(tag, out var f) ? f : CompressionFormat.SevenZip;
    }

    private static int LevelFromIndex(int index) => index switch
    {
        0 => 0, // store
        1 => 1, // fastest
        2 => 3, // fast
        3 => 5, // normal
        4 => 7, // max
        5 => 9, // ultra
        _ => 5,
    };

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var sources = _sources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFullPath(s.Trim('"')))
            .ToList();

        var missing = sources.FirstOrDefault(s => !File.Exists(s) && !Directory.Exists(s));
        if (missing is not null)
        {
            ResultWindow.Show(this, ResultKind.Warning, "无法开始压缩", $"找不到源：{missing}");
            return;
        }

        var outputPath = Path.GetFullPath(OutputBox.Text.Trim());
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Options = new CompressionOptions
        {
            Sources = sources,
            OutputPath = outputPath,
            Format = CurrentFormat(),
            Level = LevelFromIndex(LevelBox.SelectedIndex),
            Method = string.IsNullOrWhiteSpace(MethodBox.Text) ? null : MethodBox.Text.Trim(),
            VolumeSize = ParseVolumeSize(VolumeBox.Text),
            Password = string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password,
            EncryptHeaders = EncryptHeadersCheck.IsChecked == true,
            Solid = SolidCheck.IsChecked == true,
        };

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // -----------------------------------------------------------------
    // "50MB" / "1GB" / "1048576" → bytes
    // -----------------------------------------------------------------

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(KB|MB|GB|TB)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    private static long ParseVolumeSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var match = SizeRegex().Match(text);
        if (!match.Success) return 0;

        if (!double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value))
            return 0;

        var unit = match.Groups[2].Value.ToUpperInvariant();
        var multiplier = unit switch
        {
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            "" => 1,
            _ => 1,
        };

        return (long)(value * multiplier);
    }
}
