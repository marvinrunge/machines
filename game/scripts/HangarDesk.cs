using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>What the hangar has on its left, a shelf at a time: the kinds of what is owned, then the shop's.</summary>
public enum HangarShelf
{
    Weapons,
    Modules,
    Abilities,
    Chips,
    Chassis,
    Tree,
    Offer,
    Upgrades,
    Squad,
    Pod,
}

/// <summary>What a card's strip does when it is clicked.</summary>
public enum HangarDeal
{
    None,
    Buy,
    Sell,
    Upgrade,

    /// <summary>Takes a chip offered after a boss.</summary>
    Choose,

    /// <summary>Researches a chassis in the pod of Battle, for XP.</summary>
    Research,
}

/// <summary>
/// Something the hangar shows in its inventory: owned, or on offer at <paramref name="Price"/>. <paramref name="Locked"/>
/// says why it cannot be had yet; it can still be looked at on the hero.
/// </summary>
public sealed record HangarGood(WiredItem Item, int? Price = null, string? Locked = null)
{
    /// <summary>What its strip offers, if it has one.</summary>
    public HangarDeal Deal { get; init; }

    /// <summary>What the deal comes to, where that is not its price: what it sells for, the next Mk's cost, the place of a chip among those offered.</summary>
    public int Amount { get; init; }

    /// <summary>Where a chassis stands on the tree.</summary>
    public ChassisStep? Step { get; init; }

    /// <summary>A run has only just opened it: it wears a gold plate until the hangar is left.</summary>
    public bool Fresh { get; init; }

    /// <summary>It is on its shelf for its Mk alone: from there it goes onto no slot.</summary>
    public bool Stays { get; init; }
}

/// <summary>How a good dropped onto a slot goes there (<see cref="HangarDesk.Joining"/>).</summary>
public enum SlotJoin
{
    /// <summary>It stands alone in the slot; what sat there, a whole battery, comes off.</summary>
    Put,

    /// <summary>It joins the battery of the same weapon there as its last copy.</summary>
    Add,

    /// <summary>The battery of the same weapon there is full: its lowest Mk comes off and the good takes its place.</summary>
    Swap,

    /// <summary>The battery of the same weapon there is full, or the weapon forms none, and nothing in it is below the good's Mk: refused.</summary>
    Full,
}

/// <summary>
/// What the hangar works on, so that one screen serves both: a hero being made before a run (<see cref="CreationDesk"/>)
/// and the hero of a run (<see cref="MatchDesk"/>). Whatever it tries out it works out on a copy of the loadout, the screen
/// asks for nothing else, and only <see cref="Put"/>, <see cref="TakeOff"/>, <see cref="Wear"/> and <see cref="BuyCopy"/>
/// change anything. A weapon dropped onto a slot that carries the same weapon joins the battery there while it has room;
/// <c>replace</c> takes the battery off instead, as a put did before batteries. Wired is the remake's own addition.
/// </summary>
public abstract class HangarDesk
{
    /// <summary>Why a spare that cost nothing brings nothing.</summary>
    protected const string Free = "that came free and sells for nothing";

    public abstract GameData Data { get; }

    public abstract ClassicAssets Assets { get; }

    /// <summary>The colours the hero wears.</summary>
    public abstract Race Look { get; }

    public abstract HeroLoadout Loadout { get; }

    /// <summary>The BMUs there are to spend.</summary>
    public abstract int Bank { get; }

    /// <summary>Whether a chassis has a slot here; Wired's heroes have every slot their chassis has.</summary>
    public virtual bool HasSlot(WiredSlot slot, WiredChassis chassis) => true;

    /// <summary>How the bank is shown.</summary>
    public virtual string BankText => $"{Bank} BMU";

    /// <summary>What the right mouse button does here.</summary>
    public virtual string Hint => "Right click takes off.";

    /// <summary>The inventory's title, untranslated.</summary>
    public virtual string Title => "Hangar";

