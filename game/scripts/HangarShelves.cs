using Godot;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The chassis tree of the shop, and the path shown before a run: every chassis in its place (<see cref="ChassisTree"/>), the
/// lines between them running as a board's tracks do. The way the hero has come glows and sends pulses along, what is
/// open next is yellow, what lies further on stays dark. Wired is the remake's own addition.
/// </summary>
public partial class ChassisTreeView(HangarPage page) : Control
{
    private const float GapAcross = 14, GapDown = 16, MostHeight = 76;

    /// <summary>What the tree needs for its words to be read: below that the shelf scrolls.</summary>
    public const float LeastHeight = ChassisTree.Rows * 60 + (ChassisTree.Rows - 1) * GapDown;

    private readonly Dictionary<string, TreeCard> _cards = [];
    private float _time;

    public IEnumerable<TreeCard> Cards => _cards.Values;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Material = null;
        Resized += Place;
        Place();
    }

    /// <summary>Puts a chassis on the tree. All of them go on before it enters the world.</summary>
    public TreeCard Add(HangarGood good)
    {
        var card = new TreeCard { Good = good, Page = page };
        _cards[good.Item.Catalogue] = card;
        AddChild(card);
        return card;
    }

    private void Place()
    {
        var width = (Size.X - (ChassisTree.Columns - 1) * GapAcross) / ChassisTree.Columns;
        var height = Math.Min(MostHeight, (Size.Y - (ChassisTree.Rows - 1) * GapDown) / ChassisTree.Rows);
        var step = (Size.Y - height) / (ChassisTree.Rows - 1);
        foreach (var (id, card) in _cards)
        {
            var (column, row) = ChassisTree.Place(id);
            card.Position = new Vector2(column * (width + GapAcross), row * step);
            card.Size = new Vector2(width, height);
        }
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // The dark ones go first, so that the way the hero has come lies on top.
        foreach (var (from, to, kind) in ChassisTree.Lines.Select(l => (l.From, l.To, Kind: Kind(l.From, l.To))).OrderBy(l => l.Kind))
            Run(Route(_cards[from], _cards[to], ChassisTree.TurnsAtOnce(from, to)), kind);
    }

    private ChassisLine Kind(string from, string to) => ChassisTree.Line(_cards[from].Good.Step ?? ChassisStep.Closed, _cards[to].Good.Step ?? ChassisStep.Closed,
        page.Desk.Reached(from), page.Desk.Shop is not null);

    private static Vector2[] Route(Control from, Control to, bool atOnce)
    {
        var start = from.Position + new Vector2(from.Size.X / 2, from.Size.Y);
        var end = to.Position + new Vector2(to.Size.X / 2, 0);
        if (Math.Abs(start.X - end.X) < 1)
            return [start, end];
        var turn = atOnce ? start.Y + GapDown * 0.5f : end.Y - GapDown * 0.5f;
        var slant = Math.Min(GapDown * 0.3f, Math.Abs(end.X - start.X) / 2) * Math.Sign(end.X - start.X);
        var fall = Math.Abs(slant);
        return [start, new Vector2(start.X, turn - fall), new Vector2(start.X + slant, turn), new Vector2(end.X - slant, turn), new Vector2(end.X, turn + fall), end];
    }

    private void Run(Vector2[] route, ChassisLine kind)
    {
        var colour = kind == ChassisLine.Come ? MenuTheme.Glow : kind == ChassisLine.Next ? MenuTheme.Bright : MenuTheme.Edge;
        var dark = kind == ChassisLine.Ahead;
        if (!dark)
            DrawPolyline(route, new Color(colour, 0.14f), 7, true);
        DrawPolyline(route, new Color(colour, dark ? 0.65f : 0.95f), dark ? 1.4f : 2, true);
        if (dark)
            return;
        var length = 0f;
        for (var i = 1; i < route.Length; i++)
            length += route[i - 1].DistanceTo(route[i]);
        var at = (_time * 70) % Math.Max(length, 1);
        for (var i = 1; i < route.Length; i++)
        {
            var part = route[i - 1].DistanceTo(route[i]);
            if (at <= part)
            {
                var spot = part < 0.01f ? route[i] : route[i - 1].Lerp(route[i], at / part);
                DrawCircle(spot, 2.6f, new Color(colour, 0.95f));
                DrawCircle(spot, 6, new Color(colour, 0.2f));
                break;
            }
            at -= part;
        }
    }
}

