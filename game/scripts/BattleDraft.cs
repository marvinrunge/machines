using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>Where a chassis stands for the player, on the pod's tree.</summary>
public enum BattleStage
{
    /// <summary>The hero is on it.</summary>
    Worn,

    /// <summary>Bought: the hero can change to it.</summary>
    Owned,

    /// <summary>Researched, and the credits cover it.</summary>
    ForSale,

    /// <summary>Researched, and the credits do not cover it yet.</summary>
    TooDear,

    /// <summary>A parent is researched and the XP covers the research.</summary>
    Researchable,

    /// <summary>A parent is researched, and the XP does not cover the research yet.</summary>
    Short,

    /// <summary>No parent is researched yet.</summary>
    Closed,
}

/// <summary>A chassis on the pod's tree: its tier, where it stands, what the next step costs (XP to research, credits to buy) and why it cannot be taken.</summary>
public sealed record BattleNode(string Chassis, int Tier, BattleStage Stage, int Cost, BattleWhy? Lock);

/// <summary>
/// An item on a shelf of the pod: <paramref name="Mk"/> is the Mk it goes on the hero at (0 when it is not owned),
/// <paramref name="Price"/> what buying it costs where it is not owned, <paramref name="Upgrade"/> what its next Mk costs
/// where that may be had for the chassis worn, and <paramref name="Lock"/> why it cannot go on this hero at all.
/// </summary>
public sealed record BattleOffer(string Item, WiredKind Kind, int Mk, int? Price, int? Upgrade, BattleWhy? Lock, BattleWhy? Note = null);

/// <summary>
/// The hero being put together in the pod, before a battle: the chassis worn and what it wears, out of what the account owns,
/// and the battle chosen (map, lives, victory). Each change makes the hero anew with <see cref="WiredState.ForBattle"/>, as the
/// battle will, so the screen shows what will fight. Owned items are the account's, not the hero's: one owned may go on any
/// number of slots and on every chassis that may take it, at the highest Mk owned that the chassis' tier allows, and fills a
/// battery for nothing (Q8): put onto a slot that carries the same weapon, it joins the battery there as one copy more. Free
/// of Godot, so the tests can compile it. Battle is the remake's own addition.
/// </summary>
public sealed class BattleDraft
{
    /// <summary>A draft has no race yet: this one stands in, the colours are <see cref="Colour"/>'s.</summary>
    private const Race Maker = Race.Red;

    private static readonly string[] Numerals = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X"];

    private readonly GameData _data;
    private readonly IBattleAccount _account;
    private List<BattleItem> _items = [];
    private List<WiredItem> _made = [];
    private WiredState _state = null!;
    private string _map = BattleCatalogue.Maps[0].Scenario;
    private int _lives = 1;

    /// <summary>Takes up the last hero as far as the account still owns it, or starts on the first grunt with its bolter.</summary>
    public BattleDraft(GameData data, IBattleAccount account)
    {
        (_data, _account) = (data, account);
        if (account.LastHero is { } last)
        {
            Name = ShortName(last.Name);
            Colour = last.Colour;
            Map = last.Map;
            Lives = last.Lives;
            Win = last.Win;
            if (account.Owned(last.Chassis) && WiredCatalogue.FindChassis(last.Chassis) is not null)
            {
                Chassis = last.Chassis;
                _items = last.Items.ToList();
            }
        }
        // A start of the tree with nothing on gets the start bolter, onto the first weapon slot it has.
        if (_items.Count == 0 && WiredCatalogue.Roots.Any(root => root.Id == Chassis))
            _items = [new BattleItem(WiredBalance.StartWeapon, FirstWeaponSlot(Chassis), 1)];
        Settle();
    }

    /// <summary>A name as a hero carries it: trimmed, and at most <see cref="BattleHero.MostName"/> characters, no pair of a character halved.</summary>
    public static string ShortName(string name)
    {
        name = name.Trim();
        if (name.Length <= BattleHero.MostName)
            return name;
        var cut = BattleHero.MostName;
        if (char.IsHighSurrogate(name[cut - 1]))
            cut--;
        return name[..cut].TrimEnd();
    }

