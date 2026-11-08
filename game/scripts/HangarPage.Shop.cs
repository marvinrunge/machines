using Godot;
using Machines.Sim.Survival;

namespace Machines.Game;

// The hangar's left side as shelves, and all that is done on them: buying has a click of its own, selling goes through the
// same strip, and what the mouse is on goes to the comparison under the hero's values.
public partial class HangarPage
{
    /// <summary>The click of money changing hands: no other button of the game has it.</summary>
    private const string BuySound = "waypoint.wav", SellSound = "ctrlpoff.wav";

    private static readonly (HangarShelf Shelf, string Name)[] Store =
    [
        (HangarShelf.Tree, "Tree"),
        (HangarShelf.Offer, "Offer"),
        (HangarShelf.Upgrades, "Mk"),
        (HangarShelf.Squad, "Squad"),
        (HangarShelf.Pod, "Pod"),
    ];

    private readonly List<SquadCard> _squad = [];
    private HangarChoice? _choice;
    private ChipOffer? _chipOffer;
    private ChassisTreeView? _tree;
    private Control? _stockRow, _storeRow;
    private HangarTab? _stockTab, _storeTab;
    private ChipButton? _reroll;
    private HangarShelf _lastStock = HangarShelf.Weapons, _lastStore = HangarShelf.Tree;
    private const int TabGap = 4;

    private float _narrow, _wide;
    private int _stamp;
    private bool _board;
    private string? _lookedChassis, _lookedWeapon;
    private (string? Chassis, string? Weapon, HeroLoadout? Worn, int Bank, string? Pointed, int Price) _choiceAsked;
    private readonly DealClicks _deals = new();

    /// <summary>The sockets as shown now, with a chip being tried in its place.</summary>
    public IReadOnlyList<WiredItem?> ChipsShown { get; private set; } = new WiredItem?[WiredBalance.ChipSockets];

    public HangarShelf ShelfShown => _shelf;

    /// <summary>The escorts on the shelf.</summary>
    public IReadOnlyList<SquadCard> Squad => _squad;

    /// <summary>The chips a boss left, while they wait.</summary>
    public IReadOnlyList<ItemCard> ChipChoices => _chipOffer?.Cards ?? [];

    public Control? Reroll => _reroll;

    /// <summary>The tab of the owned goods, or of the shop.</summary>
    public Control? GroupTab(bool shop) => shop ? _storeTab : _stockTab;

    /// <summary>A socket of the board at the foot: it works as any slot does.</summary>
    public void AddSocket(SlotCard card)
    {
        _board = true;
        _slots.Add(card);
    }

    private Control BuildTabs()
    {
        Control Row(IEnumerable<(HangarShelf Shelf, string Name)> shelves)
        {
            var row = Line(TabGap);
            foreach (var (shelf, text) in shelves)
            {
                var tab = new HangarTab { Text = Lang.T(text).ToUpperInvariant() };
                tab.Pressed += () => ShowShelf(shelf);
                row.AddChild(_tabs[shelf] = tab);
            }
            return row;
        }
        if (desk.Shop is null)
            return Row(Stock.Append((HangarShelf.Tree, "Path")));
        _stockTab = new HangarTab { Text = "" };
        _storeTab = new HangarTab { Text = Lang.T("Shop").ToUpperInvariant() };
        _stockTab.Pressed += () => ShowShelf(_lastStock);
        _storeTab.Pressed += () => ShowShelf(_lastStore);
        return Column(TabGap, Line(TabGap, _stockTab, _storeTab), _stockRow = Row(Stock), _storeRow = Row(Store));
    }

    /// <summary>A row of tabs wider than the panel would widen it while it is shown, and the stage would jump from shelf to shelf: so the panel takes the widest from the start.</summary>
    private void FitTabs()
    {
        var rows = _tabs.GroupBy(t => desk.Shop is not null && t.Key >= HangarShelf.Tree);
        var widest = rows.Max(row => row.Sum(t => t.Value.CustomMinimumSize.X + TabGap) - TabGap);
        _narrow = Math.Max(_narrow, widest + 2 * (ChipArt.FrameWidth(0.5f) + 12));
        _wide = Math.Max(_wide, _narrow);
    }

    /// <summary>How full the inventory is, on its tab, so that a shelf of offers can be read with it in view.</summary>
    private void ShowStock()
    {
        if (_stockTab is null || desk.Shop is not { } shop)
            return;
        _stockTab.Text = Lang.T("Inventory {0} / {1}", shop.Spares, WiredBalance.InventoryLimit).ToUpperInvariant();
        _stockTab.Warning = shop.Full;
    }

    /// <summary>Opens a shelf.</summary>
    public void ShowShelf(HangarShelf shelf)
    {
        _shelf = shelf;
        if (shelf < HangarShelf.Tree)
            _lastStock = shelf;
        else
            _lastStore = shelf;
        _picked = null;
        _pointed = null;
        _hovered = null;
        Fill();
        _scroll.ScrollVertical = 0;
    }

