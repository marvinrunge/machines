using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The prologue from the alarm to the workshop (plan WP-H and WP-G, stage 3's fixes): the hunters draw before they fire and
/// a fleeing Locator gets away; the Reapers keep out of the cable duct and the vent and hold their fire beyond reach; the
/// Commander splits his squad and only calls, by data link; the gates hold while a Grunt stands on the switch; a hero
/// spotted while the trio still sneaks puts the ship on alert; the Bolter taken from its rack, the armed Grunts beaten;
/// the squad called off stays off; nobody is left behind at the pod.
/// </summary>
public class PrologueAssaultTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Hero(GameWorld world, int seat) => world.HeroOf(world.Story!.Seats[seat])!;

    private static List<Machine> Cast(GameWorld world, string role) => world.Story!.Director.Of(world, role).ToList();

    /// <summary>Steps until done or the seconds are up; whether it was done.</summary>
    private static bool RunUntil(GameWorld world, Func<bool> done, float seconds, Action? each = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && !done(); i++)
        {
            each?.Invoke();
            world.Step();
        }
        return done();
    }

    private static void Run(GameWorld world, float seconds, Action? each = null) => RunUntil(world, () => false, seconds, each);

    /// <summary>A hero set down on free ground for it nearest a point.</summary>
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

    /// <summary>A hack played by the board's own solution from where its target is.</summary>
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

    private static bool Inside(Vector2 at, float x0, float y0, float x1, float y1) => at.X >= x0 && at.X <= x1 && at.Y >= y0 && at.Y <= y1;

    // ---- Die Enge ------------------------------------------------------------------------------------

    [Fact]
    public void A_hunter_draws_before_it_fires_and_its_spotting_comes_first()
    {
        var world = Chapter(3);
        var run = world.Story!;
        var locator = Hero(world, 0);
        var spotted = new Dictionary<int, float>();
        (int Shooter, float At)? firstShot = null;
        float? firstHurt = null, lost = null;
        Run(world, 25, () =>
        {
            if (run.Over)
                lost ??= world.Time;
            foreach (var news in world.Events.Where(e => e.Kind == GameEventKind.StorySpotted))
                spotted.TryAdd(news.Actor, world.Time);
            foreach (var shot in world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Other == locator.Id))
                firstShot ??= (shot.Actor, world.Time);
            if (locator.HitPoints < locator.MaxHitPoints)
                firstHurt ??= world.Time;
        });

        Assert.NotNull(firstShot);
        var (shooter, at) = firstShot.Value;
        // The first shot at the Locator comes from a hunter that spotted it (the stinger) a drawing moment before.
        Assert.True(spotted.TryGetValue(shooter, out var seen), $"{shooter} fired unspotted");
        Assert.True(at - seen >= StorySight.Draw - GameWorld.Dt, $"fired {at - seen:0.00} s after spotting");
        // No one-shot: standing in the open, it lasts several seconds under fire.
        Assert.NotNull(firstHurt);
        if (lost is { } end)
            Assert.True(end - firstHurt.Value >= 5, $"lost {end - firstHurt.Value:0.0} s after the first hit");
    }

    [Fact]
    public void The_locator_fleeing_along_the_vent_reaches_the_crossing()
    {
        var world = Chapter(3);
        var run = world.Story!;
        var locator = Hero(world, 0);

        // Facing east at the vent's mouth: full ahead along the duct, past the grate windows, out at its end.
        Assert.True(RunUntil(world, () => locator.Position.X > 172 || run.Over, 20,
            () => world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Forward: 1))));

        Assert.False(run.Over);
        Assert.True(locator.HitPoints > locator.MaxHitPoints / 2, $"{locator.HitPoints} HP left");
    }

    [Fact]
    public void The_reapers_keep_out_of_the_cable_duct_and_the_vent_and_spare_the_grunts_at_its_end()
    {
        var world = Chapter(3);
        var run = world.Story!;
        // The Locator through already, at the crossing; the Grunts make for the cable duct's far end.
        SetDown(world, Hero(world, 0), new Vector2(180, 104));
        var hunters = Cast(world, Prologue.ReaperRole).Concat(Cast(world, Prologue.CommanderRole)).ToList();
        var inside = new List<string>();

        var locator = Hero(world, 0);
        Run(world, 35, () =>
        {
            // Its driver keeps it still behind the shut gates.
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true));
            foreach (var m in hunters.Where(m => !m.IsDead))
            {
                if (Inside(m.Position, 106, 100, 169, 103.5f) || Inside(m.Position, 106, 116, 171, 119.5f))
                    inside.Add($"{m.Id} at {m.Position}");
            }
        });

        Assert.Empty(inside);
        Assert.False(run.Over);
        for (var seat = 1; seat <= 2; seat++)
        {
            Assert.False(world.CompanionDown(run.Seats[seat]), $"seat {seat} down");
            Assert.True(Hero(world, seat).Position.X > 150, $"seat {seat} at {Hero(world, seat).Position}");
        }
    }

    [Fact]
    public void The_commander_splits_his_squad_between_the_passages_and_only_calls()
    {
        var world = Chapter(3);
        var reapers = Cast(world, Prologue.ReaperRole).OrderBy(m => m.Id).ToList();
        var commander = Cast(world, Prologue.CommanderRole).Single();
        Vector2 Place(string xy) => new(float.Parse(xy.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(xy.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture));
        var (g1, g2) = (Place(Prologue.AtG1), Place(Prologue.AtG2));

        // Two by the spine into P1, one by the core's east door into P2 (MoveCommand with its way).
        Assert.Equal(3, reapers.Count);
        bool InP1(Vector2 at) => Inside(at, 108, 121.5f, 169.5f, 128.5f) && Vector2.Distance(at, g1) < 12;
        bool InP2(Vector2 at) => Inside(at, 106, 105.5f, 169.5f, 112.5f) && Vector2.Distance(at, g2) < 12;
        Assert.All(reapers.Take(2), m => Assert.True(m.Order is MoveOrder move && InP1(move.Target), $"{m.Id}: {m.Order}"));
        Assert.True(reapers[2].Order is MoveOrder last && InP2(last.Target), $"{reapers[2].Id}: {reapers[2].Order}");
        world.Step();
        Assert.True(world.StorySeekerOf(commander.Id)!.Caller);
        Assert.All(reapers, m => Assert.False(world.StorySeekerOf(m.Id)!.Caller));

        // At the crossing's checkpoint, the Locator in the spine before him: he calls his squad by data link and never fires.
        world = Chapter(4);
        commander = Cast(world, Prologue.CommanderRole).Single();
        var locator = Hero(world, 0);
        SetDown(world, locator, commander.Position + new Vector2(14, 0));
        var calls = 0;
        var shots = 0;
        Run(world, 12, () =>
        {
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true));
            calls += world.Events.Count(e => e.Kind == GameEventKind.StoryCue && e.Detail == "datalink" && e.Actor == commander.Id);
            shots += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == commander.Id);
        });
        Assert.True(calls > 0, "the Commander never called");
        Assert.Equal(0, shots);
        // After the core's alarm the ship is hunting anyway: no sneaking alert.
        Assert.DoesNotContain(StoryMarks.Alert, world.Story!.Timers.Keys);
    }

    [Fact]
    public void A_gate_holds_while_a_grunt_stands_on_its_switch_and_gives_way_otherwise()
    {
        var world = Chapter(4);
        var run = world.Story!;
        var cues = new List<string>();
        // Bob is told to wait where he stands, off his switch; Ike holds G1.
        world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero, run.Seats[2]));
        Run(world, Prologue.GateForce + 3, () => cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!)));

        Assert.True(world.SwitchHeld("switch_g1"));
        Assert.False(world.SwitchHeld("switch_g2"));
        Assert.True(world.DoorClosed('G'));
        Assert.False(world.DoorClosed('O'));
        Assert.Contains("gate O strain", cues);
        Assert.Equal(2, cues.Count(c => c == "gate O strain"));
        Assert.Contains("gate O forced", cues);
        Assert.DoesNotContain("gate G forced", cues);
    }

    [Fact]
    public void Door_d5_opens_by_the_locators_hack_from_the_crossing()
    {
        var world = Chapter(3);
        var run = world.Story!;
        Assert.Contains("hack_d5", run.Hacks.Locked);
        SetDown(world, Hero(world, 0), new Vector2(180, 110));
        Run(world, 0.5f);
        Assert.True(run.Director.Has("crossing"));
        Assert.DoesNotContain("hack_d5", run.Hacks.Locked);
        Assert.True(world.DoorClosed('Q'));

        Hack(world, "hack_d5");
        world.Step();

        Assert.False(world.DoorClosed('Q'));
        Assert.Equal(CompanionTask.HoldSwitch, run.Crew.Of(run.Seats[1]).Task);
        Assert.Equal(CompanionTask.HoldSwitch, run.Crew.Of(run.Seats[2]).Task);
    }

    // ---- sneaking --------------------------------------------------------------------------------------

    [Fact]
    public void A_hero_spotted_while_the_trio_sneaks_puts_the_ship_on_alert_by_data_link()
    {
        var world = Chapter(1);
        var run = world.Story!;
        var locator = Hero(world, 0);
        var thresher = Cast(world, Prologue.ThresherRole).Single();
        Run(world, 1);
        Assert.False(StoryMarks.Alerted(run, world.Time));
        // In the Thresher's way along deck A, its searchlight on the Locator.
        SetDown(world, locator, thresher.Position + 12 * new Vector2(MathF.Cos(thresher.Facing), MathF.Sin(thresher.Facing)));
        var called = false;
        Assert.True(RunUntil(world, () => called, 15, () =>
        {
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true));
            called |= world.Events.Any(e => e.Kind == GameEventKind.StoryCue && e.Detail == "datalink");
        }) || StoryMarks.Alerted(run, world.Time), "nobody called");
        world.Step();

        Assert.True(StoryMarks.Alerted(run, world.Time));
        Assert.InRange(run.Timers[StoryMarks.Alert] - world.Time, StorySight.AlertFor - 2, StorySight.AlertFor);
        Assert.DoesNotContain(StoryMarks.Alarm, run.Flags);
    }

    // ---- Erste Gegenwehr -------------------------------------------------------------------------------

    [Fact]
    public void The_bolter_is_taken_from_its_rack_and_the_armed_grunts_go_down_to_it()
    {
        var world = Chapter(4);
        var run = world.Story!;
        var locator = Hero(world, 0);
        var hostiles = Cast(world, Prologue.HostileRole);
        Assert.Equal(3, hostiles.Count);
        Assert.All(hostiles, m => Assert.Equal((Race?)Race.Red, m.DisplayAs));
        Assert.Empty(locator.Weapons);

        // Out of the hostiles' sight they stand their posts: nobody has seen the trio yet.
        world.Step();
        Assert.All(hostiles, m => Assert.IsNotType<AttackOrder>(m.Order));

        // All three into the workshop, the Locator at the rack: it takes the Bolter with the use key.
        var rack = world.Deck!.Marker("pickup_bolter").Position;
        SetDown(world, locator, rack + new Vector2(-1.5f, -1.5f));
        SetDown(world, Hero(world, 1), rack + new Vector2(-6, -4));
        SetDown(world, Hero(world, 2), rack + new Vector2(-6, 0));
        world.Step();
        Assert.Equal("pickup_bolter", world.StoryUseTarget(run.Locator));
        world.Issue(new StoryUseCommand(Race.Red, "pickup_bolter"));
        world.Step();
        Assert.NotEmpty(locator.Weapons);
        Assert.Contains(StoryMarks.Done("pickup_bolter"), run.Flags);
        Assert.True(run.Director.Has("workshop") && run.Director.Has("sealed"));
        world.Step();
        Assert.True(run.Director.Has("weapon"));
        Assert.Equal("Stop the armed Grunts", StoryMarks.Objective(run)!.Value.Text);

        // They saw it and fight; the Locator shoots back, the Grunts ram.
        Assert.True(RunUntil(world, () => hostiles.All(m => m.IsDead) || run.Over, 60, () =>
        {
            var foe = hostiles.Where(m => !m.IsDead).OrderBy(m => Vector2.DistanceSquared(m.Position, locator.Position)).ThenBy(m => m.Id).First();
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Aim: foe.Position));
        }));
        Assert.False(run.Over);
        Assert.True(locator.HitPoints < locator.MaxHitPoints, "they never fired back");
        world.Step();
        Assert.True(run.Director.Has("cleared"));
        Assert.Equal("repair_station", StoryMarks.Objective(run)!.Value.Place);
    }

    [Fact]
    public void Without_a_weapon_the_armed_grunts_are_deadly()
    {
        var world = Chapter(4);
        var run = world.Story!;
        var locator = Hero(world, 0);
        SetDown(world, locator, world.Deck!.Marker("pickup_bolter").Position + new Vector2(-1.5f, -1.5f));

        Assert.True(RunUntil(world, () => run.Over, 60, () => world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true))));
        Assert.Equal(StoryEnding.LocatorLost, run.Ending);
    }

    [Fact]
    public void The_squad_called_off_at_the_workshop_stays_off()
    {
        var world = Chapter(4);
        var run = world.Story!;
        var rack = world.Deck!.Marker("pickup_bolter").Position;
        for (var seat = 0; seat < 3; seat++)
            SetDown(world, Hero(world, seat), rack + new Vector2(-1.5f - 4 * seat, -1.5f));
        world.Step();
        Assert.True(run.Director.Has("sealed"));
        Assert.Contains(Prologue.Sealed, run.Flags);
        var squad = Cast(world, Prologue.ReaperRole).Concat(Cast(world, Prologue.CommanderRole)).ToList();
        Assert.All(squad, m => Assert.True(world.StorySeekerOf(m.Id)!.Blind));
        var stood = squad.ToDictionary(m => m.Id, m => m.Position);

        // The fight in the workshop (its Grunts hit) does not draw them in: they stand their ground where they were.
        var locator = Hero(world, 0);
        world.Issue(new StoryUseCommand(Race.Red, "pickup_bolter"));
        var hostiles = Cast(world, Prologue.HostileRole);
        Run(world, 40, () =>
        {
            var foe = hostiles.Where(m => !m.IsDead).OrderBy(m => Vector2.DistanceSquared(m.Position, locator.Position)).ThenBy(m => m.Id).FirstOrDefault();
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true, Aim: foe?.Position));
        });
        Assert.All(squad.Where(m => !m.IsDead), m => Assert.True(Vector2.Distance(m.Position, stood[m.Id]) < 3 && m.Order is StandGroundOrder, $"{m.Id} at {m.Position} from {stood[m.Id]}: {m.Order}"));
        Assert.True(world.DoorClosed('G') && world.DoorClosed('O'));
    }

    // ---- Abwurf ---------------------------------------------------------------------------------------

    [Fact]
    public void Nobody_is_left_behind_a_downed_grunt_outside_the_pod_holds_the_launch_until_put_together()
    {
        var world = Chapter(6);
        var run = world.Story!;
        Hack(world, "pod_3_hatch");
        var (locator, ike, bob) = (Hero(world, 0), Hero(world, 1), Hero(world, 2));
        SetDown(world, locator, world.Deck!.Marker("pod_3_hatch").Position + new Vector2(0, 1.5f));
        world.Issue(new StoryUseCommand(Race.Red, "pod_3_hatch"));
        Run(world, 0.5f);
        var pod = world.Deck!.Zone(GameWorld.PodZone)!;
        var middle = (pod.Min + pod.Max) / 2;

        // Ike falls outside the pod: the objective points at him, and the others in the pod do not launch it.
        SetDown(world, ike, new Vector2(240, 60));
        world.Damage(ike, 100000, Race.Blue);
        Run(world, 0.5f);
        Assert.True(world.CompanionDown(run.Seats[1]));
        Assert.Equal(Prologue.RallyText, StoryMarks.Objective(run)!.Value.Text);
        SetDown(world, locator, middle);
        SetDown(world, bob, middle + new Vector2(3, 0));
        Run(world, 2, () => SetDown(world, bob, middle + new Vector2(3, 0)));
        Assert.False(run.Director.Has("launch"));

        // The Locator puts him together (use held four seconds), the objective comes back, and all three board.
        SetDown(world, locator, ike.Position + new Vector2(0, 3));
        world.Issue(new StoryUseCommand(Race.Red, $"seat:{run.Seats[1].Value}"));
        Assert.True(RunUntil(world, () => !world.CompanionDown(run.Seats[1]), GameWorld.ReviveSeconds + 1));
        world.Issue(new StoryUseCommand(Race.Red, Held: false));
        Run(world, 0.2f);
        Assert.Equal("Get everyone into the pod", StoryMarks.Objective(run)!.Value.Text);

        // Bob falls inside the pod: one down aboard counts.
        world.Damage(bob, 100000, Race.Blue);
        SetDown(world, locator, middle);
        SetDown(world, ike, middle + new Vector2(-3, 0));
        world.Step();
        Assert.True(world.CompanionDown(run.Seats[2]));
        Assert.True(run.Director.Has("launch"), $"no launch; ike at {ike.Position} down {world.CompanionDown(run.Seats[1])}");
    }
}
