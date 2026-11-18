using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>A tower as the strip of the tower mode shows it: what it costs the hero, and why it cannot be placed now, wherever.</summary>
/// <param name="Why">null if only the place could still be wrong.</param>
public sealed record WiredTowerKind(WiredTower Tower, int Price, string? Why);

/// <summary>
/// The towers a constructor hero raises, the remake's own addition: the original's missile emplacements, placed and paid
/// for at once at the original's price, built by the hero standing at the site (four times the original's rate, also while
/// it is driven), mended by it for BMUs as the original's constructors mend, and pulled down for part of their price.
/// Which a chassis builds goes by its machine's place in the original's construction order; how many may stand by the
/// catalogue. A battle has none: its buildings cannot be hit, but towers would still fire.
/// </summary>
public sealed partial class GameWorld
{
    private const string NoTowersInBattle = "no towers in a battle";

    /// <summary>The towers each chassis builds, by its id, worked out once.</summary>
    private readonly Dictionary<string, IReadOnlyList<WiredTower>> _towersFor = [];

    /// <returns>
    /// null if the hero can place a tower of the catalogue there now, turned by <paramref name="orientation"/> degrees,
    /// otherwise why not. The ghost of the tower mode asks the same; it changes nothing.
    /// </returns>
    public string? WhyNoTower(WiredState state, string tower, Vector2 position, int orientation)
    {
        if (WhyNoTowerKind(state, tower) is { } why)
            return why;
        if (orientation % 90 != 0)
            return "a tower turns only by quarter turns";
        var hero = HeroOf(state)!;
        if (Vector2.DistanceSquared(hero.Position, position) > WiredBalance.TowerReach * WiredBalance.TowerReach)
            return "too far";
        return WhyNoTowerThere(TowerProbe(state.Race, WiredCatalogue.FindTower(tower)!, position, orientation));
    }

    /// <returns>null if a tower can stand where <paramref name="probe"/> stands, on open even ground off the pods' doors and clear of every machine, otherwise why not.</returns>
    private string? WhyNoTowerThere(Construction probe)
    {
        if (!Nav.OnPlanet(probe.Position) || !Nav.IsAreaFree(probe.Footprint))
            return "obstacles or uneven terrain";
        // The waves march to the pod's door, and the hero comes out by it.
        if (Wired!.Heroes.Values.Select(state => WiredPod(state)).OfType<Construction>().Any(pod => probe.DistanceFrom(Doorstep(pod)) < WiredBalance.PodKeepOut))
            return "too close to the pod";
        // Every machine, not the index of where they stood at the start of the tick: the command comes before it.
        if (Machines.Any(m => !m.IsDead && m.Inside is null && probe.DistanceFrom(m.Position) < m.Radius))
            return "something stands there";
        return null;
    }

    /// <returns>
    /// null if the hero could place a tower of that kind now somewhere, otherwise why not: the battle, the hero, the chassis,
    /// the towers standing and the bank, in that order.
    /// </returns>
    private string? WhyNoTowerKind(WiredState state, string tower)
    {
        if (WhyNoTowers(state) is { } why)
            return why;
        if (WiredCatalogue.FindTower(tower) is not { } kind)
            return "no such tower";
        if (!TowersFor(state.Build(Data).Chassis).Contains(kind))
            return "this chassis cannot build that";
        var standing = TowersOf(state).Count;
        if (standing >= MostTowers(state))
            return $"{standing} towers stand already";
        return Bank(state) < TowerPrice(state, kind) ? "not enough BMUs" : null;
    }

    /// <returns>null if the hero is where it can deal with towers, otherwise why not: in a battle, down, in the pod or at its door.</returns>
    private string? WhyNoTowers(WiredState state)
    {
        if (Battle is not null)
            return NoTowersInBattle;
        if (HeroOf(state) is not { } hero)
            return "the hero is down";
        if (hero.Inside is not null)
            return "not from inside the pod";
        return hero.Doorway is not null ? "not from the doorway" : null;
    }

    /// <summary>The tower as it would stand there, to ask about its footprint; it is not on the map.</summary>
    private Construction TowerProbe(Race race, WiredTower tower, Vector2 position, int orientation)
    {
        var spec = Data.Parm.Construction($"{WiredTower.Type} {tower.SubType}");
        return new Construction(0, race, position, spec, spec.Level(tower.Level), orientation);
    }

    /// <summary>
    /// Places a tower: its whole price comes off the bank at once, with no warning of a low bank, as in the hangar. Never by
    /// the original's orders: those of a driven machine never run, and the hero builds it by standing at it.
    /// </summary>
    private string? PlaceTower(WiredState state, WiredTowerCommand command)
    {
        if (WhyNoTower(state, command.Tower, command.Position, command.Orientation) is { } why)
            return why;
        var tower = WiredCatalogue.FindTower(command.Tower)!;
        Charge(state, TowerPrice(state, tower));
        RaiseTower(state.Race, tower, command.Position, command.Orientation, state.Player);
        return null;
    }

