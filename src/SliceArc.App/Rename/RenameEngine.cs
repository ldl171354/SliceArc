using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SliceArc.App.Rename;

// =========================================================================
// 批量重命名引擎：纯 C#，不依赖 WPF。
// 从 Python/tkinter 版「图吧工具箱·批量重命名」移植而来，保留原有的
// 规则语义、复合后缀识别库、安全校验、冲突检测与撤销日志。
// =========================================================================

/// <summary>重命名规则的种类。</summary>
public enum RenameRuleType { Replace, Regex, Prefix, Suffix, Number, Case, Remove }

/// <summary>大小写转换的模式。</summary>
public enum CaseMode { Upper, Lower, Title, Camel }

/// <summary>
/// 一条重命名规则。参数集中在一个对象里，便于 UI 直接绑定；
/// 每种类型只用到其中一部分字段（对应 Python 版的 params 字典）。
/// </summary>
public sealed class RenameRule
{
    public int Id { get; set; }
    public RenameRuleType Type { get; set; }

    // replace / remove
    public string Find { get; set; } = "";
    public string To { get; set; } = "";
    public bool CaseSensitive { get; set; }

    // regex
    public string Pattern { get; set; } = "";

    // prefix / suffix
    public string Text { get; set; } = "";

    // number
    public int Start { get; set; } = 1;
    public int Step { get; set; } = 1;
    public int Width { get; set; } = 2;
    public string Position { get; set; } = "suffix";
    public string Sep { get; set; } = "_";

    // case
    public CaseMode Mode { get; set; } = CaseMode.Lower;

    /// <summary>按 Python 版的默认参数新建一条规则。</summary>
    public static RenameRule Create(RenameRuleType type, int id) => new()
    {
        Id = id,
        Type = type,
        Text = type switch
        {
            RenameRuleType.Prefix => "new_",
            RenameRuleType.Suffix => "_v2",
            _ => "",
        },
    };

    /// <summary>UI 列表里显示的中文类型名。</summary>
    public string TypeName => Type switch
    {
        RenameRuleType.Replace => "替换文本",
        RenameRuleType.Regex => "正则替换",
        RenameRuleType.Prefix => "添加前缀",
        RenameRuleType.Suffix => "添加后缀",
        RenameRuleType.Number => "序号编号",
        RenameRuleType.Case => "大小写转换",
        RenameRuleType.Remove => "删除文本",
        _ => Type.ToString(),
    };
}

/// <summary>复合后缀识别库的一条规则（可内置，也可自定义）。</summary>
public sealed class CompoundPattern
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Regex { get; set; } = "";
    public int SeqGroup { get; set; } = 1;
    public string Sample { get; set; } = "";
    public bool Builtin { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>复合后缀解析结果。</summary>
public sealed record CompoundMatch(
    CompoundPattern Pattern, string Base, string SeqStr, long SeqNum, int Pad, string FullSuffix);

/// <summary>预览表格的一行（含勾选状态）。</summary>
public sealed class RenameRow : INotifyPropertyChanged
{
    public int Index { get; init; }
    public string Original { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Next { get; set; } = "";

    /// <summary>ok / warn / skip。</summary>
    public string Status { get; set; } = "skip";

    public bool CanSelect { get; set; }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public string StatusText => Status switch
    {
        "ok" => "正常",
        "warn" => "警告",
        _ => "跳过",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>一次真实重命名的目标（源 → 目标全路径）。</summary>
public sealed record RenamePlanItem(string OldPath, string NewPath);

/// <summary>重命名 / 撤销的执行结果。</summary>
public sealed class RenameOutcome
{
    public int Ok { get; set; }

    /// <summary>撤销时没有可撤销的记录。</summary>
    public bool None { get; set; }

    public List<string> Errors { get; } = new();
}

/// <summary>撤销日志里的一条记录。</summary>
public sealed class UndoEntry
{
    public string Dir { get; set; } = "";
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
    public string Time { get; set; } = "";
}

/// <summary>
/// 撤销日志：记录 (目录, 旧名, 新名)。落盘位置与设置文件同级，
/// 便携版就放在 exe 旁边，保证崩溃后仍能撤销。
/// </summary>
public static class UndoJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "SliceArc.rename.undo.json");

    public static void Save(IReadOnlyList<UndoEntry> entries)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, Json));
        }
        catch
        {
            // 日志写入失败不应影响重命名本身。
        }
    }

    /// <summary>没有记录时返回 null。</summary>
    public static List<UndoEntry>? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var list = JsonSerializer.Deserialize<List<UndoEntry>>(File.ReadAllText(FilePath), Json);
            return list is { Count: > 0 } ? list : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear() => Save(Array.Empty<UndoEntry>());
}

