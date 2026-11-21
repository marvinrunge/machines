using Godot;

namespace Machines.Game;

// What the story's cutscenes and its ship in distress set off themselves (the remake's own).
public partial class Effects
{
    /// <summary>
    /// A shower of sparks from a torn cable or a burst pipe at <paramref name="at"/>, spraying round
    /// <paramref name="direction"/> (down when none is given) and falling, bouncing off the floor: about
    /// <paramref name="amount"/> sparks, white-hot cooling to <paramref name="colour"/> (orange when none is given).
    /// </summary>
    public void SparkShower(Vector3 at, Vector3? direction = null, int amount = 40, Color? colour = null)
    {
        if (!GraphicsSettings.Effects)
            return;
        var way = direction is { } d && d.LengthSquared() > 0.0001f ? d.Normalized() : Vector3.Down;
        Sparks(at, colour ?? new Color(1, 0.6f, 0.25f), amount, way, spread: 35, speed: 7, lifetime: 1.6f, size: 0.45f);
    }
}
