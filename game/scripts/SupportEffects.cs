using Godot;

namespace Machines.Game;

/// <summary>
/// The support weapons' and the transporters' effects as the original drew them, from its own meshes
/// (<c>classic/models/fx</c>, see tools/classic-export): the healing beam of two helices turning against each other
/// (MachPhysHealHelix) and the green crosses round the machine it heals (MachPhysHealAura), the electric charger's
/// spinning lightning (MachPhysElectro) and the halo that runs up and down its victim (MachPhysLightningHalo), the
/// flames of a machine a flamethrower set alight (MachPhysMachineBurning) and the rings of light round a
/// transporter as it loads or unloads ore (MachPhysResourceLoading). Without the export they are left out.
/// </summary>
public partial class SupportEffects : Node3D
{
    private ClassicAssets _assets = null!;
    private float _time;

    public static SupportEffects Create(ClassicAssets assets) => new() { Name = "SupportEffects", _assets = assets };

    public override void _Process(double delta)
    {
        _time += (float)delta;
        UpdateBeams();
        UpdateAuras();
        UpdateTimed((float)delta);
    }

    // ---- healing ----------------------------------------------------------------------------------

    private sealed class Beam
    {
        public required Node3D Root { get; init; }
        public required MeshInstance3D Left { get; init; }
        public required MeshInstance3D Right { get; init; }
        public required StandardMaterial3D Material { get; init; }
        public Vector3 From, To;
        public bool Seen;
    }

    private readonly Dictionary<int, Beam> _beams = [];

    private sealed class Aura
    {
        public required Node3D Root { get; init; }
        public required List<(MeshInstance3D Cross, Vector3 Outward, Vector3 Start)> Radial { get; init; }
        public required MeshInstance3D Central { get; init; }
        public required StandardMaterial3D Material { get; init; }
        public required float Size { get; init; }
        public required float Clearance { get; init; }
        public float Started;
        public bool Seen;
    }

    private readonly Dictionary<ActorView, Aura> _auras = [];

    /// <summary>
    /// The healing going on now: each healer's beam from its charger to the machine it heals, and that machine's
    /// aura. Beams and auras of healing that stopped go away.
    /// </summary>
    public void ShowHealing(IEnumerable<(int Healer, Vector3 From, ActorView Patient)> healing)
    {
        foreach (var beam in _beams.Values)
            beam.Seen = false;
        foreach (var aura in _auras.Values)
            aura.Seen = false;
        foreach (var (healer, from, patient) in healing)
        {
            if (!_beams.TryGetValue(healer, out var beam) && NewBeam() is { } made)
                _beams[healer] = beam = made;
            if (beam is not null)
                (beam.From, beam.To, beam.Seen) = (from, patient.Centre, true);
            if (!_auras.TryGetValue(patient, out var aura) && NewAura(patient) is { } madeAura)
                _auras[patient] = aura = madeAura;
            if (aura is not null)
                aura.Seen = true;
        }
        foreach (var (healer, beam) in _beams.Where(b => !b.Value.Seen).ToList())
        {
            beam.Root.QueueFree();
            _beams.Remove(healer);
        }
        foreach (var (patient, aura) in _auras.Where(a => !a.Value.Seen || !IsInstanceValid(a.Key)).ToList())
        {
            if (IsInstanceValid(aura.Root))
                aura.Root.QueueFree();
            _auras.Remove(patient);
        }
    }

