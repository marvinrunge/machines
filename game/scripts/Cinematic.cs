using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// A short film of a match, the remake's own (MACHINES_CINEMATIC=trailer with MACHINES_MAP=trailer,hilly): it gives
/// the armies of data/maps/trailer.scn their orders and films them with a camera of its own, take after take, with
/// depth of field, black bars, graded colours, grain and fades, the panel hidden. Time can slow within a take.
/// Godot's movie writer records it frame by frame (tools/cinematic/render.sh), with <c>--fixed-fps</c> so the battle
/// plays the same each time. MACHINES_CINEMATIC_STILLS=&lt;folder&gt;;&lt;seconds&gt;,... saves the picture at those
/// moments of the film instead and quits after the last.
/// </summary>
public partial class Cinematic : Node
{
    /// <summary>The film asked for, if any.</summary>
    public static string? Asked => System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC") is { Length: > 0 } name ? name : null;

    /// <summary>A take: how long it lasts, the camera at each moment of it (0 to 1), and how fast time runs then.</summary>
    private sealed record Shot(string Name, float Length, Func<float, Frame> At, Func<float, float>? Speed = null);

    /// <summary>Where the camera is and looks, its field of view (degrees), its focus (metres) and how much it blurs.</summary>
    private readonly record struct Frame(Vector3 Eye, Vector3 Target, float Fov = 40, float? Focus = null, float Blur = 1.5f);

    // Black bars that make the 16:9 picture 2.39:1.
    private const float Bars = (1 - 16f / 9 / 2.39f) / 2;

    // Where the takes change: on the strong beats of the original's intro music (Track 00), which starts with the
    // film: a bar every 1.845 s up to 7.4 s, and again from 10.5 s. The title comes on the beat at 27.24 s.
    private static readonly float[] Cuts = [0, 3.69f, 7.38f, 10.51f, 14.2f, 18.01f, 19.85f, 21.69f, 25.39f, 30];
    private const float TitleAt = 27.24f;

    // The missile launcher is held unloaded until it fires on the cut to its take.
    private const float Launch = 10.45f;

    private readonly GameWorld _world;
    private readonly Func<int, Node3D?> _view;
    private readonly Func<int, Node3D?> _round;
    private readonly CameraRig _rig;
    private readonly Camera3D _camera = new() { Far = 6000, Near = 0.25f, Fov = 40, Name = "FilmCamera" };
    private readonly FilmLook _look = new();
    private readonly CanvasLayer _titles = new() { Layer = 0, Name = "Titles" };
    private Control _title = null!;
    private Shot[] _shots = [];
    private float _length;

    // The film's own time, which slowed time does not slow; the take it is in.
    private double _time;
    private int _shot = -1;
    private float _step;

    // The cast: who is filmed and ordered about.
    private List<int> _horde = [], _line = [], _builders = [];
    private int _knight, _launcher;
    private int? _missile;
    private readonly List<(float At, Action Do)> _cues = [];

    // Points the camera follows smoothly, or holds from the start of a take, and who a take picked to film.
    private readonly Dictionary<string, Vector3> _followed = [];
    private int? _runner;

    private readonly SortedSet<float> _stills = [];
    private string? _stillFolder;

