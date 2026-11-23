using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// The story's harm seam (plan stufe7 §4 F item 7, C4; GameWorld.StoryHarm, called from WiredHit): the trio never hurts
/// itself; the Thresher's shots, while a section uses his rule (flag stealth or escape), each take 34 % of the target's hit
/// points with armour ignored, one counted shot every 0.5 s (the rest of a burst glances off), so three down a Grunt;
/// without the flags he hits as before; and two worlds stay in step.
/// </summary>
public class StoryHarmSeamTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    private static GameWorld Unscripted() =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Human, "Anna"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Human, "Ben"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), 7,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    /// <summary>The Thresher cast as the prologue casts him, standing off, his weapons gone so only the test's hits land.</summary>
    private static Machine Thresher(GameWorld world)
    {
        var thresher = world.AddMachine(Race.Blue, world.HeroOf(Locator)!.Position + new Vector2(0, 30), "AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");
        thresher.Weapons.Clear();
        world.Story!.Director.Cast[Prologue.ThresherRole] = [thresher.Id];
        return thresher;
    }

    [Fact]
    public void The_trio_never_hurts_itself()
    {
        var world = Unscripted();
        var (locator, ike) = (world.HeroOf(Locator)!, world.HeroOf(Ike)!);
        var (hp, armour) = (locator.HitPoints, locator.Armour);

        world.Damage(locator, 40, Race.Red, shooter: ike, weapon: "BOLTER");
        world.Damage(ike, 40, Race.Red, shooter: ike, weapon: "BOLTER");

        Assert.Equal((hp, armour), (locator.HitPoints, locator.Armour));
        Assert.Equal(ike.MaxHitPoints, ike.HitPoints);
        Assert.False(world.Story!.Harm.Any);
    }

    [Fact]
    public void Without_a_section_using_it_the_thresher_hits_as_before()
    {
        var world = Unscripted();
        var thresher = Thresher(world);
        var ike = world.HeroOf(Ike)!;
        ike.Armour = 0;

        world.Damage(ike, 3, Race.Blue, shooter: thresher, weapon: "LR_PLASMA_RIFLE_X2");
        world.Damage(ike, 3, Race.Blue, shooter: thresher, weapon: "LR_PLASMA_RIFLE_X2");

        Assert.True(ike.MaxHitPoints - ike.HitPoints <= 6);
        Assert.False(world.Story!.Harm.Any);
    }

    [Theory]
    [InlineData(StoryMarks.Stealth)]
    [InlineData(StoryMarks.Escape)]
    public void Under_the_rule_each_counted_shot_takes_a_third_armour_ignored_and_three_down_a_grunt(string flag)
    {
        var world = Unscripted();
        world.Story!.Flags.Add(flag);
        var thresher = Thresher(world);
        var ike = world.HeroOf(Ike)!;
        var share = (int)MathF.Ceiling(Machines.Sim.Story.StoryHarm.ThresherShare * ike.MaxHitPoints);
        var armour = ike.Armour;
        Assert.True(armour > 0);

        world.Damage(ike, 3, Race.Blue, shooter: thresher, weapon: "LR_PLASMA_RIFLE_X2");
        Assert.Equal(ike.MaxHitPoints - share, ike.HitPoints);
        // The burst's next round in the same moment glances off.
        world.Damage(ike, 3, Race.Blue, shooter: thresher, weapon: "LR_PLASMA_RIFLE_X2");
        Assert.Equal(ike.MaxHitPoints - share, ike.HitPoints);

        Run(world, Machines.Sim.Story.StoryHarm.Gap);
        world.Damage(ike, 3, Race.Blue, shooter: thresher, weapon: "LR_PLASMA_RIFLE_X2");
        Assert.Equal(ike.MaxHitPoints - 2 * share, ike.HitPoints);
        Run(world, Machines.Sim.Story.StoryHarm.Gap);
        world.Damage(ike, 3, Race.Blue, shooter: thresher, weapon: "LR_PLASMA_RIFLE_X2");
        world.Step();

        Assert.True(world.CompanionDown(Ike));
        Assert.True(world.Story.Harm.Any);
    }

    [Fact]
    public void The_seam_keeps_two_worlds_in_step()
    {
        var (a, b) = (Unscripted(), Unscripted());
        foreach (var world in new[] { a, b })
        {
            world.Story!.Flags.Add(StoryMarks.Escape);
            var thresher = Thresher(world);
            for (var i = 0; i < 4; i++)
            {
                world.Damage(world.HeroOf(Bob)!, 3, Race.Blue, shooter: thresher, weapon: "LR_PLASMA_RIFLE_X2");
                Run(world, 0.3f);
            }
        }
        Assert.Equal(a.Checksum(), b.Checksum());
    }
}
