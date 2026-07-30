using System.IO.Ports;
using Ts.Core.Definition;
using Ts.Core.Pipeline;
using Ts.Core.Time;

namespace Ts.Core.Transport;

/// <summary>
/// Receives telemetry from a serial port.
///
/// The port is opened when the loop starts and its <see cref="SerialPort.BaseStream"/> handed to
/// <see cref="StreamSource"/>, so the framing and timestamping path is exactly the one the tests
/// cover — the only thing specific to serial here is opening the port.
/// </summary>
public sealed class SerialSource : StreamSource
{
    private SerialPort? _port;

    public SerialSource(ChannelSet set, FrameQueue queue, IClock clock)
        : this(set.Framing, set.Source, queue, clock)
    {
    }

    private SerialSource(FramingDef framing, SourceDef source, FrameQueue queue, IClock clock)
        : this(framing, queue, clock, new SerialSettings(source))
    {
    }

    private SerialSource(FramingDef framing, FrameQueue queue, IClock clock, SerialSettings settings)
        : base(framing, queue, clock, settings.Open, settings.Description)
    {
        settings.Owner = this;
    }

    /// <summary>Serial ports visible to this machine right now.</summary>
    public static string[] AvailablePorts()
    {
        try
        {
            var ports = SerialPort.GetPortNames();
            Array.Sort(ports, StringComparer.OrdinalIgnoreCase);
            return ports;
        }
        catch (PlatformNotSupportedException)
        {
            return Array.Empty<string>();
        }
    }

    protected override void OnStopped()
    {
        base.OnStopped();
        ClosePort();
    }

    public override void Dispose()
    {
        base.Dispose();
        ClosePort();
    }

    private void ClosePort()
    {
        var port = _port;
        _port = null;

        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (IOException)
        {
            // A port removed while open throws on close; there is nothing left to do about it.
        }

        port.Dispose();
    }

    /// <summary>
    /// Holds the port settings until the receive loop asks for a stream. Opening in the
    /// constructor would claim the hardware as soon as the source is created, which is not what
    /// pressing Connect means.
    /// </summary>
    private sealed class SerialSettings
    {
        private readonly SourceDef _source;

        public SerialSettings(SourceDef source) => _source = source;

        public SerialSource? Owner { get; set; }

        public string Description => $"serial {_source.PortName} @ {_source.BaudRate}";

        public Stream Open()
        {
            var port = new SerialPort(_source.PortName, _source.BaudRate)
            {
                DataBits = _source.DataBits,
                Parity = ParseParity(_source.Parity),
                StopBits = ParseStopBits(_source.StopBits),
                Handshake = Handshake.None,
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 2000,
            };

            port.Open();

            if (Owner is not null)
            {
                Owner._port = port;
            }

            return port.BaseStream;
        }

        private static Parity ParseParity(string text) => text.ToLowerInvariant() switch
        {
            "none" or "n" or "" => Parity.None,
            "even" or "e" => Parity.Even,
            "odd" or "o" => Parity.Odd,
            "mark" or "m" => Parity.Mark,
            "space" or "s" => Parity.Space,
            _ => throw new DefinitionException($"Unknown parity '{text}'."),
        };

        private static StopBits ParseStopBits(string text) => text.ToLowerInvariant() switch
        {
            "one" or "1" => StopBits.One,
            "onepointfive" or "1.5" => StopBits.OnePointFive,
            "two" or "2" => StopBits.Two,
            _ => throw new DefinitionException($"Unknown stop bits '{text}'."),
        };
    }
}
