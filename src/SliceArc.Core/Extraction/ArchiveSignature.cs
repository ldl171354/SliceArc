using System.IO;

namespace SliceArc.Core.Extraction;

/// <summary>
/// Header sniffing, used for one thing only: making a failure message honest.
/// <para>
/// A text file renamed to .zip reaches 7z.dll as garbage and comes back as "unexpected
/// end of data", so the old message told the user to go find missing volumes that never
/// existed. Sniffing the first bytes lets the message say what the file actually is.
/// </para>
/// <para>
/// This never decides whether an archive can be opened - 7z.dll does that. It only
/// classifies files that already failed, and answers <see cref="Kind.Unknown"/> when it
/// has no business having an opinion.
/// </para>
/// </summary>
internal static class ArchiveSignature
{
    public enum Kind
    {
        /// <summary>Header matches a format 7-Zip knows.</summary>
        Archive,

        /// <summary>Reads as text: certainly not an archive.</summary>
        PlainData,

        /// <summary>Not enough evidence to say anything.</summary>
        Unknown,
    }

    private static readonly byte[][] Magic =
    {
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },             // 7z
        new byte[] { 0x50, 0x4B, 0x03, 0x04 },                         // zip
        new byte[] { 0x50, 0x4B, 0x05, 0x06 },                         // zip (empty archive)
        new byte[] { 0x50, 0x4B, 0x07, 0x08 },                         // zip (spanned)
        new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 },             // rar 4/5
        new byte[] { 0x1F, 0x8B },                                     // gzip
        new byte[] { 0x42, 0x5A, 0x68 },                               // bzip2
        new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 },             // xz
        new byte[] { 0x28, 0xB5, 0x2F, 0xFD },                         // zstd
        new byte[] { 0x4D, 0x53, 0x43, 0x46 },                         // cab
        new byte[] { 0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00, 0x00, 0x00 }, // wim
        new byte[] { 0x60, 0xEA },                                     // arj
        new byte[] { 0xED, 0xAB, 0xEE, 0xDB },                         // rpm
        new byte[] { 0x21, 0x3C, 0x61, 0x72, 0x63, 0x68, 0x3E },       // deb / ar
        new byte[] { 0x63, 0x6F, 0x6E, 0x65, 0x63, 0x74, 0x69, 0x78 }, // vhd
        new byte[] { 0x68, 0x73, 0x71, 0x73 },                         // squashfs (le)
        new byte[] { 0x73, 0x71, 0x73, 0x68 },                         // squashfs (be)
        new byte[] { 0x30, 0x37, 0x30, 0x37, 0x30, 0x31 },             // cpio (newc)
        new byte[] { 0x4D, 0x5A },                                     // PE: a .part01.exe SFX archive
    };

    public static Kind Sniff(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Kind.Unknown;

        byte[] head;
        try
        {
            using var fs = File.OpenRead(path);
            head = new byte[512];
            var read = fs.Read(head, 0, head.Length);
            if (read < 8) return Kind.Unknown;
            if (read < head.Length) Array.Resize(ref head, read);
        }
        catch
        {
            return Kind.Unknown;
        }

        foreach (var sig in Magic)
        {
            if (head.Length >= sig.Length && head.AsSpan(0, sig.Length).SequenceEqual(sig))
                return Kind.Archive;
        }

        // tar keeps its magic 257 bytes in.
        if (head.Length >= 262 && head[257] == (byte)'u' && head[258] == (byte)'s'
            && head[259] == (byte)'t' && head[260] == (byte)'a' && head[261] == (byte)'r')
            return Kind.Archive;

        // LZH: "-lh" at offset 2.
        if (head.Length >= 5 && head[2] == (byte)'-' && head[3] == (byte)'l' && head[4] == (byte)'h')
            return Kind.Archive;

        return LooksLikeText(head) ? Kind.PlainData : Kind.Unknown;
    }

    /// <summary>
    /// Text with no archive signature. A NUL byte, any C0 control byte other than tab /
    /// CR / LF, or a high-byte share above 40 % means "binary, keep quiet". Random data
    /// is rejected by the control-byte rule roughly 95 % of the time, and anything this
    /// method is unsure about becomes <see cref="Kind.Unknown"/> rather than a guess.
    /// </summary>
    private static bool LooksLikeText(byte[] head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return true;

        var limit = Math.Min(head.Length, 256);
        var high = 0;
        for (var i = 0; i < limit; i++)
        {
            var b = head[i];
            if (b == 0) return false;
            if (b is 0x09 or 0x0A or 0x0D) continue;
            if (b < 0x20) return false;          // control byte → binary
            if (b >= 0x80) high++;               // UTF-8 continuation / CJK
        }
        return high <= limit * 2 / 5;
    }
}
