namespace Ts.Core.Pipeline;

/// <summary>A frame as received: when it arrived, and the bytes.</summary>
public readonly record struct CapturedFrame(long TimeMicros, byte[] Bytes);

/// <summary>
/// The bounded hand-off between whoever is receiving bytes and whoever is drawing them.
///
/// Receivers must never block: a socket that stops reading loses datagrams the operating system
/// has already accepted, and a serial port that stops reading overruns. So the queue has a ceiling
/// and drops the *oldest* frame when it is reached — on a scope, the newest data is the data
/// someone is looking at.
///
/// Drops are counted rather than swallowed. A number on screen saying frames were lost is worth
/// far more than a chart that silently shows less than it received.
/// </summary>
public sealed class FrameQueue
{
    private readonly Queue<CapturedFrame> _queue;
    private readonly int _capacity;
    private readonly object _gate = new();

    public FrameQueue(int capacity = 1 << 16)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _capacity = capacity;
        _queue = new Queue<CapturedFrame>(Math.Min(capacity, 1024));
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Frames discarded because the consumer could not keep up.</summary>
    public long Dropped { get; private set; }

    public void Enqueue(long timeMicros, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        lock (_gate)
        {
            while (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                Dropped++;
            }

            _queue.Enqueue(new CapturedFrame(timeMicros, bytes));
        }
    }

    public bool TryDequeue(out CapturedFrame frame)
    {
        lock (_gate)
        {
            return _queue.TryDequeue(out frame);
        }
    }

    /// <summary>
    /// Moves up to <paramref name="max"/> frames into <paramref name="destination"/>. The cap
    /// exists so a burst cannot hold the UI thread for a whole second trying to catch up in one go.
    /// </summary>
    public int DrainTo(List<CapturedFrame> destination, int max)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (_gate)
        {
            var taken = 0;
            while (taken < max && _queue.TryDequeue(out var frame))
            {
                destination.Add(frame);
                taken++;
            }

            return taken;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            Dropped = 0;
        }
    }
}
