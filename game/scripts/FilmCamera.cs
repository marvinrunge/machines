using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// A camera that films a match take after take, the remake's own: what the films of <see cref="Cinematic"/> and the story's
/// live <see cref="Cutscene"/>s share. A take (<see cref="Shot"/>) gives the camera's <see cref="Frame"/> at each moment of
/// it; the camera follows points smoothly within a take (<see cref="Follow"/>), holds them from its start
/// (<see cref="Hold"/>), picks a machine once (<see cref="Pick"/>), circles (<see cref="Around"/>), rides in a machine
/// (<see cref="Cockpit"/>) or turns one's upper body (<see cref="Steer"/>); its focus eases within a take and the ground's
/// shaking reaches it. It only films: it gives no orders.
/// </summary>
public abstract partial class FilmCamera : Node
{
    /// <summary>A take: how long it lasts, the camera at each moment of it (0 to 1), and how fast time runs then.</summary>
    protected sealed record Shot(string Name, float Length, Func<float, Frame> At, Func<float, float>? Speed = null);

    /// <summary>Where the camera is and looks, its field of view (degrees), its focus (metres) and how much it blurs.</summary>
    protected readonly record struct Frame(Vector3 Eye, Vector3 Target, float Fov = 40, float? Focus = null, float Blur = 1.5f);

    protected readonly GameWorld _world;
    protected readonly Func<int, Node3D?> _view;
    protected readonly Camera3D _camera;

    // Seconds of the film this frame (slowed time does not slow it).
    protected float _step;

    // Metres to the sharp zone as shown, following the take's own.
    private float _focus;

    // Points the camera follows smoothly, or holds from the start of a take, and who a take picked to film.
    protected readonly Dictionary<string, Vector3> _followed = [];
    protected readonly Dictionary<string, int?> _picked = [];

    // The machine the camera rides in (hidden from it), and the one whose upper body the film turns.
    protected int? _inside;
    private int? _steered;
    protected Frame _last;

    /// <param name="view">The view of a machine or building by its id.</param>
    protected FilmCamera(GameWorld world, Func<int, Node3D?> view, Camera3D camera)
    {
        _world = world;
        _view = view;
        _camera = camera;
    }

    // ---- a take ----------------------------------------------------------------------------------

    /// <summary>A cut: what was followed is picked up afresh, and the camera leaves the machine it rode in.</summary>
    protected void Cut()
    {
        _followed.Clear();
        _picked.Clear();
        Leave();
        Steer(null);
    }

    /// <summary>
    /// Puts the camera where <paramref name="frame"/> says, shaken as the ground shakes (half as much outside a machine),
    /// its focus easing towards the frame's within a take (at once on a <paramref name="cut"/>).
    /// </summary>
    protected void Show(Frame frame, bool cut)
    {
        // A script's space take films its own stage with that stage's camera.
        var camera = SpaceCamera ?? _camera;
        camera.Fov = frame.Fov;
        camera.LookAtFromPosition(frame.Eye, frame.Target, Mathf.Abs((frame.Target - frame.Eye).Normalized().Y) > 0.999f ? Vector3.Forward : Vector3.Up);
        var shake = CameraRig.ShakeAt(frame.Eye).GetRotationQuaternion();
        camera.Basis *= new Basis(Quaternion.Identity.Slerp(shake, _inside is null ? 0.5f : 1));
        // A script's camera leans as the ship lists.
        if (_part is not null && !_inSpace && TiltNow() is { Level: false } tilt)
            camera.Basis *= tilt.At(FrameClock.Now);
        // The focus follows its frame smoothly within a take: a followed machine's every jolt moved the sharp zone, and
        // the blur behind it pulsed.
        var focus = frame.Focus ?? frame.Eye.DistanceTo(frame.Target);
        _focus = cut ? focus : Mathf.Lerp(_focus, focus, 1 - Mathf.Exp(-_step / 0.15f));
        FilmLook.Focus(camera, _focus, frame.Blur);
    }

    // ---- who is in it --------------------------------------------------------------------------

    protected List<int> Of(Race race, string type, string? sub = null) =>
        _world.Machines.Where(m => m.Race == race && m.IsType(type, sub)).Select(m => m.Id).ToList();

    protected static int Nearest(IEnumerable<Actor> actors, float x, float y) =>
        actors.OrderBy(a => NVector2.Distance(a.Position, new NVector2(x, y))).FirstOrDefault()?.Id ?? -1;

    // The one of those machines nearest a point.
    protected int NearestOf(IEnumerable<int> ids, float x, float y) =>
        Nearest(ids.Select(_world.FindTarget).OfType<Actor>(), x, y);

    // A race's building of that type nearest a point.
    protected int Building(Race race, string type, float x, float y, string? sub = null) =>
        Nearest(_world.Constructions.Where(c => c.Race == race && c.IsType(type, sub)), x, y);

