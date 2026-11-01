using Godot;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// Moves a hovering or gliding machine's parts with each move it begins, as the original's locomotion planned them
/// when the move began (MachPhysLocomotionMethod::moveAnimations), not from how the machine is seen to move frame by
/// frame. A hovering machine (MachPhysHoverBoots) keeps its body level and swings its two boots: back while it speeds
/// up, a third of that while it runs on, forward while it slows down, and one against the other as it turns on the
/// spot. A gliding machine (MachPhysGlider) pitches its body nose down as each straight chunk begins, levels it
/// halfway and lifts the nose before the end, rolls it into a turn on the spot, and holds its bobbing meanwhile; its
/// thrust light shows as it sets off and its brake light as it slows. Either rocks when it stops dead, and takes the
/// first-person driver's postures.
/// </summary>
public sealed class LocomotionView
{
    private static readonly float MostSwing = Mathf.DegToRad(30), MostTurnSwing = Mathf.DegToRad(5);
    private static readonly float MostPitch = Mathf.DegToRad(20), MostRoll = Mathf.DegToRad(30);

    private readonly RigView _rig;
    private readonly int _left = -1, _right = -1, _hip = -1, _body = -1;
    private readonly Track<Vector2> _boots = new(Vector2.Zero, (a, b, w) => a.Lerp(b, w));
    private readonly Track<Transform3D> _bodyPlan;
    private MachineMove? _move;
    private float _holdFrom, _holdUntil;
    private readonly OmniLight3D? _thrust, _brake;
    private readonly List<(float From, float To)> _thrusting = [], _braking = [];

    private LocomotionView(RigView rig, int left, int right, int hip, int body)
    {
        (_rig, _left, _right, _hip, _body) = (rig, left, right, hip, body);
        _bodyPlan = new(body >= 0 ? rig[body] : Transform3D.Identity, (a, b, w) => a.InterpolateWith(b, w));
        if (body < 0 || !GraphicsSettings.Lighting)
            return;
        // Red thrust and orange brake lights below and behind their parts, lighting the machine alone (W4dPointLight
        // with DYNAMIC scope, illuminating the machine); the original's offsets in its axes, z up.
        _thrust = Light(rig.Find("thrust"), new Vector3(-10, -7, 0), new Color(1, 0, 0), 30);
        _brake = Light(rig.Find("brake"), new Vector3(-10, -10, 0), new Color(1, 0.8f, 0), 20);
    }

    // Lights only this machine's own surfaces.
    private const uint LitLayer = 1 << 19;

    /// <summary>The parts a hovering or gliding machine moves, or null for one without them (or of another locomotion).</summary>
    public static LocomotionView? For(Machine machine, RigView rig) => machine.Stats.Locomotion switch
    {
        Locomotion.Hover when rig.Find("l_hv") is >= 0 and var left && rig.Find("r_hv") is >= 0 and var right =>
            new LocomotionView(rig, left, right, -1, -1),
        Locomotion.Glider when rig.Find("body") is >= 0 and var body => new LocomotionView(rig, -1, -1, rig.Find("hip"), body),
        _ => null,
    };

