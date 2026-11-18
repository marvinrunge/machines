using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Machines.Tests.Net;

/// <summary>
/// A line between a client and the relay that a test shapes: it forwards TCP both ways, each way after a latency with some
/// jitter (the bytes keep their order), toward the client no faster than a downlink allows, holds everything for a stall,
/// and breaks every line through it at once when it goes down.
/// </summary>
internal sealed class LineProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _open = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random;
    private readonly Task _accepting;
    private readonly int _target;
    private volatile bool _down;
    private long _stalledUntil;
    private long _downlink;

    /// <param name="target">The relay's port on this computer.</param>
    /// <param name="latency">How long the bytes take each way.</param>
    /// <param name="jitter">How much more or less than the latency they may take, at random.</param>
    public LineProxy(int target, TimeSpan latency = default, TimeSpan jitter = default, int seed = 1)
    {
        _target = target;
        (Latency, Jitter) = (latency, jitter);
        _random = new Random(seed);
        _listener.Start();
        _accepting = Accept();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How long the bytes take each way.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>How much more or less than <see cref="Latency"/> they may take.</summary>
    public TimeSpan Jitter { get; set; }

    /// <summary>The most bytes a second toward the client; null for as fast as the computer goes.</summary>
    public int? Downlink
    {
        get => Interlocked.Read(ref _downlink) is var rate && rate > 0 ? (int)rate : null;
        set => Interlocked.Exchange(ref _downlink, value ?? 0);
    }

    /// <summary>Nothing goes through either way for <paramref name="time"/> from now; what comes meanwhile follows after it.</summary>
    public void Stall(TimeSpan time) => Interlocked.Exchange(ref _stalledUntil, (_clock.Elapsed + time).Ticks);

    /// <summary>Breaks every line through it, and every new one at once.</summary>
    public void Down()
    {
        _down = true;
        lock (_open)
        {
            foreach (var socket in _open)
                Break(socket);
            _open.Clear();
        }
    }

    private static void Break(TcpClient socket)
    {
        try
        {
            // Both ways of a line break both its sockets, and going down breaks them again: a socket broken once has none.
            if (socket.Client is { } raw)
                raw.LingerState = new LingerOption(true, 0);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }
        socket.Dispose();
    }

    private async Task Accept()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (_down)
                {
                    Break(client);
                    continue;
                }
                var relay = new TcpClient();
                await relay.ConnectAsync(IPAddress.Loopback, _target, _stop.Token);
                client.NoDelay = relay.NoDelay = true;
                lock (_open)
                    _open.AddRange([client, relay]);
                _ = Carry(client, relay, down: false);
                _ = Carry(relay, client, down: true);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    /// <summary>One way of a line: every piece read is due a latency (and jitter) later, never before the piece before it.</summary>
    private async Task Carry(TcpClient from, TcpClient to, bool down)
    {
        var pieces = Channel.CreateUnbounded<(byte[] Data, TimeSpan Due)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var writing = Write(pieces.Reader, to, down);
        try
        {
            var stream = from.GetStream();
            var buffer = new byte[16 * 1024];
            var last = TimeSpan.Zero;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                    break;
                TimeSpan spread;
                lock (_random)
                    spread = Jitter * (_random.NextDouble() * 2 - 1);
                var due = _clock.Elapsed + Latency + spread;
                last = due > last ? due : last;
                pieces.Writer.TryWrite((buffer[..read], last));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException or InvalidOperationException
                                              or OperationCanceledException)
        {
        }
        pieces.Writer.TryComplete();
        await writing;
        Break(from);
        Break(to);
    }

    private async Task Write(ChannelReader<(byte[] Data, TimeSpan Due)> pieces, TcpClient to, bool down)
    {
        try
        {
            var stream = to.GetStream();
            await foreach (var (data, due) in pieces.ReadAllAsync(_stop.Token))
            {
                while (true)
                {
                    var now = _clock.Elapsed;
                    var until = TimeSpan.FromTicks(Math.Max(due.Ticks, Interlocked.Read(ref _stalledUntil)));
                    if (now >= until)
                        break;
                    await Task.Delay(until - now, _stop.Token);
                }
                if (down && Downlink is not null)
                {
                    // A narrow line: a slice at a time, each taking the time the downlink needs for it.
                    for (var at = 0; at < data.Length;)
                    {
                        var slice = Math.Min(256, data.Length - at);
                        await stream.WriteAsync(data.AsMemory(at, slice), _stop.Token);
                        at += slice;
                        if (Downlink is { } rate)
                            await Task.Delay(TimeSpan.FromSeconds((double)slice / rate), _stop.Token);
                    }
                }
                else
                    await stream.WriteAsync(data, _stop.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException or InvalidOperationException
                                              or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Down();
        await _accepting;
    }
}
