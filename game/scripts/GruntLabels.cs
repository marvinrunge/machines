using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The labels over the others of the trio in a story chapter (StoryHud.Labels), in the way of the objective's mark: the
/// name, how far, and under it what the Grunt is about as its card says it. Not in the original. What shows and how is
/// worked out here, Godot-free, so the tests hold it: a label fades away when its Grunt is near and in plain view (it is
/// plain who stands there), it shrinks with distance, and a Grunt off the screen gets an arrow at the edge only when it is
/// far or down (one following at the hero's back would keep an arrow up the whole chapter).
/// </summary>
public static class GruntLabels
{
    /// <summary>In plain view a label is gone nearer than <see cref="Near"/> metres and whole from <see cref="Clear"/> on.</summary>
    public const float Near = 6, Clear = 14;

    /// <summary>A label is drawn whole up to <see cref="Large"/> metres and at <see cref="Smallest"/> of that from <see cref="Small"/> on.</summary>
    public const float Large = 8, Small = 40, Smallest = 0.7f;

    /// <summary>How far a Grunt off the screen must be for its arrow at the edge (a downed one always has one).</summary>
    public const float ArrowFrom = 12;

    /// <summary>
    /// How much of a label shows, 0 to 1: whole out of plain view (behind a wall, a closed door, high cover) and far off,
    /// fading out as its Grunt comes near in plain view. A Grunt down keeps half of it: the Locator has to find it.
    /// </summary>
    public static float Alpha(float distance, bool plainView, GruntDoing doing)
    {
        if (!plainView)
            return 1;
        var share = Math.Clamp((distance - Near) / (Clear - Near), 0, 1);
        share = share * share * (3 - 2 * share);
        return doing == GruntDoing.Down ? MathF.Max(share, 0.5f) : share;
    }

    /// <summary>How large a label is drawn: whole near, down to <see cref="Smallest"/> far off.</summary>
    public static float Scale(float distance) =>
        1 - (1 - Smallest) * Math.Clamp((distance - Large) / (Small - Large), 0, 1);

    /// <summary>Whether a Grunt off the screen gets an arrow at the edge: when it is down, or far and not still in its clamp.</summary>
    public static bool Arrow(float distance, GruntDoing doing) =>
        doing == GruntDoing.Down || doing != GruntDoing.Dormant && distance >= ArrowFrom;

    /// <summary>
    /// Whether the line between two places on the deck is open to the eye: walls, high cover and closed doors block it, grates
    /// do not (a grate wall lets sight through). Off a deck everything is in plain view.
    /// </summary>
    public static bool PlainView(GameWorld world, Vector2 from, Vector2 to)
    {
        if (world.Deck is not { } deck)
            return true;
        bool See(int x, int y) => deck.Cell(x, y) switch
        {
            DeckCell.Wall or DeckCell.High or DeckCell.Cover => false,
            DeckCell.Door => !world.DoorClosed(deck.At(x, y)),
            _ => true,
        };
        // A supercover line, as DeckRoute.Clear walks it: through a corner it asks both cells beside it.
        var (x, y) = ((int)MathF.Floor(from.X), (int)MathF.Floor(from.Y));
        var (tx, ty) = ((int)MathF.Floor(to.X), (int)MathF.Floor(to.Y));
        var (nx, ny) = (Math.Abs(tx - x), Math.Abs(ty - y));
        var (sx, sy) = (Math.Sign(tx - x), Math.Sign(ty - y));
        for (int ix = 0, iy = 0; ix < nx || iy < ny;)
        {
            var across = (1 + 2 * ix) * ny - (1 + 2 * iy) * nx;
            if (across == 0)
            {
                if (!See(x + sx, y) && !See(x, y + sy))
                    return false;
                (x, y, ix, iy) = (x + sx, y + sy, ix + 1, iy + 1);
            }
            else if (across < 0)
                (x, ix) = (x + sx, ix + 1);
            else
                (y, iy) = (y + sy, iy + 1);
            // The far end's own cell is the Grunt's: it stands there, whatever the grid says under it.
            if ((x, y) != (tx, ty) && !See(x, y))
                return false;
        }
        return true;
    }
}
