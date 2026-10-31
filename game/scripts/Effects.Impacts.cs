using Godot;

namespace Machines.Game;

/// <summary>
/// Where rounds land and things blow up: in the remake's effects sparks streaking off metal, dust thrown up from the
/// ground, plasma and pulse splats, missiles bursting in balls of fire, and explosions of fire, smoke, embers and a
/// shock ring. The fire and smoke are flipbooks rendered in Blender (tools/blender/flipbooks.py).
/// </summary>
public partial class Effects
{
    /// <summary>
    /// A missile strikes (MachPhysTrailedProjectile's explosion), or in the remake's effects a bolt that struck the
    /// ground (<see cref="Shot"/> sent it there just before): three fireballs 2 to 4 m across within a metre of the
    /// point, a little smoke, a flash of light and a small scorch.
    /// </summary>
    public void Impact(Vector3 at)
    {
        if (!GraphicsSettings.Effects)
        {
            ClassicImpact(at);
            return;
        }
        // The bolt's tracer bursts in dust when it gets there.
        if (_lastRound is { } last && last.To.IsEqualApprox(at))
        {
            last.Round.Ground = true;
            _lastRound = null;
            return;
        }
        MissileBurst(at);
    }

    /// <summary>
    /// A missile flying along <paramref name="heading"/> ends its flight at <paramref name="at"/> (in the remake's
    /// effects): it bursts just in front of the hull of what it struck, or there.
    /// </summary>
    public void MissileHit(Vector3 at, Vector3 heading) =>
        MissileBurst((Hull(at - heading.Normalized() * 15, at) ?? at) - heading.Normalized() * 0.8f);

    /// <summary>
    /// MachPhysTrailedProjectile's explosion: three balls of fire 2.5 to 4.5 m across within a metre of the point, a
    /// flash, smoke, sparks and a small scorch.
    /// </summary>
    private void MissileBurst(Vector3 at)
    {
        Light(at + Vector3.Up, new Color(1, 0.6f, 0.25f), 4, 12, 0.35f);
        FlashOrb(at + Vector3.Up * 0.5f, new Color(1, 0.7f, 0.35f), 3, 0.12f);
        Burst(at + Vector3.Up * 0.5f, amount: 3, lifetime: 0.9f, Flipbook("fireball"), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 1,
            Direction = Vector3.Up,
            Spread = 60,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 1.5f,
            Gravity = new Vector3(0, 1.5f, 0),
            ScaleMin = 2.5f,
            ScaleMax = 4.5f,
            ScaleCurve = Grow(0.6f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Ramp(new Color(2.4f, 2.2f, 2f, 1), new Color(1.5f, 1.4f, 1.3f, 1), new Color(1, 1, 1, 0)),
        }, explosiveness: 0.8f);
        Smoke(at + Vector3.Up, amount: 3, size: 4, lifetime: 2.5f);
        Sparks(at, new Color(1, 0.6f, 0.25f), 12, Vector3.Up, spread: 70, speed: 9, lifetime: 0.7f, size: 0.5f);
        Scorch(at, 2.5f);
    }

    /// <summary>The classic look's impact: three soft glows, a little smoke and a scorch.</summary>
    private void ClassicImpact(Vector3 at)
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

    /// <summary>
    /// Something blows up (only in the remake's effects): balls of fire swelling and cooling into smoke, a flash, a
    /// shock ring running over the ground, embers and debris thrown up, dark smoke rolling up, and a scorch mark,
    /// all scaled by its size.
    /// </summary>
    public void Explosion(Vector3 at, float size)
    {
        var scale = Mathf.Clamp(size / 3, 1f, 4f);
        var centre = at + Vector3.Up * scale;
        Light(at + Vector3.Up * 2 * scale, new Color(1, 0.6f, 0.25f), 7, 20 * scale, 0.7f);
        FlashOrb(centre, new Color(1, 0.75f, 0.45f), 3.5f * scale, 0.15f);
        ShockRing(at + Vector3.Up * 0.4f, 7 * scale, new Color(1, 0.65f, 0.3f), 0.35f);
        Fireballs(centre, scale, (int)(7 * scale));

        Smoke(centre, amount: (int)(5 * scale), size: 9 * scale, lifetime: 4.5f);
        Sparks(centre, new Color(1, 0.55f, 0.2f), (int)(24 * scale), Vector3.Up, spread: 80, speed: 12 * Mathf.Sqrt(scale),
            lifetime: 1.4f, size: 0.7f);

        // Debris: shards of plate thrown up and falling, turning with their flight.
        Burst(centre, amount: (int)(14 * scale), lifetime: 1.6f, Shard.Value, new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 60,
            InitialVelocityMin = 6 * Mathf.Sqrt(scale),
            InitialVelocityMax = 14 * Mathf.Sqrt(scale),
            Gravity = new Vector3(0, -20, 0),
            ScaleMin = 0.3f,
            ScaleMax = 0.8f,
            ParticleFlagAlignY = true,
        });

