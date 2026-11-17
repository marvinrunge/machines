using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// The relay's beat on made-up times (P2 design §2, T3): where turns are placed, when bundles close, who is late, marked,
/// handed over, back or joined, how the delay adapts and how the end is agreed. Nothing here touches a socket or the clock.
/// </summary>
public class BeatTests
{
    private static readonly PlayerId Red = PlayerId.Of(Race.Red), Blue = PlayerId.Of(Race.Blue), Green = PlayerId.Of(Race.Green), Yellow = PlayerId.Of(Race.Yellow);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static Command Drive(Race race, int machine) => new DriveCommand(race, machine, true);

    private static MatchPlayer Person(Race race, string name) => new(1, name, race, race, Ai: false, PlayerId.Of(race));

    private static MatchPlayer Computer(Race race) => new(0, "AI", race, race, Ai: true, PlayerId.Of(race));

    /// <summary>A skirmish: Anna Red, Ben Blue, Cleo Green as <paramref name="people"/> says, the computer the rest.</summary>
    private static MatchInfo Skirmish(int people = 2)
    {
        var names = new[] { (Race.Red, "Anna"), (Race.Blue, "Ben"), (Race.Green, "Cleo"), (Race.Yellow, "Dan") };
        return new MatchInfo("m_desert", "m_desert", new MatchOptions(), 1, 4,
            [.. names.Select((n, i) => i < people ? Person(n.Item1, n.Item2) : Computer(n.Item1))]);
    }

    /// <summary>A battle: Anna Red and Ben Green, bots on Blue and Yellow.</summary>
    private static MatchInfo Battle() =>
        new("m_desert", "m_desert", new MatchOptions { Battle = new BattleOptions() }, 1, 4,
            [Person(Race.Red, "Anna"), Computer(Race.Blue), Person(Race.Green, "Ben"), Computer(Race.Yellow)]);

    /// <summary>
    /// A relay around one beat: its clock (set by the test), the bundles it sent, its log, and clients that make their turns as
    /// a client on time does: one a slot, stamped the delay past the bundle the beat closes next, with the newest bundle seen
    /// <see cref="Lag"/> ticks further back than the one before it.
    /// </summary>
    private sealed class Relay
    {
        public Relay(BeatTimings timings, MatchInfo match, bool load = true)
        {
            Beat = new Beat(timings, match, TimeSpan.Zero);
            Beat.Log += Lines.Add;
            if (!load)
                return;
            foreach (var seat in Beat.Seats.Where(s => s.State == SeatState.Playing))
                Beat.Loaded(seat.Player, Now);
            Assert.Equal(TimeSpan.Zero, Beat.Anchor);
        }

        public Beat Beat { get; }

        public TimeSpan Now { get; set; }

        public List<Bundle> Sent { get; } = [];

        public List<string> Lines { get; } = [];

        /// <summary>How many ticks further back than an on-time line each player's newest bundle seen lies.</summary>
        public Dictionary<PlayerId, int> Lag { get; } = [];

        public Offered Send(PlayerId player, long tick, IReadOnlyList<Command>? commands = null, long seen = -1, int held = 0) =>
            Beat.Offer(new Turn(tick, player, commands ?? [], seen, held), Now);

        /// <summary>The player's turn as their client makes it now, or none where its stamp would not rise.</summary>
        public Offered? OnTime(PlayerId player, params Command[] commands)
        {
            var stamp = Beat.NextTick + Beat.Delay;
            if (stamp <= Beat.Seat(player)!.Stamped)
                return null;
            return Send(player, stamp, commands, seen: Beat.NextTick - 1 - Lag.GetValueOrDefault(player), held: 1);
        }

        public IReadOnlyList<Bundle> Close()
        {
            var closed = Beat.Close(Now);
            Sent.AddRange(closed);
            return closed;
        }

        /// <summary>
        /// Runs the beat through bundle <paramref name="last"/>: 10 ms before each is due the players named make their turns,
        /// then it closes when due.
        /// </summary>
        public List<Bundle> Play(long last, params PlayerId[] onTime)
        {
            var closed = new List<Bundle>();
            while (Beat.NextTick <= last && !Beat.Stopped)
            {
                var tick = Beat.NextTick;
                Now = Max(Now, Beat.Due(tick) - Ms(10));
                foreach (var player in onTime)
                    OnTime(player);
                Now = Max(Now, Beat.Due(tick));
                closed.AddRange(Close());
            }
            return closed;
        }

        public Bundle At(long tick) => Sent.Single(b => b.Tick == tick);

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    private static List<NetMessage> PresenceOf(IEnumerable<Bundle> bundles) => [.. bundles.SelectMany(b => b.Presence ?? [])];

