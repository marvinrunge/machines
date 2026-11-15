using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.Survival;

/// <summary>
/// The heroes the computer brings to a battle, and the checks every hero passes: a loadout for any chassis and tier, the
/// chassis a bot takes by the mirror rule, and a hero that does not resolve clamped to a bot's loadout, so that a hand-made
/// room can never crash or split a match.
/// </summary>
public static class BattleBots
{
    /// <summary>The bots' names, by race and their place in the team.</summary>
    private static readonly IReadOnlyList<string> Names = ["Rivet", "Flux", "Cinder", "Gauge", "Torque", "Ember", "Static", "Piston"];

    /// <summary>The order modules go on a bot: plates and frame first, the thrusters' dash from the tier of the abilities.</summary>
    private static readonly IReadOnlyList<string> ModuleOrder = ["plates", "frame", "thrusters", "repair", "drive", "targeting", "gyro", "cooler", "reactor"];

    /// <summary>The abilities a bot takes by tier, the better first; never treachery.</summary>
    private static IReadOnlyList<string> AbilityOrder(int tier) => tier switch
    {
        >= 7 => ["ionstrike", "vortex", "supercharge"],
        >= 5 => ["vortex", "supercharge", "discharge"],
        _ => ["supercharge", "discharge"],
    };

    /// <summary>A bot's name.</summary>
    public static string Name(Race race, int index) => Names[((int)race * 2 + index) % Names.Count];

    /// <summary>
    /// The chassis a bot of a tier takes. The line (which chassis of that tier) goes by its place in the team, so the two bots
    /// of a team differ where the tier has more than one, and the k-th of either team are alike: a mirror, not a lottery.
    /// The race does not change it, so that a team is not stronger for the slots it plays.
    /// </summary>
    public static string Pick(int tier, Race race, int index)
    {
        var line = BattleCatalogue.OfTier(Math.Clamp(tier, 1, BattleCatalogue.MostTier));
        return line.Count == 0 ? WiredBalance.StartChassis : line[Math.Max(0, index) % line.Count].Id;
    }

    /// <summary>A whole bot hero of a tier.</summary>
    public static BattleHero Bot(int tier, Race race, int index)
    {
        var chassis = Pick(tier, race, index);
        return new BattleHero(race, Name(race, index), chassis, Loadout(chassis, BattleCatalogue.Tier(chassis)), Bot: true);
    }

    /// <summary>
    /// A deterministic loadout for a chassis at a tier: in each weapon slot the best weapon by size and price that load and
    /// power still carry, at the tier's Mk; then modules in a fixed order (plates and frame first) and from tier III two
    /// abilities, as far as load and power allow. Nothing is overloaded and no power is short.
    /// </summary>
    public static IReadOnlyList<BattleItem> Loadout(string chassisId, int tier)
    {
        if (WiredCatalogue.FindChassis(chassisId) is not { } chassis)
            return [];
        if (tier <= BattleBalance.StartKitTier)
            return StartKit(chassis);
        var mk = BattleCatalogue.MostMk(tier);
        var items = new List<BattleItem>();
        int load = 0, capacity = chassis.Load, draw = 0, supply = chassis.Power;
        var free = chassis.Slots.Where(s => s.Fixed is null).ToList();
        // From tier III the weapons leave power for the thriftiest of the abilities the tier takes.
        var abilityPower = tier >= BattleCatalogue.AbilitiesFrom
            ? AbilityOrder(tier).Select(WiredCatalogue.FindAbility).OfType<WiredAbility>().Select(a => a.Power).DefaultIfEmpty(0).Min()
            : 0;
        // A weapon for each slot, leaving room for the others to have the lightest that fits them.
        for (var i = 0; i < free.Count; i++)
        {
            var slot = free[i];
            var reserve = free.Skip(i + 1).Sum(s => Lightest(chassis, s.Mounting)?.Load ?? 0);
            var reservePower = free.Skip(i + 1).Sum(s => Lightest(chassis, s.Mounting)?.Power ?? 0) + abilityPower;
            var weapon = WiredCatalogue.Weapons
                .Where(w => WiredCatalogue.WhyNotFit(chassis, slot.Mounting, w) is null
                    && load + w.Load + reserve <= capacity && draw + w.Power + reservePower <= supply)
                .OrderByDescending(w => w.Size).ThenByDescending(w => w.Price).FirstOrDefault()
                ?? Lightest(chassis, slot.Mounting);
            if (weapon is null)
                continue;
            load += weapon.Load;
            draw += weapon.Power;
            items.Add(new BattleItem(weapon.Id, WiredSlot.Weapon(slot.Mounting), mk));
        }
        var modules = 0;
        var abilities = 0;
        bool Takes(int addLoad, int addDraw) => load + addLoad <= capacity && draw + addDraw <= supply;
        void AddModule(WiredModule module)
        {
            var level = Math.Min(mk, module.Prices.Count);
            load += module.Load;
            draw += module.Power;
            capacity += module.Effect.Capacity * level;
            supply += module.Effect.Power * level;
            items.Add(new BattleItem(module.Id, WiredSlot.Module(modules++), level));
        }
        // Plates and frame first, then the abilities, then the rest of the modules.
        foreach (var id in ModuleOrder.Take(2))
        {
            if (modules < chassis.Modules && WiredCatalogue.FindModule(id) is { } module && Takes(module.Load, module.Power))
                AddModule(module);
        }
        if (tier >= BattleCatalogue.AbilitiesFrom)
        {
            foreach (var id in AbilityOrder(tier))
            {
                if (abilities < WiredBalance.AbilitySlots && WiredCatalogue.FindAbility(id) is { } ability && Takes(0, ability.Power))
                {
                    draw += ability.Power;
                    items.Add(new BattleItem(ability.Id, WiredSlot.Ability(abilities++)));
                }
            }
        }
        foreach (var id in ModuleOrder.Skip(2))
        {
            if (modules >= chassis.Modules)
                break;
            if (id == "thrusters" && tier < BattleCatalogue.AbilitiesFrom || WiredCatalogue.FindModule(id) is not { } module)
                continue;
            // A reactor pays for itself in power only where power is short.
            if (Takes(module.Load, module.Power))
                AddModule(module);
        }
        return items;
    }

