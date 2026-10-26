using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Vortex, punch, ion cannon and nuclear missile, with the original radii and damage.</summary>
public class AreaWeaponTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch() =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7);

    private static WeaponSpec Weapon(string name) => DataLoader.Value.Parm.Weapons.Single(w => w.TypeName == name);

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    // An open patch of m_desert, away from both bases.
    private static readonly Vector2 Field = new(800, 800);

    [Fact]
    public void A_vortex_destroys_everything_in_its_40_metre_radius_and_nothing_beyond()
    {
        var world = NewMatch();
        var near = world.AddMachine(Race.Blue, Field + new Vector2(25, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var own = world.AddMachine(Race.Red, Field + new Vector2(0, 30), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var far = world.AddMachine(Race.Blue, Field + new Vector2(70, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        world.StartBlast(Weapon("VORTEX"), Field, Race.Red);
        Run(world, 4);

        Assert.True(near.IsDead);
        Assert.True(own.IsDead);
        Assert.Equal(far.MaxHitPoints, far.HitPoints);
        Assert.Empty(world.Blasts);
    }

    [Fact]
    public void A_punch_hits_in_front_and_spares_the_sides_and_the_puncher()
    {
        var world = NewMatch();
        var puncher = world.AddMachine(Race.Red, Field, "CONSTRUCTOR", "DOZER", 1, null);
        var front = world.AddMachine(Race.Blue, Field + new Vector2(20, 0), "CONSTRUCTOR", "DOZER", 1, null);
        var side = world.AddMachine(Race.Blue, Field + new Vector2(0, 20), "CONSTRUCTOR", "DOZER", 1, null);

        world.StartBlast(Weapon("GORILLA_PUNCH"), puncher.Position, Race.Red, direction: new Vector2(1, 0));
        Run(world, 2);

        Assert.True(front.IsDead);
        Assert.Equal(side.MaxHitPoints, side.HitPoints);
        Assert.Equal(puncher.MaxHitPoints, puncher.HitPoints);
    }

    [Fact]
    public void Researching_the_ion_cannon_arms_pods_which_charge_before_firing()
    {
        var world = NewMatch();
        var pod = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        var target = world.AddMachine(Race.Blue, Field, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        world.Races[Race.Red].Researched.Add(GameWorld.IonCannonResearch);
        world.Step();
        var cannon = Assert.Single(pod.Weapons);
        Assert.Equal("ION_ORBITAL_CANNON", cannon.Spec.TypeName);

        world.Issue(new StrikeCommand(Race.Red, pod.Id, Field));
        world.Step();
        Assert.Contains(world.Rejected, r => r.Reason.Contains("charging"));

        cannon.Reload = 0;
        world.Issue(new StrikeCommand(Race.Red, pod.Id, Field));
        Run(world, 5);

        Assert.True(target.IsDead);
        Assert.InRange(cannon.Reload, 230, 240);
    }

    [Fact]
    public void A_nuclear_missile_flies_then_blasts_with_damage_falling_off()
    {
        var world = NewMatch();
        var silo = world.AddConstruction(Race.Red, new Vector2(500, 300), "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: true);
        var missile = Assert.Single(silo.Weapons);
        Assert.Equal(360, missile.Reload);
        var centre = world.AddMachine(Race.Blue, Field, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var edge = world.AddMachine(Race.Blue, Field + new Vector2(150, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var outside = world.AddMachine(Race.Blue, Field + new Vector2(260, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        missile.Reload = 0;
        world.Issue(new StrikeCommand(Race.Red, silo.Id, Field));
        var flight = Vector2.Distance(silo.Position, Field) / missile.Spec.ProjectileSpeed;
        Run(world, flight - 1);
        Assert.Equal(centre.MaxHitPoints, centre.HitPoints);

        Run(world, 6);
        Assert.True(centre.IsDead);
        // Beyond the main radius (110 m) only the 5-point flash burn lands; outside 215 m nothing.
        Assert.True(edge.HitPoints + edge.Armour < edge.MaxHitPoints + edge.MaxArmour);
        Assert.False(edge.IsDead);
        Assert.Equal(outside.MaxHitPoints, outside.HitPoints);
        Assert.Equal(outside.MaxArmour, outside.Armour);
    }
}
