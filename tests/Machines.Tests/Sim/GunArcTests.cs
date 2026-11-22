using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The arc of a sighted hero's guns (GameWorld.GunArc.cs): a hero without an upper body keeps its hull while where its
/// driver looks lies within its weapon's DEVIATION_ANGLES, the weapon angles and its rounds go there; past the arc they go
/// along its edge. Heroes with an upper body, other matches and recordings from before keep what they had.
/// </summary>
public class GunArcTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private const float Degree = MathF.PI / 180;

    private static float Wrap(float angle) => MathF.IEEERemainder(angle, 2 * MathF.PI);

    private static float Bearing(Vector2 from, Vector2 to) => MathF.Atan2(to.Y - from.Y, to.X - from.X);

    private static Vector2 Along(Actor from, float bearing, float metres) => from.Position + new Vector2(MathF.Cos(bearing), MathF.Sin(bearing)) * metres;

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    /// <summary>A run held in its preparation, its hero out of the hangar on <paramref name="chassis"/>, facing away from its pod.</summary>
    private static (GameWorld World, Machine Hero, float Out) Run(string chassis = "grunt1", bool arcs = true)
    {
        var world = WiredHeroTests.InHangar();
        world.Options = world.Options with { HeroGunArc = arcs };
        world.Wired!.Countdown = 100_000;
        if (chassis != "grunt1")
            WiredHeroTests.Board(world, chassis);
        world.Issue(new WiredHangarCommand(Race.Red, false));
        for (var i = 0; i < 41; i++)
            world.Step();
        var hero = world.HeroOf(Race.Red)!;
        var away = Vector2.Normalize(hero.Position - world.WiredPod(Race.Red)!.Position);
        hero.Facing = MathF.Atan2(away.Y, away.X);
        return (world, hero, hero.Facing);
    }

    /// <summary>One tick at the wheel without turning; the hero's rounds fired in it.</summary>
    private static List<GameEvent> Drive(GameWorld world, Machine hero, float yaw, Vector2? aim = null, float? height = null)
    {
        world.Issue(new DriveCommand(hero.Race, hero.Id, true, 0, 0, aim, aim is null ? 0 : 1, AimHeight: height, Yaw: yaw));
        world.Step();
        return world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id).ToList();
    }

    /// <summary>The trigger held on <paramref name="aim"/> until a few rounds are out: where they landed.</summary>
    private static List<Vector2> Rounds(GameWorld world, Machine hero, float yaw, Vector2 aim, int count = 3)
    {
        var marks = new List<Vector2>();
        for (var i = 0; i < 400 && marks.Count < count; i++)
            marks.AddRange(Drive(world, hero, yaw, aim, world.AimPoint(hero).Z).Select(e => Flat(e.Mark!.Value)));
        Assert.True(marks.Count >= count, $"{marks.Count} rounds");
        return marks;
    }

    private static float Spread(GameWorld world, Machine hero) => (world.AimSpread(hero) + 0.5f) * Degree;

    [Fact]
    public void A_weapons_arc_is_95_percent_of_its_deviation_angles_tangent()
    {
        var world = WiredHeroTests.InHangar();
        float Expected(float degrees) => MathF.Atan(0.95f * MathF.Tan(degrees * Degree));
        Assert.Equal(Expected(30), world.ArcOf(Data.Parm.Weapon("BOLTER")), 4);
        Assert.Equal(Expected(15), world.ArcOf(Data.Parm.Weapon("PLASMA_RIFLE")), 4);
        Assert.Equal(Expected(22), world.ArcOf(Data.Parm.Weapon("MULTI_LAUNCHER7")), 4);
        // A weapon that names none angles 10 degrees, as MachPhysWeaponData's default.
        var plain = Data.Parm.Weapons.First(w => w.Presentation.All(p => p.Keyword != "DEVIATION_ANGLES"));
        Assert.Equal(Expected(10), world.ArcOf(plain), 4);
    }

    [Fact]
    public void Within_the_arc_the_hull_stays_and_the_rounds_go_where_the_driver_looks()
    {
        var (world, hero, way) = Run();
        Assert.False(hero.CanTurnUpperBody);
        var arc = world.GunArcOf(hero);
        Assert.Equal(world.ArcOf(hero.Weapons[0].Spec), arc);
        Assert.True(arc > 10 * Degree, $"arc {arc / Degree} degrees");
        var yaw = way + arc * 0.7f;
        var aim = Along(hero, yaw, 30);

        foreach (var mark in Rounds(world, hero, yaw, aim))
            Assert.True(MathF.Abs(Wrap(Bearing(hero.Position, mark) - yaw)) <= Spread(world, hero), $"mark at {Bearing(hero.Position, mark)}, looked at {yaw}");
        Assert.Equal(way, hero.Facing);
        Assert.True(world.OnSight(hero.Id));
        Assert.Equal(arc * 0.7f, world.GunAngleOf(hero), 4);
        Assert.True(MathF.Abs(Wrap(world.GunHeading(hero) - yaw)) <= WiredBalance.GunPlay);
    }

    [Fact]
    public void Past_the_arc_the_rounds_go_along_its_edge()
    {
        var (world, hero, way) = Run();
        var arc = world.GunArcOf(hero);
        var yaw = way - arc - 25 * Degree;

        foreach (var mark in Rounds(world, hero, yaw, Along(hero, yaw, 30)))
            Assert.True(MathF.Abs(Wrap(Bearing(hero.Position, mark) - (way - arc))) <= Spread(world, hero), $"mark at {Bearing(hero.Position, mark)}, edge at {way - arc}");
        Assert.False(world.OnSight(hero.Id));
        Assert.Equal(-arc, world.GunAngleOf(hero), 5);
    }

    [Fact]
    public void Without_the_arcs_a_hull_gun_fires_only_along_the_hull_as_before()
    {
        var (world, hero, way) = Run(arcs: false);
        Assert.Equal(0, world.GunArcOf(hero));
        var yaw = way + 12 * Degree;

        foreach (var mark in Rounds(world, hero, yaw, Along(hero, yaw, 30)))
            Assert.True(MathF.Abs(Wrap(Bearing(hero.Position, mark) - way)) <= Spread(world, hero), $"mark at {Bearing(hero.Position, mark)}, hull at {way}");
        Assert.False(world.OnSight(hero.Id));
        Assert.Equal(0, world.GunAngleOf(hero));
    }

    [Fact]
    public void A_turret_has_no_arc_and_turns_as_before()
    {
        var (world, hero, way) = Run("grunt2");
        Assert.True(hero.CanTurnUpperBody);
        Assert.Equal(0, world.GunArcOf(hero));
        Drive(world, hero, way + 9 * Degree);
        Assert.Equal(0, world.GunAngleOf(hero));
        Assert.Equal(9 * Degree, world.HeadOf(hero.Id), 5);
    }

    [Fact]
    public void Only_a_wired_run_or_a_battle_has_the_arc()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7, new MatchOptions());
        var grunt = world.AddMachine(Race.Red, new Vector2(500, 500), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.True(world.Options.HeroGunArc);
        Assert.Equal(0, world.GunArcOf(grunt));
    }

    [Fact]
    public void A_recording_from_before_plays_without_the_arcs()
    {
        var match = new MatchInfo("m_desert", "m_desert", new MatchOptions { Wired = new WiredOptions() }, 7, 4,
            [new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false)]);
        var folder = Path.Combine(Path.GetTempPath(), "gunarc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "now" + Replay.Extension);
            new Replay(match, [], 10).Save(path);
            Assert.True(Replay.Load(path).Match.Options.HeroGunArc);

            // The same recording stamped with protocol 14 or 15 (fitted bodies), the last before the arcs, whose options said nothing of them.
            foreach (var format in new[] { 14, 15 })
            {
                var old = Path.Combine(folder, $"old{format}" + Replay.Extension);
                File.WriteAllBytes(old, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Replay(match, [], 10, Format: format), NetJson.Options));
                var loaded = Replay.Load(old).Match.Options;
                Assert.False(loaded.HeroGunArc);
                Assert.True(loaded.FittedBodies);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
