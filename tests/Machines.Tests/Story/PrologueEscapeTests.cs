using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using static Machines.Tests.Story.EscapeWorlds;

namespace Machines.Tests.Story;

/// <summary>
/// The prologue's escape run as a whole (package E, design-escape §13): its checkpoints, the Galerie (13) and the Bucht (14),
/// build the world a run that reached them has; two peers play it to the same checksum, a shutdown and a restart included; and
/// a Locator walked along the route with its Grunts following on their own reaches the pod.
/// </summary>
public class PrologueEscapeTests
{
    /// <summary>What the escape's state at a checkpoint is made of, as the tests compare it.</summary>
    private static string Shape(GameWorld world)
    {
        var run = world.Story!;
        var h = run.Hazards;
        var pack = string.Join(",", world.PackAlive(run).Select(m => run.Pack.Members[m.Id]).OrderBy(k => k));
        return $"cp {run.Checkpoint} section {run.Section} escape {run.Flags.Contains(StoryMarks.Escape)} rails {h.Rails.Count} jets {h.JetsFrom >= 0} "
            + $"shutter {h.Shutter} collapsed {h.Collapsed} bridge {h.BridgeFallen} X {world.DoorClosed('X')} Y {world.DoorClosed('Y')} "
            + $"Ylocked {run.Director.Locked.Contains('Y')} P {world.DoorClosed('P')} S {world.DoorClosed('S')} gone {run.Pack.GoneRound} pack {pack}";
    }

    [Fact]
    public void The_galerie_checkpoint_builds_the_world_a_run_that_reached_it_has()
    {
        var reached = Chapter(12);
        var run = reached.Story!;
        // Carried to the gallery's entry; the checkpoint is taken there.
        Carry(reached, 920, each: _ => { });
        SetDown(reached, Hero(reached, 0), new Vector2(340, 130));
        SetDown(reached, Hero(reached, 1), new Vector2(340, 124));
        SetDown(reached, Hero(reached, 2), new Vector2(340, 136));
        Assert.True(RunUntil(reached, () => run.Checkpoint == 13, 3));
        Assert.True(RunUntil(reached, () => reached.DoorClosed('X'), 3));

        var built = Chapter(13);
        static string World(string shape) => shape[..shape.IndexOf(" pack ", StringComparison.Ordinal)];
        Assert.Equal(World(Shape(reached)), World(Shape(built)));
        // Who of the pack is still up by the gallery is the run's fortune (a container, a jet or the Grunts' Bolters, armed from
        // checkpoint 9, may take a Reaper out on the way): the checkpoint casts no member a run to it could not still have.
        static Dictionary<PackKind, int> Kinds(IEnumerable<PackKind> kinds) => kinds.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var cast = Kinds(run.Pack.Members.Values);
        foreach (var (kind, count) in Kinds(built.PackAlive(built.Story!).Select(m => built.Story!.Pack.Members[m.Id])))
            Assert.True(count <= cast.GetValueOrDefault(kind), $"checkpoint 13 casts {count} {kind}, a run has {cast.GetValueOrDefault(kind)}");
        Assert.True(built.PackAlive(built.Story!).Count() >= 5, "the gallery's pack is thinner than the design's");
        // The countdown from the gallery leaves at least as much as the run had when it took the checkpoint (170 s on Normal).
        Assert.Equal(Prologue.CountdownFrom[13]!.Value, built.Story!.Timers[StoryMarks.Countdown] - built.Time, 1);
    }

    [Fact]
    public void The_bucht_checkpoint_builds_the_world_a_run_that_reached_it_has()
    {
        var reached = Chapter(13);
        var run = reached.Story!;
        // The gallery takes a run its 20 s or so: the Thresher has rammed X open behind them by then.
        Assert.True(RunUntil(reached, () => run.Hazards.Shutter == ShutterState.Broken, 15));
        // Over the bridge and into the bay; the bridge falls behind them, the pack goes round.
        foreach (var at in new[] { new Vector2(340, 96), new Vector2(400, 92), new Vector2(452, 92) })
        {
            for (var seat = 0; seat < 3; seat++)
                SetDown(reached, Hero(reached, seat), at + new Vector2(0, (seat - 1) * 3.5f));
            Run(reached, 1);
        }
        Assert.True(RunUntil(reached, () => run.Checkpoint == 14 && run.Pack.GoneRound, 6));

        var built = Chapter(14);
        Assert.Equal(Shape(reached), Shape(built));
    }

