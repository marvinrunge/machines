using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

/// <summary>
/// The Thresher's aim in the story (the player's note of 2026-11-22, "Der Thresher sollte in die Ferne schlecht zielen"):
/// caught close, the trio is hit as ever; further off more and more of its bursts go wide, onto the floor and the walls
/// beside the trio. Only the Thresher's, only at the trio, only in the story: a Reaper aims as ever, and outside the story
/// nothing changes.
/// </summary>
public class StoryAimTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld Prologue(int seed = 3) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    // The middle of the Thresher's lane on deck A (R4, 56 m deep), which the deck leaves dark; the deck runs 144 m east-west.
    private static readonly Vector2 Lane = new(140, 52);

    /// <summary>
    /// A hunter of the ship's in the lane facing east, and the Locator (its lamp on, nothing hurts it) that far east of it:
    /// how many rounds the hunter fires at the Locator in so many seconds, how many of them hit it, and how many bursts it
    /// sent wide. The hunter is held where it stands, so the distance stays.
    /// </summary>
    private static (int Fired, int Hit, int Wide) Shoot(float distance, int level = 4, string combo = "LR_PLASMA_RIFLE_X2", int seed = 3, float seconds = 40)
    {
        var world = Prologue(seed);
        var hunter = world.AddMachine(Race.Blue, Lane, "AGGRESSOR", "ASSASSIN", level, combo);
        var hero = world.HeroOf(world.Story!.Locator)!;
        hero.Position = Lane + new Vector2(distance, 0);
        world.Wired!.Heroes[world.Story.Locator].InvulnerableUntil = float.MaxValue;
        world.SetStoryLamp(hero.Id, true);
        var (fired, hit, wide) = (0, 0, 0);
        // A burst is taken once for all the weapons that open fire together: a new one shows as a new FireData.
        var last = hunter.Weapons[0].Burst;
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            hunter.Position = Lane;
            hunter.Facing = 0;
            hero.Position = Lane + new Vector2(distance, 0);
            world.Step();
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == hunter.Id && e.Other == hero.Id);
            hit += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == hero.Id && e.Other == hunter.Id);
            var burst = hunter.Weapons[0].Burst;
            if (burst != last && burst.Wide != 0)
                wide++;
            last = burst;
        }
        return (fired, hit, wide);
    }

    [Fact]
    public void The_chance_of_a_wide_burst_grows_from_nothing_up_close_to_most_at_the_end_of_its_fire()
    {
        Assert.Equal(0, StorySight.WildChance(0));
        Assert.Equal(0, StorySight.WildChance(StorySight.SureAim));
        Assert.Equal(StorySight.WildMiss, StorySight.WildChance(StorySight.WildAim));
        Assert.Equal(StorySight.WildMiss, StorySight.WildChance(StorySight.FireReach + 20));
        Assert.Equal(StorySight.WildAim, StorySight.FireReach);
        for (var d = 0f; d < 60; d += 1)
            Assert.True(StorySight.WildChance(d + 1) >= StorySight.WildChance(d));
        Assert.InRange(StorySight.WildChance(30), 0.4f, 0.7f);
    }

    [Fact]
    public void The_thresher_hits_up_close_and_mostly_misses_at_the_end_of_its_fire()
    {
        var near = Shoot(10);
        var far = Shoot(38);
        output.WriteLine($"10 m: {near}; 38 m: {far}");

        Assert.True(near.Fired >= 20 && far.Fired >= 20, $"{near} {far}");
        Assert.Equal(0, near.Wide);
        Assert.True(near.Hit >= near.Fired * 0.8f, $"near {near}");
        Assert.True(far.Wide > 0);
        Assert.True(far.Hit <= far.Fired * 0.35f, $"far {far}");
    }

    [Fact]
    public void A_wide_round_strikes_the_floor_or_a_wall_beside_the_hero_clear_of_it()
    {
        var world = Prologue(seed: 5);
        var thresher = world.AddMachine(Race.Blue, Lane, "AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");
        var hero = world.HeroOf(world.Story!.Locator)!;
        world.Wired!.Heroes[world.Story.Locator].InvulnerableUntil = float.MaxValue;
        world.SetStoryLamp(hero.Id, true);
        var at = Lane + new Vector2(36, 0);
        var impacts = new List<Vector2>();
        var hits = 0;
        for (var i = 0; i < 40 * GameWorld.TicksPerSecond; i++)
        {
            (thresher.Position, thresher.Facing, hero.Position) = (Lane, 0, at);
            world.Step();
            impacts.AddRange(world.Events.Where(e => e.Kind == GameEventKind.Impact && e.Actor == thresher.Id).Select(e => e.Position));
            hits += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == hero.Id);
        }
        output.WriteLine(string.Join(" ", impacts.Select(p => $"{p.X:0.0},{p.Y:0.0}")));

        // The wide rounds strike the deck (floor or wall), none of them within its burst's reach of the Locator, and on both sides.
        Assert.True(impacts.Count >= 10, $"{impacts.Count} impacts");
        Assert.All(impacts, p => Assert.True(Vector2.Distance(p, at) > hero.Radius + 3, $"{p} too close to {at}"));
        Assert.Contains(impacts, p => p.Y > at.Y);
        Assert.Contains(impacts, p => p.Y < at.Y);
    }

    [Fact]
    public void A_reaper_aims_as_ever_at_every_distance()
    {
        var far = Shoot(38, level: 3);
        output.WriteLine($"Reaper 3 at 38 m: {far}");
        Assert.True(far.Fired >= 20);
        Assert.Equal(0, far.Wide);
    }

    [Fact]
    public void Two_runs_of_the_thresher_firing_wide_are_alike()
    {
        Assert.Equal(Shoot(32, seed: 9, seconds: 20), Shoot(32, seed: 9, seconds: 20));
    }

    [Fact]
    public void Outside_the_story_a_reaper_4_aims_as_ever()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7);
        var field = new Vector2(800, 800);
        var thresher = world.AddMachine(Race.Red, field, "AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");
        var target = world.AddMachine(Race.Blue, field + new Vector2(38, 0), "CONSTRUCTOR", "DOZER", 1, null);
        target.DefCon = DefCon.High;
        target.HitPoints = 1000000;
        world.Issue(new AttackCommand(Race.Red, [thresher.Id], target.Id));
        var (fired, hit) = (0, 0);
        for (var i = 0; i < 30 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            Assert.All(thresher.Weapons, w => Assert.Equal(0, w.Burst.Wide));
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == thresher.Id);
            hit += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == target.Id);
        }
        output.WriteLine($"outside the story at 38 m: fired {fired}, hit {hit}");
        Assert.True(fired >= 20);
        Assert.True(hit >= fired * 0.7f, $"hit {hit} of {fired}");
    }

    [Fact]
    public void The_further_off_the_fewer_of_its_rounds_hit()
    {
        var shares = new[] { 15f, 27, 39 }.Select(d =>
        {
            var (fired, hit, wide) = Shoot(d);
            output.WriteLine($"{d,3} m: fired {fired,3} hit {hit,3} wide bursts {wide}");
            return hit / (float)Math.Max(1, fired);
        }).ToList();

        Assert.True(shares[0] > shares[1] && shares[1] > shares[2], string.Join(" ", shares));
    }
}
