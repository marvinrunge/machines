using System.Numerics;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

/// <summary>
/// The core fight, played (docs/STORY.md, Der Hack and Erwischt): the Locator hacks the core's three boards while the Grunts
/// barricade doors J and I with crates; the Commander's squad sets out on the second board, hammers at J's barricade, goes
/// round by door W to I and shoots its crates away; the last board holds at 90 % until they are in; the break-in is the
/// new checkpoint 3, their first volleys go into the core, which blows, and the trio gets out by the vent and the cable duct.
/// Without barricades the squad is through J at once and cuts the hack off. All on ticks, the same every time.
/// </summary>
public class PrologueCoreTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Hero(GameWorld world, int seat) => world.HeroOf(world.Story!.Seats[seat])!;

    private static List<Machine> Cast(GameWorld world, string role) => world.Story!.Director.Of(world, role).ToList();

    private static bool Inside(Vector2 at, float x0, float y0, float x1, float y1) => at.X >= x0 && at.X <= x1 && at.Y >= y0 && at.Y <= y1;

    /// <summary>
    /// The core hack played as a person plays it: through door I to the console, plugged in, the board's own solution a turn
    /// every <paramref name="every"/> seconds; while the board is locked the Locator waits at it. It keeps the beats as they
    /// fire (game time), the cues and the shots.
    /// </summary>
    private sealed class Hacker(GameWorld world, float every)
    {
        private int _next;

        private readonly HashSet<string> _seen = [];

        private StoryRun Run => world.Story!;

        public Dictionary<string, float> At { get; } = [];

        public List<string> Timeline { get; } = [];

        public List<string> Cues { get; } = [];

        public List<string> Refused { get; } = [];

        public List<GameEvent> Fired { get; } = [];

        public HackSession? Session => Run.Hacks.Session("hack_core");

        public void Plug()
        {
            // Through door I, which opens by itself as the trio comes near.
            world.OpenDoor('I');
            Hero(world, 0).Position = world.Deck!.Marker("hack_core").Position;
            world.Issue(new StoryHackOpenCommand(Race.Red, "hack_core"));
            Step();
        }

        public void Step()
        {
            StoryHackTurnCommand? turn = null;
            if (Session is { Open: true, Broken: false, Puzzle: { } puzzle } && world.Tick >= _next)
            {
                if (puzzle.SolutionTurns().Take(1).ToList() is [var (cell, clockwise)])
                    world.Issue(turn = new StoryHackTurnCommand(Race.Red, "hack_core", cell, clockwise));
                _next = world.Tick + (int)(every * GameWorld.TicksPerSecond);
            }
            world.Step();
            if (turn is not null && world.Rejected.FirstOrDefault(r => ReferenceEquals(r.Command, turn)) is { } no)
                Refused.Add(no.Reason);
            Cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
            Fired.AddRange(world.Events.Where(e => e.Kind == GameEventKind.Fired));
            foreach (var beat in Run.Director.Fired.Keys.Where(_seen.Add))
            {
                At[beat] = world.Time;
                Timeline.Add(FormattableString.Invariant($"{world.Time,6:0.0} s  {beat,-12} board {Session?.Stage + 1}, hack {Session?.Progress * 100:0} %"));
            }
        }

        public bool Until(Func<bool> done, float seconds)
        {
            for (var i = 0; i < seconds * GameWorld.TicksPerSecond && !done(); i++)
                Step();
            return done();
        }
    }

    /// <summary>The fight from the antechamber's checkpoint until a beat fired (the core blowing by default).</summary>
    private static (GameWorld World, Hacker Hacker) Fight(float every = 1.5f, bool barricades = true, string until = "core_blows")
    {
        var world = Chapter(6);
        var run = world.Story!;
        var hacker = new Hacker(world, every);
        hacker.Plug();
        if (!barricades)
        {
            // The Locator tells both Grunts to wait where they stand: nobody barricades.
            world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero));
            hacker.Step();
        }
        Assert.True(hacker.Until(() => run.Director.Has(until) || run.Over, 200), $"no {until} by {world.Time:0} s");
        return (world, hacker);
    }

    [Fact]
    public void The_grunts_barricade_both_doors_while_the_locator_hacks_and_the_barricade_blocks_the_way()
    {
        var world = Chapter(6);
        var run = world.Story!;
        var hacker = new Hacker(world, 1.5f);
        hacker.Plug();
        Assert.True(hacker.Until(() => run.Director.Has("hammer_j") || run.Director.Has("burst_j"), 60));

        // The squad set out on the second board and came down the spine to J; by then the Grunts filled every slot of both doors.
        Assert.True(hacker.At["squad"] > hacker.At["core_open"]);
        Assert.Contains(StoryMarks.Squad, run.Flags);
        Assert.Equal(3, world.BarricadeCrates("J").Count);
        Assert.Equal(3, world.BarricadeCrates("I").Count);
        // Neither the Reapers nor the Commander find a way into the chamber past them (door W, the long way, still shut).
        var reaper = Cast(world, Prologue.ReaperRole)[0];
        var core = world.Deck!.Marker("hack_core").Position;
        foreach (var from in new[] { world.Deck.Marker("squad_j_3").Position, world.Deck.Marker("squad_i_3").Position })
        {
            var path = world.Nav.FindPath(from, core, GameWorld.MobilityOf(reaper));
            Assert.True(path.Count == 0 || Vector2.Distance(path[^1], core) > 10, $"a way from {from} to the console");
        }
    }

    [Fact]
    public void At_a_barricaded_door_j_the_squad_hammers_then_goes_round_by_door_w_to_door_i()
    {
        var (world, hacker) = Fight(until: "at_i");
        var run = world.Story!;

        Assert.True(hacker.At["hammer_j"] - hacker.At["at_j"] < 2);
        Assert.Contains("barricade J strain", hacker.Cues);
        Assert.Equal(Prologue.HammerJ, hacker.At["go_round"] - hacker.At["hammer_j"], 1);
        Assert.False(world.DoorClosed('W'));
        Assert.False(run.Director.Has("burst_j"));
        // J's crates still stand; the squad came round through the service rooms, never into the chamber.
        Assert.Equal(3, world.BarricadeCrates("J").Count);
        Assert.Contains(Cast(world, Prologue.ReaperRole), m => Inside(m.Position, 176, 208, 212, 228));
        Assert.DoesNotContain(Cast(world, Prologue.ReaperRole).Concat(Cast(world, Prologue.CommanderRole)), m => Inside(m.Position, 160, 232, 224, 296));
    }

    [Fact]
    public void At_door_i_they_shoot_its_crates_which_take_harm_but_stand_until_they_fly_aside()
    {
        var (world, hacker) = Fight(until: "shoot_i");
        var run = world.Story!;
        var crates = world.BarricadeCrates("I");
        Assert.Equal(3, crates.Count);
        Assert.True(hacker.Until(() => run.Director.Has("break_in"), Prologue.ShootMax + 1));

        Assert.Contains(crates, crate => crate.HitPoints < crate.MaxHitPoints);
        Assert.True(hacker.Fired.Count(f => crates.Any(c => c.Id == f.Other)) >= 3);
        Assert.Contains("barricade I fire", hacker.Cues);
        Assert.True(hacker.Cues.Count(c => c == "barricade I strain") >= 3);
        // Thrown aside at the break-in: the doorway is free, the view throws each crate.
        Assert.Empty(world.BarricadeCrates("I"));
        Assert.Equal(3, hacker.Cues.Count(c => c.StartsWith("thrown ", StringComparison.Ordinal) && c.EndsWith("crate_large", StringComparison.Ordinal)));
        Assert.InRange(hacker.At["break_in"] - hacker.At["shoot_i"], Prologue.ShootMin - 0.1f, Prologue.ShootMax + 0.1f);
        Assert.True(run.Director.Has("burst_i"));
    }

    [Fact]
    public void The_last_board_holds_at_90_percent_until_the_break_in_cuts_the_hack_off()
    {
        var (world, hacker) = Fight(until: "break_in");
        var run = world.Story!;
        var session = hacker.Session!;

        // The core locked its last board short of the win, the Locator plugged in and waiting; the HUD said why.
        Assert.True(hacker.At["held"] < hacker.At["break_in"], "the board never held");
        Assert.Contains("the board is locked", hacker.Refused);
        Assert.Equal(2, session.Stage);
        Assert.True(session.Progress >= (2 + 0.9f) / 3 - 0.01f, $"held at {session.Progress:0.00}");
        Assert.DoesNotContain("hack_core", run.Hacks.Done);
        // The break-in: the hack cut off, the probe out, the Locator free to move at once.
        Assert.True(session.Broken);
        Assert.False(session.Open);
        Assert.False(world.IsHacking(Hero(world, 0).Id));
        Assert.False(world.HeldByStory(Hero(world, 0).Id));
        Assert.Equal(7, run.Checkpoint);
        Assert.Equal(4, run.Section);
        Assert.DoesNotContain(StoryMarks.Squad, run.Flags);
    }

    [Fact]
    public void The_first_volleys_hit_the_core_at_whose_foot_the_locator_stands_and_the_core_blows()
    {
        var (world, hacker) = Fight();
        var run = world.Story!;
        var core = world.Artefacts.First(a => a.Tag == Prologue.CoreTag);
        var locator = Hero(world, 0);

        // Their first rounds after the break-in were at the core, past the Locator at its foot; only once they had stormed in
        // did they turn on it (Erwischt is played: the Locator takes cover or runs).
        var reapers = Cast(world, Prologue.ReaperRole).Select(r => r.Id).ToHashSet();
        var rounds = hacker.Fired.Where(f => reapers.Contains(f.Actor)).ToList();
        var first = rounds.FindIndex(f => f.Other == locator.Id);
        var before = rounds.Take(first < 0 ? rounds.Count : first).Count(f => f.Other == core.Id);
        Assert.True(before >= 3, $"{before} rounds at the core before any at the Locator");
        Assert.True(hacker.At["storm_sight"] - hacker.At["break_in"] >= Prologue.StormIn + Prologue.StormSight - 0.1f);
        Assert.False(locator.IsDead);
        Assert.Equal(["core hit 1", "core hit 2", "core hit 3", "core blows"], hacker.Cues.Where(c => c.StartsWith("core ", StringComparison.Ordinal)));
        Assert.Equal(Prologue.CoreBlows, hacker.At["core_blows"] - hacker.At["break_in"], 1);
        // Blown: the chamber in red light, the vent and the cable duct open.
        Assert.True(core.IsDead);
        Assert.Equal("emergency", world.StoryLightState("core"));
        Assert.False(world.DoorClosed('V'));
        Assert.False(world.DoorClosed('K'));
        Assert.Equal(("Escape through the vent", (string?)"226,268"), StoryMarks.Objective(run));
        Assert.True(hacker.Until(() => run.Director.Has("control_lost"), Prologue.ControlLost + 1));
        Assert.Equal("emergency", world.StoryLightState("spine"));
    }

    [Fact]
    public void The_core_is_hit_by_the_reapers_rounds_but_stands_until_it_blows()
    {
        var (world, _) = Fight(until: "core_hit_3");
        var core = world.Artefacts.First(a => a.Tag == Prologue.CoreTag);
        Assert.False(core.IsDead);
        Assert.True(core.HitPoints <= core.MaxHitPoints * 0.2f + 1, $"{core.HitPoints} of {core.MaxHitPoints}");
        Assert.True(core.HitPoints >= world.Story!.Director.Floors[core.Id]);
    }

    [Fact]
    public void The_break_in_is_checkpoint_3_and_the_chapter_starts_there_again()
    {
        var world = Chapter(7);
        var run = world.Story!;
        Assert.Equal((7, 4), (run.Checkpoint, run.Section));
        // Both doors barricaded, the squad at I about to shoot its crates away, the core standing, the alarm on.
        Assert.Equal(3, world.BarricadeCrates("J").Count);
        Assert.Equal(3, world.BarricadeCrates("I").Count);
        Assert.All(Cast(world, Prologue.ReaperRole), m => Assert.True(Inside(m.Position, 176, 208, 212, 228), $"{m.Id} at {m.Position}"));
        Assert.Contains(world.Artefacts, a => a.Tag == Prologue.CoreTag && !a.IsDead);
        Assert.Contains(StoryMarks.Alarm, run.Flags);
        var cues = new List<string>();
        var locator = Hero(world, 0);
        float? broken = null, stormed = null;
        int? whole = null, open = null;
        for (var i = 0; i < 20 * GameWorld.TicksPerSecond && !run.Director.Has("core_blows"); i++)
        {
            world.Step();
            cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
            if (run.Director.Has("break_in"))
                broken ??= world.Time;
            if (run.Director.Has("storm_sight") && stormed is null)
                (stormed, whole) = (world.Time, locator.HitPoints);
            // The Locator stands in the open at the console a few seconds after they storm in, then takes cover by the vent
            // (four: their rounds hurt only where they meet its body, and the first to do so came after three).
            if (stormed is { } since && open is null && world.Time >= since + 4)
            {
                open = locator.HitPoints;
                locator.Position = new Vector2(219, 262);
            }
        }

        // In within a few seconds, the crates thrown, and on as a run played through has it.
        Assert.NotNull(broken);
        Assert.True(broken < 6, $"the break-in came at {broken:0.0} s");
        Assert.Contains("break in", cues);
        Assert.Equal(3, cues.Count(c => c.StartsWith("thrown ", StringComparison.Ordinal)));
        Assert.True(run.Director.Has("core_blows"));
        // Their first volleys went at the core past the Locator; once they stormed in, the Locator in the open was fired at,
        // and in cover it lived to see the core blow.
        Assert.Equal(Prologue.StormIn + Prologue.StormSight, stormed!.Value - broken!.Value, 1);
        Assert.Equal(locator.MaxHitPoints, whole);
        Assert.True(open < locator.MaxHitPoints, $"{open} of {locator.MaxHitPoints} in the open");
        Assert.False(locator.IsDead);
        var inside = Cast(world, Prologue.ReaperRole).Where(m => !m.IsDead && Inside(m.Position, 160, 228, 224, 296)).ToList();
        Assert.True(inside.Count >= 2, string.Join(", ", Cast(world, Prologue.ReaperRole).Select(m => $"{m.Id} at {m.Position}")));
        Assert.Equal(7, run.Checkpoint);
    }

    [Fact]
    public void Without_barricades_the_squad_is_through_j_at_once_and_cuts_the_hack_off_earlier()
    {
        var (bare, open) = Fight(barricades: false, until: "break_in");
        var (_, held) = Fight(until: "break_in");

        Assert.True(bare.Story!.Director.Has("burst_j"));
        Assert.True(open.At["break_in"] - open.At["at_j"] < 2);
        Assert.True(open.At["break_in"] < held.At["break_in"] - 15, $"{open.At["break_in"]:0} s against {held.At["break_in"]:0} s");
        Assert.False(open.At.ContainsKey("held"));
        Assert.True(open.Session!.Broken);
        Assert.False(open.Session.Held);
        Assert.Empty(bare.BarricadeCrates("J"));
        Assert.Equal(7, bare.Story.Checkpoint);
    }

    [Fact]
    public void Out_of_the_chamber_by_the_vent_the_pursuit_starts()
    {
        var (world, hacker) = Fight();
        var run = world.Story!;
        Hero(world, 0).Position = new Vector2(240, 268);
        Assert.True(hacker.Until(() => run.Director.Has("fled"), 1));
        Assert.Equal(5, run.Section);
        Assert.False(world.DoorClosed('M'));
        Assert.Equal(CompanionTask.HoldSwitch, run.Crew.Of(run.Seats[1]).Task);
        Assert.Equal(CompanionTask.Go, run.Crew.Of(run.Seats[2]).Task);
    }

    [Fact]
    public void The_core_fight_is_the_same_every_time()
    {
        (ulong, int, string) Play()
        {
            var (world, hacker) = Fight(every: 1.2f, until: "control_lost");
            return (world.Checksum(), world.Tick, string.Join(" ", hacker.At.OrderBy(a => a.Value).Select(a => a.Key)));
        }

        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void A_grunt_a_person_drives_pushes_a_crate_into_a_barricade_slot_and_sets_one_down_backing_up()
    {
        PlayerId locator = new(0), ike = new(4), bob = new(5);
        var world = MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
                new PlayerSeat(locator, Race.Red, PlayerKind.Human, "Host"), new PlayerSeat(ike, Race.Red, PlayerKind.Human, "Anna"),
                new PlayerSeat(bob, Race.Red, PlayerKind.Bot, "Bolt"), new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]),
            3, new MatchOptions { Story = new StoryOptions { Checkpoint = 6 }, FogOfWar = false });
        var run = world.Story!;
        var grunt = world.HeroOf(ike)!;
        var cues = new List<string>();
        void Drive(float forward, int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                world.Issue(new DriveCommand(Race.Red, grunt.Id, true, forward) { Issuer = ike });
                world.Step();
                cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
            }
        }

        // Before the loose crate by door J, facing it: driving into it takes it up.
        var crate = world.Artefacts.Single(a => a.Tag == 307);
        grunt.Position = crate.Position + new Vector2(5, 0);
        grunt.Facing = MathF.PI;
        Drive(1, 40);
        var companion = run.Crew.Of(ike);
        Assert.Equal(CompanionStep.Pushing, companion.Step);
        Assert.Equal(307, companion.Carrying?.Tag);
        Assert.Contains("crate pushed", cues);

        // Brought before J's first slot: it locks in.
        var slot = world.BarricadeSlots("J")[0];
        grunt.Position = slot.Position + new Vector2(0, -6);
        grunt.Facing = MathF.PI / 2;
        Drive(0, 2);
        Assert.Null(companion.Carrying);
        Assert.True(world.SlotFilled(slot));
        Assert.Equal(307, world.BarricadeCrates("J").Single().Tag);
        Assert.Contains("crate set", cues);

        // Another taken up and set down again by backing up, in the middle of the chamber.
        var other = world.Artefacts.Single(a => a.Tag == 308);
        grunt.Position = other.Position + new Vector2(-5, 0);
        grunt.Facing = 0;
        Drive(1, 40);
        Assert.Equal(308, companion.Carrying?.Tag);
        grunt.Position = new Vector2(200, 250);
        grunt.Facing = 0;
        Drive(-1, 2);
        Assert.Null(companion.Carrying);
        Assert.Contains(world.Artefacts, a => a.Tag == 308 && !a.IsDead && Vector2.Distance(a.Position, new Vector2(200, 250)) < 6);
    }

    [Fact]
    public void A_crate_a_person_was_pushing_is_set_down_when_the_computer_takes_the_grunt_over()
    {
        PlayerId locator = new(0), ike = new(4), bob = new(5);
        var world = MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
                new PlayerSeat(locator, Race.Red, PlayerKind.Human, "Host"), new PlayerSeat(ike, Race.Red, PlayerKind.Human, "Anna"),
                new PlayerSeat(bob, Race.Red, PlayerKind.Bot, "Bolt"), new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]),
            3, new MatchOptions { Story = new StoryOptions { Checkpoint = 6 }, FogOfWar = false });
        var run = world.Story!;
        var grunt = world.HeroOf(ike)!;
        var crate = world.Artefacts.Single(a => a.Tag == 307);
        grunt.Position = crate.Position + new Vector2(5, 0);
        grunt.Facing = MathF.PI;
        for (var i = 0; i < 40; i++)
        {
            world.Issue(new DriveCommand(Race.Red, grunt.Id, true, 1) { Issuer = ike });
            world.Step();
        }
        var companion = run.Crew.Of(ike);
        Assert.Equal(307, companion.Carrying?.Tag);

        // Anna goes away: the computer takes Ike, and the crate he pushed stands in the world again, nobody carrying it.
        world.ChangePresence([new PresenceChange(ike, Away: true)]);
        world.Step();
        Assert.True(world.StoryBotPlays(ike));
        Assert.Null(companion.Carrying);
        Assert.NotEqual(CompanionStep.Pushing, companion.Step);
        Assert.Contains(world.Artefacts, a => a.Tag == 307 && !a.IsDead);
    }

    [Fact]
    public void The_scare_grunt_glares_up_as_the_locator_comes_to_the_duct_mouth_on_deck_a()
    {
        var world = Chapter(1);
        var run = world.Story!;
        var locator = Hero(world, 0);
        // Along R4 from its west door toward duct V1a: nothing yet at the door, the glare at the duct's mouth.
        locator.Position = new Vector2(124, 52);
        for (var i = 0; i < 5; i++)
            world.Step();
        Assert.False(run.Director.Has("scare_grunt2"));
        locator.Position = new Vector2(160, 78);
        for (var i = 0; i < 5; i++)
            world.Step();
        Assert.True(run.Director.Has("scare_grunt2"));
    }

    [Fact]
    public void No_barricade_slot_stands_before_door_k_the_grunts_way_out()
    {
        var world = Chapter(6);
        Assert.DoesNotContain(world.Deck!.Markers, m => m.Name.StartsWith("barricade_K", StringComparison.Ordinal));
        Assert.Equal(3, world.BarricadeSlots("J").Count);
        Assert.Equal(3, world.BarricadeSlots("I").Count);
    }

    /// <summary>The core fight's timeline as a person playing a turn every 1.5 s and every 3 s meets it (it prints; the checks are above).</summary>
    [Fact]
    public void Timeline_of_the_core_fight()
    {
        foreach (var every in new[] { 1.5f, 3f })
        {
            var (world, hacker) = Fight(every, until: "control_lost");
            output.WriteLine($"a turn every {every} s:");
            foreach (var line in hacker.Timeline.Where(l => hacker.At.GetValueOrDefault(l[9..].Trim().Split(' ')[0]) > 0.2f))
                output.WriteLine(line);
            Assert.True(world.Story!.Director.Has("control_lost"));
        }
    }
}
