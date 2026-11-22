using System.Numerics;

namespace Machines.Game;

/// <summary>
/// Where the third-person camera of a sighted hero (Wired and battles) stands: behind and above the hero on the line of
/// the view's heading through the hero's pivot, looking where the driver looks, as World of Tanks follows its tank. Because
/// the camera stays on that line (no shoulder offset), every point under the crosshair beyond the hero lies along the
/// heading from the hero, so the yaw the commands carry is exactly the way to the point aimed at. Pure, in Godot's axes
/// (y up, the simulation's ground point (x, y) at (x, z)), so the tests run it.
/// </summary>
public static class ChaseCamera
{
    /// <summary>The pitch the view starts at, and the most it looks down and up, radians (down negative).</summary>
    public const float StartPitch = -6 * MathF.PI / 180, MostDown = -45 * MathF.PI / 180, MostUp = 20 * MathF.PI / 180;

    /// <summary>The steps of the mouse wheel, times the arm the options set.</summary>
    public static readonly float[] Zooms = [0.7f, 1f, 1.4f];

    /// <summary>How high over the ground about it the camera and its arm stand at least, metres.</summary>
    public const float Floor = 1.5f;

    /// <summary>
    /// The arm's length for a hero of this radius (Machine.Radius, at least 1.5 m), times <paramref name="distance"/>: the
    /// battle watch camera's, which keeps a hero at about a sixth of the picture's height whatever its size.
    /// </summary>
    public static float Arm(float radius, float distance) => (3 + 3.5f * radius) * distance;

    /// <summary>How far over the hero's top the pivot stands at least, times the arm at its full length.</summary>
    public const float Clearance = 0.15f;

    /// <summary>
    /// How far above the hero's middle the pivot stands for an arm of <paramref name="arm"/> metres (<paramref name="full"/>
    /// at its full length) and a hero whose top stands <paramref name="top"/> metres over its middle: a quarter of the arm,
    /// and at full length at least <see cref="Clearance"/> of the arm over the hero's top, so that the hero sits below the
    /// crosshair and a foe the crosshair rests on beyond it is seen over its back, however tall the hero. A shorter arm (a
    /// wall or a hill behind) brings the pivot down with it, so that the hero stays in the picture.
    /// </summary>
    public static float Lift(float arm, float full, float top) =>
        MathF.Max(0.25f * arm, (MathF.Max(0, top) + Clearance * full) * Math.Clamp(arm / MathF.Max(full, 0.01f), 0, 1));

    /// <summary>The shortest the arm becomes before walls and hills; nearer, the camera would stand in the hero.</summary>
    public static float ShortestArm(float radius) => 1.2f * radius + 1.5f;

    /// <summary>Whether a camera this close to the hero's middle would stand inside its model, which is then left out of the picture.</summary>
    public static bool InsideHero(Vector3 camera, Vector3 centre, float radius) => Vector3.Distance(camera, centre) < 1.3f * radius;

    /// <summary>The way the view looks at a heading (the simulation's radians anticlockwise) and a pitch (up positive).</summary>
    public static Vector3 Ahead(float yaw, float pitch) =>
        new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Sin(yaw));

    /// <summary>The pitch kept within its limits.</summary>
    public static float Pitch(float pitch) => Math.Clamp(pitch, MostDown, MostUp);

    /// <summary>Where the camera stands and how long its arm came out.</summary>
    public readonly record struct Spot(Vector3 Camera, float Arm, float Raised);

    /// <summary>
    /// The camera <paramref name="arm"/> behind <paramref name="pivot"/> against the view, shortened by
    /// <paramref name="walls"/> (the longest arm the buildings leave, or more) and where the arm comes within
    /// <see cref="Floor"/> of the ground about it (<paramref name="groundAbout"/>, else <paramref name="ground"/> at a ground
    /// point; null for none), so that the camera stays on the line through the hero and the hero in the picture; never
    /// shorter than <paramref name="shortest"/>, and there raised straight up if it still stands too low. Raised straight
    /// up, it stays on the line of the heading.
    /// </summary>
    public static Spot Place(Vector3 pivot, float yaw, float pitch, float arm, float shortest, float walls,
        Func<float, float, float>? ground, Func<Vector3, float>? groundAbout = null)
    {
        var ahead = Ahead(yaw, pitch);
        var length = MathF.Max(shortest, MathF.Min(arm, walls));
        var floorAt = groundAbout ?? (ground is null ? null : p => ground(p.X, p.Z));
        if (floorAt is not null)
        {
            for (var t = 0.5f; t <= length; t += 0.5f)
            {
                // Where the arm comes near a hill behind the hero (or looking up, the ground) it ends half a metre before.
                var at = pivot - ahead * t;
                if (at.Y < floorAt(at) + Floor)
                {
                    length = MathF.Max(shortest, t - 0.5f);
                    break;
                }
            }
        }
        var camera = pivot - ahead * length;
        var floor = floorAt?.Invoke(camera) + Floor;
        var raised = floor is { } least && camera.Y < least ? least - camera.Y : 0;
        return new Spot(camera + new Vector3(0, raised, 0), length, raised);
    }

    /// <summary>The most the hero's middle stands below the picture's middle, of half the picture's height.</summary>
    public const float LowestShare = 0.6f;

    /// <summary>
    /// The pitch the camera looks at from <paramref name="camera"/>: the view's, or tipped down where the hero's middle
    /// (<paramref name="centre"/>) would stand lower in the picture than <see cref="LowestShare"/> of half its height
    /// (<paramref name="fov"/>, radians, the camera's height). On the line through the pivot this never happens; raised
    /// straight up over a hill behind the hero, it keeps the hero in the picture. The aim goes through the middle of the
    /// picture whatever its pitch, so the crosshair still means where the rounds go.
    /// </summary>
    public static float Tipped(float pitch, Vector3 camera, Vector3 centre, float fov)
    {
        var flat = MathF.Max(0.1f, MathF.Sqrt((centre.X - camera.X) * (centre.X - camera.X) + (centre.Z - camera.Z) * (centre.Z - camera.Z)));
        var below = MathF.Atan2(camera.Y - centre.Y, flat);
        return MathF.Min(pitch, LowestShare * fov / 2 - below);
    }

    /// <summary>
    /// How far along a line of sight from the camera the aim begins: where it comes abreast of the hero's pivot on the ground
    /// plan, so that the ground and whatever stands between the camera and the hero are not aimed at, and every point aimed
    /// at lies ahead of the hero (a camera raised over the ground stands higher, not further back).
    /// </summary>
    public static float AimFrom(Vector3 camera, Vector3 direction, Vector3 pivot)
    {
        var flat = direction.X * direction.X + direction.Z * direction.Z;
        var to = pivot - camera;
        return flat < 1e-4f ? MathF.Max(0, Vector3.Dot(to, direction)) : MathF.Max(0, (to.X * direction.X + to.Z * direction.Z) / flat);
    }

    /// <summary>A spread cone of the hero seen from the camera: the share its marks are drawn at, the hero being <paramref name="hero"/> metres from the point and the camera <paramref name="camera"/>.</summary>
    public static float SpreadShare(float hero, float camera) => camera <= 0.01f ? 1 : Math.Clamp(hero / camera, 0.05f, 1);
}
