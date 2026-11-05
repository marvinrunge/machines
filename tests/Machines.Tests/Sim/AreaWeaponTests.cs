using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
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
    public void A_gorilla_punch_lands_when_its_fists_come_down()
    {
        var world = NewMatch();
        var gorilla = world.AddMachine(Race.Red, Field, "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");
        var target = world.AddMachine(Race.Blue, Field + new Vector2(6, 0), "CONSTRUCTOR", "DOZER", 1, null);
        // At high initiative it holds still instead of running from the gorilla (MachLogMachine::checkAndDoOnDefCon).
        target.DefCon = DefCon.High;

        world.Issue(new AttackCommand(Race.Red, [gorilla.Id], target.Id));
        float? punched = null, landed = null;
        for (var i = 0; i < 20 * GameWorld.TicksPerSecond && landed is null; i++)
        {
            world.Step();
            foreach (var e in world.Events.Where(e => e.Detail == "GORILLA_PUNCH"))
            {
                if (e.Kind == GameEventKind.Fired)
                    punched ??= world.Time;
                if (e.Kind == GameEventKind.Blast)
                    landed ??= world.Time;
            }
        }

        Assert.NotNull(punched);
        Assert.NotNull(landed);
        Assert.InRange(landed!.Value - punched!.Value, Blast.PunchLanding - 0.01f, Blast.PunchLanding + 0.2f);
    }

    [Fact]
    public void A_gorilla_stops_to_punch_and_stands_until_it_has_stood_up_again()
    {
        var world = NewMatch();
        var gorilla = world.AddMachine(Race.Red, Field, "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");
        var target = world.AddMachine(Race.Blue, Field + new Vector2(70, 0), "CONSTRUCTOR", "DOZER", 1, null);
        target.DefCon = DefCon.High;
        // Its launchers' whole bursts would make short work of it on the way.
        target.HitPoints = 1000000;

        // It walks up to the target and punches as soon as it is within reach, on its way still.
        world.Issue(new AttackCommand(Race.Red, [gorilla.Id], target.Id));
        var steps = 0;
        while (!world.Events.Any(e => e.Kind == GameEventKind.Fired && e.Detail == "GORILLA_PUNCH") && steps++ < 20 * GameWorld.TicksPerSecond)
            world.Step();
        Assert.True(world.Busy(gorilla));
        var punchedAt = (world.Time, gorilla.Position, gorilla.Facing);

        while (world.Time < punchedAt.Time + GameWorld.PunchBusy - 0.05f)
        {
            world.Step();
            Assert.Equal(punchedAt.Position, gorilla.Position);
            Assert.Equal(punchedAt.Facing, gorilla.Facing);
        }
        Run(world, 0.5f);
        Assert.False(world.Busy(gorilla));
    }

    [Fact]
    public void A_gorilla_on_its_way_somewhere_does_not_punch_what_it_passes()
    {
        var world = NewMatch();
        var gorilla = world.AddMachine(Race.Red, Field, "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");
        // It keeps to its way rather than taking on what it meets.
        gorilla.DefCon = DefCon.High;
        var passed = world.AddMachine(Race.Blue, Field + new Vector2(30, 15), "CONSTRUCTOR", "DOZER", 1, null);
        passed.DefCon = DefCon.High;

        world.Issue(new MoveCommand(Race.Red, [gorilla.Id], Field + new Vector2(80, 0)));
        var punches = 0;
        for (var i = 0; i < 8 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            punches += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Detail == "GORILLA_PUNCH");
        }

        Assert.Equal(0, punches);
        Assert.True(Vector2.Distance(gorilla.Position, Field) > 40);
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
    public void What_the_ion_beam_kills_is_vaporised()
    {
        var world = NewMatch();
        var target = world.AddMachine(Race.Blue, Field, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.StartBlast(Weapon("ION_ORBITAL_CANNON"), Field, Race.Red);
        var gone = false;
        for (var i = 0; i < 5 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            gone |= world.Events.Any(e => e.Kind == GameEventKind.Destroyed && e.Actor == target.Id && e.Detail == "ION_ORBITAL_CANNON");
        }
        Assert.True(gone);
    }

    private static bool Told(GameWorld world, Race race, string mail) =>
        world.Events.Any(e => e.Kind == GameEventKind.VoiceMail && e.Other == (int)race && e.Detail == mail);

    [Fact]
    public void An_ion_cannon_reports_when_it_is_acquired_when_charged_and_when_it_fires()
    {
        var world = NewMatch();
        var pod = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        world.AddConstruction(Race.Blue, new Vector2(900, 900), "BEACON", null, 3, 0, complete: true);

        world.Races[Race.Red].Researched.Add(GameWorld.IonCannonResearch);
        world.Step();
        Assert.True(Told(world, Race.Red, "VID_POD_ION_CANNON_ACQUIRED"));
        Assert.True(Told(world, Race.Blue, "VID_POD_ENEMY_ION_CANNON_ONLINE"));

        // Charged once, as it comes ready, and not again while it waits.
        var cannon = Assert.Single(pod.Weapons);
        cannon.Reload = 0.05f;
        var charged = 0;
        for (var i = 0; i < GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            charged += world.Events.Count(e => e.Kind == GameEventKind.VoiceMail && e.Other == (int)Race.Red && e.Detail == "VID_POD_ION_CANNON_CHARGED");
        }
        Assert.Equal(1, charged);

        world.Issue(new StrikeCommand(Race.Red, pod.Id, Field));
        world.Step();
        Assert.True(Told(world, Race.Blue, "VID_POD_ENEMY_ION_CANNON"));
        Assert.False(Told(world, Race.Red, "VID_POD_ENEMY_ION_CANNON"));
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

    [Fact]
    public void A_nuclear_missile_is_in_flight_from_its_silo_until_its_bomb_goes_off()
    {
        var world = NewMatch();
        var silo = world.AddConstruction(Race.Red, new Vector2(500, 300), "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: true);
        var missile = Assert.Single(silo.Weapons);
        missile.Reload = 0;
        Assert.Empty(world.MissilesInFlight);

        world.Issue(new StrikeCommand(Race.Red, silo.Id, Field));
        Run(world, 1);
        var flight = Assert.Single(world.MissilesInFlight);
        Assert.Equal(silo.Id, flight.Launcher);
        Assert.Equal(silo.Position, flight.From);
        Assert.Equal(Field, flight.Target);
        Assert.Equal(Vector2.Distance(silo.Position, Field) / missile.Spec.ProjectileSpeed, flight.Lands - flight.Launched, 3);

        Run(world, flight.Lands - world.Time - 0.2f);
        Assert.Single(world.MissilesInFlight);
        Assert.DoesNotContain(world.Blasts, b => b.Weapon == "NUCLEAR_MISSILE");
        Run(world, 0.4f);
        Assert.Empty(world.MissilesInFlight);
        Assert.Contains(world.Blasts, b => b.Weapon == "NUCLEAR_MISSILE");
    }

    [Fact]
    public void A_silo_opens_for_its_ready_missile_and_closes_after_the_launch()
    {
        var world = NewMatch();
        var silo = world.AddConstruction(Race.Red, new Vector2(500, 300), "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: true);
        var missile = Assert.Single(silo.Weapons);
        Run(world, 1);
        Assert.False(silo.DomeRaised);

        // Ready: it starts opening at once and counts as open 3.5 s later, when the race hears of it.
        missile.Reload = 0;
        world.Step();
        Assert.True(silo.DomeRaised);
        Assert.False(silo.DomeOpen);
        var told = false;
        for (var i = 0; i < 4 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            told |= world.Events.Any(e => e.Kind == GameEventKind.VoiceMail && e.Other == (int)Race.Red && e.Detail == "VID_POD_NUKE_READY");
        }
        Assert.True(told);
        Assert.True(silo.DomeOpen);

        // Launched: the head turns to the target first (59 degrees here), stays there for 2.5 s after the launch,
        // and the dome starts closing 2 s after that.
        world.Issue(new StrikeCommand(Race.Red, silo.Id, Field));
        world.Step();
        Assert.Equal(Field, silo.StrikeAt);
        Assert.Empty(world.MissilesInFlight);
        Run(world, 1);
        Assert.Single(world.MissilesInFlight);
        Run(world, 1.5f);
        Assert.Equal(Field, silo.StrikeAt);
        Assert.True(silo.DomeRaised);
        Run(world, 1);
        Assert.Null(silo.StrikeAt);
        Assert.True(silo.DomeRaised);
        Run(world, 2.2f);
        Assert.False(silo.DomeRaised);
        Assert.False(silo.DomeOpen);
    }

    [Fact]
    public void A_silo_launches_at_once_at_a_target_ahead_of_its_head()
    {
        var world = NewMatch();
        var silo = world.AddConstruction(Race.Red, new Vector2(500, 300), "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: true);
        Assert.Single(silo.Weapons).Reload = 0;

        world.Issue(new StrikeCommand(Race.Red, silo.Id, new Vector2(900, 400)));
        world.Step();
        Assert.Single(world.MissilesInFlight);
        Assert.Null(silo.StrikeAt);
    }

    [Fact]
    public void A_silo_being_built_keeps_its_dome_shut()
    {
        var world = NewMatch();
        var silo = world.AddConstruction(Race.Red, new Vector2(500, 300), "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: false);
        Assert.Single(silo.Weapons).Reload = 0;
        Run(world, 5);
        Assert.False(silo.DomeRaised);
    }
}
