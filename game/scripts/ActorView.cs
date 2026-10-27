using Godot;
using Machines.Import.Model;
using Machines.Sim.Presentation;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>Shows one simulation actor: its model, a wireframe box when selected or pointed at, and a health bar.</summary>
public partial class ActorView : Node3D
{
    private MeshInstance3D _marker = null!;
    private StandardMaterial3D _markerMaterial = null!;
    private bool _selected, _highlighted;
    private float _healthShare = 1;
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

    /// <summary>
    /// The weapon at <paramref name="mounting"/> fires one round and moves with it: back and forward by its RECOIL
    /// distance and times, fitted into the round's share of the burst as MachPhysWeapon::recoil fitted them, or
    /// the autocannon's barrels turn on.
    /// </summary>
    public void Fire(Machines.Sim.Combat.Mounting mounting, Machines.Import.Model.WeaponSpec weapon)
    {
        if (_rigView is null)
            return;
        var recoil = weapon.Presentation.FirstOrDefault(l => l.Keyword == "RECOIL" && l.Args.Count >= 3)?.Args
            .Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray() ?? [0f, 0.05f, 0.15f];
        var (distance, back, fore) = (recoil[0], recoil[1], recoil[2]);
        var round = weapon.BurstDuration / Math.Max(1, weapon.BurstRounds);
        // Rounds faster than the recoil cut its way forward short.
        if (round - (back + fore) < 0.01f)
            fore = Math.Max(round - back, 0.01f);
        var side = mounting switch
        {
            Machines.Sim.Combat.Mounting.Left => "left",
            Machines.Sim.Combat.Mounting.Right => "right",
            _ => "top",
        };
        _rigView.Fire(side, distance, back, fore, round, turn: weapon.TypeName == "AUTO_CANNON");
    }

    /// <summary>A building's interior (see <see cref="RigView.ShowInterior"/>).</summary>
    public bool HasInterior => _rigView?.HasInterior ?? false;

    /// <summary>Shows the interior while first person looks out from <paramref name="eye"/> inside it; null hides it.</summary>
    public void ShowInteriorFrom(Vector3? eye)
    {
        if (_rigView is not null)
            _rigView.ShowInterior = eye is { } point && _rigView.InInterior(point);
    }

    /// <summary>The model's bounds in the view's own space.</summary>
    public Aabb Bounds => _bounds;

    /// <summary>
    /// Where the upper body (or a tower's head) should turn, radians anticlockwise from the hull in the original's
    /// axes; <see cref="Sync"/> sets it to the target, first person to where the driver looks.
    /// </summary>
    public float UpperBodyAngle
    {
        set => _aim = value;
    }

    /// <summary>Where the upper body has turned to so far (it turns at its own rate), radians as <see cref="UpperBodyAngle"/>.</summary>
    public float UpperBodyNow => _rigView?.UpperBody ?? 0;

    /// <summary>The middle of the model, where rounds fired at it fly to (high up for a flyer).</summary>
    public Vector3 Centre => GlobalTransform * _bounds.GetCenter();

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

    private GpuParticles3D? _damageSmoke, _thickSmoke, _damageFire;

    /// <summary>
    /// Smoke and fire as the damage mounts, at the original's thresholds: a machine smokes black from 65 % damage,
    /// and from 90 % it smokes thickly and burns (MachPhysMachineDamage); a finished building smokes from 40 %,
    /// burns from 50 % and smokes thickly from 75 % (MachPhysEntityDamage). The smoke trails behind what moves.
    /// </summary>
    private void ShowDamage(Actor actor)
    {
        if (actor.MaxHitPoints <= 0 || actor is Construction { Complete: false } || actor is not (Machine or Construction))
            return;
        var damage = 100 * (1 - actor.HitPoints / (float)actor.MaxHitPoints);
        var building = actor is Construction;
        var size = Mathf.Clamp(Size * 0.5f, 1.5f, 6);
        _damageSmoke = Emitter(_damageSmoke, damage >= (building ? 40 : 65),
            () => Effects.Plume(4, 3, size * 3, size * 1.2f, new Color(0.08f, 0.08f, 0.08f, 0.85f)));
        _thickSmoke = Emitter(_thickSmoke, damage >= (building ? 75 : 90),
            () => Effects.Plume(6, 4, size * 4, size * 1.4f, new Color(0.14f, 0.07f, 0.05f, 0.8f)));
        _damageFire = Emitter(_damageFire, damage >= (building ? 50 : 90), () =>
        {
            var fire = Effects.DamageFire(size * 0.8f, size);
            // In the remake the flames light what is around them.
            if (GraphicsSettings.Remake)
                fire.AddChild(new OmniLight3D { LightColor = new Color(1, 0.55f, 0.2f), LightEnergy = 2, OmniRange = size * 4, Position = Vector3.Up * size * 0.5f });
            return fire;
        });
    }

