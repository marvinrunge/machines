using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.WiredBench;

/// <summary>
/// A hero under simple rules, as the balance targets describe it: it stays near the pod, attacks what comes close, collects
/// wrecks and buys by its strategy. It has only what a player has, commands through <see cref="GameWorld.Issue"/>, or
/// through <paramref name="issue"/> for whoever carries them out instead (the game's run driver plays them with the
/// player's input). It tests the numbers, not the fun.
/// </summary>
internal sealed class Bot(GameWorld world, GameData data, Strategy strategy, Race side = Race.Red, Action<Command>? issue = null)
{
    private Race Side => side;

    /// <summary>How far from the pod machines are gone out to.</summary>
    private const float PodGuard = 130;

    /// <summary>How near a wreck must be in a wave for the bot to fetch it when nothing is in reach.</summary>
    private const float WreckNear = 40;

    /// <summary>The most commands of one visit to the hangar, against a command refused again and again.</summary>
    private const int MostActions = 60;

    /// <summary>The longest the hangar stays open for the hero to mend, in seconds.</summary>
    private const float MostMending = 40;

    /// <summary>How often a visit to the hangar goes down the strategy's list.</summary>
    private const int RoundsPerVisit = 3;

    /// <summary>What a gun outside a strategy's family counts for.</summary>
    private const float OtherGuns = 0.4f;

    /// <summary>The pod is mended below this share of its hit points, with up to half the bank.</summary>
    private const float MendPodBelow = 0.7f;

    private readonly WiredState _state = world.Wired!.Heroes[side];
    private int _turn, _actions, _podMended = -1;
    private float _openedAt;

    /// <summary>What it bought, by name: items by their id, upgrades with a ^, escorts by their machine.</summary>
    public SortedDictionary<string, int> Purchases { get; } = new(StringComparer.Ordinal);

    private float Bank => world.Races[Side].Bmu;

    private void Issue(Command command) => (issue ?? world.Issue)(command);

    /// <summary>One look at the world: a command or none.</summary>
    public void Think()
    {
        var run = world.Wired!;
        if (run.Phase == WiredPhase.Wave)
            Fight(run);
        else if (run.BetweenWaves)
            Prepare(run);
    }

    // ---- the wave ---------------------------------------------------------------------------------

    private void Fight(WiredRun run)
    {
        if (world.HeroOf(Side) is not { Inside: null } hero || world.WiredPod(Side) is not { } pod)
            return;
        if (Dodge(run, hero))
            return;
        UseAbilities(run, hero);
        var inReach = run.Foes.Any(f => !f.Machine.IsDead && f.Machine.DistanceTo(hero) <= 40);
        if (!inReach && NearestWreck(hero) is { } wreck)
            Go(hero, wreck);
        else if (Target(run, pod) is { } target)
        {
            if (hero.Order is not AttackOrder attack || attack.Target != target.Id)
                Issue(new AttackCommand(Side, [hero.Id], target.Id));
        }
        else if (hero.DistanceTo(pod) > 30)
            Go(hero, Home(pod));
    }

    /// <summary>Out of a circle that warns of a strike, with the thrusters if it has them.</summary>
    private bool Dodge(WiredRun run, Machine hero)
    {
        foreach (var strike in run.Strikes)
        {
            var away = hero.Position - strike.Position;
            if (away.Length() > strike.Radius + 5)
                continue;
            var way = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX;
            if (_state.Build(data).DashCooldown is not null && world.Time >= _state.DashReadyAt)
                Issue(new WiredDashCommand(Side, way));
            var place = strike.Position + way * (strike.Radius + 15);
            Go(hero, world.Nav.NearestFreePoint(place, crossesWater: false) ?? place);
            return true;
        }
        return false;
    }

    /// <summary>The machine of the wave nearest the pod, if one has come close.</summary>
    private static Machine? Target(WiredRun run, Construction pod)
    {
        Machine? nearest = null;
        var distance = PodGuard;
        foreach (var foe in run.Foes)
        {
            if (!foe.Machine.IsDead && foe.Machine.DistanceTo(pod) <= distance)
                (nearest, distance) = (foe.Machine, foe.Machine.DistanceTo(pod));
        }
        return nearest;
    }

