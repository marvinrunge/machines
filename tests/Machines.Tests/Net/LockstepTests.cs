using System.Net;
using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>Deterministic lockstep through the real relay, with two clients in this process.</summary>
public class LockstepTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    [Fact]
    public void Commands_survive_the_wire_bit_for_bit()
    {
        Command[] commands =
        [
            new MoveCommand(Race.Red, [1, 2, 3], new Vector2(123.456789f, 0.1f + 0.2f)),
            new PlaceConstructionCommand(Race.Blue, [7], "MISSILE_EMPLACEMENT", "TURRET", 2, new Vector2(-1.5e-3f, 1e7f), 270),
            new ResearchCommand(Race.Green, 4, new ResearchKey("AGGRESSOR", "GRUNT", 2, "L_BOLTER")),
            new DriveCommand(Race.Yellow, 9, true, 0.3333333f, -1, 12),
            new StrikeCommand(Race.Red, 5, new Vector2(MathF.PI, MathF.E)),
        ];
        var turn = new Turn(42, Race.Red, commands);

        var back = (Turn)NetJson.Decode(NetJson.Encode(turn));

        Assert.Equal(turn.Tick, back.Tick);
        Assert.Equal(commands.Length, back.Commands.Count);
        for (var i = 0; i < commands.Length; i++)
            Assert.Equal(NetJson.Encode(new Turn(0, Race.Red, [commands[i]])), NetJson.Encode(new Turn(0, Race.Red, [back.Commands[i]])));
        var move = (MoveCommand)back.Commands[0];
        Assert.Equal(BitConverter.SingleToInt32Bits(0.1f + 0.2f), BitConverter.SingleToInt32Bits(move.Target.Y));
    }

    [Fact]
    public async Task Two_players_through_the_relay_keep_identical_worlds_and_a_replay_repeats_them()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var a = await Player.Join(relay, "Anna", host: true);
        await using var b = await Player.Join(relay, "Ben", host: false);
        await Player.Start(a, b);

        var rng = new Random(3);
        for (var frame = 0; frame < 600; frame++)
        {
            // Each player orders its own machines about now and then.
            foreach (var player in new[] { a, b })
            {
                if (rng.Next(25) == 0 && player.Session.World.Machines.Where(m => m.Race == player.Race).ToList() is { Count: > 0 } own)
                {
                    var target = new Vector2(200 + (float)rng.NextDouble() * 1200, 200 + (float)rng.NextDouble() * 1200);
                    player.Session.Queue(new MoveCommand(player.Race, [own[rng.Next(own.Count)].Id], target));
                }
            }
            await Player.Frame(a, b);
        }
        await Player.CatchUp(a, b);

        Assert.True(a.Session.World.Tick > 300, $"only {a.Session.World.Tick} ticks ran");
        Assert.Equal(a.Session.World.Tick, b.Session.World.Tick);
        Assert.Equal(a.Session.World.Checksum(), b.Session.World.Checksum());
        Assert.Null(a.Session.DesyncTick);
        Assert.NotEmpty(a.Session.Played);

        // The replay rebuilds the same world from the setup and the turns alone.
        var replay = new Replay(a.Match, a.Session.Played, a.Session.World.Tick);
        var path = Path.Combine(Path.GetTempPath(), $"lockstep-{Guid.NewGuid():N}{Replay.Extension}");
        replay.Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);
        var playback = loaded.Playback(Replay.BuildWorld(DataLoader.Value, loaded.Match));
        while (playback.World.Tick < loaded.Ticks && playback.TryStep())
        {
        }
        Assert.Equal(a.Session.World.Tick, playback.World.Tick);
        Assert.Equal(a.Session.World.Checksum(), playback.World.Checksum());
    }

    [Fact]
    public async Task The_relay_reports_a_desync_when_worlds_differ()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var a = await Player.Join(relay, "Anna", host: true);
        await using var b = await Player.Join(relay, "Ben", host: false);
        await Player.Start(a, b);

        for (var frame = 0; frame < 30; frame++)
            await Player.Frame(a, b);
        // Something only one world has: a cheat, or a bug.
        b.Session.World.Races[b.Race].Bmu += 1;
        for (var frame = 0; frame < 80 && a.Session.DesyncTick is null; frame++)
            await Player.Frame(a, b);

        Assert.NotNull(a.Session.DesyncTick);
    }

    [Fact]
    public async Task The_others_play_on_when_a_player_leaves()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var a = await Player.Join(relay, "Anna", host: true);
        var b = await Player.Join(relay, "Ben", host: false);
        await Player.Start(a, b);
        for (var frame = 0; frame < 20; frame++)
            await Player.Frame(a, b);

        await b.DisposeAsync();
        var before = a.Session.World.Tick;
        for (var frame = 0; frame < 100; frame++)
            await Player.Frame(a);

        Assert.True(a.Session.World.Tick > before + 20, $"stuck at {a.Session.World.Tick}");
    }

    /// <summary>A client as the game runs it: a connection, and once started a world and a lockstep session.</summary>
    private sealed class Player : IAsyncDisposable
    {
        public required Connection Connection { get; init; }
        public required int Id { get; init; }
        public MatchInfo Match { get; private set; } = null!;
        public LockstepSession Session { get; private set; } = null!;
        public Race Race { get; private set; }

        public static async Task<Player> Join(RelayServer relay, string name, bool host)
        {
            var connection = await Connection.ConnectAsync("127.0.0.1", relay.Port, CancellationToken.None);
            connection.Send(new Hello(name, NetJson.Protocol));
            var welcome = (Welcome)await Next(connection, m => m is Welcome);
            if (host)
            {
                connection.Send(new CreateRoom("test", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false));
            }
            else
            {
                connection.Send(new ListRooms());
                var rooms = (RoomList)await Next(connection, m => m is RoomList);
                connection.Send(new JoinRoom(rooms.Rooms.Single().Id));
            }
            await Next(connection, m => m is RoomState state && state.Players.Any(p => p.ClientId == welcome.ClientId));
            return new Player { Connection = connection, Id = welcome.ClientId };
        }

        public static async Task Start(Player host, Player guest)
        {
            await Next(host.Connection, m => m is RoomState { Players.Count: 2 });
            guest.Connection.Send(new SetPlayer(Race.Blue, Ready: true));
            await Next(host.Connection, m => m is RoomState state && state.Players.Any(p => p.ClientId == guest.Id && p.Ready));
            host.Connection.Send(new SetPlayer(Race.Red, Ready: true));
            await Next(host.Connection, m => m is RoomState state && state.Players.All(p => p.Ready));
            host.Connection.Send(new StartMatch());
            foreach (var player in new[] { host, guest })
            {
                var started = (MatchStarted)await Next(player.Connection, m => m is MatchStarted);
                player.Begin(started.Match);
            }
        }

        private void Begin(MatchInfo match)
        {
            Match = match;
            Race = match.Players.Single(p => p.ClientId == Id).Slot;
            Session = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), Race,
                match.Players.Where(p => !p.Ai).Select(p => p.Slot), match.InputDelay);
            Session.Outgoing += Connection.Send;
        }

        /// <summary>One frame of each client: send turns, read what arrived, step what can be stepped.</summary>
        public static async Task Frame(params Player[] players)
        {
            foreach (var player in players)
            {
                player.Session.Pump();
                while (player.Connection.Inbox.TryRead(out var message))
                    player.Session.Receive(message);
                player.Session.TryStep();
            }
            await Task.Delay(1);
        }

        /// <summary>Lets both worlds reach the same tick.</summary>
        public static async Task CatchUp(Player a, Player b)
        {
            for (var i = 0; i < 500 && (a.Session.World.Tick != b.Session.World.Tick || !a.Session.CanStep && !b.Session.CanStep); i++)
            {
                foreach (var player in new[] { a, b })
                {
                    while (player.Connection.Inbox.TryRead(out var message))
                        player.Session.Receive(message);
                    var other = player == a ? b : a;
                    if (player.Session.World.Tick < other.Session.World.Tick)
                        player.Session.TryStep();
                }
                await Task.Delay(1);
                if (a.Session.World.Tick == b.Session.World.Tick)
                    break;
            }
        }

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
}