/// <summary>Who stands in the squad: a line for each escort with what it has left, kept up to date.</summary>
public partial class SquadRoster(MatchDesk desk) : Control
{
    private const float RowHeight = 26;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta)
    {
        var height = Math.Max(1, desk.Shop.SquadSize) * RowHeight;
        if (CustomMinimumSize.Y != height)
            CustomMinimumSize = new Vector2(0, height);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var y = 0f;
        foreach (var escort in desk.Escorts)
        {
            var key = new Machines.Import.Model.ResearchKey(escort.Spec.Type, escort.Spec.SubType, escort.Stats.HardwareLevel, escort.WeaponCombo);
            HangarText.Line(this, new Vector2(0, y + 18), HangarText.Machine(key), Size.X * 0.45f, 17, MenuTheme.Text);
            var bar = new Rect2(Size.X * 0.48f, y + 7, Size.X * 0.52f, 12);
            var share = escort.MaxHitPoints <= 0 ? 0 : Mathf.Clamp(escort.HitPoints / (float)escort.MaxHitPoints, 0, 1);
            DrawRect(bar, MenuTheme.Well);
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * share, bar.Size.Y)), share < 0.35f ? HangarPage.Red : MenuTheme.Glow.Darkened(0.2f));
            DrawRect(bar, MenuTheme.Edge, false, 1);
            y += RowHeight;
        }
        if (y == 0)
            HangarText.Line(this, new Vector2(0, 18), Lang.T("Nobody yet."), Size.X, 17, MenuTheme.Dim.Lightened(0.25f));
    }
}

/// <summary>The pod on its shelf: how it stands and what mending it costs, a part of it or all of it.</summary>
public partial class PodPanel(HangarPage page, MatchDesk desk) : VBoxContainer
{
    private static readonly int[] Amounts = [25, 100];

    private Bar _bar = null!;
    private Label _facts = null!;

    private sealed partial class Bar(MatchDesk desk) : Control
    {
        public override void _Ready()
        {
            CustomMinimumSize = new Vector2(0, 26);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Process(double delta) => QueueRedraw();

        public override void _Draw()
        {
            var body = new Rect2(Vector2.Zero, Size);
            DrawRect(body, MenuTheme.Well);
            if (desk.Pod is { } pod)
            {
                var share = Mathf.Clamp(pod.HitPoints / (float)Math.Max(1, pod.MaxHitPoints), 0, 1);
                DrawRect(new Rect2(0, 0, Size.X * share, Size.Y), share < 0.35f ? HangarPage.Red : MenuTheme.Glow.Darkened(0.2f));
            }
            DrawRect(body, MenuTheme.Edge, false, 1);
        }
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 14);
        AddChild(_bar = new Bar(desk));
        // Wrapped, as a line wider than the shelf would push the whole stage aside.
        AddChild(_facts = MenuTheme.Paragraph("", 20, MenuTheme.Text));
        foreach (var amount in Amounts)
            AddChild(MenuPage.Button(Lang.T("Repair for {0} BMU", amount), () => Mend(amount), 60, 20));
        AddChild(MenuPage.Button("Repair it all", () => Mend(0), 60, 20));
        AddChild(MenuTheme.Paragraph(Lang.T("What the pod loses in a wave stays lost until it is paid for: it does not mend by itself."), 17, MenuTheme.Dim.Lightened(0.25f)));
    }

    public override void _Process(double delta)
    {
        _facts.Text = desk.Pod is { } pod
            ? Lang.T("{0} / {1} hit points · all of it {2} BMU", pod.HitPoints, pod.MaxHitPoints, pod.Price).Replace(" · ", "\n")
            : HangarText.Reason("the pod is gone");
    }

    private void Mend(int bmu)
    {
        if (desk.MendPod(bmu) is { } why)
            page.Refuse(why);
        else
            page.Bought();
    }
}
