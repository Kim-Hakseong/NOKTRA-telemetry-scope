using Ts.Core.Decoding;

namespace Ts.Core.Analysis;

/// <summary>
/// A fixed-capacity ring of timestamped samples for one channel.
///
/// A live scope runs for hours; keeping every sample would trade a bounded memory footprint for an
/// unbounded one to show data that scrolled off the screen long ago. The recording on disk is the
/// archive — this is only what is on screen and just behind it.
///
/// Samples are appended in time order, which is what lets a window be found by binary search
/// instead of a scan.
/// </summary>
public sealed class SampleBuffer
{
    private readonly long[] _times;
    private readonly double[] _values;
    private readonly SampleStatus[] _statuses;
    private int _head;

    public SampleBuffer(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        Capacity = capacity;
        _times = new long[capacity];
        _values = new double[capacity];
        _statuses = new SampleStatus[capacity];
    }

    public int Capacity { get; }

    public int Count { get; private set; }

    /// <summary>Samples dropped off the back since the last <see cref="Clear"/>.</summary>
    public long Evicted { get; private set; }

    public long OldestMicros => Count == 0 ? 0 : TimeAt(0);

    public long NewestMicros => Count == 0 ? 0 : TimeAt(Count - 1);

    public double Latest => Count == 0 ? double.NaN : ValueAt(Count - 1);

    public SampleStatus LatestStatus => Count == 0 ? SampleStatus.Missing : StatusAt(Count - 1);

    public long TimeAt(int index) => _times[Physical(index)];

    public double ValueAt(int index) => _values[Physical(index)];

    public SampleStatus StatusAt(int index) => _statuses[Physical(index)];

    public void Add(long timeMicros, double value, SampleStatus status)
    {
        _times[_head] = timeMicros;
        _values[_head] = value;
        _statuses[_head] = status;

        _head = (_head + 1) % Capacity;

        if (Count < Capacity)
        {
            Count++;
        }
        else
        {
            Evicted++;
        }
    }

    public void Clear()
    {
        _head = 0;
        Count = 0;
        Evicted = 0;
    }

    /// <summary>Index of the first sample at or after <paramref name="timeMicros"/>.</summary>
    public int LowerBound(long timeMicros)
    {
        var low = 0;
        var high = Count;

        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (TimeAt(mid) < timeMicros)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int Physical(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Outside the buffer.");
        }

        // The oldest live sample sits just after the write head once the ring has wrapped.
        var start = Count == Capacity ? _head : 0;
        return (start + index) % Capacity;
    }
}