    /// <summary>The healing green, glowing, see-through and drawn on both sides as the original's emissive helices.</summary>
    private static StandardMaterial3D Glow(Color colour) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = colour,
        DisableReceiveShadows = true,
    };

    private Beam? NewBeam()
    {
        if (_assets.EffectMesh("helix1") is not { } left || _assets.EffectMesh("helix2") is not { } right)
            return null;
        var material = Glow(new Color(0.2f, 1f, 0.3f));
        var root = new Node3D { Name = "HealBeam" };
        var leftView = new MeshInstance3D { Mesh = left, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var rightView = new MeshInstance3D { Mesh = right, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        root.AddChild(leftView);
        root.AddChild(rightView);
        // MachPhysSuperCharger::lighting: the healer glows green while it heals.
        if (GraphicsSettings.Remake)
            root.AddChild(new OmniLight3D { LightColor = new Color(0.3f, 1, 0.4f), LightEnergy = 1.5f, OmniRange = 8 });
        AddChild(root);
        return new Beam { Root = root, Left = leftView, Right = rightView, Material = material };
    }

    /// <summary>
    /// The helices run 5 m along their x axis and are stretched to reach the patient (W4dObjectTrackerScalePlan).
    /// One turns a full circle every 3 s one way, the other the other way, two thirds of a turn apart; both fade
    /// from 0.8 to 0.2 and back every second (MachPhysHealHelix).
    /// </summary>
    private void UpdateBeams()
    {
        var alpha = 0.2f + 0.6f * Mathf.Abs(1 - 2 * (_time % 1));
        foreach (var beam in _beams.Values)
        {
            var reach = beam.To - beam.From;
            var length = reach.Length();
            if (length < 0.1f)
                continue;
            var along = reach / length;
            var up = Mathf.Abs(along.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
            var side = along.Cross(up).Normalized();
            var normal = side.Cross(along);
            beam.Root.GlobalTransform = new Transform3D(new Basis(along * length / 5, normal, along.Cross(normal)), beam.From);
            var turn = Mathf.Tau * _time / 3;
            beam.Left.Transform = new Transform3D(new Basis(Vector3.Right, turn + Mathf.Tau * 2 / 3), Vector3.Zero);
            beam.Right.Transform = new Transform3D(new Basis(Vector3.Right, -turn), Vector3.Zero);
            beam.Material.AlbedoColor = new Color(0.2f, 1f, 0.3f, alpha);
        }
    }

    /// <summary>
    /// The crosses round a machine being healed (MachPhysHealAura): one on each side, standing upright and moving
    /// outwards, and one lying above it, all as big as the smallest of three quarters of its height, its width and
    /// its length, growing by a third over each 1.2 s cycle and fading out in its last fifth of a second.
    /// </summary>
    private Aura? NewAura(ActorView patient)
    {
        if (_assets.EffectMesh("healcross") is not { } cross || _assets.EffectMesh("healaura") is not { } central)
            return null;
        var bounds = patient.Bounds;
        var size = Mathf.Min(bounds.Size.Y * 0.75f, Mathf.Min(bounds.Size.X, bounds.Size.Z));
        var clearance = size * 0.2f;
        var material = Glow(new Color(0.2f, 1f, 0.3f));
        var root = new Node3D { Name = "HealAura" };
        var centre = bounds.GetCenter();
        var radial = new List<(MeshInstance3D, Vector3, Vector3)>();
        // The crosses lie flat as exported; the side ones are stood up to face outwards.
        foreach (var (outward, turn) in new[]
                 {
                     (Vector3.Right, new Basis(Vector3.Back, Mathf.Pi / 2)), (Vector3.Left, new Basis(Vector3.Back, Mathf.Pi / 2)),
                     (Vector3.Back, new Basis(Vector3.Right, Mathf.Pi / 2)), (Vector3.Forward, new Basis(Vector3.Right, Mathf.Pi / 2)),
                 })
        {
            var view = new MeshInstance3D { Mesh = cross, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Basis = turn };
            root.AddChild(view);
            var edge = outward * (Mathf.Abs(outward.X) > 0 ? bounds.Size.X / 2 : bounds.Size.Z / 2);
            radial.Add((view, outward, new Vector3(centre.X, centre.Y, centre.Z) + edge));
        }
        var top = new MeshInstance3D { Mesh = central, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        root.AddChild(top);
        top.Position = new Vector3(centre.X, bounds.End.Y + clearance, centre.Z);
        patient.AddChild(root);
        return new Aura { Root = root, Radial = radial, Central = top, Material = material, Size = size, Clearance = clearance, Started = _time };
    }

    private void UpdateAuras()
    {
        const float cycle = 1.2f;
        foreach (var aura in _auras.Values)
        {
            var phase = (_time - aura.Started) % cycle / cycle;
            var scale = aura.Size * (1 + 0.3f * phase);
            foreach (var (cross, outward, start) in aura.Radial)
            {
                cross.Position = start + outward * (aura.Clearance + aura.Clearance * 3 * phase);
                cross.Scale = Vector3.One * scale;
            }
            aura.Central.Scale = Vector3.One * scale;
            var fade = Mathf.Clamp((cycle - phase * cycle) / 0.2f, 0, 1);
            aura.Material.AlbedoColor = new Color(0.2f, 1f, 0.3f, 0.8f * fade);
        }
    }

    // ---- one-off effects --------------------------------------------------------------------------

    /// <summary>Something that runs for a while and then frees itself, moved each frame.</summary>
    private sealed class Timed
    {
        public required Node3D Node { get; init; }
        public required float Duration { get; set; }
        public float Age;
        public required Action<float> Update { get; init; }
    }

    private readonly List<Timed> _timed = [];

    private void UpdateTimed(float delta)
    {
        foreach (var timed in _timed.ToList())
        {
            timed.Age += delta;
            if (timed.Age >= timed.Duration || !IsInstanceValid(timed.Node))
            {
                if (IsInstanceValid(timed.Node))
                    timed.Node.QueueFree();
                _timed.Remove(timed);
                continue;
            }
            timed.Update(timed.Age);
        }
    }

    /// <summary>
    /// The electric charger strikes (MachPhysElectroCharger): its lightning, 5 m long, stretched from the muzzle to
    /// the target and twice as wide, spins about itself at 2400° a second for half a second (EXTRAS) while its four
    /// pictures flicker in turn; two halos run up and down the victim for a second (MachPhysLightningHalo).
    /// </summary>
    public void Lightning(Vector3 from, Vector3 to, ActorView? victim)
    {
        if (_assets.EffectMesh("lightning") is not { } mesh)
            return;
        var length = from.DistanceTo(to);
        if (length < 0.5f)
            return;
        var along = (to - from) / length;
        var up = Mathf.Abs(along.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        var normal = along.Cross(up).Normalized().Cross(along);
        var root = new Node3D { Name = "Lightning" };
        AddChild(root);
        root.GlobalTransform = new Transform3D(new Basis(along * length / 5, normal * 2, along.Cross(normal) * 2), from);
        var bolt = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        root.AddChild(bolt);
        if (GraphicsSettings.Remake)
        {
            var light = new OmniLight3D { LightColor = new Color(0.32f, 0.86f, 1f), LightEnergy = 4, OmniRange = 12 };
            root.AddChild(light);
            light.GlobalPosition = (from + to) / 2;
        }
        var frames = Flicker(bolt);
        _timed.Add(new Timed
        {
            Node = root,
            Duration = 0.5f,
            Update = age =>
            {
                bolt.Transform = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(90 + 2400 * age)), Vector3.Zero);
                frames((int)(age * 20));
            },
        });
        if (victim is not null && _assets.EffectMesh("lightbox") is { } box)
            for (var sign = -1; sign <= 1; sign += 2)
                Halo(victim, box, sign);
    }

    /// <summary>One of the two halos: the victim's width and length, a third of its height, from its middle to its top or bottom and back every second.</summary>
    private void Halo(ActorView victim, Mesh box, int sign)
    {
        var bounds = victim.Bounds;
        var third = bounds.Size.Y / 3;
        var centre = bounds.GetCenter();
        var end = new Vector3(centre.X, sign > 0 ? bounds.End.Y - third / 2 : bounds.Position.Y + third / 2, centre.Z);
        var halo = new MeshInstance3D { Mesh = box, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "Halo" };
        victim.AddChild(halo);
        var frames = Flicker(halo);
        _timed.Add(new Timed
        {
            Node = halo,
            Duration = 1,
            Update = age =>
            {
                var there = age % 1 < 0.5f ? age % 1 / 0.5f : 2 - age % 1 / 0.5f;
                halo.Transform = new Transform3D(Basis.FromScale(new Vector3(bounds.Size.X, third, bounds.Size.Z)), centre.Lerp(end, there));
                frames((int)(age * 20) + (sign > 0 ? 2 : 0));
            },
        });
    }

    /// <summary>Shows one of a mesh's surfaces at a time (the lightning's four pictures), by frame number.</summary>
    private static Action<int> Flicker(MeshInstance3D view)
    {
        var count = view.Mesh.GetSurfaceCount();
        var hidden = new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0, 0, 0, 0) };
        return frame =>
        {
            for (var s = 0; s < count; s++)
                view.SetSurfaceOverrideMaterial(s, s == frame % count ? null : hidden);
        };
    }

    // ---- burning ----------------------------------------------------------------------------------

    private readonly Dictionary<ActorView, Timed> _burning = [];

    /// <summary>
    /// A flamethrower sets a machine alight for 15 seconds (MachPhysFlameThrower::applyVictimAnimation): flames
    /// flicker on it at three quarters of its height, through the original's sixteen pictures of fire. Hit again,
    /// it burns on from then.
    /// </summary>
    public void Burn(ActorView victim, float seconds = 15)
    {
        if (_burning.TryGetValue(victim, out var already) && IsInstanceValid(already.Node))
        {
            already.Duration = already.Age + seconds;
            return;
        }
        var pictures = Enumerable.Range(0, 16).Select(i => _assets.Texture($"models/texture4/fire/fl{i:D2}_bt.bmp")).OfType<Texture2D>().ToArray();
        if (pictures.Length == 0)
            return;
        var bounds = victim.Bounds;
        var size = Mathf.Clamp(Mathf.Min(bounds.Size.X, bounds.Size.Z) * 0.6f, 0.8f, 4);
        var root = new Node3D { Name = "Burning" };
        victim.AddChild(root);
        var flames = new List<(StandardMaterial3D Material, int Offset)>();
        var centre = bounds.GetCenter();
        Vector3[] spots = [new(0, 0, 0), new(bounds.Size.X * 0.25f, -0.1f * bounds.Size.Y, bounds.Size.Z * 0.2f), new(-bounds.Size.X * 0.25f, -0.15f * bounds.Size.Y, -bounds.Size.Z * 0.2f)];
        for (var i = 0; i < spots.Length; i++)
        {
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.FixedY,
                AlbedoTexture = pictures[0],
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            var flame = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(size, size * 1.6f), CenterOffset = new Vector3(0, size * 0.8f, 0) },
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            root.AddChild(flame);
            flame.Position = new Vector3(centre.X, bounds.Position.Y + bounds.Size.Y * 0.75f, centre.Z) + spots[i];
            flames.Add((material, i * 5));
        }
        OmniLight3D? light = null;
        if (GraphicsSettings.Remake)
        {
            light = new OmniLight3D { LightColor = new Color(1, 0.55f, 0.2f), LightEnergy = 2, OmniRange = size * 5 };
            root.AddChild(light);
            light.Position = new Vector3(centre.X, bounds.End.Y + 0.5f, centre.Z);
        }
        var timed = new Timed
        {
            Node = root,
            Duration = seconds,
            Update = age =>
            {
                foreach (var (material, offset) in flames)
                    material.AlbedoTexture = pictures[((int)(age * 15) + offset) % pictures.Length];
                if (light is not null)
                    light.LightEnergy = 1.6f + 0.6f * Mathf.Sin(age * 23) * Mathf.Sin(age * 7.3f);
            },
        };
        _timed.Add(timed);
        _burning[victim] = timed;
    }

    // ---- loading ----------------------------------------------------------------------------------

    /// <summary>
    /// A transporter loads or unloads ore (MachPhysResourceLoading, at the original's 22 frames a second): two
    /// rings round it, 1.05 times its size, grow to twice that over a second and fade out after their first two
    /// frames; a disc of light shrinks from its size to half over 12 frames; a light shines for 10.
    /// </summary>
    public void Loading(ActorView carrier)
    {
        if (_assets.EffectMesh("loadunder") is not { } under || _assets.EffectMesh("loadover") is not { } over
            || _assets.EffectMesh("loadlight") is not { } disc)
            return;
        const float frame = 1 / 22f;
        var bounds = carrier.Bounds;
        var a = 1.05f * Mathf.Max(bounds.Size.X, bounds.Size.Z) / (3.563743f * 2);
        var root = new Node3D { Name = "Loading", Position = new Vector3(bounds.GetCenter().X, bounds.Position.Y, bounds.GetCenter().Z) };
        carrier.AddChild(root);
        var rings = new[] { Fading(root, under), Fading(root, over) };
        var light = Fading(root, disc);
        OmniLight3D? lamp = null;
        if (GraphicsSettings.Remake)
        {
            lamp = new OmniLight3D { LightColor = new Color(0.9f, 1f, 0.9f), LightEnergy = 3, OmniRange = 20, Position = Vector3.Up };
            root.AddChild(lamp);
        }
        _timed.Add(new Timed
        {
            Node = root,
            Duration = 22 * frame,
            Update = age =>
            {
                var t = age / (22 * frame);
                var fade = age < 2 * frame ? 1 : 1 - (age - 2 * frame) / (20 * frame);
                foreach (var (view, materials) in rings)
                {
                    view.Scale = Vector3.One * Mathf.Lerp(1, 2 * a, t);
                    SetAlpha(materials, fade);
                }
                var lightT = Mathf.Clamp(age / (12 * frame), 0, 1);
                light.View.Scale = Vector3.One * Mathf.Lerp(a, 0.5f, lightT);
                light.View.Visible = lightT < 1;
                SetAlpha(light.Materials, age < 2 * frame ? 1 : Mathf.Clamp(1 - (age - 2 * frame) / (10 * frame), 0, 1));
                if (lamp is not null)
                    lamp.Visible = age < 10 * frame;
            },
        });
    }

    /// <summary>A mesh with its own copies of its materials, so it can fade without fading the others.</summary>
    private static (MeshInstance3D View, List<StandardMaterial3D> Materials) Fading(Node3D parent, Mesh mesh)
    {
        var view = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(view);
        var materials = new List<StandardMaterial3D>();
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not StandardMaterial3D shared)
                continue;
            var own = (StandardMaterial3D)shared.Duplicate();
            own.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            own.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            view.SetSurfaceOverrideMaterial(s, own);
            materials.Add(own);
        }
        return (view, materials);
    }

    private static void SetAlpha(List<StandardMaterial3D> materials, float alpha)
    {
        foreach (var material in materials)
            material.AlbedoColor = material.AlbedoColor with { A = Mathf.Clamp(alpha, 0, 1) };
    }
}
