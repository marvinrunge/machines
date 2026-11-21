using System.Numerics;
using System.Text.Json;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// The companions (plan WP-F; docs/STORY.md, Die beiden Grunts and Koop): Ike and Bob follow, wait and distract on the
/// Locator's word, barricade a door with crates and themselves, ram so the Locator can hit, lie downed instead of being
/// destroyed and are put together again by hand or by the repair station, hang dormant before their hack, hold switches and
/// board the pod. A person who takes a Grunt over plays the same actor on. All of it on ticks, alike in every world.
/// </summary>
public class CompanionTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static PlayerId P(int value) => new(value);

    private static readonly PlayerId Locator = P(0), Ike = P(4), Bob = P(5);

    private static GameWorld Prologue(PlayerKind ike = PlayerKind.Bot, PlayerKind bob = PlayerKind.Bot, int seed = 7) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, ike, ike == PlayerKind.Human ? "Anna" : "Ike"),
            new PlayerSeat(Bob, Race.Red, bob, bob == PlayerKind.Human ? "Ben" : "Bob"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), seed, new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    /// <summary>Runs until <paramref name="done"/> holds, at most <paramref name="seconds"/>; whether it did.</summary>
    private static bool RunUntil(GameWorld world, Func<bool> done, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            if (done())
                return true;
            world.Step();
        }
        return done();
    }

    /// <returns>Why the world refused the command, given by <paramref name="by"/>, or null.</returns>
    private static string? Do(GameWorld world, Command command, PlayerId? by = null)
    {
        world.Issue(command with { Issuer = by ?? Locator });
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    private static Machine Hero(GameWorld world, PlayerId seat) => world.HeroOf(seat)!;

    /// <summary>Sets the trio down in the lane of maintenance deck A, the Locator at <paramref name="at"/> and the Grunts behind it.</summary>
    private static void OnDeckA(GameWorld world, Vector2? at = null)
    {
        var spot = at ?? new Vector2(80, 26);
        Place(world, Locator, spot);
        Place(world, Ike, spot + new Vector2(-6, 2));
        Place(world, Bob, spot + new Vector2(-6, -2));
    }

    private static void Place(GameWorld world, PlayerId seat, Vector2 at, float facing = 0)
    {
        var hero = Hero(world, seat);
        hero.Position = at;
        hero.Facing = facing;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    private static IEnumerable<string> Barked(GameWorld world) => world.Story!.Barks.Select(b => b.Line);

    // ---- orders -----------------------------------------------------------------------------------------

    [Fact]
    public void The_grunts_follow_the_locator_at_a_distance()
    {
        var world = Prologue();
        OnDeckA(world);
        Run(world, 1);
        Place(world, Locator, new Vector2(110, 26));

        Run(world, 20);

        // In a queue (GameWorld.StoryMotion): the nearer keeps its distance to the Locator, the other to its mate.
        var locator = Hero(world, Locator);
        var (ike, bob) = (Hero(world, Ike), Hero(world, Bob));
        var (first, second) = Vector2.Distance(ike.Position, locator.Position) < Vector2.Distance(bob.Position, locator.Position) ? (ike, bob) : (bob, ike);
        Assert.InRange(Vector2.Distance(first.Position, locator.Position), 3, 9);
        Assert.InRange(Vector2.Distance(second.Position, first.Position), 3, 9);
        foreach (var grunt in new[] { ike, bob })
            Assert.Equal(new FollowOrder(locator.Id), grunt.Order);
    }

    [Fact]
    public void Wait_holds_the_grunts_where_they_stand_while_the_locator_goes_on()
    {
        var world = Prologue();
        OnDeckA(world);
        Run(world, 1);
        var (ike, bob) = (Hero(world, Ike).Position, Hero(world, Bob).Position);

        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero)));
        Place(world, Locator, new Vector2(112, 26));
        Run(world, 10);

        Assert.True(Vector2.Distance(ike, Hero(world, Ike).Position) < 1.5f);
        Assert.True(Vector2.Distance(bob, Hero(world, Bob).Position) < 1.5f);
        Assert.IsType<StandGroundOrder>(Hero(world, Ike).Order);
        Assert.Contains(Barked(world), line => line is "P_IKE_WAIT" or "P_BOB_HOLD");
        // One answers the order, not both.
        Assert.Single(world.Story!.Barks);

        // Follow again: they come after it.
        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Follow, Vector2.Zero)));
        Run(world, 15);
        Assert.InRange(Vector2.Distance(Hero(world, Ike).Position, Hero(world, Locator).Position), 3, 9);
    }

    [Fact]
    public void Only_the_locator_orders_and_only_the_computers_grunts()
    {
        var world = Prologue(ike: PlayerKind.Human);

        Assert.Equal("only the Locator gives the Grunts orders", Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero), Ike));
        Assert.Equal("a person plays that Grunt", Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero, Ike)));
        Assert.Equal("no such Grunt", Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero, Locator)));
        // To everyone: only Bob, the computer's, takes it.
        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero)));
        Assert.Equal(CompanionOrder.Wait, world.Story!.Crew.Of(Bob).Order);
        Assert.Equal(CompanionOrder.Follow, world.Story.Crew.Of(Ike).Order);
        // A person's Grunt never barks.
        Assert.All(world.Story.Barks, bark => Assert.Equal(Bob, bark.Seat));
    }

    [Fact]
    public void Distract_drives_to_the_point_makes_a_noise_the_ship_hears_and_comes_back_to_wait()
    {
        var world = Prologue();
        OnDeckA(world);
        // Two of the ship's machines, unarmed: one within hearing of the point, one far off.
        var near = world.AddMachine(Race.Blue, new Vector2(112, 20), "GEO_LOCATOR", null, 1, null);
        var far = world.AddMachine(Race.Blue, new Vector2(56, 74), "GEO_LOCATOR", null, 1, null);
        Run(world, 1);
        var back = Hero(world, Ike).Position;
        var point = new Vector2(104, 26);

        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Distract, point, Ike)));
        Assert.Contains(StoryMarks.Distracting(Ike), world.Story!.Flags);
        Assert.Equal(CompanionOrder.Follow, world.Story.Crew.Of(Bob).Order);

        Assert.True(RunUntil(world, () => world.Story.Crew.Noises.Count > 0, 30));
        var noise = world.Story.Crew.Noises[^1];
        Assert.Equal(Hero(world, Ike).Id, noise.Source);
        Assert.True(Vector2.Distance(noise.Position, point) < 4);
        // The hearing stub of WP-D: the near one hears the honk, the far one does not.
        Assert.Contains(near, world.HeardBy(noise));
        Assert.DoesNotContain(far, world.HeardBy(noise));

        Assert.True(RunUntil(world, () => !world.Story.Flags.Contains(StoryMarks.Distracting(Ike)), 40));
        Assert.Equal(CompanionOrder.Wait, world.Story.Crew.Of(Ike).Order);
        Assert.True(Vector2.Distance(back, Hero(world, Ike).Position) < 5);
        Assert.Contains("P_IKE_DISTRACT", Barked(world));
    }

    [Fact]
    public void A_distraction_to_both_sends_only_the_grunt_nearer_the_point()
    {
        var world = Prologue();
        OnDeckA(world);
        Run(world, 1);

        // Bob stands south of the Locator, Ike north: a point to the south is Bob's.
        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Distract, new Vector2(92, 22))));

        Assert.Equal(CompanionOrder.Distract, world.Story!.Crew.Of(Bob).Order);
        Assert.Equal(CompanionOrder.Follow, world.Story.Crew.Of(Ike).Order);
        Assert.DoesNotContain(StoryMarks.Distracting(Ike), world.Story.Flags);
        Assert.True(RunUntil(world, () => world.Story.Crew.Noises.Count > 0, 30));
    }

    // ---- barricade, switch, pod ----------------------------------------------------------------------

    /// <summary>The core room's south door open, a Reaper in the Vorraum told to go in; with <paramref name="barricade"/> both Grunts first barricade the door.</summary>
    private static (GameWorld World, Machine Reaper) Assault(bool barricade)
    {
        var world = Prologue();
        Assert.True(world.OpenDoor('I'));
        Place(world, Locator, new Vector2(88, 116));
        Place(world, Ike, new Vector2(86, 94));
        Place(world, Bob, new Vector2(90, 94));
        if (barricade)
        {
            world.AssignCompanion(Ike, CompanionTask.Barricade, "I");
            world.AssignCompanion(Bob, CompanionTask.Barricade, "I");
            var standing = RunUntil(world, () => world.Story!.Crew.Of(Ike).Step == CompanionStep.Standing && world.Story.Crew.Of(Bob).Step == CompanionStep.Standing, 90);
            Assert.True(standing, string.Join(" / ", new[] { Ike, Bob }.Select(s => $"{s}: {world.Story!.Crew.Of(s).Step} slot {world.Story.Crew.Of(s).Slot} crate {world.Story.Crew.Of(s).Crate} carry {world.Story.Crew.Of(s).Carrying?.Placement} at {Hero(world, s).Position} order {Hero(world, s).Order}")) + " filled " + string.Join(",", world.BarricadeSlots("I").Select(world.SlotFilled)) + " pile " + world.PileCrates().Count);
            // Two crates shut the doorway, the Grunts on the core's side of them: the pile is out of their reach from there, so
            // they stand (before, the way handed them the pile's crates across their own barricade and they went through it).
            var filled = world.BarricadeSlots("I").Count(world.SlotFilled);
            Assert.Equal(2, filled);
            Assert.Equal(4 - filled, world.PileCrates().Count);
            Assert.All(new[] { Ike, Bob }, seat => Assert.True(Hero(world, seat).Position.Y > 100.5f, $"{seat} at {Hero(world, seat).Position}"));
            // Each stands before the crates, close to a slot.
            Assert.All(new[] { Ike, Bob }, seat => Assert.True(world.BarricadeSlots("I").Any(slot => Vector2.Distance(slot.Position, Hero(world, seat).Position) < 6), $"{seat} at {Hero(world, seat).Position}"));
        }
        else
            Run(world, 1);
        // An unarmed one, so that the test is about the way and not a fight.
        var reaper = world.AddMachine(Race.Blue, new Vector2(88, 92), "AGGRESSOR", "ASSASSIN", 2, null);
        world.Issue(new MoveCommand(Race.Blue, [reaper.Id], new Vector2(88, 112)));
        Run(world, 20);
        return (world, reaper);
    }

    [Fact]
    public void A_barricade_of_crates_and_grunts_keeps_a_reaper_out_of_the_core()
    {
        var (open, through) = Assault(barricade: false);
        Assert.True(through.Position.Y > 103, $"without a barricade the Reaper comes in ({through.Position})");

        var (world, reaper) = Assault(barricade: true);
        Assert.True(reaper.Position.Y < 99.5f, $"the barricade holds ({reaper.Position})");
        Assert.Contains("P_IKE_BARRICADE", Barked(world));
        Assert.Contains("P_BOB_BARRICADE", Barked(world));
        Assert.NotEqual(open.Checksum(), world.Checksum());
    }

    [Fact]
    public void A_grunt_holds_a_gate_switch_by_standing_on_it()
    {
        var world = Prologue();
        Place(world, Locator, new Vector2(180, 100));
        Place(world, Ike, new Vector2(180, 120));
        Place(world, Bob, new Vector2(182, 104));
        Run(world, 1);
        Assert.False(world.SwitchHeld("switch_g1"));

        world.AssignCompanion(Ike, CompanionTask.HoldSwitch, "switch_g1");
        Assert.True(RunUntil(world, () => world.SwitchHeld("switch_g1"), 20));
        Run(world, 2);
        Assert.Contains("P_IKE_GATE", Barked(world));
        Assert.IsType<StandGroundOrder>(Hero(world, Ike).Order);
        Assert.False(world.SwitchHeld("switch_g2"));

        // The Locator's next order takes it off the switch.
        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Follow, Vector2.Zero)));
        Assert.True(RunUntil(world, () => !world.SwitchHeld("switch_g1"), 20));
    }

    [Fact]
    public void The_grunts_follow_the_locator_into_the_pod_once_it_opens_the_hatch()
    {
        var world = Prologue();
        Place(world, Locator, new Vector2(242, 27));
        Place(world, Ike, new Vector2(220, 40));
        Place(world, Bob, new Vector2(226, 42));
        Run(world, 1);
        Assert.False(world.CompanionsAboard());

        Assert.Equal("launch clearance denied", Do(world, new StoryUseCommand(Race.Red, "pod_3_hatch")));
        world.Story!.Hacks.Done.Add("pod_3_hatch");
        Assert.Null(Do(world, new StoryUseCommand(Race.Red)));
        Assert.Contains(StoryMarks.Done("pod_3_hatch"), world.Story.Flags);

        Assert.True(RunUntil(world, world.CompanionsAboard, 30));
        Run(world, 1);
        Assert.Contains("P_IKE_POD", Barked(world));
        Assert.Contains("P_BOB_POD", Barked(world));
    }

    // ---- ram ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_ram_hurts_throws_back_and_stuns_so_the_enemy_holds_its_fire()
    {
        var world = Prologue(ike: PlayerKind.Human);
        OnDeckA(world, new Vector2(70, 26));
        Place(world, Ike, new Vector2(90, 26));
        var foe = world.AddMachine(Race.Blue, new Vector2(94.5f, 26), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        // Facing the Grunt, so that its gun bears.
        foe.Facing = MathF.PI;
        world.Issue(new StandGroundCommand(Race.Blue, [foe.Id]));
        Run(world, 0.5f);

        world.Issue(new StoryRamCommand(Race.Red));
        world.Step();
        Assert.Equal("name the Grunt that rams", world.Rejected.FirstOrDefault()?.Reason);
        Assert.Equal("only a Grunt rams", Do(world, new StoryRamCommand(Race.Red, Seat: Locator), Locator));
        var (before, at) = (foe.HitPoints + foe.Armour, foe.Position);
        Assert.Null(Do(world, new StoryRamCommand(Race.Red), Ike));

        Assert.True(foe.HitPoints + foe.Armour < before);
        Assert.True(Vector2.Distance(foe.Position, Hero(world, Ike).Position) > Vector2.Distance(at, new Vector2(90, 26)) - 3);
        Assert.True(foe.Position.X > at.X, $"thrown back along the ram: {at} -> {foe.Position}, Ike {Hero(world, Ike).Position}, order {foe.Order}");
        Assert.True(world.Stunned(foe));
        Assert.Equal("that is not ready yet", Do(world, new StoryRamCommand(Race.Red), Ike));

        // While stunned (1.5 s from the ram, two ticks of which are gone) it fires at nobody; after, it does again.
        var fired = new List<GameEvent>();
        for (var i = 0; i < GameWorld.StunSeconds * GameWorld.TicksPerSecond - 2; i++)
        {
            Assert.True(world.Stunned(foe));
            world.Step();
            fired.AddRange(world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == foe.Id));
        }
        Assert.Empty(fired);
        Assert.False(world.Stunned(foe));
        Assert.True(RunUntil(world, () => world.Events.Any(e => e.Kind == GameEventKind.Fired && e.Actor == foe.Id), 5));
    }

    [Fact]
    public void The_computers_grunt_rams_an_enemy_that_fires_at_the_trio()
    {
        var world = Prologue();
        OnDeckA(world, new Vector2(80, 26));
        var foe = world.AddMachine(Race.Blue, new Vector2(88, 26), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Issue(new StandGroundCommand(Race.Blue, [foe.Id]));

        var fired = 0;
        var stunned = RunUntil(world, () =>
        {
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == foe.Id);
            return world.Stunned(foe);
        }, 15);
        Assert.True(stunned, $"fired {fired}, foe {foe.Position} {foe.HitPoints} {foe.Order}; " + string.Join(" / ", new[] { Ike, Bob }.Select(s =>
            $"{s}: {Hero(world, s).Position} {Hero(world, s).Order} down {world.CompanionDown(s)}")));
        Assert.Contains(Barked(world), line => line is "P_IKE_RAM" or "P_BOB_RAM");
    }

    // ---- downed and revived ------------------------------------------------------------------------------

    [Fact]
    public void A_grunt_is_never_destroyed_it_lies_downed_and_nobody_fires_at_it()
    {
        var world = Prologue();
        OnDeckA(world);
        Run(world, 1);
        var grunt = Hero(world, Ike);
        var id = grunt.Id;

        world.Damage(grunt, 100000, Race.Blue);
        Run(world, 1);

        Assert.Same(grunt, world.HeroOf(Ike));
        Assert.Equal(id, grunt.Id);
        Assert.Equal(1, grunt.HitPoints);
        Assert.True(world.CompanionDown(Ike));
        Assert.Contains(StoryMarks.Down(Ike), world.Story!.Flags);
        Assert.Contains("P_IKE_DOWN", Barked(world));
        Assert.Equal(Order.Idle, grunt.Order);
        // No target, no hurt, and the Locator's orders pass it by.
        var foe = world.AddMachine(Race.Blue, grunt.Position + new Vector2(6, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.False(world.CanFireAt(foe, grunt));
        world.Damage(grunt, 500, Race.Blue);
        Assert.Equal(1, grunt.HitPoints);
        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero)));
        Assert.Equal(CompanionOrder.Follow, world.Story.Crew.Of(Ike).Order);
        Assert.Null(world.Story.Ending);
    }

    [Fact]
    public void The_locator_puts_a_downed_grunt_together_by_holding_use_and_a_hit_starts_it_over()
    {
        var world = Prologue();
        OnDeckA(world);
        Run(world, 1);
        var grunt = Hero(world, Ike);
        world.Damage(grunt, 100000, Race.Blue);
        Run(world, 0.5f);
        Place(world, Locator, grunt.Position + new Vector2(3, 0));
        Assert.Equal($"seat:{Ike.Value}", world.StoryUseTarget(Locator));
        Assert.Equal("only the Locator puts a Grunt together", Do(world, new StoryUseCommand(Race.Red, $"seat:{Ike.Value}"), Bob));

        // Held for 2 s, then hit: it starts again from nothing.
        Assert.Null(Do(world, new StoryUseCommand(Race.Red)));
        Run(world, 2);
        Assert.True(world.Story!.Crew.Of(Ike).ReviveTicks > 30);
        world.Damage(Hero(world, Locator), 1, Race.Blue);
        Assert.Equal(0, world.Story.Crew.Of(Ike).ReviveTicks);
        Run(world, 3.5f);
        Assert.True(world.CompanionDown(Ike));
        // Let go: nothing kept either.
        Assert.Null(Do(world, new StoryUseCommand(Race.Red, Held: false)));
        Assert.Equal(0, world.Story.Crew.Of(Ike).ReviveTicks);

        Assert.Null(Do(world, new StoryUseCommand(Race.Red)));
        Run(world, 3.8f);
        Assert.True(world.CompanionDown(Ike));
        Run(world, 0.3f);
        Assert.False(world.CompanionDown(Ike));
        Assert.Same(grunt, world.HeroOf(Ike));
        Assert.Equal(grunt.MaxHitPoints / 2, grunt.HitPoints);
        Assert.Contains("P_IKE_UP", Barked(world));
        Assert.Empty(world.Story.Crew.Holding);
        // Back on its feet it follows again.
        Run(world, 1);
        Assert.Equal(new FollowOrder(Hero(world, Locator).Id), grunt.Order);
    }

    [Fact]
    public void The_repair_station_mends_the_trio_and_brings_a_downed_grunt_back()
    {
        var world = Prologue(bob: PlayerKind.Human);
        Place(world, Locator, new Vector2(226, 108));
        Place(world, Ike, new Vector2(230, 108));
        Place(world, Bob, new Vector2(212, 106));
        Run(world, 0.5f);
        var locator = Hero(world, Locator);
        world.Damage(locator, locator.MaxHitPoints / 2 + locator.Armour, Race.Blue);
        var hurt = locator.HitPoints;
        world.Damage(Hero(world, Ike), 100000, Race.Blue);
        world.Damage(Hero(world, Bob), 100000, Race.Blue);
        Assert.True(world.CompanionDown(Ike) && world.CompanionDown(Bob));

        Run(world, 2.9f);
        Assert.True(world.CompanionDown(Ike));
        Run(world, 0.2f);
        Assert.False(world.CompanionDown(Ike));
        // Out of the station, Bob lies on; the Locator in it mends.
        Assert.True(world.CompanionDown(Bob));
        Assert.True(locator.HitPoints > hurt);
        // A downed Grunt's person can look about but not drive.
        world.Issue(new DriveCommand(Race.Red, Hero(world, Bob).Id, true, 1) { Issuer = Bob });
        var at = Hero(world, Bob).Position;
        Run(world, 1);
        Assert.True(Vector2.Distance(at, Hero(world, Bob).Position) < 0.01f);
    }

    // ---- dormant -----------------------------------------------------------------------------------------

    [Fact]
    public void A_dormant_grunt_hangs_inert_in_its_clamp_until_its_terminal_is_hacked()
    {
        var world = Prologue();
        world.SetCompanionDormant(Ike, true);
        var grunt = Hero(world, Ike);
        var at = grunt.Position;
        Run(world, 3);

        Assert.Equal(at, grunt.Position);
        Assert.Equal(Order.Idle, grunt.Order);
        var foe = world.AddMachine(Race.Blue, at + new Vector2(5, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.False(world.CanFireAt(foe, grunt));
        var hit = grunt.HitPoints;
        world.Damage(grunt, 50, Race.Blue);
        Assert.Equal(hit, grunt.HitPoints);
        Assert.Equal("no Grunt takes orders now", Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero, Ike)));
        world.Damage(foe, 100000, null);

        world.Story!.Hacks.Done.Add("terminal_ike");
        Run(world, 1);
        Assert.False(world.CompanionDormant(Ike));
        Assert.Equal(new FollowOrder(Hero(world, Locator).Id), grunt.Order);
        Assert.Contains("P_IKE_WAKE", Barked(world));
    }

    // ---- seats ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_person_who_takes_a_bots_grunt_over_keeps_the_actor_and_on_leaving_the_computer_goes_on_with_its_order()
    {
        var world = Prologue();
        OnDeckA(world);
        Run(world, 1);
        Assert.Null(Do(world, new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero, Ike)));
        var grunt = Hero(world, Ike);
        world.Damage(grunt, 100000, Race.Blue);

        world.ChangePresence([new PresenceChange(Ike, Away: false, Joins: "Zoe")]);
        Run(world, 1);
        Assert.Same(grunt, world.HeroOf(Ike));
        Assert.True(world.CompanionDown(Ike));
        Assert.False(world.StoryBotPlays(Ike));
        var barks = world.Story!.Barks.Count;
        Place(world, Locator, grunt.Position + new Vector2(3, 0));
        Assert.Null(Do(world, new StoryUseCommand(Race.Red)));
        Run(world, 4.5f);
        Assert.False(world.CompanionDown(Ike));
        // Zoe's Grunt says nothing over the radio.
        Assert.Equal(barks, world.Story.Barks.Count);

        world.ChangePresence([new PresenceChange(Ike, Away: true)]);
        Run(world, 1);
        Assert.Same(grunt, world.HeroOf(Ike));
        Assert.True(world.StoryBotPlays(Ike));
        Assert.Equal(CompanionOrder.Wait, world.Story.Crew.Of(Ike).Order);
        Assert.IsType<StandGroundOrder>(grunt.Order);
    }

    // ---- determinism and the wire ---------------------------------------------------------------------------

    private static List<ulong> Scripted(int seed)
    {
        var world = Prologue(seed: seed);
        OnDeckA(world);
        var foe = world.AddMachine(Race.Blue, new Vector2(98, 26), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Issue(new StandGroundCommand(Race.Blue, [foe.Id]));
        var sums = new List<ulong>();
        for (var tick = 0; tick < 600; tick++)
        {
            if (tick == 100)
                world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Distract, new Vector2(110, 20), Bob) { Issuer = Locator });
            if (tick == 300)
                world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero) { Issuer = Locator });
            if (tick == 400)
                world.Issue(new StoryUseCommand(Race.Red) { Issuer = Locator });
            world.Step();
            if (tick % 50 == 49)
                sums.Add(world.Checksum());
        }
        return sums;
    }

    [Fact]
    public void Two_runs_of_the_companions_keep_the_same_checksums()
    {
        Assert.Equal(Scripted(3), Scripted(3));
    }

    [Fact]
    public void The_companions_commands_go_over_the_wire()
    {
        Command[] commands =
        [
            new StoryOrderCommand(Race.Red, CompanionOrder.Distract, new Vector2(12.5f, -3), Bob),
            new StoryRamCommand(Race.Red, 42, Ike),
            new StoryUseCommand(Race.Red, "switch_g1", Held: false),
        ];
        foreach (var command in commands)
            Assert.Equal(command, JsonSerializer.Deserialize<Command>(JsonSerializer.Serialize(command, NetJson.Options), NetJson.Options));
    }

    [Fact]
    public void Every_bark_has_its_line_in_the_table_with_the_same_english()
    {
        var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.OriginalDataFolder(), "..", "..", "tools", "voice", "lines.json")));
        var lines = table.RootElement.GetProperty("lines").EnumerateArray()
            .ToDictionary(l => l.GetProperty("id").GetString()!, l => (Speaker: l.GetProperty("speaker").GetString(), En: l.GetProperty("en").GetString()));
        foreach (var cue in Enum.GetValues<CompanionBarks.Cue>())
        {
            foreach (var seat in new[] { 1, 2 })
            {
                if (CompanionBarks.Line(cue, seat) is not { } line)
                    continue;
                Assert.True(lines.ContainsKey(line), line);
                Assert.Equal(CompanionBarks.Speaker(seat), lines[line].Speaker);
                Assert.Equal(CompanionBarks.English[line], lines[line].En);
            }
        }
    }
}
