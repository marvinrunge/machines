using Godot;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The hero's circuit board at the hangar's foot: six sockets on a ring of traces with a bridge between the first and the
/// fourth (<see cref="WiredChips.Traces"/>). A trace between two chips of one family lights up in its colour, and what a chip
/// held over a socket would link shows before it is put there. Wired is the remake's own addition.
/// </summary>
public partial class ChipBoard(HangarPage page) : Control
{
    private const float Lane = 15, Gap = 22;

    private readonly SlotCard[] _sockets = new SlotCard[WiredBalance.ChipSockets];
    private float _time;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(0, 128);
        for (var socket = 0; socket < _sockets.Length; socket++)
        {
            _sockets[socket] = new SlotCard { Slot = WiredSlot.Chip(socket), Title = "", Page = page };
            AddChild(_sockets[socket]);
            page.AddSocket(_sockets[socket]);
        }
        Resized += Place;
        Place();
    }

    private void Place()
    {
        var width = (Size.X - (_sockets.Length - 1) * Gap) / _sockets.Length;
        for (var socket = 0; socket < _sockets.Length; socket++)
        {
            _sockets[socket].Position = new Vector2(socket * (width + Gap), Lane);
            _sockets[socket].Size = new Vector2(width, Size.Y - 2 * Lane);
        }
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var (now, shown) = (page.Desk.Loadout.Chips, page.ChipsShown);
        foreach (var (a, b) in WiredChips.Traces)
            Trace(Route(a, b), Joined(shown, a, b), Joined(now, a, b) is not null);
    }

    /// <summary>The family two sockets share, if they do.</summary>
    private static ChipFamily? Joined(IReadOnlyList<WiredItem?> chips, int a, int b) =>
        chips[a] is { } one && chips[b] is { } other && WiredChips.Find(one.Catalogue) is { } first && WiredChips.Find(other.Catalogue)?.Family == first.Family
            ? first.Family
            : null;

    /// <summary>Neighbours are joined straight on; the ring closes along the foot and the bridge runs along the top.</summary>
    private Vector2[] Route(int a, int b)
    {
        var (left, right) = (_sockets[Math.Min(a, b)], _sockets[Math.Max(a, b)]);
        if (Math.Abs(a - b) == 1)
        {
            var y = left.Position.Y + left.Size.Y / 2;
            return [new Vector2(left.Position.X + left.Size.X, y), new Vector2(right.Position.X, y)];
        }
        var (start, end) = (left.Position.X + left.Size.X * 0.5f, right.Position.X + right.Size.X * 0.5f);
        var bottom = Math.Abs(a - b) == _sockets.Length - 1;
        var (from, lane) = bottom ? (left.Position.Y + left.Size.Y, Size.Y - 4) : (left.Position.Y, 4f);
        var turn = bottom ? -6 : 6;
        return
        [
            new Vector2(start, from), new Vector2(start, lane + turn), new Vector2(start + 6, lane),
            new Vector2(end - 6, lane), new Vector2(end, lane + turn), new Vector2(end, from),
        ];
    }

    private void Trace(Vector2[] route, ChipFamily? family, bool landed)
    {
        if (family is not { } joined)
        {
            DrawPolyline(route, new Color(MenuTheme.Edge, 0.9f), 2, true);
            return;
        }
        var colour = HangarText.FamilyColour(joined);
        var strength = landed ? 1 : 0.5f + 0.35f * Mathf.Sin(_time * 6);
        DrawPolyline(route, new Color(colour, 0.2f * strength), 9, true);
        DrawPolyline(route, new Color(colour, strength), 2.6f, true);
        var length = 0f;
        for (var i = 1; i < route.Length; i++)
            length += route[i - 1].DistanceTo(route[i]);
        for (var pulse = 0f; pulse < 1; pulse += 0.5f)
        {
            var at = ((_time * 0.6f + pulse) % 1) * length;
            for (var i = 1; i < route.Length; i++)
            {
                var part = route[i - 1].DistanceTo(route[i]);
                if (at <= part)
                {
                    var spot = part < 0.01f ? route[i] : route[i - 1].Lerp(route[i], at / part);
                    DrawCircle(spot, 3, new Color(colour.Lightened(0.4f), strength));
                    DrawCircle(spot, 7, new Color(colour, 0.25f * strength));
                    break;
                }
                at -= part;
            }
        }
    }
}

