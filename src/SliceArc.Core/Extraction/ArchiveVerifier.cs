using System.Diagnostics;

namespace SliceArc.Core.Extraction;

/// <summary>One member that failed verification, with the reason the handler gave.</summary>
public sealed record VerifyIssue(string EntryPath, string Description, long Size)
{
    /// <summary>Human-readable member size, for the detail list.</summary>
    public string SizeText
    {
        get
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Size;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{Size} B" : $"{value:0.#} {units[unit]}";
        }
    }
}

/// <summary>
/// Outcome of checking a single archive's CRCs from end to end: how many members
/// were decoded, which ones failed, and - when the archive could not be tested at
/// all - why. Membership failure is never fatal to the list: one broken archive in
/// a batch still leaves a result to show.
/// </summary>
public sealed record ArchiveVerifyReport
{
    /// <summary>Path the verifier was handed (absolute).</summary>
    public required string Path { get; init; }

    /// <summary>File name to show in the list.</summary>
    public required string DisplayName { get; init; }

    /// <summary>True when every member decoded and passed its CRC.</summary>
    public bool Ok { get; init; }

    /// <summary>The archive could not be opened/verified at all (missing, corrupt beyond use, unsupported).</summary>
    public bool Unverifiable { get; init; }

    /// <summary>True when the archive is encrypted and needs a password we did not have / got wrong.</summary>
    public bool NeedsPassword { get; init; }

    /// <summary>An encrypted archive was tried with the password we had and it was wrong.</summary>
    public bool WrongPassword { get; init; }

    /// <summary>True when the run was cancelled before it finished.</summary>
    public bool Cancelled { get; init; }

    public string FormatName { get; init; } = "unknown";

    public int EntryCount { get; init; }
    public int TestedCount { get; init; }
    public int FailedCount { get; init; }

    /// <summary>One-line cause when not <see cref="Ok"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>What the user can do about <see cref="Reason"/>.</summary>
    public string? Hint { get; init; }