    /// <summary>A tier as the screens write it: I to VIII.</summary>
    public static string Roman(int tier) => tier >= 1 && tier < Numerals.Length ? Numerals[tier] : tier.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Name { get; set; } = "";

    public Race Colour { get; set; } = Race.Red;

    /// <summary>The map chosen, by its scenario; one of <see cref="BattleCatalogue.Maps"/>.</summary>
    public string Map
    {
        get => _map;
        set => _map = BattleCatalogue.Map(value) is { } map ? map.Scenario : _map;
    }

    /// <summary>Bodies per hero, 1 to 5.</summary>
    public int Lives
    {
        get => _lives;
        set => _lives = Math.Clamp(value, 1, 5);
    }

    public BattleWin Win { get; set; } = BattleWin.Both;

    public string Chassis { get; private set; } = WiredBalance.StartChassis;

    /// <summary>The worn chassis' tier: the top of the bracket the hero fights in.</summary>
    public int Tier => BattleCatalogue.Tier(Chassis);

    public IBattleAccount Account => _account;

    /// <summary>The hero as it comes out of the choices so far.</summary>
    public WiredState State => _state;

    public HeroLoadout Loadout => _state.Loadout;

    /// <summary>What the hero wears, each at the Mk it goes on at, a battery as one entry with its count.</summary>
    public IReadOnlyList<BattleItem> Items => _items;

    /// <summary>The hero as a battle takes it, playing <paramref name="race"/>.</summary>
    public BattleHero Hero(Race race) => new(race, ShortName(Name), Chassis, _items.ToList());

    /// <summary>What the account keeps of this hero and the battle chosen.</summary>
    public BattleHeroSheet Sheet => new()
    {
        Name = ShortName(Name),
        Colour = Colour,
        Chassis = Chassis,
        Items = _items.ToList(),
        Map = Map,
        Lives = Lives,
        Win = Win,
    };

    /// <summary>Remembers the hero and the battle chosen in the account, and keeps the account.</summary>
    public bool Keep()
    {
        _account.LastHero = Sheet;
        return _account.Save();
    }

    // ---- what goes on the hero ----------------------------------------------------------------------

    /// <summary>The highest Mk an item may have on a chassis of a tier; 0 where it may not go at all.</summary>
    public static int MostMkOn(string item, int tier)
    {
        if (BattleCatalogue.NotOffered.Contains(item))
            return 0;
        if (WiredCatalogue.FindAbility(item) is not null)
            return tier >= BattleCatalogue.AbilitiesFrom ? 1 : 0;
        if (WiredCatalogue.FindModule(item) is { } module)
            return Math.Min(BattleCatalogue.MostMk(tier), module.Prices.Count);
        return WiredCatalogue.FindWeapon(item) is not null ? BattleCatalogue.MostMk(tier) : 0;
    }

    /// <summary>The Mk an item goes on this hero at: the highest owned that the worn chassis allows; 0 for none.</summary>
    public int MkFor(string item) => Math.Min(_account.Mk(item), MostMkOn(item, Tier));

    /// <summary>Why an item may not go on a chassis of this tier at all, or null.</summary>
    public BattleWhy? WhyNotOnTier(string item)
    {
        if (BattleCatalogue.NotOffered.Contains(item))
            return new BattleWhy("that is not offered in a battle");
        return MostMkOn(item, Tier) == 0 ? new BattleWhy("From tier {0}", Roman(BattleRules.TierFor(item))) : null;
    }

    /// <returns>
    /// null if an owned item can go into the slot, otherwise why not; with <paramref name="buying"/> whether it could once
    /// bought, at Mk I. Onto a slot that carries the same weapon it is asked as one copy more for the battery there, unless
    /// it is to <paramref name="replace"/> it.
    /// </returns>
    public BattleWhy? WhyNotPut(string item, WiredSlot slot, bool buying = false, bool replace = false)
    {
        if (WhyNotOnTier(item) is { } tier)
            return tier;
        if (!buying && !_account.Owned(item))
            return new BattleWhy("you do not own that");
        var made = new WiredItem(-1, WiredCatalogue.KindOf(item)!.Value, item, Math.Max(1, MkFor(item)));
        return Fits(made, slot, _state.Loadout, replace);
    }

