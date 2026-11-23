using System.Numerics;
using System.Reflection;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;
using Machines.Sim.Story;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// The big Grunt of stage 7 (plan stufe7 C16, package G; StoryHeavy): Old Iron, Wired's Grunt 5, with only the twin heavy
/// Bolters, tuned by its part (the armoury's boss 100 + 20 per Bolter up to 140 / 30, a pack heavy Old Iron's 380 / 200),
/// no armour coming back, size class 3 on the deck, its own copy of the stats; and two peers that set it down stay equal.
/// </summary>
public class HeavyGruntTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static PlayerId P(int value) => new(value);

    private static readonly PlayerId Locator = P(0), Ike = P(4), Bob = P(5);

    private static GameWorld Prologue(int seed = 7) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Human, "Anna"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Human, "Ben"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), seed,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    // The lane of deck A, where the trio stands in the stage's other tests; the heavy 40 m on.
    private static readonly Vector2 Lane = new(176, 52), Ahead = new(216, 52);

    [Fact]
    public void It_is_old_iron_with_twin_heavy_bolters()
    {
        var world = Prologue();
        var heavy = StoryHeavy.Spawn(world, Ahead, StoryHeavyPart.Boss);
        Assert.True(StoryHeavy.Is(heavy));
        Assert.Equal(("AGGRESSOR", "GRUNT", 5), (heavy.Spec.Type, heavy.Spec.SubType, heavy.Stats.HardwareLevel));
        Assert.Equal("LR_HEAVY_BOLTER1_X2", heavy.WeaponCombo);
        Assert.Equal([(Mounting.Left, "HEAVY_BOLTER1"), (Mounting.Right, "HEAVY_BOLTER1")],
            heavy.Weapons.Select(w => (w.Mounting, w.Base.TypeName)).OrderBy(w => w.Mounting));
        Assert.Equal(world.Story!.Options.Ship, heavy.Race);
        Assert.Equal(Machines.Sim.Story.Prologue.Hunters, heavy.DefCon);
        Assert.False(StoryHeavy.Is(world.HeroOf(Ike)!));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 120)]
    [InlineData(2, 140)]
    [InlineData(3, 140)]
    public void The_boss_has_a_hundred_plus_twenty_per_bolter_up_to_a_hundred_and_forty(int bolters, int hitPoints)
    {
        var world = Prologue();
        var boss = StoryHeavy.Spawn(world, Ahead, StoryHeavyPart.Boss, bolters);
        Assert.Equal((hitPoints, hitPoints), (boss.HitPoints, boss.MaxHitPoints));
        Assert.Equal((30, 30), (boss.Armour, boss.MaxArmour));
        Assert.Equal(0, boss.Stats.RepairRate);
    }

    [Fact]
    public void A_pack_heavy_keeps_old_irons_toughness_and_none_comes_back()
    {
        var world = Prologue();
        var heavy = StoryHeavy.Spawn(world, Ahead, StoryHeavyPart.Pack);
        Assert.Equal((380, 200), (heavy.MaxHitPoints, heavy.MaxArmour));
        Assert.Equal(0, heavy.Stats.RepairRate);
        heavy.Armour = 50;
        Run(world, 3);
        Assert.Equal(50, heavy.Armour);
    }

    [Fact]
    public void Its_tune_is_its_own_copy_and_the_rest_stays_old_irons()
    {
        var world = Prologue();
        var heavy = StoryHeavy.Spawn(world, Ahead, StoryHeavyPart.Boss, 1);
        var oldIron = world.Data.Parm.Machine("AGGRESSOR GRUNT").Level(5);
        Assert.Equal((380, 200, 1.6f), (oldIron.HitPoints, oldIron.Armour, oldIron.RepairRate));
        Assert.Equal(oldIron with { HitPoints = 120, Armour = 30, RepairRate = 0 }, heavy.Stats);
        // Old Iron's own drive: 9.5 m/s, the boss cruising at 0.45 of it (just under the trio's walk) and charging at all of it.
        Assert.Equal(9.5f, heavy.Stats.Speed);
        Assert.InRange(heavy.Stats.Speed * StoryHeavy.Cruise, 4.2f, 4.35f);
        Assert.Equal(1f, StoryHeavy.Charge);
        Assert.Equal(3.2f, StoryHeavy.MassTonnes);
    }

    [Fact]
    public void A_hurt_heavy_refitted_keeps_its_share()
    {
        var world = Prologue();
        var heavy = StoryHeavy.Spawn(world, Ahead, StoryHeavyPart.Pack);
        heavy.HitPoints = 190;
        StoryHeavy.Fit(world, heavy, StoryHeavyPart.Boss, 2);
        Assert.Equal((70, 140), (heavy.HitPoints, heavy.MaxHitPoints));
        Assert.Throws<ArgumentException>(() => StoryHeavy.Fit(world, world.HeroOf(Ike)!, StoryHeavyPart.Boss));
    }

    [Fact]
    public void It_finds_its_way_as_size_class_two_on_the_deck()
    {
        var world = Prologue();
        var heavy = StoryHeavy.Spawn(world, Ahead, StoryHeavyPart.Pack);
        Assert.InRange(heavy.Radius, 3.0f, 3.2f);
        Assert.Equal(StoryHeavy.SizeClass, GameWorld.MobilityOf(heavy).Size);
        // Its radius's class is the largest, but it squeezes through the deck's gates as class 2 (the armoury's ramp, the lift).
        Assert.Equal(3, Machines.Sim.Navigation.Mobility.SizeOf(heavy.Radius));
        Assert.Equal(2, StoryHeavy.SizeClass);
        Assert.True(world.Nav.IsFree(heavy.Position, GameWorld.MobilityOf(heavy)));
    }

    [Fact]
    public void A_beat_casts_them_in_a_role_each_tuned()
    {
        var world = Prologue();
        var run = world.Story!;
        Act(world, new Then.SpawnHeavy(StoryHeavy.PackRole1, StoryHeavyPart.Pack, ["216,52"]));
        Act(world, new Then.SpawnHeavy(StoryHeavy.BossRole, StoryHeavyPart.Boss, ["236,52"], Bolters: 2));
        var pack = Assert.Single(run.Director.Of(world, StoryHeavy.PackRole1));
        var boss = Assert.Single(run.Director.Of(world, StoryHeavy.BossRole));
        Assert.Equal((380, 140), (pack.MaxHitPoints, boss.MaxHitPoints));
        Assert.True(StoryHeavy.Is(pack) && StoryHeavy.Is(boss));
    }

    [Fact]
    public void Two_peers_that_set_it_down_stay_equal()
    {
        var (one, two) = (Prologue(seed: 11), Prologue(seed: 11));
        foreach (var world in new[] { one, two })
        {
            world.HeroOf(Locator)!.Position = Lane;
            StoryHeavy.Spawn(world, Ahead, StoryHeavyPart.Boss, 1);
            StoryHeavy.Spawn(world, Ahead + new Vector2(0, 14), StoryHeavyPart.Pack);
        }
        var start = one.Checksum();
        for (var second = 0; second < 20; second++)
        {
            Run(one, 1);
            Run(two, 1);
            Assert.Equal(one.Checksum(), two.Checksum());
        }
        Assert.NotEqual(start, one.Checksum());
        var without = Prologue(seed: 11);
        without.HeroOf(Locator)!.Position = Lane;
        Assert.NotEqual(without.Checksum(), start);
    }

    [Fact]
    public void It_attacks_with_its_bolters_but_never_rams_by_itself()
    {
        // Wired's Old Irons dash at what they attack within reach in front of them; the story's runs only by its package's charge.
        var world = Prologue();
        var ike = world.HeroOf(Ike)!;
        ike.Position = Lane;
        var heavy = StoryHeavy.Spawn(world, Lane + new Vector2(10, 0), StoryHeavyPart.Pack);
        heavy.Facing = MathF.PI;
        Assert.False(StoryHeavy.RamsByItself(world, heavy));
        typeof(GameWorld).GetMethod("SendForStory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, [heavy, new AttackOrder(ike.Id), null]);
        var (dashes, rams) = (0, 0);
        for (var i = 0; i < 10 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            dashes += world.Events.Count(e => e.Kind == GameEventKind.WiredDashed);
            rams += world.Events.Count(e => e.Kind == GameEventKind.WiredRammed);
        }
        Assert.Equal((0, 0), (dashes, rams));
        Assert.True(ike.HitPoints < ike.MaxHitPoints, "its bolters hit Ike");
    }

    private static readonly MethodInfo DoAction = typeof(StoryAction).GetMethod("Do", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Act(GameWorld world, StoryAction action) => DoAction.Invoke(action, [world, world.Story!, world.Story!.Director]);
}