/// <summary>自定义复合后缀规则的持久化（与设置文件同级）。</summary>
public static class CustomPatternStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "SliceArc.rename.patterns.json");

    public static List<CompoundPattern> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<CompoundPattern>();
            return JsonSerializer.Deserialize<List<CompoundPattern>>(File.ReadAllText(FilePath), Json)
                   ?? new List<CompoundPattern>();
        }
        catch
        {
            return new List<CompoundPattern>();
        }
    }

    public static void Save(IReadOnlyList<CompoundPattern> patterns)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(patterns, Json));
        }
        catch
        {
            // best-effort
        }
    }
}

/// <summary>重命名引擎的核心算法集合（纯函数优先，静态方法）。</summary>
public static class RenameEngine
{
    // ---------------------------------------------------------------------
    // 内置复合后缀识别库（对应 Python 的 BUILTIN_PATTERNS）
    // ---------------------------------------------------------------------
    public static readonly IReadOnlyList<CompoundPattern> BuiltinPatterns = new List<CompoundPattern>
    {
        new() { Id = "rar-part",     Name = "RAR 分卷（.partN.rar）",  Regex = @"\.part(\d+)\.rar$",    SeqGroup = 1, Sample = "archive.part1.rar",  Builtin = true, Enabled = true },
        new() { Id = "7z-vol",       Name = "7z 分卷（.7z.NNN）",      Regex = @"\.7z\.(\d+)$",         SeqGroup = 1, Sample = "backup.7z.001",     Builtin = true, Enabled = true },
        new() { Id = "zip-vol",      Name = "ZIP 分卷（.zNN / .zip）", Regex = @"\.(z(\d+)|zip)$",      SeqGroup = 2, Sample = "data.z01",          Builtin = true, Enabled = true },
        new() { Id = "rar-old",      Name = "RAR 旧式分卷（.rNN）",    Regex = @"\.r(\d+)$",            SeqGroup = 1, Sample = "movie.r00",         Builtin = true, Enabled = true },
        new() { Id = "hjsplit",      Name = "HJSplit 分卷（.NNN）",    Regex = @"\.(\d+)$",             SeqGroup = 1, Sample = "split.001",         Builtin = true, Enabled = true },
        new() { Id = "7z-multipart", Name = "7z 多卷（.volN+1.7z）",   Regex = @"\.vol(\d+)\+1\.7z$",   SeqGroup = 1, Sample = "archive.vol0+1.7z", Builtin = true, Enabled = true },
    };

    /// <summary>内置规则（已启用）+ 自定义规则（已启用）。</summary>
    public static List<CompoundPattern> GetActivePatterns(IReadOnlyList<CompoundPattern> custom)
    {
        var result = new List<CompoundPattern>();
        foreach (var p in BuiltinPatterns) if (p.Enabled) result.Add(p);
        foreach (var p in custom) if (p.Enabled) result.Add(p);
        return result;
    }

    public static List<CompoundPattern> GetAllPatterns(IReadOnlyList<CompoundPattern> custom)
    {
        var result = new List<CompoundPattern>(BuiltinPatterns);
        result.AddRange(custom);
        return result;
    }

    // ---------------------------------------------------------------------
    // 基础工具
    // ---------------------------------------------------------------------

    /// <summary>零填充：width&gt;0 时补零，否则原样返回。</summary>
    public static string PadNum(long n, int width)
    {
        var s = n.ToString(CultureInfo.InvariantCulture);
        return width > 0 ? s.PadLeft(width, '0') : s;
    }

    /// <summary>取简单后缀（含点）：最后一个 '.' 之后的部分。</summary>
    public static string GetSimpleExt(string name)
    {
        var idx = name.LastIndexOf('.');
        return idx > 0 ? name[idx..] : "";
    }