    // The middle of those of a group still alive, as the simulation has them: orders aimed by the views, which move
    // with the frames, would play out differently at another frame rate.
    protected NVector2? SimCentre(IEnumerable<int> ids)
    {
        var alive = ids.Select(_world.FindTarget).OfType<Actor>().Where(a => !a.IsDead).ToList();
        return alive.Count == 0 ? null : alive.Aggregate(NVector2.Zero, (sum, a) => sum + a.Position) / alive.Count;
    }

    // ---- where things are ----------------------------------------------------------------------

    // A point on the ground (or the water) at an original position, lifted by up metres.
    protected static Vector3 Ground(float x, float y, float up = 0) => new(x, Mathf.Max(HeightField.Current?.At(x, y) ?? 0, 0) + up, y);

    // The same point at up metres over the ground beneath it.
    protected static Vector3 Lift(Vector3 at, float up) => Ground(at.X, at.Z, up);

    protected static float Ease(float u) => u * u * (3 - 2 * u);

    protected Vector3? Position(int id) => _world.FindTarget(id) is not null ? _view(id)?.GlobalPosition : null;

    // The middle of those of a group still alive.
    protected Vector3? Centre(IEnumerable<int> ids)
    {
        var alive = ids.Select(Position).OfType<Vector3>().ToList();
        return alive.Count == 0 ? null : alive.Aggregate(Vector3.Zero, (sum, p) => sum + p) / alive.Count;
    }

    // A point the camera follows smoothly within a take (rate: how quickly it catches up); it keeps the last place
    // when the point is gone.
    protected Vector3 Follow(string name, Vector3? at, float rate = 3)
    {
        if (!_followed.TryGetValue(name, out var was))
            return _followed[name] = at ?? Vector3.Zero;
        if (at is not { } now)
            return was;
        return _followed[name] = was.Lerp(now, 1 - Mathf.Exp(-rate * _step));
    }

    // A machine a take picks once, when it begins, and keeps.
    protected int? Pick(string name, Func<int?> choose) =>
        _picked.TryGetValue(name, out var id) ? id : _picked[name] = choose();

    // The way along the ground from one point to another.
    protected static Vector3 Flat(Vector3 way) => new Vector3(way.X, 0, way.Z).Normalized();

    // A point kept at least up metres over the ground.
    protected static Vector3 Above(Vector3 at, float up) => at with { Y = Mathf.Max(at.Y, Ground(at.X, at.Z, up).Y) };

    // A point fixed where it was when the take began.
    protected Vector3 Hold(string name, Vector3? at) =>
        _followed.TryGetValue(name, out var was) ? was : _followed[name] = at ?? Vector3.Zero;

    // Around a point at a radius, an angle (radians) and a height over the ground there.
    protected static Vector3 Around(Vector3 centre, float radius, float angle, float up) =>
        Lift(centre + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius), up);

    /// <summary>
    /// From inside a machine, as the first person sees it: from its faceplate, turning with its upper body, level
    /// whatever it leans; the machine itself is hidden from the camera until the cut. Pitch in radians, up positive.
    /// </summary>
    protected Frame Cockpit(int id, float pitch = -0.04f, float fov = 62)
    {
        if (Ride(id) is not { } view)
            return _last;
        var eye = view.Eye;
        var look = new Basis(Vector3.Up, view.Yaw - view.UpperBodyNow - Mathf.Pi / 2) * new Basis(Vector3.Right, pitch) * Vector3.Forward;
        return new Frame(eye, eye + look * 40, fov, 25, 0.5f);
    }

    // Gets into a machine (hidden from the camera from now on), or null when it is gone.
    protected ActorView? Ride(int id)
    {
        if (_world.FindTarget(id) is null || _view(id) is not ActorView view)
            return null;
        if (_inside != id)
        {
            Leave();
            view.SetDriven(true);
            _inside = id;
            _camera.CullMask = ~ActorView.DrivenLayer & 0xFFFFF;
        }
        return view;
    }

    // Out of the machine the camera rode in.
    protected void Leave()
    {
        if (_inside is { } id && _view(id) is ActorView view)
            view.SetDriven(false);
        _inside = null;
        _camera.CullMask = 0xFFFFF;
    }

    // Turns a machine's upper body (radians from its hull, as ActorView.UpperBodyAngle), or leaves it to its target.
    protected void Steer(int? id, float? angle = null)
    {
        if (_steered is { } was && was != id && _view(was) is ActorView old)
            old.Steered = null;
        _steered = id;
        if (id is not { } machine || _view(machine) is not ActorView view)
            return;
        view.Steered = angle;
        if (angle is { } a)
            view.UpperBodyAngle = a;
    }

    // The angle from a machine's hull to a point, as ActorView.UpperBodyAngle.
    protected float Toward(int id, Vector3 at) => _world.FindTarget(id) is Machine m
        ? Mathf.Wrap(MathF.Atan2(at.Z - m.Position.Y, at.X - m.Position.X) - m.Facing, -Mathf.Pi, Mathf.Pi)
        : 0;
}
