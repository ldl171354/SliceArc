namespace SliceArc.Core.Compression;

/// <summary>Archive formats SliceArc can write. Kept to the ones 7z.dll exposes an IOutArchive for;
/// the constraint in HANDOFF.md §9 ("don't write custom parsers - 60 formats live in 7z.dll") means
/// every entry here is dispatched through 7z.dll's own handler, never a hand-rolled encoder.</summary>
public enum CompressionFormat
{
    /// <summary>7z - LZMA/LZMA2, supports solid, encryption, splitting. The default.</summary>
    SevenZip,
    /// <summary>zip - deflate/deflate64/bzip2/zstd, the universal interchange format.</summary>
    Zip,
    /// <summary>tar - uncompressed, often wrapped by gz/bz2/xz/zst for compression.</summary>
    Tar,
    /// <summary>gzip - single-stream deflate; pairs with tar for .tar.gz.</summary>
    GZip,
    /// <summary>bzip2 - single-stream BWT; pairs with tar for .tar.bz2.</summary>
    BZip2,
    /// <summary>xz - single-stream LZMA2; pairs with tar for .tar.xz.</summary>
    Xz,
    /// <summary>zstd - single-stream Facebook codec; pairs with tar for .tar.zst.</summary>
    Zstd,
    /// <summary>wim - Windows Imaging Model. Niche but writable by 7z.dll.</summary>
    Wim,
}

public static class CompressionFormatExtensions
{
    /// <summary>The single extension 7z.dll reports for this handler when writing (no leading dot).</summary>
    public static string Extension(this CompressionFormat f) => f switch
    {
        CompressionFormat.SevenZip => "7z",
        CompressionFormat.Zip => "zip",
        CompressionFormat.Tar => "tar",
        CompressionFormat.GZip => "gz",
        CompressionFormat.BZip2 => "bz2",
        CompressionFormat.Xz => "xz",
        CompressionFormat.Zstd => "zst",
        CompressionFormat.Wim => "wim",
        _ => "7z",
    };

    /// <summary>The 7z.dll handler-name we look up to find this format's CLSID.</summary>
    public static string HandlerName(this CompressionFormat f) => f switch
    {
        CompressionFormat.SevenZip => "7z",
        CompressionFormat.Zip => "zip",
        CompressionFormat.Tar => "tar",
        CompressionFormat.GZip => "gzip",
        CompressionFormat.BZip2 => "bzip2",
        CompressionFormat.Xz => "xz",
        CompressionFormat.Zstd => "zstd",
        CompressionFormat.Wim => "wim",
        _ => "7z",
    };
}

/// <summary>What the caller wants the compressor to do. Mirrors <see cref="SliceArc.Core.Extraction.ExtractionOptions"/>
/// in shape so the CLI/UI layer can treat both sides uniformly.</summary>
public sealed record CompressionOptions
{
    /// <summary>Files and/or directories to add. Directories are walked recursively; each top-level
    /// entry becomes a root in the new archive.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Where the finished archive lands. Parent is created if missing.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Handler to dispatch to. Default 7z (best ratio + supports volumes + encryption).</summary>
    public CompressionFormat Format { get; init; } = CompressionFormat.SevenZip;

    /// <summary>0..9. 0 = store (no compression), 1 = fastest, 5 = default, 9 = ultra. Format-specific.</summary>
    public int Level { get; init; } = 5;

    /// <summary>Codec name: "LZMA", "LZMA2", "Deflate", "Deflate64", "BZip2", "PPMd", "Zstd"...
    /// Null = handler's default for the chosen level.</summary>
    public string? Method { get; init; }

    /// <summary>Bytes per split volume, or 0 for a single file. When >0 the output becomes
    /// <c>name.ext.001, name.ext.002, ...</c> and the engine implements IArchiveUpdateCallback2.</summary>
    public long VolumeSize { get; init; }

    /// <summary>Optional password. Empty string = no encryption. When set on 7z, AES-256 is used.</summary>
    public string? Password { get; init; }

    /// <summary>Encrypt file names too (7z only). Hidden directory and file names in the archive header.</summary>
    public bool EncryptHeaders { get; init; }

    /// <summary>Create a solid archive (7z only) - small files share a dictionary, better ratio.</summary>
    public bool Solid { get; init; } = true;

    /// <summary>Thread count. 0 = let 7z.dll decide.</summary>
    public int Threads { get; init; }

    /// <summary>Cancellation token; aborts at the next SetCompleted callback.</summary>
    public CancellationToken CancellationToken { get; init; }
}

public sealed record CompressionProgress(
    int ItemsDone,
    int ItemsTotal,
    long BytesDone,
    long BytesTotal,
    long BytesWritten,
    int VolumeIndex,
    int VolumeCount,
    string? CurrentEntry)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;
}

public sealed record CompressionResult(
    int ItemsAdded,
    long BytesRead,
    long BytesWritten,
    IReadOnlyList<string> VolumePaths,
    IReadOnlyList<string> Errors)
{
    public bool Cancelled { get; init; }
    public bool Ok => Errors.Count == 0 && !Cancelled;
}

/// <summary>One input file the engine will offer to 7z.dll. Built up before UpdateItems is called.</summary>
internal sealed class PendingItem
{
    public string ArchivePath { get; init; } = string.Empty;   // forward slashes, relative to archive root
    public string FullPath { get; init; } = string.Empty;      // absolute path on disk
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public uint Attributes { get; init; }
}
