namespace SliceArc.Core.Extraction;

/// <summary>
/// Bounded blocking byte queue: the decoder thread writes, the consumer reads.
///
/// Backpressure is the whole point. 7z.dll decodes as fast as the CPU allows and
/// cannot be paused mid-member, so the only way to serve a slow reader (the
/// shell's copy engine, a few hundred KB at a time) without buffering a 40 GB
/// file in RAM is to block the decoder thread when the window is full. That is
/// exactly what <see cref="Write"/> does.
/// </summary>
internal sealed class ByteQueue
{
    /// <summary>Matches 7-Zip's own read chunk (see NOTES-7z-com-interop §4).</summary>
    private const int ChunkSize = 256 * 1024;

    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly long _limit;

    private byte[]? _head;
    private int _headOffset;
    private long _buffered;
    private bool _completed;
    private Exception? _error;

    public ByteQueue(long limit) => _limit = Math.Max(ChunkSize, limit);

    /// <summary>Producer side. Blocks while the buffer is full; returns early if abandoned.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            lock (_gate)
            {
                while (!_completed && _buffered >= _limit) Monitor.Wait(_gate);
                if (_completed) return;

                var room = _limit - _buffered;
                var take = (int)Math.Min(Math.Min(ChunkSize, room), data.Length - offset);
                if (take <= 0) continue;

                var chunk = new byte[take];
                data.Slice(offset, take).CopyTo(chunk);
                _chunks.Enqueue(chunk);
                _buffered += take;
                offset += take;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Consumer side. Blocks until data arrives; returns 0 only at end of stream.</summary>
    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            while (_buffered == 0 && !_completed) Monitor.Wait(_gate);

            if (_buffered == 0)
            {
                if (_error is not null) throw new IOException("读取归档条目失败。", _error);
                return 0;
            }

            if (_head is null)
            {
                _head = _chunks.Dequeue();
                _headOffset = 0;
            }

            var available = _head.Length - _headOffset;
            var take = Math.Min(available, destination.Length);
            _head.AsSpan(_headOffset, take).CopyTo(destination);
            _headOffset += take;
            _buffered -= take;

            if (_headOffset >= _head.Length)
            {
                _head = null;
                _headOffset = 0;
            }

            Monitor.PulseAll(_gate);
            return take;
        }
    }

    /// <summary>Producer side: mark the stream finished, optionally with a failure.</summary>
    public void Complete(Exception? error = null)
    {
        lock (_gate)
        {
            _completed = true;
            _error = error;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Consumer side: stop caring. Unblocks the producer so decoding stops.</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _completed = true;
            _chunks.Clear();
            _head = null;
            _headOffset = 0;
            _buffered = 0;
            Monitor.PulseAll(_gate);
        }
    }
}
