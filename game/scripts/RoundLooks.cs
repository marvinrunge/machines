using Godot;

namespace Machines.Game;

/// <summary>
/// How the remake's effects draw rounds in flight (see <see cref="ProjectileViews"/>), each pointing along its x
/// axis: energy bolts (plasma, pulse, the wasp's light sting) as a swirling ball of light with a tail, in place of
/// the original's flares; and on a missile's model an exhaust flame flickering at its tail, a hot glow in its
/// nozzle and a trail of smoke.
/// </summary>
public static class RoundLooks
{
    public static readonly Color Plasma = new(0.2f, 1f, 0.3f), Pulse = new(0.3f, 0.55f, 1f), Sting = new(1f, 0.38f, 0.12f);

    /// <summary>The bolt a weapon fires, or null for one that fires something else.</summary>
    public static Node3D? Bolt(string weapon) => weapon switch
    {
        "PLASMA_RIFLE" => Bolt(Plasma, 1.3f, 5f),
        "PLASMA_CANNON1" => Bolt(Plasma, 2f, 7f),
        "PLASMA_CANNON2" => Bolt(Plasma, 2.5f, 8f),
        "PULSE_RIFLE" => Bolt(Pulse, 1.1f, 4.5f),
        "PULSE_CANNON" => Bolt(Pulse, 1.9f, 6.5f),
        "WASP_LIGHT_STING" => Bolt(Sting, 0.9f, 3.5f),
        _ => null,
    };

    /// <summary>A ball of light <paramref name="size"/> metres across with a tail <paramref name="tail"/> metres long behind it.</summary>
    private static Node3D Bolt(Color colour, float size, float tail)
    {
        var bolt = new Node3D();
        bolt.AddChild(FxShaders.NewOrb(colour, 2.6f, size));
        var streak = FxShaders.NewStreak(colour, 2.2f, size * 0.5f, tail, head: 1, taper: 0.3f, white: 0.3f);
        streak.Basis = Backwards;
        bolt.AddChild(streak);
        return bolt;
    }

    /// <summary>A child's basis whose +z runs back along the round's -x, so a streak trails behind it.</summary>
    private static readonly Basis Backwards = new(new Vector3(0, 0, 1), Vector3.Up, new Vector3(-1, 0, 0));

    /// <summary>A sting's tip glowing hot (the front of <paramref name="model"/>) and a faint streak behind it.</summary>
    public static void HotTip(Node3D view, Mesh model)
    {
        var bounds = model.GetAabb();
        var centre = bounds.GetCenter();
        var tip = FxShaders.NewOrb(Sting, 2.5f, 0.6f);
        tip.Position = new Vector3(bounds.End.X - 0.1f, centre.Y, centre.Z);
        view.AddChild(tip);
        var streak = FxShaders.NewStreak(Sting, 1.5f, 0.15f, 2.5f, head: 1, taper: 0.3f, white: 0.3f);
        streak.Basis = Backwards;
        streak.Position = new Vector3(bounds.Position.X, centre.Y, centre.Z);
        view.AddChild(streak);
    }

    /// <summary>
    /// A missile's exhaust at the tail of <paramref name="model"/> (the back of its bounds): a flickering flame as long
    /// as about five times its body is wide, a glow in its nozzle, and a smoke trail (named "Trail", which
    /// <see cref="ProjectileViews"/> leaves to drift when the missile is gone).
    /// </summary>
    public static void Exhaust(Node3D view, Mesh model)
    {
        var bounds = model.GetAabb();
        var centre = bounds.GetCenter();
        // The body's width: its fins make the bounds wider, so no more than a sixth of its length.
        var width = Mathf.Min(Mathf.Min(bounds.Size.Y, bounds.Size.Z), bounds.Size.X * 0.16f);
        var tail = new Vector3(bounds.Position.X, centre.Y, centre.Z);
        var flame = FxShaders.NewStreak(new Color(1, 0.6f, 0.15f), 4, width * 1.6f, width * 5, head: 1, taper: 0.3f, flicker: 0.35f);
        flame.Basis = Backwards;
        flame.Position = tail;
        view.AddChild(flame);
        var glow = FxShaders.NewOrb(new Color(1, 0.7f, 0.35f), 3, width * 2.2f);
        glow.Position = tail;
        view.AddChild(glow);
        var smoke = Mathf.Max(width * 7, 1.4f);
        var trail = new GpuParticles3D
        {
            Name = "Trail",
            Amount = bounds.Size.X > 5 ? 160 : 100,
            Lifetime = bounds.Size.X > 5 ? 2.2f : 1.3f,
            // A puff every metre or so at the missiles' 50 m/s, not one every frame of the default 30.
            FixedFps = 60,
            LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            DrawPass1 = Effects.Billboard(Effects.Particle("smoke_04"), additive: false),
            ProcessMaterial = new ParticleProcessMaterial
            {
                Spread = 180,
                InitialVelocityMin = 0,
                InitialVelocityMax = 0.5f,
                Gravity = new Vector3(0, 0.4f, 0),
                DampingMin = 0.5f,
                DampingMax = 1,
                ScaleMin = smoke * 0.7f,
                ScaleMax = smoke,
                ScaleCurve = Effects.Grow(0.8f),
                AngleMin = -180,
                AngleMax = 180,
                ColorRamp = Effects.Ramp(new Color(0.88f, 0.87f, 0.85f, 0.8f), new Color(0.72f, 0.71f, 0.7f, 0.5f), new Color(0.6f, 0.6f, 0.6f, 0)),
            },
        };
        trail.Position = tail - Vector3.Right * width * 2;
        view.AddChild(trail);
    }
}
