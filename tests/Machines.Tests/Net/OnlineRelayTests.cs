using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// The relay on the internet: WebSockets beside TCP into the same rooms, /health, and connections that outlive their
/// sockets, whether a line drops, the host ends a socket (Cloud Run after 60 minutes) or the client moves on its own.
/// </summary>
public class OnlineRelayTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    // Short clocks, so that a test sees a whole minute's worth of the connection's life in a second or two.
    private static readonly LinkTimings Quick = new()
    {
        Ack = TimeSpan.FromMilliseconds(100),
        Heartbeat = TimeSpan.FromMilliseconds(500),
        Silence = TimeSpan.FromSeconds(5),
        Grace = TimeSpan.FromSeconds(10),
        Dial = TimeSpan.FromSeconds(2),
        Tick = TimeSpan.FromMilliseconds(25),
    };

    [Fact]
    public void An_address_picks_its_link_by_what_was_typed()
    {
        Assert.True(RelayAddress.IsWebSocket("wss://machines-relay-abc-ey.a.run.app"));
        Assert.True(RelayAddress.IsWebSocket(" HTTPS://relay.example.com "));
        Assert.True(RelayAddress.IsWebSocket("ws://127.0.0.1:8080/ws"));
        Assert.False(RelayAddress.IsWebSocket("192.168.1.20:7777"));
        Assert.False(RelayAddress.IsWebSocket("[2001:db8::1]:7777"));

        Assert.Equal("wss://machines-relay-abc-ey.a.run.app/ws", RelayAddress.WebSocketUri("https://machines-relay-abc-ey.a.run.app").ToString());
        Assert.Equal("wss://relay.example.com/ws", RelayAddress.WebSocketUri("wss://relay.example.com/").ToString());
        Assert.Equal("ws://127.0.0.1:8080/ws", RelayAddress.WebSocketUri("http://127.0.0.1:8080").ToString());
        Assert.Equal("ws://localhost:9000/other", RelayAddress.WebSocketUri("ws://localhost:9000/other").ToString());

        Assert.Equal(("192.168.1.20", 7777), RelayAddress.Split("192.168.1.20"));
        Assert.Equal(("example.org", 7000), RelayAddress.Split(" example.org:7000 "));
        Assert.Equal(("2001:db8::1", 7777), RelayAddress.Split("2001:db8::1"));
        Assert.Equal(("2001:db8::1", 7001), RelayAddress.Split("[2001:db8::1]:7001"));
    }

    [Fact]
    public void A_hello_and_welcome_without_the_resume_fields_still_read_as_before()
    {
        // A client from before asks for nothing, and a relay from before gives no session: both carry on as they did,
        // so the protocol number stays.
        var hello = (Hello)NetJson.Decode("""{"$type":"hello","Name":"Anna","Protocol":12}"""u8);
        Assert.False(hello.Resume);
        var welcome = (Welcome)NetJson.Decode("""{"$type":"welcome","ClientId":3}"""u8);
        Assert.Null(welcome.Session);
        Assert.Null(welcome.Relay);
        // And what a client from before does not know, it passes over.
        var fields = System.Text.Json.JsonDocument.Parse(NetJson.Encode(new Welcome(3, "ABC", RelayServer.Version))).RootElement;
        Assert.Equal(3, fields.GetProperty("ClientId").GetInt32());
    }

    [Fact]
    public async Task A_client_talks_to_the_relay_over_a_WebSocket_and_health_counts_it()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Client.Connect($"ws://127.0.0.1:{http.Port}", "Anna");

        Assert.NotNull(anna.Welcome.Session);
        Assert.Equal(RelayServer.Version, anna.Welcome.Relay);
        anna.Connection.Send(new CreateRoom("Online", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false));
        await anna.Next(m => m is RoomState { Players.Count: 1 });
        anna.Connection.Send(new ListRooms());
        var list = (RoomList)await anna.Next(m => m is RoomList);
        Assert.Equal("Online", Assert.Single(list.Rooms).Name);

        using var web = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http.Port}") };
        var health = await web.GetFromJsonAsync<RelayHealth>("/health");
        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(RelayServer.Version, health.Version);
        Assert.Equal(NetJson.Protocol, health.Protocol);
        Assert.Equal(1, health.Rooms);
        Assert.Equal(1, health.Players);
        // A plain request to the WebSocket's place is turned away.
        Assert.Equal(HttpStatusCode.BadRequest, (await web.GetAsync(RelayAddress.WebSocketPath)).StatusCode);
    }

    [Fact]
    public async Task A_room_opened_over_WebSocket_plays_with_a_TCP_client()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true);
        await using var ben = await Player.Join($"127.0.0.1:{relay.Port}", "Ben", host: false);
        await Player.Start(anna, ben);

        await Player.Play(300, new Random(3), anna, ben);
        await Player.CatchUp(anna, ben);

        Player.AssertSame(anna, ben, minimumTick: 150);
    }

    [Fact]
    public async Task A_cut_line_comes_back_without_a_message_lost_or_doubled()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, Quick);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true, Quick);
        await using var ben = await Player.Join($"127.0.0.1:{relay.Port}", "Ben", host: false, Quick);
        await Player.Start(anna, ben);
        var rng = new Random(7);

        await Player.Play(80, rng, anna, ben);
        // Anna's line breaks under her, mid-match.
        anna.Connection.Cut();
        await Player.Play(80, rng, anna, ben);
        // The relay's host ends every socket, as Cloud Run does after its request timeout.
        relay.CutLines();
        await Player.Play(80, rng, anna, ben);
        // And Ben's breaks while Anna's, on its new socket, carries on.
        ben.Connection.Cut();
        await Player.Play(120, rng, anna, ben);
        await Player.CatchUp(anna, ben);

        Assert.True(anna.Connection.Links >= 3, $"Anna used {anna.Connection.Links} sockets");
        Assert.True(ben.Connection.Links >= 3, $"Ben used {ben.Connection.Links} sockets");
        Player.AssertSame(anna, ben, minimumTick: 150);
        // Nobody left: the match never noticed.
        Assert.Empty(anna.Session.Presence);
        Assert.Empty(ben.Session.Presence);
        Assert.Equal(Controller.Player, anna.Session.World.Races[ben.Race].Controller);
    }

    [Fact]
    public async Task Clients_move_to_a_new_socket_on_their_own_and_the_match_goes_on()
    {
        var moving = Quick with { Rotation = TimeSpan.FromMilliseconds(300), RotationSpread = TimeSpan.FromMilliseconds(100) };
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, moving);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true, moving);
        await using var ben = await Player.Join($"127.0.0.1:{relay.Port}", "Ben", host: false, moving);
        await Player.Start(anna, ben);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rng = new Random(11);
        while (watch.Elapsed < TimeSpan.FromSeconds(2.5))
            await Player.Play(20, rng, anna, ben);
        await Player.CatchUp(anna, ben);

        Assert.True(anna.Connection.Links >= 4, $"Anna used {anna.Connection.Links} sockets");
        Assert.True(ben.Connection.Links >= 4, $"Ben used {ben.Connection.Links} sockets");
        Player.AssertSame(anna, ben, minimumTick: 30);
        Assert.Empty(anna.Session.Presence);
    }

    [Fact]
    public async Task A_line_that_stays_down_gives_the_race_to_the_AI_after_the_grace_period()
    {
        var brief = Quick with { Grace = TimeSpan.FromSeconds(1.5), Dial = TimeSpan.FromMilliseconds(300) };
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, brief);
        await using var line = new LineProxy(relay.Port);
        await using var anna = await Player.Join($"127.0.0.1:{relay.Port}", "Anna", host: true, brief);
        await using var ben = await Player.Join($"127.0.0.1:{line.Port}", "Ben", host: false, brief);
        await Player.Start(anna, ben);
        var rng = new Random(5);
        await Player.Play(60, rng, anna, ben);

        // Ben's line goes down and stays down.
        line.Down();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ben.Connection.Closed && watch.Elapsed < TimeSpan.FromSeconds(10))
            await Player.Play(5, rng, anna, ben);
        Assert.True(ben.Connection.Closed, "Ben's connection never gave up");
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1), $"Ben gave up after only {watch.Elapsed.TotalSeconds:0.0} s");

        // The relay gave up on him too: Anna plays on, his race the AI's.
        var before = anna.Session.World.Tick;
        for (var frame = 0; frame < 400 && anna.Session.World.Tick < before + 40; frame++)
            await Player.Frame(anna);
        Assert.True(anna.Session.World.Tick >= before + 40, $"stuck at {anna.Session.World.Tick}");
        Assert.Contains(anna.Session.Presence, m => m is PlayerLeft left && left.Race == ben.Race);
        Assert.Equal(Controller.Ai, anna.Session.World.Races[ben.Race].Controller);
    }

    [Fact]
    public async Task A_player_who_closes_on_purpose_is_not_waited_for()
    {
        // The default grace of a minute: closing says goodbye, so nobody waits it out.
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true);
        var ben = await Player.Join($"ws://127.0.0.1:{http.Port}", "Ben", host: false);
        await Player.Start(anna, ben);
        await Player.Play(30, new Random(2), anna, ben);

        await ben.DisposeAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!anna.Session.Presence.Any(m => m is PlayerLeft) && watch.Elapsed < TimeSpan.FromSeconds(10))
            await Player.Frame(anna);

        Assert.Contains(anna.Session.Presence, m => m is PlayerLeft left && left.Race == ben.Race);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Anna waited {watch.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>A connected client before any room.</summary>
    private sealed class Client : IAsyncDisposable
    {
        public required Connection Connection { get; init; }
        public required Welcome Welcome { get; init; }

        public static async Task<Client> Connect(string address, string name, LinkTimings? timings = null)
        {
            var connection = await Connection.ConnectAsync(address, CancellationToken.None, timings);
            connection.Send(new Hello(name, NetJson.Protocol, Resume: true));
            return new Client { Connection = connection, Welcome = (Welcome)await Next(connection, m => m is Welcome) };
        }

        public Task<NetMessage> Next(Func<NetMessage, bool> wanted) => Next(Connection, wanted);

        private static async Task<NetMessage> Next(Connection connection, Func<NetMessage, bool> wanted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var message = await connection.Inbox.ReadAsync(timeout.Token);
                if (message is Refused refused)
                    throw new InvalidOperationException(refused.Reason);
                if (wanted(message))
                    return message;
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    /// <summary>A client as the game runs it, which also notes every turn the relay hands it.</summary>
    private sealed class Player : IAsyncDisposable
    {
        public required Client Client { get; init; }
        public Connection Connection => Client.Connection;
        public int Id => Client.Welcome.ClientId;
        public MatchInfo Match { get; private set; } = null!;
        public LockstepSession Session { get; private set; } = null!;
        public Race Race { get; private set; }

        /// <summary>Every turn received, by its tick and race, in the order it came.</summary>
        public List<(long Tick, Race Race)> Turns { get; } = [];

        public static async Task<Player> Join(string address, string name, bool host, LinkTimings? timings = null)
        {
            var client = await Client.Connect(address, name, timings);
            if (host)
                client.Connection.Send(new CreateRoom("test", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false));
            else
            {
                client.Connection.Send(new ListRooms());
                var rooms = (RoomList)await client.Next(m => m is RoomList { Rooms.Count: > 0 });
                client.Connection.Send(new JoinRoom(rooms.Rooms.Single().Id));
            }
            await client.Next(m => m is RoomState state && state.Players.Any(p => p.ClientId == client.Welcome.ClientId));
            return new Player { Client = client };
        }

        public static async Task Start(Player host, Player guest)
        {
            await host.Client.Next(m => m is RoomState { Players.Count: 2 });
            guest.Connection.Send(new SetPlayer(Race.Blue, Ready: true));
            await host.Client.Next(m => m is RoomState state && state.Players.Any(p => p.ClientId == guest.Id && p.Ready));
            host.Connection.Send(new SetPlayer(Race.Red, Ready: true));
            await host.Client.Next(m => m is RoomState state && state.Players.All(p => p.Ready));
            host.Connection.Send(new StartMatch());
            foreach (var player in new[] { host, guest })
            {
                var started = (MatchStarted)await player.Client.Next(m => m is MatchStarted);
                player.Match = started.Match;
                player.Race = started.Match.Players.Single(p => p.ClientId == player.Id).Slot;
                player.Session = new LockstepSession(Replay.BuildWorld(DataLoader.Value, started.Match), player.Race,
                    started.Match.Players.Where(p => !p.Ai).Select(p => p.Slot), started.Match.InputDelay, echoed: true);
                player.Session.Outgoing += player.Connection.Send;
            }
        }

        private void Read()
        {
            while (Connection.Inbox.TryRead(out var message))
            {
                if (message is Turn turn)
                    Turns.Add((turn.Tick, turn.Race));
                Session.Receive(message);
            }
        }

        /// <summary>One frame of each client: send turns, read what arrived, step what can be stepped.</summary>
        public static async Task Frame(params Player[] players)
        {
            foreach (var player in players)
            {
                player.Session.Pump();
                player.Read();
                for (var steps = 0; steps < 40 && player.Session.TryStep(); steps++)
                {
                }
            }
            await Task.Delay(1);
        }

        /// <summary>Frames in which each player now and then orders one of its machines somewhere.</summary>
        public static async Task Play(int frames, Random rng, params Player[] players)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                foreach (var player in players)
                {
                    if (rng.Next(20) == 0 && player.Session.World.Machines.Where(m => m.Race == player.Race).ToList() is { Count: > 0 } own)
                        player.Session.Queue(new MoveCommand(player.Race, [own[rng.Next(own.Count)].Id],
                            new Vector2(200 + (float)rng.NextDouble() * 1200, 200 + (float)rng.NextDouble() * 1200)));
                }
                await Frame(players);
            }
        }

        /// <summary>Lets both worlds reach the same tick.</summary>
        public static async Task CatchUp(Player a, Player b)
        {
            for (var i = 0; i < 1000 && a.Session.World.Tick != b.Session.World.Tick; i++)
            {
                foreach (var player in new[] { a, b })
                {
                    player.Read();
                    var other = player == a ? b : a;
                    if (player.Session.World.Tick < other.Session.World.Tick)
                        player.Session.TryStep();
                }
                await Task.Delay(1);
            }
        }

        /// <summary>
        /// Both worlds are alike, and each client was handed every turn of every player exactly once, from the first tick
        /// on without a gap: nothing was lost on the way, nothing came twice.
        /// </summary>
        public static void AssertSame(Player a, Player b, long minimumTick)
        {
            Assert.True(a.Session.World.Tick >= minimumTick, $"only {a.Session.World.Tick} ticks ran");
            Assert.Equal(a.Session.World.Tick, b.Session.World.Tick);
            Assert.Equal(a.Session.World.Checksum(), b.Session.World.Checksum());
            Assert.Null(a.Session.DesyncTick);
            Assert.Null(b.Session.DesyncTick);
            foreach (var player in new[] { a, b })
            {
                foreach (var race in new[] { a.Race, b.Race })
                {
                    var ticks = player.Turns.Where(t => t.Race == race).Select(t => t.Tick).ToList();
                    Assert.True(ticks.Count > 0);
                    Assert.Equal(Enumerable.Range(player.Match.InputDelay, ticks.Count).Select(t => (long)t), ticks);
                }
            }
        }

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    /// <summary>A line between a client and the relay that the test can break: it forwards TCP both ways until it goes down.</summary>
    private sealed class LineProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _open = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private readonly int _target;
        private volatile bool _down;

        public LineProxy(int target)
        {
            _target = target;
            _listener.Start();
            _accepting = Accept();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

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
                socket.Client.LingerState = new LingerOption(true, 0);
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
                    lock (_open)
                        _open.AddRange([client, relay]);
                    _ = Pump(client, relay);
                    _ = Pump(relay, client);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private static async Task Pump(TcpClient from, TcpClient to)
        {
            try
            {
                await from.GetStream().CopyToAsync(to.GetStream());
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
            }
            Break(from);
            Break(to);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            Down();
            await _accepting;
        }
    }
}
