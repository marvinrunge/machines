using Godot;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>Shows one simulation actor: its model, a selection ring and a health bar.</summary>
public partial class ActorView : Node3D
{
    private MeshInstance3D _ring = null!;
    private MeshInstance3D _health = null!;
    private MeshInstance3D _armour = null!;
    private Vector3 _target;
    private float _targetYaw;
    private bool _placed;

    public int ActorId { get; private set; }

    /// <summary>The race the view was built for; a view is rebuilt when its actor changes sides.</summary>
    public Race Race { get; private set; }
    public float Size { get; private set; } = 3;

    public static ActorView Create(Actor actor, ClassicAssets assets)
    {
        var view = new ActorView { ActorId = actor.Id, Race = actor.Race, Name = $"Actor{actor.Id}" };
        var (id, size) = ModelOf(actor);
        view.Size = Mathf.Max(size.X, size.Z);

        var body = new MeshInstance3D { Mesh = assets.MeshFor(id, Game.Look(actor.Race), size), Name = "Body" };
        if (!assets.HasClassicModels)
            body.Position = new Vector3(0, size.Y / 2, 0);
        view.AddChild(body);

        view._ring = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = view.Size * 0.55f, OuterRadius = view.Size * 0.62f + 0.3f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 1f, 0.3f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Position = new Vector3(0, 0.2f, 0),
            Visible = false,
        };
        view.AddChild(view._ring);

        view._health = Bar(new Color(0.2f, 0.9f, 0.2f));
        view._armour = Bar(new Color(0.4f, 0.7f, 1f));
        view._health.Position = new Vector3(0, size.Y + 2.5f, 0);
        view._armour.Position = new Vector3(0, size.Y + 3.1f, 0);
        view.AddChild(view._health);
        view.AddChild(view._armour);
        return view;
    }

    private static MeshInstance3D Bar(Color colour) => new()
    {
        Mesh = new BoxMesh { Size = new Vector3(1, 0.35f, 0.35f) },
        MaterialOverride = new StandardMaterial3D { AlbedoColor = colour, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        Visible = false,
    };

    public bool Selected
    {
        get => _ring.Visible;
        set => _ring.Visible = value;
    }

    /// <summary>The exported model id (<c>TYPE_LEVEL[_SUBTYPE][_COMBO]</c>) and a fallback size in metres.</summary>
    public static (string Id, Vector3 Size) ModelOf(Actor actor)
    {
        switch (actor)
        {
            case Machine m:
            {
                var parts = new List<string> { m.Spec.Type, m.Stats.HardwareLevel.ToString() };
                if (m.Spec.SubType is not null) parts.Add(m.Spec.SubType);
                if (m.WeaponCombo is not null) parts.Add(m.WeaponCombo);
                var r = m.Radius;
                return (string.Join('_', parts), new Vector3(r * 1.6f, r * 1.4f, r * 1.6f));
            }
            case Construction c:
            {
                var parts = new List<string> { c.Spec.Type, c.Stats.HardwareLevel.ToString() };
                if (c.Spec.SubType is not null) parts.Add(c.Spec.SubType);
                if (c.WeaponCombo is not null) parts.Add(c.WeaponCombo);
                var span = c.Footprint.Max - c.Footprint.Min;
                return (string.Join('_', parts), new Vector3(span.X, 8, span.Y));
            }
            default:
                return ("", Vector3.One * 3);
        }
    }

    /// <summary>Moves the view toward the actor's latest simulated state; called after each simulation tick.</summary>
    public void Sync(Actor actor)
    {
        _target = World(actor.Position);
        _targetYaw = actor switch
        {
            Machine m => -m.Facing,
            Construction c => -Mathf.DegToRad(c.Orientation),
            _ => 0,
        };
        if (!_placed)
        {
            Position = _target;
            Rotation = new Vector3(0, _targetYaw, 0);
            _placed = true;
        }

        var hurt = actor.HitPoints < actor.MaxHitPoints || actor.Armour < actor.MaxArmour;
        _health.Visible = _armour.Visible = hurt || Selected;
        _health.Scale = new Vector3(Mathf.Max(0.05f, Size * actor.HitPoints / Mathf.Max(1, actor.MaxHitPoints)), 1, 1);
        _armour.Scale = new Vector3(Mathf.Max(0.05f, Size * actor.Armour / Mathf.Max(1, actor.MaxArmour)), 1, 1);

        // Unfinished buildings rise out of the ground as they are built.
        if (actor is Construction { Complete: false } site)
            Scale = new Vector3(1, Mathf.Max(0.1f, site.Built / Mathf.Max(1, site.Stats.Cost)), 1);
        else
            Scale = Vector3.One;

        Visible = actor is not Machine { Inside: not null };
    }

    public override void _Process(double delta)
    {
        var t = 1 - Mathf.Exp(-12 * (float)delta);
        Position = Position.Lerp(_target, t);
        Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, _targetYaw, t), 0);
        // Bars face the camera.
        if (GetViewport().GetCamera3D() is { } camera)
        {
            _health.GlobalRotation = new Vector3(0, camera.GlobalRotation.Y, 0);
            _armour.GlobalRotation = new Vector3(0, camera.GlobalRotation.Y, 0);
        }
    }

    /// <summary>Original ground coordinates (x, y) to Godot (x, ground height, y).</summary>
    public static Vector3 World(System.Numerics.Vector2 p) => new(p.X, HeightField.Current?.At(p.X, p.Y) ?? 0, p.Y);
}
