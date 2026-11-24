using Godot;

namespace Machines.Game;

// The Thresher's own gait (play test 2026-11-24: the Reaper's walk slowed down did not carry his weight). Made in code, not
// played from the original's cycle: a four-beat walk, one leg in the air at a time (left hind, left fore, right hind, right
// fore), each lifted high, held there a moment and then brought down hard and straight; the body leans its weight over the
// three feet that stand and sags a little as a foot lands. Three feet are always on the floor, at any pace (the user's wish:
// no hopping): faster, as in the escape's chase, the beats only come quicker and the strides grow. A planted foot stays where it stands on the floor while the body moves and turns
// over it, and a lifted one lands where it will be under the leg half a stride on, so the feet never skate; each leg reaches
// its foot by turning its thigh at the hip (the shortest turn) and bending its knee about the hinge its own walk bends it.
public partial class RigView
{
    private Gait? _gait;

    /// <summary>
    /// Whether the legs walk by the heavy gait made in code instead of the rig's walk cycle (the story's Thresher). Set once;
    /// a rig whose legs cannot be found keeps its cycles.
    /// </summary>
    public bool HeavyGait
    {
        get => _gait is not null;
        set
        {
            if (value == HeavyGait)
                return;
            _gait = value ? Gait.Of(this) : null;
            if (_gait is null)
                foreach (var node in Enumerable.Range(0, Math.Min(_rig.Nodes.Length, _nodes.Length)))
                    _nodes[node].Transform = _rig.Nodes[node].Rest;
        }
    }

    /// <summary>A foot of the heavy gait came down: the part, and whether it is a front foot.</summary>
    public Action<RigPlant>? FootDown { get; set; }

    private sealed class Gait
    {
        // The share of the cycle a foot is in the air (under a quarter: one foot up at a time, the next lifts only after it
        // landed); how high a foot goes slow and at the quickest beat (shares of the hip's height); how long the cycle may
        // take (seconds); the stride, as a share of the hip's height, and the longest; how far a foot may stand off its place.
        private const float Swing = 0.22f, Lift = 0.38f, QuickLift = 0.2f;
        private const float Slowest = 4.2f, Fastest = 0.8f, Stride = 1.3f, LongestStride = 1.6f, Stray = 0.9f;

        /// <summary>A landing sags the hips this far (shares of the hip's height) and rolls them this much to its side (degrees).</summary>
        private const float Sag = 0.03f, SagRoll = 1.5f;

        private sealed class Leg
        {
            public int Thigh, Shin;
            public bool Front, Left;
            public Vector3 Foot;           // the foot's tip in the shin's own axes
            public Vector3 Home;           // where it stands at rest, in the rig's axes
            public Vector3 At, From;       // where it is now, and where its swing began
            public bool Up;
            public float Knee;
            public float[] Bends = [];     // knee angles of the reach table, and the foot's distance from the hip at each
            public float[] Reach = [];
            public float Phase;
        }

        private readonly RigView _view;
        private readonly Leg[] _legs;
        private readonly int _hip;
        private readonly float _height;
        private readonly Vector3 _hinge;   // shared by all knees, in the shin's axes
        private float _phase, _sag, _roll, _pitch;
        private Vector3 _velocity, _lean;
        private float _turn;

        private Gait(RigView view, Leg[] legs, int hip, float height, Vector3 hinge)
        {
            _view = view;
            _legs = legs;
            _hip = hip;
            _height = height;
            _hinge = hinge;
        }

        private Rig Rig => _view._rig;

