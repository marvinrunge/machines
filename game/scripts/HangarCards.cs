using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>What the hangar's cards say about an item: its name and what it weighs and draws, in the player's language.</summary>
public static partial class HangarText
{
    /// <summary>The name of anything in the catalogues.</summary>
    public static string Name(string catalogue)
    {
        if (WiredCatalogue.FindChassis(catalogue) is { } chassis)
            return $"{Lang.T(Studio.Names.GetValueOrDefault(chassis.SubType, chassis.SubType))} {chassis.Level}";
        if (WiredCatalogue.FindWeapon(catalogue) is not null)
            return Lang.T(Studio.Title(catalogue));
        if (WiredCatalogue.FindModule(catalogue) is { } module)
            return Word(module.Name);
        if (WiredCatalogue.FindAbility(catalogue) is { } ability)
            return Lang.T(ability.Name);
        return WiredChips.Find(catalogue) is { } chip ? Lang.T(chip.Name) : catalogue;
    }

    /// <summary>A word that means something else here than in the menus, where Load is a button: de.po has it under the context hangar.</summary>
    public static string Word(string text) => (string)TranslationServer.Translate(text, "hangar") is var word && word != text ? word : Lang.T(text);

    public static string Size(SlotSize size) => Lang.T(size switch
    {
        SlotSize.Light => "Light",
        SlotSize.Medium => "Medium",
        _ => "Heavy",
    });

    /// <summary>Which side of the hero a mounting is on.</summary>
    public static string Side(Mounting mounting) => Lang.T(mounting switch
    {
        Mounting.Left => "Left",
        Mounting.Right => "Right",
        _ => "Top",
    });

    /// <summary>The second line of a card: Mk and size, or what a chassis offers.</summary>
    public static string Kind(WiredItem item)
    {
        if (WiredCatalogue.FindChassis(item.Catalogue) is { } chassis)
            return Lang.T("Slots {0} + {1}", chassis.Slots.Count(s => s.Fixed is null), chassis.Modules);
        if (WiredCatalogue.FindWeapon(item.Catalogue) is { } weapon)
            return $"Mk {item.Mk} · {Size(weapon.Size)}";
        return item.Kind == WiredKind.Chip ? Family(item.Catalogue) : $"Mk {item.Mk}";
    }

    private static string Family(string chip) => WiredChips.Find(chip)?.Family.ToString() is { } family ? Lang.T(family) : "";

    /// <summary>The third line: what it loads and draws, or what a chassis carries and supplies.</summary>
    public static string Budget(WiredItem item)
    {
        var (load, power) = WiredCatalogue.FindChassis(item.Catalogue) is { } chassis ? (chassis.Load, chassis.Power)
            : WiredCatalogue.FindWeapon(item.Catalogue) is { } weapon ? (weapon.Load, weapon.Power)
            : WiredCatalogue.FindModule(item.Catalogue) is { } module ? (module.Load, module.Power)
            : (0, WiredCatalogue.FindAbility(item.Catalogue)?.Power ?? 0);
        return item.Kind == WiredKind.Chip ? "" : Lang.T("Load {0} · Power {1}", load, power);
    }

    /// <summary>A chassis in full, for its plate.</summary>
    public static string Plate(WiredChassis chassis) => Lang.T("Weapons: {0} · Modules: {1} · Carries {2} · Supplies {3}",
        chassis.Slots.Count(s => s.Fixed is null), chassis.Modules, chassis.Load, chassis.Power);

    /// <summary>What a run opened, by its id in the profile.</summary>
    public static string Opened(string id) => id == WiredProfile.Hardcore ? Lang.T("Hardcore") : WiredProfile.Arenas.Any(a => a.Scenario == id) ? Arena(id) : Name(id);

    /// <summary>An arena's name in the player's language: de.po has it under the context arena, as the skirmish pages stay with the original's.</summary>
    public static string Arena(string scenario) => WiredProfile.Arenas.FirstOrDefault(a => a.Scenario == scenario) is { } arena ? TranslationServer.Translate(arena.Name, "arena") : scenario;

