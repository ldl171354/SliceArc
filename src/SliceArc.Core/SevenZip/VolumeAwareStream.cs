using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SliceArc.Core.Volumes;

namespace SliceArc.Core.SevenZip;

/// <summary>
/// IStreamGetSize (group 3, sub 6). 7-Zip queries this to learn the logical
/// stream length up front; answering it lets the decoder skip its own probing.
/// </summary>
[ComVisible(true)]
[Guid("23170F69-40C1-278A-0000-000300060000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IStreamGetSize
{
    [PreserveSig]
    int GetSize(IntPtr size);
}

/// <summary>
/// Presents a set of physical volume files as one contiguous archive stream,
/// and records exactly how far into that stream the decoder has read.
///
/// This class is the linchpin of the whole product:
///   * 7-Zip never opens a file itself - it only ever calls Read/Seek here, so
///     every byte it consumes is accounted for against a specific volume.
///   * That accounting is what makes "delete each volume as it is spent"
///     possible without parsing a single archive header ourselves.
///
/// Deletion safety rule (see <see cref="IsVolumeSpent"/>):
///   a volume is spent once the merged set of ranges the decoder has read
///   covers [0, volumeEnd) - i.e. every byte before that volume's end has been
///   consumed at least once. A high-water mark cannot express this: 7-Zip seeks
///   to the archive tail during Open to find the header, so the mark jumps to
///   EOF while the leading volumes are still untouched.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed unsafe class VolumeAwareStream : IInStream, IStreamGetSize, IDisposable
{
    private readonly VolumeGroup _group;
    private readonly SafeFileHandle?[] _handles;
    private readonly long[] _volumeReadEnd;

    private long _position;
    private long _highWaterMark;
    private long _lowWaterMark = -1;
    private long _sequentialEnd = -1;

    /// <summary>
    /// Merged set of archive byte ranges the decoder has read at least once,
    /// accumulated across Open and Extract. A volume is finished with only when
    /// [0, volumeEnd) is covered - which correctly credits the tail read Open
    /// performs while still refusing to credit a volume whose data region has
    /// not been touched.
    /// </summary>
    private readonly List<(long Start, long End)> _covered = new();
    private long _bytesRead;
    private long _seekCount;
    private long _backSeekCount;
    private bool _disposed;

    // Diagnostic counters: prove the CCW is actually being called through the
    // right vtable slots when 7z.dll opens the archive.
    private static int _readCallCount;
    private static int _seekCallCount;
    private static int _sizeCallCount;

    /// <summary>
    /// When true the first few callbacks are traced to stderr. Off by default so
    /// the GUI never spams a console; the probe turns it on.
    /// </summary>
    public static bool Diagnostics { get; set; }

    public static int ReadCallCount => _readCallCount;
    public static int SeekCallCount => _seekCallCount;
    public static int SizeCallCount => _sizeCallCount;
    public static void ResetCallCounters()
    {
        _readCallCount = 0;
        _seekCallCount = 0;
        _sizeCallCount = 0;
    }

    public VolumeAwareStream(VolumeGroup group)
    {
        _group = group ?? throw new ArgumentNullException(nameof(group));
        _handles = new SafeFileHandle?[group.Volumes.Count];
        _volumeReadEnd = new long[group.Volumes.Count];
    }

    /// <summary>Wrap a single ordinary file as a one-volume set.</summary>
    public static VolumeAwareStream ForSingleFile(string path)
    {
        var full = Path.GetFullPath(path);
        var length = new FileInfo(full).Length;
        var one = new VolumeInfo(full, 0, length, 0);
        return new VolumeAwareStream(
            new VolumeGroup(Path.GetFileName(full), VolumeStyle.None, new[] { one }, true));
    }

    public VolumeGroup Group => _group;
    public long Position => _position;
    public long TotalBytes => _group.TotalBytes;
    public long BytesRead => _bytesRead;
    public long SeekCount => _seekCount;
    public long BackSeekCount => _backSeekCount;

    /// <summary>Highest archive offset the decoder has ever read up to.</summary>
    public long HighWaterMark => _highWaterMark;

    /// <summary>
    /// End of the unbroken sequential run consumed since
    /// <see cref="BeginExtractionTracking"/>; -1 before the first read. This -
    /// not the high-water mark - is what decides whether a volume is finished.
    /// </summary>
    public long SequentialEnd => _sequentialEnd;

    /// <summary>
    /// Lowest offset seeked back to since the last <see cref="BeginExtractionTracking"/>,
    /// or -1 when no rewind happened.
    /// </summary>
    public long LowWaterMark => _lowWaterMark;

    /// <summary>
    /// True when the decoder has been strictly sequential since tracking began.
    /// A single rewind disables automatic deletion, because we can no longer
    /// prove which volumes are finished with.
    /// </summary>
    public bool IsDeletionSafe => _backSeekCount == 0 && !_disposed;

    // ---------------------------------------------------------------------
    // ISequentialInStream / IInStream
    // ---------------------------------------------------------------------

    /// <inheritdoc />
    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
        var callNo = Interlocked.Increment(ref _readCallCount);
        if (_disposed) return SevenZipNative.HResult.E_FAIL;
        if (size == 0 || data == IntPtr.Zero) return SevenZipNative.HResult.S_OK;

        var readStart = _position;
        var span = new Span<byte>((void*)data, (int)Math.Min(size, int.MaxValue));
        long remaining = size;
        long written = 0;

        while (remaining > 0)
        {
            var volIndex = _group.VolumeIndexAt(_position);
            if (volIndex >= _group.Volumes.Count) break;   // clean EOF

            var vol = _group.Volumes[volIndex];
            var local = _position - vol.ArchiveOffset;

            if (local >= vol.Length)
            {
                // Straddling a boundary: jump to the start of the next volume.
                if (volIndex + 1 >= _group.Volumes.Count) break;
                _position = _group.Volumes[volIndex + 1].ArchiveOffset;
                continue;
            }

            var want = (int)Math.Min(remaining, vol.Length - local);
            int got;
            try
            {
                got = RandomAccess.Read(GetHandle(volIndex), span.Slice((int)written, want), local);
            }
            catch (IOException)
            {
                // Surface a short read rather than an exception across the COM boundary.
                break;
            }

            if (got <= 0) break;

            written += got;
            remaining -= got;
            _position += got;
            _bytesRead += got;

            if (_position > _highWaterMark) _highWaterMark = _position;

            var readEnd = local + got;
            if (readEnd > _volumeReadEnd[volIndex]) _volumeReadEnd[volIndex] = readEnd;
        }

        if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, (int)written);

        // Sequential-coverage accounting. Opening a 7z archive jumps to the tail
        // to read the header, and that must not count as "volume consumed": only
        // a read that continues an unbroken run from the previous one advances
        // the mark. This is why HighWaterMark is the wrong signal and
        // SequentialEnd is the right one.
        if (written > 0)
        {
            if (_sequentialEnd < 0) _sequentialEnd = _position;
            else if (readStart <= _sequentialEnd) _sequentialEnd = Math.Max(_sequentialEnd, _position);

            MarkCovered(readStart, _position);
        }

        if (Diagnostics && callNo <= 4)
        {
            var n = (int)Math.Min(written, 16);
            var head = n > 0
                ? Convert.ToHexString(new ReadOnlySpan<byte>((void*)data, n))
                : "(none)";
            Console.Error.WriteLine(
                $"[CCW] Read #{callNo} req={size} got={written} endPos={_position} head={head}");
        }

        return SevenZipNative.HResult.S_OK;
    }

    /// <inheritdoc />
    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, 0);
        var callNo = Interlocked.Increment(ref _seekCallCount);
        if (Diagnostics && callNo <= 3)
            Console.Error.WriteLine($"[CCW] Seek #{callNo} off={offset} origin={seekOrigin} pos={_position}");
        if (_disposed) return SevenZipNative.HResult.E_FAIL;

        long target = seekOrigin switch
        {
            SevenZipNative.SeekOrigin7z.Begin => offset,
            SevenZipNative.SeekOrigin7z.Current => _position + offset,
            SevenZipNative.SeekOrigin7z.End => _group.TotalBytes + offset,
            _ => long.MinValue,
        };

        if (target == long.MinValue) return SevenZipNative.HResult.E_INVALIDARG;
        if (target < 0) return SevenZipNative.HResult.E_NEGATIVE_SEEK;   // 7-Zip's expected code

        _seekCount++;
        if (target < _position)
        {
            _backSeekCount++;
            if (_lowWaterMark < 0 || target < _lowWaterMark) _lowWaterMark = target;
        }

        _position = target;
        if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, target);
        return SevenZipNative.HResult.S_OK;
    }

    /// <summary>IStreamGetSize::GetSize - the logical length of the whole set.</summary>
    public int GetSize(IntPtr size)
    {
        var callNo = Interlocked.Increment(ref _sizeCallCount);
        if (Diagnostics && callNo <= 3)
            Console.Error.WriteLine($"[CCW] GetSize #{callNo}");
        if (size != IntPtr.Zero) Marshal.WriteInt64(size, _group.TotalBytes);
        return SevenZipNative.HResult.S_OK;
    }

    // ---------------------------------------------------------------------
    // Consumption tracking and volume lifecycle
    // ---------------------------------------------------------------------

    /// <summary>
    /// Arm tracking for the extraction phase. Call this once Open() has finished,
    /// so that the tail-seek 7-Zip performs while hunting for the central
    /// directory does not poison the accounting.
    /// </summary>
    public void BeginExtractionTracking()
    {
        // Deliberately does NOT clear _covered: the ranges Open() already read
        // are legitimately consumed and must count toward volume completion.
        _highWaterMark = _position;
        _lowWaterMark = -1;
        _backSeekCount = 0;
        _sequentialEnd = -1;
        Array.Clear(_volumeReadEnd);
    }

    /// <summary>
    /// Throw away accumulated coverage.
    ///
    /// Legal only between handler attempts during Open, before any extraction has
    /// started: a handler that 7z.dll rejects may still have read the whole
    /// stream, and keeping that coverage would mark every volume spent - deleting
    /// them on that basis would destroy the archive. Once a handler is accepted
    /// this must never be called again.
    /// </summary>
    public void ResetCoverage()
    {
        _covered.Clear();
        _highWaterMark = _position;
        _lowWaterMark = -1;
        _backSeekCount = 0;
        _sequentialEnd = -1;
        Array.Clear(_volumeReadEnd);
    }

    /// <summary>
    /// Can volume <paramref name="index"/> be deleted without breaking the
    /// remainder of the extraction?
    /// </summary>
    public bool IsVolumeSpent(int index)
    {
        if (_disposed) return false;
        if (index < 0 || index >= _group.Volumes.Count) return false;

        // A volume is finished with when every archive byte from 0 up to its end
        // has been read at least once. Tracking a merged range set rather than a
        // high-water mark makes this correct in both directions:
        //   * Open() reads the trailing header, which a high-water mark would
        //     mistake for "everything consumed";
        //   * Open() also reads bytes 0..31 (the signature), which a
        //     "largest offset seen" rule would ignore entirely;
        //   * the last volume's data region and its header region are read in
        //     two separate passes, and only the union of both is complete.
        var end = _group.VolumeEnd(index);
        foreach (var (start, coveredEnd) in _covered)
        {
            if (start <= 0 && coveredEnd >= end) return true;
        }
        return false;
    }

    /// <summary>Highest archive offset covered by an unbroken read range from 0.</summary>
    public long CoveredEnd
    {
        get
        {
            foreach (var (start, end) in _covered)
            {
                if (start <= 0) return end;
            }
            return 0;
        }
    }

    /// <summary>Merge [start, end) into the covered-range set.</summary>
    private void MarkCovered(long start, long end)
    {
        if (end <= start) return;

        var i = 0;
        while (i < _covered.Count && _covered[i].End < start) i++;

        var newStart = start;
        var newEnd = end;
        while (i < _covered.Count && _covered[i].Start <= newEnd)
        {
            if (_covered[i].Start < newStart) newStart = _covered[i].Start;
            if (_covered[i].End > newEnd) newEnd = _covered[i].End;
            _covered.RemoveAt(i);
        }
        _covered.Insert(i, (newStart, newEnd));
    }

    /// <summary>How many bytes of volume <paramref name="index"/> have been read at least once.</summary>
    public long VolumeBytesRead(int index) =>
        index >= 0 && index < _volumeReadEnd.Length ? _volumeReadEnd[index] : 0;

    /// <summary>
    /// Release the OS handle for a volume. On Windows a file cannot be fully
    /// reclaimed while a handle is open, so this must run before the delete.
    /// </summary>
    public void ReleaseVolumeHandle(int index)
    {
        if (index < 0 || index >= _handles.Length) return;
        var handle = _handles[index];
        if (handle is null) return;
        _handles[index] = null;
        handle.Dispose();
    }

    private SafeFileHandle GetHandle(int index)
    {
        var existing = _handles[index];
        if (existing is not null) return existing;

        // FileShare.Delete lets the file be unlinked while other readers still
        // hold it; FileShare.ReadWrite keeps us friendly to a second instance.
        //
        // No RandomAccess hint: extraction reads the archive front to back, and
        // FILE_FLAG_RANDOM_ACCESS tells the cache manager to switch off
        // read-ahead, which turns every 7z.dll read into a synchronous disk
        // round-trip. Default caching lets the prefetcher stay ahead of the
        // decoder - measured ~10% of total extraction time on a large archive.
        var handle = File.OpenHandle(
            _group.Volumes[index].Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        _handles[index] = handle;
        return handle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = 0; i < _handles.Length; i++) ReleaseVolumeHandle(i);
    }
}
