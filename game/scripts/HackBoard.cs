using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// The board of a hack (docs/STORY.md, Hacken; plan WP-E), drawn as the hangar's board is: dark sockets for the tiles, the
/// traces on them with the three-layer glow of <see cref="HangarTraces"/>, light running out from the Locator's probe along
/// what it reaches, chips on the edges for the probe, the targets and the firewall (<see cref="ChipArt"/> with the silk
/// screen's drawings, <see cref="HangarGlyphs"/>), red warden chips, the firewall's pulse in red, a quarter turn eased in,
/// a surge when the board is won and a red flash when it is traced. It shows a <see cref="HackPuzzle"/> as the world keeps
/// it and changes nothing: a click or the cursor's button asks for a turn (<see cref="Turned"/>), which the host sends as
/// a lockstep command.
/// </summary>
public partial class HackBoard : Control
{
    /// <summary>The red of danger, as the Wired screen has it.</summary>
    public static readonly Color Danger = new(1, 0.26f, 0.16f);

    private static readonly Color Unlit = new(0.4f, 0.68f, 0.34f), Socket = new(0.016f, 0.05f, 0.022f);
    private static readonly int[] Sides = [HackPuzzle.N, HackPuzzle.E, HackPuzzle.S, HackPuzzle.W];

    private HackPuzzle? _shown;
    private float[] _angle = [], _goal = [];
    private int[] _rotation = [];
    private float _time, _surge, _alarm;
    private int _hover = -1;

    public HackBoard()
    {
        CustomMinimumSize = new Vector2(500, 500);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
    }

    /// <summary>The board as the world keeps it, read each frame; null shows an empty board.</summary>
    public Func<HackPuzzle?> Source { get; set; } = () => null;

    /// <summary>What the hack is aimed at: the targets' drawing.</summary>
    public HackKind Kind { get; set; }

    /// <summary>The story broke the hack off: the traces stand amber and take no turn.</summary>
    public bool Broken { get; set; }

    /// <summary>The cell the gamepad's or keyboard's cursor is on, -1 for none.</summary>
    public int Cursor { get; set; } = -1;

    /// <summary>Asked for a turn of a cell, clockwise or back.</summary>
    public event Action<int, bool>? Turned;

    /// <summary>Light runs through the traces: the board is won.</summary>
    public void Surge() => _surge = 1;

    /// <summary>The board flashes red: the hack is traced.</summary>
    public void Alarm() => _alarm = 1;

    // ---- geometry ----------------------------------------------------------------------------------

    private float CellSize => _shown is null ? 0 : Mathf.Min(Size.X, Size.Y) / (_shown.Size + 1.45f);

    private Vector2 Origin => _shown is null ? Vector2.Zero : (Size - Vector2.One * CellSize * _shown.Size) / 2;

    /// <summary>A cell's square on the board.</summary>
    public Rect2 CellRect(int cell)
    {
        var (x, y) = _shown!.At(cell);
        return new Rect2(Origin + new Vector2(x, y) * CellSize, Vector2.One * CellSize);
    }

    /// <summary>The cell under a point of the board, or -1.</summary>
    public int CellAt(Vector2 point)
    {
        if (_shown is null || CellSize <= 0)
            return -1;
        var at = (point - Origin) / CellSize;
        var (x, y) = ((int)Mathf.Floor(at.X), (int)Mathf.Floor(at.Y));
        return x < 0 || y < 0 || x >= _shown.Size || y >= _shown.Size ? -1 : _shown.CellOf(x, y);
    }

    private static Vector2 Direction(int side) => side switch
    {
        HackPuzzle.N => new Vector2(0, -1),
        HackPuzzle.E => new Vector2(1, 0),
        HackPuzzle.S => new Vector2(0, 1),
        _ => new Vector2(-1, 0),
    };

    /// <summary>The body of a chip on the edge, beside its cell on its side.</summary>
    private Rect2 PortChip(HackPort port)
    {
        var cell = CellRect(port.Cell);
        var size = CellSize * 0.56f;
        var centre = cell.GetCenter() + Direction(port.Side) * (CellSize * 0.5f + CellSize * 0.42f);
        return new Rect2(centre - Vector2.One * size / 2, Vector2.One * size);
    }