    /// <returns>null if what the hero wears, of a battery one copy, can move to the slot, otherwise why not.</returns>
    public BattleWhy? WhyNotMove(int made, WiredSlot slot, bool replace = false) =>
        _made.Find(i => i.Id == made) is { } item ? Fits(item, slot, _state.Loadout, replace) : new BattleWhy("you do not own that");

    /// <summary>Whether the hero wears this one of its items (by its id in <see cref="State"/>).</summary>
    public bool Wears(int made) => _made.Any(i => i.Id == made);

    /// <summary>
    /// Puts an owned item into a slot. Onto a slot that carries the same weapon it joins the battery there for nothing;
    /// otherwise, or with <paramref name="replace"/>, what sat there comes off, a whole battery.
    /// </summary>
    /// <returns>null if done, otherwise why not.</returns>
    public BattleWhy? Put(string item, WiredSlot slot, bool replace = false)
    {
        if (WhyNotPut(item, slot, replace: replace) is { } why)
            return why;
        if (!replace && Battery(item, slot) is >= 0 and var battery)
            _items[battery] = _items[battery] with { Count = _items[battery].Count + 1 };
        else
        {
            Empty(slot);
            _items.Add(new BattleItem(item, slot, MkFor(item)));
        }
        Settle();
        return null;
    }

    /// <summary>
    /// Moves what the hero wears to another slot, of a battery one copy. Onto a slot that carries the same weapon it joins
    /// the battery there; otherwise, or with <paramref name="replace"/>, what sat there comes off.
    /// </summary>
    /// <returns>null if done, otherwise why not.</returns>
    public BattleWhy? Move(int made, WiredSlot slot, bool replace = false)
    {
        if (WhyNotMove(made, slot, replace) is { } why)
            return why;
        var at = EntryOf(made);
        var moving = _items[at];
        var items = _items.ToList();
        if (moving.Slot == slot)
        {
            // Onto its own battery: it stays as it is, or stands alone there.
            if (replace)
                items[at] = moving with { Count = 1 };
        }
        else if (!replace && Battery(moving.Catalogue, slot) is >= 0 and var battery)
        {
            items[battery] = items[battery] with { Count = items[battery].Count + 1 };
            Lessen(items, at);
        }
        else
        {
            var sitting = items.FindIndex(e => e.Slot == slot);
            if (moving.Count == 1)
                items[at] = moving with { Slot = slot };
            else
            {
                items[at] = moving with { Count = moving.Count - 1 };
                items.Add(moving with { Slot = slot, Count = 1 });
            }
            if (sitting >= 0)
                items.RemoveAt(sitting);
        }
        _items = items;
        Settle();
        return null;
    }

    /// <summary>Takes something off the hero, of a battery one copy; the account still owns it.</summary>
    public void TakeOff(int made)
    {
        var at = EntryOf(made);
        if (at < 0)
            return;
        Lessen(_items, at);
        Settle();
    }

    /// <returns>null if the hero can change to the chassis, otherwise why not.</returns>
    public BattleWhy? WhyNotWear(string chassis)
    {
        if (WiredCatalogue.FindChassis(chassis) is null)
            return new BattleWhy("no such chassis");
        if (!_account.Researched(chassis))
            return new BattleWhy("Research {0} first", (object)new[] { chassis });
        if (!_account.Owned(chassis))
            return new BattleWhy("Buy it first");
        return null;
    }

    /// <summary>
    /// Changes to another owned chassis: what is worn moves as it would in the hangar (<see cref="HeroLoadout.OnChassis"/>),
    /// each item at the Mk the new tier allows, a battery cut to what its new slot carries; what does not fit or may not go
    /// on that tier comes off.
    /// </summary>
    public BattleWhy? Wear(string chassis)
    {
        if (WhyNotWear(chassis) is { } why)
            return why;
        var moved = _state.Loadout.OnChassis(new WiredItem(-1, WiredKind.Chassis, chassis));
        var (items, made) = (new List<BattleItem>(), 0);
        foreach (var entry in _items)
        {
            var copies = _made.Skip(made).Take(entry.Count).ToList();
            made += entry.Count;
            if (copies.Count > 0 && moved.SlotOf(copies[0].Id) is { } slot)
                items.Add(entry with { Slot = slot, Count = copies.Count(copy => moved.Holds(copy.Id)) });
        }
        _items = items;
        Chassis = chassis;
        Settle();
        return null;
    }

