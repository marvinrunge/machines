using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The pod of Battle, on the hangar's screen: the hero is put together out of what the account owns (<see cref="BattleDraft"/>),
/// and research and purchases are made here, outside any battle. The shelves show every weapon, module and ability with what
/// it costs in credits; the tree (the Path) every chassis with its tier, the XP its research takes or the credits it is sold
/// for, and why it cannot be had yet. There are no chips. Battle is the remake's own addition.
/// </summary>
public sealed class BattleDesk(GameData data, ClassicAssets assets, IBattleAccount account) : HangarDesk
{
    private int _changes;

    public BattleDraft Draft { get; } = new(data, account);

    public IBattleAccount Account => account;

    /// <summary>Chassis that came within reach since the pod was last up.</summary>
    public IReadOnlySet<string> Fresh { get; init; } = new HashSet<string>();

    /// <summary>The shelf on show when the pod comes up.</summary>
    public HangarShelf First { get; init; } = HangarShelf.Weapons;

    public override HangarShelf FirstShelf => First;

    public override GameData Data => data;

    public override ClassicAssets Assets => assets;

    public override Race Look => Draft.Colour;

    public override HeroLoadout Loadout => Draft.Loadout;

    /// <summary>The credits, as far as the cards weigh them.</summary>
    public override int Bank => (int)Math.Min(int.MaxValue, account.Credits);

    public override string BankText => PriceText(Bank);

    public override string Title => "Pod";

    public override string Hint => "Right click takes off.";

    public override string PriceText(int amount) => Lang.T("{0} Cr", amount);

    /// <summary>Abilities go on chassis from tier III: below it the ability slots show as no slot.</summary>
    public override bool HasSlot(WiredSlot slot, WiredChassis chassis) =>
        slot.Kind != WiredSlotKind.Ability || BattleCatalogue.Tier(chassis.Id) >= BattleCatalogue.AbilitiesFrom;

    /// <summary>XP as the cards write it.</summary>
    public static string XpText(int amount) => Lang.T("{0} XP", amount);

    public override IReadOnlyList<HangarShelf> Shelves => [HangarShelf.Weapons, HangarShelf.Modules, HangarShelf.Abilities, HangarShelf.Chassis];

    public override int Stamp => HashCode.Combine(account.Credits, account.FreeXp, account.Xp(Draft.Chassis), _changes);

    /// <summary>A reason of the draft or the account in the player's language: ids by their names, several joined by "or".</summary>
    public static string Say(BattleWhy why) => Lang.T(why.Words, why.Args.Select(arg => arg switch
    {
        string[] ids => string.Join($" {Lang.T("or")} ", ids.Select(HangarText.Name)),
        string id when WiredCatalogue.KindOf(id) is not null => HangarText.Name(id),
        _ => arg,
    }).ToArray());

    private static string? Said(BattleWhy? why) => why is null ? null : Say(why);

    public override IEnumerable<HangarGood> Goods(WiredKind kind)
    {
        if (kind == WiredKind.Chassis)
            return Draft.Tree().Where(n => n.Stage != BattleStage.Worn).Select(Node)
                .OrderBy(g => g.Step switch { ChassisStep.Owned => 0, ChassisStep.ForSale or ChassisStep.TooDear when g.Deal == HangarDeal.Buy => 1, _ => 2 });
        if (kind == WiredKind.Chip)
            return [];
        return Draft.Offers(kind).Select(Good);
    }

    /// <summary>An item on its shelf: owned with its next Mk on the strip where that may be had, or for sale.</summary>
    private HangarGood Good(BattleOffer offer)
    {
        var at = WiredCatalogue.Weapons.Select(w => w.Id).Concat(WiredCatalogue.Modules.Select(m => m.Id)).Concat(WiredCatalogue.Abilities.Select(a => a.Id)).ToList().IndexOf(offer.Item);
        var item = new WiredItem(-1 - at, offer.Kind, offer.Item, Math.Max(1, offer.Mk));
        if (offer.Mk == 0)
            return new HangarGood(item, offer.Price, Said(offer.Lock)) { Deal = HangarDeal.Buy };
        return new HangarGood(item, null, Said(offer.Lock)) { Deal = offer.Upgrade is null ? HangarDeal.None : HangarDeal.Upgrade, Amount = offer.Upgrade ?? 0 };
    }

    public override IEnumerable<HangarGood> Tree() => Draft.Tree().Select(Node);

    /// <summary>A chassis on the tree, its stage told in the hangar's steps: research is a deal of its own.</summary>
    private HangarGood Node(BattleNode node)
    {
        var at = WiredCatalogue.Chassis.ToList().FindIndex(c => c.Id == node.Chassis);
        var item = new WiredItem(-100 - at, WiredKind.Chassis, node.Chassis);
        var good = new HangarGood(item, null, Said(node.Lock)) { Fresh = Fresh.Contains(node.Chassis), Amount = node.Cost };
        return node.Stage switch
        {
            BattleStage.Worn => good with { Step = ChassisStep.Worn },
            BattleStage.Owned => good with { Step = ChassisStep.Owned },
            BattleStage.ForSale => good with { Step = ChassisStep.ForSale, Price = node.Cost, Deal = HangarDeal.Buy },
            BattleStage.TooDear => good with { Step = ChassisStep.TooDear, Price = node.Cost, Deal = HangarDeal.Buy },
            BattleStage.Researchable => good with { Step = ChassisStep.ForSale, Deal = HangarDeal.Research },
            BattleStage.Short => good with { Step = ChassisStep.TooDear, Deal = HangarDeal.Research },
            _ => good with { Step = ChassisStep.Closed },
        };
    }

