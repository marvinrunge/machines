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
    private HackPuzzle? _seen;
    private int _seenStage, _seenClaimed, _alarms;
    private bool _warned, _pulsing, _pad;
    private double _stickWait;

    public HackHost(Game game, string target, HackKind kind)
    {
        _game = game;
        Target = target;
        Kind = kind;
        Name = "HackHost";
    }

    public string Target { get; }

    public HackKind Kind { get; }

    /// <summary>The hack has ended and the chip is going.</summary>
    public bool Closing { get; private set; }

    /// <summary>For automated checks.</summary>
    public HackBoard Board => _panel.Board;

    private HackSession? Session => _game.World.Story?.Hacks.Session(Target);

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        _stage = new Control { Theme = MenuTheme.Build(), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_stage);
        _panel = new HackPanel(Kind, BoardSide);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        centre.AddChild(_panel);
        _stage.AddChild(centre);
        Resized += Fit;
        Fit();
        _panel.Board.Source = () => Session?.Puzzle;
        _panel.Board.Turned += Turn;
        _alarms = Session?.Alarms ?? 0;
        _panel.SwitchOn(0);
        HackSounds.Play(this, HackSounds.Click);
    }

    /// <summary>Scales the chip as the menus' stage is, so it is sharp at any window size.</summary>
    private void Fit()
    {
        if (Size.Y <= 0)
            return;
        var scale = Size.Y / MenuStage.DesignHeight * MenuTheme.UiScale;
        _stage.Scale = new Vector2(scale, scale);
        _stage.Size = Size / scale;
        MenuTheme.FitFont(scale);
    }

    private void Turn(int cell, bool clockwise)
    {
        if (Closing || Session is not { Open: true, Broken: false })
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
        if (Closing || Session is not { } session)
            return;
        _panel.Show(session.Stage, session.Boards.Count, session.Broken, _pad);
        _panel.Board.Cursor = _pad ? Math.Max(0, _panel.Board.Cursor) : _panel.Board.Cursor;
        Listen(session);
        Stick(delta);
    }

    /// <summary>Sounds and light for what the world did since the last frame.</summary>
    private void Listen(HackSession session)
    {
        if (session.Puzzle is not { } puzzle)
            return;
        if (!ReferenceEquals(puzzle, _seen))
        {
            // The next board of several: the last one was won.
            if (_seen is not null && session.Stage > _seenStage)
            {
                _panel.Board.Surge();
                HackSounds.Play(this, HackSounds.Connect);
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

    /// <summary>The hack ended in the world: won (a surge), traced (the alarm) or left; the chip plays it out and goes.</summary>
    public void Finish()
    {
        if (Closing)
            return;
        Closing = true;
        MouseFilter = MouseFilterEnum.Ignore;
        _panel.MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled;
        var won = _game.World.Story?.Hacks.Done.Contains(Target) == true;
        var traced = (Session?.Alarms ?? 0) > _alarms;
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
        _panel.SwitchOff(wait);
        var tween = CreateTween();
        tween.TweenInterval(wait + 0.3);
        tween.TweenCallback(Callable.From(() => GetParent().QueueFree()));
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
