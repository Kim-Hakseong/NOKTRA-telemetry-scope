using Ts.Core.Decoding;
using Ts.Core.Definition;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>
/// The decode vectors. These are frozen: they may be added to, never edited.
/// </summary>
public class DecoderVectorTests
{
    [Fact]
    public void S16BigEndian_FFF6_ScaledByTenth_IsMinusOne()
    {
        var channel = new ChannelDef
        {
            Name = "Vector",
            Offset = 0,
            Type = FieldType.S16,
            Endian = Endian.Big,
            A = 0.1,
            B = 0,
        };

        var sample = ChannelDecoder.DecodeChannel(channel, Vectors.Hex("FF F6"));

        Assert.Equal(-10.0, sample.Raw);
        Assert.Equal(-1.0, sample.Value, 12);
        Assert.Equal(SampleStatus.Ok, sample.Status);
    }

    [Fact]
    public void Float32BigEndian_40490FDB_IsPi()
    {
        var channel = new ChannelDef
        {
            Name = "Vector",
            Offset = 0,
            Type = FieldType.F32,
            Endian = Endian.Big,
        };

        var sample = ChannelDecoder.DecodeChannel(channel, Vectors.Hex("40 49 0F DB"));

        Assert.Equal(3.1415927f, (float)sample.Value);
        Assert.Equal(3.1415927, sample.Value, 7);
    }

    [Fact]
    public void Float32LittleEndian_IsTheSameValueByteReversed()
    {
        var channel = new ChannelDef
        {
            Name = "Vector",
            Offset = 0,
            Type = FieldType.F32,
            Endian = Endian.Little,
        };

        var sample = ChannelDecoder.DecodeChannel(channel, Vectors.Hex("DB 0F 49 40"));

        Assert.Equal(3.1415927f, (float)sample.Value);
    }

    /// <summary>
    /// The 4-20 mA shape: a current loop scaled to engineering units, where the interesting part
    /// is that an under-range reading is flagged rather than clipped.
    /// </summary>
    [Theory]
    [InlineData(12000, 12.0, SampleStatus.Ok)]
    [InlineData(3000, 3.0, SampleStatus.UnderRange)]
    [InlineData(21000, 21.0, SampleStatus.OverRange)]
    [InlineData(4000, 4.0, SampleStatus.Ok)]
    [InlineData(20000, 20.0, SampleStatus.Ok)]
    public void CurrentLoopChannel_FlagsRangeViolations(int raw, double expected, SampleStatus status)
    {
        var channel = new ChannelDef
        {
            Name = "Loop",
            Offset = 0,
            Type = FieldType.U16,
            Endian = Endian.Big,
            A = 0.001,
            B = 0,
            Unit = "mA",
            Min = 4,
            Max = 20,
        };

        var frame = new byte[2];
        FieldCodec.WriteRaw(frame, channel, raw);

        var sample = ChannelDecoder.DecodeChannel(channel, frame);

        Assert.Equal(raw, sample.Raw);
        Assert.Equal(expected, sample.Value, 12);
        Assert.Equal(status, sample.Status);
        Assert.Equal(status == SampleStatus.Ok, sample.InRange);
    }

    [Fact]
    public void ShortFrame_ReportsMissingRatherThanThrowing()
    {
        var channel = new ChannelDef { Name = "Late", Offset = 6, Type = FieldType.U16 };

        var sample = ChannelDecoder.DecodeChannel(channel, Vectors.Hex("01 02 03 04"));

        Assert.Equal(SampleStatus.Missing, sample.Status);
        Assert.False(sample.IsPresent);
    }

    /// <summary>Every type survives a write/read round trip in both byte orders.</summary>
    [Theory]
    [InlineData(FieldType.U8, 200.0)]
    [InlineData(FieldType.S8, -100.0)]
    [InlineData(FieldType.U16, 65000.0)]
    [InlineData(FieldType.S16, -32000.0)]
    [InlineData(FieldType.U32, 4000000000.0)]
    [InlineData(FieldType.S32, -2000000000.0)]
    [InlineData(FieldType.U64, 9000000000000.0)]
    [InlineData(FieldType.S64, -9000000000000.0)]
    [InlineData(FieldType.F32, 0.15625)]
    [InlineData(FieldType.F64, -1234.56789)]
    public void EveryFieldType_RoundTrips(FieldType type, double raw)
    {
        foreach (var endian in new[] { Endian.Big, Endian.Little })
        {
            var channel = new ChannelDef { Name = "T", Offset = 1, Type = type, Endian = endian };
            var frame = new byte[channel.EndOffset + 1];

            FieldCodec.WriteRaw(frame, channel, raw);
            Assert.True(FieldCodec.TryReadRaw(frame, channel, out var read));

            Assert.Equal(raw, read, 6);
        }
    }

    [Fact]
    public void ByteOrder_ActuallyReversesTheBytes()
    {
        var big = new ChannelDef { Name = "B", Offset = 0, Type = FieldType.U32, Endian = Endian.Big };
        var little = new ChannelDef { Name = "L", Offset = 0, Type = FieldType.U32, Endian = Endian.Little };

        var frame = Vectors.Hex("01 02 03 04");

        Assert.True(FieldCodec.TryReadRaw(frame, big, out var beValue));
        Assert.True(FieldCodec.TryReadRaw(frame, little, out var leValue));

        Assert.Equal(0x01020304, beValue);
        Assert.Equal(0x04030201, leValue);
    }

    [Fact]
    public void DecoderProducesOneSamplePerChannelInDeclaredOrder()
    {
        var set = ChannelSetReader.Read("""
            name: Two
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: First
                offset: 0
                type: u16
                a: 2
                b: 1
              - name: Second
                offset: 2
                type: u16
            """);

        var samples = new ChannelDecoder(set).Decode(Vectors.Hex("00 05 00 07"));

        Assert.Equal(2, samples.Length);
        Assert.Equal(11.0, samples[0].Value);
        Assert.Equal(7.0, samples[1].Value);
    }
}