    /// <summary>A sum of the bank's money as the cards write it.</summary>
    public virtual string PriceText(int amount) => $"{amount} BMU";

    /// <summary>The shelves of what is owned, where there is no shop: the shop's own are fixed.</summary>
    public virtual IReadOnlyList<HangarShelf> Shelves => [HangarShelf.Weapons, HangarShelf.Modules, HangarShelf.Abilities, HangarShelf.Chips, HangarShelf.Chassis];

    /// <summary>The shelf on show when the hangar comes up.</summary>
    public virtual HangarShelf FirstShelf => HangarShelf.Weapons;

    /// <summary>The word under a chassis on the tree, where the desk has its own: null keeps the step's.</summary>
    public virtual string? StepWord(HangarGood chassis) => null;

    /// <summary>A short mark in the corner of a chassis on the tree, or none.</summary>
    public virtual string? Badge(HangarGood chassis) => null;

    /// <summary>What a deal on a card comes to as its strip writes it: the bank's money, unless the desk deals in more than one.</summary>
    public virtual string DealText(HangarGood good, int amount) => PriceText(amount);

    /// <summary>What the banner says of a good the mouse is on (<paramref name="onTree"/>: a chassis on the tree), before what it does; null for nothing.</summary>
    public virtual string? Pointing(HangarGood good, bool onTree) => null;

    /// <summary>The shop's books, in a run; before one there is nothing to weigh up.</summary>
    public virtual HangarShop? Shop => null;

    /// <summary>Changes with anything the shelves show that the loadout does not: the screen fills them anew then.</summary>
    public virtual int Stamp => 0;

    /// <summary>What the inventory shows of one kind.</summary>
    public abstract IEnumerable<HangarGood> Goods(WiredKind kind);

    /// <summary>Every chassis in the order of the catalogue, each with its <see cref="HangarGood.Step"/>.</summary>
    public abstract IEnumerable<HangarGood> Tree();

    /// <summary>Whether the way on from a chassis is open.</summary>
    public abstract bool Reached(string chassis);

    /// <summary>The shop's offers, null where one has been bought.</summary>
    public virtual IEnumerable<HangarGood?> Offers() => [];

    /// <summary>What the hero owns that has or had a higher Mk.</summary>
    public virtual IEnumerable<HangarGood> Upgrades() => [];

    /// <summary>The chips offered after a boss, until one is taken.</summary>
    public virtual IReadOnlyList<HangarGood> ChipChoices => [];

    /// <summary>Does what a card's strip offers.</summary>
    /// <returns>null if done or on its way, otherwise why not.</returns>
    public virtual string? Deal(HangarGood good) => null;

    /// <returns>
    /// null if the good can go into the slot now as <see cref="Joining"/> says, otherwise why not, in the sim's words; with
    /// <paramref name="replace"/> whether it can take the place of what sits there.
    /// </returns>
    public abstract string? WhyNot(HangarGood good, WiredSlot slot, bool replace = false);

    /// <returns>null if the hero can change to the chassis now, otherwise why not.</returns>
    public abstract string? WhyNotWear(HangarGood chassis);

    /// <summary>
    /// Puts a good into a slot, buying it if it is on offer: as <see cref="Joining"/> says, into the battery of the same
    /// weapon there while it has room; with <paramref name="replace"/> what sat there comes off, a whole battery.
    /// </summary>
    /// <returns>null if done or on its way, otherwise why not.</returns>
    public abstract string? Put(HangarGood good, WiredSlot slot, bool replace = false);

    /// <summary>Takes an item off the hero, of a battery that copy; it stays owned.</summary>
    public abstract string? TakeOff(WiredItem item);

    /// <summary>Takes off what a slot carries, of a battery its last copy.</summary>
    /// <returns>null if done, on its way or nothing was there, otherwise why not.</returns>
    public string? TakeOff(WiredSlot slot) =>
        (slot.Kind == WiredSlotKind.Weapon ? Loadout.Battery((Mounting)slot.Index).LastOrDefault() : Loadout.At(slot)) is { } item ? TakeOff(item) : null;

