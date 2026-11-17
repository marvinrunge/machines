using System.Net;
using System.Net.Sockets;
using Machines.Net;

namespace Machines.Tests.Net;

/// <summary>
/// A resumable <see cref="Connection"/> on its own, without the relay's rooms: messages counted both ways through
/// many broken sockets, sockets that break while a resume is still on its way, a planned move that hangs while the old
/// line breaks, what is kept until it is acknowledged, and a goodbye after a burst.
/// </summary>
public class ResumableLinkTests
{
    private static readonly LinkTimings Quick = new()
    {
        Ack = TimeSpan.FromMilliseconds(50),
        Heartbeat = TimeSpan.FromMilliseconds(300),
        Silence = TimeSpan.FromSeconds(5),
        Grace = TimeSpan.FromSeconds(20),
        Dial = TimeSpan.FromSeconds(2),
        Tick = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task Messages_both_ways_come_once_and_in_order_through_many_breaks()
    {
        // Sockets break on either side every few dozen milliseconds, some new ones right after they were dialed (during
        // the resume or just after it), and the client also moves on its own.
        var timings = Quick with { Rotation = TimeSpan.FromMilliseconds(300), RotationSpread = TimeSpan.FromMilliseconds(250) };
        await using var far = new FarEnd(timings);
        var dialed = 0;
        await using var near = await Connection.Open(async token =>
        {
            var link = await TcpLink.ConnectAsync("127.0.0.1", far.Port, token);
            if (Interlocked.Increment(ref dialed) > 1 && Random.Shared.Next(3) == 0)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(Random.Shared.Next(4));
                    link.Abort();
                });
            return link;
        }, CancellationToken.None, timings);
        near.Send(new Hello("near", NetJson.Protocol, Resume: true));
        var other = await far.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<Welcome>(await Next(near));
        Assert.IsType<Hello>(await Next(other));

