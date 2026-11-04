using Godot;

namespace Machines.Game;

/// <summary>
/// A nuclear silo's dome (MachPhysMissileEmplacement::prepForLaunch): the Opening plan folds its flaps away, raises
/// the head out of the ground and stands the barrel up on its piston, the Closing plan takes all that back. Unlike
/// an action's, the parts stay where a plan leaves them, and the head goes on turning on top of them.
/// </summary>
public partial class RigView
{
    private bool? _domeOpen;
    private RigAnimation? _domePlan;
    private float _domeTime;
    private Transform3D? _head;

    /// <summary>
    /// The dome should stand open or shut. Told for the first time it just stands so, as a loaded silo does;
    /// after that a change plays its plan. True when a plan started, for its sound.
    /// </summary>
    public bool Dome(bool open)
    {
        if (_domeOpen == open || !_rig.Animations.TryGetValue("opening", out var opening) || !_rig.Animations.TryGetValue("closing", out var closing))
            return false;
        var first = _domeOpen is null;
        _domeOpen = open;
        if (first)
        {
            if (open)
                DomePose(opening, 1);
            return false;
        }
        (_domePlan, _domeTime) = (open ? opening : closing, 0);
        return true;
    }

    private void MoveDome(float delta)
    {
        if (_domePlan is not { } plan)
            return;
        _domeTime += delta;
        DomePose(plan, _domeTime / plan.Duration);
        if (_domeTime >= plan.Duration)
            _domePlan = null;
    }

    private void DomePose(RigAnimation plan, float phase)
    {
        foreach (var track in plan.Tracks)
            if (track.Node < _nodes.Length)
            {
                _nodes[track.Node].Transform = track.At(phase);
                if (track.Node == _rig.UpperBody)
                    _head = _nodes[track.Node].Transform;
            }
    }
}
