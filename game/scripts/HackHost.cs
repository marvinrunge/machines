using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// The hack's sounds (docs/STORY.md, Klang): a click for a turn, a surge when a target or a board is reached, the
/// trace-back's warning, the firewall's pulse and the alarm. The story's audio (plan T4) can take them over by setting
/// <see cref="Player"/>; until then they play from <c>res://assets/story/sfx/&lt;id&gt;.ogg</c> (the generated effects,
/// game/assets/story/sfx/manifest.json), flat on the effects bus.
/// </summary>
public static class HackSounds
{
    public const string Click = "hack_click", Connect = "hack_connect", TraceBack = "hack_traceback", Firewall = "hack_firewall", Fail = "hack_fail";

    private static readonly Dictionary<string, AudioStream?> Streams = [];

    /// <summary>Plays a hack sound by its id, in place of the files here; set by the story's audio.</summary>
    public static Action<string>? Player { get; set; }

    /// <summary>Plays the sound under <paramref name="at"/> (a player of its own, freed when done).</summary>
    public static void Play(Node at, string id)
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_AUDIO_TRACE") == "1")
            GD.Print($"audio story/{id}");
        if (Player is { } player)
        {
            player(id);
            return;
        }
        if (!at.IsInsideTree() || Stream(id) is not { } stream)
            return;
        var sound = new AudioStreamPlayer { Stream = stream, Bus = AudioServer.GetBusIndex("Effects") >= 0 ? "Effects" : "Master" };
        at.AddChild(sound);
        sound.Finished += sound.QueueFree;
        sound.Play();
    }

    /// <summary>Loads the hack's sounds before a match begins (the warm-up), so the first board does not wait for them.</summary>
    public static void Preload()
    {
        foreach (var id in new[] { Click, Connect, TraceBack, Firewall, Fail })
            Stream(id);
    }

    private static AudioStream? Stream(string id)
    {
        if (Streams.TryGetValue(id, out var known))
            return known;
        var path = $"res://assets/story/sfx/{id}.ogg";
        return Streams[id] = ResourceLoader.Exists(path) ? GD.Load<AudioStream>(path) : null;
    }
}

/// <summary>
/// The hack over the running world (plan WP-E): the board's chip (<see cref="HackPanel"/>) at about 60 % of the window's
/// height in its middle, the world going on round it, as the hangar lies over a run (<see cref="HangarHost"/>), but not the
/// whole screen. The mouse is free and the camera stands; keys stay here. A click turns a tile clockwise, the right button
/// back; a gamepad moves a cursor over the cells (the pad's cross or left stick) and turns with A and X, and B or Esc pulls
/// the probe out. Every turn is a lockstep command; what is shown is the world's board. Up while the world has this
/// player at the target's hack; when that ends it plays out how (a surge, the red alarm, or just off) and goes.
/// </summary>
public partial class HackHost : Control
{
    /// <summary>The board's side in menu pixels: with its head, bar and hint the chip is about 60 % of the window high.</summary>
    public const float BoardSide = 470;

    private readonly Game _game;
    private Control _stage = null!;
    private HackPanel _panel = null!;
    private ShipFeed _feed = null!;
    private float _scale = 1;
    private HackPuzzle? _seen;
    private int _seenStage, _seenClaimed, _alarms;
    private bool _warned, _pulsing, _pad;
    private double _stickWait;
    private Tween? _going;

    /// <summary>
    /// How long a won board waits for its last tile to come to rest at most, and stands lit after its light has run
    /// through: the last board of a hack (with ACCESS GRANTED on it) before the chip goes, one of several before the next.
    /// </summary>
    public const double SettleWait = 0.5, WonHold = 1.7, BoardHold = 0.9;

    // The won board held on the chip (the world has gone on to the next board, or let go of the hack), its stage, and the
    // hold's clock: since it began, and when its light began to run (-1: its last tile still turning).
    private HackPuzzle? _held;
    private int _heldStage;
    private double _holdTime, _sweptAt = -1;

    /// <summary>
    /// The chip, made once a story match begins and hidden until a hack opens (<see cref="Open"/>): made afresh for each
    /// hack, its controls, theme and first layout held that frame up by 40 to 120 ms.
    /// </summary>
    public HackHost(Game game)
    {
        _game = game;
        Name = "HackHost";
    }

    /// <summary>The hack's target while it is up, or the last one.</summary>
    public string Target { get; private set; } = "";

    public HackKind Kind { get; private set; }

    /// <summary>The hack has ended and the chip is going, or is put away.</summary>
    public bool Closing { get; private set; } = true;

