using SliceArc.Core.SevenZip;
using SliceArc.Core.Volumes;

namespace SliceArc.Core.Extraction;

/// <summary>One archive on disk, collapsed to its first volume.</summary>
/// <param name="FirstVolumePath">Path to open: the first volume of a set, or the single archive file.</param>
/// <param name="DisplayName">File name to show in a list.</param>
/// <param name="VolumeCount">1 for an ordinary archive.</param>
/// <param name="TotalBytes">Sum of every volume, or the file size.</param>
/// <param name="IsComplete">False when a volume is missing from the sequence.</param>
public sealed record ArchiveCandidate(
    string FirstVolumePath,
    string DisplayName,
    int VolumeCount,
    long TotalBytes,
    bool IsComplete)
{
    public bool IsMultiVolume => VolumeCount > 1;
}

/// <summary>
/// Finds archives on disk without opening them: a volume set is reported once,
/// keyed by its first volume, so a 50-part archive does not appear 50 times.
/// </summary>
public static class ArchiveScanner
{
    /// <summary>Every archive directly inside <paramref name="directory"/> (or below it).</summary>
    public static IReadOnlyList<ArchiveCandidate> Scan(string directory, bool recursive = false)
    {
        if (!Directory.Exists(directory)) return Array.Empty<ArchiveCandidate>();

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ArchiveCandidate>();

        foreach (var file in Directory.EnumerateFiles(directory, "*", option))
        {
            var candidate = Describe(file);
            if (candidate is null) continue;
            if (seen.Add(candidate.FirstVolumePath)) result.Add(candidate);
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Describe one file as an archive, or return null when it is neither a known
    /// archive nor the first volume of a set. Non-first volumes return null, which
    /// is what keeps a volume set from being listed once per part.
    /// </summary>
    public static ArchiveCandidate? Describe(string file)
    {
        if (!File.Exists(file)) return null;

        var group = VolumeDetector.DetectGroup(file);
        if (group is not null)
        {
            if (!IsFirstVolume(group, file)) return null;
            return new ArchiveCandidate(
                group.FirstVolumePath!,
                Path.GetFileName(group.FirstVolumePath!),
                group.Volumes.Count,
                group.TotalBytes,
                group.IsComplete);
        }

        if (!IsKnownArchiveFile(file)) return null;
        return new ArchiveCandidate(
            Path.GetFullPath(file),
            Path.GetFileName(file),
            1,
            new FileInfo(file).Length,
            true);
    }

    /// <summary>True when the file name matches a handler 7z.dll registered.</summary>
    public static bool IsKnownArchiveFile(string fileName) =>
        SevenZipLibrary.FindByExtension(Path.GetFileName(fileName)) is not null;

    /// <summary>True when <paramref name="file"/> is the first part of its volume set.</summary>
    public static bool IsFirstVolume(VolumeGroup group, string file)
    {
        var first = group.FirstVolumePath;
        return first is not null &&
               string.Equals(Path.GetFullPath(first), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
    }
}