    /// <summary>The little gold plate of what has only just come: its right end at a point, in the middle of its height, as far as it has come up.</summary>
    public static void Tag(CanvasItem item, Vector2 rightMiddle, string text, float shown = 1)
    {
        const int size = 14;
        var word = text.ToUpperInvariant();
        var width = MenuTheme.Font.GetStringSize(word, HorizontalAlignment.Left, -1, size).X + 12;
        var plate = new Rect2(rightMiddle - new Vector2(width, 10), new Vector2(width, 20));
        item.DrawRect(plate, new Color(MenuTheme.Bright, shown));
        item.DrawString(MenuTheme.Font, plate.Position + new Vector2(6, 15), word, HorizontalAlignment.Left, -1, size, new Color(MenuTheme.OnLight, shown));
    }

    /// <summary>A reason from the sim, which writes its own in small letters, as a sentence. The way to a chassis it says by the catalogue's names.</summary>
    public static string Reason(string why)
    {
        var text = why.StartsWith("needs ") && why.EndsWith(" first")
            ? Lang.T("needs {0} first", string.Join($" {Lang.T("or")} ", why[6..^6].Split(" or ").Select(Name)))
            : Lang.T(why);
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Draws a line, smaller if it would not fit, and narrower if even that will not do: Mehrfachwerfer 3 must keep its number.</summary>
    public static void Line(CanvasItem item, Vector2 at, string text, float width, int size, Color colour, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var font = MenuTheme.Font;
        float Wide() => font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        while (size > 13 && Wide() > width)
            size--;
        if (Wide() <= width)
        {
            item.DrawString(font, at, text, align, width, size, colour);
            return;
        }
        item.DrawSetTransform(at, 0, new Vector2(width / Wide(), 1));
        item.DrawString(font, Vector2.Zero, text, HorizontalAlignment.Left, -1, size, colour);
        item.DrawSetTransform(Vector2.Zero);
    }
}

/// <summary>
/// A card of the hangar, a small chip on the board, drawn entirely by itself as the buttons are. Everything that is done
/// to it goes to <see cref="HangarPage"/>, which holds what is being dragged and tried out.
/// </summary>
public abstract partial class HangarCard : Control
{
    private float _light;

    /// <summary>Set before it enters the tree.</summary>
    public HangarPage? Page { get; init; }

    protected float Light => _light;

    /// <summary>A brighter frame while something wants attention on the card.</summary>
    protected virtual bool Lit => false;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        MouseFilter = MouseFilterEnum.Pass;
        MouseEntered += () => Page?.Hover(this, true);
        MouseExited += () => Page?.Hover(this, false);
    }

    public override void _Process(double delta)
    {
        var target = Lit ? 1f : 0f;
        _light = Mathf.MoveToward(_light, target, (float)delta * (target > _light ? 12 : 4));
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (Page is null)
            return;
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                Page.Press(this, press.DoubleClick);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                Page.Release(this);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                Page.RightClick(this);
                AcceptEvent();
                break;
            case InputEventMouseMotion:
                Page.Moved(this);
                break;
        }
    }

    /// <summary>
    /// An item's picture with its name, Mk and budget beside it, closer together on a low card. On a narrow card the words
    /// get all the width, and with <paramref name="note"/> that takes the place of the budget. Narrower still there is no room
    /// for the budget, which the banner gives anyway, and a name of two words takes its line.
    /// </summary>
    protected void DrawItem(WiredItem item, Rect2 inner, Color name, float alpha = 1, string? note = null, Color noteColour = default)
    {
        var (x, width) = (inner.Position.X + 4, inner.Size.X - 6);
        if (inner.Size.X >= Narrow)
        {
            var size = Math.Min(inner.Size.Y - 4, 58);
            var icon = new Rect2(inner.Position + new Vector2(2, (inner.Size.Y - size) / 2), new Vector2(size, size));
            HangarIcons.Draw(this, item, icon, new Color(1, 1, 1, alpha));
            (x, width) = (icon.End.X + 7, inner.End.X - icon.End.X - 8);
        }
        var low = inner.Size.Y < 56;
        var (top, first, lines) = (inner.Position.Y, low ? 15 : 21, low ? new[] { 29, 43 } : [41, 59]);
        var dim = new Color(MenuTheme.Text, alpha * 0.85f);
        var (title, small) = (HangarText.Name(item.Catalogue), low ? 14 : 15);
        var second = title.LastIndexOf(' ') is > 0 and var gap && width < Tight ? title[(gap + 1)..] : null;
        HangarText.Line(this, new Vector2(x, top + first), second is null ? title : title[..title.LastIndexOf(' ')], width, low ? 17 : 20, new Color(name, alpha));
        if (second is not null)
            HangarText.Line(this, new Vector2(x, top + lines[0]), second, width, low ? 17 : 20, new Color(name, alpha));
        HangarText.Line(this, new Vector2(x, top + lines[second is null ? 0 : 1]), HangarText.Kind(item), width, small, dim);
        if (note is not null)
            HangarText.Line(this, new Vector2(x, top + lines[1]), note, width, small, noteColour);
        else if (width >= Tight)
            HangarText.Line(this, new Vector2(x, top + lines[1]), HangarText.Budget(item), width, small, new Color(MenuTheme.Dim.Lightened(0.25f), alpha));
    }

    /// <summary>Below this width of card its words need the room of the picture.</summary>
    protected const float Narrow = 150;

    /// <summary>Below this width for its words a card has room for no more than its name and Mk.</summary>
    private const float Tight = 80;
}

