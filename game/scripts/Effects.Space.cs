using Godot;

namespace Machines.Game;

// The effects at the scale of ships and planets, for a cutscene's space stage (SpaceStage): a hull blowing open in a ball of
// fire and debris that hang in the void, and something huge striking a planet seen from orbit. The remake's own; no match
// asks for them.
public partial class Effects
{
    /// <summary>
    /// A blast on a ship's hull <paramref name="size"/> metres across: a flash and its light, balls of fire swelling into dark
    /// smoke, sparks and plates thrown out every way, none of it falling (there is no down out there).
    /// </summary>
    public void SpaceBlast(Vector3 at, float size)
    {
        var scale = size / 6;
        Light(at, new Color(1, 0.6f, 0.3f), 9, size * 10, 1.2f);
        FlashOrb(at, new Color(1, 0.78f, 0.5f), size * 0.9f, 0.22f, grow: 1.8f);
        // The blast itself: a filmed fireball rolling into its own smoke (Unity Labs' Explosion02HD), a few overlapping, and
        // smaller blasts after it about the wound (Explosion01), each from white-hot to dark smoke over its life.
        Large(Burst(at, 3, 3.4f, Book("explosion_big", 5, 5, fade: size * 0.8f), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.15f * size,
            Spread = 180,
            InitialVelocityMin = 0.05f * size,
            InitialVelocityMax = 0.2f * size,
            Gravity = Vector3.Zero,
            ScaleMin = 1.8f * size,
            ScaleMax = 2.4f * size,
            ScaleCurve = Grow(0.55f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Ramp(new Color(1.9f, 1.7f, 1.5f, 1), new Color(1.15f, 1.1f, 1.05f, 1), new Color(0.9f, 0.88f, 0.86f, 0), hold: 0.5f),
        }, explosiveness: 0.9f), size * 6);
        Large(Burst(at, 6, 3f, Book("explosion_small", 5, 5, fade: size * 0.5f), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.6f * size,
            Spread = 180,
            InitialVelocityMin = 0.1f * size,
            InitialVelocityMax = 0.35f * size,
            Gravity = Vector3.Zero,
            ScaleMin = 0.8f * size,
            ScaleMax = 1.4f * size,
            ScaleCurve = Grow(0.6f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Ramp(new Color(1.6f, 1.45f, 1.3f, 1), new Color(1.1f, 1.05f, 1f, 1), new Color(0.9f, 0.88f, 0.86f, 0), hold: 0.5f),
        }, explosiveness: 0.35f), size * 6);
        Large(Burst(at, Mathf.Clamp((int)size, 30, 120), 2.4f, FxShaders.SparkQuad.Value, new ParticleProcessMaterial
        {
            ParticleFlagAlignY = true,
            Spread = 180,
            InitialVelocityMin = 4f * scale,
            InitialVelocityMax = 11f * scale,
            Gravity = Vector3.Zero,
            ScaleMin = 0.25f * scale,
            ScaleMax = 0.6f * scale,
            ColorRamp = Ramp(new Color(4f, 3f, 2f, 1), new Color(2.4f, 1.1f, 0.35f, 1), new Color(1.2f, 0.4f, 0.1f, 0)),
        }, explosiveness: 0.95f), size * 30);
        // Pieces of the hull torn off, tumbling away: bent plates with a thickness, dark and lit by the sun, of several
        // shapes and sizes.
        var pieces = HullPieces.Value;
        for (var i = 0; i < pieces.Length; i++)
            Large(Burst(at, Mathf.Clamp((int)(size / 10), 2, 7), 8f, pieces[i], new ParticleProcessMaterial
            {
                Spread = 180,
                InitialVelocityMin = 1.2f * scale,
                InitialVelocityMax = 4f * scale,
                Gravity = Vector3.Zero,
                DampingMin = 0.02f * scale,
                DampingMax = 0.08f * scale,
                AngularVelocityMin = -160,
                AngularVelocityMax = 160,
                ScaleMin = 0.4f * scale * (1 + i * 0.35f),
                ScaleMax = 1.1f * scale * (1 + i * 0.35f),
                // Turned along their flight and spinning about it: from outside they tumble.
                ParticleFlagAlignY = true,
                ParticleFlagRotateY = true,
            }, explosiveness: 0.92f), size * 40);
    }