    /// <inheritdoc cref="Loadout(string, int)"/>
    public static IReadOnlyList<BattleItem> Loadout(GameData data, string chassisId, int tier) => Loadout(chassisId, tier);

    /// <summary>
    /// What a new account fights with, and so a bot of the first tier: the start weapon in each slot it fits (else the
    /// lightest that does) and no modules, so that a player's first battles are not against better-fitted machines.
    /// </summary>
    private static IReadOnlyList<BattleItem> StartKit(WiredChassis chassis)
    {
        var items = new List<BattleItem>();
        foreach (var slot in chassis.Slots.Where(s => s.Fixed is null))
        {
            var weapon = WiredCatalogue.FindWeapon(WiredBalance.StartWeapon) is { } start && WiredCatalogue.WhyNotFit(chassis, slot.Mounting, start) is null
                ? start
                : Lightest(chassis, slot.Mounting);
            if (weapon is not null)
                items.Add(new BattleItem(weapon.Id, WiredSlot.Weapon(slot.Mounting)));
        }
        return items;
    }

    /// <summary>The lightest weapon that fits a mounting of the chassis, or null.</summary>
    private static WiredWeapon? Lightest(WiredChassis chassis, Mounting mounting) => WiredCatalogue.Weapons
        .Where(w => WiredCatalogue.WhyNotFit(chassis, mounting, w) is null).OrderBy(w => w.Load).ThenBy(w => w.Power).FirstOrDefault();

