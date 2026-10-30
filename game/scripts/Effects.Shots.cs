using Godot;

namespace Machines.Game;

/// <summary>
/// The shots: in the remake's effects a flash at the muzzle and, for the weapons that strike at once (bolters, the
/// autocannon), a tracer racing to the mark and sparks or dust where it lands; the flamethrower throws a stream of
/// flame. In the classic look a glow at the muzzle and a glowing bar.
/// </summary>
public partial class Effects
{
    /// <summary>A round on its way; <see cref="Impact"/> marks the one that struck the ground.</summary>
    private sealed class Round
    {
        public bool Ground;
    }

    // The last round sent off: Game reports a bolt striking the ground as its shot and then an impact at its mark.
    private (Vector3 To, Round Round)? _lastRound;

    /// <summary>
    /// A shot from <paramref name="from"/> to <paramref name="to"/> at <paramref name="speed"/> m/s, or only its
    /// muzzle flash when the two are the same (rounds that fly are drawn by <see cref="ProjectileViews"/>).
    /// </summary>
    public void Shot(Vector3 from, Vector3 to, string weapon, float speed)
    {
        if (!GraphicsSettings.Effects)
        {
            ClassicShot(from, to, weapon, speed);
            return;
        }
        var towards = to - from;
        var direction = towards.Length() < 0.5f ? (Vector3?)null : towards.Normalized();
        if (weapon.StartsWith("FLAME_THROWER", StringComparison.Ordinal))
        {
            FlameJet(from, to, weapon);
            return;
        }
        MuzzleFlash(from, direction, weapon);
        if (direction is not null)
            Tracer(from, to, weapon, speed);
    }