/// <summary>A card of the inventory: one good, picked up by dragging or a click.</summary>
public partial class ItemCard : HangarCard
{
    public const float ArtScale = 0.22f;

    /// <summary>The height of the strip that offers the card's deal.</summary>
    public const float Strip = 26;

    public required HangarGood Good { get; init; }

    /// <summary>A click has picked it, to be put with a click on a slot.</summary>
    public bool Picked { get; set; }

    /// <summary>It follows the mouse and takes no input.</summary>
    public bool Ghost { get; init; }

    protected override bool Lit => Picked || Ghost || IsHovered();

    private bool IsHovered() => Page is { Dragging: false } page && GetGlobalRect().HasPoint(page.Mouse);

    /// <summary>Whether a click at a point of the window is a click on the deal and not on the good.</summary>
    public virtual bool OnDeal(Vector2 at) => Good.Deal == HangarDeal.Choose || Good.Deal != HangarDeal.None && (GetGlobalTransform().AffineInverse() * at).Y >= Size.Y - Strip - ChipArt.FrameWidth(ArtScale) - 3;

    /// <summary>What the deal comes to.</summary>
    protected int DealPrice => Good.Deal == HangarDeal.Buy ? Good.Price ?? 0 : Good.Amount;

    /// <summary>Whether the bank does not cover the deal.</summary>
    protected bool TooDear => Good.Deal is HangarDeal.Buy or HangarDeal.Upgrade && DealPrice > (Page?.Bank ?? int.MaxValue);

    public override void _Ready()
    {
        base._Ready();
        if (Ghost)
            MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var body = new Rect2(Vector2.Zero, Size);
        ChipArt.Draw(this, body, ArtScale, Light * 0.45f, dim: Good.Locked is not null);
        var inner = body.Grow(-ChipArt.FrameWidth(ArtScale) - 3);
        var below = Good.Deal != HangarDeal.None ? Strip : Good.Price is null && Good.Locked is null ? 0 : 20;
        DrawItem(Good.Item, new Rect2(inner.Position, inner.Size - new Vector2(0, below)),
            Good.Locked is null ? MenuTheme.Bright : MenuTheme.Dim.Lightened(0.35f), Good.Locked is null ? 1 : 0.75f);
        var baseline = inner.End.Y - 3;
        if (Good.Deal != HangarDeal.None)
            DrawDeal(new Rect2(inner.End - new Vector2(inner.Size.X, Strip), new Vector2(inner.Size.X, Strip)));
        else if (Good.Locked is { } locked)
            HangarText.Line(this, new Vector2(inner.Position.X + 4, baseline), HangarText.Reason(locked), inner.Size.X - 8, 15, MenuTheme.Dim.Lightened(0.2f));
        else if (Good.Price is { } price)
            HangarText.Line(this, new Vector2(inner.Position.X + 4, baseline), $"{price} BMU", inner.Size.X - 8, 17, price > (Page?.Bank ?? int.MaxValue) ? HangarPage.Red : MenuTheme.Glow, HorizontalAlignment.Right);
        if (HangarText.Family(Good.Item) is { } family)
            DrawRect(inner.Grow(1), new Color(family, 0.7f), false, 2);
        if (Picked)
            DrawRect(inner.Grow(1), MenuTheme.Glow, false, 2);
        DrawFresh(inner);
    }

