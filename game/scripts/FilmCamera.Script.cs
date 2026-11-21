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
        return part.Takes.Select(take => new Shot(take.Name, take.Seconds, u => TakeFrame(take, u))).ToArray();
    }

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
        if (look is { } kept)
            _followed["look"] = kept;
    }

    // ---- the takes' cameras ------------------------------------------------------------------------

    private Frame TakeFrame(CutsceneTake take, float u)
    {
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
                var offset = CutscenePoint.Parse(take.Text("offset") ?? "0,0,0")[0];
                var at = prop.Node.GlobalPosition;
                eye = at + new Vector3(offset.X, offset.Up, offset.Y);
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
        // On a deck heights are from its floor; outside, over the ground below.
        if (point.Name is null)
            return deck is not null ? new Vector3(point.X, point.Up, point.Y) : Ground(point.X, point.Y, point.Up);
        var name = point.Name;
        if (name == "camera")
            return _camera.GlobalPosition + up;
        if (_props.TryGetValue(name, out var prop))
            return prop.Node.GlobalPosition + up;
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
        var effects = GraphicsSettings.Effects ? _effects : null;
        switch (cue.Kind)
        {
            case "line":
                Say(cue.Args[0]);
                break;
            case "sound":
                Play($"res://assets/story/sfx/{cue.Args[0]}.ogg", cue.Text("at") is { } place ? Point(place) : null, cue.Number("db", 0), cue.Number("for", 0));
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
                effects?.Explosion(at, cue.Number(1));
                break;
            case "sparks" when Point(cue.Args[0]) is { } at:
                effects?.SparkShower(at, cue.Text("toward") is { } toward && Point(toward) is { } aim ? aim - at : null, (int)cue.Number("amount", 40));
                break;
            case "shot" when Point(cue.Args[1]) is { } from && Point(cue.Args[2]) is { } to:
                Shoot(effects, cue.Args[0], from, to, cue.Number("speed", 90));
                break;
            case "fire" when Point(cue.Args[0]) is { } at:
                Burn(at, cue.Number(1), cue.Number(2), cue.Number("for", 0));
                break;
            case "smoke" when Point(cue.Args[0]) is { } at:
                Smoke(at, cue.Number(1), cue.Number("rise", 14));
                break;
            case "flash" when Point(cue.Args[0]) is { } at:
                Flash(at, cue.Number(1), cue.Number("light", cue.Number(1) * 2), cue.Number("for", 1.2f),
                    cue.Text("colour") is { } colour ? CutscenePoint.Parse(colour)[0] : null);
                break;
            case "prop":
                Prop(cue);
                break;
            case "silence":
                Silence(cue.Number(0));
                break;
            case "dusk" when PlanetLookNode() is { } planet:
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
    private void Play(string path, Vector3? at, float db, float seconds = 0)
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
            var spatial = new AudioStreamPlayer3D { Stream = stream, Bus = bus, VolumeDb = db, UnitSize = 30, MaxDistance = 0 };
            parent.AddChild(spatial);
            spatial.GlobalPosition = where;
            spatial.Finished += spatial.QueueFree;
            spatial.Play();
            player = spatial;
        }
        else
        {
            var flat = new AudioStreamPlayer { Stream = stream, Bus = bus, VolumeDb = db };
            parent.AddChild(flat);
            flat.Finished += flat.QueueFree;
            flat.Play();
            player = flat;
        }
        _sounds.Add(player);
        // A sound that loops (the alarm, the roar of re-entry) plays for its seconds, or until the cutscene ends.
        if (seconds > 0)
            GetTree().CreateTimer(seconds).Timeout += () => Hush(player, 0.6f);
    }

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
        foreach (var sound in _sounds.Where(IsInstanceValid))
            sound.QueueFree();
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

    // Flames left burning (under the match's effects, so they outlast the cutscene), for so long or for good.
    private void Burn(Vector3 at, float width, float height, float seconds)
    {
        if (_effects is null || Effects.DamageFire(width, height) is not { } fire)
            return;
        _effects.AddChild(fire);
        fire.GlobalPosition = at;
        fire.Emitting = true;
        if (seconds > 0)
            GetTree().CreateTimer(seconds).Timeout += () =>
            {
                if (IsInstanceValid(fire))
                    fire.Emitting = false;
            };
    }

    // A column of smoke left rising.
    private void Smoke(Vector3 at, float size, float rise)
    {
        if (_effects is null || !GraphicsSettings.Effects)
            return;
        var plume = Effects.Plume(8, rise, size * 6, size, new Color(0.16f, 0.15f, 0.14f, 0.85f));
        _effects.AddChild(plume);
        plume.GlobalPosition = at;
        plume.Emitting = true;
    }

    // A blinding flash, swelling and fading, and the light it throws.
    private void Flash(Vector3 at, float size, float range, float seconds, CutscenePoint? rgb)
    {
        var colour = rgb is { } c ? new Color(c.X, c.Y, c.Up) : new Color(1, 0.85f, 0.6f);
        var flash = FxShaders.NewFlash(colour, 6, size, 6);
        flash.SetInstanceShaderParameter("energy", 6f);
        AddChild(flash);
        flash.GlobalPosition = at;
        var light = new OmniLight3D { LightColor = colour, LightEnergy = 12, OmniRange = range, OmniAttenuation = 0.6f, ShadowEnabled = false };
        AddChild(light);
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

    // A model of the scene, set moving.
    private void Prop(CutsceneCue cue)
    {
        var name = cue.Args[0];
        if (_props.Remove(name, out var old))
            old.Node.QueueFree();
        if (Point(cue.Args[2]) is not { } from || Point(cue.Args[3]) is not { } to || SceneModel(cue.Args[1]) is not { } model)
        {
            GD.Print($"cutscene: prop {name} ({cue.Args[1]}) at line {cue.Line} is not there");
            return;
        }
        AddChild(model);
        _props[name] = new SceneProp(model, from, to, cue, _effects);
        _props[name].Update(this, cue.At);
    }

    // A model by name: the story's own props, else the original's (Models).
    private Node3D? SceneModel(string name)
    {
        var path = $"res://assets/story/props/{name}.glb";
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
        foreach (var sound in _sounds.Where(s => IsInstanceValid(s) && s is AudioStreamPlayer { Stream: AudioStreamOggVorbis { Loop: true } }
            or AudioStreamPlayer3D { Stream: AudioStreamOggVorbis { Loop: true } }))
            Hush(sound, 0.8f);
    }

    /// <summary>A model of the scene moving from one point to another: a ship burning through the air, a pod falling.</summary>
    private sealed class SceneProp
    {
        public Node3D Node { get; }
        private readonly Vector3 _from, _to;
        private readonly float _start, _seconds, _until, _yaw, _land;
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
            else
                Node.Basis = new Basis(Vector3.Up, _yaw).Scaled(Node.Scale);
            if (_land > 0 && u >= 1 && _landed is null)
                Land(camera, at, time);
            if (_landed is { } landed)
            {
                // It bounces once or twice and settles, rocking.
                var t = time - landed;
                var hop = _land * 0.35f * Mathf.Exp(-t * 5) * Mathf.Abs(Mathf.Sin(t * 12));
                at += Vector3.Up * hop;
                Node.Basis = Node.Basis.Rotated(Vector3.Right, 0.06f * Mathf.Exp(-t * 3) * Mathf.Sin(t * 9));
                Cool(Mathf.Exp(-t / 1.5f));
            }
            Node.GlobalPosition = at;
            if (_trail is not null)
                Trail(at, time);
        }

        private void Land(FilmCamera camera, Vector3 at, float time)
        {
            _landed = time;
            if (GraphicsSettings.Effects && _effects is not null)
            {
                _effects.Explosion(at, _land);
                _effects.KickUp(at, new Vector2(_land * 3, _land * 3));
            }
            _effects?.Scorch(at, _land * 2);
            CameraRig.Shake(at, 4 + _land, 0.9f);
        }

        // The trail: where the model has been, newest first, as a band that widens and cools to smoke.
        private void Trail(Vector3 at, float time)
        {
            if (time - _sampled >= 0.08f)
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
    /// A trail through the air, built of the points where its head has been (<see cref="FxShaders.RibbonMesh"/>): white-hot
    /// at the head, cooling through orange to grey smoke and widening as it goes, fading at its end.
    /// </summary>
    private static readonly Lazy<ShaderMaterial> TrailMaterial = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform float width = 100.0;

                void vertex() {
                    // NORMAL carries the line's direction there, UV.y which side of it the vertex lies.
                    vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    vec3 along = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
                    vec3 side = cross(along, normalize(CAMERA_POSITION_WORLD - world));
                    side = length(side) > 0.001 ? normalize(side) : vec3(0.0, 1.0, 0.0);
                    world += side * (UV.y * 2.0 - 1.0) * width * (0.35 + 1.4 * UV.x) * 0.5;
                    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
                }

                void fragment() {
                    float a = abs(UV.y * 2.0 - 1.0);
                    float along = UV.x;
                    float billow = 0.75 + 0.25 * sin(along * 60.0 - TIME * 3.0 + UV.y * 5.0);
                    vec3 fire = mix(vec3(4.0, 3.2, 2.2), vec3(2.2, 0.8, 0.25), smoothstep(0.0, 0.08, along));
                    vec3 smoke = vec3(0.2, 0.19, 0.18);
                    ALBEDO = mix(fire, smoke, smoothstep(0.06, 0.3, along));
                    ALPHA = clamp((1.0 - a * a) * billow * pow(1.0 - along, 1.3) * 0.9, 0.0, 1.0);
                }
                """,
        },
    });
}
