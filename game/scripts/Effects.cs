using Godot;

namespace Machines.Game;

/// <summary>
/// The remake's battle effects, in place of the plain beams and flashes of the classic look: muzzle flashes,
/// glowing bolts that fly at the weapon's speed, sparks where they hit, explosions of fire, smoke and debris
/// with a flash of light, and scorch marks that stay on the ground for a while. Everything is made in code from
/// a few generated textures, and each effect frees itself.
/// </summary>
public partial class Effects : Node3D
{
    private const int MaxLights = 10, MaxScorches = 80;

    private readonly Queue<Decal> _scorches = new();
    private int _lights;

    // ---- shots ----------------------------------------------------------------------------------

    /// <summary>A shot: flash at the muzzle, a bolt flying to the target at <paramref name="speed"/> m/s, sparks on arrival.</summary>
    public void Shot(Vector3 from, Vector3 to, string weapon, float speed)
    {
        var colour = WeaponColour(weapon);
        Glow(from, colour * 4, 1.2f, 0.06f);
        Light(from, colour, 2.5f, 8, 0.08f);
        if (Smokes(weapon))
            FiringSmoke(from);

        var distance = from.DistanceTo(to);
        if (distance < 0.5f)
            return;
        var flight = Mathf.Clamp(distance / Mathf.Max(speed, 1), 0.04f, 0.6f);
        var bolt = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.18f, 0.18f, Mathf.Min(3f, distance * 0.3f)) },
            MaterialOverride = Emissive(colour, 5),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(bolt);
        bolt.GlobalPosition = from;
        bolt.LookAt(to, Vector3.Up);
        var tween = bolt.CreateTween();
        tween.TweenProperty(bolt, "global_position", to, flight);
        tween.TweenCallback(Callable.From(() =>
        {
            Sparks(to, colour, 10);
            bolt.QueueFree();
        }));
    }

    // ---- explosions -----------------------------------------------------------------------------

    /// <summary>Something blows up: a fireball, rolling smoke, flying debris, a flash and a scorch mark, scaled by its size.</summary>
    public void Explosion(Vector3 at, float size)
    {
        var scale = Mathf.Clamp(size / 3, 1f, 4f);
        Light(at + Vector3.Up * 2 * scale, new Color(1, 0.6f, 0.25f), 6, 18 * scale, 0.5f);

        // Fireball: bright, growing, fading to dark red.
        Burst(at + Vector3.Up * scale, amount: (int)(24 * scale), lifetime: 0.9f, texture: Soft,
            new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                EmissionSphereRadius = 1.2f * scale,
                Direction = Vector3.Up,
                Spread = 180,
                InitialVelocityMin = 2 * scale,
                InitialVelocityMax = 6 * scale,
                Gravity = new Vector3(0, 2, 0),
                DampingMin = 4,
                DampingMax = 6,
                ScaleMin = 5f * scale,
                ScaleMax = 8.5f * scale,
                ScaleCurve = Grow(0.45f),
                ColorRamp = Ramp(new Color(3.2f, 1.8f, 0.6f), new Color(2.2f, 0.7f, 0.12f, 0.9f), new Color(0.5f, 0.12f, 0.04f, 0)),
            }, additive: true);

        // Smoke: dark, slow, rising and spreading.
        Burst(at + Vector3.Up * scale, amount: (int)(18 * scale), lifetime: 4f, texture: Soft,
            new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                EmissionSphereRadius = 1.5f * scale,
                Direction = Vector3.Up,
                Spread = 35,
                InitialVelocityMin = 1.5f,
                InitialVelocityMax = 4f,
                Gravity = new Vector3(0.4f, 1.2f, 0),
                DampingMin = 0.5f,
                DampingMax = 1,
                ScaleMin = 10f * scale,
                ScaleMax = 15f * scale,
                ScaleCurve = Grow(0.25f),
                ColorRamp = Ramp(new Color(0.15f, 0.13f, 0.12f, 0.6f), new Color(0.13f, 0.12f, 0.11f, 0.85f), new Color(0.32f, 0.3f, 0.29f, 0)),
            }, additive: false);

        // Debris: small dark chunks thrown up and falling.
        Burst(at + Vector3.Up * scale, amount: (int)(16 * scale), lifetime: 1.6f, texture: Square,
            new ParticleProcessMaterial
            {
                Direction = Vector3.Up,
                Spread = 60,
                InitialVelocityMin = 6 * Mathf.Sqrt(scale),
                InitialVelocityMax = 14 * Mathf.Sqrt(scale),
                Gravity = new Vector3(0, -20, 0),
                ScaleMin = 0.3f,
                ScaleMax = 0.7f,
                AngularVelocityMin = -400,
                AngularVelocityMax = 400,
                Color = new Color(0.12f, 0.1f, 0.09f),
            }, additive: false);

        Scorch(at, 3.5f * scale);
    }

    /// <summary>
    /// A missile strikes (MachPhysTrailedProjectile's explosion): three fireballs 2 to 4 m across within a metre of
    /// the point, a little smoke, a flash of light and a small scorch.
    /// </summary>
    public void Impact(Vector3 at)
    {
        Light(at + Vector3.Up, new Color(1, 0.6f, 0.25f), 4, 10, 0.3f);
        Burst(at, amount: 3, lifetime: 0.6f, texture: Soft,
            new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                EmissionSphereRadius = 1,
                Gravity = new Vector3(0, 1.5f, 0),
                ScaleMin = 2,
                ScaleMax = 4,
                ScaleCurve = Grow(0.5f),
                ColorRamp = Ramp(new Color(3.2f, 1.8f, 0.6f), new Color(2.2f, 0.7f, 0.12f, 0.9f), new Color(0.5f, 0.12f, 0.04f, 0)),
            }, additive: true);
        Burst(at + Vector3.Up * 0.5f, amount: 4, lifetime: 2f, texture: Soft,
            new ParticleProcessMaterial
            {
                Direction = Vector3.Up,
                Spread = 30,
                InitialVelocityMin = 1,
                InitialVelocityMax = 2.5f,
                Gravity = new Vector3(0.3f, 0.8f, 0),
                ScaleMin = 3,
                ScaleMax = 5,
                ScaleCurve = Grow(0.3f),
                ColorRamp = Ramp(new Color(0.15f, 0.13f, 0.12f, 0.5f), new Color(0.2f, 0.18f, 0.17f, 0.6f), new Color(0.32f, 0.3f, 0.29f, 0)),
            }, additive: false);
        Scorch(at, 2.5f);
    }

    /// <summary>Bolters, the autocannon, flamethrowers and electric chargers puff out smoke as they fire (MachPhysSmokeCloud).</summary>
    private static bool Smokes(string weapon) =>
        weapon.Contains("BOLTER") || weapon == "AUTO_CANNON" || weapon.StartsWith("FLAME_THROWER") || weapon.StartsWith("ELECTRIC");

    /// <summary>Three grey wisps rising from the muzzle and spreading as they thin out.</summary>
    private void FiringSmoke(Vector3 at) =>
        Burst(at, amount: 3, lifetime: 1.2f, texture: Soft, new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 25,
            InitialVelocityMin = 1,
            InitialVelocityMax = 2,
            Gravity = Vector3.Zero,
            DampingMin = 0.5f,
            DampingMax = 1,
            ScaleMin = 0.8f,
            ScaleMax = 1.3f,
            ScaleCurve = Grow(0.4f),
            ColorRamp = Ramp(new Color(0.55f, 0.55f, 0.55f, 0), new Color(0.5f, 0.5f, 0.5f, 0.45f), new Color(0.55f, 0.55f, 0.55f, 0)),
        }, additive: false);

    /// <summary>An area weapon goes off: a big flash and a scorch the size of its reach.</summary>
    public void Blast(Vector3 at, float radius, Color colour)
    {
        Light(at + Vector3.Up * 6, colour, 10, radius * 2.5f, 1.2f);
        Glow(at + Vector3.Up * 3, colour * 6, radius * 0.6f, 0.4f);
        Scorch(at, radius * 1.4f);
    }

    // ---- ground ---------------------------------------------------------------------------------

    /// <summary>A dark burn on the ground that fades away after a minute.</summary>
    public void Scorch(Vector3 at, float size)
    {
        var decal = new Decal
        {
            TextureAlbedo = Burn,
            Size = new Vector3(size, 12, size),
            Modulate = new Color(1, 1, 1, 0.85f),
            UpperFade = 0.2f,
            LowerFade = 0.2f,
            CullMask = TerrainBuilder.GroundLayer,
        };
        AddChild(decal);
        decal.GlobalPosition = at;
        decal.RotateY((float)GD.RandRange(0, Mathf.Tau));
        _scorches.Enqueue(decal);
        if (_scorches.Count > MaxScorches && _scorches.Dequeue() is { } oldest && IsInstanceValid(oldest))
            oldest.QueueFree();
        var fade = decal.CreateTween();
        fade.TweenInterval(45);
        fade.TweenProperty(decal, "modulate:a", 0f, 15);
        fade.TweenCallback(Callable.From(decal.QueueFree));
    }

    private const int MaxTreadMarks = 300;
    private readonly Queue<Decal> _treadMarks = new();

    /// <summary>
    /// A stretch of the marks a tracked or wheeled machine leaves on the ground: two ribbed stripes as far apart as
    /// it is wide, along its heading, fading after half a minute. Only the ground takes them.
    /// </summary>
    public void TreadMark(Vector3 at, float yaw, float width, float length)
    {
        var decal = new Decal
        {
            TextureAlbedo = Treads,
            Size = new Vector3(length, 3, width),
            Modulate = new Color(1, 1, 1, 0.55f),
            UpperFade = 0.3f,
            LowerFade = 0.3f,
            CullMask = TerrainBuilder.GroundLayer,
        };
        AddChild(decal);
        decal.GlobalPosition = at;
        decal.Rotation = new Vector3(0, yaw, 0);
        _treadMarks.Enqueue(decal);
        if (_treadMarks.Count > MaxTreadMarks && _treadMarks.Dequeue() is { } oldest && IsInstanceValid(oldest))
            oldest.QueueFree();
        var fade = decal.CreateTween();
        fade.TweenInterval(20);
        fade.TweenProperty(decal, "modulate:a", 0f, 10);
        fade.TweenCallback(Callable.From(decal.QueueFree));
    }

    /// <summary>Two dark ribbed stripes along the picture's length, clear between and beside them.</summary>
    private static readonly Texture2D Treads = MakeTreads();

    private static Texture2D MakeTreads()
    {
        const int length = 64, width = 32;
        var image = Image.CreateEmpty(length, width, false, Image.Format.Rgba8);
        image.Fill(new Color(0.1f, 0.08f, 0.06f, 0));
        foreach (var (from, to) in new[] { (1, 9), (23, 31) })
            for (var y = from; y < to; y++)
                for (var x = 0; x < length; x++)
                {
                    // Ribs across the stripe, a little darker at its middle.
                    var rib = x % 8 < 5 ? 0.8f : 0.45f;
                    var middle = 1 - Mathf.Abs((y - (from + to) / 2f) / ((to - from) / 2f)) * 0.4f;
                    image.SetPixel(x, y, new Color(0.1f, 0.08f, 0.06f, rib * middle));
                }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// A particle picture from game/assets/particles (Kenney's Particle Pack, CC0), or the soft spot when it is missing.
    /// Imported pictures load as resources; ones not yet imported (a fresh checkout run without the editor) straight from
    /// their file.
    /// </summary>
    public static Texture2D Particle(string name)
    {
        lock (Particles)
        {
            if (Particles.TryGetValue(name, out var cached))
                return cached;
            // "flipbooks/billow" names one of the animated sheets rendered in Blender (tools/blender/flipbooks.py).
            var path = $"res://assets/particles/{name}.png";
            Texture2D texture = Soft;
            if (ResourceLoader.Exists(path))
                texture = GD.Load<Texture2D>(path);
            else if (Image.LoadFromFile(ProjectSettings.GlobalizePath(path)) is { } image && !image.IsEmpty())
            {
                image.GenerateMipmaps();
                texture = ImageTexture.CreateFromImage(image);
            }
            return Particles[name] = texture;
        }
    }

    private static readonly Dictionary<string, Texture2D> Particles = [];

    /// <summary>The colour of the dust machines kick up: the planet's ground (sand by default; see Weather).</summary>
    public static Color DustTint { get; set; } = new(0.72f, 0.62f, 0.48f);

    /// <summary>A puff of dust kicked up behind a moving machine.</summary>
    public static GpuParticles3D DustTrail()
    {
        var dust = new GpuParticles3D
        {
            Amount = 10,
            Lifetime = 1.4f,
            Emitting = false,
            LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Billowing puffs in the remake look, the soft spot in the classic one.
            DrawPass1 = Billboard(GraphicsSettings.Remake ? Particle("smoke_04") : Soft, additive: false),
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(1, 0.1f, 1),
                Direction = Vector3.Up,
                Spread = 50,
                InitialVelocityMin = 0.3f,
                InitialVelocityMax = 1.2f,
                Gravity = new Vector3(0, 0.3f, 0),
                ScaleMin = 2.2f,
                ScaleMax = 4f,
                ScaleCurve = Grow(0.35f),
                AngleMin = -180,
                AngleMax = 180,
                ColorRamp = Ramp(DustTint with { A = 0 }, DustTint with { A = 0.35f }, DustTint.Darkened(0.05f) with { A = 0 }),
            },
        };
        return dust;
    }

    /// <summary>
    /// A chimney's smoke (MachPhysSmokePlume) while it is <see cref="GpuParticles3D.Emitting"/>: puffs that rise
    /// <paramref name="height"/> metres over <paramref name="rise"/> seconds with a little drift, swelling to
    /// about <paramref name="size"/> halfway and thinning out at the top. <paramref name="wisps"/>, the original's
    /// puffs per plume, sets how thick it is.
    /// </summary>
    public static GpuParticles3D Plume(int wisps, float rise, float height, float size, Color colour)
    {
        var swell = new Curve();
        swell.AddPoint(new Vector2(0, 0.2f));
        swell.AddPoint(new Vector2(0.5f, 1));
        swell.AddPoint(new Vector2(1, 0.7f));
        return new GpuParticles3D
        {
            Amount = Math.Max(1, wisps) * 6,
            Lifetime = rise,
            Randomness = 0.3f,
            Emitting = false,
            LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-size * 2, -size, -size * 2), new Vector3(size * 4, height + size * 2, size * 4)),
            DrawPass1 = Billboard(Soft, additive: false),
            ProcessMaterial = new ParticleProcessMaterial
            {
                Direction = Vector3.Up,
                // The original's path wanders a tenth of the height either way.
                Spread = 6,
                InitialVelocityMin = height / rise * 0.9f,
                InitialVelocityMax = height / rise * 1.1f,
                Gravity = Vector3.Zero,
                ScaleMin = size * 0.9f,
                ScaleMax = size * 1.3f,
                ScaleCurve = new CurveTexture { Curve = swell },
                AngleMin = 0,
                AngleMax = 360,
                ColorRamp = Ramp(new Color(colour, colour.A * 0.6f), colour, new Color(colour, 0)),
            },
        };
    }

    /// <summary>
    /// The vapour a missile leaves behind it (MachPhysVapourTrail: sixteen puffs of about 1.5 m), white turning
    /// grey as the puffs swell and thin out; set it emitting at the missile's tail.
    /// </summary>
    public static GpuParticles3D VapourTrail() => new()
    {
        Name = "Trail",
        Amount = 16,
        Lifetime = 1.1f,
        LocalCoords = false,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        DrawPass1 = Billboard(Soft, additive: false),
        ProcessMaterial = new ParticleProcessMaterial
        {
            Spread = 180,
            InitialVelocityMin = 0,
            InitialVelocityMax = 0.4f,
            Gravity = new Vector3(0, 0.3f, 0),
            ScaleMin = 1.2f,
            ScaleMax = 1.8f,
            ScaleCurve = Grow(0.4f),
            ColorRamp = Ramp(new Color(0.9f, 0.9f, 0.88f, 0.6f), new Color(0.75f, 0.75f, 0.74f, 0.4f), new Color(0.6f, 0.6f, 0.6f, 0)),
        },
    };

    /// <summary>
    /// Flames licking up from a badly damaged machine or building (MachPhysDoublesidedFlame, MachPhysSTFFlame)
    /// over <paramref name="width"/> metres, about <paramref name="height"/> metres high, while it is emitting.
    /// </summary>
    public static GpuParticles3D DamageFire(float width, float height) => new()
    {
        Name = "DamageFire",
        Amount = 18,
        Lifetime = 0.7f,
        LocalCoords = false,
        Emitting = false,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        DrawPass1 = Billboard(Soft, additive: true),
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(width / 2, 0.2f, width / 2),
            Direction = Vector3.Up,
            Spread = 10,
            InitialVelocityMin = height / 0.7f * 0.8f,
            InitialVelocityMax = height / 0.7f * 1.2f,
            Gravity = Vector3.Zero,
            ScaleMin = width * 0.7f,
            ScaleMax = width * 1.1f,
            ScaleCurve = Grow(0.6f),
            ColorRamp = Ramp(new Color(2.6f, 1.4f, 0.4f), new Color(2f, 0.6f, 0.1f, 0.8f), new Color(0.4f, 0.08f, 0.02f, 0)),
        },
    };

    // ---- building blocks ------------------------------------------------------------------------

    private void Sparks(Vector3 at, Color colour, int amount) =>
        Burst(at, amount, 0.35f, Soft, new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 180,
            InitialVelocityMin = 4,
            InitialVelocityMax = 9,
            Gravity = new Vector3(0, -15, 0),
            ScaleMin = 0.15f,
            ScaleMax = 0.35f,
            ColorRamp = Ramp(colour * 5, colour * 3, new Color(colour, 0)),
        }, additive: true);

    private void Burst(Vector3 at, int amount, float lifetime, Texture2D texture, ParticleProcessMaterial process, bool additive)
    {
        var particles = new GpuParticles3D
        {
            Amount = Math.Max(1, amount),
            Lifetime = lifetime,
            OneShot = true,
            Explosiveness = 0.9f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ProcessMaterial = process,
            DrawPass1 = Billboard(texture, additive),
        };
        AddChild(particles);
        particles.GlobalPosition = at;
        particles.Emitting = true;
        GetTree().CreateTimer(lifetime + 0.5).Timeout += particles.QueueFree;
    }

    /// <summary>A brief glowing sprite (muzzle flash, blast core).</summary>
    private void Glow(Vector3 at, Color colour, float size, float duration)
    {
        var sprite = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(size, size) },
            MaterialOverride = Sprite(Soft, colour, additive: true),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(sprite);
        sprite.GlobalPosition = at;
        var tween = sprite.CreateTween();
        tween.TweenProperty(sprite, "scale", Vector3.One * 1.6f, duration);
        tween.TweenCallback(Callable.From(sprite.QueueFree));
    }

    /// <summary>A short flash of light; only a few at a time, as lights are the costly part.</summary>
    private void Light(Vector3 at, Color colour, float energy, float range, float duration)
    {
        if (_lights >= MaxLights)
            return;
        _lights++;
        var light = new OmniLight3D { LightColor = colour, LightEnergy = energy, OmniRange = range, ShadowEnabled = false };
        AddChild(light);
        light.GlobalPosition = at;
        var tween = light.CreateTween();
        tween.TweenProperty(light, "light_energy", 0f, duration);
        tween.TweenCallback(Callable.From(() =>
        {
            _lights--;
            light.QueueFree();
        }));
    }

    private static Color WeaponColour(string weapon) =>
        weapon.Contains("PLASMA") ? new Color(0.35f, 1f, 0.45f)
        : weapon.Contains("FLAME") ? new Color(1f, 0.5f, 0.12f)
        : weapon.Contains("ELECTRIC") || weapon.Contains("PULSE") ? new Color(0.45f, 0.7f, 1f)
        : weapon.Contains("LASER") || weapon.Contains("STING") ? new Color(1f, 0.3f, 0.3f)
        : new Color(1f, 0.85f, 0.4f);

    private static StandardMaterial3D Emissive(Color colour, float energy) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = colour * energy,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

    private static StandardMaterial3D Sprite(Texture2D texture, Color colour, bool additive) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoTexture = texture,
        AlbedoColor = colour,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
    };

    internal static QuadMesh Billboard(Texture2D texture, bool additive)
    {
        var material = Sprite(texture, Colors.White, additive);
        material.BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles;
        material.BillboardKeepScale = true;
        material.VertexColorUseAsAlbedo = true;
        return new QuadMesh { Material = material };
    }

    /// <summary>A size that grows from <paramref name="start"/> of the particle's scale to all of it.</summary>
    internal static CurveTexture Grow(float start)
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0, start));
        curve.AddPoint(new Vector2(1, 1));
        return new CurveTexture { Curve = curve };
    }

    internal static GradientTexture1D Ramp(Color start, Color middle, Color end) => new()
    {
        Gradient = new Gradient { Offsets = [0, 0.3f, 1], Colors = [start, middle, end] },
        UseHdr = true,
    };

    // ---- generated textures ---------------------------------------------------------------------

    /// <summary>A soft round spot, white in the middle fading out at the edge.</summary>
    internal static readonly Texture2D Soft = Generate(64, (x, y) =>
    {
        var d = Mathf.Clamp(1 - new Vector2(x, y).Length(), 0, 1);
        return new Color(1, 1, 1, d * d);
    });

    private static readonly Texture2D Square = Generate(8, (_, _) => Colors.White);

    /// <summary>A thin bright ring with soft edges, for selection marks on the ground.</summary>
    public static readonly Texture2D Ring = Generate(128, (x, y) => new Color(1, 1, 1, RingShare(x, y)));

    /// <summary>The ring's glow: light where the ring is, black around it (emission ignores alpha).</summary>
    public static readonly Texture2D RingGlow = Generate(128, (x, y) =>
    {
        var share = RingShare(x, y);
        return new Color(share, share, share);
    });

    private static float RingShare(float x, float y) =>
        Mathf.Clamp(1.4f - Mathf.Abs(new Vector2(x, y).Length() - 0.85f) / 0.1f, 0, 1);

    /// <summary>A burn mark: dark in the middle, ragged at the edge.</summary>
    private static readonly Texture2D Burn = Generate(128, (x, y) =>
    {
        var angle = Mathf.Atan2(y, x);
        var ragged = 0.78f + 0.06f * Mathf.Sin(angle * 5) + 0.04f * Mathf.Sin(angle * 11 + 1.3f) + 0.03f * Mathf.Sin(angle * 23 + 0.4f);
        var d = new Vector2(x, y).Length() / ragged;
        var alpha = Mathf.Clamp(1.2f - d * 1.2f, 0, 1);
        return new Color(0.05f, 0.04f, 0.035f, alpha * alpha * 0.9f);
    });

    internal static Texture2D Generate(int size, Func<float, float, Color> pixel)
    {
        var image = Image.CreateEmpty(size, size, true, Image.Format.Rgba8);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                image.SetPixel(x, y, pixel(x / (size - 1f) * 2 - 1, y / (size - 1f) * 2 - 1));
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
