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
    public void A_captured_smelter_brings_its_share_of_the_losers_bmus()
    {
        var world = NewMatch();
        var smelter = world.AddConstruction(Race.Blue, Field, "SMELTER", null, 1, 0, complete: true);
        var dozer = Dozer(world, Race.Red, Field + new Vector2(0, 40));
        // Blue stores 1500 (pod 500, smelter 1000): the smelter is two thirds of it.
        Assert.Equal(1500, world.BmuCapacity(Race.Blue));
        world.Races[Race.Blue].Bmu = 1200;
        world.Races[Race.Red].Bmu = 0;

        world.Issue(new CaptureCommand(Race.Red, [dozer.Id], smelter.Id));
        var (red, blue) = CaptureTick(world, smelter);

        // Two thirds of Blue's bank go with it (about 800 of the 1200; both pods trickle a little meanwhile).
        Assert.InRange(blue, 1200, 1215);
        Assert.Equal(red + blue * 2 / 3, world.Races[Race.Red].Bmu, 0.1f);
        Assert.Equal(blue / 3, world.Races[Race.Blue].Bmu, 0.1f);
    }

    /// <summary>Runs until the building is captured; the banks just before the tick it changed hands.</summary>
    private static (float Red, float Blue) CaptureTick(GameWorld world, Construction building)
    {
        var banks = (0f, 0f);
        for (var i = 0; i < 600 * GameWorld.TicksPerSecond && building.Race != Race.Red; i++)
        {
            banks = (world.Races[Race.Red].Bmu, world.Races[Race.Blue].Bmu);
            world.Step();
        }
        Assert.Equal(Race.Red, building.Race);
        return banks;
    }

    [Fact]
    public void Without_original_capture_a_smelter_changes_hands_empty_and_carriers_keep_hauling()
    {
        var world = NewMatch();
        world.Options = world.Options with { OriginalCapture = false };
        var smelter = world.AddConstruction(Race.Blue, Field, "SMELTER", null, 1, 0, complete: true);
        var mine = world.AddConstruction(Race.Blue, Field + new Vector2(150, 0), "MINE", null, 1, 0, complete: true);
        var carrier = world.AddMachine(Race.Blue, Field + new Vector2(100, 60), "RESOURCE_CARRIER", null, 1, null);
        var dozer = Dozer(world, Race.Red, Field + new Vector2(0, 40));
        world.Races[Race.Blue].Bmu = 1200;
        world.Races[Race.Red].Bmu = 0;

        world.Issue(new TransportCommand(Race.Blue, [carrier.Id], mine.Id, smelter.Id));
        world.Issue(new CaptureCommand(Race.Red, [dozer.Id], smelter.Id));
        Run(world, 600, () => smelter.Race == Race.Red);
        world.Step();

        Assert.Equal(Race.Red, smelter.Race);
        Assert.InRange(world.Races[Race.Red].Bmu, 0, 15);
        Assert.IsType<TransportOrder>(carrier.Order);
    }

    [Fact]
    public void Carriers_stop_hauling_for_a_mine_that_changed_sides()
    {
        var world = NewMatch();
        var smelter = world.AddConstruction(Race.Blue, Field, "SMELTER", null, 1, 0, complete: true);
        var mine = world.AddConstruction(Race.Blue, Field + new Vector2(150, 0), "MINE", null, 1, 0, complete: true);
        var carrier = world.AddMachine(Race.Blue, Field + new Vector2(100, 60), "RESOURCE_CARRIER", null, 1, null);
        var dozer = Dozer(world, Race.Red, mine.Position + new Vector2(0, 40));

        world.Issue(new TransportCommand(Race.Blue, [carrier.Id], mine.Id, smelter.Id));
        world.Issue(new CaptureCommand(Race.Red, [dozer.Id], mine.Id));
        world.Step();
        Assert.IsType<TransportOrder>(carrier.Order);
        Run(world, 600, () => mine.Race == Race.Red);
        world.Step();

        Assert.Equal(Race.Red, mine.Race);
        Assert.Same(Order.Idle, carrier.Order);
    }

    [Fact]
    public void A_captured_factory_pays_back_three_quarters_of_the_machine_it_was_building()
    {
        var world = NewMatch();
        var factory = world.AddConstruction(Race.Blue, Field, "FACTORY", "MILITARY", 1, 0, complete: true);
        var dozer = Dozer(world, Race.Red, Field + new Vector2(0, 50));
        var grunt = world.Data.ResearchTree.Items.First(i => i.Key.Type == "AGGRESSOR" && i.Key.SubType == "GRUNT").Key;
        factory.ProductionQueue.Add(grunt);
        factory.ProductionSpent = 20;
        // An empty bank keeps the grunt from being finished; only Blue's pod trickle goes into it meanwhile.
        world.Races[Race.Blue].Bmu = 0;
        world.Races[Race.Red].Bmu = 0;

        world.Issue(new CaptureCommand(Race.Red, [dozer.Id], factory.Id));
        var spent = 0f;
        var red = 0f;
        for (var i = 0; i < 600 * GameWorld.TicksPerSecond && factory.Race != Race.Red; i++)
        {
            (spent, red) = (factory.ProductionSpent, world.Races[Race.Red].Bmu);
            world.Step();
        }

        Assert.Equal(Race.Red, factory.Race);
        Assert.Empty(factory.ProductionQueue);
        Assert.InRange(spent, 20, 40);
        Assert.Equal(red + spent * 0.75f, world.Races[Race.Red].Bmu, 0.1f);
    }

    [Fact]
    public void A_smelter_destroyed_by_the_enemy_takes_its_share_of_the_bank()
    {
        var world = NewMatch();
        var smelter = world.AddConstruction(Race.Blue, Field, "SMELTER", null, 1, 0, complete: true);
        // Blue stores 1500 (pod 500, smelter 1000): the smelter is two thirds of it.
        world.Races[Race.Blue].Bmu = 1200;

        world.Damage(smelter, 100_000, Race.Red);
        world.Step();

        Assert.Null(world.Find(smelter.Id));
        Assert.Equal(500, world.BmuCapacity(Race.Blue));
        // A third of the 1200 is left, give or take the pod trickle of one tick.
        Assert.InRange(world.Races[Race.Blue].Bmu, 400, 401);
    }

    [Fact]
    public void A_smelter_demolished_by_its_own_race_only_loses_what_no_longer_fits()
    {
        var world = NewMatch();
        var smelter = world.AddConstruction(Race.Blue, Field, "SMELTER", null, 1, 0, complete: true);
        var dozer = Dozer(world, Race.Blue, Field + new Vector2(0, 40));
        world.Races[Race.Blue].Bmu = 1200;
        // An enemy's earlier hit does not count: the owner's own dozer takes it down.
        world.Damage(smelter, 10, Race.Red);

        world.Issue(new DeconstructCommand(Race.Blue, [dozer.Id], smelter.Id));
        Run(world, 600, () => world.Find(smelter.Id) is null);

        Assert.Null(world.Find(smelter.Id));
        Assert.Equal(500, world.Races[Race.Blue].Bmu, 0.01f);
    }

    [Fact]
    public void Without_original_bank_a_destroyed_smelter_leaves_the_bank_alone()
    {
        var world = NewMatch();
        world.Options = world.Options with { OriginalBank = false };
        var smelter = world.AddConstruction(Race.Blue, Field, "SMELTER", null, 1, 0, complete: true);
        world.Races[Race.Blue].Bmu = 1200;

        world.Damage(smelter, 100_000, Race.Red);
        world.Step();

        Assert.Null(world.Find(smelter.Id));
        Assert.InRange(world.Races[Race.Blue].Bmu, 1200, 1201);
    }

    [Fact]
    public void Cancelling_the_machine_in_the_factory_pays_back_three_quarters()
    {
        var world = NewMatch();
        var factory = world.AddConstruction(Race.Red, Field, "FACTORY", "MILITARY", 1, 0, complete: true);
        var grunt = world.Data.ResearchTree.Items.First(i => i.Key.Type == "AGGRESSOR" && i.Key.SubType == "GRUNT").Key;
        factory.ProductionQueue.Add(grunt);
        factory.ProductionSpent = 40;
        world.Races[Race.Red].Bmu = 0;

        world.Issue(new CancelProductionCommand(Race.Red, factory.Id, 0));
        world.Step();

        Assert.Empty(factory.ProductionQueue);
        Assert.Equal(0, factory.ProductionSpent);
        // 30 of the 40 spent, give or take the pod trickle of one tick.
        Assert.InRange(world.Races[Race.Red].Bmu, 30, 31);
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
