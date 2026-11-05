using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>What a race is told of its machines and buildings being hit, and of running short of BMUs.</summary>
public class VoiceMailTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch() =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7);

    private static Machine Grunt(GameWorld world, Vector2 at) => world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    private static int Mails(GameWorld world, string mail) =>
        world.Events.Count(e => e.Kind == GameEventKind.VoiceMail && e.Other == (int)Race.Red && e.Detail == mail);

    private static int RunCounting(GameWorld world, float seconds, string mail, Action? eachStep = null)
    {
        var told = 0;
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            eachStep?.Invoke();
            told += Mails(world, mail);
        }
        return told;
    }

    [Fact]
    public void A_machine_under_fire_reports_once_in_ten_seconds()
    {
        var world = NewMatch();
        var grunt = Grunt(world, FormationTests.FreePointNear(world, new Vector2(800, 800)));
        var told = RunCounting(world, 8, "VID_GRUNT_DAMAGED", () =>
        {
            grunt.HitPoints = grunt.MaxHitPoints;
            world.Damage(grunt, 1, Race.Blue);
        });
        Assert.Equal(1, told);
    }

    [Fact]
    public void A_machine_near_death_reports_every_few_seconds()
    {
        var world = NewMatch();
        var grunt = Grunt(world, FormationTests.FreePointNear(world, new Vector2(800, 800)));
        var told = RunCounting(world, 13, "VID_GRUNT_NEAR_DEATH", () =>
        {
            grunt.HitPoints = Math.Max(2, grunt.MaxHitPoints / 4);
            world.Damage(grunt, 1, Race.Blue);
        });
        Assert.InRange(told, 2, 4);
    }

    [Fact]
    public void Two_machines_side_by_side_share_one_report_but_one_far_off_gives_its_own()
    {
        var world = NewMatch();
        var at = FormationTests.FreePointNear(world, new Vector2(800, 800));
        var (first, beside, far) = (Grunt(world, at), Grunt(world, at + new Vector2(10, 0)), Grunt(world, at + new Vector2(300, 0)));
        world.Step();
        world.Damage(first, 1, Race.Blue);
        world.Damage(beside, 1, Race.Blue);
        Assert.Equal(1, Mails(world, "VID_GRUNT_DAMAGED"));
        world.Damage(far, 1, Race.Blue);
        Assert.Equal(2, Mails(world, "VID_GRUNT_DAMAGED"));
    }

    [Fact]
    public void The_pod_cries_out_when_critically_hit()
    {
        var world = NewMatch();
        var pod = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        world.Step();
        world.Damage(pod, 1, Race.Blue);
        Assert.Equal(1, Mails(world, "VID_POD_POD_ATTACKED"));
        pod.HitPoints = pod.MaxHitPoints / 20;
        var told = RunCounting(world, 6, "VID_POD_POD_CRITICAL_DAMAGE", () =>
        {
            pod.HitPoints = pod.MaxHitPoints / 20;
            world.Damage(pod, 1, Race.Blue);
        });
        Assert.Equal(1, told);
    }

    [Fact]
    public void A_building_destroyed_by_an_enemy_is_reported_but_one_blown_up_by_its_owner_is_not()
    {
        var world = NewMatch();
        var pod = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        world.Damage(pod, 100000, Race.Blue);
        Assert.Equal(1, RunCounting(world, 1, "VID_POD_POD_DESTROYED"));

        world = NewMatch();
        pod = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        world.Damage(pod, 100000, Race.Red);
        Assert.Equal(0, RunCounting(world, 1, "VID_POD_POD_DESTROYED"));
    }

    private static (Construction Silo, WeaponMount Missile) ReadySilo(GameWorld world)
    {
        var silo = world.AddConstruction(Race.Red, new Vector2(500, 300), "MISSILE_EMPLACEMENT", "ICBM", 5, 0, complete: true);
        var missile = Assert.Single(silo.Weapons);
        missile.Reload = 0;
        return (silo, missile);
    }

    [Fact]
    public void A_players_launch_costs_500_BMUs()
    {
        var world = NewMatch();
        var (silo, _) = ReadySilo(world);
        world.Races[Race.Red].Bmu = 800;
        world.Issue(new StrikeCommand(Race.Red, silo.Id, new Vector2(800, 800)));
        world.Step();
        Assert.Equal(300, world.Races[Race.Red].Bmu, 0);
    }

    [Fact]
    public void Without_the_BMUs_the_pod_says_so_and_nothing_flies()
    {
        var world = NewMatch();
        var (silo, missile) = ReadySilo(world);
        world.Races[Race.Red].Bmu = 499;
        world.Issue(new StrikeCommand(Race.Red, silo.Id, new Vector2(800, 800)));
        Assert.Equal(1, RunCounting(world, 1, "VID_POD_INSUFFICIENT_CASH_FOR_NUKE"));
        Assert.Equal(0, missile.Reload);
        Assert.Null(silo.LaunchAt);
    }

    [Fact]
    public void The_AI_launches_for_nothing()
    {
        var world = NewMatch();
        var (silo, missile) = ReadySilo(world);
        world.Races[Race.Red].Controller = Controller.Ai;
        world.Races[Race.Red].Bmu = 0;
        world.Issue(new StrikeCommand(Race.Red, silo.Id, new Vector2(500, 900)));
        world.Step();
        Assert.True(missile.Reload > 0 || silo.LaunchAt is not null);
    }

    [Fact]
    public void A_better_constructor_opens_new_constructions_once_and_they_stay_open()
    {
        var world = NewMatch();
        Assert.Equal(0, RunCounting(world, 2, "VID_POD_NEW_CONSTRUCTIONS"));
        foreach (var dozer in world.Machines.Where(m => m.Race == Race.Red && m.Stats.MostAdvancedConstruction is not null).ToList())
            world.Damage(dozer, 100000, Race.Blue);
        var at = FormationTests.FreePointNear(world, new Vector2(800, 800));
        var was = world.Buildable(Race.Red).Count;
        var behemoth = world.AddMachine(Race.Red, at, "CONSTRUCTOR", "BEHEMOTH", 5, null);
        Assert.Equal(1, RunCounting(world, 3, "VID_POD_NEW_CONSTRUCTIONS"));
        var opened = world.Buildable(Race.Red).Count;
        Assert.True(opened > was);

        world.Damage(behemoth, 100000, Race.Blue);
        Assert.Equal(0, RunCounting(world, 2, "VID_POD_NEW_CONSTRUCTIONS"));
        Assert.Equal(opened, world.Buildable(Race.Red).Count);
    }

    private static (Machine Carrier, Construction Mine, Construction Smelter) Route(GameWorld world)
    {
        var at = new Vector2(800, 800);
        var smelter = world.AddConstruction(Race.Red, at, "SMELTER", null, 1, 0, complete: true);
        var mine = world.AddConstruction(Race.Red, at + new Vector2(150, 0), "MINE", null, 1, 0, complete: true);
        mine.Ore = 50;
        var carrier = world.AddMachine(Race.Red, at + new Vector2(100, 60), "RESOURCE_CARRIER", null, 1, null);
        world.Issue(new TransportCommand(Race.Red, [carrier.Id], mine.Id, smelter.Id));
        world.Step();
        return (carrier, mine, smelter);
    }

    [Fact]
    public void A_carrier_that_lost_its_smelter_takes_the_next_and_says_so()
    {
        var world = NewMatch();
        var (carrier, _, smelter) = Route(world);
        world.Damage(smelter, 100000, Race.Blue);

        Assert.Equal(1, RunCounting(world, 2, "VID_RESOURCE_CARRIER_ALTERNATIVE_DESTINATION"));
        var order = Assert.IsType<TransportOrder>(carrier.Order);
        Assert.Equal("POD", world.Find<Construction>(order.Smelter)!.Spec.Type);
    }

    [Fact]
    public void A_carrier_with_nowhere_left_reports_its_route_lost()
    {
        var world = NewMatch();
        var (carrier, _, smelter) = Route(world);
        foreach (var other in world.Constructions.Where(c => c.Race == Race.Red && c.Spec.Type is "POD").ToList())
            world.Damage(other, 100000, Race.Blue);
        world.Damage(smelter, 100000, Race.Blue);

        // It keeps its suppliers and goes on fetching, for a smelter to be finished (MachLogResourceCarrier::mayHaveLostRouteElement).
        Assert.Equal(1, RunCounting(world, 2, "VID_RESOURCE_CARRIER_ROUTE_FAILURE"));
        Assert.Equal(0, Assert.IsType<TransportOrder>(carrier.Order).Smelter);
    }

    [Fact]
    public void A_machines_report_names_it_so_its_death_can_cut_it_off()
    {
        var world = NewMatch();
        var grunt = Grunt(world, FormationTests.FreePointNear(world, new Vector2(800, 800)));
        world.Step();
        world.Damage(grunt, 1, Race.Blue);
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.VoiceMail && e.Detail == "VID_GRUNT_DAMAGED" && e.Actor == grunt.Id);
    }

    [Fact]
    public void A_mine_that_runs_dry_with_nowhere_to_go_says_so_once()
    {
        var world = NewMatch();
        foreach (var other in world.Sites)
            other.Ore = 0;
        var site = world.Sites.First();
        var mine = world.AddConstruction(Race.Red, site.Position, "MINE", null, 1, 0, complete: true);
        mine.MineralSite = site.Id;

        var exhausted = 0;
        var inoperable = RunCounting(world, 4, "VID_POD_MINE_INOPERABLE", () => exhausted += Mails(world, "VID_POD_MINERALS_EXHAUSTED"));
        Assert.Equal(1, exhausted);
        Assert.Equal(1, inoperable);
    }

    [Fact]
    public void Running_low_on_BMUs_is_told_and_running_out_too()
    {
        var world = NewMatch();
        world.Step();
        world.Races[Race.Red].Bmu = 60;
        world.SpendBmu(Race.Red, 30);
        Assert.Equal(1, Mails(world, "VID_POD_BMUS_LOW"));

        world = NewMatch();
        world.Step();
        world.Races[Race.Red].Bmu = 30;
        world.SpendBmu(Race.Red, 30);
        Assert.Equal(1, Mails(world, "VID_POD_NO_BMUS_LEFT"));
    }
}
