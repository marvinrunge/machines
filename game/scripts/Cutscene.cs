using Godot;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// A cutscene played live in a match (the remake's own, for the story): the camera is taken from the player for the takes of
/// a script (<see cref="CutsceneScript"/>, the deck part on a story deck, the outside part elsewhere), the panel goes, black
/// bars slide in, the player's keys and mouse do nothing (<see cref="Game.InCutscene"/>) and their machine stands. It ends
/// after its last take, or when the player holds Esc or clicks, blending back to the first-person view, and tells so
/// (<see cref="Finished"/>). It only films and sounds: the world goes on as the story director has it, tick by tick, so a
/// skip leaves it as a watched cutscene would. It never slows time, nor touches the volume, the exposure or the quality the
/// way a film does.
/// </summary>
public partial class Cutscene : FilmCamera
{
    /// <summary>The cutscene is over, the player has the view back; <paramref name="skipped"/> if they skipped it.</summary>
    [Signal]
    public delegate void FinishedEventHandler(bool skipped);

    /// <summary>
    /// What a cutscene takes from the match and gives back: the strategy camera, the first-person camera, the film look, the
    /// panel, the player's machine (made driven again at the end), the view's lean, voice lines and words for the story.
    /// </summary>
    public sealed record Stage(
        CameraRig Rig,
        Func<Camera3D?> PlayerCamera,
        FilmLook? Look,
        Action<bool> ShowPanel,
        Action GiveBack,
        int? Hero,
        Func<CameraTilt>? TiltNow = null,
        Action<CameraTilt, float>? TiltTo = null,
        Func<string, bool>? Speak = null,
        Action<string>? Tell = null);

    /// <summary>Seconds Esc is held to skip.</summary>
    public const float HoldToSkip = 0.6f;

    private readonly CutsceneScript _script;
    private readonly CutscenePart _playing;
    private readonly Stage _stage;
    private Shot[] _shots = [];
    private double _time;
    private int _shot = -1;

    private enum Phase { Playing, Leaving, Done }
    private Phase _phase;
    private float _leaving;
    private Transform3D _leftFrom;
    private float _leftFov;
    private bool _skipped;
    private double? _escapeSince;

    // What was so before, given back at the end.
    private Camera3D? _before;
    private bool _bars, _film, _rigActive;

    private readonly CanvasLayer _hintLayer = new() { Layer = 2, Name = "SkipHint" };
    private readonly Label _hint = new() { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };

    private readonly SortedSet<float> _stills = [];
    private string? _stillFolder;

    public Cutscene(GameWorld world, Func<int, Node3D?> view, CutsceneScript script, CutscenePart part, Stage stage)
        : base(world, view, new Camera3D { Far = 9000, Near = 0.1f, Fov = 62, Name = "CutsceneCamera" })
    {
        _script = script;
        _playing = part;
        _stage = stage;
        Hero = stage.Hero;
        Name = "Cutscene";
        // After the game has placed the views and its own cameras this frame.
        ProcessPriority = 1000;
    }

    /// <summary>The script's name.</summary>
    public string Title => _script.Name;

    /// <summary>Seconds of the part played, all takes together.</summary>
    public float Length => _playing.Length;

    protected override FilmLook? Look => _stage.Look;

    /// <summary>Saves the picture at these seconds of the cutscene into <paramref name="folder"/>, and quits the game after the last.</summary>
    public void Stills(string folder, IEnumerable<float> seconds)
    {
        _stillFolder = folder;
        foreach (var s in seconds)
            _stills.Add(s);
    }