    /// <summary>
    /// Whether an item can go into a slot of a loadout and the hero still carry it. Onto a slot that carries the same weapon
    /// it is asked as a further copy of the battery there, unless it is to <paramref name="replace"/> what sits there.
    /// </summary>
    private BattleWhy? Fits(WiredItem item, WiredSlot slot, HeroLoadout loadout, bool replace = false)
    {
        HeroLoadout then;
        if (!replace && Joins(loadout, item, slot))
        {
            if (loadout.WhyNotAdd(item, slot) is { } full)
                return new BattleWhy(full);
            then = loadout.Adding(slot, item);
        }
        else
        {
            if (loadout.WhyNotFit(item, slot) is { } misfit)
                return new BattleWhy(misfit);
            then = loadout.With(slot, item);
        }
        return HeroBuild.Resolve(_data, then).LoadShare > WiredBalance.MostLoad ? new BattleWhy("too heavy for this chassis") : null;
    }

    /// <summary>Whether an item put into a slot joins the battery there: a weapon onto a weapon slot that carries the same weapon, the item itself not counted.</summary>
    private static bool Joins(HeroLoadout loadout, WiredItem item, WiredSlot slot) =>
        item.Kind == WiredKind.Weapon && slot.Kind == WiredSlotKind.Weapon && loadout.Without(item.Id).At(slot)?.Catalogue == item.Catalogue;

    /// <summary>
    /// The entry of a slot's battery of a weapon; -1 where the slot carries another or none, and for anything that is no
    /// weapon or no weapon slot: a module or ability put onto its own slot stands there alone again, as <see cref="Joins"/> says.
    /// </summary>
    private int Battery(string catalogue, WiredSlot slot) =>
        slot.Kind == WiredSlotKind.Weapon && WiredCatalogue.KindOf(catalogue) == WiredKind.Weapon
            ? _items.FindIndex(e => e.Slot == slot && e.Catalogue == catalogue)
            : -1;

    /// <summary>The entry a worn item comes from: each entry made its copies one after another; -1 for none.</summary>
    private int EntryOf(int made)
    {
        var at = _made.FindIndex(i => i.Id == made);
        for (var entry = 0; at >= 0 && entry < _items.Count; entry++)
        {
            if (at < _items[entry].Count)
                return entry;
            at -= _items[entry].Count;
        }
        return -1;
    }

    /// <summary>One copy of an entry comes off: the entry goes with its last.</summary>
    private static void Lessen(List<BattleItem> items, int at)
    {
        if (items[at].Count > 1)
            items[at] = items[at] with { Count = items[at].Count - 1 };
        else
            items.RemoveAt(at);
    }

    /// <summary>What sits in a slot comes off, a whole battery.</summary>
    private void Empty(WiredSlot slot) => _items.RemoveAll(e => e.Slot == slot);

    /// <summary>The weapon slot a chassis' first weapon goes to.</summary>
    private static WiredSlot? FirstWeaponSlot(string chassis) => WiredCatalogue.FindChassis(chassis)?.Slots.Where(s => s.Fixed is null)
        .Select(s => WiredSlot.Weapon(s.Mounting)).FirstOrDefault();