    /// <param name="view">The view of a machine or building by its id.</param>
    /// <param name="round">The view of a flying projectile by its id.</param>
    public Cinematic(GameWorld world, Func<int, Node3D?> view, Func<int, Node3D?> round, CameraRig rig)
    {
        _world = world;
        _view = view;
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
        _title = Title();
        _titles.AddChild(_title);

        Quality();
        Cast();
        _shots = Trailer();
        _length = _shots.Sum(s => s.Length);
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
        viewport.MeshLodThreshold = 0;
        RenderingServer.DirectionalShadowAtlasSetSize(8192, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftUltra);
        RenderingServer.PositionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftHigh);
        // Exposed brighter than the match (MACHINES_CINEMATIC_EXPOSURE): the grading and the dark corners take light away.
        var exposure = float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC_EXPOSURE"), System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : 1.7f;
        foreach (var world in GetTree().Root.FindChildren("*", nameof(WorldEnvironment), true, false).OfType<WorldEnvironment>())
            world.Environment.TonemapExposure *= exposure;
    }

    // ---- the trailer ---------------------------------------------------------------------------

    /// <summary>Picks out the players of data/maps/trailer.scn and when they get their orders.</summary>
    private void Cast()
    {
        List<int> Of(Race race, string type, string? sub = null) =>
            _world.Machines.Where(m => m.Race == race && m.IsType(type, sub)).Select(m => m.Id).ToList();
        int Nearest(IEnumerable<Actor> actors, float x, float y) =>
            actors.OrderBy(a => NVector2.Distance(a.Position, new NVector2(x, y))).FirstOrDefault()?.Id ?? -1;

        _horde = _world.Machines.Where(m => m.Race == Race.Blue).Select(m => m.Id).ToList();
        _line = Of(Race.Red, "AGGRESSOR");
        _builders = Of(Race.Red, "CONSTRUCTOR");
        _knight = Nearest(_world.Machines.Where(m => m.Race == Race.Red && m.IsType("AGGRESSOR", "KNIGHT")), 1160, 740);
        _launcher = Nearest(_world.Constructions.Where(c => c.Race == Race.Red && c.IsType("MISSILE_EMPLACEMENT", "LAUNCHER")), 1235, 700);

        // Two pairs of constructors start a turret each before the line; the horde comes down the valley.
        _cues.Add((0, () =>
        {
            Place(_builders.Take(2).ToList(), 1112, 690);
            Place(_builders.Skip(2).ToList(), 1172, 690);
        }));
        _cues.Add((1, () => _world.Issue(new MoveCommand(Race.Blue, _horde, new NVector2(1165, 700)))));
        // The launcher, held unloaded until then (see _Process), fires at the horde as its take begins.
        _cues.Add((Launch, () =>
        {
            foreach (var weapon in Launcher()?.Weapons ?? [])
                weapon.Reload = 0;
        }));
    }

    private Construction? Launcher() => _world.FindTarget(_launcher) as Construction;

    private void Place(List<int> constructors, float x, float y)
    {
        var centre = new NVector2(x, y);
        var spot = Enumerable.Range(0, 400).Select(i => centre + new NVector2(i % 20 - 10, i / 20 - 10) * 3)
            .OrderBy(p => NVector2.Distance(p, centre))
            .FirstOrDefault(p => _world.WhyCannotPlace(Race.Red, "MISSILE_EMPLACEMENT", "TURRET", 2, p, 0) is null, centre);
        _world.Issue(new PlaceConstructionCommand(Race.Red, constructors, "MISSILE_EMPLACEMENT", "TURRET", 2, spot, 0));
    }

    /// <summary>
    /// Thirty seconds, cut on the music's beats: low over the river to the camp; through the camp, where constructors
    /// raise turrets; the reapers pouring down the valley; after a missile of the long-range launcher to where it
    /// strikes; behind a knight of the red line as it turns to face them; the clash, over a reaper's shoulder, the
    /// clash slowed down; and up into the sky over the battle as the title comes.
    /// </summary>
    private Shot[] Trailer()
    {
        float Take(int i) => Cuts[i + 1] - Cuts[i];
        return
        [
        new("river", Take(0), u =>
        {
            // Low over the water and across the bank towards the pod.
            var e = Ease(u);
            var eye = Ground(Mathf.Lerp(1150, 1162, e), Mathf.Lerp(150, 390, u), Mathf.Lerp(3, 8, e));
            return new Frame(eye, Ground(1185, 480, Mathf.Lerp(-12, 8, e)), 38, Blur: 1);
        }),
        new("camp", Take(1), u =>
        {
            // Through the camp between the pod and the factory, out to where the turrets rise.
            var e = Ease(u);
            var eye = Ground(Mathf.Lerp(1112, 1128, e), Mathf.Lerp(455, 630, e), Mathf.Lerp(4.5f, 5.5f, e));
            return new Frame(eye, Ground(Mathf.Lerp(1125, 1142, e), 700, 2), 44, Blur: 1.3f);
        }),
        new("horde", Take(2), u =>
        {
            // Low beside the mouth of the valley as the reapers pour down past it.
            var horde = Follow("horde", Centre(_horde));
            return new Frame(Ground(1200 - 6 * u, 915 - 25 * u, 1.6f), horde + new Vector3(0, 2, 0), 40, Blur: 1.2f);
        }),
        new("missile", Take(3), Missile),
        new("line", Take(4), u =>
        {
            var knight = Follow("knight", Position(_knight));
            var eye = knight + new Vector3(-3, 3.5f, -13 + u);
            var horde = Follow("horde", Centre(_horde));
            // The knight looks ahead, then turns its upper body to the horde as the focus moves over to it.
            Steer(u < 0.3f ? 0 : Toward(_knight, horde));
            var pull = Mathf.SmoothStep(0.45f, 0.8f, u);
            var focus = Mathf.Lerp(eye.DistanceTo(knight + Vector3.Up * 2.5f), eye.DistanceTo(horde), pull);
            return new Frame(eye, knight + new Vector3(-4, 2.5f, 30), 34, focus, 1.8f);
        }),
        new("clash", Take(5), u =>
        {
            var clash = Follow("clash", Clash());
            return new Frame(Ground(1235 - 10 * u, 775, 7), clash + new Vector3(0, 3, 0), 40, Blur: 1.4f);
        }),
        new("runner", Take(6), u =>
        {
            // Over the shoulder of a reaper running at the red line.
            var line = Centre(_line) ?? Ground(1160, 750);
            _runner ??= _horde.Where(id => Position(id) is { } p && p.DistanceTo(line) > 25)
                .OrderBy(id => Position(id)!.Value.DistanceTo(line)).Cast<int?>().FirstOrDefault();
            var runner = Follow("runner", _runner is { } id ? Position(id) : null);
            var ahead = new Vector3(line.X - runner.X, 0, line.Z - runner.Z).Normalized();
            var side = ahead.Cross(Vector3.Up);
            var eye = Lift(runner - ahead * 9 + side * 2.5f, 3.3f);
            return new Frame(eye, runner + ahead * 25 + Vector3.Up, 42, eye.DistanceTo(runner), 1.6f);
        }),
        new("slow", Take(7), u =>
        {
            var clash = Follow("clash", Clash());
            var angle = -0.6f + 1.0f * u;
            return new Frame(Lift(clash + new Vector3(Mathf.Cos(angle) * 22, 0, Mathf.Sin(angle) * 22), 4.5f), clash + new Vector3(0, 2.5f, 0), 45, Blur: 1.8f);
        }, u => 1 - 0.7f * Mathf.SmoothStep(0, 0.15f, u) * (1 - Mathf.SmoothStep(0.85f, 1, u))),
        new("rise", Take(8), u =>
        {
            var clash = Hold("rise", Clash());
            var e = Ease(u);
            var eye = clash + new Vector3(-20, 6, -35).Lerp(new Vector3(-60, 110, -230), e);
            return new Frame(eye, clash + new Vector3(0, 2, 60 * e), Mathf.Lerp(42, 48, e), Blur: Mathf.Lerp(1, 0.4f, e));
        }),
        ];
    }

    /// <summary>
    /// Beside the launcher as it fires, then after its missile: the camera lags as it takes off, closes in behind it
    /// and glides on to where it strikes.
    /// </summary>
    private Frame Missile(float u)
    {
        var launcher = Position(_launcher) ?? Ground(1235, 700);
        var horde = Hold("horde", Centre(_horde));
        var ahead = Flat(horde - launcher);
        var side = ahead.Cross(Vector3.Up);
        var round = _missile is { } id ? _world.Projectiles.FirstOrDefault(p => p.Id == id) : null;
        if (_missile is null && (round = _world.Projectiles.Where(p => p.Shooter == _launcher).MinBy(p => p.Id)) is not null)
            _missile = round.Id;
        if (round is not null && _round(round.Id)?.GlobalPosition is { } at)
        {
            var heading = Flat(new Vector3(round.Direction.X, 0, round.Direction.Y));
            _followed["struck"] = at;
            _followed["heading"] = heading;
            var chase = Mathf.Lerp(1.5f, 14, Mathf.SmoothStep(0.05f, 0.45f, u));
            // Beside and over its smoke trail, which would hide everything from right behind it.
            var eye = Above(Follow("eye", at - heading * 9 + side * 4.5f + Vector3.Up * 3.5f, chase), 1.2f);
            var target = Follow("aim", at + heading * 30, 10);
            return new Frame(eye, target, 46, eye.DistanceTo(at), 1.1f);
        }
        if (_followed.TryGetValue("struck", out var struck))
        {
            // It has struck: on to a stop short of the blast, looking at it.
            var heading = _followed["heading"];
            var eye = Above(Follow("eye", struck - heading * 16 + side * 4.5f + Vector3.Up * 5, 2.5f), 1.2f);
            var target = Follow("aim", struck + Vector3.Up * 2, 4);
            return new Frame(eye, target, 46, eye.DistanceTo(struck), 1.1f);
        }
        // Not fired yet: beside and behind the launcher, looking where it will fire.
        var start = Follow("eye", launcher - ahead * 16 + side * 5 + Vector3.Up * 7);
        return new Frame(start, Follow("aim", launcher + ahead * 40), 46, start.DistanceTo(launcher), 1.1f);
    }

    // ---- running the film ----------------------------------------------------------------------

    public override void _Process(double delta)
    {
        _step = (float)(delta / Math.Max(Engine.TimeScale, 0.001));
        _time += _step;
        foreach (var cue in _cues.Where(c => c.At <= _time).ToList())
        {
            cue.Do();
            _cues.Remove(cue);
        }
        if (_time < Launch)
            foreach (var weapon in Launcher()?.Weapons ?? [])
                weapon.Reload = weapon.Spec.ReloadTime;

        // The take now, and how far into it.
        var start = 0f;
        var index = 0;
        while (index < _shots.Length - 1 && _time >= start + _shots[index].Length)
            start += _shots[index++].Length;
        var shot = _shots[index];
        if (index != _shot)
        {
            // A cut: what was followed is picked up afresh.
            _shot = index;
            _followed.Clear();
            _runner = null;
            Steer(null);
            GD.Print($"cinematic: take {index + 1} ({shot.Name}) at {_time:0.00} s, tick {_world.Tick}");
        }
        var u = Mathf.Clamp((float)(_time - start) / shot.Length, 0, 1);
        var frame = shot.At(u);
        var speed = shot.Speed?.Invoke(u) ?? 1;
        Engine.TimeScale = speed;
        // Slowed time sounds lower, though less slowed than it is.
        AudioServer.PlaybackSpeedScale = Mathf.Lerp(1, speed, 0.6f);

        _camera.Fov = frame.Fov;
        _camera.LookAtFromPosition(frame.Eye, frame.Target, Vector3.Up);
        var shake = CameraRig.ShakeAt(frame.Eye).GetRotationQuaternion();
        _camera.Basis *= new Basis(Quaternion.Identity.Slerp(shake, 0.5f));
        FilmLook.Focus(_camera, frame.Focus ?? frame.Eye.DistanceTo(frame.Target), frame.Blur);

        // The weather and the shadows' reach follow what the camera looks at.
        _rig.Position = Ground(frame.Target.X, frame.Target.Z);
        _rig.Distance = Mathf.Max(150, frame.Eye.DistanceTo(frame.Target));

        // Fades from and to black, the title coming on its beat.
        var t = (float)_time;
        _look.Fade = 1 - Mathf.SmoothStep(0, 1.5f, t) * (1 - Mathf.SmoothStep(_length - 1.8f, _length, t));
        _title.Modulate = new Color(1, 1, 1, Mathf.SmoothStep(TitleAt - 0.1f, TitleAt + 0.3f, t) * (1 - Mathf.SmoothStep(_length - 0.6f, _length, t)));

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
        var file = System.IO.Path.Combine(_stillFolder, $"still_{at:00.0}.png".Replace(',', '.'));
        GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"cinematic: still {file}");
        if (_stills.Count == 0)
            GetTree().Quit();
    }

    // ---- where things are ----------------------------------------------------------------------

    // A point on the ground (or the water) at an original position, lifted by up metres.
    private static Vector3 Ground(float x, float y, float up = 0) => new(x, Mathf.Max(HeightField.Current?.At(x, y) ?? 0, 0) + up, y);

    // The same point at up metres over the ground beneath it.
    private static Vector3 Lift(Vector3 at, float up) => Ground(at.X, at.Z, up);

    private static float Ease(float u) => u * u * (3 - 2 * u);

    private Vector3? Position(int id) => _world.FindTarget(id) is not null ? _view(id)?.GlobalPosition : null;

    // The middle of those of a group still alive.
    private Vector3? Centre(IEnumerable<int> ids)
    {
        var alive = ids.Select(Position).OfType<Vector3>().ToList();
        return alive.Count == 0 ? null : alive.Aggregate(Vector3.Zero, (sum, p) => sum + p) / alive.Count;
    }

    // Where the armies meet: the middle of the reapers nearest the red line, or between the two.
    private Vector3? Clash()
    {
        var line = Centre(_line) ?? Ground(1160, 750);
        var near = _horde.Select(Position).OfType<Vector3>().Where(p => p.DistanceTo(line) < 90).ToList();
        if (near.Count >= 3)
            return near.Aggregate(Vector3.Zero, (sum, p) => sum + p) / near.Count;
        return Centre(_horde) is { } horde ? (horde + line) / 2 : line;
    }

    // A point the camera follows smoothly within a take (rate: how quickly it catches up); it keeps the last place
    // when the point is gone.
    private Vector3 Follow(string name, Vector3? at, float rate = 3)
    {
        if (!_followed.TryGetValue(name, out var was))
            return _followed[name] = at ?? Vector3.Zero;
        if (at is not { } now)
            return was;
        return _followed[name] = was.Lerp(now, 1 - Mathf.Exp(-rate * _step));
    }

    // The way along the ground from one point to another.
    private static Vector3 Flat(Vector3 way) => new Vector3(way.X, 0, way.Z).Normalized();

    // A point kept at least up metres over the ground.
    private static Vector3 Above(Vector3 at, float up) => at with { Y = Mathf.Max(at.Y, Ground(at.X, at.Z, up).Y) };

    // Turns the knight's upper body (radians from its hull, as ActorView.UpperBodyAngle), or leaves it to its target.
    private void Steer(float? angle)
    {
        if (_view(_knight) is not ActorView view)
            return;
        view.Steered = angle;
        if (angle is { } a)
            view.UpperBodyAngle = a;
    }

    // The angle from a machine's hull to a point, as ActorView.UpperBodyAngle.
    private float Toward(int id, Vector3 at) => _world.FindTarget(id) is Machine m
        ? Mathf.Wrap(MathF.Atan2(at.Z - m.Position.Y, at.X - m.Position.X) - m.Facing, -Mathf.Pi, Mathf.Pi)
        : 0;

    // A point fixed where it was when the take began.
    private Vector3 Hold(string name, Vector3? at) =>
        _followed.TryGetValue(name, out var was) ? was : _followed[name] = at ?? Vector3.Zero;

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
