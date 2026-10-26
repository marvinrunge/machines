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
    private Light3D? _lamp;
    private float _lampEnergy;
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

    // Hovering and gliding machines lean with their motion.
    private bool _leans;
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
    /// <summary>
    /// Where a weapon's round leaves it (MachPhysWeapon::launchTransform): its mounting part (r_gun, l_gun or
    /// gun, MachPhysCanAttack) moved out by the weapon's LAUNCH_OFFSET for that barrel; null without a rig.
    /// </summary>
    public Vector3? Muzzle(Machines.Sim.Combat.Mounting mounting, Machines.Import.Model.WeaponSpec weapon, int barrel)
    {
        var part = mounting switch
        {
            Machines.Sim.Combat.Mounting.Right => "r_gun",
            Machines.Sim.Combat.Mounting.Left => "l_gun",
            _ => "gun",
        };
        if (_rigView?.PartTransform(part) is not { } at)
            return null;
        var offsets = weapon.Presentation.Where(l => l.Keyword == "LAUNCH_OFFSET" && l.Args.Count >= 3)
            .Select(l => l.Args.Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
            .ToList();
        // Original axes, z up.
        var offset = offsets.Count > 0 ? offsets[barrel % offsets.Count] : [0f, 0f, 0f];
        return at * new Vector3(offset[0], offset[2], offset[1]);
    }

    public Vector3 Eye => _rigView?.Eye
        ?? GlobalTransform * new Vector3(_bounds.End.X, _bounds.Position.Y + _bounds.Size.Y * 0.8f, _bounds.GetCenter().Z);

    /// <summary>The Gorilla's punch: one of its two plans, picked at random as the original did (MachPhysPunchWeapon).</summary>
    public void Punch() => Play(Random.Shared.Next(2) == 0 ? "punchA" : "punchB");

    /// <summary>Plays one of the machine's exported animations once; false without a rig or that animation.</summary>
    public bool Play(string animation) => _rigView?.Play(animation) ?? false;

    /// <summary>A building's doors open for machines coming in or out.</summary>
    public bool DoorsOpen
    {
        set
        {
            if (_rigView is not null)
                _rigView.DoorsOpen = value;
        }
    }

    /// <summary>
    /// The doors of a building's ENTRANCE lines (MachPhysConstructionData::addEntrance): across the way through,
    /// at the door point, 6.9 m wide for a small door and 11.9 m for a large one.
    /// </summary>
    private static void AddDoors(RigView rig, Construction building)
    {
        foreach (var entrance in building.Stats.Geometry.Where(g => g.Keyword == "ENTRANCE" && g.Args.Count >= 8))
        {
            float F(int i) => float.Parse(entrance.Args[i], System.Globalization.CultureInfo.InvariantCulture);
            var through = new Vector2(F(2) - F(0), F(3) - F(1));
            if (through.LengthSquared() < 0.0001f)
                continue;
            var width = entrance.Args[7] == "LARGE_DOOR" ? 11.9f : 6.9f;
            rig.AddDoors(entrance.Args[6], new Vector2(through.Y, -through.X).Normalized() * width);
        }
    }

    // A constructor at work (MachPhysConstructor::startConstructing): per jet a torch beam reaching for the
    // building's centre, sparks, and a flickering light.
    private readonly List<(string Jet, Node3D Torch, Node3D Sparks, OmniLight3D Light)> _jets = [];
    private Vector3? _constructAt;
    private double _constructTime;

    /// <summary>
    /// A constructor builds or repairs at <paramref name="target"/> (the building's centre), or stops (null):
    /// its arms play the building cycle and its jets reach out with the original's torch beam and sparks.
    /// </summary>
    public void Construct(Vector3? target, Mesh? torch, Mesh? sparks)
    {
        if (_rigView is null)
            return;
        _rigView.Constructing = target is not null;
        _constructAt = target;
        if (target is null)
        {
            foreach (var jet in _jets)
            {
                jet.Torch.QueueFree();
                jet.Sparks.QueueFree();
                jet.Light.QueueFree();
            }
            _jets.Clear();
            return;
        }
        if (_jets.Count > 0)
            return;
        foreach (var jet in new[] { "l_jet", "r_jet" })
        {
            if (_rigView.PartTransform(jet) is null)
                continue;
            var beam = new MeshInstance3D { Mesh = torch ?? new BoxMesh { Size = new Vector3(5, 0.15f, 0.15f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            var spray = new MeshInstance3D { Mesh = sparks ?? new SphereMesh { Radius = 0.3f, Height = 0.6f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            // The original's light: blue-white, a few metres.
            var light = new OmniLight3D { LightColor = new Color(0.6f, 0.8f, 1), LightEnergy = 3, OmniRange = 10 };
            AddChild(beam);
            AddChild(spray);
            AddChild(light);
            _jets.Add((jet, beam, spray, light));
        }
    }

    private void UpdateJets(double delta)
    {
        if (_constructAt is not { } target || _rigView is null)
            return;
        _constructTime += delta;
        // The light's flicker (on, off at 0.3 s, on at 0.5 s ... over 1.1 s).
        var t = _constructTime % 1.1;
        var lit = t < 0.3 || t is >= 0.5 and < 0.6 || t is >= 0.8 and < 0.9;
        foreach (var (jet, torch, sparks, light) in _jets)
        {
            if (_rigView.PartTransform(jet) is not { } at)
                continue;
            var from = at.Origin;
            // Level, as the original flattened the beam's direction, and stretched to reach: the torch is 5 m long.
            var reach = new Vector3(target.X - from.X, 0, target.Z - from.Z);
            var length = reach.Length();
            if (length > 0.1f)
            {
                var along = reach / length;
                torch.GlobalTransform = new Transform3D(new Basis(along * (length / 5), Vector3.Up, along.Cross(Vector3.Up)), from);
            }
            sparks.GlobalTransform = new Transform3D(new Basis(Vector3.Up, (float)GD.RandRange(0, Mathf.Tau)), from);
            sparks.Visible = GD.Randf() < 0.8f;
            light.GlobalPosition = from;
            light.Visible = lit;
        }
    }

    /// <summary>A building at work plays its working motion (fans turn, arms dig) and smokes from its chimneys.</summary>
    public bool Working
    {
        set
        {
            if (_rigView is not null)
                _rigView.Working = value;
            _working = value;
        }
    }

    private bool _working;
    private readonly List<GpuParticles3D> _plumes = [];

    /// <summary>The building's chimneys (<see cref="ChimneyTable"/>): an emitter at each, smoking while it works.</summary>
    private void AddChimneys(Construction building)
    {
        var rise = ChimneyTable.Rise(building.Spec.Type);
        foreach (var chimney in ChimneyTable.For(building.Spec.Type, building.Spec.SubType, building.Stats.HardwareLevel))
        {
            var colour = chimney.Smoke switch
            {
                ChimneySmoke.Civilian => new Color(0.86f, 0.87f, 0.88f, 0.55f),
                ChimneySmoke.Military => new Color(0.32f, 0.04f, 0.04f, 0.6f),
                _ => new Color(0.22f, 0.21f, 0.2f, 0.65f),
            };
            var plume = Effects.Plume(chimney.Wisps, rise, ChimneyTable.Height, ChimneyTable.WispSize, colour);
            // Original axes, z up, as the models (see ClassicAssets): y and z swap.
            plume.Position = new Vector3(chimney.X, chimney.Z, chimney.Y);
            AddChild(plume);
            _plumes.Add(plume);
        }
    }

    /// <summary>Smoke flows from the chimneys while the building works; what is in the air rises on when it stops.</summary>
    private void Smoke()
    {
        foreach (var plume in _plumes)
            if (plume.Emitting != _working)
                plume.Emitting = _working;
    }

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

        // Machines and buildings with an exported rig move their parts; everything else is one mesh.
        if (assets.RigFor(id, Game.Look(actor.Race)) is { } rig)
        {
            view._rigView = RigView.Create(rig);
            view._maxSpeed = actor is Machine rigged ? rigged.Stats.Speed : 0;
            if (actor is Construction building)
                AddDoors(view._rigView, building);
            view.AddChild(view._rigView);
        }
        else
        {
            var body = new MeshInstance3D { Mesh = assets.MeshFor(id, Game.Look(actor.Race), size), Name = "Body" };
            if (!assets.HasClassicModels)
                body.Position = new Vector3(0, size.Y / 2, 0);
            view.AddChild(body);
        }

        if (actor is Construction works)
            view.AddChimneys(works);

        if (MeshBounds(view, Transform3D.Identity) is { } modelBounds)
            view._bounds = modelBounds;
        view._leans = actor is Machine { Stats.Locomotion: Locomotion.Hover or Locomotion.Glider };
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

        // At night buildings light their surroundings and machines switch on headlights.
        if (GraphicsSettings.Remake)
        {
            if (actor is Construction)
            {
                view._lamp = new OmniLight3D
                {
                    LightColor = new Color(1, 0.75f, 0.45f),
                    OmniRange = view.Size * 1.2f + 8,
                    Position = new Vector3(0, Mathf.Max(4, size.Y * 0.8f), 0),
                    Visible = false,
                };
            }
            else
            {
                // A spot looks down its -z; turned to the machine's front (+x) and tipped a little down.
                view._lamp = new SpotLight3D
                {
                    LightColor = new Color(1, 0.95f, 0.82f),
                    SpotRange = 32,
                    SpotAngle = 32,
                    Position = new Vector3(Mathf.Max(0.5f, view._fore), 1.6f, 0),
                    Rotation = new Vector3(0, -Mathf.Pi / 2, 0),
                    Visible = false,
                };
                view._lamp.RotateObjectLocal(Vector3.Right, -0.25f);
            }
            view._lampEnergy = actor is Construction ? 2.2f : 4f;
            view.AddChild(view._lamp);
        }

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
            // Motion is measured from here, not from the origin.
            _lastPosition = Position;
            _lastYaw = _yaw;
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

        // Unfinished buildings stand as a pulsing green hologram, more solid as the work goes on.
        Hologram(actor is Construction { Complete: false } site ? 100 * site.Built / Mathf.Max(1, site.Stats.Cost) : null);

        Visible = actor is not Machine { Inside: not null };
    }

    // A building under construction is drawn all in see-through glowing green whose opacity swings between
    // nothing and a peak (MachPhysConstruction::doPercentageComplete, W4dSolidFadedAlphaPlan): ten bands of
    // progress, each with its frame cycle and peak.
    private StandardMaterial3D? _hologram;
    private (int Frames, float Peak) _band;
    private double _hologramTime;

    private static (int Frames, float Peak) Band(float percent) => percent switch
    {
        < 10 => (40, 0.25f), < 20 => (40, 0.4f), < 30 => (40, 0.55f), < 40 => (40, 0.85f), < 50 => (40, 0.55f),
        < 80 => (40, 1f), < 90 => (30, 1f), _ => (2, 1f),
    };

    private void Hologram(float? percent)
    {
        var model = (Node?)_rigView ?? GetNodeOrNull("Body");
        if (model is null)
            return;
        if (percent is not { } done)
        {
            if (_hologram is null)
                return;
            _hologram = null;
            foreach (var mesh in model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Append(model as MeshInstance3D).OfType<MeshInstance3D>())
                mesh.MaterialOverride = null;
            return;
        }
        _band = Band(done);
        if (_hologram is not null)
            return;
        _hologram = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0, 1, 0, 0.25f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            EmissionEnabled = GraphicsSettings.Remake,
            Emission = new Color(0, 1, 0),
            EmissionEnergyMultiplier = 1.2f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        foreach (var mesh in model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Append(model as MeshInstance3D).OfType<MeshInstance3D>())
            mesh.MaterialOverride = _hologram;
    }

    public override void _Process(double delta)
    {
        UpdateJets(delta);
        if (_hologram is not null)
        {
            // Frame by frame, as the original counted them at about 30 a second: the peak, down to nothing and back.
            _hologramTime += delta;
            var half = _band.Frames / 2f;
            var frame = (int)(_hologramTime * 30) % _band.Frames;
            _hologram.AlbedoColor = new Color(0, 1, 0, _band.Peak * Mathf.Abs(frame - half) / half);
        }
        var t = 1 - Mathf.Exp(-12 * (float)delta);
        Position = Position.Lerp(_target, t);
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, t);
        Stand();
        Smoke();
        if (_lamp is not null)
        {
            _lamp.Visible = PlanetLook.Night > 0.05f;
            _lamp.LightEnergy = PlanetLook.Night * _lampEnergy;
        }
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
            if (_leans)
                Lean((float)delta, speed, turnRate);
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

    // Hovering and gliding machines lean with their motion, as MachPhysGlider and MachPhysHoverBoots planned it:
    // nose down while speeding up, up while braking (at most 20°), and into a turn (at most 30°), with a lag.
    private float _speed, _pitch, _roll;

    private void Lean(float delta, float speed, float turnRate)
    {
        var acceleration = (speed - _speed) / delta;
        _speed = Mathf.Lerp(_speed, speed, 1 - Mathf.Exp(-6 * delta));
        var pitchWanted = Mathf.Clamp(-acceleration * Mathf.DegToRad(2.5f), -Mathf.DegToRad(20), Mathf.DegToRad(20));
        var rollWanted = Mathf.Clamp(turnRate * 0.6f, -Mathf.DegToRad(30), Mathf.DegToRad(30));
        var follow = 1 - Mathf.Exp(-3 * delta);
        _pitch = Mathf.Lerp(_pitch, pitchWanted, follow);
        _roll = Mathf.Lerp(_roll, rollWanted, follow);
        // The model's nose is +x and its right +z: pitching turns about z, rolling about x.
        _rigView!.Basis = new Basis(Vector3.Back, _pitch) * new Basis(Vector3.Right, _roll);
    }

    /// <summary>Blows the machine apart into its parts under <paramref name="parent"/> (see <see cref="Debris"/>); null for one without parts.</summary>
    public Debris? Shatter(Node parent)
    {
        if (_rigView is null)
            return null;
        // Parts fly from the middle of the machine, not from its feet.
        return Debris.From(_rigView, GlobalPosition + Vector3.Up * Mathf.Max(1, Size * 0.3f), Size, parent);
    }

    /// <summary>Original ground coordinates (x, y) to Godot (x, ground height, y).</summary>
    public static Vector3 World(System.Numerics.Vector2 p) => new(p.X, HeightField.Current?.At(p.X, p.Y) ?? 0, p.Y);
}
