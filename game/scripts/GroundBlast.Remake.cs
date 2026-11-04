using Godot;
using Machines.Sim.Combat;

namespace Machines.Game;

/// <summary>
/// The big blasts as the remake's effects draw them, on the original's timings and sizes (see GroundBlast.cs): the
/// fire along the cracks as walls of the rendered flames; the nuke as a ball of white fire, a mushroom of rendered
/// fire and smoke whose stem and cap grow as the original's, and a shock wave of light with a wall of dust racing out
/// over the ground; the ion cannon as columns of running light from the sky with sparks flying up round their foot;
/// the vortex as a black hole drawing space and dust in.
/// </summary>
public partial class GroundBlast
{
    // The colour the flames take, or null for the rendered fire's own (the bee bomb's burn blue).
    private Color? _flameTint;

    /// <summary>A child's basis whose z (the way a streak runs) points straight up.</summary>
    private static readonly Basis Skyward = new(Vector3.Right, Vector3.Forward, Vector3.Up);

    /// <summary>
    /// A wall of the rendered flames along a crack from <paramref name="a"/> to <paramref name="b"/>, rising to
    /// <paramref name="height"/> metres and (for the punch) sinking again on the original fire's timing
    /// (<see cref="Section"/>).
    /// </summary>
    private void FlameWall(Vector3 a, Vector3 b, float start, float time, float fade, float height, bool falls)
    {
        var length = a.DistanceTo(b);
        var along = ((b - a) with { Y = 0 }).Normalized();
        if (along.LengthSquared() < 0.5f)
            return;
        // A flame is twice as high as it is wide.
        var size = Mathf.Max(height * 0.55f, 0.6f);
        var middle = (a + b) / 2;
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(length / 2, 0, 0.3f),
            Direction = Vector3.Up,
            Spread = 6,
            InitialVelocityMin = height * 0.2f,
            InitialVelocityMax = height * 0.4f,
            Gravity = Vector3.Zero,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = _flameTint is { } tint
                ? Effects.Ramp((tint * 1.6f) with { A = 0.9f }, (tint * 1.3f) with { A = 1 }, tint with { A = 0 })
                : Effects.Ramp(new Color(1.8f, 1.7f, 1.6f, 0.9f), new Color(1.4f, 1.3f, 1.2f, 1), new Color(1, 1, 1, 0)),
        };
        // Each flame lives a little over half the fire's time (the bee bomb's last a quarter of a second), at most half a second.
        var life = Mathf.Clamp(time * 0.6f, 0.2f, 0.5f);
        var flames = Emitter(Effects.Flipbook("flame", tall: true, grey: _flameTint is not null), process,
            Math.Clamp((int)Mathf.Ceil(length / size * 2.5f) + 1, 2, 16), life, middle, length / 2 + height * 2);
        flames.Basis = new Basis(along, Vector3.Up, along.Cross(Vector3.Up));
        _parts.Add(age =>
        {
            var t = age - start;
            flames.Emitting = t >= 0 && t < time - life * 0.5f;
            if (t < 0)
                return true;
            var rise = falls ? fade : time;
            var high = t < rise ? Mathf.Lerp(0.15f, 1, t / rise) : Mathf.Lerp(1, 0.15f, Mathf.Clamp((t - rise) / Mathf.Max(0.01f, time - rise), 0, 1));
            process.ScaleMin = size * 0.8f * high;
            process.ScaleMax = size * high;
            flames.Position = middle + Vector3.Up * size * high * 0.85f;
            return t < time + flames.Lifetime;
        });
    }

    /// <summary>
    /// The nuclear missile's blast over the original's 16 s: a ball of white fire swelling for a second and a half;
    /// the shock wave as a ring of light and a wall of dust racing out to 220 m (EXTRAS) in 3 s; the mushroom's stem
    /// of fire rising to seven times its height, its skirt rolling at the foot, its cap of fire and smoke growing by
    /// 2.67 times as it climbs from 6 to 60 m, all cooling to smoke and gone at the end; the cracks tearing outwards
    /// and the lights as the original's.
    /// </summary>
    private static GroundBlast RemadeNuke(ClassicAssets assets, Blast blast, IReadOnlyList<float> extras)
    {
        float Extra(int i, float fallback) => i < extras.Count ? extras[i] : fallback;
        const float duration = 400f / 25;
        var (waveStart, waveTime, fromRadius, toRadius) = (Extra(6, 1), Extra(14, 3), Extra(12, 0.01f), Extra(13, 220));
        var view = new GroundBlast { Name = "Nuke" };
        var at = ActorView.World(blast.Position);

        var ball = FxShaders.NewOrb(new Color(1, 0.8f, 0.55f), 4, 60);
        ball.SetInstanceShaderParameter("swirl", 3f);
        view.AddChild(ball);
        ball.Position = at + Vector3.Up * 20;

        var shock = new Color(1, 0.8f, 0.55f);
        var (wall, wallLook) = view.Glowing(Band.Value, Fringe);
        var dust = view.Emitter(Effects.Flipbook("billow"), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 10,
            Direction = Vector3.Right,
            Spread = 180,
            Flatness = 1,
            InitialVelocityMin = 80,
            InitialVelocityMax = 100,
            DampingMin = 20,
            DampingMax = 24,
            Gravity = new Vector3(0, 1, 0),
            ScaleMin = 30,
            ScaleMax = 45,
            ScaleCurve = Effects.Grow(0.7f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Effects.Ramp(Effects.DustTint with { A = 0.75f }, Effects.DustTint.Darkened(0.1f) with { A = 0.55f },
                Effects.DustTint.Darkened(0.2f) with { A = 0 }),
        }, 220, 6, at + Vector3.Up * 6, 280, oneShot: true);

        // The stem rises at the speed that carries its fire to its top; the skirt rolls round its foot. The rendered
        // fireball fills about a third of its frame, so the particles are about three times the fire they show.
        var stemLook = Fire(Ring(10, 0, 2), spread: 5, speed: (0, 0), size: (50, 65), played: 0.5f, hold: 0.85f);
        stemLook.DampingMin = stemLook.DampingMax = 0;
        var stem = view.Emitter(Effects.Flipbook("fireball"), stemLook, 100, 3.5f, at + Vector3.Up * 4, 150);
        // The fire shows through the cap's smoke, which hangs down round it (the two share their place).
        stem.SortingOffset = 10;
        var skirtLook = Fire(Ring(22, 8, 2), spread: 50, speed: (2, 5), size: (30, 42), played: 0.75f);
        var skirt = view.Emitter(Effects.Flipbook("fireball"), skirtLook, 50, 3, at + Vector3.Up * 3, 110);

        // The cap: fire inside smoke, in its own space so they climb and grow with it.
        var cap = new Node3D { Name = "Cap" };
        view.AddChild(cap);
        var capFire = Fire(Ring(9, 2, 5), spread: 75, speed: (1, 3), size: (28, 38), played: 0.7f, hold: 0.6f);
        var fire = view.Emitter(Effects.Flipbook("fireball"), capFire, 90, 3.2f, Vector3.Zero, 80, local: true, parent: cap);
        fire.SortingOffset = 20;
        var capSmoke = Fire(Ring(17, 12, 6), spread: 90, speed: (0.5f, 2), size: (24, 32));
        capSmoke.ColorRamp = Effects.Ramp(new Color(0.42f, 0.36f, 0.32f, 0.65f), new Color(0.32f, 0.28f, 0.25f, 0.55f), new Color(0.25f, 0.22f, 0.2f, 0));
        var smoke = view.Emitter(Effects.Flipbook("billow"), capSmoke, 60, 4.5f, Vector3.Zero, 90, local: true, parent: cap);

        var dustRaised = false;
        view._parts.Add(age =>
        {
            ball.Visible = age < 1.6f;
            ball.Scale = Vector3.One * Mathf.Lerp(60, 110, Mathf.Clamp(age / 1.6f, 0, 1));
            ball.SetInstanceShaderParameter("fade", Fading(age, 0.4f, 1.6f));

            var t = (age - waveStart) / waveTime;
            wall.Visible = t is >= 0 and < 1;
            var reach = Mathf.Lerp(fromRadius, toRadius, Mathf.Clamp(t, 0, 1));
            wall.Transform = new Transform3D(Basis.FromScale(new Vector3(reach, 12, reach)), at - Vector3.Up * 3);
            wallLook.AlbedoColor = shock with { A = Fading(t, 0.4f, 1) };
            if (!dustRaised && t >= 0)
                dust.Emitting = dustRaised = true;

            var grown = Mathf.Clamp(age / duration, 0, 1);
            var size = Mathf.Lerp(1, 2.67f, grown);
            var capHeight = Mathf.Lerp(6, 60, grown) + 3 * size;
            cap.Transform = new Transform3D(Basis.FromScale(Vector3.One * size), at + Vector3.Up * capHeight);
            // The stem's fire climbs into the cap, which sits on the original's stem (6.9 m growing sevenfold).
            stemLook.InitialVelocityMin = (capHeight - 4) / (float)stem.Lifetime * 0.8f;
            stemLook.InitialVelocityMax = (capHeight - 4) / (float)stem.Lifetime;
            // The fire glows less as it cools; everything has thinned out by the end.
            var glow = Mathf.Lerp(1.3f, 0.7f, grown);
            var left = Fading(age, duration - 1.5f, duration);
            foreach (var look in new[] { stemLook, skirtLook, capFire })
                look.Color = new Color(glow, glow, glow, left);
            capSmoke.Color = new Color(1, 1, 1, left);
            stem.Emitting = age > 0.3f && age < duration - stem.Lifetime;
            skirt.Emitting = age > 0.3f && age < duration / 2;
            fire.Emitting = age > 0.6f && age < duration - fire.Lifetime;
            smoke.Emitting = age > 0.6f && age < duration - smoke.Lifetime;
            return age < duration;
        });

        view.Flash(at + Vector3.Up * 40, new Color(1, 1, 1), 12, 400, 0, Extra(6, 1));
        view.Flash(at + Vector3.Up * 30, new Color(1, 0.55f, 0.2f), Extra(9, 4), Extra(11, 125) * 2, 0, duration * Mathf.Min(1, Extra(10, 1.5f)));
        if (assets.EffectMesh("punchcrack") is { } crack && assets.EffectMesh("punchfire") is { } flames)
        {
            var random = new Random((int)(blast.Position.X * 31 + blast.Position.Y));
            var cracks = random.Next(4, 7);
            for (var k = 0; k < cracks; k++)
            {
                var angle = Mathf.DegToRad(k * 360f / cracks + (float)random.NextDouble() * 20 - 10);
                var start = duration * (0.05f + 0.2f * (float)random.NextDouble());
                view.PunchCracks(crack, flames, blast.Position, new System.Numerics.Vector2(MathF.Cos(angle), MathF.Sin(angle)), start);
            }
        }
        return view;
    }

    /// <summary>How the column's strength steps from frame to frame, after the shell's four textures.</summary>
    private static readonly float[] IonFlicker = [1, 0.7f, 0.9f, 0.55f];

    private const int IonRipples = 3;
    private const float IonRippleTime = 1.5f;

    /// <summary>
    /// The ion cannon over the original's timing: for 6 s a flickering column of light with a white-hot core comes
    /// down from 300 m, widening from nothing to its radius (EXTRAS, 30 m), energy running down it, the ground under
    /// it glowing and sparks flying up round its foot, the original's wave and flare spreading from it
    /// (<see cref="IonWave"/>) with rings of light running out to the wave; then the column draws in to a thread
    /// within a quarter second, a ring of light runs out over the ground to the radius and back, a thread of light
    /// shoots up, and the ring of burning cracks opens as the original's.
    /// </summary>
    private static GroundBlast RemadeIonCannon(ClassicAssets assets, Blast blast, IReadOnlyList<float> extras)
    {
        float Extra(int i, float fallback) => i < extras.Count ? extras[i] : fallback;
        var radius = Extra(0, 30);
        const float expansion = 6, shrink = 0.25f;
        var view = new GroundBlast { Name = "IonCannon" };
        var at = ActorView.World(blast.Position);
        var colour = new Color(0.55f, 0.75f, 1f);

        // The columns start under the ground, so they meet it at full strength.
        var shell = FxShaders.NewStreak(colour, 1.2f, radius * 2, 320, head: 0, white: 0.3f);
        var core = FxShaders.NewStreak(colour, 3, radius * 0.6f, 320, head: 0, white: 0.9f);
        var thread = FxShaders.NewStreak(colour, 5, 3, 2000, head: 0, white: 0.9f);
        foreach (var column in new[] { shell, core, thread })
        {
            column.SetInstanceShaderParameter("flow", -6f);
            column.SetInstanceShaderParameter("wavelength", 40f);
            column.Visible = false;
            view.AddChild(column);
            column.Transform = new Transform3D(Skyward, at - Vector3.Up * 20);
        }
        // The flash as it strikes, high enough that the ground does not cut it off.
        var strike = FxShaders.NewOrb(colour, 4, radius * 1.4f);
        strike.Visible = false;
        view.AddChild(strike);
        strike.Position = at + Vector3.Up * radius * 0.75f;
        var (pool, poolLook) = view.Glowing(new PlaneMesh { Size = Vector2.One * 2 }, Effects.Soft);
        var (ring, ringLook) = view.Glowing(new PlaneMesh { Size = Vector2.One * 2 }, Effects.Ring);
        var sparkLook = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.Up,
            EmissionRingRadius = 1,
            EmissionRingInnerRadius = 0,
            EmissionRingHeight = 1,
            Direction = Vector3.Up,
            Spread = 15,
            InitialVelocityMin = 20,
            InitialVelocityMax = 45,
            Gravity = Vector3.Zero,
            ScaleMin = 2,
            ScaleMax = 4,
            ParticleFlagAlignY = true,
            ColorRamp = Effects.Ramp(new Color(2, 2.4f, 3), colour * 2, colour with { A = 0 }),
        };
        var sparks = view.Emitter(FxShaders.SparkQuad.Value, sparkLook, 80, 0.9f, at, 120);
        view.IonWave(assets, at, radius, expansion, shrink, assets.EffectMesh("ionouter")?.SurfaceGetMaterial(0) as StandardMaterial3D ?? new StandardMaterial3D());
        var ripples = Enumerable.Range(0, IonRipples).Select(_ => view.Glowing(new PlaneMesh { Size = Vector2.One * 2 }, Effects.Ring)).ToList();

        view._parts.Add(age =>
        {
            var width = Mathf.Lerp(0.01f, radius, Mathf.Clamp(age / expansion, 0, 1));
            var charging = age < expansion;
            // The original's four textures flicker the shell a frame each; here its energy does.
            var flicker = IonFlicker[(int)(age * 25) % IonFlicker.Length];
            core.Visible = charging;
            core.SetInstanceShaderParameter("energy", 3 * flicker);
            core.SetInstanceShaderParameter("thickness", Mathf.Max(width * 0.6f, 0.3f));
            var drawn = Mathf.Clamp((age - expansion) / shrink, 0, 1);
            shell.Visible = age < expansion + shrink;
            shell.SetInstanceShaderParameter("thickness", Mathf.Lerp(width * 2, 0.5f, drawn));
            shell.SetInstanceShaderParameter("energy", Mathf.Lerp(1.2f * flicker, 4, drawn));
            pool.Visible = charging;
            pool.Transform = new Transform3D(Basis.FromScale(new Vector3(width * 1.4f, 1, width * 1.4f)), at + Vector3.Up * 0.3f);
            poolLook.AlbedoColor = colour with { A = 0.7f * flicker };
            // Rings of light run out to the wave one after another, dying as they reach it.
            for (var i = 0; i < ripples.Count; i++)
            {
                var (ripple, rippleLook) = ripples[i];
                var run = age / IonRippleTime - (float)i / ripples.Count;
                ripple.Visible = charging && run >= 0;
                run = Mathf.PosMod(run, 1);
                ripple.Transform = new Transform3D(Basis.FromScale(new Vector3(3.35f * width * run, 1, 3.35f * width * run)), at + Vector3.Up * 0.6f);
                rippleLook.AlbedoColor = new Color(0.8f, 1, 1.4f, (1 - run) * flicker);
            }
            sparks.Emitting = charging && age > 0.3f;
            sparkLook.EmissionRingRadius = Mathf.Max(width, 0.5f);

            var w = age - expansion;
            ring.Visible = w >= 0 && w < 2 * shrink;
            var reach = w < shrink ? Mathf.Lerp(0.01f, radius, w / shrink) : Mathf.Lerp(radius, 0.2f * radius, Mathf.Clamp((w - shrink) / shrink, 0, 1));
            ring.Transform = new Transform3D(Basis.FromScale(new Vector3(reach, 1, reach)), at + Vector3.Up * 0.5f);
            ringLook.AlbedoColor = new Color(1.2f, 1.3f, 1.5f, Fading(w, shrink, 2 * shrink));
            strike.Visible = w >= 0 && w < 2 * shrink;
            strike.SetInstanceShaderParameter("fade", Fading(w, 0, 2 * shrink));
            thread.Visible = w >= shrink && w < 3 * shrink;
            thread.SetInstanceShaderParameter("fade", Fading(w, 2 * shrink, 3 * shrink));
            return age < expansion + 3 * shrink + sparks.Lifetime;
        });

        // Twice the original's flicker, to go with the column's.
        view.Flash(at + Vector3.Up * Extra(9, 2) * 30, new Color(Extra(1, 1.3f), Extra(2, 1.2f), Extra(3, 0.8f)), 3, radius * 4, 0, expansion + 0.3f,
            age => IonLight(age, expansion, Extra(5, 0.3f), Extra(6, 1.5f), Extra(7, 7), Extra(8, 0.5f) * 2));
        view.Flash(at + Vector3.Up * 20, new Color(1.5f, 0.78f, 0), 4, radius * 4, expansion - 0.2f, 0.1f);
        if (assets.EffectMesh("punchcrack") is { } crack && assets.EffectMesh("punchfire") is { } fire)
        {
            var path = RingPath(2).Select(p => blast.Position + p).ToList();
            var time = Extra(10, 3);
            for (var i = 0; i + 1 < path.Count; i++)
                view.Section(crack, fire, path[i], path[i + 1], expansion,
                    crackTime: time, crackFade: time / 3, crackWidth: (0.01f, 7.5f),
                    fireTime: time * 2 / 3, fireFade: time / 3, fireHeight: 12, fireFalls: true);
        }
        return view;
    }

    /// <summary>How far round the black hole space is drawn in, in its radii.</summary>
    private const float LensReach = 2.2f;

    /// <summary>
    /// The vortex as a black hole, on the original's sizes and timing (see <see cref="Vortex"/>): a shell of white
    /// light flares round it for 2 s; the hole, a solid black sphere with wisps of light winding round its edge
    /// (<see cref="FxShaders.Hole"/>), grows to its radius with space drawn in about it (<see cref="FxShaders.Lens"/>)
    /// and dust swirling in, while the ground round it darkens more and more (<see cref="Darkness"/>); in the last
    /// 2 s its wisps die, it collapses to nothing as the ground goes black, and the shell of light runs out from it
    /// 30 m further and fades.
    /// </summary>
    private static GroundBlast RemadeVortex(Blast blast, IReadOnlyList<float> extras)
    {
        var radius = extras.Count > 0 ? extras[0] : 40;
        var view = new GroundBlast { Name = "Vortex" };
        var at = ActorView.World(blast.Position);
        MeshInstance3D Shown(Mesh mesh, Material look)
        {
            var shown = new MeshInstance3D { Mesh = mesh, MaterialOverride = look, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
            view.AddChild(shown);
            return shown;
        }
        var hole = Shown(FxShaders.HoleSphere.Value, FxShaders.Hole.Value);
        var shell = Shown(FxShaders.HoleSphere.Value, FxShaders.Shell.Value);
        var lens = Shown(new QuadMesh { Size = Vector2.One }, FxShaders.Lens.Value);
        // The hole's outline lies a little outside its radius, seen in perspective.
        lens.SetInstanceShaderParameter("hole", 1.03f / LensReach);

        // Dust from round it spiralling in, darkening as it falls.
        var inflow = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.SphereSurface,
            EmissionSphereRadius = 1,
            Gravity = Vector3.Zero,
            RadialAccelMin = -60,
            RadialAccelMax = -40,
            TangentialAccelMin = 20,
            TangentialAccelMax = 35,
            ScaleMin = 5,
            ScaleMax = 9,
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 1,
            AnimSpeedMax = 1,
            ColorRamp = Effects.Ramp(Effects.DustTint with { A = 0 }, Effects.DustTint.Darkened(0.3f) with { A = 0.6f },
                Effects.DustTint.Darkened(0.8f) with { A = 0 }),
        };
        var dust = view.Emitter(Effects.Flipbook("billow"), inflow, 60, 1.2f, at, 80);

        view._parts.Add(age =>
        {
            var size = Mathf.Max(VortexSize(age, radius), 0.01f);
            hole.Visible = age < VortexTime;
            hole.Transform = new Transform3D(new Basis(Vector3.Up, age * 1.2f).Scaled(Vector3.One * size), at);
            hole.SetInstanceShaderParameter("calm", Mathf.Clamp((age - VortexSplit) / 0.3f, 0, 1));
            lens.Visible = hole.Visible;
            lens.Transform = new Transform3D(Basis.FromScale(Vector3.One * size * LensReach), at);
            lens.SetInstanceShaderParameter("pull", size / (2 * radius));

            // The shell glows through while it flares, and is only a ring of light about the hole as it collapses.
            shell.Visible = age < 2 || age >= VortexSplit && age < VortexTime;
            shell.Transform = new Transform3D(Basis.FromScale(Vector3.One * Mathf.Max(WhiteSize(age, radius), 0.01f)), at);
            shell.SetInstanceShaderParameter("fade", WhiteShown(age));
            shell.SetInstanceShaderParameter("fill", age < 2 ? 0.15f : 0);

            inflow.EmissionSphereRadius = Mathf.Max(size * 0.75f, 1);
            dust.Emitting = age > 0.5f && age < VortexSplit;
            return age < VortexTime;
        });
        view.Flash(at + Vector3.Up * 10, new Color(1, 1, 1), 6, radius * 3, 0, 2);
        view.Darkness(at, radius);
        return view;
    }

    /// <summary>
    /// Fire (or smoke) rising from <paramref name="shape"/>: swelling, turned at random, playing <paramref name="played"/>
    /// of its flipbook (the rendered fireball burns for its first half and is smoke after) and fading out after
    /// <paramref name="hold"/> of its life.
    /// </summary>
    private static ParticleProcessMaterial Fire(ParticleProcessMaterial shape, float spread, (float Min, float Max) speed, (float Min, float Max) size,
        float played = 1, float hold = 0.3f)
    {
        shape.Direction = Vector3.Up;
        shape.Spread = spread;
        shape.InitialVelocityMin = speed.Min;
        shape.InitialVelocityMax = speed.Max;
        shape.Gravity = Vector3.Zero;
        shape.DampingMin = 0.5f;
        shape.DampingMax = 1;
        shape.ScaleMin = size.Min;
        shape.ScaleMax = size.Max;
        shape.ScaleCurve = Effects.Grow(0.55f);
        shape.AngleMin = -180;
        shape.AngleMax = 180;
        shape.AnimSpeedMin = played;
        shape.AnimSpeedMax = played;
        shape.ColorRamp = Effects.Ramp(new Color(2.2f, 2f, 1.8f, 1), new Color(1.4f, 1.3f, 1.2f, 1), new Color(0.8f, 0.75f, 0.7f, 0), hold);
        return shape;
    }

    /// <summary>A process emitting from a flat ring about the up axis, <paramref name="height"/> metres thick.</summary>
    private static ParticleProcessMaterial Ring(float radius, float inner, float height) => new()
    {
        EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
        EmissionRingAxis = Vector3.Up,
        EmissionRingRadius = radius,
        EmissionRingInnerRadius = inner,
        EmissionRingHeight = height,
    };

    /// <summary>Particles at <paramref name="at"/> that start when told to, seen within <paramref name="reach"/> metres of it, the youngest in front.</summary>
    private GpuParticles3D Emitter(Mesh look, ParticleProcessMaterial process, int amount, float lifetime, Vector3 at, float reach,
        bool oneShot = false, bool local = false, Node3D? parent = null)
    {
        var particles = new GpuParticles3D
        {
            Amount = amount,
            Lifetime = lifetime,
            OneShot = oneShot,
            Explosiveness = oneShot ? 1 : 0,
            LocalCoords = local,
            Emitting = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            DrawPass1 = look,
            ProcessMaterial = process,
            VisibilityAabb = new Aabb(-Vector3.One * reach, Vector3.One * reach * 2),
            DrawOrder = GpuParticles3D.DrawOrderEnum.Lifetime,
            Position = at,
        };
        (parent ?? this).AddChild(particles);
        return particles;
    }

    /// <summary>A mesh glowing in <paramref name="texture"/> (added light, unlit), hidden until shown.</summary>
    private (MeshInstance3D View, StandardMaterial3D Look) Glowing(Mesh mesh, Texture2D texture)
    {
        var look = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = texture,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        var view = new MeshInstance3D { Mesh = mesh, MaterialOverride = look, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(view);
        return (view, look);
    }

    /// <summary>An open band round the up axis, a metre high on a circle a metre round, its texture's v from its foot (0) to its top.</summary>
    private static readonly Lazy<ArrayMesh> Band = new(() =>
    {
        const int segments = 96;
        var vertices = new Vector3[(segments + 1) * 2];
        var uvs = new Vector2[vertices.Length];
        var indices = new int[segments * 6];
        for (var i = 0; i <= segments; i++)
        {
            var angle = Mathf.Tau * i / segments;
            var round = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            (vertices[i * 2], vertices[i * 2 + 1]) = (round, round + Vector3.Up);
            (uvs[i * 2], uvs[i * 2 + 1]) = (new Vector2((float)i / segments, 0), new Vector2((float)i / segments, 1));
            if (i == segments)
                continue;
            var k = i * 6;
            (indices[k], indices[k + 1], indices[k + 2]) = (i * 2, i * 2 + 1, i * 2 + 2);
            (indices[k + 3], indices[k + 4], indices[k + 5]) = (i * 2 + 1, i * 2 + 3, i * 2 + 2);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    });

    /// <summary>Light along the foot of a band (the picture's top row, v 0), fading out above it.</summary>
    private static readonly Texture2D Fringe = Effects.Generate(32, (_, y) => new Color(1, 1, 1, Mathf.Pow((1 - y) / 2, 2)));
}
