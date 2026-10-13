using SliceArc.Core.Compression;

namespace SliceArc.App.Cli;

public enum CliMode
{
    /// <summary>Open the main window (optionally on a path).</summary>
    Window,

    /// <summary>Extract one archive, headless apart from a progress window.</summary>
    Extract,

    /// <summary>Extract every archive found in a folder.</summary>
    ExtractAll,

    /// <summary>Compress files/folders into an archive.</summary>
    Compress,

    /// <summary>Add files/folders to an existing archive (--add-to).</summary>
    AddToArchive,

    Register,
    Unregister,
    ShellStatus,
    Help,
}

/// <summary>
/// Command line accepted by SliceArc.App.exe. This is also the contract the
/// Explorer right-click verbs invoke, so the flag names are part of the
/// installed registry values - renaming one means re-registering.
/// </summary>
public sealed record CommandLineOptions
{
    public CliMode Mode { get; init; } = CliMode.Window;

    /// <summary>Archive file, folder, or "%1"/"%V" target.</summary>
    public string? Target { get; init; }

    /// <summary>Explicit output directory (--out) or output archive path (--out for compress).</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Extract beside the archive (--here). Default when nothing else is given.</summary>
    public bool Here { get; init; }

    /// <summary>Extract into &lt;archive folder&gt;\&lt;archive name&gt;\ (--to-subfolder).</summary>
    public bool ToSubfolder { get; init; }

    /// <summary>Reclaim each volume as soon as it is spent (--delete-spent).</summary>
    public bool DeleteSpent { get; init; }

    /// <summary>Never auto-delete the last N volumes (--keep-last).</summary>
    public int KeepLast { get; init; } = 1;

    /// <summary>Overwrite existing files; --no-overwrite turns it off.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Verify only (--test).</summary>
    public bool TestOnly { get; init; }

    /// <summary>No dialogs on success; errors still surface (--quiet).</summary>
    public bool Quiet { get; init; }

    /// <summary>Show the context menu on every file, not just archives (--all-files).</summary>
    public bool AllFiles { get; init; }

    public string? Password { get; init; }

    /// <summary>Exact archive paths to extract (--path, repeatable).</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    // ---- compression-only options ----

    /// <summary>Sources to add to a new archive (--add, repeatable).</summary>
    public IReadOnlyList<string> AddSources { get; init; } = Array.Empty<string>();

    /// <summary>Output archive path for --compress.</summary>
    public string? OutputArchive { get; init; }

    /// <summary>Existing archive to add into, for --add-to.</summary>
    public string? AddToArchivePath { get; init; }

    /// <summary>Format: 7z|zip|tar|gz|bz2|xz|zst|wim. Default 7z.</summary>
    public CompressionFormat Format { get; init; } = CompressionFormat.SevenZip;

    /// <summary>Level 0-9. Default 5.</summary>
    public int Level { get; init; } = 5;

    /// <summary>Method name (LZMA, LZMA2, Deflate, BZip2, Zstd...).</summary>
    public string? Method { get; init; }

    /// <summary>Split volume size in bytes; 0 = single file.</summary>
    public long VolumeSize { get; init; }

    /// <summary>Encrypt 7z headers too (--encrypt-headers).</summary>
    public bool EncryptHeaders { get; init; }

    /// <summary>Disable solid mode (--no-solid) on 7z.</summary>
    public bool NoSolid { get; init; }