    /// <summary>去掉简单后缀。</summary>
    public static string StripExt(string name)
    {
        var idx = name.LastIndexOf('.');
        return idx > 0 ? name[..idx] : name;
    }

    /// <summary>模板替换：N 代表序号占位符。</summary>
    public static string? ApplyTemplate(string? tmpl, string seqStr) =>
        string.IsNullOrEmpty(tmpl) ? null : tmpl.Replace("N", seqStr);

    /// <summary>
    /// 用识别库解析复合后缀。返回 null 表示未匹配。
    /// </summary>
    public static CompoundMatch? ParseCompoundExt(string filename, IReadOnlyList<CompoundPattern> patterns)
    {
        foreach (var p in patterns)
        {
            Match m;
            try { m = System.Text.RegularExpressions.Regex.Match(filename, p.Regex); }
            catch (ArgumentException) { continue; } // 非法正则：跳过

            if (m.Success && p.SeqGroup <= m.Groups.Count - 1)
            {
                var seqStr = m.Groups[p.SeqGroup].Value ?? "";
                var fullSuffix = m.Value;
                var baseName = filename[..(filename.Length - fullSuffix.Length)];
                var seqNum = long.TryParse(seqStr, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
                return new CompoundMatch(p, baseName, seqStr, seqNum, seqStr.Length, fullSuffix);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // 规则引擎
    // ---------------------------------------------------------------------

    /// <summary>按顺序把规则套用到单个名称上；index 供序号规则使用。</summary>
    public static string ApplyRulesToName(string name, IReadOnlyList<RenameRule> rules, int index)
    {
        var result = name;
        foreach (var r in rules)
        {
            switch (r.Type)
            {
                case RenameRuleType.Replace:
                    if (string.IsNullOrEmpty(r.Find)) break;
                    result = ReplaceLiteral(result, r.Find, r.To ?? "", r.CaseSensitive);
                    break;

                case RenameRuleType.Regex:
                    try { result = System.Text.RegularExpressions.Regex.Replace(result, r.Pattern, r.To ?? ""); }
                    catch (ArgumentException) { /* 非法正则：忽略该条 */ }
                    break;

                case RenameRuleType.Prefix:
                    result = (r.Text ?? "") + result;
                    break;

                case RenameRuleType.Suffix:
                {
                    var d = result.LastIndexOf('.');
                    var text = r.Text ?? "";
                    result = d > 0 ? result[..d] + text + result[d..] : result + text;
                    break;
                }

                case RenameRuleType.Number:
                {
                    var num = r.Start + (long)index * r.Step;
                    var s = PadNum(num, r.Width);
                    var sep = r.Sep ?? "_";
                    if (r.Position == "prefix")
                    {
                        result = s + sep + result;
                    }
                    else
                    {
                        var d = result.LastIndexOf('.');
                        result = d > 0 ? result[..d] + sep + s + result[d..] : result + sep + s;
                    }
                    break;
                }

                case RenameRuleType.Case:
                    result = r.Mode switch
                    {
                        CaseMode.Upper => result.ToUpperInvariant(),
                        CaseMode.Lower => result.ToLowerInvariant(),
                        CaseMode.Title => Regex.Replace(result, @"\b\w", static m => m.Value.ToUpperInvariant()),
                        CaseMode.Camel => Regex.Replace(result.ToLowerInvariant(), @"\b\w", static m => m.Value.ToUpperInvariant()),
                        _ => result,
                    };
                    break;

                case RenameRuleType.Remove:
                    if (string.IsNullOrEmpty(r.Text)) break;
                    result = ReplaceLiteral(result, r.Text, "", r.CaseSensitive);
                    break;
            }
        }
        return result;
    }

    /// <summary>把查找串当作字面量替换（不解释 $ 分组引用）。</summary>
    private static string ReplaceLiteral(string input, string find, string to, bool caseSensitive)
    {
        var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        return Regex.Replace(input, Regex.Escape(find), _ => to, options);
    }

    // ---------------------------------------------------------------------
    // 安全校验（文件系统 sink 防护）
    // ---------------------------------------------------------------------

    /// <summary>目标名不得为空、不得含路径分隔符或 .. 遍历。</summary>
    public static bool IsSafeTargetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains('\0')) return false;
        if (name is "." or "..") return false;
        return true;
    }

    /// <summary>
    /// 目标名冲突检测：同一目录内出现重复目标名的行标记为“警告”。
    /// （对应 Python 的 detect_target_collisions。）
    /// </summary>
    public static void DetectTargetCollisions(IReadOnlyList<RenameRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (r.Status is not ("ok" or "warn")) continue;
            if (string.IsNullOrEmpty(r.Next)) continue;
            var key = CollisionKey(r);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var r in rows)
        {
            if (r.Status is not ("ok" or "warn")) continue;
            if (counts.GetValueOrDefault(CollisionKey(r)) > 1) r.Status = "warn";
        }
    }

    private static string CollisionKey(RenameRow row) =>
        (Path.GetDirectoryName(row.FullPath) ?? "") + "\u0000" + row.Next;

    // ---------------------------------------------------------------------
    // 真实重命名 / 撤销
    // ---------------------------------------------------------------------

    /// <summary>
    /// 执行一批重命名。先把完整计划写入撤销日志再动手，保证崩溃后仍可撤销；
    /// 结束后用成功的条目覆盖日志，使撤销计数准确。
    /// </summary>
    public static RenameOutcome ApplyRenames(IReadOnlyList<RenamePlanItem> items)
    {
        var outcome = new RenameOutcome();

        var planned = new List<UndoEntry>(items.Count);
        foreach (var it in items)
        {
            planned.Add(new UndoEntry
            {
                Dir = Path.GetDirectoryName(it.OldPath) ?? "",
                Old = Path.GetFileName(it.OldPath),
                New = Path.GetFileName(it.NewPath),
                Time = DateTime.Now.ToString("o"),
            });
        }

        // 先落盘（崩溃安全）。
        UndoJournal.Save(planned);

        var succeeded = new List<UndoEntry>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var name = Path.GetFileName(it.NewPath);

            if (!IsSafeTargetName(name))
            {
                outcome.Errors.Add($"{Path.GetFileName(it.OldPath)} → 目标名不合法");
                continue;
            }

            var isDir = Directory.Exists(it.OldPath);
            if (!isDir && !File.Exists(it.OldPath))
            {
                outcome.Errors.Add($"{Path.GetFileName(it.OldPath)} → 源不存在");
                continue;
            }

            if (!string.Equals(it.OldPath, it.NewPath, StringComparison.Ordinal) &&
                (File.Exists(it.NewPath) || Directory.Exists(it.NewPath)))
            {
                outcome.Errors.Add($"{Path.GetFileName(it.OldPath)} → 目标已存在：{name}");
                continue;
            }

            try
            {
                if (isDir) Directory.Move(it.OldPath, it.NewPath);
                else File.Move(it.OldPath, it.NewPath);
                outcome.Ok++;
                succeeded.Add(planned[i]);
            }
            catch (Exception ex)
            {
                outcome.Errors.Add($"{Path.GetFileName(it.OldPath)} → {ex.Message}");
            }
        }

        UndoJournal.Save(succeeded);
        return outcome;
    }

    /// <summary>撤销上一次重命名（按逆序把 新名 → 旧名）。</summary>
    public static RenameOutcome UndoLast()
    {
        var outcome = new RenameOutcome();
        var journal = UndoJournal.Load();
        if (journal is null)
        {
            outcome.None = true;
            return outcome;
        }

        for (var i = journal.Count - 1; i >= 0; i--)
        {
            var e = journal[i];
            var current = Path.Combine(e.Dir, e.New);
            var original = Path.Combine(e.Dir, e.Old);
            try
            {
                if (Directory.Exists(current))
                {
                    if (!Directory.Exists(original) && !File.Exists(original))
                    {
                        Directory.Move(current, original);
                        outcome.Ok++;
                    }
                }
                else if (File.Exists(current))
                {
                    if (!File.Exists(original) && !Directory.Exists(original))
                    {
                        File.Move(current, original);
                        outcome.Ok++;
                    }
                }
            }
            catch (Exception ex)
            {
                outcome.Errors.Add($"{e.New} → {ex.Message}");
            }
        }

        UndoJournal.Clear();
        return outcome;
    }
}