        Scorch(at, 3.5f * scale);
    }

    /// <summary>Balls of fire swelling and cooling into smoke around <paramref name="centre"/>, about <paramref name="scale"/> times 6 m across.</summary>
    private void Fireballs(Vector3 centre, float scale, int amount) =>
        Burst(centre, amount, lifetime: 1.3f, Flipbook("fireball"), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 1.1f * scale,
            Direction = Vector3.Up,
            Spread = 180,
            InitialVelocityMin = 1.5f * scale,
            InitialVelocityMax = 4.5f * scale,
            Gravity = new Vector3(0, 1.5f, 0),
            DampingMin = 3,
            DampingMax = 5,
            ScaleMin = 4.5f * scale,
            ScaleMax = 7f * scale,
            ScaleCurve = Grow(0.55f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Ramp(new Color(2.6f, 2.4f, 2.2f, 1), new Color(1.6f, 1.5f, 1.4f, 1), new Color(1, 1, 1, 0)),
        }, explosiveness: 0.85f);

    /// <summary>Dark smoke rolling up (the billowing flipbook, darkened), <paramref name="size"/> metres across.</summary>
    private void Smoke(Vector3 at, int amount, float size, float lifetime) =>
        Burst(at, amount, lifetime, Flipbook("billow"), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = size * 0.15f,
            Direction = Vector3.Up,
            Spread = 35,
            InitialVelocityMin = 1f,
            InitialVelocityMax = 3f,
            Gravity = new Vector3(0.4f, 1.2f, 0),
            DampingMin = 0.5f,
            DampingMax = 1,
            ScaleMin = size * 0.8f,
            ScaleMax = size * 1.2f,
            ScaleCurve = Grow(0.3f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Ramp(new Color(0.16f, 0.14f, 0.13f, 0), new Color(0.15f, 0.14f, 0.13f, 0.8f), new Color(0.3f, 0.29f, 0.28f, 0)),
        }, explosiveness: 0.6f);

    /// <summary>A bolt glances off metal: a spray of sparks thrown back the way it came, and a white flash.</summary>
    private void MetalHit(Vector3 at, Vector3 direction)
    {
        FlashOrb(at, new Color(1, 0.85f, 0.6f), 0.9f, 0.06f);
        Sparks(at, new Color(1, 0.75f, 0.4f), 10, -direction + Vector3.Up * 0.4f, spread: 60, speed: 10, lifetime: 0.35f, size: 0.45f);
    }

    /// <summary>A bolt strikes the ground: a puff of dust in its colour, grit thrown up, a few sparks and a small burn.</summary>
    private void GroundHit(Vector3 at, Vector3 direction)
    {
        var dust = DustTint;
        Burst(at, amount: 4, lifetime: 1.3f, texture: Particle("smoke_04"), new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 40,
            InitialVelocityMin = 1,
            InitialVelocityMax = 2.5f,
            Gravity = new Vector3(0, -0.5f, 0),
            DampingMin = 1.5f,
            DampingMax = 2.5f,
            ScaleMin = 1.2f,
            ScaleMax = 2.2f,
            ScaleCurve = Grow(0.35f),
            AngleMin = -180,
            AngleMax = 180,
            ColorRamp = Ramp(dust with { A = 0.7f }, dust with { A = 0.5f }, dust.Darkened(0.1f) with { A = 0 }),
        }, additive: false);
        Burst(at, amount: 6, lifetime: 0.8f, texture: Square, new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 35,
            InitialVelocityMin = 4,
            InitialVelocityMax = 7,
            Gravity = new Vector3(0, -18, 0),
            ScaleMin = 0.08f,
            ScaleMax = 0.18f,
            Color = dust.Darkened(0.5f),
        }, additive: false);
        Sparks(at, new Color(1, 0.75f, 0.4f), 3, -direction + Vector3.Up, spread: 50, speed: 7, lifetime: 0.3f, size: 0.35f);
        Scorch(at, 1.2f);
    }

    /// <summary>
    /// A plasma or pulse bolt (or the wasp's light sting) flying along <paramref name="heading"/> bursts where it
    /// struck, just in front of the hull there (plasplat, pulsplat): a flash in its colour doubling in size over a
    /// third of a second, a ring running out, sparks and a flash of light; the cannons' larger.
    /// </summary>
    public void EnergySplat(Vector3 at, string weapon, Vector3 heading)
    {
        heading = heading.Normalized();
        at = (Hull(at - heading * 15, at) ?? at) - heading * 0.5f;
        var colour = weapon.Contains("PLASMA") ? RoundLooks.Plasma : weapon.Contains("PULSE") ? RoundLooks.Pulse : RoundLooks.Sting;
        var size = weapon.Contains("CANNON") ? 2.6f : 1.6f;
        FlashOrb(at, colour, size, 0.33f, grow: 2);
        var ring = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = Sprite(Ring, colour * 3, additive: true),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = Vector3.One * size,
        };
        AddChild(ring);
        ring.GlobalPosition = at;
        var spread = ring.CreateTween().SetParallel();
        spread.TweenProperty(ring, "scale", Vector3.One * size * 2.4f, 0.3f);
        spread.TweenProperty(ring.MaterialOverride, "albedo_color:a", 0f, 0.3f);
        spread.Chain().TweenCallback(Callable.From(ring.QueueFree));
        Sparks(at, colour, 12, Vector3.Up, spread: 180, speed: 8, lifetime: 0.4f, size: 0.45f);
        Light(at, colour, 3, 9, 0.3f);
    }

    // ---- building blocks ------------------------------------------------------------------------

    /// <summary>
    /// Sparks streaking out round <paramref name="direction"/> (within <paramref name="spread"/> degrees) at about
    /// <paramref name="speed"/> m/s, each a thin streak about <paramref name="size"/> metres long along its flight,
    /// falling as they go and cooling from white-hot to the colour.
    /// </summary>
    private void Sparks(Vector3 at, Color colour, int amount, Vector3 direction, float spread, float speed, float lifetime, float size) =>
        Burst(at, amount, lifetime, FxShaders.SparkQuad.Value, new ParticleProcessMaterial
        {
            ParticleFlagAlignY = true,
            Direction = new Vector3(0, 0, -1),
            Spread = spread,
            InitialVelocityMin = speed * 0.5f,
            InitialVelocityMax = speed * 1.2f,
            Gravity = new Vector3(0, -12, 0),
            ScaleMin = size * 0.6f,
            ScaleMax = size * 1.3f,
            ColorRamp = Ramp(new Color(colour.Lerp(Colors.White, 0.5f) * 3.5f, 1), new Color(colour * 2.2f, 1), new Color(colour * 1.2f, 0)),
        }, explosiveness: 0.95f, facing: direction);

    /// <summary>A ball of light (<see cref="FxShaders.Orb"/>) that flares and fades over <paramref name="duration"/>.</summary>
    private void FlashOrb(Vector3 at, Color colour, float size, float duration, float grow = 1.4f)
    {
        var flash = FxShaders.NewOrb(colour, 4, size);
        AddChild(flash);
        flash.GlobalPosition = at;
        Fade(flash, duration, grow);
    }

    /// <summary>A ring of light running out over the ground to <paramref name="radius"/> metres and fading.</summary>
    private void ShockRing(Vector3 at, float radius, Color colour, float duration)
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = Ring,
            AlbedoColor = colour * 1.4f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        var ring = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * 2 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = Vector3.One * radius * 0.1f,
        };
        AddChild(ring);
        ring.GlobalPosition = at;
        var run = ring.CreateTween().SetParallel();
        run.TweenProperty(ring, "scale", Vector3.One * radius, duration).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        run.TweenProperty(material, "albedo_color:a", 0f, duration);
        run.Chain().TweenCallback(Callable.From(ring.QueueFree));
    }

    /// <summary>A shard of dark, scorched plate, lit by the scene (unlike the other particles), its long side along y.</summary>
    private static readonly Lazy<PrismMesh> Shard = new(() => new PrismMesh
    {
        Size = new Vector3(0.5f, 0.8f, 0.07f),
        LeftToRight = 0.25f,
        Material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.34f, 0.3f, 0.27f),
            Metallic = 0.45f,
            Roughness = 0.5f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        },
    });

    /// <summary>A thin ring of light, brightest at its outer edge and soft inside it.</summary>
    internal static readonly Texture2D Ring = Generate(128, (x, y) =>
    {
        var r = new Vector2(x, y).Length();
        var rim = Mathf.Exp(-Mathf.Pow((r - 0.86f) * 14, 2));
        var inside = r < 0.86f ? 0.12f * Mathf.Pow(r / 0.86f, 4) : 0;
        return new Color(1, 1, 1, Mathf.Clamp(rim + inside, 0, 1));
    });
}