    /// <summary>
    /// Makes the hero anew: only what is owned and may go on this tier, each at the Mk it goes on at, in a slot it fits and
    /// not one taken already, a battery's copies as far as the slot carries them (<see cref="WiredCatalogue.Copies"/>: only
    /// a weapon on a weapon slot has more than one, as <see cref="WiredState.ForBattle"/> makes them), and nothing beyond
    /// what the chassis carries. What falls out the list no longer holds.
    /// </summary>
    private void Settle()
    {
        var kept = new List<BattleItem>();
        var chassis = WiredCatalogue.FindChassis(Chassis)!;
        var loadout = HeroLoadout.Bare(new WiredItem(0, WiredKind.Chassis, Chassis));
        var next = -1;
        foreach (var entry in _items)
        {
            if (entry.Slot is not { } slot || WiredCatalogue.KindOf(entry.Catalogue) is not (WiredKind.Weapon or WiredKind.Module or WiredKind.Ability) || MkFor(entry.Catalogue) < 1)
                continue;
            var (kind, mk) = (WiredCatalogue.KindOf(entry.Catalogue)!.Value, MkFor(entry.Catalogue));
            var item = new WiredItem(next--, kind, entry.Catalogue, mk);
            if (loadout.At(slot) is not null || Fits(item, slot, loadout) is not null)
                continue;
            loadout = loadout.With(slot, item);
            var most = WiredCatalogue.Copies(chassis, slot, entry.Catalogue, entry.Count);
            var count = 1;
            for (; count < most; count++)
            {
                var copy = new WiredItem(next--, kind, entry.Catalogue, mk);
                if (Fits(copy, slot, loadout) is not null)
                    break;
                loadout = loadout.Adding(slot, copy);
            }
            kept.Add(entry with { Mk = mk, Count = count });
        }
        _state = WiredState.ForBattle(_data, Maker, new BattleHero(Maker, Name, Chassis, kept), 1);
        _items = kept;
        _made = _state.Items.Where(i => i.Kind != WiredKind.Chassis).ToList();
    }

    // ---- the shelves and the tree -------------------------------------------------------------------

    /// <summary>Every chassis of the catalogue as it stands on the tree.</summary>
    public IEnumerable<BattleNode> Tree() => WiredCatalogue.Chassis.Select(c => Node(c.Id));

    public BattleNode Node(string chassis)
    {
        var tier = BattleCatalogue.Tier(chassis);
        if (chassis == Chassis)
            return new BattleNode(chassis, tier, BattleStage.Worn, 0, null);
        if (_account.Owned(chassis))
            return new BattleNode(chassis, tier, BattleStage.Owned, 0, null);
        if (_account.Researched(chassis))
        {
            var price = BattleRules.ChassisPrice(chassis);
            return new BattleNode(chassis, tier, _account.Credits >= price ? BattleStage.ForSale : BattleStage.TooDear, price,
                _account.Credits >= price ? null : new BattleWhy("Needs {0} Cr", price));
        }
        var cost = BattleRules.ResearchCost(chassis);
        return BattleRules.WhyNotResearch(_account, chassis) switch
        {
            null => new BattleNode(chassis, tier, BattleStage.Researchable, cost, null),
            { Words: "Research {0} first" } closed => new BattleNode(chassis, tier, BattleStage.Closed, cost, closed),
            var shortOf => new BattleNode(chassis, tier, BattleStage.Short, cost, shortOf),
        };
    }

    /// <summary>The XP there is towards a chassis' research, its parents' and the free XP.</summary>
    public int XpTowards(string chassis) => BattleRules.XpTowards(_account, chassis);

    /// <summary>The cheapest chassis that the worn one leads to and is not researched yet, or null when there is none left.</summary>
    public string? Next() => WiredCatalogue.Chassis.Where(c => c.Needs.Contains(Chassis) && !_account.Researched(c.Id))
        .OrderBy(c => c.Price).Select(c => c.Id).FirstOrDefault();

    /// <summary>Whether the worn chassis leads to any other at all; one that does not is the top of its line.</summary>
    public bool Leads => LeadsOn(Chassis);

    /// <summary>Whether a chassis leads to any other at all; one that does not is the top of its line.</summary>
    public static bool LeadsOn(string chassis) => WiredCatalogue.Chassis.Any(c => c.Needs.Contains(chassis));

    /// <summary>
    /// The items of a kind as the shelf shows them: owned first, then those on sale, then those this tier may not take, each in
    /// the catalogue's order. What a battle does not offer is not there.
    /// </summary>
    public IEnumerable<BattleOffer> Offers(WiredKind kind)
    {
        IEnumerable<string> ids = kind switch
        {
            WiredKind.Weapon => WiredCatalogue.Weapons.Select(w => w.Id),
            WiredKind.Module => WiredCatalogue.Modules.Select(m => m.Id),
            WiredKind.Ability => WiredCatalogue.Abilities.Select(a => a.Id),
            _ => [],
        };
        return ids.Where(id => !BattleCatalogue.NotOffered.Contains(id)).Select(id => Offer(id, kind))
            .OrderBy(o => o.Lock is not null ? 2 : o.Mk > 0 ? 0 : 1).ToList();
    }

