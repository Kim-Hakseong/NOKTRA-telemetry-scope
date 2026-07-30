using System.Buffers.Binary;
using System.Text;
using Ts.Core.Definition;

namespace Ts.Core.Recording;

/// <summary>
/// Reads a <c>.tsr</c> capture back, stopping cleanly at the last complete record.
///
/// A recording that was cut short — power loss, a killed process, a full disk — is exactly the one
/// worth reading, so a torn tail is a normal outcome and not an exception. <see cref="Truncated"/>
/// says whether it happened; the records before it are returned either way.
/// </summary>
public sealed class TsrReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _recordHeader = new byte[TsrFormat.RecordHeaderLength];
    private bool _disposed;

    public TsrReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;

        var header = new byte[TsrFormat.FixedHeaderLength];
        if (!ReadExactly(header))
        {
            throw new TsrFormatException("Not a recording: the file is shorter than its header.");
        }

        if (!header.AsSpan(0, 4).SequenceEqual(TsrFormat.Magic))
        {
            throw new TsrFormatException("Not a recording: the file does not start with 'TSR1'.");
        }

        StartUnixMicros = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(4));

        var definitionLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (definitionLength < 0 || definitionLength > TsrFormat.MaxDefinitionLength)
        {
            throw new TsrFormatException(
                $"Recording header declares an impossible definition length of {definitionLength}.");
        }

        var definitionBytes = new byte[definitionLength];
        if (!ReadExactly(definitionBytes))
        {
            throw new TsrFormatException("Recording header is incomplete: the definition is cut off.");
        }

        DefinitionText = Encoding.UTF8.GetString(definitionBytes);
    }

    /// <summary>Wall-clock microseconds at which the capture began.</summary>
    public long StartUnixMicros { get; }

    /// <summary>The channel definition as embedded, verbatim.</summary>
    public string DefinitionText { get; }

    /// <summary>True once a partial record has been met, i.e. the file ends mid-record.</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// Parses the embedded definition. Kept separate from the constructor so a file with a
    /// definition this build cannot read still yields its frames and its raw definition text.
    /// </summary>
    public ChannelSet ReadDefinition() => ChannelSetReader.Read(DefinitionText);

    /// <summary>
    /// Streams the records in order. Enumeration ends at the first incomplete record, setting
    /// <see cref="Truncated"/>.
    /// </summary>
    public IEnumerable<TsrRecord> ReadRecords()
    {
        while (true)
        {
            var headerBytes = Fill(_recordHeader);
            if (headerBytes < _recordHeader.Length)
            {
                // Nothing left is a clean end; a partial header means the file was cut mid-record.
                Truncated |= headerBytes > 0;
                yield break;
            }

            var timestamp = BinaryPrimitives.ReadInt64LittleEndian(_recordHeader);
            var length = BinaryPrimitives.ReadInt32LittleEndian(_recordHeader.AsSpan(8));

            if (timestamp < 0 || length < 0 || length > TsrFormat.MaxFrameLength)
            {
                // The header is not plausible, so these bytes are torn rather than a record.
                Truncated = true;
                yield break;
            }

            var frame = new byte[length];
            if (Fill(frame) < length)
            {
                Truncated = true;
                yield break;
            }

            yield return new TsrRecord(timestamp, frame);
        }
    }

    public static TsrFile ReadFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        using var reader = new TsrReader(stream);

        var records = reader.ReadRecords().ToList();

        return new TsrFile
        {
            Path = path,
            DefinitionText = reader.DefinitionText,
            StartUnixMicros = reader.StartUnixMicros,
            Records = records,
            Truncated = reader.Truncated,
        };
    }

    private bool ReadExactly(Span<byte> destination) => Fill(destination) == destination.Length;

    /// <summary>
    /// Reads until the buffer is full or the stream ends, returning how much arrived. The count
    /// matters: a short read of zero is a clean end of file, a short read of anything else is a
    /// torn record.
    /// </summary>
    private int Fill(Span<byte> destination)
    {
        var filled = 0;
        while (filled < destination.Length)
        {
            var read = _stream.Read(destination[filled..]);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}

/// <summary>A whole recording held in memory: what was captured, and under which definition.</summary>
public sealed class TsrFile
{
    public required string Path { get; init; }

    public required string DefinitionText { get; init; }

    public required long StartUnixMicros { get; init; }

    public required IReadOnlyList<TsrRecord> Records { get; init; }

    /// <summary>True when the file ended mid-record and the tail was recovered up to that point.</summary>
    public required bool Truncated { get; init; }

    public ChannelSet ReadDefinition() => ChannelSetReader.Read(DefinitionText);

    /// <summary>Span from the first to the last record, in microseconds.</summary>
    public long DurationMicros => Records.Count == 0 ? 0 : Records[^1].TimestampMicros;

    public long TotalFrameBytes
    {
        get
        {
            long total = 0;
            foreach (var record in Records)
            {
                total += record.Frame.Length;
            }

            return total;
        }
    }
}
