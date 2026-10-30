using Godot;

namespace Machines.Game;

/// <summary>
/// The support effects as the remake's effects draw them: the healing beam's helices as two bands of light winding
/// about a pulsing core, the electric charger's lightning as a forked bolt that jumps to a new path twenty times a
/// second, the flames of a burning machine from the rendered fire, and the loading rings as rings of light on the
/// ground. Timings and sizes stay the original's.
/// </summary>
public partial class SupportEffects
{
    private static readonly Color Healing = new(0.25f, 1f, 0.35f), Electric = new(0.45f, 0.75f, 1f);

    /// <summary>
    /// The healing beam: two helices of light, 0.35 m about the beam and two and a half turns along the original's
    /// 5 m (stretched to reach the patient like the original's), and a pulsing core between them.
    /// </summary>
    private Beam NewBand()
    {
        var root = new Node3D { Name = "HealBeam" };
        var turns = new MeshInstance3D[2];
        for (var i = 0; i < 2; i++)
        {
            var points = new List<Vector3>();
            for (var k = 0; k <= 80; k++)
            {
                var x = 5f * k / 80;
                var angle = Mathf.Tau * 2.5f * x / 5;
                points.Add(new Vector3(x, 0.35f * Mathf.Cos(angle), 0.35f * Mathf.Sin(angle)));
            }
            turns[i] = FxShaders.NewRibbon(points, Healing, 2.5f, 0.16f, flow: 14);
            root.AddChild(turns[i]);
        }
        root.AddChild(FxShaders.NewRibbon([Vector3.Zero, new Vector3(2.5f, 0, 0), new Vector3(5, 0, 0)], Healing, 1.4f, 0.3f, flow: -9));
        root.AddChild(new OmniLight3D { LightColor = new Color(0.3f, 1, 0.4f), LightEnergy = 1.5f, OmniRange = 8 });
        AddChild(root);
        return new Beam { Root = root, Left = turns[0], Right = turns[1] };
    }

    /// <summary>
    /// The electric charger's lightning for the original's half second: a jagged bolt from the muzzle to the target
    /// with a fork or two, drawn anew along another path every twentieth of a second, and a flash of light.
    /// </summary>
    private void Bolt(Vector3 from, Vector3 to)
    {
        if (from.DistanceTo(to) < 0.5f)
            return;
        var root = new Node3D { Name = "Lightning" };
        AddChild(root);
        root.GlobalTransform = Transform3D.Identity;
        var light = new OmniLight3D { LightColor = new Color(0.32f, 0.86f, 1f), LightEnergy = 4, OmniRange = 12 };
        root.AddChild(light);
        light.GlobalPosition = (from + to) / 2;
        var shown = -1;
        _timed.Add(new Timed
        {
            Node = root,
            Duration = 0.5f,
            Update = age =>
            {
                var frame = (int)(age * 20);
                if (frame == shown)
                    return;
                shown = frame;
                foreach (var old in root.GetChildren().OfType<MeshInstance3D>())
                    old.QueueFree();
                var main = Jagged(from, to, 14, 0.12f);
                root.AddChild(FxShaders.NewRibbon(main, Electric, 4, 0.6f));
                for (var fork = 0; fork < 2; fork++)
                {
                    var start = main[GD.RandRange(3, main.Count - 4)];
                    var reach = from.DistanceTo(to) * (float)GD.RandRange(0.15, 0.3);
                    var off = (to - from).Normalized() + new Vector3((float)GD.RandRange(-1, 1), (float)GD.RandRange(-0.6, 0.6), (float)GD.RandRange(-1, 1));
                    root.AddChild(FxShaders.NewRibbon(Jagged(start, start + off.Normalized() * reach, 6, 0.18f), Electric, 3, 0.3f));
                }
                light.LightEnergy = 3 + (float)GD.RandRange(0, 2);            },
        });
    }

