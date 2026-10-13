namespace SliceArc.Core.Compression;

/// <summary>
/// What the caller wants the "add to an existing archive" pipeline to do. The
/// format is not chosen here - it is detected from <see cref="ArchivePath"/>
/// (a 7z.dll handler whose <c>kUpdate</c> capability is true); unsupported
/// formats surface as <see cref="UnsupportedArchiveUpdateException"/>.
/// </summary>
public sealed record ArchiveUpdateOptions
{
    /// <summary>The existing archive to update. Must be a single-file archive.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Files and/or directories to add. Directories are walked recursively.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>0..9. Format-specific; ignored by tar/wim.</summary>
    public int Level { get; init; } = 5;

    /// <summary>Codec name ("LZMA", "Deflate", ...). Null = handler default.</summary>
    public string? Method { get; init; }

    /// <summary>Thread count. 0 = let 7z.dll decide.</summary>
    public int Threads { get; init; }

    /// <summary>Encrypt file names too (7z only) when re-writing the archive.</summary>
    public bool EncryptHeaders { get; init; }

    /// <summary>Optional password; also used to open an encrypted archive.</summary>
    public string? Password { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

public sealed record ArchiveUpdateResult(
    int ItemsAdded,
    int ItemsUpdated,
    int ItemsKept,
    long BytesWritten,
    IReadOnlyList<string> Errors)
{
    public bool Cancelled { get; init; }
    public bool Ok => Errors.Count == 0 && !Cancelled;
}

/// <summary>
/// Raised when the target archive's format cannot be updated - either the
/// handler is read-only (rar, gz/bz2/xz/zst single-stream) or the archive is a
/// multi-volume set. Carries a user-facing reason and an actionable hint, which
/// the App renders in the "原因 / 建议" panel of ResultWindow.
/// </summary>
public sealed class UnsupportedArchiveUpdateException : Exception
{
    public UnsupportedArchiveUpdateException(string message, string? hint = null, Exception? inner = null,
        bool passwordRequired = false)
        : base(message, inner)
    {
        Hint = hint;
        PasswordRequired = passwordRequired;
    }

    public string? Hint { get; }

    /// <summary>True when the archive is encrypted and needs a password to be updated.</summary>
    public bool PasswordRequired { get; }
}
