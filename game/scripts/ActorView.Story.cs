using Godot;

namespace Machines.Game;

// How the story shows a Grunt of the trio beyond its machine: knocked down it lies tipped on its side, and a crate it carries
// for a barricade rides on its back. Only the view; where it stands and what it carries is the world's (GameWorld.Companions).
// The story is the remake's own.
public partial class ActorView
{
    private Node3D? _tippedModel;
    private Transform3D _untipped;
    private float _tip;

    /// <summary>
    /// Lays the machine over on its side by <paramref name="degrees"/> (0 stands it up again), turning its model about the
    /// edge it falls on, so it rests on the floor instead of sinking into it.
    /// </summary>
    public void Tip(float degrees)
    {
        var model = _model as Node3D;
        if (model != _tippedModel)
        {
            // A new model (a refit) stands as it was made; the old one is gone.
            _tippedModel = model;
            _untipped = model?.Transform ?? Transform3D.Identity;
            _tip = 0;
        }
        if (model is null || Mathf.IsEqualApprox(degrees, _tip))
            return;
        _tip = degrees;
        if (degrees == 0)
        {
            model.Transform = _untipped;
            return;
        }
        // The model faces +x with its right along +z: it falls to its right, about the edge of its feet on that side.
        var edge = new Vector3(0, _bounds.Position.Y, _bounds.End.Z);
        var turn = new Transform3D(Basis.Identity, edge) * new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(degrees)), Vector3.Zero)
            * new Transform3D(Basis.Identity, -edge);
        model.Transform = turn * _untipped;
    }

    private MeshInstance3D? _carried;

    /// <summary>The height of the machine's top over its feet, metres, as its model measures.</summary>
    public float Top => _bounds.End.Y;

    /// <summary>A crate (a story prop) carried on the machine's back, or none.</summary>
    public void Carry(string? prop)
    {
        if (prop is null || Props.Get(prop) is not { } model)
        {
            _carried?.QueueFree();
            _carried = null;
            return;
        }
        if (_carried is not null && _carried.Name == prop)
            return;
        _carried?.QueueFree();
        // Small enough to ride on a Grunt: about 1.3 m across, on its back a little behind the middle, a little askew.
        var scale = 1.3f / Mathf.Max(model.Size.X, model.Size.Z);
        _carried = new MeshInstance3D
        {
            Name = prop,
            Mesh = model.Mesh,
            Transform = new Transform3D(new Basis(Vector3.Up, 0.2f).Scaled(Vector3.One * scale), new Vector3(_bounds.GetCenter().X - 0.2f, Top - 0.25f, _bounds.GetCenter().Z)) * model.Offset,
        };
        AddChild(_carried);
    }
}
