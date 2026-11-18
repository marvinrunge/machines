using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Machines.Net;

/// <summary>Frames on a stream: a 4-byte little-endian length, then that many bytes of JSON.</summary>
public static class Framing
{
    /// <summary>Largest frame either side accepts (a turn is a few hundred bytes).</summary>
    public const int MaxFrame = 1 << 20;

    public static async Task WriteAsync(Stream stream, NetMessage message, CancellationToken cancel)
    {
        var body = NetJson.Encode(message);
        // Length and body in one write, so that they leave in one packet.
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancel);
        await stream.FlushAsync(cancel);
    }

    /// <returns>The next message, or null when the other side closed the stream.</returns>
    public static async Task<NetMessage?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancel))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrame)
            throw new InvalidDataException($"frame of {length} bytes");
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, cancel))
            return null;
        return NetJson.Decode(body);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancel);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}

/// <summary>The clocks of a resumable <see cref="Connection"/>; tests shorten them.</summary>
public sealed record LinkTimings
{
    public static LinkTimings Default { get; } = new();

    /// <summary>How often a side tells the other how far it has received, while messages come in.</summary>
    public TimeSpan Ack { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest a side stays quiet: then it acknowledges anyway, so the other knows the line still stands.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Nothing heard for this long, and the line counts as dropped.</summary>
    public TimeSpan Silence { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a dropped line may take to come back before the player counts as gone (today's leaving).</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// After this long on one socket the client moves to a new one before the relay's host ends it (Cloud Run ends a
    /// WebSocket after at most 60 minutes)…
    /// </summary>
    public TimeSpan Rotation { get; init; } = TimeSpan.FromMinutes(50);

    /// <summary>…up to this much earlier, at random, so that the clients of a match do not all move at once.</summary>
    public TimeSpan RotationSpread { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one attempt to reach the relay again may take; a planned move that fails tries again after it.</summary>
    public TimeSpan Dial { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the connection looks at these clocks.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// A connection carrying messages both ways over a <see cref="Link"/>: TCP or WebSocket. Sends are queued and written by
/// one loop, so any thread may send; received messages wait in <see cref="Inbox"/> until the owner reads them.
/// </summary>
/// <remarks>
/// A connection the relay made resumable (<see cref="Hello.Resume"/>, <see cref="Welcome.Session"/>) outlives its socket.
/// Each side counts the messages it sends and receives, keeps what it sent until the other acknowledges it
/// (<see cref="Ack"/>), and acknowledges now and then. When the line drops, or some fifty minutes after connecting, the
/// client opens a new socket and says how far it got (<see cref="ResumeLink"/>); the relay swaps the socket under the
/// same seat and says how far it got (<see cref="LinkResumed"/>), and each side sends again what the other missed. A
/// socket being left is read no further from the moment its side states its count, so nothing arrives twice and nothing
/// is lost; the inbox never notices. A line that does not come back within <see cref="LinkTimings.Grace"/> closes the
/// connection, and the relay hands the player's race to the AI as when anyone leaves.
/// </remarks>
public sealed class Connection : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Channel<NetMessage> _inbox = Channel.CreateUnbounded<NetMessage>();
    private readonly CancellationTokenSource _stop = new();
    private readonly LinkTimings _timings;
    private readonly long _origin = Stopwatch.GetTimestamp();

    // How a client reaches the relay again; null at the relay's end, which waits for the client instead.
    private readonly Func<CancellationToken, Task<Link>>? _dial;

    // What was sent and not yet acknowledged, by number.
    private readonly Queue<(long Number, NetMessage Message)> _kept = new();
    private readonly List<Task> _pumps = [];

    private Link? _link;
    private Channel<NetMessage>? _outbox;
    private Task? _writing, _reading;
    // Bumped whenever a socket is left: its reader and writer stop at the next message.
    private int _generation;
    private long _sent, _received, _acknowledged, _ackSent;
    private bool _keep, _closed, _disposing;
    // A planned move is between its old socket and its new one: the line has not dropped, and nobody need be told.
    private bool _moving;
    private string? _session;
    // Milliseconds on the connection's own clock.
    private long _heard, _said, _ackAt, _lostAt, _rotateAt;
    private Task _ticking = Task.CompletedTask, _redialing = Task.CompletedTask;
    private int _links;

    /// <summary>The relay's end of a link; <paramref name="first"/> is the message already read from it.</summary>
    public Connection(Link link, NetMessage? first = null, LinkTimings? timings = null) : this(timings, dial: null, keep: false)
    {
        lock (_lock)
        {
            if (first is not null)
            {
                _received = 1;
                _inbox.Writer.TryWrite(first);
            }
            Attach(link);
        }
    }

    public Connection(TcpClient client) : this(new TcpLink(client))
    {
    }

    private Connection(LinkTimings? timings, Func<CancellationToken, Task<Link>>? dial, bool keep)
    {
        _timings = timings ?? LinkTimings.Default;
        _dial = dial;
        // A client keeps what it sends from the start, until the relay's Welcome says whether it can resume.
        _keep = keep;
    }

    /// <summary>Connects over TCP.</summary>
    public static Task<Connection> ConnectAsync(string host, int port, CancellationToken cancel, LinkTimings? timings = null) =>
        Open(async token => await TcpLink.ConnectAsync(host, port, token), cancel, timings);

    /// <summary>Connects to what the player typed: a web address by WebSocket, "host:port" over TCP (see <see cref="RelayAddress"/>).</summary>
    public static Task<Connection> ConnectAsync(string address, CancellationToken cancel, LinkTimings? timings = null)
    {
        if (!RelayAddress.IsWebSocket(address))
        {
            var (host, port) = RelayAddress.Split(address);
            return ConnectAsync(host, port, cancel, timings);
        }
        var uri = RelayAddress.WebSocketUri(address);
        return Open(async token => await WebSocketLink.ConnectAsync(uri, token), cancel, timings);
    }

    /// <summary>A client's connection over whatever <paramref name="dial"/> opens, now and each time the line has to be found again (tests break it).</summary>
    internal static async Task<Connection> Open(Func<CancellationToken, Task<Link>> dial, CancellationToken cancel, LinkTimings? timings)
    {
        var link = await dial(cancel);
        var connection = new Connection(timings, dial, keep: true);
        lock (connection._lock)
            connection.Attach(link);
        return connection;
    }

    /// <summary>Messages received, in order; completes when the connection closes.</summary>
    public ChannelReader<NetMessage> Inbox => _inbox.Reader;

    public bool Closed => _inbox.Reader.Completion.IsCompleted;

    /// <summary>The key the relay gave this connection to come back with over a new socket; null when it cannot.</summary>
    public string? Session
    {
        get
        {
            lock (_lock)
                return _session;
        }
    }

    /// <summary>Whether the line dropped and the connection is trying to get it back (not while it only moves to a new socket on its own).</summary>
    public bool Reconnecting
    {
        get
        {
            lock (_lock)
                return !_closed && _session is not null && _link is null && !_moving;
        }
    }

    /// <summary>How many sockets the connection has used so far.</summary>
    internal int Links
    {
        get
        {
            lock (_lock)
                return _links;
        }
    }

    /// <summary>How many sent messages are kept because the other side has not yet acknowledged them (tests).</summary>
    internal int Unacknowledged
    {
        get
        {
            lock (_lock)
                return _kept.Count;
        }
    }

    /// <summary>
    /// How many messages sent here have not reached the other side as far as this side knows: on a resumable connection those
    /// it has not acknowledged yet, else those still waiting to be written to the socket. The relay takes a member out of a
    /// match whose traffic its line cannot carry by it (P2 design §2.4).
    /// </summary>
    public int Backlog
    {
        get
        {
            lock (_lock)
                return _session is not null ? _kept.Count : _outbox?.Reader.Count ?? 0;
        }
    }

    public void Send(NetMessage message)
    {
        lock (_lock)
        {
            if (_closed || _disposing)
                return;
            _sent++;
            if (_keep)
                _kept.Enqueue((_sent, message));
            _outbox?.Writer.TryWrite(message);
        }
    }

    /// <summary>
    /// The relay's side of <see cref="Hello.Resume"/>: from now on this connection keeps what it sends until it is
    /// acknowledged, and waits <see cref="LinkTimings.Grace"/> for a dropped client to come back under <paramref name="session"/>.
    /// Call it before sending the <see cref="Welcome"/> that carries the session.
    /// </summary>
    public void MakeResumable(string session)
    {
        lock (_lock)
        {
            if (_session is null && !_closed)
                Resumable(session);
        }
    }

    /// <summary>
    /// The relay's side of <see cref="ResumeLink"/>: carries on over <paramref name="link"/>, the client having received
    /// <paramref name="received"/> of this side's messages. Returns false, leaving the link to the caller, when the
    /// connection is over or the count cannot be right.
    /// </summary>
    public bool Resume(Link link, long received)
    {
        Link? old;
        lock (_lock)
        {
            if (_closed || _disposing || _session is null || !Acknowledge(received))
                return false;
            old = Detach();
            Attach(link, new LinkResumed(_received));
        }
        if (old is not null)
            Retire(old);
        return true;
    }

    /// <summary>Cuts the socket in use as a broken line would; a resumable connection then gets a new one (tests).</summary>
    internal void Cut()
    {
        lock (_lock)
            _link?.Abort();
    }

    private long Now => (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

    private static long Ms(TimeSpan span) => (long)span.TotalMilliseconds;

    // Under the lock: the link becomes the one in use, with whatever the other side has not yet received sent again.
    private void Attach(Link link, NetMessage? first = null)
    {
        var generation = ++_generation;
        _link = link;
        _links++;
        _moving = false;
        // A channel that counts what waits in it (Backlog); one for a single reader cannot.
        var outbox = _outbox = Channel.CreateUnbounded<NetMessage>();
        if (first is not null)
            outbox.Writer.TryWrite(first);
        foreach (var (_, message) in _kept)
            outbox.Writer.TryWrite(message);
        _heard = _said = Now;
        if (_dial is not null)
            _rotateAt = Now + Ms(_timings.Rotation) - (long)(Random.Shared.NextDouble() * Ms(_timings.RotationSpread));
        _pumps.RemoveAll(t => t.IsCompleted);
        _writing = Task.Run(() => Write(link, outbox.Reader, generation));
        _reading = Task.Run(() => Read(link, generation));
        _pumps.Add(_writing);
        _pumps.Add(_reading);
    }

    // Under the lock: leaves the link in use, if any; its reader and writer stop. Returns it for disposal.
    private Link? Detach()
    {
        _generation++;
        var link = _link;
        _link = null;
        _outbox?.Writer.TryComplete();
        _outbox = null;
        return link;
    }

    // Under the lock: the connection is over for good.
    private void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _inbox.Writer.TryComplete();
        if (Detach() is { } link)
            Retire(link);
    }

    // Under the lock.
    private void Resumable(string session)
    {
        _session = session;
        if (!_keep)
        {
            // What was sent before is not kept: a resume that needs it fails.
            _keep = true;
            _acknowledged = _sent;
        }
        _ackAt = Now;
        _ticking = Task.Run(Tick);
    }

    // Under the lock: the other side has the first `received` messages; false when that cannot be.
    private bool Acknowledge(long received)
    {
        if (received < _acknowledged || received > _sent)
            return false;
        _acknowledged = received;
        while (_kept.TryPeek(out var first) && first.Number <= received)
            _kept.Dequeue();
        return true;
    }

    private static void Retire(Link link) => _ = Task.Run(() => link.DisposeAsync().AsTask());

    private static bool IsLineError(Exception exception) => exception is IOException or SocketException
        or System.Net.WebSockets.WebSocketException or OperationCanceledException or ObjectDisposedException
        or System.Text.Json.JsonException or InvalidDataException;

    private async Task Read(Link link, int generation)
    {
        try
        {
            while (await link.ReceiveAsync(_stop.Token) is { } message)
            {
                if (!Arrived(message, generation))
                    return;
            }
        }
        catch (Exception exception) when (IsLineError(exception))
        {
        }
        Lost(link, generation);
    }

    // A message read from a link: a word of the connection's own, or one for the inbox. False stops the reading.
    private bool Arrived(NetMessage message, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation || _closed)
                return false;
            _heard = Now;
            switch (message)
            {
                case Ack ack:
                    if (!Acknowledge(ack.Received))
                        Close();
                    return !_closed;
                case Goodbye:
                    Close();
                    return false;
                case LinkMessage:
                    // A resume only ever opens a socket.
                    return true;
            }
            _received++;
            _inbox.Writer.TryWrite(message);
            // A client learns from the relay's welcome whether it can resume.
            if (_dial is not null && _session is null && _keep && message is Welcome welcome)
            {
                if (welcome.Session is { Length: > 0 } session)
                    Resumable(session);
                else
                {
                    _keep = false;
                    _kept.Clear();
                }
            }
            return true;
        }
    }

    private async Task Write(Link link, ChannelReader<NetMessage> outbox, int generation)
    {
        try
        {
            await foreach (var message in outbox.ReadAllAsync(_stop.Token))
            {
                if (Volatile.Read(ref _generation) != generation)
                    return;
                await link.SendAsync(message, _stop.Token);
                Interlocked.Exchange(ref _said, Now);
            }
        }
        catch (Exception exception) when (IsLineError(exception))
        {
            Lost(link, generation);
        }
    }

    // A link failed or was closed by the other side.
    private void Lost(Link link, int generation)
    {
        lock (_lock)
        {
            if (generation == _generation && _link == link)
                LostInUse();
        }
        Retire(link);
    }

    // Under the lock: the link in use is gone. A resumable client dials again, the relay waits; anything else ends.
    private void LostInUse()
    {
        if (Detach() is { } link)
            Retire(link);
        if (_closed || _disposing || _session is null)
            Close();
        else if (_dial is not null)
        {
            if (_redialing.IsCompleted)
                _redialing = Task.Run(Redial);
        }
        else
            _lostAt = Now;
    }

    // Acknowledges, keeps the line awake, notices silence, moves a client to a new socket in time, and ends a relay's
    // wait for a client that does not come back.
    private async Task Tick()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_timings.Tick, _stop.Token);
                lock (_lock)
                {
                    if (_closed)
                        return;
                    var now = Now;
                    if (_link is null)
                    {
                        if (_dial is null && now - _lostAt > Ms(_timings.Grace))
                        {
                            Close();
                            return;
                        }
                        // A client whose line is down and that is not finding it again starts anew: the line broke while a
                        // planned move gave up, or broke again the moment the way back ended.
                        if (_dial is not null && !_disposing && _redialing.IsCompleted)
                            _redialing = Task.Run(Redial);
                        continue;
                    }
                    if (now - _heard > Ms(_timings.Silence))
                    {
                        LostInUse();
                        continue;
                    }
                    if (_received != _ackSent && now - _ackAt >= Ms(_timings.Ack) || now - Interlocked.Read(ref _said) >= Ms(_timings.Heartbeat))
                    {
                        _outbox?.Writer.TryWrite(new Ack(_received));
                        (_ackSent, _ackAt) = (_received, now);
                        Interlocked.Exchange(ref _said, now);
                    }
                    if (_dial is not null && now >= _rotateAt && _redialing.IsCompleted)
                        _redialing = Task.Run(Redial);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The client's way back: a new socket, a resume, and what the relay missed sent again. A planned move leaves the old
    // socket only once the new one is open; a dropped line is tried again and again until the grace period is over.
    private async Task Redial()
    {
        // When a dropped line has to be back, counted from the moment it is found down.
        long? deadline = null;
        var pause = 100;
        while (true)
        {
            bool planned;
            lock (_lock)
            {
                if (_closed || _disposing)
                    return;
                planned = _link is not null;
                if (!planned)
                {
                    _moving = false;
                    deadline ??= Now + Ms(_timings.Grace);
                }
            }
            if (!planned && Now > deadline)
                break;
            Link? link = null;
            var left = false;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(deadline is { } end ? Math.Clamp(end - Now, 1, Ms(_timings.Dial)) : Ms(_timings.Dial)));
                link = await _dial!(attempt.Token);
                Link? old;
                long received;
                string session;
                lock (_lock)
                {
                    if (_closed || _disposing)
                    {
                        Retire(link);
                        return;
                    }
                    // From here on the old socket is read no further: what still comes on it comes again on the new one.
                    old = Detach();
                    left = true;
                    // A move whose old socket still stood is no dropped line; one whose socket broke meanwhile is.
                    _moving = old is not null;
                    received = _received;
                    session = _session!;
                }
                if (old is not null)
                    Retire(old);
                await link.SendAsync(new ResumeLink(session, received), attempt.Token);
                var reply = await link.ReceiveAsync(attempt.Token);
                lock (_lock)
                {
                    if (reply is LinkResumed resumed && !_closed && !_disposing && Acknowledge(resumed.Received))
                    {
                        Attach(link);
                        return;
                    }
                    if (reply is not null || _closed || _disposing)
                    {
                        // The relay no longer knows the session (it gave up waiting, or started afresh), or the counts
                        // cannot be put together: the connection is over.
                        Retire(link);
                        Close();
                        return;
                    }
                }
                // Closed without a word: perhaps the relay is only starting. Try again.
                Retire(link);
            }
            catch (Exception)
            {
                if (link is not null)
                    Retire(link);
                if (_stop.IsCancellationRequested)
                    return;
                if (planned && !left)
                {
                    lock (_lock)
                    {
                        // The old socket still stands: try the move again a little later. If it broke while the move
                        // waited for its new one, this is a dropped line now, and the way back goes on below.
                        if (_link is not null)
                        {
                            _rotateAt = Now + Ms(_timings.Dial);
                            return;
                        }
                    }
                }
            }
            try
            {
                await Task.Delay(pause, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            pause = Math.Min(pause * 2, 2000);
        }
        lock (_lock)
            Close();
    }

    public async ValueTask DisposeAsync()
    {
        Task? writing, reading = null;
        lock (_lock)
        {
            if (_disposing)
                return;
            _disposing = true;
            // A resumable other side is told not to wait for this one.
            if (!_closed && _session is not null && _outbox is not null)
            {
                _outbox.Writer.TryWrite(new Goodbye());
                reading = _reading;
            }
            _outbox?.Writer.TryComplete();
            writing = _outbox is null ? null : _writing;
        }
        if (writing is not null)
        {
            try
            {
                await writing.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
        if (reading is not null)
        {
            // The other side closes its end on the goodbye; reading on until it has (a second at most) lets the goodbye
            // arrive. Stopping at once would cut it off: a WebSocket whose read is cancelled is aborted, perhaps before
            // the goodbye left, and a socket closed with something unread resets the line, perhaps before the other side
            // read the goodbye, which then thinks its line dropped and tries for a minute to come back.
            try
            {
                await reading.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
        _stop.Cancel();
        Link? link;
        Task[] running;
        lock (_lock)
        {
            link = Detach();
            if (!_closed)
            {
                _closed = true;
                _inbox.Writer.TryComplete();
            }
            running = [.. _pumps, _ticking, _redialing];
        }
        if (link is not null)
            await link.DisposeAsync();
        try
        {
            await Task.WhenAll(running);
        }
        catch (Exception exception) when (IsLineError(exception))
        {
        }
    }
}