    /// <summary>The classic look's shot: a glow at the muzzle and a glowing bar flying to the mark.</summary>
    private void ClassicShot(Vector3 from, Vector3 to, string weapon, float speed)
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
            ClassicSparks(to, colour, 10);
            bolt.QueueFree();
        }));
    }

    private void ClassicSparks(Vector3 at, Color colour, int amount) =>
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

    /// <summary>How big a weapon's muzzle flash is: the bolter's small (bolter.cpp's 0.8), the heavy bolter's large (1.5).</summary>
    private static float FlashSize(string weapon) => weapon switch
    {
        "BOLTER" => 0.8f,
        "AUTO_CANNON" => 1.2f,
        _ when weapon.StartsWith("HEAVY_BOLTER", StringComparison.Ordinal) => 1.5f,
        _ when weapon.StartsWith("MULTI_LAUNCHER", StringComparison.Ordinal) || weapon == "LARGE_MISSILE" => 1.3f,
        _ when weapon.Contains("CANNON") => 1.4f,
        _ => 1f,
    };

    /// <summary>
    /// The flash as a weapon fires: a star of light turned at random, a tongue of fire along the barrel when its
    /// direction is known, a flash of light on what is round it, and smoke from the guns that smoke. Energy weapons
    /// flash in their colour; launchers puff smoke as their missile leaves.
    /// </summary>
    private void MuzzleFlash(Vector3 at, Vector3? direction, string weapon)
    {
        var colour = WeaponColour(weapon);
        var size = FlashSize(weapon);
        var energy = weapon.Contains("PLASMA") || weapon.Contains("PULSE") || weapon.Contains("STING");
        var star = energy ? FxShaders.NewOrb(colour, 4, 1.4f * size) : FxShaders.NewFlash(colour, 5, 1.6f * size, (float)GD.RandRange(4, 6));
        AddChild(star);
        star.GlobalPosition = at;
        Fade(star, 0.07f, grow: 1.3f);

        if (direction is { } along && !energy)
        {
            // The tongue: from the muzzle forward, bright at the muzzle; the streak runs back along its +z.
            var tongue = FxShaders.NewStreak(colour, 4, 0.55f * size, 1.1f * size, head: 1, taper: 0.25f);
            AddChild(tongue);
            tongue.GlobalPosition = at;
            tongue.LookAt(at - along, Mathf.Abs(along.Y) > 0.98f ? Vector3.Right : Vector3.Up);
            Fade(tongue, 0.06f);
        }
        Light(at, colour, 2.5f, 8, 0.08f);
        if (Smokes(weapon))
            FiringSmoke(at);
        else if (weapon.StartsWith("MULTI_LAUNCHER", StringComparison.Ordinal) || weapon == "LARGE_MISSILE")
            LaunchSmoke(at);
    }

    /// <summary>A missile leaves its launcher in a puff of smoke.</summary>
    private void LaunchSmoke(Vector3 at) =>
        Burst(at, amount: 4, lifetime: 1.4f, texture: Particle("smoke_04"), new ParticleProcessMaterial
        {
            Spread = 180,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 1.5f,
            Gravity = new Vector3(0, 0.5f, 0),
            DampingMin = 1,
            DampingMax = 2,
            ScaleMin = 1.2f,
            ScaleMax = 2f,
            ScaleCurve = Grow(0.4f),
            AngleMin = -180,
            AngleMax = 180,
            ColorRamp = Ramp(new Color(0.85f, 0.84f, 0.82f, 0), new Color(0.8f, 0.8f, 0.78f, 0.5f), new Color(0.7f, 0.7f, 0.7f, 0)),
        }, additive: false);

    /// <summary>
    /// A tracer: a streak of light that leaves the muzzle, races to the mark at the weapon's speed (seen for at least
    /// a few frames) and draws its tail in after it, the heavier guns' thicker. Where it lands, sparks off metal, or
    /// dust and a small burn on the ground.
    /// </summary>
    private void Tracer(Vector3 from, Vector3 to, string weapon, float speed)
    {
        var colour = WeaponColour(weapon);
        var round = new Round();
        _lastRound = (to, round);
        // Game aims at the middle of the machine; the tracer stops where it meets its hull.
        var mark = Hull(from, to) ?? to;
        var distance = from.DistanceTo(mark);
        if (distance < 0.5f)
            return;
        var direction = (mark - from) / distance;
        var longest = Mathf.Min(distance * 0.6f, 7f);
        var flight = Mathf.Clamp(distance / Mathf.Max(speed, 1), 0.05f, 0.6f);
        var tracer = FxShaders.NewStreak(colour, 5, 0.22f * FlashSize(weapon), longest, head: 1, taper: 0.4f);
        AddChild(tracer);
        tracer.GlobalPosition = from;
        tracer.LookAt(mark, Mathf.Abs(direction.Y) > 0.98f ? Vector3.Right : Vector3.Up);
        var landed = false;
        var tween = tracer.CreateTween();
        // How far the head has gone: on to the mark, then the tail catches up with it there.
        tween.TweenMethod(Callable.From<float>(gone =>
        {
            var head = Mathf.Min(gone, distance);
            tracer.GlobalPosition = from + direction * head;
            tracer.SetInstanceShaderParameter("reach", Mathf.Max(0.01f, head - Mathf.Max(0, gone - longest)));
            if (gone < distance || landed)
                return;
            landed = true;
            if (round.Ground)
                GroundHit(mark, direction);
            else
                MetalHit(mark, direction);
        }), 0f, distance + longest, flight * (distance + longest) / distance);
        tween.TweenCallback(Callable.From(tracer.QueueFree));
    }

    /// <summary>
    /// Where a shot from <paramref name="from"/> to <paramref name="to"/> first meets the bounds of a machine or
    /// building round its mark (the actor views beside the effects), or null when it meets none.
    /// </summary>
    private Vector3? Hull(Vector3 from, Vector3 to)
    {
        Vector3? nearest = null;
        foreach (var node in GetParent().GetChildren())
        {
            if (node is not ActorView view || view.GlobalPosition.DistanceSquaredTo(to) > 400)
                continue;
            var local = view.GlobalTransform.AffineInverse();
            // Not the shooter it leaves.
            if (view.Bounds.HasPoint(local * from))
                continue;
            var (start, end) = (local * from, local * to);
            if (Entry(view.Bounds, start, end) is { } entry
                && (nearest is null || from.DistanceSquaredTo(view.GlobalTransform * entry) < from.DistanceSquaredTo(nearest.Value)))
                nearest = view.GlobalTransform * entry;
        }
        return nearest;
    }

    /// <summary>Where the line from <paramref name="start"/> to <paramref name="end"/> enters the box, or null if it misses it.</summary>
    private static Vector3? Entry(Aabb box, Vector3 start, Vector3 end)
    {
        var along = end - start;
        float near = 0, far = 1;
        for (var axis = 0; axis < 3; axis++)
        {
            var (s, v, low, high) = (start[axis], along[axis], box.Position[axis], box.End[axis]);
            if (Mathf.Abs(v) < 1e-6f)
            {
                if (s < low || s > high)
                    return null;
                continue;
            }
            var (t0, t1) = ((low - s) / v, (high - s) / v);
            near = Mathf.Max(near, Mathf.Min(t0, t1));
            far = Mathf.Min(far, Mathf.Max(t0, t1));
            if (near > far)
                return null;
        }
        return start + along * near;
    }

    /// <summary>
    /// The flamethrower's stream (flamethr.cpp): balls of flame thrown from the nozzle that swell as they fly, sink a
    /// little and burn out at the mark, leaving smoke there; where they reach the ground a small fire and a burn.
    /// </summary>
    private void FlameJet(Vector3 from, Vector3 to, string weapon)
    {
        var towards = to - from;
        var distance = Mathf.Max(towards.Length(), 1);
        var heavy = weapon == "FLAME_THROWER2" ? 1.3f : 1f;
        const float life = 0.5f;
        Light(from, new Color(1, 0.55f, 0.2f), 3, 10 * heavy, 0.4f);
        // A stream of balls of fire (flamebal.cpp) swelling as they fly, glowing where they crowd: only the
        // flipbook's burning half, the smoke comes after.
        Burst(from, amount: 30, lifetime: life, Flipbook("fireball", additive: true), new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 0, -1),
            Spread = 4,
            InitialVelocityMin = distance / life * 0.85f,
            InitialVelocityMax = distance / life * 1.05f,
            Gravity = new Vector3(0, -4, 0),
            ScaleMin = 3.2f * heavy,
            ScaleMax = 4.4f * heavy,
            ScaleCurve = Grow(0.25f),
            AngleMin = -180,
            AngleMax = 180,
            AnimSpeedMin = 0.5f,
            AnimSpeedMax = 0.55f,
            ColorRamp = Ramp(new Color(1.7f, 1.4f, 1.1f, 1), new Color(1.4f, 1.1f, 0.85f, 1), new Color(0.9f, 0.6f, 0.4f, 0), hold: 0.6f),
        }, explosiveness: 0, facing: towards);
        // The smoke it leaves where it ends.
        Burst(from + towards * 0.75f, amount: 6, lifetime: 1.2f, Flipbook("fireball"), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.6f,
            Direction = Vector3.Up,
            Spread = 30,
            InitialVelocityMin = 0.6f,
            InitialVelocityMax = 1.4f,
            Gravity = Vector3.Zero,
            ScaleMin = 3.5f * heavy,
            ScaleMax = 5f * heavy,
            ScaleCurve = Grow(0.6f),
            AngleMin = -180,
            AngleMax = 180,
            AnimOffsetMin = 0.5f,
            AnimOffsetMax = 0.6f,
            AnimSpeedMin = 0.4f,
            AnimSpeedMax = 0.4f,
            ColorRamp = Ramp(new Color(1, 1, 1, 0), new Color(1, 1, 1, 0.7f), new Color(1, 1, 1, 0)),
        }, explosiveness: 0.2f);
        var round = new Round();
        _lastRound = (to, round);
        GetTree().CreateTimer(life * 0.9f).Timeout += () =>
        {
            if (round.Ground)
            {
                Burst(to, amount: 3, lifetime: 0.8f, Flipbook("flame", tall: true), new ParticleProcessMaterial
                {
                    EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                    EmissionSphereRadius = 0.8f,
                    Direction = Vector3.Up,
                    Spread = 15,
                    InitialVelocityMin = 0.5f,
                    InitialVelocityMax = 1.5f,
                    Gravity = Vector3.Zero,
                    ScaleMin = 1.5f,
                    ScaleMax = 2.4f,
                    AnimSpeedMin = 1,
                    AnimSpeedMax = 1,
                    ColorRamp = Ramp(new Color(1.8f, 1.7f, 1.6f, 1), new Color(1.4f, 1.3f, 1.2f, 1), new Color(1, 1, 1, 0)),
                });
                Scorch(to, 2f);
            }
        };
    }

    /// <summary>Fades out a shader effect (<c>fade</c>) over <paramref name="duration"/>, growing it, then frees it.</summary>
    private static void Fade(Node3D effect, float duration, float grow = 1)
    {
        var tween = effect.CreateTween().SetParallel();
        tween.TweenMethod(Callable.From<float>(f => ((GeometryInstance3D)effect).SetInstanceShaderParameter("fade", f)), 1f, 0f, duration);
        if (grow != 1)
            tween.TweenProperty(effect, "scale", effect.Scale * grow, duration);
        tween.Chain().TweenCallback(Callable.From(effect.QueueFree));
    }
}
