using System.Text;

namespace Ts.Core.Definition;

/// <summary>
/// A node in a parsed definition document: a scalar, a mapping, or a sequence.
/// </summary>
public abstract class YamlNode
{
    protected YamlNode(int line) => Line = line;

    /// <summary>1-based line the node started on. Used for error reporting.</summary>
    public int Line { get; }
}

public sealed class YamlScalar : YamlNode
{
    public YamlScalar(string value, int line) : base(line) => Value = value;

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed class YamlMapping : YamlNode
{
    // Insertion-ordered so error messages and round-tripped output follow the file, and
    // case-insensitive because "MinValue" and "minValue" are the same intent from a human.
    private readonly List<string> _order = new();
    private readonly Dictionary<string, YamlNode> _entries = new(StringComparer.OrdinalIgnoreCase);

    public YamlMapping(int line) : base(line) { }

    public IReadOnlyList<string> Keys => _order;

    public int Count => _order.Count;

    internal void Add(string key, YamlNode value)
    {
        if (_entries.ContainsKey(key))
        {
            throw new DefinitionException($"Duplicate key '{key}'.", value.Line);
        }

        _order.Add(key);
        _entries[key] = value;
    }

    public YamlNode? Find(string key) => _entries.TryGetValue(key, out var node) ? node : null;

    /// <summary>First present alias, or null. Lets a schema accept 'a' and 'scale' for one field.</summary>
    public YamlNode? FindAny(params string[] keys)
    {
        foreach (var key in keys)
        {
            var node = Find(key);
            if (node is not null)
            {
                return node;
            }
        }

        return null;
    }
}

public sealed class YamlSequence : YamlNode
{
    public YamlSequence(int line) : base(line) { }

    public List<YamlNode> Items { get; } = new();
}

/// <summary>
/// A deliberately small YAML reader covering exactly the subset a channel definition needs:
/// block mappings, block sequences, plain and quoted scalars, and comments.
///
/// Writing this rather than taking a dependency keeps the dependency list to the framework and
/// the UI toolkit, and the subset is also a feature: anchors, flow collections, multi-line scalars
/// and implicit typing are the parts of YAML that make a definition file hard to review, and none
/// of them are accepted here.
/// </summary>
public static class Yaml
{
    public static YamlMapping ParseDocument(string text)
    {
        var lines = Scan(text);
        if (lines.Count == 0)
        {
            return new YamlMapping(1);
        }

        var parser = new Parser(lines);
        var root = parser.ParseBlock(lines[0].Indent);
        parser.ExpectEnd();

        if (root is not YamlMapping mapping)
        {
            throw new DefinitionException("The document root must be a mapping of keys.", root.Line);
        }

        return mapping;
    }

    private sealed class Line
    {
        public Line(int indent, string text, int number)
        {
            Indent = indent;
            Text = text;
            Number = number;
        }

        public int Indent { get; set; }

        public string Text { get; set; }

        public int Number { get; }
    }

    private static List<Line> Scan(string text)
    {
        var result = new List<Line>();
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            var number = i + 1;

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (indent < line.Length && line[indent] == '\t')
            {
                throw new DefinitionException("Tabs cannot be used for indentation.", number);
            }

            var content = StripComment(line[indent..]).TrimEnd();
            if (content.Length == 0)
            {
                continue;
            }

            result.Add(new Line(indent, content, number));
        }

        return result;
    }

