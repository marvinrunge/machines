using System.Numerics;
using System.Reflection;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>Carriers' rounds, scavengers, APCs, labs, allies, the unit limit and factory pads as the original has them.</summary>
public class LogisticsTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld NewMatch(Controller blue = Controller.Player, MatchOptions? options = null) =>
        MatchSetup.Create(DataLoader.Value, "m_moon", "m_moon",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = blue }, seed: 7, options: options);

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    /// <summary>The middle of an open square of ground 480 metres across.</summary>
    private static Vector2 Open(GameWorld world)
    {
        var (width, depth) = (world.Planet.Surface.Width, world.Planet.Surface.Depth);
        for (var y = 240f; y < depth - 240; y += 10)
            for (var x = 240f; x < width - 240; x += 10)
                if (world.Nav.IsAreaFree((new Vector2(x, y) - new Vector2(240), new Vector2(x, y) + new Vector2(240))))
                    return new Vector2(x, y);
        throw new InvalidOperationException("no open ground");
    }

    private static Machine Carrier(GameWorld world, Vector2 at, Race race = Race.Red) => world.AddMachine(race, at, "RESOURCE_CARRIER", null, 1, null);

    private static Construction Mine(GameWorld world, Vector2 at, float ore = 50, Race race = Race.Red)
    {
        var mine = world.AddConstruction(race, at, "MINE", null, 1, 0, complete: true);
        mine.Ore = ore;
        return mine;
    }

    private static Construction Smelter(GameWorld world, Vector2 at, Race race = Race.Red, bool complete = true) =>
        world.AddConstruction(race, at, "SMELTER", null, 1, 0, complete: complete);

    // ---- carriers ---------------------------------------------------------------------------------

    [Fact]
    public void A_carrier_steals_from_another_races_smelter_and_brings_it_home()
    {
        var world = NewMatch();
        var at = Open(world);
        var theirs = Smelter(world, at + new Vector2(90, 0), Race.Blue);
        var ours = Smelter(world, at - new Vector2(90, 0));
        var carrier = Carrier(world, at);
        var capacity = carrier.Stats.Capacity!.Value;
        world.Races[Race.Blue].Bmu = 500;
        var bank = world.Races[Race.Red].Bmu = 0;

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], theirs.Id, ours.Id));
        Run(world, 60, () => carrier.Ore > 0);
        Assert.Equal(capacity, carrier.Ore);
        // Each side's pod brings in a little of its own.
        Assert.InRange(world.Races[Race.Blue].Bmu, 500 - capacity, 500 - capacity + 3);

        Run(world, 90, () => carrier.Ore == 0);
        Assert.InRange(world.Races[Race.Red].Bmu, bank + capacity, bank + capacity + 6);
    }

    [Fact]
    public void Ones_own_smelter_is_no_supplier_and_one_taken_over_drops_out_of_the_round()
    {
        var world = NewMatch();
        var at = Open(world);
        var ours = Smelter(world, at);
        var theirs = Smelter(world, at + new Vector2(120, 0), Race.Blue);
        var carrier = Carrier(world, at + new Vector2(60, 60));

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], ours.Id, ours.Id));
        world.Step();
        Assert.IsNotType<TransportOrder>(carrier.Order);

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], theirs.Id, ours.Id));
        world.Step();
        typeof(GameWorld).GetMethod("TakeOver", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(world, [theirs, Race.Red]);
        world.Step();
        Assert.IsNotType<TransportOrder>(carrier.Order);
    }

    [Fact]
    public void A_round_runs_nearest_first_from_the_smelter_unless_its_DefCon_is_high()
    {
        var world = NewMatch();
        var at = Open(world);
        var smelter = Smelter(world, at);
        var far = Mine(world, at + new Vector2(140, 0));
        var near = Mine(world, at + new Vector2(70, 0));
        var side = Mine(world, at + new Vector2(70, 80));
        var free = Carrier(world, at + new Vector2(40, 40));
        var held = Carrier(world, at + new Vector2(40, -40));
        held.DefCon = DefCon.High;

        world.Issue(new TransportCommand(Race.Red, [free.Id, held.Id], far.Id, smelter.Id, [near.Id, side.Id]));
        world.Step();

        // From the smelter to the nearest, on from there to that one's nearest; the one it was dealing with stays
        // current (MachLogResourceCarrier::reorderTransportRoute).
        var sorted = Assert.IsType<TransportOrder>(free.Order);
        Assert.Equal([near.Id, far.Id, side.Id], sorted.Round);
        Assert.Equal(far.Id, sorted.Mine);
        Assert.Equal([far.Id, near.Id, side.Id], Assert.IsType<TransportOrder>(held.Order).Round);
    }

    [Fact]
    public void A_carrier_with_cargo_does_not_wait_at_a_mine_with_no_ore_ready()
    {
        var world = NewMatch();
        var at = Open(world);
        var smelter = Smelter(world, at);
        var mine = Mine(world, at + new Vector2(100, 0), ore: 0);
        var carrier = Carrier(world, mine.Position + new Vector2(30, 30));
        carrier.Ore = 1;

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], mine.Id, smelter.Id));
        Run(world, 1);

        Assert.True(Assert.IsType<TransportOrder>(carrier.Order).Delivering);
    }

    [Fact]
    public void A_later_supplier_with_ore_takes_over_from_an_empty_one()
    {
        var world = NewMatch();
        var at = Open(world);
        var smelter = Smelter(world, at);
        var empty = Mine(world, at + new Vector2(70, 0), ore: 0);
        var full = Mine(world, at + new Vector2(140, 0));
        var carrier = Carrier(world, at + new Vector2(40, 40));

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], empty.Id, smelter.Id, [full.Id]));
        Run(world, 1);

        Assert.Equal(full.Id, Assert.IsType<TransportOrder>(carrier.Order).Mine);
    }

    [Fact]
    public void An_empty_carrier_stands_rather_than_drive_to_a_mine_with_no_deposit()
    {
        var world = NewMatch();
        var at = Open(world);
        var smelter = Smelter(world, at);
        var mine = Mine(world, at + new Vector2(140, 0), ore: 0);
        Assert.False(GameWorld.WorthVisiting(mine));
        var carrier = Carrier(world, at + new Vector2(40, 40));
        var start = carrier.Position;

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], mine.Id, smelter.Id));
        Run(world, 5);

        Assert.IsType<TransportOrder>(carrier.Order);
        Assert.Equal(start, carrier.Position);
    }

    [Fact]
    public void A_carrier_free_to_choose_goes_over_to_a_nearer_smelter_when_it_is_finished()
    {
        var world = NewMatch();
        var at = Open(world);
        var mine = Mine(world, at);
        var far = Smelter(world, at + new Vector2(200, 0));
        var near = Smelter(world, at - new Vector2(100, 0), complete: false);
        var free = Carrier(world, at + new Vector2(40, 40));
        var told = Carrier(world, at + new Vector2(40, -40));

        world.Issue(new TransportCommand(Race.Red, [free.Id], mine.Id, 0));
        world.Issue(new TransportCommand(Race.Red, [told.Id], mine.Id, far.Id));
        world.Step();
        Assert.Equal(far.Id, Assert.IsType<TransportOrder>(free.Order).Smelter);

        near.MakeComplete();
        Run(world, 1.1f);

        Assert.Equal(near.Id, Assert.IsType<TransportOrder>(free.Order).Smelter);
        Assert.Equal(far.Id, Assert.IsType<TransportOrder>(told.Order).Smelter);
    }

    [Fact]
    public void Dormant_mines_do_not_count_when_a_carrier_chooses_its_smelter()
    {
        var world = NewMatch();
        var at = Open(world);
        var worked = Mine(world, at);
        var dormant = Mine(world, at + new Vector2(280, 0), ore: 0);
        var left = Smelter(world, at - new Vector2(80, 0));
        Smelter(world, at + new Vector2(200, 0));
        var carrier = Carrier(world, at + new Vector2(40, 40));

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], worked.Id, 0, [dormant.Id]));
        world.Step();

        Assert.Equal(left.Id, Assert.IsType<TransportOrder>(carrier.Order).Smelter);
    }

    [Fact]
    public void A_carrier_takes_up_the_next_supplier_when_one_is_lost_and_delivers_when_none_is_left()
    {
        var world = NewMatch();
        var at = Open(world);
        var smelter = Smelter(world, at);
        var first = Mine(world, at + new Vector2(70, 0));
        var second = Mine(world, at + new Vector2(140, 0));
        var carrier = Carrier(world, at + new Vector2(40, 40));
        world.Issue(new TransportCommand(Race.Red, [carrier.Id], first.Id, smelter.Id, [second.Id]));
        world.Step();

        world.Damage(first, 100000, Race.Blue);
        Run(world, 1);
        var order = Assert.IsType<TransportOrder>(carrier.Order);
        Assert.Equal(second.Id, order.Mine);
        Assert.Equal([second.Id], order.Round);

        carrier.Ore = 1;
        carrier.Order = order with { Delivering = true };
        world.Damage(second, 100000, Race.Blue);
        Run(world, 1);
        Assert.IsType<TransportOrder>(carrier.Order);
        Run(world, 60, () => carrier.Ore == 0);
        world.Step();
        Assert.IsType<IdleOrder>(carrier.Order);
    }

    [Fact]
    public void Adding_a_supplier_keeps_the_ones_a_carrier_has()
    {
        var world = NewMatch();
        var at = Open(world);
        var smelter = Smelter(world, at);
        var first = Mine(world, at + new Vector2(70, 0));
        var second = Mine(world, at + new Vector2(140, 0));
        var carrier = Carrier(world, at + new Vector2(40, 40));
        world.Issue(new TransportCommand(Race.Red, [carrier.Id], first.Id, smelter.Id));
        world.Step();

        world.Issue(new TransportCommand(Race.Red, [carrier.Id], second.Id, 0, Adding: true));
        world.Step();

        var order = Assert.IsType<TransportOrder>(carrier.Order);
        Assert.Equal([first.Id, second.Id], order.Round);
        Assert.Equal(smelter.Id, order.Smelter);
    }

    [Fact]
    public void A_carrier_left_without_a_smelter_takes_the_next_one_finished()
    {
        var world = NewMatch();
        var at = Open(world);
        var smelter = Smelter(world, at + new Vector2(100, 0));
        var mine = Mine(world, at);
        var carrier = Carrier(world, at + new Vector2(40, 40));
        foreach (var other in world.Constructions.Where(c => c.Race == Race.Red && c.Spec.Type is "POD").ToList())
            world.Damage(other, 100000, Race.Blue);
        world.Issue(new TransportCommand(Race.Red, [carrier.Id], mine.Id, smelter.Id));
        world.Step();

        world.Damage(smelter, 100000, Race.Blue);
        var next = Smelter(world, at - new Vector2(100, 0), complete: false);
        Run(world, 2);
        Assert.Equal(0, Assert.IsType<TransportOrder>(carrier.Order).Smelter);

        next.MakeComplete();
        Run(world, 1.1f);
        Assert.Equal(next.Id, Assert.IsType<TransportOrder>(carrier.Order).Smelter);
    }

    // ---- scavengers -------------------------------------------------------------------------------

    private static Machine Scavenger(GameWorld world, Vector2 at) => world.AddMachine(Race.Red, at, "RESOURCE_CARRIER", null, 3, null);

    private static void Wreck(GameWorld world, Vector2 at)
    {
        world.AddMachine(Race.Blue, at, "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2").HitPoints = 0;
        world.Step();
    }

    [Fact]
    public void A_scavenger_on_the_way_is_drawn_off_by_a_wreck_and_takes_up_its_way_again()
    {
        var world = NewMatch();
        var at = Open(world);
        var scavenger = Scavenger(world, at);
        var goal = at + new Vector2(230, 0);
        world.Issue(new MoveCommand(Race.Red, [scavenger.Id], goal));
        Wreck(world, at + new Vector2(120, 40));

        // For a while after the order it sticks to it (its virtual DefCon is high).
        Run(world, 5);
        Assert.IsType<MoveOrder>(scavenger.Order);

        Run(world, 5, () => scavenger.Order is ScavengeOrder);
        Assert.IsType<ScavengeOrder>(scavenger.Order);
        Run(world, 40, () => world.Debris.Count == 0);
        Assert.Empty(world.Debris);
        Run(world, 1);
        Assert.Equal(goal, Assert.IsType<MoveOrder>(scavenger.Order).Target);
    }

    [Fact]
    public void A_scavengers_eyes_reach_140_m_at_low_DefCon_and_it_goes_back_where_it_stood()
    {
        var world = NewMatch();
        var at = Open(world);
        var normal = Scavenger(world, at);
        var low = Scavenger(world, at + new Vector2(0, 20));
        low.DefCon = DefCon.Low;
        Wreck(world, at + new Vector2(110, 10));

        Run(world, 2);

        Assert.IsType<IdleOrder>(normal.Order);
        Assert.IsType<ScavengeOrder>(low.Order);
        var home = low.Position;
        Run(world, 40, () => world.Debris.Count == 0);
        Run(world, 40, () => low.Order is IdleOrder);
        Assert.InRange(Vector2.Distance(low.Position, home), 0, 12);
    }

    [Fact]
    public void A_scavenger_standing_its_ground_leaves_wrecks_be()
    {
        var world = NewMatch();
        var at = Open(world);
        var scavenger = Scavenger(world, at);
        world.Issue(new StandGroundCommand(Race.Red, [scavenger.Id]));
        Wreck(world, at + new Vector2(40, 0));

        Run(world, 10);

        Assert.IsType<StandGroundOrder>(scavenger.Order);
    }

    // ---- APCs -------------------------------------------------------------------------------------

    private static (Machine Apc, List<Machine> Riders) LoadedApc(GameWorld world, int riders)
    {
        var at = Open(world);
        var apc = world.AddMachine(Race.Red, at, "APC", null, 2, null);
        var machines = Enumerable.Range(0, riders)
            .Select(i => world.AddMachine(Race.Red, at + new Vector2(20 + i * 8, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER")).ToList();
        foreach (var rider in machines)
        {
            world.Issue(new BoardCommand(Race.Red, [rider.Id], apc.Id));
            Run(world, 25, () => rider.Inside is not null);
            Assert.Equal(apc.Id, rider.Inside);
        }
        Run(world, 8);
        return (apc, machines);
    }

    [Theory]
    [InlineData(DefCon.Low, 0.15f, true)]
    [InlineData(DefCon.Low, 0.25f, false)]
    [InlineData(DefCon.Normal, 0.08f, true)]
    [InlineData(DefCon.Normal, 0.15f, false)]
    [InlineData(DefCon.High, 0.05f, false)]
    public void A_hit_apc_evacuates_by_its_DefCon(DefCon defCon, float share, bool evacuates)
    {
        var world = NewMatch();
        var (apc, riders) = LoadedApc(world, 1);
        apc.DefCon = defCon;
        apc.HitPoints = (int)(apc.MaxHitPoints * share) + 1;

        world.Damage(apc, 1, Race.Blue);
        Run(world, 1.5f);

        Assert.Equal(evacuates, riders[0].Inside is null);
    }

    [Fact]
    public void An_apc_lets_out_one_a_second_the_last_in_first()
    {
        var world = NewMatch();
        var (apc, riders) = LoadedApc(world, 3);
        world.Issue(new DeployCommand(Race.Red, [apc.Id], apc.Position + new Vector2(2, 0)));

        var outAt = new Dictionary<int, float>();
        Run(world, 10, () =>
        {
            foreach (var rider in riders.Where(r => r.Inside is null))
                outAt.TryAdd(rider.Id, world.Time);
            return outAt.Count == 3;
        });

        Assert.Equal(3, outAt.Count);
        Assert.True(outAt[riders[2].Id] < outAt[riders[1].Id] && outAt[riders[1].Id] < outAt[riders[0].Id]);
        Assert.InRange(outAt[riders[0].Id] - outAt[riders[2].Id], 1.9f, 2.2f);
        Assert.NotEqual(riders[0].Position, riders[1].Position);
    }

    [Fact]
    public void An_apc_turns_its_rear_to_a_machine_coming_aboard()
    {
        var world = NewMatch();
        var at = Open(world);
        var apc = world.AddMachine(Race.Red, at, "APC", null, 2, null);
        var grunt = world.AddMachine(Race.Red, at + new Vector2(DetMath.Cos(apc.Facing) * 40, DetMath.Sin(apc.Facing) * 40), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        world.Issue(new BoardCommand(Race.Red, [grunt.Id], apc.Id));
        var entered = default(Vector2);
        Run(world, 25, () =>
        {
            if (grunt.Inside is null)
                entered = grunt.Position;
            return grunt.Inside is not null;
        });

        Assert.Equal(apc.Id, grunt.Inside);
        var forward = new Vector2(DetMath.Cos(apc.Facing), DetMath.Sin(apc.Facing));
        Assert.True(Vector2.Dot(forward, Vector2.Normalize(entered - apc.Position)) < -0.9f);
    }

    // ---- labs -------------------------------------------------------------------------------------

    private static float ResearchPerSecond(GameWorld world, Construction lab, int technicians)
    {
        var item = world.Data.ResearchTree.Items.First(i => !world.Races[Race.Red].Researched.Contains(i.Key));
        lab.ResearchQueue.Add(item.Key);
        for (var i = 0; i < technicians; i++)
        {
            var technician = world.AddMachine(Race.Red, lab.Position, "TECHNICIAN", "LAB_TECH", 2, null);
            technician.Inside = lab.Id;
            lab.Technicians.Add(technician.Id);
        }
        world.Races[Race.Red].Bmu = 100000;
        world.Races[Race.Red].ResearchProgress.TryGetValue(item.Key, out var before);
        world.Step();
        world.Races[Race.Red].ResearchProgress.TryGetValue(item.Key, out var after);
        return (after - before) * GameWorld.TicksPerSecond;
    }

    [Fact]
    public void A_technician_beyond_the_stations_adds_a_fifth_and_a_labs_level_five_percent_each()
    {
        var one = NewMatch();
        var at = Open(one);
        var lab = one.AddConstruction(Race.Red, at, "HARDWARE_LAB", "MILITARY", 3, 0, complete: true);
        var stations = GameWorld.ResearchStations(lab);
        var full = ResearchPerSecond(one, lab, stations);

        var two = NewMatch();
        var crowded = two.AddConstruction(Race.Red, at, "HARDWARE_LAB", "MILITARY", 3, 0, complete: true);
        var more = ResearchPerSecond(two, crowded, stations + 1);
        Assert.Equal(full * (stations + 0.2f) / stations, more, 0.01f);

        var three = NewMatch();
        var civilian = three.AddConstruction(Race.Red, at, "HARDWARE_LAB", "CIVILIAN", 1, 0, complete: true);
        var plain = ResearchPerSecond(three, civilian, 1);
        Assert.Equal(full / stations, plain * 1.1f, 0.01f);
    }

    [Fact]
    public void A_spy_gets_into_a_full_lab_of_another_race_and_takes_no_station_there()
    {
        var world = NewMatch();
        var at = Open(world);
        var lab = world.AddConstruction(Race.Blue, at, "HARDWARE_LAB", "CIVILIAN", 1, 0, complete: true);
        for (var i = 0; i < GameWorld.ResearchStations(lab); i++)
            lab.Technicians.Add(world.AddMachine(Race.Blue, lab.Position, "TECHNICIAN", "LAB_TECH", 2, null).Id);
        Assert.False(world.HasFreeBay(lab));
        var door = lab.EntranceOutside!.Value;
        var spy = world.AddMachine(Race.Red, door + Vector2.Normalize(door - lab.Position) * 10, "SPY_LOCATOR", null, 3, null);

        world.Issue(new EnterLabCommand(Race.Red, [spy.Id], lab.Id));
        Run(world, 30, () => spy.Inside is not null);

        Assert.Equal(lab.Id, spy.Inside);
        lab.Technicians.RemoveAt(0);
        Assert.True(world.HasFreeBay(lab));
    }

    // ---- allies -----------------------------------------------------------------------------------

    private static GameWorld Allied()
    {
        var world = NewMatch();
        world.Races[Race.Red].Allies.Add(Race.Blue);
        world.Races[Race.Blue].Allies.Add(Race.Red);
        return world;
    }

    [Fact]
    public void An_allys_machine_can_be_healed()
    {
        var world = Allied();
        var at = Open(world);
        var healer = world.AddMachine(Race.Red, at, "ADMINISTRATOR", "OVERSEER", 2, "L_SUPERCHARGE_ADVANCED_R_SUPERCHARGE_ADVANCED");
        Assert.Contains(healer.Weapons, w => w.IsHealer);
        var hurt = world.AddMachine(Race.Blue, at + new Vector2(15, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        hurt.HitPoints = hurt.MaxHitPoints / 2;

        world.Issue(new HealCommand(Race.Red, [healer.Id], hurt.Id));
        var hitPoints = hurt.HitPoints;
        Run(world, 3);

        Assert.Empty(world.Rejected);
        Assert.True(hurt.HitPoints > hitPoints);
    }

    [Fact]
    public void An_allys_building_can_be_repaired_at_the_repairers_cost()
    {
        var world = Allied();
        var at = Open(world);
        var garrison = world.AddConstruction(Race.Blue, at, "GARRISON", null, 1, 0, complete: true);
        garrison.HitPoints = garrison.MaxHitPoints / 2;
        var dozer = world.AddMachine(Race.Red, at + new Vector2(0, 40), "CONSTRUCTOR", "DOZER", 1, null);
        world.Races[Race.Red].Bmu = 500;
        var blue = world.Races[Race.Blue].Bmu;

        world.Issue(new RepairCommand(Race.Red, [dozer.Id], garrison.Id));
        var hitPoints = garrison.HitPoints;
        Run(world, 30, () => garrison.HitPoints > hitPoints);

        Assert.True(garrison.HitPoints > hitPoints);
        Assert.True(world.Races[Race.Red].Bmu < 500);
        Assert.Equal(blue, world.Races[Race.Blue].Bmu);
    }

    [Fact]
    public void Recycling_at_an_allys_smelter_pays_the_ally()
    {
        var world = Allied();
        var at = Open(world);
        var smelter = Smelter(world, at, Race.Blue);
        var grunt = world.AddMachine(Race.Red, at + new Vector2(0, 60), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var value = world.RecycleValue(grunt);
        world.Races[Race.Red].Bmu = 0;
        world.Races[Race.Blue].Bmu = 0;

        world.Issue(new RecycleCommand(Race.Red, [grunt.Id], smelter.Id));
        Run(world, 30, () => world.Find(grunt.Id) is null);

        Assert.Null(world.Find(grunt.Id));
        Assert.InRange(world.Races[Race.Blue].Bmu, value, value + 3);
        Assert.InRange(world.Races[Race.Red].Bmu, 0, 3);
    }

    [Fact]
    public void An_allys_building_can_be_captured()
    {
        var world = Allied();
        var at = Open(world);
        var garrison = world.AddConstruction(Race.Blue, at, "GARRISON", null, 1, 0, complete: true);
        var dozer = world.AddMachine(Race.Red, at + new Vector2(0, 40), "CONSTRUCTOR", "DOZER", 1, null);

        world.Issue(new CaptureCommand(Race.Red, [dozer.Id], garrison.Id));
        Run(world, 300, () => garrison.Race == Race.Red);

        Assert.Equal(Race.Red, garrison.Race);
    }

    [Fact]
    public void An_allys_garrison_takes_a_machine_in_and_heals_it_but_never_an_enemy()
    {
        var world = Allied();
        var at = Open(world);
        var garrison = world.AddConstruction(Race.Blue, at, "GARRISON", null, 1, 0, complete: true);
        var door = garrison.EntranceOutside!.Value;
        var grunt = world.AddMachine(Race.Red, door + Vector2.Normalize(door - garrison.Position) * 10, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        grunt.HitPoints = grunt.MaxHitPoints / 2;

        world.Issue(new EnterGarrisonCommand(Race.Red, [grunt.Id], garrison.Id));
        Run(world, 30, () => grunt.Inside is not null);
        Assert.Equal(garrison.Id, grunt.Inside);
        var hitPoints = grunt.HitPoints;
        Run(world, 5);
        Assert.True(grunt.HitPoints > hitPoints);

        world.Races[Race.Red].Allies.Clear();
        world.Races[Race.Blue].Allies.Clear();
        hitPoints = grunt.HitPoints;
        Run(world, 5);
        Assert.Equal(hitPoints, grunt.HitPoints);
    }

    // ---- the unit limit, factory pads, machines coming back ------------------------------------------

    private static void Fill(GameWorld world, int units)
    {
        var count = world.Machines.Count(m => m.Race == Race.Red) + world.Constructions.Count(c => c.Race == Race.Red);
        for (var i = count; i < units; i++)
            world.AddMachine(Race.Red, new Vector2(100 + i % 50 * 8, 100 + i / 50 * 8), "CONSTRUCTOR", "DOZER", 1, null);
    }

    [Fact]
    public void A_factory_holds_its_machine_at_the_unit_limit_and_no_building_can_be_placed()
    {
        var world = NewMatch();
        var at = Open(world);
        var factory = world.AddConstruction(Race.Red, at, "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var key = world.Data.FactoryOffer("CIVILIAN", 1).First();
        world.Races[Race.Red].Researched.Add(key);
        world.Races[Race.Red].Bmu = 1000;
        Fill(world, GameWorld.MaxUnitsPerRace);
        Assert.True(world.MaxUnitsExist(Race.Red));
        var building = world.Buildable(Race.Red).First();
        Assert.Equal("too many units", world.WhyCannotPlace(Race.Red, building.Type, building.SubType, building.HardwareLevel, at + new Vector2(0, 120), 0));

        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key));
        var units = world.Machines.Count(m => m.Race == Race.Red);
        Run(world, 120);
        Assert.Equal(units, world.Machines.Count(m => m.Race == Race.Red));
        Assert.Single(factory.ProductionQueue);

        world.Machines.First(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR")).HitPoints = 0;
        Run(world, 2);
        Assert.Empty(factory.ProductionQueue);
    }

    [Fact]
    public void A_campaign_has_forty_units_more()
    {
        var world = NewMatch(options: new MatchOptions { Campaign = new CampaignCarry() });
        Fill(world, GameWorld.MaxUnitsPerRace);
        Assert.False(world.MaxUnitsExist(Race.Red));
        Fill(world, GameWorld.MaxUnitsPerRace + GameWorld.CampaignExtraUnitsPerRace);
        Assert.True(world.MaxUnitsExist(Race.Red));
    }

    [Fact]
    public void A_machine_on_the_pad_holds_the_new_one_back_and_is_moved_aside_if_idle()
    {
        var world = NewMatch();
        var at = Open(world);
        var factory = world.AddConstruction(Race.Red, at, "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var pad = factory.EntranceOutside!.Value;
        var key = world.Data.FactoryOffer("CIVILIAN", 1).First();
        world.Races[Race.Red].Researched.Add(key);
        world.Races[Race.Red].Bmu = 1000;
        var enemy = world.AddMachine(Race.Blue, pad, "CONSTRUCTOR", "DOZER", 1, null);
        enemy.Order = new StandGroundOrder();

        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key));
        var units = world.Machines.Count(m => m.Race == Race.Red);
        Run(world, 90);
        Assert.Equal(units, world.Machines.Count(m => m.Race == Race.Red));

        // An enemy stays; one's own idle machine is told to go, and the new one comes out.
        enemy.HitPoints = 0;
        var idle = world.AddMachine(Race.Red, pad, "CONSTRUCTOR", "DOZER", 1, null);
        Run(world, 30, () => world.Machines.Count(m => m.Race == Race.Red) == units + 2);
        Assert.Equal(units + 2, world.Machines.Count(m => m.Race == Race.Red));
        Assert.True(Vector2.Distance(idle.Position, pad) >= 5);
    }

    [Fact]
    public void A_new_machine_comes_out_on_full_alert()
    {
        var world = NewMatch();
        var at = Open(world);
        var factory = world.AddConstruction(Race.Red, at, "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var key = world.Data.FactoryOffer("CIVILIAN", 1).First();
        world.Races[Race.Red].Researched.Add(key);
        world.Races[Race.Red].Bmu = 1000;
        var known = world.Machines.Select(m => m.Id).ToHashSet();

        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key));
        Machine? fresh = null;
        Run(world, 120, () => (fresh = world.Machines.FirstOrDefault(m => !known.Contains(m.Id))) is not null);

        Assert.Equal(125, fresh!.Alert.Level);
    }

    [Fact]
    public void A_machine_that_comes_back_regains_its_squadron()
    {
        var world = NewMatch();
        var at = Open(world);
        var grunt = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        grunt.Squad = 3;
        var defect = typeof(GameWorld).GetMethod("Defect", BindingFlags.NonPublic | BindingFlags.Instance)!;

        defect.Invoke(world, [grunt, Race.Blue]);
        Assert.Null(grunt.Squad);
        defect.Invoke(world, [grunt, Race.Red]);

        Assert.Equal(3, grunt.Squad);
    }
}
