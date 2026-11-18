using System.Text;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// A <see cref="LockstepSession"/> in bundle mode (P2 design §4.1, §4.2, §4.4, §5.1, §6.2): it runs a tick once the relay's
/// bundle for it is in, sends one rising turn a slot, tells when its own turns landed, follows the lag marks, takes the
/// relay's log lazily, and learns of a late join without ever marking the bot's row away. Also the messages of protocol 14.
/// </summary>
public class BundleSessionTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly PlayerId Anna = new(0), Ben = new(1), Cleo = new(4);

    /// <summary>Anna Red and Ben Blue on Canyon Attack; Green the computer's.</summary>
    private static MatchInfo Skirmish() => new("m_desert", "m_desert", new MatchOptions(), 7, 4,
    [
        new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false, Anna),
        new MatchPlayer(2, "Ben", Race.Blue, Race.Blue, Ai: false, Ben),
        new MatchPlayer(0, "AI", Race.Green, Race.Green, Ai: true, new PlayerId(2)),
    ]);

    /// <summary>Anna and Cleo command Red, Ben Blue.</summary>
    private static MatchInfo Together() => new("m_desert", "m_desert", new MatchOptions(), 7, 4,
    [
        new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false, Anna),
        new MatchPlayer(3, "Cleo", Race.Red, Race.Red, Ai: false, Cleo),
        new MatchPlayer(2, "Ben", Race.Blue, Race.Blue, Ai: false, Ben),
        new MatchPlayer(0, "AI", Race.Green, Race.Green, Ai: true, new PlayerId(2)),
    ]);

    /// <summary>A battle on Canyon Attack: Anna Red and Ben Green; bots on Blue and Yellow.</summary>
    private static MatchInfo Battle()
    {
        var map = BattleCatalogue.Map("m_desert")!;
        var options = BattleBots.Compose(new BattleOptions { Tier = 3, Lives = 2, TeamA = map.TeamA, TeamB = map.TeamB },
            [BattleNetTests.Brought("Anna", 3) with { Race = Race.Red }, BattleNetTests.Brought("Ben", 2) with { Race = Race.Green }], map.Scenario);
        return new MatchInfo("m_desert", "m_desert", new MatchOptions { Battle = options, FogOfWar = false }, 5, 4,
        [
            new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false, PlayerId.Of(Race.Red)),
            new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true, PlayerId.Of(Race.Blue)),
            new MatchPlayer(2, "Ben", Race.Green, Race.Green, Ai: false, PlayerId.Of(Race.Green)),
            new MatchPlayer(0, "AI", Race.Yellow, Race.Yellow, Ai: true, PlayerId.Of(Race.Yellow)),
        ]);
    }

    private static LockstepSession Session(MatchInfo match, PlayerId? local, SessionMode mode = SessionMode.Bundles) =>
        new(Replay.BuildWorld(Data, match), local, match.InputDelay, mode: mode);

    private static Bundle Empty(long tick) => new(tick, []);

    /// <summary>Empty bundles from the session's next to <paramref name="to"/>.</summary>
    private static void EmptyTo(LockstepSession session, long to)
    {
        for (var tick = session.NewestBundle + 1; tick <= to; tick++)
            session.Receive(Empty(tick));
    }

    private static void StepTo(LockstepSession session, long tick)
    {
        while (session.World.Tick < tick)
            Assert.True(session.TryStep(), $"stuck at tick {session.World.Tick}");
    }

    private static Command Drive(Race race, int machine, float forward = 1) => new DriveCommand(race, machine, true, Forward: forward);

    [Fact]
    public void A_gap_in_the_relay_s_numbering_stops_the_session()
    {
        var session = Session(Skirmish(), Anna);
        var gaps = new List<(long, long)>();
        session.Gap += (expected, got) => gaps.Add((expected, got));

        session.Receive(Empty(0));
        session.Receive(Empty(2));
        session.Receive(Empty(1));

        Assert.Equal([(1L, 2L)], gaps);
        Assert.Equal(0, session.NewestBundle);
        Assert.True(session.TryStep());
        Assert.False(session.CanStep);
        Assert.False(session.TryStep());

        // A piece of the log after a plain bundle breaks it too, and so does one out of its place.
        var late = Session(Skirmish(), Anna);
        late.Gap += (expected, got) => gaps.Add((expected, got));
        late.Receive(Empty(0));
        late.Receive(BundleSegments.Seal(1, 99, [], 64)[0]);
        var early = Session(Skirmish(), Anna);
        early.Gap += (expected, got) => gaps.Add((expected, got));
        early.Receive(BundleSegments.Seal(5, 99, [], 64)[0]);
        Assert.Equal([(1L, 2L), (1L, 1L), (0L, 5L)], gaps);
    }

    [Fact]
    public void Bundle_mode_takes_only_bundles_and_turn_mode_none()
    {
        var bundles = Session(Skirmish(), Anna);
        var turns = Session(Skirmish(), Anna, SessionMode.Turns);
        foreach (var session in new[] { bundles, turns })
        {
            session.Receive(new Turn(0, Ben, []));
            session.Receive(new PlayerLeft(Ben, 0));
            session.Receive(Empty(0));
        }

        Assert.Equal((SessionMode.Bundles, 0L), (bundles.Mode, bundles.NewestBundle));
        Assert.Empty(bundles.Presence);
        Assert.Equal((SessionMode.Turns, -1L), (turns.Mode, turns.NewestBundle));
        Assert.Equal([new PlayerLeft(Ben, 0)], turns.Presence);
    }

    [Fact]
    public void Ready_counts_the_bundles_in_hand_and_a_turn_missing_from_one_is_nothing()
    {
        var session = Session(Skirmish(), Anna);
        Assert.Equal(0, session.Ready(10));
        Assert.False(session.CanStep);

        EmptyTo(session, 4);

        Assert.Equal((5, 3, 0), (session.Ready(10), session.Ready(3), session.Ready(-1)));
        Assert.Empty(session.Waiting());
        StepTo(session, 5);
        Assert.Equal(0, session.Ready(10));
        Assert.False(session.TryStep());
        Assert.Empty(session.Played);
    }

    [Fact]
    public void Pump_sends_one_rising_turn_a_call_with_what_its_world_saw_and_the_newest_drive_of_a_machine()
    {
        var session = Session(Skirmish(), Anna);
        var sent = new List<Turn>();
        session.Outgoing += message => sent.AddRange(message is Turn turn ? [turn] : []);
        var (one, two) = (session.World.Machines.Where(m => m.Race == Race.Red).ElementAt(0).Id, session.World.Machines.Where(m => m.Race == Race.Red).ElementAt(1).Id);

        session.Pump();
        session.Pump();
        EmptyTo(session, 2);
        session.Pump();
        Assert.Equal([(4L, -1L, 0)], sent.Select(t => (t.Tick, t.Seen, t.Held)));

        session.TryStep();
        session.Pump();
        session.Queue(Drive(Race.Red, one, 1));
        session.Queue(new StopCommand(Race.Red, [one]));
        session.Queue(Drive(Race.Red, two, 1));
        session.Queue(Drive(Race.Red, one, -1));
        session.TryStep();
        session.Pump();

        Assert.Equal([(4L, -1L, 0), (5L, 0L, 2), (6L, 1L, 1)], sent.Select(t => (t.Tick, t.Seen, t.Held)));
        Assert.Equal([new StopCommand(Race.Red, [one]), Drive(Race.Red, two, 1), Drive(Race.Red, one, -1)], sent[^1].Commands, CommandText.Instance);

        // The delay comes with the bundles: up, the stamps jump; down, none is sent until the world is past the last.
        session.Receive(new Bundle(3, [], Delay: 6));
        session.TryStep();
        session.Pump();
        Assert.Equal((9L, 2L, 1), (sent[^1].Tick, sent[^1].Seen, sent[^1].Held));
        session.Receive(new Bundle(4, [], Delay: 3));
        EmptyTo(session, 9);
        Assert.Equal(3, session.InputDelay);
        for (var tick = 4; tick <= 7; tick++)
        {
            session.TryStep();
            session.Pump();
        }
        Assert.Equal([4L, 5, 6, 9, 10], sent.Select(t => t.Tick));
        Assert.Equal((10L, 6L, 3), (sent[^1].Tick, sent[^1].Seen, sent[^1].Held));

        // Turn mode keeps every drive, as before.
        var turns = Session(Skirmish(), Anna, SessionMode.Turns);
        var kept = new List<Turn>();
        turns.Outgoing += message => kept.AddRange(message is Turn turn ? [turn] : []);
        turns.Queue(Drive(Race.Red, one, 1));
        turns.Queue(Drive(Race.Red, one, -1));
        turns.Pump();
        Assert.Equal(2, kept.SelectMany(t => t.Commands).Count());
    }

    [Fact]
    public void Landed_says_where_each_of_the_local_turns_went_on_time_late_or_split_and_not_while_away()
    {
        var session = Session(Skirmish(), Anna);
        var landed = new List<(long Stamp, long Tick)>();
        session.Landed += (stamp, tick) => landed.Add((stamp, tick));
        var red = session.World.Machines.First(m => m.Race == Race.Red).Id;
        Command A = Drive(Race.Red, red, 1), B = Drive(Race.Red, red, -1), C = new StopCommand(Race.Red, [red]);
        var stamps = new List<long>();
        session.Outgoing += message => stamps.AddRange(message is Turn { Commands.Count: > 0 } turn ? [turn.Tick] : []);
        EmptyTo(session, 3);

        // Stamps 4, 5 and 6 with commands, 7 without.
        foreach (var command in new Command?[] { A, B, C, null })
        {
            if (command is not null)
                session.Queue(command);
            session.Pump();
            session.TryStep();
        }
        Assert.Equal([4L, 5, 6], stamps);

        // 4 on time; 5 and 6 late, both on 7.
        session.Receive(new Bundle(4, [new Turn(4, Anna, [A], Asked: 4)]));
        Assert.Equal([(4L, 4L)], landed);
        session.Receive(Empty(5));
        session.Receive(Empty(6));
        Assert.Single(landed);
        session.Receive(new Bundle(7, [new Turn(7, Anna, [B, C], Asked: 6)]));
        Assert.Equal([(4L, 4L), (5L, 7L), (6L, 7L)], landed);

        // Two late turns split over two bundles: each lands where the relay merged it.
        StepTo(session, 6);
        session.Queue(A);
        session.Pump();
        session.TryStep();
        session.Queue(B);
        session.Pump();
        Assert.Equal([4L, 5, 6, 10, 11], stamps);
        EmptyTo(session, 10);
        session.Receive(new Bundle(11, [new Turn(11, Anna, [A], Asked: 10)]));
        session.Receive(new Bundle(12, [new Turn(12, Anna, [B], Asked: 11)]));
        Assert.Equal([(4L, 4L), (5L, 7L), (6L, 7L), (10L, 11L), (11L, 12L)], landed);

        // Away, the relay drops the player's commands: their stamps never land.
        StepTo(session, 12);
        session.Queue(C);
        session.Pump();
        session.Receive(new Bundle(13, [], [new PlayerLeft(Anna, 13)]));
        EmptyTo(session, 16);
        session.Receive(new Bundle(17, [], [new PlayerReturned(Anna, 17)]));
        StepTo(session, 17);
        session.Queue(A);
        session.Pump();
        EmptyTo(session, 20);
        session.Receive(new Bundle(21, [new Turn(21, Anna, [A], Asked: 21)]));
        Assert.Equal([4L, 5, 6, 10, 11, 16, 21], stamps);
        Assert.Equal([(4L, 4L), (5L, 7L), (6L, 7L), (10L, 11L), (11L, 12L), (21L, 21L)], landed);
    }

    [Fact]
    public void A_lag_mark_lets_go_of_the_player_s_wheels_at_its_tick_and_Lagging_follows_the_marks_until_a_leave()
    {
        var session = Session(Skirmish(), Anna);
        var blue = session.World.Machines.First(m => m.Race == Race.Blue);
        session.Receive(Empty(0));
        session.Receive(new Bundle(1, [new Turn(1, Ben, [Drive(Race.Blue, blue.Id)], Asked: 1)]));
        session.Receive(new Bundle(2, [], [new PlayerLagging(Ben, 2, true)]));
        StepTo(session, 2);
        Assert.Equal((true, Ben), (blue.DrivenByPlayer, blue.Driver));
        Assert.False(session.Lagging(Ben));

        StepTo(session, 3);
        Assert.False(blue.DrivenByPlayer);
        Assert.True(session.Lagging(Ben));
        Assert.False(session.Lagging(Anna));

        // A slow client drives on with its late turns.
        session.Receive(new Bundle(3, [new Turn(3, Ben, [Drive(Race.Blue, blue.Id)], Asked: 1)]));
        session.Receive(new Bundle(4, [], [new PlayerLagging(Ben, 4, false)]));
        StepTo(session, 4);
        Assert.True(blue.DrivenByPlayer && session.Lagging(Ben));
        StepTo(session, 5);
        Assert.False(session.Lagging(Ben));
        Assert.True(blue.DrivenByPlayer);

        // Marked, then gone: no longer lagging, and not once back.
        session.Receive(new Bundle(5, [], [new PlayerLagging(Ben, 5, true)]));
        EmptyTo(session, 6);
        session.Receive(new Bundle(7, [], [new PlayerLeft(Ben, 7)]));
        EmptyTo(session, 8);
        session.Receive(new Bundle(9, [], [new PlayerReturned(Ben, 9)]));
        StepTo(session, 7);
        Assert.True(session.Lagging(Ben));
        StepTo(session, 8);
        Assert.False(session.Lagging(Ben));
        StepTo(session, 10);
        Assert.False(session.Lagging(Ben));
        Assert.Equal(5, session.Presence.Count);
    }

    [Fact]
    public void A_tick_s_comings_and_goings_are_announced_in_player_order_once_the_world_has_them()
    {
        var session = Session(Together(), Anna);
        var heard = new List<(NetMessage Change, long Tick, bool CleoAway)>();
        session.PresenceApplied += change => heard.Add((change, session.World.Tick, session.World.Players.Away(Cleo)));
        EmptyTo(session, 2);
        session.Receive(new Bundle(3, [], [new PlayerLeft(Cleo, 3), new PlayerLagging(Ben, 3, true)]));
        StepTo(session, 3);
        Assert.Empty(heard);

        StepTo(session, 4);

        Assert.Equal([(new PlayerLagging(Ben, 3, true), 3L, true), (new PlayerLeft(Cleo, 3), 3L, true)], heard);
        Assert.Equal([new PlayerLeft(Cleo, 3), new PlayerLagging(Ben, 3, true)], session.Presence);
    }

    /// <summary>
    /// 300 ticks of a skirmish as the relay logs them: Ben's drives every ninth tick, his lag mark at 40, his leave at 150
    /// and his return at 220.
    /// </summary>
    private static List<Bundle> SkirmishLog(GameWorld world)
    {
        var blue = world.Machines.Where(m => m.Race == Race.Blue).Take(3).Select(m => m.Id).ToList();
        return
        [
            .. Enumerable.Range(0, 300).Select(t => new Bundle(t,
                t % 9 == 4 && t is < 150 or >= 220 ? [new Turn(t, Ben, [Drive(Race.Blue, blue[t % 3], t % 2 == 0 ? 1 : -1)], Asked: t - 2)] : [],
                t switch
                {
                    40 => [new PlayerLagging(Ben, 40, true)],
                    60 => [new PlayerLagging(Ben, 60, false)],
                    150 => [new PlayerLeft(Ben, 150)],
                    220 => [new PlayerReturned(Ben, 220)],
                    _ => null,
                }, 4)),
        ];
    }

    [Fact]
    public void The_relay_s_log_is_taken_in_sealed_segments_decoded_only_when_the_world_reaches_them_then_the_tail()
    {
        var match = Skirmish();
        var live = Session(match, Anna);
        var rebuilt = Session(match, Anna);
        var log = SkirmishLog(live.World);
        foreach (var (from, to) in new[] { (0L, 99L), (100L, 199L) })
        {
            foreach (var part in BundleSegments.Seal(from, to, log, 200))
                rebuilt.Receive(part);
        }
        foreach (var bundle in log.Skip(200))
            rebuilt.Receive(bundle);

        Assert.Equal(299, rebuilt.NewestBundle);
        Assert.Equal(300, rebuilt.Ready(1000));
        Assert.Empty(rebuilt.Presence);
        for (var tick = 0; tick < 300; tick++)
        {
            live.Receive(log[tick]);
            Assert.True(live.TryStep());
            Assert.True(rebuilt.TryStep());
            Assert.Equal(live.World.Checksum(), rebuilt.World.Checksum());
            // The first segment's comings and goings, then the second's and the tail's once the world reaches the second.
            Assert.Equal(tick < 100 ? 2 : 4, rebuilt.Presence.Count);
        }
        Assert.Equal(live.Presence, rebuilt.Presence);
        Assert.Equal(live.Played.Count, rebuilt.Played.Count);
        Assert.Contains(live.World.Machines, m => m.Race == Race.Blue && m.DrivenByPlayer);
    }

    /// <summary>
    /// 400 ticks of a battle as the relay logs them: Anna drives every tenth tick, Cleo takes Blue's bot seat at 250 and
    /// drives from there every seventh, Anna lags from 300 to 330, Ben leaves at 350.
    /// </summary>
    private static List<Bundle> BattleLog(GameWorld world)
    {
        var (anna, blue) = (world.HeroOf(PlayerId.Of(Race.Red))!.Id, world.HeroOf(PlayerId.Of(Race.Blue))!.Id);
        var (red, cleo, ben) = (PlayerId.Of(Race.Red), PlayerId.Of(Race.Blue), PlayerId.Of(Race.Green));
        return
        [
            .. Enumerable.Range(0, 400).Select(t =>
            {
                var turns = new List<Turn>();
                if (t % 10 == 3)
                    turns.Add(new Turn(t, red, [new DriveCommand(Race.Red, anna, true, Forward: 1, Turn: t % 20 == 3 ? 1 : 0, Buttons: t % 30 == 3)], Asked: t));
                if (t > 250 && t % 7 == 0)
                    turns.Add(new Turn(t, cleo, [new DriveCommand(Race.Blue, blue, true, Forward: 1, Turn: -1)], Asked: t));
                NetMessage[]? presence = t switch
                {
                    250 => [new PlayerJoined(cleo, 250, "Cleo")],
                    300 => [new PlayerLagging(red, 300, true)],
                    330 => [new PlayerLagging(red, 330, false)],
                    350 => [new PlayerLeft(ben, 350)],
                    _ => null,
                };
                return new Bundle(t, turns, presence, 4);
            }),
        ];
    }

    [Fact]
    public void A_join_known_before_tick_0_plays_as_one_learnt_live_and_no_leave_ever_reaches_the_bot_s_row()
    {
        var match = Battle();
        var blue = PlayerId.Of(Race.Blue);
        var live = Session(match, null);
        var log = BattleLog(live.World);
        // A rebuild whose log holds the join in a sealed segment, one whose join lies in the tail (no segment at all here).
        var fromSegment = Session(match, null);
        foreach (var (from, to) in new[] { (0L, 99L), (100L, 199L), (200L, 299L) })
        {
            foreach (var part in BundleSegments.Seal(from, to, log, 512))
                fromSegment.Receive(part);
        }
        foreach (var bundle in log.Skip(300))
            fromSegment.Receive(bundle);
        var fromTail = Session(match, null);
        foreach (var bundle in log)
            fromTail.Receive(bundle);
        Assert.Contains(blue, fromTail.Humans);

        var sessions = new List<LockstepSession> { live, fromSegment, fromTail };
        for (var tick = 0; tick < 400; tick++)
        {
            live.Receive(log[tick]);
            foreach (var session in sessions)
            {
                Assert.True(session.TryStep());
                Assert.False(session.World.Players.Away(blue), $"the bot's row was marked away at tick {tick}");
                Assert.Equal(tick < 250 ? PlayerKind.Bot : PlayerKind.Human, session.World.Players[blue].Kind);
            }
            Assert.Equal(live.World.Checksum(), fromSegment.World.Checksum());
            Assert.Equal(live.World.Checksum(), fromTail.World.Checksum());
        }
        Assert.Equal([blue], live.World.Players.Joined);
        Assert.Equal("Cleo", live.World.Players[blue].Name);
        Assert.DoesNotContain(blue, live.World.HeroBots.Keys);
        Assert.Contains(live.Played, turn => turn.Player == blue);

        // Its recording, saved and loaded, plays back in turn mode, the join and the marks known before tick 0.
        var path = Path.Combine(Path.GetTempPath(), $"late-join-{Guid.NewGuid():N}{Replay.Extension}");
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
        Assert.Equal(live.Presence, loaded.Presence!);
        var playback = loaded.Playback(Replay.BuildWorld(Data, loaded.Match));
        Assert.Equal(SessionMode.Turns, playback.Mode);
        var again = Session(match, null);
        for (var tick = 0; tick < 400; tick++)
        {
            again.Receive(log[tick]);
            Assert.True(again.TryStep());
            Assert.True(playback.TryStep(), $"the playback stuck at tick {tick}");
            Assert.False(playback.World.Players.Away(blue));
            Assert.Equal(again.World.Checksum(), playback.World.Checksum());
        }
        Assert.Equal(live.World.Checksum(), playback.World.Checksum());
    }

    [Fact]
    public void A_join_of_a_seat_that_is_no_bot_s_or_joined_already_is_passed_over()
    {
        var session = Session(Battle(), null);
        var (red, blue) = (PlayerId.Of(Race.Red), PlayerId.Of(Race.Blue));
        session.Receive(new Bundle(0, [], [new PlayerJoined(red, 0, "Cleo"), new PlayerJoined(blue, 0, "Dan"), new PlayerJoined(new PlayerId(20), 0, "Eve")]));
        session.Receive(new Bundle(1, [], [new PlayerJoined(blue, 1, "Fay")]));

        StepTo(session, 2);

        Assert.Equal([new PlayerJoined(blue, 0, "Dan")], session.Presence);
        Assert.Equal(("Dan", PlayerKind.Human), (session.World.Players[blue].Name, session.World.Players[blue].Kind));
        Assert.Equal("Anna", session.World.Players[red].Name);
    }

    [Fact]
    public void A_turn_leaves_out_what_only_bundle_mode_fills_in_and_the_new_messages_come_through_the_wire()
    {
        var plain = new Turn(5, Anna, []);
        var paced = new Turn(9, Anna, [], Seen: 4, Held: 2);
        var landed = new Turn(9, Anna, [], Asked: 7);

        var json = Encoding.UTF8.GetString(NetJson.Encode(plain));
        Assert.DoesNotContain("Seen", json);
        Assert.DoesNotContain("Held", json);
        Assert.DoesNotContain("Asked", json);
        Assert.Contains("\"Seen\":4", Encoding.UTF8.GetString(NetJson.Encode(paced)));
        Assert.DoesNotContain("Asked", Encoding.UTF8.GetString(NetJson.Encode(paced)));
        Assert.DoesNotContain("Seen", Encoding.UTF8.GetString(NetJson.Encode(landed)));
        foreach (var turn in new[] { plain, paced, landed })
        {
            var back = (Turn)NetJson.Decode(NetJson.Encode(turn));
            Assert.Equal((turn.Tick, turn.Player, turn.Seen, turn.Held, turn.Asked), (back.Tick, back.Player, back.Seen, back.Held, back.Asked));
        }
        // A turn as protocol 13 wrote it.
        var old = (Turn)NetJson.Decode("""{"$type":"turn","Tick":3,"Player":1,"Commands":[]}"""u8);
        Assert.Equal((3L, Ben, -1L, 0, -1L), (old.Tick, old.Player, old.Seen, old.Held, old.Asked));

        NetMessage[] messages =
        [
            new PlayerJoined(Ben, 40, "Ben"), new PlayerLagging(Ben, 41, true), new MatchLoaded(), new MatchDecided(42), new MatchEnded(43),
            new SeatKey(2, Ben, "k3y"), new LeftMatch("Your computer cannot keep up with this match."), new JoinRoom(2, 3, 99),
            new RejoinMatch(2, Ben, "k3y"),
        ];
        foreach (var message in messages)
            Assert.Equal(message, NetJson.Decode(NetJson.Encode(message)));
        var bundle = (Bundle)NetJson.Decode(NetJson.Encode(new Bundle(7, [new Turn(7, Ben, [Drive(Race.Blue, 3)], Asked: 6)], [new PlayerLeft(Anna, 7)], 5)));
        Assert.Equal((7L, 5, 6L), (bundle.Tick, bundle.Delay, bundle.Turns.Single().Asked));
        Assert.Equal([new PlayerLeft(Anna, 7)], bundle.Presence!);
        Assert.True(((Bundle)NetJson.Decode(NetJson.Encode(new Bundle(8, [])))).Empty);
        // A bundle's turns are turns by their declared type, without a name of their own; its comings and goings are named.
        var text = Encoding.UTF8.GetString(NetJson.Encode(new Bundle(7, [new Turn(7, Ben, [], Asked: 6)], [new PlayerLeft(Anna, 7)], 5)));
        Assert.DoesNotContain("\"turn\"", text);
        Assert.Contains("\"playerLeft\"", text);
        Assert.InRange(NetJson.Encode(new Bundle(8, [])).Length, 40, 80);
        var resumed = (MatchResumed)NetJson.Decode(NetJson.Encode(new MatchResumed(Skirmish(), Ben, 600, ResumeReason.LateJoin, Parts: 3, Key: "k3y", Resyncs: 1)));
        Assert.Equal((ResumeReason.LateJoin, 3, "k3y", 1), (resumed.Reason, resumed.Parts, resumed.Key, resumed.Resyncs));

        // Why a player left goes along only where it is a lag or a rebuild (S4): a plain leave, and so every recording's, reads as before.
        Assert.DoesNotContain("Why", text);
        foreach (var why in Enum.GetValues<LeaveReason>())
            Assert.Equal(new PlayerLeft(Anna, 9, why), NetJson.Decode(NetJson.Encode(new PlayerLeft(Anna, 9, why))));
        Assert.Contains("\"Why\":\"Rebuilding\"", Encoding.UTF8.GetString(NetJson.Encode(new PlayerLeft(Anna, 9, LeaveReason.Rebuilding))));
        Assert.Equal(new PlayerLeft(Anna, 9), NetJson.Decode("""{"$type":"playerLeft","Player":0,"FromTick":9}"""u8));
        // The relay's reasons for taking a player out are the texts the game tells apart and translates.
        Assert.Equal(["Your computer cannot keep up with this match.", "Your connection cannot keep up with this match.",
                "Your game kept going out of step with the others, so it has left the match.", "you came back to this match from another connection"],
            [LeftMatch.CannotKeepUp, LeftMatch.SlowLine, LeftMatch.OutOfStep, LeftMatch.TakenOver]);
    }

    /// <summary>Commands compared by their JSON: some hold lists, which records compare by reference.</summary>
    private sealed class CommandText : IEqualityComparer<Command>
    {
        public static CommandText Instance { get; } = new();

        private static string Text(Command command) => Encoding.UTF8.GetString(NetJson.Encode(new Turn(0, default, [command])));

        public bool Equals(Command? x, Command? y) => x is not null && y is not null && Text(x) == Text(y);

        public int GetHashCode(Command obj) => Text(obj).GetHashCode(StringComparison.Ordinal);
    }
}
