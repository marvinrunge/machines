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

/// <summary>
/// What the hangar works on, so that one screen serves both: a hero being made before a run (<see cref="CreationDesk"/>)
/// and the hero of a run (<see cref="MatchDesk"/>). Whatever it tries out it works out on a copy of the loadout, the screen
/// asks for nothing else, and only <see cref="Put"/>, <see cref="TakeOff"/> and <see cref="Wear"/> change anything.
/// Wired is the remake's own addition.
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

    /// <summary>How the bank is shown.</summary>
    public virtual string BankText => $"{Bank} BMU";

    /// <summary>What the right mouse button does here.</summary>
    public virtual string Hint => "Right click takes off.";

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

    /// <returns>null if the good can go into the slot now, otherwise why not, in the sim's words.</returns>
    public abstract string? WhyNot(HangarGood good, WiredSlot slot);

    /// <returns>null if the hero can change to the chassis now, otherwise why not.</returns>
    public abstract string? WhyNotWear(HangarGood chassis);

    /// <summary>Puts a good into a slot, buying it if it is on offer.</summary>
    /// <returns>null if done or on its way, otherwise why not.</returns>
    public abstract string? Put(HangarGood good, WiredSlot slot);

    /// <summary>Takes an item off the hero; it stays owned.</summary>
    public abstract string? TakeOff(WiredItem item);

    /// <summary>Changes the chassis.</summary>
    public abstract string? Wear(HangarGood chassis);

    /// <summary>Whether a good is one of the hero's spares, which a right click gives back.</summary>
    public abstract bool IsSpare(HangarGood good);

    /// <summary>Gives a spare back for what it brings.</summary>
    /// <returns>null if done or on its way, otherwise why not.</returns>
    public abstract string? GiveBack(HangarGood good);

    /// <summary>The loadout with a good in a slot: what the hero would be.</summary>
    public HeroLoadout Trying(HangarGood good, WiredSlot slot) => Loadout.With(slot, good.Item);

    /// <summary>The loadout on another chassis: what stays on and what falls off.</summary>
    public HeroLoadout Trying(HangarGood chassis) => Loadout.OnChassis(chassis.Item);

    public HeroBuild Build(HeroLoadout loadout) => HeroBuild.Resolve(Data, loadout);

    /// <summary>The loadout with a worn item one Mk up. A spare goes up where it lies: the hero stays as it is.</summary>
    public HeroLoadout Upgraded(WiredItem item) => Loadout.Holds(item.Id) ? Loadout.Replacing(item with { Mk = item.Mk + 1 }) : Loadout;

    /// <summary>
    /// The first slot a good fits, an empty one if there is one, and for a chip one that links it if there is one;
    /// null for none, or when it goes into no slot.
    /// </summary>
    public WiredSlot? BestSlot(HangarGood good)
    {
        var fitting = Slots(Loadout, good.Item.Kind).Where(s => Loadout.WhyNotFit(good.Item, s) is null).ToList();
        var free = fitting.Where(s => Loadout.At(s) is null).ToList();
        if (good.Item.Kind == WiredKind.Chip && free.FirstOrDefault(s => WiredChips.Linked(Loadout.With(s, good.Item).Chips, s.Index)) is { } linked)
            return linked;
        return free.FirstOrDefault() ?? fitting.FirstOrDefault();
    }

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

    public override string? WhyNot(HangarGood good, WiredSlot slot)
    {
        if (good.Locked is { } locked)
            return locked;
        return good.Price is null ? Draft.WhyNotHold(good.Item, slot) : Draft.WhyNotBuy(good.Item.Catalogue, slot);
    }

    public override string? WhyNotWear(HangarGood chassis) => chassis.Locked;

    public override string? Put(HangarGood good, WiredSlot slot) =>
        WhyNot(good, slot) ?? (good.Price is null ? Draft.Put(good.Item.Id, slot) : Draft.Buy(good.Item.Catalogue, slot));

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

    public override string? WhyNot(HangarGood good, WiredSlot slot)
    {
        if (Loadout.WhyNotFit(good.Item, slot) is { } misfit)
            return misfit;
        return (good.Price is null ? null : World.WhyCannotBuy(State, good.Item.Catalogue)) ?? State.WhyNotWear(Data, Trying(good, slot));
    }

    public override string? WhyNotWear(HangarGood chassis) => State.Item(chassis.Item.Id) is null ? "you own no such chassis" : State.WhyNotWear(Data, Trying(chassis));

    public override string? Put(HangarGood good, WiredSlot slot)
    {
        if (WhyNot(good, slot) is { } why)
            return why;
        // What is bought or chosen onto a slot is one command, so that it gets there even if the hangar closes right behind it.
        game.Issue(good switch
        {
            { Deal: HangarDeal.Choose } => new WiredChipChoiceCommand(Game.Player, good.Amount, slot.Index),
            { Price: not null } => new WiredBuyCommand(Game.Player, good.Item.Catalogue, slot),
            _ when slot.Kind == WiredSlotKind.Chip => new WiredChipCommand(Game.Player, good.Item.Id, slot.Index),
            _ => new WiredEquipCommand(Game.Player, good.Item.Id, slot),
        });
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

    /// <summary>The machine that comes for a choice from the squad list: with Veterans a better one.</summary>
    public ResearchKey EscortAs(ResearchKey machine) => WiredChips.Veteran(State, machine);

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
