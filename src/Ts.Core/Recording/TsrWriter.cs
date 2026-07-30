using System.Buffers.Binary;
using System.Text;
using Ts.Core.Definition;

namespace Ts.Core.Recording;

/// <summary>
/// Streams frames to a <c>.tsr</c> file as they arrive.
///
/// The channel definition is copied into the header at the moment recording starts, so the file
/// can always be replayed under the definition it was captured with even after the definition on
/// disk has been edited. That is the difference between an archive and a pile of bytes.
/// </summary>
public sealed class TsrWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _recordHeader = new byte[TsrFormat.RecordHeaderLength];
    private bool _disposed;

    public TsrWriter(Stream stream, ChannelSet definition, long startUnixMicros, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ArgumentNullException.ThrowIfNull(definition);
        _leaveOpen = leaveOpen;

        StartUnixMicros = startUnixMicros;
        WriteHeader(definition, startUnixMicros);
    }

    /// <summary>Wall-clock microseconds at which the capture began. Records are relative to it.</summary>
    public long StartUnixMicros { get; }

    public long RecordCount { get; private set; }

    public long BytesWritten { get; private set; }

    public static TsrWriter Create(string path, ChannelSet definition, long startUnixMicros)
    {
        var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return new TsrWriter(stream, definition, startUnixMicros);
    }

    /// <summary>
    /// Appends one frame. <paramref name="timestampMicros"/> is measured from the start of the
    /// capture, which keeps a long recording exact regardless of wall-clock adjustments underneath.
    /// </summary>
    public void Write(long timestampMicros, ReadOnlySpan<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (timestampMicros < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampMicros), timestampMicros, "Timestamps are relative to the start.");
        }

        if (frame.Length > TsrFormat.MaxFrameLength)
        {
            throw new ArgumentException(
                $"Frame of {frame.Length} bytes exceeds the {TsrFormat.MaxFrameLength} byte limit.",
                nameof(frame));
        }

        BinaryPrimitives.WriteInt64LittleEndian(_recordHeader, timestampMicros);
        BinaryPrimitives.WriteInt32LittleEndian(_recordHeader.AsSpan(8), frame.Length);

        _stream.Write(_recordHeader);
        _stream.Write(frame);

        RecordCount++;
        BytesWritten += TsrFormat.RecordHeaderLength + frame.Length;
    }

    public void Flush() => _stream.Flush();

    private void WriteHeader(ChannelSet definition, long startUnixMicros)
    {
        var text = Encoding.UTF8.GetBytes(definition.SourceText);
        if (text.Length > TsrFormat.MaxDefinitionLength)
        {
            throw new ArgumentException("Channel definition is too large to embed.", nameof(definition));
        }

        var header = new byte[TsrFormat.FixedHeaderLength];
        TsrFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(4), startUnixMicros);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), text.Length);

        _stream.Write(header);
        _stream.Write(text);
        BytesWritten = header.Length + text.Length;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Flush();

        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
