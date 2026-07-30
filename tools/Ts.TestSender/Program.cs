using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Ts.Core.Definition;
using Ts.Core.Recording;
using Ts.Core.Time;

namespace Ts.TestSender;

/// <summary>
/// A transmitter for testing the scope without hardware.
///
/// It reads the same channel definition the scope reads and emits frames that match it, so a
/// loop-back run exercises the definition, the framing, the transport and the decoder together —
/// which is the only combination that can actually be wrong in the field.
/// </summary>
internal static class Program
{
    private const string Usage = """
        Noktra Telemetry Scope - test transmitter

          Ts.TestSender --definition <file.yaml> [mode] [options]

        Modes
          --udp <host> <port>     send frames as UDP datagrams (default: 127.0.0.1 5005)
          --serial <port> <baud>  send frames out a serial port
          --record <file.tsr>     write a recording instead of transmitting

        Options
          --rate <hz>             frames per second (default 50)
          --seconds <n>           run length; 0 means until interrupted (default 0, or 60 for --record)
          --seed <n>              noise seed, for byte-identical repeat runs (default 20260731)
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DefinitionException or IOException or SocketException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var options = Options.Parse(args);
        if (options.DefinitionPath is null)
        {
            Console.Error.WriteLine("error: --definition is required.");
            return 1;
        }

        var set = ChannelSetReader.ReadFile(options.DefinitionPath);
        var generator = new SignalGenerator(set, options.Seed);

        Console.WriteLine($"definition : {set.Name} ({set.Channels.Count} channels)");
        Console.WriteLine($"framing    : {set.Framing.Mode}, {generator.FrameLength} byte frames");
        Console.WriteLine($"rate       : {options.Rate} Hz");

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        return options.Mode switch
        {
            Mode.Record => WriteRecording(set, generator, options),
            Mode.Serial => await SendSerialAsync(generator, options, stopping.Token).ConfigureAwait(false),
            _ => await SendUdpAsync(generator, options, stopping.Token).ConfigureAwait(false),
        };
    }

    private static int WriteRecording(ChannelSet set, SignalGenerator generator, Options options)
    {
        var seconds = options.Seconds > 0 ? options.Seconds : 60;
        var interval = 1_000_000L / options.Rate;
        var count = (long)(seconds * options.Rate);

        using var writer = TsrWriter.Create(options.Target, set, SystemClock.UnixNowMicros);

        for (long i = 0; i < count; i++)
        {
            var time = i * interval;
            writer.Write(time, generator.Next(time));
        }

        writer.Flush();
        Console.WriteLine(
            $"wrote {writer.RecordCount:N0} records ({writer.BytesWritten:N0} bytes) to {options.Target}");
        return 0;
    }

    private static async Task<int> SendUdpAsync(
        SignalGenerator generator, Options options, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Parse(options.Host), options.Port);

        Console.WriteLine($"sending    : udp {options.Host}:{options.Port}");
        Console.WriteLine("press ctrl-c to stop");

        return await PaceAsync(
            options,
            cancellationToken,
            async (frame, _) => await socket.SendAsync(frame, endpoint, cancellationToken)
                .ConfigureAwait(false),
            generator).ConfigureAwait(false);
    }

    private static async Task<int> SendSerialAsync(
        SignalGenerator generator, Options options, CancellationToken cancellationToken)
    {
        using var port = new SerialPort(options.Host, options.Port);
        port.Open();

        Console.WriteLine($"sending    : serial {options.Host} @ {options.Port}");
        Console.WriteLine("press ctrl-c to stop");

        return await PaceAsync(
            options,
            cancellationToken,
            (frame, _) =>
            {
                port.BaseStream.Write(frame, 0, frame.Length);
                return Task.CompletedTask;
            },
            generator).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits frames on the same absolute-time schedule the replay engine uses, so a slow send
    /// costs one late frame instead of a rate that quietly drifts below what was asked for.
    /// </summary>
    private static async Task<int> PaceAsync(
        Options options,
        CancellationToken cancellationToken,
        Func<byte[], long, Task> send,
        SignalGenerator generator)
    {
        var clock = SystemClock.Instance;
        var start = clock.NowMicros;
        var interval = 1_000_000L / options.Rate;
        var limit = options.Seconds > 0 ? (long)(options.Seconds * options.Rate) : long.MaxValue;

        long sent = 0;
        var nextReport = start + 1_000_000;

        try
        {
            while (sent < limit && !cancellationToken.IsCancellationRequested)
            {
                var due = start + (sent * interval);
                await clock.DelayUntilAsync(due, cancellationToken).ConfigureAwait(false);

                var time = sent * interval;
                await send(generator.Next(time), time).ConfigureAwait(false);
                sent++;

                if (clock.NowMicros >= nextReport)
                {
                    Console.WriteLine($"  {sent:N0} frames sent");
                    nextReport += 1_000_000;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C is how this program is normally ended.
        }

        Console.WriteLine($"stopped after {sent:N0} frames");
        return 0;
    }

    private enum Mode
    {
        Udp,
        Serial,
        Record,
    }

    private sealed class Options
    {
        public string? DefinitionPath { get; private set; }

        public Mode Mode { get; private set; } = Mode.Udp;

        /// <summary>UDP host, or serial port name.</summary>
        public string Host { get; private set; } = "127.0.0.1";

        /// <summary>UDP port, or serial baud rate.</summary>
        public int Port { get; private set; } = 5005;

        public string Target { get; private set; } = "capture.tsr";

        public int Rate { get; private set; } = 50;

        public double Seconds { get; private set; }

        public int Seed { get; private set; } = 20260731;

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--definition" or "-d":
                        options.DefinitionPath = Next(args, ref i);
                        break;
                    case "--udp":
                        options.Mode = Mode.Udp;
                        options.Host = Next(args, ref i, options.Host);
                        options.Port = Int(Next(args, ref i, "5005"));
                        break;
                    case "--serial":
                        options.Mode = Mode.Serial;
                        options.Host = Next(args, ref i, string.Empty);
                        options.Port = Int(Next(args, ref i, "115200"));
                        break;
                    case "--record":
                        options.Mode = Mode.Record;
                        options.Target = Next(args, ref i, options.Target);
                        break;
                    case "--rate":
                        options.Rate = Math.Clamp(Int(Next(args, ref i, "50")), 1, 100_000);
                        break;
                    case "--seconds":
                        options.Seconds = double.Parse(
                            Next(args, ref i, "0"), CultureInfo.InvariantCulture);
                        break;
                    case "--seed":
                        options.Seed = Int(Next(args, ref i, "0"));
                        break;
                }
            }

            return options;
        }

        private static string? Next(string[] args, ref int index)
            => index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : null;

        private static string Next(string[] args, ref int index, string fallback)
            => Next(args, ref index) ?? fallback;

        private static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);
    }
}