    /// <summary>A line from one point to another in <paramref name="steps"/>, each point thrown aside by up to <paramref name="rough"/> of its length, least at the ends.</summary>
    private static List<Vector3> Jagged(Vector3 from, Vector3 to, int steps, float rough)
    {
        var length = from.DistanceTo(to);
        var along = (to - from) / length;
        var side = along.Cross(Mathf.Abs(along.Y) > 0.98f ? Vector3.Right : Vector3.Up).Normalized();
        var up = side.Cross(along);
        var points = new List<Vector3>();
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            var swing = Mathf.Sin(t * Mathf.Pi) * rough * length;
            points.Add(from.Lerp(to, t) + side * (float)GD.RandRange(-swing, swing) + up * (float)GD.RandRange(-swing, swing) * 0.6f);
        }
        return points;
    }

    /// <summary>
    /// A machine set alight burns for <paramref name="seconds"/>: tongues of the rendered fire licking up at three
    /// spots at three quarters of its height, trailing behind it as it moves, and a flickering light.
    /// </summary>
    private void Fire(ActorView victim, float seconds)
    {
        var bounds = victim.Bounds;
        var size = Mathf.Clamp(Mathf.Min(bounds.Size.X, bounds.Size.Z) * 0.6f, 0.8f, 4);
        var root = new Node3D { Name = "Burning" };
        victim.AddChild(root);
        var centre = bounds.GetCenter();
        Vector3[] spots = [new(0, 0, 0), new(bounds.Size.X * 0.25f, -0.1f * bounds.Size.Y, bounds.Size.Z * 0.2f), new(-bounds.Size.X * 0.25f, -0.15f * bounds.Size.Y, -bounds.Size.Z * 0.2f)];
        foreach (var spot in spots)
        {
            var flames = new GpuParticles3D
            {
                Amount = 8,
                Lifetime = 0.8f,
                LocalCoords = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                DrawPass1 = Effects.Flipbook("flame", tall: true),
                ProcessMaterial = new ParticleProcessMaterial
                {
                    EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
                    EmissionSphereRadius = size * 0.2f,
                    Direction = Vector3.Up,
                    Spread = 10,
                    InitialVelocityMin = 1,
                    InitialVelocityMax = 2,
                    Gravity = Vector3.Zero,
                    ScaleMin = size * 0.6f,
                    ScaleMax = size * 0.9f,
                    AnimSpeedMin = 1,
                    AnimSpeedMax = 1,
                    ColorRamp = Effects.Ramp(new Color(1.8f, 1.7f, 1.6f, 0.9f), new Color(1.4f, 1.3f, 1.2f, 1), new Color(1, 1, 1, 0)),
                },
            };
            root.AddChild(flames);
            flames.Position = new Vector3(centre.X, bounds.Position.Y + bounds.Size.Y * 0.75f, centre.Z) + spot;
        }
        var light = new OmniLight3D { LightColor = new Color(1, 0.55f, 0.2f), LightEnergy = 2, OmniRange = size * 5 };
        root.AddChild(light);
        light.Position = new Vector3(centre.X, bounds.End.Y + 0.5f, centre.Z);
        var timed = new Timed
        {
            Node = root,
            Duration = seconds,
            Update = age => light.LightEnergy = 1.6f + 0.6f * Mathf.Sin(age * 23) * Mathf.Sin(age * 7.3f),
        };
        _timed.Add(timed);
        _burning[victim] = timed;
    }

    /// <summary>
    /// A transporter loads or unloads ore, on the original's timing (22 frames a second): two rings of light on the
    /// ground round it, 1.05 times its size, grow to twice that over a second and fade after their first two
    /// frames; a disc of light shrinks to half over 12 frames; a light shines for 10.
    /// </summary>
    private void Rings(ActorView carrier)
    {
        const float frame = 1 / 22f;
        var bounds = carrier.Bounds;
        var radius = 1.05f * Mathf.Max(bounds.Size.X, bounds.Size.Z) / 2;
        var root = new Node3D { Name = "Loading", Position = new Vector3(bounds.GetCenter().X, bounds.Position.Y + 0.15f, bounds.GetCenter().Z) };
        carrier.AddChild(root);
        StandardMaterial3D Lit(Texture2D texture) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = texture,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        MeshInstance3D Flat(StandardMaterial3D material, float height)
        {
            var view = new MeshInstance3D { Mesh = new PlaneMesh { Size = Vector2.One * 2 }, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            root.AddChild(view);
            view.Position = Vector3.Up * height;
            return view;
        }
        var ringLook = Lit(Effects.Ring);
        var discLook = Lit(Effects.Soft);
        var rings = new[] { Flat(ringLook, 0), Flat(ringLook, bounds.Size.Y * 0.6f) };
        var disc = Flat(discLook, 0.05f);
        var lamp = new OmniLight3D { LightColor = new Color(0.9f, 1f, 0.9f), LightEnergy = 3, OmniRange = 20, Position = Vector3.Up };
        root.AddChild(lamp);
        _timed.Add(new Timed
        {
            Node = root,
            Duration = 22 * frame,
            Update = age =>
            {
                var t = age / (22 * frame);
                var fade = age < 2 * frame ? 1 : 1 - (age - 2 * frame) / (20 * frame);
                foreach (var ring in rings)
                    ring.Scale = Vector3.One * radius * Mathf.Lerp(1, 2, t);
                ringLook.AlbedoColor = new Color(0.7f, 1.6f, 0.9f, Mathf.Clamp(fade, 0, 1));
                var lightT = Mathf.Clamp(age / (12 * frame), 0, 1);
                disc.Scale = Vector3.One * radius * Mathf.Lerp(1, 0.5f, lightT);
                disc.Visible = lightT < 1;
                discLook.AlbedoColor = new Color(0.8f, 1.4f, 0.9f, 0.6f * (age < 2 * frame ? 1 : Mathf.Clamp(1 - (age - 2 * frame) / (10 * frame), 0, 1)));
                lamp.Visible = age < 10 * frame;
            },
        });
    }
}