    /// <summary>What a run has only just opened says so in its corner.</summary>
    protected void DrawFresh(Rect2 inner)
    {
        if (!Good.Fresh)
            return;
        DrawRect(inner.Grow(1), MenuTheme.Bright, false, 2);
        HangarText.Tag(this, inner.Position + new Vector2(inner.Size.X - 3, -4), Lang.T("New"));
    }

    /// <summary>The strip: what it does on the left and what it comes to on the right, in red if the bank does not cover it; if it cannot be done, why not.</summary>
    private void DrawDeal(Rect2 strip)
    {
        var (baseline, width) = (strip.Position + new Vector2(6, strip.Size.Y - 7), strip.Size.X - 12);
        if (Good.Locked is { } locked)
        {
            var dim = MenuTheme.Dim.Lightened(0.3f);
            DrawRect(strip, new Color(dim, 0.13f));
            var price = $"{DealPrice} BMU";
            var priceWidth = Math.Min(MenuTheme.Font.GetStringSize(price, HorizontalAlignment.Left, -1, 17).X, width * 0.45f);
            HangarText.Line(this, baseline, HangarText.Reason(locked), width - priceWidth - 6, 15, dim);
            HangarText.Line(this, baseline + new Vector2(width - priceWidth, 0), price, priceWidth, 17, dim, HorizontalAlignment.Right);
            return;
        }
        var colour = TooDear ? HangarPage.Red : Good.Deal == HangarDeal.Sell ? MenuTheme.Bright : MenuTheme.Glow;
        var over = Page is { Dragging: false } page && GetGlobalRect().HasPoint(page.Mouse) && OnDeal(page.Mouse);
        DrawRect(strip, new Color(colour, over ? 0.34f : 0.13f));
        var word = Good.Deal switch
        {
            HangarDeal.Buy => Lang.T("Buy"),
            HangarDeal.Sell => Lang.T("Sell"),
            HangarDeal.Upgrade => $"Mk {Good.Item.Mk + 1}",
            _ => Lang.T("Take"),
        };
        HangarText.Line(this, baseline, word, width * 0.42f, 17, colour);
        if (Good.Deal != HangarDeal.Choose)
            HangarText.Line(this, baseline + new Vector2(width * 0.42f, 0), $"{DealPrice} BMU", width * 0.58f, 17, colour, HorizontalAlignment.Right);
    }
}

/// <summary>
/// A chassis on the shop's tree: where it stands on the way and what it costs, at a glance. A click is its deal, or a
/// change to it if it is owned; pointing at it shows the hero on it as any chassis does.
/// </summary>
public partial class TreeCard : ItemCard
{
    public override bool OnDeal(Vector2 at) => Good.Deal == HangarDeal.Buy;