    /// <summary>Starts or stops a damage emitter at the top of the model, making it the first time it is needed.</summary>
    private GpuParticles3D? Emitter(GpuParticles3D? emitter, bool on, Func<GpuParticles3D> make)
    {
        if (emitter is null)
        {
            if (!on)
                return null;
            emitter = make();
            AddChild(emitter);
            emitter.Position = new Vector3(_bounds.GetCenter().X, _bounds.End.Y * 0.9f, _bounds.GetCenter().Z);
        }
        emitter.Emitting = on;
        if (emitter.GetChildCount() > 0 && emitter.GetChild(0) is OmniLight3D light)
            light.Visible = on;
        return emitter;
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
            // A machine's upper body turns at its BODY_TURN_RATE, degrees per second (MachPhysTurnerTracker).
            if (actor is Machine { Stats.BodyTurnRate: > 0 and var bodyRate })
                view._rigView.TurnRate = Mathf.DegToRad(bodyRate);
            if (actor is Construction building)
            {
                AddDoors(view._rigView, building);
                // A tower's head turns at its MAX_ROTATION, degrees per second (MachPhysTurnerTracker).
                if (building.Stats.MaxRotation is > 0 and var rate)
                    view._rigView.TurnRate = Mathf.DegToRad(rate);
            }
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

        // Selected or pointed at, the model stands in a wireframe box, as the original marked it (MachPhysMarker).
        view._markerMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = MarkerGreen,
            DisableReceiveShadows = true,
            // An edge passing right by the camera (a tall building's, seen from close) would sweep across the view.
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelDither,
            DistanceFadeMinDistance = 3,
            DistanceFadeMaxDistance = 10,
        };
        view._marker = new MeshInstance3D
        {
            Name = "Marker",
            Mesh = MarkerBox(view._bounds),
            MaterialOverride = view._markerMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        view.AddChild(view._marker);
        view._model = (Node?)view._rigView ?? view.GetNodeOrNull("Body");

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
                    // A faint beam in the haze; at full strength it washed the ground out at night.
                    LightVolumetricFogEnergy = 0.25f,
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
        get => _selected;
        set
        {
            _selected = value;
            ShowMarker();
        }
    }