    private void Fill()
    {
        _cards.Clear();
        _squad.Clear();
        _tree = null;
        _reroll = null;
        foreach (var box in new Control[] { _grid, _extra })
        {
            foreach (var child in box.GetChildren())
            {
                box.RemoveChild(child);
                child.QueueFree();
            }
        }
        foreach (var (shelf, tab) in _tabs)
            tab.On = shelf == _shelf;
        var shop = _shelf >= HangarShelf.Tree;
        if (_stockTab is not null)
        {
            _stockTab.On = !shop;
            _storeTab!.On = shop;
            _stockRow!.Visible = !shop;
            _storeRow!.Visible = shop;
        }
        _inventory.CustomMinimumSize = new Vector2(_shelf == HangarShelf.Tree ? _wide : _narrow, 0);

        switch (_shelf)
        {
            case HangarShelf.Tree:
                FillTree();
                break;
            case HangarShelf.Offer:
                FillOffer();
                break;
            case HangarShelf.Upgrades:
                Head("Mk upgrades");
                Goods(desk.Upgrades());
                break;
            case HangarShelf.Squad:
                FillSquad();
                break;
            case HangarShelf.Pod:
                Head("Pod repair");
                _extra.AddChild(new PodPanel(this, (MatchDesk)desk));
                break;
            default:
                Goods(desk.Goods(Stock[(int)_shelf].Shelf switch
                {
                    HangarShelf.Weapons => WiredKind.Weapon,
                    HangarShelf.Modules => WiredKind.Module,
                    HangarShelf.Abilities => WiredKind.Ability,
                    HangarShelf.Chips => WiredKind.Chip,
                    _ => WiredKind.Chassis,
                }));
                break;
        }
    }

    private float CardWidth => (_inventory.CustomMinimumSize.X - 2 * (ChipArt.FrameWidth(0.5f) + 12) - 22 - (_grid.Columns - 1) * 22 - 12) / _grid.Columns;

    /// <summary>A shelf's heading, with what it has beside it.</summary>
    private void Head(string title, Control? beside = null)
    {
        var name = MenuTheme.Label(Lang.T(title), 23, MenuTheme.Bright);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var line = Line(10, name);
        if (beside is not null)
            line.AddChild(beside);
        _extra.AddChild(line);
    }

    private void Goods(IEnumerable<HangarGood?> goods)
    {
        var width = CardWidth;
        var list = goods.ToList();
        if (list.Count == 0)
            Nothing();
        foreach (var good in list)
        {
            if (good is null)
            {
                _grid.AddChild(new SoldCard { CustomMinimumSize = new Vector2(width, 112) });
                continue;
            }
            var height = good.Deal != HangarDeal.None ? 112 : good.Price is null && good.Locked is null ? 86 : 106;
            var card = new ItemCard { Good = good, Page = this, CustomMinimumSize = new Vector2(width, height) };
            _grid.AddChild(card);
            _cards.Add(card);
        }
    }

    /// <summary>An empty shelf says so, and how it fills.</summary>
    private void Nothing()
    {
        var (none, how) = _shelf switch
        {
            HangarShelf.Weapons => ("No spare weapons.", "What you buy in the shop or take off the hero waits here."),
            HangarShelf.Modules => ("No spare modules.", "What you buy in the shop or take off the hero waits here."),
            HangarShelf.Abilities => ("No spare abilities.", "What you buy in the shop or take off the hero waits here."),
            HangarShelf.Chips => ("No spare chips.", "Chips come from bosses and crates."),
            HangarShelf.Chassis => ("No other chassis.", "The tree in the shop leads on from what the hero is on."),
            _ => ("Nothing here yet.", "What the hero owns can go up a Mk here."),
        };
        _extra.AddChild(MenuTheme.Paragraph(Lang.T(none), 21, MenuTheme.Text));
        _extra.AddChild(MenuTheme.Paragraph(Lang.T(how), 17, MenuTheme.Dim.Lightened(0.25f)));
    }

    private void FillTree()
    {
        _tree = new ChassisTreeView(this);
        foreach (var good in desk.Tree())
            _cards.Add(_tree.Add(good));
        _extra.AddChild(_tree);
        FitTree();
    }

    /// <summary>The tree takes what the shelf has: it is meant to be seen whole.</summary>
    private void FitTree()
    {
        if (_tree is not null)
            _tree.CustomMinimumSize = new Vector2(0, Math.Max(_scroll.Size.Y - 36, ChassisTreeView.LeastHeight));
    }

    private void FillOffer()
    {
        var shop = desk.Shop!;
        _reroll = Button(Lang.T("Reroll {0} BMU", shop.RerollPrice), RollAgain, 48, 18);
        _reroll.CustomMinimumSize = new Vector2(210, 48);
        Head("Offer", _reroll);
        Goods(desk.Offers());
    }