    public override void _Draw()
    {
        var body = new Rect2(Vector2.Zero, Size);
        var step = Good.Step ?? ChassisStep.Closed;
        ChipArt.Draw(this, body, ArtScale, Light * 0.45f, dim: step == ChassisStep.Closed);
        var inner = body.Grow(-ChipArt.FrameWidth(ArtScale) - 2);
        var (frame, word) = step switch
        {
            ChassisStep.Worn => (MenuTheme.Glow, Lang.T("Worn")),
            ChassisStep.Owned => (MenuTheme.Text, Lang.T(Good.Deal == HangarDeal.None && Good.Price is null && Page?.Desk.Shop is null ? "Open" : "Owned")),
            ChassisStep.ForSale or ChassisStep.TooDear when Good.Locked is not null => (MenuTheme.Dim.Lightened(0.3f), Lang.T("Inventory full")),
            ChassisStep.ForSale => (MenuTheme.Bright, $"{Good.Price} BMU"),
            ChassisStep.TooDear => (HangarPage.Red, $"{Good.Price} BMU"),
            _ => (MenuTheme.Dim, $"{Good.Amount} BMU"),
        };
        var alpha = step == ChassisStep.Closed ? 0.6f : 1;
        DrawRect(inner.Grow(1), new Color(frame, step == ChassisStep.Closed ? 0.25f : 0.8f), false, step == ChassisStep.Worn ? 3 : 1.5f);
        var size = Math.Min(inner.Size.Y - 2, 46);
        var x = inner.Position.X + 3;
        if (inner.Size.X >= 104)
        {
            var icon = new Rect2(new Vector2(x, inner.Position.Y + (inner.Size.Y - size) / 2), new Vector2(size, size));
            HangarIcons.Draw(this, Good.Item, icon, new Color(1, 1, 1, alpha));
            x = icon.End.X + 5;
        }
        var width = inner.End.X - x - 3;
        var name = step == ChassisStep.Closed ? MenuTheme.Dim.Lightened(0.35f) : step == ChassisStep.Worn ? MenuTheme.Glow : MenuTheme.Bright;
        var top = inner.Position.Y + (inner.Size.Y - 46) / 2;
        HangarText.Line(this, new Vector2(x, top + 15), HangarText.Name(Good.Item.Catalogue), width, 17, name);
        HangarText.Line(this, new Vector2(x, top + 30), HangarText.Kind(Good.Item), width, 13, new Color(MenuTheme.Text, alpha * 0.85f));
        HangarText.Line(this, new Vector2(x, top + 45), word, width, 15, frame);
        DrawFresh(inner);
    }
}

/// <summary>An offer already bought: a gap on the shelf, until the next wave's stock fills it.</summary>
public partial class SoldCard : Control
{
    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Draw()
    {
        var body = new Rect2(Vector2.Zero, Size);
        DrawRect(body, new Color(MenuTheme.Edge, 0.5f), false, 1);
        DrawString(MenuTheme.Font, new Vector2(0, Size.Y / 2 + 7), Lang.T("Sold").ToUpperInvariant(), HorizontalAlignment.Center, Size.X, 19, MenuTheme.Dim);
    }
}

/// <summary>An escort for sale: the machine that comes, what it carries and at the foot its price, or why it cannot be had. A click recruits it.</summary>
public partial class SquadCard : HangarCard
{
    private const float ArtScale = 0.22f;

    /// <summary>The choice on the squad list, which the price and the command go by.</summary>
    public required ResearchKey Machine { get; init; }

    /// <summary>What is delivered for it: with Veterans a better one.</summary>
    public required ResearchKey Arrives { get; init; }

    public required int Price { get; init; }

    /// <summary>Why it cannot be had, or null.</summary>
    public required string? Why { get; init; }

    protected override bool Lit => Page is { Dragging: false } page && GetGlobalRect().HasPoint(page.Mouse);

    public override void _Draw()
    {
        var body = new Rect2(Vector2.Zero, Size);
        ChipArt.Draw(this, body, ArtScale, Light * 0.45f, dim: Why is not null);
        var inner = body.Grow(-ChipArt.FrameWidth(ArtScale) - 3);
        var above = inner.Size.Y - ItemCard.Strip;
        var size = Math.Min(above - 4, 58);
        var icon = new Rect2(inner.Position + new Vector2(2, (above - size) / 2), new Vector2(size, size));
        HangarIcons.Draw(this, Arrives, icon, new Color(1, 1, 1, Why is null ? 1 : 0.7f));
        var (x, width) = (icon.End.X + 7, inner.End.X - icon.End.X - 8);
        var top = inner.Position.Y;
        HangarText.Line(this, new Vector2(x, top + 21), HangarText.Machine(Arrives), width, 20, Why is null ? MenuTheme.Bright : MenuTheme.Text);
        var y = top + 40;
        foreach (var weapon in HangarText.Weapons(Arrives).Take(2))
        {
            HangarText.Line(this, new Vector2(x, y), weapon, width, 15, MenuTheme.Text);
            y += 18;
        }
        DrawStrip(new Rect2(inner.End - new Vector2(inner.Size.X, ItemCard.Strip), new Vector2(inner.Size.X, ItemCard.Strip)));
    }

