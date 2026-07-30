using System.Net;
using System.Net.Sockets;
using Ts.Core.Definition;
using Ts.Core.Pipeline;
using Ts.Core.Time;

namespace Ts.Core.Transport;

/// <summary>
/// Receives telemetry over UDP.
///
/// A datagram already carries a boundary the sender chose, so by default one datagram is one
/// frame and the framing rules are not consulted — running a packet through a length-field
/// assembler can only rediscover the edge the network already gave us, and would silently discard
/// a packet whose length field happens to disagree.
///
/// A sender that packs several frames into a datagram, or splits one across two, is a real if
/// less common arrangement; <c>datagramPerFrame: false</c> in the definition feeds the bytes
/// through the framing rules instead.
/// </summary>
public sealed class UdpSource : TelemetrySource
{
    private readonly IPEndPoint _endpoint;
    private readonly bool _datagramPerFrame;
    private UdpClient? _client;

    public UdpSource(ChannelSet set, FrameQueue queue, IClock clock)
        : this(set.Framing, set.Source, queue, clock)
    {
    }

    public UdpSource(FramingDef framing, SourceDef source, FrameQueue queue, IClock clock)
        : base(framing, queue, clock)
    {
        ArgumentNullException.ThrowIfNull(source);

        var address = source.Host is "" or "any" ? IPAddress.Any : IPAddress.Parse(source.Host);
        _endpoint = new IPEndPoint(address, source.Port);
        _datagramPerFrame = source.DatagramPerFrame;
    }

    public override string Description => $"udp {_endpoint}";

    /// <summary>
    /// The port actually bound. With port 0 the operating system picks one, which is how the
    /// loop-back tests avoid fighting over a fixed number.
    /// </summary>
    public int BoundPort => (_client?.Client.LocalEndPoint as IPEndPoint)?.Port ?? _endpoint.Port;

    /// <summary>
    /// Binds the socket before the receive loop starts, so a port already in use is reported to
    /// the caller instead of surfacing later on a background thread.
    /// </summary>
    public void Bind()
    {
        if (_client is not null)
        {
            return;
        }

        var client = new UdpClient(AddressFamily.InterNetwork);

        // A scope is a passive observer: several may watch the same stream at once.
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(_endpoint);

        // A datagram that outgrows the buffer is truncated silently, so the buffer is generous.
        client.Client.ReceiveBufferSize = 1 << 20;

        _client = client;
    }

    protected override async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        Bind();
        var client = _client!;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (_datagramPerFrame)
            {
                IngestDatagram(result.Buffer);
            }
            else
            {
                Ingest(result.Buffer);
            }
        }
    }

    protected override void OnStopped()
    {
        _client?.Dispose();
        _client = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        _client?.Dispose();
        _client = null;
    }
}
