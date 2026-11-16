using Godot;

namespace Machines.Game;

/// <summary>
/// How many screen pixels the first-person HUD gives each of the original's (it ran at 640 by 480): as many as the
/// screen's height has whole times 480, times the HUD's size chosen in the options (<see cref="GameOptions.HudSize"/>), at
/// least one. At the standard size it is a whole number, as ever; at another it may not be, and the painted art is then
/// shrunk to the size it comes to in whole pixels (<see cref="HudArt"/>). Grown, it takes at most half as much again of
/// the screen as the original's HUD took of its own, so that on a small window the radar stays clear of the sight.
/// </summary>
public static class HudScale
{
    /// <summary>The HUD's pixels to the original's one on a screen of this size.</summary>
    public static float Of(Vector2 screen)
    {
        var whole = Math.Max(1, (int)Math.Round(screen.Y / 480f));
        var largest = 1.5f * Math.Min(screen.Y / 480f, screen.X / 640f);
        return Math.Max(1, Math.Min(whole * GameOptions.HudSize, Math.Max(whole, largest)));
    }
}
