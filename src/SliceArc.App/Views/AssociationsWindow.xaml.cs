using System.Collections.ObjectModel;
using System.Windows;
using SliceArc.App.Models;
using SliceArc.App.Services;
using SliceArc.Core.Shell;

namespace SliceArc.App.Views;

/// <summary>
/// Settings &gt; 文件关联: the same picker as the first-run guide, but with the
/// handler Windows currently uses shown per extension, so a default that another
/// program took over is visible and can be rebound in one click.
/// </summary>
public partial class AssociationsWindow : Window
{
    private readonly ObservableCollection<AssociationRow> _rows = new();

    public AssociationsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        foreach (var type in FileAssociationRegistrar.SupportedTypes) _rows.Add(new AssociationRow(type));
        TypeGrid.ItemsSource = _rows;
        RefreshStatus(selectBound: true);
    }

    // =====================================================================
    // Status
    // =====================================================================

    private void RefreshStatus(bool selectBound)
    {
        var ours = 0;
        foreach (var row in _rows)
        {
            var progId = FileAssociationRegistrar.QueryCurrentDefaultProgId(row.Extension);
            row.IsCurrent = IsOurs(progId);
            row.CurrentDefault = Describe(progId);
            if (row.IsCurrent) ours++;
        }

        if (selectBound)
        {
            // Default to the set the user asked for last time (so "重新绑定" after
            // another program stole a default simply re-requests the same set).
            // With no stored choice, fall back to what is currently ours, or to
            // everything so a single click binds the lot.
            var preferred = AppSettings.Load().BoundExtensions;
            foreach (var row in _rows)
            {
                row.IsChecked = preferred is { Length: > 0 }
                    ? preferred.Contains(row.Extension, StringComparer.OrdinalIgnoreCase)
                    : ours > 0 ? row.IsCurrent : true;
            }
        }

        StatusText.Text = ours == 0 ? "尚未绑定任何格式" : $"已绑定 {ours} 种格式";
    }

    private static bool IsOurs(string? progId) =>
        progId is not null && progId.StartsWith(FileAssociationRegistrar.AppRegName, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId)) return "未指定";
        if (IsOurs(progId)) return Brand.Name;

        var lower = progId.ToLowerInvariant();
        if (lower.Contains("7-zip")) return "7-Zip";
        if (lower.Contains("winrar")) return "WinRAR";
        if (lower.Contains("bandizip")) return "Bandizip";
        if (lower.Contains("peazip")) return "PeaZip";
        if (lower.Contains("nanazip")) return "NanaZip";
        if (lower.Contains("compressedfolder") || lower.Contains("cabfolder")) return "Windows 资源管理器";
        return progId;
    }

    // =====================================================================
    // Actions
    // =====================================================================

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNoneClick(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var row in _rows) row.IsChecked = value;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshStatus(selectBound: false);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnBindClick(object sender, RoutedEventArgs e)
    {
        var selectedTypes = _rows.Where(r => r.IsChecked).Select(r => r.Type).ToList();
        if (selectedTypes.Count == 0)
        {
            ResultWindow.Show(this, ResultKind.Info, "尚未选择格式",
                "请至少勾选一种压缩包格式。", null, null, null);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            ResultWindow.Show(this, ResultKind.Error, "无法绑定",
                "无法确定程序自身路径，请从安装目录重新启动后再试。", null, null, null);
            return;
        }

        try
        {
            FileAssociationRegistrar.Register(exe, Brand.Name, Brand.Description, selectedTypes);
            var outcomes = FileAssociationRegistrar.Bind(selectedTypes.Select(t => t.Extension));

            var bound = outcomes.Count(o => o.Status == AssociationStatus.Bound);
            var pending = outcomes.Count - bound;

            var settings = AppSettings.Load();
            settings.BoundExtensions = selectedTypes.Select(t => t.Extension).ToArray();
            settings.Save();

            RefreshStatus(selectBound: false);

            if (pending == 0)
            {
                StatusText.Text = $"已绑定 {bound} 种格式";
                ResultWindow.Show(this, ResultKind.Success, "已绑定文件关联",
                    $"已把 {bound} 种压缩包格式的默认打开方式设为「{Brand.Name}」。");
            }
            else
            {
                StatusText.Text = $"已生效 {bound} 种，{pending} 种待系统确认";
                ResultWindow.Show(this, ResultKind.Info, "已提交绑定请求",
                    $"已注册 {selectedTypes.Count} 种格式，其中 {bound} 种已生效，{pending} 种需要你在系统「默认应用」界面确认。",
                    null,
                    "Windows 不允许程序自动修改默认打开方式。",
                    "在系统「默认应用」界面确认后，回到这里点「刷新当前状态」即可看到结果。");
            }
        }
        catch (Exception ex)
        {
            ResultWindow.Show(this, ResultKind.Error, "绑定失败", ex.Message,
                null, null, FailureAdvice.For(ex));
        }
    }
}