        const int count = 2500;
        // The messages go out alongside the hundred cuts, a hundredth of them per cut so far, so they pass through the
        // breaks however fast the sending goes (with a relay in the same process asking Windows for its 1 ms timer, a
        // Task.Delay(1) takes a millisecond, not sixteen, and unpaced the messages were through after a dozen cuts).
        var cuts = 0;
        var sending = Task.WhenAll(Count(near, count, () => Volatile.Read(ref cuts)), Count(other, count, () => Volatile.Read(ref cuts)));
        var nearGot = Task.Run(() => Gather(near, count));
        var otherGot = Task.Run(() => Gather(other, count));
        var rng = new Random(17);
        while (!sending.IsCompleted || !nearGot.IsCompleted || !otherGot.IsCompleted)
        {
            await Task.Delay(rng.Next(10, 60));
            if (cuts < 100)
                (rng.Next(2) == 0 ? near : other).Cut();
            Interlocked.Increment(ref cuts);
            if (cuts > 2000)
                break;
        }
        await sending;
        Assert.Equal(Enumerable.Range(1, count), await nearGot.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(Enumerable.Range(1, count), await otherGot.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(near.Closed);
        Assert.False(other.Closed);
        Assert.True(near.Links > 20, $"only {near.Links} sockets");
    }

    [Fact]
    public async Task A_line_that_breaks_while_a_planned_move_hangs_comes_back()
    {
        var timings = Quick with { Rotation = TimeSpan.FromMilliseconds(200), RotationSpread = TimeSpan.Zero, Dial = TimeSpan.FromSeconds(1) };
        await using var far = new FarEnd(timings);
        var hang = 0;
        var hanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var near = await Connection.Open(async token =>
        {
            if (Volatile.Read(ref hang) == 1)
            {
                // The relay does not answer the new socket (a slow proxy, a waking host).
                hanging.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return await TcpLink.ConnectAsync("127.0.0.1", far.Port, token);
        }, CancellationToken.None, timings);
        near.Send(new Hello("near", NetJson.Protocol, Resume: true));
        var other = await far.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<Welcome>(await Next(near));

        Volatile.Write(ref hang, 1);
        await hanging.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The old line breaks while the move waits for its new socket; then the relay answers again.
        near.Cut();
        Volatile.Write(ref hang, 0);
        other.Send(new Chat("far", "still there?"));

        var chat = Assert.IsType<Chat>(await Next(near, TimeSpan.FromSeconds(8)));
        Assert.Equal("still there?", chat.Text);
        Assert.False(near.Reconnecting);
    }

    [Fact]
    public async Task What_was_acknowledged_is_no_longer_kept()
    {
        await using var far = new FarEnd(Quick);
        await using var near = await Connection.Open(async token => await TcpLink.ConnectAsync("127.0.0.1", far.Port, token), CancellationToken.None, Quick);
        near.Send(new Hello("near", NetJson.Protocol, Resume: true));
        var other = await far.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        await Next(near);
        await Next(other);

        await Task.WhenAll(Count(near, 500), Count(other, 500));
        await Task.WhenAll(Gather(near, 500), Gather(other, 500));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while ((near.Unacknowledged > 0 || other.Unacknowledged > 0) && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Assert.Equal(0, near.Unacknowledged);
        Assert.Equal(0, other.Unacknowledged);
    }

    [Fact]
    public async Task A_goodbye_comes_after_everything_sent_before_it()
    {
        await using var far = new FarEnd(Quick);
        var near = await Connection.Open(async token => await TcpLink.ConnectAsync("127.0.0.1", far.Port, token), CancellationToken.None, Quick);
        near.Send(new Hello("near", NetJson.Protocol, Resume: true));
        var other = await far.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        await Next(near);
        await Next(other);

        await Count(near, 100);
        await near.DisposeAsync();
        Assert.Equal(Enumerable.Range(1, 100), await Gather(other, 100).WaitAsync(TimeSpan.FromSeconds(10)));
        // And the far end closes at once rather than waiting out the grace period (20 s here).
        await other.Inbox.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Sends Chats numbered 1 to count, a few at a time; with cuts, no more than a hundredth of them per cut made so far
    // until the hundredth.
    private static async Task Count(Connection connection, int count, Func<int>? cuts = null)
    {
        for (var i = 1; i <= count; i++)
        {
            connection.Send(new Chat("", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (i % 8 == 0)
                await Task.Delay(1);
            while (cuts is not null && cuts() < 100 && i >= (cuts() + 1) * count / 100)
                await Task.Delay(1);
        }
    }

    // The numbers of the next count Chats.
    private static async Task<List<int>> Gather(Connection connection, int count)
    {
        var got = new List<int>();
        while (got.Count < count)
            got.Add(int.Parse(Assert.IsType<Chat>(await Next(connection, TimeSpan.FromSeconds(30))).Text, System.Globalization.CultureInfo.InvariantCulture));
        return got;
    }

    private static async Task<NetMessage> Next(Connection connection, TimeSpan? wait = null)
    {
        using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(10));
        return await connection.Inbox.ReadAsync(timeout.Token);
    }

    /// <summary>The relay's end of one resumable connection, without rooms: it takes a hello and the resumes after it, as the relay does.</summary>
    private sealed class FarEnd : IAsyncDisposable
    {
        private const string Session = "S";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<Connection> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly LinkTimings _timings;
        private readonly Task _accepting;

        public FarEnd(LinkTimings timings)
        {
            _timings = timings;
            _listener.Start();
            _accepting = Accept();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<Connection> Connected => _connected.Task;

        private async Task Accept()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Take(new TcpLink(client));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private async Task Take(Link link)
        {
            NetMessage? first;
            try
            {
                first = await link.ReceiveAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                first = null;
            }
            if (first is Hello && !_connected.Task.IsCompleted)
            {
                var connection = new Connection(link, first, _timings);
                connection.MakeResumable(Session);
                connection.Send(new Welcome(1, Session, "test"));
                _connected.TrySetResult(connection);
                return;
            }
            if (first is ResumeLink { Session: Session } resume && _connected.Task.IsCompleted && _connected.Task.Result.Resume(link, resume.Received))
                return;
            if (first is ResumeLink)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await link.SendAsync(new Refused("that connection is over"), timeout.Token);
                }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
            await link.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accepting;
            if (_connected.Task.IsCompleted)
                await _connected.Task.Result.DisposeAsync();
        }
    }
}
