using System.Windows.Media;

namespace SliceArc.App.Models;

/// <summary>
/// One visible line in the main list. Deliberately flat and immutable: the view
/// rebuilds rows on every navigation instead of mutating them.
/// </summary>
public sealed class BrowserRow
{
    private static readonly Brush TextBrush = Frozen(0x1F, 0x23, 0x28);
    private static readonly Brush DimBrush = Frozen(0xA0, 0xA6, 0xAE);
    private static readonly Brush AccentBrush = Frozen(0x2F, 0x6F, 0xEB);
    private static readonly Brush FolderBrush = Frozen(0xE0, 0xA9, 0x3B);
    private static readonly Brush VolumeBrush = Frozen(0x8A, 0x92, 0x9E);

    public required string Name { get; init; }

    /// <summary>Segoe MDL2 glyph shown before the name.</summary>
    public string Glyph { get; init; } = "\uE7C3";

    /// <summary>Path on disk. Empty for entries inside an archive.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Path inside the archive, forward slashes.</summary>
    public string EntryPath { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    /// <summary>Folder mode: the file is an openable archive (or its first volume).</summary>
    public bool IsArchive { get; init; }

    /// <summary>Folder mode: a volume that is not the first one, so it cannot be opened on its own.</summary>
    public bool IsVolumeTail { get; init; }

    public long Size { get; init; }

    public long PackedSize { get; init; }

    public int VolumeCount { get; init; } = 1;

    public DateTime? Modified { get; init; }

    public string TypeText { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    // ---- display ----
    public string SizeText => IsDirectory ? string.Empty : FormatSize(Size);

    public string PackedText => IsDirectory || PackedSize <= 0 ? string.Empty : FormatSize(PackedSize);

    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    public Brush NameBrush => IsVolumeTail ? DimBrush : TextBrush;

    public Brush GlyphBrush => IsDirectory ? FolderBrush
        : IsVolumeTail ? VolumeBrush
        : IsArchive ? AccentBrush
        : DimBrush;

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Everything the window needs to know about an opened archive.</summary>
public sealed record ArchiveSummary(
    string Path,
    IReadOnlyList<Core.Extraction.ArchiveEntry> Entries,
    int VolumeCount,
    long TotalBytes,
    bool Complete,
    string FormatName)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}
