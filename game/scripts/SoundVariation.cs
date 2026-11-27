namespace Machines.Game;

/// <summary>
/// Every sound effect a little different each time it plays (user 2026-11-27, first for the story, then for the whole game):
/// its pitch strays by up to <see cref="PitchVary"/> either way, so a gun fired twenty times or a step repeated does not
/// sound like one recording looped. Free of Godot (the tests read it).
/// </summary>
public static class SoundVariation
{
    /// <summary>How far a one-shot's pitch strays each time it plays, either way.</summary>
    public const float PitchVary = 0.04f;

    /// <summary><paramref name="pitch"/> strayed by up to <see cref="PitchVary"/> either way, <paramref name="unit"/> (0 to 1) saying where.</summary>
    public static float Varied(float pitch, float unit) => pitch * (1 + PitchVary * (2 * Math.Clamp(unit, 0, 1) - 1));
}