    public override void _Ready()
    {
        _before = GetViewport().GetCamera3D();
        AddChild(_camera);
        if (_before is not null)
        {
            _camera.GlobalTransform = _before.GlobalTransform;
            _camera.Fov = _before.Fov;
        }
        _camera.MakeCurrent();
        _bars = ActorView.Bars;
        _film = GameAudio.Film;
        _rigActive = _stage.Rig.Active;
        ActorView.Bars = false;
        // Every sound heard from where the camera is; the machines' own voices keep quiet.
        GameAudio.Film = true;
        if (_script.World != 0)
            StoryAudio.FilmLevel(_script.World, 0.5f);
        _stage.Rig.Active = false;
        _stage.ShowPanel(false);
        if (_stage.Look is { } look && !GameOptions.FilmLook)
        {
            // Bars without the film look the player turned off.
            look.Vignette = 0;
            look.Grain = 0;
            look.Grade = 0;
        }
        AddChild(_hintLayer);
        _hint.Text = Lang.T("Hold Esc to skip");
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.Modulate = new Color(1, 1, 1, 0.75f);
        _hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _hint.GrowHorizontal = Control.GrowDirection.Begin;
        _hint.GrowVertical = Control.GrowDirection.Begin;
        _hint.Position -= new Vector2(24, 24);
        _hintLayer.AddChild(_hint);
        _shots = Takes(_playing);
        GD.Print($"cutscene: {_script.Name} ({_playing.Name}) starts at frame {Engine.GetFramesDrawn()}, {_playing.Length:0.0} s in {_shots.Length} takes");
    }

    // ---- the player's hands -----------------------------------------------------------------------