    private void RollAgain()
    {
        if (((MatchDesk)desk).Reroll() is { } why)
            Refuse(why);
        else
            Bought();
    }

    private void FillSquad()
    {
        var match = (MatchDesk)desk;
        var shop = match.Shop;
        Head(Lang.T("Squad {0} / {1}", shop.SquadSize, shop.SquadLimit));
        _extra.AddChild(new SquadRoster(match));
        var width = CardWidth;
        foreach (var key in shop.Escorts())
        {
            var card = new SquadCard { Machine = key, Arrives = match.EscortAs(key), Price = match.EscortPrice(key), Why = match.WhyNoEscort(key), Page = this, CustomMinimumSize = new Vector2(width, 118) };
            _grid.AddChild(card);
            _squad.Add(card);
        }
    }

    private void Recruit(SquadCard card)
    {
        Dealing();
        if (((MatchDesk)desk).Recruit(card.Machine) is { } why)
            Refuse(why);
        else
            Bought();
    }

    /// <summary>The click of a purchase.</summary>
    public void Bought() => MenuSounds.Gui(BuySound);

    /// <summary>Whether a click here and now is the second half of a double click on a deal.</summary>
    private bool JustDealt => _deals.Second(Mouse.X, Mouse.Y, Time.GetTicksMsec(), DragStart * GetGlobalTransform().Scale.Y);

    /// <summary>A deal goes out from where the mouse is.</summary>
    private void Dealing() => _deals.Dealt(Mouse.X, Mouse.Y, Time.GetTicksMsec());

    /// <summary>A click on a card's strip.</summary>
    private void Deal(HangarGood good)
    {
        Dealing();
        if (desk.Deal(good) is { } why)
        {
            Refuse(why);
            return;
        }
        switch (good.Deal)
        {
            case HangarDeal.Sell:
                MenuSounds.Gui(SellSound);
                break;
            case HangarDeal.Upgrade when _shown.SlotOf(good.Item.Id) is { } slot:
                _sparks.Add(slot);
                MenuSounds.Gui("weapchng.wav");
                MenuSounds.Effect("SID_CONSTRUCTING");
                break;
            default:
                Bought();
                break;
        }
    }

    /// <summary>A click on a chassis of the tree that is no purchase: the hero changes to it if that can be done.</summary>
    private void WearFromTree(HangarGood good)
    {
        if (good.Step != ChassisStep.Owned)
            return;
        Dealing();
        Wear(good);
    }

    /// <summary>What a good does, for the banner: for a chip with what linking would add where it would go.</summary>
    private string? Said(HangarGood good)
    {
        if (HangarText.Effect(good.Item) is not { } plain)
            return null;
        var then = _trial?.Loadout ?? _shown;
        if (good.Item.Kind != WiredKind.Chip || then.SlotOf(good.Item.Id) is not { } socket)
            return plain;
        return WiredChips.Linked(then.Chips, socket.Index) ? $"{Lang.T("Linked")}: {HangarText.Effect(good.Item, WiredChips.Strength(then.Chips, socket.Index))}" : plain;
    }

    /// <summary>Hands the comparison what there is to weigh up, when that has changed.</summary>
    private void ShowChoice()
    {
        if (_choice is null || desk.Shop is not { } shop)
            return;
        FitTree();
        var good = _pointed;
        if (good is { Deal: HangarDeal.Buy, Item.Kind: WiredKind.Chassis })
            _lookedChassis = good.Item.Catalogue;
        else if (good is { Deal: HangarDeal.Buy, Item.Kind: WiredKind.Weapon })
            _lookedWeapon = good.Item.Catalogue;
        var (name, price) = (_hovered, good) switch
        {
            (SquadCard card, _) => (HangarText.Machine(card.Arrives), card.Price),
            (_, { Deal: HangarDeal.Buy, Locked: null, Price: { } cost }) => (HangarText.Name(good.Item.Catalogue), cost),
            (_, { Deal: HangarDeal.Upgrade, Amount: var cost }) => ($"{HangarText.Name(good.Item.Catalogue)} Mk {good.Item.Mk + 1}", cost),
            _ => ((string?)null, 0),
        };
        var asked = (_lookedChassis, _lookedWeapon, (HeroLoadout?)_shown, desk.Bank, name, price);
        if (asked == _choiceAsked)
            return;
        _choiceAsked = asked;
        _choice.Show(shop.Choice(_lookedChassis, _lookedWeapon), shop.Wave, shop.WaveBonus, name, price);
    }
}

internal static class HangarGoods
{
    /// <summary>What sits in a slot as a good to be told about.</summary>
    public static HangarGood? Good(this WiredItem? item) => item is null ? null : new HangarGood(item);
}