    [Fact]
    public void The_escape_checkpoints_build_the_same_world_twice()
    {
        foreach (var cp in new[] { 12, 13, 14 })
            Assert.Equal(Chapter(cp, seed: 5).Checksum(), Chapter(cp, seed: 5).Checksum());
    }

    [Fact]
    public void Two_peers_play_the_escape_to_the_same_checksum_with_a_shutdown_and_a_restart()
    {
        static List<ulong> Play()
        {
            var world = Chapter(12, seed: 11);
            var run = world.Story!;
            var sums = new List<ulong>();
            void Sum()
            {
                if (world.Tick % 20 == 0)
                    sums.Add(world.Checksum());
            }
            // Carried into the hold, the Locator is shot down to nothing there: he shuts down (the reserve), and Ike's bot
            // drives over and plugs him in again; then on.
            Carry(world, 160, each: _ => Sum());
            world.Damage(Hero(world, 0), 100000, Race.Blue);
            Assert.Contains(StoryMarks.L7Down, run.Flags);
            Assert.True(RunUntil(world, () => !run.Flags.Contains(StoryMarks.L7Down) || run.Over, 20, each: Sum));
            Assert.False(run.Over, $"ended {run.Ending}");
            Assert.Equal(1, run.Reserve.Restarts);
            Run(world, 10, each: Sum);
            return sums;
        }

        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void No_escape_checkpoint_starts_within_40_metres_of_a_hunter()
    {
        foreach (var cp in new[] { 12, 13, 14 })
        {
            var world = Chapter(cp);
            var heroes = Heroes(world).ToList();
            foreach (var hunter in world.Machines.Where(m => !m.IsDead && m.Race != heroes[0].Race))
            {
                var near = heroes.Min(h => Vector2.Distance(h.Position, hunter.Position));
                Assert.True(near >= 40, $"checkpoint {cp}: {hunter.Id} at {hunter.Position} is {near:0.#} m from the trio");
            }
        }
    }

    [Fact]
    public void The_pack_and_the_thresher_all_leave_the_freight_lift()
    {
        // Set down in a crowded lift, the big ones once stood where their size class had no room and never found a way off.
        var world = Chapter(12);
        var run = world.Story!;
        Assert.True(RunUntil(world, () => run.Director.Has("thresher_out"), 15));
        world.Step();
        // He bursts out of the lift itself, not set down in a room beside it, on ground with room for his size.
        var thresher = world.Find<Machine>(run.Pack.Members.First(kv => kv.Value == PackKind.Thresher).Key)!;
        Assert.True(world.Deck!.Zone("R15b") is { } lift && GameWorld.Overlaps(lift, thresher.Position, 0), $"the Thresher at {thresher.Position}");
        Assert.True(run.Pack.Members.Keys.All(id => world.Find<Machine>(id) is not { } m || world.Nav.IsFree(m.Position, GameWorld.MobilityOf(m))),
            "a big one set down without room for its size");
        bool Out() => run.Pack.Members.Keys.All(id => world.Find<Machine>(id) is not { IsDead: false } m || m.Position.Y < 256);
        Assert.True(RunUntil(world, Out, 75), string.Join(" | ", run.Pack.Members.Select(kv => world.Find<Machine>(kv.Key) is { } m ? $"{kv.Value} {m.Position}" : "")));
    }

    [Fact]
    public void At_the_galerie_the_thresher_rams_the_shutter_open_within_fifteen_seconds()
    {
        var world = Chapter(13);
        var events = new List<GameEvent>();
        Assert.True(RunUntil(world, () => world.Story!.Hazards.Shutter == ShutterState.Broken, 15, events),
            $"shutter {world.Story!.Hazards.Shutter}, rammed {world.Story.Hazards.ShutterRammed}");
        Assert.Contains(Cues(events), c => c.StartsWith("shutter ram", StringComparison.Ordinal));
    }

    /// <summary>The Locator's way through the escape as a player walks it (move orders), checkpoint 12 to the pod.</summary>
    private static readonly Vector2[] Way =
    [
        new(500, 228), new(520, 228), new(540, 224), new(540, 308), new(560, 308), new(560, 192), new(580, 192), new(580, 308), new(600, 308),
        new(600, 172), new(520, 172), new(520, 166), new(504, 163), new(470, 168), new(400, 168), new(372, 168),
        new(356, 144), new(340, 140), new(340, 110), new(340, 96), new(352, 92), new(400, 92), new(440, 92), new(470, 92),
    ];

    /// <summary>
    /// One run from checkpoint 12 with the Locator walked along <see cref="Way"/> by move orders and the Grunts on their own
    /// (they follow, hold the shutter's frame, wait out the jets); at the bay the pod is hacked and boarded. Whether it launched.
    /// </summary>
    /// <summary>
    /// Past the foot of the lane the Locator stands in once it is up at a crate wall: the waypoint south of it in its lane
    /// is behind it, and going back for it after the wall would run it into the pack.
    /// </summary>
    private static int PastLaneFoot(Machine locator, int next) =>
        next < Way.Length && Way[next].Y > locator.Position.Y + 4 && MathF.Abs(Way[next].X - locator.Position.X) < 8 ? next + 1 : next;

    private static bool Walk(int seed, Action<string>? log = null)
    {
        var world = Chapter(12, seed);
        var run = world.Story!;
        var next = 0;
        for (var tick = 0; tick < (Prologue.CountdownFull + 5) * GameWorld.TicksPerSecond && !run.Over && !run.Director.Has("launch"); tick++)
        {
            var locator = Hero(world, 0);
            if (tick % 100 == 0)
                log?.Invoke($"{tick / 20}s next {next} at {locator.Position} hp {locator.HitPoints} ike {Hero(world, 1).Position} bob {Hero(world, 2).Position} order {locator.Order} beats {string.Join(",", run.Director.Fired.OrderBy(kv => kv.Value).Select(kv => kv.Key).TakeLast(3))} flags {string.Join(",", run.Flags)}");
            if (run.Flags.Contains(StoryMarks.L7Down))
            {
                world.Step();
                continue;
            }
            // A Grunt down: the Locator goes to it and puts it together (Use held), as a player does.
            if (run.Seats.Skip(1).Where(s => world.CompanionDown(s)).Select(s => (PlayerId?)s).FirstOrDefault() is { } downed && world.HeroOf(downed) is { } grunt)
            {
                if (Vector2.Distance(locator.Position, grunt.Position) > 4)
                {
                    if (tick % 20 == 0)
                        world.Issue(new MoveCommand(Race.Red, [locator.Id], grunt.Position));
                }
                else if (!run.Crew.Holding.ContainsKey(run.Locator.Value))
                    world.Issue(new StoryUseCommand(Race.Red, $"seat:{downed.Value}"));
                world.Step();
                continue;
            }
            if (run.Crew.Holding.ContainsKey(run.Locator.Value))
                world.Issue(new StoryUseCommand(Race.Red, Held: false));
            // A crate wall ahead in the Kühlschlange: shot through, as a player does (play test 2026-11-25).
            if (WallAhead(world, locator.Position) is { } wall)
            {
                next = PastLaneFoot(locator, next);
                if (tick % 20 == 0)
                    world.Issue(new AttackCommand(Race.Red, [locator.Id], wall.Id));
                if (tick % 100 == 0)
                    log?.Invoke($"{tick / 20}s wall {wall.Tag} hp {wall.HitPoints} at {Vector2.Distance(wall.Position, locator.Position):0} m, order {locator.Order}");
                world.Step();
                continue;
            }
            // Door P jammed by the breach: its panel hacked.
            if (run.Director.Has("breach") && !run.Hacks.Done.Contains("hack_p") && !run.Hacks.Locked.Contains("hack_p"))
            {
                log?.Invoke($"{tick / 20}s door P hacked {HackBySolution(world, locator, "hack_p")}");
                continue;
            }
            if (next < Way.Length && Vector2.Distance(locator.Position, Way[next]) < 4)
                next++;
            if (next < Way.Length)
            {
                if (tick % 20 == 0)
                    world.Issue(new MoveCommand(Race.Red, [locator.Id], Way[next]));
                world.Step();
                continue;
            }
            // The bay: the pod's hack played by its solution, the hatch opened, everyone in.
            if (!run.Hacks.Done.Contains("pod_3_hatch"))
            {
                SetDown(world, locator, world.Deck!.Marker("pod_3_hatch").Position + new Vector2(0, 1.5f));
                world.Issue(new StoryHackOpenCommand(Race.Red, "pod_3_hatch"));
                world.Step();
                var session = run.Hacks.Session("pod_3_hatch");
                for (var turns = 0; session is not null && turns < 400 && !run.Hacks.Done.Contains("pod_3_hatch") && !run.Over; turns++)
                {
                    if (!session.Open)
                        world.Issue(new StoryHackOpenCommand(Race.Red, "pod_3_hatch"));
                    else if (session.Puzzle!.SolutionTurns().Take(1).ToList() is [var turn])
                        world.Issue(new StoryHackTurnCommand(Race.Red, "pod_3_hatch", turn.Cell, turn.Clockwise));
                    world.Step();
                    tick++;
                }
                continue;
            }
            if (!run.Flags.Contains(StoryMarks.Done("pod_3_hatch")))
            {
                SetDown(world, locator, world.Deck!.Marker("pod_3_hatch").Position + new Vector2(0, 1.5f));
                world.Issue(new StoryUseCommand(Race.Red, "pod_3_hatch"));
                world.Step();
                continue;
            }
            var pod = world.Deck!.Zone(GameWorld.PodZone)!;
            if (tick % 20 == 0)
                world.Issue(new MoveCommand(Race.Red, [locator.Id], (pod.Min + pod.Max) / 2));
            world.Step();
        }
        log?.Invoke($"end {run.Ending} over {run.Over} tick {world.Tick}");
        return run.Director.Has("launch");
    }

    // Eight, not nine, since the Kühlschlange: a walker that never sprints is nearly three times as long under the pack's guns, and a
    // Grunt brought down in a lane (seeds 5 and 6) can cost it the run. Six since the crate walls (play test 2026-11-25: the escape
    // was "just running", the user wanted the pack dangerously near): each costs a Bolter alone some ten seconds, the pack closes
    // in on a walker at the second (seeds 1 and 6), and one shooting the wall while the pack shoots it can be worn down
    // (seeds 3 and 5). A player sprints between them, as the sprinting driver does.
    [Fact]
    public void A_locator_walked_along_the_route_with_its_grunts_on_their_own_reaches_the_pod_in_six_seeds_of_ten()
    {
        var results = Enumerable.Range(1, 10).Select(seed => { var log = new List<string>(); var ok = Walk(seed, log.Add); return (seed, ok, last: string.Join(" | ", log.TakeLast(3))); }).ToList();
        var launched = results.Count(r => r.ok);
        Assert.True(launched >= 6, string.Join(" || ", results.Where(r => !r.ok).Select(r => $"seed {r.seed}: {r.last}")));
    }

    // ---- a Locator sprinting ahead --------------------------------------------------------------------

    /// <summary>Whether a hero stands on the hold's side of the shutter X, its doorway (x 348-352) counted as through.</summary>
    private static bool ShutOut(Machine hero) => !(hero.Position.X < 352 && hero.Position.Y < 160 || hero.Position.Y < 100);

    /// <summary>The shutter's panel played by its solution from the gallery, the Locator set down at it; whether it was done.</summary>
    private static bool HackShutter(GameWorld world, Machine locator)
    {
        var run = world.Story!;
        SetDown(world, locator, run.Hacks.Find("hack_x", world.Deck)!.Value.Position);
        world.Issue(new StoryHackOpenCommand(Race.Red, "hack_x"));
        world.Step();
        var session = run.Hacks.Session("hack_x");
        for (var turns = 0; session is not null && turns < 400 && !run.Hacks.Done.Contains("hack_x") && !run.Over; turns++)
        {
            if (!session.Open)
                world.Issue(new StoryHackOpenCommand(Race.Red, "hack_x"));
            else if (session.Puzzle!.SolutionTurns().Take(1).ToList() is [var turn])
                world.Issue(new StoryHackTurnCommand(Race.Red, "hack_x", turn.Cell, turn.Clockwise));
            world.Step();
        }
        return run.Hacks.Done.Contains("hack_x");
    }

    /// <summary>
    /// One run from checkpoint 12 with the Locator driven along <see cref="Way"/> at the keys with the sprint held (as the
    /// game's story driver plays it), waiting only before a lane while its container telegraphs or slides, and hacking the
    /// shutter's panel when it came down on a Grunt; the Grunts on their own. Where the Grunts are when it reaches the
    /// footbridge's far end: true when both are past the shutter and not wrecked.
    /// </summary>
    private static bool Sprint(int seed, Action<string> log)
    {
        var world = Chapter(12, seed);
        var run = world.Story!;
        var next = 0;
        var lanes = world.Deck!.Zones.Where(z => z.Name.StartsWith("lane_", StringComparison.Ordinal)).OrderByDescending(z => z.Min.X).ToList();
        for (var tick = 0; tick < 150 * GameWorld.TicksPerSecond && !run.Over && next < Way.Length; tick++)
        {
            var locator = Hero(world, 0);
            if (run.Hazards.Shutter == ShutterState.Shut && !run.Hacks.Done.Contains("hack_x") && Enumerable.Range(1, 2).Any(s => ShutOut(Hero(world, s))))
            {
                log($"seed {seed}: X came down on a Grunt at {world.Time:0} s, hacked {HackShutter(world, locator)}");
                continue;
            }
            // A crate wall ahead: driven up to below it and shot through, the trigger held on it (play test 2026-11-25).
            var wall = WallAhead(world, locator.Position);
            if (wall is not null)
                next = PastLaneFoot(locator, next);
            if (run.Director.Has("breach") && !run.Hacks.Done.Contains("hack_p") && !run.Hacks.Locked.Contains("hack_p"))
            {
                log($"seed {seed}: door P hacked {HackBySolution(world, locator, "hack_p")} at {world.Time:0} s");
                continue;
            }
            if (wall is null && Vector2.Distance(locator.Position, Way[next]) < 4)
            {
                next++;
                continue;
            }
            var target = wall is null ? Way[next] : wall.Position + new Vector2(0, 9);
            if (wall is null && locator.Position.Y is > 150 and < 180)
            {
                foreach (var lane in lanes.Where(l => locator.Position.X > l.Min.X - 1).Take(1))
                {
                    var number = int.Parse(lane.Name["lane_".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    if (locator.Position.X > lane.Max.X + 1.5f && run.Hazards.Rails.Any(r => r.Lane == number && r.State is RailState.Telegraph or RailState.Slide))
                        target = new Vector2(lane.Max.X + 3.5f, 168);
                }
            }
            var way = target - locator.Position;
            if (way.Length() > 1.5f)
            {
                var off = MathF.Atan2(way.Y, way.X) - locator.Facing;
                off = MathF.Atan2(MathF.Sin(off), MathF.Cos(off));
                world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Forward: MathF.Abs(off) < 0.6f ? 1 : 0.2f, Turn: Math.Clamp(off * 2, -1, 1),
                    Aim: wall?.Position, Sprint: wall is null));
            }
            else if (wall is not null)
            {
                var face = wall.Position - locator.Position;
                var off = MathF.Atan2(face.Y, face.X) - locator.Facing;
                off = MathF.Atan2(MathF.Sin(off), MathF.Cos(off));
                world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Turn: Math.Clamp(off * 2, -1, 1), Aim: wall.Position));
            }
            else
                world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true));
            world.Step();
        }
        var grunts = Enumerable.Range(1, 2).Select(s => Hero(world, s)).ToList();
        log($"seed {seed}: at {world.Time:0} s, way {next}/{Way.Length}, " + string.Join(", ", grunts.Select(g => $"grunt at {g.Position.X:0},{g.Position.Y:0} hp {g.HitPoints:0}{(g.IsDead ? " wrecked" : "")}")));
        return next == Way.Length && grunts.All(g => !g.IsDead && !ShutOut(g));
    }

    /// <summary>
    /// A Locator sprinting ahead through the hold no longer leaves its Grunts to the pack: they keep their places at a lane
    /// rather than turning each other aside (GameWorld.KeepsPlaceInEscape), the one sent to hold the shutter runs there, and
    /// one shut out waits at the shutter until the Locator hacks its panel. Before, they fell 30 s behind and died in the
    /// hold, or went back through it looking for another way round.
    /// </summary>
    [Fact]
    public void A_locator_sprinting_ahead_finds_both_grunts_past_the_shutter_in_six_seeds_of_eight()
    {
        var log = new List<string>();
        var through = Enumerable.Range(1, 8).Count(seed => Sprint(seed, log.Add));
        Assert.True(through >= 6, $"{through} of 8: " + string.Join(" || ", log));
    }

    /// <summary>
    /// The shutter's panel: locked until the shutter comes down with a Grunt still in the hold, then hackable from the gallery;
    /// the Grunt shut out waits at the shutter meanwhile, and the hack raises it again (design-escape §3 C3).
    /// </summary>
    [Fact]
    public void A_grunt_shut_out_by_the_shutter_waits_there_until_the_locator_hacks_its_panel()
    {
        var world = Chapter(12);
        var run = world.Story!;
        Assert.Contains("hack_x", run.Hacks.Locked);
        var locator = Hero(world, 0);
        Run(world, 1);
        SetDown(world, locator, new Vector2(340, 120));
        // The Grunts held in the hold while it comes down, then let go.
        foreach (var seat in new[] { 1, 2 })
        {
            SetDown(world, Hero(world, seat), new Vector2(400 + 6 * seat, 168));
            run.Director.Held.Add(Hero(world, seat).Id);
        }
        world.CloseShutter(run);
        Assert.True(RunUntil(world, () => run.Hazards.Shutter == ShutterState.Shut, 15), $"shutter {run.Hazards.Shutter}");
        foreach (var seat in new[] { 1, 2 })
            run.Director.Held.Remove(Hero(world, seat).Id);
        Assert.DoesNotContain("hack_x", run.Hacks.Locked);
        Assert.NotNull(run.Hacks.Find("hack_x", world.Deck));
        Run(world, 20);
        foreach (var seat in new[] { 1, 2 })
        {
            var grunt = Hero(world, seat);
            Assert.True(Vector2.Distance(grunt.Position, new Vector2(357, 144)) < 8 || grunt.IsDead || world.CompanionDown(run.Seats[seat]),
                $"grunt {seat} at {grunt.Position} order {grunt.Order} task {run.Crew.Of(run.Seats[seat]).Task} escape {run.Flags.Contains(StoryMarks.Escape)} shutter {run.Hazards.Shutter} held {run.Director.Held.Contains(grunt.Id)} path {grunt.PathIndex}/{grunt.Path.Count}");
        }
        Assert.True(HackShutter(world, locator));
        Assert.True(RunUntil(world, () => run.Hazards.Shutter != ShutterState.Shut, 2), $"shutter {run.Hazards.Shutter}");
    }

    /// <summary>
    /// The computer's Grunts may cheat in the escape (the user's word): one far behind the Locator and out of its sight
    /// for six seconds is set down behind it, and a hit on it does half the harm.
    /// </summary>
    [Fact]
    public void A_computers_grunt_lost_far_behind_unseen_is_set_down_behind_the_locator()
    {
        var world = Chapter(12);
        Run(world, 1);
        var locator = Hero(world, 0);
        SetDown(world, locator, new Vector2(340, 120));
        var grunt = Hero(world, 1);
        SetDown(world, grunt, new Vector2(480, 168));
        Assert.True(RunUntil(world, () => Vector2.Distance(grunt.Position, locator.Position) < 12, 10), $"grunt at {grunt.Position}, locator at {locator.Position} task {world.Story!.Crew.Of(world.Story.Seats[1]).Task} escape {world.Story.Flags.Contains(StoryMarks.Escape)} held {world.Story.Director.Held.Contains(grunt.Id)} lost {world.Story.Crew.Of(world.Story.Seats[1]).LostSince} tick {world.Tick}");
    }
}
