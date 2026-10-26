using Godot;
using Machines.Sim.Presentation;

namespace Machines.Game;

/// <summary>
/// Draws a <see cref="Rig"/> and moves its parts as the original did: spider legs play their walking, turning and
/// resting cycles (MachPhysSpiderLegs), wheels roll by the distance travelled over their radius (MachPhysWheels),
/// track treads scroll their texture (MachPhysITrack), and the upper body turns towards its target
/// (MachPhysMachine::upperBodyTurnTo). One-off actions, such as the Gorilla's punches, play over the cycles.
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
    private float _idleTime = (float)GD.RandRange(0, 10);
    private float _workTime;
    private RigAnimation? _action;
    private float _actionTime;
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
        view.OwnGlow();
        return view;
    }

    private readonly List<(StandardMaterial3D Material, Color Colour)> _glow = [];
    private bool _glowing;

    /// <summary>Gives the chimneys' hot surfaces (<see cref="ChimneyTable.GlowTexture"/>) their own materials, to pulse.</summary>
    private void OwnGlow()
    {
        foreach (var node in _nodes)
        {
            if (node.GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: { } mesh } instance)
                continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is StandardMaterial3D shared
                    && shared.GetMeta("texture", "").AsString() == ChimneyTable.GlowTexture)
                {
                    var own = (StandardMaterial3D)shared.Duplicate();
                    instance.SetSurfaceOverrideMaterial(s, own);
                    _glow.Add((own, own.AlbedoColor));
                }
            }
        }
    }

    /// <summary>
    /// While a factory works its chimneys glow, pulsing from half to full brightness (W4dColourPulsePlan); the
    /// remake lets them shine too. When it stops they go back to their colour.
    /// </summary>
    private void Glow(bool working, float time)
    {
        if (_glow.Count == 0 || (!working && !_glowing))
            return;
        _glowing = working;
        var pulse = working
            ? ChimneyTable.GlowLow + (1 - ChimneyTable.GlowLow) * (0.5f - 0.5f * Mathf.Cos(Mathf.Tau * time / ChimneyTable.GlowPeriod))
            : 1;
        foreach (var (material, colour) in _glow)
        {
            material.AlbedoColor = new Color(colour.R * pulse, colour.G * pulse, colour.B * pulse, colour.A);
            material.EmissionEnabled = working && GraphicsSettings.Remake;
            if (material.EmissionEnabled)
            {
                material.EmissionTexture = material.AlbedoTexture;
                material.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
                material.Emission = Colors.White;
                material.EmissionEnergyMultiplier = 1.5f * pulse;
            }
        }
    }

    /// <summary>
    /// Where first person looks out from: the centre front of the faceplate as it stands now (the original's
    /// camera attachment), or null when the export has none.
    /// </summary>
    /// <summary>A building's working motion plays (MachPhysConstruction::isWorking).</summary>
    public bool Working { get; set; }

    /// <summary>A constructor building or repairing: its arms play the building cycle.</summary>
    public bool Constructing { get; set; }

    /// <summary>The building's doors open (they slide at the original's 1 m/s, MachPhysDoor) or close.</summary>
    public bool DoorsOpen { get; set; }

    private readonly List<(int Node, Vector3 Open)> _doors = [];
    private float _doorOpening;

    /// <summary>
    /// The doors of a building's entrance (MachPhysConstruction::setupEntrances): the part called
    /// <paramref name="name"/>, or the halves l<paramref name="name"/> and r<paramref name="name"/>, which slide
    /// apart along the door line; <paramref name="along"/> is the door's full width along it (original axes).
    /// </summary>
    public void AddDoors(string name, Vector2 along)
    {
        var left = Array.FindIndex(_rig.Nodes, n => n.Name.Equals("l" + name, StringComparison.OrdinalIgnoreCase)
                                                    || n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var right = Array.FindIndex(_rig.Nodes, n => n.Name.Equals("r" + name, StringComparison.OrdinalIgnoreCase));
        var halves = (left >= 0 ? 1 : 0) + (right >= 0 ? 1 : 0);
        if (halves == 0)
            return;
        // The left half slides one way, the right the other, each its share of the width; z up becomes y.
        var open = new Vector3(along.X, 0, along.Y) / halves;
        if (left >= 0)
            _doors.Add((left, -open));
        if (right >= 0)
            _doors.Add((right, open));
    }

    /// <summary>Moves its own parts every frame, for things no actor view drives (an ore hologram).</summary>
    public bool RunsItself { get; set; }

    public override void _Process(double delta)
    {
        if (RunsItself)
            Animate((float)delta, 0, 0, 0, 0);
    }

    /// <summary>A part's place in the world as it stands now, by its name in the model, or null.</summary>
    public Transform3D? PartTransform(string name)
    {
        var index = Array.FindIndex(_rig.Nodes, n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && _nodes[index].IsInsideTree() ? _nodes[index].GlobalTransform : null;
    }

    public Vector3? Eye => _rig.Faceplate >= 0 && _rig.Faceplate < _nodes.Length
        ? _nodes[_rig.Faceplate].GlobalTransform * _rig.Eye
        : null;

    /// <summary>
    /// Plays the animation <paramref name="name"/> once from its start, over the cycles (a machine's
    /// <c>plan</c> in the original); false when the export has none of that name.
    /// </summary>
    public bool Play(string name)
    {
        if (!_rig.Animations.TryGetValue(name, out var animation) || animation.Duration <= 0)
            return false;
        _action = animation;
        _actionTime = 0;
        return true;
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

        // A finished action gives its parts back: to rest, then to the cycles below.
        if (_action is { } done && (_actionTime += delta) >= done.Duration)
        {
            foreach (var track in done.Tracks)
                if (track.Node < _nodes.Length)
                    _nodes[track.Node].Transform = _rig.Nodes[track.Node].Rest;
            _action = null;
        }

        // Parts that move by themselves; the walking cycles below take over the parts they move.
        _idleTime += delta;
        foreach (var idle in _rig.Idle)
            if (idle.Track.Node < _nodes.Length && idle.Duration > 0)
                _nodes[idle.Track.Node].Transform = idle.Track.At(_idleTime % idle.Duration / idle.Duration);

        // Doors slide open as machines come and shut behind them.
        if (_doors.Count > 0)
        {
            var width = _doors.Max(d => d.Open.Length());
            var step = width > 0 ? delta / width : 1;
            _doorOpening = Mathf.Clamp(_doorOpening + (DoorsOpen ? step : -step), 0, 1);
            foreach (var (node, open) in _doors)
                _nodes[node].Transform = _rig.Nodes[node].Rest.Translated(open * _doorOpening);
        }

        // A building at work; when it stops, its parts stay where they stopped.
        if (Working)
        {
            _workTime += delta;
            foreach (var work in _rig.Working)
                if (work.Track.Node < _nodes.Length && work.Duration > 0)
                    _nodes[work.Track.Node].Transform = work.Track.At(_workTime % work.Duration / work.Duration);
        }
        Glow(Working, _workTime);

        if (_rig.Animations.Count > 0)
        {
            // A constructor at work plays its building cycle (MachPhysConstructor::startConstructing) instead.
            var cycle = Constructing && _rig.Animations.ContainsKey("construct") ? "construct"
                : moving ? "walk" : turning ? (turnRate > 0 ? "turnLeft" : "turnRight") : "rest";
            if (cycle != _cycle)
            {
                // Parts the new cycle leaves alone go back to rest (the arms after building).
                if (_rig.Animations.TryGetValue(_cycle, out var ended) && !_rig.Animations.ContainsKey(cycle))
                    foreach (var track in ended.Tracks)
                        if (track.Node < _nodes.Length)
                            _nodes[track.Node].Transform = _rig.Nodes[track.Node].Rest;
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

        if (_action is { } action)
            foreach (var track in action.Tracks)
                if (track.Node < _nodes.Length)
                    _nodes[track.Node].Transform = track.At(_actionTime / action.Duration);

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
