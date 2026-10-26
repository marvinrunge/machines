using Godot;
using Machines.Import.Model;
using Machines.Sim.Presentation;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>Shows one simulation actor: its model, a selection ring and a health bar.</summary>
public partial class ActorView : Node3D
{
    private VisualInstance3D _ring = null!;
    private GpuParticles3D? _dust;
    private MeshInstance3D _health = null!;
    private MeshInstance3D _armour = null!;
    private Vector3 _target;
    private float _targetYaw;
    private bool _placed;

    public int ActorId { get; private set; }

    /// <summary>The race the view was built for; a view is rebuilt when its actor changes sides.</summary>
    public Race Race { get; private set; }
    public float Size { get; private set; } = 3;

    /// <summary>A building rather than a machine; it explodes with the construction sounds.</summary>
    public bool IsBuilding { get; private set; }

    /// <summary>The machine's voice-mail name (GRUNT, DOZER ...), kept for its last report after it is gone.</summary>
    public string? Voice { get; private set; }

    private GameAudio.EngineSound? _engine;
    private Vector3 _lastTarget;

    // Animated parts
    private RigView? _rigView;
    private float _maxSpeed;
    private float _aim;
    private Vector3 _lastPosition;
    private float _lastYaw;

    // Ground contact (MachPhysLocomotionMethodImpl): wheeled, tracked and legged machines stand on the ground
    // at their front and back ends and lean with it; hovering and gliding ones keep level.
    private bool _followsGround;
    private float _fore = 1, _aft = -1;
    private float _yaw;

    /// <summary>The direction the view faces, about the vertical (Godot's sense); leaning on slopes leaves it.</summary>
    public float Yaw => _yaw;

    // The model's box in its own space, for an eye when the export names no faceplate.
    private Aabb _bounds = new(new Vector3(-1, 0, -1), new Vector3(2, 2, 2));

    /// <summary>
    /// Where first person looks out from: the centre front of the faceplate, as the original attached its camera,
    /// or high at the front of the model without one.
    /// </summary>
    public Vector3 Eye => _rigView?.Eye
        ?? GlobalTransform * new Vector3(_bounds.End.X, _bounds.Position.Y + _bounds.Size.Y * 0.8f, _bounds.GetCenter().Z);

    /// <summary>Render layer of a machine being driven: the first-person camera does not draw it, so it never blocks the view.</summary>
    public const uint DrivenLayer = 2;

    /// <summary>Hides the model from the first-person camera (it still casts its shadow and shows elsewhere).</summary>
    public void SetDriven(bool driven) => SetLayers(this, driven ? DrivenLayer : 1);

