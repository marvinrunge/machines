using Godot;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>A big number of the end screen under its name, which rolls up to what it is told and flares when it means something.</summary>
public partial class EndFigure(string title, int fontSize) : Control
{
    private const int TitleSize = 19;

    private double _from, _to, _time, _left;
    private float _flare;

    public Color Colour { get; set; } = MenuTheme.Text;

    /// <summary>Rolls on to a value within some seconds.</summary>
    public void To(long value, double seconds)
    {
        _from = Now;
        _to = value;
        _time = _left = Math.Max(seconds, 0.001);
    }

    /// <summary>The roll is over: the number stands.</summary>
    public void Land()
    {
        _left = 0;
        QueueRedraw();
    }

    /// <summary>It takes a colour, in a flash of white.</summary>
    public void Flare(Color colour)
    {
        Colour = colour;
        _flare = 1;
    }

    private double Now => _left > 0 ? _to + (_from - _to) * (_left / _time) * (_left / _time) : _to;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta)
    {
        if (_left <= 0 && _flare <= 0)
            return;
        _left = Math.Max(0, _left - delta);
        _flare = Math.Max(0, _flare - (float)delta * 1.4f);
        QueueRedraw();
    }

    public override Vector2 _GetMinimumSize() => new(0, TitleSize + fontSize + 12);

    public override void _Draw()
    {
        HangarText.Line(this, new Vector2(2, TitleSize), Lang.T(title).ToUpperInvariant(), Size.X - 4, TitleSize, MenuTheme.Dim.Lightened(0.35f));
        HangarText.Line(this, new Vector2(2, TitleSize + fontSize + 2), WiredReport.Figure((long)Math.Round(Now), Lang.Code == "de"), Size.X - 4, fontSize, Colour.Lerp(Colors.White, _flare * 0.75f));
    }
}

/// <summary>A row of the end screen, ready in words: the first cell on the left, the others up to their stops, which come as shares of the width. It slides in when its time comes.</summary>
public partial class EndRow : Control
{
    private float _shown, _tag;

    public required string[] Cells { get; init; }

    public required float[] Stops { get; init; }

    public int FontSize { get; init; } = 21;

    /// <summary>Column names, smaller and darker.</summary>
    public bool Heading { get; init; }

    /// <summary>The colour of the last cell.</summary>
    public Color Last { get; set; } = MenuTheme.Text;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Pass;

    public void Arrive() => SetProcess(true);

    /// <summary>Its gold plate comes up, for a best beaten.</summary>
    public void Tag()
    {
        _tag = 0.001f;
        SetProcess(true);
    }

    /// <summary>The row stands where it belongs.</summary>
    public void Land()
    {
        _shown = 1;
        if (_tag > 0)
            _tag = 1;
        QueueRedraw();
    }

    public override void _EnterTree() => SetProcess(false);

    public override void _Process(double delta)
    {
        _shown = Math.Min(1, _shown + (float)delta * 7);
        if (_tag > 0)
            _tag = Math.Min(1, _tag + (float)delta * 5);
        QueueRedraw();
        if (_shown >= 1 && _tag is 0 or >= 1)
            SetProcess(false);
    }

    public override Vector2 _GetMinimumSize() => new(0, FontSize + 9);

    public override void _Draw()
    {
        if (_shown <= 0)
            return;
        var left = (1 - _shown) * -18;
        var baseline = FontSize + 1;
        var (text, dim) = (new Color(MenuTheme.Text, _shown), new Color(MenuTheme.Dim.Lightened(0.35f), _shown));
        var start = 0f;
        for (var cell = 0; cell < Cells.Length; cell++)
        {
            var end = Stops[cell] * Size.X;
            var colour = Heading ? dim : cell == Cells.Length - 1 ? new Color(Last, _shown) : text;
            HangarText.Line(this, new Vector2(start + left, baseline), Cells[cell], end - start - 8, Heading ? FontSize - 3 : FontSize, colour, cell == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            start = end;
        }
        if (_tag > 0)
            HangarText.Tag(this, new Vector2(Stops[0] * Size.X - 4, baseline - FontSize * 0.42f), Lang.T("New"), _tag);
    }
}

/// <summary>The stamp that comes down on a score: large and fast, then it stays where it hit, a little crooked.</summary>
public partial class EndStamp : Control
{
    private const float Drop = 0.2f, Settle = 0.6f;

    /// <summary>How many of its letters' heights a panel has to be wide to take it at full size: it must leave the next figure's title alone.</summary>
    private const float Widths = 25.6f;

    private string _text = "";
    private Color _colour;
    private int _size;
    private float _age = -1;