    /// <summary>
    /// An unfinished site of the original's emplacement stands at a place, with the weapons its research item gives it, for
    /// whoever pays or not: nothing here asks whether it may. A hero's tower is its player's (<paramref name="owner"/>).
    /// </summary>
    private Construction RaiseTower(Race race, WiredTower tower, Vector2 position, int orientation, PlayerId? owner = null)
    {
        var site = AddConstruction(race, position, WiredTower.Type, tower.SubType, tower.Level, orientation, complete: false, owner: owner);
        Raise(new GameEvent(GameEventKind.WiredTowerPlaced, site.Id, site.Position, Detail: tower.Id));
        return site;
    }

    /// <summary>What a tower costs this hero: the original's price of the emplacement, with the difficulty and Trader's discount.</summary>
    public int TowerPrice(WiredState state, WiredTower tower) =>
        state.Priced(Data.Parm.Construction($"{WiredTower.Type} {tower.SubType}").Level(tower.Level).Cost);

    /// <returns>null if the hero can pull down a tower of its own now, otherwise why not.</returns>
    public string? WhyNoScrap(WiredState state, int tower)
    {
        if (Battle is not null)
            return NoTowersInBattle;
        if (HeroOf(state) is not { } hero)
            return "the hero is down";
        if (hero.Inside is not null)
            return "not from inside the pod";
        if (Find<Construction>(tower) is not { IsDead: false } building || building.Race != state.Race || building.Owner != state.Player
            || !building.IsType(WiredTower.Type))
            return "no tower of yours there";
        return Vector2.DistanceSquared(hero.Position, building.Position) > WiredBalance.TowerReach * WiredBalance.TowerReach ? "too far" : null;
    }

    /// <summary>
    /// What pulling a tower down brings back: what it costs now at the share the hangar pays for what it buys back (Recycler
    /// included), of a finished tower only by the share of its hit points left. No price is kept for each tower.
    /// </summary>
    public int ScrapRefund(WiredState state, Construction tower)
    {
        var worth = (long)state.Priced(tower.Stats.Cost) * WiredChips.SellPercent(state);
        return (int)(tower.Complete ? worth * tower.HitPoints / (100L * Math.Max(1, tower.MaxHitPoints)) : worth / 100);
    }

    /// <summary>Pulls a tower down, by any chassis: part of its price comes back, and it leaves no wreck, nor counts as lost.</summary>
    private string? ScrapTower(WiredState state, int tower)
    {
        if (WhyNoScrap(state, tower) is { } why)
            return why;
        var building = Find<Construction>(tower)!;
        var refund = ScrapRefund(state, building);
        Refund(state, refund);
        _actors.Remove(building.Id);
        _hitPointCarry.Remove(building.Id);
        _turretTargets.Remove(building.Id);
        _turretOrders.Remove(building.Id);
        Nav.SetBuilding(building.Footprint, present: false);
        Raise(new GameEvent(GameEventKind.WiredTowerScrapped, building.Id, building.Position, Detail: refund.ToString()));
        return null;
    }

    /// <summary>
    /// A constructor hero builds on the nearest site of its race in reach, or, with none, mends the nearest damaged tower there
    /// for BMUs, both at four times the original's rate. Building costs nothing more: the tower was paid for when it was
    /// placed. The original's constructor reports no finished job here, and the hero is not turned to the site.
    /// </summary>
    private void UpdateTowerWork(WiredState state, Machine hero)
    {
        if (TowerWork(state, hero) is { } site)
            WorkOnTower(hero, site);
    }

    /// <summary>A tick of a constructor's work on a tower: building a site up, or mending a finished tower for BMUs.</summary>
    private void WorkOnTower(Machine builder, Construction tower)
    {
        if (tower.Complete)
        {
            Mend(builder, tower, WiredBalance.TowerBuildSpeed);
            return;
        }
        tower.AddConstruction(TowerRate(builder) * Dt);
        if (!tower.Complete)
            return;
        tower.MakeComplete();
        Races[tower.Race].Score.ConstructionsBuilt++;
        Raise(new GameEvent(GameEventKind.ConstructionComplete, tower.Id, tower.Position, builder.Id, tower.TypeName));
    }

    /// <summary>The BMUs a second a constructor puts into a tower, building or mending: four times its machine's rate.</summary>
    private static float TowerRate(Machine builder) => (builder.Stats.ConstructionRate ?? 0) / 60f * WiredBalance.TowerBuildSpeed;

