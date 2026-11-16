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
    /// The chassis a bot of a tier takes on a map. The line (which chassis of that tier) goes by its place in the team, so the
    /// two bots of a team differ where the tier has more than one, and the k-th of either team are alike: a mirror, not a
    /// lottery. Where the line starts goes by the map and the tier (<see cref="LineStart"/>), so that every chassis of a tier
    /// comes up somewhere, not only the first two of the catalogue. Neither the race nor anything else changes it, so that a
    /// team is not stronger for the slots it plays, and the relay and every client pick alike. The constructors are left
    /// out: without towers in a battle they are one-slot chassis no bot would choose.
    /// </summary>
    public static string Pick(string scenario, int tier, int index)
    {
        var clamped = Math.Clamp(tier, 1, BattleCatalogue.MostTier);
        var line = BattleCatalogue.OfTier(clamped).Where(c => (c.Special & ChassisSpecial.Towers) == 0).ToList();
        return line.Count == 0 ? WiredBalance.StartChassis : line[(LineStart(scenario, clamped, line.Count) + Math.Max(0, index)) % line.Count].Id;
    }

    /// <summary>
    /// Where a tier's line of bots starts on a map: FNV-1a over the scenario's UTF-8 bytes and then the tier's four bytes,
    /// its bits stirred as MurmurHash3 finishes a hash (FNV-1a alone leaves the low bits alike for names that differ in a
    /// few letters, and so one start for every map), taken modulo the line's length. Never <see cref="string.GetHashCode()"/>,
    /// which differs from one run to the next.
    /// </summary>
    private static int LineStart(string scenario, int tier, int count)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(scenario))
            hash = unchecked((hash ^ b) * 16777619u);
        for (var shift = 0; shift < 32; shift += 8)
            hash = unchecked((hash ^ (byte)(tier >> shift)) * 16777619u);
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x85EBCA6Bu);
        hash ^= hash >> 13;
        hash = unchecked(hash * 0xC2B2AE35u);
        hash ^= hash >> 16;
        return (int)(hash % (uint)count);
    }

    /// <summary>A whole bot hero of a tier on a map.</summary>
    public static BattleHero Bot(string scenario, int tier, Race race, int index)
    {
        var chassis = Pick(scenario, tier, index);
        return new BattleHero(race, Name(race, index), chassis, Loadout(chassis, BattleCatalogue.Tier(chassis)), Bot: true);
    }

    /// <summary>
    /// A deterministic loadout for a chassis at a tier: in each weapon slot the best weapon by size and price that load and
    /// power still carry, at the tier's Mk, and then copies of those weapons as batteries (<see cref="Batteries"/>); then
    /// modules in a fixed order (plates and frame first, never thrusters on a chassis with a dash of its own) and from
    /// tier III two abilities, as far as load and power allow. Nothing is overloaded and no power is short.
    /// </summary>
    public static IReadOnlyList<BattleItem> Loadout(string chassisId, int tier) =>
        WiredCatalogue.FindChassis(chassisId) is { } chassis ? Loadout(chassis, tier) : [];

    /// <inheritdoc cref="Loadout(string, int)"/>
    public static IReadOnlyList<BattleItem> Loadout(WiredChassis chassis, int tier)
    {
        if (tier <= BattleBalance.StartKitTier)
            return StartKit(chassis);
        var mk = BattleCatalogue.MostMk(tier);
        var items = new List<BattleItem>();
        int load = 0, capacity = chassis.Load, draw = 0, supply = chassis.Power;
        var free = chassis.Slots.Where(s => s.Fixed is null).ToList();
        var abilityPower = AbilityReserve(tier);
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
        (load, draw) = Batteries(chassis, items, load, draw, capacity, supply - abilityPower);
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
            // Thrusters come with the abilities' tier, and never onto a chassis with a dash of its own (Old Iron, the
            // Hornet), where they would add little.
            if (id == "thrusters" && (tier < BattleCatalogue.AbilitiesFrom || chassis.Dash is not null) || WiredCatalogue.FindModule(id) is not { } module)
                continue;
            // A reactor pays for itself in power only where power is short.
            if (Takes(module.Load, module.Power))
                AddModule(module);
        }
        return items;
    }

    /// <inheritdoc cref="Loadout(string, int)"/>
    public static IReadOnlyList<BattleItem> Loadout(GameData data, string chassisId, int tier) => Loadout(chassisId, tier);

    /// <summary>The power a bot's weapons leave from tier III for the thriftiest of the abilities the tier takes; none below.</summary>
    public static int AbilityReserve(int tier) => tier >= BattleCatalogue.AbilitiesFrom
        ? AbilityOrder(tier).Select(WiredCatalogue.FindAbility).OfType<WiredAbility>().Select(a => a.Power).DefaultIfEmpty(0).Min()
        : 0;

    /// <summary>
    /// The single weapons in <paramref name="items"/> grown into batteries: round the slots again and again, the heaviest
    /// slot first (ties left, right, top), each slot that has room for one more copy of its own weapon takes one, as long as
    /// the weapon forms batteries and the chassis' own load and power, less what is kept for an ability, still carry it.
    /// A battery is one entry with a count, every copy at the entry's Mk.
    /// </summary>
    /// <returns>The load and power drawn with the copies.</returns>
    private static (int Load, int Draw) Batteries(WiredChassis chassis, List<BattleItem> items, int load, int draw, int capacity, int supply)
    {
        var order = items.Select((item, at) => (Item: item, At: at))
            .Where(e => e.Item.Slot is { Kind: WiredSlotKind.Weapon } && WiredCatalogue.FindWeapon(e.Item.Catalogue) is { Single: false })
            .OrderByDescending(e => chassis.Slot((Mounting)e.Item.Slot!.Index)!.Size).ThenBy(e => e.Item.Slot!.Index)
            .Select(e => e.At).ToList();
        for (var added = true; added;)
        {
            added = false;
            foreach (var at in order)
            {
                var entry = items[at];
                var weapon = WiredCatalogue.FindWeapon(entry.Catalogue)!;
                if (entry.Count >= WiredCatalogue.MostCopies(chassis, (Mounting)entry.Slot!.Index)
                    || load + weapon.Load > capacity || draw + weapon.Power > supply)
                    continue;
                items[at] = entry with { Count = entry.Count + 1 };
                load += weapon.Load;
                draw += weapon.Power;
                added = true;
            }
        }
        return (load, draw);
    }

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
            // A battery is one entry with a count, all of it at the entry's Mk: owning a weapon fills it (no credits per copy).
            if (entry.Count < 1)
                return $"{entry.Catalogue} cannot come {entry.Count} times";
            if (entry.Count > 1 && (kind != WiredKind.Weapon || entry.Slot is not { Kind: WiredSlotKind.Weapon }))
                return "a battery needs a weapon slot";
            if (entry.Slot is not { } slot)
                continue;
            var item = new WiredItem(next++, kind.Value, entry.Catalogue, entry.Mk);
            if (loadout.At(slot) is not null)
                return "two items in one slot";
            if (loadout.WhyNotFit(item, slot) is { } why)
                return why;
            if (entry.Count > 1 && WiredCatalogue.WhyNotCopies(chassis, (Mounting)slot.Index, WiredCatalogue.FindWeapon(entry.Catalogue)!, entry.Count) is { } many)
                return many;
            loadout = loadout.With(slot, item);
            for (var copy = 1; copy < entry.Count; copy++)
                loadout = loadout.Adding(slot, new WiredItem(next++, kind.Value, entry.Catalogue, entry.Mk));
        }
        return LoadShare(loadout) > WiredBalance.MostLoad ? "too heavy for this chassis" : null;
    }

    /// <summary>Load against capacity, as <see cref="HeroBuild"/> works it out, without the game data: each copy of a battery weighs as a weapon.</summary>
    private static float LoadShare(HeroLoadout loadout)
    {
        var chassis = loadout.ChassisEntry;
        int load = 0, capacity = chassis.Load;
        foreach (var item in loadout.Modules.OfType<WiredItem>())
        {
            if (WiredCatalogue.FindModule(item.Catalogue) is { } module)
                (load, capacity) = (load + module.Load, capacity + module.Effect.Capacity * item.Mk);
        }
        foreach (var mounting in Enum.GetValues<Mounting>())
        {
            foreach (var item in loadout.Battery(mounting))
                load += WiredCatalogue.FindWeapon(item.Catalogue)?.Load ?? 0;
        }
        return capacity <= 0 ? 0 : load / (float)capacity;
    }

    /// <summary>
    /// The hero as it plays on a map: itself if it may, else clamped to a bot's loadout at the bottom of the bracket, on its
    /// own line (the same machine at that tier) where there is one, else on the chassis a bot of its place would take. Its
    /// race, name and whether it is a bot stay.
    /// </summary>
    public static BattleHero Resolved(BattleHero hero, int tier, int index, string scenario)
    {
        if (WhyNot(hero, tier) is null)
            return hero;
        var bottom = BattleCatalogue.BracketBottom(tier);
        var line = WiredCatalogue.FindChassis(hero.Chassis) is { } own
            ? BattleCatalogue.OfTier(bottom).FirstOrDefault(c => c.Type == own.Type && c.SubType == own.SubType)?.Id
            : null;
        var chassis = line ?? Pick(scenario, bottom, index);
        return hero with { Chassis = chassis, Items = Loadout(chassis, BattleCatalogue.Tier(chassis)) };
    }

    /// <summary>
    /// The options with a hero for every race of the two teams: the given ones (players' heroes), and bots for the rest,
    /// each picked for the map's <paramref name="scenario"/> (<see cref="Pick"/>). The k-th hero of a team, by race order,
    /// mirrors the tier of the k-th of the other team; a bot whose counterpart is a bot too takes the bracket's top tier.
    /// The relay, the pod and the sim's own fallback compose alike for the same map; options made for no map in particular
    /// (an empty name) start the lines where that name puts them.
    /// </summary>
    public static BattleOptions Compose(BattleOptions options, IReadOnlyList<BattleHero> given, string scenario = "")
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
                heroes.Add(Bot(scenario, tier, race, k));
            }
        }
        return options with { Heroes = heroes.OrderBy(h => h.Race).ToList() };
    }
}
