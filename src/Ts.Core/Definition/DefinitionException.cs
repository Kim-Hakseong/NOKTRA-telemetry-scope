namespace Ts.Core.Definition;

/// <summary>
/// Raised when a channel definition cannot be read: malformed YAML, a missing required key, an
/// out-of-range value, or a combination the framing modes do not accept.
///
/// The line number is carried separately from the message so callers can point at the offending
/// line in an editor instead of re-parsing the message text.
/// </summary>
public sealed class DefinitionException : Exception
{
    public DefinitionException(string message, int line = 0)
        : base(line > 0 ? $"line {line}: {message}" : message)
    {
        Line = line;
        Detail = message;
    }

    /// <summary>1-based source line, or 0 when the problem is not tied to one line.</summary>
    public int Line { get; }

    /// <summary>The message without the line prefix.</summary>
    public string Detail { get; }
}
