// Linked into both SliceArc.Core and SliceArc.ShellExt - keep it dependency-free
// (no 7z.dll, no WPF): it runs inside the Explorer COM surrogate.

namespace SliceArc.Shared;

/// <summary>
/// "Does this file name look like an archive?" - the cheap, name-only guess used
/// wherever file content cannot be inspected.
///
/// Deliberately broader than the classic menu's extension list: every volume
/// member counts (.zNN, .rNN, .sNN, .NNN), because the Windows 11 modern menu's
/// GetState has no AQS limitations and can afford a more generous test.
///
/// The two callers must agree:
///   * the Windows 11 modern menu, via <c>SliceArc.ShellExt</c> (this file)
///   * the classic menu, via the AQS AppliesTo condition in ShellMenuRegistrar
///     (AQS cannot call code, so that one spells the patterns out - keep them in
///     sync by hand; the shared Probe assertions cover this file, and the shell
///     probe covers the AQS side against a real menu)
///
/// They disagreed once. The cost: <c>name.part01.exe</c> - the first volume of a
/// self-extracting RAR set - matched neither, so every extract verb, including
/// "delete each volume as it is spent", was hidden for exactly the files that
/// need it most. Its extension is <c>.exe</c>; the PATTERN is what makes it an
/// archive.
/// </summary>
public static class ArchiveNameHeuristics
{
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".zip", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst", ".lz", ".lzh",
        ".cab", ".iso", ".wim", ".tgz", ".tbz", ".txz", ".arj", ".ace", ".apk",
    };

    /// <summary>True when the name alone suggests an archive worth offering verbs for.</summary>
    public static bool LooksLikeArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var ext = Path.GetExtension(path);
        if (KnownExtensions.Contains(ext)) return true;

        // .z01 / .r00 / .s01 style: letter + two digits.
        if (ext.Length == 4 && ext[0] == '.' &&
            char.ToLowerInvariant(ext[1]) is 'z' or 'r' or 's' &&
            char.IsAsciiDigit(ext[2]) && char.IsAsciiDigit(ext[3]))
            return true;

        // .001 / .002 ... plain numeric slices.
        if (ext.Length >= 4 && ext[0] == '.' && AllDigits(ext.AsSpan(1))) return true;

        // .partN.rar - and .partN.exe, the self-extracting FIRST volume.
        return IsRarVolumeName(Path.GetFileName(path));
    }

    /// <summary>True for <c>name.partN.rar</c> / <c>name.partN.exe</c> (case-insensitive).</summary>
    public static bool IsRarVolumeName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;

        var marker = fileName.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;

        var tail = fileName.AsSpan(marker + 5);
        var digits = 0;
        while (digits < tail.Length && char.IsAsciiDigit(tail[digits])) digits++;
        if (digits == 0) return false;

        var extension = tail[digits..];
        return extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllDigits(ReadOnlySpan<char> value) =>
        value.Length > 0 && value.IndexOfAnyExceptInRange('0', '9') < 0;
}
