using Ts.Core.Definition;
using Ts.Core.Pipeline;
using Ts.Core.Time;

namespace Ts.Core.Transport;

/// <summary>
/// Receives telemetry from any byte stream.
///
/// Serial ports are the reason it exists, but taking a <see cref="Stream"/> rather than a
/// <c>SerialPort</c> means the receive path can be tested against a pipe — the framing, the
/// timestamps and the queue hand-off are all exercised without a cable or a virtual COM driver.
/// A serial-only implementation would be the one part of the pipeline nothing could check.
/// </summary>
public class StreamSource : TelemetrySource
{
    private readonly Func<Stream> _open;
    private readonly byte[] _buffer;
    private Stream? _stream;

    public StreamSource(
        FramingDef framing,
        FrameQueue queue,
        IClock clock,
        Func<Stream> open,
        string description = "stream",
        int bufferSize = 8192)
        : base(framing, queue, clock)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _buffer = new byte[bufferSize];
        Description = description;
    }

    public override string Description { get; }

    protected override async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        _stream = _open();

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // End of stream. For a file or a pipe this is the end; for a serial port it does
                // not happen while the port is open.
                break;
            }

            Ingest(_buffer.AsSpan(0, read));
        }
    }

    protected override void OnStopped()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        _stream?.Dispose();
        _stream = null;
    }
}