    /// <summary>
    /// The bow shock's moving air (<see cref="FilmCamera"/>'s scene props): fine glowing streaks born on the shock's front and
    /// swept back along the hull, in the prop's own frame, and embers peeling off behind it in the world's, left behind as a
    /// wake of fine sparks. <paramref name="across"/> the shock's width, <paramref name="length"/> the prop's; the node faces
    /// its -z ahead. Their share (AmountRatio) follows the shock's strength.
    /// </summary>
    public static (GpuParticles3D Streaks, GpuParticles3D Embers) ShockParticles(float across, float length)
    {
        var streaks = new GpuParticles3D
        {
            Amount = 900,
            Lifetime = 0.55f,
            LocalCoords = true,
            DrawPass1 = FxShaders.SparkQuad.Value,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = "ShockStreaks",
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.SphereSurface,
                EmissionSphereRadius = across * 0.22f,
                Direction = new Vector3(0, 0, 1),
                Spread = 9,
                InitialVelocityMin = length * 1.8f,
                InitialVelocityMax = length * 3.2f,
                Gravity = Vector3.Zero,
                ScaleMin = length * 0.006f,
                ScaleMax = length * 0.018f,
                ParticleFlagAlignY = true,
                ColorRamp = Ramp(new Color(3.2f, 2.6f, 2f, 1), new Color(2.2f, 0.9f, 0.3f, 0.8f), new Color(1f, 0.3f, 0.08f, 0), hold: 0.2f),
            },
        };
        Large(streaks, length * 4);
        var embers = new GpuParticles3D
        {
            Amount = 700,
            Lifetime = 2.2f,
            LocalCoords = false,
            DrawPass1 = FxShaders.SparkQuad.Value,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = "ShockEmbers",
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.SphereSurface,
                EmissionSphereRadius = across * 0.5f,
                Direction = new Vector3(0, 0, 1),
                Spread = 40,
                InitialVelocityMin = length * 0.05f,
                InitialVelocityMax = length * 0.4f,
                Gravity = Vector3.Zero,
                DampingMin = length * 0.05f,
                DampingMax = length * 0.15f,
                ScaleMin = length * 0.003f,
                ScaleMax = length * 0.009f,
                ColorRamp = Ramp(new Color(2.6f, 1.6f, 0.7f, 1), new Color(1.6f, 0.55f, 0.15f, 0.7f), new Color(0.6f, 0.2f, 0.05f, 0)),
            },
        };
        // Left behind a ship that covers kilometres a second: drawn wherever they are.
        Large(embers, length * 200);
        return (streaks, embers);
    }

    // Torn hull plates: irregular outlines, a little bent, as thick as armour, flat-shaded, their torn edges scorched; all
    // dark (two red-hot ones glowed orange and looked cheap).
    private static readonly Lazy<Mesh[]> HullPieces = new(() =>
    {
        var cold = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.3f, 0.27f, 0.25f),
            Metallic = 0.55f,
            Roughness = 0.6f,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var random = new Random(7);
        return Enumerable.Range(0, 3).Select(i => HullPiece(random, cold, corners: 5 + i % 3, bend: 0.15f + i * 0.05f)).ToArray();
    });

    // One plate, about one metre across before its scale: a ring of corners at uneven radii, bent along y, extruded.
    private static Mesh HullPiece(Random random, Material material, int corners, float bend)
    {
        var rim = Enumerable.Range(0, corners).Select(c =>
        {
            var angle = Mathf.Tau * (c + (float)random.NextDouble() * 0.6f) / corners;
            var radius = 0.35f + (float)random.NextDouble() * 0.25f;
            return new Vector2(Mathf.Cos(angle) * radius * 0.7f, Mathf.Sin(angle) * radius * 1.3f);
        }).ToList();
        Vector3 At(Vector2 p, float side) => new(p.X, p.Y, side * 0.04f + bend * p.Y * p.Y);
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        tool.SetSmoothGroup(uint.MaxValue);
        void Corner(Vector3 v, float hot)
        {
            tool.SetColor(hot > 0 ? new Color(0.45f, 0.4f, 0.37f) : Colors.White);
            tool.AddVertex(v);
        }
        var centre = Vector2.Zero;
        for (var c = 0; c < corners; c++)
        {
            var a = rim[c];
            var b = rim[(c + 1) % corners];
            // Both faces, then the torn edge between them (the edge scorched darker).
            Corner(At(centre, 1), 0); Corner(At(b, 1), 0); Corner(At(a, 1), 0);
            Corner(At(centre, -1), 0); Corner(At(a, -1), 0); Corner(At(b, -1), 0);
            Corner(At(a, 1), 1); Corner(At(b, 1), 1); Corner(At(b, -1), 1);
            Corner(At(a, 1), 1); Corner(At(b, -1), 1); Corner(At(a, -1), 1);
        }
        tool.GenerateNormals();
        tool.SetMaterial(material);
        return tool.Commit();
    }

    /// <summary>
    /// Something huge strikes a planet's face (<paramref name="up"/> the way out of it there), seen from orbit: a blinding
    /// flash and its glare on the ground about it, a ring of light running out over the face, a mushroom of dust and fire
    /// rising, the crater glowing; <paramref name="size"/> metres the width of the blast. A column of smoke stays rising.
    /// </summary>
    public void PlanetImpact(Vector3 at, Vector3 up, float size)
    {
        up = up.Normalized();
        Light(at + up * size * 0.3f, new Color(1, 0.7f, 0.4f), 16, size * 6, 5f);
        // The flash as glare lying on the face, white-hot and spreading: a flash or orb standing up as a picture cut into
        // the planet's curve in a hard white wedge.
        var flat = Flat(up);
        Glare(at + up * size * 0.08f, flat, new Color(1, 0.84f, 0.62f) * 1.15f, size * 0.5f, size * 2.4f, 2.4f);
        Glare(at + up * size * 0.04f, flat, new Color(1, 0.6f, 0.3f) * 0.9f, size * 0.3f, size * 1.1f, 4f);
        // The ring over the face, lying on it.
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = Ring,
            AlbedoColor = new Color(1, 0.75f, 0.45f) * 1.6f,
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
        };
        AddChild(ring);
        ring.GlobalTransform = new Transform3D(flat.Scaled(Vector3.One * size * 0.2f), at + up * size * 0.02f);
        var run = ring.CreateTween().SetParallel();
        run.TweenMethod(Callable.From<float>(r => ring.GlobalTransform = new Transform3D(flat.Scaled(Vector3.One * r), at + up * size * 0.02f)),
            size * 0.2f, size * 5f, 6f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        run.TweenProperty(material, "albedo_color:a", 0f, 6f);
        run.Chain().TweenCallback(Callable.From(ring.QueueFree));
        // The mushroom: filmed fireballs rolling up into their smoke, and a cloud of dust spreading over them.
        var scale = size / 6;
        Large(Burst(at + up * size * 0.45f, 7, 5f, Book("explosion_big", 5, 5, fade: size * 0.9f), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.25f * size,
            Direction = new Vector3(0, 0, -1),
            Spread = 40,
            InitialVelocityMin = 0.08f * size,
            InitialVelocityMax = 0.25f * size,
            Gravity = Vector3.Zero,
            ScaleMin = 1.2f * size,
            ScaleMax = 1.9f * size,
            ScaleCurve = Grow(0.4f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Ramp(new Color(1.5f, 1.3f, 1.1f, 1), new Color(1.1f, 0.98f, 0.86f, 1), new Color(1f, 0.92f, 0.82f, 0), hold: 0.55f),
        }, explosiveness: 0.75f, facing: up), size * 4);
        Large(Burst(at + up * size * 0.5f, 24, 16f, Book("cloud", 8, 8, fade: size * 0.8f), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.5f * size,
            Direction = new Vector3(0, 0, -1),
            Spread = 75,
            InitialVelocityMin = 0.015f * size,
            InitialVelocityMax = 0.06f * size,
            Gravity = Vector3.Zero,
            DampingMin = 0.005f * size,
            DampingMax = 0.012f * size,
            ScaleMin = 0.8f * size,
            ScaleMax = 1.4f * size,
            ScaleCurve = Grow(0.35f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 0.6f,
            AnimSpeedMax = 0.9f,
            ColorRamp = Ramp(new Color(0.9f, 0.7f, 0.52f, 0), new Color(0.8f, 0.64f, 0.5f, 0.9f), new Color(0.7f, 0.6f, 0.5f, 0), hold: 0.25f),
        }, explosiveness: 0.4f, facing: up), size * 8);
        // The column that stays, and the crater's glow under it.
        var column = Plume(10, 18, size * 2.5f, size * 0.8f, new Color(0.24f, 0.2f, 0.18f, 0.85f));
        AddChild(column);
        column.GlobalTransform = new Transform3D(Flat(up), at);
        column.Emitting = true;
        Glare(at + up * size * 0.03f, flat, new Color(1, 0.42f, 0.12f) * 1.2f, size * 0.45f, size * 0.55f, 24f, keep: 0.25f);
    }

    /// <summary>A soft glow lying flat on a face, growing from one radius to another and fading (to <paramref name="keep"/> of it).</summary>
    private void Glare(Vector3 at, Basis flat, Color colour, float from, float to, float seconds, float keep = 0)
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = GlareTexture,
            AlbedoColor = colour,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        var disc = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * 2 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(disc);
        disc.GlobalTransform = new Transform3D(flat.Scaled(Vector3.One * from), at);
        var tween = disc.CreateTween().SetParallel();
        tween.TweenMethod(Callable.From<float>(r => disc.GlobalTransform = new Transform3D(flat.Scaled(Vector3.One * r), at)), from, to, seconds)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenProperty(material, "albedo_color:a", keep, seconds).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        if (keep <= 0)
            tween.Chain().TweenCallback(Callable.From(disc.QueueFree));
    }

    /// <summary>A soft round glow, bright in the middle and gone at the edge.</summary>
    private static readonly Texture2D GlareTexture = Generate(128, (x, y) =>
    {
        var r = Mathf.Min(new Vector2(x, y).Length(), 1);
        var glow = Mathf.Exp(-r * r * 5) * (1 - r * r);
        return new Color(1, 1, 1, glow);
    });

    /// <summary>
    /// A wound burning on a hull in space <paramref name="width"/> metres across: filmed fireballs (Unity Labs' FireBall02)
    /// boiling out of it and left behind as the ship moves, flames <paramref name="height"/> metres long; no flicker of
    /// cartoon flames, nothing rising (there is no up). Null with effects off.
    /// </summary>
    public static GpuParticles3D? HullFire(float width, float height) => GraphicsSettings.Effects ? Bigger(new GpuParticles3D
    {
        Amount = 14,
        Lifetime = 1.6f,
        LocalCoords = false,
        DrawPass1 = Book("fireball_loop", 8, 8, additive: true, loop: true, fade: width * 0.7f),
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = width * 0.25f,
            Spread = 180,
            InitialVelocityMin = height * 0.1f,
            InitialVelocityMax = height * 0.35f,
            Gravity = Vector3.Zero,
            ScaleMin = width * 0.7f,
            ScaleMax = width * 1.2f,
            ScaleCurve = Grow(0.5f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 0.8f,
            AnimSpeedMax = 1.2f,
            AnimOffsetMax = 1,
            ColorRamp = Ramp(new Color(1.4f, 1.2f, 1f, 0), new Color(1.5f, 1.15f, 0.9f, 1), new Color(0.5f, 0.25f, 0.12f, 0), hold: 0.2f),
        },
    }, width * 40) : null;

    /// <summary>
    /// Smoke streaming from a burning hull in space, <paramref name="size"/> metres a puff: filmed wisps (Unity Labs'
    /// WispySmoke02), dark, drifting out at <paramref name="drift"/> metres a second and spreading as they are left behind.
    /// </summary>
    public static GpuParticles3D SpaceSmoke(float size, float drift) => Bigger(new GpuParticles3D
    {
        Amount = 24,
        Lifetime = 6f,
        LocalCoords = false,
        DrawPass1 = Book("wisp", 8, 8, loop: true, fade: size * 0.9f),
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = size * 0.3f,
            Spread = 180,
            InitialVelocityMin = drift * 0.5f,
            InitialVelocityMax = drift * 1.5f,
            Gravity = Vector3.Zero,
            ScaleMin = size * 1.6f,
            ScaleMax = size * 2.6f,
            ScaleCurve = Grow(0.3f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 0.3f,
            AnimSpeedMax = 0.5f,
            AnimOffsetMax = 1,
            ColorRamp = Ramp(new Color(0.3f, 0.27f, 0.25f, 0), new Color(0.22f, 0.2f, 0.19f, 0.9f), new Color(0.3f, 0.29f, 0.28f, 0), hold: 0.2f),
        },
    }, size * 80);

    /// <summary>
    /// Dust thrown up where something heavy strikes the ground at <paramref name="at"/>: a ring of filmed cloud (Unity Labs'
    /// Cloud02) rolling out low over the ground <paramref name="size"/> metres, and a taller burst over the strike, in the
    /// ground's sandy colour, settling slowly.
    /// </summary>
    public void DustBurst(Vector3 at, float size, Color sand)
    {
        if (!GraphicsSettings.Effects)
            return;
        Large(Burst(at + Vector3.Up * size * 0.03f, 140, 8f, Book("cloud", 8, 8, fade: size * 0.12f), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.Up,
            EmissionRingRadius = size * 0.15f,
            EmissionRingInnerRadius = size * 0.05f,
            EmissionRingHeight = size * 0.05f,
            Direction = new Vector3(1, 0.12f, 0),
            Spread = 180,
            Flatness = 1,
            InitialVelocityMin = size * 0.3f,
            InitialVelocityMax = size * 0.9f,
            DampingMin = size * 0.2f,
            DampingMax = size * 0.4f,
            Gravity = new Vector3(0, 0.05f, 0),
            ScaleMin = size * 0.25f,
            ScaleMax = size * 0.45f,
            ScaleCurve = Grow(0.3f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 0.5f,
            AnimSpeedMax = 0.8f,
            ColorRamp = Ramp(sand with { A = 0.7f }, sand with { A = 0.6f }, sand with { A = 0 }, hold: 0.3f),
        }, explosiveness: 0.95f), size * 3);
        Large(Burst(at, 16, 9f, Book("cloud", 8, 8, fade: size * 0.12f), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = size * 0.08f,
            Direction = Vector3.Up,
            Spread = 30,
            InitialVelocityMin = size * 0.1f,
            InitialVelocityMax = size * 0.3f,
            DampingMin = size * 0.06f,
            DampingMax = size * 0.12f,
            Gravity = Vector3.Zero,
            ScaleMin = size * 0.3f,
            ScaleMax = size * 0.55f,
            ScaleCurve = Grow(0.3f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 0.4f,
            AnimSpeedMax = 0.6f,
            ColorRamp = Ramp(sand with { A = 0.7f }, (sand * 0.85f) with { A = 0.6f }, (sand * 0.8f) with { A = 0 }, hold: 0.3f),
        }, explosiveness: 0.8f), size * 3);
    }

    // A sheet of filmed frames (game/assets/particles/flipbooks/unity, Unity Labs' CC0 flipbooks), h by v frames, played once
    // over a particle's life or looped, fading out over <paramref name="fade"/> metres before whatever stands behind it.
    private static QuadMesh Book(string name, int h, int v, bool additive = false, bool loop = false, float fade = 6)
    {
        fade = Mathf.Max(1, Mathf.Round(fade));
        var key = $"unity/{name}|{additive}|{loop}|{fade}";
        if (Flipbooks.TryGetValue(key, out var cached))
            return cached;
        var mesh = Billboard(Particle($"flipbooks/unity/{name}"), additive);
        var material = (StandardMaterial3D)mesh.Material;
        (material.ParticlesAnimHFrames, material.ParticlesAnimVFrames, material.ParticlesAnimLoop) = (h, v, loop);
        // Faded where they meet the ground, a hull or the planet, over a depth as large as the effect: the flat quads, as
        // wide as a blast, cut straight edges into what they reached.
        material.ProximityFadeEnabled = true;
        material.ProximityFadeDistance = fade;
        return Flipbooks[key] = mesh;
    }

    private static GpuParticles3D Bigger(GpuParticles3D particles, float reach)
    {
        Large(particles, reach);
        return particles;
    }

    // A basis whose y is up (a plane lying on the face there).
    private static Basis Flat(Vector3 up)
    {
        var side = up.Cross(Mathf.Abs(up.Y) < 0.95f ? Vector3.Up : Vector3.Right).Normalized();
        var ahead = side.Cross(up).Normalized();
        return new Basis(side, up, ahead);
    }

    // Particles seen from far away: drawn while anywhere near the view, however large they grow.
    private static void Large(GpuParticles3D particles, float reach) =>
        particles.VisibilityAabb = new Aabb(-Vector3.One * reach, Vector3.One * reach * 2);
}
