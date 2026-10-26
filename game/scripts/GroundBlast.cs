using Godot;
using Machines.Sim.Combat;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// The ground tearing open as the original drew it (MachPhysCrackFire): the Gorilla's punch sends cracks with a
/// wall of fire along six forking paths ahead of it (MachPhysPunchBlast), the bee bomb throws up rings of blue
/// fire one after another, wider and wider, under its spreading shock wave (MachPhysBeeBombExplosion and
/// MachPhysBeeBombWave). Each piece is a section of the exported chasm (<c>fx/punchcrack</c>, <c>punchfire</c>,
/// <c>beecrack</c>, <c>beefire</c>, <c>beewave</c>) stretched between two points on the ground. It frees itself
/// when it has burnt out.
/// </summary>
public partial class GroundBlast : Node3D
{
    private const float FrameRate = 25;

    private readonly List<Func<float, bool>> _parts = [];
    private float _age;

    /// <summary>The effect of a punch or bee bomb, or null for other blasts or without the exported effects.</summary>
    public static GroundBlast? Create(ClassicAssets assets, Blast blast, IReadOnlyList<float> beeExtras) => blast.Weapon switch
    {
        "GORILLA_PUNCH" => Punch(assets, blast),
        "BEE_BOMB" => BeeBomb(assets, blast, beeExtras),
        _ => null,
    };

    public override void _Process(double delta)
    {
        _age += (float)delta;
        var alive = false;
        foreach (var part in _parts)
            alive |= part(_age);
        if (!alive)
            QueueFree();
    }

    // MachPhysPunchBlast::allPoints: where the cracks run, ahead of the blast (x forward, y to the left), and in
    // z the frame at 25 a second when a crack reaching that point starts.
    private static readonly (float X, float Y, float Frame)[] PunchPoints =
    [
        (2.9f, 0.2f, 3), (8.6f, 0.9f, 3), (8.0f, -1.0f, 3), (8.1f, -3.1f, 3), (12.7f, 3.7f, 6), (12.9f, 0.5f, 6),
        (13.1f, -3.5f, 6), (18.2f, 4.2f, 9), (17.7f, -0.9f, 9), (17.1f, -6.3f, 9), (20.5f, 9.1f, 12), (22.6f, 0.4f, 12),
        (22.6f, -6.8f, 12), (25.0f, 12.4f, 16), (27.7f, -0.9f, 16), (28.4f, -6.8f, 16), (30.3f, 15.5f, 19),
        (29.4f, 8.0f, 19), (32.8f, 0.4f, 19), (33.9f, -6.8f, 19), (32.6f, -10.7f, 19), (36.4f, 4.9f, 22),
        (38.0f, -1.0f, 22), (38.9f, -9.6f, 22), (34.7f, -15.9f, 22), (36.0f, 15.0f, 26), (37.9f, 10.5f, 26),
        (43.6f, 0.5f, 26), (44.8f, -9.6f, 26), (39.0f, -19.5f, 26), (42.8f, 13.4f, 29),
    ];

    // path1 to path6: the points each crack runs through, in turn.
    private static readonly int[][] PunchPaths =
    [
        [0, 1, 4, 7, 10, 13, 16, 25], [13, 17], [18, 21, 26, 30], [0, 2, 5, 8, 11, 14, 18, 22, 27],
        [0, 3, 6, 9, 12, 15, 19, 23, 28], [15, 20, 24, 29],
    ];

    private static GroundBlast? Punch(ClassicAssets assets, Blast blast)
    {
        if (assets.EffectMesh("punchcrack") is not { } crack || assets.EffectMesh("punchfire") is not { } fire)
            return null;
        var view = new GroundBlast { Name = "Punch" };
        // The blast stands 2.9 m ahead of the Gorilla and a little to its left (MachPhysPunchWeapon).
        var forward = blast.Direction.LengthSquared() > 0.01f ? NVector2.Normalize(blast.Direction) : NVector2.UnitX;
        var left = new NVector2(-forward.Y, forward.X);
        var origin = blast.Position + forward * 2.9f + left * 0.2f;
        NVector2 At((float X, float Y, float) p) => origin + forward * p.X + left * p.Y;
        foreach (var path in PunchPaths)
        {
            for (var i = 0; i + 1 < path.Length; i++)
            {
                var (from, to) = (PunchPoints[path[i]], PunchPoints[path[i + 1]]);
                // A crack opens from nothing to 7.5 times its width in 35 frames, fading after 10; its fire leaps
                // to 9 times its height in 10 frames and sinks back in 10 more, fading as it sinks.
                view.Section(crack, fire, At(from), At(to), to.Frame / FrameRate,
                    crackTime: 35 / FrameRate, crackFade: 10 / FrameRate, crackWidth: (0.01f, 7.5f),
                    fireTime: 20 / FrameRate, fireFade: 10 / FrameRate, fireHeight: 9, fireFalls: true);
            }
        }
        // A white flash at the fists for 0.8 s.
        view.Flash(ActorView.World(origin) + Vector3.Up * 2, new Color(1, 1, 1), 3, 25, 0, 0.8f);
        return view;
    }