    // ---- input -------------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
            _hover = CellAt(motion.Position);
        else if (@event is InputEventMouseButton { Pressed: true } click && click.ButtonIndex is MouseButton.Left or MouseButton.Right)
        {
            var cell = CellAt(click.Position);
            if (cell >= 0 && _shown is { } puzzle && puzzle.IsTile(cell) && !Broken)
                Turned?.Invoke(cell, click.ButtonIndex == MouseButton.Left);
            AcceptEvent();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
            _hover = -1;
    }

    // ---- frame -------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _time += dt;
        _surge = Mathf.Max(0, _surge - dt / 0.9f);
        _alarm = Mathf.Max(0, _alarm - dt / 0.8f);
        var puzzle = Source();
        if (!ReferenceEquals(puzzle, _shown))
            Take(puzzle);
        if (_shown is { } board)
        {
            // A quarter turn the world made is eased in, the short way it went.
            for (var cell = 0; cell < board.Cells; cell++)
            {
                var now = board.Rotation(cell);
                if (now != _rotation[cell])
                {
                    var step = (now - _rotation[cell] + 4) % 4;
                    _goal[cell] += step == 3 ? -1 : step;
                    _rotation[cell] = now;
                }
                _angle[cell] = Mathf.Lerp(_angle[cell], _goal[cell], 1 - Mathf.Exp(-16 * dt));
            }
        }
        QueueRedraw();
    }

    /// <summary>A new board (another stage, a board dealt anew): its tiles stand as they lie.</summary>
    private void Take(HackPuzzle? puzzle)
    {
        _shown = puzzle;
        var cells = puzzle?.Cells ?? 0;
        _rotation = new int[cells];
        _angle = new float[cells];
        _goal = new float[cells];
        for (var cell = 0; cell < cells; cell++)
            _angle[cell] = _goal[cell] = _rotation[cell] = puzzle!.Rotation(cell);
        if (Cursor >= cells)
            Cursor = cells > 0 ? 0 : -1;
    }

    // ---- drawing -----------------------------------------------------------------------------------

    public override void _Draw()
    {
        if (_shown is not { } puzzle || CellSize <= 0)
            return;
        var size = CellSize;
        var network = puzzle.Network();
        var depth = Depths(puzzle, network);
        var board = new Rect2(Origin, Vector2.One * size * puzzle.Size);

        // The board's ground: the well, a faint grid of the silk screen, red while traced.
        DrawRect(board.Grow(size * 0.08f), MenuTheme.Well.Lerp(new Color(0.25f, 0.02f, 0.01f), _alarm * 0.8f));
        DrawRect(board.Grow(size * 0.08f), new Color(MenuTheme.Edge, 0.7f), false, 2);

        for (var cell = 0; cell < puzzle.Cells; cell++)
            DrawSocket(puzzle, cell, size);

        // The chips on the edges, and the short traces from them into their cells.
        DrawPort(puzzle.Probe, "probe", MenuTheme.Glow, 1, network[puzzle.Probe.Cell] && (puzzle.Mask(puzzle.Probe.Cell) & puzzle.Probe.Side) != 0, null);
        for (var i = 0; i < puzzle.Targets.Count; i++)
        {
            var target = puzzle.Targets[i];
            var claimed = i < puzzle.Claimed;
            var next = i == puzzle.Claimed;
            var light = claimed ? 1 : next ? 0.35f + 0.25f * Mathf.Sin(_time * 4) : 0;
            DrawPort(target, Glyph(Kind), claimed ? MenuTheme.Bright : next ? MenuTheme.Glow : MenuTheme.Dim, light,
                claimed || puzzle.Touches(network, target), puzzle.Targets.Count > 1 ? (i + 1).ToString() : null);
        }
        if (puzzle.Firewall is { } wall)
            DrawPort(wall, "firewall", Danger, 0, puzzle.PulseCell == wall.Cell, null, red: 0.45f + 0.3f * Mathf.Sin(_time * 5));

        // The tiles' traces, then the light running on those the probe reaches.
        for (var cell = 0; cell < puzzle.Cells; cell++)
        {
            if (puzzle.IsWarden(cell) || puzzle.Base(cell) == 0)
                continue;
            DrawTile(puzzle, cell, network[cell], size);
        }
        for (var cell = 0; cell < puzzle.Cells; cell++)
            if (network[cell] && !puzzle.IsWarden(cell))
                DrawFlow(puzzle, cell, depth[cell], size);

        if (puzzle.PulseCell >= 0)
        {
            var at = CellRect(puzzle.PulseCell).GetCenter();
            var beat = 0.75f + 0.25f * Mathf.Sin(_time * 12);
            DrawCircle(at, size * 0.2f * beat, new Color(Danger, 0.22f));
            DrawCircle(at, size * 0.1f, new Color(Danger, 0.9f));
            DrawCircle(at, size * 0.045f, new Color(1, 0.85f, 0.7f));
        }

        // The cursor and the tile under the mouse.
        foreach (var (cell, colour, width) in new[] { (_hover, new Color(MenuTheme.Glow, 0.55f), 2f), (Cursor, MenuTheme.Bright, 3f) })
        {
            if (cell < 0 || cell >= puzzle.Cells)
                continue;
            DrawRect(CellRect(cell).Grow(-size * 0.03f), colour, false, width);
        }
    }

    /// <summary>The steps from the probe to each cell of its network, for the light running outward.</summary>
    private static int[] Depths(HackPuzzle puzzle, bool[] network)
    {
        var depth = Enumerable.Repeat(-1, puzzle.Cells).ToArray();
        if (!network[puzzle.Probe.Cell])
            return depth;
        var queue = new Queue<int>();
        depth[puzzle.Probe.Cell] = 0;
        queue.Enqueue(puzzle.Probe.Cell);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var side in Sides)
            {
                if ((puzzle.Mask(cell) & side) == 0)
                    continue;
                var next = puzzle.Next(cell, side);
                if (next < 0 || depth[next] >= 0 || !network[next] || (puzzle.Mask(next) & HackPuzzle.Opposite(side)) == 0)
                    continue;
                depth[next] = depth[cell] + 1;
                queue.Enqueue(next);
            }
        }
        return depth;
    }

    private static string Glyph(HackKind kind) => kind switch
    {
        HackKind.Clamp => "clamp",
        HackKind.Grunt => "GRUNT",
        HackKind.Tower => "tower",
        HackKind.Pod => "pod",
        HackKind.Core => "core",
        _ => "door",
    };

    private void DrawSocket(HackPuzzle puzzle, int cell, float size)
    {
        var rect = CellRect(cell).Grow(-size * 0.05f);
        if (puzzle.IsWarden(cell))
        {
            // A warden chip: red, its light beating slowly.
            var body = CellRect(cell).Grow(-size * 0.22f);
            ChipArt.Draw(this, body, size / 520, dim: true);
            var beat = 0.5f + 0.5f * Mathf.Sin(_time * 2.4f);
            DrawRect(body.Grow(-size * 0.03f), new Color(Danger, 0.18f + 0.22f * beat));
            DrawGlyph("warden", body.Grow(-size * 0.07f), Danger, 1);
            return;
        }
        DrawRect(rect, Socket);
        DrawRect(rect, new Color(MenuTheme.Edge, 0.55f), false, 1.5f);
        // The socket's four pads where traces meet the next tile.
        foreach (var side in Sides)
            DrawCircle(rect.GetCenter() + Direction(side) * (size * 0.5f - size * 0.05f), size * 0.03f, new Color(0.64f, 0.56f, 0.22f, 0.5f));
    }

    private void DrawTile(HackPuzzle puzzle, int cell, bool live, float size)
    {
        var centre = CellRect(cell).GetCenter();
        var turn = _angle[cell] * Mathf.Pi / 2;
        var (cos, sin) = (Mathf.Cos(turn), Mathf.Sin(turn));
        var settled = Mathf.Abs(_angle[cell] - _goal[cell]) < 0.05f;
        var pulse = cell == puzzle.PulseCell;
        foreach (var side in Sides)
        {
            if ((puzzle.Base(cell) & side) == 0)
                continue;
            var d = Direction(side);
            var dir = new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
            var end = centre + dir * size * 0.5f;
            // The side this arm points to as the tile lies, once it has come to rest.
            var lying = HackPuzzle.Rotate(side, puzzle.Rotation(cell));
            var onWarden = live && settled && puzzle.Next(cell, lying) is var next && next >= 0 && puzzle.IsWarden(next);
            var colour = pulse || onWarden ? Danger : Broken && live ? MenuTheme.Bright : live ? MenuTheme.Glow : Unlit;
            var strength = live || pulse ? 1f : 0.7f;
            Trace([centre, end], colour.Lerp(Danger, _alarm), strength, size, live ? _surge : 0);
        }
        DrawCircle(centre, size * 0.075f, live ? new Color(MenuTheme.Glow, 0.9f) : new Color(Unlit, 0.8f));
        DrawCircle(centre, size * 0.035f, MenuTheme.Well);
    }

    /// <summary>Light running out from the probe along the reached tile's arms, as the hangar's traces carry it.</summary>
    private void DrawFlow(HackPuzzle puzzle, int cell, int depth, float size)
    {
        if (depth < 0 || Mathf.Abs(_angle[cell] - _goal[cell]) > 0.05f)
            return;
        var centre = CellRect(cell).GetCenter();
        var mask = puzzle.Mask(cell);
        foreach (var side in Sides)
        {
            if ((mask & side) == 0)
                continue;
            for (var pulse = 0; pulse < 2; pulse++)
            {
                var t = (_time * 0.9f - depth * 0.5f + pulse * 0.5f) % 1;
                if (t < 0)
                    t += 1;
                var at = centre + Direction(side) * size * 0.5f * t;
                var colour = Broken ? MenuTheme.Bright : MenuTheme.Glow;
                DrawCircle(at, size * 0.035f, new Color(colour, 0.85f));
                DrawCircle(at, size * 0.08f, new Color(colour, 0.16f));
            }
        }
    }

    /// <summary>A trace with the hangar's three layers of glow (HangarTraces.Run), wider in a surge.</summary>
    private void Trace(Vector2[] route, Color colour, float strength, float size, float surge)
    {
        var width = size / 70 * (1 + surge * 1.5f);
        if (surge > 0)
            colour = colour.Lerp(new Color(1, 1, 0.8f), surge * 0.8f);
        DrawPolyline(route, new Color(colour, 0.07f * strength + 0.2f * surge), 14 * width, true);
        DrawPolyline(route, new Color(colour, 0.18f * strength + 0.3f * surge), 7 * width, true);
        DrawPolyline(route, new Color(colour, 0.85f * strength + 0.15f * surge), 2.6f * width, true);
    }

    private void DrawPort(HackPort port, string glyph, Color colour, float light, bool joined, string? number, float red = 0)
    {
        var size = CellSize;
        var chip = PortChip(port);
        // The short trace from the chip into its cell's edge.
        var edge = CellRect(port.Cell).GetCenter() + Direction(port.Side) * size * 0.5f;
        var from = chip.GetCenter() - Direction(port.Side) * chip.Size.X * 0.5f;
        Trace([from, edge], joined ? colour : Unlit, joined ? 1 : 0.6f, size, joined ? _surge : 0);
        ChipArt.Draw(this, chip, size / 560, light, dim: light <= 0 && red <= 0);
        if (red > 0)
            DrawRect(chip.Grow(-size * 0.02f), new Color(Danger, red * 0.45f));
        DrawGlyph(glyph, chip.Grow(-chip.Size.X * 0.12f), colour, 1);
        if (number is not null)
        {
            // Its place in the order, printed beside the chip along the edge.
            var font = Mathf.RoundToInt(size * 0.24f);
            var beside = port.Side is HackPuzzle.N or HackPuzzle.S ? new Vector2(chip.Size.X * 0.5f + font * 0.55f, 0) : new Vector2(0, -chip.Size.Y * 0.5f - font * 0.55f);
            var at = chip.GetCenter() + beside;
            DrawString(MenuTheme.Font, new Vector2(at.X - font, at.Y + font * 0.36f), number, HorizontalAlignment.Center, font * 2, font, colour);
        }
        if (joined && _surge > 0)
            DrawArc(chip.GetCenter(), chip.Size.X * 0.5f + (1 - _surge) * size * 1.2f, 0, Mathf.Tau, 48, new Color(1, 1, 0.75f, _surge * 0.8f), 2.5f, true);
    }

    private void DrawGlyph(string name, Rect2 box, Color colour, float strength)
    {
        if (HangarGlyphs.All.GetValueOrDefault(name) is not { } lines)
            return;
        var scale = box.Size / HangarGlyphs.Grid;
        foreach (var line in lines)
        {
            var points = new Vector2[line.Length / 2];
            for (var i = 0; i < points.Length; i++)
                points[i] = box.Position + new Vector2(line[i * 2], line[i * 2 + 1]) * scale;
            DrawPolyline(points, new Color(colour, 0.22f * strength), Mathf.Max(3, box.Size.X / 14), true);
            DrawPolyline(points, new Color(colour, strength), Mathf.Max(1.2f, box.Size.X / 36), true);
        }
    }
}

