using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// Recordings and saved games of matches whose races have several players (P1 design T8, slice S4b): a skirmish two
/// commanders a race beside the computer, and five against five of people and bots, both as the relay starts them, played
/// with many comings and goings (some within one tick), saved to a file, read back and played: to the same sum at every
/// checksum tick, and a saved game goes on as the player it is resumed for, a race's second commander or a team's later seat.
/// </summary>
public class CoopReplayTests
{
    private static PlayerId P(int value) => new(value);

    /// <summary>
    /// A co-op skirmish as the relay starts one, with the computer filling the open seats: Anna and Cleo command Red, Ben and
    /// Dan Blue, the AI plays Green. Cleo and Dan move to the seats they want (<see cref="TakeSeat"/>).
    /// </summary>
    private static async Task<MatchInfo> RelayedTogether()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var cleo = await relay.Connect("Cleo", web: false);
        var dan = await relay.Connect("Dan", web: true);
        await RelayClients.Open(anna,
            new CreateRoom("Together", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], new MatchOptions(), FillWithAi: true, SeatsPerSlot: 2), ben, cleo);
        await cleo.TakeSeat(1);
        await dan.Join((await anna.Next<RoomState>(s => s.Seats[1].ClientId == cleo.Id)).Id);
        await dan.TakeSeat(3);
        return await RelayClients.Begin(anna, [anna, ben, cleo, dan], play: false);
    }

    /// <summary>
    /// Five against five on Canyon Attack at tier I, as the relay starts it: Anna at Red's first seat (player 0), Cleo at
    /// Red's fourth (player 6), Ben at Green's second (player 8); the computer's bots at the seven others.
    /// </summary>
    private static async Task<MatchInfo> RelayedFiveASide()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: true);
        var ben = await relay.Connect("Ben", web: false);
        var cleo = await relay.Connect("Cleo", web: true);
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 1, TeamSize = 5, TimeLimit = 240 }, FogOfWar = false };
        await RelayClients.Open(anna, new CreateRoom("Ten", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false), ben, cleo);
        await ben.TakeSeat(6);
        await cleo.TakeSeat(3);
        return await RelayClients.Begin(anna, [anna, ben, cleo], c => BattleNetTests.Brought($"{c.Name}'s hero", 1, line: c.Id % 2), play: false);
    }

    [Fact]
    public async Task A_co_op_skirmish_recording_plays_back_to_every_sum_and_a_save_goes_on_as_the_second_commander()
    {
        var match = await RelayedTogether();
        Assert.Equal([(Race.Red, 0, false), (Race.Red, 4, false), (Race.Blue, 1, false), (Race.Blue, 5, false), (Race.Green, 2, true)],
            match.Players.Select(p => (p.Slot, p.Player!.Value.Value, p.Ai)));
        // Cleo goes; Anna goes as Cleo comes back, in one tick; Dan goes, then Ben, and the computer plays Blue; Anna and Ben
        // come back, Dan never.
        var live = new LocalMatch(match, [new CommanderScript(P(0), 1), new CommanderScript(P(1), 2), new CommanderScript(P(4), 3), new CommanderScript(P(5), 4)],
            new PlayerLeft(P(4), 200), new PlayerLeft(P(0), 400), new PlayerReturned(P(4), 400), new PlayerLeft(P(5), 500), new PlayerLeft(P(1), 600),
            new PlayerReturned(P(0), 800), new PlayerReturned(P(1), 900));
        var players = live.World.Players;

        live.PlayTo(401);
        Assert.Equal([P(0)], players.AwayPlayers);
        Assert.Equal(P(4), players.KeeperOf(P(0)));
        Assert.Equal(Controller.Player, live.World.Races[Race.Red].Controller);
        Assert.DoesNotContain(live.World.AiControllers, ai => ai.Race == Race.Red);
        live.PlayTo(601);
        Assert.Equal(Controller.Ai, live.World.Races[Race.Blue].Controller);
        Assert.Contains(live.World.AiControllers, ai => ai.Race == Race.Blue);
        // A game saved here, with Cleo the only one of the people still playing.
        live.PlayTo(700);
        var saved = Load(live.Recording());
        live.PlayTo(1400);
        Assert.Equal([P(5)], players.AwayPlayers);
        Assert.Equal(P(1), players.KeeperOf(P(5)));
        Assert.Equal(Controller.Player, live.World.Races[Race.Blue].Controller);
        Assert.DoesNotContain(live.World.AiControllers, ai => ai.Race == Race.Blue);
        // The commanders' orders went in: machines queued, given and moved, under each player.
        Assert.Contains(live.Session.Played, t => t.Player == P(4) && t.Commands.Any(c => c is BuildMachineCommand));
        Assert.Contains(live.Session.Played, t => t.Commands.Any(c => c is GiveCommand));
        Assert.Contains(live.World.Machines, m => m.Race == Race.Blue && m.Owner == P(5));

        // The whole match plays back from its file to every sum.
        var recording = Load(live.Recording());
        Assert.Equal(match.Players, recording.Match.Players);
        Assert.Equal(7, recording.Presence!.Count);
        var played = PlayBack(recording, live.Sums);
        Assert.Equal(live.World.Checksum(), played.Checksum());
        Assert.Equal(players.Keepers, played.Players.Keepers);

        // The saved game goes on as Cleo: the same world at the tick it was saved, waiting only for her, as the second
        // commander of Red, who keeps Anna's machines.
        var resumed = saved.Resume(Replay.BuildWorld(RelayClients.Data, saved.Match), P(4));
        while (resumed.World.Tick < saved.Ticks)
            Assert.True(resumed.TryStep(), $"the save stuck at tick {resumed.World.Tick}");
        Assert.Equal(live.Sums[700], resumed.World.Checksum());
        Assert.Equal(P(4), resumed.World.Players.KeeperOf(P(0)));
        Assert.Equal([P(4)], resumed.Waiting());
        // She plays on alone; saved again, the game holds all of it, the comings and goings before the first save too.
        var cleo = new CommanderScript(P(4), 33);
        while (resumed.World.Tick < 1000)
        {
            foreach (var command in cleo.Next(resumed.World))
                resumed.Queue(command);
            resumed.Pump();
            Assert.True(resumed.TryStep(), $"stuck at tick {resumed.World.Tick}");
        }
        Assert.Contains(resumed.Played, t => t.Player == P(4) && t.Tick >= 700);
        var again = Load(new Replay(saved.Match, resumed.Played, resumed.World.Tick, resumed.Presence));
        Assert.Equal(saved.Presence, again.Presence);
        var second = again.Playback(Replay.BuildWorld(RelayClients.Data, again.Match));
        while (second.World.Tick < again.Ticks)
            Assert.True(second.TryStep());
        Assert.Equal(resumed.World.Checksum(), second.World.Checksum());
    }

    [Fact]
    public async Task Five_against_five_of_people_and_bots_plays_back_to_every_sum_and_its_result_and_a_save_goes_on_as_a_later_seat()
    {
        var match = await RelayedFiveASide();
        Assert.Equal([(0, false), (6, false), (8, false)], match.Players.Where(p => !p.Ai).Select(p => (p.Player!.Value.Value, p.Ai)));
        Assert.Equal(7, match.Players.Count(p => p.Ai));
        // Ben goes; Cleo goes as Ben comes back, in one tick; Anna goes, then Ben for good: bots play the battle out.
        var live = new LocalMatch(match, [new HeroScript(P(0), 11), new HeroScript(P(6), 12), new HeroScript(P(8), 13)],
            new PlayerLeft(P(8), 600), new PlayerLeft(P(6), 800), new PlayerReturned(P(8), 800), new PlayerLeft(P(0), 1000), new PlayerLeft(P(8), 1200));
        var world = live.World;
        var bots = new[] { 2, 4, 5, 7, 9, 10, 11 }.Select(P).ToList();
        Assert.Equal(bots, world.HeroBots.Keys);

        live.PlayTo(601);
        Assert.Equal(bots.Append(P(8)).Order(), world.HeroBots.Keys);
        live.PlayTo(700);
        var saved = Load(live.Recording());
        live.PlayTo(801);
        Assert.Equal(bots.Append(P(6)).Order(), world.HeroBots.Keys);
        Assert.Equal(world.HeroBots.Keys, world.Battle!.Bots);
        live.PlayTo(1201);
        Assert.Equal(10, world.HeroBots.Count);
        var limit = (BattleBalance.Opening + 240) * GameWorld.TicksPerSecond + 200;
        while (!world.Battle.Over)
        {
            Assert.True(world.Tick < limit, $"no end by tick {world.Tick}");
            live.PlayTo(world.Tick + 1);
        }
        Assert.Contains(live.Session.Played, t => t.Player == P(6) && t.Commands.Any(c => c is DriveCommand));

        var recording = Load(live.Recording());
        Assert.Equal(match.Players, recording.Match.Players);
        Assert.Equal(match.Options.Battle!.Heroes.Select(h => (h.Player, h.Name, h.Chassis, h.Bot)),
            recording.Match.Options.Battle!.Heroes.Select(h => (h.Player, h.Name, h.Chassis, h.Bot)));
        var played = PlayBack(recording, live.Sums);
        Assert.Equal(world.Checksum(), played.Checksum());
        Assert.Equal(world.Battle.Result, played.Battle!.Result);
        Assert.Equal(world.Battle.Fingerprint(), played.Battle.Fingerprint());

        // The game saved before Cleo went goes on as her, at Red's fourth seat: the same world, waiting for her and Anna.
        var resumed = saved.Resume(Replay.BuildWorld(RelayClients.Data, saved.Match), P(6));
        while (resumed.World.Tick < saved.Ticks)
            Assert.True(resumed.TryStep(), $"the save stuck at tick {resumed.World.Tick}");
        Assert.Equal(live.Sums[700], resumed.World.Checksum());
        Assert.Equal([P(0), P(6)], resumed.Waiting());
        resumed.Pump();
        Assert.Equal([P(0)], resumed.Waiting());
        // Her hero is hers to drive, Ben's a bot's while he is away.
        Assert.DoesNotContain(P(6), resumed.World.HeroBots.Keys);
        Assert.Contains(P(8), resumed.World.HeroBots.Keys);
    }

    /// <summary>A recording through its file: written, read back (which refuses one too old to play) and the file gone again.</summary>
    private static Replay Load(Replay replay)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coop-{Guid.NewGuid():N}{Replay.Extension}");
        replay.Save(path);
        try
        {
            var loaded = Replay.Load(path);
            Assert.Equal(NetJson.Protocol, loaded.Format);
            return loaded;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Plays a recording back from the start, checking it against every sum taken live; returns the world at its end.</summary>
    private static GameWorld PlayBack(Replay recording, IReadOnlyDictionary<long, ulong> sums)
    {
        var playback = recording.Playback(Replay.BuildWorld(RelayClients.Data, recording.Match));
        var compared = 0;
        while (playback.World.Tick < recording.Ticks)
        {
            Assert.True(playback.TryStep(), $"the recording stuck at tick {playback.World.Tick}");
            if (sums.TryGetValue(playback.World.Tick, out var sum))
            {
                Assert.True(sum == playback.World.Checksum(), $"the recording differs at tick {playback.World.Tick}");
                compared++;
            }
        }
        Assert.Equal(sums.Count(s => s.Key <= recording.Ticks), compared);
        return playback.World;
    }

    /// <summary>
    /// A match played on this computer as every client of it plays it, without a relay, so that who comes and goes does so at
    /// the very tick it should: one session waits for every person; at each tick each person still there sends their turn
    /// for the tick ahead (through the wire's JSON), with what their script orders, and who leaves or comes back at that tick
    /// is heard of just before. The sum is taken every checksum tick.
    /// </summary>
    private sealed class LocalMatch
    {
        private readonly IReadOnlyList<RelayScript> _scripts;
        private readonly IReadOnlyList<NetMessage> _notices;

        public LocalMatch(MatchInfo match, IReadOnlyList<RelayScript> scripts, params NetMessage[] notices)
        {
            Match = match;
            _scripts = [.. scripts.OrderBy(s => s.Player)];
            _notices = notices;
            Session = new LockstepSession(Replay.BuildWorld(RelayClients.Data, match), local: null, match.InputDelay);
            Assert.Equal(_scripts.Select(s => s.Player), Session.Humans);
        }

        public MatchInfo Match { get; }
        public LockstepSession Session { get; }
        public GameWorld World => Session.World;
        public SortedDictionary<long, ulong> Sums { get; } = [];

        public void PlayTo(long tick)
        {
            while (World.Tick < tick)
            {
                var due = World.Tick + Session.InputDelay;
                foreach (var notice in _notices.Where(n => n switch { PlayerLeft left => left.FromTick, PlayerReturned back => back.FromTick, _ => -1 } == due))
                    Session.Receive(notice);
                foreach (var script in _scripts)
                {
                    if (!Session.Away(script.Player, due))
                        Session.Receive(NetJson.Decode(NetJson.Encode(new Turn(due, script.Player, script.Next(World)))));
                }
                Assert.True(Session.TryStep(), $"stuck at tick {World.Tick}, waiting for [{string.Join(", ", Session.Waiting())}]");
                if (World.Tick % Session.ChecksumInterval == 0)
                    Sums[World.Tick] = World.Checksum();
            }
        }

        /// <summary>The match so far as the game records it: the turns carried out and the comings and goings heard of.</summary>
        public Replay Recording() => new(Match, [.. Session.Played], World.Tick, [.. Session.Presence]);
    }
}
