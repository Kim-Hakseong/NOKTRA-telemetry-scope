using Ts.Core.Definition;
using Ts.Core.Framing;
using Ts.Core.Pipeline;
using Ts.Core.Time;

namespace Ts.Core.Transport;

/// <summary>
/// A live feed of bytes, cut into frames and handed to the display queue.
///
/// The receive loop never decodes, never draws and never blocks on anything but the wire. A socket
/// that stops reading loses datagrams the operating system already accepted and a serial port that
/// stops reading overruns, so everything else — decoding, statistics, painting — happens on the
/// other side of <see cref="FrameQueue"/>.
/// </summary>
public abstract class TelemetrySource : IDisposable
{
    private readonly FrameAssembler _assembler;
    private readonly FrameQueue _queue;
    private readonly IClock _clock;
    private readonly FrameHandler _onFrame;

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private long _startMicros;
    private long _pendingTimestamp;
    private long _datagramFrames;

    protected TelemetrySource(FramingDef framing, FrameQueue queue, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(framing);

        _assembler = new FrameAssembler(framing);
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // Cached so the hot path does not allocate a closure for every chunk received.
        _onFrame = frame => _queue.Enqueue(_pendingTimestamp, frame.ToArray());
    }

    /// <summary>What to show the operator: "udp 0.0.0.0:5005", "serial COM3 @ 115200".</summary>
    public abstract string Description { get; }

    public bool IsRunning => _loop is { IsCompleted: false };

    public long BytesReceived { get; private set; }

    public long FramesAssembled => _assembler.FrameCount + _datagramFrames;

    /// <summary>Bytes dropped resynchronising — a direct reading of line quality.</summary>
    public long DiscardedBytes => _assembler.DiscardedBytes;

    /// <summary>Microseconds since the source started, whether or not anything has arrived.</summary>
    public long ElapsedMicros => _startMicros == 0 && !IsRunning ? 0 : _clock.NowMicros - _startMicros;

    /// <summary>Set when the receive loop ended because of an error rather than a stop request.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised off the UI thread when the loop fails. Marshal before touching a view.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>
    /// How long a stop waits for the receive loop. A serial read that the driver will not cancel
    /// must not be able to freeze the window; closing the handle unblocks it in practice, and this
    /// is the backstop for when it does not.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private volatile bool _stopping;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        LastError = null;
        _stopping = false;
        _assembler.Reset();
        _datagramFrames = 0;
        BytesReceived = 0;
        _startMicros = _clock.NowMicros;

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        _loop = Task.Run(async () =>
        {
            try
            {
                await ReceiveAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping is how this loop normally ends.
            }
            catch (Exception) when (_stopping)
            {
                // Closing the handle is what unblocks a read the driver will not cancel, so the
                // exception it raises on the way out is the stop working, not a failure.
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Failed?.Invoke(this, ex.Message);
            }
        }, cancellation.Token);
    }

    public async Task StopAsync()
    {
        var cancellation = _cancellation;
        var loop = _loop;

        _cancellation = null;
        _loop = null;

        if (cancellation is null)
        {
            return;
        }

        _stopping = true;
        await cancellation.CancelAsync().ConfigureAwait(false);

        // Closing the socket or port first: a pending read is what the loop is sitting in, and the
        // cancellation token alone does not always reach it.
        OnStopped();

        if (loop is not null)
        {
            await Task.WhenAny(loop, Task.Delay(StopTimeout)).ConfigureAwait(false);
        }

        cancellation.Dispose();
    }

    /// <summary>Reads from the wire until cancelled, calling <see cref="Ingest"/> as bytes arrive.</summary>
    protected abstract Task ReceiveAsync(CancellationToken cancellationToken);

    protected virtual void OnStopped()
    {
    }

    /// <summary>Feeds received bytes through the framing rules.</summary>
    protected void Ingest(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        BytesReceived += data.Length;

        // Every frame completed by this chunk is stamped with the moment the chunk arrived. A
        // per-frame clock read would be more precise about nothing: the bytes came in together.
        _pendingTimestamp = _clock.NowMicros - _startMicros;
        _assembler.Push(data, _onFrame);
    }

    /// <summary>
    /// Queues a whole datagram as one frame, bypassing the byte-stream framing rules. This is what
    /// a datagram usually is, and running it through a length-field assembler would only find the
    /// same boundary the packet already gave us.
    /// </summary>
    protected void IngestDatagram(ReadOnlySpan<byte> datagram)
    {
        if (datagram.IsEmpty)
        {
            return;
        }

        BytesReceived += datagram.Length;
        _datagramFrames++;
        _queue.Enqueue(_clock.NowMicros - _startMicros, datagram.ToArray());
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _loop = null;
    }
}