    /// <summary>
    /// What one more copy of the weapon on a slot costs, for the card's "+1 barrel" (Q7): null where the slot carries no
    /// weapon, or its battery has no room for another by the slot's size and the weapon's kind.
    /// </summary>
    public abstract int? CopyPrice(WiredSlot slot);

    /// <returns>null if one more copy of the weapon on a slot can be had now (<see cref="BuyCopy"/>), otherwise why not.</returns>
    public abstract string? WhyNoCopy(WiredSlot slot);

    /// <summary>Gets one more copy of the weapon on a slot onto its battery, buying it where copies are bought.</summary>
    /// <returns>null if done or on its way, otherwise why not.</returns>
    public abstract string? BuyCopy(WiredSlot slot);

    /// <summary>Changes the chassis.</summary>
    public abstract string? Wear(HangarGood chassis);

    /// <summary>Whether a good is one of the hero's spares, which a right click gives back.</summary>
    public abstract bool IsSpare(HangarGood good);

    /// <summary>Gives a spare back for what it brings.</summary>
    /// <returns>null if done or on its way, otherwise why not.</returns>
    public abstract string? GiveBack(HangarGood good);

    /// <summary>
    /// How a good dropped onto a slot goes there, and with <see cref="SlotJoin.Swap"/> the copy it takes the place of: the
    /// lowest Mk of the battery, the last of equals. A weapon onto a weapon slot that carries the same weapon (the good
    /// itself not counted) joins the battery while it has room; anything else, or with <paramref name="replace"/>, is a put.
    /// </summary>
    public (SlotJoin Join, WiredItem? Lowest) Joining(HangarGood good, WiredSlot slot, bool replace = false)
    {
        var item = good.Item;
        if (replace || item.Kind != WiredKind.Weapon || slot.Kind != WiredSlotKind.Weapon || slot.Index is < 0 or > (int)Mounting.Top)
            return (SlotJoin.Put, null);
        var rest = Loadout.Without(item.Id);
        var battery = rest.Battery((Mounting)slot.Index);
        if (battery.Count == 0 || battery[0].Catalogue != item.Catalogue)
            return (SlotJoin.Put, null);
        if (rest.WhyNotAdd(item, slot) is null)
            return (SlotJoin.Add, null);
        var lowest = battery.Last(copy => copy.Mk == battery.Min(b => b.Mk));
        return lowest.Mk < item.Mk ? (SlotJoin.Swap, lowest) : (SlotJoin.Full, null);
    }

    /// <summary>
    /// The loadout with a good in a slot: what the hero would be. As <see cref="Joining"/> says, the good joins the battery
    /// there, takes the place of its lowest Mk, or stands alone; a good a full battery refuses leaves the hero as it is.
    /// </summary>
    public HeroLoadout Trying(HangarGood good, WiredSlot slot, bool replace = false) => Joining(good, slot, replace) switch
    {
        (SlotJoin.Add, _) => Loadout.Adding(slot, good.Item),
        (SlotJoin.Swap, { } lowest) => Loadout.Without(lowest.Id).Adding(slot, good.Item),
        (SlotJoin.Full, _) => Loadout,
        _ => Loadout.With(slot, good.Item),
    };

    /// <summary>The loadout on another chassis: what stays on and what falls off.</summary>
    public HeroLoadout Trying(HangarGood chassis) => Loadout.OnChassis(chassis.Item);

    public HeroBuild Build(HeroLoadout loadout) => HeroBuild.Resolve(Data, loadout);

    /// <summary>The loadout with a worn item one Mk up. A spare goes up where it lies: the hero stays as it is.</summary>
    public HeroLoadout Upgraded(WiredItem item) => Loadout.Holds(item.Id) ? Loadout.Replacing(item with { Mk = item.Mk + 1 }) : Loadout;