        /// <summary>The gait for a rig with four two-part legs under one hip moved by its walk cycle, or null.</summary>
        public static Gait? Of(RigView view)
        {
            var rig = view._rig;
            if (!rig.Animations.TryGetValue("walk", out var walk))
                return null;
            var nodes = rig.Nodes;
            var moved = walk.Tracks.Select(t => t.Node).Where(n => n >= 0 && n < nodes.Length).ToHashSet();
            // The shins: moved, with a mesh, their parent a moved thigh with a mesh, all thighs under one hip.
            var shins = moved.Where(n => nodes[n].Mesh is not null && nodes[n].Parent is var t and >= 0 && moved.Contains(t)
                && nodes[t].Mesh is not null && !moved.Any(o => o != n && nodes[o].Parent == n)).ToList();
            // The four that hang under one hip (the waist, under the hip as well, is no leg).
            if (shins.GroupBy(s => nodes[nodes[s].Parent].Parent).FirstOrDefault(g => g.Count() == 4 && g.Key >= 0) is not { } four)
                return null;
            shins = [.. four];
            var hip = four.Key;

            Transform3D Rest(int node) => nodes[node].Parent is var p and >= 0 && p != node ? Rest(p) * nodes[node].Rest : nodes[node].Rest;

            // The knee's hinge: the axis the walk turns the shins about the most.
            var (hinge, most) = (Vector3.Up, 0f);
            foreach (var track in walk.Tracks.Where(t => shins.Contains(t.Node)))
                foreach (var sample in track.Samples)
                {
                    var turn = (nodes[track.Node].Rest.Basis.Inverse() * sample.Basis).GetRotationQuaternion();
                    if (turn.GetAngle() > most && turn.GetAngle() < Mathf.Pi)
                        (hinge, most) = (turn.GetAxis(), turn.GetAngle());
                }
            if (most < 0.1f)
                return null;
            hinge = hinge.Normalized();

            var legs = new List<Leg>();
            foreach (var shin in shins)
            {
                var thigh = nodes[shin].Parent;
                var restShin = Rest(shin);
                // The foot's tip: the shin's lowest points at rest.
                var faces = nodes[shin].Mesh!.GetFaces();
                if (faces.Length == 0)
                    return null;
                var low = faces.Min(v => (restShin * v).Y);
                var tips = faces.Where(v => (restShin * v).Y <= low + 0.05f).ToArray();
                var foot = tips.Aggregate(Vector3.Zero, (sum, v) => sum + v) / tips.Length;
                var home = restShin * foot;
                var leg = new Leg { Thigh = thigh, Shin = shin, Foot = foot, Home = home, At = home, From = home };
                // The knee's bends the walk uses, widened, and the foot's reach from the thigh's root at each.
                var bends = walk.Tracks.Where(t => t.Node == shin).SelectMany(t => t.Samples)
                    .Select(s => (nodes[shin].Rest.Basis.Inverse() * s.Basis).GetRotationQuaternion())
                    .Select(q => q.GetAngle() * Mathf.Sign(q.GetAxis().Dot(hinge))).Append(0).ToList();
                var (bendFrom, bendTo) = (bends.Min() - 0.35f, bends.Max() + 0.35f);
                const int steps = 96;
                leg.Bends = new float[steps + 1];
                leg.Reach = new float[steps + 1];
                for (var i = 0; i <= steps; i++)
                {
                    leg.Bends[i] = Mathf.Lerp(bendFrom, bendTo, i / (float)steps);
                    leg.Reach[i] = (nodes[thigh].Rest.Basis * Bent(nodes[shin].Rest, hinge, leg.Bends[i], foot)).Length();
                }
                legs.Add(leg);
            }
            var middle = legs.Aggregate(Vector3.Zero, (sum, l) => sum + l.Home) / legs.Count;
            foreach (var leg in legs)
            {
                leg.Front = leg.Home.X > middle.X;
                leg.Left = leg.Home.Z < middle.Z;   // the model's +z is its right
                // Left hind, left fore, right hind, right fore.
                leg.Phase = (leg.Front, leg.Left) switch { (false, true) => 0, (true, true) => 0.25f, (false, false) => 0.5f, _ => 0.75f };
            }
            var height = Rest(hip).Origin.Y - legs.Average(l => l.Home.Y);
            return height > 0.1f ? new Gait(view, [.. legs], hip, height, hinge) : null;
        }

        // The foot in the thigh's axes with the knee bent so far.
        private static Vector3 Bent(Transform3D shinRest, Vector3 hinge, float bend, Vector3 foot) =>
            new Transform3D(shinRest.Basis * new Basis(hinge, bend), shinRest.Origin) * foot;

