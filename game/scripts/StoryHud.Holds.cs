using System.Globalization;
using Godot;
using Machines.Sim.World;

namespace Machines.Game;

// One hold ring for every Use that is held (plan stufe7 C9, F item 9): putting a Grunt together, the cable in the shut-down
// Locator, and the stage's later holds (strip, mount, mend, wheel, lever), which their packages add to HoldShare.
public partial class StoryHud
{
    /// <summary>How far the local player's held Use has come, 0 to 1; null while nothing is held.</summary>
    private float? HoldShare()
    {
        var world = _game.World;
        if (world.Story is not { } run || !run.Crew.Holding.TryGetValue(Game.Me.Value, out var what))
            return null;
        if (ArmouryHoldShare(run, what) is { } armoury)
            return armoury;
        if (!what.StartsWith(GameWorld.SeatTarget, StringComparison.Ordinal)
            || !int.TryParse(what[GameWorld.SeatTarget.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        if (id == run.Locator.Value)
            return world.CableShare;
        return run.Crew.Grunts.TryGetValue(id, out var companion)
            ? Math.Min(1, companion.ReviveTicks / (GameWorld.ReviveSeconds * GameWorld.TicksPerSecond)) : null;
    }

    /// <summary>The ring round the sight's middle, filling as the hold comes on.</summary>
    private void DrawHolds()
    {
        if (HoldShare() is not { } share)
            return;
        var at = new Vector2(Left + (_size.X - Left) / 2, _size.Y / 2);
        DrawArc(at, 26 * _u, 0, Mathf.Tau, 48, new Color(0, 0, 0, 0.6f), 5 * _u);
        DrawArc(at, 26 * _u, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * Mathf.Clamp(share, 0, 1), 48, MenuTheme.Glow, 4 * _u);
    }
}