    /// <summary>
    /// The first slot a good fits: an empty one if there is one, and for a chip one that links it if there is one; else a
    /// battery of the same weapon with room; else the first that fits. Null for none, or when it goes into no slot.
    /// </summary>
    public WiredSlot? BestSlot(HangarGood good)
    {
        var fitting = Slots(Loadout, good.Item.Kind).Where(s => Loadout.WhyNotFit(good.Item, s) is null).ToList();
        var free = fitting.Where(s => Loadout.At(s) is null).ToList();
        if (good.Item.Kind == WiredKind.Chip && free.FirstOrDefault(s => WiredChips.Linked(Loadout.With(s, good.Item).Chips, s.Index)) is { } linked)
            return linked;
        return free.FirstOrDefault() ?? fitting.FirstOrDefault(s => Joining(good, s).Join == SlotJoin.Add) ?? fitting.FirstOrDefault();
    }

    /// <summary>The weapon on a slot, as a further copy of it would be asked about: null for a slot that carries none.</summary>
    protected string? CopyWeapon(WiredSlot slot) => slot.Kind == WiredSlotKind.Weapon ? Loadout.At(slot)?.Catalogue : null;

    /// <summary>One more copy of a weapon, before it is had.</summary>
    protected static WiredItem Copy(string weapon) => new(-1, WiredKind.Weapon, weapon);

    /// <summary>Whether the battery on a slot has room for one more copy, by the slot's size and the weapon's kind.</summary>
    protected bool HasRoom(WiredSlot slot) => CopyWeapon(slot) is { } weapon && Loadout.WhyNotAdd(Copy(weapon), slot) is null;

    /// <summary>Why there is no further copy to be had of what a slot carries: it carries no weapon.</summary>
    protected const string NoWeapon = "that is not on the hero";

    /// <summary>Why a good has no <see cref="BestSlot"/>.</summary>
    public string WhyNoSlot(HangarGood good) => Loadout.WhyNoSlot(good.Item) ?? "this chassis has no such slot";

    /// <summary>The slots a chassis offers for a kind of item, a fixed weapon's left out.</summary>
    private static IEnumerable<WiredSlot> Slots(HeroLoadout loadout, WiredKind kind) => kind switch
    {
        WiredKind.Weapon => loadout.ChassisEntry.Slots.Where(s => s.Fixed is null).Select(s => WiredSlot.Weapon(s.Mounting)),
        WiredKind.Module => Enumerable.Range(0, loadout.Modules.Count).Select(WiredSlot.Module),
        WiredKind.Ability => Enumerable.Range(0, loadout.Abilities.Count).Select(WiredSlot.Ability),
        WiredKind.Chip => Enumerable.Range(0, loadout.Chips.Count).Select(WiredSlot.Chip),
        _ => [],
    };
}

/// <summary>The hangar before a run: everything is a choice of <see cref="HeroDraft"/>, and nothing has happened yet.</summary>
public sealed class CreationDesk(GameData data, ClassicAssets assets, WiredProfile profile) : HangarDesk
{
    public HeroDraft Draft { get; } = new(data, profile);

    public WiredProfile Profile => profile;

    public override GameData Data => data;

    public override ClassicAssets Assets => assets;

    public override Race Look => Draft.Colour;

    public override HeroLoadout Loadout => Draft.State.Loadout;

    public override int Bank => Draft.Bank;

    public override string BankText => $"{Draft.Bank} / {Draft.Capital} BMU";

    public override string Hint => "Right click takes off, or gives a spare back.";