    /// <summary>What the card offers: to buy at the price, or in plain words why not, the price in red if that is what is missing.</summary>
    private void DrawStrip(Rect2 strip)
    {
        var dear = Price > (Page?.Bank ?? int.MaxValue);
        var colour = Why is null ? MenuTheme.Glow : dear ? HangarPage.Red : MenuTheme.Dim.Lightened(0.3f);
        var over = Why is null && Page is { Dragging: false } page && GetGlobalRect().HasPoint(page.Mouse);
        DrawRect(strip, new Color(colour, over ? 0.34f : 0.13f));
        var (baseline, width) = (strip.Position + new Vector2(6, strip.Size.Y - 7), strip.Size.X - 12);
        var price = $"{Price} BMU";
        var priceWidth = Math.Min(MenuTheme.Font.GetStringSize(price, HorizontalAlignment.Left, -1, 17).X, width * 0.45f);
        HangarText.Line(this, baseline, Why is null ? Lang.T("Buy") : HangarText.Reason(Why), width - priceWidth - 6, 15, colour);
        HangarText.Line(this, baseline + new Vector2(width - priceWidth, 0), price, priceWidth, 17, colour, HorizontalAlignment.Right);
    }
}

/// <summary>
/// A slot of the hero, with a trace on the screen running to its place on the model. Empty it pulses, under something
/// being dragged it turns green or red, and while another build is tried out it says what becomes of it.
/// </summary>
public partial class SlotCard : HangarCard
{
    public const float ArtScale = 0.22f;

    private float _time;

    public required WiredSlot Slot { get; init; }

    /// <summary>Which word heads the card.</summary>
    public required string Title { get; set; }

    /// <summary>What sits here, or would.</summary>
    public WiredItem? Item { get; set; }

    /// <summary>A weapon that is part of the chassis.</summary>
    public string? Fixed { get; set; }

    /// <summary>The chassis has no such slot.</summary>
    public bool Absent { get; set; }

    /// <summary>What the slot says of something held over it; null with nothing held.</summary>
    public bool? Fits { get; set; }

    /// <summary>What becomes of it in a build being tried.</summary>
    public string? Note { get; set; }

    /// <summary>Why it will not take what is held over it.</summary>
    public string? Why { get; set; }

    public Color NoteColour { get; set; } = MenuTheme.Bright;

    /// <summary>Under the mouse.</summary>
    public bool Pointed { get; set; }

    /// <summary>The item is only tried out.</summary>
    public bool Trial { get; set; }

    /// <summary>A trace joins the chip in this socket to one of its family.</summary>
    public bool Linked { get; set; }

    public bool Empty => Item is null && Fixed is null && !Absent;

    protected override bool Lit => Pointed && !Absent;

    public override void _Process(double delta)
    {
        _time += (float)delta;
        base._Process(delta);
    }

    /// <summary>The pulse of an empty slot, 0 to 1.</summary>
    public float Pulse => 0.5f + 0.5f * Mathf.Sin(_time * 3.4f);

