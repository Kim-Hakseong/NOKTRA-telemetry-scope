using Ts.Core.Decoding;
using Ts.Core.Definition;
using Ts.Core.Pipeline;
using Ts.Core.Recording;
using Ts.Core.Replay;
using Ts.Core.Time;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>
/// The pipeline is where the "replay is indistinguishable from live" claim is either true or not,
/// so it is asserted directly: capture a stream, replay the file, compare every sample.
/// </summary>
public class PipelineTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("tsr-pipeline-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ChannelSet Definition => ChannelSetReader.Read("""
        name: Pipeline vector
        framing:
          mode: fixed
          frameLength: 4
        channels:
          - name: Current
            offset: 0
            type: u16
            a: 0.001
            unit: mA
            min: 4
            max: 20
          - name: Counter
            offset: 2
            type: u16
        """);

    private static byte[] FrameAt(int index)
    {
        var frame = new byte[4];
        var current = (ushort)(3000 + (index * 500));
        frame[0] = (byte)(current >> 8);
        frame[1] = (byte)current;
        frame[2] = (byte)(index >> 8);
        frame[3] = (byte)index;
        return frame;
    }

    [Fact]
    public void DecodesEveryChannelOfEveryFrameIntoItsHistory()
    {
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 100);

        for (var i = 0; i < 10; i++)
        {
            pipeline.Accept(i * 1000, FrameAt(i));
        }

        Assert.Equal(10, pipeline.FrameCount);
        Assert.Equal(40, pipeline.ByteCount);
        Assert.Equal(10, pipeline.Histories[0].Count);

        Assert.Equal(3.0, pipeline.Histories[0].ValueAt(0), 9);
        Assert.Equal(3.5, pipeline.Histories[0].ValueAt(1), 9);
        Assert.Equal(9, pipeline.Histories[1].ValueAt(9));
    }

    [Fact]
    public void CountsFramesThatViolateARangeWithoutAlteringTheValue()
    {
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 100);

        // 3.0 mA is under the 4 mA minimum; 12.0 mA is inside it.
        pipeline.Accept(0, FrameAt(0));
        pipeline.Accept(1000, FrameAt(18));

        Assert.Equal(1, pipeline.ViolationFrameCount);
        Assert.Equal(SampleStatus.UnderRange, pipeline.Histories[0].StatusAt(0));
        Assert.Equal(3.0, pipeline.Histories[0].ValueAt(0), 9);
        Assert.Equal(SampleStatus.Ok, pipeline.Histories[0].StatusAt(1));
    }

    [Fact]
    public void CountsShortFramesSeparatelyFromRangeViolations()
    {
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 100);

        pipeline.Accept(0, new byte[2]);

        Assert.Equal(1, pipeline.ShortFrameCount);
        Assert.Equal(SampleStatus.Missing, pipeline.Histories[1].StatusAt(0));
    }

    [Fact]
    public void ARecordedThenReplayedCaptureProducesIdenticalSamples()
    {
        var path = Path.Combine(_directory, "live.tsr");
        var live = new TelemetryPipeline(Definition, historyCapacity: 1000);

        using (var recorder = TsrWriter.Create(path, Definition, startUnixMicros: 0))
        {
            live.Recorder = recorder;
            for (var i = 0; i < 250; i++)
            {
                live.Accept(i * 2000, FrameAt(i));
            }

            live.Recorder = null;
        }

        var file = TsrReader.ReadFile(path);
        var replayed = new TelemetryPipeline(file.ReadDefinition(), historyCapacity: 1000);

        var engine = new ReplayEngine(new VirtualClock(), speed: 4.0);
        var task = engine.RunAsync(file.Records, (record, _) =>
            replayed.Accept(record.TimestampMicros, record.Frame));

        Assert.True(task.IsCompletedSuccessfully);

        Assert.Equal(live.FrameCount, replayed.FrameCount);
        Assert.Equal(live.ByteCount, replayed.ByteCount);
        Assert.Equal(live.ViolationFrameCount, replayed.ViolationFrameCount);

        for (var channel = 0; channel < live.Histories.Length; channel++)
        {
            var a = live.Histories[channel];
            var b = replayed.Histories[channel];

            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a.TimeAt(i), b.TimeAt(i));
                Assert.Equal(a.ValueAt(i), b.ValueAt(i));
                Assert.Equal(a.StatusAt(i), b.StatusAt(i));
            }
        }
    }

    [Fact]
    public void TheRecorderSeesTheBytesNotTheInterpretation()
    {
        var path = Path.Combine(_directory, "raw.tsr");
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 10);

        using (var recorder = TsrWriter.Create(path, Definition, 0))
        {
            pipeline.Recorder = recorder;

            // Too short for the second channel: the frame is still captured verbatim.
            pipeline.Accept(0, new byte[] { 0x2E, 0xE0 });
        }

        var file = TsrReader.ReadFile(path);

        Assert.Equal(new byte[] { 0x2E, 0xE0 }, Assert.Single(file.Records).Frame);
        Assert.Equal(1, pipeline.ShortFrameCount);
    }

    [Fact]
    public void ResetClearsHistoryAndCounters()
    {
        var pipeline = new TelemetryPipeline(Definition, historyCapacity: 100);
        pipeline.Accept(0, FrameAt(0));

        pipeline.Reset();

        Assert.Equal(0, pipeline.FrameCount);
        Assert.Equal(0, pipeline.ViolationFrameCount);
        Assert.Equal(0, pipeline.Histories[0].Count);
    }
}
