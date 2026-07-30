using System.Globalization;

namespace Ts.Core.Definition;

/// <summary>
/// Turns definition text into a validated <see cref="ChannelSet"/>.
///
/// Everything that could be ambiguous on the wire is stated by the file, never inferred: byte
/// order, whether a length field counts its own header, whether a delimiter is kept. When the file
/// does not say, the read fails with the line number rather than picking a convention.
/// </summary>
public static class ChannelSetReader
{
    public static ChannelSet Read(string text)
    {
        var root = Yaml.ParseDocument(text);

        var name = ReadString(root, "Untitled", "name");

        var framingNode = root.Find("framing")
            ?? throw new DefinitionException("Missing 'framing' section.", root.Line);
        var framing = ReadFraming(RequireMapping(framingNode, "framing"));

        var channelsNode = root.Find("channels")
            ?? throw new DefinitionException("Missing 'channels' section.", root.Line);
        if (channelsNode is not YamlSequence sequence || sequence.Items.Count == 0)
        {
            throw new DefinitionException(
                "'channels' must be a non-empty list of channel definitions.", channelsNode.Line);
        }

        var channels = new List<ChannelDef>(sequence.Items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sequence.Items)
        {
            var channel = ReadChannel(RequireMapping(item, "channels[]"));
            if (!seen.Add(channel.Name))
            {
                throw new DefinitionException($"Duplicate channel name '{channel.Name}'.", channel.Line);
            }

            channels.Add(channel);
        }

        var source = root.Find("source") is { } sourceNode
            ? ReadSource(RequireMapping(sourceNode, "source"))
            : new SourceDef();

        var set = new ChannelSet
        {
            Name = name,
            Framing = framing,
            Channels = channels,
            Source = source,
            SourceText = text,
        };

        Validate(set, framingNode.Line);
        return set;
    }

    public static ChannelSet ReadFile(string path) => Read(File.ReadAllText(path));

    private static void Validate(ChannelSet set, int framingLine)
    {
        if (set.Framing.Mode == FramingMode.Fixed && set.Framing.FrameLength < set.MinimumFrameLength)
        {
            throw new DefinitionException(
                $"Fixed frame length {set.Framing.FrameLength} is shorter than the " +
                $"{set.MinimumFrameLength} bytes the channels require.",
                framingLine);
        }

        foreach (var channel in set.Channels)
        {
            if (channel.EndOffset > set.Framing.MaxFrameLength)
            {
                throw new DefinitionException(
                    $"Channel '{channel.Name}' ends at byte {channel.EndOffset}, past the " +
                    $"maximum frame length {set.Framing.MaxFrameLength}.",
                    channel.Line);
            }
        }
    }

    private static FramingDef ReadFraming(YamlMapping mapping)
    {
        var modeNode = mapping.Find("mode")
            ?? throw new DefinitionException("Framing needs a 'mode'.", mapping.Line);
        var mode = ReadFramingMode(Scalar(modeNode, "framing.mode"));

        var maxFrameLength = ReadInt(mapping, 65536, "maxFrameLength", "maxFrameLen");
        if (maxFrameLength <= 0)
        {
            throw new DefinitionException("'maxFrameLength' must be positive.", mapping.Line);
        }

        switch (mode)
        {
            case FramingMode.Fixed:
            {
                var length = ReadInt(mapping, 0, "frameLength", "frameLen", "length");
                if (length <= 0)
                {
                    throw new DefinitionException(
                        "Fixed framing needs a positive 'frameLength'.", mapping.Line);
                }

                if (length > maxFrameLength)
                {
                    throw new DefinitionException(
                        $"'frameLength' {length} exceeds 'maxFrameLength' {maxFrameLength}.",
                        mapping.Line);
                }

                return new FramingDef
                {
                    Mode = mode,
                    FrameLength = length,
                    MaxFrameLength = maxFrameLength,
                };
            }

            case FramingMode.LengthField:
            {
                var headerLength = ReadInt(mapping, -1, "headerLength", "headerLen");
                var lengthOffset = ReadInt(mapping, -1, "lengthOffset", "lenOffset");
                var lengthSize = ReadInt(mapping, 1, "lengthSize", "lenSize", "size");
                var adjust = ReadInt(mapping, 0, "adjust");
                var lengthEndian = ReadEndian(mapping, Endian.Big, "lengthEndian", "lenEndian", "endian");

                if (lengthOffset < 0)
                {
                    throw new DefinitionException(
                        "Length-field framing needs 'lengthOffset'.", mapping.Line);
                }

                if (lengthSize is < 1 or > 4)
                {
                    throw new DefinitionException(
                        "'lengthSize' must be between 1 and 4 bytes.", mapping.Line);
                }

                if (headerLength < 0)
                {
                    throw new DefinitionException(
                        "Length-field framing needs 'headerLength'.", mapping.Line);
                }

                if (headerLength < lengthOffset + lengthSize)
                {
                    throw new DefinitionException(
                        $"'headerLength' {headerLength} does not reach the length field, which ends " +
                        $"at byte {lengthOffset + lengthSize}.",
                        mapping.Line);
                }

                return new FramingDef
                {
                    Mode = mode,
                    HeaderLength = headerLength,
                    LengthOffset = lengthOffset,
                    LengthSize = lengthSize,
                    LengthEndian = lengthEndian,
                    Adjust = adjust,
                    MaxFrameLength = maxFrameLength,
                };
            }

            case FramingMode.Delimiter:
            {
                var delimiterNode = mapping.Find("delimiter")
                    ?? throw new DefinitionException(
                        "Delimiter framing needs a 'delimiter'.", mapping.Line);
                var delimiter = ReadBytes(Scalar(delimiterNode, "framing.delimiter"), delimiterNode.Line);
                if (delimiter.Length == 0)
                {
                    throw new DefinitionException("'delimiter' cannot be empty.", delimiterNode.Line);
                }

                return new FramingDef
                {
                    Mode = mode,
                    Delimiter = delimiter,
                    KeepDelimiter = ReadBool(mapping, false, "keepDelimiter"),
                    MaxFrameLength = maxFrameLength,
                };
            }

            default:
                throw new DefinitionException($"Unsupported framing mode '{mode}'.", mapping.Line);
        }
    }

