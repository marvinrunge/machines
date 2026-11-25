using Godot;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Game;

// A cutscene's script played by a film camera: its takes made frames, its cues set off as their moments come, and the
// scene's own models (the mothership, the falling pod) moved. Only what is seen and heard: nothing here gives an order.
public abstract partial class FilmCamera
{
    /// <summary>The part of a script being played, if any.</summary>
    protected CutscenePart? _part;

    private int _take = -1;
    private int _cue;
    private float _sceneTime;
    private readonly Dictionary<string, IReadOnlyList<CutscenePoint>> _points = [];
    private readonly Dictionary<string, SceneProp> _props = [];
    private readonly List<Node> _sounds = [];
    private readonly Dictionary<int, float> _hushed = [];
    private bool _silenced;
    private Ramp _fade, _glitch, _rumble;
    private bool _rumbled;
    private CameraTilt _tiltFrom, _tiltTo;
    private float _tiltStart, _tiltEase;
    private float? _duskWas, _dimWas;
    private Effects? _effects;

    // The stage a script's space takes are filmed on (made when its part has any), whether the cue or take at hand is in
    // space, and whether the match's own picture drew its world before a space take turned that off.
    private SpaceStage? _space;
    private bool _inSpace;
    private bool? _world3DWas;

    /// <summary>The camera of the space stage while a space take is on, else null.</summary>
    protected Camera3D? SpaceCamera => _space is { Showing: true } stage ? stage.Camera : null;

    /// <summary>
    /// The models of the original a scene shows by name (<c>pod</c>), in a race's colours; the story's own props come from
    /// game/assets/story/props.
    /// </summary>
    public Func<string, Node3D?>? Models { get; set; }

    /// <summary>The player's own machine, the Locator when no story seat says otherwise.</summary>
    protected int? Hero { get; set; }

    /// <summary>The film look the script fades and breaks up.</summary>
    protected abstract FilmLook? Look { get; }

    /// <summary>How far the script has faded the picture to black, 0 to 1.</summary>
    protected float ScriptFade { get; private set; }

    /// <summary>A value easing from one to another over some seconds from a moment.</summary>
    private readonly record struct Ramp(float From, float To, float Start, float Seconds)
    {
        public float At(float time) => Seconds <= 0 || time >= Start + Seconds ? To
            : Mathf.Lerp(From, To, Ease(Mathf.Clamp((time - Start) / Seconds, 0, 1)));
    }

    /// <summary>The file of a cutscene's script by its name: <c>data/planets/&lt;chapter&gt;/&lt;name&gt;.cut</c>.</summary>
    public static string? ScriptPath(string name)
    {
        var planets = System.IO.Path.Combine(Paths.Root, "data", "planets");
        return System.IO.Directory.Exists(planets)
            ? System.IO.Directory.GetFiles(planets, name + ".cut", System.IO.SearchOption.AllDirectories).Order().FirstOrDefault()
            : null;
    }

    /// <summary>The takes of a part, ready to be filmed one after the other.</summary>
    protected Shot[] Takes(CutscenePart part)
    {
        _part = part;
        _take = -1;
        _cue = 0;
        if (part.HasSpace && _space is null)
        {
            _space = new SpaceStage(part.Options);
            AddChild(_space);
        }
        return part.Takes.Select(take => new Shot(take.Name, take.Seconds, u => TakeFrame(take, u))).ToArray();
    }

    /// <summary>
    /// The space stage shown or hidden for a take: shown, it is drawn over the match's picture, and the match's world is
    /// not drawn under it.
    /// </summary>
    private void ShowSpace(bool on)
    {
        if (_space is null || _space.Showing == on)
            return;
        _space.Showing = on;
        var viewport = GetViewport();
        _world3DWas ??= viewport.Disable3D;
        viewport.Disable3D = on || _world3DWas.Value;
    }

    /// <summary>Back from space to the match's picture (the cutscene ending or skipped): the stage hidden.</summary>
    protected void LeaveSpace() => ShowSpace(false);

    /// <summary>
    /// The cut to a take: what was followed is picked up afresh, except that a cockpit cut to the same machine's cockpit
    /// keeps looking where it looked (it is one view, cut only to change where it turns).
    /// </summary>
    protected void Enter(int index)
    {
        if (_part is null)
        {
            Cut();
            return;
        }
        var was = _take >= 0 ? _part.Takes[_take] : null;
        var now = _part.Takes[index];
        Vector3? look = was is { Kind: "cockpit" } && now.Kind == "cockpit" && was.Subject == now.Subject && _followed.TryGetValue("look", out var l) ? l : null;
        _take = index;
        Cut();
        ShowSpace(now.Space);
        if (look is { } kept)
            _followed["look"] = kept;
    }

    // ---- the takes' cameras ------------------------------------------------------------------------

    private Frame TakeFrame(CutsceneTake take, float u)
    {
        _inSpace = take.Space;
        var cockpit = take.Kind == "cockpit";
        var fov = take.Number("fov", cockpit ? 62 : 45);
        var blur = take.Number("blur", cockpit ? 0.5f : 1f);
        float? focus = take.Options.ContainsKey("focus") ? take.Number("focus", 20) : null;
        var rate = take.Number("rate", 2.5f);
        Vector3 eye;
        Vector3 ahead;
        switch (take.Kind)
        {
            case "cockpit":
            {
                if (Actor(take.Subject!) is not { } id || Ride(id) is not { } view)
                    return _last;
                eye = view.Eye;
                ahead = new Basis(Vector3.Up, view.Yaw - view.UpperBodyNow - Mathf.Pi / 2) * new Basis(Vector3.Right, Mathf.DegToRad(take.Number("pitch", -2))) * Vector3.Forward;
                focus ??= 25;
                break;
            }
            case "cam":
            {
                if (_world.Deck?.Cameras.FirstOrDefault(c => c.Name == take.Subject) is not { } cam)
                    return _last;
                eye = new Vector3(cam.Position.X, cam.Position.Z, cam.Position.Y);
                ahead = Along(cam.Yaw, cam.Pitch);
                break;
            }
            case "rail":
            {
                if (_world.Deck?.Rails.FirstOrDefault(r => r.Name == take.Subject) is not { Points.Count: >= 2 } rail)
                    return _last;
                (eye, ahead) = OnRail(rail.Points.Select(p => new Vector3(p.X, p.Z, p.Y)).ToList(), Ease(u));
                break;
            }
            case "fixed":
            {
                if (Point(take.Subject!) is not { } at)
                    return _last;
                eye = at;
                ahead = Vector3.Forward;
                break;
            }
            case "chase":
            {
                if (!_props.TryGetValue(take.Subject!, out var prop))
                    return _last;
                // offset= beside it (x, y across, up), easing to to= over the take when given.
                var offset = CutscenePoint.Parse(take.Text("offset") ?? "0,0,0")[0];
                var beside = new Vector3(offset.X, offset.Up, offset.Y);
                if (take.Text("to") is { } toward && CutscenePoint.Parse(toward)[0] is var end)
                    beside = beside.Lerp(new Vector3(end.X, end.Up, end.Y), Ease(u));
                var at = prop.Node.GlobalPosition;
                eye = at + beside;
                ahead = (at - eye).Normalized();
                break;
            }
            default:
                return _last;
        }
        // The view turns towards what it is told to look at, smoothly, from where it looked.
        var wanted = take.Text("look") is { } look && Point(look) is { } target && target.DistanceSquaredTo(eye) > 0.01f
            ? (target - eye).Normalized()
            : ahead;
        var way = Follow("look", wanted, rate);
        if (way.LengthSquared() < 1e-6f)
            way = wanted;
        return new Frame(eye, eye + way.Normalized() * 30, fov, focus, blur);
    }