        /// <summary>
        /// One frame: the body's speed forwards and to its right (metres per second) and its turn (radians per second,
        /// positive to the left) move the planted feet back under it, the cycle runs at the pace they need, and the legs reach.
        /// </summary>
        public void Step(float delta, float speed, float sideways, float turnRate)
        {
            var scale = _view.IsInsideTree() ? Mathf.Max(0.01f, _view.GlobalBasis.Scale.X) : 1;
            // The ground's motion under the body in the rig's axes, smoothed against the view's frame-to-frame jitter.
            var follow = 1 - Mathf.Exp(-delta * 6);
            _velocity = _velocity.Lerp(new Vector3(speed, 0, sideways) / scale, follow);
            _turn = Mathf.Lerp(_turn, turnRate, follow);
            var reach = _legs.Max(l => new Vector2(l.Home.X, l.Home.Z).Length());
            var pace = _velocity.Length() + Mathf.Abs(_turn) * reach;

            var stride = Stride * _height;
            var stance = 1 - Swing;
            var cycle = pace > 1e-3f ? Mathf.Clamp(stride / (pace * stance), Fastest, Slowest) : Slowest;
            var quick = Mathf.InverseLerp(Slowest * 0.5f, Fastest, cycle);

            // Planted feet keep their place on the floor, dragged along only where they would stand out of reach; the clock
            // runs while the body moves, a foot is up or one stands off its place.
            var turn = new Basis(Vector3.Up, -_turn * delta);
            var furthest = Stray * _height;
            foreach (var leg in _legs.Where(l => !l.Up))
            {
                leg.At = turn * (leg.At - _velocity * delta);
                var off = new Vector3(leg.At.X - leg.Home.X, 0, leg.At.Z - leg.Home.Z);
                if (off.Length() > furthest)
                    leg.At = new Vector3(leg.Home.X, leg.At.Y, leg.Home.Z) + off.Normalized() * furthest;
            }
            var astray = _legs.Any(l => !l.Up && (l.At - l.Home).Length() > 0.12f * stride);
            if (pace > 0.05f * stride || astray || _legs.Any(l => l.Up))
                _phase = (_phase + delta / cycle) % 1;

            var standing = cycle * stance;
            var lift = Mathf.Lerp(Lift, QuickLift, quick) * _height;
            foreach (var leg in _legs)
            {
                var own = Mathf.PosMod(_phase - leg.Phase, 1);
                var up = own < Swing;
                if (up && !leg.Up)
                    leg.From = leg.At;
                // Where it lands: under its place half a stance on, as the body moves and turns.
                var drift = _velocity + new Vector3(_turn * leg.Home.Z, 0, -_turn * leg.Home.X);
                var land = leg.Home + drift * (standing / 2);
                var longest = Mathf.Min(LongestStride * _height / 2, furthest);
                if ((land - leg.Home).Length() > longest)
                    land = leg.Home + (land - leg.Home).Normalized() * longest;
                if (up)
                {
                    var u = own / Swing;
                    // Up fast, hang, then straight down hard: forwards early so the foot falls on its spot.
                    var across = Mathf.SmoothStep(0.08f, 0.62f, u);
                    var high = u < 0.3f ? 1 - Mathf.Pow(1 - u / 0.3f, 2)
                        : u < 0.62f ? 1 + 0.06f * Mathf.Sin((u - 0.3f) / 0.32f * Mathf.Pi)
                        : 1 - Mathf.Pow((u - 0.62f) / 0.38f, 2.6f);
                    leg.At = leg.From.Lerp(land, across) + Vector3.Up * lift * high;
                    leg.At.Y = Mathf.Max(leg.At.Y, leg.Home.Y);
                }
                else if (leg.Up)
                {
                    leg.At = new Vector3(land.X, leg.Home.Y, land.Z);
                    Landed(leg);
                }
                leg.Up = up;
            }
            Body(delta);
            foreach (var leg in _legs)
                Reach(leg);
        }

        private void Landed(Leg leg)
        {
            var weight = leg.Front ? 1 : 0.6f;
            _sag = Mathf.Max(_sag, Sag * _height * weight);
            _roll = SagRoll * weight * (leg.Left ? -1 : 1);
            _view.FootDown?.Invoke(new RigPlant(leg.Shin, 0, leg.Front));
        }

