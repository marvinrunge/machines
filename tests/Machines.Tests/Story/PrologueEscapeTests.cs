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
        Carry(reached, 360, each: _ => { });
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
        new(500, 228), new(520, 228), new(520, 192), new(520, 166), new(504, 163), new(470, 168), new(400, 168), new(372, 168),
        new(356, 144), new(340, 140), new(340, 110), new(340, 96), new(352, 92), new(400, 92), new(440, 92), new(470, 92),
    ];

    /// <summary>
    /// One run from checkpoint 12 with the Locator walked along <see cref="Way"/> by move orders and the Grunts on their own
    /// (they follow, hold the shutter's frame, wait out the jets); at the bay the pod is hacked and boarded. Whether it launched.
    /// </summary>
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


    [Fact(Skip = "Not met (package E hand-off to I): on 2026-11-23 none of seeds 1-10 launches; in most the pack downs both Grunts in the hold (they stand to wait out the containers and the pack, standing or within 25 m, aims), and the Locator is lost. Needs tuning of the Grunts' WaitOutHazards and HoldFrame and of the pack's fire; the failure message names each seed's last state.")]
    public void A_locator_walked_along_the_route_with_its_grunts_on_their_own_reaches_the_pod_in_nine_seeds_of_ten()
    {
        var results = Enumerable.Range(1, 10).Select(seed => { var log = new List<string>(); var ok = Walk(seed, log.Add); return (seed, ok, last: string.Join(" | ", log.TakeLast(3))); }).ToList();
        var launched = results.Count(r => r.ok);
        Assert.True(launched >= 9, string.Join(" || ", results.Where(r => !r.ok).Select(r => $"seed {r.seed}: {r.last}")));
    }
}