    private static void SetLayers(Node node, uint layers)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is VisualInstance3D visual)
                visual.Layers = layers;
            SetLayers(child, layers);
        }
    }

    public static ActorView Create(Actor actor, ClassicAssets assets, GameAudio audio)
    {
        var view = new ActorView { ActorId = actor.Id, Race = actor.Race, Name = $"Actor{actor.Id}", IsBuilding = actor is Construction };
        var (id, size) = ModelOf(actor);
        view.Size = Mathf.Max(size.X, size.Z);
        if (actor is Machine machine)
        {
            view.Voice = SoundTable.VoiceOf(machine);
            view._engine = audio.EngineFor(machine);
            if (view._engine is not null)
                view.AddChild(view._engine.Player);
        }

        // Machines with an exported rig move their parts; everything else is one mesh.
        if (actor is Machine rigged && assets.RigFor(id, Game.Look(actor.Race)) is { } rig)
        {
            view._rigView = RigView.Create(rig);
            view._maxSpeed = rigged.Stats.Speed;
            view.AddChild(view._rigView);
        }
        else
        {
            var body = new MeshInstance3D { Mesh = assets.MeshFor(id, Game.Look(actor.Race), size), Name = "Body" };
            if (!assets.HasClassicModels)
                body.Position = new Vector3(0, size.Y / 2, 0);
            view.AddChild(body);
        }

        if (MeshBounds(view, Transform3D.Identity) is { } modelBounds)
            view._bounds = modelBounds;
        if (actor is Machine grounded && grounded.Stats.Locomotion is Locomotion.Wheels or Locomotion.Tracks or Locomotion.Spider)
        {
            // The original takes the ends from the machine's bounding box along its facing (x).
            var bounds = MeshBounds(view, Transform3D.Identity);
            view._followsGround = true;
            (view._fore, view._aft) = bounds is { } b && b.End.X > 0.01f && b.Position.X < -0.01f
                ? (b.End.X, b.Position.X)
                : (grounded.Radius, -grounded.Radius);
        }

        // The remake lays the selection ring onto the ground (over slopes too); the classic look floats a torus.
        view._ring = GraphicsSettings.Remake
            ? new Decal
            {
                TextureAlbedo = Effects.Ring,
                TextureEmission = Effects.RingGlow,
                EmissionEnergy = 2,
                Modulate = new Color(0.35f, 1f, 0.35f),
                AlbedoMix = 1,
                Size = new Vector3(view.Size * 1.6f + 2, 16, view.Size * 1.6f + 2),
                CullMask = TerrainBuilder.GroundLayer,
                Visible = false,
            }
            : new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = view.Size * 0.55f, OuterRadius = view.Size * 0.62f + 0.3f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 1f, 0.3f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
                Position = new Vector3(0, 0.2f, 0),
                Visible = false,
            };
        view.AddChild(view._ring);

        // Ground machines kick up dust as they go.
        if (GraphicsSettings.Remake && view._followsGround)
        {
            view._dust = Effects.DustTrail();
            view._dust.Position = new Vector3(view._aft * 0.6f, 0.3f, 0);
            view.AddChild(view._dust);
        }

        view._health = Bar(new Color(0.2f, 0.9f, 0.2f));
        view._armour = Bar(new Color(0.4f, 0.7f, 1f));
        view._health.Position = new Vector3(0, size.Y + 2.5f, 0);
        view._armour.Position = new Vector3(0, size.Y + 3.1f, 0);
        view.AddChild(view._health);
        view.AddChild(view._armour);
        return view;
    }

    /// <summary>The bounding box of every mesh under <paramref name="node"/>, in the view's own space.</summary>
    private static Aabb? MeshBounds(Node node, Transform3D toView)
    {
        Aabb? bounds = null;
        foreach (var child in node.GetChildren())
        {
            if (child is not Node3D child3D)
                continue;
            var transform = toView * child3D.Transform;
            if (child is MeshInstance3D { Mesh: { } mesh })
            {
                var box = transform * mesh.GetAabb();
                bounds = bounds is { } b ? b.Merge(box) : box;
            }
            if (MeshBounds(child, transform) is { } inner)
                bounds = bounds is { } b ? b.Merge(inner) : inner;
        }
        return bounds;
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
    /// <param name="aimAt">What the machine is attacking, for its upper body to turn to.</param>
    public void Sync(Actor actor, Actor? aimAt = null)
    {
        _target = World(actor.Position);
        if (actor is Machine machine)
        {
            // The upper body's angle relative to the hull, in the original's sense (counter-clockwise).
            var toTarget = aimAt is null ? 0 : MathF.Atan2(aimAt.Position.Y - actor.Position.Y, aimAt.Position.X - actor.Position.X) - machine.Facing;
            _aim = aimAt is null ? 0 : Mathf.Wrap(toTarget, -Mathf.Pi, Mathf.Pi);
        }
        _targetYaw = actor switch
        {
            Machine m => -m.Facing,
            Construction c => -Mathf.DegToRad(c.Orientation),
            _ => 0,
        };
        if (!_placed)
        {
            Position = _target;
            _yaw = _targetYaw;
            Stand();
            _placed = true;
        }

        // Engines hum at rest and roar on the move; machines inside buildings are silent.
        if (_engine is not null)
        {
            var moving = _target.DistanceSquaredTo(_lastTarget) > 0.0001f;
            if (actor is Machine { Inside: not null })
                _engine.Player.Stop();
            else
                _engine.Update(moving);
            _lastTarget = _target;
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
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, t);
        Stand();
        if (_dust is not null && delta > 0)
            _dust.Emitting = Visible && new Vector2(Position.X - _lastPosition.X, Position.Z - _lastPosition.Z).Length() / (float)delta > 1.5f;

        if (_rigView is not null && delta > 0)
        {
            // Speed along the hull's facing, from how the view itself moved this frame.
            var moved = Position - _lastPosition;
            var facing = new Vector3(Mathf.Cos(-_yaw), 0, Mathf.Sin(-_yaw));
            var speed = new Vector3(moved.X, 0, moved.Z).Dot(facing) / (float)delta;
            var turnRate = -Mathf.AngleDifference(_lastYaw, _yaw) / (float)delta;
            _rigView.Animate((float)delta, speed, turnRate, _maxSpeed, _aim);
        }
        _lastPosition = Position;
        _lastYaw = _yaw;
        // Bars face the camera.
        if (GetViewport().GetCamera3D() is { } camera)
        {
            _health.GlobalRotation = new Vector3(0, camera.GlobalRotation.Y, 0);
            _armour.GlobalRotation = new Vector3(0, camera.GlobalRotation.Y, 0);
        }
    }

    /// <summary>
    /// Sets the view's height and lean on the ground at its position and yaw. Machines on the ground do as
    /// MachPhysLocomotionMethodImpl::foreAftSurfacePosition: the ground is sampled under the front and back
    /// ends, the base height is interpolated between them, and the machine tilts along the line joining them
    /// and rolls with the average of the ground there.
    /// </summary>
    private void Stand()
    {
        var yawOnly = new Basis(Vector3.Up, _yaw).Scaled(Scale);
        if (!_followsGround || HeightField.Current is not { } ground)
        {
            Basis = yawOnly;
            return;
        }
        var facing = new Vector3(Mathf.Cos(-_yaw), 0, Mathf.Sin(-_yaw));
        var fore = Position + facing * _fore;
        var aft = Position + facing * _aft;
        fore.Y = ground.At(fore.X, fore.Z);
        aft.Y = ground.At(aft.X, aft.Z);
        Position = Position with { Y = (_fore * aft.Y - _aft * fore.Y) / (_fore - _aft) };

        var along = (fore - aft).Normalized();
        var side = ground.Normal(fore.X, fore.Z).Cross(along) + ground.Normal(aft.X, aft.Z).Cross(along);
        var up = side.Cross(along).Normalized();
        if (up.Y < 0)
            up = -up;
        // The model faces +x; the basis keeps Godot's handedness (z = x × y).
        var z = along.Cross(up).Normalized();
        Basis = new Basis(along, z.Cross(along), z).Scaled(Scale);
    }

    /// <summary>Original ground coordinates (x, y) to Godot (x, ground height, y).</summary>
    public static Vector3 World(System.Numerics.Vector2 p) => new(p.X, HeightField.Current?.At(p.X, p.Y) ?? 0, p.Y);
}