    public override void _Draw()
    {
        var body = new Rect2(Vector2.Zero, Size);
        var inner = body.Grow(-ChipArt.FrameWidth(ArtScale) - 3);
        ChipArt.Draw(this, body, ArtScale, Light * 0.4f, dim: Absent);
        if (Fits is { } fits)
        {
            var tint = fits ? MenuTheme.Glow : HangarPage.Red;
            DrawRect(inner, new Color(tint, fits ? 0.16f : 0.34f));
            DrawRect(inner.Grow(1), new Color(tint, 0.9f), false, 2);
        }
        else if (Empty)
        {
            DrawRect(inner.Grow(1), new Color(MenuTheme.Bright, 0.25f + 0.6f * Pulse), false, 2);
            DrawRect(inner, new Color(MenuTheme.Bright, 0.05f + 0.07f * Pulse));
        }

        var head = new Vector2(inner.Position.X + 4, inner.Position.Y + 14);
        var note = Fits == false ? Lang.T("No fit") : Note;
        var noteColour = Fits == false ? HangarPage.Red : NoteColour;
        // On a narrow card a note under the item is better than two halves of a line, if there is an item.
        var below = note is not null && Item is not null && inner.Size.X < Narrow;
        var beside = below ? null : note;
        var noteWidth = beside is null ? 0 : Math.Min(MenuTheme.Font.GetStringSize(beside, HorizontalAlignment.Left, -1, 14).X, inner.Size.X * 0.58f - 4);
        HangarText.Line(this, head, Title, inner.Size.X - noteWidth - 16, 13, Absent ? MenuTheme.Dim : MenuTheme.Dim.Lightened(0.4f));
        if (beside is not null)
            HangarText.Line(this, head + new Vector2(inner.Size.X * 0.42f - 4, 0), beside, inner.Size.X * 0.58f - 4, 14, noteColour, HorizontalAlignment.Right);
        var rest = new Rect2(inner.Position + new Vector2(0, 16), inner.Size - new Vector2(0, 16));
        if (Item is { } item && HangarText.Family(item) is { } family)
        {
            DrawRect(inner.Grow(1), new Color(family, Linked ? 0.75f + 0.25f * Pulse : 0.55f), false, Linked ? 3 : 2);
            if (Linked)
                DrawRect(inner, new Color(family, 0.06f + 0.06f * Pulse));
        }
        if (Item is { } worn)
            DrawItem(worn, rest, Trial ? MenuTheme.Glow : MenuTheme.Bright, Absent ? 0.45f : 1, below ? note : null, noteColour);
        else if (Fixed is { } part)
        {
            HangarText.Line(this, rest.Position + new Vector2(6, 30), Lang.T(Studio.Title(part)), rest.Size.X - 10, 20, MenuTheme.Text);
            HangarText.Line(this, rest.Position + new Vector2(6, 50), Lang.T("Part of the chassis"), rest.Size.X - 10, 15, MenuTheme.Dim.Lightened(0.25f));
        }
        else
        {
            var word = Absent ? Lang.T("No slot") : Lang.T("Empty");
            var baseline = rest.Position.Y + rest.Size.Y / 2 + 8;
            DrawString(MenuTheme.Font, new Vector2(rest.Position.X, baseline), word.ToUpperInvariant(), HorizontalAlignment.Center, rest.Size.X, 20,
                Absent ? MenuTheme.Dim : new Color(MenuTheme.Bright, 0.45f + 0.5f * Pulse));
        }
    }
}

/// <summary>The chassis' plate over the hero: where a chassis is dropped, and the way it goes when one is tried.</summary>
public partial class ChassisPlate : HangarCard
{
    private const float ArtScale = 0.26f;

    public required string Word { get; init; }

    public string Chassis { get; set; } = "";

    public string Facts { get; set; } = "";

    /// <summary>What the slot says of a chassis held over it; null with none held.</summary>
    public bool? Fits { get; set; }

    public bool Trial { get; set; }

    public bool Pointed { get; set; }

    protected override bool Lit => Pointed || Fits == true;