    private OmniLight3D? Light(int part, Vector3 offset, Color colour, float range)
    {
        if (part < 0)
            return null;
        var light = new OmniLight3D { Position = offset, LightColor = colour, OmniRange = range, LightEnergy = 2, LightCullMask = LitLayer, Visible = false };
        _rig.Attach(part, light);
        foreach (var mesh in _rig.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
            mesh.Layers |= LitLayer;
        return light;
    }

    /// <summary>Plans the parts' motion for the machine's latest move, once when it begins.</summary>
    public void Plan(MachineMove? move)
    {
        if (move is null || ReferenceEquals(move, _move))
            return;
        _move = move;
        if (_body >= 0)
            PlanBody(move);
        else
            PlanBoots(move);
    }

    /// <summary>Stands the parts as planned at <paramref name="time"/> (world time); after the rig's own motion.</summary>
    public void Animate(float time)
    {
        if (_body >= 0)
        {
            _rig[_body] = _bodyPlan.At(time);
            _bodyPlan.Forget(time);
            if (_hip >= 0)
                _rig.HoldIdle(_hip, time >= _holdFrom && time < _holdUntil);
            if (_thrust is not null)
                _thrust.Visible = _thrusting.Any(on => time >= on.From && time < on.To);
            if (_brake is not null)
                _brake.Visible = _braking.Any(on => time >= on.From && time < on.To);
            _thrusting.RemoveAll(on => on.To < time);
            _braking.RemoveAll(on => on.To < time);
            return;
        }
        var boots = _boots.At(time);
        _boots.Forget(time);
        // Swung about the original's (0, -1, 0); the axis swap makes that Godot's z, the angle unchanged.
        _rig[_left] = _rig.Rest(_left) * new Transform3D(new Basis(Vector3.Back, boots.X), Vector3.Zero);
        _rig[_right] = _rig.Rest(_right) * new Transform3D(new Basis(Vector3.Back, boots.Y), Vector3.Zero);
    }

    // ---- hover boots ---------------------------------------------------------------------------

    private void PlanBoots(MachineMove move)
    {
        var start = move.Start;
        _boots.Cut(start);
        switch (move.Kind)
        {
            case MoveKind.Turn:
            {
                // One boot forward and the other back by a third of the turn, at most 5°, the most halfway round.
                var swing = Mathf.Clamp(move.TurnBy / 3, -MostTurnSwing, MostTurnSwing);
                _boots.Add(start + move.Duration / 2, new Vector2(-swing, swing));
                _boots.Add(start + move.Duration, Vector2.Zero);
                break;
            }
            case MoveKind.Run:
                foreach (var chunk in move.Chunks ?? [])
                    PlanBoots(start, chunk);
                break;
            // The boots' angle is about (0, -1, 0), the rocking's about (0, 1, 0).
            case MoveKind.StopDead:
                Rock(_boots, start, Mathf.DegToRad(30), 1, a => new Vector2(-a, -a));
                break;
            case MoveKind.Halted:
                Rock(_boots, start, Mathf.DegToRad(25), 1, a => new Vector2(-a, -a));
                break;
            // Driven in first person the boots jump to their posture (the original set them, with no plan).
            case MoveKind.Forwards:
                _boots.Add(start, Vector2.One * Mathf.DegToRad(25));
                break;
            case MoveKind.Backwards:
                _boots.Add(start, Vector2.One * -Mathf.DegToRad(25));
                break;
        }
    }

    // One straight chunk of a run: back while speeding up (20° a second of it, at most 30°, or 30° when it does
    // not), over a transition of a third of the time at speed (none under a quarter of a second, at most one) to a
    // third of that while it runs on, and forward again to slow down, coming to 0 as it stops.
    private void PlanBoots(float start, MoveChunk chunk)
    {
        var (begin, end) = (start + chunk.Start, start + chunk.End);
        var steady = MathF.Max(0, chunk.End - chunk.Start - chunk.SpeedingUp - chunk.SlowingDown);
        var swing = chunk.SpeedingUp > 0 ? MathF.Min(chunk.SpeedingUp * Mathf.DegToRad(20), MostSwing) : MostSwing;
        var transition = steady * 0.333f;
        transition = transition > 1 ? 1 : transition < 0.25f ? 0 : transition;
        var running = Vector2.One * (-swing * 0.33f);
        var at = begin;
        if (chunk.SpeedingUp > 0)
        {
            at += chunk.SpeedingUp;
            _boots.Add(at, Vector2.One * -swing);
            if (transition > 0)
                _boots.Add(at + transition, running);
        }
        var last = running;
        if (chunk.SlowingDown > 0)
        {
            var braking = MathF.Max(end - chunk.SlowingDown, at);
            if (transition > 0)
            {
                _boots.Add(braking - transition, running);
                _boots.Add(braking, Vector2.One * swing);
            }
            last = Vector2.Zero;
        }
        _boots.Add(end, last);
    }

    // ---- glider body ---------------------------------------------------------------------------

    private void PlanBody(MachineMove move)
    {
        var start = move.Start;
        _bodyPlan.Cut(start);
        _thrusting.RemoveAll(on => on.From >= start);
        _braking.RemoveAll(on => on.From >= start);
        var now = _bodyPlan.At(start);
        switch (move.Kind)
        {
            case MoveKind.Turn:
            {
                // Into the turn, as far as it turns, at most 30°, the most halfway round.
                HoldBobbing(start, start + move.Duration);
                var roll = Mathf.Clamp(-move.TurnBy, -MostRoll, MostRoll);
                _bodyPlan.Add(start + move.Duration / 2, InMachine(now, Roll(roll)));
                _bodyPlan.Add(start + move.Duration, InMachine(now, Basis.Identity));
                break;
            }
            case MoveKind.Run:
                HoldBobbing(start, start + move.Duration);
                foreach (var chunk in move.Chunks ?? [])
                    PlanBody(start, chunk, now);
                break;
            case MoveKind.StopDead:
                Rock(_bodyPlan, start, Mathf.DegToRad(20), 1, p => new Transform3D(Pitch(p), now.Origin));
                break;
            case MoveKind.Halted:
                Rock(_bodyPlan, start, Mathf.DegToRad(5), 0.3f, p => new Transform3D(Pitch(p), now.Origin));
                break;
            // Driven in first person: level, then the nose up a little going forwards, down going backwards.
            case MoveKind.Forwards or MoveKind.Backwards:
                _bodyPlan.Add(start + 0.3f, new Transform3D(Basis.Identity, now.Origin));
                _bodyPlan.Add(start + 0.6f, new Transform3D(Pitch(Mathf.DegToRad(move.Kind == MoveKind.Forwards ? -5 : 5)), now.Origin));
                break;
        }
    }

    // One straight chunk: nose down (20°, or 5° on a chunk of a second and a half or less) over 0.7 s (a fifth of a
    // short chunk), level halfway, nose up as far before the end and level at it. The thrust light shows while the
    // nose goes down, the brake light while it comes up from its last pitch.
    private void PlanBody(float start, MoveChunk chunk, Transform3D now)
    {
        var (begin, length) = (start + chunk.Start, chunk.End - chunk.Start);
        if (length <= 0)
            return;
        var pitch = length > 1.5f ? MostPitch : Mathf.DegToRad(5);
        var pitching = length > 1.5f ? 0.7f : 0.2f * length;
        _bodyPlan.Add(begin + pitching, InMachine(now, Pitch(pitch)));
        _bodyPlan.Add(begin + length / 2, InMachine(now, Basis.Identity));
        _bodyPlan.Add(begin + length - pitching, InMachine(now, Pitch(-pitch)));
        _bodyPlan.Add(begin + length, InMachine(now, Basis.Identity));
        _thrusting.Add((begin, begin + pitching));
        _braking.Add((begin + length - pitching, begin + length));
    }

    // The original pitched about its y axis, nose down for a positive angle, and rolled about its x axis; the axis
    // swap turns both the other way about Godot's z and x.
    private static Basis Pitch(float angle) => new(Vector3.Back, -angle);
    private static Basis Roll(float angle) => new(Vector3.Right, -angle);

    // A move's posture is turned in the machine's axes about where the body stands, then given within the hip as it
    // stands held: the body leaves the hip's rocking behind for the move.
    private Transform3D InMachine(Transform3D body, Basis turn)
    {
        var hip = _hip >= 0 ? _rig.InMachine(_hip) : Transform3D.Identity;
        return hip.AffineInverse() * new Transform3D(turn, (hip * body).Origin);
    }

    // The hip stops bobbing while the move lasts and goes on from there after it (stopBobbing, startBobbing).
    private void HoldBobbing(float from, float until) => (_holdFrom, _holdUntil) = (from, until);

    // Rocks one way, the other and back to rest over a duration, in thirds (pitchOrRollAnimation): the angle about the
    // original's y axis, so first up and then down.
    private static void Rock<T>(Track<T> track, float start, float angle, float duration, Func<float, T> posture)
    {
        track.Add(start + duration / 3, posture(-angle));
        track.Add(start + duration * 2 / 3, posture(angle));
        track.Add(start + duration, posture(0));
    }

    /// <summary>Values at times, and in between straight from one to the next (PhysLinearMotionPlan, PhysTimedAnglePlan).</summary>
    private sealed class Track<T>(T rest, Func<T, T, float, T> between)
    {
        private readonly List<(float Time, T Value)> _keys = [(float.MinValue, rest)];

        public T At(float time)
        {
            for (var i = 1; i < _keys.Count; i++)
            {
                var (from, to) = (_keys[i - 1], _keys[i]);
                if (time < to.Time)
                    return to.Time > from.Time ? between(from.Value, to.Value, (time - from.Time) / (to.Time - from.Time)) : to.Value;
            }
            return _keys[^1].Value;
        }

        /// <summary>A new plan starts at <paramref name="time"/>: the old one goes on until then and is cut there.</summary>
        public void Cut(float time)
        {
            var value = At(time);
            _keys.RemoveAll(k => k.Time >= time);
            _keys.Add((time, value));
        }

        public void Add(float time, T value) => _keys.Add((MathF.Max(time, _keys[^1].Time), value));

        /// <summary>Drops what is over by <paramref name="time"/>.</summary>
        public void Forget(float time)
        {
            while (_keys.Count > 2 && _keys[1].Time <= time)
                _keys.RemoveAt(0);
        }
    }
}