    /// <summary>
    /// The hack is won and its board stands lit a moment (<see cref="WonHold"/>) before the chip goes: still up over the
    /// world and taking the keys and the mouse, but no turn; Esc or the pad's B lets it go at once.
    /// </summary>
    public bool Holding { get; private set; }

    /// <summary>The chip gives the mouse and the keys back: when a hack ends, or after a won board's hold.</summary>
    public event Action? Released;

    /// <summary>How many times the chip came up: a new count is a new hack, for automated checks.</summary>
    public int Opened { get; private set; }

    /// <summary>For automated checks.</summary>
    public HackBoard Board => _panel.Board;

    private HackSession? Session => _game.World.Story?.Hacks.Session(Target);

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _stage = new Control { Theme = MenuTheme.Build(), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_stage);
        _panel = new HackPanel(Kind, BoardSide);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        centre.AddChild(_panel);
        _stage.AddChild(centre);
        _feed = new ShipFeed(_game);
        _stage.AddChild(_feed);
        Resized += Fit;
        Fit();
        _panel.Board.Source = () => _held ?? Session?.Puzzle;
        _panel.Board.Turned += Turn;
        PutAway();
    }

    /// <summary>Puts the chip up for the hack at <paramref name="target"/>, switching its screen on.</summary>
    public void Open(string target, HackKind kind)
    {
        _going?.Kill();
        _going = null;
        (Target, Kind, Closing, Holding, _held) = (target, kind, false, false, null);
        Opened++;
        _panel.Board.Win(false);
        _panel.Board.Stamp = null;
        (_seen, _seenStage, _seenClaimed, _warned, _pulsing, _pad, _stickWait) = (null, 0, 0, false, false, false, 0);
        _alarms = Session?.Alarms ?? 0;
        _panel.Kind = kind;
        _panel.Board.Cursor = -1;
        _panel.Trace.Value = 0;
        _panel.Trace.Snap();
        MouseFilter = MouseFilterEnum.Stop;
        _panel.MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Inherited;
        ProcessMode = ProcessModeEnum.Inherit;
        Modulate = Colors.White;
        Visible = true;
        if (Session is { } session)
            _panel.Show(session.Stage, session.Boards.Count, session.Broken, _pad, session.Held);
        _panel.SwitchOn(0);
        HackSounds.Play(this, HackSounds.Click);
    }

    /// <summary>Hidden and still until the next hack.</summary>
    private void PutAway()
    {
        _feed.Follow(false, _scale, 0);
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        ProcessMode = ProcessModeEnum.Disabled;
        _going = null;
    }

    /// <summary>
    /// A board as the hack puts it up, half played, for the warm-up to draw behind the loading screen: the board's code, its
    /// letters at their sizes and its drawing are then ready when the first hack comes up.
    /// </summary>
    public static Control Warm()
    {
        var root = new Control { Name = "WarmHack", MouseFilter = MouseFilterEnum.Ignore };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var stage = new Control { Theme = MenuTheme.Build(), MouseFilter = MouseFilterEnum.Ignore };
        root.AddChild(stage);
        var specs = HackSpec.Boards(HackKind.Pod);
        var puzzle = HackPuzzle.Generate(specs[0], 1);
        foreach (var (cell, clockwise) in puzzle.SolutionTurns().Take(3))
            puzzle.Turn(cell, clockwise);
        var panel = new HackPanel(HackKind.Pod, BoardSide);
        panel.Board.Source = () => puzzle;
        panel.Show(0, specs.Count, false, pad: false);
        panel.Trace.Value = 0.5f;
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        centre.AddChild(panel);
        stage.AddChild(centre);
        root.Resized += () =>
        {
            if (root.Size.Y <= 0)
                return;
            var scale = root.Size.Y / MenuStage.DesignHeight * MenuTheme.UiScale;
            stage.Scale = new Vector2(scale, scale);
            stage.Size = root.Size / scale;
            MenuTheme.FitFont(scale);
        };
        return root;
    }

    /// <summary>Scales the chip as the menus' stage is, so it is sharp at any window size.</summary>
    private void Fit()
    {
        if (Size.Y <= 0)
            return;
        var scale = Size.Y / MenuStage.DesignHeight * MenuTheme.UiScale;
        _stage.Scale = new Vector2(scale, scale);
        _stage.Size = Size / scale;
        _scale = scale;
        MenuTheme.FitFont(scale);
    }

    /// <summary>
    /// The ship's camera beside the core's board (<see cref="ShipFeed"/>), to its right with the same gap as the window
    /// leaves; up while the core is hacked and the chip is not going, and only where the window is wide enough for both.
    /// </summary>
    private void Feed(double delta)
    {
        var right = _stage.Size.X / 2 + _panel.Size.X / 2;
        var room = _stage.Size.X - right;
        var size = _feed.GetCombinedMinimumSize();
        _feed.Follow(!Closing && Target == "hack_core" && room >= size.X + 40, _scale, delta);
        if (_feed.Visible)
            _feed.Position = new Vector2(right + (room - size.X) / 2, (_stage.Size.Y - size.Y) / 2);
    }

    private void Turn(int cell, bool clockwise)
    {
        // Not on a won board held a moment: the world's next board is not the one shown.
        if (Closing || _held is not null || Session is not { Open: true, Broken: false })
            return;
        _game.Issue(new StoryHackTurnCommand(Game.Player, Target, cell, clockwise));
        HackSounds.Play(this, HackSounds.Click);
    }

    /// <summary>Pulls the probe out.</summary>
    public void Abort()
    {
        if (!Closing)
            _game.Issue(new StoryHackAbortCommand(Game.Player));
    }

    public override void _Process(double delta)
    {
        if (Holding)
        {
            Feed(delta);
            if (Swept(delta, WonHold))
                Leave(0);
            return;
        }
        if (Closing || Session is not { } session)
            return;
        _panel.Show(_held is null ? session.Stage : _heldStage, session.Boards.Count, session.Broken, _pad, session.Held, won: _held is not null);
        Feed(delta);
        _panel.Board.Cursor = _held is not null ? -1 : _pad ? Math.Max(0, _panel.Board.Cursor) : _panel.Board.Cursor;
        Listen(session, delta);
        Stick(delta);
    }

    /// <summary>
    /// A won board held on the chip: its last tile turns to rest, then its light runs through from the probe to the targets
    /// and the connect sounds; true once it has stood lit for <paramref name="hold"/> seconds.
    /// </summary>
    private bool Swept(double delta, double hold)
    {
        _holdTime += delta;
        if (_sweptAt < 0 && (_panel.Board.Settled || _holdTime >= SettleWait))
        {
            _sweptAt = _holdTime;
            _panel.Board.Win();
            HackSounds.Play(this, HackSounds.Connect);
        }
        return _sweptAt >= 0 && _holdTime - _sweptAt >= hold;
    }

    /// <summary>Holds a won board on the chip, as it lies after its last turn; the last of a hack with ACCESS GRANTED.</summary>
    private void Hold(HackPuzzle won, int stage, bool last)
    {
        (_held, _heldStage, _holdTime, _sweptAt) = (won, stage, 0, -1);
        _panel.Board.Cursor = -1;
        _panel.Board.Stamp = last ? Lang.T("Access granted") : null;
    }

    /// <summary>Sounds and light for what the world did since the last frame.</summary>
    private void Listen(HackSession session, double delta)
    {
        // A board of several won: it stands lit a moment, then the next comes up (the world's has been waiting, untouched).
        if (_held is not null)
        {
            if (Swept(delta, BoardHold))
                (_held, _seen) = (null, null);
            return;
        }
        if (session.Puzzle is not { } puzzle)
            return;
        if (!ReferenceEquals(puzzle, _seen))
        {
            // The next board of several: the last one was won, and is held as its last turn left it.
            if (_seen is { Complete: true } won && session.Stage > _seenStage)
            {
                Hold(won, _seenStage, last: false);
                return;
            }
            (_seen, _seenStage, _seenClaimed, _warned, _pulsing) = (puzzle, session.Stage, puzzle.Claimed, false, false);
            _panel.Trace.Snap();
            return;
        }
        if (puzzle.Claimed > _seenClaimed && !puzzle.Complete)
            HackSounds.Play(this, HackSounds.Connect);
        _seenClaimed = puzzle.Claimed;
        var hot = puzzle.TraceShare >= 0.75f;
        if (hot && !_warned)
            HackSounds.Play(this, HackSounds.TraceBack);
        _warned = hot;
        var pulsing = puzzle.PulseCell >= 0;
        if (pulsing && !_pulsing)
            HackSounds.Play(this, HackSounds.Firewall);
        _pulsing = pulsing;
        _panel.Trace.Value = puzzle.TraceShare;
    }

    /// <summary>
    /// The hack ended in the world: won, traced (the alarm) or left; the chip plays it out and goes. A won board is held
    /// (<see cref="Holding"/>): its last tile turns to rest, its light runs through to the targets, ACCESS GRANTED comes up
    /// and it stands lit for <see cref="WonHold"/> before the chip goes and gives the mouse back. The world is not kept
    /// waiting: what the hack opens opens on the tick it was won, round the chip.
    /// </summary>
    public void Finish()
    {
        if (Closing)
            return;
        Closing = true;
        _panel.MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled;
        var won = _game.World.Story?.Hacks.Done.Contains(Target) == true;
        var traced = (Session?.Alarms ?? 0) > _alarms;
        if (won && (Session?.Puzzle ?? _seen) is { } board)
        {
            // The chip still lies over the world and takes the clicks, so that none goes to the field.
            Holding = true;
            Hold(board, Session?.Stage ?? _seenStage, last: true);
            _panel.Show(_heldStage, Session?.Boards.Count ?? 1, false, _pad, won: true);
            return;
        }
        var wait = 0.0;
        if (won)
        {
            _panel.Board.Surge();
            HackSounds.Play(this, HackSounds.Connect);
            wait = 0.75;
        }
        else if (traced)
        {
            _panel.Board.Alarm();
            _panel.Trace.Value = 1;
            HackSounds.Play(this, HackSounds.Fail);
            wait = 0.7;
        }
        Leave(wait);
    }

    /// <summary>The chip switches off after <paramref name="wait"/> and fades, and the mouse and keys go back at once.</summary>
    private void Leave(double wait)
    {
        Holding = false;
        MouseFilter = MouseFilterEnum.Ignore;
        _panel.MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled;
        Released?.Invoke();
        _panel.SwitchOff(wait);
        // The chip fades with its screen: its frame stood empty and black for a few frames after the screen went out
        // (Erwischt opened on it).
        _going = CreateTween();
        _going.TweenInterval(wait);
        _going.TweenProperty(this, "modulate:a", 0f, 0.15);
        _going.TweenInterval(0.15);
        _going.TweenCallback(Callable.From(PutAway));
    }

    // ---- keys and the gamepad ----------------------------------------------------------------------

    public override void _Input(InputEvent @event)
    {
        // The mouse moved: its pointer leads again, and the pad's cursor goes until the pad is used.
        if (@event is InputEventMouseMotion { Relative: var moved } && moved.LengthSquared() > 4 && _pad)
        {
            _pad = false;
            _panel.Board.Cursor = -1;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // A won board held a moment takes every key and button; Esc or B lets it go at once.
        if (Holding && @event is InputEventKey or InputEventJoypadButton)
        {
            GetViewport().SetInputAsHandled();
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } or InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B or JoyButton.Back })
                Leave(0);
            return;
        }
        if (Closing || @event is not (InputEventKey or InputEventJoypadButton))
            return;
        GetViewport().SetInputAsHandled();
        switch (@event)
        {
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                Abort();
                break;
            case InputEventKey { Pressed: true } key:
                Keyboard(key.Keycode);
                break;
            case InputEventJoypadButton { Pressed: true } button:
                _pad = true;
                Pad(button.ButtonIndex);
                break;
        }
    }

    private void Keyboard(Key key)
    {
        switch (key)
        {
            case Key.Up or Key.W: Move(0, -1); break;
            case Key.Down or Key.S: Move(0, 1); break;
            case Key.Left or Key.A: Move(-1, 0); break;
            case Key.Right or Key.D: Move(1, 0); break;
            case Key.Space or Key.Enter or Key.KpEnter or Key.E: TurnCursor(true); break;
            case Key.Q or Key.Backspace: TurnCursor(false); break;
        }
    }

    private void Pad(JoyButton button)
    {
        switch (button)
        {
            case JoyButton.DpadUp: Move(0, -1); break;
            case JoyButton.DpadDown: Move(0, 1); break;
            case JoyButton.DpadLeft: Move(-1, 0); break;
            case JoyButton.DpadRight: Move(1, 0); break;
            case JoyButton.A: TurnCursor(true); break;
            case JoyButton.X or JoyButton.LeftShoulder: TurnCursor(false); break;
            case JoyButton.RightShoulder: TurnCursor(true); break;
            case JoyButton.B or JoyButton.Back: Abort(); break;
        }
    }

    /// <summary>The left stick steps the cursor too, a cell at a time while it is held over.</summary>
    private void Stick(double delta)
    {
        _stickWait -= delta;
        var (x, y) = (Input.GetJoyAxis(0, JoyAxis.LeftX), Input.GetJoyAxis(0, JoyAxis.LeftY));
        if (Mathf.Abs(x) < 0.6f && Mathf.Abs(y) < 0.6f)
        {
            _stickWait = 0;
            return;
        }
        if (_stickWait > 0)
            return;
        _pad = true;
        _stickWait = 0.18;
        if (Mathf.Abs(x) > Mathf.Abs(y))
            Move(Math.Sign(x), 0);
        else
            Move(0, Math.Sign(y));
    }

    private void Move(int dx, int dy)
    {
        if (Session?.Puzzle is not { } puzzle)
            return;
        var cursor = _panel.Board.Cursor;
        if (cursor < 0)
        {
            _panel.Board.Cursor = puzzle.Probe.Cell;
            return;
        }
        var (x, y) = puzzle.At(cursor);
        _panel.Board.Cursor = puzzle.CellOf(Math.Clamp(x + dx, 0, puzzle.Size - 1), Math.Clamp(y + dy, 0, puzzle.Size - 1));
    }

    private void TurnCursor(bool clockwise)
    {
        if (_panel.Board.Cursor < 0)
        {
            Move(0, 0);
            return;
        }
        if (Session?.Puzzle is { } puzzle && puzzle.IsTile(_panel.Board.Cursor))
            Turn(_panel.Board.Cursor, clockwise);
    }
}