    /// <summary>Pointed at by the mouse while not selected: the box shows in blue.</summary>
    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            _highlighted = value;
            ShowMarker();
        }
    }

    // The marker's colours (MachPhysIHealthMaterials): blue for pointed at; selected, green above 90 % of its hit
    // points, through yellow at 50 % to red at 10 % and below.
    private static readonly Color MarkerBlue = new(0, 122 / 255f, 179 / 255f), MarkerGreen = new(0, 194 / 255f, 0),
        MarkerYellow = new(238 / 255f, 215 / 255f, 6 / 255f), MarkerRed = new(224 / 255f, 0, 0);

    private static Color HealthColour(float percent) => percent switch
    {
        > 90 => MarkerGreen,
        > 50 => MarkerYellow.Lerp(MarkerGreen, (percent - 50) / 40),
        > 10 => MarkerRed.Lerp(MarkerYellow, (percent - 10) / 40),
        _ => MarkerRed,
    };

    private void ShowMarker()
    {
        _marker.Visible = _selected || _highlighted;
        // The original picked one of 20 shades, each standing for a twentieth of the hit points.
        var shade = (int)(20 * Mathf.Clamp(_healthShare, 0, 1) * 100 / 100.1f);
        var colour = _selected ? HealthColour(100f * shade / 19) : MarkerBlue;
        if (_markerMaterial.AlbedoColor != colour)
            _markerMaterial.AlbedoColor = colour;
    }

    /// <summary>
    /// The marker: the edges of a box round the model, a metre out to the sides, from half a metre above its foot
    /// (or the ground, for what is sunk into it) to half a metre over its top (MachPhysMarker's 3D wireframe). The
    /// edges are thin bars, so they keep their weight on a large screen, and lines, so they never vanish far off.
    /// </summary>
    private static ArrayMesh MarkerBox(Aabb bounds)
    {
        var min = new Vector3(bounds.Position.X - 1, Mathf.Max(bounds.Position.Y, 0) + 0.5f, bounds.Position.Z - 1);
        var max = new Vector3(bounds.End.X + 1, bounds.End.Y + 0.5f, bounds.End.Z + 1);
        var corners = new Vector3[8];
        for (var i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        // Corner pairs differing in one coordinate.
        var edges = new List<(Vector3 A, Vector3 B)>();
        for (var i = 0; i < 8; i++)
            foreach (var bit in new[] { 1, 2, 4 })
                if ((i & bit) == 0)
                    edges.Add((corners[i], corners[i | bit]));

        var mesh = new ArrayMesh();
        var lines = new SurfaceTool();
        lines.Begin(Mesh.PrimitiveType.Lines);
        foreach (var (a, b) in edges)
        {
            lines.AddVertex(a);
            lines.AddVertex(b);
        }
        lines.Commit(mesh);

        var bars = new SurfaceTool();
        bars.Begin(Mesh.PrimitiveType.Triangles);
        var half = Mathf.Clamp(Mathf.Max(max.X - min.X, max.Z - min.Z) * 0.006f, 0.03f, 0.12f);
        foreach (var (a, b) in edges)
        {
            var low = a.Min(b) - Vector3.One * half;
            var high = a.Max(b) + Vector3.One * half;
            Vector3 C(int i) => new((i & 1) == 0 ? low.X : high.X, (i & 2) == 0 ? low.Y : high.Y, (i & 4) == 0 ? low.Z : high.Z);
            // The bar's six faces, two triangles each.
            foreach (var face in new[] { (0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3) })
            {
                var (p, q, r, s) = face;
                foreach (var corner in new[] { p, q, r, p, r, s })
                    bars.AddVertex(C(corner));
            }
        }
        bars.Commit(mesh);
        return mesh;
    }

    // ---- picking ----------------------------------------------------------------------------------

    // The model's own meshes, without the marker, bars and effects around it.
    private Node? _model;

    /// <summary>
    /// How far along the ray the model is first met, or null: through the box round the whole model and then the
    /// box of each of its parts, as the original found what lies under the cursor
    /// (W4dComposite::defaultCompositeIntersectsLine, MEDIUM accuracy).
    /// </summary>
    public float? Hit(Vector3 from, Vector3 direction)
    {
        if (!Visible || _model is null || BoxHit(GlobalTransform, _bounds, from, direction) is null)
            return null;
        return PartsHit(_model, from, direction);
    }

    /// <summary>How far along the ray it first meets the box of a mesh under <paramref name="root"/>, or null.</summary>
    public static float? PartsHit(Node root, Vector3 from, Vector3 direction)
    {
        float? nearest = null;
        foreach (var mesh in root.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Append(root as MeshInstance3D).OfType<MeshInstance3D>())
            if (mesh.Mesh is { } shape && mesh.IsVisibleInTree() && BoxHit(mesh.GlobalTransform, shape.GetAabb(), from, direction) is { } distance
                && (nearest is null || distance < nearest))
                nearest = distance;
        return nearest;
    }

    /// <summary>Where a ray enters a box placed by <paramref name="transform"/>, in the ray's own lengths (0 from inside), or null.</summary>
    private static float? BoxHit(Transform3D transform, Aabb box, Vector3 from, Vector3 direction)
    {
        // In the box's own space the ray keeps its parameter: the transform is affine.
        var inverse = transform.AffineInverse();
        var origin = inverse * from;
        var along = inverse.Basis * direction;
        float near = 0, far = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var (o, d, low, high) = (origin[axis], along[axis], box.Position[axis], box.End[axis]);
            if (Mathf.Abs(d) < 1e-8f)
            {
                if (o < low || o > high)
                    return null;
                continue;
            }
            var (t1, t2) = ((low - o) / d, (high - o) / d);
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            near = Mathf.Max(near, t1);
            far = Mathf.Min(far, t2);
            if (near > far)
                return null;
        }
        return near;
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
    /// <param name="aimAt">What the machine or tower is attacking, for its upper body or head to turn to.</param>
    public void Sync(Actor actor, Actor? aimAt = null)
    {
        _target = World(actor.Position);
        // The upper body's (or a tower head's) angle relative to the hull, in the original's sense
        // (counter-clockwise); with nothing to aim at it turns back to the front.
        var facing = actor switch
        {
            Machine m => m.Facing,
            Construction c => Mathf.DegToRad(c.Orientation),
            _ => 0,
        };
        var toTarget = aimAt is null ? 0 : MathF.Atan2(aimAt.Position.Y - actor.Position.Y, aimAt.Position.X - actor.Position.X) - facing;
        _aim = aimAt is null ? 0 : Mathf.Wrap(toTarget, -Mathf.Pi, Mathf.Pi);
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
        ShowDamage(actor);

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
        // The marker of a selected actor takes the colour of its health.
        var share = actor.HitPoints / (float)Mathf.Max(1, actor.MaxHitPoints);
        if (share != _healthShare)
        {
            _healthShare = share;
            ShowMarker();
        }
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
