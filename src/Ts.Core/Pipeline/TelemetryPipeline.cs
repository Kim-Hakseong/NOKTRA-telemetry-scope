using Ts.Core.Analysis;
using Ts.Core.Decoding;
using Ts.Core.Definition;
using Ts.Core.Recording;

namespace Ts.Core.Pipeline;

/// <summary>
/// The single path every frame takes, whichever door it came in by.
///
/// A frame from a UDP socket, a serial port or a replayed recording arrives here as the same
/// thing: a timestamp and some bytes. It is recorded before it is decoded, so a capture holds what
/// was on the wire rather than this build's interpretation of it, and one decoder fills the
/// histories. That is what makes "replay looks exactly like the live capture" a structural fact
/// rather than a hope.
///
/// The pipeline is not thread-safe by design. Receivers hand frames to a bounded queue and the UI
/// thread drains it in batches, so there is exactly one thread in here and no lock on the hot path.
/// </summary>
public sealed class TelemetryPipeline
{
    private readonly ChannelSample[] _scratch;

    public TelemetryPipeline(ChannelSet definition, int historyCapacity = 200_000)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
        Decoder = new ChannelDecoder(definition);
        _scratch = new ChannelSample[Decoder.ChannelCount];

        Histories = new SampleBuffer[Decoder.ChannelCount];
        for (var i = 0; i < Histories.Length; i++)
        {
            Histories[i] = new SampleBuffer(historyCapacity);
        }
    }

    public ChannelSet Definition { get; }

    public ChannelDecoder Decoder { get; }

    /// <summary>One history per channel, in declaration order.</summary>
    public SampleBuffer[] Histories { get; }

    /// <summary>Recorder to copy raw frames into, or null when not recording.</summary>
    public TsrWriter? Recorder { get; set; }

    public long FrameCount { get; private set; }

    public long ByteCount { get; private set; }

    /// <summary>Frames containing at least one out-of-range channel.</summary>
    public long ViolationFrameCount { get; private set; }

    /// <summary>Frames too short to carry every channel.</summary>
    public long ShortFrameCount { get; private set; }

    public long LastFrameMicros { get; private set; }

    /// <summary>Timestamp of the newest sample in any history, or 0 when nothing has arrived.</summary>
    public long NewestSampleMicros => FrameCount == 0 ? 0 : LastFrameMicros;

    public void Accept(long timeMicros, ReadOnlySpan<byte> frame)
    {
        // Record first. If decoding throws on a definition that does not match the wire, the
        // capture still holds the bytes needed to work out why.
        Recorder?.Write(timeMicros, frame);

        Decoder.Decode(frame, _scratch);

        var violation = false;
        var missing = false;

        for (var i = 0; i < _scratch.Length; i++)
        {
            var sample = _scratch[i];
            Histories[i].Add(timeMicros, sample.Value, sample.Status);

            violation |= sample.IsViolation;
            missing |= !sample.IsPresent;
        }

        FrameCount++;
        ByteCount += frame.Length;
        LastFrameMicros = timeMicros;

        if (violation)
        {
            ViolationFrameCount++;
        }

        if (missing)
        {
            ShortFrameCount++;
        }
    }

    /// <summary>Latest decoded sample per channel, from the last accepted frame.</summary>
    public ChannelSample Latest(int channelIndex)
    {
        var history = Histories[channelIndex];
        return history.Count == 0
            ? ChannelSample.Missing
            : new ChannelSample(double.NaN, history.Latest, history.LatestStatus);
    }

    /// <summary>Drops all history and counters. The recorder, if any, is left alone.</summary>
    public void Reset()
    {
        foreach (var history in Histories)
        {
            history.Clear();
        }

        FrameCount = 0;
        ByteCount = 0;
        ViolationFrameCount = 0;
        ShortFrameCount = 0;
        LastFrameMicros = 0;
    }
}