    public override IEnumerable<HangarGood> Goods(WiredKind kind)
    {
        if (kind == WiredKind.Chassis)
            return WiredCatalogue.Chassis.Where(c => c.Id != Draft.Chassis).Select(c => Chassis(c.Id))
                .OrderBy(g => g.Locked is null ? 0 : WiredProfile.StartChassis.Any(s => s.Chassis == g.Item.Catalogue) ? 1 : 2);
        if (kind == WiredKind.Chip)
            return WiredChips.All.Select(Chip).OrderBy(g => g.Locked == Lang.T("Won in a run") ? 0 : 1);
        var spares = Draft.State.Inventory.Where(i => i.Kind == kind).Select(i => new HangarGood(i));
        var sale = Draft.ForSale(kind).ToHashSet();
        var offers = kind switch
        {
            WiredKind.Weapon => WiredCatalogue.Weapons.Select(w => (w.Id, w.FromWave)),
            WiredKind.Module => WiredCatalogue.Modules.Select(m => (m.Id, m.FromWave)),
            _ => WiredCatalogue.Abilities.Select(a => (a.Id, a.FromWave)),
        };
        return spares.Concat(offers.OrderBy(o => o.FromWave).Select((offer, at) => new HangarGood(
            new WiredItem(-1 - at, kind, offer.Id), Draft.Price(offer.Id),
            sale.Contains(offer.Id) ? null : Lang.T("From wave {0} in the shop", offer.FromWave))));
    }

    public override IEnumerable<HangarGood> Tree() => WiredCatalogue.Chassis.Select(c => Chassis(c.Id) is var good && c.Id == Draft.Chassis
        ? good with { Step = ChassisStep.Worn, Amount = Draft.Price(c.Id) }
        : good with { Step = good.Locked is null ? ChassisStep.Owned : ChassisStep.Closed, Amount = Draft.Price(c.Id) });

    /// <summary>What runs opened since the hangar was last up, as ids of the profile.</summary>
    public IReadOnlySet<string> Fresh { get; init; } = new HashSet<string>();

    public override bool Reached(string chassis) => profile.CanStartOn(chassis);

    /// <summary>A chassis: open to start on, to be unlocked, or only to be reached in a run.</summary>
    private HangarGood Chassis(string id)
    {
        var start = WiredProfile.StartChassis.FirstOrDefault(s => s.Chassis == id);
        var needs = WiredCatalogue.FindChassis(id)?.Needs ?? [];
        var locked = start is null ? Lang.T("After {0}", string.Join(" / ", needs.Select(HangarText.Name))) :
            profile.CanStartOn(id) ? null :
            start.Squad > 0 ? Lang.T("Clear wave {0} with a squad of {1}", start.Wave, start.Squad) : Lang.T("Clear wave {0}", start.Wave);
        var at = WiredCatalogue.Chassis.ToList().FindIndex(c => c.Id == id);
        return new HangarGood(new WiredItem(-100 - at, WiredKind.Chassis, id), null, locked) { Fresh = Fresh.Contains(id) };
    }

    /// <summary>A chip is won in a run; the pool shows which can turn up.</summary>
    private HangarGood Chip(WiredChip chip, int at)
    {
        var milestone = WiredProfile.Milestones.FirstOrDefault(m => m.Chip == chip.Id);
        var open = chip.Base || profile.Unlocks.Contains(chip.Id);
        var locked = open ? Lang.T("Won in a run") : milestone is null ? Lang.T("Locked") : Lang.T(WiredProfile.Words(milestone.Measure, milestone.Amount), milestone.Amount);
        return new HangarGood(new WiredItem(-200 - at, WiredKind.Chip, chip.Id), null, locked) { Fresh = Fresh.Contains(chip.Id) };
    }

    public override string? WhyNot(HangarGood good, WiredSlot slot, bool replace = false)
    {
        if (good.Locked is { } locked)
            return locked;
        return good.Price is null ? Draft.WhyNotHold(good.Item, slot, replace) : Draft.WhyNotBuy(good.Item.Catalogue, slot, replace);
    }

    public override string? WhyNotWear(HangarGood chassis) => chassis.Locked;

    public override string? Put(HangarGood good, WiredSlot slot, bool replace = false) =>
        WhyNot(good, slot, replace) ?? (good.Price is null ? Draft.Put(good.Item.Id, slot, replace) : Draft.Buy(good.Item.Catalogue, slot, replace));

