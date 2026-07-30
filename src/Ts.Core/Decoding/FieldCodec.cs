using System.Buffers.Binary;
using Ts.Core.Definition;

namespace Ts.Core.Decoding;

/// <summary>
/// Reads and writes one field's raw value at its declared offset, byte order and width.
///
/// The .NET base class library already implements IEEE 754 and endian-aware integer reads, so
/// nothing here transcribes a numeric constant; see spec/README.md for why that matters.
/// </summary>
public static class FieldCodec
{
    /// <summary>
    /// Reads the raw (unscaled) value. Returns false when the frame is too short to contain the
    /// field, which is a normal condition on a variable-length stream rather than an error.
    /// </summary>
    public static bool TryReadRaw(ReadOnlySpan<byte> frame, ChannelDef channel, out double raw)
    {
        raw = 0;
        if (channel.Offset < 0 || channel.EndOffset > frame.Length)
        {
            return false;
        }

        var bytes = frame.Slice(channel.Offset, channel.Size);
        var big = channel.Endian == Endian.Big;

        raw = channel.Type switch
        {
            FieldType.U8 => bytes[0],
            FieldType.S8 => (sbyte)bytes[0],
            FieldType.U16 => big ? BinaryPrimitives.ReadUInt16BigEndian(bytes)
                                 : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            FieldType.S16 => big ? BinaryPrimitives.ReadInt16BigEndian(bytes)
                                 : BinaryPrimitives.ReadInt16LittleEndian(bytes),
            FieldType.U32 => big ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
                                 : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            FieldType.S32 => big ? BinaryPrimitives.ReadInt32BigEndian(bytes)
                                 : BinaryPrimitives.ReadInt32LittleEndian(bytes),
            FieldType.U64 => big ? BinaryPrimitives.ReadUInt64BigEndian(bytes)
                                 : BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            FieldType.S64 => big ? BinaryPrimitives.ReadInt64BigEndian(bytes)
                                 : BinaryPrimitives.ReadInt64LittleEndian(bytes),
            FieldType.F32 => big ? BinaryPrimitives.ReadSingleBigEndian(bytes)
                                 : BinaryPrimitives.ReadSingleLittleEndian(bytes),
            FieldType.F64 => big ? BinaryPrimitives.ReadDoubleBigEndian(bytes)
                                 : BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel.Type, "Unknown type."),
        };

        return true;
    }

    /// <summary>
    /// Writes a raw value back into a frame. Used by the synthetic transmitter and by the
    /// round-trip tests, which is the only way to prove the reader against a known encoding
    /// without shipping recorded sample data.
    /// </summary>
    public static void WriteRaw(Span<byte> frame, ChannelDef channel, double raw)
    {
        if (channel.EndOffset > frame.Length)
        {
            throw new ArgumentException(
                $"Frame of {frame.Length} bytes cannot hold '{channel.Name}', which ends at byte " +
                $"{channel.EndOffset}.",
                nameof(frame));
        }

        var bytes = frame.Slice(channel.Offset, channel.Size);
        var big = channel.Endian == Endian.Big;

        switch (channel.Type)
        {
            case FieldType.U8:
                bytes[0] = (byte)ClampToIntegral(raw, byte.MinValue, byte.MaxValue);
                break;
            case FieldType.S8:
                bytes[0] = (byte)(sbyte)ClampToIntegral(raw, sbyte.MinValue, sbyte.MaxValue);
                break;
            case FieldType.U16:
            {
                var value = (ushort)ClampToIntegral(raw, ushort.MinValue, ushort.MaxValue);
                if (big) { BinaryPrimitives.WriteUInt16BigEndian(bytes, value); }
                else { BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); }
                break;
            }

            case FieldType.S16:
            {
                var value = (short)ClampToIntegral(raw, short.MinValue, short.MaxValue);
                if (big) { BinaryPrimitives.WriteInt16BigEndian(bytes, value); }
                else { BinaryPrimitives.WriteInt16LittleEndian(bytes, value); }
                break;
            }

            case FieldType.U32:
            {
                var value = (uint)ClampToIntegral(raw, uint.MinValue, uint.MaxValue);
                if (big) { BinaryPrimitives.WriteUInt32BigEndian(bytes, value); }
                else { BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); }
                break;
            }

            case FieldType.S32:
            {
                var value = (int)ClampToIntegral(raw, int.MinValue, int.MaxValue);
                if (big) { BinaryPrimitives.WriteInt32BigEndian(bytes, value); }
                else { BinaryPrimitives.WriteInt32LittleEndian(bytes, value); }
                break;
            }

            case FieldType.U64:
            {
                var value = (ulong)ClampToIntegral(raw, ulong.MinValue, ulong.MaxValue);
                if (big) { BinaryPrimitives.WriteUInt64BigEndian(bytes, value); }
                else { BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); }
                break;
            }

            case FieldType.S64:
            {
                var value = (long)ClampToIntegral(raw, long.MinValue, long.MaxValue);
                if (big) { BinaryPrimitives.WriteInt64BigEndian(bytes, value); }
                else { BinaryPrimitives.WriteInt64LittleEndian(bytes, value); }
                break;
            }

            case FieldType.F32:
            {
                var value = (float)raw;
                if (big) { BinaryPrimitives.WriteSingleBigEndian(bytes, value); }
                else { BinaryPrimitives.WriteSingleLittleEndian(bytes, value); }
                break;
            }

            case FieldType.F64:
                if (big) { BinaryPrimitives.WriteDoubleBigEndian(bytes, raw); }
                else { BinaryPrimitives.WriteDoubleLittleEndian(bytes, raw); }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(channel), channel.Type, "Unknown type.");
        }
    }

    /// <summary>
    /// Rounds half away from zero and clamps, so a transmitter never wraps a value silently into
    /// a completely different reading.
    /// </summary>
    private static double ClampToIntegral(double raw, double min, double max)
        => Math.Clamp(Math.Round(raw, MidpointRounding.AwayFromZero), min, max);
}
