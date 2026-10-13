namespace SliceArc.Core.Extraction;

/// <summary>One item inside an archive, as reported by 7z.dll.</summary>
public sealed record ArchiveEntry(
    uint Index,
    string Path,
    bool IsDirectory,
    long Size,
    long PackedSize,
    bool IsSolid,
    bool IsEncrypted,
    long ArchiveOffset)
{
    /// <summary>Local last-write time, when the handler exposes one.</summary>
    public DateTime? Modified { get; init; }

    /// <summary>Stored CRC-32, 0 when the handler reports none.</summary>
    public uint Crc { get; init; }

    /// <summary>Raw DOS/Windows attribute bits (kpidAttrib).</summary>
    public uint Attributes { get; init; }

    public string Name => System.IO.Path.GetFileName(Path.Replace('\\', '/'));

    public string Extension => System.IO.Path.GetExtension(Path);

    /// <summary>Archive path with forward slashes, which is what globs match against.</summary>
    public string NormalizedPath => Path.Replace('\\', '/');

    public override string ToString() =>
        $"{(IsDirectory ? "[dir] " : "")}{Path} ({Size:N0} B)";
}

/// <summary>What the caller wants to happen.</summary>
public sealed record ExtractionOptions
{
    /// <summary>Where the payload lands. Never a temp directory.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// Simple wildcard filters (<c>*</c> and <c>?</c>). Null or empty means
    /// "everything". Filtering a solid archive still decodes the whole block -
    /// it saves disk, not CPU.
    /// </summary>
    public IReadOnlyList<string>? IncludeGlobs { get; init; }

    /// <summary>
    /// Exact archive paths to extract (forward or back slashes). Takes precedence
    /// over <see cref="IncludeGlobs"/> when non-empty, and exists because a path
    /// that happens to contain a wildcard character must not be pattern-matched.
    /// </summary>
    public IReadOnlyList<string>? IncludePaths { get; init; }

    /// <summary>
    /// Delete each volume the moment it is provably spent. Off by default:
    /// once a volume is gone it cannot be recovered.
    /// </summary>
    public bool DeleteSpentVolumes { get; init; }

    /// <summary>
    /// Safety fuse. The final N volumes are never auto-deleted, so a late
    /// failure still leaves the archive re-openable. Default 1.
    /// </summary>
    public int KeepLastVolumes { get; init; } = 1;

    /// <summary>Overwrite existing files. When false, existing files are skipped.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Password for encrypted archives. Null for none.</summary>
    public string? Password { get; init; }

    /// <summary>Refuse to write more than this many bytes (zip-bomb guard). 0 disables.</summary>
    public long MaxTotalBytes { get; init; }

    /// <summary>
    /// Alternative destination for decoded bytes. When set, every selected
    /// member is written into the stream this factory returns instead of a file
    /// under <see cref="OutputDirectory"/>, and the engine disposes that stream
    /// when the member is finished.
    ///
    /// This is what lets drag-out hand bytes to the shell's copy engine with no
    /// temporary file anywhere: the factory returns a queue-backed stream and
    /// the shell reads from the other end.
    /// </summary>
    public Func<ArchiveEntry, Stream>? OutputFactory { get; init; }

    /// <summary>
    /// Verify only: decode every selected member and check its CRC without
    /// writing a single byte to disk. OutputDirectory is then unused.
    /// </summary>
    public bool TestOnly { get; init; }

    /// <summary>
    /// Verification runs only (<see cref="TestOnly"/>): called once per member
    /// that was decoded, with <c>ok</c> telling whether it passed. Directories
    /// are not reported. Lets a caller show a per-file pass/fail list instead of
    /// a bare count, which is what the archive-check tool needs.
    /// </summary>
    public Action<ArchiveEntry, bool>? OnMemberVerified { get; init; }

    /// <summary>Cancelling aborts the run at the next callback; partial files are removed.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Snapshot of extraction state, pushed after each item and each delete.</summary>
public sealed record ExtractionProgress(
    int EntriesDone,
    int EntriesTotal,
    long BytesDone,
    long BytesTotal,
    int CurrentVolume,
    int VolumeCount,
    long BytesFreed,
    int VolumesDeleted,
    string? CurrentEntry)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;

