using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Active artefacts: shot at on command, destroyed with their wreckage, and linked by ONDESTROY.</summary>
public class ArtefactTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Control(MatchOptions? options = null) =>
        MatchSetup.Create(DataLoader.Value, "control", "control",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player }, seed: 5, options);

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    [Fact]
    public void Only_active_artefacts_are_placed_with_their_stats()
    {
        var world = Control();
        Assert.Equal(5, world.Artefacts.Count());
        var control = world.Artefacts.Single(a => a.Type.Name == "control");
        Assert.Equal((8000, 80000), (control.HitPoints, control.Armour));
        Assert.Equal(0, control.SubType);
        Assert.All(world.Artefacts.Where(a => a.Type.Name == "cool1"), a => Assert.Equal(1, a.SubType));
    }

    [Fact]
    public void A_destroyed_cooler_weakens_the_control_centre_and_leaves_wreckage()
    {
        var world = Control();
        var control = world.Artefacts.Single(a => a.Type.Name == "control");
        var cooler = world.Artefacts.First(a => a.Type.Name == "cool1");
        world.Damage(cooler, 100000, Race.Red);
        world.Step();

        Assert.DoesNotContain(cooler, world.Artefacts);
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.Destroyed && e.Actor == cooler.Id && e.Detail == "ARTEFACT");
        Assert.Contains(world.Debris, d => d.Position == cooler.Position && d.Quantity == 10);
        Assert.Equal((7999, 80000 - 19990), (control.HitPoints, control.Armour));
    }

    [Fact]
    public void Machines_attack_an_artefact_they_are_told_to()
    {
        var world = Control();
        var cooler = world.Artefacts.First(a => a.Type.Name == "cool1");
        var knights = Enumerable.Range(0, 3)
            .Select(i => world.AddMachine(Race.Red, cooler.Position + new Vector2(30, i * 8 - 8), "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2"))
            .ToList();
        world.Issue(new AttackCommand(Race.Red, knights.Select(k => k.Id).ToList(), cooler.Id));
        Run(world, 5);
        Assert.InRange(cooler.HitPoints, 1, cooler.MaxHitPoints - 1);
        Run(world, 60);
        Assert.True(cooler.IsDead);
        Assert.DoesNotContain(cooler, world.Artefacts);
    }

    [Fact]
    public void Recordings_from_before_keep_artefacts_whole()
    {
        var world = Control(new MatchOptions { DestructibleArtefacts = false });
        var cooler = world.Artefacts.First(a => a.Type.Name == "cool1");
        var knight = world.AddMachine(Race.Red, cooler.Position + new Vector2(30, 0), "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2");
        Assert.False(world.CanAttack(Race.Red, cooler));
        world.Damage(cooler, 100000, Race.Red);
        world.Issue(new AttackCommand(Race.Red, [knight.Id], cooler.Id));
        Run(world, 5);
        Assert.Equal(cooler.MaxHitPoints, cooler.HitPoints);
        Assert.Contains(cooler, world.Artefacts);
    }
}
