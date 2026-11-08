using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The hangar: the hero big on its turntable in the middle, traces running from its mounts to the slots around it, the
/// inventory on the left and its values on the right. It serves hero creation in the menus as well as the run, whichever
/// <see cref="HangarDesk"/> it works on. Everything can be tried before it is done: pointing at a good shows the model
/// and the values as they would be, worked out by <see cref="HeroBuild.Resolve"/> and <see cref="HeroRig.Compose"/> on a
/// copy, and for a chassis what stays on, what falls off and which slots are left empty. In a run its left side holds the
/// shop too (HangarPage.Shop) and its foot the chip board. Wired is the remake's own addition.
/// </summary>
public partial class HangarPage(HangarDesk desk, Action back, Func<HangarPage, Control?>? plate, Func<HangarPage, Control> bar) : MenuPage(back)
{
    public static readonly Color Red = new(1f, 0.26f, 0.2f);

    /// <summary>How long the mouse must rest on a good before its model is put together: sweeping over a list must not stall.</summary>
    private const float Settle = 0.09f;

    private const float DragStart = 8;
    private const float RefusalTime = 3.2f;

    private const float ArrivalTime = 9;

    /// <summary>What the banner may shrink to, so that all of a text is there: its box holds two lines only from the second size on.</summary>
    private static readonly int[] BannerSizes = [18, 15, 13, 12];

    private static readonly (HangarShelf Shelf, string Name)[] Stock =
    [
        (HangarShelf.Weapons, "Weapons"),
        (HangarShelf.Modules, "Modules"),
        (HangarShelf.Abilities, "Abilities"),
        (HangarShelf.Chips, "Chips"),
        (HangarShelf.Chassis, "Chassis"),
    ];

    /// <summary>A build being tried: <paramref name="Chassis"/> says the hero changes chassis.</summary>
    private sealed record Trial(HeroLoadout Loadout, bool Chassis);

    private readonly List<SlotCard> _slots = [];
    private readonly List<ItemCard> _cards = [];
    private readonly Dictionary<HangarShelf, HangarTab> _tabs = [];
    private HangarScreen _screen = null!;
    private HangarTraces _traces = null!;
    private HangarValues _values = null!;
    private ChassisPlate _plate = null!;
    private Control _layers = null!, _floating = null!, _inventory = null!;
    private VBoxContainer _extra = null!;
    private GridContainer _grid = null!;
    private ScrollContainer _scroll = null!;
    private Label _bank = null!, _banner = null!, _hint = null!;
    private ChipButton _testFire = null!;

    private HangarShelf _shelf = HangarShelf.Weapons;
    private HeroLoadout _shown = null!;
    private HeroBuild _build = null!;
    private int _shownBank = int.MinValue;
    private HangarGood? _picked, _held, _pointed;
    private HangarCard? _pressed, _hovered;
    private Vector2 _pressedAt;
    private bool _mouseDown;
    private ItemCard? _ghost;
    private SlotCard? _under;
    private bool _overPlate;
    private (HangarGood? Good, WiredSlot? Slot, bool Plate, HangarCard? Hovered, HeroLoadout? Shown) _asked;
    private Trial? _trial;
    private HeroLoadout? _wanted;
    private float _settling, _refusalLeft, _arrivalLeft;
    private string? _refusal, _arrival;
    private readonly List<WiredSlot> _sparks = [];

    public HangarDesk Desk => desk;

    /// <summary>The BMUs there are, for the cards' prices.</summary>
    public int Bank => desk.Bank;

    /// <summary>Something is being carried by the mouse.</summary>
    public bool Dragging => _held is not null;

    public IReadOnlyList<ItemCard> Cards => _cards;

    public IReadOnlyList<SlotCard> Slots => _slots;

    public ChassisPlate Plate => _plate;

    public HangarScreen Screen => _screen;

    public string Banner => _banner.Text;

    /// <summary>What it announced on coming up, while it still says so.</summary>
    public string Arrival => _arrival ?? "";

    /// <summary>For automated checks: what the bar's own line says.</summary>
    public string Bar { get; set; } = "";

    /// <summary>What has arrived since the hangar was last up: the banner says so in gold for a while.</summary>
    public void Announce(string text)
    {
        _arrival = text;
        _arrivalLeft = ArrivalTime;
    }