    /// <summary>Thread count; 0 = automatic.</summary>
    public int Threads { get; init; }

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
        string? target = null;
        var paths = new List<string>();
        var addSources = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--extract":
                    options = options with { Mode = CliMode.Extract };
                    target = Next(args, ref i) ?? target;
                    break;
                case "--extract-all":
                    options = options with { Mode = CliMode.ExtractAll };
                    target = Next(args, ref i) ?? target;
                    break;
                case "--compress":
                    options = options with { Mode = CliMode.Compress };
                    // --compress takes an optional output path; sources come from --add.
                    // If the next token is not a flag, treat it as the output archive path.
                    var peek = Peek(args, i);
                    if (peek is not null && !peek.StartsWith('-'))
                    {
                        i++;
                        options = options with { OutputArchive = peek };
                    }
                    break;
                case "--add":
                    if (Next(args, ref i) is { } addSrc) addSources.Add(addSrc);
                    break;
                case "--add-to":
                    options = options with { Mode = CliMode.AddToArchive };
                    if (Next(args, ref i) is { } addToArchive)
                        options = options with { AddToArchivePath = addToArchive, Target = addToArchive };
                    break;
                case "--out":
                    options = options with { OutputDirectory = Next(args, ref i) };
                    break;
                case "--path":
                    if (Next(args, ref i) is { } p) paths.Add(p);
                    break;
                case "--here":
                    options = options with { Here = true };
                    break;
                case "--to-subfolder":
                    options = options with { ToSubfolder = true };
                    break;
                case "--delete-spent":
                    options = options with { DeleteSpent = true };
                    break;
                case "--keep-last":
                    if (int.TryParse(Next(args, ref i), out var keep)) options = options with { KeepLast = Math.Max(0, keep) };
                    break;
                case "--no-overwrite":
                    options = options with { Overwrite = false };
                    break;
                case "--test":
                    options = options with { TestOnly = true };
                    break;
                case "--quiet":
                    options = options with { Quiet = true };
                    break;
                case "--all-files":
                    options = options with { AllFiles = true };
                    break;
                case "--password":
                    options = options with { Password = Next(args, ref i) };
                    break;
                case "--format":
                    var fmtStr = Next(args, ref i);
                    if (fmtStr is not null && TryParseFormat(fmtStr, out var fmt))
                        options = options with { Format = fmt };
                    break;
                case "--level":
                    if (int.TryParse(Next(args, ref i), out var lvl)) options = options with { Level = Math.Clamp(lvl, 0, 9) };
                    break;
                case "--method":
                    options = options with { Method = Next(args, ref i) };
                    break;
                case "--split-volume":
                    if (TryParseSize(Next(args, ref i), out var vs2)) options = options with { VolumeSize = Math.Max(0, vs2) };
                    break;
                case "--encrypt-headers":
                    options = options with { EncryptHeaders = true };
                    break;
                case "--no-solid":
                    options = options with { NoSolid = true };
                    break;
                case "--threads":
                    if (int.TryParse(Next(args, ref i), out var th)) options = options with { Threads = Math.Max(0, th) };
                    break;
                case "--register":
                    options = options with { Mode = CliMode.Register };
                    break;
                case "--unregister":
                    options = options with { Mode = CliMode.Unregister };
                    break;
                case "--shell-status":
                    options = options with { Mode = CliMode.ShellStatus };
                    break;
                case "--help":
                case "-h":
                case "/?":
                case "-?":
                    options = options with { Mode = CliMode.Help };
                    break;
                default:
                    if (!arg.StartsWith('-') && target is null) target = arg;
                    break;
            }
        }

        return options with { Target = target, Paths = paths, AddSources = addSources };
    }

    private static string? Next(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count) return null;
        index++;
        return args[index];
    }

    private static string? Peek(IReadOnlyList<string> args, int index)
    {
        if (index + 1 >= args.Count) return null;
        return args[index + 1];
    }

    /// <summary>
    /// Parse --format accepting both enum names (SevenZip, GZip…) and short
    /// aliases (7z, gz, bz2, xz, zst, wim, tar, zip). BandZip-style UX means
    /// users type the extension, not the CLR enum name.
    /// </summary>
    private static bool TryParseFormat(string s, out CompressionFormat format)
    {
        var lower = s.Trim().ToLowerInvariant();
        format = lower switch
        {
            "7z" or "sevenzip" => CompressionFormat.SevenZip,
            "zip" => CompressionFormat.Zip,
            "tar" => CompressionFormat.Tar,
            "gz" or "gzip" or "tgz" => CompressionFormat.GZip,
            "bz2" or "bzip2" or "tbz2" => CompressionFormat.BZip2,
            "xz" or "txz" => CompressionFormat.Xz,
            "zst" or "zstd" or "tzst" => CompressionFormat.Zstd,
            "wim" => CompressionFormat.Wim,
            _ => Enum.TryParse<CompressionFormat>(s, ignoreCase: true, out var f) ? f : CompressionFormat.SevenZip,
        };
        return true;
    }

    /// <summary>Parse a size string like "100KB", "1MB", "500" (bytes) into bytes.</summary>
    private static bool TryParseSize(string? s, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var span = s.AsSpan().Trim();
        // Find where the digits end
        var i = 0;
        while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.')) i++;
        if (i == 0) return false;
        if (!double.TryParse(span[..i], System.Globalization.CultureInfo.InvariantCulture, out var n)) return false;
        var suffix = span[i..].Trim().ToString().ToUpperInvariant();
        var mult = suffix switch
        {
            "" or "B" => 1L,
            "KB" or "K" => 1024L,
            "MB" or "M" => 1024L * 1024,
            "GB" or "G" => 1024L * 1024 * 1024,
            _ => 1L,
        };
        bytes = (long)(n * mult);
        return bytes > 0;
    }

    public static string Usage =>
        """
        超速解压缩 (SliceArc) — 分卷压缩包解压 / 压缩器

        用法:
          SliceArc.App.exe [<压缩包或文件夹>]                 打开主窗口
          SliceArc.App.exe --extract <压缩包> [选项]          直接解压
          SliceArc.App.exe --extract-all <文件夹> [选项]      解压文件夹内全部压缩包
          SliceArc.App.exe --compress [<输出归档>] --add <源> [选项]   压缩
          SliceArc.App.exe --add-to <压缩包> --add <源> [选项]        向现有压缩包添加 / 更新文件
          SliceArc.App.exe --register [--all-files]           安装资源管理器右键菜单
          SliceArc.App.exe --unregister                       移除右键菜单
          SliceArc.App.exe --shell-status                     查看右键菜单注册状态

        解压选项:
          --here                解压到压缩包所在文件夹（默认）
          --to-subfolder        解压到 <压缩包名>\ 子文件夹
          --out <目录>          指定输出目录
          --delete-spent        每解完一卷立即删除该分卷（默认保留最后 1 卷）
          --keep-last <n>       保险丝：最后 n 卷永不删除（默认 1）
          --no-overwrite        已存在的文件不覆盖
          --password <口令>     加密压缩包的口令（压缩与解压通用）
          --test                只校验 CRC，不写任何文件
          --quiet               成功时不弹窗（失败仍会提示）
          --all-files           右键菜单对所有文件显示（默认只在压缩包上显示）

        压缩选项:
          --add <路径>          要加入归档的文件 / 文件夹（可重复）
          --format <fmt>        7z|zip|tar|gz|bz2|xz|wim（默认 7z）
                                zst 只能解压不能创建（7-Zip 26.04 未实现 zstd 写入）；
                                gz/bz2/xz 是单流格式，多个文件会自动打成 .tar.gz/.tar.bz2/.tar.xz
          --level <0..9>       压缩级别（0=存储 / 1=最快 / 5=默认 / 9=极限）
          --method <名>        编码器：LZMA / LZMA2 / Deflate / BZip2 / Zstd…
          --split-volume <字节> 创建分卷（如 1048576 = 每卷 1MB；输出 archive.7z.001/.002…）
          --password <口令>     加密（7z 用 AES-256）
          --encrypt-headers     连文件名一起加密（仅 7z）
          --no-solid            关闭固实模式（7z 默认开）
          --threads <n>         线程数（0=自动）
        """;
}