    /// <summary>Before a run a copy is bought out of the capital at its full price, as any.</summary>
    public override int? CopyPrice(WiredSlot slot) => HasRoom(slot) ? Draft.Price(CopyWeapon(slot)!) : null;

    public override string? WhyNoCopy(WiredSlot slot) => CopyWeapon(slot) is { } weapon ? Draft.WhyNotBuy(weapon, slot) : NoWeapon;

    public override string? BuyCopy(WiredSlot slot) => WhyNoCopy(slot) ?? Draft.Buy(CopyWeapon(slot)!, slot);

    public override string? TakeOff(WiredItem item)
    {
        Draft.TakeOff(item.Id);
        return null;
    }

    public override string? Wear(HangarGood chassis)
    {
        if (WhyNotWear(chassis) is { } why)
            return why;
        Draft.Wear(chassis.Item.Catalogue);
        return null;
    }

    public override bool IsSpare(HangarGood good) => Draft.State.Inventory.Any(i => i.Id == good.Item.Id);

    public override string? GiveBack(HangarGood good) => Draft.GiveBack(good.Item.Id) ? null : Free;
}

/// <summary>
/// The hangar of a run: the world goes on behind it and what the player does are the Wired commands, which come back in
/// the next ticks; the screen sees them land by <see cref="Loadout"/> and <see cref="Stamp"/> changing. What it says no to
/// beforehand it says in the sim's words, by the sim's own queries, so that the two cannot differ.
/// </summary>
public sealed class MatchDesk(Game game, ClassicAssets assets) : HangarDesk
{
    private HangarShop? _shop;

    private WiredState State => game.World.Wired!.Heroes[Game.Player];

    private GameWorld World => game.World;

    public override GameData Data => World.Data;

    public override ClassicAssets Assets => assets;

    public override Race Look => Game.Look(Game.Player);

    public override HeroLoadout Loadout => State.Loadout;

    public override int Bank => (int)World.Races[Game.Player].Bmu;

    public override string Hint => "Right click takes off, or sells a spare.";

    public override HangarShop Shop => _shop ??= new HangarShop(World, State);

    public override int Stamp
    {
        get
        {
            var hash = new HashCode();
            hash.Add(Bank);
            hash.Add(State.Rerolled);
            hash.Add(Shop.SquadSize);
            foreach (var item in State.Items)
                hash.Add(item);
            foreach (var offer in State.Offer)
                hash.Add(offer);
            foreach (var chip in State.ChipChoice)
                hash.Add(chip);
            return hash.ToHashCode();
        }
    }

    public override IEnumerable<HangarGood> Goods(WiredKind kind) => State.Inventory.Where(i => i.Kind == kind).Select(Spare);

    private HangarGood Spare(WiredItem item) =>
        new(item) { Deal = HangarDeal.Sell, Amount = State.SellValue(item) }; // for 0 BMU too: else a free spare blocks its place for good

    public override IEnumerable<HangarGood> Tree() => WiredCatalogue.Chassis.Select((chassis, at) =>
    {
        var step = Shop.Step(chassis);
        var owned = State.Items.Find(i => i.Catalogue == chassis.Id);
        var price = Shop.Price(chassis.Id);
        if (owned is not null)
            return (step == ChassisStep.Worn ? new HangarGood(owned) : Spare(owned)) with { Step = step };
        var item = new WiredItem(-100 - at, WiredKind.Chassis, chassis.Id);
        return step == ChassisStep.Closed
            ? new HangarGood(item, Locked: World.WhyCannotBuy(State, chassis.Id)) { Step = step, Amount = price }
            : new HangarGood(item, price, World.WhyNoRoom(State)) { Step = step, Deal = HangarDeal.Buy };
    });

    public override bool Reached(string chassis) => State.Reached.Contains(chassis);