    public HeroBuild Hero => _build;

    /// <summary>The build being tried now, or null.</summary>
    public HeroBuild? Trying => _trial is { } trial ? desk.Build(trial.Loadout) : null;

    public Control TabOf(WiredKind kind) => _tabs[Shelf(kind)];

    public Control TabOf(HangarShelf shelf) => _tabs[shelf];

    public IEnumerable<HangarShelf> Shelves => _tabs.Keys;

    public Control Inventory => _inventory;

    protected override void Build()
    {
        foreach (var (side, value) in new[] { ("left", 30), ("right", 30), ("top", 26), ("bottom", 26) })
            AddThemeConstantOverride($"margin_{side}", value);
        AddChild(new HangarIcons(desk.Assets, () => desk.Look));

        var compact = Stage.Size.X < 1700;
        // The tree takes room from the stage, but leaves it enough for the hero.
        (_narrow, _wide) = (compact ? 372 : 470, compact ? Math.Clamp(Stage.Size.X - 860, 440, 560) : 740);
        var column = Column(18);
        AddChild(column);
        var row = Line(20);
        row.SizeFlagsVertical = SizeFlags.ExpandFill;
        column.AddChild(row);
        row.AddChild(_inventory = BuildInventory(_narrow, compact ? 1 : 2));
        FitTabs();
        row.AddChild(BuildCentre());
        row.AddChild(BuildValues(compact ? 306 : 372));
        column.AddChild(bar(this));

        // What the mouse carries floats over all of it.
        _floating = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_floating);

