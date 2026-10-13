using System.Text.RegularExpressions;

namespace SliceArc.Core.Volumes;

/// <summary>How a multi-volume set names its parts.</summary>
public enum VolumeStyle
{
    /// <summary>Not a volume member.</summary>
    None,

    /// <summary>archive.7z.001, archive.zip.001, blob.001 — plain byte slicing.</summary>
    NumericSuffix,

    /// <summary>
    /// archive.part1.rar / archive.part2.rar, and the self-extracting first
    /// volume <c>archive.part01.exe</c> (its siblings are still .part02.rar).
    /// </summary>
    RarNewStyle,

    /// <summary>archive.rar + archive.r00 + archive.r01 ... — legacy RAR naming.</summary>
    RarOldStyle,

    /// <summary>archive.z01 ... archive.z99 + archive.zip — PKZIP split, .zip comes LAST.</summary>
    ZipSplit,
}

/// <summary>One physical file that participates in a volume set.</summary>
/// <param name="Path">Absolute path on disk.</param>
/// <param name="Index">0-based ordinal within the set, in archive-stream order.</param>
/// <param name="Length">Size of this physical file in bytes.</param>
/// <param name="ArchiveOffset">Byte offset where this volume starts inside the logical archive stream.</param>
public sealed record VolumeInfo(string Path, int Index, long Length, long ArchiveOffset)
{
    /// <summary>Exclusive end offset of this volume inside the logical archive stream.</summary>
    public long ArchiveEnd => ArchiveOffset + Length;

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// A resolved set of volumes forming one logical archive.
/// Offsets here are what the deletion policy uses to decide when a volume is spent.
/// </summary>
public sealed class VolumeGroup
{
    public VolumeGroup(string baseName, VolumeStyle style, IReadOnlyList<VolumeInfo> volumes, bool isComplete)
    {
        BaseName = baseName;
        Style = style;
        Volumes = volumes;
        IsComplete = isComplete;
        TotalBytes = volumes.Count == 0 ? 0 : volumes[^1].ArchiveEnd;
    }

    public string BaseName { get; }
    public VolumeStyle Style { get; }
    public IReadOnlyList<VolumeInfo> Volumes { get; }
    public bool IsComplete { get; }
    public long TotalBytes { get; }

    public VolumeInfo? First => Volumes.Count > 0 ? Volumes[0] : null;
    public bool IsMultiVolume => Volumes.Count > 1;
    public string? FirstVolumePath => First?.Path;

    /// <summary>
    /// True when each volume is an archive in its own right rather than a byte
    /// slice of one logical stream.
    ///
    /// The distinction decides how the decoder is fed. Sliced sets (.7z.001,
    /// .z01/.../.zip) are concatenated into a single stream. RAR sets must NOT
    /// be: their volumes each carry their own headers, and 7-Zip's RAR handler
    /// stitches the set together itself by asking for each volume by name
    /// through IArchiveOpenVolumeCallback. Concatenating a RAR set decodes as
    /// corrupt data (measured, not assumed).
    /// </summary>
    public bool IsIndependentVolumes =>
        Style is VolumeStyle.RarNewStyle or VolumeStyle.RarOldStyle;

    /// <summary>Index of the volume owning <paramref name="offset"/> in the archive stream.</summary>
    public int VolumeIndexAt(long offset)
    {
        for (var i = Volumes.Count - 1; i >= 0; i--)
        {
            if (offset >= Volumes[i].ArchiveOffset) return i;
        }
        return 0;
    }

    /// <summary>Exclusive end offset of volume <paramref name="index"/> in the archive stream.</summary>
    public long VolumeEnd(int index) =>
        index >= 0 && index < Volumes.Count ? Volumes[index].ArchiveEnd : 0;

