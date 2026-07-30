using Ts.Core.Analysis;
using Ts.Core.Decoding;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>The frozen decimation vector, and the properties that make it useful.</summary>
public class DecimationVectorTests
{
    [Fact]
    public void EightValuesIntoTwoBuckets_KeepTheExtremesOfEachHalf()
    {
        var buckets = Decimator.Decimate(new double[] { 0, 10, -5, 3, 7, 2, 8, 1 }, bucketCount: 2);

        Assert.Equal(2, buckets.Length);
        Assert.Equal(new Envelope(-5, 10, true), buckets[0]);
        Assert.Equal(new Envelope(1, 8, true), buckets[1]);
    }

    [Fact]
    public void ASingleSpikeSurvivesAnyAmountOfReduction()
    {
        var values = new double[10_000];
        values[6_137] = 42;

        foreach (var bucketCount in new[] { 1, 3, 17, 800, 9_999 })
        {
            var buckets = Decimator.Decimate(values, bucketCount);
            Assert.Equal(42, buckets.Max(b => b.Max));
        }
    }

    [Fact]
    public void EveryValueLandsInExactlyOneBucket()
    {
        // 7 into 3 does not divide evenly; nothing may fall between the buckets.
        var values = new double[] { 1, 2, 3, 4, 5, 6, 7 };

        var buckets = Decimator.Decimate(values, bucketCount: 3);

        Assert.Equal(1, buckets[0].Min);
        Assert.Equal(7, buckets[^1].Max);
        Assert.All(buckets, b => Assert.True(b.HasData));
    }

    [Fact]
    public void MoreBucketsThanValuesLeavesTheSurplusEmpty()
    {
        var buckets = Decimator.Decimate(new double[] { 5, 6 }, bucketCount: 5);

        Assert.Equal(2, buckets.Count(b => b.HasData));
        Assert.All(buckets.Where(b => !b.HasData), b => Assert.Equal(Envelope.Empty, b));
    }

    [Fact]
    public void NotANumberIsSkippedRatherThanPoisoningTheBucket()
    {
        var buckets = Decimator.Decimate(new[] { double.NaN, 4.0, double.NaN, 9.0 }, bucketCount: 1);

        Assert.Equal(new Envelope(4, 9, true), buckets[0]);
    }

    [Fact]
    public void AllNotANumberIsAnEmptyBucketNotAZeroOne()
    {
        var buckets = Decimator.Decimate(new[] { double.NaN, double.NaN }, bucketCount: 1);

        Assert.False(buckets[0].HasData);
    }

    // --- windowed decimation, as the chart uses it

    private static SampleBuffer Ramp(int count, int stepMicros)
    {
        var buffer = new SampleBuffer(count);
        for (var i = 0; i < count; i++)
        {
            buffer.Add((long)i * stepMicros, i, SampleStatus.Ok);
        }

        return buffer;
    }

    [Fact]
    public void ColumnsCoverTheRequestedWindowOnly()
    {
        var buffer = Ramp(1000, 1000);
        var columns = new Envelope[10];

        Assert.True(Decimator.BuildColumns(buffer, 200_000, 400_000, columns));

        Assert.Equal(200, columns[0].Min);
        Assert.Equal(400, columns[^1].Max);
    }

    [Fact]
    public void AGapInTheDataStaysAGap()
    {
        var buffer = new SampleBuffer(16);
        buffer.Add(0, 1, SampleStatus.Ok);
        buffer.Add(100_000, 2, SampleStatus.Ok);

        var columns = new Envelope[10];
        Decimator.BuildColumns(buffer, 0, 100_000, columns);

        Assert.True(columns[0].HasData);
        Assert.True(columns[^1].HasData);
        Assert.All(columns[1..^1], c => Assert.False(c.HasData));
    }

    [Fact]
    public void AWindowWithNoSamplesReportsNothingToDraw()
    {
        var buffer = Ramp(10, 1000);
        var columns = new Envelope[8];

        Assert.False(Decimator.BuildColumns(buffer, 500_000, 600_000, columns));
        Assert.All(columns, c => Assert.False(c.HasData));
    }

    [Fact]
    public void AnEmptyBufferIsNotAnError()
    {
        var columns = new Envelope[8];

        Assert.False(Decimator.BuildColumns(new SampleBuffer(4), 0, 1000, columns));
    }
}