    public override IEnumerable<HangarGood?> Offers() => State.Offer.Select((id, at) => id is null ? null
        : new HangarGood(new WiredItem(-1 - at, WiredCatalogue.KindOf(id)!.Value, id), Shop.Price(id), World.WhyNoRoom(State)) { Deal = HangarDeal.Buy });

    public override IEnumerable<HangarGood> Upgrades() => State.Items.Where(i => i.Kind is WiredKind.Weapon or WiredKind.Module)
        .OrderBy(i => Loadout.Holds(i.Id) ? 0 : 1)
        .Select(i => State.UpgradePrice(i) is { } price
            ? new HangarGood(i) { Deal = HangarDeal.Upgrade, Amount = price, Stays = true }
            : new HangarGood(i, Locked: "that has no higher Mk") { Stays = true });

    public override IReadOnlyList<HangarGood> ChipChoices => State.ChipChoice
        .Select((id, at) => new HangarGood(new WiredItem(-300 - at, WiredKind.Chip, id), Locked: World.WhyNoRoom(State)) { Deal = HangarDeal.Choose, Amount = at }).ToList();

    /// <remarks>
    /// A good that joins a battery is asked as the sim asks an add: bought, it need not be on offer (Q7). One that takes the
    /// place of a full battery's lowest Mk is asked as the two commands it goes as, the lowest coming off first.
    /// </remarks>
    public override string? WhyNot(HangarGood good, WiredSlot slot, bool replace = false)
    {
        if (Loadout.WhyNotFit(good.Item, slot) is { } misfit)
            return misfit;
        var (join, lowest) = Joining(good, slot, replace);
        if (join == SlotJoin.Full)
            return Loadout.WhyNotAdd(good.Item, slot);
        var copyOnto = join is SlotJoin.Add or SlotJoin.Swap ? slot : null;
        if (good.Price is not null && World.WhyCannotBuy(State, good.Item.Catalogue, copyOnto) is { } dear)
            return dear;
        if (lowest is not null && State.WhyNotWear(Data, Loadout.Without(lowest.Id)) is { } off)
            return off;
        return State.WhyNotWear(Data, Trying(good, slot, replace));
    }

    public override string? WhyNotWear(HangarGood chassis) => State.Item(chassis.Item.Id) is null ? "you own no such chassis" : State.WhyNotWear(Data, Trying(chassis));

    public override string? Put(HangarGood good, WiredSlot slot, bool replace = false)
    {
        if (WhyNot(good, slot, replace) is { } why)
            return why;
        var (join, lowest) = Joining(good, slot, replace);
        var add = join is SlotJoin.Add or SlotJoin.Swap;
        // A full battery makes room first: its lowest Mk comes off, in the same tick and before the add.
        if (lowest is not null)
            game.Issue(new WiredUnequipCommand(Game.Player, lowest.Id));
        // What is bought or chosen onto a slot is one command, so that it gets there even if the hangar closes right behind it.
        game.Issue(good switch
        {
            { Deal: HangarDeal.Choose } => new WiredChipChoiceCommand(Game.Player, good.Amount, slot.Index),
            { Price: not null } => new WiredBuyCommand(Game.Player, good.Item.Catalogue, slot, add),
            _ when slot.Kind == WiredSlotKind.Chip => new WiredChipCommand(Game.Player, good.Item.Id, slot.Index),
            _ => new WiredEquipCommand(Game.Player, good.Item.Id, slot, add),
        });
        return null;
    }

    /// <summary>A copy is bought at the hero's full price, whether or not the weapon is on offer (Q7).</summary>
    public override int? CopyPrice(WiredSlot slot) => HasRoom(slot) ? Shop.Price(CopyWeapon(slot)!) : null;

    public override string? WhyNoCopy(WiredSlot slot)
    {
        if (CopyWeapon(slot) is not { } weapon)
            return NoWeapon;
        var copy = Copy(weapon);
        return World.WhyCannotBuy(State, weapon, slot) ?? Loadout.WhyNotAdd(copy, slot) ?? State.WhyNotWear(Data, Loadout.Adding(slot, copy));
    }