    [Fact]
    public void An_on_time_turn_is_placed_on_the_tick_it_asks_for_and_never_moved()
    {
        var relay = new Relay(BeatTimings.Default, Skirmish());

        Assert.Equal(Offered.Placed, relay.Send(Red, 4, [Drive(Race.Red, 1)]));
        Assert.Equal(Offered.Placed, relay.Send(Blue, 0, [Drive(Race.Blue, 2)]));
        Assert.Equal(Offered.Stamped, relay.Send(Red, 5));
        relay.Play(5);

        Assert.Equal(0, relay.Beat.Moved);
        Assert.Equal([new Turn(0, Blue, [Drive(Race.Blue, 2)], Asked: 0)], relay.At(0).Turns, TurnComparer.Instance);
        Assert.Equal([new Turn(4, Red, [Drive(Race.Red, 1)], Asked: 4)], relay.At(4).Turns, TurnComparer.Instance);
        Assert.All(new long[] { 1, 2, 3, 5 }, tick => Assert.True(relay.At(tick).Empty));
        Assert.All(relay.Sent, bundle => Assert.Equal(4, bundle.Delay));
        Assert.Equal(Enumerable.Range(0, 6).Select(t => (long)t), relay.Sent.Select(b => b.Tick));
    }

    [Fact]
    public void Late_turns_go_on_the_first_open_bundle_merged_in_their_order_and_say_the_newest_stamp()
    {
        var relay = new Relay(BeatTimings.Default, Skirmish());
        relay.Play(4);
        Assert.Equal(5, relay.Beat.NextTick);

        Assert.Equal(Offered.Moved, relay.Send(Red, 2, [Drive(Race.Red, 1)]));
        Assert.Equal(Offered.Placed, relay.Send(Blue, 5, [Drive(Race.Blue, 7)]));
        Assert.Equal(Offered.Moved, relay.Send(Red, 3, [Drive(Race.Red, 2), Drive(Race.Red, 3)]));
        Assert.Equal(Offered.Placed, relay.Send(Red, 5, [Drive(Race.Red, 4)]));
        Assert.Equal(Offered.Placed, relay.Send(Red, 6, [Drive(Race.Red, 5)]));
        relay.Play(6);

        Assert.Equal(2, relay.Beat.Moved);
        Assert.Equal(
            [
                new Turn(5, Red, [Drive(Race.Red, 1), Drive(Race.Red, 2), Drive(Race.Red, 3), Drive(Race.Red, 4)], Asked: 5),
                new Turn(5, Blue, [Drive(Race.Blue, 7)], Asked: 5),
            ], relay.At(5).Turns, TurnComparer.Instance);
        Assert.Equal([new Turn(6, Red, [Drive(Race.Red, 5)], Asked: 6)], relay.At(6).Turns, TurnComparer.Instance);

        // On time alone, and late alone: the bundle says which stamp it took last.
        relay.Send(Red, 7, [Drive(Race.Red, 6)]);
        relay.Play(9);
        Assert.Equal(new Turn(7, Red, [Drive(Race.Red, 6)], Asked: 7), relay.At(7).Turns.Single(), TurnComparer.Instance);
        Assert.Equal(Offered.Moved, relay.Send(Red, 8, [Drive(Race.Red, 8)]));
        relay.Play(10);
        Assert.Equal(new Turn(10, Red, [Drive(Race.Red, 8)], Asked: 8), relay.At(10).Turns.Single(), TurnComparer.Instance);
    }

    [Fact]
    public void Turns_going_back_or_too_far_ahead_are_dropped_and_noted_once_a_minute()
    {
        var relay = new Relay(BeatTimings.Default, Skirmish());
        relay.Send(Red, 6, [Drive(Race.Red, 1)]);

        Assert.Equal(Offered.WentBack, relay.Send(Red, 6, [Drive(Race.Red, 2)]));
        Assert.Equal(Offered.WentBack, relay.Send(Red, 5, [Drive(Race.Red, 2)]));
        Assert.Equal(Offered.TooFar, relay.Send(Red, BeatTimings.Default.MaxDelay + 1, [Drive(Race.Red, 3)]));
        Assert.Equal(Offered.Placed, relay.Send(Red, BeatTimings.Default.MaxDelay, [Drive(Race.Red, 4)]));
        Assert.Equal(Offered.Ignored, relay.Send(Yellow, 5, [Drive(Race.Yellow, 5)]));
        Assert.Equal(BeatTimings.Default.MaxDelay, relay.Beat.Seat(Red)!.Stamped);

        Assert.Single(relay.Lines, l => l.Contains("not past its last"));
        Assert.Single(relay.Lines, l => l.Contains("past the next bundle"));
        relay.Now = TimeSpan.FromMinutes(1);
        relay.Send(Red, 3);
        Assert.Equal(2, relay.Lines.Count(l => l.Contains("not past its last")));

        relay.Play(BeatTimings.Default.MaxDelay);
        Assert.Equal([Drive(Race.Red, 1)], relay.At(6).Turns.Single().Commands);
        Assert.Equal([Drive(Race.Red, 4)], relay.At(BeatTimings.Default.MaxDelay).Turns.Single().Commands);
    }