        // The hips: their weight over the feet that stand, a little higher with a front foot up, sagging after a landing.
        private void Body(float delta)
        {
            var standing = _legs.Where(l => !l.Up).ToList();
            var over = standing.Count > 0 ? standing.Aggregate(Vector3.Zero, (s, l) => s + l.Home) / standing.Count : Vector3.Zero;
            var middle = _legs.Aggregate(Vector3.Zero, (s, l) => s + l.Home) / _legs.Length;
            var lean = new Vector3(over.X - middle.X, 0, over.Z - middle.Z) * 0.35f;
            _lean = _lean.Lerp(lean, 1 - Mathf.Exp(-delta * 5));
            var frontUp = _legs.Where(l => l.Up && l.Front).Select(l => (l.At.Y - l.Home.Y) / _height).DefaultIfEmpty(0).Max();
            var backUp = _legs.Where(l => l.Up && !l.Front).Select(l => (l.At.Y - l.Home.Y) / _height).DefaultIfEmpty(0).Max();
            _pitch = Mathf.Lerp(_pitch, Mathf.DegToRad(6) * (frontUp - 0.6f * backUp), 1 - Mathf.Exp(-delta * 8));
            _sag = Mathf.MoveToward(_sag, 0, delta * Sag * _height * 2.5f);
            _roll = Mathf.MoveToward(_roll, 0, delta * SagRoll * 2.5f);
            var rest = Rig.Nodes[_hip].Rest;
            // Pitch about the model's right (+z), nose up; roll about its forwards (+x).
            var tilt = new Basis(Vector3.Back, _pitch) * new Basis(Vector3.Right, Mathf.DegToRad(_roll));
            var shift = new Vector3(_lean.X, -_sag, _lean.Z);
            var parent = Rig.Nodes[_hip].Parent;
            // The shift and tilt are the rig's, put into the hip's parent's axes.
            var parentRest = parent >= 0 ? _view._nodes[parent].Transform.Basis : Basis.Identity;
            var local = parentRest.Inverse() * tilt * parentRest;
            _view._nodes[_hip].Transform = new Transform3D(local * rest.Basis, rest.Origin + parentRest.Inverse() * shift);
        }

        // The rig's axes of a part as it stands now.
        private Transform3D Posed(int node)
        {
            var parent = Rig.Nodes[node].Parent;
            var own = _view._nodes[node].Transform;
            return parent >= 0 && parent != node ? Posed(parent) * own : own;
        }

        private void Reach(Leg leg)
        {
            var nodes = Rig.Nodes;
            var thighRest = nodes[leg.Thigh].Rest;
            var shinRest = nodes[leg.Shin].Rest;
            var hip = Posed(nodes[leg.Thigh].Parent);
            var want = hip.AffineInverse() * leg.At - thighRest.Origin;
            var distance = want.Length();
            // The knee: the bend whose reach is the distance, the crossing nearest last frame's bend.
            var (best, bestBy) = (leg.Knee, float.MaxValue);
            for (var i = 0; i + 1 < leg.Reach.Length; i++)
            {
                var (a, b) = (leg.Reach[i] - distance, leg.Reach[i + 1] - distance);
                if (a * b > 0)
                    continue;
                var bend = Mathf.Lerp(leg.Bends[i], leg.Bends[i + 1], Mathf.Abs(a - b) < 1e-6f ? 0 : a / (a - b));
                if (Mathf.Abs(bend - leg.Knee) < bestBy)
                    (best, bestBy) = (bend, Mathf.Abs(bend - leg.Knee));
            }
            if (bestBy == float.MaxValue)
            {
                // Out of reach: as near as the knee goes.
                var nearest = Enumerable.Range(0, leg.Reach.Length).MinBy(i => Mathf.Abs(leg.Reach[i] - distance));
                best = leg.Bends[nearest];
            }
            leg.Knee = best;
            var shin = new Transform3D(shinRest.Basis * new Basis(_hinge, leg.Knee), shinRest.Origin);
            var has = thighRest.Basis * (shin * leg.Foot);
            var thigh = has.LengthSquared() > 1e-8f && want.LengthSquared() > 1e-8f && !has.Normalized().IsEqualApprox(want.Normalized())
                ? new Basis(new Quaternion(has.Normalized(), want.Normalized())) * thighRest.Basis
                : thighRest.Basis;
            _view._nodes[leg.Thigh].Transform = new Transform3D(thigh, thighRest.Origin);
            _view._nodes[leg.Shin].Transform = shin;
        }
    }
}