    private Vector2? NearestWreck(Machine hero)
    {
        Vector2? nearest = null;
        var distance = WreckNear;
        foreach (var wreck in world.Debris)
        {
            var away = Vector2.Distance(wreck.Position, hero.Position);
            if (away < distance)
                (nearest, distance) = (wreck.Position, away);
        }
        return nearest;
    }

    private void UseAbilities(WiredRun run, Machine hero)
    {
        for (var slot = 0; slot < _state.Loadout.Abilities.Count; slot++)
        {
            if (_state.Loadout.Abilities[slot] is { } item && AimFor(item.Catalogue, run, hero) is { } aim && world.WhyNoAbility(_state, slot, aim) is null)
                Issue(new WiredAbilityCommand(Side, slot, aim));
        }
    }

    /// <summary>Where an ability would be worth using now, or null.</summary>
    private Vector2? AimFor(string ability, WiredRun run, Machine hero)
    {
        var foes = run.Foes.Where(f => !f.Machine.IsDead).ToList();
        switch (ability)
        {
            case "discharge":
                return foes.Any(f => f.Machine.DistanceTo(hero) <= 12) ? hero.Position : null;
            case "supercharge":
                return hero.HitPoints < hero.MaxHitPoints * 0.6f ? hero.Position : null;
            case "minefield":
                return foes.Select(f => f.Machine).Where(m => m.DistanceTo(hero) <= 60).OrderBy(m => m.DistanceTo(hero)).ThenBy(m => m.Id).FirstOrDefault()?.Position;
            case "vortex":
                return Crowd(foes, hero, 50, 90);
            case "ionstrike":
                return Crowd(foes, hero, 45, 400);
            case "treachery":
                return foes.Where(f => !f.IsBoss && f.Machine.DistanceTo(hero) <= 80)
                    .OrderByDescending(f => f.Machine.Stats.Cost).ThenBy(f => f.Machine.Id).FirstOrDefault()?.Machine.Position;
            default:
                return null;
        }
    }

    /// <summary>A place where a blast catches a boss or several machines, far enough from the hero.</summary>
    private static Vector2? Crowd(List<WiredFoe> foes, Machine hero, float nearest, float furthest)
    {
        Vector2? best = null;
        var most = 1;
        foreach (var foe in foes)
        {
            var distance = foe.Machine.DistanceTo(hero);
            if (distance < nearest || distance > furthest)
                continue;
            var around = foes.Count(f => Vector2.Distance(f.Machine.Position, foe.Machine.Position) <= 30) + (foe.IsBoss ? 1 : 0);
            if (around > most)
                (best, most) = (foe.Machine.Position, around);
        }
        return best;
    }

    // ---- between waves ----------------------------------------------------------------------------

    private void Prepare(WiredRun run)
    {
        if (world.HeroOf(Side) is not { Inside: null } hero || world.WiredPod(Side) is not { } pod)
            return;
        if (!_state.HangarOpen)
        {
            if (Loot(run, hero, pod) is { } place)
                Go(hero, place);
            else if (world.WhyNoHangar(_state) is null)
            {
                (_actions, _turn, _openedAt) = (0, 0, world.Time);
                Issue(new WiredHangarCommand(Side, true));
            }
            else
                Go(hero, Home(pod));
            return;
        }
        if (_actions < MostActions && Shop(run))
            _actions++;
        else if (Mended(hero) || world.Time - _openedAt >= MostMending)
            Issue(new WiredReadyCommand(Side));
    }

    /// <summary>The nearest crate or wreck there is time to fetch and be back from before the wave.</summary>
    private Vector2? Loot(WiredRun run, Machine hero, Construction pod)
    {
        var speed = MathF.Max(1, hero.Stats.Speed);
        Vector2? best = null;
        var nearest = float.MaxValue;
        foreach (var place in world.WiredPickups.Select(c => c.Position).Concat(world.Debris.Select(d => d.Position)))
        {
            var away = Vector2.Distance(hero.Position, place);
            if (away < nearest && (away + Vector2.Distance(place, pod.Position)) / speed + 2 < run.Countdown)
                (best, nearest) = (place, away);
        }
        return best;
    }