    /// <summary>Where it lands, as shares of the panel.</summary>
    public Vector2 Place { get; set; } = new(0.37f, 0.15f);

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public void Land(string text, Color colour, int size)
    {
        (_text, _colour, _size, _age) = (text, colour, Math.Min(size, (int)(Size.X / Widths)), 0);
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_age < 0 || _age >= Drop + Settle)
            return;
        _age += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_age < 0)
            return;
        var fall = Math.Clamp(_age / Drop, 0, 1);
        var ring = Math.Clamp((_age - Drop) / Settle, 0, 1);
        var scale = Mathf.Lerp(2.8f, 1, fall * fall) + 0.07f * MathF.Sin(ring * 14) * (1 - ring);
        var font = MenuTheme.Font;
        var width = font.GetStringSize(_text, HorizontalAlignment.Left, -1, _size).X;
        DrawSetTransform(Size * Place, -0.06f, new Vector2(scale, scale));
        var plate = new Rect2(-width / 2 - 16, -_size * 0.78f, width + 32, _size * 1.56f);
        var colour = new Color(_colour, fall);
        DrawRect(plate, new Color(MenuTheme.Well, 0.85f * fall));
        DrawRect(plate, colour, false, 4);
        DrawRect(plate.Grow(-6), new Color(_colour, 0.5f * fall), false, 1.5f);
        DrawString(font, new Vector2(-width / 2, _size * 0.36f), _text, HorizontalAlignment.Left, -1, _size, colour);
        DrawSetTransform(Vector2.Zero);
    }
}

/// <summary>What goes over the whole end screen when something was earned: a flash, and sparks flying off a place.</summary>
public partial class EndFanfare : Control
{
    private const float Fall = 620;

    private sealed class Spark
    {
        public Vector2 At, Way;
        public float Left, Life, Width;
        public Color Colour;
    }

    private readonly List<Spark> _sparks = [];
    private Color _flash;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public void Flash(Color colour, float strength) => _flash = new Color(colour, strength);

    /// <summary>Sparks off a point of the window.</summary>
    public void Burst(Vector2 at, Color colour, int count, float speed = 520)
    {
        var from = GetGlobalTransform().AffineInverse() * at;
        for (var i = 0; i < count; i++)
        {
            var angle = GD.Randf() * Mathf.Tau;
            var life = 0.45f + GD.Randf() * 0.7f;
            _sparks.Add(new Spark
            {
                At = from,
                Way = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed * (0.25f + GD.Randf()) + new Vector2(0, -speed * 0.35f),
                Left = life,
                Life = life,
                Width = 1.5f + GD.Randf() * 2.5f,
                Colour = colour.Lerp(Colors.White, GD.Randf() * 0.6f),
            });
        }
    }

    public override void _Process(double delta)
    {
        if (_sparks.Count == 0 && _flash.A <= 0)
            return;
        var step = (float)delta;
        _flash.A = Math.Max(0, _flash.A - step * 1.3f);
        foreach (var spark in _sparks)
        {
            spark.At += spark.Way * step;
            spark.Way += new Vector2(0, Fall * step);
            spark.Left -= step;
        }
        _sparks.RemoveAll(s => s.Left <= 0);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_flash.A > 0)
            DrawRect(new Rect2(-Vector2.One * 60, Size + Vector2.One * 120), _flash);
        foreach (var spark in _sparks)
            DrawLine(spark.At, spark.At - spark.Way * 0.035f, new Color(spark.Colour, Math.Min(1, spark.Left / spark.Life * 1.6f)), spark.Width);
    }
}

/// <summary>A card of what a run opened: a small chip that lights up and takes a stroke of light as it comes on the board.</summary>
public partial class EndCard : Control
{
    private const float ArtScale = 0.22f, Height = 86;
    private const int LineSize = 14;

    private float _in, _sweep = 1;
    private bool _pictured;

    /// <summary>The id in the profile.</summary>
    public required string Id { get; init; }

    /// <summary>The picture of an arena.</summary>
    public Texture2D? Map { get; init; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Pass;
        CustomMinimumSize = new Vector2(0, Height);
    }

    public void Arrive() => _sweep = 0;

