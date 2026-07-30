using Ts.Core.Definition;
using Ts.Core.Framing;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>
/// The frozen framing vectors, plus the invariance that makes them meaningful: however the stream
/// is chopped up on its way in, the frames that come out are the same.
/// </summary>
public class FramingVectorTests
{
    private static FramingDef LengthFieldVector => ChannelSetReader
        .Read(Vectors.LengthFieldYaml).Framing;

    [Fact]
    public void LengthFieldVector_A503112233_IsExactlyOneFrame()
    {
        var frames = new FrameAssembler(LengthFieldVector).Push(Vectors.Hex("A5 03 11 22 33"));

        var frame = Assert.Single(frames);
        Assert.Equal(Vectors.Hex("A5 03 11 22 33"), frame);
    }

    [Fact]
    public void LengthFieldVector_IsUnchangedWhenInjectedOneByteAtATime()
    {
        var stream = Vectors.Hex("A5 03 11 22 33");
        var assembler = new FrameAssembler(LengthFieldVector);
        var frames = new List<byte[]>();

        foreach (var b in stream)
        {
            assembler.Push(new[] { b }, f => frames.Add(f.ToArray()));
        }

        var frame = Assert.Single(frames);
        Assert.Equal(stream, frame);
    }

    [Fact]
    public void LengthFieldVector_HoldsAnIncompleteFrameBack()
    {
        var assembler = new FrameAssembler(LengthFieldVector);

        Assert.Empty(assembler.Push(Vectors.Hex("A5 03 11 22")));
        Assert.Equal(4, assembler.Buffered);

        Assert.Single(assembler.Push(Vectors.Hex("33")));
        Assert.Equal(0, assembler.Buffered);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(4096)]
    public void ChunkSizeNeverChangesTheFrames(int chunkSize)
    {
        foreach (var (framing, stream, expected) in AllModeStreams())
        {
            var assembler = new FrameAssembler(framing);
            var frames = new List<byte[]>();

            for (var offset = 0; offset < stream.Length; offset += chunkSize)
            {
                var take = Math.Min(chunkSize, stream.Length - offset);
                assembler.Push(stream.AsSpan(offset, take), f => frames.Add(f.ToArray()));
            }

            Assert.Equal(expected.Count, frames.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], frames[i]);
            }
        }
    }

    [Fact]
    public void FixedFraming_CutsEveryNBytes()
    {
        var framing = new FramingDef { Mode = FramingMode.Fixed, FrameLength = 4 };

        var frames = new FrameAssembler(framing).Push(Vectors.Hex("01 02 03 04 05 06 07 08 09"));

        Assert.Equal(2, frames.Count);
        Assert.Equal(Vectors.Hex("01 02 03 04"), frames[0]);
        Assert.Equal(Vectors.Hex("05 06 07 08"), frames[1]);
    }

    [Fact]
    public void DelimiterFraming_DropsTheTerminatorByDefault()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.Delimiter,
            Delimiter = Vectors.Hex("0D 0A"),
        };

        var frames = new FrameAssembler(framing).Push(Vectors.Hex("41 42 0D 0A 43 0D 0A 44"));

        Assert.Equal(2, frames.Count);
        Assert.Equal(Vectors.Hex("41 42"), frames[0]);
        Assert.Equal(Vectors.Hex("43"), frames[1]);
    }

    [Fact]
    public void DelimiterFraming_CanKeepTheTerminator()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.Delimiter,
            Delimiter = Vectors.Hex("0D 0A"),
            KeepDelimiter = true,
        };

        var frames = new FrameAssembler(framing).Push(Vectors.Hex("41 42 0D 0A"));

        Assert.Equal(Vectors.Hex("41 42 0D 0A"), Assert.Single(frames));
    }

    [Fact]
    public void DelimiterFraming_FindsATerminatorSplitAcrossTwoPushes()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.Delimiter,
            Delimiter = Vectors.Hex("0D 0A"),
        };

        var assembler = new FrameAssembler(framing);
        var frames = new List<byte[]>();

        assembler.Push(Vectors.Hex("41 42 0D"), f => frames.Add(f.ToArray()));
        Assert.Empty(frames);

        assembler.Push(Vectors.Hex("0A"), f => frames.Add(f.ToArray()));
        Assert.Equal(Vectors.Hex("41 42"), Assert.Single(frames));
    }

    [Fact]
    public void LengthField_ResynchronisesPastLeadingGarbage()
    {
        // 0xFF as a length would declare 257 bytes, past maxFrameLength, so it cannot be a frame
        // start. The assembler steps one byte at a time until the real frame lines up.
        var framing = new FramingDef
        {
            Mode = FramingMode.LengthField,
            HeaderLength = 2,
            LengthOffset = 1,
            LengthSize = 1,
            Adjust = 2,
            MaxFrameLength = 64,
        };

        var assembler = new FrameAssembler(framing);
        var frames = assembler.Push(Vectors.Hex("00 FF A5 03 11 22 33"));

        Assert.Equal(Vectors.Hex("A5 03 11 22 33"), Assert.Single(frames));
        Assert.Equal(2, assembler.DiscardedBytes);
    }

    [Fact]
    public void LengthField_RejectsALengthShorterThanTheHeader()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.LengthField,
            HeaderLength = 4,
            LengthOffset = 2,
            LengthSize = 2,
            Adjust = 0,
            MaxFrameLength = 256,
        };

        var assembler = new FrameAssembler(framing);

        // Declared total of 1 byte cannot hold its own 4-byte header.
        Assert.Empty(assembler.Push(Vectors.Hex("AA BB 00 01")));
        Assert.True(assembler.DiscardedBytes > 0);
    }

    [Fact]
    public void LengthField_ReadsALittleEndianLengthField()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.LengthField,
            HeaderLength = 4,
            LengthOffset = 2,
            LengthSize = 2,
            LengthEndian = Endian.Little,
            Adjust = 4,
            MaxFrameLength = 256,
        };

        // 0x0002 little-endian is "02 00"; total = 2 + 4 = 6.
        var frames = new FrameAssembler(framing).Push(Vectors.Hex("AA BB 02 00 11 22"));

        Assert.Equal(6, Assert.Single(frames).Length);
    }

    [Fact]
    public void DelimiterFraming_AbandonsAFrameThatOutgrowsTheMaximum()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.Delimiter,
            Delimiter = Vectors.Hex("0A"),
            MaxFrameLength = 16,
        };

        var assembler = new FrameAssembler(framing);
        assembler.Push(new byte[64]);

        Assert.Equal(0, assembler.FrameCount);
        Assert.True(assembler.DiscardedBytes >= 48);
        Assert.True(assembler.Buffered <= 16);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var assembler = new FrameAssembler(LengthFieldVector);
        assembler.Push(Vectors.Hex("A5 03 11 22 33 A5 03"));

        assembler.Reset();

        Assert.Equal(0, assembler.Buffered);
        Assert.Equal(0, assembler.FrameCount);
        Assert.Equal(0, assembler.DiscardedBytes);
    }

    /// <summary>
    /// One well-formed stream per framing mode, built here rather than committed as a blob so the
    /// expected frames are derived from the same construction the assertion checks.
    /// </summary>
    private static IEnumerable<(FramingDef Framing, byte[] Stream, List<byte[]> Expected)> AllModeStreams()
    {
        yield return BuildFixed();
        yield return BuildLengthField();
        yield return BuildDelimited();
    }

    private static (FramingDef, byte[], List<byte[]>) BuildFixed()
    {
        var framing = new FramingDef { Mode = FramingMode.Fixed, FrameLength = 6 };
        var expected = new List<byte[]>();
        var stream = new List<byte>();

        for (var i = 0; i < 40; i++)
        {
            var frame = new byte[6];
            for (var j = 0; j < frame.Length; j++)
            {
                frame[j] = (byte)((i * 7) + j);
            }

            expected.Add(frame);
            stream.AddRange(frame);
        }

        return (framing, stream.ToArray(), expected);
    }

    private static (FramingDef, byte[], List<byte[]>) BuildLengthField()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.LengthField,
            HeaderLength = 2,
            LengthOffset = 1,
            LengthSize = 1,
            Adjust = 2,
            MaxFrameLength = 256,
        };

        var expected = new List<byte[]>();
        var stream = new List<byte>();

        for (var i = 0; i < 40; i++)
        {
            var payload = 1 + (i % 17);
            var frame = new byte[payload + 2];
            frame[0] = 0xA5;
            frame[1] = (byte)payload;
            for (var j = 2; j < frame.Length; j++)
            {
                frame[j] = (byte)((i * 3) + j);
            }

            expected.Add(frame);
            stream.AddRange(frame);
        }

        return (framing, stream.ToArray(), expected);
    }

    private static (FramingDef, byte[], List<byte[]>) BuildDelimited()
    {
        var framing = new FramingDef
        {
            Mode = FramingMode.Delimiter,
            Delimiter = Vectors.Hex("0D 0A"),
            MaxFrameLength = 256,
        };

        var expected = new List<byte[]>();
        var stream = new List<byte>();

        for (var i = 0; i < 40; i++)
        {
            var length = 1 + (i % 13);
            var frame = new byte[length];
            for (var j = 0; j < length; j++)
            {
                // Stay clear of CR and LF so the payload cannot fake a terminator.
                frame[j] = (byte)(0x20 + ((i + j) % 90));
            }

            expected.Add(frame);
            stream.AddRange(frame);
            stream.AddRange(Vectors.Hex("0D 0A"));
        }

        return (framing, stream.ToArray(), expected);
    }
}