    /// <summary>
    /// The tower a hero works on, if it is a constructor out on the map: the nearest site of its race within the original's
    /// reach, or else the nearest damaged tower there while the hero's bank has BMUs to mend it with. Nearest by the edge, the
    /// smaller id first between two as near.
    /// </summary>
    private Construction? TowerWork(WiredState state, Machine hero)
    {
        if (Battle is not null || hero.IsDead || hero.Inside is not null || (hero.Stats.ConstructionRate ?? 0) <= 0)
            return null;
        Construction? site = null, damaged = null;
        float siteGap = 0, damagedGap = 0;
        foreach (var tower in TowersOf(state.Race))
        {
            if (!InReach(hero, tower))
                continue;
            var gap = tower.DistanceFrom(hero.Position);
            if (!tower.Complete)
            {
                if (site is null || gap < siteGap)
                    (site, siteGap) = (tower, gap);
            }
            else if (tower.HitPoints < tower.MaxHitPoints && (damaged is null || gap < damagedGap))
                (damaged, damagedGap) = (tower, gap);
        }
        return site ?? (Bank(state) > 0 ? damaged : null);
    }

    /// <summary>The tower a Wired hero is building or mending now, for its view to work at; null for any other machine.</summary>
    public Construction? TowerWorkOf(Machine hero) => HeroStateOf(hero) is { } state ? TowerWork(state, hero) : null;

    /// <summary>The towers a chassis builds, smallest first: those of the catalogue up to its machine's most advanced construction.</summary>
    public IReadOnlyList<WiredTower> TowersFor(WiredChassis chassis)
    {
        if (_towersFor.TryGetValue(chassis.Id, out var known))
            return known;
        IReadOnlyList<WiredTower> towers = [];
        if ((chassis.Special & ChassisSpecial.Towers) != 0
            && Data.Parm.Machine(chassis.TypeName).Level(chassis.Level).MostAdvancedConstruction is { } most)
        {
            var order = Data.ConstructionOrder.UpTo(most).Where(e => e.Type == WiredTower.Type).ToList();
            towers = WiredCatalogue.Towers.Where(t => order.Any(e => e.SubType == t.SubType && e.HardwareLevel == t.Level)).ToList();
        }
        return _towersFor[chassis.Id] = towers;
    }

    /// <summary>The towers of a race standing, finished or not, by id: the waves' at a wave's end, those a hero works on.</summary>
    public IReadOnlyList<Construction> TowersOf(Race race) =>
        Constructions.Where(c => c.Race == race && !c.IsDead && c.IsType(WiredTower.Type)).OrderBy(c => c.Id).ToList();

    /// <summary>
    /// The towers a hero's player has standing, finished or not, by id: what counts against its chassis' most. A race's only
    /// hero has every tower of its race.
    /// </summary>
    public IReadOnlyList<Construction> TowersOf(WiredState state) =>
        Constructions.Where(c => c.Race == state.Race && c.Owner == state.Player && !c.IsDead && c.IsType(WiredTower.Type)).OrderBy(c => c.Id).ToList();

    /// <summary>The most towers the hero's chassis lets stand at once. More may stand from a chassis it wore before; they stay.</summary>
    public int MostTowers(WiredState state) => state.Build(Data).Chassis.Towers;

    /// <summary>Every tower of the catalogue with its price for this hero and why it cannot be placed now, for the tower mode's strip.</summary>
    public IReadOnlyList<WiredTowerKind> TowerKinds(WiredState state) =>
        WiredCatalogue.Towers.Select(t => new WiredTowerKind(t, TowerPrice(state, t), WhyNoTowerKind(state, t.Id))).ToList();

    /// <summary>
    /// What the HUD shows of the towers: how many stand of how many, and the work under way, how far it has come (built, or
    /// hit points mended) and the seconds left at the hero's rate. Ready while the hero is where it can deal with towers.
    /// </summary>
    private WiredSpecialView? TowerView(WiredState state)
    {
        var (share, left) = (0f, 0f);
        if (HeroOf(state) is { } hero && TowerWork(state, hero) is { } work && TowerRate(hero) is var rate and > 0)
        {
            if (!work.Complete)
                (share, left) = (work.Built / work.Stats.Cost, (work.Stats.Cost - work.Built) / rate);
            else if (HitPointValue(work, rate) is var mended and > 0)
                (share, left) = (work.HitPoints / (float)work.MaxHitPoints, (work.MaxHitPoints - work.HitPoints) / mended);
        }
        var why = WhyNoTowers(state);
        return new WiredSpecialView(ChassisSpecial.Towers, "Towers", null, left, share, TowersOf(state).Count, MostTowers(state), why is null, why);
    }
}