    /// <returns>null if the hero may play in the bracket whose top is <paramref name="tier"/>, otherwise why not.</returns>
    public static string? WhyNot(BattleHero hero, int tier)
    {
        if (WiredCatalogue.FindChassis(hero.Chassis) is not { } chassis)
            return "no such chassis";
        if (!BattleCatalogue.InBracket(hero.Chassis, tier))
            return "that chassis is not in the bracket";
        // A hand-made hero may leave its items out, or bring more than a chassis can wear: what has no slot stays in the
        // inventory, so without this nothing would bound it.
        if (hero.Items is null || hero.Items.Any(i => i?.Catalogue is null))
            return "no such item";
        if (hero.Items.Count > chassis.Slots.Count + chassis.Modules + WiredBalance.AbilitySlots)
            return "more items than the chassis wears";
        var own = BattleCatalogue.Tier(hero.Chassis);
        var loadout = HeroLoadout.Bare(new WiredItem(0, WiredKind.Chassis, chassis.Id));
        var next = 1;
        foreach (var entry in hero.Items)
        {
            var kind = WiredCatalogue.KindOf(entry.Catalogue);
            if (kind is not (WiredKind.Weapon or WiredKind.Module or WiredKind.Ability))
                return $"no such item {entry.Catalogue}";
            if (BattleCatalogue.NotOffered.Contains(entry.Catalogue))
                return $"{entry.Catalogue} is not offered in a battle";
            if (kind == WiredKind.Ability && own < BattleCatalogue.AbilitiesFrom)
                return "abilities go on chassis from tier III";
            var most = kind switch
            {
                WiredKind.Module => Math.Min(BattleCatalogue.MostMk(own), WiredCatalogue.FindModule(entry.Catalogue)!.Prices.Count),
                WiredKind.Weapon => BattleCatalogue.MostMk(own),
                _ => 1,
            };
            if (entry.Mk < 1 || entry.Mk > most)
                return $"{entry.Catalogue} cannot be Mk {entry.Mk} on this tier";
            if (entry.Slot is not { } slot)
                continue;
            var item = new WiredItem(next++, kind.Value, entry.Catalogue, entry.Mk);
            if (loadout.At(slot) is not null)
                return "two items in one slot";
            if (loadout.WhyNotFit(item, slot) is { } why)
                return why;
            loadout = loadout.With(slot, item);
        }
        return LoadShare(loadout) > WiredBalance.MostLoad ? "too heavy for this chassis" : null;
    }

    /// <summary>Load against capacity, as <see cref="HeroBuild"/> works it out, without the game data.</summary>
    private static float LoadShare(HeroLoadout loadout)
    {
        var chassis = loadout.ChassisEntry;
        int load = 0, capacity = chassis.Load;
        foreach (var item in loadout.Modules.OfType<WiredItem>())
        {
            if (WiredCatalogue.FindModule(item.Catalogue) is { } module)
                (load, capacity) = (load + module.Load, capacity + module.Effect.Capacity * item.Mk);
        }
        foreach (var item in loadout.Weapons.OfType<WiredItem>())
            load += WiredCatalogue.FindWeapon(item.Catalogue)?.Load ?? 0;
        return capacity <= 0 ? 0 : load / (float)capacity;
    }

    /// <summary>
    /// The hero as it plays: itself if it may, else clamped to a bot's loadout at the bottom of the bracket, on its own line
    /// (the same machine at that tier) where there is one. Its race, name and whether it is a bot stay.
    /// </summary>
    public static BattleHero Resolved(BattleHero hero, int tier, int index)
    {
        if (WhyNot(hero, tier) is null)
            return hero;
        var bottom = BattleCatalogue.BracketBottom(tier);
        var line = WiredCatalogue.FindChassis(hero.Chassis) is { } own
            ? BattleCatalogue.OfTier(bottom).FirstOrDefault(c => c.Type == own.Type && c.SubType == own.SubType)?.Id
            : null;
        var chassis = line ?? Pick(bottom, hero.Race, index);
        return hero with { Chassis = chassis, Items = Loadout(chassis, BattleCatalogue.Tier(chassis)) };
    }

    /// <summary>
    /// The options with a hero for every race of the two teams: the given ones (players' heroes), and bots for the rest. The
    /// k-th hero of a team, by race order, mirrors the tier of the k-th of the other team; a bot whose counterpart is a bot
    /// too takes the bracket's top tier.
    /// </summary>
    public static BattleOptions Compose(BattleOptions options, IReadOnlyList<BattleHero> given)
    {
        var teams = new[] { options.TeamA.Order().ToList(), options.TeamB.Order().ToList() };
        var heroes = new List<BattleHero>();
        for (var team = 0; team < 2; team++)
        {
            for (var k = 0; k < teams[team].Count; k++)
            {
                var race = teams[team][k];
                if (given.FirstOrDefault(h => h.Race == race) is { } own)
                {
                    heroes.Add(own);
                    continue;
                }
                var other = teams[1 - team];
                var mirror = k < other.Count ? given.FirstOrDefault(h => h.Race == other[k]) : null;
                var tier = mirror is not null && BattleCatalogue.InBracket(mirror.Chassis, options.Tier)
                    ? BattleCatalogue.Tier(mirror.Chassis)
                    : options.Tier;
                heroes.Add(Bot(tier, race, k));
            }
        }
        return options with { Heroes = heroes.OrderBy(h => h.Race).ToList() };
    }
}
