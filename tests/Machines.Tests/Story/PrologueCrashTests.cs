using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The prologue's crash (plan WP-I, docs/STORY.md "Absturz" and "Abwurf"): the countdown from the Notgang (150 s from the
/// last checkpoint) with its stages at 120, 60, 30 and 10 seconds left, each with its line, the ship listing further (the
/// view's lean, never the sim's), blasts and debris falling into the lanes after its warning; the pod's hack a 5 × 5 board
/// with a firewall; the launch only once the hack is done, the Locator opened the hatch and stands at the pod, and both
/// Grunts are aboard (standing in the pod's zone, or lying down in its bay); and the chapter lost when the countdown runs
/// out, told as the story's failure.
/// </summary>
public class PrologueCrashTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Hero(GameWorld world, int seat) => world.HeroOf(world.Story!.Seats[seat])!;

    /// <summary>Steps until done or the seconds are up, gathering the events; whether it was done.</summary>
    private static bool RunUntil(GameWorld world, Func<bool> done, float seconds, List<GameEvent>? events = null, Action? each = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && !done(); i++)
        {
            each?.Invoke();
            world.Step();
            events?.AddRange(world.Events);
        }
        return done();
    }

    private static void Run(GameWorld world, float seconds, List<GameEvent>? events = null, Action? each = null) =>
        RunUntil(world, () => false, seconds, events, each);

    private static void SetDown(GameWorld world, Machine hero, Vector2 at)
    {
        var mobility = GameWorld.MobilityOf(hero);
        for (var r = 0f; r <= 6; r += 0.5f)
        {
            for (var a = 0; a < 16; a++)
            {
                var spot = at + r * new Vector2(MathF.Cos(a * MathF.PI / 8), MathF.Sin(a * MathF.PI / 8));
                if (world.Nav.IsFree(spot, mobility))
                {
                    hero.Position = spot;
                    return;
                }
            }
        }
        hero.Position = at;
    }

    private static void Hack(GameWorld world, string target)
    {
        var run = world.Story!;
        Hero(world, 0).Position = world.Deck!.Marker(target).Position;
        world.Issue(new StoryHackOpenCommand(Race.Red, target));
        world.Step();
        var session = run.Hacks.Session(target)!;
        for (var turns = 0; turns < 3000 && !run.Hacks.Done.Contains(target); turns++)
        {
            if (!session.Open)
                world.Issue(new StoryHackOpenCommand(Race.Red, target));
            else if (session.Puzzle!.SolutionTurns().Take(1).ToList() is [var next])
                world.Issue(new StoryHackTurnCommand(Race.Red, target, next.Cell, next.Clockwise));
            world.Step();
        }
        Assert.Contains(target, run.Hacks.Done);
    }

    private static float Left(GameWorld world) => world.Story!.Timers[StoryMarks.Countdown] - world.Time;

    private static IEnumerable<string> Cues(IEnumerable<GameEvent> events) =>
        events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!);

    private static IEnumerable<string> Lines(IEnumerable<GameEvent> events) =>
        events.Where(e => e.Kind == GameEventKind.StoryLine).Select(e => e.Detail!);

    // ---- the countdown ---------------------------------------------------------------------------------

    [Fact]
    public void The_countdown_comes_in_stages_with_lines_lean_blasts_and_debris_and_running_out_crashes_the_chapter()
    {
        var world = Chapter(Prologue.LastCheckpoint);
        var run = world.Story!;
        Assert.Equal(Prologue.CountdownAtCheckpoint, Left(world), 2);
        var events = new List<GameEvent>();

        // Nobody goes for the pod: the countdown runs out.
        Assert.True(RunUntil(world, () => run.Over, Prologue.CountdownAtCheckpoint + 2, events));

        // Its stages, each told to the view with the seconds left, each with its line.
        var stages = events.Where(e => e is { Kind: GameEventKind.StoryCountdown, Detail: "stage" }).Select(e => e.Other).ToList();
        Assert.Equal(Prologue.CountdownStages.Select(s => (int?)s), stages);
        Assert.Equal([Prologue.Lines.T120, Prologue.Lines.Breach, Prologue.Lines.T60, Prologue.Lines.T30, Prologue.Lines.T10],
            Lines(events).Where(l => l.StartsWith("P_AUTO_", StringComparison.Ordinal)));
        // The ship lists further at each stage (the view's lean: the world stays level), and it tears: blasts, debris.
        Assert.Equal(["tilt 7 1 2", "tilt 9 -1 2", "tilt 12 2 2"], Cues(events).Where(c => c.StartsWith("tilt ", StringComparison.Ordinal)));
        // Blasts and debris at every stage, the last (10 s) too (docs/STORY.md: "bei 120, 60, 30 und 10 Sekunden").
        Assert.True(events.Count(e => e.Kind == GameEventKind.Explosion) >= 5);
        var falling = Cues(events).Where(c => c.StartsWith("debris falling", StringComparison.Ordinal)).ToList();
        var landed = Cues(events).Where(c => c.StartsWith("debris landed", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, falling.Count);
        Assert.Equal(5, landed.Count);
        Assert.Empty(run.Director.Falling);
        // The five, and the Notgang's crate the checkpoint starts with.
        var debris = world.Artefacts.Where(a => a.Id - Artefact.IdBase >= StoryDirector.PlacedBase && !a.IsDead).ToList();
        Assert.Equal(6, debris.Count);
        Assert.All(debris, a => Assert.Equal("crate_large", a.Type.Name));
        Assert.All(debris, a => Assert.False(world.Nav.IsFree(a.Position, GameWorld.MobilityOf(Hero(world, 0)))));

        // Run out: the ship took the trio down, the chapter lost and told as the story's failure.
        Assert.Equal(StoryEnding.Crashed, run.Ending);
        Assert.Null(world.Winner);
        Assert.True(world.Races[Race.Red].Defeated);
        var failed = Assert.Single(events, e => e.Kind == GameEventKind.StoryFailed);
        Assert.Equal(nameof(StoryEnding.Crashed), failed.Detail);
        Assert.Equal(run.Locator, failed.Player);
        Assert.Equal(Prologue.LastCheckpoint, run.Checkpoint);
    }

    [Fact]
    public void From_the_notgang_the_countdown_starts_at_three_minutes_and_debris_lands_behind_its_warning()
    {
        var world = Chapter(5);
        var run = world.Story!;
        var events = new List<GameEvent>();
        SetDown(world, Hero(world, 0), new Vector2(520, 240));
        Run(world, 0.2f, events);

        Assert.True(run.Director.Has("notgang"));
        Assert.Equal((Prologue.LastCheckpoint, 7), (run.Checkpoint, run.Section));
        Assert.InRange(Left(world), Prologue.CountdownFull - 0.5f, Prologue.CountdownFull);
        Assert.Contains(Prologue.Lines.Auto3, Lines(events));
        // The first debris is on its way, warned of; it lands after the warning, not before.
        var fall = Assert.Single(run.Director.Falling).Value;
        Assert.Contains(Cues(events), c => c.StartsWith("debris falling 0", StringComparison.Ordinal));
        Assert.DoesNotContain(Cues(events), c => c.StartsWith("debris landed", StringComparison.Ordinal));
        Assert.Equal(world.Deck!.Marker("debris_1").Position + new Vector2(5, 0), fall.Where);
        Run(world, Prologue.FallWarning, events);
        Assert.Contains("debris landed 0", Cues(events));
        Assert.Contains(world.Artefacts, a => !a.IsDead && a.Position == fall.Where && a.Type.Name == "crate_large");
    }

    [Fact]
    public void Debris_harms_what_stands_under_it_and_lies_beside_a_machine_rather_than_on_it()
    {
        var world = Chapter(Prologue.LastCheckpoint);
        var run = world.Story!;
        var locator = Hero(world, 0);
        // The stage at 120 s lets debris fall at its marker in the Notgang; the Locator steps under it.
        Assert.True(RunUntil(world, () => run.Director.Falling.Count > 0, 40));
        var fall = run.Director.Falling.Values.Single();
        locator.Position = fall.Where;
        var health = locator.HitPoints + locator.Armour;
        var placed = run.Director.Placed;
        Assert.True(RunUntil(world, () => run.Director.Falling.Count == 0, Prologue.FallWarning + 0.5f, each: () => locator.Position = fall.Where));

        Assert.True(locator.HitPoints + locator.Armour < health, "the debris did not hit the Locator");
        Assert.False(locator.IsDead);
        Assert.Equal(placed + 1, run.Director.Placed);
        var piece = world.Artefacts.Single(a => a.Id == Artefact.IdBase + StoryDirector.PlacedBase + placed);
        Assert.True(Vector2.Distance(piece.Position, locator.Position) >= locator.Radius + StoryFall.Lies, $"{piece.Position} by the Locator at {locator.Position}");
        Assert.True(Vector2.Distance(piece.Position, fall.Where) < locator.Radius + 7);
        Assert.True(world.Nav.IsFree(locator.Position, GameWorld.MobilityOf(locator)));
    }

    // ---- the pod --------------------------------------------------------------------------------------

    [Fact]
    public void The_pods_hack_is_a_five_by_five_board_with_a_firewall_and_the_hatch_is_refused_before_it()
    {
        var board = Assert.Single(HackSpec.Boards(HackKind.Pod));
        Assert.Equal(5, board.Size);
        Assert.True(board.Firewall);

        var world = Chapter(Prologue.LastCheckpoint);
        var run = world.Story!;
        SetDown(world, Hero(world, 0), world.Deck!.Marker("pod_3_hatch").Position + new Vector2(0, 1.5f));
        var use = new StoryUseCommand(Race.Red, "pod_3_hatch");
        world.Issue(use);
        world.Step();
        var events = new List<GameEvent>(world.Events);
        Assert.Equal("launch clearance denied", world.Rejected.FirstOrDefault(r => ReferenceEquals(r.Command, use))?.Reason);
        Run(world, 0.5f, events);
        Assert.Contains(Prologue.Lines.PodNo, Lines(events));
        Assert.DoesNotContain(StoryMarks.Done("pod_3_hatch"), run.Flags);
    }

    [Fact]
    public void The_pod_launches_only_with_the_hack_done_the_hatch_opened_the_locator_at_the_pod_and_both_grunts_aboard()
    {
        var world = Chapter(Prologue.LastCheckpoint);
        var run = world.Story!;
        var director = run.Director;
        var (locator, ike, bob) = (Hero(world, 0), Hero(world, 1), Hero(world, 2));
        var pod = world.Deck!.Zone(GameWorld.PodZone)!;
        var middle = (pod.Min + pod.Max) / 2;
        void AllIn()
        {
            SetDown(world, locator, middle);
            SetDown(world, ike, middle + new Vector2(-3, 0));
            SetDown(world, bob, middle + new Vector2(3, 0));
        }

        // Everyone at the pod, the hack not done: no launch.
        AllIn();
        Run(world, 1, each: AllIn);
        Assert.False(director.Has("launch"));

        // The hack done, the hatch not yet opened: still none; the objective says to open it.
        Hack(world, "pod_3_hatch");
        AllIn();
        Run(world, 1, each: AllIn);
        Assert.False(director.Has("launch"));
        Assert.Equal(("Open the hatch", (string?)"pod_3_hatch"), StoryMarks.Objective(run));

        // The hatch opened, but Ike stands out in the Notgang (held there): none.
        SetDown(world, locator, world.Deck.Marker("pod_3_hatch").Position + new Vector2(0, 1.5f));
        world.Issue(new StoryUseCommand(Race.Red, "pod_3_hatch"));
        Run(world, 0.2f, each: () => SetDown(world, ike, new Vector2(520, 136)));
        Assert.Contains(StoryMarks.Done("pod_3_hatch"), run.Flags);
        Assert.Equal("Get everyone into the pod", StoryMarks.Objective(run)!.Value.Text);
        Assert.Equal(CompanionTask.Board, run.Crew.Of(run.Seats[2]).Task);
        Run(world, 1, each: () => SetDown(world, ike, new Vector2(520, 136)));
        Assert.False(director.Has("launch"));

        // Both Grunts in, the Locator off at the bay's far side: none.
        Run(world, 1, each: () =>
        {
            AllIn();
            SetDown(world, locator, new Vector2(450, 106));
        });
        Assert.False(director.Has("launch"));

        // Ike lies down in the bay outside the pod's zone, Bob and the Locator at the pod: he is carried in, the pod goes.
        SetDown(world, ike, new Vector2(468, 80));
        world.Damage(ike, 100000, Race.Blue);
        Assert.True(world.CompanionDown(run.Seats[1]));
        AllInButIke();
        Assert.True(RunUntil(world, () => director.Has("launch"), 1, each: AllInButIke));
        Assert.Equal(8, run.Section);
        Assert.DoesNotContain(StoryMarks.Countdown, run.Timers.Keys);
        // The rally does not call for a Grunt down in the pod's bay.
        Assert.Null(StoryMarks.Objective(run));
        Assert.True(RunUntil(world, () => run.Over, Prologue.Away + 1));
        Assert.Equal(StoryEnding.Escaped, run.Ending);
        Assert.Equal(Race.Red, world.Winner);

        void AllInButIke()
        {
            SetDown(world, locator, middle);
            SetDown(world, bob, middle + new Vector2(3, 0));
        }
    }

    [Fact]
    public void The_countdown_and_the_debris_go_into_the_checksum_and_play_the_same_twice()
    {
        static (ulong Sum, int Placed) Play()
        {
            var world = Chapter(Prologue.LastCheckpoint, seed: 7);
            Run(world, 40);
            return (world.Checksum(), world.Story!.Director.Placed);
        }

        var (a, b) = (Play(), Play());
        Assert.Equal(a, b);
        Assert.True(a.Placed >= 1);
    }
}
