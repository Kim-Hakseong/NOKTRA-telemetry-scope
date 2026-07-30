using Ts.Core.Definition;
using Xunit;

namespace Ts.Core.Tests;

public class ChannelSetReaderTests
{
    [Fact]
    public void ReadsFramingChannelsAndSource()
    {
        var set = ChannelSetReader.Read("""
            # A vehicle bus, roughly
            name: Vehicle
            source:
              type: udp
              port: 5005
            framing:
              mode: fixed
              frameLength: 12
            channels:
              - name: Airspeed
                offset: 0
                type: s16
                endian: big
                a: 0.1
                b: 0
                unit: m/s
                min: 0
                max: 300
              - name: Battery
                offset: 2
                type: u16
                a: 0.01
                unit: V
            """);

        Assert.Equal("Vehicle", set.Name);
        Assert.Equal(FramingMode.Fixed, set.Framing.Mode);
        Assert.Equal(12, set.Framing.FrameLength);
        Assert.Equal(SourceKind.Udp, set.Source.Kind);
        Assert.Equal(5005, set.Source.Port);

        Assert.Equal(2, set.Channels.Count);
        var airspeed = set.Channels[0];
        Assert.Equal("Airspeed", airspeed.Name);
        Assert.Equal(FieldType.S16, airspeed.Type);
        Assert.Equal(0.1, airspeed.A);
        Assert.Equal("m/s", airspeed.Unit);
        Assert.Equal(0, airspeed.Min);
        Assert.Equal(300, airspeed.Max);

        // Unset bounds stay unset rather than defaulting to the type's representable range.
        Assert.Null(set.Channels[1].Min);
        Assert.Equal(4, set.MinimumFrameLength);
    }

    [Fact]
    public void KeepsTheSourceTextForEmbeddingInRecordings()
    {
        const string text = """
            name: Keep
            framing:
              mode: fixed
              frameLength: 2
            channels:
              - name: A
                offset: 0
                type: u16
            """;

        Assert.Equal(text, ChannelSetReader.Read(text).SourceText);
    }

    [Theory]
    [InlineData("u8", FieldType.U8)]
    [InlineData("int16", FieldType.S16)]
    [InlineData("ushort", FieldType.U16)]
    [InlineData("float", FieldType.F32)]
    [InlineData("double", FieldType.F64)]
    [InlineData("F32", FieldType.F32)]
    public void AcceptsTheCommonSpellingsOfEachType(string spelling, FieldType expected)
    {
        var set = ChannelSetReader.Read($"""
            framing:
              mode: fixed
              frameLength: 16
            channels:
              - name: A
                offset: 0
                type: {spelling}
            """);

        Assert.Equal(expected, set.Channels[0].Type);
    }

    [Theory]
    [InlineData("scale", "a")]
    [InlineData("bias", "b")]
    public void AcceptsScaleAliases(string alias, string canonical)
    {
        var set = ChannelSetReader.Read($"""
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: 0
                type: u16
                {alias}: 3
            """);

        var actual = canonical == "a" ? set.Channels[0].A : set.Channels[0].B;
        Assert.Equal(3.0, actual);
    }

    [Fact]
    public void HexAndDecimalOffsetsAreBothAccepted()
    {
        var set = ChannelSetReader.Read("""
            framing:
              mode: fixed
              frameLength: 32
            channels:
              - name: A
                offset: 0x10
                type: u8
            """);

        Assert.Equal(16, set.Channels[0].Offset);
    }

    [Fact]
    public void DelimiterAcceptsHexBytes()
    {
        var set = ChannelSetReader.Read("""
            framing:
              mode: delimiter
              delimiter: 0D 0A
            channels:
              - name: A
                offset: 0
                type: u8
            """);

        Assert.Equal(new byte[] { 0x0D, 0x0A }, set.Framing.Delimiter);
        Assert.False(set.Framing.KeepDelimiter);
    }

    [Fact]
    public void RejectsAFixedFrameShorterThanItsChannels()
    {
        var error = Assert.Throws<DefinitionException>(() => ChannelSetReader.Read("""
            framing:
              mode: fixed
              frameLength: 2
            channels:
              - name: A
                offset: 0
                type: u32
            """));

        Assert.Contains("shorter than", error.Message);
    }

    [Fact]
    public void RejectsALengthFieldHeaderThatCannotReachTheLengthField()
    {
        var error = Assert.Throws<DefinitionException>(() => ChannelSetReader.Read("""
            framing:
              mode: lengthField
              headerLength: 1
              lengthOffset: 2
              lengthSize: 2
            channels:
              - name: A
                offset: 0
                type: u8
            """));

        Assert.Contains("does not reach the length field", error.Message);
    }

    [Fact]
    public void RejectsDuplicateChannelNames()
    {
        var error = Assert.Throws<DefinitionException>(() => ChannelSetReader.Read("""
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: 0
                type: u8
              - name: a
                offset: 1
                type: u8
            """));

        Assert.Contains("Duplicate channel name", error.Message);
    }

    [Fact]
    public void RejectsAnInvertedRange()
    {
        var error = Assert.Throws<DefinitionException>(() => ChannelSetReader.Read("""
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: 0
                type: u8
                min: 100
                max: 10
            """));

        Assert.Contains("greater than max", error.Message);
    }

    [Fact]
    public void RejectsAnUnknownFieldTypeAndSaysWhatIsAllowed()
    {
        var error = Assert.Throws<DefinitionException>(() => ChannelSetReader.Read("""
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: 0
                type: bcd
            """));

        Assert.Contains("Unknown field type", error.Message);
        Assert.Contains("u16", error.Message);
    }

    [Fact]
    public void ReportsTheLineOfTheOffendingEntry()
    {
        var error = Assert.Throws<DefinitionException>(() => ChannelSetReader.Read("""
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: -1
                type: u8
            """));

        Assert.Equal(6, error.Line);
    }

    [Fact]
    public void MissingSectionsAreNamedNotGuessed()
    {
        Assert.Contains("Missing 'framing'", Assert.Throws<DefinitionException>(
            () => ChannelSetReader.Read("name: x")).Message);

        Assert.Contains("Missing 'channels'", Assert.Throws<DefinitionException>(
            () => ChannelSetReader.Read("""
                framing:
                  mode: fixed
                  frameLength: 4
                """)).Message);
    }
}
