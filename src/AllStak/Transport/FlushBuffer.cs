using Microsoft.Extensions.Logging;

namespace AllStak.Transport;

/// <summary>
/// Bounded ring buffer with a background timer thread that periodically
/// drains the buffer and calls the supplied flush delegate.
/// </summary>
internal sealed class FlushBuffer<T> : IDisposable
{
    private readonly Func<IReadOnlyList<T>, Task> _flush;
    private readonly int _maxSize;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly Queue<T> _queue;
    private readonly ILogger _logger;
    private readonly string _name;
    private bool _overflowWarned;
    private int _flushing;  // interlocked
    private bool _disposed;
    private long _droppedCount;

    public int Count { get { lock (_lock) return _queue.Count; } }
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public FlushBuffer(string name, int maxSize, int intervalMs, Func<IReadOnlyList<T>, Task> flush, ILogger logger)
    {
        _name = name;
        _maxSize = maxSize;
        _queue = new Queue<T>(maxSize);
        _flush = flush;
        _logger = logger;
        _timer = new Timer(_ => _ = FlushAsync(), null, intervalMs, intervalMs);
    }

    public void Push(T item)
    {
        lock (_lock)
        {
            if (_queue.Count >= _maxSize)
            {
                _queue.Dequeue(); // drop oldest
                Interlocked.Increment(ref _droppedCount);
                if (!_overflowWarned)
                {
                    _overflowWarned = true;
                    _logger.LogWarning(
                        "[AllStak] Buffer {Name} is full ({Max} items); oldest events are being dropped.",
                        _name, _maxSize);
                }
            }
            else
            {
                _overflowWarned = false;
            }
            _queue.Enqueue(item);
        }

        if (Count >= _maxSize * 0.8)
            _ = FlushAsync();
    }

    public async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
            return;
        try
        {
            List<T> drained;
            lock (_lock)
            {
                if (_queue.Count == 0) return;
                drained = new List<T>(_queue);
                _queue.Clear();
            }
            try
            {
                await _flush(drained).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[AllStak] flush error in {Name}", _name);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        try { FlushAsync().GetAwaiter().GetResult(); } catch { }
    }
}
