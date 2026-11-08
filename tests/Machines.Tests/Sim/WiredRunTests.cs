using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The run of the survival mode Wired: its waves, bosses, wrecks and crates, and the hero's death.</summary>
public class WiredRunTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    public static readonly TheoryData<string> Arenas = new() { "m_desert", "m_green", "m_moon", "m_dales" };

    private static GameWorld NewRun(WiredOptions? options = null, int seed = 7, string arena = "m_desert") =>
        MatchSetup.Create(Data, arena, arena,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = options ?? new WiredOptions() });

    /// <summary>A run with the given wave just begun and its first group on the map.</summary>
    private static GameWorld AtWave(int wave, WiredOptions? options = null, int seed = 7, string arena = "m_desert")
    {
        var world = NewRun(options, seed, arena);
        world.Wired!.Wave = wave - 1;
        world.Wired.Countdown = 0;
        world.Step();
        world.Step();
        Assert.Equal((WiredPhase.Wave, wave), (world.Wired.Phase, world.Wired.Wave));
        return world;
    }

    /// <summary>The ground of an arena, where the export has been made; it is not part of the repository.</summary>
    private static void LoadGround(GameWorld world, string arena)
    {
        var repository = Path.GetFullPath(Path.Combine(RepoPaths.OriginalDataFolder(), "..", ".."));
        var heights = Path.Combine(repository, "classic", "planets", arena, "heights.bin");
        if (File.Exists(heights))
            world.Terrain = TerrainHeights.FromExport(File.ReadAllBytes(heights));
    }

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    private static Construction Pod(GameWorld world) => world.WiredPod(Race.Red)!;

    private static int Bank(GameWorld world) => (int)world.Races[Race.Red].Bmu;

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    /// <returns>All that happened in that time.</returns>
    private static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < seconds / GameWorld.Dt; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    private static void Kill(GameWorld world, Machine machine, Race by = Race.Red) => world.Damage(machine, 1_000_000, by);

    /// <summary>The wave's first machine alone: the rest of its group destroyed by no hero, the later groups called off.</summary>
    private static WiredFoe Alone(GameWorld world)
    {
        var run = world.Wired!;
        run.Groups.Clear();
        foreach (var other in run.Foes.Skip(1).ToList())
            Kill(world, other.Machine, Race.Blue);
        world.Step();
        return Assert.Single(run.Foes);
    }

    /// <summary>Destroys all the wave brings as it arrives, until it is over.</summary>
    private static List<GameEvent> ClearWave(GameWorld world)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < 20 * 120 && world.Wired!.Phase == WiredPhase.Wave; i++)
        {
            foreach (var foe in world.Wired.Foes)
                Kill(world, foe.Machine);
            world.Step();
            events.AddRange(world.Events);
        }
        Assert.NotEqual(WiredPhase.Wave, world.Wired!.Phase);
        return events;
    }

    private static Machine Grunt(GameWorld world, Vector2 at, Race race = Race.Blue) =>
        world.AddMachine(race, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    private static Vector2 FreeAt(GameWorld world, Vector2 near) => world.Nav.NearestFreePoint(near, crossesWater: false)!.Value;

    // ---- what the waves are made of ---------------------------------------------------------------

    [Fact]
    public void A_wave_has_the_budget_of_its_number_and_difficulty()
    {
        Assert.Equal(40, WiredBalance.Budget(1, WiredDifficulty.Normal));
        Assert.Equal(46.8f, WiredBalance.Budget(2, WiredDifficulty.Normal), 0.01f);
        Assert.Equal(164.3f, WiredBalance.Budget(10, WiredDifficulty.Normal), 0.1f);
        Assert.Equal(52, WiredBalance.Budget(1, WiredDifficulty.Hard), 0.01f);
        Assert.Equal(64, WiredBalance.Budget(1, WiredDifficulty.Hardcore), 0.01f);
    }

    [Fact]
    public void The_first_wave_is_a_crowd_of_weak_grunts()
    {
        var plan = WiredDirector.Plan(Data, 1, WiredDifficulty.Normal, new Random(1));

        var foes = plan.Groups.SelectMany(g => g.Machines).ToList();
        Assert.Equal(WiredBalance.CrowdSize, foes.Count);
        Assert.All(foes, foe => Assert.Equal(("GRUNT", 1, 1f / WiredBalance.CrowdSize), (foe.Key.SubType, foe.Key.HardwareLevel, foe.HitPoints)));
        Assert.Null(plan.Boss);
    }

    [Fact]
    public void Waves_bring_what_their_tier_allows_for_what_they_can_pay()
    {
        for (var wave = 1; wave <= 60; wave++)
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                var plan = WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(seed));
                var bought = plan.Groups.SelectMany(g => g.Machines).Where(m => m.Boss is null && !IsGuard(m, plan.Boss)).ToList();

                Assert.NotEmpty(bought);
                Assert.InRange(bought.Count, 1, WiredBalance.MostInWave);
                Assert.All(bought, m => Assert.Contains(WiredCatalogue.Tiers, t => t.Wave <= wave && t.Offers(m.Key)));
                Assert.True(bought.Sum(m => Data.Parm.Machine(m.Key.TypeName).Level(m.Key.HardwareLevel).Cost * m.Worth) <= MathF.Max(plan.Budget, 35) + 0.01f);
            }
        }

        static bool IsGuard(WiredFoePlan machine, WiredBoss? boss) => boss?.Guard == machine.Key && machine is { Elite: false };
    }

    [Fact]
    public void A_big_wave_stops_at_sixty_machines()
    {
        var plan = WiredDirector.Plan(Data, 59, WiredDifficulty.Hardcore, new Random(1));

        Assert.Equal(WiredBalance.MostInWave, plan.Groups.Sum(g => g.Machines.Count));
    }

    [Fact]
    public void A_wave_arrives_in_small_groups_over_a_minute_to_a_minute_and_a_half()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var plan = WiredDirector.Plan(Data, seed + 8, WiredDifficulty.Normal, new Random(seed));
            if (plan.Boss is not null)
                continue;

            Assert.True(plan.Groups.Count >= Math.Min(WiredBalance.FewestGroups, plan.Groups.Sum(g => g.Machines.Count)));
            Assert.True(plan.Groups.Count > 1);
            Assert.Equal(0, plan.Groups[0].After);
            Assert.InRange(plan.Groups[^1].After, WiredBalance.LeastSpread, WiredBalance.MostSpread);
            Assert.All(plan.Groups, g => Assert.InRange(g.Machines.Count, 1, WiredBalance.GroupSize));
            Assert.Equal(plan.Groups.Select(g => g.After).Order(), plan.Groups.Select(g => g.After));
        }
    }

    [Fact]
    public void The_first_wave_arrives_in_two_groups_close_together()
    {
        var plan = WiredDirector.Plan(Data, 1, WiredDifficulty.Normal, new Random(1));

        Assert.Equal(WiredBalance.CrowdGroups, plan.Groups.Count);
        Assert.Equal(0, plan.Groups[0].After);
        Assert.Equal(WiredBalance.CrowdSpread, plan.Groups[^1].After);
        Assert.All(plan.Groups, g => Assert.Equal(WiredBalance.CrowdSize / WiredBalance.CrowdGroups, g.Machines.Count));
    }

    [Fact]
    public void Plans_depend_on_the_random_numbers_alone()
    {
        static string Plan(int seed) => string.Join(";", WiredDirector.Plan(Data, 22, WiredDifficulty.Normal, new Random(seed)).Groups
            .SelectMany(g => g.Machines.Select(m => $"{g.After}:{m.Key}:{m.Elite}")));

        Assert.Equal(Plan(3), Plan(3));
        Assert.NotEqual(Plan(3), Plan(4));
    }

    [Theory]
    [InlineData(5, "The Crusher", 8f, 1, 0)]
    [InlineData(10, "Ironfist", 8f, 1, 0)]
    [InlineData(15, "The Traitor", 7f, 1, 4)]
    [InlineData(20, "Primus", 3f, 1, 0)]
    [InlineData(25, "The Twins", 4f, 2, 0)]
    [InlineData(30, "The Queen", 8f, 1, 6)]
    [InlineData(35, "The Crusher", 12.8f, 1, 0)]
    [InlineData(65, "The Crusher", 20.48f, 1, 0)]
    public void Bosses_come_every_fifth_wave_and_the_list_repeats_stronger(int wave, string name, float hitPoints, int count, int guards)
    {
        var plan = WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(1));

        var first = plan.Groups[0].Machines;
        Assert.Equal(name, plan.Boss!.Name);
        Assert.Equal(count, first.Count(m => m.Boss is not null));
        Assert.All(first.Where(m => m.Boss is not null), m => Assert.Equal(hitPoints, m.HitPoints, 0.01f));
        Assert.Equal(guards, plan.Boss.Guards);
        Assert.All(first.Skip(count).Take(guards), m => Assert.Equal(plan.Boss.Guard, m.Key));
        Assert.DoesNotContain(plan.Groups.Skip(1).SelectMany(g => g.Machines), m => m.Boss is not null);
    }

    [Fact]
    public void Other_waves_have_no_boss()
    {
        Assert.Null(WiredDirector.BossOf(4));
        Assert.Null(WiredDirector.BossOf(6));
        Assert.Null(WiredDirector.BossOf(0));
    }

    [Fact]
    public void Everything_the_waves_bring_can_fight()
    {
        var world = NewRun();
        var keys = WiredCatalogue.Tiers.SelectMany(t => t.Combos.Select(c => new ResearchKey(t.Type, t.SubType, t.Level, c)))
            .Concat(WiredDirector.Bosses.Select(b => b.Machine))
            .Concat(WiredDirector.Bosses.Select(b => b.Guard).OfType<ResearchKey>());

        foreach (var key in keys)
        {
            var machine = world.AddMachine(Race.Blue, Pod(world).Position, key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo);
            Assert.True(machine.CanFight, key.ToString());
        }
    }

    [Fact]
    public void Elites_grow_only_after_the_thirtieth_wave()
    {
        Assert.Equal(1, WiredBalance.EliteStrength(30));
        Assert.Equal(1.04f, WiredBalance.EliteStrength(31), 0.001f);
        Assert.Equal(1.4f, WiredBalance.EliteStrength(40), 0.001f);

        var plan = WiredDirector.Plan(Data, 44, WiredDifficulty.Normal, new Random(2));
        var machines = plan.Groups.SelectMany(g => g.Machines).ToList();
        Assert.Contains(machines, m => m.Elite);
        Assert.All(machines.Where(m => m.Elite), m => Assert.Equal(WiredBalance.EliteHitPoints * 1.56f, m.HitPoints, 0.01f));
        Assert.All(machines.Where(m => !m.Elite), m => Assert.Equal((1f, 1f), (m.HitPoints, m.Damage)));
    }

    // ---- the start --------------------------------------------------------------------------------

    [Fact]
    public void Nobody_but_the_director_leads_the_enemy()
    {
        var world = NewRun();

        Assert.Empty(world.AiControllers);
        Assert.Equal(Race.Blue, world.Wired!.Enemy);
        Assert.DoesNotContain(world.Machines, m => m.Race == Race.Blue);
        Assert.DoesNotContain(world.Constructions, c => c.Race == Race.Blue);
        Assert.Empty(world.Races[Race.Red].Allies);
    }

    [Fact]
    public void A_run_with_only_a_player_gets_an_enemy()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player }, 7,
            new MatchOptions { Wired = new WiredOptions() });

        Assert.Equal(Race.Blue, world.Wired!.Enemy);
        Assert.Equal(Controller.Ai, world.Races[Race.Blue].Controller);
        Assert.Empty(world.AiControllers);
        Run(world, WiredBalance.PrepareTime + 1);
        Assert.Contains(world.Machines, m => m.Race == Race.Blue);
    }

    [Theory]
    [MemberData(nameof(Arenas))]
    public void Spawn_points_are_far_from_the_pod_and_a_way_leads_there(string arena)
    {
        var world = NewRun(arena: arena);
        var door = Pod(world).EntranceOutside!.Value;

        var points = world.Wired!.SpawnPoints;
        Assert.True(points.Count >= 3, $"{points.Count} spawn points");
        foreach (var point in points)
        {
            Assert.True(Vector2.Distance(point, door) >= WiredBalance.SpawnDistance);
            Assert.True(world.Nav.IsFree(point, crossesWater: false));
            var way = world.Nav.FindPath(point, door, crossesWater: false, int.MaxValue);
            Assert.True(Vector2.Distance(way[^1], door) <= 2 * NavGrid.CellSize);
        }
    }

    [Theory]
    [MemberData(nameof(Arenas))]
    public void The_other_starts_of_the_scenario_come_first(string arena)
    {
        var world = NewRun(arena: arena);
        var blue = Data.LoadScenario(arena).Race(Race.Blue)!.Constructions.First(b => b.Type == "POD").Position;

        Assert.True(Vector2.Distance(world.Wired!.SpawnPoints[0], new Vector2(blue.X, blue.Y)) < 20);
    }

    // ---- a wave -----------------------------------------------------------------------------------

    [Fact]
    public void The_first_wave_comes_after_the_preparation()
    {
        var world = NewRun();

        var events = Run(world, WiredBalance.PrepareTime + 0.2f);

        var started = Assert.Single(events, e => e.Kind == GameEventKind.WiredWaveStarted);
        Assert.Equal((1, null), (started.Other, started.Detail));
        Assert.Equal(WiredBalance.CrowdSize / WiredBalance.CrowdGroups, world.Wired!.Foes.Count);
        var foe = world.Wired.Foes[0];
        Assert.Equal((Race.Blue, DefCon.High), (foe.Machine.Race, foe.Machine.DefCon));
        Assert.Contains(world.Wired.SpawnPoints, p => Vector2.Distance(p, foe.Machine.Position) < 30);
    }

    [Theory]
    [MemberData(nameof(Arenas))]
    public void The_first_wave_arrives_near_the_pod_and_not_at_the_edge_of_the_map(string arena)
    {
        var world = NewRun(arena: arena);
        var door = Pod(world).EntranceOutside!.Value;

        Run(world, WiredBalance.PrepareTime + 0.2f);

        Assert.NotEmpty(world.Wired!.Foes);
        Assert.All(world.Wired.Foes, foe => Assert.InRange(Vector2.Distance(foe.Machine.Position, door), WiredBalance.SpawnDistance - 30, WiredBalance.SpawnReach(1) + 30));
    }

    [Fact]
    public void A_wave_arrives_group_by_group()
    {
        var world = AtWave(18);
        var run = world.Wired!;
        var (groups, left) = (run.Groups.Count, run.EnemiesLeft);
        Assert.True(groups > 0);
        Assert.Equal(left - run.Groups.Sum(g => g.Machines.Count), run.Foes.Count);

        Run(world, run.Groups[0].After + 0.2f);

        Assert.True(run.Groups.Count < groups);
        Assert.Equal(left, run.EnemiesLeft);
    }

    [Fact]
    public void About_a_third_goes_for_the_hero_and_the_rest_for_the_pod()
    {
        var world = AtWave(40);
        var run = world.Wired!;
        var pod = Pod(world);

        // Sixty of the last tiers make short work of hero and pod, and the run would end before all are there.
        for (var i = 0; i < 20 * (WiredBalance.MostSpread + 1); i++)
        {
            pod.HitPoints = pod.MaxHitPoints;
            Red(world).SpareBodies = 5;
            world.Step();
        }

        Assert.Empty(run.Groups);
        Assert.True(run.Foes.Count >= 40, $"{run.Foes.Count} machines");
        var hunters = run.Foes.Count(f => f.HuntsHero);
        Assert.InRange(hunters * 100 / run.Foes.Count, 15, 45);
    }

    [Fact]
    public void Those_on_their_way_march_on_the_pod_and_hunters_on_the_hero()
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var world = AtWave(1, seed: seed);
            var foe = Alone(world);

            if (foe.HuntsHero)
            {
                var hunt = Assert.IsType<AttackOrder>(foe.Machine.Order);
                Assert.Equal(Hero(world).Id, hunt.Target);
                Assert.Equal(Pod(world).EntranceOutside, hunt.Then);
            }
            else
                Assert.IsType<AttackMoveOrder>(foe.Machine.Order);
        }
    }

    [Fact]
    public void The_idle_get_new_orders()
    {
        var world = AtWave(1);
        var foe = Alone(world);

        foe.Machine.Order = Order.Idle;
        Run(world, WiredBalance.OrdersEvery + 0.1f);

        Assert.IsNotType<IdleOrder>(foe.Machine.Order);
    }

    [Theory]
    [MemberData(nameof(Arenas))]
    public void A_machine_that_hangs_is_fetched_to_a_place_the_pod_can_be_reached_from(string arena)
    {
        var world = AtWave(1, arena: arena);
        var run = world.Wired!;
        var foe = Alone(world);
        var stuck = foe.Machine.Position;

        GameEvent? fetched = null;
        for (var i = 0; i < 20 * 80 && fetched is null; i++)
        {
            foe.Machine.Position = stuck;
            world.Step();
            fetched = world.Events.FirstOrDefault(e => e.Kind == GameEventKind.WiredFoePulled);
        }

        Assert.NotNull(fetched);
        Assert.Equal(foe.Machine.Id, fetched.Actor);
        Assert.InRange(world.Time - run.LastGroupAt, WiredBalance.StandingTime, WiredBalance.StandingTime + WiredBalance.OrdersEvery + 1);
        Assert.InRange(Vector2.Distance(foe.Machine.Position, Pod(world).Position), WiredBalance.PullDistance - 30, WiredBalance.PullDistance + 30);
        var (place, door) = (world.Nav.Cell(foe.Machine.Position), world.Nav.Cell(Pod(world).EntranceOutside!.Value));
        Assert.Equal(world.Nav.Region(door.X, door.Y, false), world.Nav.Region(place.X, place.Y, false));
        Assert.IsNotType<IdleOrder>(foe.Machine.Order);
    }

    [Fact]
    public void Even_a_busy_machine_is_fetched_at_the_last_call_but_only_once()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        var foe = Alone(world);
        var far = foe.Machine.Position;

        var fetches = new List<float>();
        for (var i = 0; i < 20 * 215; i++)
        {
            if (fetches.Count == 0)
                foe.Machine.Position = far + new Vector2(world.Tick % 7, 0);
            world.Step();
            if (world.Events.Any(e => e.Kind == GameEventKind.WiredFoePulled))
                fetches.Add(world.Time - run.LastGroupAt);
        }

        var last = WiredBalance.StandingTime + WiredBalance.LastCall;
        Assert.InRange(Assert.Single(fetches), last, last + WiredBalance.OrdersEvery + 1);
    }

    [Fact]
    public void What_stands_near_the_pod_is_not_fetched()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        var foe = Alone(world);
        var near = FreeAt(world, Pod(world).Position + new Vector2(60, 0));

        for (var i = 0; i < 20 * 60; i++)
        {
            foe.Machine.Position = near;
            foe.Machine.Weapons.ForEach(w => w.Reload = 0);
            world.Step();
            Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.WiredFoePulled);
        }
    }

    [Fact]
    public void A_wave_ends_when_none_of_it_is_left_and_pays_its_bonus()
    {
        var world = AtWave(6);
        var run = world.Wired!;
        var bank = Bank(world);

        var events = ClearWave(world);

        var cleared = Assert.Single(events, e => e.Kind == GameEventKind.WiredWaveCleared);
        Assert.Equal((6, "68"), (cleared.Other, cleared.Detail));
        Assert.Equal(WiredPhase.Intermission, run.Phase);
        Assert.True(Bank(world) >= bank + 68);
        Assert.Equal(1, Red(world).Stats.WavesCleared);
        Assert.Equal(68, Red(world).Stats.BmusEarned);
    }

    [Fact]
    public void After_the_intermission_comes_the_next_wave()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        ClearWave(world);
        Assert.Equal(WiredBalance.IntermissionTime, run.Countdown, 0.1f);

        var events = Run(world, WiredBalance.IntermissionTime + 0.2f);

        Assert.Equal(2, Assert.Single(events, e => e.Kind == GameEventKind.WiredWaveStarted).Other);
        Assert.Equal((WiredPhase.Wave, 2), (run.Phase, run.Wave));
    }

    [Fact]
    public void An_open_hangar_holds_the_intermission_and_ready_pays_for_the_rest()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        ClearWave(world);
        Hero(world).Position = Pod(world).EntranceOutside!.Value;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        var left = run.Countdown;

        Run(world, 40);

        Assert.Equal(left, run.Countdown);
        var earned = Red(world).Stats.BmusEarned;
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));

        // That time was taken, even if the clock stood: nothing was saved.
        Assert.Equal(earned, Red(world).Stats.BmusEarned);
        Run(world, 0.2f);
        Assert.Equal(2, run.Wave);
    }

    [Fact]
    public void A_wave_with_nowhere_to_arrive_ends_at_once()
    {
        var world = NewRun();
        world.Wired!.SpawnPoints.Clear();

        var events = Run(world, WiredBalance.PrepareTime + 0.2f);

        Assert.Single(events, e => e.Kind == GameEventKind.WiredWaveCleared);
        Assert.Equal(WiredPhase.Intermission, world.Wired.Phase);
    }

    [Fact]
    public void A_machine_that_comes_over_to_the_enemy_joins_the_wave()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        var turned = Grunt(world, FreeAt(world, Pod(world).Position + new Vector2(300, 200)));
        turned.DefCon = DefCon.Normal;

        Run(world, WiredBalance.OrdersEvery + 0.1f);

        Assert.Contains(run.Foes, f => f.Machine == turned);
        Assert.Equal(DefCon.High, turned.DefCon);
        foreach (var foe in run.Foes.Where(f => f.Machine != turned))
            Kill(world, foe.Machine);
        Run(world, 1);
        Assert.Equal(WiredPhase.Wave, run.Phase);
    }

    // ---- bosses and elites ------------------------------------------------------------------------

    [Fact]
    public void A_boss_has_stats_of_its_own_and_the_type_stays_as_it_was()
    {
        var shared = Data.Parm.Machine("AGGRESSOR BALLISTA").Level(3);
        var (hitPoints, armour) = (shared.HitPoints, shared.Armour);

        var world = NewRun();
        world.Wired!.Wave = 4;
        world.Wired.Countdown = 0;
        var events = Run(world, 0.2f);

        var boss = Assert.Single(world.Wired.Bosses);
        Assert.Equal("The Crusher", boss.Plan.Boss!.Name);
        Assert.Equal("The Crusher", world.Wired.Boss!.Name);
        Assert.Equal("The Crusher", Assert.Single(events, e => e.Kind == GameEventKind.WiredWaveStarted).Detail);
        var appeared = Assert.Single(events, e => e.Kind == GameEventKind.WiredBossAppeared);
        Assert.Equal((boss.Machine.Id, "The Crusher"), (appeared.Actor, appeared.Detail));
        Assert.Equal(hitPoints * 8, boss.Machine.MaxHitPoints);
        Assert.Equal(boss.Machine.MaxHitPoints, boss.Machine.HitPoints);
        Assert.NotSame(shared, boss.Machine.Stats);
        Assert.Equal((hitPoints, armour), (shared.HitPoints, shared.Armour));
        Assert.Equal(hitPoints, Data.Parm.Machine("AGGRESSOR BALLISTA").Level(3).HitPoints);
    }

    [Fact]
    public void An_elite_is_stronger_on_its_own_copies()
    {
        for (var seed = 1; ; seed++)
        {
            Assert.True(seed < 40, "no elite came");
            var world = AtWave(11, seed: seed);
            Run(world, WiredBalance.MostSpread + 1);
            if (world.Wired!.Foes.FirstOrDefault(f => f.Plan.Elite) is not { } elite)
                continue;

            var key = elite.Plan.Key;
            var shared = Data.Parm.Machine(key.TypeName).Level(key.HardwareLevel);
            Assert.Equal((int)(shared.HitPoints * WiredBalance.EliteHitPoints), elite.Machine.MaxHitPoints);
            foreach (var weapon in elite.Machine.Weapons)
            {
                var plain = Data.Parm.Weapons.First(w => w.TypeName == weapon.Spec.TypeName);
                Assert.NotSame(plain, weapon.Spec);
                Assert.Equal(plain.Damage * WiredBalance.EliteDamage, weapon.Spec.Damage, 0.01f);
            }
            return;
        }
    }

    [Fact]
    public void Some_elites_leave_a_crate_and_ordinary_machines_none()
    {
        var (elites, crates) = (0, 0);
        for (var seed = 1; seed <= 6; seed++)
        {
            var world = AtWave(44, seed: seed);
            var run = world.Wired!;
            elites += run.Foes.Count(f => f.Plan.Elite) + run.Groups.Sum(g => g.Machines.Count(m => m.Elite));
            ClearWave(world);
            crates += world.WiredPickups.Count;
        }

        Assert.True(elites >= 20, $"{elites} elites");
        Assert.InRange(crates, 1, elites / 2);

        var early = AtWave(4);
        ClearWave(early);
        Assert.Empty(early.WiredPickups);
    }

    [Fact]
    public void A_boss_below_half_calls_for_help_once()
    {
        var world = AtWave(5);
        var run = world.Wired!;
        var boss = Assert.Single(run.Bosses);
        var left = run.EnemiesLeft;

        boss.Machine.HitPoints = boss.Machine.MaxHitPoints / 2 + 1;
        Assert.DoesNotContain(Run(world, 1), e => e.Kind == GameEventKind.WiredBossBelowHalf);

        boss.Machine.HitPoints = boss.Machine.MaxHitPoints / 2 - 1;
        var events = Run(world, 8);

        var call = Assert.Single(events, e => e.Kind == GameEventKind.WiredBossBelowHalf);
        Assert.Equal((boss.Machine.Id, "The Crusher"), (call.Actor, call.Detail));
        Assert.InRange(run.EnemiesLeft - left, 1, WiredBalance.MostReinforcements);
        Assert.True(run.LastGroupAt > run.WaveStarted);
    }

    [Fact]
    public void A_boss_leaves_a_crate_a_credit_and_a_choice_of_chips()
    {
        var world = AtWave(5);
        var run = world.Wired!;
        var boss = Assert.Single(run.Bosses);
        var cost = boss.Machine.Stats.Cost;

        Kill(world, boss.Machine);
        Run(world, 0.2f);

        Assert.Empty(run.Bosses);
        Assert.Equal(1, Red(world).Stats.BossesKilled);
        Assert.True(Red(world).Stats.KillValue >= cost);
        var crate = Assert.Single(world.WiredPickups);
        Assert.Equal(5, crate.Wave);

        var events = ClearWave(world);

        var ready = Assert.Single(events, e => e.Kind == GameEventKind.WiredChipChoiceReady);
        Assert.Equal((int)Race.Red, ready.Other);
        Assert.Equal(WiredChips.ChoiceSize, Red(world).ChipChoice.Count);
        Assert.Null(run.Boss);
    }

    [Fact]
    public void A_wave_without_a_boss_offers_no_chips()
    {
        var world = AtWave(4);

        var events = ClearWave(world);

        Assert.Empty(Red(world).ChipChoice);
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.WiredChipChoiceReady);
    }

    [Fact]
    public void The_twins_come_as_two_and_both_must_fall()
    {
        var world = AtWave(25);

        Assert.Equal(2, world.Wired!.Bosses.Count());
        Kill(world, world.Wired.Bosses.First().Machine);
        Run(world, 0.2f);
        Assert.Single(world.Wired.Bosses);
        Assert.Single(world.WiredPickups);
    }

    [Fact]
    public void The_traitors_overseers_stay_with_it_and_heal_it()
    {
        var world = AtWave(15);
        var run = world.Wired!;
        var boss = Assert.Single(run.Bosses);
        var healers = run.Foes.Where(f => f.Plan.Healer).ToList();
        Assert.Equal(4, healers.Count);
        Assert.All(healers, h => Assert.Equal(boss.Machine.Id, Assert.IsType<FollowOrder>(h.Machine.Order).Target));

        boss.Machine.HitPoints = boss.Machine.MaxHitPoints * 3 / 4;
        var hurt = boss.Machine.HitPoints;
        Run(world, WiredBalance.OrdersEvery + 0.1f);
        Assert.Contains(healers, h => h.Machine.Order is HealOrder);
        Run(world, 25);

        Assert.True(boss.Machine.HitPoints > hurt);
    }

    [Fact]
    public void Healers_go_on_as_the_rest_when_their_boss_is_gone()
    {
        var world = AtWave(15);
        var run = world.Wired!;
        var healer = run.Foes.First(f => f.Plan.Healer);

        Kill(world, Assert.Single(run.Bosses).Machine);
        Run(world, 2 * WiredBalance.OrdersEvery);

        Assert.True(healer.Machine.Order is AttackMoveOrder or AttackOrder, healer.Machine.Order.GetType().Name);
    }

    // ---- wrecks -----------------------------------------------------------------------------------

    /// <summary>A destroyed grunt's wreck where the hero is not.</summary>
    private static DebrisState Wreck(GameWorld world)
    {
        var grunt = Grunt(world, FreeAt(world, Pod(world).Position + new Vector2(120, 0)));
        Kill(world, grunt);
        world.Step();
        return world.Debris[^1];
    }

    [Fact]
    public void Driving_over_a_wreck_pays_its_bmus()
    {
        var world = NewRun();
        var wreck = Wreck(world);
        var bank = Bank(world);
        Assert.Equal(Data.Parm.Machine("AGGRESSOR GRUNT").Level(1).Cost / 2, wreck.Quantity);

        Hero(world).Position = wreck.Position + new Vector2(0, Hero(world).Radius + WiredBalance.PickupReach - 0.1f);
        var events = Run(world, 0.05f);

        Assert.DoesNotContain(wreck, world.Debris);
        Assert.Equal(bank + 17, Bank(world));
        var collected = Assert.Single(events, e => e.Kind == GameEventKind.WiredWreckCollected);
        Assert.Equal((Hero(world).Id, wreck.Id, "17"), (collected.Actor, collected.Other, collected.Detail));
        Assert.Equal((1, 17), (Red(world).Stats.WrecksCollected, Red(world).Stats.BmusEarned));
    }

    [Fact]
    public void A_wreck_out_of_reach_stays_and_burns_out()
    {
        var world = NewRun();
        var wreck = Wreck(world);
        var bank = Bank(world);

        Hero(world).Position = wreck.Position + new Vector2(0, Hero(world).Radius + WiredBalance.PickupReach + 0.2f);
        Run(world, 1);

        Assert.Contains(wreck, world.Debris);
        Assert.Equal(bank, Bank(world));
        Run(world, GameWorld.DebrisBurnTime);
        Assert.DoesNotContain(wreck, world.Debris);
    }

    [Fact]
    public void The_scrap_collector_makes_a_wreck_worth_more()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 1000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Red(world).Offer.Add("scrap");
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "scrap")));
        var scrap = Red(world).Items.Single(i => i.Catalogue == "scrap");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, scrap.Id, WiredSlot.Module(0))));
        var wreck = Wreck(world);
        var bank = Bank(world);

        Hero(world).Position = wreck.Position;
        Run(world, 0.05f);

        Assert.Equal(bank + (int)(17 * 1.5f), Bank(world));
    }

    [Fact]
    public void A_flyer_leaves_a_wreck_in_a_run_on_ground_the_hero_can_reach()
    {
        var world = NewRun();
        var over = BlockedPlace(world);
        var wasp = world.AddMachine(Race.Blue, over, "ADMINISTRATOR", "BOSS", 1, "T_WASP_LIGHT_STING");

        Kill(world, wasp);
        world.Step();

        var wreck = Assert.Single(world.Debris);
        Assert.Equal(wasp.Stats.Cost / 2, wreck.Quantity);
        Assert.True(world.Nav.IsFree(wreck.Position, crossesWater: false));
    }

    private static Vector2 BlockedPlace(GameWorld world)
    {
        for (var y = 0; y < world.Nav.Depth; y++)
        {
            for (var x = 0; x < world.Nav.Width; x++)
            {
                if (!world.Nav.IsFree(x, y, crossesWater: false) && world.Nav.NearestFreePoint(world.Nav.Centre(x, y), false) is not null)
                    return world.Nav.Centre(x, y);
            }
        }
        throw new InvalidOperationException("nothing is blocked");
    }

    [Fact]
    public void A_flyer_leaves_no_wreck_in_a_normal_match()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7);
        var wasp = world.AddMachine(Race.Blue, new Vector2(800, 800), "ADMINISTRATOR", "BOSS", 1, "T_WASP_LIGHT_STING");

        Kill(world, wasp);
        world.Step();

        Assert.Empty(world.Debris);
    }

    [Fact]
    public void Artefacts_are_no_source_of_bmus_in_a_run()
    {
        var world = NewRun();
        var artefact = world.Artefacts.First(a => a.Cost > 0);

        world.Damage(artefact, 1_000_000, Race.Red);
        world.Step();

        Assert.True(artefact.IsDead);
        Assert.Empty(world.Debris);
    }

    // ---- crates -----------------------------------------------------------------------------------

    /// <summary>A run with a crate of the given wave right at the hero's feet, after a boss that fell.</summary>
    private static GameWorld WithCrate(int wave, int seed = 7, WiredOptions? options = null)
    {
        var world = AtWave(wave, options, seed);
        var boss = world.Wired!.Bosses.First();
        boss.Machine.Position = FreeAt(world, Hero(world).Position + new Vector2(150, 0));
        Kill(world, boss.Machine);
        world.Step();
        return world;
    }

    [Fact]
    public void Driving_over_a_crate_puts_its_content_in_the_inventory()
    {
        var world = WithCrate(5);
        var crate = Assert.Single(world.WiredPickups);
        var owned = Red(world).Items.Count;

        Hero(world).Position = crate.Position;
        var events = Run(world, 0.05f);

        Assert.Empty(world.WiredPickups);
        Assert.Equal(owned + 1, Red(world).Items.Count);
        var item = Red(world).Items[^1];
        var collected = Assert.Single(events, e => e.Kind == GameEventKind.WiredCrateCollected);
        Assert.Equal((crate.Id, item.Catalogue), (collected.Other, collected.Detail));
        Assert.Equal(1, Red(world).Stats.CratesCollected);
    }

    [Fact]
    public void A_full_inventory_leaves_the_crate_on_the_ground()
    {
        var world = WithCrate(5);
        var state = Red(world);
        while (state.Inventory.Count() < WiredBalance.InventoryLimit)
            state.Add(WiredKind.Weapon, "BOLTER", 0);

        Hero(world).Position = world.WiredPickups[0].Position;
        Run(world, 0.2f);

        Assert.Single(world.WiredPickups);
        Assert.Equal(WiredBalance.InventoryLimit, state.Inventory.Count());
    }

    [Fact]
    public void What_a_crate_holds_rises_with_the_wave_and_is_never_a_chassis()
    {
        static double MeanPrice(int wave)
        {
            var random = new Random(11);
            return Enumerable.Range(0, 400).Select(_ => WiredDirector.Crate(wave, [], random)).Average(c => WiredCatalogue.Price(c.Catalogue));
        }

        Assert.True(MeanPrice(25) > MeanPrice(1));
        var random = new Random(12);
        var rolls = Enumerable.Range(0, 400).Select(_ => WiredDirector.Crate(25, WiredChips.BasePool, random)).ToList();
        Assert.Contains(rolls, r => WiredCatalogue.KindOf(r.Catalogue) == WiredKind.Chip);
        Assert.Contains(rolls, r => WiredCatalogue.KindOf(r.Catalogue) == WiredKind.Weapon);
        Assert.Contains(rolls, r => WiredCatalogue.KindOf(r.Catalogue) == WiredKind.Module);
        Assert.DoesNotContain(rolls, r => WiredCatalogue.KindOf(r.Catalogue) is null or WiredKind.Chassis);
        Assert.Contains(rolls, r => r.Mk == 3);
        Assert.All(Enumerable.Range(0, 200).Select(_ => WiredDirector.Crate(9, [], random)), r => Assert.Equal(1, r.Mk));
    }

    [Fact]
    public void Loot_sells_for_half_of_what_the_shop_asks_and_a_chip_for_nothing()
    {
        var world = NewRun(new WiredOptions { Difficulty = WiredDifficulty.Hard });
        var state = Red(world);

        var cannon = state.AddLoot("PLASMA_RIFLE", 1);
        var better = state.AddLoot("PLASMA_RIFLE", 3);
        var chip = state.AddLoot("savings", 1);

        var price = state.Priced(WiredCatalogue.Price("PLASMA_RIFLE"));
        Assert.Equal(price, cannon.Paid);
        Assert.Equal(price / 2, state.SellValue(cannon));
        var stored = state.Item(better.Id)!;
        Assert.Equal(3, stored.Mk);
        Assert.True(stored.Paid > price);
        Assert.Equal(0, state.SellValue(chip));
    }

    // ---- the hero's death -------------------------------------------------------------------------

    [Fact]
    public void A_fallen_hero_loses_the_bank_and_a_body_and_comes_back_after_six_seconds()
    {
        var world = NewRun();
        var state = Red(world);
        world.Races[Race.Red].Bmu = 300;
        var (old, loadout, spare) = (Hero(world), state.Loadout, state.SpareBodies);

        Kill(world, old, Race.Blue);
        var events = Run(world, 0.05f);

        var died = Assert.Single(events, e => e.Kind == GameEventKind.WiredHeroDied);
        Assert.Equal((old.Id, (int)Race.Red, (spare - 1).ToString()), (died.Actor, died.Other, died.Detail));
        Assert.Equal(old.Position, died.Position);
        Assert.Null(world.HeroOf(Race.Red));
        Assert.Equal(0, Bank(world));
        Assert.Equal(spare - 1, state.SpareBodies);
        Assert.Equal(1, state.Stats.Deaths);
        Assert.Equal(WiredPhase.Prepare, world.Wired!.Phase);

        Assert.DoesNotContain(Run(world, WiredBalance.RespawnTime - 0.2f), e => e.Kind == GameEventKind.WiredHeroRespawned);
        var back = Assert.Single(Run(world, 0.4f), e => e.Kind == GameEventKind.WiredHeroRespawned);

        var hero = Hero(world);
        Assert.NotEqual(old.Id, hero.Id);
        Assert.Equal(hero.Id, back.Actor);
        Assert.Same(loadout, state.Loadout);
        Assert.Equal(old.WeaponCombo, hero.WeaponCombo);
        Assert.Equal(old.MaxHitPoints, hero.MaxHitPoints);
        Assert.Equal(hero.MaxHitPoints, hero.HitPoints);
        Assert.True(hero.DistanceTo(Pod(world)) <= WiredBalance.PodReach);
    }

    [Fact]
    public void A_refitted_hero_comes_back_as_it_last_was()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 1000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "grunt2")));
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, Red(world).Items.Single(i => i.Catalogue == "grunt2").Id)));
        var (max, combo) = (Hero(world).MaxHitPoints, Hero(world).WeaponCombo);

        Kill(world, Hero(world), Race.Blue);
        Run(world, WiredBalance.RespawnTime + 0.2f);

        Assert.Equal((max, combo), (Hero(world).MaxHitPoints, Hero(world).WeaponCombo));
    }

    [Fact]
    public void A_body_taken_by_something_at_the_door_comes_up_beside_it()
    {
        var world = NewRun();
        var door = Pod(world).EntranceOutside!.Value;
        var blocker = Grunt(world, door, Race.Red);

        Kill(world, Hero(world), Race.Blue);
        Run(world, WiredBalance.RespawnTime + 0.2f);

        Assert.True(Vector2.Distance(Hero(world).Position, blocker.Position) >= 3);
        Assert.True(world.Nav.IsFree(Hero(world).Position, crossesWater: false));
    }

    [Fact]
    public void The_savings_bank_keeps_half_the_bmus()
    {
        var world = NewRun();
        var state = Red(world);
        world.Races[Race.Red].Bmu = 300;
        var chip = state.Add(WiredKind.Chip, "savings", 0);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 0)));

        Kill(world, Hero(world), Race.Blue);
        Run(world, 0.05f);

        Assert.Equal(150, Bank(world));
    }

    [Fact]
    public void Escorts_fight_on_and_follow_the_new_body()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 1000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
        var escort = Assert.Single(world.Escorts(Race.Red));
        Assert.Equal(Hero(world).Id, Assert.IsType<FollowOrder>(escort.Order).Target);

        Kill(world, Hero(world), Race.Blue);
        Run(world, 1);
        Assert.False(escort.IsDead);
        Assert.IsType<FollowOrder>(escort.Order);
        Run(world, WiredBalance.RespawnTime);

        Assert.Equal(Hero(world).Id, Assert.IsType<FollowOrder>(escort.Order).Target);
    }

    [Fact]
    public void A_fight_an_escort_is_in_ends_with_the_new_body()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 1000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
        var escort = Assert.Single(world.Escorts(Race.Red));
        var old = Hero(world).Id;
        var foe = Grunt(world, FreeAt(world, Pod(world).Position + new Vector2(300, 200)));
        escort.Order = new AttackOrder(foe.Id, Back: new Interrupted(new FollowOrder(old), null, false));

        Kill(world, Hero(world), Race.Blue);
        for (var i = 0; i < 20 * 7 && world.HeroOf(Race.Red) is null; i++)
        {
            escort.Order = escort.Order is AttackOrder ? escort.Order : new AttackOrder(foe.Id, Back: new Interrupted(new FollowOrder(old), null, false));
            world.Step();
        }

        var fight = Assert.IsType<AttackOrder>(escort.Order);
        Assert.Equal(Hero(world).Id, Assert.IsType<FollowOrder>(fight.Back!.Order).Target);
    }

    /// <summary>An escort sent at a foe as the squad's attack key does it, with following the hero as the series' next step.</summary>
    private static (Machine Escort, Machine Foe) SquadOnAFoe(GameWorld world)
    {
        world.Races[Race.Red].Bmu = 1000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var escort = Assert.Single(world.Escorts(Race.Red));
        var hero = Hero(world);
        var foe = Grunt(world, FreeAt(world, hero.Position + new Vector2(25, 0)));
        Assert.Null(Do(world, new SeriesCommand(Race.Red, [escort.Id], [new AttackCommand(Race.Red, [], foe.Id), new FollowCommand(Race.Red, [], hero.Id)])));
        Assert.IsType<AttackOrder>(escort.Order);
        return (escort, foe);
    }

    private static void RunKeeping(GameWorld world, float seconds, Machine kept)
    {
        for (var i = 0; i < seconds / GameWorld.Dt; i++)
        {
            world.Step();
            kept.HitPoints = kept.MaxHitPoints;
        }
    }

    [Fact]
    public void A_squad_on_the_attack_goes_back_to_following_the_new_body()
    {
        var world = NewRun();
        var (escort, foe) = SquadOnAFoe(world);
        var old = Hero(world).Id;

        Kill(world, Hero(world), Race.Blue);
        RunKeeping(world, WiredBalance.RespawnTime + 0.5f, foe);
        var body = Hero(world);
        Assert.NotEqual(old, body.Id);
        Assert.IsType<AttackOrder>(escort.Order);

        Kill(world, foe, Race.Red);
        Run(world, 3);
        Assert.Equal(body.Id, Assert.IsType<FollowOrder>(escort.Order).Target);
    }

    [Fact]
    public void A_squad_on_the_attack_goes_back_to_following_a_hero_who_lived()
    {
        var world = NewRun();
        var (escort, foe) = SquadOnAFoe(world);

        Kill(world, foe, Race.Red);
        Run(world, 3);
        Assert.Equal(Hero(world).Id, Assert.IsType<FollowOrder>(escort.Order).Target);
    }

    [Fact]
    public void A_series_that_follows_another_machine_is_left_alone_by_a_new_body()
    {
        var world = NewRun();
        var (escort, foe) = SquadOnAFoe(world);
        var other = Grunt(world, FreeAt(world, Hero(world).Position + new Vector2(0, 25)));
        other.Race = Race.Red;
        Assert.Null(Do(world, new SeriesCommand(Race.Red, [escort.Id], [new AttackCommand(Race.Red, [], foe.Id), new FollowCommand(Race.Red, [], other.Id)])));

        Kill(world, Hero(world), Race.Blue);
        RunKeeping(world, WiredBalance.RespawnTime + 0.5f, foe);
        Kill(world, foe, Race.Red);
        Run(world, 3);
        Assert.Equal(other.Id, Assert.IsType<FollowOrder>(escort.Order).Target);
    }

    [Fact]
    public void The_last_body_ends_the_run()
    {
        var world = NewRun();
        var state = Red(world);

        for (var body = state.SpareBodies; body > 0; body--)
        {
            Kill(world, Hero(world), Race.Blue);
            Run(world, WiredBalance.RespawnTime + 0.2f);
            Assert.NotNull(world.HeroOf(Race.Red));
        }
        Assert.NotEqual(WiredPhase.Ended, world.Wired!.Phase);
        Kill(world, Hero(world), Race.Blue);
        var events = Run(world, 0.05f);

        Assert.Equal((WiredPhase.Ended, WiredEnding.OutOfBodies), (world.Wired.Phase, world.Wired.Ending));
        Assert.Equal(nameof(WiredEnding.OutOfBodies), Assert.Single(events, e => e.Kind == GameEventKind.WiredRunEnded).Detail);
        Assert.True(world.Races[Race.Red].Defeated);
        Assert.Null(world.Winner);
        Assert.DoesNotContain(Run(world, WiredBalance.RespawnTime + 1), e => e.Kind == GameEventKind.WiredHeroRespawned);
    }

    [Fact]
    public void Hardcore_ends_with_the_first_death()
    {
        var world = NewRun(new WiredOptions { Difficulty = WiredDifficulty.Hardcore });

        Kill(world, Hero(world), Race.Blue);
        Run(world, 0.05f);

        Assert.Equal(WiredEnding.OutOfBodies, world.Wired!.Ending);
    }

    [Fact]
    public void The_pods_loss_ends_the_run_with_the_hero_still_standing()
    {
        var world = NewRun();

        world.Damage(Pod(world), 1_000_000, Race.Blue);
        var events = Run(world, 0.05f);

        Assert.Equal((WiredPhase.Ended, WiredEnding.PodLost), (world.Wired!.Phase, world.Wired.Ending));
        Assert.Equal(nameof(WiredEnding.PodLost), Assert.Single(events, e => e.Kind == GameEventKind.WiredRunEnded).Detail);
    }

    [Fact]
    public void A_pod_lost_while_the_hero_waits_for_its_body_ends_the_run()
    {
        var world = NewRun();
        Kill(world, Hero(world), Race.Blue);
        Run(world, 1);

        world.Damage(Pod(world), 1_000_000, Race.Blue);
        Run(world, WiredBalance.RespawnTime);

        Assert.Equal(WiredEnding.PodLost, world.Wired!.Ending);
        Assert.Null(world.HeroOf(Race.Red));
    }

    [Fact]
    public void A_pod_lost_while_the_hero_is_down_leaves_no_body_to_wait_for()
    {
        var world = NewRun();
        Kill(world, Hero(world), Race.Blue);
        Run(world, 1);
        Assert.NotNull(Red(world).RespawnAt);

        world.Damage(Pod(world), 1_000_000, Race.Blue);
        Run(world, 0.1f);

        Assert.Equal(WiredPhase.Ended, world.Wired!.Phase);
        Assert.Null(Red(world).RespawnAt);
        Run(world, WiredBalance.RespawnTime);
        Assert.Null(world.HeroOf(Race.Red));
    }

    [Fact]
    public void Nothing_turns_the_hero()
    {
        var world = NewRun();
        var hero = Hero(world);
        var knight = world.AddMachine(Race.Blue, FreeAt(world, hero.Position + new Vector2(40, 0)), "AGGRESSOR", "KNIGHT", 4, "L_MULTI_LAUNCHER5_R_TREACHERY");
        knight.Weapons.RemoveAll(w => !w.IsTreachery);
        world.Issue(new AttackCommand(Race.Blue, [knight.Id], hero.Id));

        var events = Run(world, 20);

        Assert.Contains(events, e => e.Kind == GameEventKind.Fired && e.Detail == "ORB_OF_TREACHERY");
        Assert.Equal(Race.Red, hero.Race);
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.WiredHeroDied);
    }

    [Fact]
    public void Nothing_goes_for_recycling_in_a_run()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 1000;
        var hero = Hero(world);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
        var escort = Assert.Single(world.Escorts(Race.Red));

        Assert.NotNull(Do(world, new RecycleCommand(Race.Red, [hero.Id])));
        Assert.IsNotType<RecycleOrder>(hero.Order);

        Assert.NotNull(Do(world, new RecycleCommand(Race.Red, [escort.Id])));
        Assert.IsNotType<RecycleOrder>(escort.Order);

        // Nor does a machine the hero has turned, which would be money from nothing as well.
        var turned = Grunt(world, FreeAt(world, Pod(world).Position + new Vector2(60, 60)), Race.Red);
        Assert.NotNull(Do(world, new RecycleCommand(Race.Red, [turned.Id], Pod(world).Id)));
        Assert.IsNotType<RecycleOrder>(turned.Order);
    }

    [Fact]
    public void A_hero_that_destroys_itself_has_simply_died()
    {
        var world = NewRun();
        var old = Hero(world);

        Assert.Null(Do(world, new SelfDestructCommand(Race.Red, [old.Id])));
        var events = Run(world, 15);

        Assert.Contains(events, e => e.Kind == GameEventKind.WiredHeroDied && e.Actor == old.Id);
        Assert.Equal(1, Red(world).Stats.Deaths);
        Assert.NotNull(world.HeroOf(Race.Red));
    }

    [Fact]
    public void Clean_waves_count_from_the_last_fall_and_the_best_row_stays()
    {
        var world = AtWave(1);
        var stats = Red(world).Stats;
        void NextWave()
        {
            world.Wired!.Countdown = 0;
            world.Step();
            world.Step();
            ClearWave(world);
        }

        ClearWave(world);
        NextWave();
        Assert.Equal((2, 2), (stats.CleanStreak, stats.CleanWaves));

        Kill(world, Hero(world), Race.Blue);
        Run(world, 0.05f);
        Assert.Equal((0, 2), (stats.CleanStreak, stats.CleanWaves));

        Run(world, WiredBalance.RespawnTime + 1);
        NextWave();
        Assert.Equal((1, 2), (stats.CleanStreak, stats.CleanWaves));
        Assert.Equal(3, stats.WavesCleared);
    }

    [Fact]
    public void A_run_counts_its_openings_of_the_hangar_only_when_they_happen()
    {
        var world = NewRun();
        var state = Red(world);

        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));

        Assert.Equal(2, state.HangarOpens);
        var before = world.Checksum();
        state.HangarOpens++;
        Assert.NotEqual(before, world.Checksum());
    }

    // ---- free repair ------------------------------------------------------------------------------

    [Fact]
    public void Between_waves_the_hero_and_its_escorts_mend_near_the_pod()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 1000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        var escort = Assert.Single(world.Escorts(Race.Red));
        foreach (var machine in new[] { hero, escort })
            (machine.HitPoints, machine.Armour) = (1, 0);

        Run(world, 5);

        Assert.InRange(hero.HitPoints, 1 + hero.MaxHitPoints * 4 / 10, 1 + hero.MaxHitPoints * 6 / 10);
        Assert.True(escort.HitPoints > 1);
        Assert.True(hero.Armour >= hero.MaxArmour / 3, $"armour {hero.Armour}");
        Run(world, 10);
        Assert.Equal(hero.MaxHitPoints, hero.HitPoints);
        Assert.Equal(hero.MaxArmour, hero.Armour);
    }

    [Fact]
    public void Far_from_the_pod_nothing_mends()
    {
        var world = NewRun();
        var hero = Hero(world);
        hero.Position = Pod(world).Position + new Vector2(200, 200);
        hero.HitPoints = 1;

        Run(world, 5);

        Assert.Equal(1, hero.HitPoints);
    }

    [Fact]
    public void During_a_wave_nothing_mends()
    {
        var world = AtWave(1);
        var hero = Hero(world);
        hero.HitPoints = 1;

        Run(world, 5);

        Assert.Equal(1, hero.HitPoints);
    }

    // ---- score and statistics ---------------------------------------------------------------------

    [Theory]
    [InlineData(WiredDifficulty.Normal, 100 + 2 * 150 + 500 + 40)]
    [InlineData(WiredDifficulty.Hard, (100 + 2 * 150 + 500 + 40) * 3 / 2)]
    [InlineData(WiredDifficulty.Hardcore, (100 + 2 * 150 + 500 + 40) * 2)]
    public void The_score_counts_kills_waves_bosses_and_bmus(WiredDifficulty difficulty, int expected)
    {
        var stats = new WiredStats { KillValue = 100, WavesCleared = 2, BossesKilled = 1, BmusEarned = 80 };

        Assert.Equal(expected, WiredBalance.Score(stats, difficulty));
    }

    [Fact]
    public void The_hero_keeps_count_of_its_weapons()
    {
        var world = AtWave(1);
        var state = Red(world);
        var hero = Hero(world);
        var foe = Alone(world);
        foe.Machine.Position = FreeAt(world, hero.Position + new Vector2(20, 0));
        (foe.Machine.HitPoints, foe.Machine.Armour) = (3, 0);
        // A machine of the first waves' crowd counts for its share of what was bought.
        var cost = (int)(foe.Machine.Cost * foe.Plan.Worth);

        for (var i = 0; i < 20 * 30 && !foe.Machine.IsDead; i++)
            world.Step();
        world.Step();

        Assert.True(foe.Machine.IsDead);
        var bolter = state.Stats.Weapon("BOLTER");
        Assert.True(bolter.Rounds > 0);
        Assert.Equal(3, bolter.Damage);
        Assert.Equal(1, bolter.Kills);
        Assert.Equal((1, cost), (state.Stats.Kills, state.Stats.KillValue));
        Assert.Equal(3, state.Stats.DamageDealt);
        Assert.Equal(WiredBalance.Score(state.Stats, WiredDifficulty.Normal), state.Score);
    }

    [Fact]
    public void What_the_hero_takes_is_counted_and_what_friends_do_is_not()
    {
        var world = AtWave(1);
        var state = Red(world);
        var foe = Alone(world);

        world.Damage(Hero(world), 6, Race.Blue);
        world.Damage(foe.Machine, 4, Race.Blue);

        Assert.Equal(6, state.Stats.DamageTaken);
        Assert.Equal(0, state.Stats.DamageDealt);
    }

    [Fact]
    public void Kills_of_the_squad_count_for_the_run()
    {
        var world = AtWave(1);
        var foe = Alone(world);

        Kill(world, foe.Machine);
        world.Step();

        Assert.Equal(1, Red(world).Stats.Kills);
        Assert.Empty(Red(world).Stats.Weapons);
    }

    // ---- the checksum -----------------------------------------------------------------------------

    [Fact]
    public void The_wave_and_the_crates_are_in_the_checksum()
    {
        var (a, b) = (WithCrate(5), WithCrate(5));
        Assert.Equal(a.Checksum(), b.Checksum());

        b.WiredPickups.Clear();
        Assert.NotEqual(a.Checksum(), b.Checksum());

        var (c, d) = (AtWave(1), AtWave(1));
        Red(d).Stats.Kills++;
        Assert.NotEqual(c.Checksum(), d.Checksum());
    }

    [Fact]
    public void A_run_is_the_same_each_time()
    {
        static ulong Play()
        {
            var world = NewRun(seed: 3);
            Run(world, 200);
            return world.Checksum();
        }

        Assert.Equal(Play(), Play());
    }

    // ---- whole runs -------------------------------------------------------------------------------

    /// <summary>
    /// A whole run with a hero that drives away and back, on the real map and its ground if it was exported. Nothing is
    /// done by hand but what a player could do.
    /// </summary>
    [Theory]
    [MemberData(nameof(Arenas))]
    public void A_run_goes_its_whole_way(string arena)
    {
        var world = NewRun(arena: arena);
        LoadGround(world, arena);
        var run = world.Wired!;
        var pod = Pod(world);
        var scenario = Data.LoadScenario(arena).Race(Race.Red)!.Constructions.First(b => b.Type == "POD");
        Assert.Equal((scenario.Position.X, scenario.Position.Y), (pod.Position.X, pod.Position.Y));
        Assert.Equal(Data.LoadScenario(arena).Artefacts!.Placements.Count > 0, world.Artefacts.Any());

        // The hero is not stuck at its door.
        var hero = Hero(world);
        var door = hero.Position;
        var aside = FreeAt(world, door + (door - pod.Position) * 0.5f);
        world.Issue(new MoveCommand(Race.Red, [hero.Id], aside));
        Run(world, 12);
        Assert.True(Vector2.Distance(hero.Position, aside) < 10, $"{hero.Position} short of {aside}");
        // Back at the door, where it covers the pod: aside, the first waves' weak machines nibble at the pod's far side out of its reach.
        world.Issue(new MoveCommand(Race.Red, [hero.Id], door));

        var events = new List<GameEvent>();
        var podHit = false;
        for (var i = 0; i < 20 * 1800 && run.Phase != WiredPhase.Ended; i++)
        {
            world.Step();
            events.AddRange(world.Events);
            podHit |= pod.HitPoints < pod.MaxHitPoints;
        }

        Assert.True(podHit, "the waves never reached the pod");
        Assert.Equal(WiredPhase.Ended, run.Phase);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredWaveCleared);
        var waves = events.Where(e => e.Kind == GameEventKind.WiredWaveStarted).Select(e => e.Other!.Value).ToList();
        Assert.True(waves.Count >= 2, $"{waves.Count} waves");
        Assert.Equal(Enumerable.Range(1, waves.Count), waves);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredHeroDied);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredHeroRespawned);
        Assert.Single(events, e => e.Kind == GameEventKind.WiredRunEnded);
    }

    // ---- what the review found ----------------------------------------------------------------------

    private static Machine BoughtEscort(GameWorld world)
    {
        world.Races[Race.Red].Bmu = Math.Max(world.Races[Race.Red].Bmu, 1000);
        var open = Red(world).HangarOpen;
        if (!open)
            Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
        if (!open)
            Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        return world.Escorts(Race.Red).Last();
    }

    [Fact]
    public void Ready_pays_for_the_seconds_saved_and_not_for_those_the_hangar_held()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        ClearWave(world);
        Hero(world).Position = Pod(world).EntranceOutside!.Value;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));

        Run(world, 10);

        var earned = Red(world).Stats.BmusEarned;
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));
        Assert.InRange(Red(world).Stats.BmusEarned - earned, 39, 40);
    }

    /// <summary>A wave just cleared, with the hero at the door of the pod.</summary>
    private static GameWorld AfterWave()
    {
        var world = AtWave(1);
        ClearWave(world);
        Hero(world).Position = Pod(world).EntranceOutside!.Value;
        return world;
    }

    /// <summary>Sends all the commands in one tick.</summary>
    private static void InOneTick(GameWorld world, params Command[] commands)
    {
        foreach (var command in commands)
            world.Issue(command);
        world.Step();
    }

    [Fact]
    public void Ready_pays_once_however_many_times_it_comes_in_a_tick()
    {
        var world = AfterWave();
        var earned = Red(world).Stats.BmusEarned;

        InOneTick(world, new WiredReadyCommand(Race.Red), new WiredReadyCommand(Race.Red), new WiredReadyCommand(Race.Red));

        Assert.Equal(2, world.Rejected.Count);
        Assert.All(world.Rejected, r => Assert.Equal("there is no wave to wait for", r.Reason));
        Assert.InRange(Red(world).Stats.BmusEarned - earned, 58, 60);
        Assert.Equal((WiredPhase.Wave, 2), (world.Wired!.Phase, world.Wired.Wave));
    }

    [Fact]
    public void Ready_from_an_open_hangar_pays_once_too()
    {
        var world = AfterWave();
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        var earned = Red(world).Stats.BmusEarned;

        InOneTick(world, new WiredReadyCommand(Race.Red), new WiredReadyCommand(Race.Red));

        Assert.Equal("there is no wave to wait for", Assert.Single(world.Rejected).Reason);
        Assert.InRange(Red(world).Stats.BmusEarned - earned, 58, 60);
        Assert.False(Red(world).HangarOpen);
        Assert.Equal((WiredPhase.Wave, 2), (world.Wired!.Phase, world.Wired.Wave));
    }

    [Fact]
    public void No_hangar_opens_behind_ready_so_the_wave_comes()
    {
        var world = AfterWave();

        InOneTick(world, new WiredReadyCommand(Race.Red), new WiredHangarCommand(Race.Red, true));

        Assert.Equal("the hangar opens only between waves", Assert.Single(world.Rejected).Reason);
        Assert.False(Red(world).HangarOpen);
        Assert.Equal((WiredPhase.Wave, 2), (world.Wired!.Phase, world.Wired.Wave));
    }

    [Fact]
    public void No_hangar_opens_behind_ready_before_the_first_wave_either()
    {
        var world = NewRun();

        InOneTick(world, new WiredReadyCommand(Race.Red), new WiredHangarCommand(Race.Red, true));

        Assert.Equal("the hangar opens only between waves", Assert.Single(world.Rejected).Reason);
        Assert.Equal((WiredPhase.Wave, 1), (world.Wired!.Phase, world.Wired.Wave));
    }

    [Fact]
    public void Ready_and_the_hangar_sent_every_tick_earn_one_bonus_and_bring_the_wave()
    {
        var world = AfterWave();
        var earned = Red(world).Stats.BmusEarned;
        Command[] macro = [new WiredReadyCommand(Race.Red), new WiredHangarCommand(Race.Red, true)];

        InOneTick(world, macro);

        Assert.Equal(WiredPhase.Wave, world.Wired!.Phase);
        var paid = Red(world).Stats.BmusEarned - earned;
        Assert.InRange(paid, 58, 60);

        // All that comes after is refused, so nothing more can be paid for it.
        for (var i = 0; i < 200; i++)
        {
            InOneTick(world, macro);
            Assert.Equal(macro.Length, world.Rejected.Count);
        }
        Assert.Equal((WiredPhase.Wave, 2), (world.Wired.Phase, world.Wired.Wave));
    }

    [Fact]
    public void An_intermission_pays_for_ready_again()
    {
        var world = AfterWave();
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));
        ClearWave(world);
        var earned = Red(world).Stats.BmusEarned;

        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));

        Assert.InRange(Red(world).Stats.BmusEarned - earned, 58, 60);
        Assert.Equal((WiredPhase.Wave, 3), (world.Wired!.Phase, world.Wired.Wave));
    }

    [Fact]
    public void An_escort_lost_leaves_no_wreck_to_sell_it_back_with()
    {
        var world = NewRun();
        var scrap = Red(world).Add(WiredKind.Module, "scrap", 0, 3);
        Red(world).Loadout = Red(world).Loadout.With(WiredSlot.Module(0), scrap);
        var escort = BoughtEscort(world);
        var bank = Bank(world);
        Assert.Null(Do(world, new SelfDestructCommand(Race.Red, [escort.Id])));

        Run(world, GameWorld.SelfDestructDelay + 1);

        Assert.True(escort.IsDead);
        Assert.Empty(world.Debris);
        Assert.Equal(bank, Bank(world));
    }

    [Fact]
    public void Escorts_wait_for_a_hero_in_the_pod_and_go_on_following_it_when_it_comes_out()
    {
        var world = NewRun();
        var (hero, pod) = (Hero(world), Pod(world));
        var before = BoughtEscort(world);
        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [hero.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && hero.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, hero.Inside);

        // One bought while the hero is in there waits as well.
        var after = BoughtEscort(world);
        Run(world, 2);
        Assert.Equal(new FollowOrder(hero.Id), before.Order);
        Assert.Equal(new FollowOrder(hero.Id), after.Order);

        var far = FreeAt(world, pod.Position + (pod.EntranceOutside!.Value - pod.Position) * 6);
        Assert.Null(Do(world, new MoveCommand(Race.Red, [hero.Id], far)));
        Run(world, 25);

        Assert.Null(hero.Inside);
        Assert.True(Vector2.Distance(hero.Position, pod.Position) > 30);
        Assert.Equal(new FollowOrder(hero.Id), before.Order);
        Assert.Equal(new FollowOrder(hero.Id), after.Order);
        Assert.InRange(before.DistanceTo(hero), 0, 20);
        Assert.InRange(after.DistanceTo(hero), 0, 20);
    }

    [Fact]
    public void Who_else_follows_a_machine_into_a_building_still_stands_down()
    {
        var world = NewRun();
        var (hero, pod) = (Hero(world), Pod(world));
        var other = Grunt(world, FreeAt(world, pod.Position + new Vector2(40, 40)), Race.Red);
        other.Order = new FollowOrder(hero.Id);
        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [hero.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && hero.Inside is null; i++)
            world.Step();

        Run(world, 0.5f);

        Assert.IsType<IdleOrder>(other.Order);
    }

    [Fact]
    public void An_escort_in_a_fight_of_its_own_or_on_its_way_back_goes_with_the_new_body()
    {
        var world = NewRun();
        var (fighting, going) = (BoughtEscort(world), BoughtEscort(world));
        var foe = Grunt(world, FreeAt(world, Pod(world).Position + new Vector2(300, 200)));
        var back = new ReturnOrder(fighting.Position, foe.Position, 1);
        Kill(world, Hero(world), Race.Blue);
        for (var i = 0; i < 20 * 7 && world.HeroOf(Race.Red) is null; i++)
        {
            fighting.Order = fighting.Order is AttackOrder ? fighting.Order : new AttackOrder(foe.Id, Back: new Interrupted(back, null, false));
            going.Order = going.Order is ReturnOrder ? going.Order : back;
            world.Step();
        }
        var hero = Hero(world);

        Assert.Equal(new FollowOrder(hero.Id), Assert.IsType<AttackOrder>(fighting.Order).Back!.Order);
        Assert.Equal(new FollowOrder(hero.Id), going.Order);

        // When the fight ends it goes after the new hero, however far that has gone.
        Kill(world, foe);
        Run(world, 0.5f);
        Assert.Equal(new FollowOrder(hero.Id), fighting.Order);
    }

    [Fact]
    public void A_fight_the_escort_picks_while_the_hero_is_down_ends_with_the_new_body()
    {
        var world = NewRun();
        var escort = BoughtEscort(world);
        Run(world, 3);
        Kill(world, Hero(world), Race.Blue);
        Run(world, 0.5f);
        var foe = Grunt(world, FreeAt(world, escort.Position + new Vector2(25, 0)));
        foe.HitPoints = 100000;
        foe.Order = new StandGroundOrder();
        for (var i = 0; i < 20 * 5 && escort.Order is not AttackOrder; i++)
            world.Step();
        Assert.IsType<AttackOrder>(escort.Order);
        for (var i = 0; i < 20 * WiredBalance.RespawnTime; i++)
        {
            escort.HitPoints = escort.MaxHitPoints;
            world.Step();
        }
        var hero = Hero(world);

        Kill(world, foe);
        Run(world, 1);
        Assert.Equal(new FollowOrder(hero.Id), escort.Order);

        var far = FreeAt(world, hero.Position + (hero.Position - Pod(world).Position) * 4);
        Assert.Null(Do(world, new MoveCommand(Race.Red, [hero.Id], far)));
        Run(world, 20);
        Assert.InRange(escort.DistanceTo(hero), 0, 25);
    }

    [Fact]
    public void Orders_the_player_gave_an_escort_stay_through_a_respawn()
    {
        var world = NewRun();
        var (held, sent) = (BoughtEscort(world), BoughtEscort(world));
        Assert.Null(Do(world, new StandGroundCommand(Race.Red, [held.Id])));
        Assert.Null(Do(world, new MoveCommand(Race.Red, [sent.Id], FreeAt(world, Pod(world).Position + new Vector2(600, 600)))));

        Kill(world, Hero(world), Race.Blue);
        Run(world, WiredBalance.RespawnTime + 0.5f);

        Assert.IsType<StandGroundOrder>(held.Order);
        Assert.IsType<MoveOrder>(sent.Order);
    }

    [Fact]
    public void A_machine_turned_at_the_last_moment_holds_the_wave_open()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        var traitor = Grunt(world, FreeAt(world, Pod(world).Position + new Vector2(80, 80)), Race.Red);
        Red(world).EscortIds.Add(traitor.Id);
        var bonus = Red(world).Stats.BmusEarned;

        traitor.Race = run.Enemy;
        foreach (var foe in run.Foes.ToList())
            Kill(world, foe.Machine);
        run.Groups.Clear();
        Run(world, 0.2f);

        Assert.Equal(WiredPhase.Wave, run.Phase);
        Assert.Equal(1, run.EnemiesLeft);
        Assert.Contains(run.Foes, f => f.Machine == traitor);
        Assert.Equal(bonus, Red(world).Stats.BmusEarned);

        Kill(world, traitor);
        Run(world, 0.2f);
        Assert.Equal(WiredPhase.Intermission, run.Phase);
    }

    // ---- a hero in the pod --------------------------------------------------------------------------

    private static void GoIn(GameWorld world, Machine machine, Construction pod)
    {
        Assert.Null(Do(world, new EnterGarrisonCommand(machine.Race, [machine.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && machine.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, machine.Inside);
    }

    /// <summary>A wave down to one machine beside the pod and a last group that never comes, so that it does not end; a hunter if asked for.</summary>
    private static (GameWorld World, WiredFoe Foe) OneFoeAtThePod(bool hunter)
    {
        for (var seed = 1; ; seed++)
        {
            var world = AtWave(1, seed: seed);
            var run = world.Wired!;
            var foe = Alone(world);
            if (foe.HuntsHero != hunter)
                continue;
            run.Groups.Clear();
            run.Groups.Add(new WiredGroup(100_000, [foe.Plan]));
            foe.Machine.Position = FreeAt(world, Pod(world).EntranceOutside!.Value + new Vector2(25, 25));
            return (world, foe);
        }
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(10, 3)]
    public void With_the_hero_in_the_pod_hunters_bosses_too_turn_on_the_pod(int wave, int seed)
    {
        var world = AtWave(wave, seed: seed);
        var (run, hero, pod) = (world.Wired!, Hero(world), Pod(world));
        GoIn(world, hero, pod);
        var fired = 0;

        for (var i = 0; i < 20 * 240 && run.Phase == WiredPhase.Wave; i++)
        {
            foreach (var other in run.Foes.Where(f => !f.HuntsHero).ToList())
                Kill(world, other.Machine);
            world.Step();
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired && run.Foes.Any(f => f.Machine.Id == e.Actor));
        }

        Assert.True(fired > 0, "no hunter fired");
        Assert.True(pod.HitPoints < pod.MaxHitPoints, $"the pod stands at {pod.HitPoints}");
    }

    [Fact]
    public void A_hunter_gives_up_the_hero_at_the_pods_door_and_takes_it_up_again_when_it_comes_out()
    {
        var (world, foe) = OneFoeAtThePod(hunter: true);
        var (hero, pod) = (Hero(world), Pod(world));

        GoIn(world, hero, pod);
        Run(world, WiredBalance.OrdersEvery + 5);

        Assert.Equal(pod.Id, Assert.IsType<AttackOrder>(foe.Machine.Order).Target);

        Assert.Null(Do(world, new MoveCommand(Race.Red, [hero.Id], pod.EntranceOutside!.Value)));
        for (var i = 0; i < 20 * 20 && hero.Inside is not null; i++)
            world.Step();
        Assert.Null(hero.Inside);
        Run(world, WiredBalance.OrdersEvery + 0.5f);

        Assert.Equal(hero.Id, Assert.IsType<AttackOrder>(foe.Machine.Order).Target);
    }

    [Fact]
    public void No_review_sends_a_hunter_after_a_hero_in_the_pod()
    {
        var (world, foe) = OneFoeAtThePod(hunter: true);
        var (hero, pod) = (Hero(world), Pod(world));
        GoIn(world, hero, pod);
        Run(world, 1);
        var sent = new List<Order>();

        for (var i = 0; i < 20 * 3 * WiredBalance.OrdersEvery; i++)
        {
            foe.Machine.Order = new AttackOrder(pod.Id, Then: pod.EntranceOutside);
            var given = foe.Machine.Order;
            world.Step();
            if (!ReferenceEquals(foe.Machine.Order, given))
                sent.Add(foe.Machine.Order);
        }

        Assert.Empty(sent);
    }

    [Fact]
    public void One_that_picked_a_fight_with_the_hero_marches_on_when_it_goes_in()
    {
        var (world, foe) = OneFoeAtThePod(hunter: false);
        var (hero, pod) = (Hero(world), Pod(world));
        foe.Machine.Order = new AttackOrder(hero.Id, Then: pod.EntranceOutside);

        GoIn(world, hero, pod);
        Run(world, 0.5f);

        Assert.NotEqual(hero.Id, (foe.Machine.Order as AttackOrder)?.Target);
        Run(world, 30);
        Assert.True(pod.HitPoints < pod.MaxHitPoints);
    }

    [Fact]
    public void Outside_a_run_an_attacker_stays_on_a_target_gone_indoors()
    {
        var match = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        var pod = match.Constructions.First(c => c.Race == Race.Red && c.IsType("POD"));
        var door = pod.EntranceOutside!.Value;
        var (target, attacker) = (Grunt(match, door, Race.Red), Grunt(match, FreeAt(match, door + new Vector2(25, 25))));

        GoIn(match, target, pod);
        attacker.DefCon = DefCon.High;
        attacker.Order = new AttackOrder(target.Id);
        Run(match, 2);

        Assert.Equal(target.Id, Assert.IsType<AttackOrder>(attacker.Order).Target);
    }

    // ---- what was left of the reviews ---------------------------------------------------------------

    /// <summary>A place on a run's water; with <paramref name="cutOff"/> one whose nearest land the hero cannot drive to, or that has no land near at all.</summary>
    private static Vector2 OnWater(GameWorld world, bool cutOff)
    {
        var nav = world.Nav;
        var door = nav.Cell(Pod(world).EntranceOutside!.Value);
        var region = nav.Region(door.X, door.Y, crossesWater: false);
        for (var y = 0; y < nav.Depth; y++)
        {
            for (var x = 0; x < nav.Width; x++)
            {
                var place = nav.Centre(x, y);
                if (nav.IsFree(place, crossesWater: false) || !nav.IsFree(place, crossesWater: true))
                    continue;
                var nearest = nav.NearestFreePoint(place, crossesWater: false);
                var reached = nearest is { } land && nav.Region(nav.Cell(land).X, nav.Cell(land).Y, crossesWater: false) == region;
                if (reached != cutOff)
                    return place;
            }
        }
        throw new InvalidOperationException("no such water");
    }

    /// <summary>Whether a hero on land can drive to a place.</summary>
    private static bool OnTheHerosLand(GameWorld world, Vector2 place)
    {
        var (door, there) = (world.Nav.Cell(Pod(world).EntranceOutside!.Value), world.Nav.Cell(place));
        return world.Nav.Region(there.X, there.Y, crossesWater: false) == world.Nav.Region(door.X, door.Y, crossesWater: false);
    }

    [Theory]
    [InlineData("AGGRESSOR", "KNIGHT", 4, "LR_MULTI_LAUNCHER3_X2", false)]
    [InlineData("AGGRESSOR", "KNIGHT", 4, "LR_MULTI_LAUNCHER3_X2", true)]
    [InlineData("AGGRESSOR", "KNIGHT", 5, "LRT_MULTI_LAUNCHER4_X3", false)]
    [InlineData("ADMINISTRATOR", "COMMANDER", 4, "LR_MULTI_LAUNCHER5_X2", false)]
    [InlineData("ADMINISTRATOR", "COMMANDER", 5, "LR_MULTI_LAUNCHER5_X2", true)]
    [InlineData("ADMINISTRATOR", "BOSS", 1, "T_WASP_LIGHT_STING", true)]
    public void A_wreck_that_falls_over_water_lies_where_a_hero_on_land_can_drive_to_it(string type, string subType, int level, string combo, bool cutOff)
    {
        var world = NewRun(arena: "m_dales");
        var foe = world.AddMachine(Race.Blue, OnWater(world, cutOff), type, subType, level, combo);
        var wrecks = world.Debris.Count;

        Kill(world, foe);
        world.Step();

        Assert.Equal(wrecks + 1, world.Debris.Count);
        Assert.True(OnTheHerosLand(world, world.Debris[^1].Position));
    }

    [Fact]
    public void A_crate_that_falls_over_water_lies_where_a_hero_on_land_can_drive_to_it()
    {
        var world = AtWave(5, arena: "m_dales");
        var boss = world.Wired!.Bosses.First();
        boss.Machine.Position = OnWater(world, cutOff: true);

        Kill(world, boss.Machine);
        world.Step();

        Assert.True(OnTheHerosLand(world, Assert.Single(world.WiredPickups).Position));
    }

    [Fact]
    public void A_wreck_on_land_stays_where_it_fell()
    {
        var world = NewRun(arena: "m_dales");
        var place = FreeAt(world, Pod(world).Position + new Vector2(120, 0));
        Assert.True(OnTheHerosLand(world, place));
        var foe = world.AddMachine(Race.Blue, place, "AGGRESSOR", "KNIGHT", 4, "LR_MULTI_LAUNCHER3_X2");

        Kill(world, foe);
        world.Step();

        Assert.Equal(place, world.Debris[^1].Position);
    }

    [Fact]
    public void Outside_a_run_a_hover_wreck_stays_on_the_water_and_what_flew_leaves_none()
    {
        var match = MatchSetup.Create(Data, "m_dales", "m_dales",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        var nav = match.Nav;
        var water = Enumerable.Range(0, nav.Width * nav.Depth).Select(i => nav.Centre(i % nav.Width, i / nav.Width))
            .First(p => !nav.IsFree(p, crossesWater: false) && nav.IsFree(p, crossesWater: true));
        var knight = match.AddMachine(Race.Blue, water, "AGGRESSOR", "KNIGHT", 4, "LR_MULTI_LAUNCHER3_X2");
        var wasp = match.AddMachine(Race.Blue, water, "ADMINISTRATOR", "BOSS", 1, "T_WASP_LIGHT_STING");

        Kill(match, knight);
        Kill(match, wasp);
        match.Step();

        Assert.Equal(water, Assert.Single(match.Debris).Position);
    }

    /// <summary>The hero's next body a moment away, with an escort bought and the old body's id.</summary>
    private static (GameWorld World, Machine Escort, int Fallen) BeforeTheNewBody()
    {
        var world = NewRun();
        var escort = BoughtEscort(world);
        var fallen = Hero(world).Id;
        Kill(world, Hero(world), Race.Blue);
        world.Step();
        while (world.Time + GameWorld.Dt < Red(world).RespawnAt)
            world.Step();
        Assert.Null(world.HeroOf(Race.Red));
        return (world, escort, fallen);
    }

    private static EvadeOrder Evading(GameWorld world, Order resume) => new(resume, null, world.Time, EvadeGoal.None, null);

    [Fact]
    public void An_escort_evading_when_the_new_body_stands_follows_that_when_it_stops()
    {
        var (world, escort, fallen) = BeforeTheNewBody();
        escort.Order = Evading(world, new FollowOrder(fallen));

        Run(world, 0.2f);
        var hero = Hero(world);
        Assert.NotEqual(fallen, hero.Id);
        Assert.Equal(new FollowOrder(hero.Id), Assert.IsType<EvadeOrder>(escort.Order).Resume);

        Run(world, 10);
        Assert.Equal(new FollowOrder(hero.Id), escort.Order);

        // It really goes after it, as the others do.
        var far = FreeAt(world, hero.Position + (hero.Position - Pod(world).Position) * 4);
        Assert.Null(Do(world, new MoveCommand(Race.Red, [hero.Id], far)));
        Run(world, 20);
        Assert.InRange(escort.DistanceTo(hero), 0, 25);
    }

    [Fact]
    public void An_escort_that_evades_out_of_a_fight_goes_back_to_the_new_body_after_it()
    {
        var (world, escort, fallen) = BeforeTheNewBody();
        var foe = Grunt(world, FreeAt(world, Pod(world).Position + new Vector2(300, 0)));
        var back = new Interrupted(new FollowOrder(fallen), null, false);
        escort.Order = Evading(world, new AttackOrder(foe.Id, Back: back));

        Run(world, 0.2f);

        var fight = Assert.IsType<AttackOrder>(Assert.IsType<EvadeOrder>(escort.Order).Resume);
        Assert.Equal(new FollowOrder(Hero(world).Id), fight.Back!.Order);
    }

    [Fact]
    public void An_escort_that_stood_idle_before_it_evaded_follows_the_new_body_like_one_that_still_stands()
    {
        var (world, escort, _) = BeforeTheNewBody();
        escort.Order = Evading(world, Order.Idle);

        Run(world, 10);

        Assert.Equal(new FollowOrder(Hero(world).Id), escort.Order);
    }

    [Fact]
    public void The_players_order_stays_under_an_evasion_through_a_respawn()
    {
        var (world, escort, _) = BeforeTheNewBody();
        var sent = new MoveOrder(FreeAt(world, Pod(world).Position + new Vector2(600, 600)));
        escort.Order = Evading(world, sent);

        Run(world, 0.2f);

        Assert.Equal(sent, Assert.IsType<EvadeOrder>(escort.Order).Resume);
    }

    [Fact]
    public void Shots_at_artefacts_do_not_count_against_a_red_wave()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Blue] = Controller.Player }, 7,
            new MatchOptions { Wired = new WiredOptions() });
        var run = world.Wired!;
        Assert.Equal(Race.Red, run.Enemy);
        var (state, hero) = (run.Heroes[Race.Blue], world.HeroOf(Race.Blue)!);
        state.Loadout = state.Loadout.With(WiredSlot.Chip(0), state.Add(WiredKind.Chip, "revenge", 0));
        state.RevengeUntil = 1000;
        var stone = world.Artefacts.OrderByDescending(a => a.HitPoints).First();
        Assert.Equal(Race.Red, stone.Race);
        Assert.InRange(stone.HitPoints, 5, int.MaxValue);
        var whole = stone.HitPoints + stone.Armour;

        world.Damage(stone, 4, Race.Blue, hero, "BOLTER");

        Assert.Equal(4, whole - stone.HitPoints - stone.Armour);
        Assert.Equal(0, state.Stats.DamageDealt);
        Assert.Empty(state.Stats.Weapons);

        // What really comes for it in red counts as ever, chip and all.
        var foe = Grunt(world, FreeAt(world, hero.Position + new Vector2(60, 0)), Race.Red);
        var healthy = foe.HitPoints + foe.Armour;
        world.Damage(foe, 10, Race.Blue, hero, "BOLTER");
        var lost = healthy - foe.HitPoints - foe.Armour;
        Assert.InRange(lost, 12, 14);
        Assert.Equal(lost, state.Stats.DamageDealt);
        Assert.Equal(lost, state.Stats.Weapon("BOLTER").Damage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_checksum_sees_which_socket_a_chip_sits_in(int socket)
    {
        GameWorld Placed(int second)
        {
            var world = NewRun();
            var state = Red(world);
            state.Loadout = state.Loadout.With(WiredSlot.Chip(0), state.Add(WiredKind.Chip, "sustained", 0));
            state.Loadout = state.Loadout.With(WiredSlot.Chip(second), state.Add(WiredKind.Chip, "revenge", 0));
            return world;
        }

        Assert.Equal(Placed(socket).Checksum(), Placed(socket).Checksum());
        Assert.NotEqual(Placed(socket).Checksum(), Placed(socket + 2).Checksum());
    }

    [Fact]
    public void The_checksum_sees_which_slot_a_gun_a_module_or_an_ability_sits_in()
    {
        static ulong Sum(WiredStartItem item) => NewRun(new WiredOptions { Chassis = "grunt3", Items = [item] }).Checksum();
        static ulong WithAbility(int slot)
        {
            var world = NewRun();
            var state = Red(world);
            state.Loadout = state.Loadout.With(WiredSlot.Ability(slot), state.Add(WiredKind.Ability, "discharge", 0));
            return world.Checksum();
        }

        Assert.Equal(Sum(new("BOLTER", WiredSlot.Weapon(Mounting.Left))), Sum(new("BOLTER", WiredSlot.Weapon(Mounting.Left))));
        Assert.NotEqual(Sum(new("BOLTER", WiredSlot.Weapon(Mounting.Left))), Sum(new("BOLTER", WiredSlot.Weapon(Mounting.Right))));
        Assert.NotEqual(Sum(new("scanner", WiredSlot.Module(0))), Sum(new("scanner", WiredSlot.Module(1))));
        Assert.NotEqual(WithAbility(0), WithAbility(1));
    }

    [Fact]
    public void The_checksum_sees_what_the_run_goes_by()
    {
        var changes = new (string What, Action<GameWorld> Change)[]
        {
            ("fetched", world => world.Wired!.Foes[0].Fetched = true),
            ("last place", world => world.Wired!.Foes[0].LastPlace += Vector2.One),
            ("last durability", world => world.Wired!.Foes[0].LastDurability--),
            ("boss", world => world.Wired!.Boss = WiredDirector.Bosses[0]),
            ("dash way", world => Red(world).DashWay = Vector2.UnitX),
            ("reached", world => Red(world).Reached.Add("grunt2")),
            ("last special", world => world.Wired!.LastSpecial = WiredSpecial.Night),
            ("wave started", world => world.Wired!.WaveStarted--),
            ("last group", world => world.Wired!.LastGroupAt--),
            ("next review", world => world.Wired!.NextReview++),
            ("spawn points", world => world.Wired!.SpawnPoints.RemoveAt(0)),
            ("ending", world => world.Wired!.Ending = WiredEnding.PodLost),
        };
        foreach (var (what, change) in changes)
        {
            var (one, other) = (AtWave(1), AtWave(1));
            Assert.Equal(one.Checksum(), other.Checksum());

            change(other);

            Assert.True(one.Checksum() != other.Checksum(), what);
        }
    }

    [Fact]
    public void The_checksum_sees_where_a_wreck_lies_and_which_wave_a_crate_is_from()
    {
        var (one, other) = (NewRun(), NewRun());
        foreach (var world in new[] { one, other })
            Wreck(world);
        Assert.Equal(one.Checksum(), other.Checksum());

        var wreck = other.Debris[^1];
        other.Debris[^1] = new DebrisState(wreck.Id, wreck.Race, wreck.Position + Vector2.One, wreck.Quantity, wreck.Boundary, wreck.CreatedAt, wreck.ExpiresAt);
        Assert.NotEqual(one.Checksum(), other.Checksum());

        var (first, second) = (WithCrate(5), WithCrate(5));
        Assert.Equal(first.Checksum(), second.Checksum());
        second.WiredPickups[0] = second.WiredPickups[0] with { Wave = 6 };
        Assert.NotEqual(first.Checksum(), second.Checksum());
    }
}
