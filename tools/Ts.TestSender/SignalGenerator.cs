using System.Buffers.Binary;
using Ts.Core.Decoding;
using Ts.Core.Definition;

namespace Ts.TestSender;

/// <summary>
/// Builds frames that match a channel definition, carrying waveforms a test engineer would
/// recognise.
///
/// This exists because the alternative — committing a captured file to demonstrate the tool — puts
/// data of unknown provenance in the repository and proves nothing about the decoder. Everything
/// here is a function of the definition and the elapsed time, so the same definition and the same
/// seed give the same bytes on any machine.
///
/// It lives in the test tool, not the product. Nothing shipped to a user invents readings.
/// </summary>
public sealed class SignalGenerator
{
    private readonly ChannelSet _set;
    private readonly Random _noise;
    private readonly int _frameLength;

    public SignalGenerator(ChannelSet set, int seed = 20260731)
    {
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _noise = new Random(seed);
        _frameLength = FrameLengthFor(set);
    }

    public int FrameLength => _frameLength;

    /// <summary>Encodes the state of every channel at <paramref name="timeMicros"/> into a frame.</summary>
    public byte[] Next(long timeMicros)
    {
        var frame = new byte[_frameLength];
        var seconds = timeMicros / 1_000_000.0;

        for (var i = 0; i < _set.Channels.Count; i++)
        {
            var channel = _set.Channels[i];
            var value = ValueFor(channel, i, seconds);

            // The generator thinks in engineering units; the wire carries raw, so invert the scale
            // the decoder will apply. A generator that wrote engineering units straight to the wire
            // would make every scale factor untestable.
            var raw = channel.A == 0 ? 0 : (value - channel.B) / channel.A;
            FieldCodec.WriteRaw(frame, channel, raw);
        }

        ApplyFraming(frame);
        return frame;
    }

    /// <summary>
    /// One waveform per channel, chosen by position so a definition with several channels produces
    /// a scope that is actually readable rather than a bundle of identical sines.
    /// </summary>
    private double ValueFor(ChannelDef channel, int index, double seconds)
    {
        var low = channel.Min ?? 0;
        var high = channel.Max ?? 100;
        if (high <= low)
        {
            high = low + 100;
        }

        var centre = (low + high) / 2;
        var amplitude = (high - low) * 0.38;
        var period = 3.0 + (index * 1.7);
        var phase = 2 * Math.PI * seconds / period;

        var shape = (index % 4) switch
        {
            0 => Math.Sin(phase),
            1 => Triangle(phase),
            2 => Math.Sign(Math.Sin(phase)) * 0.85,
            _ => Sawtooth(phase),
        };

        var value = centre + (amplitude * shape);

        // A little noise, scaled to the channel, so decimation has something to reduce and the
        // min/max envelope is visibly doing work.
        value += (_noise.NextDouble() - 0.5) * (high - low) * 0.012;

        // The last channel of a definition that declares limits drops below them for about a
        // second every thirteen. A range highlight that has never been seen to fire has never been
        // seen to work, and a fault that recurs is also what a real sensor failure looks like.
        if (channel.Min is not null && index == _set.Channels.Count - 1)
        {
            if (Math.Sin(2 * Math.PI * seconds / 13.0) > 0.9)
            {
                value = low - ((high - low) * 0.15);
            }
        }

        return value;
    }

    private static double Triangle(double phase)
    {
        var t = (phase / (2 * Math.PI)) % 1.0;
        return (4 * Math.Abs(t - 0.5)) - 1;
    }

    private static double Sawtooth(double phase)
    {
        var t = (phase / (2 * Math.PI)) % 1.0;
        return (2 * t) - 1;
    }

    private void ApplyFraming(byte[] frame)
    {
        var framing = _set.Framing;

        switch (framing.Mode)
        {
            case FramingMode.LengthField:
            {
                var declared = frame.Length - framing.Adjust;
                var field = frame.AsSpan(framing.LengthOffset, framing.LengthSize);

                switch (framing.LengthSize)
                {
                    case 1:
                        field[0] = (byte)declared;
                        break;
                    case 2:
                        if (framing.LengthEndian == Endian.Big)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(field, (ushort)declared);
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)declared);
                        }

                        break;
                    default:
                        if (framing.LengthEndian == Endian.Big)
                        {
                            BinaryPrimitives.WriteUInt32BigEndian(field, (uint)declared);
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)declared);
                        }

                        break;
                }

                if (framing.LengthOffset > 0)
                {
                    frame[0] = 0xA5;
                }

                break;
            }

            case FramingMode.Delimiter:
                framing.Delimiter.CopyTo(frame.AsSpan(frame.Length - framing.Delimiter.Length));
                break;
        }
    }

    /// <summary>
    /// The shortest frame that carries every channel and satisfies the framing. Anything longer
    /// would be padding nobody declared.
    /// </summary>
    private static int FrameLengthFor(ChannelSet set)
    {
        var payload = Math.Max(1, set.MinimumFrameLength);
        var framing = set.Framing;

        return framing.Mode switch
        {
            FramingMode.Fixed => framing.FrameLength,
            FramingMode.LengthField => Math.Max(payload, framing.HeaderLength),
            FramingMode.Delimiter => payload + framing.Delimiter.Length,
            _ => payload,
        };
    }
}