/// <summary>What is left to spend and when the next wave comes, beside the board, with the button that calls it.</summary>
public partial class HangarClock(Game game) : VBoxContainer
{
    private Label _bank = null!, _wave = null!, _time = null!;

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        CustomMinimumSize = new Vector2(350, 0);
        AddThemeConstantOverride("separation", 2);
        AddChild(_bank = MenuTheme.Label("", 34, MenuTheme.Glow));
        AddChild(_wave = MenuTheme.Label("", 20, MenuTheme.Bright));
        AddChild(_time = MenuTheme.Label("", 19, MenuTheme.Text));
    }

    public override void _Process(double delta)
    {
        if (game.World.Wired is not { } run)
            return;
        _bank.Text = $"{(int)game.World.Races[Game.Player].Bmu} BMU";
        _wave.Text = Lang.T("Next: wave {0}", run.ShopWave) + (Coming(run) is { } coming ? $" · {coming}" : "");
        var pays = game.World.ReadyPays;
        _time.Text = !run.Waiting ? Lang.T("The wave is on its way")
            : pays > 0 ? Lang.T("{0} s, held · Ready pays {1} BMU", Math.Ceiling(run.Countdown), pays)
            : Lang.T("{0} s, held while the hangar is open", Math.Ceiling(run.Countdown));
    }

    /// <summary>What the player is told of the next wave: its boss, or what is special about it.</summary>
    private static string? Coming(WiredRun run)
    {
        if (WiredDirector.BossOf(run.ShopWave) is { } boss)
            return Lang.T("Boss: {0}", Lang.T(boss.Name));
        return HangarText.Special(run.NextSpecial);
    }
}

/// <summary>
/// The chips a boss leaves to choose from, over the shelves until one is taken, so that the hero stays in view. Pointing at one
/// shows what it would do on the board, as any good does; a click takes it, a drag takes it onto a socket, and it can wait
/// while the rest of the hangar is used.
/// </summary>
public partial class ChipOffer(HangarPage page) : VBoxContainer
{
    private const float CardHeight = 64;

    private readonly List<ItemCard> _cards = [];

    public IReadOnlyList<ItemCard> Cards => _cards;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        AddChild(MenuTheme.Label(Lang.T("Choose a chip").ToUpperInvariant(), 20, MenuTheme.Bright));
    }

    /// <summary>The choices as they stand.</summary>
    public void Show(IReadOnlyList<HangarGood> choices)
    {
        _cards.Clear();
        foreach (var child in GetChildren().OfType<ItemCard>())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        foreach (var good in choices)
        {
            var card = new ChoiceCard { Good = good, Page = page, CustomMinimumSize = new Vector2(0, CardHeight) };
            AddChild(card);
            _cards.Add(card);
        }
        Visible = choices.Count > 0;
    }
}

/// <summary>A chip on offer: a low card, with the word that takes it at its end.</summary>
public partial class ChoiceCard : ItemCard
{
    public override void _Draw()
    {
        var body = new Rect2(Vector2.Zero, Size);
        ChipArt.Draw(this, body, ArtScale, Light * 0.45f);
        var inner = body.Grow(-ChipArt.FrameWidth(ArtScale) - 3);
        var colour = HangarText.Family(Good.Item) ?? MenuTheme.Glow;
        DrawRect(inner.Grow(1), new Color(colour, 0.7f), false, 2);
        var icon = new Rect2(inner.Position + new Vector2(2, 2), new Vector2(inner.Size.Y - 4, inner.Size.Y - 4));
        HangarIcons.Draw(this, Good.Item, icon, Colors.White);
        var word = Lang.T("Take");
        var wordWidth = MenuTheme.Font.GetStringSize(word, HorizontalAlignment.Left, -1, 17).X + 14;
        var (x, width) = (icon.End.X + 7, inner.End.X - icon.End.X - wordWidth - 10);
        var top = inner.Position.Y;
        HangarText.Line(this, new Vector2(x, top + 17), HangarText.Name(Good.Item.Catalogue), width, 18, MenuTheme.Bright);
        // With no room in the inventory it says so at once, not only after a click.
        var open = Good.Locked is null;
        HangarText.Line(this, new Vector2(x, top + 34), open ? HangarText.Kind(Good.Item) : HangarText.Reason(Good.Locked!), width, 14, open ? new Color(MenuTheme.Text, 0.85f) : HangarPage.Red);
        var strip = new Rect2(new Vector2(inner.End.X - wordWidth, inner.Position.Y), new Vector2(wordWidth, inner.Size.Y));
        var over = open && Page is { Dragging: false } page && GetGlobalRect().HasPoint(page.Mouse);
        var tint = open ? MenuTheme.Glow : MenuTheme.Dim.Lightened(0.3f);
        DrawRect(strip, new Color(tint, over ? 0.34f : 0.13f));
        HangarText.Line(this, strip.Position + new Vector2(0, strip.Size.Y / 2 + 6), word, strip.Size.X, 17, tint, HorizontalAlignment.Center);
    }
}