    private static GroundBlast? BeeBomb(ClassicAssets assets, Blast blast, IReadOnlyList<float> extras)
    {
        if (assets.EffectMesh("beecrack") is not { } crack || assets.EffectMesh("beefire") is not { } fire)
            return null;
        float Extra(int i, float fallback) => i < extras.Count ? extras[i] : fallback;
        var view = new GroundBlast { Name = "BeeBomb" };
        var light = new Color(Extra(0, 0), Extra(1, 0), Extra(2, 2));
        // EXTRAS: the light's colour, then for each of the six rings its size, fire height, start and duration.
        for (var ring = 0; ring < 6; ring++)
        {
            var size = Extra(3 + ring, 0.1f + 0.4f * ring);
            var height = Extra(9 + ring, 50);
            var start = 0.25f + Extra(15 + ring, ring == 0 ? 0 : 1.5f + 0.5f * ring);
            var duration = Extra(21 + ring, ring == 0 ? 2 : 0.4f);
            var points = RingPath(size).Select(p => blast.Position + p).ToList();
            for (var i = 0; i + 1 < points.Count; i++)
            {
                // The ring's crack keeps its width and fades after a third of its time; its fire shoots up over
                // two thirds of it, fading from the first third.
                view.Section(crack, fire, points[i], points[i + 1], start,
                    crackTime: duration, crackFade: duration / 3, crackWidth: (1, 1),
                    fireTime: duration * 2 / 3, fireFade: duration / 3, fireHeight: height, fireFalls: false);
            }
            view.Flash(ActorView.World(blast.Position) + Vector3.Up * 3, light, 4, 15 * size + 20, start, 0.12f);
        }
        if (assets.EffectMesh("beewave") is { } wave)
            view.Wave(wave, ActorView.World(blast.Position));
        return view;
    }

    /// <summary>beebombe.cpp createRingPath: eighteen sections round a circle 15 m across at size 1.</summary>
    private static List<NVector2> RingPath(float size)
    {
        (float X, float Y)[] unit =
        [
            (0, 15), (5.13f, 14.1f), (9.64f, 11.49f), (12.99f, 7.5f), (14.77f, 2.6f), (14.77f, -2.6f), (12.99f, -7.5f),
            (9.64f, -11.49f), (5.13f, -14.1f), (0, -15), (-5.13f, -14.1f), (-9.64f, -11.49f), (-12.99f, -7.5f),
            (-14.77f, -2.6f), (-14.77f, 2.6f), (-12.99f, 7.5f), (-9.64f, 11.49f), (-5.13f, 14.49f), (0, 15),
        ];
        return unit.Select(p => new NVector2(p.X, p.Y) * size).ToList();
    }

