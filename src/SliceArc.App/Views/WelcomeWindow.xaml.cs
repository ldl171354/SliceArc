using System.Collections.ObjectModel;
using System.Windows;
using SliceArc.App.Models;
using SliceArc.App.Services;
using SliceArc.Core.Shell;

namespace SliceArc.App.Views;

/// <summary>
/// The first-run guide: what the app is good at, the two things worth knowing,
/// and - the part that actually matters - a Bandizip-style picker that binds the
/// chosen archive extensions to this app so double-clicking them opens it here.
///
/// Shown once (AppSettings.WelcomeShown); the same binding lives in
/// <see cref="AssociationsWindow"/> for re-running after another program steals
/// a default.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly ObservableCollection<AssociationRow> _rows = new();

    public WelcomeWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        // Never taller than the desktop's working area: on a short screen the
        // footer buttons must stay reachable (the body scrolls instead).
        var work = SystemParameters.WorkArea;
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, work.Height - 32));

        foreach (var type in FileAssociationRegistrar.SupportedTypes)
        {
            var row = new AssociationRow(type);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AssociationRow.IsChecked)) UpdateSelectionText();
            };
            _rows.Add(row);
        }

        TypeList.ItemsSource = _rows;
        UpdateSelectionText();
    }

    // =====================================================================
    // Selection helpers
    // =====================================================================

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNoneClick(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var row in _rows) row.IsChecked = value;
        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        var count = _rows.Count(r => r.IsChecked);
        SelectionText.Text = count == 0
            ? "未选择任何格式"
            : $"已选择 {count} / {_rows.Count} 种格式";
    }

    // =====================================================================
    // Actions
    // =====================================================================

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        SaveShown();
        Close();
    }

    private void OnBindClick(object sender, RoutedEventArgs e)
    {
        var selectedTypes = _rows.Where(r => r.IsChecked).Select(r => r.Type).ToList();
        if (selectedTypes.Count == 0)
        {
            ResultWindow.Show(this, ResultKind.Info, "尚未选择格式",
                "请至少勾选一种压缩包格式，或点「稍后再说」跳过。",
                null, null, "之后可在「设置 → 文件关联…」中随时绑定。");
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            ResultWindow.Show(this, ResultKind.Error, "无法绑定",
                "无法确定程序自身路径，请从安装目录重新启动后再试。",
                null, null, "也可在「设置 → 文件关联…」中重试。");
            return;
        }

        try
        {
            FileAssociationRegistrar.Register(exe, Brand.Name, Brand.Description, selectedTypes);
            var outcomes = FileAssociationRegistrar.Bind(selectedTypes.Select(t => t.Extension));

            var bound = outcomes.Count(o => o.Status == AssociationStatus.Bound);
            var pending = outcomes.Count - bound;

            var settings = AppSettings.Load();
            settings.WelcomeShown = true;
            settings.BoundExtensions = selectedTypes.Select(t => t.Extension).ToArray();
            settings.Save();

            if (pending == 0)
            {
                ResultWindow.Show(this, ResultKind.Success, "已绑定文件关联",
                    $"已把 {bound} 种压缩包格式的默认打开方式设为「{Brand.Name}」，现在双击即可直接打开。");
            }
            else
            {
                ResultWindow.Show(this, ResultKind.Info, "已提交绑定请求",
                    $"已注册 {selectedTypes.Count} 种格式，其中 {bound} 种已生效，" +
                    $"{pending} 种需要你在系统「默认应用」界面确认。",
                    null,
                    "Windows 不允许程序自动修改默认打开方式。",
                    "若未看到确认界面，或之后被其他软件改走，可在「设置 → 文件关联…」中重新绑定。");
            }

            Close();
        }
        catch (Exception ex)
        {
            ResultWindow.Show(this, ResultKind.Error, "绑定失败", ex.Message,
                null, null, FailureAdvice.For(ex));
        }
    }

    private void SaveShown()
    {
        try
        {
            var settings = AppSettings.Load();
            settings.WelcomeShown = true;
            settings.Save();
        }
        catch
        {
            // Settings persistence is best-effort; never block closing.
        }
    }
}
