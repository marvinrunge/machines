using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// Short films of matches, the remake's own (MACHINES_CINEMATIC=&lt;film&gt; with the film's MACHINES_MAP, as
/// tools/cinematic/films.txt lists them): a film gives the armies of its map their orders and films them with a camera
/// of its own, take after take, with depth of field, black bars, graded colours, grain and fades, the panel hidden.
/// Time can slow within a take. Godot's movie writer records it frame by frame (tools/cinematic/render.sh,
/// shoot.sh), with <c>--fixed-fps</c> and the orders given on ticks, so the battle plays the same each time and at
/// every frame rate. MACHINES_CINEMATIC_STILLS=&lt;folder&gt;;&lt;seconds&gt;,... saves the picture at those moments of
/// the film instead and quits after the last.
/// </summary>
public partial class Cinematic : FilmCamera
{
    /// <summary>The film asked for, if any.</summary>
    public static string? Asked => System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC") is { Length: > 0 } name ? name : null;

    // Black bars that make the 16:9 picture 2.39:1.
    private const float Bars = (1 - 16f / 9 / 2.39f) / 2;

    private readonly Func<int, Node3D?> _round;
    private readonly CameraRig _rig;
    private readonly FilmLook _look = new();
    protected override FilmLook? Look => _look;
    private readonly CanvasLayer _titles = new() { Layer = 0, Name = "Titles" };
    private Control? _title;
    private Shot[] _shots = [];
    private float _length;

    // The trailer's title and when it comes; how long the film fades in, and whether it fades out at its end (the
    // other films are footage to be cut).
    private float? _titleAt;
    private float _fadeIn = 0.5f;
    private bool _fadeOut;
    private float _exposure = 1.7f;

    // The film's own time, which slowed time does not slow; the take it is in.
    private double _time;
    private int _shot = -1;

    // The orders, by the tick after which they are given, and what is done after every tick.
    private readonly List<(int Tick, Action Do)> _cues = [];
    private readonly List<Action> _everyTick = [];

    private readonly SortedSet<float> _stills = [];
    private string? _stillFolder;

    /// <param name="view">The view of a machine or building by its id.</param>
    /// <param name="round">The view of a flying projectile by its id.</param>
    public Cinematic(GameWorld world, Func<int, Node3D?> view, Func<int, Node3D?> round, CameraRig rig)
        : base(world, view, new Camera3D { Far = 6000, Near = 0.25f, Fov = 40, Name = "FilmCamera" })
    {
        _round = round;
        _rig = rig;
        Name = "Cinematic";
        // After the views have moved this frame.
        ProcessPriority = 1000;
    }

