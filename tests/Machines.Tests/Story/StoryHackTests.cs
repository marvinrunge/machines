using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// Hacking in a story match (plan WP-E): the lockstep commands open a board at a target in reach, turn its tiles and walk
/// away; the Locator stands still while it hacks; boards won, traced or broken off by the story; the firewall on the
/// world's clock; and all of it in the checksum, the same for every run.
/// </summary>
public class StoryHackTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld Prologue(int seed = 3) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions(), FogOfWar = false });

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    /// <summary>Gives the command and steps once; the world's reason when it refused it.</summary>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault(r => ReferenceEquals(r.Command, command))?.Reason;
    }

    private static Machine Locator(GameWorld world) => world.HeroOf(world.Story!.Locator)!;

    /// <summary>The Locator taken to a target's marker (the deck's walls are not asked).</summary>
    private static void GoTo(GameWorld world, string target) =>
        Locator(world).Position = world.Deck!.Marker(target).Position;

    /// <summary>Plays the board's own solution by commands until the board changes or the hack ends.</summary>
    private static void SolveBoard(GameWorld world, string target)
    {
        var session = world.Story!.Hacks.Session(target)!;
        var (puzzle, stage) = (session.Puzzle!, session.Stage);
        foreach (var (cell, clockwise) in puzzle.SolutionTurns().ToList())
        {
            Assert.Null(Do(world, new StoryHackTurnCommand(Race.Red, target, cell, clockwise)));
            if (!session.Open || session.Stage != stage || session.Broken)
                return;
        }
    }

    [Fact]
    public void The_locator_hacks_its_clamp_where_it_wakes_and_the_clamp_is_then_hacked()
    {
        var world = Prologue();
        var desk = world.Story!.Hacks;

        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));

        var session = desk.OpenBy(world.Story.Locator);
        Assert.NotNull(session);
        Assert.Equal((HackKind.Clamp, 3, Locator(world).Id), (session.Kind, session.Puzzle!.Size, session.Hero));
        Assert.True(world.IsHacking(Locator(world).Id));
        SolveBoard(world, "clamp_l7");

        Assert.Contains("clamp_l7", desk.Done);
        Assert.False(session.Open);
        Assert.False(world.IsHacking(Locator(world).Id));
        Assert.Equal("that is hacked already", Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
    }

    [Fact]
    public void A_hack_is_refused_out_of_reach_nowhere_twice_at_once_and_to_a_grunt()
    {
        var world = Prologue();
        var story = world.Story!;

        Assert.Equal("too far away to hack", Do(world, new StoryHackOpenCommand(Race.Red, "hack_core")));
        Assert.Equal("nothing to hack there", Do(world, new StoryHackOpenCommand(Race.Red, "spawn_thresher")));
        Assert.Equal("only the Locator hacks", Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7") { Issuer = story.Seats[1] }));
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        Assert.Equal("already at a hack", Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        Assert.Equal("not at that hack", Do(world, new StoryHackTurnCommand(Race.Red, "hack_core", 0)));
        story.Hacks.Locked.Add("terminal_ike");
        GoTo(world, "terminal_ike");
        Assert.Null(Do(world, new StoryHackAbortCommand(Race.Red)));
        Assert.Equal("that cannot be hacked yet", Do(world, new StoryHackOpenCommand(Race.Red, "terminal_ike")));
    }

    [Fact]
    public void Hack_commands_are_refused_outside_a_story()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 1);

        Assert.Equal("no story", Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        Assert.Equal("no story", Do(world, new StoryHackAbortCommand(Race.Red)));
    }

    [Fact]
    public void The_locator_stands_still_while_it_hacks_and_drives_again_after()
    {
        var world = Prologue();
        var locator = Locator(world);
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        var at = locator.Position;

        world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Forward: 1, Turn: 0.5f, Weapons: -1, Buttons: true));
        Run(world, 2);

        Assert.Equal((0f, 0f, 0f), (world.DriveInput[locator.Id].Forward, world.DriveInput[locator.Id].Turn, world.DriveInput[locator.Id].Strafe));
        Assert.Equal(0, world.PlayerWeapons[locator.Id]);
        Assert.DoesNotContain(locator.Id, world.PlayerButtons);
        Assert.Equal(at, locator.Position);

        Assert.Null(Do(world, new StoryHackAbortCommand(Race.Red)));
        world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Forward: 1));
        world.Step();
        Assert.Equal(1f, world.DriveInput[locator.Id].Forward);
    }

    [Fact]
    public void Walking_away_keeps_the_board_and_costs_trace_back()
    {
        var world = Prologue();
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        var session = world.Story!.Hacks.Session("clamp_l7")!;
        var puzzle = session.Puzzle!;
        var (cell, clockwise) = puzzle.SolutionTurns().First();
        Assert.Null(Do(world, new StoryHackTurnCommand(Race.Red, "clamp_l7", cell, clockwise)));

        Assert.Null(Do(world, new StoryHackAbortCommand(Race.Red)));

        Assert.False(session.Open);
        Assert.Same(puzzle, session.Puzzle);
        Assert.Equal(1 + HackPuzzle.AbortCost, puzzle.Trace);
        Assert.Equal("not at a hack", Do(world, new StoryHackAbortCommand(Race.Red)));
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        Assert.Same(puzzle, session.Puzzle);
    }

    [Fact]
    public void A_full_trace_back_raises_the_alarm_and_deals_a_new_board()
    {
        var world = Prologue();
        var desk = world.Story!.Hacks;
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        var session = desk.Session("clamp_l7")!;
        var first = session.Puzzle!;
        var idle = Enumerable.Range(0, first.Cells).First(c => first.IsTile(c) && !first.OnPath(c));

        while (session.Open)
            Assert.Null(Do(world, new StoryHackTurnCommand(Race.Red, "clamp_l7", idle)));

        Assert.Equal(world.Tick - 1, desk.LastAlarmTick);
        Assert.Equal(world.Story.Locator, desk.LastAlarmBy);
        Assert.Equal(1, session.Alarms);
        Assert.Null(session.Puzzle);
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        Assert.NotSame(first, session.Puzzle);
        Assert.Equal(0, session.Puzzle!.Trace);
    }

    [Fact]
    public void The_main_computer_is_three_boards_and_the_last_breaks_off()
    {
        var world = Prologue();
        GoTo(world, "hack_core");
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "hack_core")));
        var session = world.Story!.Hacks.Session("hack_core")!;
        Assert.Equal((4, 0), (session.Puzzle!.Size, session.Stage));

        SolveBoard(world, "hack_core");
        Assert.Equal((5, 1, 3), (session.Puzzle!.Size, session.Stage, session.Puzzle.Targets.Count));
        SolveBoard(world, "hack_core");
        Assert.Equal((5, 2), (session.Puzzle!.Size, session.Stage));
        SolveBoard(world, "hack_core");

        Assert.True(session.Broken);
        Assert.True(session.Open);
        Assert.True(session.Progress >= 2.9f / 3);
        Assert.DoesNotContain("hack_core", world.Story.Hacks.Done);
        Assert.Equal("the link is cut", Do(world, new StoryHackTurnCommand(Race.Red, "hack_core", 0)));
        var trace = session.Puzzle!.Trace;
        Assert.Null(Do(world, new StoryHackAbortCommand(Race.Red)));
        Assert.Equal(trace, session.Puzzle.Trace);
        Assert.Equal("the link is cut", Do(world, new StoryHackOpenCommand(Race.Red, "hack_core")));
    }

    [Fact]
    public void The_director_can_break_a_hack_off()
    {
        var world = Prologue();
        Assert.False(world.BreakHack("clamp_l7"));
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));

        Assert.True(world.BreakHack("clamp_l7"));

        Assert.Equal("the link is cut", Do(world, new StoryHackTurnCommand(Race.Red, "clamp_l7", 0)));
    }

    [Fact]
    public void A_hacker_pushed_away_or_fallen_lets_go_of_the_board()
    {
        var world = Prologue();
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        var session = world.Story!.Hacks.Session("clamp_l7")!;

        Locator(world).Position += new System.Numerics.Vector2(0, GameWorld.HackReach + GameWorld.HackSlack + 1);
        world.Step();

        Assert.False(session.Open);
        Assert.NotNull(session.Puzzle);
    }

    [Fact]
    public void The_pods_firewall_steps_on_the_worlds_clock()
    {
        var world = Prologue();
        GoTo(world, "pod_3_hatch");
        Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "pod_3_hatch")));
        var session = world.Story!.Hacks.Session("pod_3_hatch")!;
        Assert.True(session.Puzzle!.Spec.Firewall);
        var opened = world.Tick - 1;

        // The firewall steps every 1.2 s from the opening on, and its board changes at those ticks only.
        var steps = new List<int>();
        var before = session.Puzzle.Fingerprint();
        for (var tick = 0; tick < 30 * GameWorld.TicksPerSecond && session.Open; tick++)
        {
            world.Step();
            var now = session.Puzzle?.Fingerprint() ?? 0;
            if (now != before)
                steps.Add(world.Tick - 1);
            before = now;
        }

        Assert.True(steps.Count >= 5, $"only {steps.Count} firewall steps in 30 s");
        Assert.All(steps, tick => Assert.Equal(0, (tick - opened) % GameWorld.HackPulseTicks));
        Assert.Equal(GameWorld.HackPulseTicks, steps[0] - opened);
    }

    [Fact]
    public void Hacks_go_into_the_checksum_and_two_runs_agree()
    {
        GameWorld Play()
        {
            var world = Prologue(seed: 21);
            Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
            var puzzle = world.Story!.Hacks.Session("clamp_l7")!.Puzzle!;
            var (cell, clockwise) = puzzle.SolutionTurns().First();
            Assert.Null(Do(world, new StoryHackTurnCommand(Race.Red, "clamp_l7", cell, clockwise)));
            GoTo(world, "pod_3_hatch");
            Assert.Null(Do(world, new StoryHackAbortCommand(Race.Red)));
            Assert.Null(Do(world, new StoryHackOpenCommand(Race.Red, "pod_3_hatch")));
            Run(world, 10);
            return world;
        }

        var (a, b) = (Play(), Play());
        Assert.Equal(a.Checksum(), b.Checksum());
        Assert.Equal(a.Story!.Hacks.Session("pod_3_hatch")!.Puzzle!.Fingerprint(), b.Story!.Hacks.Session("pod_3_hatch")!.Puzzle!.Fingerprint());

        var idle = Prologue(seed: 21);
        Run(idle, a.Tick / (float)GameWorld.TicksPerSecond);
        Assert.Equal(a.Tick, idle.Tick);
        Assert.NotEqual(a.Checksum(), idle.Checksum());
    }
}
