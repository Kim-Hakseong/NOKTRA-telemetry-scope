using System.Diagnostics;

namespace Ts.Core.Time;

/// <summary>
/// The only source of time in the core.
///
/// Every schedule-sensitive component takes one of these rather than reading the wall clock, which
/// is what lets the replay vectors assert exact emission instants without a single real sleep.
/// </summary>
public interface IClock
{
    /// <summary>Microseconds since an arbitrary, fixed origin. Monotonic.</summary>
    long NowMicros { get; }

    /// <summary>
    /// Completes once <see cref="NowMicros"/> has reached <paramref name="targetMicros"/>.
    /// Returns immediately when the target is already past, so a late consumer catches up rather
    /// than drifting further behind.
    /// </summary>
    Task DelayUntilAsync(long targetMicros, CancellationToken cancellationToken);
}

/// <summary>Real time, measured monotonically so a wall-clock adjustment cannot move it backwards.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>
    /// Below this, waiting costs more than it buys: the platform timer cannot resolve it, and the
    /// UI coalesces updates far more coarsely anyway. Records due inside the same quantum are
    /// emitted together, which keeps the timeline exact in aggregate even when each individual
    /// wait is not.
    /// </summary>
    private const long TimerResolutionMicros = 1000;

    private readonly Stopwatch _watch = Stopwatch.StartNew();

    public static SystemClock Instance { get; } = new();

    public long NowMicros => _watch.Elapsed.Ticks / 10;

    /// <summary>Microseconds since the Unix epoch. Used to stamp the start of a recording.</summary>
    public static long UnixNowMicros => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10;

    public async Task DelayUntilAsync(long targetMicros, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = targetMicros - NowMicros;
            if (remaining <= TimerResolutionMicros)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay((int)(remaining / 1000), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A clock that jumps straight to whatever it is asked to wait for, recording the instants it
/// passed through.
///
/// Replay timing is then testable as data: run the engine, read back the emission times, compare
/// with the vector. No sleeping, no tolerance windows, no flakiness.
/// </summary>
public sealed class VirtualClock : IClock
{
    private readonly List<long> _waits = new();

    public VirtualClock(long startMicros = 0) => NowMicros = startMicros;

    public long NowMicros { get; private set; }

    /// <summary>Every instant the clock was advanced to, in order.</summary>
    public IReadOnlyList<long> Waits => _waits;

    public Task DelayUntilAsync(long targetMicros, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _waits.Add(targetMicros);
        if (targetMicros > NowMicros)
        {
            NowMicros = targetMicros;
        }

        return Task.CompletedTask;
    }

    /// <summary>Moves the clock forward by hand, for tests that drive something other than a wait.</summary>
    public void Advance(long micros)
    {
        if (micros < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(micros), micros, "Time does not run backwards.");
        }

        NowMicros += micros;
    }
}