    public override string? BuyCopy(WiredSlot slot)
    {
        if (WhyNoCopy(slot) is { } why)
            return why;
        game.Issue(new WiredBuyCommand(Game.Player, CopyWeapon(slot)!, slot, Add: true));
        return null;
    }

    public override string? TakeOff(WiredItem item)
    {
        game.Issue(Loadout.SlotOf(item.Id) is { Kind: WiredSlotKind.Chip } socket
            ? new WiredChipCommand(Game.Player, null, socket.Index)
            : new WiredUnequipCommand(Game.Player, item.Id));
        return null;
    }

    public override string? Wear(HangarGood chassis)
    {
        if (WhyNotWear(chassis) is { } why)
            return why;
        game.Issue(new WiredChassisCommand(Game.Player, chassis.Item.Id));
        return null;
    }

    public override bool IsSpare(HangarGood good) => State.Item(good.Item.Id) is not null && !Loadout.Holds(good.Item.Id);

    public override string? GiveBack(HangarGood good) => good.Deal == HangarDeal.Sell ? Deal(good) : Free;

    public override string? Deal(HangarGood good)
    {
        var (id, catalogue) = (good.Item.Id, good.Item.Catalogue);
        if (good.Deal == HangarDeal.Buy && World.WhyCannotBuy(State, catalogue) is { } dear)
            return dear;
        if (good.Deal == HangarDeal.Upgrade && World.WhyNoUpgrade(State, good.Item) is { } last)
            return last;
        if (good.Deal == HangarDeal.Sell && Loadout.Holds(id))
            return "take it off first";
        game.Issue(good.Deal switch
        {
            HangarDeal.Buy => new WiredBuyCommand(Game.Player, catalogue),
            HangarDeal.Sell => new WiredSellCommand(Game.Player, id),
            HangarDeal.Upgrade => new WiredUpgradeCommand(Game.Player, id),
            _ => new WiredChipChoiceCommand(Game.Player, good.Amount),
        });
        return null;
    }

    /// <summary>A new offer, once per intermission.</summary>
    public string? Reroll() => Asked(World.WhyNoReroll(State), new WiredRerollCommand(Game.Player));

    /// <summary>An escort of a type.</summary>
    public string? Recruit(ResearchKey machine) => Asked(World.WhyNoEscort(State, machine), new WiredSquadBuyCommand(Game.Player, machine));

    /// <summary>Mends the pod for so many BMUs, or wholly for 0.</summary>
    public string? MendPod(int bmu) => Asked(World.WhyNoPodRepair(State), new WiredPodRepairCommand(Game.Player, bmu));

    /// <summary>The machine that comes for a choice from the squad list: with Veterans a better one, as the sim delivers it.</summary>
    public ResearchKey EscortAs(ResearchKey machine) => World.EscortAs(State, machine);

    /// <summary>Why an escort cannot be had, or null.</summary>
    public string? WhyNoEscort(ResearchKey machine) => World.WhyNoEscort(State, machine);

    public int EscortPrice(ResearchKey machine) => World.EscortPrice(State, machine);

    /// <summary>The escorts standing.</summary>
    public IEnumerable<Machine> Escorts => World.Escorts(Game.Player);

    /// <summary>The pod's hit points as they stand and what mending it wholly costs; null when it is gone.</summary>
    public (int HitPoints, int MaxHitPoints, int Price)? Pod => World.WiredPod(Game.Player) is { } pod
        ? (pod.HitPoints, pod.MaxHitPoints, State.PodRepairPrice(pod.MaxHitPoints - pod.HitPoints))
        : null;

    /// <summary>The hit points of the pod that so many BMUs mend.</summary>
    public int PodRepairFor(int bmu) => State.PodRepairFor(bmu);

    private string? Asked(string? why, WiredCommand command)
    {
        if (why is null)
            game.Issue(command);
        return why;
    }
}
