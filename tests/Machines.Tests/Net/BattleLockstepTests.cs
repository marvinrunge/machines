using System.Net;
using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>A battle in deterministic lockstep through the real relay: two players against each other, the computer's bots beside them.</summary>
public class BattleLockstepTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    [Fact]
    public async Task Two_players_and_two_bots_keep_identical_worlds_and_a_replay_repeats_them()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        var (a, b) = await Player.Battle(relay, new BattleOptions { Tier = 3, Lives = 2 });
        await using var _ = a;
        await using var __ = b;
        Assert.Equal((Race.Red, Race.Green), (a.Race, b.Race));
        Assert.Equal(new[] { Race.Blue, Race.Yellow }, a.Session.World.HeroBots.Keys);

        var rng = new Random(3);
        // Half the drives say where their driver looks, so the guns turn there; a hero with a turned upper body shows it.
        var turned = false;
        for (var frame = 0; frame < 700; frame++)
        {
            foreach (var player in new[] { a, b })
            {
                if (rng.Next(8) == 0 && player.Session.World.HeroOf(player.Race) is { } hero)
                {
                    float? yaw = rng.Next(2) == 0 ? (float)(rng.NextDouble() * 8 - 4) : null;
                    player.Session.Queue(new DriveCommand(player.Race, hero.Id, true, rng.Next(3) - 1, rng.Next(3) - 1,
                        hero.Position + new Vector2(rng.Next(-80, 80), rng.Next(-80, 80)), Weapons: rng.Next(4), Buttons: rng.Next(2) == 0, Yaw: yaw));
                }
            }
            await Player.Frame(a, b);
            var world = a.Session.World;
            turned |= new[] { a.Race, b.Race }.Select(world.HeroOf).OfType<Machine>().Any(h => world.HasYaw(h.Id) && world.HeadOf(h.Id) != 0);
        }
        await Player.CatchUp(a, b);

        Assert.True(a.Session.World.Tick > BattleBalance.Opening / GameWorld.Dt + 100, $"only {a.Session.World.Tick} ticks ran");
        Assert.Equal(a.Session.World.Tick, b.Session.World.Tick);
        Assert.Equal(a.Session.World.Checksum(), b.Session.World.Checksum());
        Assert.Null(a.Session.DesyncTick);
        Assert.Null(b.Session.DesyncTick);
        Assert.Contains(a.Session.Played, t => t.Race == b.Race);
        Assert.True(turned, "no sighted hero turned its upper body");

        var replay = new Replay(a.Match, a.Session.Played, a.Session.World.Tick, a.Session.Presence);
        var path = Path.Combine(Path.GetTempPath(), $"battle-lockstep-{Guid.NewGuid():N}{Replay.Extension}");
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
    public async Task A_leavers_hero_is_driven_by_a_bot_and_the_battle_goes_on_to_its_end()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        var (a, b) = await Player.Battle(relay, new BattleOptions { Tier = 2, TimeLimit = 20 });
        await using var _ = a;
        await using var __ = b;
        for (var frame = 0; frame < 20; frame++)
            await Player.Frame(a, b);
        var leaver = b.Race;
        Assert.Equal(Controller.Player, a.Session.World.Races[leaver].Controller);

        await b.DisposeAsync();
        var world = a.Session.World;
        for (var frame = 0; frame < 300 && !world.Races[leaver].Controller.Equals(Controller.Ai); frame++)
            await Player.Frame(a);
        Assert.Equal(Controller.Ai, world.Races[leaver].Controller);
        Assert.True(world.HeroBots.ContainsKey(leaver));
        Assert.Contains(leaver, world.Battle!.Bots);
        var hero = world.HeroOf(leaver)!;
        var from = hero.Position;

        // The bot takes the leaver's hero into the fight once the opening is over, and the battle runs to its end.
        var limit = (BattleBalance.Opening + 20) / GameWorld.Dt + 400;
        for (var frame = 0; frame < 20_000 && !world.Battle.Over && world.Tick < limit; frame++)
            await Player.Frame(a);
        Assert.True(world.Battle.Over, $"no end by tick {world.Tick}");
        Assert.True(hero.IsDead || Vector2.Distance(hero.Position, from) > 10, "the leaver's hero stood still");

        // A replay hands over on the same tick and ends in the same world.
        var replay = new Replay(a.Match, a.Session.Played, world.Tick, a.Session.Presence);
        var playback = replay.Playback(Replay.BuildWorld(DataLoader.Value, replay.Match));
        while (playback.World.Tick < replay.Ticks && playback.TryStep())
        {
        }
        Assert.Equal(world.Checksum(), playback.World.Checksum());
        Assert.Equal(world.Battle.Result, playback.World.Battle!.Result);
    }

    /// <summary>A client as the game runs it: a connection, and once started a world and a lockstep session.</summary>
    private sealed class Player : IAsyncDisposable
    {
        public required Connection Connection { get; init; }
        public required int Id { get; init; }
        public MatchInfo Match { get; private set; } = null!;
        public LockstepSession Session { get; private set; } = null!;
        public Race Race { get; private set; }
        private bool _gone;

        private static async Task<Player> Connect(RelayServer relay, string name)
        {
            var connection = await Connection.ConnectAsync("127.0.0.1", relay.Port, CancellationToken.None);
            connection.Send(new Hello(name, NetJson.Protocol));
            var welcome = (Welcome)await Next(connection, m => m is Welcome);
            return new Player { Connection = connection, Id = welcome.ClientId };
        }

        /// <summary>
        /// A battle room on Canyon Attack: the host at Red, the second seat the computer's, so the guest takes Green, on the
        /// other team; both bring a hero of the bracket's top tier, and the host starts it.
        /// </summary>
        public static async Task<(Player Host, Player Guest)> Battle(RelayServer relay, BattleOptions options)
        {
            var host = await Connect(relay, "Anna");
            var guest = await Connect(relay, "Ben");
            host.Connection.Send(new CreateRoom("battle", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green, Race.Yellow],
                new MatchOptions { Battle = options, FogOfWar = false }, FillWithAi: false));
            var room = (RoomState)await Next(host.Connection, m => m is RoomState state && state.Players.Any(p => p.ClientId == host.Id));
            host.Connection.Send(new SetSeat(1, SeatUse.Computer));
            await Next(host.Connection, m => m is RoomState state && state.Seats[1].Use == SeatUse.Computer);
            guest.Connection.Send(new JoinRoom(room.Id));
            await Next(guest.Connection, m => m is RoomState state && state.Players.Any(p => p.ClientId == guest.Id));

            guest.Connection.Send(new SetPlayer(Race.Blue, Ready: true, Hero: BattleNetTests.Brought("Ben", options.Tier, line: 1)));
            await Next(host.Connection, m => m is RoomState state && state.Players.Any(p => p.ClientId == guest.Id && p.Ready));
            host.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: BattleNetTests.Brought("Anna", options.Tier)));
            await Next(host.Connection, m => m is RoomState { Players.Count: 2 } state && state.Players.All(p => p.Ready));
            host.Connection.Send(new StartMatch());
            foreach (var player in new[] { host, guest })
            {
                var started = (MatchStarted)await Next(player.Connection, m => m is MatchStarted);
                player.Begin(started.Match);
            }
            return (host, guest);
        }

        private void Begin(MatchInfo match)
        {
            Match = match;
            Race = match.Players.Single(p => p.ClientId == Id).Slot;
            Session = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), Race,
                match.Players.Where(p => !p.Ai).Select(p => p.Slot), match.InputDelay, echoed: true);
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
                for (var steps = 0; steps < 40 && player.Session.TryStep(); steps++)
                {
                }
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

        public async ValueTask DisposeAsync()
        {
            if (_gone)
                return;
            _gone = true;
            await Connection.DisposeAsync();
        }
    }
}
