using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Ts.Core.Definition;
using Ts.Core.Pipeline;
using Ts.Core.Time;
using Ts.Core.Transport;
using Xunit;

namespace Ts.Core.Tests;

/// <summary>
/// Loop-back tests for the receive path.
///
/// These are the one part of the suite that touches real I/O, because the thing being checked is
/// precisely that bytes crossing a socket or a pipe come out as the frames the definition
/// describes. They wait on the data arriving, not on a fixed delay — a sleep long enough to be
/// reliable on a loaded machine is long enough to make the suite unpleasant.
/// </summary>
public class TransportTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static ChannelSet Definition(bool datagramPerFrame = true) => ChannelSetReader.Read($"""
        name: Transport vector
        source:
          type: udp
          host: 127.0.0.1
          port: 0
          datagramPerFrame: {(datagramPerFrame ? "true" : "false")}
        framing:
          mode: lengthField
          headerLength: 2
          lengthOffset: 1
          lengthSize: 1
          adjust: 2
        channels:
          - name: Counter
            offset: 2
            type: u16
        """);

    private static byte[] FrameAt(int index)
    {
        // A5 | payload length | u16 counter
        var frame = new byte[4];
        frame[0] = 0xA5;
        frame[1] = 2;
        frame[2] = (byte)(index >> 8);
        frame[3] = (byte)index;
        return frame;
    }

    /// <summary>Polls until the queue holds what was sent, or gives up loudly.</summary>
    private static List<CapturedFrame> WaitForFrames(FrameQueue queue, int expected)
    {
        var collected = new List<CapturedFrame>();
        var watch = Stopwatch.StartNew();

        while (collected.Count < expected && watch.Elapsed < Patience)
        {
            if (queue.DrainTo(collected, expected - collected.Count) == 0)
            {
                Thread.Sleep(1);
            }
        }

        Assert.True(
            collected.Count == expected,
            $"Expected {expected} frames within {Patience.TotalSeconds}s but saw {collected.Count}.");

        return collected;
    }

    [Fact]
    public async Task UdpSource_ReceivesEachDatagramAsOneFrame()
    {
        const int count = 200;

        var queue = new FrameQueue();
        using var source = new UdpSource(Definition(), queue, SystemClock.Instance);
        source.Bind();

        var port = source.BoundPort;
        Assert.True(port > 0);

        source.Start();

        using (var sender = new UdpClient())
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            for (var i = 0; i < count; i++)
            {
                await sender.SendAsync(FrameAt(i), endpoint);
            }
        }

        var frames = WaitForFrames(queue, count);
        await source.StopAsync();

        for (var i = 0; i < count; i++)
        {
            Assert.Equal(FrameAt(i), frames[i].Bytes);
        }

        Assert.Equal(count, source.FramesAssembled);
        Assert.Equal(count * 4, source.BytesReceived);
    }

    [Fact]
    public async Task UdpSource_CanTreatDatagramsAsAByteStreamInstead()
    {
        var queue = new FrameQueue();
        using var source = new UdpSource(Definition(datagramPerFrame: false), queue, SystemClock.Instance);
        source.Bind();
        source.Start();

        // Three frames packed into one datagram, and a fourth split across two.
        var packed = FrameAt(0).Concat(FrameAt(1)).Concat(FrameAt(2)).ToArray();
        var split = FrameAt(3);

        using (var sender = new UdpClient())
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, source.BoundPort);
            await sender.SendAsync(packed, endpoint);
            await sender.SendAsync(split[..2], endpoint);
            await sender.SendAsync(split[2..], endpoint);
        }

        var frames = WaitForFrames(queue, 4);
        await source.StopAsync();

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(FrameAt(i), frames[i].Bytes);
        }
    }

    [Fact]
    public async Task StreamSource_FramesAByteStreamWhateverTheChunkBoundariesAre()
    {
        const int count = 100;

        var queue = new FrameQueue();

        using var writeEnd = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var readEnd = new AnonymousPipeClientStream(PipeDirection.In, writeEnd.ClientSafePipeHandle);

        using var source = new StreamSource(
            Definition().Framing, queue, SystemClock.Instance, () => readEnd, "pipe");

        source.Start();

        // Deliberately awkward chunking: the frame arrives split in two, so the assembler has to
        // hold the first byte back until the rest turns up.
        for (var i = 0; i < count; i++)
        {
            var frame = FrameAt(i);
            await writeEnd.WriteAsync(frame.AsMemory(0, 1));
            await writeEnd.WriteAsync(frame.AsMemory(1));
            await writeEnd.FlushAsync();
        }

        var frames = WaitForFrames(queue, count);
        await source.StopAsync();

        for (var i = 0; i < count; i++)
        {
            Assert.Equal(FrameAt(i), frames[i].Bytes);
        }
    }

    [Fact]
    public async Task StreamSource_TimestampsAdvanceWithTheStream()
    {
        var queue = new FrameQueue();

        using var writeEnd = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var readEnd = new AnonymousPipeClientStream(PipeDirection.In, writeEnd.ClientSafePipeHandle);

        using var source = new StreamSource(
            Definition().Framing, queue, SystemClock.Instance, () => readEnd, "pipe");

        source.Start();

        await writeEnd.WriteAsync(FrameAt(0));
        await writeEnd.FlushAsync();
        var first = WaitForFrames(queue, 1)[0];

        await writeEnd.WriteAsync(FrameAt(1));
        await writeEnd.FlushAsync();
        var second = WaitForFrames(queue, 1)[0];

        await source.StopAsync();

        // Timestamps are relative to the start of the session and never run backwards.
        Assert.True(first.TimeMicros >= 0);
        Assert.True(second.TimeMicros >= first.TimeMicros);
    }

    [Fact]
    public async Task AFailingSourceReportsWhyRatherThanDyingQuietly()
    {
        var queue = new FrameQueue();
        using var source = new StreamSource(
            Definition().Framing,
            queue,
            SystemClock.Instance,
            () => throw new IOException("the port is not there"),
            "broken");

        string? reported = null;
        source.Failed += (_, message) => reported = message;

        source.Start();

        var watch = Stopwatch.StartNew();
        while (reported is null && watch.Elapsed < Patience)
        {
            await Task.Delay(1);
        }

        await source.StopAsync();

        Assert.Equal("the port is not there", reported);
        Assert.Equal("the port is not there", source.LastError);
    }

    [Fact]
    public void TwoScopesCanWatchTheSamePort()
    {
        var definition = ChannelSetReader.Read("""
            source:
              type: udp
              host: 127.0.0.1
              port: 0
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: 0
                type: u16
            """);

        using var first = new UdpSource(definition, new FrameQueue(), SystemClock.Instance);
        first.Bind();

        var shared = ChannelSetReader.Read($"""
            source:
              type: udp
              host: 127.0.0.1
              port: {first.BoundPort}
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: 0
                type: u16
            """);

        using var second = new UdpSource(shared, new FrameQueue(), SystemClock.Instance);

        // Observing a stream must never require exclusive ownership of it.
        second.Bind();
        Assert.Equal(first.BoundPort, second.BoundPort);
    }

    [Fact]
    public void APortAlreadyTakenFailsWhenBoundNotLaterOnABackgroundThread()
    {
        var definition = ChannelSetReader.Read("""
            source:
              type: udp
              host: 240.0.0.1
              port: 5005
            framing:
              mode: fixed
              frameLength: 4
            channels:
              - name: A
                offset: 0
                type: u16
            """);

        using var source = new UdpSource(definition, new FrameQueue(), SystemClock.Instance);

        Assert.Throws<SocketException>(() => source.Bind());
    }
}
