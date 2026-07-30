using Ts.Core.Recording;
using Ts.Core.Replay;
using Ts.Core.Time;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>
/// The frozen replay vector. Every assertion here runs on a virtual clock, so the suite makes no
/// real-time claim it would have to sleep to justify.
/// </summary>
public class ReplayVectorTests
{
    private const long Ms = 1000;

    private static List<TsrRecord> Records(params long[] millisecondStamps)
        => millisecondStamps
            .Select((stamp, index) => new TsrRecord(stamp * Ms, new[] { (byte)index }))
            .ToList();

    private static (List<long> ClockMicros, List<TsrRecord> Emitted) Run(
        IReadOnlyList<TsrRecord> records, double speed, VirtualClock? clock = null)
    {
        var times = new List<long>();
        var emitted = new List<TsrRecord>();

        var engine = new ReplayEngine(clock ?? new VirtualClock(), speed);
        var task = engine.RunAsync(records, (record, now) =>
        {
            emitted.Add(record);
            times.Add(now);
        });

        Assert.True(task.IsCompletedSuccessfully);
        return (times, emitted);
    }

    [Fact]
    public void Records_At_0_100_250ms_AtDoubleSpeed_EmitAt_0_50_125ms()
    {
        var (times, emitted) = Run(Records(0, 100, 250), speed: 2.0);

        Assert.Equal(new[] { 0L, 50 * Ms, 125 * Ms }, times);
        Assert.Equal(3, emitted.Count);
    }

    [Fact]
    public void AtOriginalSpeed_TheTimelineIsReproducedExactly()
    {
        var (times, _) = Run(Records(0, 100, 250), speed: 1.0);

        Assert.Equal(new[] { 0L, 100 * Ms, 250 * Ms }, times);
    }

    [Fact]
    public void AtOneTenthSpeed_TheTimelineStretchesTenfold()
    {
        var (times, _) = Run(Records(0, 100, 250), speed: 0.1);

        Assert.Equal(new[] { 0L, 1000 * Ms, 2500 * Ms }, times);
    }

    [Fact]
    public void ReplayStartsFromTheClocksCurrentTimeNotFromZero()
    {
        var clock = new VirtualClock(startMicros: 7_000_000);

        var (times, _) = Run(Records(0, 100, 250), speed: 2.0, clock);

        Assert.Equal(new[] { 7_000_000L, 7_050_000, 7_125_000 }, times);
    }

    [Fact]
    public void ARecordingThatDoesNotStartAtZeroIsAnchoredOnItsFirstRecord()
    {
        var (times, _) = Run(Records(500, 600, 750), speed: 2.0);

        Assert.Equal(new[] { 0L, 50 * Ms, 125 * Ms }, times);
    }

    [Fact]
    public void SpeedIsClampedToTheSupportedRange()
    {
        var engine = new ReplayEngine(new VirtualClock());

        engine.Speed = 100;
        Assert.Equal(ReplayEngine.MaxSpeed, engine.Speed);

        engine.Speed = 0.0001;
        Assert.Equal(ReplayEngine.MinSpeed, engine.Speed);
    }

    [Fact]
    public void ChangingSpeedMidRunKeepsThePlayheadAndRepacesFromThere()
    {
        var clock = new VirtualClock();
        var engine = new ReplayEngine(clock, speed: 1.0);
        var times = new List<long>();

        var records = Records(0, 100, 200, 300);

        var task = engine.RunAsync(records, (_, now) =>
        {
            times.Add(now);

            // Halve the pace once the second record is out.
            if (times.Count == 2)
            {
                engine.Speed = 0.5;
            }
        });

        Assert.True(task.IsCompletedSuccessfully);

        // First two at 1x: 0, 100ms. Then 100ms of recording time takes 200ms of clock time.
        Assert.Equal(new[] { 0L, 100 * Ms, 300 * Ms, 500 * Ms }, times);
    }

    [Fact]
    public void StartIndexSkipsTheRecordsBeforeIt()
    {
        var clock = new VirtualClock();
        var engine = new ReplayEngine(clock, speed: 1.0);
        var emitted = new List<TsrRecord>();
        var times = new List<long>();

        var task = engine.RunAsync(Records(0, 100, 250, 400), (record, now) =>
        {
            emitted.Add(record);
            times.Add(now);
        }, startIndex: 2);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(2, emitted.Count);

        // Seeking re-anchors: the first record played becomes "now".
        Assert.Equal(new[] { 0L, 150 * Ms }, times);
        Assert.Equal(4, engine.Position);
    }

    [Fact]
    public void CancellationStopsTheRunWhereItStood()
    {
        var engine = new ReplayEngine(new VirtualClock());
        using var cancellation = new CancellationTokenSource();
        var emitted = 0;

        var task = engine.RunAsync(Records(0, 100, 200, 300), (_, _) =>
        {
            if (++emitted == 2)
            {
                cancellation.Cancel();
            }
        }, startIndex: 0, cancellation.Token);

        Assert.True(task.IsCanceled || task.IsFaulted);
        Assert.Equal(2, emitted);
        Assert.Equal(2, engine.Position);
    }

    [Fact]
    public void AnEmptyRecordingEmitsNothing()
    {
        var (times, emitted) = Run(Array.Empty<TsrRecord>(), speed: 1.0);

        Assert.Empty(times);
        Assert.Empty(emitted);
    }

    [Fact]
    public void RecordsSharingATimestampAreEmittedTogether()
    {
        var (times, _) = Run(Records(0, 100, 100, 250), speed: 1.0);

        Assert.Equal(new[] { 0L, 100 * Ms, 100 * Ms, 250 * Ms }, times);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100 * Ms, 1)]
    [InlineData(150 * Ms, 2)]
    [InlineData(250 * Ms, 2)]
    [InlineData(999 * Ms, 3)]
    public void SeekFindsTheFirstRecordAtOrAfterAnInstant(long micros, int expected)
    {
        Assert.Equal(expected, ReplayEngine.IndexAt(Records(0, 100, 250), micros));
    }

    /// <summary>
    /// The bytes handed to the sink must be the bytes that were captured. A replay that reformats
    /// its payload proves nothing about the live path.
    /// </summary>
    [Fact]
    public void FramesArePassedThroughUntouched()
    {
        var records = new List<TsrRecord>
        {
            new(0, Vectors.Hex("A5 03 11 22 33")),
            new(50_000, Vectors.Hex("A5 02 44 55")),
        };

        var (_, emitted) = Run(records, speed: 1.0);

        Assert.Equal(records[0].Frame, emitted[0].Frame);
        Assert.Equal(records[1].Frame, emitted[1].Frame);
    }
}