    /// <summary>
    /// One chasm section between two points on the ground (MachPhysCrackFire::startCrackAndFire): its crack lies
    /// flat and widens, its fire stands up along it; both fade to nothing.
    /// </summary>
    private void Section(Mesh crackMesh, Mesh fireMesh, NVector2 from, NVector2 to, float start,
        float crackTime, float crackFade, (float From, float To) crackWidth,
        float fireTime, float fireFade, float fireHeight, bool fireFalls)
    {
        var a = ActorView.World(from) + Vector3.Up * 0.15f;
        var b = ActorView.World(to) + Vector3.Up * 0.15f;
        if (a.DistanceSquaredTo(b) < 0.01f)
            return;
        // The sections are 2 m long as exported: along the line, scaled to its length.
        var along = (b - a) / 2;
        var across = along.Cross(Vector3.Up).Normalized();
        var (crack, crackMaterial) = Piece(crackMesh);
        var (fire, fireMaterial) = Piece(fireMesh);

        _parts.Add(age =>
        {
            var t = age - start;
            crack.Visible = t >= 0 && t < crackTime;
            fire.Visible = t >= 0 && t < fireTime;
            if (!crack.Visible && !fire.Visible)
                return t < 0;
            if (crack.Visible)
            {
                var width = Mathf.Lerp(crackWidth.From, crackWidth.To, t / crackTime);
                crack.Transform = new Transform3D(new Basis(along, Vector3.Up, across * width), a);
                Fade(crackMaterial, Fading(t, crackFade, crackTime));
            }
            if (fire.Visible)
            {
                // From a tenth of its height up to fireHeight, and for the punch straight back down again.
                var rise = fireFalls ? fireFade : fireTime;
                var height = t < rise
                    ? Mathf.Lerp(0.1f, fireHeight, t / rise)
                    : Mathf.Lerp(fireHeight, 0.1f, (t - rise) / Mathf.Max(0.01f, fireTime - rise));
                fire.Transform = new Transform3D(new Basis(along, Vector3.Up * height, across), a);
                Fade(fireMaterial, Fading(t, fireFade, fireTime));
            }
            return true;
        });
    }

    /// <summary>The bee bomb's shock wave: from a tenth to 2.2 times its size between 1.5 s and 4 s, showing from 1.5 s to 4 s.</summary>
    private void Wave(Mesh mesh, Vector3 at)
    {
        var (wave, material) = Piece(mesh);
        // Half clear at its strongest (its material's alpha), faded in and out by the original's alpha plan.
        var clear = material.AlbedoColor.A;
        _parts.Add(age =>
        {
            wave.Visible = age is > 1.5f and < 4f;
            if (!wave.Visible)
                return age < 4;
            var scale = Mathf.Lerp(0.1f, 2.2f, Mathf.Clamp((age - 1.5f) / 2.5f, 0, 1));
            wave.Transform = new Transform3D(Basis.Identity.Scaled(new Vector3(scale, 1, scale)), at);
            var alpha = age < 2 ? (age - 1.5f) / 0.5f : age < 3.5f ? 1 : (4 - age) / 0.5f;
            Fade(material, alpha, clear);
            return true;
        });
    }

    /// <summary>A light that is on from <paramref name="start"/> for <paramref name="time"/> seconds.</summary>
    private void Flash(Vector3 at, Color colour, float energy, float range, float start, float time)
    {
        // The original's colours go above 1 (the bee bomb's blue is 0, 0, 2): that much more energy.
        var strongest = Mathf.Max(1, Mathf.Max(colour.R, Mathf.Max(colour.G, colour.B)));
        var light = new OmniLight3D
        {
            LightColor = new Color(colour.R / strongest, colour.G / strongest, colour.B / strongest),
            LightEnergy = energy * strongest,
            OmniRange = range,
            Visible = false,
        };
        AddChild(light);
        light.Position = at;
        _parts.Add(age =>
        {
            light.Visible = age >= start && age < start + time;
            return age < start + time;
        });
    }

    private (MeshInstance3D View, StandardMaterial3D Material) Piece(Mesh mesh)
    {
        // Each piece fades on its own, so it has its own copy of the material.
        var material = mesh.SurfaceGetMaterial(0) is StandardMaterial3D shared
            ? (StandardMaterial3D)shared.Duplicate()
            : new StandardMaterial3D();
        material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        // Unlit as in the original (doNotLight): the texture in the emissive colour where it glows (the fire),
        // else in the plain colour (the crack, black).
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        if (material.EmissionEnabled)
        {
            material.AlbedoColor = new Color(material.Emission, material.AlbedoColor.A);
            material.EmissionEnabled = false;
        }
        var view = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(view);
        return (view, material);
    }

    /// <summary>Fully there until <paramref name="from"/>, then linearly gone by <paramref name="until"/> (PhysLinearScalarPlan).</summary>
    private static float Fading(float t, float from, float until) =>
        t <= from ? 1 : Mathf.Clamp(1 - (t - from) / Mathf.Max(0.01f, until - from), 0, 1);

    private static void Fade(StandardMaterial3D material, float alpha, float clear = 1) =>
        material.AlbedoColor = material.AlbedoColor with { A = clear * alpha };
}
