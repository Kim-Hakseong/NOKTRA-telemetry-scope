using Ts.Core.Analysis;
using Ts.Core.Decoding;
using Xunit;

namespace Ts.Core.Tests;

public class StatisticsTests
{
    private static SampleBuffer Buffer(params (long Time, double Value, SampleStatus Status)[] samples)
    {
        var buffer = new SampleBuffer(Math.Max(1, samples.Length));
        foreach (var (time, value, status) in samples)
        {
            buffer.Add(time, value, status);
        }

        return buffer;
    }

    [Fact]
    public void SummarisesTheWindowNotTheWholeSession()
    {
        var buffer = new SampleBuffer(100);
        for (var i = 0; i < 100; i++)
        {
            buffer.Add(i * 1000, i, SampleStatus.Ok);
        }

        var all = Statistics.Over(buffer, 0, 99_000);
        Assert.Equal(100, all.Count);
        Assert.Equal(0, all.Min);
        Assert.Equal(99, all.Max);
        Assert.Equal(49.5, all.Mean, 9);

        // 90..99 only: a warm-up half an hour ago must not drag the mean.
        var tail = Statistics.Over(buffer, 90_000, 99_000);
        Assert.Equal(10, tail.Count);
        Assert.Equal(90, tail.Min);
        Assert.Equal(99, tail.Max);
        Assert.Equal(94.5, tail.Mean, 9);
        Assert.Equal(99, tail.Latest);
    }

    [Fact]
    public void CountsViolationsSeparatelyFromMissingSamples()
    {
        var stats = Statistics.Over(
            Buffer(
                (0, 5, SampleStatus.Ok),
                (1, 30, SampleStatus.OverRange),
                (2, -1, SampleStatus.UnderRange),
                (3, double.NaN, SampleStatus.Missing)),
            0,
            10);

        Assert.Equal(3, stats.Count);
        Assert.Equal(2, stats.ViolationCount);
        Assert.Equal(1, stats.MissingCount);

        // A violating reading is still a reading and belongs in the extremes.
        Assert.Equal(-1, stats.Min);
        Assert.Equal(30, stats.Max);
    }

    [Fact]
    public void AWindowWithNothingInItIsEmptyNotZero()
    {
        var stats = Statistics.Over(Buffer((0, 5, SampleStatus.Ok)), 1000, 2000);

        Assert.False(stats.HasData);
        Assert.Equal(0, stats.Count);
        Assert.True(double.IsNaN(stats.Mean));
    }

    [Fact]
    public void TheMeanSurvivesALongRunOfLargeSimilarValues()
    {
        // Naive accumulation loses the fractional part here; the mean is what people compare
        // between runs, so it has to hold up.
        const int count = 200_000;
        var buffer = new SampleBuffer(count);
        for (var i = 0; i < count; i++)
        {
            buffer.Add(i, 1e8 + (i % 2 == 0 ? 0.5 : -0.5), SampleStatus.Ok);
        }

        var stats = Statistics.Over(buffer, 0, count);

        Assert.Equal(1e8, stats.Mean, 6);
    }

    [Fact]
    public void MissingOnlyWindowStillReportsWhatWasMissing()
    {
        var stats = Statistics.Over(
            Buffer((0, double.NaN, SampleStatus.Missing), (1, double.NaN, SampleStatus.Missing)),
            0,
            10);

        Assert.False(stats.HasData);
        Assert.Equal(2, stats.MissingCount);
    }
}
