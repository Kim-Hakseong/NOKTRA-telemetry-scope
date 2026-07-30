namespace Ts.Core.Definition;

/// <summary>Scalar wire types a channel can be decoded from.</summary>
public enum FieldType
{
    U8,
    S8,
    U16,
    S16,
    U32,
    S32,
    U64,
    S64,
    F32,
    F64,
}

public enum Endian
{
    Big,
    Little,
}

/// <summary>How a byte stream is cut into frames.</summary>
public enum FramingMode
{
    /// <summary>Every frame is the same number of bytes.</summary>
    Fixed,

    /// <summary>A field inside the header carries the length.</summary>
    LengthField,

    /// <summary>Frames end at a byte sequence.</summary>
    Delimiter,
}

public enum SourceKind
{
    None,
    Udp,
    Serial,
}

public static class FieldTypes
{
    public static int SizeOf(FieldType type) => type switch
    {
        FieldType.U8 or FieldType.S8 => 1,
        FieldType.U16 or FieldType.S16 => 2,
        FieldType.U32 or FieldType.S32 or FieldType.F32 => 4,
        FieldType.U64 or FieldType.S64 or FieldType.F64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown field type."),
    };

    public static bool IsFloating(FieldType type) => type is FieldType.F32 or FieldType.F64;

    public static string ToWireName(FieldType type) => type.ToString().ToLowerInvariant();
}

/// <summary>
/// One decoded quantity: where it sits in the frame, how the bytes become a number, and what
/// engineering range that number is expected to stay inside.
/// </summary>
public sealed class ChannelDef
{
    public required string Name { get; init; }

    /// <summary>Byte offset of the field from the start of the frame.</summary>
    public required int Offset { get; init; }

    public required FieldType Type { get; init; }

    public Endian Endian { get; init; } = Endian.Big;

    /// <summary>Scale factor: value = a x raw + b.</summary>
    public double A { get; init; } = 1.0;

    /// <summary>Offset term: value = a x raw + b.</summary>
    public double B { get; init; }

    public string Unit { get; init; } = string.Empty;

    /// <summary>Lower valid bound in engineering units, or null when unbounded.</summary>
    public double? Min { get; init; }

    /// <summary>Upper valid bound in engineering units, or null when unbounded.</summary>
    public double? Max { get; init; }

    /// <summary>Line the channel was declared on, for editor diagnostics.</summary>
    public int Line { get; init; }

    public int Size => FieldTypes.SizeOf(Type);

    /// <summary>First byte after the field. A frame must be at least this long to decode it.</summary>
    public int EndOffset => Offset + Size;

    public double Apply(double raw) => (A * raw) + B;

    public string Label => string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]";
}

/// <summary>Frame delimitation parameters. Which properties matter depends on <see cref="Mode"/>.</summary>
public sealed class FramingDef
{
    public required FramingMode Mode { get; init; }

    /// <summary>Fixed mode: the exact frame size in bytes.</summary>
    public int FrameLength { get; init; }

    /// <summary>
    /// Length-field mode: bytes that must be buffered before the length field can be read. Must
    /// cover the length field itself.
    /// </summary>
    public int HeaderLength { get; init; }

    /// <summary>Length-field mode: byte offset of the length field inside the frame.</summary>
    public int LengthOffset { get; init; }

    /// <summary>Length-field mode: width of the length field, 1 to 4 bytes.</summary>
    public int LengthSize { get; init; } = 1;

    public Endian LengthEndian { get; init; } = Endian.Big;

    /// <summary>
    /// Length-field mode: added to the encoded length to obtain the total frame size. Encodings
    /// differ on whether the header and trailer are counted, so the definition states it rather
    /// than the code assuming it.
    /// </summary>
    public int Adjust { get; init; }

    /// <summary>Delimiter mode: the terminating byte sequence.</summary>
    public byte[] Delimiter { get; init; } = Array.Empty<byte>();

    /// <summary>Delimiter mode: whether the delimiter bytes stay in the emitted frame.</summary>
    public bool KeepDelimiter { get; init; }

    /// <summary>Upper bound used to abandon a frame that never completes.</summary>
    public int MaxFrameLength { get; init; } = 65536;
}

/// <summary>Where live bytes come from. Optional — a definition can be used for replay only.</summary>
public sealed class SourceDef
{
    public SourceKind Kind { get; init; } = SourceKind.None;

    public int Port { get; init; }

    public string Host { get; init; } = "0.0.0.0";

    /// <summary>
    /// UDP only: treat each datagram as exactly one frame, which is what a sender normally means.
    /// Set false when frames are packed into, or split across, datagrams and the framing rules
    /// have to find the boundaries instead.
    /// </summary>
    public bool DatagramPerFrame { get; init; } = true;

    public string PortName { get; init; } = string.Empty;

    public int BaudRate { get; init; } = 115200;

    public int DataBits { get; init; } = 8;

    public string Parity { get; init; } = "none";

    public string StopBits { get; init; } = "one";
}

/// <summary>
/// A whole channel definition file: framing plus the channels decoded out of each frame.
/// </summary>
public sealed class ChannelSet
{
    public required string Name { get; init; }

    public required FramingDef Framing { get; init; }

    public required IReadOnlyList<ChannelDef> Channels { get; init; }

    public SourceDef Source { get; init; } = new();

    /// <summary>
    /// The exact text the set was parsed from. A recording embeds this verbatim so a file can
    /// always be replayed with the definition it was captured under, even if the definition on
    /// disk has since been edited.
    /// </summary>
    public string SourceText { get; init; } = string.Empty;

    /// <summary>Smallest frame that can carry every channel.</summary>
    public int MinimumFrameLength
    {
        get
        {
            var required = 0;
            foreach (var channel in Channels)
            {
                required = Math.Max(required, channel.EndOffset);
            }

            return required;
        }
    }

    public ChannelDef? Find(string name)
    {
        foreach (var channel in Channels)
        {
            if (string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return channel;
            }
        }

        return null;
    }
}