/// <summary>
/// The story's key for a hack until the story's own keys are set (plan WP-J: an act "Hack/Use" on the Keys page): U, or Y
/// on a gamepad, plugs the Locator into the nearest target in reach (<see cref="Game.TryOpenHack"/>).
/// </summary>
public partial class HackKey(Game game) : Node
{
    public override void _UnhandledInput(InputEvent @event)
    {
        if ((KeyMap.Is(@event, KeyMap.Act.Use) || @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.U } or InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.Y })
            && game.TryOpenHack())
            GetViewport().SetInputAsHandled();
    }
}

/// <summary>
/// For automated checks, from the menus' scene: MACHINES_HACK_SHOT=&lt;png&gt;,&lt;kind&gt;,&lt;seed&gt;[,&lt;turns&gt;] pictures a board
/// of that kind (door, clamp, grunt, tower, pod, core, core2, core3: the main computer's boards) made from the seed, with the
/// first <c>turns</c> of its solution played (default half of them) and, on a pod's, the firewall's pulse let out, on the
/// menus' circuit board, and quits. It needs no match and no game copy.
/// </summary>
public partial class HackShot(string path, string kind, int seed, int? turns) : Control
{
    private HackPanel _panel = null!;
    private double _time;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var stage = new MenuStage();
        AddChild(stage);
        var (target, board) = kind.ToLowerInvariant() switch
        {
            "door" => (HackKind.Door, 0),
            "clamp" => (HackKind.Clamp, 0),
            "grunt" => (HackKind.Grunt, 0),
            "tower" => (HackKind.Tower, 0),
            "pod" => (HackKind.Pod, 0),
            "core2" => (HackKind.Core, 1),
            "core3" => (HackKind.Core, 2),
            _ => (HackKind.Core, 0),
        };
        var specs = HackSpec.Boards(target);
        var puzzle = HackPuzzle.Generate(specs[board], seed);
        var solution = puzzle.SolutionTurns().ToList();
        foreach (var (cell, clockwise) in solution.Take(turns ?? solution.Count / 2))
            puzzle.Turn(cell, clockwise);
        if (puzzle.Firewall is not null)
        {
            // Until a pulse is out on the board and has run a tile, or a while.
            for (var step = 0; step < 30 && !(step > HackPuzzle.FirstPulse + 1 && puzzle.PulseCell >= 0); step++)
                puzzle.StepFirewall();
        }
        _panel = new HackPanel(target, HackHost.BoardSide);
        _panel.Board.Source = () => puzzle;
        _panel.Trace.Value = puzzle.TraceShare;
        _panel.Show(board, specs.Count, false, pad: false);
        _panel.Board.Cursor = solution.Count > 0 ? solution[^1].Cell : -1;
        var centre = new CenterContainer();
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        centre.AddChild(_panel);
        var page = new Control();
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        page.AddChild(centre);
        stage.Show(page);
        GD.Print($"hack shot: {target} board {board + 1}/{specs.Count} seed {seed}, {puzzle.TurnsMade} of {solution.Count} turns, " +
                  $"trace {puzzle.Trace}/{puzzle.Spec.Trace}, claimed {puzzle.Claimed}/{puzzle.Targets.Count}, pulse {puzzle.PulseCell}");
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (_time < 1.5)
            return;
        SetProcess(false);
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"hack shot saved to {path}");
        GetTree().Quit();
    }
}
