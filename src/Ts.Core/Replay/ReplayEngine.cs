using Ts.Core.Recording;
using Ts.Core.Time;

namespace Ts.Core.Replay;

/// <summary>Receives a replayed frame, together with the clock instant it was released at.</summary>
public delegate void ReplaySink(TsrRecord record, long clockMicros);

/// <summary>
/// Plays a recording back at its original pace, optionally faster or slower.
///
/// Timing is anchored to absolute record timestamps rather than accumulated per-record intervals,
/// so a slow consumer or a coarse platform timer costs one late frame instead of a timeline that
/// drifts further out with every record.
///
/// The engine emits records; it does not decode them. Replayed bytes therefore travel the same
/// path as live ones, which is the only way a replay can be trusted to show what the capture saw.
/// </summary>
public sealed class ReplayEngine
{
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 10.0;

    private readonly IClock _clock;
    private double _speed = 1.0;

    public ReplayEngine(IClock clock, double speed = 1.0)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Speed = speed;
    }

    /// <summary>
    /// Playback rate, 0.1x to 10x. Setting it during a run takes effect at the next record and
    /// keeps the current position — the playhead does not jump.
    /// </summary>
    public double Speed
    {
        get => Volatile.Read(ref _speed);
        set
        {
            if (double.IsNaN(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Speed must be a number.");
            }

            Volatile.Write(ref _speed, Math.Clamp(value, MinSpeed, MaxSpeed));
        }
    }

    /// <summary>Index of the next record to emit.</summary>
    public int Position { get; private set; }

    /// <summary>Timestamp of the most recently emitted record, in recording time.</summary>
    public long CurrentRecordMicros { get; private set; }

    /// <summary>
    /// Emits records from <paramref name="startIndex"/> onwards, waiting between them so the
    /// original intervals are reproduced divided by <see cref="Speed"/>.
    /// </summary>
    /// <returns>The number of records emitted.</returns>
    public async Task<int> RunAsync(
        IReadOnlyList<TsrRecord> records,
        ReplaySink sink,
        int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sink);

        if (startIndex < 0 || (startIndex > 0 && startIndex > records.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), startIndex, "Outside the recording.");
        }

        Position = startIndex;
        if (startIndex >= records.Count)
        {
            return 0;
        }

        var speed = Speed;
        var anchorClock = _clock.NowMicros;
        var anchorRecord = records[startIndex].TimestampMicros;
        var emitted = 0;

        for (var i = startIndex; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = Speed;
            if (current != speed)
            {
                // Re-anchor on the playhead as it stands right now, so changing speed shifts the
                // pace from here on without replaying or skipping anything.
                var now = _clock.NowMicros;
                anchorRecord += (long)((now - anchorClock) * speed);
                anchorClock = now;
                speed = current;
            }

            var record = records[i];
            var target = anchorClock + (long)((record.TimestampMicros - anchorRecord) / speed);

            await _clock.DelayUntilAsync(target, cancellationToken).ConfigureAwait(false);

            sink(record, _clock.NowMicros);

            Position = i + 1;
            CurrentRecordMicros = record.TimestampMicros;
            emitted++;
        }

        return emitted;
    }

    /// <summary>
    /// Index of the first record at or after <paramref name="recordMicros"/>. Used to seek without
    /// the caller having to know how the timeline is stored.
    /// </summary>
    public static int IndexAt(IReadOnlyList<TsrRecord> records, long recordMicros)
    {
        ArgumentNullException.ThrowIfNull(records);

        var low = 0;
        var high = records.Count;

        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (records[mid].TimestampMicros < recordMicros)
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
}