    private static ChannelDef ReadChannel(YamlMapping mapping)
    {
        var name = ReadString(mapping, string.Empty, "name");
        if (name.Length == 0)
        {
            throw new DefinitionException("Every channel needs a 'name'.", mapping.Line);
        }

        var offsetNode = mapping.Find("offset")
            ?? throw new DefinitionException($"Channel '{name}' needs an 'offset'.", mapping.Line);
        var offset = ParseInt(Scalar(offsetNode, "offset"), offsetNode.Line);
        if (offset < 0)
        {
            throw new DefinitionException(
                $"Channel '{name}' has a negative offset.", offsetNode.Line);
        }

        var typeNode = mapping.Find("type")
            ?? throw new DefinitionException($"Channel '{name}' needs a 'type'.", mapping.Line);
        var type = ReadFieldType(Scalar(typeNode, "type"), typeNode.Line);

        var min = ReadNullableDouble(mapping, "min", "minValue");
        var max = ReadNullableDouble(mapping, "max", "maxValue");
        if (min is { } lo && max is { } hi && lo > hi)
        {
            throw new DefinitionException(
                $"Channel '{name}' has min {lo} greater than max {hi}.", mapping.Line);
        }

        return new ChannelDef
        {
            Name = name,
            Offset = offset,
            Type = type,
            Endian = ReadEndian(mapping, Endian.Big, "endian", "byteOrder"),
            A = ReadDouble(mapping, 1.0, "a", "scale"),
            B = ReadDouble(mapping, 0.0, "b", "bias"),
            Unit = ReadString(mapping, string.Empty, "unit", "units"),
            Min = min,
            Max = max,
            Line = mapping.Line,
        };
    }

    private static SourceDef ReadSource(YamlMapping mapping)
    {
        var kindText = ReadString(mapping, "none", "type", "kind");
        var kind = kindText.ToLowerInvariant() switch
        {
            "none" or "" => SourceKind.None,
            "udp" => SourceKind.Udp,
            "serial" or "com" or "uart" => SourceKind.Serial,
            _ => throw new DefinitionException(
                $"Unknown source type '{kindText}'. Expected udp, serial or none.", mapping.Line),
        };

        return new SourceDef
        {
            Kind = kind,
            Port = ReadInt(mapping, 0, "port", "udpPort"),
            Host = ReadString(mapping, "0.0.0.0", "host", "bind", "address"),
            DatagramPerFrame = ReadBool(mapping, true, "datagramPerFrame", "datagramFraming"),
            PortName = ReadString(mapping, string.Empty, "portName", "serialPort", "com"),
            BaudRate = ReadInt(mapping, 115200, "baudRate", "baud"),
            DataBits = ReadInt(mapping, 8, "dataBits"),
            Parity = ReadString(mapping, "none", "parity"),
            StopBits = ReadString(mapping, "one", "stopBits"),
        };
    }

    // --- scalar helpers

    private static YamlMapping RequireMapping(YamlNode node, string path)
        => node as YamlMapping
           ?? throw new DefinitionException($"'{path}' must be a mapping of keys.", node.Line);