    public void Land()
    {
        _in = 1;
        _sweep = 1;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_sweep >= 1 && _in >= 1)
        {
            // The photograph comes when it comes, often after the card has landed.
            if (!_pictured && !HangarIcons.Awaited(Item))
            {
                _pictured = true;
                QueueRedraw();
            }
            return;
        }
        _sweep = Math.Min(1, _sweep + (float)delta * 1.5f);
        _in = Math.Min(1, _in + (float)delta * 6);
        QueueRedraw();
    }

    /// <summary>What it is, what it is called and what it means.</summary>
    private (string Kind, string Name, string Line) Words()
    {
        var name = HangarText.Opened(Id);
        if (Id == WiredProfile.Hardcore)
            return (Lang.T("New difficulty"), name, Lang.T("No spare bodies, double the score."));
        if (WiredProfile.Arenas.Any(a => a.Scenario == Id))
            return (Lang.T("New arena"), name, Lang.T("Yours to choose for the next run."));
        return WiredCatalogue.FindChassis(Id) is not null
            ? (Lang.T("New chassis"), name, Lang.T("A hero can start on it now."))
            : (Lang.T("New chip"), name, HangarText.Effect(new WiredItem(-1, WiredKind.Chip, Id)) ?? "");
    }

    private void DrawIcon(Rect2 box)
    {
        if (Id == WiredProfile.Hardcore)
        {
            var (mark, middle) = (HangarPage.Red, box.GetCenter());
            var arm = box.Size.X * 0.3f;
            DrawLine(middle - new Vector2(arm, arm), middle + new Vector2(arm, arm), mark, 7);
            DrawLine(middle + new Vector2(-arm, arm), middle + new Vector2(arm, -arm), mark, 7);
            DrawArc(middle, box.Size.X * 0.46f, 0, Mathf.Tau, 32, mark, 3);
        }
        else if (WiredProfile.Arenas.Any(a => a.Scenario == Id))
        {
            if (Map is { } map)
                DrawTextureRect(map, box, false);
        }
        else
        {
            HangarIcons.Draw(this, Item, box, Colors.White);
        }
    }

    private WiredItem Item => new(-1, WiredCatalogue.FindChassis(Id) is not null ? WiredKind.Chassis : WiredKind.Chip, Id);

    public override void _Draw()
    {
        if (_in <= 0)
            return;
        var body = new Rect2(Vector2.Zero, Size);
        ChipArt.Draw(this, body, ArtScale, (1 - _sweep) * 0.7f, _in);
        var inner = body.Grow(-ChipArt.FrameWidth(ArtScale) - 3);
        var side = inner.Size.Y - 2;
        DrawIcon(new Rect2(inner.Position + new Vector2(2, 1), new Vector2(side, side)));
        var x = inner.Position.X + side + 10;
        var width = inner.End.X - x - 4;
        var (kind, name, line) = Words();
        // A chip's effect can take two lines: the rest makes room for it.
        var top = inner.Position.Y - (MenuTheme.Font.GetMultilineStringSize(line, HorizontalAlignment.Left, width, LineSize).Y > LineSize * 1.8f ? 6 : 0);
        HangarText.Line(this, new Vector2(x, top + 15), kind.ToUpperInvariant(), width, 13, MenuTheme.Glow);
        HangarText.Line(this, new Vector2(x, top + 36), name, width, 21, MenuTheme.Bright);
        DrawMultilineString(MenuTheme.Font, new Vector2(x, top + 52), line, HorizontalAlignment.Left, width, LineSize, 2, MenuTheme.Text);
        if (_sweep < 1)
        {
            var band = Mathf.Lerp(inner.Position.X - 60, inner.End.X, _sweep);
            DrawRect(new Rect2(band, inner.Position.Y, 60, inner.Size.Y).Intersection(inner), new Color(MenuTheme.Bright, 0.3f * (1 - _sweep)));
        }
        DrawRect(inner.Grow(1), new Color(MenuTheme.Bright, 0.35f + 0.65f * (1 - _sweep)), false, 2);
    }
}

/// <summary>What is still closed and how far off it is, with a bar that fills to where the player stands.</summary>
public partial class EndGoal(WiredGoal goal) : Control
{
    public const float Height = 50;

    private float _shown = -1;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(0, Height);
    }

    public void Arrive() => _shown = 0;

    public void Land()
    {
        _shown = 1;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_shown is < 0 or >= 1)
            return;
        _shown = Math.Min(1, _shown + (float)delta * 2.2f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_shown < 0)
            return;
        var alpha = Math.Min(1, _shown * 3);
        var figures = $"{goal.Have} / {goal.Need}";
        var room = MenuTheme.Font.GetStringSize(figures, HorizontalAlignment.Left, -1, 16).X + 6;
        HangarText.Line(this, new Vector2(0, 17), HangarText.Opened(goal.Id), Size.X - room, 19, new Color(MenuTheme.Text, alpha));
        HangarText.Line(this, new Vector2(Size.X - room, 17), figures, room, 16, new Color(MenuTheme.Dim.Lightened(0.35f), alpha), HorizontalAlignment.Right);
        HangarText.Line(this, new Vector2(0, 35), Lang.T(goal.Words, goal.Need, goal.Squad), Size.X, 15, new Color(MenuTheme.Dim.Lightened(0.3f), alpha));
        var bar = new Rect2(0, 41, Size.X, 5);
        DrawRect(bar, new Color(MenuTheme.Edge, 0.6f * alpha));
        DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * goal.Share * _shown, bar.Size.Y)), new Color(MenuTheme.Glow, alpha));
    }
}