    // A direction from a yaw (degrees, 0 east, 90 north) and a pitch (degrees, up positive).
    private static Vector3 Along(float yaw, float pitch)
    {
        var (y, p) = (Mathf.DegToRad(yaw), Mathf.DegToRad(pitch));
        return new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Sin(y));
    }

    // Where along a rail a share of its length lies, and the way on there.
    private static (Vector3 At, Vector3 Way) OnRail(List<Vector3> points, float share)
    {
        var lengths = points.Zip(points.Skip(1), (a, b) => a.DistanceTo(b)).ToList();
        var left = share * lengths.Sum();
        for (var i = 0; i < lengths.Count; i++)
        {
            if (left <= lengths[i] || i == lengths.Count - 1)
            {
                var way = (points[i + 1] - points[i]).Normalized();
                return (points[i].Lerp(points[i + 1], lengths[i] > 0 ? Mathf.Clamp(left / lengths[i], 0, 1) : 1), way);
            }
            left -= lengths[i];
        }
        return (points[^1], Vector3.Forward);
    }

    // ---- who and where ------------------------------------------------------------------------------

    /// <summary>
    /// A machine a script names: the Locator, Ike or Bob by their seats, or the machine of a role
    /// (<see cref="CutsceneScript.Roles"/>) nearest the camera, other than the player's, picked once a take.
    /// </summary>
    protected int? Actor(string name)
    {
        var seats = _world.Story?.Seats;
        int? Seat(int i) => seats is not null && i < seats.Count ? _world.HeroOf(seats[i])?.Id : null;
        switch (name)
        {
            case "locator":
                return Seat(0) ?? Hero;
            case "ike":
                return Seat(1);
            case "bob":
                return Seat(2);
        }
        if (!CutsceneScript.Roles.TryGetValue(name, out var role))
            return null;
        // Kept while it lives; looked for again while there is none (the Commander comes in during the take).
        var key = "role " + name;
        if (_picked.TryGetValue(key, out var picked) && picked is { } chosen && _world.FindTarget(chosen) is not null)
            return chosen;
        var hero = Hero ?? Seat(0);
        var near = _camera.GlobalPosition;
        return _picked[key] = _world.Machines
            .Where(m => !m.IsDead && m.Id != hero && m.IsType(role.Type, role.Sub) && _view(m.Id) is not null)
            .OrderBy(m => _view(m.Id)!.GlobalPosition.DistanceSquaredTo(near)).ThenBy(m => m.Id)
            .Select(m => (int?)m.Id).FirstOrDefault();
    }

    /// <summary>A point a script names (<see cref="CutscenePoint"/>), the first of its alternatives that is there.</summary>
    protected Vector3? Point(string text)
    {
        if (!_points.TryGetValue(text, out var alternatives))
            _points[text] = alternatives = CutscenePoint.Parse(text);
        foreach (var point in alternatives)
            if (Resolve(point) is { } at)
                return at;
        return null;
    }

    private Vector3? Resolve(CutscenePoint point)
    {
        var deck = _world.Deck;
        var up = new Vector3(0, point.Up, 0);
        // On a deck heights are from its floor; outside, over the ground below; in space there is nothing below.
        if (point.Name is null)
            return deck is not null || _inSpace ? new Vector3(point.X, point.Up, point.Y) : Ground(point.X, point.Y, point.Up);
        var name = point.Name;
        // A point on a prop, in the model's own frame: it moves and turns with it.
        if (point.Local)
            return _props.TryGetValue(name, out var carrier) ? carrier.Node.GlobalTransform * new Vector3(point.X, point.Y, point.Up) : null;
        if (name == "camera")
            return (_inSpace && _space is not null ? _space.Camera : _camera).GlobalPosition + up;
        if (_props.TryGetValue(name, out var prop))
            return prop.Node.GlobalPosition + up;
        if (_inSpace)
            return null;
        if (CutsceneScript.Special.Contains(name) || CutsceneScript.Roles.ContainsKey(name))
            return Actor(name) is { } id && _view(id) is { } view ? view.GlobalPosition + up : null;
        if (deck?.Markers.FirstOrDefault(m => m.Name == name) is { } marker)
            return new Vector3(marker.Position.X, point.Up, marker.Position.Y);
        if (deck?.Cameras.FirstOrDefault(c => c.Name == name) is { } cam)
            return new Vector3(cam.Position.X, cam.Position.Z + point.Up, cam.Position.Y);
        if (deck?.Zones.FirstOrDefault(z => z.Name == name) is { } zone)
            return new Vector3((zone.Min.X + zone.Max.X) / 2, point.Up, (zone.Min.Y + zone.Max.Y) / 2);
        return null;
    }

    // ---- the cues and the scene ---------------------------------------------------------------------

    /// <summary>
    /// The scene at <paramref name="time"/> seconds of the part: the cues come due, the props move, and the fade, the
    /// interference and the rumble follow their ramps. Call it each frame before the take's frame is made, so that a
    /// camera riding beside a prop sees it where it is now.
    /// </summary>
    protected void Scene(float time)
    {
        if (_part is null)
            return;
        _sceneTime = time;
        _effects ??= GetParent()?.GetNodeOrNull<Effects>("Effects");
        while (_cue < _part.Cues.Count && _part.Cues[_cue].At <= time)
            Fire(_part.Cues[_cue++]);
        foreach (var prop in _props.Values)
            prop.Update(this, time);
        ScriptFade = Mathf.Clamp(_fade.At(time), 0, 1);
        if (Look is { } look)
            look.Glitch = Mathf.Clamp(_glitch.At(time), 0, 1);
        if (_rumbled)
            CameraRig.Rumble = Mathf.Max(0, _rumble.At(time));
    }

    /// <summary>Drops the cues still to come (a skip): what is already under way plays on.</summary>
    protected void DropCues()
    {
        if (_part is not null)
            _cue = _part.Cues.Count;
    }

    /// <summary>How the script leans the camera now.</summary>
    protected virtual CameraTilt TiltNow() =>
        CameraTilt.Lerp(_tiltFrom, _tiltTo, _tiltEase <= 0 ? 1 : Mathf.Clamp((_sceneTime - _tiltStart) / _tiltEase, 0, 1));

    /// <summary>The script leans the camera (and, in a match, the player's view).</summary>
    protected virtual void Tilt(CameraTilt to, float ease)
    {
        _tiltFrom = TiltNow();
        _tiltTo = to;
        _tiltStart = _sceneTime;
        _tiltEase = ease;
    }

    /// <summary>A voice line: played from the story's voice files (the player's language, else English), or not at all.</summary>
    protected virtual void Say(string id)
    {
        var path = $"res://assets/story/voice/{Lang.Code}/{id}.ogg";
        if (!ResourceLoader.Exists(path))
            path = $"res://assets/story/voice/en/{id}.ogg";
        Play(path, null, 0);
    }

    /// <summary>Words for the story (lights, doors ...): only told on the console here; a match's cutscene passes them on.</summary>
    protected virtual void Event(string words) => GD.Print($"cutscene: event {words} at {_sceneTime:0.00} s");

    private void Fire(CutsceneCue cue)
    {
        // A slow frame tells the cues that came in it (MACHINES_HITCH).
        HitchLog.Mark($"cue {cue.Kind} line {cue.Line}");
        _inSpace = cue.Space && _space is not null;
        var stage = _inSpace ? _space!.Effects : _effects;
        var effects = GraphicsSettings.Effects ? stage : null;
        switch (cue.Kind)
        {
            case "line":
                Say(cue.Args[0]);
                break;
            case "sound":
                // In space every sound is heard flat: there is no listener on the stage.
                Play($"res://assets/story/sfx/{cue.Args[0]}.ogg", !_inSpace && cue.Text("at") is { } place ? Point(place) : null, cue.Number("db", 0), cue.Number("for", 0),
                    cue.Number("pitch", 1), cue.Number("from", 0));
                break;
            case "shake" when Point(cue.Args[0]) is { } at:
                CameraRig.Shake(at, cue.Number(1), cue.Number(2));
                break;
            case "rumble":
                _rumbled = true;
                _rumble = new Ramp(CameraRig.Rumble, cue.Number(0), cue.At, cue.Number("ease", 0.5f));
                break;
            case "tilt":
                Tilt(new CameraTilt(cue.Number(0), cue.Number(1), cue.Number("sway", 0), cue.Number("period", 7)), cue.Number("ease", 1));
                break;
            case "glitch":
                // At once, then fading over its seconds.
                _glitch = new Ramp(cue.Number(0), 0, cue.At, cue.Number(1));
                break;
            case "fade":
                _fade = new Ramp(ScriptFade, cue.Number(0), cue.At, cue.Number(1));
                break;
            case "explosion" when Point(cue.Args[0]) is { } at:
                if (_inSpace)
                    effects?.SpaceBlast(at, cue.Number(1));
                else
                    effects?.Explosion(at, cue.Number(1));
                break;
            case "impact" when Point(cue.Args[0]) is { } at:
                Impact(effects, at, cue.Number(1));
                break;
            case "bowshock" when _props.TryGetValue(cue.Args[0], out var shocked):
                shocked.Bowshock(cue.Number(1), cue.At, cue.Number("ease", 1), cue.Number("size", 0));
                break;
            case "sparks" when Point(cue.Args[0]) is { } at:
                effects?.SparkShower(at, cue.Text("toward") is { } toward && Point(toward) is { } aim ? aim - at : null, (int)cue.Number("amount", 40));
                break;
            case "shot" when Point(cue.Args[1]) is { } from && Point(cue.Args[2]) is { } to:
                Shoot(effects, cue.Args[0], from, to, cue.Number("speed", 90));
                break;
            case "fire" when Point(cue.Args[0]) is { } at:
                Burn(stage, at, Carrier(cue.Args[0]), cue.Number(1), cue.Number(2), cue.Number("for", 0));
                break;
            case "smoke" when Point(cue.Args[0]) is { } at:
                Smoke(stage, at, Carrier(cue.Args[0]), cue.Number(1), cue.Number("rise", 14), cue.Number("early", 0), _inSpace);
                break;
            case "flash" when Point(cue.Args[0]) is { } at:
                Flash(at, cue.Number(1), cue.Number("light", cue.Number(1) * 2), cue.Number("for", 1.2f),
                    cue.Text("colour") is { } colour ? CutscenePoint.Parse(colour)[0] : null);
                break;
            case "light" when Point(cue.Args[0]) is { } at:
                Light(at, cue.Number(1), cue.Number("above", 10), cue.Number("energy", 6), cue.Number("angle", 35),
                    cue.Text("colour") is { } tint ? CutscenePoint.Parse(tint)[0] : null);
                break;
            case "prop":
                Prop(cue);
                break;
            case "opening" when Point(cue.Args[0]) is { } hole:
                Opening(hole, cue.Number(1), cue.Number("above", 0.2f));
                break;
            case "silence":
                Silence(cue.Number(0));
                break;
            case "music":
                Music(cue.Args[0], cue.Number("from", 0), cue.Number("fade", 2), cue.Number("db", 0));
                break;
            case "dusk" when !_inSpace && PlanetLookNode() is { } planet:
                _duskWas ??= planet.ForcedNight;
                _dimWas ??= planet.Dim;
                planet.ForcedNight = Mathf.Clamp(cue.Number(0), 0, 1);
                // dim=: the picture darker besides (PlanetLook.Dim), as the forced night's moonlight alone keeps it bright.
                planet.Dim = cue.Number("dim", 0);
                break;
            case "event":
                Event(string.Join(' ', cue.Args));
                break;
            default:
                GD.Print($"cutscene: cue {cue.Kind} at line {cue.Line} has nothing to act on");
                break;
        }
    }

    private PlanetLook? PlanetLookNode() =>
        GetParent()?.GetChildren().OfType<PlanetLook>().FirstOrDefault()
        ?? GetTree().Root.FindChildren("*", "", true, false).OfType<PlanetLook>().FirstOrDefault();

    // A sound of the scene, heard everywhere or from a point; after a silence it alone is heard.
    private void Play(string path, Vector3? at, float db, float seconds = 0, float pitch = 1, float from = 0)
    {
        if (!ResourceLoader.Exists(path) || GD.Load<AudioStream>(path) is not { } stream || GetParent() is not { } parent)
        {
            GD.Print($"cutscene: no sound {path}");
            return;
        }
        var bus = _silenced ? (StringName)"Master" : GameAudio.EffectsBus;
        Node player;
        if (at is { } where)
        {
            var spatial = new AudioStreamPlayer3D { Stream = stream, Bus = bus, VolumeDb = db, UnitSize = 30, MaxDistance = 0, PitchScale = pitch };
            parent.AddChild(spatial);
            spatial.GlobalPosition = where;
            spatial.Finished += spatial.QueueFree;
            spatial.Play(from);
            player = spatial;
        }
        else
        {
            var flat = new AudioStreamPlayer { Stream = stream, Bus = bus, VolumeDb = db, PitchScale = pitch };
            parent.AddChild(flat);
            flat.Finished += flat.QueueFree;
            flat.Play(from);
            player = flat;
        }
        // A loop starts somewhere in its wave (the roar of re-entry at a sixth of its loudest, off its middle), which clicked:
        // it comes in over a moment.
        if (stream is AudioStreamOggVorbis { Loop: true } or AudioStreamWav { LoopMode: not AudioStreamWav.LoopModeEnum.Disabled })
            FadeIn(player, db, 0.25f);
        _sounds.Add(player);
        // A sound that loops (the alarm, the roar of re-entry) plays for its seconds, or until the cutscene ends.
        if (seconds > 0)
            GetTree().CreateTimer(seconds).Timeout += () => Hush(player, 0.6f);
    }

    // A sound coming in from silence to its loudness over seconds, evenly in what is heard (not in decibels).
    private static void FadeIn(Node player, float db, float seconds)
    {
        player.Set("volume_db", -80f);
        var target = Mathf.DbToLinear(db);
        player.CreateTween().TweenMethod(Callable.From<float>(share => player.Set("volume_db", Mathf.LinearToDb(Mathf.Max(share * target, 1e-4f)))),
            0f, 1f, seconds);
    }

    // The cutscene's own music (game/assets/<name>.ogg): the story's music makes way, the track starts that many seconds in
    // and comes in over its fade. A silence ends it with the rest; when the cutscene ends it fades out.
    private void Music(string name, float from, float fade, float db)
    {
        var path = $"res://assets/{name}.ogg";
        if (!ResourceLoader.Exists(path) || GD.Load<AudioStream>(path) is not { } stream || GetParent() is not { } parent)
        {
            GD.Print($"cutscene: no music {path}");
            return;
        }
        StoryAudio.MusicOut(Mathf.Max(fade, 0.3f));
        string bus = AudioServer.GetBusIndex(StoryAudio.MusicBus) >= 0 ? StoryAudio.MusicBus : "Master";
        var music = new AudioStreamPlayer { Stream = stream, Bus = bus, VolumeDb = -80, Name = "CutsceneMusic" };
        parent.AddChild(music);
        music.Finished += music.QueueFree;
        music.Play(from);
        FadeIn(music, db, fade);
        _sounds.Add(music);
        _music = music;
    }

    private AudioStreamPlayer? _music;

    // A sound of the scene fading out and gone.
    private void Hush(Node player, float seconds)
    {
        if (!IsInstanceValid(player) || player.GetParent() is not { } parent)
            return;
        var tween = parent.CreateTween();
        tween.TweenProperty(player, "volume_db", -60f, seconds);
        tween.TweenCallback(Callable.From(player.QueueFree));
    }

    // Every sound of the match fades out; what the script plays from now on is heard alone.
    private void Silence(float seconds)
    {
        _silenced = true;
        // The scene's own sounds (its music too) die away over the same seconds; cut off at once, they clicked.
        foreach (var sound in _sounds.Where(IsInstanceValid))
            Hush(sound, Mathf.Max(seconds, 0.05f));
        _sounds.Clear();
        if (AudioServer.BusCount < 2)
            return;
        var tween = CreateTween().SetParallel();
        for (var bus = 1; bus < AudioServer.BusCount; bus++)
        {
            _hushed.TryAdd(bus, AudioServer.GetBusVolumeDb(bus));
            var index = bus;
            tween.TweenMethod(Callable.From<float>(db => AudioServer.SetBusVolumeDb(index, db)), AudioServer.GetBusVolumeDb(bus), -80f, Mathf.Max(seconds, 0.01f));
        }
    }

    // A round that only flies, from a muzzle to where it splashes.
    private void Shoot(Effects? effects, string weapon, Vector3 from, Vector3 to, float speed)
    {
        if (effects is null)
            return;
        effects.Shot(from, to, weapon, speed);
        var flight = from.DistanceTo(to) / Mathf.Max(speed, 1);
        GetTree().CreateTimer(flight).Timeout += () =>
        {
            if (IsInstanceValid(effects))
                effects.EnergySplat(to, weapon, to - from);
        };
    }

    // The prop a point lies on in its own frame (name@x,y,z), and where on it: what burns or smokes there goes with it.
    private (Node3D Node, Vector3 At)? Carrier(string text)
    {
        foreach (var point in _points.TryGetValue(text, out var known) ? known : CutscenePoint.Parse(text))
        {
            if (point is { Local: true, Name: { } name } && _props.TryGetValue(name, out var prop))
                return (prop.Node, new Vector3(point.X, point.Y, point.Up));
            if (Resolve(point) is not null)
                return null;
        }
        return null;
    }

    // Puts an emitter at a point, or on the prop the point lies on, moving with it while what it gives off stays behind.
    private static void Place(Node3D emitter, Node parent, Vector3 at, (Node3D Node, Vector3 At)? carrier)
    {
        if (carrier is var (node, local))
        {
            node.AddChild(emitter);
            emitter.Position = local;
            return;
        }
        parent.AddChild(emitter);
        emitter.GlobalPosition = at;
    }

    // Something huge strikes the ground: in space the planet's face under the point (SpaceStage.Surface), on a map the
    // ground there, with a flash, a blast and a column of smoke.
    private void Impact(Effects? effects, Vector3 at, float size)
    {
        if (_inSpace && _space is not null)
        {
            var (ground, up) = _space.Surface(at);
            effects?.PlanetImpact(ground, up, size);
            return;
        }
        Flash(at, size, size * 4, 2, null);
        effects?.Explosion(at, size);
        Smoke(_effects, at, null, size / 3, 20);
    }

    // Flames left burning (under the match's effects, so they outlast the cutscene), for so long or for good.
    private void Burn(Effects? effects, Vector3 at, (Node3D, Vector3)? carrier, float width, float height, float seconds)
    {
        if (effects is null || (_inSpace ? Effects.HullFire(width, height) : Effects.DamageFire(width, height)) is not { } fire)
            return;
        Place(fire, effects, at, carrier);
        fire.Emitting = true;
        if (seconds > 0)
            GetTree().CreateTimer(seconds).Timeout += () =>
            {
                if (IsInstanceValid(fire))
                    fire.Emitting = false;
            };
    }

    // A column of smoke left rising.
    private static void Smoke(Effects? effects, Vector3 at, (Node3D, Vector3)? carrier, float size, float rise, float early = 0, bool space = false)
    {
        if (effects is null || !GraphicsSettings.Effects)
            return;
        // In space, filmed wisps drifting out (nothing rises there); on a map, the rising column.
        var plume = space ? Effects.SpaceSmoke(size, rise) : Effects.Plume(8, rise, size * 6, size, new Color(0.16f, 0.15f, 0.14f, 0.85f));
        // Streaming from a moving ship it spreads far behind it: drawn while any of it is in view.
        if (carrier is not null)
            plume.VisibilityAabb = new Aabb(-Vector3.One * size * 60, Vector3.One * size * 120);
        Place(plume, effects, at, carrier);
        // A column already burning when the take begins (early= seconds of it risen).
        plume.Preprocess = early;
        plume.Emitting = true;
    }

    // Where a flash, a lamp or a model of the scene goes: on the space stage for a space cue, else beside the camera.
    private Node StageNode => _inSpace && _space is not null ? _space.Root : this;

    // A blinding flash, swelling and fading, and the light it throws.
    private void Flash(Vector3 at, float size, float range, float seconds, CutscenePoint? rgb)
    {
        var colour = rgb is { } c ? new Color(c.X, c.Y, c.Up) : new Color(1, 0.85f, 0.6f);
        var flash = FxShaders.NewFlash(colour, 6, size, 6);
        flash.SetInstanceShaderParameter("energy", 6f);
        StageNode.AddChild(flash);
        flash.GlobalPosition = at;
        var light = new OmniLight3D { LightColor = colour, LightEnergy = 12, OmniRange = range, OmniAttenuation = 0.6f, ShadowEnabled = false };
        StageNode.AddChild(light);
        light.GlobalPosition = at + Vector3.Up * size * 0.2f;
        var tween = CreateTween().SetParallel();
        tween.TweenProperty(flash, "scale", Vector3.One * size * 1.8f, seconds).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenMethod(Callable.From<float>(e => flash.SetInstanceShaderParameter("energy", e)), 6f, 0f, seconds);
        tween.TweenProperty(light, "light_energy", 0f, seconds * 2.2f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            flash.QueueFree();
            light.QueueFree();
        }));
    }

    // A lamp over a point, shining down on it until the scene ends (it goes with the camera); without shadows, so a ceiling
    // between does not stop it: the pod bay's shaft, where pod 3 is let go, stood black in the middle of the take.
    private void Light(Vector3 at, float range, float above, float energy, float angle, CutscenePoint? rgb)
    {
        var colour = rgb is { } c ? new Color(c.X, c.Y, c.Up) : new Color(1, 0.9f, 0.78f);
        var lamp = new SpotLight3D
        {
            LightColor = colour,
            LightEnergy = 0,
            SpotRange = Mathf.Max(range, above + 1),
            SpotAngle = angle,
            SpotAttenuation = 0.6f,
            ShadowEnabled = false,
        };
        StageNode.AddChild(lamp);
        lamp.GlobalTransform = new Transform3D(Basis.LookingAt(Vector3.Down, Vector3.Forward), at + Vector3.Up * above);
        CreateTween().TweenProperty(lamp, "light_energy", energy, 0.4f);
    }

    // A model of the scene, set moving.
    private void Prop(CutsceneCue cue)
    {
        var name = cue.Args[0];
        // Its way may start where the prop it replaces is now (or on it): read before that one goes.
        var (start, end) = (Point(cue.Args[2]), Point(cue.Args[3]));
        if (_props.Remove(name, out var old))
            old.Node.QueueFree();
        if (start is not { } from || end is not { } to || SceneModel(cue.Args[1]) is not { } model)
        {
            GD.Print($"cutscene: prop {name} ({cue.Args[1]}) at line {cue.Line} is not there");
            return;
        }
        StageNode.AddChild(model);
        if (_opening is not null)
            SeenBelow(model);
        _props[name] = new SceneProp(model, from, to, cue, _inSpace ? _space!.Effects : _effects);
        // engines=: its drives lit at that strength (Effects.Engines knows where its bells are).
        if (cue.Number("engines", 0) is > 0 and var drive && GraphicsSettings.Effects)
            Effects.Engines(model, cue.Args[1], drive);
        _props[name].Update(this, cue.At);
    }

    // A model by name: the story's own props (game/assets/story/props/<name>.glb, or <folder>/<name> under
    // game/assets/story), else the original's (Models).
    private Node3D? SceneModel(string name)
    {
        var path = name.Contains('/') ? $"res://assets/story/{name}.glb" : $"res://assets/story/props/{name}.glb";
        if (ResourceLoader.Exists(path) && GD.Load<PackedScene>(path)?.Instantiate() is Node3D prop)
            return prop;
        return Models?.Invoke(name);
    }

    /// <summary>
    /// Ends what the script set going that outlives the camera: the rumble, the interference, the dusk and the silence
    /// (a match goes on after its cutscene; a film never needs this).
    /// </summary>
    protected void EndScene()
    {
        if (_rumbled)
            CameraRig.Rumble = 0;
        _rumbled = false;
        if (Look is { } look)
            look.Glitch = 0;
        if (_duskWas is { } dusk && PlanetLookNode() is { } planet)
            planet.ForcedNight = dusk;
        if (_dimWas is { } dim && PlanetLookNode() is { } dimmed)
            dimmed.Dim = dim;
        (_duskWas, _dimWas) = (null, null);
        foreach (var (bus, db) in _hushed)
            if (bus < AudioServer.BusCount)
                AudioServer.SetBusVolumeDb(bus, db);
        _hushed.Clear();
        _silenced = false;
        if (_music is not null && IsInstanceValid(_music))
            Hush(_music, 2f);
        _music = null;
        foreach (var sound in _sounds.Where(s => IsInstanceValid(s) && s is AudioStreamPlayer { Stream: AudioStreamOggVorbis { Loop: true } }
            or AudioStreamPlayer3D { Stream: AudioStreamOggVorbis { Loop: true } }))
            Hush(sound, 0.8f);
        CloseOpening();
        // The match's world drawn again, the space stage gone.
        LeaveSpace();
        if (_space is not null && IsInstanceValid(_space))
            _space.QueueFree();
        _space = null;
        _world3DWas = null;
    }

    /// <summary>A model of the scene moving from one point to another: a ship burning through the air, a pod falling.</summary>
    private sealed class SceneProp
    {
        public Node3D Node { get; }
        private readonly Vector3 _from, _to;
        private readonly float _start, _seconds, _until, _yaw, _land, _lean, _sink;
        private readonly string _ease;
        private readonly string? _nose;
        private readonly Effects? _effects;
        private readonly MeshInstance3D? _trail;
        private readonly float _trailWidth;
        private readonly List<Vector3> _past = [];
        private float _sampled = float.MinValue;
        private float? _landed;

        // The glowing shells and their light, which cool once it has landed.
        private readonly List<MeshInstance3D> _shells = [];
        private readonly OmniLight3D? _light;
        private readonly float _glow;

        // The shock before its nose (made on the first bowshock cue), its light, and how it eases from one strength to another.
        private readonly List<MeshInstance3D> _shockShells = [];
        private bool _shocked;
        private Vector3 _ahead;
        private OmniLight3D? _shockLight;
        private GpuParticles3D? _streaks, _embers;
        private float _shockFrom, _shockTo, _shockStart, _shockEase, _shockNow;

        public SceneProp(Node3D node, Vector3 from, Vector3 to, CutsceneCue cue, Effects? effects)
        {
            Node = node;
            _from = from;
            _to = to;
            _start = cue.At;
            _seconds = Mathf.Max(cue.Number(4), 0.01f);
            _until = cue.Number("until", float.MaxValue);
            _yaw = Mathf.DegToRad(cue.Number("yaw", 0));
            _land = cue.Number("land", 0);
            _lean = Mathf.DegToRad(cue.Number("lean", 0));
            _sink = cue.Number("sink", 0);
            _ease = cue.Text("ease") ?? "linear";
            _nose = cue.Text("nose");
            _effects = effects;
            node.Scale = Vector3.One * cue.Number("scale", 1);
            if (cue.Number("glow", 0) is > 0 and var glow)
            {
                _glow = glow;
                _light = Glow(node, glow, _nose == "+x" ? 1 : -1, _shells);
            }
            if (cue.Number("trail", 0) is > 0 and var width)
            {
                _trailWidth = width;
                _trail = new MeshInstance3D { MaterialOverride = TrailMaterial.Value, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                _trail.SetInstanceShaderParameter("width", width);
                // Its points are the world's: the trail is not moved with the model.
                _trail.TopLevel = true;
                node.AddChild(_trail);
            }
        }

        public void Update(FilmCamera camera, float time)
        {
            Node.Visible = time < _until;
            var u = Mathf.Clamp((time - _start) / _seconds, 0, 1);
            var e = _ease switch
            {
                "in" => u * u,
                "out" => 1 - (1 - u) * (1 - u),
                "both" => Ease(u),
                _ => u,
            };
            var at = _from.Lerp(_to, e);
            var way = (_to - _from).Normalized();
            // A nose (+x or -x of the model) points along the way; anything else turns by its yaw.
            if (_nose is "+x" or "-x" && way.LengthSquared() > 0)
            {
                var x = _nose == "+x" ? way : -way;
                var z = x.Cross(Vector3.Up);
                z = z.LengthSquared() < 1e-6f ? Vector3.Back : z.Normalized();
                var y = z.Cross(x).Normalized();
                Node.Basis = new Basis(x, y, z).Scaled(Node.Scale);
            }
            // Or its bottom (-y) or top leads, a pod falling base first.
            else if (_nose is "+y" or "-y" && way.LengthSquared() > 0)
            {
                var y = _nose == "+y" ? way : -way;
                var x = Vector3.Forward.Cross(y);
                x = x.LengthSquared() < 1e-6f ? Vector3.Right : x.Normalized();
                var z = x.Cross(y).Normalized();
                Node.Basis = new Basis(x, y, z).Scaled(Node.Scale);
            }
            else
            {
                var basis = new Basis(Vector3.Up, _yaw);
                // lean=: upright, but its base tipped that many degrees into the way it falls (at most as far as the way
                // itself), and it strikes the ground so, slanting along its path.
                var axis = Vector3.Down.Cross(way);
                if (_lean > 0 && axis.LengthSquared() > 1e-6f)
                {
                    var angle = Mathf.Min(_lean, Vector3.Down.AngleTo(way));
                    basis = new Basis(axis.Normalized(), angle) * basis;
                }
                Node.Basis = basis.Scaled(Node.Scale);
            }
            if (_land > 0 && u >= 1 && _landed is null)
                Land(camera, at, time);
            if (_landed is { } landed)
            {
                // It bounces once or twice and settles, rocking.
                var t = time - landed;
                var hop = _land * 0.35f * Mathf.Exp(-t * 5) * Mathf.Abs(Mathf.Sin(t * 12));
                // sink=: it drives that many metres into the ground along its own axis at once, and hardly bounces.
                at += Vector3.Up * hop * (_sink > 0 ? 0.15f : 1);
                at -= Node.Basis.Y.Normalized() * _sink * Mathf.Min(1, t * 10);
                Node.Basis = Node.Basis.Rotated(Vector3.Right, 0.06f * Mathf.Exp(-t * 3) * Mathf.Sin(t * 9));
                Cool(Mathf.Exp(-t / 1.5f));
            }
            Node.GlobalPosition = at;
            if (_trail is not null)
                Trail(at, time);
            if (_shocked)
            {
                var share = _shockEase <= 0 ? 1 : Mathf.Clamp((time - _shockStart) / _shockEase, 0, 1);
                _shockNow = Mathf.Lerp(_shockFrom, _shockTo, Ease(share));
                // It meets the air the way the prop flies, whichever way the model is turned.
                var ahead = way.LengthSquared() > 0 ? way : (Node.GlobalBasis * _ahead).Normalized();
                foreach (var shell in _shockShells)
                {
                    shell.SetInstanceShaderParameter("energy", _shockNow);
                    shell.SetInstanceShaderParameter("ahead", ahead);
                    shell.Visible = _shockNow > 0.001f;
                }
                foreach (var particles in new[] { _streaks, _embers })
                    if (particles is not null)
                        particles.GlobalBasis = Basis.LookingAt(ahead, Mathf.Abs(ahead.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
                if (_shockLight is not null)
                    _shockLight.LightEnergy = _shockNow * 3 * (0.85f + 0.15f * Mathf.Sin(time * 31));
                foreach (var particles in new[] { _streaks, _embers })
                    if (particles is not null)
                    {
                        particles.AmountRatio = Mathf.Clamp(_shockNow / 4, 0, 1);
                        particles.Emitting = _shockNow > 0.05f && GraphicsSettings.Effects;
                    }
            }
        }

        /// <summary>
        /// The bow shock: a skin of glowing air over the model's own shape, standing off it before the way it flies and hugging
        /// its flanks, brightest at the front, easing to <paramref name="strength"/> (0 gone) over <paramref name="ease"/> seconds
        /// from <paramref name="start"/>; <paramref name="size"/> metres the width its particles are born over, or the model's.
        /// </summary>
        public void Bowshock(float strength, float start, float ease, float size)
        {
            if (!_shocked)
            {
                _shocked = true;
                var (box, ahead) = Bounds();
                _ahead = ahead;
                var length = Mathf.Abs(ahead.Dot(box.Size));
                var across = size > 0 ? size : Mathf.Max(box.Size.X + box.Size.Y + box.Size.Z - length, 1) * 1.2f;
                var tip = box.GetCenter() + ahead * length * 0.5f;
                // A shell over every mesh of the model, pushed out along its normals (far before the front, close on the
                // flanks): the shock takes the hull's own shape.
                foreach (var mesh in Node.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().ToList())
                {
                    if (mesh.Mesh is null || mesh.MaterialOverride is not null)
                        continue;
                    var shell = new MeshInstance3D { Mesh = mesh.Mesh, MaterialOverride = ShockMaterial.Value, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Bowshock", Visible = false };
                    shell.SetInstanceShaderParameter("inflate", length * 0.025f);
                    mesh.AddChild(shell);
                    _shockShells.Add(shell);
                }
                var facing = Basis.LookingAt(ahead, Mathf.Abs(ahead.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
                // The air it tears: fine streaks swept back along the hull, embers left behind.
                (_streaks, _embers) = Effects.ShockParticles(across, length);
                foreach (var particles in new[] { _streaks, _embers })
                {
                    particles.AmountRatio = 0;
                    particles.Emitting = false;
                    Node.AddChild(particles);
                    particles.Transform = new Transform3D(facing, tip - ahead * length * 0.05f);
                }
                _shockLight = new OmniLight3D { LightColor = new Color(1, 0.6f, 0.3f), OmniRange = length * 2.5f, OmniAttenuation = 1.4f, ShadowEnabled = false };
                Node.AddChild(_shockLight);
                _shockLight.Position = tip + ahead * length * 0.1f;
            }
            _shockFrom = _shockNow;
            _shockTo = strength;
            _shockStart = start;
            _shockEase = ease;
        }

        // The model's box in its own frame, and the way its nose points in it.
        private (Aabb Box, Vector3 Ahead) Bounds()
        {
            var ahead = _nose switch
            {
                "+x" => Vector3.Right,
                "-x" => Vector3.Left,
                "+y" => Vector3.Up,
                "-y" => Vector3.Down,
                _ => Vector3.Forward,
            };
            var inverse = Node.GlobalTransform.AffineInverse();
            Aabb? box = null;
            foreach (var mesh in Node.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
            {
                if (mesh.Mesh is null || mesh.MaterialOverride is not null)
                    continue;
                var local = (inverse * mesh.GlobalTransform) * mesh.GetAabb();
                box = box is { } b ? b.Merge(local) : local;
            }
            return (box ?? new Aabb(-Vector3.One, Vector3.One * 2), ahead);
        }

        private void Land(FilmCamera camera, Vector3 at, float time)
        {
            _landed = time;
            if (GraphicsSettings.Effects && _effects is not null)
            {
                _effects.Explosion(at, _land);
                _effects.KickUp(at, new Vector2(_land * 3, _land * 3));
                // And a cloud of the ground's sand rolling out low all round and rising over the strike.
                _effects.DustBurst(at, _land * 18, new Color(0.62f, 0.47f, 0.32f));
            }
            _effects?.Scorch(at, _land * 2);
            CameraRig.Shake(at, 4 + _land, 0.9f);
        }

        // The trail: where the model has been, newest first, as a band that widens and cools to smoke.
        private void Trail(Vector3 at, float time)
        {
            // Down, the trail draws itself in from its far end and is gone in a moment (it stood on as a white pole).
            if (_landed is not null)
            {
                for (var i = 0; i < 3 && _past.Count > 0; i++)
                    _past.RemoveAt(_past.Count - 1);
                _trail!.Visible = _past.Count >= 2;
                if (!_trail.Visible)
                    return;
            }
            else if (time - _sampled >= 0.08f)
            {
                _sampled = time;
                _past.Insert(0, at);
                if (_past.Count > 90)
                    _past.RemoveAt(_past.Count - 1);
            }
            else if (_past.Count > 0)
                _past[0] = at;
            if (_past.Count < 2 || _past[0].DistanceSquaredTo(_past[^1]) < 1)
                return;
            _trail!.Mesh = FxShaders.RibbonMesh(_past);
            _trail.ExtraCullMargin = _trailWidth * 4;
        }

        // The glow at that share of its heat.
        private void Cool(float share)
        {
            foreach (var shell in _shells)
                shell.SetInstanceShaderParameter("energy", _glow * share);
            if (_light is not null)
                _light.LightEnergy = _glow * 2 * share;
        }

        // A shell of fire round every mesh of the model, brightest where it meets the air, and its light.
        private static OmniLight3D Glow(Node3D model, float energy, float nose, List<MeshInstance3D> shells)
        {
            foreach (var mesh in model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().ToList())
            {
                if (mesh.Mesh is null)
                    continue;
                var shell = new MeshInstance3D { Mesh = mesh.Mesh, MaterialOverride = GlowMaterial.Value, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                shell.SetInstanceShaderParameter("energy", energy);
                shell.SetInstanceShaderParameter("nose", nose);
                mesh.AddChild(shell);
                shells.Add(shell);
            }
            var light = new OmniLight3D { LightColor = new Color(1, 0.55f, 0.25f), LightEnergy = energy * 2, OmniRange = 4000, OmniAttenuation = 1.2f, ShadowEnabled = false };
            model.AddChild(light);
            return light;
        }
    }

    /// <summary>
    /// Re-entry: the hull's skin glowing orange-white, brightest at its edges as seen and on the side that meets the air
    /// (its nose, the model's -x or +x as the prop is flown), flickering; drawn over the hull, pushed out a little.
    /// </summary>
    private static readonly Lazy<ShaderMaterial> GlowMaterial = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_back, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform float energy = 2.0;
                instance uniform float nose = -1.0;
                varying vec3 local;

                void vertex() {
                    local = VERTEX;
                    VERTEX += NORMAL * 1.5;
                }

                void fragment() {
                    float edge = 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0);
                    vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
                    vec3 ahead = normalize((MODEL_MATRIX * vec4(nose, 0.0, 0.0, 0.0)).xyz);
                    float front = clamp(dot(world_normal, ahead), 0.0, 1.0);
                    float flicker = 0.7 + 0.3 * sin(local.x * 0.05 + TIME * 11.0) * sin(local.y * 0.09 - TIME * 7.0);
                    vec3 hot = mix(vec3(1.0, 0.42, 0.12), vec3(1.0, 0.9, 0.7), front);
                    ALBEDO = hot * (pow(edge, 3.0) * 0.8 + pow(front, 4.0) * 0.9 + 0.02) * energy * flicker;
                }
                """,
        },
    });

    /// <summary>
    /// The bow shock (<see cref="SceneProp.Bowshock"/>): the hull's own meshes pushed out along their normals, far before the
    /// way it flies and close along its flanks, white-hot at the front turning orange towards the flanks, brightest where seen
    /// edge-on, fine bands of glowing air running back over it and thinning to nothing towards the back; flickering.
    /// </summary>
    private static readonly Lazy<ShaderMaterial> ShockMaterial = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_back, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform float energy = 0.0;
                // The way it flies (world), and how far the skin stands off the hull on its flanks (metres).
                instance uniform vec3 ahead = vec3(0.0, -1.0, 0.0);
                instance uniform float inflate = 1.0;
                varying float front;
                varying float along;
                varying vec3 world;

                void vertex() {
                    vec3 turned = (MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz;
                    float stretch = max(length(turned), 1e-4);
                    vec3 way = normalize(ahead);
                    front = dot(turned / stretch, way);
                    // Far before the front (the shock stands off where the air is met), close along the flanks.
                    float off = inflate * (0.4 + 2.6 * pow(max(front, 0.0), 1.5));
                    VERTEX += NORMAL * off / stretch;
                    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    along = dot(world - MODEL_MATRIX[3].xyz, way) / max(inflate, 0.01);
                }

                void fragment() {
                    float edge = 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0);
                    float lead = max(front, 0.0);
                    // Along the flanks it thins to nothing towards the back of the hull.
                    float body = smoothstep(-0.6, 0.15, front);
                    float flicker = 0.78 + 0.22 * sin(TIME * 23.0 + world.x * 0.3) * sin(TIME * 17.0 - world.y * 0.27);
                    // Fine bands of glowing air running back over it.
                    float streaks = 0.5 + 0.5 * sin(along * 1.7 + TIME * 34.0 + sin(world.z * 0.21 + world.x * 0.17) * 3.0);
                    vec3 hot = mix(vec3(1.0, 0.36, 0.08), vec3(1.0, 0.92, 0.8), pow(lead, 3.0));
                    float glow = pow(edge, 2.2) * 0.75 * mix(streaks, 1.0, pow(lead, 4.0)) + pow(lead, 3.0) * 0.55;
                    ALBEDO = hot * glow * body * energy * flicker;
                }
                """,
        },
    });

    /// <summary>
    /// A trail through the air, built of the points where its head has been (<see cref="FxShaders.RibbonMesh"/>): white-hot
    /// at the head, cooling through orange to grey smoke and widening as it goes, broken into soft billows with ragged edges,
    /// fading at its end.
    /// </summary>
    private static readonly Lazy<ShaderMaterial> TrailMaterial = new(() => WithNoise(new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform float width = 100.0;
                uniform sampler2D noise : repeat_enable, filter_linear_mipmap;

                void vertex() {
                    // NORMAL carries the line's direction there, UV.y which side of it the vertex lies.
                    vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    vec3 along = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
                    vec3 side = cross(along, normalize(CAMERA_POSITION_WORLD - world));
                    side = length(side) > 0.001 ? normalize(side) : vec3(0.0, 1.0, 0.0);
                    world += side * (UV.y * 2.0 - 1.0) * width * (0.3 + 1.1 * UV.x) * 0.5;
                    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
                }

                void fragment() {
                    float a = abs(UV.y * 2.0 - 1.0);
                    float along = UV.x;
                    // Two layers of drifting noise break it into billows, ragged at its edges, not a hard band.
                    float n1 = texture(noise, vec2(along * 5.0 - TIME * 0.12, UV.y * 0.9 + along * 1.7)).r;
                    float n2 = texture(noise, vec2(along * 14.0 - TIME * 0.35, UV.y * 1.9 - along * 3.0)).r;
                    float puff = n1 * 0.65 + n2 * 0.35;
                    float edge = clamp(a + (puff - 0.5) * 0.7, 0.0, 1.0);
                    float soft = pow(1.0 - smoothstep(0.0, 1.0, edge), 1.6);
                    // It grows out of the hull, not from a hard point, and thins away behind.
                    float head = smoothstep(0.0, 0.02, along);
                    vec3 fire = mix(vec3(2.6, 2.0, 1.3), vec3(1.6, 0.6, 0.18), smoothstep(0.0, 0.05, along));
                    vec3 smoke = vec3(0.17, 0.16, 0.15) * (0.75 + 0.5 * n2);
                    ALBEDO = mix(fire, smoke, smoothstep(0.03, 0.2, along));
                    float body = soft * smoothstep(0.2, 0.7, puff + (1.0 - a) * 0.35);
                    ALPHA = clamp(body * pow(1.0 - along, 1.7) * head * 0.7, 0.0, 1.0);
                }
                """,
        },
    }));

    // The trail's billows: soft seamless noise, made once in code.
    private static ShaderMaterial WithNoise(ShaderMaterial material)
    {
        material.SetShaderParameter("noise", new NoiseTexture2D
        {
            Width = 256,
            Height = 256,
            Seamless = true,
            GenerateMipmaps = true,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.012f, FractalOctaves = 4 },
        });
        return material;
    }
}