        Refresh();
        foreach (var (shelf, tab) in _tabs)
            tab.Fresh = shelf switch
            {
                HangarShelf.Tree => desk.Tree().Any(g => g.Fresh),
                HangarShelf.Chassis => desk.Goods(WiredKind.Chassis).Any(g => g.Fresh),
                HangarShelf.Chips => desk.Goods(WiredKind.Chip).Any(g => g.Fresh),
                _ => false,
            };
        HangarIcons.Ahead(desk.Tree().Concat(desk.Goods(WiredKind.Weapon)).Concat(desk.Offers().OfType<HangarGood>()).Select(g => g.Item), desk.Shop?.Escorts() ?? []);
        SwitchOn();
        // MACHINES_HANGAR_SCRIPT=<folder>,<what>: see HangarScript.
        if (System.Environment.GetEnvironmentVariable("MACHINES_HANGAR_SCRIPT")?.Split(',') is [var folder, var what] && !(what == "start" && HangarScript.Started))
            AddChild(new HangarScript(this, folder, desk is CreationDesk || what is "shop" or "bay" or "view" ? what : "match"));
    }

    private ChipPanel BuildInventory(float width, int columns)
    {
        var chip = new ChipPanel { CustomMinimumSize = new Vector2(width, 0), Padding = 12 };
        var title = Line(10);
        var name = MenuTheme.Label(Lang.T("Hangar").ToUpperInvariant(), 34, MenuTheme.Bright);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.AddChild(name);
        title.AddChild(_bank = MenuTheme.Label("", 26, MenuTheme.Glow, HorizontalAlignment.Right));
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        _scroll.AddThemeConstantOverride("margin", 0);
        var inset = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            inset.AddThemeConstantOverride($"margin_{side}", 11);
        _grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 22);
        _grid.AddThemeConstantOverride("v_separation", 22);
        _extra = Column(12);
        _extra.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        inset.AddChild(Column(12, _extra, _grid));
        _scroll.AddChild(inset);
        var rows = Column(10, title, BuildTabs());
        if (desk.Shop is not null)
            rows.AddChild(_chipOffer = new ChipOffer(this) { Visible = false });
        rows.AddChild(_scroll);
        chip.AddChild(rows);
        return chip;
    }

    private ChipPanel BuildCentre()
    {
        var chip = new ChipPanel { Padding = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _layers = new Control();
        chip.AddChild(_layers);
        _screen = new HangarScreen(desk.Assets) { Classic = !GraphicsSettings.Effects };
        _screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _layers.AddChild(_screen);

        for (var i = 0; i < WiredBalance.AbilitySlots; i++)
            _slots.Add(new SlotCard { Slot = WiredSlot.Ability(i), Title = "", Page = this });
        for (var i = 0; i < WiredCatalogue.Chassis.Max(c => c.Modules); i++)
            _slots.Add(new SlotCard { Slot = WiredSlot.Module(i), Title = "", Page = this });
        foreach (var mounting in new[] { Mounting.Right, Mounting.Left, Mounting.Top })
            _slots.Add(new SlotCard { Slot = WiredSlot.Weapon(mounting), Title = "", Page = this });
        _traces = new HangarTraces { Cards = _slots, Screen = _screen };
        _traces.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _layers.AddChild(_traces);
        foreach (var card in _slots)
            _layers.AddChild(card);

        _plate = new ChassisPlate { Word = Lang.T("Chassis").ToUpperInvariant(), Page = this };
        _layers.AddChild(_plate);
        _hint = MenuTheme.Label(Lang.T("Drag to turn, wheel to zoom.") + '\n' + Lang.T(desk.Hint), 16, MenuTheme.Dim.Lightened(0.2f), HorizontalAlignment.Left);
        _hint.MouseFilter = MouseFilterEnum.Ignore;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _layers.AddChild(_hint);
        _banner = MenuTheme.Label("", BannerSizes[0], MenuTheme.Bright, HorizontalAlignment.Left);
        _banner.MouseFilter = MouseFilterEnum.Ignore;
        _banner.VerticalAlignment = VerticalAlignment.Center;
        _banner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _banner.ClipText = true;
        _banner.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(MenuTheme.Well, 0.88f), BorderColor = MenuTheme.Edge, BorderWidthBottom = 1, BorderWidthTop = 1, ContentMarginLeft = 8, ContentMarginRight = 8 });
        _layers.AddChild(_banner);
        _testFire = Button("Test fire", TestFire, 44, 18);
        _layers.AddChild(_testFire);
        _layers.Resized += Arrange;
        return chip;
    }

    private ChipPanel BuildValues(float width)
    {
        var chip = new ChipPanel { CustomMinimumSize = new Vector2(width, 0), Padding = 16 };
        var rows = Column(12);
        if (plate?.Invoke(this) is { } above)
            rows.AddChild(above);
        _values = new HangarValues { TurnLimit = desk.Data.Parm.General.FirstPersonMaxFastTurnRate };
        rows.AddChild(_values);
        if (desk.Shop is not null)
            rows.AddChild(_choice = new HangarChoice());
        chip.AddChild(rows);
        return chip;
    }

    /// <summary>Lays out the stage (<see cref="HangarLayout"/>), and the hero finds its place among what stands there.</summary>
    private void Arrange()
    {
        var modules = _slots.Count(c => c.Slot.Kind == WiredSlotKind.Module && c.Visible);
        var top = _slots.First(c => c.Slot == WiredSlot.Weapon(Mounting.Top)).Visible;
        var layout = HangarLayout.For(_layers.Size.X, _layers.Size.Y, top, modules);
        foreach (var card in _slots)
            if (layout.Cards.TryGetValue(card.Slot, out var box))
                Put(card, box);
        Put(_plate, layout.Plate);
        Put(_banner, layout.Status);
        Put(_hint, layout.Status);
        Put(_testFire, layout.Button);
        _screen.KeepOff(layout.Boxes);
        Say(_said.Text, _said.Colour);
    }

    private static void Put(Control control, HangarBox box)
    {
        control.Position = new Vector2(box.X, box.Y);
        control.Size = new Vector2(box.Width, box.Height);
    }

    // ---- showing the hero -------------------------------------------------------------------------

    /// <summary>Everything anew from the desk, after a change.</summary>
    public void Refresh()
    {
        _shown = desk.Loadout;
        _stamp = desk.Stamp;
        _build = desk.Build(_shown);
        ShowBank();
        var picked = _picked;
        _picked = null;
        _pointed = null;
        _trial = null;
        _hovered = null;
        _asked = default;
        Fill();
        _chipOffer?.Show(desk.ChipChoices);
        // What was picked stays picked as long as it is there.
        if (_cards.Find(c => c.Good == picked) is { } card)
        {
            _picked = card.Good;
            card.Picked = true;
        }
        ShowTrial(null);
        Recompose();
        Arrange();
        foreach (var slot in _sparks)
        {
            _traces.Surge(slot);
            if (_traces.End(slot) is { } at)
                _screen.Spark(at);
        }
        _sparks.Clear();
    }

    private void ShowBank()
    {
        _shownBank = desk.Bank;
        _bank.Text = desk.BankText;
        _choiceAsked = default;
        ShowStock();
    }

    /// <summary>The model anew, for a change of colours as well.</summary>
    public void Recompose()
    {
        _wanted = null;
        if (HeroRig.Compose(desk.Assets, desk.Look, _shown) is { } rig && rig != _screen.View?.Rig)
            _screen.Present(rig);
    }

    /// <summary>The slots, plate, values and model as they stand, or as <paramref name="trial"/> would have them.</summary>
    private void ShowTrial(Trial? trial)
    {
        _trial = trial;
        var then = trial?.Loadout;
        _values.Show(_build, then is null ? null : desk.Build(then));
        var chassis = then?.ChassisEntry ?? _shown.ChassisEntry;
        _plate.Chassis = HangarText.Name(chassis.Id).ToUpperInvariant();
        _plate.Facts = HangarText.Plate(chassis);
        _plate.Trial = trial?.Chassis == true;

        ChipsShown = (then ?? _shown).Chips;
        var modules = 0;
        foreach (var card in _slots)
        {
            ShowSlot(card, _shown, then, trial?.Chassis == true);
            if (card.Slot.Kind == WiredSlotKind.Module && card.Visible)
                modules++;
        }
        var cards = (modules, _slots.First(c => c.Slot == WiredSlot.Weapon(Mounting.Top)).Visible);
        var arrangeAgain = cards != _cardsShown;
        _cardsShown = cards;
        if (arrangeAgain)
            Arrange();

        _wanted = then ?? _shown;
        _settling = then is null ? 0.02f : Settle;
        ShowBanner();
    }

    private (int Modules, bool Top) _cardsShown;

    private static bool Exists(HeroLoadout loadout, WiredSlot slot) => slot.Kind switch
    {
        WiredSlotKind.Weapon => loadout.ChassisEntry.Slot((Mounting)slot.Index) is not null,
        WiredSlotKind.Module => slot.Index < loadout.Modules.Count,
        _ => true,
    };

    private static void ShowSlot(SlotCard card, HeroLoadout now, HeroLoadout? then, bool chassis)
    {
        var slot = card.Slot;
        var shown = then ?? now;
        var (had, has) = (Exists(now, slot), Exists(shown, slot));
        var (was, comes) = (now.At(slot), shown.At(slot));
        var mount = slot.Kind == WiredSlotKind.Weapon ? shown.ChassisEntry.Slot((Mounting)slot.Index) : null;
        card.Title = Heading(slot, mount).ToUpperInvariant();
        card.Fixed = mount?.Fixed;
        card.Absent = !has;
        card.Visible = slot switch
        {
            { Kind: WiredSlotKind.Weapon, Index: (int)Mounting.Top } => has || had,
            { Kind: WiredSlotKind.Module } => has || had,
            _ => true,
        };
        card.Item = comes ?? (was is not null && then is not null ? was : null);
        card.Trial = then is not null && comes is not null && (comes.Id != was?.Id || comes.Mk != was.Mk);
        (card.Note, card.NoteColour) = then is null ? (null, MenuTheme.Bright) : Fate(then, had, has, was, comes, now, chassis);
        if (slot.Kind != WiredSlotKind.Chip)
            return;
        card.Linked = WiredChips.Linked(shown.Chips, slot.Index);
        if (chassis)
            card.Note = null;
        if (card.Linked && card.Note is null && comes is not null)
            (card.Note, card.NoteColour) = (Lang.T("Linked"), HangarText.Family(comes) ?? MenuTheme.Glow);
    }

    private static string Heading(WiredSlot slot, ChassisSlot? mount)
    {
        if (slot.Kind == WiredSlotKind.Module)
            return Lang.T("Module {0}", slot.Index + 1);
        if (slot.Kind == WiredSlotKind.Ability)
            return Lang.T("Ability {0}", slot.Index + 1);
        if (slot.Kind == WiredSlotKind.Chip)
            return Lang.T("Socket {0}", slot.Index + 1);
        var side = HangarText.Side((Mounting)slot.Index);
        return mount is null ? side : $"{side} · {HangarText.Size(mount.Size)}";
    }

    /// <summary>What becomes of a slot in a build being tried: the user's question when changing chassis.</summary>
    private static (string? Note, Color Colour) Fate(HeroLoadout then, bool had, bool has, WiredItem? was, WiredItem? comes, HeroLoadout now, bool chassis)
    {
        var (good, bad, warn) = (MenuTheme.Glow, Red, MenuTheme.Bright);
        if (!had && has)
            return comes is null ? (Lang.T("New slot"), warn) : (Lang.T("Moves here"), good);
        if (had && !has)
            return was is null ? (Lang.T("Slot goes"), bad) : then.Holds(was.Id) ? (Lang.T("Moves"), warn) : (Lang.T("Falls off"), bad);
        if (!has)
            return (null, warn);
        if (comes is not null && comes.Id == was?.Id && comes.Mk != was.Mk)
            return ($"Mk {comes.Mk}", good);
        if (comes?.Id == was?.Id)
            return !chassis ? (null, warn) : was is null ? (Lang.T("Stays empty"), warn) : (Lang.T("Stays on"), good);
        if (comes is null)
            return then.Holds(was!.Id) ? (Lang.T("Moves"), warn) : (Lang.T("Comes off"), bad);
        if (was is not null)
            return (Lang.T("Replaces"), warn);
        return now.Holds(comes.Id) ? (Lang.T("Moves here"), good) : (Lang.T("New"), good);
    }

    private void ShowBanner()
    {
        var (text, colour) = (_refusal, Red);
        if (text is null && _under?.Fits == false)
            (text, colour) = (_under.Why, Red);
        if (text is null && _arrival is not null)
            (text, colour) = (_arrival, MenuTheme.Bright);
        if (text is null && _hovered is SlotCard { Slot.Kind: WiredSlotKind.Weapon, Item: not null, Absent: false } && _trial is null)
            (text, colour) = (Lang.T("Click: test fire. Right click: take off."), MenuTheme.Text);
        if (text is null && _trial is { Chassis: true } trial)
        {
            var worn = _shown.Worn.Where(i => i.Kind is WiredKind.Weapon or WiredKind.Module).ToList();
            var staying = worn.Count(i => trial.Loadout.Holds(i.Id));
            var then = trial.Loadout;
            var weaponSlots = then.ChassisEntry.Slots.Count(s => s.Fixed is null && then.Weapon(s.Mounting) is null);
            var empty = weaponSlots + then.Modules.Count(m => m is null);
            (text, colour) = (Lang.T("Stays on: {0} · Falls off: {1} · Empty slots: {2}", staying, worn.Count - staying, empty), MenuTheme.Bright);
        }
        if (text is null && _pointed is { Deal: HangarDeal.Upgrade } spare && !_shown.Holds(spare.Item.Id))
            (text, colour) = (Lang.T("The spare goes up a Mk where it lies: the hero stays as it is."), MenuTheme.Text);
        if (text is null && _trial is null && (_held ?? _picked ?? _pointed) is { Stays: false, Item.Kind: not (WiredKind.Chassis or WiredKind.Chip) } homeless && desk.BestSlot(homeless) is null)
            (text, colour) = (HangarText.Reason(desk.WhyNoSlot(homeless)), MenuTheme.Text);
        if (text is null && (_held ?? _picked ?? _pointed ?? (_hovered as SlotCard)?.Item.Good()) is { } good && Said(good) is { } said)
            (text, colour) = (said, MenuTheme.Text);
        Say(text, colour);
    }

    private (string? Text, Color Colour) _said;

    /// <summary>The line at the stage's foot: what is said over the hint, which comes back when nothing is. It takes the size at which the box shows all of it.</summary>
    private void Say(string? text, Color colour)
    {
        _said = (text, colour);
        _banner.Visible = text is not null;
        _hint.Visible = text is null;
        _banner.AddThemeColorOverride("font_color", colour);
        foreach (var size in BannerSizes)
        {
            _banner.AddThemeFontSizeOverride("font_size", size);
            _banner.Text = text ?? "";
            // A list that does not fit on a line breaks with no dot left hanging.
            if (_banner.GetLineCount() > 1)
                _banner.Text = _banner.Text.Replace(" · ", "   ");
            if (_banner.GetVisibleLineCount() >= _banner.GetLineCount())
                break;
        }
    }

    // ---- the mouse --------------------------------------------------------------------------------

    /// <summary>Where the mouse is by its last event, in the window: unlike the system's cursor that holds for scripted input too.</summary>
    public Vector2 Mouse { get; private set; }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouse mouse)
            Mouse = mouse.GlobalPosition;
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            return;
        _mouseDown = button.Pressed;
        // A drag ends where the button comes up, whatever became of the card it began on.
        if (!button.Pressed && _held is { } good)
        {
            _pressed = null;
            EndDrag(good);
        }
    }

    public override void _Process(double delta)
    {
        if (!_mouseDown)
            _pressed = null;
        // The cards are made anew at a change, so that waits while the mouse holds one: the bank may tick on all the while.
        if ((!ReferenceEquals(desk.Loadout, _shown) || desk.Stamp != _stamp) && !Dragging && _pressed is null)
            Refresh();
        else if (desk.Bank != _shownBank)
            ShowBank();
        ShowChoice();
        var mouse = Mouse;
        if (_ghost is not null)
            _ghost.Position = _floating.GetGlobalTransform().AffineInverse() * mouse - _ghost.Size / 2;
        _under = _slots.FirstOrDefault(c => c.Visible && c.GetGlobalRect().HasPoint(mouse));
        _overPlate = _plate.GetGlobalRect().HasPoint(mouse);
        foreach (var card in _slots)
            card.Pointed = card == _under;
        _plate.Pointed = _overPlate;

        if (_refusal is not null && (_refusalLeft -= (float)delta) <= 0)
        {
            _refusal = null;
            ShowBanner();
        }
        if (_arrival is not null && (_arrivalLeft -= (float)delta) <= 0)
        {
            _arrival = null;
            ShowBanner();
        }
        var carried = _held ?? _picked;
        var asked = (carried ?? _pointed, _under?.Slot, _overPlate, _hovered, (HeroLoadout?)_shown);
        if (asked != _asked)
        {
            _asked = asked;
            ShowFits(carried);
            ShowTrial(Tried(carried));
        }
        if (_wanted is { } wanted && (_settling -= (float)delta) <= 0)
        {
            _wanted = null;
            if (HeroRig.Compose(desk.Assets, desk.Look, wanted) is { } rig && rig != _screen.View?.Rig)
                _screen.Present(rig);
        }
    }

    /// <summary>What the slots say to what the mouse carries.</summary>
    private void ShowFits(HangarGood? carried)
    {
        foreach (var card in _slots)
        {
            var why = carried is null || carried.Item.Kind == WiredKind.Chassis || !card.Visible ? null : desk.WhyNot(carried, card.Slot);
            card.Fits = carried is null || carried.Item.Kind == WiredKind.Chassis ? null : why is null;
            card.Why = why is null ? null : HangarText.Reason(why);
        }
        _plate.Fits = carried?.Item.Kind == WiredKind.Chassis ? desk.WhyNotWear(carried) is null : null;
    }

    /// <summary>What the mouse is asking about, as a build.</summary>
    private Trial? Tried(HangarGood? carried)
    {
        if (carried is not null)
        {
            if (carried.Item.Kind == WiredKind.Chassis)
                return new Trial(desk.Trying(carried), true);
            if (_under?.Fits == true)
                return new Trial(desk.Trying(carried, _under.Slot), false);
            return Dragging ? null : Tried(null);
        }
        if (_pointed is { } good)
        {
            if (good.Item.Kind == WiredKind.Chassis)
                return new Trial(desk.Trying(good), true);
            if (good.Stays)
                return good.Deal == HangarDeal.Upgrade && _shown.Holds(good.Item.Id) ? new Trial(desk.Upgraded(good.Item), false) : null;
            // Before a run there is no board to show a chip on: looked at, it changes nothing.
            return (good.Item.Kind != WiredKind.Chip || _board) && desk.BestSlot(good) is { } slot ? new Trial(desk.Trying(good, slot), false) : null;
        }
        return null;
    }

    /// <summary>The mouse came onto or left a card.</summary>
    public void Hover(HangarCard card, bool on)
    {
        if (Dragging)
            return;
        if (on)
        {
            _hovered = card;
            _pointed = (card as ItemCard)?.Good;
            _refusal = null;
            if (card is not ChassisPlate)
                MenuSounds.Gui("listhigh.wav");
        }
        else if (_hovered == card)
        {
            _hovered = null;
            _pointed = null;
        }
    }

    public void Press(HangarCard card, bool twice)
    {
        (_pressed, _pressedAt) = (card, Mouse);
        // A click on the tree acts at once, so a second one has nothing left to do, nor has one after a deal, whatever card it left here.
        if (twice && !JustDealt && card is ItemCard { Good: { Stays: false } good } item and not TreeCard && !item.OnDeal(Mouse))
        {
            _pressed = null;
            _picked = null;
            Quick(good);
        }
    }

    public void Moved(HangarCard card)
    {
        if (_pressed != card || Dragging || Mouse.DistanceTo(_pressedAt) < DragStart * GetGlobalTransform().Scale.Y)
            return;
        var good = card switch
        {
            // A slot with something picked waits for the click that puts it there: a hand that slips is no drag.
            SlotCard when _picked is not null => null,
            ItemCard item => item.Good,
            // What the card shows may only be tried: it is what the hero wears that comes away.
            SlotCard { Absent: false, Slot: var slot } => _shown.At(slot).Good(),
            _ => null,
        };
        if (good is null)
            return;
        if (good.Locked is { } locked)
        {
            Refuse(locked);
            _pressed = null;
            return;
        }
        if (good.Stays)
            return;
        _held = good;
        _picked = null;
        foreach (var other in _cards)
            other.Picked = false;
        _ghost = new ItemCard { Good = good, Ghost = true, Size = card is ItemCard ? card.Size : new Vector2(230, 96), Modulate = new Color(1, 1, 1, 0.92f) };
        _floating.AddChild(_ghost);
        MenuSounds.Gui("listclik.wav");
    }

    public void Release(HangarCard card)
    {
        var pressed = _pressed;
        _pressed = null;
        if (_held is { } good)
        {
            EndDrag(good);
            return;
        }
        // A hand that has left the card has changed its mind.
        if (pressed == card && card.GetGlobalRect().HasPoint(Mouse))
            Click(card);
    }

    private void EndDrag(HangarGood good)
    {
        var mouse = Mouse;
        var slot = _slots.FirstOrDefault(c => c.Visible && c.GetGlobalRect().HasPoint(mouse));
        _held = null;
        _ghost?.QueueFree();
        _ghost = null;
        if (good.Item.Kind == WiredKind.Chassis)
        {
            if (_plate.GetGlobalRect().HasPoint(mouse))
                Wear(good);
            return;
        }
        if (slot is not null)
            Put(good, slot.Slot);
        else if (_inventory.GetGlobalRect().HasPoint(mouse) && _shown.Holds(good.Item.Id))
            TakeOff(good.Item);
    }

    private void Click(HangarCard card)
    {
        // The second half of a double click: the first may have left another card under the mouse.
        if (card is ItemCard or SquadCard && JustDealt)
            return;
        switch (card)
        {
            case SquadCard squad:
                Recruit(squad);
                break;
            case ItemCard { Good: var good } item:
                if (good.Locked is { } locked)
                {
                    Refuse(locked);
                    return;
                }
                if (item.OnDeal(Mouse))
                {
                    Deal(good);
                    return;
                }
                if (card is TreeCard)
                {
                    WearFromTree(good);
                    return;
                }
                if (good.Stays)
                    return;
                _picked = _picked == good ? null : good;
                foreach (var other in _cards)
                    other.Picked = other == item && _picked is not null;
                MenuSounds.Gui("listclik.wav");
                break;
            case SlotCard slot when _picked is { } carried && carried.Item.Kind != WiredKind.Chassis:
                Put(carried, slot.Slot);
                break;
            case SlotCard { Slot.Kind: WiredSlotKind.Weapon } slot:
                TestFire((Mounting)slot.Slot.Index);
                break;
            case ChassisPlate when _picked is { Item.Kind: WiredKind.Chassis } chassis:
                Wear(chassis);
                break;
        }
    }

    public void RightClick(HangarCard card)
    {
        if (Dragging)
            return;
        switch (card)
        {
            case SlotCard when _picked is not null:
                Unpick();
                break;
            // What the card shows may only be tried: it is what the hero wears that comes off.
            case SlotCard { Absent: false, Slot: var slot } when _shown.At(slot) is { } worn:
                TakeOff(worn);
                break;
            case ItemCard { Picked: true }:
                _picked = null;
                ((ItemCard)card).Picked = false;
                break;
            case ItemCard { Good.Stays: true }:
                Refuse("that is done in the inventory");
                break;
            case ItemCard { Good: var good } when desk.IsSpare(good) && !JustDealt:
                GiveBack(good);
                break;
        }
    }

    private void GiveBack(HangarGood good)
    {
        Dealing();
        if (desk.GiveBack(good) is { } why)
            Refuse(why);
        else
            MenuSounds.Gui(SellSound);
    }

    private void Unpick()
    {
        _picked = null;
        foreach (var card in _cards)
            card.Picked = false;
    }

    /// <summary>A double click: into the first slot that takes it, or onto the hero if it is a chassis.</summary>
    private void Quick(HangarGood good)
    {
        if (good.Item.Kind == WiredKind.Chassis)
            Wear(good);
        else if (desk.BestSlot(good) is { } slot)
            Put(good, slot);
        else
            Refuse(good.Locked ?? desk.WhyNoSlot(good));
    }

    /// <summary>What a shelf holds of the things that go on the hero.</summary>
    private static HangarShelf Shelf(WiredKind kind) => kind switch
    {
        WiredKind.Weapon => HangarShelf.Weapons,
        WiredKind.Module => HangarShelf.Modules,
        WiredKind.Ability => HangarShelf.Abilities,
        WiredKind.Chip => HangarShelf.Chips,
        _ => HangarShelf.Chassis,
    };

    private void Put(HangarGood good, WiredSlot slot)
    {
        if (desk.Put(good, slot) is { } why)
        {
            Refuse(why);
            return;
        }
        if (good.Price is not null)
            Bought();
        Unpick();
        _sparks.Add(slot);
        MenuSounds.Gui("weapchng.wav");
        MenuSounds.Effect("SID_CONSTRUCTING");
    }

    private void TakeOff(WiredItem item)
    {
        if (desk.TakeOff(item) is { } why)
        {
            Refuse(why);
            return;
        }
        MenuSounds.Gui("ctrlpoff.wav");
    }

    private void Wear(HangarGood chassis)
    {
        if (desk.Wear(chassis) is { } why)
        {
            Refuse(why);
            return;
        }
        _sparks.AddRange(_slots.Where(c => c.Slot.Kind == WiredSlotKind.Weapon && c.Visible).Select(c => c.Slot));
        MenuSounds.Gui("ctrlpon.wav");
        MenuSounds.Effect("SID_CONSTRUCTING");
    }

    /// <summary>Says why not, in red, with the bad click.</summary>
    public void Refuse(string why)
    {
        _refusal = HangarText.Reason(why);
        _refusalLeft = RefusalTime;
        MenuSounds.Gui("clickbad.wav");
        ShowBanner();
    }

    /// <summary>Everything on the hero fires.</summary>
    private void TestFire()
    {
        foreach (var weapon in _build.Weapons)
            _screen.Fire(weapon.Mounting, weapon.Spec);
    }

    /// <summary>One mount fires, if there is something on it.</summary>
    private void TestFire(Mounting mounting)
    {
        if (_build.Weapons.FirstOrDefault(w => w.Mounting == mounting) is { } weapon)
            _screen.Fire(mounting, weapon.Spec);
    }

    public override void Back()
    {
        if (Dragging)
        {
            _held = null;
            _ghost?.QueueFree();
            _ghost = null;
            return;
        }
        base.Back();
    }
}
