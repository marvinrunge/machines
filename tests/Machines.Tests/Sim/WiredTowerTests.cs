using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The towers of Wired's constructor line: which each chassis builds and how many may stand, their price, where they may
/// go, how the hero builds, mends and pulls them down, and that the waves still find the pod.
/// </summary>
public class WiredTowerTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld NewRun(WiredOptions? options = null, int seed = 7) =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = options ?? new WiredOptions() });

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    private static Construction Pod(GameWorld world) => world.WiredPod(Race.Red)!;

    private static Vector2 Door(GameWorld world) => Pod(world).EntranceOutside ?? Pod(world).Position;

    private static float Bank(GameWorld world) => world.Races[Race.Red].Bmu;

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    private static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    /// <summary>Puts the hero on any chassis, without the way there; the hangar must be open.</summary>
    private static void Board(GameWorld world, string chassis)
    {
        var item = Red(world).Add(WiredKind.Chassis, chassis, 0);
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, item.Id)));
    }

    /// <summary>
    /// A run whose hero wears the chassis, out of the hangar at the pod's door and not at the wheel, with a bank that does
    /// not get in the way and the first wave held back.
    /// </summary>
    private static GameWorld OutOn(string chassis, WiredOptions? options = null, int seed = 7)
    {
        var world = NewRun(options, seed);
        world.Wired!.Countdown = 100_000;
        world.Races[Race.Red].Bmu = 100_000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Board(world, chassis);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        Hero(world).DrivenByPlayer = false;
        return world;
    }

    /// <summary>Takes the hero to open ground well away from the pod, where towers fit all round it.</summary>
    private static void Away(GameWorld world)
    {
        var nav = world.Nav;
        var door = nav.Cell(Door(world));
        var region = nav.Region(door.X, door.Y, false);
        bool Open(Vector2 at)
        {
            for (var x = -24f; x <= 24; x += 4)
            {
                for (var y = -24f; y <= 24; y += 4)
                {
                    if (x * x + y * y <= 24 * 24 && !nav.IsFree(at + new Vector2(x, y), false))
                        return false;
                }
            }
            var cell = nav.Cell(at);
            return nav.OnPlanet(at) && nav.Region(cell.X, cell.Y, false) == region;
        }
        foreach (var distance in new[] { 60f, 80, 100, 130 })
        {
            for (var turn = 0; turn < 16; turn++)
            {
                var angle = turn * MathF.Tau / 16;
                var at = Door(world) + distance * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                if (Open(at))
                {
                    Hero(world).Position = at;
                    return;
                }
            }
        }
        Assert.Fail("no open ground near the pod");
    }

    /// <summary>Places round the hero, from <paramref name="near"/> to <paramref name="far"/> metres out, where a tower may go now.</summary>
    private static List<Vector2> Ring(GameWorld world, string tower, float near, float far)
    {
        var (state, hero) = (Red(world), Hero(world));
        var places = new List<Vector2>();
        for (var distance = near; distance <= far; distance += 1)
        {
            for (var turn = 0; turn < 24; turn++)
            {
                var angle = turn * MathF.Tau / 24;
                var at = hero.Position + distance * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                if (world.WhyNoTower(state, tower, at, 0) is null)
                    places.Add(at);
            }
        }
        return places;
    }

    /// <summary>A place round the hero where the tower may go now.</summary>
    private static Vector2 Spot(GameWorld world, string tower, float near, float far) =>
        Ring(world, tower, near, far) is [var first, ..] ? first : throw new InvalidOperationException($"no place for {tower} {near}-{far} m from the hero");

    /// <summary>Places a tower and gives back its site.</summary>
    private static Construction Place(GameWorld world, string tower, Vector2 at, int orientation = 0)
    {
        Assert.Null(Do(world, new WiredTowerCommand(Race.Red, tower, at, orientation)));
        var placed = Assert.Single(world.Events, e => e.Kind == GameEventKind.WiredTowerPlaced);
        Assert.Equal(tower, placed.Detail);
        return world.Find<Construction>(placed.Actor)!;
    }

    /// <summary>Places a tower somewhere round the hero, where the next ones still find room.</summary>
    private static Construction PlaceNear(GameWorld world, string tower = "turret1", float near = 6, float far = 9) =>
        Place(world, tower, Spot(world, tower, near, far));

    // ---- which and how many -------------------------------------------------------------------------

    [Theory]
    [InlineData("dozer1", new[] { "turret1", "turret2" })]
    [InlineData("builder3", new[] { "turret1", "turret2", "turret3", "sentry3", "sentry4" })]
    [InlineData("behemoth4", new[] { "turret1", "turret2", "turret3", "sentry3", "sentry4", "launcher4" })]
    [InlineData("titan6", new[] { "turret1", "turret2", "turret3", "sentry3", "sentry4", "launcher4" })]
    [InlineData("grunt1", new string[0])]
    [InlineData("spy3", new string[0])]
    [InlineData("phantom6", new string[0])]
    public void Each_chassis_builds_the_towers_of_its_machine_up_to_its_most_advanced_construction(string chassis, string[] towers)
    {
        var world = NewRun();

        Assert.Equal(towers, world.TowersFor(WiredCatalogue.FindChassis(chassis)!).Select(t => t.Id));
    }

    [Fact]
    public void The_titan_builds_every_tower_and_no_silo()
    {
        var world = NewRun();
        var titan = WiredCatalogue.FindChassis("titan6")!;

        Assert.Equal(WiredCatalogue.Towers, world.TowersFor(titan));
        Assert.Contains(Data.ConstructionOrder.UpTo(Data.Parm.Machine(titan.TypeName).Level(titan.Level).MostAdvancedConstruction!),
            e => e.SubType == "ICBM");
        Assert.DoesNotContain(world.TowersFor(titan), t => t.SubType == "ICBM");
    }

    [Theory]
    [InlineData("turret1", "T_FLAME_THROWER1")]
    [InlineData("turret2", "LR_AUTO_CANNON_X2")]
    [InlineData("turret3", "LR_PULSE_RIFLE_X2")]
    [InlineData("sentry3", "LR_MULTI_LAUNCHER5_X2")]
    [InlineData("sentry4", "LR_MULTI_LAUNCHER6_X2")]
    [InlineData("launcher4", "LR_LARGE_MISSILE_X2")]
    public void A_tower_is_the_original_s_emplacement_with_its_weapons(string tower, string combo)
    {
        var world = OutOn("titan6");
        Away(world);

        var site = PlaceNear(world, tower, 8, 14);

        var kind = WiredCatalogue.FindTower(tower)!;
        Assert.True(site.IsType(WiredTower.Type, kind.SubType));
        Assert.Equal((Race.Red, kind.Level, combo, false), (site.Race, site.Stats.HardwareLevel, site.WeaponCombo, site.Complete));
        Assert.NotEmpty(site.Weapons);
        Assert.Equal([site], world.TowersOf(Race.Red));
    }

    [Theory]
    [InlineData("dozer1", 2)]
    [InlineData("builder3", 4)]
    [InlineData("behemoth4", 6)]
    [InlineData("titan6", 8)]
    public void A_chassis_lets_only_so_many_towers_stand_at_once_sites_included(string chassis, int most)
    {
        var world = OutOn(chassis);
        Away(world);

        for (var i = 0; i < most; i++)
            PlaceNear(world, "turret1", 6, 20);

        Assert.Equal(most, world.MostTowers(Red(world)));
        Assert.Equal(most, world.TowersOf(Race.Red).Count);
        // Built or not, each counts; the limit comes before the place is looked at.
        Assert.Contains(world.TowersOf(Race.Red), t => !t.Complete);
        Assert.Equal($"{most} towers stand already", Do(world, new WiredTowerCommand(Race.Red, "turret1", Hero(world).Position + new Vector2(25, 0), 0)));
        Assert.Equal(most, world.TowersOf(Race.Red).Count);
    }

    [Fact]
    public void Towers_above_a_smaller_limit_stay_and_only_new_ones_are_refused()
    {
        var world = OutOn("titan6");
        for (var i = 0; i < 8; i++)
            PlaceNear(world, "turret1", 13, 28);
        var standing = world.TowersOf(Race.Red).Select(t => t.Id).ToList();

        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Board(world, "dozer1");
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        Run(world, 1);

        Assert.Equal(standing, world.TowersOf(Race.Red).Select(t => t.Id));
        Assert.Equal(2, world.MostTowers(Red(world)));
        Assert.Equal("8 towers stand already", world.WhyNoTower(Red(world), "turret1", Hero(world).Position + new Vector2(20, 0), 0));
        Assert.Null(Do(world, new WiredScrapTowerCommand(Race.Red, standing[0])));
        Assert.Equal("7 towers stand already", world.WhyNoTower(Red(world), "turret1", Hero(world).Position + new Vector2(20, 0), 0));
    }

    // ---- the price ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(WiredDifficulty.Normal, new[] { 40, 80, 130, 200, 175, 400 })]
    [InlineData(WiredDifficulty.Hard, new[] { 46, 92, 150, 230, 201, 460 })]
    public void A_tower_costs_the_original_s_price_with_the_difficulty_paid_in_full_when_placed(WiredDifficulty difficulty, int[] prices)
    {
        var world = OutOn("titan6", new WiredOptions { Difficulty = difficulty });
        Away(world);
        var state = Red(world);

        Assert.Equal(prices, WiredCatalogue.Towers.Select(t => world.TowerPrice(state, t)));
        Assert.Equal(prices, WiredCatalogue.Towers.Select(t => Data.Parm.Construction($"{WiredTower.Type} {t.SubType}").Level(t.Level).Cost)
            .Select(cost => WiredBalance.Priced(cost, difficulty)));
        foreach (var (tower, price) in WiredCatalogue.Towers.Zip(prices))
        {
            var before = Bank(world);
            PlaceNear(world, tower.Id, 6, 22);
            Assert.Equal(before - price, Bank(world));
        }
    }

    [Fact]
    public void Trader_takes_its_share_off_the_towers_too()
    {
        var world = OutOn("titan6");
        var state = Red(world);
        var full = WiredCatalogue.Towers.Select(t => world.TowerPrice(state, t)).ToList();

        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        var chip = state.Add(WiredKind.Chip, "trader", 0);
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 0)));

        var off = WiredChips.Discount(state);
        Assert.True(off > 0);
        Assert.Equal(full.Select(p => (int)MathF.Round(p * (1 - off))), WiredCatalogue.Towers.Select(t => world.TowerPrice(state, t)));
        Assert.All(full.Zip(WiredCatalogue.Towers.Select(t => world.TowerPrice(state, t))), pair => Assert.True(pair.Second < pair.First));
    }

    // ---- where -------------------------------------------------------------------------------------

    [Fact]
    public void Placing_is_refused_in_the_spec_s_order_for_each_reason()
    {
        var world = OutOn("dozer1");
        Away(world);
        var (state, hero) = (Red(world), Hero(world));
        var good = Spot(world, "turret1", 8, 12);
        string? Why(string tower = "turret1", Vector2? at = null, int orientation = 0) => world.WhyNoTower(state, tower, at ?? good, orientation);

        Assert.Null(Why());
        Assert.Equal("no such tower", Why("nothing"));
        Assert.Equal("this chassis cannot build that", Why("sentry3"));
        Assert.Equal("a tower turns only by quarter turns", Why(orientation: 45));
        Assert.Null(Why(orientation: 270));
        Assert.Equal("too far", Why(at: hero.Position + new Vector2(31, 0)));
        Assert.Equal("something stands there", Why(at: hero.Position));

        // The bank comes before the turn and the place.
        world.Races[Race.Red].Bmu = 39;
        Assert.Equal("not enough BMUs", Why(orientation: 45));
        world.Races[Race.Red].Bmu = 40;
        Assert.Null(Why());

        // Over another tower the ground is taken.
        world.Races[Race.Red].Bmu = 100_000;
        var first = Place(world, "turret1", good);
        Assert.Equal("obstacles or uneven terrain", Why(at: first.Position + new Vector2(0.5f, 0)));
        Assert.Null(Do(world, new WiredScrapTowerCommand(Race.Red, first.Id)));
        Assert.Null(Why());
    }

    [Fact]
    public void Nothing_is_placed_off_the_planet()
    {
        var world = OutOn("dozer1");
        var hero = Hero(world);
        hero.Position = new Vector2(3, 3);

        Assert.Equal("obstacles or uneven terrain", world.WhyNoTower(Red(world), "turret1", new Vector2(-6, 3), 0));
        Assert.Equal("obstacles or uneven terrain", world.WhyNoTower(Red(world), "turret1", new Vector2(0.5f, 3), 0));
    }

    [Fact]
    public void No_tower_stands_within_twelve_metres_of_the_pod_s_door()
    {
        var world = OutOn("dozer1");
        var (state, door) = (Red(world), Door(world));
        Hero(world).Position = door;
        var probe = new Vector2(1, 1);

        var tooClose = new List<Vector2>();
        for (var distance = 4f; distance <= 10; distance += 1)
        {
            for (var turn = 0; turn < 24; turn++)
            {
                var angle = turn * MathF.Tau / 24;
                var at = door + distance * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                if (world.Nav.IsAreaFree((at - probe, at + probe)) && Vector2.Distance(at, door) > 5)
                    tooClose.Add(at);
            }
        }

        Assert.NotEmpty(tooClose);
        Assert.All(tooClose, at => Assert.Equal("too close to the pod", world.WhyNoTower(state, "turret1", at, 0)));
        Assert.NotEmpty(Ring(world, "turret1", 13.5f, 20));
        Assert.All(Ring(world, "turret1", 0, 30), at => Assert.True(Vector2.Distance(at, door) >= WiredBalance.PodKeepOut));
    }

    [Fact]
    public void No_tower_is_placed_on_a_machine()
    {
        var world = OutOn("dozer1");
        Away(world);
        var at = Spot(world, "turret1", 10, 14);
        var grunt = world.AddMachine(Race.Blue, at + new Vector2(1.5f, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        Assert.Equal("something stands there", Do(world, new WiredTowerCommand(Race.Red, "turret1", at, 0)));
        Assert.Empty(world.TowersOf(Race.Red));

        world.Damage(grunt, 1_000_000, Race.Red);
        world.Step();
        Assert.Null(world.WhyNoTower(Red(world), "turret1", at, 0));
    }

    [Fact]
    public void A_hero_down_inside_or_in_the_doorway_places_none()
    {
        var world = OutOn("dozer1");
        var (state, hero, pod) = (Red(world), Hero(world), Pod(world));
        var at = Spot(world, "turret1", 13, 25);

        hero.Doorway = pod.Id;
        Assert.Equal("not from the doorway", world.WhyNoTower(state, "turret1", at, 0));
        hero.Doorway = null;

        // A Dozer is too big for the pod's door, a Grunt is not; the hero is asked about before its chassis.
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Board(world, "grunt2");
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        hero = Hero(world);
        hero.DrivenByPlayer = false;
        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [hero.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && hero.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, hero.Inside);
        Assert.Equal("not from inside the pod", Do(world, new WiredTowerCommand(Race.Red, "turret1", at, 0)));

        world.Damage(hero, 1_000_000, Race.Blue);
        world.Step();
        Assert.Null(world.HeroOf(Race.Red));
        Assert.Equal("the hero is down", Do(world, new WiredTowerCommand(Race.Red, "turret1", at, 0)));
        Assert.Empty(world.TowersOf(Race.Red));
    }

    [Fact]
    public void A_battle_has_no_towers()
    {
        var world = Battles.Begun();
        var hero = Battles.Hero(world, Race.Red);

        Assert.Equal("no towers in a battle", Battles.Do(world, new WiredTowerCommand(Race.Red, "turret1", hero.Position + new Vector2(10, 0), 0)));
        Assert.Equal("no towers in a battle", Battles.Do(world, new WiredScrapTowerCommand(Race.Red, 1)));
        var view = world.SpecialView(Race.Red);
        Assert.True(view is null || view.Special != ChassisSpecial.Towers);
    }

    [Fact]
    public void A_constructor_in_a_battle_builds_nothing_and_its_view_says_why()
    {
        var tier = BattleCatalogue.Tier("dozer1");
        var hero = new BattleHero(Race.Red, "Ace", "dozer1", BattleBots.Loadout("dozer1", tier));
        var world = Battles.New(BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, [hero]));
        Battles.Run(world, BattleBalance.Opening + 1);

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal((ChassisSpecial.Towers, false, "no towers in a battle"), (view.Special, view.Ready, view.Why));
        Assert.Null(world.TowerWorkOf(Battles.Hero(world, Race.Red)));
        Assert.All(world.TowerKinds(Red(world)), kind => Assert.Equal("no towers in a battle", kind.Why));
    }

    // ---- building ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("dozer1", "turret1", 6f)]
    [InlineData("dozer1", "turret2", 12f)]
    [InlineData("builder3", "turret3", 9.75f)]
    [InlineData("builder3", "sentry3", 15f)]
    [InlineData("builder3", "sentry4", 13.125f)]
    [InlineData("behemoth4", "launcher4", 21.43f)]
    [InlineData("titan6", "launcher4", 17.14f)]
    public void The_hero_builds_a_tower_at_four_times_the_original_s_rate_and_pays_nothing_more(string chassis, string tower, float seconds)
    {
        var world = OutOn(chassis);
        Away(world);
        var at = Spot(world, tower, 6, 14);
        world.Issue(new WiredTowerCommand(Race.Red, tower, at, 0));
        var bank = Bank(world) - world.TowerPrice(Red(world), WiredCatalogue.FindTower(tower)!);

        GameEvent? done = null;
        var ticks = 0;
        for (; ticks < 40 * GameWorld.TicksPerSecond && done is null; ticks++)
        {
            world.Step();
            done = world.Events.FirstOrDefault(e => e.Kind == GameEventKind.ConstructionComplete);
            // Nothing more is charged while it goes up.
            Assert.Equal(bank, Bank(world));
        }

        Assert.NotNull(done);
        Assert.InRange(ticks * GameWorld.Dt, seconds - 0.1f, seconds + 0.1f);
        var site = world.Find<Construction>(done.Actor)!;
        Assert.Equal((Hero(world).Id, true, site.MaxHitPoints), (done.Other, site.Complete, site.HitPoints));
        Assert.Equal(bank, Bank(world));
        Assert.Equal(1, world.Races[Race.Red].Score.ConstructionsBuilt);
        Assert.Null(world.TowerWorkOf(Hero(world)));
    }

    [Fact]
    public void A_site_grows_only_with_the_hero_in_reach_and_also_while_it_is_driven()
    {
        var world = OutOn("dozer1");
        Away(world);
        var hero = Hero(world);
        var site = Place(world, "turret1", Spot(world, "turret1", 25, 29));

        Run(world, 2);
        Assert.Equal(0, site.Built);
        Assert.Null(world.TowerWorkOf(hero));

        hero.Position = world.Nav.NearestFreePoint(site.Position + Vector2.Normalize(hero.Position - site.Position) * 6, false)!.Value;
        hero.DrivenByPlayer = true;
        Assert.Same(site, world.TowerWorkOf(hero));
        Run(world, 1);
        Assert.InRange(site.Built, 6, 7.5f);
        Assert.True(hero.DrivenByPlayer);

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal((ChassisSpecial.Towers, 1, 2, true), (view.Special, view.Count, view.Most, view.Ready));
        Assert.InRange(view.Share, 0.15f, 0.19f);
        Assert.InRange(view.SecondsLeft, 4.8f, 5.2f);
    }

    [Fact]
    public void Only_a_constructor_builds_and_a_site_waits_for_one()
    {
        var world = OutOn("dozer1");
        var site = PlaceNear(world, "turret1", 13, 16);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Board(world, "grunt2");
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        hero.Position = world.Nav.NearestFreePoint(site.Position + new Vector2(5, 0), false)!.Value;

        var built = site.Built;
        Run(world, 2);

        Assert.Equal(built, site.Built);
        Assert.Null(world.TowerWorkOf(hero));
        Assert.Null(world.SpecialView(Race.Red));
        Assert.Contains(site, world.TowersOf(Race.Red));
    }

    [Fact]
    public void The_nearest_site_is_built_first_whatever_its_id()
    {
        var world = OutOn("builder3");
        Away(world);
        var hero = Hero(world);
        // The further site is placed first, so the nearer one has the larger id.
        var further = PlaceNear(world, "turret1", 9, 10);
        var near = PlaceNear(world, "turret1", 5, 6);
        Assert.True(further.Id < near.Id);
        Assert.True(near.DistanceFrom(hero.Position) < further.DistanceFrom(hero.Position));
        var started = further.Built;

        Assert.Same(near, world.TowerWorkOf(hero));
        Run(world, 1);

        Assert.True(near.Built > 0);
        Assert.Equal(started, further.Built);
        // The further one is in reach too: it comes next.
        near.MakeComplete();
        Assert.Same(further, world.TowerWorkOf(hero));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Of_two_sites_as_near_the_one_with_the_smaller_id_is_built_first(bool eastFirst)
    {
        var world = OutOn("builder3");
        Away(world);
        var hero = Hero(world);
        var at = hero.Position;
        // Two sites east and west of the hero, and a place between them exactly as near the one's edge as the other's.
        var kind = WiredCatalogue.FindTower("turret1")!;
        var spec = Data.Parm.Construction($"{WiredTower.Type} {kind.SubType}");
        Construction Probe(Vector2 position) => new(0, Race.Red, position, spec, spec.Level(kind.Level), 0);
        Vector2? Between(float apart)
        {
            var (right, left) = (Probe(at + new Vector2(apart, 0)), Probe(at - new Vector2(apart, 0)));
            var middle = new Vector2((right.Footprint.Min.X + left.Footprint.Max.X) / 2, at.Y);
            var free = world.WhyNoTower(Red(world), "turret1", right.Position, 0) is null && world.WhyNoTower(Red(world), "turret1", left.Position, 0) is null;
            return free && right.DistanceFrom(middle) == left.DistanceFrom(middle) ? middle : null;
        }
        var (gap, between) = Enumerable.Range(0, 49).Select(i => 6 + i / 8f).Select(g => (g, Between(g))).FirstOrDefault(c => c.Item2 is not null);
        Assert.NotNull(between);
        var (east, west) = (at + new Vector2(gap, 0), at - new Vector2(gap, 0));
        var smaller = Place(world, "turret1", eastFirst ? east : west);
        var larger = Place(world, "turret1", eastFirst ? west : east);
        hero.Position = between.Value;
        Assert.True(smaller.Id < larger.Id);
        Assert.Equal(smaller.DistanceFrom(hero.Position), larger.DistanceFrom(hero.Position));
        var (smallerBuilt, largerBuilt) = (smaller.Built, larger.Built);

        Assert.Same(smaller, world.TowerWorkOf(hero));
        Run(world, 1);

        Assert.True(smaller.Built > smallerBuilt);
        Assert.Equal(largerBuilt, larger.Built);
    }

    // ---- mending -----------------------------------------------------------------------------------

    [Fact]
    public void The_hero_mends_a_damaged_tower_for_bmus_and_stops_on_an_empty_bank()
    {
        var world = OutOn("dozer1");
        Away(world);
        var hero = Hero(world);
        var tower = PlaceNear(world, "turret2", 6, 9);
        tower.MakeComplete();
        tower.HitPoints = tower.MaxHitPoints / 4;
        world.Races[Race.Red].Bmu = 1000;

        Assert.Same(tower, world.TowerWorkOf(hero));
        var before = tower.HitPoints;
        Run(world, 1);

        // The original's rule at four times the rate: 100 BMUs a minute become 400, in hit points by the tower's cost.
        var spent = 1000 - Bank(world);
        Assert.InRange(spent, 6.4f, 6.9f);
        Assert.InRange(tower.HitPoints - before, (int)(spent / tower.Stats.Cost * tower.MaxHitPoints) - 1, (int)(spent / tower.Stats.Cost * tower.MaxHitPoints) + 1);
        var view = world.SpecialView(Race.Red)!;
        Assert.InRange(view.Share, tower.HitPoints / (float)tower.MaxHitPoints - 0.01f, tower.HitPoints / (float)tower.MaxHitPoints + 0.01f);
        Assert.True(view.SecondsLeft > 0);

        world.Races[Race.Red].Bmu = 2;
        Run(world, 1);
        Assert.Equal(0, Bank(world));
        var mended = tower.HitPoints;
        Run(world, 2);
        Assert.Equal(mended, tower.HitPoints);
        Assert.Equal(0, Bank(world));
        Assert.Null(world.TowerWorkOf(hero));
        Assert.True(tower.HitPoints < tower.MaxHitPoints);
    }

    [Fact]
    public void Mending_a_tower_brings_no_warning_of_a_low_bank()
    {
        var world = OutOn("dozer1");
        Away(world);
        var tower = PlaceNear(world, "turret2", 6, 9);
        tower.MakeComplete();
        tower.HitPoints = tower.MaxHitPoints / 4;
        world.Races[Race.Red].Bmu = 50;

        var events = Run(world, 1);
        Assert.InRange(Bank(world), 40, 45);
        Assert.True(tower.HitPoints > tower.MaxHitPoints / 4);

        // Down to nothing, where the original's pod would say there are no BMUs left.
        world.Races[Race.Red].Bmu = 2;
        world.Races[Race.Red].NextLowBankMail = 0;
        events.AddRange(Run(world, 2));
        Assert.Equal(0, Bank(world));

        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.VoiceMail && e.Detail is "VID_POD_BMUS_LOW" or "VID_POD_NO_BMUS_LEFT");
    }

    [Fact]
    public void A_whole_tower_is_not_mended_and_costs_nothing()
    {
        var world = OutOn("dozer1");
        Away(world);
        var tower = PlaceNear(world, "turret1", 6, 9);
        tower.MakeComplete();
        var bank = Bank(world);

        Run(world, 2);

        Assert.Equal(bank, Bank(world));
        Assert.Null(world.TowerWorkOf(Hero(world)));
    }

    [Fact]
    public void A_behemoth_hero_mends_at_the_pod_though_it_does_not_fit_through_the_door()
    {
        var world = OutOn("behemoth4");
        var (hero, pod) = (Hero(world), Pod(world));
        Assert.Null(hero.Inside);
        Assert.True(hero.DistanceTo(pod) <= WiredBalance.PodReach);
        hero.HitPoints = 1;

        Run(world, 2);

        Assert.True(hero.HitPoints > 1 + hero.MaxHitPoints / 10, $"{hero.HitPoints} of {hero.MaxHitPoints}");
        Assert.Null(world.WhyNoHangar(Red(world)));
    }

    // ---- fighting ----------------------------------------------------------------------------------

    /// <summary>A run at its first wave down to one machine, a hunter or not, and a tower of the hero's beside it.</summary>
    private static (GameWorld World, Machine Foe, Construction Tower) TowerByAFoe(string tower, bool armed)
    {
        var world = NewRun();
        world.Wired!.Countdown = 0;
        world.Step();
        world.Step();
        var run = world.Wired;
        Assert.Equal(WiredPhase.Wave, run.Phase);
        run.Groups.Clear();
        foreach (var other in run.Foes.Skip(1).ToList())
            world.Damage(other.Machine, 1_000_000, Race.Blue);
        world.Step();
        var foe = Assert.Single(run.Foes).Machine;
        Hero(world).Position = world.Nav.NearestFreePoint(Door(world), false)!.Value;
        var kind = WiredCatalogue.FindTower(tower)!;
        var at = world.Nav.NearestFreePoint(foe.Position + new Vector2(12, 0), false)!.Value;
        var built = world.AddConstruction(Race.Red, at, WiredTower.Type, kind.SubType, kind.Level, 0, complete: true);
        if (!armed)
            built.Weapons.Clear();
        return (world, foe, built);
    }

    [Fact]
    public void A_tower_fires_at_the_foes_and_its_kills_count_for_the_hero()
    {
        var (world, foe, tower) = TowerByAFoe("turret3", armed: true);
        var kills = Red(world).Stats.Kills;

        var events = Run(world, 20);

        Assert.Contains(events, e => e.Kind == GameEventKind.Fired && e.Actor == tower.Id);
        Assert.True(foe.IsDead);
        Assert.Equal(Race.Red, foe.LastHitBy);
        Assert.Equal(kills + 1, Red(world).Stats.Kills);
    }

    [Fact]
    public void Foes_attack_a_tower_and_a_fallen_tower_leaves_no_wreck()
    {
        var (world, foe, tower) = TowerByAFoe("turret1", armed: false);
        var durability = tower.HitPoints + tower.Armour;

        var events = new List<GameEvent>();
        for (var i = 0; i < 60 * GameWorld.TicksPerSecond && !tower.IsDead && tower.HitPoints + tower.Armour == durability; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }

        Assert.True(tower.HitPoints + tower.Armour < durability, "the foe never fired at the tower");
        Assert.Contains(events, e => e.Kind == GameEventKind.Fired && e.Actor == foe.Id);

        var wrecks = world.Debris.Count;
        world.Damage(tower, 1_000_000, Race.Blue);
        world.Step();
        Assert.Null(world.Find(tower.Id));
        Assert.Equal(wrecks, world.Debris.Count);
    }

    // ---- pulling down ------------------------------------------------------------------------------

    [Fact]
    public void Scrap_gives_back_half_the_price_of_a_site_or_of_a_whole_tower_and_less_of_a_damaged_one_and_leaves_no_wreck()
    {
        var world = OutOn("dozer1");
        Away(world);
        var state = Red(world);
        var site = PlaceNear(world, "turret2", 6, 9);
        var whole = PlaceNear(world, "turret2", 12, 16);
        whole.MakeComplete();
        var wrecks = world.Debris.Count;

        var bank = Bank(world);
        Assert.Equal(40, world.ScrapRefund(state, site));
        Assert.Null(Do(world, new WiredScrapTowerCommand(Race.Red, site.Id)));
        var scrapped = Assert.Single(world.Events, e => e.Kind == GameEventKind.WiredTowerScrapped);
        Assert.Equal((site.Id, "40"), (scrapped.Actor, scrapped.Detail));
        Assert.Equal(bank + 40, Bank(world));
        Assert.Null(world.Find(site.Id));
        Assert.True(world.Nav.IsAreaFree(site.Footprint));

        whole.HitPoints = whole.MaxHitPoints * 3 / 10;
        var refund = 80 * 50 * whole.HitPoints / (100 * whole.MaxHitPoints);
        Assert.Equal(refund, world.ScrapRefund(state, whole));
        whole.HitPoints = whole.MaxHitPoints;
        Assert.Equal(40, world.ScrapRefund(state, whole));
        whole.HitPoints = whole.MaxHitPoints * 3 / 10;
        bank = Bank(world);
        Assert.Null(Do(world, new WiredScrapTowerCommand(Race.Red, whole.Id)));
        Assert.Equal(bank + refund, Bank(world));

        Assert.Equal(wrecks, world.Debris.Count);
        Assert.Empty(world.TowersOf(Race.Red));
        Assert.Equal(0, world.Races[Race.Red].Score.ConstructionsLost);
    }

    [Fact]
    public void Any_chassis_pulls_down_its_own_towers_within_thirty_metres()
    {
        var world = OutOn("dozer1");
        var tower = PlaceNear(world, "turret1", 13, 20);
        var state = Red(world);

        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Board(world, "grunt2");
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);

        Assert.Equal("no tower of yours there", world.WhyNoScrap(state, Pod(world).Id));
        Assert.Equal("no tower of yours there", world.WhyNoScrap(state, 0));
        var enemy = world.AddConstruction(Race.Blue, world.Nav.NearestFreePoint(hero.Position + new Vector2(-8, 8), false)!.Value,
            WiredTower.Type, "TURRET", 1, 0, complete: true);
        Assert.Equal("no tower of yours there", world.WhyNoScrap(state, enemy.Id));
        hero.Position = tower.Position + new Vector2(31, 0);
        Assert.Equal("too far", world.WhyNoScrap(state, tower.Id));
        hero.Position = tower.Position + new Vector2(29, 0);
        Assert.Null(world.WhyNoScrap(state, tower.Id));
    }

    // ---- the waves still come ----------------------------------------------------------------------

    [Fact]
    public void Foes_still_reach_the_pod_past_eight_towers_round_it()
    {
        var world = OutOn("titan6");
        var (run, hero, door) = (world.Wired!, Hero(world), Door(world));
        hero.Position = door;
        // At the wheel it fires at nothing of its own accord.
        hero.DrivenByPlayer = true;

        // As close round the door as they may stand, the nearest places first.
        for (var i = 0; i < 8; i++)
        {
            var at = Ring(world, "turret1", 12, 30).OrderBy(p => Vector2.DistanceSquared(p, door)).First();
            Place(world, "turret1", at).MakeComplete();
        }
        var towers = world.TowersOf(Race.Red);
        Assert.Equal(8, towers.Count);
        Assert.All(towers, t => Assert.InRange(Vector2.Distance(t.Position, door), 12, 30));

        // Every place the waves come from still has its way to the door.
        foreach (var spawn in run.SpawnPoints)
        {
            var path = world.Nav.FindPath(spawn, door, crossesWater: false, maxExpanded: int.MaxValue);
            Assert.NotEmpty(path);
            Assert.True(Vector2.Distance(path[^1], door) <= 2 * 4, $"from {spawn} the way ends at {path[^1]}");
        }

        // A machine sent from outside the ring drives up to the door between them, with the hero out of its way.
        foreach (var tower in towers)
            tower.Weapons.Clear();
        Away(world);
        var outside = world.Nav.NearestFreePoint(door + Vector2.Normalize(towers[0].Position - door) * 45, false)!.Value;
        var grunt = world.AddMachine(Race.Blue, outside, "AGGRESSOR", "GRUNT", 1, null);
        Assert.Null(Do(world, new MoveCommand(Race.Blue, [grunt.Id], door)));
        for (var i = 0; i < 60 * GameWorld.TicksPerSecond && Vector2.Distance(grunt.Position, door) > 4; i++)
            world.Step();
        Assert.True(Vector2.Distance(grunt.Position, door) <= 4, $"stopped at {grunt.Position}, {Vector2.Distance(grunt.Position, door)} m from the door");
        Assert.Equal(8, world.TowersOf(Race.Red).Count);
    }

    // ---- what the HUD reads, and the checksum ------------------------------------------------------

    [Fact]
    public void The_view_counts_the_towers_and_lists_every_kind_with_its_price_and_why_not()
    {
        var world = OutOn("dozer1");
        Away(world);
        var state = Red(world);

        var view = world.SpecialView(Race.Red)!;
        Assert.Equal(new WiredSpecialView(ChassisSpecial.Towers, "Towers", null, 0, 0, 0, 2, true, null), view);
        var kinds = world.TowerKinds(state);
        Assert.Equal(WiredCatalogue.Towers, kinds.Select(k => k.Tower));
        Assert.Equal(WiredCatalogue.Towers.Select(t => world.TowerPrice(state, t)), kinds.Select(k => k.Price));
        Assert.Equal([null, null, "this chassis cannot build that", "this chassis cannot build that", "this chassis cannot build that", "this chassis cannot build that"],
            kinds.Select(k => k.Why));

        PlaceNear(world, "turret1", 6, 9);
        PlaceNear(world, "turret2", 12, 16);
        view = world.SpecialView(Race.Red)!;
        Assert.Equal((2, 2, true), (view.Count, view.Most, view.Ready));
        Assert.All(world.TowerKinds(state).Take(2), k => Assert.Equal("2 towers stand already", k.Why));

        world.Races[Race.Red].Bmu = 0;
        Assert.Null(world.WhyNoScrap(state, world.TowersOf(Race.Red)[0].Id));
    }

    [Fact]
    public void Asking_about_towers_changes_nothing_in_the_world()
    {
        var world = OutOn("builder3");
        Away(world);
        var (state, hero) = (Red(world), Hero(world));
        var site = PlaceNear(world, "turret3", 6, 9);
        var whole = PlaceNear(world, "sentry3", 12, 16);
        whole.MakeComplete();
        whole.HitPoints /= 2;
        var before = world.Checksum();

        foreach (var tower in WiredCatalogue.Towers)
        {
            foreach (var orientation in new[] { 0, 45, 90 })
            {
                world.WhyNoTower(state, tower.Id, hero.Position + new Vector2(10, 3), orientation);
                world.WhyNoTower(state, tower.Id, site.Position, orientation);
            }
            world.TowerPrice(state, tower);
        }
        world.TowerKinds(state);
        world.SpecialView(Race.Red);
        world.TowerWorkOf(hero);
        foreach (var tower in world.TowersOf(Race.Red))
        {
            world.WhyNoScrap(state, tower.Id);
            world.ScrapRefund(state, tower);
        }

        Assert.Equal(before, world.Checksum());
        Assert.Equal(2, world.TowersOf(Race.Red).Count);
    }

    [Fact]
    public void Two_worlds_fed_the_same_commands_keep_equal_checksums_with_towers_built_mended_fighting_and_pulled_down()
    {
        var worlds = new[] { OutOn("titan6"), OutOn("titan6") };
        foreach (var world in worlds)
            Away(world);
        // The Titan is broad: its edge lies near nine metres out.
        var edge = Hero(worlds[0]).Radius;
        var places = Ring(worlds[0], "turret3", edge + 3, edge + 6).Take(1).Concat(Ring(worlds[0], "sentry4", edge + 8, edge + 11).Take(1)).ToList();
        Assert.Equal(2, places.Count);
        Assert.Equal(worlds[0].Checksum(), worlds[1].Checksum());

        void Both(Command command)
        {
            foreach (var world in worlds)
                world.Issue(command);
        }

        Both(new WiredTowerCommand(Race.Red, "turret3", places[0], 90));
        Both(new WiredTowerCommand(Race.Red, "sentry4", places[1], 180));
        var seen = new List<GameEvent>();
        for (var tick = 0; tick < 90 * GameWorld.TicksPerSecond; tick++)
        {
            if (tick == 5 * GameWorld.TicksPerSecond)
                Both(new WiredTowerCommand(Race.Red, "turret1", Ring(worlds[0], "turret1", edge + 3, edge + 12).First(), 0));
            if (tick == 25 * GameWorld.TicksPerSecond)
            {
                // The wave comes, and a tower is hit hard enough for the hero to mend it.
                foreach (var world in worlds)
                {
                    world.Wired!.Countdown = 0;
                    var damaged = world.TowersOf(Race.Red).First(t => t.Complete);
                    damaged.HitPoints = damaged.MaxHitPoints / 2;
                }
            }
            if (tick == 60 * GameWorld.TicksPerSecond)
                Both(new WiredScrapTowerCommand(Race.Red, worlds[0].TowersOf(Race.Red)[^1].Id));
            foreach (var world in worlds)
                world.Step();
            seen.AddRange(worlds[0].Events);
            Assert.Equal(worlds[0].Checksum(), worlds[1].Checksum());
        }

        Assert.Equal(3, seen.Count(e => e.Kind == GameEventKind.ConstructionComplete));
        Assert.Single(seen, e => e.Kind == GameEventKind.WiredTowerScrapped);
        Assert.Contains(seen, e => e.Kind == GameEventKind.WiredWaveStarted);
    }
}
