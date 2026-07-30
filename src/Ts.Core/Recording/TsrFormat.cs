namespace Ts.Core.Recording;

/// <summary>Raised when a recording's header cannot be understood at all.</summary>
public sealed class TsrFormatException : Exception
{
    public TsrFormatException(string message) : base(message) { }
}

/// <summary>
/// Layout constants for the <c>.tsr</c> capture format.
///
/// The format is deliberately the simplest thing that can be written while frames are still
/// arriving and read back after a crash: a fixed header, the channel definition verbatim, then
/// length-prefixed records. There is no index and no trailer, because both would be the parts
/// missing from a file that was cut short — and a capture is most valuable exactly when the
/// capture stopped badly.
/// </summary>
public static class TsrFormat
{
    /// <summary>File magic. The digit is the container version.</summary>
    public static ReadOnlySpan<byte> Magic => "TSR1"u8;

    /// <summary>magic(4) + startUnixMicros(8) + definitionLength(4).</summary>
    public const int FixedHeaderLength = 16;

    /// <summary>timestampMicros(8) + frameLength(4).</summary>
    public const int RecordHeaderLength = 12;

    /// <summary>
    /// Ceilings used to tell a plausible field from a torn one. A length past these means the
    /// bytes are not a record header, so the read stops rather than trying to allocate them.
    /// </summary>
    public const int MaxDefinitionLength = 16 * 1024 * 1024;

    public const int MaxFrameLength = 64 * 1024 * 1024;

    public const string FileExtension = ".tsr";
}

/// <summary>One captured frame: when it was received, and the bytes exactly as they arrived.</summary>
public readonly record struct TsrRecord(long TimestampMicros, byte[] Frame);
