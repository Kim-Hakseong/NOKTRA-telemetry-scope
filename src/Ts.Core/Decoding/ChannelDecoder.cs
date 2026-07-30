using Ts.Core.Definition;

namespace Ts.Core.Decoding;

/// <summary>How a decoded sample stands against the channel's declared valid range.</summary>
public enum SampleStatus
{
    /// <summary>Decoded and inside the valid range (or no range was declared).</summary>
    Ok,

    /// <summary>Below the declared minimum.</summary>
    UnderRange,

    /// <summary>Above the declared maximum.</summary>
    OverRange,

    /// <summary>The frame was too short to contain the field.</summary>
    Missing,
}

/// <summary>
/// One channel's reading from one frame: the raw wire number, the engineering value, and whether
/// the value is inside its declared range.
/// </summary>
public readonly record struct ChannelSample(double Raw, double Value, SampleStatus Status)
{
    public static ChannelSample Missing => new(0, double.NaN, SampleStatus.Missing);

    public bool IsPresent => Status != SampleStatus.Missing;

    /// <summary>True only when a value was decoded and it respects the declared range.</summary>
    public bool InRange => Status == SampleStatus.Ok;

    /// <summary>True when a value was decoded but violates the declared range.</summary>
    public bool IsViolation => Status is SampleStatus.UnderRange or SampleStatus.OverRange;
}

/// <summary>
/// Applies a channel set to a frame: raw extraction, the a x raw + b scale, and the range check.
///
/// The decoder holds no per-frame state, so the same instance serves the live receive path, the
/// replay path and the tests. That is what makes a replayed recording provably identical to the
/// live capture it came from — there is only one implementation to be identical to.
/// </summary>
public sealed class ChannelDecoder
{
    private readonly ChannelDef[] _channels;

    public ChannelDecoder(ChannelSet set)
    {
        Set = set ?? throw new ArgumentNullException(nameof(set));
        _channels = set.Channels.ToArray();
    }

    public ChannelSet Set { get; }

    public int ChannelCount => _channels.Length;

    public IReadOnlyList<ChannelDef> Channels => _channels;

    /// <summary>Decodes every channel into a caller-owned buffer, avoiding a per-frame allocation.</summary>
    public void Decode(ReadOnlySpan<byte> frame, Span<ChannelSample> destination)
    {
        if (destination.Length < _channels.Length)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} samples but the set has {_channels.Length}.",
                nameof(destination));
        }

        for (var i = 0; i < _channels.Length; i++)
        {
            destination[i] = DecodeChannel(_channels[i], frame);
        }
    }

    public ChannelSample[] Decode(ReadOnlySpan<byte> frame)
    {
        var samples = new ChannelSample[_channels.Length];
        Decode(frame, samples);
        return samples;
    }

    public static ChannelSample DecodeChannel(ChannelDef channel, ReadOnlySpan<byte> frame)
    {
        if (!FieldCodec.TryReadRaw(frame, channel, out var raw))
        {
            return ChannelSample.Missing;
        }

        var value = channel.Apply(raw);

        var status = SampleStatus.Ok;
        if (channel.Min is { } min && value < min)
        {
            status = SampleStatus.UnderRange;
        }
        else if (channel.Max is { } max && value > max)
        {
            status = SampleStatus.OverRange;
        }

        return new ChannelSample(raw, value, status);
    }
}
