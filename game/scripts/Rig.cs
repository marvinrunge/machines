using System.Text.Json;
using Godot;

namespace Machines.Game;

/// <summary>
/// A machine as the original animates it (exported by tools/classic-export): a tree of parts, each with its rest
/// transform and mesh; the part that turns the upper body; and its locomotion: spider-leg cycles sampled from
/// the original plans, wheels with their radius, or tracks with their texture speed.
/// </summary>
public sealed class Rig
{
    public required RigNode[] Nodes { get; init; }
    /// <summary>The part that turns the weapons towards a target, or -1.</summary>
    public int UpperBody { get; init; } = -1;
    public Dictionary<string, RigAnimation> Animations { get; init; } = [];
    public RigWheels? Wheels { get; init; }
    public RigTracks? Tracks { get; init; }

    /// <summary>What parts do by themselves all the time: rotors spin, hips bob, a weapon's ball turns.</summary>
    public RigIdle[] Idle { get; init; } = [];

    /// <summary>A building's parts while it works (MachPhysConstruction::doWorking): fans turn, arms dig.</summary>
    public RigIdle[] Working { get; init; } = [];

    /// <summary>The faceplate part and the point on it first person looks out from (centre front), or -1.</summary>
    public int Faceplate { get; init; } = -1;
    public Vector3 Eye { get; init; }

    public static Rig Read(JsonElement rig, Func<JsonElement, Mesh?> mesh)
    {
        var nodes = rig.GetProperty("nodes").EnumerateArray()
            .Select(n => new RigNode(n.GetProperty("parent").GetInt32(), Transform(n.GetProperty("transform")), mesh(n)))
            .ToArray();
        var animations = new Dictionary<string, RigAnimation>();
        if (rig.TryGetProperty("animations", out var anims))
        {
            foreach (var anim in anims.EnumerateObject())
            {
                var tracks = anim.Value.GetProperty("tracks").EnumerateArray()
                    .Select(t => new RigTrack(t.GetProperty("node").GetInt32(), t.GetProperty("samples").EnumerateArray().Select(Transform).ToArray()))
                    .ToArray();
                animations[anim.Name] = new RigAnimation(anim.Value.GetProperty("duration").GetSingle(), tracks);
            }
        }
        RigWheels? wheels = null;
        if (rig.TryGetProperty("wheels", out var w))
            wheels = new RigWheels(w.GetProperty("radius").GetSingle(),
                w.GetProperty("left").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                w.GetProperty("right").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        RigTracks? tracksOf = null;
        if (rig.TryGetProperty("tracks", out var t2))
            tracksOf = new RigTracks(t2.GetProperty("repeatsPerMeter").GetSingle(), t2.GetProperty("left").GetInt32(), t2.GetProperty("right").GetInt32());
        return new Rig
        {
            Nodes = nodes,
            UpperBody = rig.GetProperty("upperBody").GetInt32(),
            Animations = animations,
            Wheels = wheels,
            Tracks = tracksOf,
            Idle = Motion(rig, "idle"),
            Working = Motion(rig, "working"),
            Faceplate = rig.TryGetProperty("faceplate", out var f) ? f.GetInt32() : -1,
            Eye = rig.TryGetProperty("eye", out var e) && e.EnumerateArray().Select(x => x.GetSingle()).ToArray() is [var ex, var ey, var ez]
                ? new Vector3(ex, ez, ey)   // original z up
                : Vector3.Zero,
        };
    }

    private static RigIdle[] Motion(JsonElement rig, string name) => rig.TryGetProperty(name, out var motion)
        ? motion.EnumerateArray().Select(t => new RigIdle(t.GetProperty("duration").GetSingle(),
            new RigTrack(t.GetProperty("node").GetInt32(), t.GetProperty("samples").EnumerateArray().Select(Transform).ToArray()))).ToArray()
        : [];

    /// <summary>
    /// An original transform (x, y and z basis, then position; z up) in Godot's axes (y up). The models swap y
    /// and z (see ClassicAssets), so a transform M becomes C M C with C the swap.
    /// </summary>
    private static Transform3D Transform(JsonElement e)
    {
        var v = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        static Vector3 Swap(float x, float y, float z) => new(x, z, y);
        var xb = Swap(v[0], v[1], v[2]);
        var yb = Swap(v[3], v[4], v[5]);
        var zb = Swap(v[6], v[7], v[8]);
        // C B C: the columns are C x, C z, C y.
        return new Transform3D(new Basis(xb, zb, yb), Swap(v[9], v[10], v[11]));
    }
}

public sealed record RigNode(int Parent, Transform3D Rest, Mesh? Mesh);

public sealed record RigTrack(int Node, Transform3D[] Samples)
{
    /// <summary>The part's transform at a point of the cycle (0 to 1), between the two nearest samples.</summary>
    public Transform3D At(float phase)
    {
        var position = Mathf.Clamp(phase, 0, 1) * (Samples.Length - 1);
        var i = Math.Min((int)position, Samples.Length - 2);
        return Samples[i].InterpolateWith(Samples[i + 1], position - i);
    }
}

public sealed record RigAnimation(float Duration, RigTrack[] Tracks);

/// <summary>A part's endless motion of its own, one period long.</summary>
public sealed record RigIdle(float Duration, RigTrack Track);

/// <summary>Wheels spin about their axle (the original's local y axis) by distance over radius.</summary>
public sealed record RigWheels(float Radius, int[] Left, int[] Right);

/// <summary>Track treads scroll their texture at the machine's speed times the repeats per metre.</summary>
public sealed record RigTracks(float RepeatsPerMeter, int Left, int Right);