/// <summary>
/// The hack's chip as it lies over the world: what is hacked and which board of how many, the board itself
/// (<see cref="HackBoard"/>), the trace-back as a row of LEDs running green, amber, red (<see cref="LedBar"/>) and how to
/// play it. Laid out in menu pixels; the host scales it (<see cref="HackHost"/>).
/// </summary>
public partial class HackPanel : ChipPanel
{
    private readonly Label _title, _stage, _hint;

    public HackPanel(HackKind kind, float boardSide)
    {
        Padding = 22;
        Kind = kind;
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 24);
        _title = MenuTheme.Label("", 30, MenuTheme.Bright);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _stage = MenuTheme.Label("", 24, MenuTheme.Dim, HorizontalAlignment.Right);
        _stage.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(_title);
        head.AddChild(_stage);
        column.AddChild(head);
        Board = new HackBoard { Kind = kind, CustomMinimumSize = new Vector2(boardSide, boardSide) };
        column.AddChild(Board);
        Trace = new LedBar(boardSide, 22) { Heat = true, Leds = 20, Caption = () => Lang.T("Trace-back") };
        column.AddChild(Trace);
        _hint = MenuTheme.Label("", 18, MenuTheme.Dim, HorizontalAlignment.Center);
        column.AddChild(_hint);
        AddChild(column);
    }

    /// <summary>What the hack is aimed at: the chip's title and the board's targets (the host's chip serves every hack).</summary>
    public HackKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            if (Board is not null)
                Board.Kind = value;
        }
    }

    private HackKind _kind;

    public HackBoard Board { get; }

    public LedBar Trace { get; }

    /// <summary>What a kind of target is called on the chip.</summary>
    public static string Called(HackKind kind) => kind switch
    {
        HackKind.Clamp => "Maintenance clamp",
        HackKind.Grunt => "Grunt",
        HackKind.Tower => "Tower",
        HackKind.Pod => "Escape pod",
        HackKind.Core => "Main computer",
        _ => "Door",
    };

    /// <summary>
    /// Shows where the hack stands: its board of how many, broken off or not, locked by its target (the core's last board at
    /// 90 %) or not, and the hint for the pad or the mouse.
    /// </summary>
    public void Show(int stage, int stages, bool broken, bool pad, bool held = false)
    {
        _title.Text = Lang.T("Hack: {0}", Lang.T(Called(Kind))).ToUpperInvariant();
        _stage.Text = broken ? Lang.T("Link cut").ToUpperInvariant()
            : held ? Lang.T("Core locked").ToUpperInvariant()
            : stages > 1 ? Lang.T("Board {0}/{1}", stage + 1, stages).ToUpperInvariant() : "";
        _stage.Modulate = broken || held ? HackBoard.Danger : Colors.White;
        Board.Broken = broken;
        _hint.Text = pad
            ? Lang.T("A: turn   X: turn back   B: pull out")
            : Lang.T("Click: turn   Right click: turn back   Esc: pull out");
    }
}
