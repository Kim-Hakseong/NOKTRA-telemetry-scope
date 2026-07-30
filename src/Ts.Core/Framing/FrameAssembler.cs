using System.Buffers.Binary;
using Ts.Core.Definition;

namespace Ts.Core.Framing;

/// <summary>Receives one complete frame. The span is only valid for the duration of the call.</summary>
public delegate void FrameHandler(ReadOnlySpan<byte> frame);

/// <summary>
/// Cuts a byte stream into frames according to a <see cref="FramingDef"/>.
///
/// The assembler is a pure function of the bytes it has been given, not of how they arrived: the
/// same stream fed one byte at a time and fed in one block produces the same frames. That
/// invariance is the whole point — a serial line delivers arbitrary fragments, and a recording
/// replayed later delivers different ones, yet both must decode identically.
///
/// When the stream does not make sense (a length field that cannot be right, a delimiter that
/// never arrives) the assembler resynchronises by dropping bytes and counting them, rather than
/// stalling or guessing a frame boundary.
/// </summary>
public sealed class FrameAssembler
{
    private const int InitialCapacity = 4096;

    private readonly FramingDef _framing;
    private byte[] _buffer;
    private int _start;
    private int _end;

    /// <summary>Bytes of the current candidate frame already searched for a delimiter.</summary>
    private int _scanned;

    public FrameAssembler(FramingDef framing)
    {
        _framing = framing ?? throw new ArgumentNullException(nameof(framing));
        _buffer = new byte[Math.Max(InitialCapacity, Math.Min(framing.MaxFrameLength, 1 << 20))];
    }

    /// <summary>Bytes held back waiting for the rest of a frame.</summary>
    public int Buffered => _end - _start;

    /// <summary>Bytes dropped during resynchronisation since the last <see cref="Reset"/>.</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Frames emitted since the last <see cref="Reset"/>.</summary>
    public long FrameCount { get; private set; }

    public void Reset()
    {
        _start = 0;
        _end = 0;
        _scanned = 0;
        DiscardedBytes = 0;
        FrameCount = 0;
    }

    /// <summary>
    /// Adds received bytes and reports every frame they complete, in order.
    /// </summary>
    /// <returns>The number of frames emitted by this call.</returns>
    public int Push(ReadOnlySpan<byte> data, FrameHandler onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);

        Append(data);

        var emitted = _framing.Mode switch
        {
            FramingMode.Fixed => DrainFixed(onFrame),
            FramingMode.LengthField => DrainLengthField(onFrame),
            FramingMode.Delimiter => DrainDelimiter(onFrame),
            _ => throw new InvalidOperationException($"Unsupported framing mode {_framing.Mode}."),
        };

        Compact();
        FrameCount += emitted;
        return emitted;
    }

    /// <summary>Convenience overload for callers that want to keep the frames.</summary>
    public List<byte[]> Push(ReadOnlySpan<byte> data)
    {
        var frames = new List<byte[]>();
        Push(data, frame => frames.Add(frame.ToArray()));
        return frames;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        EnsureCapacityFor(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    private void EnsureCapacityFor(int extra)
    {
        if (_end + extra <= _buffer.Length)
        {
            return;
        }

        // Sliding the live bytes down is usually enough; only grow when the frame itself is big.
        if (_start > 0)
        {
            Array.Copy(_buffer, _start, _buffer, 0, Buffered);
            _end -= _start;
            _start = 0;
        }

        if (_end + extra <= _buffer.Length)
        {
            return;
        }

        var capacity = _buffer.Length;
        while (capacity < _end + extra)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }

    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        if (_start == _end)
        {
            _start = 0;
            _end = 0;
            return;
        }

        if (_start >= _buffer.Length / 2)
        {
            Array.Copy(_buffer, _start, _buffer, 0, Buffered);
            _end -= _start;
            _start = 0;
        }
    }

    private int DrainFixed(FrameHandler onFrame)
    {
        var length = _framing.FrameLength;
        var emitted = 0;

        while (Buffered >= length)
        {
            onFrame(_buffer.AsSpan(_start, length));
            _start += length;
            emitted++;
        }

        return emitted;
    }

    private int DrainLengthField(FrameHandler onFrame)
    {
        var emitted = 0;

        while (Buffered >= _framing.HeaderLength)
        {
            var declared = ReadLength(_buffer.AsSpan(_start + _framing.LengthOffset, _framing.LengthSize));
            var total = declared + _framing.Adjust;

            if (total < _framing.HeaderLength || total > _framing.MaxFrameLength)
            {
                // The length cannot describe a real frame, so this is not a frame boundary. Step
                // one byte and look again — the only resynchronisation that cannot skip a valid
                // frame start.
                _start++;
                DiscardedBytes++;
                continue;
            }

            if (Buffered < total)
            {
                break;
            }

            onFrame(_buffer.AsSpan(_start, total));
            _start += total;
            emitted++;
        }

        return emitted;
    }

    private int ReadLength(ReadOnlySpan<byte> bytes)
    {
        var big = _framing.LengthEndian == Endian.Big;

        return bytes.Length switch
        {
            1 => bytes[0],
            2 => big ? BinaryPrimitives.ReadUInt16BigEndian(bytes)
                     : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            3 => big ? (bytes[0] << 16) | (bytes[1] << 8) | bytes[2]
                     : (bytes[2] << 16) | (bytes[1] << 8) | bytes[0],
            4 => (int)Math.Min(
                big ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
                    : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                int.MaxValue),
            _ => throw new InvalidOperationException($"Bad length width {bytes.Length}."),
        };
    }

    private int DrainDelimiter(FrameHandler onFrame)
    {
        var delimiter = _framing.Delimiter;
        var emitted = 0;

        while (true)
        {
            // Resume the search just far enough back to catch a delimiter split across two pushes.
            var from = Math.Max(0, _scanned - (delimiter.Length - 1));
            var window = _buffer.AsSpan(_start + from, Buffered - from);
            var hit = window.IndexOf(delimiter);

            if (hit < 0)
            {
                _scanned = Math.Max(0, Buffered - (delimiter.Length - 1));

                if (Buffered > _framing.MaxFrameLength)
                {
                    // Nothing in this much data terminated a frame; keep only enough tail to close
                    // a delimiter that may be arriving right now.
                    var keep = delimiter.Length - 1;
                    DiscardedBytes += Buffered - keep;
                    _start = _end - keep;
                    _scanned = 0;
                }

                break;
            }

            var contentLength = from + hit + (_framing.KeepDelimiter ? delimiter.Length : 0);
            onFrame(_buffer.AsSpan(_start, contentLength));

            _start += from + hit + delimiter.Length;
            _scanned = 0;
            emitted++;
        }

        return emitted;
    }
}
