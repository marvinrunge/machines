using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Healing, repair, capture, deconstruction, recycling, garrisons, self-destruct and treachery.</summary>
public class SupportTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch() =>
        MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, seed: 7);

    private static readonly Vector2 Field = new(800, 800);

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    private static Machine Dozer(GameWorld world, Race race, Vector2 at) => world.AddMachine(race, at, "CONSTRUCTOR", "DOZER", 1, null);

    [Fact]
    public void An_overseer_heals_damaged_machines_nearby_by_itself()
    {
        var world = NewMatch();
        var overseer = world.AddMachine(Race.Red, Field, "ADMINISTRATOR", "OVERSEER", 2, "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED");
        var patient = world.AddMachine(Race.Red, Field + new Vector2(20, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        patient.HitPoints = 2;

        Run(world, 30, () => patient.HitPoints == patient.MaxHitPoints);

        Assert.True(overseer.CanHeal);
        Assert.Equal(patient.MaxHitPoints, patient.HitPoints);
    }

    [Fact]
    public void Repairs_cost_bmus_in_proportion_to_the_building()
    {
        var world = NewMatch();
        var pod = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        var dozer = Dozer(world, Race.Red, pod.Position + new Vector2(0, 40));
        pod.HitPoints = pod.MaxHitPoints / 2;
        var bmu = world.Races[Race.Red].Bmu;

        world.Issue(new RepairCommand(Race.Red, [dozer.Id], pod.Id));
        Run(world, 60);

        // A Dozer's 100 BMUs a minute buy hit points at the pod's own price: 2000 HP for 1200 BMUs.
        var gained = pod.HitPoints - pod.MaxHitPoints / 2;
        // The bank starts above the 500 BMU storage limit, so the pod trickle adds nothing meanwhile.
        var spent = bmu - world.Races[Race.Red].Bmu;
        Assert.InRange(spent, 80, 101);
        Assert.InRange(gained, spent * pod.MaxHitPoints / pod.Stats.Cost - 5, spent * pod.MaxHitPoints / pod.Stats.Cost + 5);
    }

    [Fact]
    public void Constructors_capture_an_enemy_building_below_a_third_of_its_hit_points()
    {
        var world = NewMatch();
        var target = world.AddConstruction(Race.Blue, Field, "GARRISON", null, 1, 0, complete: true);
        var dozer = Dozer(world, Race.Red, Field + new Vector2(0, 40));

        world.Issue(new CaptureCommand(Race.Red, [dozer.Id], target.Id));
        Run(world, 600, () => target.Race == Race.Red);

        Assert.Equal(Race.Red, target.Race);
        Assert.False(target.IsDead);
        Assert.InRange(target.HitPoints, 0.3f * target.MaxHitPoints - 1, target.MaxHitPoints / 3f);
    }

    [Fact]
    public void Deconstructing_your_own_building_pays_back_half()
    {
        var world = NewMatch();
        var garrison = world.AddConstruction(Race.Red, Field, "GARRISON", null, 1, 0, complete: true);
        var dozer = Dozer(world, Race.Red, Field + new Vector2(0, 40));
        world.Races[Race.Red].Bmu = 0;

        world.Issue(new DeconstructCommand(Race.Red, [dozer.Id], garrison.Id));
        Run(world, 300, () => world.Find(garrison.Id) is null);

        Assert.Null(world.Find(garrison.Id));
        // 150 BMUs of garrison at 50%, plus the pod trickle meanwhile.
        Assert.InRange(world.Races[Race.Red].Bmu, 74, 110);
    }

    [Fact]
    public void Recycling_a_machine_returns_half_its_factory_price()
    {
        var world = NewMatch();
        var pod = world.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        var grunt = world.AddMachine(Race.Red, pod.Position + new Vector2(0, 40), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var refund = world.RecycleValue(grunt);
        world.Races[Race.Red].Bmu = 0;

        world.Issue(new RecycleCommand(Race.Red, [grunt.Id]));
        Run(world, 60, () => world.Find(grunt.Id) is null);

        Assert.Null(world.Find(grunt.Id));
        Assert.True(refund > 0);
        Assert.InRange(world.Races[Race.Red].Bmu, refund, refund + 8);
    }

    [Fact]
    public void A_garrison_repairs_the_machines_inside_and_lets_them_out_on_orders()
    {
        var world = NewMatch();
        var garrison = world.AddConstruction(Race.Red, Field, "GARRISON", null, 1, 0, complete: true);
        var grunt = world.AddMachine(Race.Red, Field + new Vector2(0, 40), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        grunt.HitPoints = 1;

        world.Issue(new EnterGarrisonCommand(Race.Red, [grunt.Id], garrison.Id));
        Run(world, 30, () => grunt.Inside is not null);
        Assert.Equal(garrison.Id, grunt.Inside);

        // REPAIR_RATE 15 hit points per minute.
        Run(world, 60);
        Assert.InRange(grunt.HitPoints, Math.Min(grunt.MaxHitPoints, 14), grunt.MaxHitPoints);

        world.Issue(new MoveCommand(Race.Red, [grunt.Id], Field + new Vector2(0, 80)));
        world.Step();
        Assert.Null(grunt.Inside);
        Assert.Empty(garrison.Garrisoned);
    }

    [Fact]
    public void Self_destruct_goes_off_after_five_seconds_unless_stopped()
    {
        var world = NewMatch();
        var a = world.AddMachine(Race.Red, Field, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var b = world.AddMachine(Race.Red, Field + new Vector2(20, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        world.Issue(new SelfDestructCommand(Race.Red, [a.Id, b.Id]));
        Run(world, 2);
        world.Issue(new StopCommand(Race.Red, [b.Id]));
        Run(world, 4);

        Assert.Null(world.Find(a.Id));
        Assert.NotNull(world.Find(b.Id));
    }

    [Fact]
    public void The_orb_of_treachery_turns_enemy_machines()
    {
        var world = NewMatch();
        var knight = world.AddMachine(Race.Red, Field, "AGGRESSOR", "KNIGHT", 4, "L_MULTI_LAUNCHER5_R_TREACHERY");
        var victim = Dozer(world, Race.Blue, Field + new Vector2(50, 0));

        Assert.Contains(knight.Weapons, w => w.IsTreachery);
        world.Issue(new AttackCommand(Race.Red, [knight.Id], victim.Id));
        Run(world, 30, () => victim.Race == Race.Red);

        Assert.Equal(Race.Red, victim.Race);
    }
}