    private bool Mended(Machine hero) => hero.HitPoints >= hero.MaxHitPoints && hero.Armour >= hero.MaxArmour;

    private static Vector2 Home(Construction pod) => pod.EntranceOutside ?? pod.Position;

    private void Go(Machine hero, Vector2 place)
    {
        if (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, place) > 3)
            Issue(new MoveCommand(Side, [hero.Id], place));
    }

    // ---- the hangar -------------------------------------------------------------------------------

    /// <summary>One thing done in the hangar, if there is one left. What was found costs nothing, so even a hero who never buys puts it on.</summary>
    private bool Shop(WiredRun run) =>
        !strategy.WearsNothing && (ChooseChip() || PlaceChip() || ChangeChassis() || WearSpares())
        || strategy.Buys && (SellSpares() || MendPod(run) || Buy(run));

    private bool ChooseChip()
    {
        if (_state.ChipChoice.Count == 0)
            return false;
        var families = _state.Chips.Select(c => c.Chip.Family).ToHashSet();
        var choice = _state.ChipChoice.FindIndex(c => families.Contains(WiredChips.Find(c)!.Family));
        Issue(new WiredChipChoiceCommand(Side, Math.Max(0, choice)));
        return true;
    }

    /// <summary>A chip goes into a free socket, one that links it if there is one.</summary>
    private bool PlaceChip()
    {
        var chips = _state.Loadout.Chips;
        var free = Enumerable.Range(0, chips.Count).Where(s => chips[s] is null).ToList();
        if (free.Count == 0 || _state.Inventory.FirstOrDefault(i => i.Kind == WiredKind.Chip) is not { } chip)
            return false;
        var family = WiredChips.Find(chip.Catalogue)!.Family;
        bool Same(int socket) => chips[socket] is { } other && WiredChips.Find(other.Catalogue)!.Family == family;
        var linked = free.Where(s => WiredChips.Traces.Any(t => t.A == s && Same(t.B) || t.B == s && Same(t.A))).Take(1).ToList();
        Issue(new WiredChipCommand(Side, chip.Id, linked.Count > 0 ? linked[0] : free[0]));
        return true;
    }

    /// <summary>On to the highest chassis of the ladder it owns.</summary>
    private bool ChangeChassis()
    {
        var best = Enumerable.Reverse(strategy.Ladder).Select(id => _state.Items.Find(i => i.Catalogue == id)).FirstOrDefault(i => i is not null);
        if (best is null || best.Id == _state.Loadout.Chassis.Id || _state.WhyNotWear(data, _state.Loadout.OnChassis(best)) is not null)
            return false;
        Issue(new WiredChassisCommand(Side, best.Id));
        return true;
    }

    /// <summary>Whether a loadout has the power it draws and does not drag.</summary>
    private bool Fits(HeroLoadout loadout)
    {
        var build = HeroBuild.Resolve(data, loadout);
        return build.PowerShare >= 1 && build.LoadShare <= 1;
    }

    private IEnumerable<WiredSlot> GunSlots => _state.Loadout.ChassisEntry.Slots.Where(s => s.Fixed is null).Select(s => WiredSlot.Weapon(s.Mounting));

    /// <summary>
    /// What a loadout's guns are worth to this strategy: their damage, more for what reaches further. A gun outside its
    /// family counts for less, so that it makes do with one until its own are on offer and then changes over.
    /// </summary>
    private float Worth(HeroLoadout loadout) => HeroBuild.Resolve(data, loadout).Weapons.Where(w => w.Item is not null)
        .Sum(w => OnOrders(w) * MathF.Sqrt(w.Spec.Range / 45) * (IsOwnGun(w.Item!.Catalogue) ? 1 : OtherGuns));

    /// <summary>What a gun does for this hero, which fires on orders: the build counts a VARIABLE burst whole, as a driver fires it, and here it comes to the average.</summary>
    private static float OnOrders(HeroWeapon gun) => gun.Spec.BurstTiming == "VARIABLE" && gun.Spec.BurstRounds > 1
        ? gun.DamagePerSecond * (1 + gun.Spec.BurstRounds) / 2f / gun.Spec.BurstRounds
        : gun.DamagePerSecond;

    /// <summary>The first free slot of a kind.</summary>
    private static WiredSlot? FreeSlot(IReadOnlyList<WiredItem?> places, Func<int, WiredSlot> slot)
    {
        for (var i = 0; i < places.Count; i++)
        {
            if (places[i] is null)
                return slot(i);
        }
        return null;
    }

    /// <summary>What it owns goes on if that makes the hero better.</summary>
    private bool WearSpares()
    {
        var loadout = _state.Loadout;
        foreach (var spare in _state.Inventory)
        {
            WiredSlot? place = null;
            if (spare.Kind == WiredKind.Weapon)
            {
                var now = Worth(loadout);
                place = GunSlots.Where(s => loadout.WhyNotFit(spare, s) is null && Fits(loadout.With(s, spare)) && Worth(loadout.With(s, spare)) > now * 1.01f)
                    .Cast<WiredSlot?>().FirstOrDefault();
            }
            else if (spare.Kind == WiredKind.Module)
                place = FreeSlot(loadout.Modules, WiredSlot.Module);
            else if (spare.Kind == WiredKind.Ability)
                place = FreeSlot(loadout.Abilities, WiredSlot.Ability);
            if (place is { } slot && Fits(loadout.With(slot, spare)))
            {
                Issue(new WiredEquipCommand(Side, spare.Id, slot));
                return true;
            }
        }
        return false;
    }

    /// <summary>What did not go on is worth more in the bank; what brings nothing (a find, the first chassis) the hangar does not sell.</summary>
    private bool SellSpares()
    {
        if (_state.Inventory.FirstOrDefault(i => i.Kind != WiredKind.Chip && _state.SellValue(i) > 0) is not { } spare)
            return false;
        Issue(new WiredSellCommand(Side, spare.Id));
        return true;
    }

    private bool MendPod(WiredRun run)
    {
        if (_podMended == run.Wave || world.WiredPod(Side) is not { } pod || pod.HitPoints >= pod.MaxHitPoints * MendPodBelow || Bank < 20)
            return false;
        _podMended = run.Wave;
        Issue(new WiredPodRepairCommand(Side, (int)(Bank / 2)));
        Note("pod repair");
        return true;
    }

    /// <summary>The next purchase of this visit: down the strategy's list and round again, until the bank runs out or a chassis has to be saved for.</summary>
    private bool Buy(WiredRun run)
    {
        for (; _turn < strategy.Turns.Count * RoundsPerVisit; _turn++)
        {
            var (command, name, saving) = Next(strategy.Turns[_turn % strategy.Turns.Count], run);
            if (saving)
            {
                _turn = strategy.Turns.Count * RoundsPerVisit;
                return false;
            }
            if (command is not null)
            {
                _turn++;
                Note(name!);
                Issue(command);
                return true;
            }
        }
        if (_state.Rerolled || Bank < 200)
            return false;
        // Another look at the shop, with what is left.
        _turn = 0;
        Issue(new WiredRerollCommand(Side));
        return true;
    }

    private void Note(string name) => Purchases[name] = Purchases.GetValueOrDefault(name) + 1;

    private (Command? Command, string? Name, bool Saving) Next(Goal goal, WiredRun run) => goal switch
    {
        Goal.Chassis => NextChassis(),
        Goal.Weapon => NextWeapon(),
        Goal.Module => NextModule(),
        Goal.Ability => NextAbility(),
        _ => NextEscort(run),
    };

    private (Command?, string?, bool) NextChassis()
    {
        if (strategy.Ladder.FirstOrDefault(id => !_state.Reached.Contains(id) && !_state.Items.Exists(i => i.Catalogue == id)) is not { } id)
            return (null, null, false);
        var price = _state.Priced(WiredCatalogue.Price(id));
        if (strategy.Passing.Contains(id))
            price += _state.Priced(WiredCatalogue.Price(strategy.Ladder[strategy.Ladder.ToList().IndexOf(id) + 1]));
        return Bank >= price ? (new WiredBuyCommand(Side, id), id, false) : (null, null, true);
    }

    /// <summary>What adds most damage for each BMU among the offers it can pay and the next Mk of what it wears.</summary>
    private (Command?, string?, bool) NextWeapon()
    {
        var loadout = _state.Loadout;
        var now = Worth(loadout);
        (Command? Command, string? Name, float Value) best = (null, null, 0);
        foreach (var slot in GunSlots)
        {
            if (loadout.At(slot) is { } worn && _state.UpgradePrice(worn) is { } upgrade && upgrade <= Bank)
                Consider(loadout.Replacing(worn with { Mk = worn.Mk + 1 }), upgrade, new WiredUpgradeCommand(Side, worn.Id), worn.Catalogue + "^");
            foreach (var id in _state.Offer.OfType<string>().Where(IsGun))
            {
                var trial = new WiredItem(-1, WiredKind.Weapon, id);
                var price = _state.Priced(WiredCatalogue.Price(id));
                if (price <= Bank && loadout.WhyNotFit(trial, slot) is null)
                    Consider(loadout.With(slot, trial), price, new WiredBuyCommand(Side, id), id);
            }
        }
        return (best.Command, best.Name, false);

        void Consider(HeroLoadout then, int price, Command command, string name)
        {
            var gain = Worth(then) - now;
            if (gain > now * 0.15f && Fits(then) && gain / price > best.Value)
                best = (command, name, gain / price);
        }
    }

    private bool IsOwnGun(string id) => strategy.Guns.Count == 0 || strategy.Guns.Any(id.Contains);

    private bool IsGun(string id) => WiredCatalogue.FindWeapon(id) is not null && IsOwnGun(id);

    private (Command?, string?, bool) NextModule()
    {
        var loadout = _state.Loadout;
        if (FreeSlot(loadout.Modules, WiredSlot.Module) is { } free)
        {
            foreach (var id in strategy.Modules.Where(_state.Offer.Contains))
            {
                if (!loadout.Worn.Any(w => w.Catalogue == id) && _state.Priced(WiredCatalogue.Price(id)) <= Bank
                    && Fits(loadout.With(free, new WiredItem(-1, WiredKind.Module, id))))
                    return (new WiredBuyCommand(Side, id), id, false);
            }
        }
        foreach (var worn in loadout.Modules.OfType<WiredItem>())
        {
            if (_state.UpgradePrice(worn) is { } upgrade && upgrade <= Bank && Fits(loadout.Replacing(worn with { Mk = worn.Mk + 1 })))
                return (new WiredUpgradeCommand(Side, worn.Id), worn.Catalogue + "^", false);
        }
        return (null, null, false);
    }

    private (Command?, string?, bool) NextAbility()
    {
        var loadout = _state.Loadout;
        if (FreeSlot(loadout.Abilities, WiredSlot.Ability) is not { } free)
            return (null, null, false);
        foreach (var id in strategy.Abilities.Where(_state.Offer.Contains))
        {
            if (!loadout.Worn.Any(w => w.Catalogue == id) && _state.Priced(WiredCatalogue.Price(id)) <= Bank
                && Fits(loadout.With(free, new WiredItem(-1, WiredKind.Ability, id))))
                return (new WiredBuyCommand(Side, id), id, false);
        }
        return (null, null, false);
    }

    /// <summary>The newest machine on the ground that the bank pays, while the squad has room.</summary>
    private (Command?, string?, bool) NextEscort(WiredRun run)
    {
        if (world.Escorts(Side).Count() >= WiredBalance.SquadLimit(run.ShopWave, WiredChips.SquadExtra(_state)))
            return (null, null, false);
        foreach (var tier in WiredCatalogue.Tiers.Where(t => t.Wave <= run.ShopWave && t.SubType != "BOSS").Reverse())
        {
            var key = new ResearchKey(tier.Type, tier.SubType, tier.Level, tier.Combos[0]);
            var price = _state.Priced((data.Parm.Machine(key.TypeName).Level(key.HardwareLevel).Cost * WiredBalance.EscortPercent + 50) / 100);
            if (price <= Bank)
                return (new WiredSquadBuyCommand(Side, key), $"escort {tier.SubType} {tier.Level}", false);
        }
        return (null, null, false);
    }
}
