using System.IO;
using System.Text.RegularExpressions;
using SliceArc.App.Cli;
using SliceArc.Core.Extraction;
using SliceArc.Core.Volumes;

namespace SliceArc.App.Services;

/// <summary>
/// Turns a CLI request into an <see cref="ExtractionOptions"/> and runs it on a
/// fresh engine.
///
/// A new engine per run is deliberate: the COM objects 7z.dll hands out are
/// created on whichever thread opens the archive, and the UI runs extraction on
/// a pool thread. Building the engine inside that thread keeps every COM call in
/// one apartment, which removes a whole class of marshalling failures.
/// </summary>
public static class ExtractionRunner
{
    public static ExtractionOptions BuildOptions(CommandLineOptions cli, string archivePath) => new()
    {
        OutputDirectory = OutputNaming.ResolveOutputDirectory(archivePath, cli),
        IncludePaths = cli.Paths.Count > 0 ? cli.Paths : null,
        DeleteSpentVolumes = cli.DeleteSpent,
        KeepLastVolumes = cli.KeepLast,
        Overwrite = cli.Overwrite,
        Password = cli.Password,
        TestOnly = cli.TestOnly,
    };

    public static ExtractionResult Run(
        string archivePath,
        ExtractionOptions options,
        Action<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var engine = new ExtractionEngine(archivePath);

        // The password has to be in place before Open(): an archive with an
        // encrypted header needs it while 7z.dll is still picking a handler.
        engine.Password = options.Password;
        try
        {
            engine.Open();
        }
        catch (PasswordRequiredException ex)
        {
            return new ExtractionResult(0, 0, 0, 0, Array.Empty<string>(), new[] { ex.Message })
            {
                NeedsPassword = true,
                WrongPassword = ex.WasSupplied,
                FailureReason = ex.Message,
                FailureHint = "请输入正确的口令后重试。",
            };
        }

        return engine.Extract(options with { CancellationToken = cancellationToken }, progress);
    }
}

/// <summary>Where a given archive should land, and what the folder is called.</summary>
public static partial class OutputNaming
{
    // "exe" belongs in this list: a self-extracting archive *is* its first volume, and
    // without it the derived folder keeps the .exe suffix and collides with the archive
    // file itself - Directory.Create("D:\foo.exe") while D:\foo.exe exists throws
    // IOException, which is exactly the "无法准备目标文件夹" seen on D:\sed-wgzl.exe.
    [GeneratedRegex(@"\.(7z|zip|rar|tar|gz|bz2|xz|zst|lz|lzh|cab|iso|wim|tgz|tbz|exe)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveExtensionRegex();

    public static string ResolveOutputDirectory(string archivePath, CommandLineOptions cli)
    {
        if (!string.IsNullOrWhiteSpace(cli.OutputDirectory))
            return Path.GetFullPath(cli.OutputDirectory);

        var full = Path.GetFullPath(archivePath);
        var folder = Path.GetDirectoryName(full) ?? Environment.CurrentDirectory;

        return cli.ToSubfolder ? Path.Combine(folder, FolderNameFor(full)) : folder;
    }

    /// <summary>
    /// "sample.7z.001" -> "sample", "foo.part1.rar" -> "foo", "bar.zip" -> "bar".
    /// Reuses the volume detector so the naming rules stay in one place.
    /// </summary>
    public static string FolderNameFor(string archivePath)
    {
        var group = VolumeDetector.DetectGroup(archivePath);
        var baseName = group?.BaseName ?? Path.GetFileName(archivePath);
        var stripped = ArchiveExtensionRegex().Replace(baseName, string.Empty);
        return string.IsNullOrWhiteSpace(stripped) ? "extracted" : stripped;
    }
}