    public override string? StepWord(HangarGood chassis) => chassis switch
    {
        { Step: ChassisStep.Worn } => null,
        { Step: ChassisStep.Owned } => Lang.T("Owned"),
        { Deal: HangarDeal.Buy, Price: { } price } => PriceText(price),
        _ => XpText(chassis.Amount),
    };

    public override string? Badge(HangarGood chassis) => BattleDraft.Roman(BattleCatalogue.Tier(chassis.Item.Catalogue));

    /// <summary>Research is paid in XP, everything else in credits.</summary>
    public override string DealText(HangarGood good, int amount) => good.Deal == HangarDeal.Research ? XpText(amount) : PriceText(amount);

    public override string? Pointing(HangarGood good, bool onTree)
    {
        var id = good.Item.Catalogue;
        if (good.Item.Kind == WiredKind.Chassis)
        {
            var tier = Lang.T("Tier {0}", BattleDraft.Roman(BattleCatalogue.Tier(id)));
            var said = good switch
            {
                { Step: ChassisStep.Worn } => tier,
                { Locked: { } locked } => $"{tier} · {locked}",
                { Deal: HangarDeal.Research } => $"{tier} · {Lang.T("Research for {0}", XpText(good.Amount))}",
                { Deal: HangarDeal.Buy, Price: { } price } => $"{tier} · {Lang.T("Buy for {0}", PriceText(price))}",
                // On the Path a click changes to it at once (WearFromTree); on the shelf a click picks it up and a double click changes.
                _ => $"{tier} · {Lang.T(onTree ? "Owned: a click changes to it." : "Owned: a double click changes to it.")}",
            };
            // What the chassis has of its own, as a battle has it, goes under it.
            return HangarText.ChassisEffect(id, battle: true) is { } own ? $"{said}\n{own}" : said;
        }
        if (Draft.Wears(good.Item.Id))
            return null;
        var offer = Draft.Offers(good.Item.Kind).FirstOrDefault(o => o.Item == id);
        return offer?.Lock is { } why ? Say(why) : offer?.Note is { } note ? Say(note) : null;
    }

    public override bool Reached(string chassis) => account.Researched(chassis);

    public override string? Deal(HangarGood good)
    {
        var id = good.Item.Catalogue;
        return Done(good.Deal switch
        {
            HangarDeal.Research => Draft.Research(id),
            HangarDeal.Buy => good.Locked is { } locked ? new BattleWhy(locked) : Draft.Buy(id),
            HangarDeal.Upgrade => good.Locked is { } locked ? new BattleWhy(locked) : Draft.Buy(id, account.Mk(id) + 1),
            _ => null,
        });
    }

    private string? Done(BattleWhy? why)
    {
        _changes++;
        return Said(why);
    }

    public override string? WhyNot(HangarGood good, WiredSlot slot, bool replace = false)
    {
        if (good.Locked is { } locked)
            return locked;
        if (Draft.Wears(good.Item.Id))
            return Said(Draft.WhyNotMove(good.Item.Id, slot, replace));
        if (good.Price is { } price)
            return Said(Draft.WhyNotPut(good.Item.Catalogue, slot, buying: true, replace: replace) ?? (account.Credits < price ? new BattleWhy("Needs {0} Cr", price) : null));
        return Said(Draft.WhyNotPut(good.Item.Catalogue, slot, replace: replace));
    }

    public override string? WhyNotWear(HangarGood chassis) => chassis.Locked ?? Said(Draft.WhyNotWear(chassis.Item.Catalogue));

    public override string? Put(HangarGood good, WiredSlot slot, bool replace = false)
    {
        if (WhyNot(good, slot, replace) is { } why)
            return why;
        if (Draft.Wears(good.Item.Id))
            return Said(Draft.Move(good.Item.Id, slot, replace));
        // What is on sale and dropped onto a slot is bought on the way, as in Wired's hangar.
        if (good.Price is not null && Done(Draft.Buy(good.Item.Catalogue)) is { } dear)
            return dear;
        return Said(Draft.Put(good.Item.Catalogue, slot, replace));
    }

    /// <summary>An owned weapon fills a battery for nothing (Q8): a copy costs no credits, only load.</summary>
    public override int? CopyPrice(WiredSlot slot) => HasRoom(slot) ? 0 : null;

    public override string? WhyNoCopy(WiredSlot slot) => CopyWeapon(slot) is { } weapon ? Said(Draft.WhyNotPut(weapon, slot)) : NoWeapon;

    public override string? BuyCopy(WiredSlot slot) => WhyNoCopy(slot) ?? Said(Draft.Put(CopyWeapon(slot)!, slot));

    public override string? TakeOff(WiredItem item)
    {
        Draft.TakeOff(item.Id);
        return null;
    }

    public override string? Wear(HangarGood chassis) => WhyNotWear(chassis) ?? Said(Draft.Wear(chassis.Item.Catalogue));

    public override bool IsSpare(HangarGood good) => false;

    public override string? GiveBack(HangarGood good) => Free;
}
