using System.Diagnostics;
using System.IO;

namespace SliceArc.Core.Extraction;

/// <summary>Progress of one entry being streamed out of an archive.</summary>
/// <param name="EntryPath">Archive path of the member being read.</param>
/// <param name="BytesRead">Bytes handed to the consumer so far.</param>
/// <param name="TotalBytes">Declared size of the member.</param>
public readonly record struct EntryStreamProgress(string EntryPath, long BytesRead, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesRead / TotalBytes, 0, 1) : 0;
}

/// <summary>
/// Pull-based reader for a single archive member, fed by a background extraction
/// through a bounded buffer.
///
/// This is the piece that makes drag-out possible: the shell's copy engine calls
/// <see cref="Read"/> and writes the bytes straight into the destination file it
/// created, while a worker thread decodes the member and blocks whenever the
/// window fills up. Nothing ever lands in %TEMP%.
///
/// The reader is single-consumer. Forward seeks are served by draining; a
/// backward seek restarts the decode, which is correct but only reachable if the
/// drop target rewinds - the shell's own copy loop reads straight through.
/// </summary>
public sealed class ArchiveEntryReader : IDisposable
{
    /// <summary>How much decoded data may sit between the decoder and the consumer.</summary>
    public const long DefaultBufferLimit = 8L * 1024 * 1024;

    private readonly string _archivePath;
    private readonly string _entryPath;
    private readonly long _bufferLimit;
    private readonly byte[] _scratch = new byte[64 * 1024];
    private readonly Action<EntryStreamProgress>? _progress;
    private readonly Stopwatch _progressClock = Stopwatch.StartNew();

    private ByteQueue _queue;
    private CancellationTokenSource _cts = new();
    private Task? _producer;
    private long _position;
    private long _reported;
    private bool _disposed;
    private Exception? _failure;

    public ArchiveEntryReader(
        string archivePath,
        string entryPath,
        long length,
        Action<EntryStreamProgress>? progress = null,
        long bufferLimit = DefaultBufferLimit)
    {
        _archivePath = Path.GetFullPath(archivePath);
        _entryPath = entryPath.Replace('\\', '/');
        Length = length;
        _progress = progress;
        _bufferLimit = bufferLimit;
        _queue = new ByteQueue(_bufferLimit);
    }

    /// <summary>Declared size of the member, from the archive header.</summary>
    public long Length { get; }

    /// <summary>Archive path this reader serves.</summary>
    public string EntryPath => _entryPath;

    /// <summary>How many bytes the consumer has taken.</summary>
    public long Position => _position;

    /// <summary>Set when decoding failed; the exception surfaces from <see cref="Read"/>.</summary>
    public Exception? Failure => _failure;

    /// <summary>True once the background decode has stopped (successfully or not).</summary>
    public bool IsFinished => _producer is null || _producer.IsCompleted;

    /// <summary>Start the background decode. Idempotent.</summary>
    public void Start()
    {
        if (_producer is not null || _disposed) return;
        _producer = Task.Run(Produce);
    }

    /// <summary>
    /// Take up to <c>destination.Length</c> bytes. Blocks until data is
    /// available; returns 0 at end of stream; throws when decoding failed.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        if (_disposed || destination.Length == 0) return 0;
        if (_position >= Length) return 0;

        Start();

        var read = _queue.Read(destination);
        _position += read;

        // Throttled: the shell reads in small chunks and a 40 GB member would
        // otherwise flood the UI thread's dispatcher with progress updates.
        if (_progress is not null && read > 0)
        {
            _reported += read;
            if (_reported >= Length || _progressClock.ElapsedMilliseconds >= 100)
            {
                _progressClock.Restart();
                _progress(new EntryStreamProgress(_entryPath, _reported, Length));
            }
        }

        return read;
    }

    /// <summary>Reposition the consumer. Backward seeks restart the decode.</summary>
    public void Seek(long position)
    {
        if (position < 0) throw new IOException($"非法的流位置：{position}。");
        if (position == _position) return;

        if (position > _position)
        {
            Skip(position - _position);
            return;
        }

        Restart();
        Skip(position);
    }

    private void Skip(long count)
    {
        while (count > 0)
        {
            var take = (int)Math.Min(count, _scratch.Length);
            var read = Read(_scratch.AsSpan(0, take));
            if (read <= 0) break;
            count -= read;
        }
    }

    private void Restart()
    {
        _queue.Abandon();
        _cts.Cancel();

        // The old token may still be read by the abandoned extraction thread,
        // so the source is left for the GC rather than disposed here.
        _queue = new ByteQueue(_bufferLimit);
        _cts = new CancellationTokenSource();
        _producer = null;
        _position = 0;
        _reported = 0;
        _failure = null;
        _progressClock.Restart();
        Start();
    }

    private void Produce()
    {
        try
        {
            using var engine = new ExtractionEngine(_archivePath);
            var result = engine.Extract(
                new ExtractionOptions
                {
                    OutputDirectory = Path.GetDirectoryName(_archivePath) ?? Environment.CurrentDirectory,
                    IncludePaths = new[] { _entryPath },
                    // Never reclaim volumes for a streamed read: a drag-out takes
                    // one member out of the set and the user may well want the
                    // next one from the same volumes a minute later.
                    DeleteSpentVolumes = false,
                    OutputFactory = _ => new QueueStream(_queue),
                    CancellationToken = _cts.Token,
                });

            if (result.Cancelled)
            {
                _queue.Complete();
                return;
            }

            if (result.Extracted == 0)
            {
                var reason = result.Errors.Count > 0 ? result.Errors[0] : "条目没有产出任何数据";
                _failure = new IOException(reason);
                _queue.Complete(_failure);
                return;
            }

            _queue.Complete();
        }
        catch (Exception ex)
        {
            _failure = ex;
            _queue.Complete(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _queue.Abandon();

        // Deliberately not waited on: the producer exits on its own once the
        // cancelled token reaches the next 7z.dll callback, and the UI thread
        // must not block here (this runs right after a drag ends).
    }

    /// <summary>
    /// Write-only adapter handed to the engine as its output. Disposal is a
    /// no-op on purpose: the reader must keep serving buffered bytes until the
    /// producer reports the final outcome, not the moment the member ends.
    /// </summary>
    private sealed class QueueStream : Stream
    {
        private readonly ByteQueue _queue;

        public QueueStream(ByteQueue queue) => _queue = queue;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            _queue.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => _queue.Write(buffer);

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
