using System.Globalization;

namespace Ts.App;

/// <summary>
/// Command-line startup, so a scope can be brought up already pointed at a stream.
///
/// The reason is a bench, not a demo: a test rig is started by a script, and having to click
/// through two pickers before the first frame arrives means the first frames are missed. It is
/// also what makes a loop-back check reproducible.
/// </summary>
public sealed class StartupOptions
{
    public string? DefinitionPath { get; private init; }

    public string? RecordingPath { get; private init; }

    /// <summary>Connect to the definition's declared source as soon as it is loaded.</summary>
    public bool Connect { get; private init; }

    /// <summary>Overrides the definition's UDP port when given.</summary>
    public int? UdpPort { get; private init; }

    /// <summary>Overrides the definition's serial port when given.</summary>
    public string? SerialPort { get; private init; }

    public const string Usage = """
        Noktra Telemetry Scope

          Ts.App [options]

          --definition <file.yaml>   load a channel definition at startup
          --open <file.tsr>          load a recording at startup
          --connect                  start receiving straight away
          --udp-port <n>             override the definition's UDP port
          --serial <name>            receive from this serial port instead
        """;

    public static StartupOptions Parse(string[] args)
    {
        string? definition = null;
        string? recording = null;
        string? serial = null;
        int? udpPort = null;
        var connect = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--definition" or "-d":
                    definition = Next(args, ref i);
                    break;
                case "--open" or "-o":
                    recording = Next(args, ref i);
                    break;
                case "--connect" or "-c":
                    connect = true;
                    break;
                case "--udp-port":
                    if (int.TryParse(Next(args, ref i), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var port))
                    {
                        udpPort = port;
                        connect = true;
                    }

                    break;
                case "--serial":
                    serial = Next(args, ref i);
                    connect = serial is not null;
                    break;
            }
        }

        return new StartupOptions
        {
            DefinitionPath = definition,
            RecordingPath = recording,
            Connect = connect,
            UdpPort = udpPort,
            SerialPort = serial,
        };
    }

    private static string? Next(string[] args, ref int index)
        => index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++index]
            : null;
}