    public override void _Ready()
    {
        AddChild(_camera);
        _camera.MakeCurrent();
        _rig.Active = false;
        ActorView.Bars = false;
        // Every sound heard from where the camera is, the machines' engines too.
        GameAudio.Film = true;
        // The recording clips where the whole battle fires at once; it is made quieter here, louder in the mix.
        AudioServer.SetBusVolumeDb(0, -20);
        AddChild(_look);
        _look.Vignette = 0.38f;
        _look.Grain = 0.05f;
        _look.Grade = 1;
        _look.Bars = Bars;
        _look.Fade = 1;
        AddChild(_titles);

        // A film can be shot more than once, each time with other takes of the same battle: "gorilla2" is the second
        // pass over the gorilla film.
        var film = Asked!.TrimEnd("0123456789".ToCharArray());
        var pass = int.TryParse(Asked[film.Length..], out var p) ? p : 1;
        _shots = film switch
        {
            "trailer" => Trailer(),
            "gorilla" => Gorilla(pass),
            "build" => Build(),
            "ice" => Ice(pass),
            "strike" => Strike(pass),
            _ when film.StartsWith("prolog-", StringComparison.Ordinal) => Scripted(film["prolog-".Length..]),
            _ => throw new InvalidOperationException($"There is no film '{Asked}' (see tools/cinematic/films.txt)."),
        };
        Quality();
        _length = _shots.Sum(s => s.Length);
        if (_titleAt is not null)
        {
            _title = Title();
            _titles.AddChild(_title);
        }
        if (System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC_STILLS")?.Split(';') is [var folder, var times])
        {
            _stillFolder = folder;
            foreach (var t in times.Split(',', StringSplitOptions.RemoveEmptyEntries))
                _stills.Add(float.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        }
        GD.Print($"cinematic: {Asked} starts at frame {Engine.GetFramesDrawn()}, {_length:0.0} s in {_shots.Length} takes");
    }

    /// <summary>
    /// The picture as fine as it gets, whatever it costs: drawn at 1.5 times the size and scaled down
    /// (MACHINES_CINEMATIC_SCALE to change it), the finest models at every distance, large soft shadows.
    /// </summary>
    private void Quality()
    {
        var viewport = GetViewport();
        // MACHINES_CINEMATIC_SIZE=2560x1440 draws the film at that size whatever the window's (the movie writer
        // keeps the project's window size and records what the window's viewport draws).
        if (System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC_SIZE")?.Split('x') is [var w, var h])
        {
            GetTree().Root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
            GetTree().Root.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore;
            GetTree().Root.ContentScaleSize = new Vector2I(int.Parse(w), int.Parse(h));
        }
        var scale = float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1.5f;
        viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
        viewport.Scaling3DScale = scale;
        // Multisampled, as the grass's blades are cut out for (alpha to coverage): without it they shimmered from frame
        // to frame, and the depth of field spread the shimmer into flickering patches.
        viewport.Msaa3D = Viewport.Msaa.Msaa4X;
        viewport.MeshLodThreshold = 0;
        RenderingServer.DirectionalShadowAtlasSetSize(8192, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftUltra);
        RenderingServer.PositionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftHigh);
        // Exposed brighter than the match (MACHINES_CINEMATIC_EXPOSURE, or the film's own): the grading and the dark
        // corners take light away.
        var exposure = float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC_EXPOSURE"), System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : _exposure;
        foreach (var world in GetTree().Root.FindChildren("*", nameof(WorldEnvironment), true, false).OfType<WorldEnvironment>())
            world.Environment.TonemapExposure *= exposure;
    }

    // ---- running the film ----------------------------------------------------------------------

    /// <summary>After each tick of the world: the orders due and what the film does every tick.</summary>
    public void OnTick()
    {
        foreach (var cue in _cues.Where(c => c.Tick <= _world.Tick).ToList())
        {
            cue.Do();
            _cues.Remove(cue);
        }
        foreach (var each in _everyTick)
            each();
        // What happens when, for timing the takes.
        foreach (var e in _world.Events.Where(e => e.Kind is GameEventKind.Blast or GameEventKind.Destroyed or GameEventKind.RaceChanged or GameEventKind.ConstructionComplete))
            GD.Print($"cinematic: {_world.Time:0.00} s {e.Kind} {e.Detail} {e.Actor} at {e.Position.X:0},{e.Position.Y:0}");
    }

    // An order given after the tick at that many seconds of the match.
    private void At(float seconds, Action order) => _cues.Add(((int)MathF.Round(seconds * GameWorld.TicksPerSecond), order));

    public override void _Process(double delta)
    {
        _step = (float)(delta / Math.Max(Engine.TimeScale, 0.001));
        _time += _step;

        // The take now, and how far into it.
        var start = 0f;
        var index = 0;
        while (index < _shots.Length - 1 && _time >= start + _shots[index].Length)
            start += _shots[index++].Length;
        var shot = _shots[index];
        var cut = index != _shot;
        if (cut)
        {
            // A cut: what was followed is picked up afresh.
            _shot = index;
            Enter(index);
            GD.Print($"cinematic: take {index + 1} ({shot.Name}) at {_time:0.00} s, tick {_world.Tick}");
        }
        var u = Mathf.Clamp((float)(_time - start) / shot.Length, 0, 1);
        Scene((float)_time);
        var frame = _last = shot.At(u);
        var speed = shot.Speed?.Invoke(u) ?? 1;
        Engine.TimeScale = speed;
        // Slowed time sounds lower, though less slowed than it is.
        AudioServer.PlaybackSpeedScale = Mathf.Lerp(1, speed, 0.6f);

        Show(frame, cut);

        // The weather and the shadows' reach follow what the camera looks at.
        _rig.Position = Ground(frame.Target.X, frame.Target.Z);
        _rig.Distance = Mathf.Max(150, frame.Eye.DistanceTo(frame.Target));

        // Fades from and to black, the title coming on its beat.
        var t = (float)_time;
        _look.Fade = Mathf.Max(1 - Mathf.SmoothStep(0, _fadeIn, t) * (_fadeOut ? 1 - Mathf.SmoothStep(_length - 1.8f, _length, t) : 1), ScriptFade);
        if (_title is not null && _titleAt is { } titleAt)
            _title.Modulate = new Color(1, 1, 1, Mathf.SmoothStep(titleAt - 0.1f, titleAt + 0.3f, t) * (1 - Mathf.SmoothStep(_length - 0.6f, _length, t)));

        TakeStills();
        if (_time >= _length + 0.05)
        {
            Engine.TimeScale = 1;
            AudioServer.PlaybackSpeedScale = 1;
            GD.Print($"cinematic: ends at frame {Engine.GetFramesDrawn()}");
            GetTree().Quit();
        }
    }

    private void TakeStills()
    {
        if (_stillFolder is null || _stills.Count == 0 || _time < _stills.Min)
            return;
        var at = _stills.Min;
        _stills.Remove(at);
        DirAccess.MakeDirRecursiveAbsolute(_stillFolder);
        var file = System.IO.Path.Combine(_stillFolder, $"{Asked}_{at:00.0}.png".Replace(',', '.'));
        GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"cinematic: still {file}");
        if (_stills.Count == 0)
            GetTree().Quit();
    }

    private Control Title()
    {
        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var height = GetViewport().GetVisibleRect().Size.Y;
        var name = MenuTheme.Label("M A C H I N E S", (int)(height * 0.09f), new Color(0.95f, 0.93f, 0.88f), HorizontalAlignment.Center);
        var line = MenuTheme.Label("R E M A K E", (int)(height * 0.025f), new Color(0.85f, 0.7f, 0.45f), HorizontalAlignment.Center);
        box.AddChild(name);
        box.AddChild(line);
        return box;
    }
}