    /// <summary>Removes a trailing comment, honouring quotes so a '#' inside a string survives.</summary>
    private static string StripComment(string text)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || text[i - 1] is ' ' or '\t'))
            {
                return text[..i];
            }
        }

        return text;
    }

    private sealed class Parser
    {
        private readonly List<Line> _lines;
        private int _index;

        public Parser(List<Line> lines) => _lines = lines;

        private bool End => _index >= _lines.Count;

        private Line Current => _lines[_index];

        public void ExpectEnd()
        {
            if (!End)
            {
                throw new DefinitionException("Unexpected indentation.", Current.Number);
            }
        }

        public YamlNode ParseBlock(int indent)
        {
            return IsSequenceItem(Current.Text) ? ParseSequence(indent) : ParseMapping(indent);
        }

        private YamlMapping ParseMapping(int indent)
        {
            var mapping = new YamlMapping(Current.Number);

            while (!End && Current.Indent == indent && !IsSequenceItem(Current.Text))
            {
                var line = Current;
                var colon = FindKeyColon(line.Text);
                if (colon < 0)
                {
                    throw new DefinitionException(
                        $"Expected 'key: value' but found '{line.Text}'.", line.Number);
                }

                var key = line.Text[..colon].Trim();
                if (key.Length == 0)
                {
                    throw new DefinitionException("Empty key.", line.Number);
                }

                var rest = line.Text[(colon + 1)..].Trim();
                _index++;

                YamlNode value;
                if (rest.Length > 0)
                {
                    value = new YamlScalar(Unquote(rest, line.Number), line.Number);
                }
                else if (!End && (Current.Indent > indent ||
                                  (Current.Indent == indent && IsSequenceItem(Current.Text))))
                {
                    // A block sequence is allowed to sit at its key's own indentation, which is how
                    // most hand-written definition files are laid out.
                    value = ParseBlock(Current.Indent);
                }
                else
                {
                    value = new YamlScalar(string.Empty, line.Number);
                }

                mapping.Add(key, value);
            }

            if (!End && Current.Indent > indent)
            {
                throw new DefinitionException("Unexpected indentation.", Current.Number);
            }

            return mapping;
        }

        private YamlSequence ParseSequence(int indent)
        {
            var sequence = new YamlSequence(Current.Number);

            while (!End && Current.Indent == indent && IsSequenceItem(Current.Text))
            {
                var line = Current;

                var offset = 1;
                while (offset < line.Text.Length && line.Text[offset] == ' ')
                {
                    offset++;
                }

                var rest = line.Text[offset..];

                if (rest.Length == 0)
                {
                    _index++;
                    if (End || Current.Indent <= indent)
                    {
                        throw new DefinitionException("Sequence item has no value.", line.Number);
                    }

                    sequence.Items.Add(ParseBlock(Current.Indent));
                }
                else if (FindKeyColon(rest) >= 0)
                {
                    // "- name: x" is a mapping whose first entry shares the dash's line. Re-point the
                    // line past the dash and the ordinary mapping parser handles it and every
                    // sibling key underneath.
                    var itemIndent = indent + offset;
                    line.Indent = itemIndent;
                    line.Text = rest;
                    sequence.Items.Add(ParseMapping(itemIndent));
                }
                else
                {
                    _index++;
                    sequence.Items.Add(new YamlScalar(Unquote(rest, line.Number), line.Number));
                }
            }

            if (!End && Current.Indent > indent)
            {
                throw new DefinitionException("Unexpected indentation.", Current.Number);
            }

            return sequence;
        }

        private static bool IsSequenceItem(string text)
            => text.Length > 0 && text[0] == '-' && (text.Length == 1 || text[1] == ' ');

        private static int FindKeyColon(string text)
        {
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == ':' && (i + 1 == text.Length || text[i + 1] is ' ' or '\t'))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Unquote(string text, int line)
        {
            if (text.Length < 2)
            {
                return text;
            }

            var quote = text[0];
            if (quote is not ('"' or '\'') || text[^1] != quote)
            {
                return text;
            }

            var inner = text[1..^1];
            if (quote == '\'')
            {
                return inner.Replace("''", "'");
            }

            var builder = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\' || i + 1 == inner.Length)
                {
                    builder.Append(inner[i]);
                    continue;
                }

                i++;
                builder.Append(inner[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    _ => throw new DefinitionException($"Unknown escape '\\{inner[i]}'.", line),
                });
            }

            return builder.ToString();
        }
    }
}