    public override string ToString() =>
        $"{BaseName} [{Style}] {Volumes.Count} volume(s), {TotalBytes:N0} bytes";
}

/// <summary>
/// Recognises volume sets on disk. This is deliberately independent of any decoder:
/// knowing the byte layout is enough to track consumption and decide deletions.
/// </summary>
public static partial class VolumeDetector
{
    // .7z.001 / .zip.001 / anything.001  — three or more digits is the convention.
    [GeneratedRegex(@"^(?<base>.+)\.(?<num>\d{3,})$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericSuffixRegex();

    // .part1.rar / .part01.rar, and .part01.exe for a self-extracting first volume
    [GeneratedRegex(@"^(?<base>.+)\.part(?<num>\d+)\.(rar|exe)$", RegexOptions.IgnoreCase)]
    private static partial Regex RarNewStyleRegex();


    // .rar (first) / .r00 .. .r99 / .s00 .. .s99 (continuation)
    [GeneratedRegex(@"^(?<base>.+)\.(?<slot>[rs])(?<num>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex RarOldStyleRegex();

    [GeneratedRegex(@"^(?<base>.+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarPlainRegex();

    // .z01 .. .z99 (continuation)
    [GeneratedRegex(@"^(?<base>.+)\.z(?<num>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipSplitPartRegex();

    [GeneratedRegex(@"^(?<base>.+)\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipPlainRegex();

    /// <summary>
    /// Given any member path, resolve the whole set it belongs to.
    /// Returns null when the path is an ordinary single-file archive.
    /// </summary>
    public static VolumeGroup? DetectGroup(string anyVolumePath)
    {
        var full = Path.GetFullPath(anyVolumePath);
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var name = Path.GetFileName(full);

        // Try each style; the first that produces a multi-file set wins.
        var candidate = TryNumericSuffix(dir, name)
                        ?? TryRarNewStyle(dir, name)
                        ?? TryZipSplit(dir, name)
                        ?? TryRarOldStyle(dir, name);

        return candidate is { IsMultiVolume: true } ? candidate : null;
    }

    /// <summary>Enumerate every volume set found in a directory.</summary>
    public static IReadOnlyList<VolumeGroup> ScanDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<VolumeGroup>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<VolumeGroup>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var group = DetectGroup(file);
            if (group is null) continue;
            if (seen.Add(group.FirstVolumePath!)) groups.Add(group);
        }
        return groups;
    }

    // ----------------------------------------------------------------------

    private static VolumeGroup? TryNumericSuffix(string dir, string name)
    {
        var m = NumericSuffixRegex().Match(name);
        if (!m.Success) return null;

        var baseName = m.Groups["base"].Value;
        var parts = new List<(int Ordinal, string Path, long Length)>();

        foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*"))
        {
            var fm = NumericSuffixRegex().Match(Path.GetFileName(f));
            if (!fm.Success) continue;
            if (!string.Equals(fm.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(fm.Groups["num"].Value, out var ordinal)) continue;
            parts.Add((ordinal, f, new FileInfo(f).Length));
        }

        if (parts.Count == 0) return null;
        parts.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        // Completeness: ordinals must run 1..N with no holes.
        var complete = true;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Ordinal != i + 1) { complete = false; break; }
        }

        return Build(baseName, VolumeStyle.NumericSuffix, parts, complete);
    }

    private static VolumeGroup? TryRarNewStyle(string dir, string name)
    {
        var m = RarNewStyleRegex().Match(name);
        if (!m.Success) return null;

        var baseName = m.Groups["base"].Value;
        var parts = new List<(int Ordinal, string Path, long Length)>();

        // Both extensions: a self-extracting set is name.part01.exe followed by
        // name.part02.rar, name.part03.rar, ...
        foreach (var f in Directory.EnumerateFiles(dir, baseName + ".part*"))
        {
            var fm = RarNewStyleRegex().Match(Path.GetFileName(f));
            if (!fm.Success) continue;
            if (!string.Equals(fm.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(fm.Groups["num"].Value, out var ordinal)) continue;

            // Only the FIRST volume may be an .exe; a stray .part02.exe would be
            // something else entirely, so keep the rule strict.
            var isExe = Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase);
            if (isExe && ordinal != 1) continue;

            parts.Add((ordinal, f, new FileInfo(f).Length));
        }

        if (parts.Count == 0) return null;
        parts.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        var complete = true;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Ordinal != i + 1) { complete = false; break; }
        }

        return Build(baseName, VolumeStyle.RarNewStyle, parts, complete);
    }

    private static VolumeGroup? TryZipSplit(string dir, string name)
    {
        var isPart = ZipSplitPartRegex().IsMatch(name);
        var isFinal = ZipPlainRegex().IsMatch(name);
        if (!isPart && !isFinal) return null;

        var baseName = (isPart ? ZipSplitPartRegex().Match(name) : ZipPlainRegex().Match(name))
            .Groups["base"].Value;

        // A .zNN sibling must exist, otherwise this is just an ordinary .zip.
        var hasZParts = Directory.EnumerateFiles(dir, baseName + ".z*")
            .Any(f => ZipSplitPartRegex().IsMatch(Path.GetFileName(f)));
        if (!hasZParts) return null;

        var parts = new List<(int Ordinal, string Path, long Length)>();

        foreach (var f in Directory.EnumerateFiles(dir, baseName + ".z*"))
        {
            var fm = ZipSplitPartRegex().Match(Path.GetFileName(f));
            if (!fm.Success) continue;
            if (!string.Equals(fm.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(fm.Groups["num"].Value, out var ordinal)) continue;
            parts.Add((ordinal, f, new FileInfo(f).Length));
        }

        parts.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        // The plain .zip is the LAST volume, not the first.
        var finalPath = Path.Combine(dir, baseName + ".zip");
        var complete = true;
        if (File.Exists(finalPath))
        {
            parts.Add((parts.Count + 1, finalPath, new FileInfo(finalPath).Length));
        }
        else
        {
            complete = false;
        }

        for (var i = 0; i < parts.Count - (File.Exists(finalPath) ? 1 : 0); i++)
        {
            if (parts[i].Ordinal != i + 1) { complete = false; break; }
        }

        if (parts.Count == 0) return null;
        return Build(baseName, VolumeStyle.ZipSplit, parts, complete);
    }

    private static VolumeGroup? TryRarOldStyle(string dir, string name)
    {
        var plain = RarPlainRegex().Match(name);
        var slot = RarOldStyleRegex().Match(name);
        if (!plain.Success && !slot.Success) return null;

        var baseName = plain.Success ? plain.Groups["base"].Value : slot.Groups["base"].Value;

        var parts = new List<(int Ordinal, string Path, long Length)>();

        // Part 1 is always <base>.rar
        var firstPath = Path.Combine(dir, baseName + ".rar");
        var hasFirst = File.Exists(firstPath);

        // Windows file enumeration understands only * and ? - a character class
        // like ".[rs]*" is matched literally, so it finds nothing. Enumerate the
        // whole family and let the regex decide.
        foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*"))
        {
            var fm = RarOldStyleRegex().Match(Path.GetFileName(f));
            if (!fm.Success) continue;
            if (!string.Equals(fm.Groups["base"].Value, baseName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(fm.Groups["num"].Value, out var num)) continue;

            // r00 -> 1, r01 -> 2, ... r99 -> 100, s00 -> 101, ...
            var isS = fm.Groups["slot"].Value.Equals("s", StringComparison.OrdinalIgnoreCase);
            var ordinal = 1 + num + (isS ? 100 : 0);
            parts.Add((ordinal, f, new FileInfo(f).Length));
        }

        if (hasFirst) parts.Add((0, firstPath, new FileInfo(firstPath).Length));
        if (parts.Count == 0) return null;

        parts.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));

        var complete = hasFirst;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Ordinal != i) { complete = false; break; }
        }

        return Build(baseName, VolumeStyle.RarOldStyle, parts, complete);
    }

    private static VolumeGroup Build(
        string baseName,
        VolumeStyle style,
        List<(int Ordinal, string Path, long Length)> parts,
        bool complete)
    {
        var volumes = new List<VolumeInfo>(parts.Count);
        long offset = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            volumes.Add(new VolumeInfo(parts[i].Path, i, parts[i].Length, offset));
            offset += parts[i].Length;
        }
        return new VolumeGroup(baseName, style, volumes, complete);
    }
}