    private BattleOffer Offer(string id, WiredKind kind)
    {
        var owned = _account.Mk(id);
        var lockOn = WhyNotOnTier(id);
        if (owned == 0)
            return new BattleOffer(id, kind, 0, BattleRules.Price(id, 1), null, lockOn);
        var next = owned + 1;
        if (BattleRules.Price(id, next) is not { } upgrade)
            return new BattleOffer(id, kind, MkFor(id), null, null, lockOn);
        // The next Mk is sold for the chassis that can take it: from its tier on.
        var note = MostMkOn(id, Tier) < next ? new BattleWhy("Mk {0} from tier {1}", next, Roman(BattleRules.TierFor(id, next))) : null;
        return new BattleOffer(id, kind, MkFor(id), null, note is null ? upgrade : null, lockOn, note);
    }

    /// <summary>Buys an item at a Mk, or a chassis; an item's Mk must be one the worn chassis can take.</summary>
    /// <returns>null if done, otherwise why not.</returns>
    public BattleWhy? Buy(string item, int mk = 1)
    {
        if (WiredCatalogue.FindChassis(item) is null)
        {
            if (WhyNotOnTier(item) is { } tier)
                return tier;
            if (MostMkOn(item, Tier) < mk)
                return new BattleWhy("Mk {0} from tier {1}", mk, Roman(BattleRules.TierFor(item, mk)));
        }
        if (_account.Buy(item, mk) is { } why)
            return why;
        Settle();
        return null;
    }

    /// <summary>Researches a chassis with the XP on its parents and the free XP.</summary>
    /// <returns>null if done, otherwise why not.</returns>
    public BattleWhy? Research(string chassis)
    {
        if (_account.Research(chassis) is { } why)
            return why;
        Settle();
        return null;
    }

    // ---- the battle ---------------------------------------------------------------------------------

    /// <returns>null if the hero may fight, otherwise why not, in the sim's words.</returns>
    public BattleWhy? WhyNotFight() => BattleBots.WhyNot(Hero(Maker), Tier) is { } why ? new BattleWhy(why) : null;

    /// <summary>The race the player plays on a map: the colour chosen where it is a slot of the teams, else the first team's anchor.</summary>
    public static Race PlayerRace(BattleMap map, Race colour) => map.TeamA.Contains(colour) || map.TeamB.Contains(colour) ? colour : map.TeamA[0];

    /// <summary>
    /// The battle these choices make against the computer: the map's teams, the bracket topped by the worn chassis' tier, and
    /// a hero for every race, the player's own (none in <paramref name="watch"/>, when a bot plays it too) and bots for the
    /// rest by the mirror rule (<see cref="BattleBots.Compose"/>). A hero with no name yet takes <paramref name="unnamed"/>
    /// (the player's), as a room's relay gives it one.
    /// </summary>
    public BattleOptions Options(bool watch = false, string? unnamed = null)
    {
        var map = BattleCatalogue.Map(Map)!;
        var hero = Hero(PlayerRace(map, Colour));
        if (hero.Name.Length == 0 && unnamed is not null)
            hero = hero with { Name = ShortName(unnamed) };
        return Compose(map, Tier, Lives, Win, watch ? null : hero);
    }

    /// <summary>A battle on a map at a tier, with <paramref name="player"/>'s hero if there is one and bots for every other race.</summary>
    public static BattleOptions Compose(BattleMap map, int tier, int lives, BattleWin win, BattleHero? player) =>
        BattleBots.Compose(new BattleOptions
        {
            Tier = Math.Clamp(tier, 1, BattleCatalogue.MostTier),
            Lives = Math.Clamp(lives, 1, 5),
            Win = win,
            TeamA = map.TeamA,
            TeamB = map.TeamB,
        }, player is null ? [] : [player]);
}
