using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SliceArc.App.Rename;
using SliceArc.App.Services;

namespace SliceArc.App.Views;

/// <summary>
/// 批量重命名窗口。移植自 Python/tkinter 版「图吧工具箱·批量重命名」：
/// 三种模式（文件 / 文件夹 / 扩展名），可叠加的规则链，实时预览，
/// 真实改名 + 崩溃安全的撤销日志。
/// </summary>
public partial class RenameWindow : Window
{
    private readonly List<string> _entries = new();          // 待处理项（全路径）
    private readonly List<RenameRule> _rules = new();          // 当前规则链
    private readonly ObservableCollection<RenameRow> _rows = new();
    private List<CompoundPattern> _customPatterns = new();

    private string? _directory;
    private int _ruleCounter;
    private bool _ready;

    /// <param name="initialDirectory">
    /// 主窗口传入的当前浏览目录；非空且存在时自动加载该目录内容。
    /// </param>
    public RenameWindow(string? initialDirectory = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowTheme.Apply(this);

        PreviewGrid.ItemsSource = _rows;
        _customPatterns = CustomPatternStore.Load();

        RuleTypeBox.SelectedIndex = 0;
        _ready = true;

        ModeBox.SelectedIndex = 0; // 触发一次 OnModeChanged → 刷新

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            LoadDirectory(initialDirectory);

        RenderRules();
        UpdateUndoButton();
        RefreshPreview();
    }

    // =====================================================================
    // 顶部：来源与模式
    // =====================================================================

