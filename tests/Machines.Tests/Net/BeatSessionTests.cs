using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Net;

/// <summary>
/// The relay's beat and the clients' sessions together (P2 design §2, §4.1, §5.1, §8.1): every coming and going a
/// <see cref="Beat"/> puts into its bundles is one a session in bundle mode takes, whatever its clients do (freeze, lag, are
/// rebuilt, leave, come back, join late, are barred, the relay stalls); a world played live, one rebuilt from the log sealed
/// in segments and the recording played back in turn mode run alike at every tick; the session's LAG follows the beat's
/// mark; and a player the beat marks or holds has every wheel let go of, also one a late drive took again since the mark.
/// </summary>
public class BeatSessionTests
{
    private static GameData Data => Battles.Data;

    private static readonly PlayerId Anna = new(0), Ben = new(1);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Anna Red and Ben Blue on Canyon Attack, the computer Green.</summary>
    private static MatchInfo Skirmish() => new("m_desert", "m_desert", new MatchOptions(), 1, 4,
    [
        new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false, Anna),
        new MatchPlayer(2, "Ben", Race.Blue, Race.Blue, Ai: false, Ben),
        new MatchPlayer(0, "AI", Race.Green, Race.Green, Ai: true, new PlayerId(2)),
    ]);

    /// <summary>Anna and Cleo command Red, Ben Blue, the computer Green: a race of several, whose rebuilds are held.</summary>
    private static MatchInfo Together() => new("m_desert", "m_desert", new MatchOptions(), 7, 4,
    [
        new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false, Anna),
        new MatchPlayer(3, "Cleo", Race.Red, Race.Red, Ai: false, new PlayerId(4)),
        new MatchPlayer(2, "Ben", Race.Blue, Race.Blue, Ai: false, Ben),
        new MatchPlayer(0, "AI", Race.Green, Race.Green, Ai: true, new PlayerId(2)),
    ]);

    /// <summary>Five against five on Canyon Attack at tier III: people at the seats named, bots at every other, which late joiners may take.</summary>
    private static MatchInfo FiveAgainstFive(params int[] people)
    {
        var map = BattleCatalogue.Map("m_desert")!;
        var options = new BattleOptions { Tier = 3, Lives = 2, TeamSize = 5, TeamA = map.TeamA, TeamB = map.TeamB };
        return new MatchInfo("m_desert", "m_desert", new MatchOptions { Battle = options, FogOfWar = false }, 5, 4,
        [
            .. BattleSeats.Of(options).Select(seat => people.Contains(seat.Player.Value)
                ? new MatchPlayer(seat.Player.Value + 1, $"Seat {seat.Player}", seat.Race, seat.Race, Ai: false, seat.Player)
                : new MatchPlayer(0, "AI", seat.Race, seat.Race, Ai: true, seat.Player)),
        ]);
    }

    private static LockstepSession Session(MatchInfo match, SessionMode mode = SessionMode.Bundles) =>
        new(Replay.BuildWorld(Data, match), local: null, match.InputDelay, mode: mode);

    private static NetMessage Wire(NetMessage message) => NetJson.Decode(NetJson.Encode(message));

    private static bool Drives(GameWorld world, PlayerId player) => world.Machines.Any(m => m.DrivenByPlayer && m.Driver == player);

    /// <summary>
    /// A relay keeping one beat on made-up times (<see cref="BeatTimings.Quick"/>), and a client for every seat on a line
    /// without delay: unless silent, disconnected or still loading, it makes one turn a bundle, stamped the delay past the
    /// bundles closed so far (its world has run them all), now and then with a drive of the player's machine. Before each
    /// bundle something may happen, by the seed: a client falls silent for a while (and may stay behind after it, a turn a
    /// bundle stamped one past its newest, so late every bundle without catching up), is rebuilt, leaves, comes back, a person
    /// joins a battle's bot seat late (or leaves before joining), a seat is barred, or the relay stalls for up to four seconds.
    /// </summary>
    private sealed class Relay
    {
        private readonly Dictionary<PlayerId, Client> _clients;
        private readonly Dictionary<PlayerId, int> _machines;
        private readonly Random _random;
        private readonly MatchInfo _match;
        private int _joiners;

        public Relay(MatchInfo match, int seed)
        {
            _match = match;
            _random = new Random(seed);
            Beat = new Beat(BeatTimings.Quick, match, TimeSpan.Zero);
            Beat.Log += Lines.Add;
            var world = Replay.BuildWorld(Data, match);
            _machines = Beat.Seats.ToDictionary(s => s.Player, s => Machine(world, s.Player));
            _clients = Beat.Seats.ToDictionary(s => s.Player, s => new Client { Connected = s.State == SeatState.Playing });
            foreach (var seat in Beat.Seats.Where(s => s.State == SeatState.Playing))
                Beat.Loaded(seat.Player, Now);
        }

        public Beat Beat { get; }

        public TimeSpan Now { get; private set; }

        public List<Bundle> Log { get; } = [];

        public List<string> Lines { get; } = [];

        /// <summary>
        /// What the beat said of each seat right after a close, by the last bundle of that close: lag-marked, held, and let go
        /// of (marked, with no drive placed since the newest let-go).
        /// </summary>
        public Dictionary<long, Dictionary<PlayerId, (bool Marked, bool Held, bool LetGo)>> States { get; } = [];

        /// <summary>How many of each thing happened, so a test can tell its seeds reached them.</summary>
        public SortedDictionary<string, int> Happened { get; } = new(StringComparer.Ordinal);

        private static int Machine(GameWorld world, PlayerId player)
        {
            if (world.HeroOf(player) is { } hero)
                return hero.Id;
            var race = world.Players.RaceOf(player);
            return world.Machines.Where(m => m.Race == race).OrderBy(m => m.Owner == player ? 0 : 1).ThenBy(m => m.Id).First().Id;
        }

        public void Play(long ticks)
        {
            while (Beat.NextTick < ticks)
            {
                var tick = Beat.NextTick;
                if (tick > 8)
                    Happen(tick);
                Now = Max(Now, Beat.Due(tick) - Ms(10));
                foreach (var (player, client) in _clients)
                {
                    if (!client.Connected)
                        continue;
                    if (client.LoadsAt >= 0)
                    {
                        if (tick < client.LoadsAt)
                            continue;
                        Beat.Loaded(player, Now);
                        client.LoadsAt = -1;
                    }
                    var seat = Beat.Seat(player)!;
                    var stamp = tick < client.BehindUntil && seat is { State: SeatState.Playing, Stamped: >= 0 } ? Math.Min(seat.Stamped + 1, tick + Beat.Delay) : tick + Beat.Delay;
                    if (tick < client.SilentUntil || stamp <= seat.Stamped)
                        continue;
                    var commands = _random.Next(3) == 0 ? [Drive(player)] : Array.Empty<Command>();
                    Beat.Offer(new Turn(stamp, player, commands, Seen: tick - 1, Held: 1), Now);
                }
                Now = Max(Now, Beat.Due(tick));
                if (_random.Next(250) == 0)
                {
                    Now += Ms(_random.Next(200, 4000));
                    Count("stall");
                }
                var closed = Beat.Close(Now);
                Log.AddRange(closed);
                foreach (var notice in Beat.Drain())
                {
                    if (notice is { Message: LeftMatch, To: { } taken })
                    {
                        _clients[taken].Connected = false;
                        Count("taken out");
                    }
                }
                if (closed.Count > 0)
                    States[closed[^1].Tick] = Beat.Seats.ToDictionary(s => s.Player, s => (s.Marked, s.Held, s.Marked && !s.TookTheWheel));
            }
        }

        private Command Drive(PlayerId player)
        {
            var race = _match.Table()[player].Race;
            return _random.Next(8) == 0
                ? new DriveCommand(race, _machines[player], false)
                : new DriveCommand(race, _machines[player], true, Forward: _random.Next(2) * 2 - 1, Turn: _random.Next(3) - 1, Buttons: _random.Next(2) == 0);
        }

        private void Happen(long tick)
        {
            var roll = _random.Next(1000);
            if (roll >= 45)
                return;
            var people = Beat.Seats.Where(s => s.State != SeatState.Bot).ToList();
            var seat = people[_random.Next(people.Count)];
            var client = _clients[seat.Player];
            switch (roll)
            {
                case < 15 when client.Connected:
                    client.SilentUntil = tick + (_random.Next(4) == 0 ? _random.Next(60, 110) : _random.Next(3, 45));
                    Count("silent");
                    if (_random.Next(2) == 0)
                    {
                        client.BehindUntil = client.SilentUntil + _random.Next(5, 30);
                        Count("behind");
                    }
                    break;
                case < 23 when client.Connected && seat.State is SeatState.Playing or SeatState.Returning:
                    Beat.Rebuild(seat.Player);
                    client.LoadsAt = tick + _random.Next(1, 15);
                    Count(seat.State == SeatState.Returning && seat.Held ? "held rebuild" : "rebuild");
                    break;
                case < 28 when client.Connected:
                    Beat.Leave(seat.Player);
                    client.Connected = false;
                    Count(seat.State == SeatState.Bot ? "lost before joining" : "leave");
                    break;
                case < 36 when !client.Connected && seat.State == SeatState.Gone:
                    Beat.Rebuild(seat.Player);
                    (client.Connected, client.LoadsAt, client.SilentUntil) = (true, tick + _random.Next(1, 15), -1);
                    Count("rejoin");
                    break;
                case < 44 when Beat.Battle && Beat.Seats.FirstOrDefault(s => s.State == SeatState.Bot) is { } bot:
                    Beat.Rebuild(bot.Player, $"Joiner {++_joiners}");
                    (_clients[bot.Player].Connected, _clients[bot.Player].LoadsAt, _clients[bot.Player].SilentUntil) = (true, tick + _random.Next(1, 15), -1);
                    Count("late join");
                    break;
                case 44 when seat.State is not SeatState.Barred:
                    Beat.Bar(seat.Player);
                    client.Connected = false;
                    Count("barred");
                    break;
            }
        }

        private void Count(string what) => Happened[what] = Happened.GetValueOrDefault(what) + 1;

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private sealed class Client
        {
            public bool Connected { get; set; }

            public long SilentUntil { get; set; } = -1;

            public long LoadsAt { get; set; } = -1;

            /// <summary>Until this bundle the client is slow after a silence: its world keeps pace with the beat but never catches up.</summary>
            public long BehindUntil { get; set; } = -1;
        }
    }

    public static TheoryData<string, int> Runs => new()
    {
        { "skirmish", 11 }, { "skirmish", 12 }, { "together", 21 }, { "together", 22 }, { "five", 31 }, { "five", 32 }, { "five", 33 },
    };

    /// <summary>
    /// A beat's log played three ways: live (each bundle through the wire just before its tick), rebuilt from sealed segments
    /// of 100 ticks and the tail, and the live session's recording played back in turn mode. Every coming and going the beat
    /// sent is one the session took; the three worlds have the same checksum at every tick; each bundle holds at most one turn
    /// a player, in player order; the session's LAG is the beat's mark after each close; and while the beat holds a rebuild,
    /// nothing is driven by its player.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runs))]
    public void Every_coming_and_going_of_the_beat_is_taken_and_the_log_plays_alike_live_rebuilt_and_in_turn_mode(string kind, int seed)
    {
        var match = kind switch
        {
            "skirmish" => Skirmish(),
            "together" => Together(),
            _ => FiveAgainstFive(0, 2, 6),
        };
        var relay = new Relay(match, seed);
        relay.Play(900);
        var log = relay.Log;
        Assert.Equal(Enumerable.Range(0, log.Count).Select(t => (long)t), log.Select(b => b.Tick));
        Assert.All(log, b => Assert.Equal(b.Turns.Select(t => t.Player).Order(), b.Turns.Select(t => t.Player).Distinct()));
        Assert.Contains(relay.Happened, h => h.Key == "silent");
        Assert.Contains(log, b => b.Presence is { Count: > 0 });

        var live = Session(match);
        var rebuilt = Session(match);
        var sealedTo = log.Count / 100 * 100 - 1;
        for (var from = 0L; from < sealedTo; from += 100)
        {
            foreach (var part in BundleSegments.Seal(from, from + 99, log, 4096))
                rebuilt.Receive(Wire(part));
        }
        foreach (var bundle in log.Skip(sealedTo + 1))
            rebuilt.Receive(Wire(bundle));
        Assert.Equal(log.Count - 1, rebuilt.NewestBundle);

        var stepped = new List<ulong>();
        foreach (var bundle in log)
        {
            live.Receive(Wire(bundle));
            Assert.True(live.TryStep());
            Assert.True(rebuilt.TryStep());
            var tick = bundle.Tick;
            stepped.Add(live.World.Checksum());
            Assert.True(stepped[^1] == rebuilt.World.Checksum(), $"{kind} {seed}: the rebuilt world differs at tick {tick}");
            if (relay.States.TryGetValue(tick, out var states))
            {
                foreach (var (player, (marked, held, letGo)) in states)
                {
                    if (match.Table()[player].Kind == PlayerKind.Bot && !live.World.Players.Joined.Contains(player))
                        continue;
                    Assert.True(live.Lagging(player) == marked, $"{kind} {seed}: player {player} lags {live.Lagging(player)} in the session, marked {marked} by the beat at tick {tick}");
                    Assert.True(!held || !Drives(live.World, player), $"{kind} {seed}: player {player}, held by the beat, drives at tick {tick}");
                    Assert.True(!letGo || !Drives(live.World, player), $"{kind} {seed}: player {player}, let go of by the beat, drives at tick {tick}");
                }
            }
        }
        // Every coming and going the beat sent was taken, in its order.
        Assert.Equal(log.SelectMany(b => b.Presence ?? []).ToList(), live.Presence);
        Assert.Equal(live.Presence, rebuilt.Presence);

        var path = Path.Combine(Path.GetTempPath(), $"beat-{kind}-{seed}-{Guid.NewGuid():N}{Replay.Extension}");
        Replay loaded;
        try
        {
            new Replay(match, live.Played, live.World.Tick, live.Presence).Save(path);
            loaded = Replay.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
        var playback = loaded.Playback(Replay.BuildWorld(Data, loaded.Match));
        for (var tick = 0; tick < log.Count; tick++)
        {
            Assert.True(playback.TryStep(), $"{kind} {seed}: the playback stuck at tick {tick}");
            Assert.True(stepped[tick] == playback.World.Checksum(), $"{kind} {seed}: the playback differs at tick {tick}");
        }
    }

    /// <summary>The seeds above reach what they are for: late joins, rebuilds, holds, leaves and returns, handovers, a stall.</summary>
    [Fact]
    public void The_seeds_reach_every_kind_of_coming_and_going()
    {
        var presence = new List<NetMessage>();
        var happened = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in Runs)
        {
            var (kind, seed) = ((string)row[0], (int)row[1]);
            var relay = new Relay(kind switch { "skirmish" => Skirmish(), "together" => Together(), _ => FiveAgainstFive(0, 2, 6) }, seed);
            relay.Play(900);
            presence.AddRange(relay.Log.SelectMany(b => b.Presence ?? []));
            foreach (var (what, count) in relay.Happened)
                happened[what] = happened.GetValueOrDefault(what) + count;
        }
        Assert.Contains(presence, m => m is PlayerJoined);
        Assert.Contains(presence, m => m is PlayerLagging { Lagging: true });
        Assert.Contains(presence, m => m is PlayerLagging { Lagging: false });
        Assert.True(Renewed(presence) > 0, "no mark renewed");
        Assert.Contains(presence, m => m is PlayerLeft);
        Assert.Contains(presence, m => m is PlayerReturned);
        Assert.All(new[] { "rebuild", "leave", "rejoin", "late join", "stall", "silent", "behind" }, what => Assert.True(happened.ContainsKey(what), $"no {what}"));
    }

    /// <summary>How many <c>PlayerLagging(true)</c> came for a player already marked: renewed let-goes and held rebuilds of the marked.</summary>
    private static int Renewed(IEnumerable<NetMessage> presence)
    {
        var marked = new HashSet<PlayerId>();
        var renewed = 0;
        foreach (var change in presence)
        {
            switch (change)
            {
                case PlayerLagging { Lagging: true } on when !marked.Add(on.Player):
                    renewed++;
                    break;
                case PlayerLagging { Lagging: false } off:
                    marked.Remove(off.Player);
                    break;
                case PlayerLeft left:
                    marked.Remove(left.Player);
                    break;
            }
        }
        return renewed;
    }

    /// <summary>
    /// A beat and a skirmish session over the bundles it closes: Anna on time throughout, Ben as each test says; each bundle
    /// is run by the session as soon as it closes.
    /// </summary>
    private sealed class Pair
    {
        public Pair()
        {
            Beat = new Beat(BeatTimings.Quick, Skirmish(), TimeSpan.Zero);
            Beat.Loaded(Anna, TimeSpan.Zero);
            Beat.Loaded(Ben, TimeSpan.Zero);
            Session = BeatSessionTests.Session(Skirmish());
            Machine = Session.World.Machines.Where(m => m.Race == Race.Blue).OrderBy(m => m.Id).First().Id;
        }

        public Beat Beat { get; }

        public LockstepSession Session { get; }

        /// <summary>The Blue machine Ben drives.</summary>
        public int Machine { get; }

        public List<Bundle> Sent { get; } = [];

        public Command Drive() => new DriveCommand(Race.Blue, Machine, true, Forward: 1, Buttons: true);

        /// <summary>Closes the next bundle at its due time, after Anna's on-time turn and Ben's turn if given, and runs it.</summary>
        public void Next(Turn? ben = null)
        {
            var tick = Beat.NextTick;
            var now = Beat.Due(tick) - Ms(10);
            Beat.Offer(new Turn(tick + Beat.Delay, Anna, [], Seen: tick - 1, Held: 1), now);
            if (ben is not null)
                Beat.Offer(ben, now);
            foreach (var bundle in Beat.Close(Beat.Due(tick)))
            {
                Sent.Add(bundle);
                Session.Receive(Wire(bundle));
                Assert.True(Session.TryStep());
            }
        }

        public void OnTime(bool drive = false) => Next(new Turn(Beat.NextTick + Beat.Delay, Ben, drive ? [Drive()] : [], Seen: Beat.NextTick - 1, Held: 1));

        public bool BenDrives => Drives(Session.World, Ben);

        public List<NetMessage> Presence => [.. Sent.SelectMany(b => b.Presence ?? [])];
    }

    /// <summary>
    /// Ben drives, falls silent and is marked (every world lets go of his wheel); a late turn of his then drives again; his
    /// world is rebuilt while still marked. The hold lets go of the wheel once more, so his machine does not drive on with
    /// that late input while he reloads (P2 design §5.3: "their machines let go of the wheel").
    /// </summary>
    [Fact]
    public void A_held_rebuild_lets_go_of_a_wheel_a_late_drive_took_again_after_the_mark()
    {
        var quick = BeatTimings.Quick;
        var pair = new Pair();
        for (var tick = 0; tick < 6; tick++)
            pair.OnTime(drive: tick == 5);
        while (!pair.Beat.Seat(Ben)!.Marked)
            pair.Next();
        var marked = pair.Beat.NextTick - 1;
        Assert.Equal([new PlayerLagging(Ben, marked, true)], pair.Presence);
        Assert.False(pair.BenDrives);

        // A late turn: his world is behind, its drive goes on the next bundle and takes the wheel again.
        Assert.Equal(Offered.Moved, pair.Beat.Offer(new Turn(pair.Beat.Seat(Ben)!.Stamped + 1, Ben, [pair.Drive()], Seen: 2, Held: 1), pair.Beat.Due(pair.Beat.NextTick) - Ms(10)));
        pair.Next();
        Assert.True(pair.BenDrives);
        Assert.True(pair.Beat.Seat(Ben)!.Marked);

        Assert.Equal(ReturnKind.Rebuilding, pair.Beat.Rebuild(Ben));
        var held = pair.Beat.NextTick;
        for (var i = 0; i < quick.LagLeave - 1; i++)
        {
            pair.Next();
            Assert.False(pair.BenDrives, $"Ben's machine drives on while his rebuild is held, at tick {pair.Session.World.Tick - 1}");
            Assert.True(pair.Session.Lagging(Ben));
        }
        Assert.Equal([new PlayerLagging(Ben, marked, true), new PlayerLagging(Ben, held, true)], pair.Presence);
    }

    /// <summary>
    /// Ben is marked, is on time for fewer bundles than take the mark away, drives again with that turn, and falls silent once
    /// more: LagMark late bundles after his last drive ran, every world lets go of his wheel again rather than drive on with
    /// his last input until he is handed over (P2 design §2.6). A player marked and silent who drove nothing since is not
    /// marked again, and one who drives on with late turns keeps the wheel.
    /// </summary>
    [Fact]
    public void A_marked_player_silent_again_after_a_drive_is_let_go_of_again()
    {
        var quick = BeatTimings.Quick;
        var pair = new Pair();
        for (var tick = 0; tick < 6; tick++)
            pair.OnTime(drive: tick == 5);
        while (!pair.Beat.Seat(Ben)!.Marked)
            pair.Next();
        var first = pair.Beat.NextTick - 1;

        // On time for two bundles, not the BackAfter that would take the mark away, with a drive placed on the next.
        Assert.True(quick.BackAfter > 2);
        var stamp = pair.Beat.NextTick + 1;
        pair.Next(new Turn(stamp, Ben, [pair.Drive()], Seen: 2, Held: 1));
        pair.Next();
        Assert.True(pair.BenDrives);
        Assert.True(pair.Beat.Seat(Ben)!.Marked);

        // Silent again: the wheel is let go of LagMark late bundles after the drive ran, once.
        var again = -1L;
        for (var i = 0; i < quick.LagLeave - 2; i++)
        {
            pair.Next();
            if (!pair.BenDrives && again < 0)
                again = pair.Session.World.Tick - 1;
        }
        Assert.Equal(stamp + quick.LagMark, again);
        Assert.Equal([new PlayerLagging(Ben, first, true), new PlayerLagging(Ben, again, true)], pair.Presence);
        Assert.True(pair.Session.Lagging(Ben));

        // Late but driving on: no let-go while the late drives come, for twice LagMark bundles (short of the handover).
        var slow = new Pair();
        for (var tick = 0; tick < 6; tick++)
            slow.OnTime(drive: tick == 5);
        while (!slow.Beat.Seat(Ben)!.Marked)
            slow.Next();
        Assert.True(2 * quick.LagMark < quick.LagLeave - quick.LagMark);
        for (var i = 0; i < 2 * quick.LagMark; i++)
        {
            slow.Next(new Turn(slow.Beat.Seat(Ben)!.Stamped + 1, Ben, [slow.Drive()], Seen: 2, Held: 1));
            Assert.True(slow.BenDrives, $"Ben's late drive was let go of at tick {slow.Session.World.Tick - 1}");
        }
        Assert.Single(slow.Presence);
        Assert.Equal(SeatState.Playing, slow.Beat.Seat(Ben)!.State);
    }

    /// <summary>
    /// A marked player who drove nothing since the mark is neither let go of again while silent nor when his world is rebuilt:
    /// the mark let go of his wheel once, and one notice says so.
    /// </summary>
    [Fact]
    public void A_marked_player_who_drove_nothing_since_is_let_go_of_once()
    {
        var quick = BeatTimings.Quick;
        var pair = new Pair();
        for (var tick = 0; tick < 6; tick++)
            pair.OnTime(drive: tick == 5);
        while (!pair.Beat.Seat(Ben)!.Marked)
            pair.Next();
        for (var i = 0; i < quick.LagLeave - quick.LagMark - 1; i++)
            pair.Next();
        Assert.Equal(ReturnKind.Rebuilding, pair.Beat.Rebuild(Ben));
        for (var i = 0; i < quick.LagLeave - 1; i++)
        {
            pair.Next();
            Assert.False(pair.BenDrives);
            Assert.True(pair.Session.Lagging(Ben));
        }
        Assert.Single(pair.Presence);
    }
}