    /// <summary>
    /// Every press and every turn of the mouse is the cutscene's while it plays: none reaches the match. Letting go of a key
    /// or button still does, so nothing stays held. Esc held skips, and so does a click.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (_phase == Phase.Done)
            return;
        switch (@event)
        {
            case InputEventKey { Keycode: Key.Escape } key:
                if (key.Pressed && !key.Echo)
                    _escapeSince = _time;
                else if (!key.Pressed)
                    _escapeSince = null;
                GetViewport().SetInputAsHandled();
                return;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right } when _time > 0.3:
                Skip();
                GetViewport().SetInputAsHandled();
                return;
            case InputEventKey { Pressed: false } or InputEventMouseButton { Pressed: false }:
                return;
            default:
                GetViewport().SetInputAsHandled();
                return;
        }
    }

    /// <summary>Ends the cutscene now (cues still to come are dropped), blending back to the player's view.</summary>
    public void Skip()
    {
        if (_phase != Phase.Playing)
            return;
        _skipped = true;
        GD.Print($"cutscene: {_script.Name} skipped at {_time:0.00} s");
        DropCues();
        End(black: false);
    }

    // ---- playing --------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        _step = (float)delta;
        switch (_phase)
        {
            case Phase.Playing:
                Play();
                break;
            case Phase.Leaving:
                Blend();
                break;
        }
    }

    private void Play()
    {
        _time += _step;
        var start = 0f;
        var index = 0;
        while (index < _shots.Length - 1 && _time >= start + _shots[index].Length)
            start += _shots[index++].Length;
        var shot = _shots[index];
        var cut = index != _shot;
        if (cut)
        {
            var first = _shot < 0;
            _shot = index;
            Enter(index);
            // The first view from inside the player's machine looks where the player looked.
            if (first && _playing.Takes[0].Kind == "cockpit" && _before is not null)
                _followed["look"] = -_before.GlobalBasis.Z;
            GD.Print($"cutscene: take {index + 1} ({shot.Name}) at {_time:0.00} s, tick {_world.Tick}");
            if (HitchLog.On)
                HitchLog.Phase = $"cutscene {shot.Name}";
        }
        var u = Mathf.Clamp((float)(_time - start) / shot.Length, 0, 1);
        Scene((float)_time);
        Show(_last = shot.At(u), cut);
        if (_stage.Look is { } look)
        {
            look.Visible = true;
            look.Bars = _script.Bars * Ease(Mathf.Clamp((float)_time / 0.4f, 0, 1));
            look.Fade = ScriptFade;
        }
        if (_escapeSince is { } since)
        {
            var held = (float)(_time - since);
            _hint.Visible = true;
            _hint.Text = $"{Lang.T("Hold Esc to skip")} {new string('|', (int)(Mathf.Clamp(held / HoldToSkip, 0, 1) * 10))}";
            if (held >= HoldToSkip)
                Skip();
        }
        else
            _hint.Visible = false;
        TakeStills();
        if (_phase == Phase.Playing && _time >= _playing.Length)
            End(black: ScriptFade >= 0.99f);
    }

    // The last take is over (or skipped): back to the player's view, at once when the picture ended black.
    private void End(bool black)
    {
        _phase = Phase.Leaving;
        _hint.Visible = false;
        // Skipped in space: back to the match's picture before it blends to the player's view.
        LeaveSpace();
        _leftFrom = _camera.GlobalTransform;
        _leftFov = _camera.Fov;
        _leaving = 0;
        if (black || PlayerCamera() is null)
        {
            Finish(black);
            return;
        }
        // The blend ends in the player's head: their machine is hidden from this camera as from theirs.
        _stage.GiveBack();
        _camera.CullMask = ~ActorView.DrivenLayer & 0xFFFFF;
    }

    private Camera3D? PlayerCamera() =>
        _stage.PlayerCamera() is { } camera && IsInstanceValid(camera) && camera.IsInsideTree() ? camera : null;

    private void Blend()
    {
        _leaving += _step;
        var share = Ease(Mathf.Clamp(_leaving / Mathf.Max(_script.Blend, 0.01f), 0, 1));
        if (PlayerCamera() is not { } player)
        {
            Finish(false);
            return;
        }
        _camera.GlobalTransform = _leftFrom.InterpolateWith(player.GlobalTransform, share);
        _camera.Fov = Mathf.Lerp(_leftFov, player.Fov, share);
        if (_stage.Look is { } look)
        {
            look.Visible = true;
            look.Bars = _script.Bars * (1 - share);
            look.Fade = ScriptFade * (1 - share);
        }
        if (share >= 1)
            Finish(false);
    }

    private void Finish(bool black)
    {
        if (_phase == Phase.Done)
            return;
        GiveBack(black);
        GD.Print($"cutscene: {_script.Name} ends at frame {Engine.GetFramesDrawn()}{(_skipped ? ", skipped" : "")}");
        EmitSignal(SignalName.Finished, _skipped);
        QueueFree();
        // Pictures asked for past the end are not coming.
        if (_stillFolder is not null)
            GetTree().Quit();
    }

    /// <summary>Everything as it was before, the player's camera current again (the picture kept black if it ended so).</summary>
    private void GiveBack(bool black)
    {
        if (_phase == Phase.Done)
            return;
        _phase = Phase.Done;
        Cut();
        _stage.GiveBack();
        EndScene();
        ActorView.Bars = _bars;
        GameAudio.Film = _film;
        if (_script.World != 0)
            StoryAudio.FilmLevel(0, 2.5f);
        if (IsInstanceValid(_stage.Rig))
            _stage.Rig.Active = _rigActive;
        _stage.ShowPanel(true);
        if (_stage.Look is { } look)
        {
            look.Match();
            look.Bars = 0;
            look.Fade = black ? 1 : 0;
        }
        if ((PlayerCamera() ?? (_before is not null && IsInstanceValid(_before) && _before.IsInsideTree() ? _before : null)) is { } back)
            back.MakeCurrent();
    }

    public override void _ExitTree() => GiveBack(false);

    // ---- what the script hands on to the match --------------------------------------------------------

    protected override CameraTilt TiltNow() => _stage.TiltNow?.Invoke() ?? base.TiltNow();

    protected override void Tilt(CameraTilt to, float ease)
    {
        if (_stage.TiltTo is { } tilt)
            tilt(to, ease);
        else
            base.Tilt(to, ease);
    }

    protected override void Say(string id)
    {
        if (_stage.Speak?.Invoke(id) != true)
            base.Say(id);
    }

    protected override void Event(string words)
    {
        base.Event(words);
        _stage.Tell?.Invoke(words);
    }

    private void TakeStills()
    {
        if (_stillFolder is null || _stills.Count == 0 || _time < _stills.Min)
            return;
        var at = _stills.Min;
        _stills.Remove(at);
        DirAccess.MakeDirRecursiveAbsolute(_stillFolder);
        var file = System.IO.Path.Combine(_stillFolder, $"{_script.Name}-{_playing.Name}_{at:00.0}.png".Replace(',', '.'));
        GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"cutscene: still {file}");
        if (_stills.Count == 0)
            GetTree().Quit();
    }
}