    public override void _Draw()
    {
        var body = new Rect2(Vector2.Zero, Size);
        ChipArt.Draw(this, body, ArtScale, Light * 0.45f);
        var inner = body.Grow(-ChipArt.FrameWidth(ArtScale) - 4);
        if (Fits is { } fits)
        {
            var tint = fits ? MenuTheme.Glow : HangarPage.Red;
            DrawRect(inner, new Color(tint, 0.2f));
            DrawRect(inner.Grow(1), new Color(tint, 0.9f), false, 2);
        }
        var (name, width) = (Trial ? MenuTheme.Glow : MenuTheme.Bright, inner.Size.X);
        // Too narrow for its facts in a line, the word goes and they take two.
        if (MenuTheme.Font.GetStringSize(Facts, HorizontalAlignment.Left, -1, 13).X > width && Facts.Split(" · ") is { Length: 4 } facts)
        {
            HangarText.Line(this, inner.Position + new Vector2(0, 26), Chassis, width, 26, name, HorizontalAlignment.Center);
            HangarText.Line(this, inner.Position + new Vector2(0, 43), $"{facts[0]} · {facts[1]}", width, 14, MenuTheme.Text, HorizontalAlignment.Center);
            HangarText.Line(this, inner.Position + new Vector2(0, 59), $"{facts[2]} · {facts[3]}", width, 14, MenuTheme.Text, HorizontalAlignment.Center);
            return;
        }
        HangarText.Line(this, inner.Position + new Vector2(0, 15), Word, width, 13, MenuTheme.Dim.Lightened(0.35f), HorizontalAlignment.Center);
        HangarText.Line(this, inner.Position + new Vector2(0, 42), Chassis, width, 28, name, HorizontalAlignment.Center);
        HangarText.Line(this, inner.Position + new Vector2(0, 62), Facts, width, 15, MenuTheme.Text, HorizontalAlignment.Center);
    }
}

/// <summary>A tab of the inventory: its name, with a green bar under it when it is the one showing.</summary>
public partial class HangarTab : Control
{
    private const int FontSize = 16;

    private float _light;
    private string _text = "";

    public required string Text
    {
        get => _text;
        set
        {
            _text = value;
            CustomMinimumSize = new Vector2(MenuTheme.Font.GetStringSize(value, HorizontalAlignment.Left, -1, FontSize).X + 12, 38);
            QueueRedraw();
        }
    }

    /// <summary>Its word is red: there is something to see to.</summary>
    public bool Warning { get; set; }

    /// <summary>Something a run has only just opened lies on this shelf.</summary>
    public bool Fresh { get; set; }

    public bool On { get; set; }

    public event Action? Pressed;

    public override void _Ready()
    {
        MouseDefaultCursorShape = CursorShape.PointingHand;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Process(double delta)
    {
        var target = On ? 1f : GetGlobalRect().HasPoint(GetGlobalMousePosition()) ? 0.5f : 0;
        if (_light == target)
            return;
        _light = Mathf.MoveToward(_light, target, (float)delta * 8);
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            return;
        MenuSounds.Gui("listclik.wav");
        Pressed?.Invoke();
        AcceptEvent();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, Size.Y - 3, Size.X, 3), new Color(MenuTheme.Glow, 0.12f + 0.88f * _light));
        if (_light > 0)
            DrawRect(new Rect2(0, 0, Size.X, Size.Y - 3), new Color(MenuTheme.Glow, 0.13f * _light));
        var colour = Warning ? HangarPage.Red : Fresh ? MenuTheme.Bright : MenuTheme.Dim.Lightened(0.3f).Lerp(MenuTheme.Bright, _light);
        DrawString(MenuTheme.Font, new Vector2(0, Size.Y - 12), Text, HorizontalAlignment.Center, Size.X, FontSize, colour);
        if (Fresh)
            DrawCircle(new Vector2(Size.X - 5, 7), 4, MenuTheme.Bright);
    }
}

/// <summary>One of the four race colours to wear: a lamp in that colour, ringed when chosen.</summary>
public partial class ColourLamp : Control
{
    private float _time;

    public required Color Colour { get; init; }

    public bool On { get; set; }

    public event Action? Pressed;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(46, 46);
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            return;
        MenuSounds.Gui("listclik.wav");
        Pressed?.Invoke();
        AcceptEvent();
    }

    public override void _Draw()
    {
        var middle = Size / 2;
        var radius = Math.Min(Size.X, Size.Y) / 2 - 7;
        DrawCircle(middle, radius + 5, new Color(Colour, On ? 0.22f + 0.1f * Mathf.Sin(_time * 3) : 0.06f));
        DrawCircle(middle, radius, On ? Colour : Colour.Darkened(0.4f));
        DrawCircle(middle - new Vector2(radius * 0.3f, radius * 0.3f), radius * 0.28f, new Color(1, 1, 1, On ? 0.6f : 0.2f));
        if (On)
            DrawArc(middle, radius + 5, 0, Mathf.Tau, 40, MenuTheme.Bright, 2, true);
    }
}