    [Fact]
    public void A_silent_player_is_marked_handed_over_and_returns_once_on_time_again_without_their_commands_meanwhile()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish());

        relay.Play(29, Red);

        // Late from bundle 0: the mark at the LagMark-th late bundle, the handover at the LagLeave-th.
        Assert.Equal([new PlayerLagging(Blue, quick.LagMark - 1, true), new PlayerLeft(Blue, quick.LagLeave - 1)], PresenceOf(relay.Sent));
        var seat = relay.Beat.Seat(Blue)!;
        Assert.Equal((SeatState.Returning, ReturnKind.Lagged, quick.LagLeave - 1L, false), (seat.State, seat.Returning, seat.Since, seat.Marked));
        Assert.Equal(SeatState.Playing, relay.Beat.Seat(Red)!.State);

        // On time again: stamped, its commands dropped while it returns, back after BackAfter bundles.
        Assert.Equal(Offered.Stamped, relay.OnTime(Blue, Drive(Race.Blue, 1)));
        relay.Play(29 + quick.BackAfter + 2, Red, Blue);

        var back = 30 + quick.BackAfter - 1;
        Assert.Equal(new PlayerReturned(Blue, back), PresenceOf(relay.Sent).Last());
        Assert.Equal(SeatState.Playing, seat.State);
        Assert.DoesNotContain(relay.Sent, b => b.Turns.Any(t => t.Player == Blue));
        Assert.Equal(quick.LagLeave, seat.Late);
    }

    [Fact]
    public void A_mark_goes_after_BackAfter_on_time_bundles()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish());

        relay.Play(9, Red);
        Assert.True(relay.Beat.Seat(Blue)!.Marked);
        // While marked, the player's commands still go out: a slow client drives on with its late turns.
        Assert.Equal(Offered.Placed, relay.OnTime(Blue, Drive(Race.Blue, 3)));
        relay.Play(20, Red, Blue);

        Assert.Equal([new PlayerLagging(Blue, quick.LagMark - 1, true), new PlayerLagging(Blue, 10 + quick.BackAfter - 1, false)], PresenceOf(relay.Sent));
        Assert.False(relay.Beat.Seat(Blue)!.Marked);
        Assert.Equal([Drive(Race.Blue, 3)], relay.At(14).Turns.Single().Commands);
    }

    [Fact]
    public void A_seat_lagged_for_LaggedOut_bundles_is_taken_out()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish());
        var handover = quick.LagLeave - 1;

        relay.Play(handover + quick.LaggedOut - 1, Red);
        Assert.Empty(relay.Beat.Drain());
        Assert.Equal(SeatState.Returning, relay.Beat.Seat(Blue)!.State);
        relay.Play(handover + quick.LaggedOut, Red);

        Assert.Equal([new BeatNotice(new LeftMatch("Your computer cannot keep up with this match."), Blue)], relay.Beat.Drain());
        Assert.Equal(SeatState.Gone, relay.Beat.Seat(Blue)!.State);
        Assert.Equal(2, PresenceOf(relay.Sent).Count);
        Assert.Equal(Offered.Ignored, relay.OnTime(Blue));
    }

    [Fact]
    public void A_rebuild_outside_a_battle_is_held_ignores_the_old_world_s_turns_and_ends_with_the_mark_gone()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish());
        relay.Play(9, Red, Blue);

        Assert.Equal(ReturnKind.Rebuilding, relay.Beat.Rebuild(Blue));
        var seat = relay.Beat.Seat(Blue)!;
        Assert.True(seat.Held && seat.Marked);
        // The old world's turns, still on their way, count for nothing until the rebuilt world is loaded.
        Assert.Equal(Offered.Ignored, relay.Send(Blue, 30, [Drive(Race.Blue, 1)]));
        relay.Play(14, Red);
        Assert.Equal([new PlayerLagging(Blue, 10, true)], PresenceOf(relay.Sent));
        Assert.Equal(-1, seat.Stamped);

        relay.Beat.Loaded(Blue, relay.Now);
        relay.Play(25, Red, Blue);

        var back = 15 + quick.BackAfter - 1;
        Assert.Equal([new PlayerLagging(Blue, 10, true), new PlayerLagging(Blue, back, false)], PresenceOf(relay.Sent));
        Assert.Equal((SeatState.Playing, false, false), (seat.State, seat.Held, seat.Marked));
    }

    [Fact]
    public void A_held_rebuild_not_back_after_LagLeave_bundles_is_handed_over_and_returns_later()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish());
        relay.Play(9, Red, Blue);
        relay.Beat.Rebuild(Blue);

        relay.Play(10 + quick.LagLeave + 5, Red);

        Assert.Equal([new PlayerLagging(Blue, 10, true), new PlayerLeft(Blue, 10 + quick.LagLeave)], PresenceOf(relay.Sent));
        var seat = relay.Beat.Seat(Blue)!;
        Assert.Equal((SeatState.Returning, ReturnKind.Rebuilding, false, false), (seat.State, seat.Returning, seat.Held, seat.Marked));

        relay.Beat.Loaded(Blue, relay.Now);
        var from = relay.Beat.NextTick;
        relay.Play(from + quick.BackAfter, Red, Blue);
        Assert.Equal(new PlayerReturned(Blue, from + quick.BackAfter - 1), PresenceOf(relay.Sent).Last());
    }

    [Fact]
    public void A_rebuild_in_a_battle_hands_the_hero_to_a_bot_and_the_player_returns()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Battle());
        Assert.True(relay.Beat.Battle);
        Assert.Equal([SeatState.Playing, SeatState.Bot, SeatState.Playing, SeatState.Bot], relay.Beat.Seats.Select(s => s.State));
        relay.Play(9, Red, Green);
        Assert.Equal(Offered.Placed, relay.Send(Green, relay.Beat.Seat(Green)!.Stamped + 1, [Drive(Race.Green, 4)]));

        relay.Beat.Rebuild(Green);
        relay.Play(12, Red);
        relay.Beat.Loaded(Green, relay.Now);
        relay.Play(20, Red, Green);

        Assert.Equal([new PlayerLeft(Green, 10), new PlayerReturned(Green, 13 + quick.BackAfter - 1)], PresenceOf(relay.Sent));
        Assert.False(relay.Beat.Seat(Green)!.Held);
        // Its commands placed before the rebuild went with it.
        Assert.DoesNotContain(relay.Sent, b => b.Turns.Any(t => t.Player == Green));
    }

    [Fact]
    public void A_late_joiner_stays_joining_through_a_resync_ends_with_PlayerJoined_and_leaving_before_leaves_a_bot()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Battle());
        relay.Play(9, Red, Green);

        Assert.Equal(ReturnKind.Joining, relay.Beat.Rebuild(Blue, "Cleo"));
        Assert.Equal(Offered.Ignored, relay.Send(Blue, 12));
        relay.Play(11, Red, Green);
        // A resync while catching up: still joining, never a return of a bot's row.
        Assert.Equal(ReturnKind.Joining, relay.Beat.Rebuild(Blue));
        relay.Beat.Loaded(Blue, relay.Now);
        relay.Play(20, Red, Green, Blue);

        Assert.Equal([new PlayerJoined(Blue, 12 + quick.BackAfter - 1, "Cleo")], PresenceOf(relay.Sent));
        var seat = relay.Beat.Seat(Blue)!;
        Assert.Equal((SeatState.Playing, true), (seat.State, seat.Joined));
        Assert.Equal(ReturnKind.Rebuilding, relay.Beat.Rebuild(Blue));

        // Yellow's joiner leaves before joining: nothing happened in any world, the seat is the bot's again.
        relay.Beat.Rebuild(Yellow, "Dan");
        relay.Beat.Leave(Yellow);
        relay.Play(25, Red, Green);
        Assert.Equal(SeatState.Bot, relay.Beat.Seat(Yellow)!.State);
        Assert.DoesNotContain(PresenceOf(relay.Sent), m => m is PlayerLeft l && l.Player == Yellow || m is PlayerJoined j && j.Player == Yellow);
    }

    [Fact]
    public void A_player_who_leaves_or_is_barred_leaves_at_the_next_bundle_with_their_commands()
    {
        var relay = new Relay(BeatTimings.Quick, Skirmish(3));
        relay.Play(4, Red, Blue, Green);
        Assert.Equal(Offered.Placed, relay.Send(Blue, relay.Beat.Seat(Blue)!.Stamped + 1, [Drive(Race.Blue, 1)]));
        Assert.Equal(Offered.Placed, relay.Send(Green, relay.Beat.Seat(Green)!.Stamped + 1, [Drive(Race.Green, 1)]));

        relay.Beat.Leave(Blue);
        relay.Beat.Bar(Green);
        relay.Play(10, Red);

        Assert.Equal([new PlayerLeft(Blue, 5), new PlayerLeft(Green, 5)], PresenceOf(relay.Sent));
        Assert.DoesNotContain(relay.Sent, b => b.Turns.Count > 0);
        Assert.Equal((SeatState.Gone, SeatState.Barred), (relay.Beat.Seat(Blue)!.State, relay.Beat.Seat(Green)!.State));
        Assert.Throws<InvalidOperationException>(() => relay.Beat.Rebuild(Green));
        Assert.Equal(ReturnKind.Rebuilding, relay.Beat.Rebuild(Blue));
    }

    [Fact]
    public void The_delay_rises_to_the_need_after_DelayUpAfter_bundles_and_drops_by_one_after_DelayDownAfter()
    {
        var timings = BeatTimings.Quick with { DelayWindow = Beat.MinSamples };
        var relay = new Relay(timings, Skirmish(1));
        // A slow line: the newest bundle seen 6 further back, a turnaround of 340 ms, which needs 7 ticks.
        relay.Lag[Red] = 6;

        relay.Play(45, Red);

        // Samples from bundle 7 (bundle 0 was the first sent), enough of them at 26, the delay up DelayUpAfter bundles later.
        var up = 7 + Beat.MinSamples - 1 + timings.DelayUpAfter - 1;
        Assert.All(relay.Sent.Where(b => b.Tick < up), b => Assert.Equal(4, b.Delay));
        Assert.All(relay.Sent.Where(b => b.Tick >= up), b => Assert.Equal(7, b.Delay));
        Assert.Single(relay.Lines, l => l.StartsWith("delay 4 → 7", StringComparison.Ordinal));

        // A fast line again: down by one at a time.
        relay.Lag[Red] = 0;
        relay.Play(160, Red);
        var changes = relay.Sent.Zip(relay.Sent.Skip(1)).Where(p => p.First.Delay != p.Second.Delay).Select(p => (p.Second.Tick, p.Second.Delay)).ToList();
        Assert.Equal([7, 6, 5, 4, 3], changes.Select(c => c.Delay));
        Assert.All(changes.Skip(1).Zip(changes.Skip(2)), p => Assert.Equal(timings.DelayDownAfter, p.Second.Tick - p.First.Tick));
        Assert.Equal(3, relay.Beat.Delay);
        // Lowering it never sent a stamp back.
        Assert.DoesNotContain(relay.Lines, l => l.Contains("dropped"));
    }

    [Fact]
    public void A_late_turn_gives_a_sample_none_comes_from_a_marked_or_backlogged_one_and_the_window_is_empty_after_a_return()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish());
        relay.Play(5, Red, Blue);
        var seat = relay.Beat.Seat(Blue)!;
        var samples = seat.Window.Count;
        Assert.True(samples > 0);

        // With a backlog, or without a bundle seen, the turn gives no sample.
        relay.Send(Blue, relay.Beat.NextTick + 4, seen: relay.Beat.NextTick - 1, held: BundlePacer.Buffer + 2);
        relay.Send(Blue, relay.Beat.NextTick + 5, seen: -1, held: 1);
        Assert.Equal(samples, seat.Window.Count);
        relay.Send(Blue, relay.Beat.NextTick + 6, seen: relay.Beat.NextTick - 1, held: BundlePacer.Buffer + 1);
        Assert.Equal(samples + 1, seat.Window.Count);

        // Late but not marked: a sample (a line a little slower than the delay makes every turn late, and only its samples
        // can raise the delay before the mark); marked: none.
        relay.Play(14, Red);
        Assert.True(seat.LateStreak > 0 && !seat.Marked);
        Assert.Equal(Offered.Stamped, relay.Send(Blue, 14, seen: relay.Beat.NextTick - 1, held: 1));
        Assert.Equal(samples + 2, seat.Window.Count);
        relay.Play(20, Red);
        Assert.True(seat.Marked);
        Assert.Equal(Offered.Stamped, relay.Send(Blue, 15, seen: relay.Beat.NextTick - 1, held: 1));
        Assert.Equal(samples + 2, seat.Window.Count);
        relay.Play(25, Red);
        Assert.True(seat.Marked);
        relay.Play(28, Red, Blue);
        Assert.True(seat.Marked && seat.LateStreak == 0);
        Assert.Equal(samples + 2, seat.Window.Count);

        // Handed over with its old samples, back with none.
        relay.Play(60, Red);
        Assert.Equal(SeatState.Returning, seat.State);
        Assert.Equal(samples + 2, seat.Window.Count);
        relay.Play(60 + quick.BackAfter, Red, Blue);
        Assert.Equal(new PlayerReturned(Blue, 60 + quick.BackAfter), PresenceOf(relay.Sent).Last());
        Assert.Equal(SeatState.Playing, seat.State);
        Assert.Empty(seat.Window);
    }

    [Fact]
    public void Bundles_close_early_by_at_most_EarlyBy_once_everyone_is_in_and_never_with_EarlyBy_zero()
    {
        var early = BeatTimings.Default with { EarlyBy = Ms(100) };
        var relay = new Relay(early, Skirmish());
        Assert.Equal([0L], relay.Close().Select(b => b.Tick));
        relay.Now = Ms(10);
        relay.Send(Red, 8);
        Assert.Equal(relay.Beat.Due(1), relay.Beat.NextWake(relay.Now));
        Assert.Empty(relay.Close());

        relay.Send(Blue, 8);
        Assert.Equal(relay.Beat.Due(1) - Ms(100), relay.Beat.NextWake(relay.Now));
        // 1 and 2 are within 100 ms of their due times; 3 is not yet.
        Assert.Equal([1L, 2], relay.Close().Select(b => b.Tick));
        Assert.Equal(relay.Beat.Due(3) - Ms(100), relay.Beat.NextWake(relay.Now));
        relay.Now = relay.Beat.Due(3) - Ms(100);
        Assert.Equal([3L], relay.Close().Select(b => b.Tick));

        var clock = new Relay(BeatTimings.Default, Skirmish());
        Assert.Equal([0L], clock.Close().Select(b => b.Tick));
        clock.Send(Red, 8);
        clock.Send(Blue, 8);
        clock.Now = clock.Beat.Due(1) - Ms(1);
        Assert.Equal(clock.Beat.Due(1), clock.Beat.NextWake(clock.Now));
        Assert.Empty(clock.Close());
        clock.Now = clock.Beat.Due(1);
        Assert.Equal([1L], clock.Close().Select(b => b.Tick));
    }

    [Fact]
    public void With_nobody_expected_only_the_clock_closes_and_loaded_rebuilds_are_expected_when_nobody_plays()
    {
        // Scripted closes a bundle as soon as everyone's turn for it is in.
        var relay = new Relay(BeatTimings.Scripted, Skirmish());
        relay.Send(Red, 4);
        relay.Send(Blue, 4);
        Assert.Equal([0L, 1, 2, 3, 4], relay.Close().Select(b => b.Tick));
        relay.Beat.Leave(Red);
        relay.Beat.Leave(Blue);

        relay.Now = Ms(500);
        Assert.Equal(relay.Beat.Due(5), relay.Beat.NextWake(relay.Now));
        Assert.Empty(relay.Close());
        relay.Now = relay.Beat.Due(5);
        Assert.Equal([new Bundle(5, [], [new PlayerLeft(Red, 5), new PlayerLeft(Blue, 5)], 4)], relay.Close(), BundleComparer.Instance);

        // Both rejoin: once loaded, their turns close bundles early again; before, nothing does.
        relay.Beat.Rebuild(Red);
        relay.Beat.Rebuild(Blue);
        relay.Now += Ms(100);
        Assert.Equal(Offered.Ignored, relay.Send(Red, 9));
        Assert.Empty(relay.Close());
        relay.Beat.Loaded(Red, relay.Now);
        relay.Beat.Loaded(Blue, relay.Now);
        Assert.Equal(Offered.Stamped, relay.Send(Red, 9));
        Assert.Empty(relay.Close());
        relay.Send(Blue, 8);
        Assert.Equal([6L, 7, 8], relay.Close().Select(b => b.Tick));
    }

    [Fact]
    public void A_stalled_relay_closes_every_overdue_bundle_in_order_and_a_stall_of_five_seconds_marks_nobody()
    {
        var relay = new Relay(BeatTimings.Default, Skirmish());
        relay.Play(100, Red, Blue);

        // Five seconds without a bundle: the clients' worlds stand, so they stamp nothing new.
        relay.Now = relay.Beat.Due(200);
        var caught = relay.Close();
        Assert.Equal(Enumerable.Range(101, 100).Select(t => (long)t), caught.Select(b => b.Tick));
        Assert.Single(relay.Lines, l => l.StartsWith("the beat fell behind by 4950 ms: 100 bundles closed at once, to tick 200", StringComparison.Ordinal));

        relay.Play(400, Red, Blue);
        Assert.Empty(PresenceOf(relay.Sent));
        Assert.All(new[] { Red, Blue }, p => Assert.Equal((SeatState.Playing, false, 0), (relay.Beat.Seat(p)!.State, relay.Beat.Seat(p)!.Marked, relay.Beat.Seat(p)!.LateStreak)));
        // Only the bundles the stall let the clients stamp before it counted: one of them late.
        Assert.All(new[] { Red, Blue }, p => Assert.Equal(1, relay.Beat.Seat(p)!.Late));
        Assert.Equal(Enumerable.Range(0, 401).Select(t => (long)t), relay.Sent.Select(b => b.Tick));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Turns_that_waited_out_a_stall_of_the_relay_give_no_turnaround_sample_and_the_delay_stays(bool turnsFirst)
    {
        var relay = new Relay(BeatTimings.Default, Skirmish());
        relay.Play(40, Red, Blue);
        var windows = new[] { Red, Blue }.Select(p => relay.Beat.Seat(p)!.Window.Count).ToList();
        Assert.All(windows, count => Assert.True(count >= Beat.MinSamples, $"{count} samples"));

        // The relay stands still for five seconds. The turns the clients made meanwhile, from the bundles they still had in
        // hand, waited at the relay for it: the relay takes them in before it closes the overdue bundles, or after.
        relay.Now = relay.Beat.Due(41) + TimeSpan.FromSeconds(5);
        if (!turnsFirst)
            Assert.Equal(101, relay.Close().Count);
        foreach (var player in new[] { Red, Blue })
        {
            for (var i = 0; i < 3; i++)
                relay.Send(player, 45 + i, seen: 38 + i, held: 3 - i);
        }
        if (turnsFirst)
            Assert.Equal(101, relay.Close().Count);

        // The stall is the relay's, not their lines': their windows did not take it, and the delay never rose.
        var after = new[] { Red, Blue }.Select(p => relay.Beat.Seat(p)!.Window.Count).ToList();
        relay.Play(260, Red, Blue);
        Assert.True(relay.Sent.Max(b => b.Delay) <= BeatTimings.Default.StartDelay,
            string.Join(Environment.NewLine, relay.Lines.Where(l => l.StartsWith("delay ", StringComparison.Ordinal))));
        Assert.Equal(windows, after);
        // After it, the turns measure the lines again.
        Assert.All(new[] { Red, Blue }, p => Assert.Equal(BeatTimings.Default.DelayWindow, relay.Beat.Seat(p)!.Window.Count));
    }

    [Fact]
    public void Nobody_is_counted_after_their_own_report_and_the_end_needs_a_majority_on_one_tick()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish(3));
        relay.Play(54, Red, Blue, Green);

        relay.Beat.Decided(Red, 50, relay.Now);
        relay.Beat.Decided(Blue, 52, relay.Now);
        relay.Beat.Decided(Yellow, 50, relay.Now);
        // Red's world ended: it sends no more turns, and is not late for it.
        relay.Play(54 + quick.LagMark * 3, Blue, Green);

        Assert.Null(relay.Beat.DecidedAt);
        Assert.Empty(relay.Beat.Drain());
        Assert.Empty(PresenceOf(relay.Sent));
        Assert.Equal(0, relay.Beat.Seat(Red)!.LateStreak);

        relay.Beat.Decided(Green, 50, relay.Now);
        Assert.Equal(50, relay.Beat.DecidedAt);
        Assert.Equal([new BeatNotice(new MatchEnded(50))], relay.Beat.Drain());
        Assert.True(relay.Beat.Stopped);
        Assert.Null(relay.Beat.NextWake(relay.Now));
        Assert.Empty(relay.Close());
    }

    [Fact]
    public void Once_the_end_is_agreed_its_ticks_get_no_commands_nobody_is_counted_and_the_beat_stops_after_the_hold()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish(3));
        relay.Play(54, Red, Blue, Green);
        var end = relay.Beat.NextTick + 5;
        Assert.Equal(Offered.Placed, relay.Send(Blue, end - 1, [Drive(Race.Blue, 1)]));
        Assert.Equal(Offered.Placed, relay.Send(Blue, end, [Drive(Race.Blue, 2)]));

        relay.Beat.Decided(Red, end, relay.Now);
        Assert.Null(relay.Beat.DecidedAt);
        relay.Beat.Decided(Green, end, relay.Now);
        var agreed = relay.Now;

        Assert.Equal(end, relay.Beat.DecidedAt);
        Assert.Equal([new BeatNotice(new MatchEnded(end))], relay.Beat.Drain());
        Assert.False(relay.Beat.Stopped);
        Assert.Equal(Offered.Stamped, relay.Send(Blue, end + 1, [Drive(Race.Blue, 3)]));
        // Blue's world goes on running, silent: nobody is marked any more.
        var last = relay.Play(10_000);

        Assert.True(relay.Beat.Stopped);
        Assert.Null(relay.Beat.NextWake(relay.Now));
        Assert.Equal([Drive(Race.Blue, 1)], relay.Sent.SelectMany(b => b.Turns).Where(t => t.Tick >= 55).SelectMany(t => t.Commands));
        Assert.DoesNotContain(relay.Sent, b => b.Tick >= end && b.Turns.Count > 0);
        Assert.Empty(PresenceOf(relay.Sent));
        // Every bundle due within the hold went out, none after.
        Assert.True(relay.Beat.Due(relay.Sent[^1].Tick) <= agreed + quick.DecidedHold);
        Assert.True(relay.Beat.Due(relay.Beat.NextTick) > agreed + quick.DecidedHold);
        Assert.NotEmpty(last);
        Assert.Contains(relay.Lines, l => l.Contains("the hold after the decision is over"));
    }

    [Fact]
    public void The_beat_starts_once_everyone_loaded_or_after_the_load_timeout_and_a_player_still_loading_is_late()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Skirmish(), load: false);
        relay.Now = Ms(500);
        relay.Beat.Loaded(Red, relay.Now);
        Assert.Null(relay.Beat.Anchor);
        Assert.Equal(quick.LoadTimeout, relay.Beat.NextWake(relay.Now));
        // Turns before the start are placed by their own tick.
        Assert.Equal(Offered.Placed, relay.Send(Red, 4, [Drive(Race.Red, 1)]));
        relay.Now = quick.LoadTimeout - Ms(1);
        Assert.Empty(relay.Close());
        Assert.Throws<InvalidOperationException>(() => relay.Beat.Due(0));

        relay.Now = quick.LoadTimeout;
        Assert.Equal([0L], relay.Close().Select(b => b.Tick));
        Assert.Equal(quick.LoadTimeout, relay.Beat.Anchor);
        Assert.Contains("the beat starts: 1 of 2 loaded after 2 s", relay.Lines);
        relay.Play(20, Red);
        Assert.Equal([Drive(Race.Red, 1)], relay.At(4).Turns.Single().Commands);
        Assert.Equal(new PlayerLagging(Blue, quick.LagMark - 1, true), PresenceOf(relay.Sent).First());

        // Everyone loaded (or gone): at once.
        var all = new Relay(quick, Skirmish(3), load: false);
        all.Beat.Leave(Green);
        all.Now = Ms(300);
        all.Beat.Loaded(Red, all.Now);
        Assert.Null(all.Beat.Anchor);
        all.Beat.Loaded(Blue, all.Now);
        Assert.Equal(Ms(300), all.Beat.Anchor);
        Assert.Equal(all.Now, all.Beat.NextWake(all.Now));
    }

    [Fact]
    public void A_bundle_lists_its_comings_and_goings_by_player_and_the_summary_says_how_the_match_went()
    {
        var quick = BeatTimings.Quick;
        var relay = new Relay(quick, Battle());
        relay.Play(9, Red, Green);
        relay.Beat.Rebuild(Blue, "Cleo");
        relay.Beat.Loaded(Blue, relay.Now);
        relay.Play(9 + quick.BackAfter - 1, Red, Green, Blue);
        relay.Beat.Leave(Red);
        relay.Play(9 + quick.BackAfter, Green, Blue);

        Assert.Equal([new PlayerLeft(Red, 9 + quick.BackAfter), new PlayerJoined(Blue, 9 + quick.BackAfter, "Cleo")], relay.Sent[^1].Presence!);
        var ticks = 9 + quick.BackAfter + 1;
        Assert.Equal($"{ticks} ticks, 0 turns moved, late bundles by player: {Red} 0, {Blue} 0, {Green} 0; delay (bundles): 4: {ticks}; 0 closed over 20 ms late, 0 counted nobody",
            relay.Beat.Summary());
    }

    /// <summary>Turns and bundles compared by what they say: their commands are lists, which records compare by reference.</summary>
    private sealed class TurnComparer : IEqualityComparer<Turn>
    {
        public static TurnComparer Instance { get; } = new();

        public bool Equals(Turn? x, Turn? y) =>
            x is not null && y is not null && (x.Tick, x.Player, x.Seen, x.Held, x.Asked) == (y.Tick, y.Player, y.Seen, y.Held, y.Asked) && x.Commands.SequenceEqual(y.Commands);

        public int GetHashCode(Turn obj) => HashCode.Combine(obj.Tick, obj.Player);
    }

    /// <inheritdoc cref="TurnComparer"/>
    private sealed class BundleComparer : IEqualityComparer<Bundle>
    {
        public static BundleComparer Instance { get; } = new();

        public bool Equals(Bundle? x, Bundle? y) =>
            x is not null && y is not null && (x.Tick, x.Delay) == (y.Tick, y.Delay) && x.Turns.SequenceEqual(y.Turns, TurnComparer.Instance)
            && (x.Presence ?? []).SequenceEqual(y.Presence ?? []);

        public int GetHashCode(Bundle obj) => obj.Tick.GetHashCode();
    }
}
