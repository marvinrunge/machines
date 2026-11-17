using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// The relay's beat on the real clock (P2 design §2, slice S2a): clients run as the game paces its frames, each on a thread of
/// its own, over TCP, WebSocket and lines a test shapes (<see cref="LineProxy"/>), while the relay closes a bundle every 50 ms
/// with whatever turns are in. A silent client is marked, handed over and back, and nobody waits for it; a relay that stood
/// still marks nobody; the delay follows a slow line, not a stalled client; a line too narrow for the match is cut and its
/// player comes back by key; a leave lands on the next bundle and the leaver comes back by name; a key takes a seat over from a
/// lingering connection, or rebuilds the world of the one that holds it, holding the race meanwhile; the beat waits for every
/// world to be built, but not forever; a majority ends the match; turns that go back are dropped; and the log is sealed in
/// order into pieces every rebuild shares. The tests measure time, so they run one at a time.
/// </summary>
[Collection(nameof(BeatRelayTests))]
public class BeatRelayTests
{
    /// <summary><see cref="BeatTimings.Quick"/>, but the beat waits for every world to be built, however long the first takes.</summary>
    private static readonly BeatTimings Fast = BeatTimings.Quick with { LoadTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// <see cref="Fast"/>, with a held rebuild handed over after ten seconds rather than one: a paced client catches up on
    /// fewer than <see cref="BundlePacer.CatchUpLead"/> bundles at two a slot, which alone takes about a second.
    /// </summary>
    private static readonly BeatTimings Holding = Fast with { LagLeave = 200 };

    /// <summary>How far behind the relay's clock a client playing on time may stand: a second.</summary>
    private const int MostBehind = 20;

    [Fact]
    public async Task A_silent_client_is_marked_handed_over_and_back_on_time_and_nobody_else_waits_for_it()
    {
        await using var relay = await RelayClients.Start(Fast);
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben", "Cleo"]);
        var (anna, ben, cleo) = (clients[0], clients[1], clients[2]);
        var controllers = new ConcurrentQueue<(long Tick, Controller Controller)>();
        anna.AfterStep = world => controllers.Enqueue((world.Tick, world.Races[cleo.Race].Controller));
        RelayClients.Run(clients, clock);
        await PlayOn(relay, clock,40);

        // Cleo's game hangs for two seconds: forty bundles, past the mark (6 late in a row) and the handover (20).
        var froze = cleo.Tick;
        cleo.Frozen = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        cleo.Frozen = false;
        var thawed = Stopwatch.StartNew();
        await Until(relay, () => Heard(anna, cleo.Me, froze).Any(m => m is PlayerReturned), TimeSpan.FromSeconds(10), "Cleo back");
        Assert.True(thawed.Elapsed < TimeSpan.FromSeconds(5), $"Cleo back {thawed.Elapsed.TotalSeconds:F1} s after her game ran again");
        await PlayOn(relay, clock,20);
        RelayClients.Halt(clients);

        var news = Heard(anna, cleo.Me, froze);
        Assert.Collection(news,
            m => Assert.True(m is PlayerLagging { Lagging: true }, $"{m}"),
            m => Assert.IsType<PlayerLeft>(m),
            m => Assert.IsType<PlayerReturned>(m));
        var (marked, left, back) = (FromTick(news[0]), FromTick(news[1]), FromTick(news[2]));
        Assert.True(left - marked >= Fast.LagLeave - Fast.LagMark, $"marked at {marked}, handed over at {left}");
        Assert.True(back > left);
        Assert.Contains($"room {room.Id}: player {cleo.Me} late {Fast.LagMark} bundles in a row: marked at tick {marked}", relay.Lines);
        Assert.Contains($"room {room.Id}: player {cleo.Me} late {Fast.LagLeave} bundles in a row: handed over at tick {left}", relay.Lines);
        Assert.Contains($"room {room.Id}: player {cleo.Me} back at tick {back} (returned)", relay.Lines);
        // The computer stood in for her race from the handover to her return, and gave it back.
        Assert.Contains(controllers, c => c.Tick > left && c.Tick <= back);
        Assert.All(controllers.Where(c => c.Tick > left && c.Tick <= back), c => Assert.Equal(Controller.Ai, c.Controller));
        Assert.All(controllers.Where(c => c.Tick > back), c => Assert.Equal(Controller.Player, c.Controller));

        // Anna and Ben were never handed over, and their worlds kept to the relay's clock throughout.
        Assert.DoesNotContain(anna.Heard, m => m is PlayerLeft gone && gone.Player != cleo.Me);
        AssertNeverStalled(anna);
        AssertNeverStalled(ben);
        Assert.True(RelayClients.AssertSumsAgree(clients) > 0);
        foreach (var client in clients)
            RelayClients.AssertPlaysBack(client);
    }

    [Fact]
    public async Task A_relay_that_stood_still_for_five_seconds_marks_and_hands_over_nobody()
    {
        // The real timings, a mark after 3 s late and a handover after 30 s: a stall of the relay is nobody's lateness, and the
        // clients catch up on the hundred bundles it closes at once within the counted ticks a mark takes.
        await using var relay = await RelayClients.Start(BeatTimings.Default with { LoadTimeout = TimeSpan.FromSeconds(30) });
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben", "Cleo"]);
        RelayClients.Run(clients, clock);
        // The stall comes once everyone's turnarounds count for the delay, while they are still few: a turn that waited at the
        // relay through it would then be past the 95th percentile.
        await Until(relay, () => relay.Relay.WithBeat(room.Id, b => b.Seats.All(s => s.Window.Count >= Beat.MinSamples)), TimeSpan.FromSeconds(30),
            "enough turnarounds");

        await Task.Run(() => relay.Relay.HoldStill(TimeSpan.FromSeconds(5)));
        await PlayOn(relay, clock,80);
        RelayClients.Halt(clients);

        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: the beat fell behind by ", StringComparison.Ordinal));
        var summary = relay.Relay.WithBeat(room.Id, b => b.Summary());
        Assert.True(int.Parse(Regex.Match(summary, @"(\d+) counted nobody").Groups[1].Value, CultureInfo.InvariantCulture) > 0, summary);
        Assert.All(clients, c => Assert.DoesNotContain(c.Heard, m => m is PlayerLagging or PlayerLeft));
        Assert.DoesNotContain(relay.Lines, l => l.Contains("marked at tick", StringComparison.Ordinal) || l.Contains("handed over at tick", StringComparison.Ordinal));
        // Nor is it anybody's line: the turns that waited at the relay through the stall measure nothing (they would give
        // turnarounds of five seconds, and with the few samples there are yet raise everyone's delay to the most), so the delay
        // stays within what a loopback line needs.
        var windows = relay.Relay.WithBeat(room.Id, b => b.Seats.Select(s => (s.Player, Window: s.Window.ToList())).ToList());
        var changes = relay.Lines.Where(l => l.Contains(": delay ", StringComparison.Ordinal)).ToList();
        var said = string.Join(Environment.NewLine, changes.Concat(windows.Select(w => $"{w.Player}: {string.Join(" ", w.Window.Select(ms => ms.ToString("F0", CultureInfo.InvariantCulture)))}")));
        Assert.All(windows, w => Assert.True(w.Window.All(ms => ms < 1000), said));
        Assert.True(relay.Relay.WithBeat(room.Id, b => b.Delay) <= BeatTimings.Default.StartDelay, said);
        Assert.True(changes.All(l => Regex.Match(l, @": delay \d → (\d) ") is { Success: true } change
                                     && int.Parse(change.Groups[1].Value, CultureInfo.InvariantCulture) <= BeatTimings.Default.StartDelay), said);
        // Everyone caught up and plays on the clock again.
        Assert.All(clients, c => Assert.True(c.Samples.Last().Clock - c.Samples.Last().Tick <= MostBehind, $"{c.Name}: {c.Samples.Last()}"));
        Assert.True(RelayClients.AssertSumsAgree(clients) > 0);
    }

    [Fact]
    public async Task The_delay_rises_to_a_line_of_150_ms_and_a_client_stalled_for_two_seconds_gives_it_no_sample()
    {
        // No mark for ten seconds: with the starting delay of 4 every turn over such a line is late, and a marked player gives
        // no samples. The delay only rises here, so that it stands still unless something pushes it up.
        var timings = Fast with { LagMark = 200, LagLeave = 600, DelayDownAfter = 100_000 };
        await using var relay = await RelayClients.Start(timings);
        await using var annas = new LineProxy(relay.Relay.Port, TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(5), seed: 1);
        await using var bens = new LineProxy(relay.Relay.Port, TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(5), seed: 2);
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben"], i => i == 0 ? annas.Port : bens.Port);
        var (anna, ben) = (clients[0], clients[1]);
        int Delay() => relay.Relay.WithBeat(room.Id, b => b.Delay);
        RelayClients.Run(clients, clock);

        // 75 ms each way: a turn made from a bundle reaches the relay some 250 to 350 ms after the bundle left, five bundles or more.
        await Until(relay, () => Delay() >= 5, TimeSpan.FromSeconds(20), "the delay up to the line");
        await PlayOn(relay, clock,40);
        var before = Delay();
        var since = relay.Lines.Count;

        // Ben's game hangs for two seconds and then catches up: the turns it makes meanwhile, with up to forty bundles in hand,
        // are no measure of his line (they would give turnarounds of seconds).
        ben.Frozen = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        ben.Frozen = false;
        await PlayOn(relay, clock,80);
        RelayClients.Halt(clients);

        var window = relay.Relay.WithBeat(room.Id, b => b.Seat(ben.Me)!.Window.ToList());
        Assert.True(window.Count >= Beat.MinSamples, $"{window.Count} samples");
        Assert.True(window.Max() < 1000, $"Ben's turnarounds go up to {window.Max():F0} ms");
        var changes = relay.Lines.Skip(since).Where(l => l.Contains(": delay ", StringComparison.Ordinal)).ToList();
        Assert.All(changes, l => Assert.True(int.Parse(Regex.Match(l, @"(\d+) ms$").Groups[1].Value, CultureInfo.InvariantCulture) < 1000, l));
        if (changes.Count == 0)
            Assert.Equal(before, Delay());
        Assert.DoesNotContain(relay.Lines, l => l.Contains("marked at tick", StringComparison.Ordinal));
        Assert.True(RelayClients.AssertSumsAgree(clients) > 0);
        RelayClients.AssertPlaysBack(anna);
    }

    [Fact]
    public async Task A_client_whose_downlink_cannot_carry_the_match_is_taken_out_and_comes_back_by_its_key()
    {
        // Only the backlog takes Cleo out: no mark, no handover and no lagged-out seat in the time it takes.
        var timings = Fast with { LagMark = 2000, LagLeave = 4000, LaggedOut = 100_000 };
        await using var relay = await RelayClients.Start(timings);
        await using var line = new LineProxy(relay.Relay.Port);
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben", "Cleo"], i => i == 2 ? line.Port : null);
        var (anna, ben, cleo) = (clients[0], clients[1], clients[2]);
        RelayClients.Run(clients, clock);
        await PlayOn(relay, clock,20);

        // 300 bytes a second toward Cleo, a fraction of what the match sends her: what she has not acknowledged piles up.
        line.Downlink = 300;
        var cut = $"room {room.Id}: Cleo (player {cleo.Me}) has ";
        await Until(relay, () => relay.Lines.Any(l => l.StartsWith(cut, StringComparison.Ordinal)), TimeSpan.FromSeconds(40), "Cleo taken out for her backlog");
        var backlog = int.Parse(Regex.Match(relay.Lines.First(l => l.StartsWith(cut, StringComparison.Ordinal)), @" has (\d+) messages").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(backlog > timings.MostBacklog, $"{backlog} messages");

        // Her line widens again: what waited for her comes, the reason last; she was gone from the next bundle.
        line.Downlink = null;
        await Until(relay, () => cleo.Out is not null, TimeSpan.FromSeconds(20), "Cleo told");
        Assert.Equal("Your connection cannot keep up with this match.", cleo.Out!.Reason);
        await Until(relay, () => Heard(anna, cleo.Me).Count > 0, TimeSpan.FromSeconds(5), "Cleo's leave");
        Assert.IsType<PlayerLeft>(Assert.Single(Heard(anna, cleo.Me)));

        // She asks for her seat back with her key over the connection she plays on, and is rebuilt.
        cleo.RejoinHere();
        await Until(relay, () => Heard(anna, cleo.Me).Any(m => m is PlayerReturned), TimeSpan.FromSeconds(20), "Cleo back");
        await PlayOn(relay, clock,20);
        RelayClients.Halt(clients);

        Assert.Collection(Heard(anna, cleo.Me), m => Assert.IsType<PlayerLeft>(m), m => Assert.IsType<PlayerReturned>(m));
        Assert.Equal(1, cleo.Rebuilds);
        Assert.Null(cleo.Resumed!.Key);
        Assert.True(cleo.InMatch);
        AssertNeverStalled(anna);
        AssertNeverStalled(ben);
        Assert.True(RelayClients.AssertSumsAgree(clients) > 0);
        RelayClients.AssertPlaysBack(anna);
        RelayClients.AssertPlaysBack(cleo);
    }

    [Fact]
    public async Task A_leave_lands_on_the_next_bundle_and_the_leaver_comes_back_by_name()
    {
        await using var relay = await RelayClients.Start(Fast);
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben", "Cleo"]);
        var (anna, ben, cleo) = (clients[0], clients[1], clients[2]);
        var controllers = new ConcurrentQueue<(long Tick, Controller Controller)>();
        anna.AfterStep = world => controllers.Enqueue((world.Tick, world.Races[ben.Race].Controller));
        RelayClients.Run(clients, clock);
        await PlayOn(relay, clock,40);

        // Ben quits: he is gone from the bundle the relay closes next.
        var key = ben.Key;
        await ben.Leave();
        var gone = await TickIn(relay, $@"^room {room.Id}: player {ben.Me} gone from tick (\d+)$");
        await Until(relay, () => Heard(anna, ben.Me).Count > 0, TimeSpan.FromSeconds(5), "Ben's leave");
        Assert.Equal(new PlayerLeft(ben.Me, gone), Assert.Single(Heard(anna, ben.Me)));
        await PlayOn(relay, clock,10);

        // He comes back by name, over TCP now, and gets a new key; his old one opens nothing any more.
        var back = await relay.Rejoin("Ben", web: false, room.Id);
        back.Script = new CommanderScript(back.Me, 88);
        back.Run(clock);
        await Until(relay, () => Heard(anna, ben.Me).Any(m => m is PlayerReturned), TimeSpan.FromSeconds(15), "Ben back");
        var returned = FromTick(Heard(anna, ben.Me).OfType<PlayerReturned>().Single());
        await using (var stale = await relay.Connect("Ben", web: true))
        {
            stale.Connection.Send(new RejoinMatch(room.Id, ben.Me, key));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => stale.Next<MatchResumed>());
            Assert.Equal("Ben: that seat key is not this match's", refused.Message);
        }
        await PlayOn(relay, clock,20);
        RelayClients.Halt([.. clients, back]);

        Assert.Equal(ben.Me, back.Me);
        Assert.NotNull(back.Key);
        Assert.NotEqual(key, back.Key);
        Assert.Equal(back.Key, back.Resumed!.Key);
        Assert.True(returned > gone);
        // The computer played his race from the leave to the return, and he does again.
        Assert.Contains(controllers, c => c.Tick > gone && c.Tick <= returned);
        Assert.All(controllers, c => Assert.Equal(c.Tick > gone && c.Tick <= returned ? Controller.Ai : Controller.Player, c.Controller));
        AssertNeverStalled(anna);
        AssertNeverStalled(cleo);
        Assert.True(RelayClients.AssertSumsAgree([.. clients, back]) > 0);
        RelayClients.AssertPlaysBack(anna);
        RelayClients.AssertPlaysBack(back);
    }

    [Fact]
    public async Task A_key_rejoin_while_the_old_connection_lingers_takes_the_seat_over_holding_the_race_and_nobody_stops()
    {
        await using var relay = await RelayClients.Start(Holding);
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben", "Cleo"]);
        var (anna, ben, cleo) = (clients[0], clients[1], clients[2]);
        var controllers = new ConcurrentQueue<Controller>();
        anna.AfterStep = world => controllers.Enqueue(world.Races[ben.Race].Controller);
        RelayClients.Run(clients, clock);
        await PlayOn(relay, clock,40);

        // Ben's game comes back over a new connection with his seat key, while the old one still plays.
        var taker = await relay.RejoinWithKey(ben, web: false);
        taker.Script = new CommanderScript(taker.Me, 77);
        taker.Run(clock);
        var from = await TickIn(relay, $@"^room {room.Id}: rejoin of Ben \(player {ben.Me}\) from tick (\d+):");
        await Until(relay, () => ben.Out is not null, TimeSpan.FromSeconds(10), "the old connection told");
        Assert.Equal("you came back to this match from another connection", ben.Out!.Reason);
        await Until(relay, () => Heard(anna, ben.Me, from).Any(m => m is PlayerLagging { Lagging: false }), TimeSpan.FromSeconds(15), "Ben's race given back");
        await PlayOn(relay, clock,20);
        RelayClients.Halt([.. clients, taker]);

        // His race was held while the new connection's world was rebuilt, never the computer's, and is his again.
        Assert.Collection(Heard(anna, ben.Me, from),
            m => Assert.Equal(new PlayerLagging(ben.Me, from, true), m),
            m => Assert.True(m is PlayerLagging { Lagging: false }, $"{m}"));
        Assert.DoesNotContain(anna.Heard, m => m is PlayerLeft);
        Assert.All(controllers, c => Assert.Equal(Controller.Player, c));
        Assert.Equal((ben.Me, ben.Key, ResumeReason.Rejoin), (taker.Me, taker.Key, taker.Resumed!.Reason));
        Assert.Null(taker.Resumed.Key);
        Assert.True(taker.InMatch);
        AssertNeverStalled(anna);
        AssertNeverStalled(cleo);
        Assert.True(RelayClients.AssertSumsAgree([.. clients, taker]) > 0);
        RelayClients.AssertPlaysBack(anna);
        RelayClients.AssertPlaysBack(taker);
    }

    [Fact]
    public async Task A_key_rejoin_from_the_connection_that_holds_the_seat_rebuilds_its_world_without_a_leave()
    {
        await using var relay = await RelayClients.Start(Holding);
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben"]);
        var (anna, ben) = (clients[0], clients[1]);
        var controllers = new ConcurrentQueue<Controller>();
        anna.AfterStep = world => controllers.Enqueue(world.Races[ben.Race].Controller);
        RelayClients.Run(clients, clock);
        await PlayOn(relay, clock,40);

        // Ben's game asks for its seat back over the connection it plays on, as after a gap in the relay's numbering.
        ben.RejoinHere();
        var from = await TickIn(relay, $@"^room {room.Id}: rejoin of Ben \(player {ben.Me}\) from tick (\d+):");
        await Until(relay, () => Heard(anna, ben.Me, from).Any(m => m is PlayerLagging { Lagging: false }), TimeSpan.FromSeconds(15), "Ben's race given back");
        await PlayOn(relay, clock,20);
        RelayClients.Halt(clients);

        Assert.Null(ben.Out);
        Assert.True(ben.InMatch);
        Assert.Equal(1, ben.Rebuilds);
        Assert.Equal((ResumeReason.Rejoin, null), (ben.Resumed!.Reason, ben.Resumed.Key));
        Assert.Collection(Heard(anna, ben.Me, from),
            m => Assert.Equal(new PlayerLagging(ben.Me, from, true), m),
            m => Assert.True(m is PlayerLagging { Lagging: false }, $"{m}"));
        Assert.DoesNotContain(anna.Heard, m => m is PlayerLeft);
        Assert.All(controllers, c => Assert.Equal(Controller.Player, c));
        AssertNeverStalled(anna);
        Assert.True(RelayClients.AssertSumsAgree(clients) > 0);
        RelayClients.AssertPlaysBack(anna);
        RelayClients.AssertPlaysBack(ben);
    }

    [Fact]
    public async Task The_beat_waits_for_every_world_to_be_built()
    {
        await using var relay = await RelayClients.Start(Fast);
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        ben.HoldLoad = true;
        var room = await RelayClients.Open(anna, new CreateRoom("Beat", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false), ben);
        await RelayClients.Begin(anna, [anna, ben]);
        var clock = relay.Relay.Clock(room.Id);
        RelayClients.Run([anna, ben], clock);

        // Ben's world is built, but his game has not said so: no bundle for a second.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Null(clock());
        Assert.Equal((0L, 0L), (anna.Tick, ben.Tick));
        ben.Load();
        await Until(relay, () => anna.Tick >= 20 && ben.Tick >= 20, TimeSpan.FromSeconds(10), "the match under way");
        RelayClients.Halt([anna, ben]);

        var (started, anchor) = relay.Relay.WithBeat(room.Id, b => (b.Started, b.Anchor!.Value));
        Assert.InRange(anchor - started, TimeSpan.FromSeconds(1), Fast.LoadTimeout);
        Assert.Contains($"room {room.Id}: the beat starts: 2 of 2 loaded", relay.Lines);
        Assert.True(RelayClients.AssertSumsAgree([anna, ben]) > 0);
    }

    [Fact]
    public async Task The_beat_starts_without_a_world_not_built_within_the_load_timeout()
    {
        await using var relay = await RelayClients.Start(BeatTimings.Quick);
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var room = await RelayClients.Open(anna, new CreateRoom("Beat", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false), ben);
        // Anna's world is built; Ben's game never builds one.
        var match = await RelayClients.Begin(anna, [anna, ben], play: false);
        anna.Begin(match);
        var clock = relay.Relay.Clock(room.Id);
        RelayClients.Run([anna], clock);

        await Until(relay, () => anna.Tick >= 20, TimeSpan.FromSeconds(15), "Anna's match under way");
        var (started, anchor) = relay.Relay.WithBeat(room.Id, b => (b.Started, b.Anchor!.Value));
        Assert.True(anchor - started >= BeatTimings.Quick.LoadTimeout, $"the beat started after {(anchor - started).TotalSeconds:F2} s");
        Assert.Contains($"room {room.Id}: the beat starts: 1 of 2 loaded after 2 s", relay.Lines);

        // Ben, who never plays, counts late from the start: marked, then the computer takes his race.
        await Until(relay, () => anna.Heard.Any(m => m is PlayerLeft), TimeSpan.FromSeconds(10), "Ben handed over");
        RelayClients.Halt([anna]);
        var bens = match.Players.Single(p => p.ClientId == ben.Id && !p.Ai).Player!.Value;
        Assert.Collection(Heard(anna, bens),
            m => Assert.True(m is PlayerLagging { Lagging: true }, $"{m}"),
            m => Assert.IsType<PlayerLeft>(m));
    }

    [Fact]
    public async Task One_report_of_the_end_ends_nothing_a_majority_ends_the_match_and_the_beat_stops()
    {
        await using var relay = await RelayClients.Start(Fast);
        var sent = new ConcurrentQueue<(int To, NetMessage Message)>();
        relay.Relay.Sent = (to, message) =>
        {
            if (message is Bundle or MatchEnded)
                sent.Enqueue((to, message));
        };
        var (room, clients, clock) = await Skirmish(relay, ["Anna", "Ben", "Cleo"], script: c => new Busy(c.Me));
        var (anna, ben) = (clients[0], clients[1]);
        long NextTick() => relay.Relay.WithBeat(room.Id, b => b.NextTick);
        RelayClients.Run(clients, clock);
        await PlayOn(relay, clock,20);

        // Anna's world says the match was decided a second and a half ahead: one of three ends nothing.
        var decided = NextTick() + 30;
        anna.Connection.Send(new MatchDecided(decided));
        await Task.Delay(TimeSpan.FromSeconds(0.5));
        Assert.Contains($"room {room.Id}: player {anna.Me} says the match was decided at tick {decided}", relay.Lines);
        Assert.Null(relay.Relay.WithBeat(room.Id, b => b.DecidedAt));
        Assert.DoesNotContain(sent, s => s.Message is MatchEnded);

        // Ben's says the same: two of three, and the match ends there for everyone.
        ben.Connection.Send(new MatchDecided(decided));
        await Until(relay, () => clients.All(c => c.Ended is not null), TimeSpan.FromSeconds(5), "the end told to everyone");
        Assert.All(clients, c => Assert.Equal(new MatchEnded(decided), c.Ended));
        Assert.Contains($"room {room.Id}: the match is decided at tick {decided}: 2 of 3 playing agree", relay.Lines);

        // The beat runs on for the hold after it, with no command from the decided tick on, and then stops.
        await Until(relay, () => relay.Relay.WithBeat(room.Id, b => b.Stopped), TimeSpan.FromSeconds(10), "the beat stopped");
        var last = NextTick();
        await Task.Delay(TimeSpan.FromSeconds(0.3));
        Assert.Equal(last, NextTick());
        RelayClients.Halt(clients);
        Assert.Contains($"room {room.Id}: the beat stops at tick {last}: the hold after the decision is over", relay.Lines);
        var bundles = sent.Where(s => s.To == anna.Id).Select(s => s.Message).OfType<Bundle>().ToList();
        Assert.Equal(Enumerable.Range(0, (int)last).Select(t => (long)t), bundles.Select(b => b.Tick));
        Assert.True(last > decided, $"stopped at {last}");
        Assert.Contains(bundles, b => b.Tick < decided && b.Turns.Count > 0);
        Assert.All(bundles.Where(b => b.Tick >= decided), b => Assert.Empty(b.Turns));
        Assert.True(RelayClients.AssertSumsAgree(clients) > 0);
    }

    [Fact]
    public async Task The_beat_goes_through_a_room_only_when_something_of_it_is_due()
    {
        // The relay's own timings: a room's bundles every 50 ms, another room waiting a minute for its world to be built.
        await using var relay = RelayServer.Start(0, System.Net.IPAddress.Loopback);
        var lines = new ConcurrentQueue<string>();
        relay.Log += lines.Enqueue;
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var running = await Alone(anna, load: true);
        var loading = await Alone(ben, load: false);
        await Task.Delay(TimeSpan.FromSeconds(2));

        // The running room was gone through for its bundles (about one each, as each came due), the loading one never: a relay
        // of many rooms would otherwise go through them all at every bundle of any.
        var (advanced, bundles) = relay.Beats(running);
        Assert.True(bundles >= 20, $"{bundles} bundles");
        Assert.True(advanced <= bundles + 5, $"gone through {advanced} times for {bundles} bundles");
        Assert.Equal((0, 0L), relay.Beats(loading));
        Assert.Single(lines, l => Regex.IsMatch(l, $@"^room {running}: bundle 0 was due at \d{{13}} ms UTC$"));
        Assert.DoesNotContain(lines, l => l.StartsWith($"room {loading}: bundle 0", StringComparison.Ordinal));

        static async Task<int> Alone(ChatTests.Client client, bool load)
        {
            var room = await client.Open("Alone", 2);
            client.Connection.Send(new SetPlayer(Race.Red, Ready: true));
            await client.Next<RoomState>(s => s.Players.Single().Ready);
            client.Connection.Send(new StartMatch());
            await client.Next<MatchStarted>();
            if (load)
            {
                client.Connection.Send(new MatchLoaded());
                await client.Next<Bundle>();
            }
            return room;
        }
    }

    [Fact]
    public async Task A_world_rebuilt_after_the_end_was_agreed_is_told_of_the_end_with_its_log()
    {
        await using var relay = await RelayClients.Start();
        var told = new ConcurrentQueue<(int To, NetMessage Message)>();
        relay.Relay.Sent = (to, message) =>
        {
            if (message is MatchResumed or MatchEnded or Bundle)
                told.Enqueue((to, message));
        };
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var cleo = await relay.Connect("Cleo", web: false);
        var room = await RelayClients.Open(anna, new CreateRoom("Beat", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], new MatchOptions(), FillWithAi: false), ben, cleo);
        List<RelayClient> clients = [anna, ben, cleo];
        await RelayClients.Begin(anna, clients);
        await RelayClients.PlayTo(100, clients);

        // Cleo quits; the two still playing say the match was decided at 90: it is, and the beat stops at once.
        await cleo.Leave();
        anna.Connection.Send(new MatchDecided(90));
        ben.Connection.Send(new MatchDecided(90));
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, b => b.Stopped), TimeSpan.FromSeconds(10), "the beat stopped");
        var last = relay.Relay.WithBeat(room.Id, b => b.NextTick) - 1;

        // Cleo comes back by name, and Ben's game asks for its world to be rebuilt: each gets the log, then the end.
        var back = await relay.Rejoin("Cleo", web: false, room.Id);
        ben.RejoinHere();
        await RelayClients.Until(() => told.Count(t => t.To == ben.Id && t.Message is MatchResumed) == 1, TimeSpan.FromSeconds(10), "Ben's rebuild");
        await RelayClients.PlayTo(last + 1, [back]);
        Assert.Equal(new MatchEnded(90), back.Ended);
        foreach (var id in new[] { back.Id, ben.Id })
        {
            var package = told.Where(t => t.To == id).Select(t => t.Message).SkipWhile(m => m is not MatchResumed).ToList();
            Assert.Equal(new MatchEnded(90), package[^1]);
            Assert.Equal(last, Assert.IsType<Bundle>(package[^2]).Tick);
        }
        Assert.True(RelayClients.AssertSumsAgree([anna, ben, back]) > 0);
    }

    [Fact]
    public async Task A_key_rejoin_that_leaves_its_own_match_without_members_is_refused()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var room = await RelayClients.Open(anna, new CreateRoom("Beat", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false), ben);
        List<RelayClient> clients = [anna, ben];
        await RelayClients.Begin(anna, clients);
        await RelayClients.PlayTo(20, clients);

        // Ben quits, and Anna, the last member, turns up with his key: taking his seat she leaves hers, and with it the match
        // its last member, which goes with its beat and log.
        var key = ben.Key;
        await ben.Leave();
        await TickIn(relay, $@"^room {room.Id}: player {ben.Me} gone from tick (\d+)$");
        anna.Connection.Send(new RejoinMatch(room.Id, ben.Me, key));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => anna.Next<MatchResumed>());

        Assert.Equal("Anna: that match is not running", refused.Message);
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: dropped with its last member", StringComparison.Ordinal));
        Assert.Throws<KeyNotFoundException>(() => relay.Relay.WithBeat(room.Id, b => b.NextTick));
    }

    [Fact]
    public async Task No_bundle_outgrows_a_frame_the_commands_that_would_make_it_are_dropped_and_the_match_goes_on()
    {
        await using var relay = await RelayClients.Start();
        var sent = new ConcurrentQueue<Bundle>();
        relay.Relay.Sent = (_, message) =>
        {
            if (message is Bundle bundle)
                sent.Enqueue(bundle);
        };
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var room = await RelayClients.Open(anna, new CreateRoom("Beat", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false), ben);
        List<RelayClient> clients = [anna, ben];
        await RelayClients.Begin(anna, clients);
        await RelayClients.PlayTo(40, clients);

        // Both order a move of 60,000 machines for the same tick: each turn fits in a frame (some 660 KB), the two together
        // would not, and no client could read the bundle (nor would one after a reconnect, which sends it again).
        var tick = anna.Session.NextLocalTurn;
        Assert.Equal(tick, ben.Session.NextLocalTurn);
        anna.Session.Queue(Huge(anna.Race, 1, 60_000));
        ben.Session.Queue(Huge(ben.Race, 2, 60_000));
        var playing = RelayClients.PlayTo(80, clients);
        Assert.True(await Task.WhenAny(playing, Task.Delay(TimeSpan.FromSeconds(30))) == playing, "the match stood still");
        await playing;

        Assert.All(sent, b => Assert.True(NetJson.Encode(b).Length <= Framing.MaxFrame, $"bundle {b.Tick} of {NetJson.Encode(b).Length} bytes"));
        var orders = sent.SelectMany(b => b.Turns.Where(t => t.Commands.Any(c => c is MoveCommand { Machines.Count: 60_000 })).Select(t => (b.Tick, t.Player))).Distinct().ToList();
        Assert.Equal(tick, Assert.Single(orders).Tick);
        var dropped = orders[0].Player == anna.Me ? ben.Me : anna.Me;
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: player {dropped}'s turn for tick {tick} dropped: its commands would make bundle {tick} larger than", StringComparison.Ordinal));
        RelayClients.AssertAgree(clients);
    }

    [Fact]
    public async Task Turns_going_back_or_too_far_ahead_are_dropped()
    {
        await using var relay = await RelayClients.Start();
        var sent = new ConcurrentQueue<Bundle>();
        relay.Relay.Sent = (_, message) =>
        {
            if (message is Bundle bundle)
                sent.Enqueue(bundle);
        };
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var room = await RelayClients.Open(anna, new CreateRoom("Beat", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false), ben);
        await RelayClients.Begin(anna, [anna, ben]);
        await RelayClients.PlayTo(60, [anna, ben]);

        // Once the relay has every turn sent so far: Anna's newest stamp, and the next bundle the one after it.
        var stamp = anna.Session.NextLocalTurn - 1;
        await Until(relay, () => relay.Relay.WithBeat(room.Id, b => (b.Seat(anna.Me)!.Stamped, b.NextTick)) == (stamp, stamp + 1),
            TimeSpan.FromSeconds(10), $"Anna's stamp {stamp} in");
        Command Stop() => new StopCommand(anna.Race, []);
        anna.Connection.Send(new Turn(stamp - 10, anna.Me, [Stop()]));
        anna.Connection.Send(new Turn(stamp, anna.Me, [Stop()]));
        anna.Connection.Send(new Turn(stamp + 1 + BeatTimings.Scripted.MaxDelay + 1, anna.Me, [Stop()]));
        await RelayClients.PlayTo(120, [anna, ben]);

        Assert.Contains($"room {room.Id}: player {anna.Me}'s turn for tick {stamp - 10} dropped: not past its last, {stamp}", relay.Lines);
        Assert.Contains($"room {room.Id}: player {anna.Me}'s turn for tick {stamp + 6} dropped: more than 4 past the next bundle, {stamp + 1}", relay.Lines);
        // Neither has a script: none of their bundles carries a command.
        Assert.DoesNotContain(sent, b => b.Turns.Any(t => t.Commands.Count > 0));
        RelayClients.AssertAgree([anna, ben]);
    }

    [Fact]
    public async Task The_log_is_sealed_in_order_into_pieces_every_rebuild_shares_each_fitting_in_a_frame()
    {
        // Segments of 50 bundles, pieces of the relay's own size.
        var timings = BeatTimings.Scripted with { SegmentTicks = 50 };
        await using var relay = await RelayClients.Start(timings);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.Relay.BeforeSeal = (from, _) => from == 0 ? gate.Task : Task.CompletedTask;
        var packages = new ConcurrentDictionary<int, ConcurrentQueue<NetMessage>>();
        relay.Relay.Sent = (to, message) =>
        {
            if (message is LogPart or MatchResumed)
                packages.GetOrAdd(to, _ => new ConcurrentQueue<NetMessage>()).Enqueue(message);
        };
        try
        {
            var anna = await relay.Connect("Anna", web: false);
            var ben = await relay.Connect("Ben", web: true);
            var cleo = await relay.Connect("Cleo", web: false);
            var room = await RelayClients.Open(anna, new CreateRoom("Beat", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], new MatchOptions(), FillWithAi: false), ben, cleo);
            List<RelayClient> clients = [anna, ben, cleo];
            await RelayClients.Begin(anna, clients);
            foreach (var client in clients)
                client.Script = new CommanderScript(client.Me, 30 + client.Me.Value);

            // Anna orders four moves of 40,000 machines nobody has in the first run: some 1.7 MB of JSON, more than one piece
            // when gzipped.
            for (var i = 0; i < 4; i++)
            {
                anna.Session.Queue(Huge(anna.Race, i));
                await RelayClients.PlayTo(5 + 10 * i, clients);
            }
            await RelayClients.PlayTo(220, clients);

            // The first run is not sealed yet; the three after it are, and wait behind it.
            await Until(relay, () => relay.Relay.LogShape(room.Id).Sealing.Skip(1).All(r => r.Done), TimeSpan.FromSeconds(10), "the later runs sealed");
            var shape = relay.Relay.LogShape(room.Id);
            Assert.Empty(shape.Segments);
            Assert.Equal([(0L, 49L, false), (50L, 99L, true), (100L, 149L, true), (150L, 199L, true)], shape.Sealing);
            Assert.Equal(200, shape.TailFrom);

            // A rebuild meanwhile gets every bundle as a plain one.
            ben.RejoinHere();
            await RelayClients.PlayTo(260, clients);
            Assert.Equal((1, 0), (ben.Rebuilds, ben.Resumed!.Parts));
            Assert.DoesNotContain(packages[ben.Id], m => m is LogPart);

            // Once the first is sealed, every run joins the segments in order.
            gate.SetResult();
            await Until(relay, () => relay.Relay.LogShape(room.Id) is { Sealing.Count: 0, Segments.Count: 5 }, TimeSpan.FromSeconds(10), "every run sealed");
            shape = relay.Relay.LogShape(room.Id);
            Assert.Equal([(0L, 49L), (50L, 99L), (100L, 149L), (150L, 199L), (200L, 249L)], shape.Segments.Select(s => (s.From, s.To)));
            Assert.True(shape.Segments[0].Parts > 1, $"the first segment in {shape.Segments[0].Parts} pieces");

            // Two rebuilds at once get the very same pieces, each a frame a line can carry, then the bundles since as plain ones.
            cleo.RejoinHere();
            anna.RejoinHere();
            await RelayClients.PlayTo(320, clients);
            var cleos = packages[cleo.Id].OfType<LogPart>().ToList();
            var annas = packages[anna.Id].OfType<LogPart>().ToList();
            Assert.Equal(shape.Segments.Sum(s => s.Parts), cleos.Count);
            Assert.Equal((cleos.Count, cleos.Count), (cleo.Resumed!.Parts, anna.Resumed!.Parts));
            Assert.Equal(cleos.Count, annas.Count);
            Assert.All(cleos.Zip(annas), pair => Assert.Same(pair.First, pair.Second));
            Assert.Contains(cleos, part => part.Data.Length == timings.PartBytes);
            Assert.All(cleos, part =>
            {
                Assert.InRange(part.Data.Length, 1, timings.PartBytes);
                Assert.True(NetJson.Encode(part).Length <= Framing.MaxFrame, $"a piece of {NetJson.Encode(part).Length} bytes");
            });

            // Every rebuilt world plays on to the same sums, and every recording plays back.
            Assert.True(RelayClients.AssertAgree(clients) >= 320 / 20);
            foreach (var client in clients)
                RelayClients.AssertPlaysBack(client);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// A run of the log whose sealing fails is tried once more (P2 slice S3); failing again it stays plain bundles, and so do the
    /// runs after it, which wait for it: a rebuild then gets the sealed segments before it and every bundle from it on as
    /// plain ones, without a gap, and its world agrees.
    /// </summary>
    [Fact]
    public async Task A_run_whose_sealing_fails_is_tried_again_and_else_stays_plain_bundles_which_a_rebuild_gets_without_a_gap()
    {
        await using var relay = await RelayClients.Start(BeatTimings.Scripted with { SegmentTicks = 50 });
        // The run from 0 fails once and is sealed the second time; the one from 50 fails both times.
        var tries = new ConcurrentDictionary<long, int>();
        relay.Relay.BeforeSeal = (from, _) => tries.AddOrUpdate(from, 1, (_, n) => n + 1) is var n && (from == 0 && n == 1 || from == 50)
            ? throw new InvalidDataException($"no seal for {from} this time")
            : Task.CompletedTask;
        var (room, clients, _) = await Skirmish(relay, ["Anna", "Ben", "Cleo"]);
        await RelayClients.PlayTo(220, clients);
        var given = $"room {room.Id}: sealing ticks 50–99 failed; they stay plain bundles: no seal for 50 this time";
        await Until(relay, () => relay.Relay.LogShape(room.Id) is { Segments.Count: 1 } log && log.Sealing.Skip(1).All(r => r.Done) && relay.Lines.Contains(given),
            TimeSpan.FromSeconds(10), "every run tried");

        Assert.Contains($"room {room.Id}: sealing ticks 0–49 failed, trying again: no seal for 0 this time", relay.Lines);
        Assert.Contains($"room {room.Id}: sealing ticks 50–99 failed, trying again: no seal for 50 this time", relay.Lines);
        Assert.Equal((2, 2, 1, 1), (tries[0], tries[50], tries[100], tries[150]));
        var shape = relay.Relay.LogShape(room.Id);
        Assert.Equal([(0L, 49L)], shape.Segments.Select(s => (s.From, s.To)));
        Assert.Equal([(50L, 99L, false), (100L, 149L, true), (150L, 199L, true)], shape.Sealing);

        // A rebuild: the one segment in pieces, then every bundle from 50 on, and the world agrees with everyone's.
        var ben = clients[1];
        ben.RejoinHere();
        await RelayClients.PlayTo(300, clients);
        Assert.Equal(1, ben.Rebuilds);
        Assert.Equal(shape.Segments[0].Parts, ben.Resumed!.Parts);
        Assert.Empty(ben.Gaps);
        Assert.True(RelayClients.AssertAgree(clients) >= 300 / 20);
        RelayClients.AssertPlaysBack(ben);
    }

    /// <summary>
    /// A skirmish of one person a race (Red, Blue, Green, Yellow in turn) on m_desert, every second client over a WebSocket
    /// unless <paramref name="via"/> gives it a port (a <see cref="LineProxy"/>'s, over TCP); begun, every world built, each
    /// person with a script (a <see cref="CommanderScript"/> unless <paramref name="script"/> says otherwise); not run yet.
    /// Returns the room, the clients in the order named, and the relay's clock of the match.
    /// </summary>
    private static async Task<(RoomState Room, List<RelayClient> Clients, Func<long?> Clock)> Skirmish(RelayClients relay, string[] names,
        Func<int, int?>? via = null, Func<RelayClient, RelayScript>? script = null)
    {
        var clients = new List<RelayClient>();
        for (var i = 0; i < names.Length; i++)
        {
            var port = via?.Invoke(i);
            clients.Add(await relay.Connect(names[i], web: port is null && i % 2 == 1, port));
        }
        Race[] races = [Race.Red, Race.Blue, Race.Green, Race.Yellow];
        var room = await RelayClients.Open(clients[0],
            new CreateRoom("Beat", "m_desert", "m_desert", races.Take(names.Length).ToList(), new MatchOptions(), FillWithAi: false), [.. clients.Skip(1)]);
        await RelayClients.Begin(clients[0], clients);
        foreach (var client in clients)
            client.Script = script?.Invoke(client) ?? new CommanderScript(client.Me, 40 + client.Me.Value);
        return (room, clients, relay.Relay.Clock(room.Id));
    }

    /// <summary>Until the relay's clock has gone on by <paramref name="ticks"/>.</summary>
    private static Task PlayOn(RelayClients relay, Func<long?> clock, int ticks)
    {
        var until = (clock() ?? 0) + ticks;
        return Until(relay, () => clock() >= until, TimeSpan.FromSeconds(ticks / 20.0 + 30), $"the relay's clock at tick {until}");
    }

    /// <summary>Waits as <see cref="RelayClients.Until"/> does; a failure shows the relay's last lines.</summary>
    private static async Task Until(RelayClients relay, Func<bool> done, TimeSpan within, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > within)
                Assert.Fail($"{what}: not within {within.TotalSeconds:0.#} s; the relay's last lines:{Environment.NewLine}{string.Join(Environment.NewLine, relay.Lines.TakeLast(40))}");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// The client's world never stood more than <see cref="MostBehind"/> ticks behind the relay's clock in the samples of its
    /// thread, from <paramref name="from"/> on (the first second has the start's warm-up), while it played.
    /// </summary>
    private static void AssertNeverStalled(RelayClient client, long from = MostBehind)
    {
        var samples = client.Samples.Where(s => s.Clock >= from).ToList();
        Assert.True(samples.Count > 10, $"{client.Name} took {samples.Count} pace samples");
        var worst = samples.MaxBy(s => s.Clock!.Value - s.Tick);
        Assert.True(worst.Clock - worst.Tick <= MostBehind,
            $"{client.Name}'s world stood at tick {worst.Tick} when the relay's clock was at {worst.Clock}, {worst.Ms:F0} ms into its run");
    }

    /// <summary>The comings and goings of a player a client heard, in order, those from <paramref name="from"/> on.</summary>
    private static List<NetMessage> Heard(RelayClient client, PlayerId player, long from = 0) =>
        client.Heard.Where(m => Who(m) == player && FromTick(m) >= from).ToList();

    /// <summary>The tick in the first line of the relay's log the pattern matches (its group 1), once there is one.</summary>
    private static async Task<long> TickIn(RelayClients relay, string pattern)
    {
        var regex = new Regex(pattern);
        Match? found = null;
        await Until(relay, () => (found = relay.Lines.Select(l => regex.Match(l)).FirstOrDefault(m => m.Success)) is not null,
            TimeSpan.FromSeconds(10), $"a line like {pattern}");
        return long.Parse(found!.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static PlayerId Who(NetMessage change) => change switch
    {
        PlayerLeft left => left.Player,
        PlayerReturned back => back.Player,
        PlayerLagging lagging => lagging.Player,
        PlayerJoined joined => joined.Player,
        _ => throw new ArgumentException($"{change} is no coming or going"),
    };

    private static long FromTick(NetMessage change) => change switch
    {
        PlayerLeft left => left.FromTick,
        PlayerReturned back => back.FromTick,
        PlayerLagging lagging => lagging.FromTick,
        PlayerJoined joined => joined.FromTick,
        _ => throw new ArgumentException($"{change} is no coming or going"),
    };

    /// <summary>
    /// A move of <paramref name="machines"/> machines nobody has (40,000 are some 440 KB of JSON), which every world carries
    /// out alike (moving nothing).
    /// </summary>
    private static MoveCommand Huge(Race race, int seed, int machines = 40_000)
    {
        var random = new Random(seed);
        return new MoveCommand(race, Enumerable.Range(0, machines).Select(_ => random.Next(1_000_000_000, int.MaxValue)).ToList(), new Vector2(100, 100));
    }

    /// <summary>A person who gives an order every tick: a stop to none of their machines, which every world takes alike.</summary>
    private sealed class Busy(PlayerId player) : RelayScript(player, 0)
    {
        public override IReadOnlyList<Command> Next(GameWorld world) => [new StopCommand(world.Players.RaceOf(Player), [])];
    }
}

[CollectionDefinition(nameof(BeatRelayTests), DisableParallelization = true)]
public class BeatRelayCollection;
