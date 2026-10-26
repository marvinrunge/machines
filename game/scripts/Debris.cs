using Godot;

namespace Machines.Game;

/// <summary>
/// A machine blown apart: each of its parts flies off, spinning, falls, bounces off the ground and comes to rest,
/// then sinks away. Simple ballistics against the height field, so hundreds of parts cost little.
/// </summary>
public partial class Debris : Node3D
{
    private const float Gravity = 20, Lifetime = 8, SinkFrom = 5;

    private sealed class Piece(Node3D node, Vector3 velocity, Vector3 spin)
    {
        public Node3D Node = node;
        public Vector3 Velocity = velocity;
        public Vector3 Spin = spin;
        public bool Resting;
    }

    private readonly List<Piece> _pieces = [];
    private float _age;

    /// <summary>Takes the meshes under <paramref name="model"/> as pieces flying out from <paramref name="centre"/>, under <paramref name="parent"/>.</summary>
    public static Debris From(Node3D model, Vector3 centre, float size, Node parent)
    {
        var debris = new Debris { Name = "Debris" };
        parent.AddChild(debris);
        var random = new RandomNumberGenerator();
        var force = Mathf.Clamp(size / 3, 0.8f, 2.5f);
        foreach (var mesh in model.FindChildren("*", nameof(MeshInstance3D), recursive: true, owned: false).OfType<MeshInstance3D>())
        {
            if (mesh.Mesh is null || !mesh.IsVisibleInTree())
                continue;
            var piece = new MeshInstance3D { Mesh = mesh.Mesh, MaterialOverride = mesh.MaterialOverride, Layers = 1 };
            debris.AddChild(piece);
            debris._pieces.Add(new Piece(piece, Vector3.Zero, Vector3.Zero));
            piece.GlobalTransform = mesh.GlobalTransform;
            var outward = piece.GlobalPosition - centre;
            outward.Y = 0;
            var direction = (outward.LengthSquared() > 0.01f ? outward.Normalized() : new Vector3(random.RandfRange(-1, 1), 0, random.RandfRange(-1, 1)).Normalized())
                            + Vector3.Up * random.RandfRange(0.8f, 1.8f);
            debris._pieces[^1].Velocity = direction.Normalized() * random.RandfRange(6, 13) * force;
            debris._pieces[^1].Spin = new Vector3(random.RandfRange(-9, 9), random.RandfRange(-9, 9), random.RandfRange(-9, 9));
        }
        return debris;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _age += dt;
        foreach (var piece in _pieces)
        {
            var node = piece.Node;
            if (_age > SinkFrom)
            {
                node.GlobalPosition += Vector3.Down * dt * 0.6f;
                continue;
            }
            if (piece.Resting)
                continue;
            piece.Velocity += Vector3.Down * Gravity * dt;
            var position = node.GlobalPosition + piece.Velocity * dt;
            var ground = HeightField.Current?.At(position.X, position.Z) ?? 0;
            if (position.Y < ground)
            {
                // A bounce, losing most of the speed; slow pieces lie still.
                position.Y = ground;
                piece.Velocity = new Vector3(piece.Velocity.X * 0.55f, -piece.Velocity.Y * 0.35f, piece.Velocity.Z * 0.55f);
                piece.Spin *= 0.5f;
                if (piece.Velocity.Length() < 1.5f)
                    piece.Resting = true;
            }
            node.GlobalPosition = position;
            if (piece.Spin.LengthSquared() > 0.0001f)
                node.GlobalBasis = new Basis(piece.Spin.Normalized(), piece.Spin.Length() * dt) * node.GlobalBasis;
        }
        if (_age > Lifetime)
            QueueFree();
    }
}
