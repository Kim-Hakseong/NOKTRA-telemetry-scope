using System.Text;
using Ts.Core.Definition;
using Ts.Core.Recording;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>
/// The frozen recording vectors: a thousand records survive a write/read round trip byte-for-byte,
/// and a file cut mid-record still yields everything up to the last complete one.
/// </summary>
public class TsrRoundTripTests : IDisposable
{
    private const int TotalRecords = 1000;
    private const int RecordsBeforeTheCut = 997;

    private readonly string _directory = Directory.CreateTempSubdirectory("tsr-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ChannelSet Definition => ChannelSetReader.Read("""
        name: Recorder vector
        framing:
          mode: lengthField
          headerLength: 2
          lengthOffset: 1
          lengthSize: 1
          adjust: 2
        channels:
          - name: Counter
            offset: 2
            type: u16
            a: 0.5
            unit: rpm
        """);

    /// <summary>
    /// Frames of varying length whose contents are a function of the index, so the assertion can
    /// re-derive what byte should be where instead of trusting a stored copy.
    /// </summary>
    private static byte[] FrameAt(int index)
    {
        var payload = 2 + (index % 23);
        var frame = new byte[payload + 2];
        frame[0] = 0xA5;
        frame[1] = (byte)payload;
        for (var i = 2; i < frame.Length; i++)
        {
            frame[i] = (byte)((index * 31) + i);
        }

        return frame;
    }

    private static long TimestampAt(int index) => (index * 1000L) + ((index % 7) * 137);

    private string WriteThousandRecords(out long byteOffsetAfter997)
    {
        var path = Path.Combine(_directory, "capture.tsr");
        var definition = Definition;

        using (var writer = TsrWriter.Create(path, definition, startUnixMicros: 1_700_000_000_000_000))
        {
            for (var i = 0; i < TotalRecords; i++)
            {
                writer.Write(TimestampAt(i), FrameAt(i));
            }
        }

        var offset = (long)TsrFormat.FixedHeaderLength
                     + Encoding.UTF8.GetByteCount(definition.SourceText);
        for (var i = 0; i < RecordsBeforeTheCut; i++)
        {
            offset += TsrFormat.RecordHeaderLength + FrameAt(i).Length;
        }

        byteOffsetAfter997 = offset;
        return path;
    }

    [Fact]
    public void ThousandRecords_RoundTripExactly()
    {
        var path = WriteThousandRecords(out _);

        var file = TsrReader.ReadFile(path);

        Assert.False(file.Truncated);
        Assert.Equal(TotalRecords, file.Records.Count);
        Assert.Equal(1_700_000_000_000_000, file.StartUnixMicros);

        for (var i = 0; i < TotalRecords; i++)
        {
            Assert.Equal(TimestampAt(i), file.Records[i].TimestampMicros);
            Assert.Equal(FrameAt(i), file.Records[i].Frame);
        }
    }

    [Fact]
    public void ACutInTheMiddleOfARecord_Recovers997()
    {
        var path = WriteThousandRecords(out var offsetAfter997);

        // Cut five bytes into record 998's header: the worst case, where even the length of the
        // record being written was not fully on disk.
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(offsetAfter997 + 5);
        }

        var recovered = TsrReader.ReadFile(path);

        Assert.True(recovered.Truncated);
        Assert.Equal(RecordsBeforeTheCut, recovered.Records.Count);

        for (var i = 0; i < RecordsBeforeTheCut; i++)
        {
            Assert.Equal(TimestampAt(i), recovered.Records[i].TimestampMicros);
            Assert.Equal(FrameAt(i), recovered.Records[i].Frame);
        }
    }

    [Fact]
    public void ACutInTheMiddleOfAFramePayload_KeepsTheRecordsBeforeIt()
    {
        var path = WriteThousandRecords(out var offsetAfter997);

        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            // Past record 998's header but short of its payload.
            file.SetLength(offsetAfter997 + TsrFormat.RecordHeaderLength + 1);
        }

        var recovered = TsrReader.ReadFile(path);

        Assert.True(recovered.Truncated);
        Assert.Equal(RecordsBeforeTheCut, recovered.Records.Count);
    }

    [Fact]
    public void ACutExactlyOnARecordBoundary_IsNotReportedAsTruncated()
    {
        var path = WriteThousandRecords(out var offsetAfter997);

        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(offsetAfter997);
        }

        var recovered = TsrReader.ReadFile(path);

        Assert.False(recovered.Truncated);
        Assert.Equal(RecordsBeforeTheCut, recovered.Records.Count);
    }

    [Fact]
    public void TheDefinitionTravelsWithTheRecording()
    {
        var path = WriteThousandRecords(out _);

        var file = TsrReader.ReadFile(path);

        Assert.Equal(Definition.SourceText, file.DefinitionText);

        var reparsed = file.ReadDefinition();
        Assert.Equal("Recorder vector", reparsed.Name);
        Assert.Equal("Counter", reparsed.Channels[0].Name);
        Assert.Equal(0.5, reparsed.Channels[0].A);
    }

    [Fact]
    public void AFileThatIsNotARecordingIsRejectedByName()
    {
        var path = Path.Combine(_directory, "not-a-capture.bin");
        File.WriteAllBytes(path, "PKand then some"u8.ToArray());

        var error = Assert.Throws<TsrFormatException>(() => TsrReader.ReadFile(path));
        Assert.Contains("TSR1", error.Message);
    }

    [Fact]
    public void AHeaderCutBeforeTheDefinitionIsRejected()
    {
        var path = WriteThousandRecords(out _);

        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(TsrFormat.FixedHeaderLength + 4);
        }

        Assert.Throws<TsrFormatException>(() => TsrReader.ReadFile(path));
    }

    [Fact]
    public void EmptyFrames_AreLegalRecords()
    {
        var path = Path.Combine(_directory, "empty-frames.tsr");

        using (var writer = TsrWriter.Create(path, Definition, 0))
        {
            writer.Write(0, ReadOnlySpan<byte>.Empty);
            writer.Write(100, new byte[] { 1 });
        }

        var file = TsrReader.ReadFile(path);

        Assert.Equal(2, file.Records.Count);
        Assert.Empty(file.Records[0].Frame);
    }

    [Fact]
    public void WriterCountsWhatItWrote()
    {
        var path = Path.Combine(_directory, "counted.tsr");

        using var writer = TsrWriter.Create(path, Definition, 0);
        var headerBytes = writer.BytesWritten;

        writer.Write(0, new byte[10]);
        writer.Write(1, new byte[20]);

        Assert.Equal(2, writer.RecordCount);
        Assert.Equal(headerBytes + (2 * TsrFormat.RecordHeaderLength) + 30, writer.BytesWritten);
    }

    [Fact]
    public void NegativeTimestampsAreRefusedAtTheSource()
    {
        var path = Path.Combine(_directory, "negative.tsr");

        using var writer = TsrWriter.Create(path, Definition, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Write(-1, new byte[1]));
    }
}