    private static string Scalar(YamlNode node, string path)
        => node is YamlScalar scalar
            ? scalar.Value
            : throw new DefinitionException($"'{path}' must be a single value.", node.Line);

    private static string ReadString(YamlMapping mapping, string fallback, params string[] keys)
    {
        var node = mapping.FindAny(keys);
        return node is null ? fallback : Scalar(node, keys[0]);
    }

    private static int ReadInt(YamlMapping mapping, int fallback, params string[] keys)
    {
        var node = mapping.FindAny(keys);
        return node is null ? fallback : ParseInt(Scalar(node, keys[0]), node.Line);
    }

    private static double ReadDouble(YamlMapping mapping, double fallback, params string[] keys)
        => ReadNullableDouble(mapping, keys) ?? fallback;

    private static double? ReadNullableDouble(YamlMapping mapping, params string[] keys)
    {
        var node = mapping.FindAny(keys);
        if (node is null)
        {
            return null;
        }

        var text = Scalar(node, keys[0]);
        if (text.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new DefinitionException($"'{text}' is not a number.", node.Line);
        }

        return value;
    }

    private static bool ReadBool(YamlMapping mapping, bool fallback, params string[] keys)
    {
        var node = mapping.FindAny(keys);
        if (node is null)
        {
            return fallback;
        }

        return Scalar(node, keys[0]).ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            var other => throw new DefinitionException(
                $"'{other}' is not a true/false value.", node.Line),
        };
    }

    private static Endian ReadEndian(YamlMapping mapping, Endian fallback, params string[] keys)
    {
        var node = mapping.FindAny(keys);
        if (node is null)
        {
            return fallback;
        }

        return Scalar(node, keys[0]).ToLowerInvariant() switch
        {
            "big" or "be" or "big-endian" or "msb" => Endian.Big,
            "little" or "le" or "little-endian" or "lsb" => Endian.Little,
            var other => throw new DefinitionException(
                $"Unknown byte order '{other}'. Expected big or little.", node.Line),
        };
    }

    private static int ParseInt(string text, int line)
    {
        text = text.Trim();
        var negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex
                : throw new DefinitionException($"'{text}' is not a number.", line)
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
                ? dec
                : throw new DefinitionException($"'{text}' is not a number.", line);

        return negative ? -parsed : parsed;
    }

    private static FieldType ReadFieldType(string text, int line)
    {
        return text.Trim().ToLowerInvariant() switch
        {
            "u8" or "uint8" or "byte" => FieldType.U8,
            "s8" or "i8" or "int8" or "sbyte" => FieldType.S8,
            "u16" or "uint16" or "ushort" => FieldType.U16,
            "s16" or "i16" or "int16" or "short" => FieldType.S16,
            "u32" or "uint32" or "uint" => FieldType.U32,
            "s32" or "i32" or "int32" or "int" => FieldType.S32,
            "u64" or "uint64" or "ulong" => FieldType.U64,
            "s64" or "i64" or "int64" or "long" => FieldType.S64,
            "f32" or "float" or "float32" or "single" => FieldType.F32,
            "f64" or "double" or "float64" => FieldType.F64,
            _ => throw new DefinitionException(
                $"Unknown field type '{text}'. Expected one of " +
                "u8 s8 u16 s16 u32 s32 u64 s64 f32 f64.",
                line),
        };
    }

    /// <summary>
    /// Reads a byte sequence written either as hex ("0D 0A", "0x0D,0x0A") or as literal text
    /// ("\n" once the YAML layer has unescaped it). Hex wins when every token looks like a byte,
    /// which is how delimiters are written in practice.
    /// </summary>
    internal static byte[] ReadBytes(string text, int line)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var tokens = text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var allHex = true;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                token = token[2..];
            }

            if (token.Length is 1 or 2 &&
                byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                bytes[i] = value;
            }
            else
            {
                allHex = false;
                break;
            }
        }

        if (allHex)
        {
            return bytes;
        }

        var literal = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 0xFF)
            {
                throw new DefinitionException(
                    "A delimiter written as text must be single-byte characters; " +
                    "write it as hex bytes instead.",
                    line);
            }

            literal[i] = (byte)text[i];
        }

        return literal;
    }

    private static FramingMode ReadFramingMode(string text)
    {
        return text.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty) switch
        {
            "fixed" or "fixedlength" => FramingMode.Fixed,
            "lengthfield" or "length" or "len" => FramingMode.LengthField,
            "delimiter" or "delimited" or "terminator" => FramingMode.Delimiter,
            _ => throw new DefinitionException(
                $"Unknown framing mode '{text}'. Expected fixed, lengthField or delimiter.", 0),
        };
    }
}
