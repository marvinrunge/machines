using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Presentation;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The hangar's screen: the hero big on a lit turntable, turned by dragging and zoomed with the wheel, its weapons firing
/// on request. Gliders stand twenty metres up in their rigs, so it goes by the parts as <see cref="HeroStudio"/> does, and
/// the camera eases over when a model of another size takes the place of the one there. Wired is the remake's own addition.
/// </summary>
public partial class HangarScreen : MachineScreen
{
    /// <summary>How far above its disc a glider hovers.</summary>
    private const float Hover = 1.2f;

    /// <summary>How many moments of its idle motion a hero is measured at.</summary>
    private const int Moments = 12;

    private const float Ease = 7, HomeElevation = 0.3f, Exposure = 0.85f, Shine = 0.4f;

    private static readonly Lazy<Shader> DiscShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;

            uniform float turn = 0.0;
            uniform float surge = 0.0;
            uniform vec3 colour : source_color = vec3(0.32, 1.0, 0.2);

            void fragment() {
                vec2 p = (UV - 0.5) * 2.0;
                float r = length(p);
                float a = atan(p.y, p.x) + turn;
                float px = fwidth(r) * 1.5;
                float rim = smoothstep(px + 0.006, 0.0, abs(r - 0.93));
                float inner = smoothstep(px + 0.003, 0.0, abs(r - 0.70)) * 0.55;
                float mid = smoothstep(px + 0.002, 0.0, abs(r - 0.60)) * 0.3;
                float ticks = step(0.72, fract(a * 72.0 / 6.28318)) * step(0.85, r) * step(r, 0.91) * 0.7;
                float notches = step(0.9, fract(a * 8.0 / 6.28318)) * step(0.72, r) * step(r, 0.83) * 0.5;
                float sweep = pow(fract((a + TIME * 0.6) / 6.28318), 8.0) * smoothstep(0.93, 0.5, r) * smoothstep(0.2, 0.5, r) * 0.32;
                float wave = smoothstep(0.03, 0.0, abs(r - fract(TIME * 0.22) * 0.93)) * (1.0 - fract(TIME * 0.22)) * 0.18;
                float pool = smoothstep(1.0, 0.1, r) * 0.1;
                float lit = (rim + inner + mid + ticks + notches + sweep + wave + pool) * (1.0 + surge * 2.0);
                float blob = smoothstep(0.62, 0.1, r) * 0.7;
                float outside = smoothstep(1.0, 0.95, r);
                ALBEDO = colour * min(lit, 1.0) * (1.0 + lit);
                ALPHA = clamp(max(lit, blob) * outside, 0.0, 1.0);
            }
            """,
    });

    /// <summary>A weapon firing on the turntable: where its burst has got to.</summary>
    private sealed class Burst(Mounting mounting, WeaponSpec spec)
    {
        public Mounting Mounting { get; } = mounting;
        public WeaponSpec Spec { get; } = spec;
        public float Wait;
        public int Round;
    }

    private readonly List<Burst> _bursts = [];
    private readonly Dictionary<Rig, Aabb> _extents = [];
    private readonly Dictionary<Rig, HeroBand[]> _bands = [];
    private readonly Dictionary<Mounting, Vector3> _middles = [];
    private readonly ClassicAssets _assets;
    private Effects _effects = null!;
    private SupportEffects _support = null!;
    private MeshInstance3D _discMesh = null!;
    private ShaderMaterial _discLook = null!;
    private RigView? _view;
    private HangarBox[] _keepOff = [];
    private HangarShot? _wanted;
    private Vector2 _shift;
    private float _disc = 1, _surge, _time;
    private readonly float _yawHome = -0.28f;
    private bool _turning, _handled;
    private HullSpot[] _spots = [];
    private Node3D?[] _spotParts = [];

    public HangarScreen(ClassicAssets assets) => _assets = assets;

    /// <summary>The model on the screen now.</summary>
    public RigView? View => _view;

    /// <summary>Whether a test fire is under way.</summary>
    public bool Firing => _bursts.Count > 0;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        base._Ready();
        // MachineScreen's key light comes out as green as its rim where it casts no shadow, in Classic; the hero shows its own colours.
        foreach (var light in Scene.GetChildren().OfType<DirectionalLight3D>().Where(l => l.LightEnergy > 1))
            light.LightColor = Colors.White;
        // The model viewer's light is made for a whole machine at a distance: this close, HD metal turned to it burns out white.
        if (!Classic)
        {
            Scene.GetChildren().OfType<WorldEnvironment>().First().Environment.TonemapExposure = Exposure;
            foreach (var light in Scene.GetChildren().OfType<DirectionalLight3D>())
                light.LightSpecular = Shine;
        }
        Yaw = _yawHome;
        Elevation = HomeElevation;
        // By a frustum and not a field of view, so that the picture can move aside without turning the hero (see Frame).
        Camera.Projection = Camera3D.ProjectionType.Frustum;
        Camera.Size = 2 * Camera.Near * Mathf.Tan(Mathf.DegToRad(Fov / 2));
        _effects = new Effects { Name = "Effects" };
        Scene.AddChild(_effects);
        _support = SupportEffects.Create(_assets);
        Scene.AddChild(_support);
        _discLook = new ShaderMaterial { Shader = DiscShader.Value };
        _discMesh = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * 2 },
            MaterialOverride = _discLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0, 0.01f, 0),
        };
        Scene.AddChild(_discMesh);
    }

    /// <summary>Puts a hero on the turntable in place of the one there, or nothing.</summary>
    public void Present(Rig? rig)
    {
        var (target, first) = (Target, _view is null);
        _view = rig is null ? null : RigView.Create(rig);
        Place(_view);
        Target = target;
        _bursts.Clear();
        _middles.Clear();
        _spots = [];
        if (_view is null)
            return;
        Frame(jump: first);
        _view.Animate(0.016f, 0, 0, 0, 0);
        foreach (var mounting in Enum.GetValues<Mounting>())
            if (Mount(mounting) is { } mount)
                _middles[mounting] = MiddleOf(mount);
        _spots = HeroRig.Spots(_assets, _view.Rig);
        _spotParts = _spots.Select(s => Part(s.Part)).ToArray();
    }

    /// <summary>What stands on the stage, which the hero has to keep clear of.</summary>
    public void KeepOff(IEnumerable<HangarBox> boxes)
    {
        var first = _keepOff.Length == 0;
        _keepOff = boxes.ToArray();
        Frame(jump: first);
    }

    /// <summary>The camera goes where the hero stands biggest and whole among the cards, seen as it is before anyone turns it.</summary>
    private void Frame(bool jump)
    {
        if (_view is null || !_bands.TryGetValue(_view.Rig, out var bands) || Size.X < 1 || Size.Y < 1)
            return;
        var shot = HangarFrame.Fit(bands, Size.X, Size.Y, _keepOff, Fov, HomeElevation);
        _wanted = shot;
        _disc = Math.Max(bands.Max(b => b.Radius) * 1.06f, 1.2f);
        if (jump)
            (Distance, Target, _shift) = (shot.Distance, new Vector3(0, shot.Height, 0), new Vector2(shot.Across, shot.Up));
    }

    /// <summary>The box of a hero's parts: the export's root and sprites do not count, and a glider gets room below.</summary>
    protected override Aabb Extent(Node3D view)
    {
        if (view is not RigView rigView)
            return base.Extent(view);
        var rig = rigView.Rig;
        if (_extents.TryGetValue(rig, out var kept))
            return kept;
        var points = HeroStudio.Points(rigView, rig);
        var (low, high) = (points[0], points[0]);
        foreach (var point in points)
            (low, high) = (new Vector3(Math.Min(low.X, point.X), Math.Min(low.Y, point.Y), Math.Min(low.Z, point.Z)),
                new Vector3(Math.Max(high.X, point.X), Math.Max(high.Y, point.Y), Math.Max(high.Z, point.Z)));
        if (low.Y > 3)
            low.Y -= Hover;
        var (middle, floor) = ((low + high) / 2, low.Y);
        _bands[rig] = HangarFrame.Bands(points.Concat(Swept(rigView)).Select(p => new System.Numerics.Vector3(p.X - middle.X, p.Y - floor, p.Z - middle.Z)).ToList());
        return _extents[rig] = new Aabb(low, high - low);
    }

    /// <summary>Where its parts get to in its idle motion, by their boxes' corners: a knight rises a good metre on its hip and sinks again.</summary>
    private static List<Vector3> Swept(RigView view)
    {
        var rig = view.Rig;
        var corners = new List<Vector3>();
        var cycle = rig.Idle.Select(i => i.Duration).DefaultIfEmpty(0).Max();
        if (cycle <= 0)
            return corners;
        var meshes = view.FindChildren("Mesh", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Select(m => (Mesh: m, Still: m.GlobalTransform)).ToList();
        for (var moment = 0; moment < Moments; moment++)
        {
            view.Animate(cycle / Moments, 0, 0, 0, 0);
            foreach (var (mesh, _) in meshes.Where(m => !m.Mesh.GlobalTransform.IsEqualApprox(m.Still)))
                for (var corner = 0; corner < 8; corner++)
                    corners.Add(mesh.GlobalTransform * mesh.GetAabb().GetEndpoint(corner));
        }
        return corners;
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } press:
                _turning = press.Pressed;
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                Zoom = Math.Min(Zoom * 1.1f, 3.5f);
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                Zoom = Math.Max(Zoom / 1.1f, 0.7f);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _turning:
                // Handled once, the swaying stops for good: the player has put the hero where they want it.
                _handled = true;
                Yaw += motion.Relative.X * 0.01f;
                Elevation = Math.Clamp(Elevation + motion.Relative.Y * 0.006f, 0.02f, 1.2f);
                break;
        }
    }

    public override void _Process(double delta)
    {
        var step = (float)delta;
        _time += step;
        // Left alone it sways a little, so that the light moves over it.
        if (!_handled)
            Yaw = _yawHome + Mathf.Sin(_time * 0.4f) * 0.2f;
        var ease = 1 - Mathf.Exp(-Ease * step);
        if (_wanted is { } shot)
        {
            Distance = Mathf.Lerp(Distance ?? shot.Distance, shot.Distance, ease);
            Target = Target.Lerp(new Vector3(0, shot.Height, 0), ease);
            _shift = _shift.Lerp(new Vector2(shot.Across, shot.Up), ease);
        }
        Camera.FrustumOffset = new Vector2(-_shift.X, _shift.Y) * Camera.Size;
        base._Process(delta);
        _surge = Mathf.MoveToward(_surge, 0, step * 1.6f);
        _discLook.SetShaderParameter("turn", Yaw);
        _discLook.SetShaderParameter("surge", _surge);
        _discMesh.Scale = Mathf.Lerp(_discMesh.Scale.X, _disc, ease) * Vector3.One;
        _view?.Animate(step, 0, 0, 0, 0);
        FireBursts(step);
    }

    // ---- where things are on the model ------------------------------------------------------------

    private int? Mount(Mounting mounting)
    {
        if (_view is null)
            return null;
        var parts = _view.Rig.Nodes.Select(n => new RigPart(n.Parent, n.Name)).ToArray();
        return RigGraft.MountOf(parts, mounting) is >= 0 and var mount ? mount : null;
    }

    /// <summary>The middle of what hangs from a mount, in the mount's own terms; its origin when it is bare.</summary>
    private Vector3 MiddleOf(int mount)
    {
        var rig = _view!.Rig;
        var (points, inverse) = (new List<Vector3>(), Part(mount)?.GlobalTransform.AffineInverse() ?? Transform3D.Identity);
        for (var i = 0; i < rig.Nodes.Length; i++)
        {
            var under = false;
            for (var at = i; at >= 0 && !under; at = rig.Nodes[at].Parent)
                under = at == mount;
            if (under && Part(i)?.GetNodeOrNull<MeshInstance3D>("Mesh") is { } mesh)
                points.Add(inverse * (mesh.GlobalTransform * mesh.GetAabb().GetCenter()));
        }
        return points.Count == 0 ? Vector3.Zero : points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / points.Count;
    }

    private Node3D? Part(int index) => _view?.FindChild($"Part{index}", true, false) as Node3D;

    /// <summary>Where a mount's weapon is in the small world, or the mount itself when bare; null if the model has no such mount.</summary>
    public Vector3? MountPoint(Mounting mounting)
    {
        if (Mount(mounting) is not { } mount || _view!.PartTransform(_view.Rig.Nodes[mount].Name) is not { } at)
            return null;
        return at * _middles.GetValueOrDefault(mounting);
    }

    /// <summary>Where a module's or ability's trace ends on the skin, in the small world, and how far that skin looks towards the camera, 1 straight at it; null if the model has no such spot.</summary>
    public (Vector3 At, float Facing)? HullPoint(WiredSlot slot)
    {
        var spot = slot.Kind == WiredSlotKind.Ability ? HeroHull.ModuleSpots + slot.Index : slot.Index;
        if (spot >= _spots.Length || _spotParts[spot] is not { } part)
            return null;
        var (local, skin) = (_spots[spot].At, _spots[spot].Facing);
        var at = part.GlobalTransform * new Vector3(local.X, local.Y, local.Z);
        var facing = (part.GlobalBasis * new Vector3(skin.X, skin.Y, skin.Z)).Normalized();
        return (at, facing.Dot((Camera.GlobalPosition - at).Normalized()));
    }

    /// <summary>A point of the small world on the screen, in the screen's own terms.</summary>
    public Vector2 Project(Vector3 point)
    {
        var size = (Vector2)Scene.Size;
        return Camera.UnprojectPosition(point) / size * Size;
    }

    // ---- firing and sparks ------------------------------------------------------------------------

    /// <summary>A weapon fires one burst, with its sound and recoil and what the game shows at its muzzle.</summary>
    public void Fire(Mounting mounting, WeaponSpec spec)
    {
        _bursts.RemoveAll(b => b.Mounting == mounting);
        _bursts.Add(new Burst(mounting, spec));
    }

    private void FireBursts(float step)
    {
        foreach (var burst in _bursts.ToList())
        {
            burst.Wait -= step;
            if (burst.Wait > 0)
                continue;
            Round(burst);
            var rounds = Math.Max(1, burst.Spec.BurstRounds);
            burst.Wait = burst.Spec.BurstDuration / rounds;
            if (++burst.Round >= rounds)
                _bursts.Remove(burst);
        }
    }

    private void Round(Burst burst)
    {
        if (_view is null || Mount(burst.Mounting) is not { } mount || _view.PartTransform(_view.Rig.Nodes[mount].Name) is not { } at)
            return;
        var (spec, weapon) = (burst.Spec, burst.Spec.TypeName);
        _view.Fire(burst.Mounting, spec);
        MenuSounds.Effect(SoundTable.FiringSound(weapon));
        var muzzle = at * Launch(spec, burst.Round);
        var along = at.Basis.X.Normalized();
        var mark = muzzle + along * Math.Clamp(spec.Range, 10, 40);
        if (weapon.StartsWith("ELECTRIC", StringComparison.Ordinal))
        {
            _support.Lightning(muzzle, muzzle + along * 8, null);
            return;
        }
        _effects.Shot(muzzle, mark, weapon, spec.ProjectileSpeed, spec);
        if (GraphicsSettings.Effects)
            return;
        _support.BolterFlash(muzzle, along, weapon, Effects.RoundLit(spec), ground: false);
        if (weapon.StartsWith("FLAME_THROWER", StringComparison.Ordinal))
            _support.FlameBalls(muzzle, mark, spec);
    }

    /// <summary>A barrel's LAUNCH_OFFSET, in Godot's axes: the original's have z up.</summary>
    private static Vector3 Launch(WeaponSpec spec, int round)
    {
        var offsets = spec.Presentation.Where(l => l.Keyword == "LAUNCH_OFFSET" && l.Args.Count >= 3)
            .Select(l => l.Args.Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();
        if (offsets.Count == 0)
            return Vector3.Zero;
        var offset = offsets[round % offsets.Count];
        return new Vector3(offset[0], offset[2], offset[1]);
    }

    /// <summary>Sparks fly where something was just fitted, and the turntable lights up.</summary>
    public void Spark(Vector3 at)
    {
        _surge = 1;
        _support.BoltSparks(at, Vector3.Zero, at + Camera.GlobalBasis.Z * 4);
        // They are made for a battlefield: this close they would hide the hero.
        if (_support.GetChildren().OfType<Node3D>().LastOrDefault() is { } spark)
            spark.Scale = Vector3.One * 0.35f;
    }
}
