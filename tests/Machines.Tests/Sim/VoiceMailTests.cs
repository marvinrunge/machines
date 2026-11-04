using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
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
}