    /// <summary>Bytes decoded so far for the member named by <see cref="CurrentEntry"/>.</summary>
    public long CurrentFileBytes { get; init; }

    /// <summary>
    /// Uncompressed size of the current member. 0 when it is unknown or has no
    /// size (a directory), which is the signal to show an indeterminate bar.
    /// </summary>
    public long CurrentFileTotal { get; init; }

    /// <summary>Progress of the current member, or null when it has no meaningful size.</summary>
    public double? FileFraction => CurrentFileTotal > 0
        ? Math.Clamp((double)CurrentFileBytes / CurrentFileTotal, 0, 1)
        : null;
}

/// <summary>
/// Per-volume reclamation state. Exposed for tests and diagnostics: "why was
/// this volume not deleted" is otherwise invisible from outside the engine.
/// </summary>
/// <param name="Index">Position in the volume group.</param>
/// <param name="FileName">File name on disk.</param>
/// <param name="Length">Size of that volume file.</param>
/// <param name="Served">True when the decoder has opened this volume.</param>
/// <param name="Spent">True when the engine considers it reclaimable.</param>
/// <param name="DeletionSafe">
/// False when the decoder rewound inside this volume, which is what makes
/// mid-flight reclamation unsafe for it: the stream can no longer prove that the
/// bytes behind the rewind will not be read again. Independent-volume sets are
/// not reclaimed mid-flight at all any more, but this flag is what explains why
/// the first volumes of a RAR set were the ones that used to survive.
/// </param>
/// <param name="CoveredEnd">
/// Highest archive offset covered by an unbroken read range from 0. For a sliced
/// set this is the merged stream's value (one stream spans every volume); for a
/// RAR set it is that volume's own coverage.
/// </param>
public sealed record VolumeReclaimState(
    int Index,
    string FileName,
    long Length,
    bool Served,
    bool Spent,
    bool DeletionSafe,
    long CoveredEnd);

/// <summary>Outcome of an extraction run.</summary>
public sealed record ExtractionResult(
    int Extracted,
    int Failed,
    long BytesWritten,
    long BytesFreed,
    IReadOnlyList<string> DeletedVolumes,
    IReadOnlyList<string> Errors)
{
    /// <summary>Members that passed a <see cref="ExtractionOptions.TestOnly"/> run.</summary>
    public int Tested { get; init; }

    /// <summary>Members deliberately skipped (already present and Overwrite is off, or filtered out).</summary>
    public int Skipped { get; init; }

    /// <summary>Directories created.</summary>
    public int Directories { get; init; }

    /// <summary>True when the caller cancelled before the archive was finished.</summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// One-line cause of the first failure, in Chinese (e.g. "数据损坏"). Null
    /// when nothing failed. The UI leads with this so a failure never has to be
    /// inferred from a list of per-file symptoms.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>What the user can do about <see cref="FailureReason"/>.</summary>
    public string? FailureHint { get; init; }

    /// <summary>
    /// The handler asked for a password at some point - either because the
    /// archive is encrypted and none was supplied, or because the one we supplied
    /// was wrong. The caller uses this to prompt and retry; the engine itself
    /// never asks.
    /// </summary>
    public bool NeedsPassword { get; init; }

    /// <summary>The failure is specifically "this password is wrong".</summary>
    public bool WrongPassword { get; init; }

    public bool Ok => Failed == 0 && Errors.Count == 0 && !Cancelled;
}

/// <summary>
/// Raised while opening an archive whose *header* is encrypted (7z with "encrypt
/// file names") and no usable password is available - the handler cannot even
/// list the members, so there is nothing to extract and no per-entry result to
/// report. Callers should prompt and retry, not treat it as a corrupt file.
/// </summary>
public sealed class PasswordRequiredException : Exception
{
    public PasswordRequiredException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>True when a password was supplied, so this is a retry after a wrong one.</summary>
    public bool WasSupplied { get; init; }
}