    private void OnChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择文件夹",
            InitialDirectory = _directory ?? AppSettings.Load().LastDirectory ?? string.Empty,
        };
        if (dialog.ShowDialog(this) != true) return;
        LoadDirectory(dialog.FolderName);
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加文件",
            Multiselect = true,
            InitialDirectory = _directory ?? AppSettings.Load().LastDirectory ?? string.Empty,
        };
        if (dialog.ShowDialog(this) != true) return;

        var existing = new HashSet<string>(_entries, StringComparer.OrdinalIgnoreCase);
        foreach (var f in dialog.FileNames)
        {
            if (existing.Add(f)) _entries.Add(f);
        }
        if (_directory is null && _entries.Count > 0) _directory = Path.GetDirectoryName(_entries[0]);
        PathText.Text = _directory ?? $"{_entries.Count} 个文件";
        RefreshPreview();
    }

    private void LoadDirectory(string directory)
    {
        _directory = directory;
        _entries.Clear();
        try
        {
            _entries.AddRange(Directory.GetFileSystemEntries(directory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取文件夹失败: {ex.Message}");
        }
        PathText.Text = directory;
        if (_ready) RefreshPreview();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var ext = CurrentMode() == "ext";
        RulesCard.Visibility = ext ? Visibility.Collapsed : Visibility.Visible;
        ExtCard.Visibility = ext ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }

    private string CurrentMode() =>
        (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "file";

    // =====================================================================
    // 扩展名模式的子面板切换
    // =====================================================================

    private void OnExtSubChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var compound = CompoundRadio.IsChecked == true;
        SimplePane.Visibility = compound ? Visibility.Collapsed : Visibility.Visible;
        CompoundPane.Visibility = compound ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }

    private void OnCompoundOpChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var op = CurrentCompoundOp();
        CompoundReplacePane.Visibility = op is "replace" or "both" ? Visibility.Visible : Visibility.Collapsed;
        CompoundRenumberPane.Visibility = op is "renumber" or "both" ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }

    private void OnExtTextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();

    private void OnExtCheckChanged(object sender, RoutedEventArgs e) => RefreshPreview();

    private string CurrentCompoundOp() =>
        CompoundOpRenumber.IsChecked == true ? "renumber"
        : CompoundOpBoth.IsChecked == true ? "both"
        : "replace";

    // =====================================================================
    // 规则列表
    // =====================================================================

    private void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        var tag = (RuleTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "replace";
        var type = tag switch
        {
            "regex" => RenameRuleType.Regex,
            "prefix" => RenameRuleType.Prefix,
            "suffix" => RenameRuleType.Suffix,
            "number" => RenameRuleType.Number,
            "case" => RenameRuleType.Case,
            "remove" => RenameRuleType.Remove,
            _ => RenameRuleType.Replace,
        };
        _ruleCounter++;
        _rules.Add(RenameRule.Create(type, _ruleCounter));
        RenderRules();
        RefreshPreview();
    }

    private void RenderRules()
    {
        RulesPanel.Children.Clear();
        RuleCountText.Text = $"{_rules.Count} 条规则";

        if (_rules.Count == 0)
        {
            RulesPanel.Children.Add(new TextBlock
            {
                Text = "暂无规则，添加后将实时生成预览",
                Foreground = (Brush)FindResource("B.TextFaint"),
                Margin = new Thickness(2, 10, 0, 10),
            });
            return;
        }

        for (var i = 0; i < _rules.Count; i++)
            RulesPanel.Children.Add(BuildRuleEditor(_rules[i], i));
    }

    private Border BuildRuleEditor(RenameRule rule, int index)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("B.SurfaceAlt"),
            BorderBrush = (Brush)FindResource("B.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var stack = new StackPanel();
        card.Child = stack;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = $"{index + 1}. {rule.TypeName}",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        var del = new Button
        {
            Content = "删除",
            Style = (Style)FindResource("OutlineButton"),
            Height = 24,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        del.Click += (_, _) => { _rules.Remove(rule); RenderRules(); RefreshPreview(); };
        Grid.SetColumn(del, 1);
        header.Children.Add(del);
        stack.Children.Add(header);

        var body = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(body);

        switch (rule.Type)
        {
            case RenameRuleType.Replace:
                body.Children.Add(Text("查找", rule.Find, v => { rule.Find = v; RefreshPreview(); }));
                body.Children.Add(Text("替换为", rule.To, v => { rule.To = v; RefreshPreview(); }));
                body.Children.Add(Check("区分大小写", rule.CaseSensitive, v => { rule.CaseSensitive = v; RefreshPreview(); }));
                break;

            case RenameRuleType.Regex:
                body.Children.Add(Text("正则表达式", rule.Pattern, v => { rule.Pattern = v; RefreshPreview(); }, 220));
                body.Children.Add(Text("替换为", rule.To, v => { rule.To = v; RefreshPreview(); }));
                break;

            case RenameRuleType.Prefix:
                body.Children.Add(Text("前缀文本", rule.Text, v => { rule.Text = v; RefreshPreview(); }));
                break;

            case RenameRuleType.Suffix:
                body.Children.Add(Text("后缀文本", rule.Text, v => { rule.Text = v; RefreshPreview(); }));
                break;

            case RenameRuleType.Number:
                body.Children.Add(Num("起始", rule.Start, v => { rule.Start = v; RefreshPreview(); }));
                body.Children.Add(Num("步长", rule.Step, v => { rule.Step = v; RefreshPreview(); }));
                body.Children.Add(Num("位数", rule.Width, v => { rule.Width = v; RefreshPreview(); }));
                body.Children.Add(Text("分隔符", rule.Sep, v => { rule.Sep = v; RefreshPreview(); }, 64));
                body.Children.Add(Choice("位置", new[]
                {
                    ("后缀", "suffix"),
                    ("前缀", "prefix"),
                }, rule.Position, v => { rule.Position = v; RefreshPreview(); }));
                break;

            case RenameRuleType.Case:
                body.Children.Add(Choice("模式", new[]
                {
                    ("全部大写", "Upper"),
                    ("全部小写", "Lower"),
                    ("首字母大写", "Title"),
                    ("驼峰式", "Camel"),
                }, rule.Mode.ToString(), v => { rule.Mode = Enum.Parse<CaseMode>(v, true); RefreshPreview(); }));
                break;

            case RenameRuleType.Remove:
                body.Children.Add(Text("删除文本", rule.Text, v => { rule.Text = v; RefreshPreview(); }));
                body.Children.Add(Check("区分大小写", rule.CaseSensitive, v => { rule.CaseSensitive = v; RefreshPreview(); }));
                break;
        }

        return card;
    }

    private FrameworkElement Text(string label, string value, Action<string> onChange, double width = 140)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption") });
        var tb = new TextBox
        {
            Text = value,
            Width = width,
            FontFamily = new FontFamily("Consolas"),
            Style = (Style)FindResource("MinimalTextBox"),
        };
        tb.TextChanged += (_, _) => onChange(tb.Text);
        sp.Children.Add(tb);
        return sp;
    }

    private FrameworkElement Num(string label, int value, Action<int> onChange)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption") });
        var tb = new TextBox
        {
            Text = value.ToString(),
            Width = 64,
            FontFamily = new FontFamily("Consolas"),
            Style = (Style)FindResource("MinimalTextBox"),
        };
        tb.TextChanged += (_, _) => { if (int.TryParse(tb.Text, out var n)) onChange(n); };
        sp.Children.Add(tb);
        return sp;
    }

    private FrameworkElement Check(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Style = (Style)FindResource("MinimalCheckBox"),
            Margin = new Thickness(0, 18, 14, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cb.Click += (_, _) => onChange(cb.IsChecked == true);
        return cb;
    }

    private FrameworkElement Choice(string label, IReadOnlyList<(string Label, string Value)> options,
        string current, Action<string> onChange)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caption") });
        var cb = new ComboBox { Width = 120, Style = (Style)FindResource("MinimalComboBox") };
        foreach (var (lab, val) in options) cb.Items.Add(new ComboBoxItem { Content = lab, Tag = val });
        foreach (ComboBoxItem item in cb.Items)
        {
            if ((string)item.Tag == current) { cb.SelectedItem = item; break; }
        }
        if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem it && it.Tag is string v) onChange(v);
        };
        sp.Children.Add(cb);
        return sp;
    }

    // =====================================================================
    // 预览生成
    // =====================================================================

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        // 丢弃磁盘上已不存在的来源，避免旧路径堆积。
        _entries.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!_ready) return;

        var mode = CurrentMode();

        // 按模式筛选：文件夹模式取目录，文件 / 扩展名模式取文件。
        var items = new List<string>();
        foreach (var path in _entries)
        {
            var isDir = Directory.Exists(path);
            if (mode == "folder")
            {
                if (isDir) items.Add(path);
            }
            else if (!isDir && File.Exists(path))
            {
                items.Add(path);
            }
        }

        var rows = new List<RenameRow>();
        var patterns = RenameEngine.GetActivePatterns(_customPatterns);
        var compoundCount = 0;
        var matchedName = "";

        for (var i = 0; i < items.Count; i++)
        {
            var full = items[i];
            var original = Path.GetFileName(full);
            var next = original;
            var status = "skip";

            if (mode is "file" or "folder")
            {
                next = RenameEngine.ApplyRulesToName(original, _rules, i);
                status = next == original ? "skip"
                    : !RenameEngine.IsSafeTargetName(next) ? "warn"
                    : "ok";
            }
            else if (SimpleRadio.IsChecked == true)
            {
                var frm = SimpleFromBox.Text.Trim();
                var to = SimpleToBox.Text.Trim();
                var curExt = RenameEngine.GetSimpleExt(original);
                if (frm.Length > 0 && curExt.Equals(frm, StringComparison.OrdinalIgnoreCase))
                {
                    next = SimpleKeepCheck.IsChecked == true
                        ? RenameEngine.StripExt(original) + to
                        : to;
                    status = next != original ? "ok" : "skip";
                }
                else if (frm.Length == 0 && curExt.Length > 0)
                {
                    next = RenameEngine.StripExt(original) + (to.Length > 0 ? to : curExt);
                    status = to.Length > 0 && to != curExt ? "ok" : "skip";
                }
            }
            else
            {
                var parsed = RenameEngine.ParseCompoundExt(original, patterns);
                if (parsed is not null)
                {
                    compoundCount++;
                    matchedName = parsed.Pattern.Name;

                    var op = CurrentCompoundOp();
                    var newSeq = parsed.SeqStr;
                    var newSuffix = parsed.FullSuffix;
                    var renumbered = op is "renumber" or "both";

                    if (renumbered)
                    {
                        var start = IntOf(RenStartBox.Text, 1);
                        var step = IntOf(RenStepBox.Text, 1);
                        var padw = IntOf(RenPadBox.Text, 2);

                        // 同 base 组内计数，序号按组递进。
                        var gidx = 0;
                        for (var j = 0; j <= i; j++)
                        {
                            var pj = RenameEngine.ParseCompoundExt(Path.GetFileName(items[j]), patterns);
                            if (pj is not null && pj.Base == parsed.Base) gidx++;
                        }
                        gidx -= 1;

                        newSeq = RenameEngine.PadNum(start + (long)gidx * step, padw);
                    }

                    var tmpl = CmpToTmplBox.Text.Trim();
                    if (op is "replace" or "both" && tmpl.Length > 0)
                        newSuffix = RenameEngine.ApplyTemplate(tmpl, newSeq) ?? newSuffix;
                    else if (renumbered && parsed.SeqStr.Length > 0)
                        newSuffix = parsed.FullSuffix.Replace(parsed.SeqStr, newSeq);

                    var baseName = RenKeepBaseCheck.IsChecked == true && parsed.Base.Length > 0
                        ? parsed.Base
                        : RenameEngine.StripExt(original);
                    next = baseName + newSuffix;
                    status = next != original && RenameEngine.IsSafeTargetName(next) ? "ok" : "skip";
                }
            }

            rows.Add(new RenameRow
            {
                Index = i + 1,
                Original = original,
                FullPath = full,
                Next = next,
                Status = status,
                CanSelect = status != "skip",
                Selected = status != "skip",
            });
        }

        RenameEngine.DetectTargetCollisions(rows);

        _rows.Clear();
        foreach (var r in rows) _rows.Add(r);

        var applicable = rows.Count(r => r.Status is "ok" or "warn");
        PreviewCountText.Text = $"共 {rows.Count} 项 · 可应用 {applicable}";
        ApplyToolbarButton.IsEnabled = applicable > 0;
        ApplyFooterButton.IsEnabled = applicable > 0;

        if (mode == "ext" && CompoundRadio.IsChecked == true)
        {
            if (compoundCount > 0)
            {
                CompoundMatchText.Text = $"✓ 已匹配「{matchedName}」，共 {compoundCount} 个分卷文件";
                CompoundMatchText.Foreground = (Brush)FindResource("B.Accent");
            }
            else
            {
                CompoundMatchText.Text = "⚠ 当前文件未匹配任何识别规则，请在识别库中添加";
                CompoundMatchText.Foreground = (Brush)FindResource("B.Danger");
            }
        }
    }

    // =====================================================================
    // 应用 / 撤销
    // =====================================================================

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.Selected && r.Status is "ok" or "warn").ToList();
        if (selected.Count == 0)
        {
            ResultWindow.Show(this, ResultKind.Warning, "无法重命名", "请至少勾选一项可应用的行。");
            return;
        }

        var plan = new List<RenamePlanItem>(selected.Count);
        foreach (var r in selected)
        {
            var dir = Path.GetDirectoryName(r.FullPath) ?? "";
            plan.Add(new RenamePlanItem(r.FullPath, Path.Combine(dir, r.Next)));
        }

        var outcome = RenameEngine.ApplyRenames(plan);
        AppLog.Info($"批量重命名：成功 {outcome.Ok} 项，失败 {outcome.Errors.Count} 项");

        // 让列表跟上改名后的真实路径。
        foreach (var r in selected)
        {
            var idx = _entries.FindIndex(p => string.Equals(p, r.FullPath, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            var newPath = Path.Combine(Path.GetDirectoryName(r.FullPath) ?? "", r.Next);
            if (File.Exists(newPath) || Directory.Exists(newPath)) _entries[idx] = newPath;
        }

        UpdateUndoButton();
        RefreshPreview();

        if (outcome.Errors.Count > 0)
            ResultWindow.Show(this, ResultKind.Warning, "重命名完成（有错误）",
                $"成功 {outcome.Ok} 项，失败 {outcome.Errors.Count} 项。", outcome.Errors);
        else
            ResultWindow.Show(this, ResultKind.Success, "重命名完成", $"成功重命名 {outcome.Ok} 项。");
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        var journal = UndoJournal.Load();
        var outcome = RenameEngine.UndoLast();

        if (journal is not null)
        {
            foreach (var entry in journal)
            {
                var newPath = Path.Combine(entry.Dir, entry.New);
                var oldPath = Path.Combine(entry.Dir, entry.Old);
                var idx = _entries.FindIndex(p => string.Equals(p, newPath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && (File.Exists(oldPath) || Directory.Exists(oldPath))) _entries[idx] = oldPath;
            }
        }

        UpdateUndoButton();
        RefreshPreview();

        if (outcome.None)
            ResultWindow.Show(this, ResultKind.Info, "无可撤销操作", "没有找到上一次的重命名记录。");
        else if (outcome.Errors.Count > 0)
            ResultWindow.Show(this, ResultKind.Warning, "撤销完成（有错误）",
                $"已撤销 {outcome.Ok} 项，失败 {outcome.Errors.Count} 项。", outcome.Errors);
        else
            ResultWindow.Show(this, ResultKind.Success, "已撤销", $"已撤销 {outcome.Ok} 项重命名。");
    }

    private void UpdateUndoButton() => UndoButton.IsEnabled = UndoJournal.Load() is not null;

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private static int IntOf(string? text, int fallback) => int.TryParse(text, out var n) ? n : fallback;
}