    /// <summary>Every member that failed, in archive order.</summary>
    public IReadOnlyList<VerifyIssue> Issues { get; init; } = Array.Empty<VerifyIssue>();

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Runs a full decode-and-CRC check over one archive without writing a byte, and
/// folds the engine's result into a report the archive-check tool can render
/// directly. A thin wrapper on purpose: the decoding, volume handling, password
/// prompting and per-member outcomes all live in <see cref="ExtractionEngine"/>.
/// </summary>
public static class ArchiveVerifier
{
    /// <param name="archivePath">Archive to check (first volume of a set, or a single file).</param>
    /// <param name="password">Password for an encrypted archive, or null.</param>
    /// <param name="onMember">Fired once per decoded member with its pass/fail state.</param>
    /// <param name="progress">Byte/member progress, same shape the extractor uses.</param>
    public static ArchiveVerifyReport Verify(
        string archivePath,
        string? password = null,
        Action<ArchiveEntry, bool>? onMember = null,
        Action<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var full = SafeFullPath(archivePath);
        var name = Path.GetFileName(full);

        if (!File.Exists(full))
        {
            return Broken(full, name, "找不到文件", "文件可能已被移动、重命名或删除，请确认位置后重试。", clock.Elapsed);
        }

        using var engine = new ExtractionEngine(full);
        engine.Password = password;

        try
        {
            engine.Open();
        }
        catch (PasswordRequiredException ex)
        {
            return new ArchiveVerifyReport
            {
                Path = full,
                DisplayName = name,
                NeedsPassword = true,
                WrongPassword = ex.WasSupplied,
                Reason = ex.WasSupplied ? "口令不正确" : "压缩包已加密，需要口令",
                Hint = "请输入正确的口令后重新校验。",
                Elapsed = clock.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return Broken(full, name, ex.Message,
                FailureHintFor(ex), clock.Elapsed);
        }

        var format = engine.Format?.Name ?? "unknown";
        var entryCount = engine.Entries.Count;
        var failedPaths = new List<string>();

        ExtractionResult result;
        try
        {
            result = engine.Extract(
                new ExtractionOptions
                {
                    OutputDirectory = Path.GetDirectoryName(full) ?? ".",
                    TestOnly = true,
                    Password = password,
                    OnMemberVerified = (entry, ok) =>
                    {
                        if (!ok) failedPaths.Add(entry.NormalizedPath);
                        onMember?.Invoke(entry, ok);
                    },
                    CancellationToken = cancellationToken,
                },
                progress);
        }
        catch (Exception ex)
        {
            return Broken(full, name, ex.Message, FailureHintFor(ex), clock.Elapsed);
        }

        if (result.Cancelled)
        {
            return new ArchiveVerifyReport
            {
                Path = full,
                DisplayName = name,
                Cancelled = true,
                FormatName = format,
                EntryCount = entryCount,
                TestedCount = result.Tested,
                FailedCount = result.Failed,
                Reason = "校验已取消",
                Hint = "重新校验即可从头检查全部内容。",
                Issues = BuildIssues(failedPaths, result, engine.Entries),
                Elapsed = clock.Elapsed,
            };
        }

        if (result.NeedsPassword)
        {
            return new ArchiveVerifyReport
            {
                Path = full,
                DisplayName = name,
                NeedsPassword = true,
                WrongPassword = result.WrongPassword,
                FormatName = format,
                EntryCount = entryCount,
                Reason = result.WrongPassword ? "口令不正确" : "压缩包已加密，需要口令",
                Hint = "请输入正确的口令后重新校验。",
                Elapsed = clock.Elapsed,
            };
        }

        var ok = result.Ok;
        return new ArchiveVerifyReport
        {
            Path = full,
            DisplayName = name,
            Ok = ok,
            FormatName = format,
            EntryCount = entryCount,
            TestedCount = result.Tested,
            FailedCount = result.Failed,
            Reason = ok ? null : result.FailureReason ?? "校验未通过",
            Hint = ok ? null : result.FailureHint ?? "压缩包中的部分内容已损坏，请重新获取后重试。",
            Issues = ok ? Array.Empty<VerifyIssue>() : BuildIssues(failedPaths, result, engine.Entries),
            Elapsed = clock.Elapsed,
        };
    }

    /// <summary>
    /// Pair each failing member with the handler's description. The engine's
    /// error lines read "path: description"; the path we captured in the member
    /// callback gives an exact key, so a corrupt entry gets its precise cause
    /// instead of a generic label.
    /// </summary>
    private static IReadOnlyList<VerifyIssue> BuildIssues(
        List<string> failedPaths, ExtractionResult result, IReadOnlyList<ArchiveEntry> entries)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) sizes[entry.NormalizedPath] = entry.Size;

        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Errors)
        {
            var split = line.IndexOf(": ", StringComparison.Ordinal);
            if (split <= 0) continue;
            var key = line[..split];
            if (!descriptions.ContainsKey(key)) descriptions[key] = line[(split + 2)..];
        }

        var issues = new List<VerifyIssue>(failedPaths.Count);
        foreach (var path in failedPaths)
        {
            var description = descriptions.TryGetValue(path, out var d) ? d : "CRC 校验失败或数据损坏";
            sizes.TryGetValue(path, out var size);
            issues.Add(new VerifyIssue(path, description, size));
        }
        return issues;
    }

    private static ArchiveVerifyReport Broken(string path, string name, string reason, string hint, TimeSpan elapsed) =>
        new()
        {
            Path = path,
            DisplayName = name,
            Unverifiable = true,
            Reason = reason,
            Hint = hint,
            Elapsed = elapsed,
        };

    private static string FailureHintFor(Exception ex) => ex switch
    {
        FileNotFoundException => "文件可能已被移动、重命名或删除，请确认位置后重试。",
        UnauthorizedAccessException => "没有读取该文件的权限，请以管理员身份重试，或检查文件属性。",
        InvalidDataException => "该文件可能已损坏、不完整，或不是本程序支持的压缩包。",
        IOException => "读取文件时出错：文件可能正被其他程序占用，或磁盘出现故障。",
        _ => "请确认压缩包完整、未被占用，然后重试。",
    };

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
