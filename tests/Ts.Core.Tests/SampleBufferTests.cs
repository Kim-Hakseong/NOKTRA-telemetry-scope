using Ts.Core.Analysis;
using Ts.Core.Decoding;
using Xunit;

namespace Ts.Core.Tests;

public class SampleBufferTests
{
    [Fact]
    public void KeepsSamplesInOrderUntilFull()
    {
        var buffer = new SampleBuffer(4);
        for (var i = 0; i < 3; i++)
        {
            buffer.Add(i * 10, i, SampleStatus.Ok);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(0, buffer.TimeAt(0));
        Assert.Equal(2, buffer.ValueAt(2));
        Assert.Equal(2, buffer.Latest);
        Assert.Equal(0, buffer.Evicted);
    }

    [Fact]
    public void OldestSamplesFallOffTheBackOnceItWraps()
    {
        var buffer = new SampleBuffer(4);
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(i * 10, i, SampleStatus.Ok);
        }

        Assert.Equal(4, buffer.Count);
        Assert.Equal(6, buffer.ValueAt(0));
        Assert.Equal(9, buffer.ValueAt(3));
        Assert.Equal(60, buffer.OldestMicros);
        Assert.Equal(90, buffer.NewestMicros);
        Assert.Equal(6, buffer.Evicted);
    }

    [Fact]
    public void LowerBoundFindsTheWindowStartAcrossTheWrap()
    {
        var buffer = new SampleBuffer(4);
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(i * 10, i, SampleStatus.Ok);
        }

        // Live range is 60..90.
        Assert.Equal(0, buffer.LowerBound(0));
        Assert.Equal(0, buffer.LowerBound(60));
        Assert.Equal(2, buffer.LowerBound(75));
        Assert.Equal(3, buffer.LowerBound(90));
        Assert.Equal(4, buffer.LowerBound(1000));
    }

    [Fact]
    public void StatusTravelsWithTheSample()
    {
        var buffer = new SampleBuffer(2);
        buffer.Add(0, 1, SampleStatus.OverRange);

        Assert.Equal(SampleStatus.OverRange, buffer.StatusAt(0));
        Assert.Equal(SampleStatus.OverRange, buffer.LatestStatus);
    }

    [Fact]
    public void ClearEmptiesItWithoutReallocating()
    {
        var buffer = new SampleBuffer(4);
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(i, i, SampleStatus.Ok);
        }

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Evicted);
        Assert.Equal(4, buffer.Capacity);
        Assert.True(double.IsNaN(buffer.Latest));
    }

    [Fact]
    public void ReadingPastTheEndIsRefused()
    {
        var buffer = new SampleBuffer(4);
        buffer.Add(0, 1, SampleStatus.Ok);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ValueAt(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ValueAt(-1));
    }
}
