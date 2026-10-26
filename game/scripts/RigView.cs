using Godot;

namespace Machines.Game;

/// <summary>
/// Draws a <see cref="Rig"/> and moves its parts as the original did: spider legs play their walking, turning and
/// resting cycles (MachPhysSpiderLegs), wheels roll by the distance travelled over their radius (MachPhysWheels),
/// track treads scroll their texture (MachPhysITrack), and the upper body turns towards its target
/// (MachPhysMachine::upperBodyTurnTo).
/// </summary>
public partial class RigView : Node3D
{
    /// <summary>The upper body's turning speed, radians per second.</summary>
    private const float UpperBodyTurnRate = 2.5f;

    private Rig _rig = null!;
    private Node3D[] _nodes = [];
    private string _cycle = "rest";
    private float _phase;
    private float _leftWheel, _rightWheel;
    private float _upperBody;
    private readonly List<StandardMaterial3D> _leftTread = [], _rightTread = [];

    public static RigView Create(Rig rig)
    {
        var view = new RigView { _rig = rig, Name = "Rig" };
        view._nodes = rig.Nodes.Select((n, i) =>
        {
            var node = new Node3D { Name = $"Part{i}", Transform = n.Rest };
            if (n.Mesh is not null)
                node.AddChild(new MeshInstance3D { Mesh = n.Mesh, Name = "Mesh" });
            return node;
        }).ToArray();
        for (var i = 0; i < view._nodes.Length; i++)
        {
            var parent = rig.Nodes[i].Parent;
            (parent >= 0 && parent < view._nodes.Length && parent != i ? view._nodes[parent] : view).AddChild(view._nodes[i]);
        }
        if (rig.Tracks is { } tracks)
        {
            view.OwnMaterials(tracks.Left, view._leftTread);
            view.OwnMaterials(tracks.Right, view._rightTread);
        }
        return view;
    }

    /// <summary>
    /// Moves the parts for one frame. <paramref name="speed"/> is metres per second forwards (negative backwards),
    /// <paramref name="turnRate"/> radians per second (positive turns left), <paramref name="maxSpeed"/> the
    /// machine's top speed, and <paramref name="aim"/> the upper body's wanted angle relative to the body.
    /// </summary>
    public void Animate(float delta, float speed, float turnRate, float maxSpeed, float aim)
    {
        var moving = Mathf.Abs(speed) > 0.3f;
        var turning = !moving && Mathf.Abs(turnRate) > 0.3f;

        if (_rig.Animations.Count > 0)
        {
            var cycle = moving ? "walk" : turning ? (turnRate > 0 ? "turnLeft" : "turnRight") : "rest";
            if (cycle != _cycle)
            {
                _cycle = cycle;
                _phase = 0;
            }
            if (_rig.Animations.TryGetValue(_cycle, out var animation) && animation.Duration > 0)
            {
                // Legs keep pace with the ground: the walk cycle runs at the machine's share of its top speed.
                var rate = moving ? Mathf.Clamp(Mathf.Abs(speed) / Mathf.Max(1, maxSpeed), 0.5f, 1.5f) : 1;
                _phase = (_phase + delta * rate / animation.Duration) % 1;
                foreach (var track in animation.Tracks)
                    if (track.Node < _nodes.Length)
                        _nodes[track.Node].Transform = track.At(_phase);
            }
        }

        if (_rig.Wheels is { } wheels && wheels.Radius > 0)
        {
            // Turning on the spot drives the sides against each other, as the original reversed one side.
            var spin = turnRate * 1.5f;
            _leftWheel += (speed - spin) * delta / wheels.Radius;
            _rightWheel += (speed + spin) * delta / wheels.Radius;
            // The axle is the original's local y axis, Godot's local z; the axis swap reverses the angle.
            foreach (var i in wheels.Left)
                Spin(i, -_leftWheel);
            foreach (var i in wheels.Right)
                Spin(i, -_rightWheel);
        }

        if (_rig.Tracks is { } tracks)
        {
            var spin = turnRate * 1.5f;
            Scroll(_leftTread, (speed - spin) * tracks.RepeatsPerMeter * delta);
            Scroll(_rightTread, (speed + spin) * tracks.RepeatsPerMeter * delta);
        }

        if (_rig.UpperBody > 0 && _rig.UpperBody < _nodes.Length)
        {
            var step = Mathf.Clamp(Mathf.AngleDifference(_upperBody, aim), -UpperBodyTurnRate * delta, UpperBodyTurnRate * delta);
            _upperBody += step;
            // The original turns about its vertical z axis; with the axis swap that is Godot's y, reversed.
            _nodes[_rig.UpperBody].Transform = _rig.Nodes[_rig.UpperBody].Rest * new Transform3D(new Basis(Vector3.Up, -_upperBody), Vector3.Zero);
        }
    }

    private void Spin(int node, float angle)
    {
        if (node > 0 && node < _nodes.Length)
            _nodes[node].Transform = _rig.Nodes[node].Rest * new Transform3D(new Basis(Vector3.Back, angle), Vector3.Zero);
    }

    private static void Scroll(List<StandardMaterial3D> materials, float by)
    {
        foreach (var material in materials)
            material.Uv1Offset = new Vector3(material.Uv1Offset.X, (material.Uv1Offset.Y + by) % 1, 0);
    }

    /// <summary>Gives a tread its own copies of its materials, so scrolling it leaves other machines alone.</summary>
    private void OwnMaterials(int node, List<StandardMaterial3D> into)
    {
        if (node <= 0 || node >= _nodes.Length || _nodes[node].GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: { } mesh } instance)
            return;
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is StandardMaterial3D shared)
            {
                var own = (StandardMaterial3D)shared.Duplicate();
                instance.SetSurfaceOverrideMaterial(s, own);
                into.Add(own);
            }
        }
    }
}
