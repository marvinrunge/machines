using System.Numerics;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// The way to a place on a ship deck as the HUD shows it: not the straight line through the walls but a chain of waypoints,
/// each as far along the way as can be seen from the one before, so that the next one is the door or the corner to make for.
/// It goes where the Locator goes: floors, ducts and their lips, catwalks and every door's slot (a closed door is there to be
/// hacked), not through walls, grates or high cover. One field of distances to the goal is kept per goal; the chain is worked
/// out again only when the start moves into another cell. Godot-free, so that the tests can run it on the prologue's deck.
/// </summary>
public sealed class DeckRoute(Deck deck)
{
    /// <summary>How many waypoints the chain holds at most: the HUD shows the next and hints at the one after.</summary>
    public const int Longest = 6;

    private readonly int _width = deck.Width, _depth = deck.Depth;
    private int[]? _field;
    private (int X, int Y) _goal = (-1, -1);
    private (int X, int Y) _from = (-1, -1);
    private List<Vector2> _chain = [];

    /// <summary>Whether the Locator can be in a cell.</summary>
    public static bool Open(DeckCell cell) => cell is DeckCell.Floor or DeckCell.Duct or DeckCell.Lip or DeckCell.Catwalk or DeckCell.Door;

    private bool Open(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _depth && Open(deck.Cell(x, y));

    /// <summary>
    /// The waypoints from <paramref name="from"/> to <paramref name="goal"/>, the goal last (at most <see cref="Longest"/>, the
    /// goal itself not always among them then); empty when there is no way or the goal is in sight already, as the straight
    /// line is the way then.
    /// </summary>
    public IReadOnlyList<Vector2> Chain(Vector2 from, Vector2 goal)
    {
        var goalCell = Nearest(Cell(goal));
        var fromCell = Nearest(Cell(from));
        if (goalCell is not { } g)
        {
            // A goal off every floor has no way, and no length of one either.
            (_goal, _field) = ((-1, -1), null);
            return [];
        }
        if (fromCell is not { } f)
            return [];
        if (g != _goal || _field is null)
        {
            _goal = g;
            _field = Field(g);
            _from = (-1, -1);
        }
        if (f == _from)
            return _chain;
        _from = f;
        _chain = Pulled(Walk(f), goal);
        return _chain;
    }

    /// <summary>The length of the way from <paramref name="from"/> to the goal last asked for, in metres (cells); null without one.</summary>
    public int? Steps(Vector2 from) =>
        _field is not null && Nearest(Cell(from)) is { } f && _field[f.Y * _width + f.X] is >= 0 and var steps ? steps : null;

    private static (int X, int Y) Cell(Vector2 p) => ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

    /// <summary>The cell itself where it is open, else the nearest open one within three cells (a hero stands on a door's edge, a marker in a clamp).</summary>
    private (int X, int Y)? Nearest((int X, int Y) cell)
    {
        if (Open(cell.X, cell.Y))
            return cell;
        for (var r = 1; r <= 3; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Open(cell.X + dx, cell.Y + dy))
                        return (cell.X + dx, cell.Y + dy);
                }
            }
        }
        return null;
    }

    private static readonly (int X, int Y)[] Ways = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Every cell's distance to the goal in steps across, -1 where there is no way.</summary>
    private int[] Field((int X, int Y) goal)
    {
        var field = new int[_width * _depth];
        Array.Fill(field, -1);
        var queue = new Queue<(int X, int Y)>();
        field[goal.Y * _width + goal.X] = 0;
        queue.Enqueue(goal);
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            var next = field[y * _width + x] + 1;
            foreach (var (dx, dy) in Ways)
            {
                var (nx, ny) = (x + dx, y + dy);
                if (!Open(nx, ny) || field[ny * _width + nx] >= 0)
                    continue;
                field[ny * _width + nx] = next;
                queue.Enqueue((nx, ny));
            }
        }
        return field;
    }

    /// <summary>The cells down the field from <paramref name="from"/> to the goal; empty where the goal cannot be reached.</summary>
    private List<(int X, int Y)> Walk((int X, int Y) from)
    {
        var field = _field!;
        if (field[from.Y * _width + from.X] < 0)
            return [];
        var path = new List<(int X, int Y)> { from };
        var at = from;
        while (field[at.Y * _width + at.X] > 0)
        {
            var here = field[at.Y * _width + at.X];
            foreach (var (dx, dy) in Ways)
            {
                var (nx, ny) = (at.X + dx, at.Y + dy);
                if (Open(nx, ny) && field[ny * _width + nx] == here - 1)
                {
                    at = (nx, ny);
                    break;
                }
            }
            path.Add(at);
        }
        return path;
    }

    /// <summary>The path pulled tight: from each waypoint the furthest cell of the path in a clear line is the next.</summary>
    private List<Vector2> Pulled(List<(int X, int Y)> path, Vector2 goal)
    {
        var chain = new List<Vector2>();
        if (path.Count < 2)
            return chain;
        var at = 0;
        while (at < path.Count - 1 && chain.Count < Longest)
        {
            var next = at + 1;
            while (next + 1 < path.Count && Clear(path[at], path[next + 1]))
                next++;
            // The goal in sight from the start: the straight line is the way.
            if (at == 0 && next == path.Count - 1)
                return chain;
            chain.Add(next == path.Count - 1 ? goal : new Vector2(path[next].X + 0.5f, path[next].Y + 0.5f));
            at = next;
        }
        return chain;
    }

    /// <summary>
    /// Whether the line between two cells' middles crosses only open cells, the corners it passes through by both cells beside
    /// them (a supercover line), so that it never cuts the corner of a wall.
    /// </summary>
    public bool Clear((int X, int Y) a, (int X, int Y) b)
    {
        var (x, y) = a;
        var (nx, ny) = (Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        var (sx, sy) = (Math.Sign(b.X - a.X), Math.Sign(b.Y - a.Y));
        for (int ix = 0, iy = 0; ix < nx || iy < ny;)
        {
            if (!Open(x, y))
                return false;
            // Which edge the line leaves the cell by: the next crossing across or the next along, or both at a corner.
            var across = (1 + 2 * ix) * ny - (1 + 2 * iy) * nx;
            if (across == 0)
            {
                if (!Open(x + sx, y) || !Open(x, y + sy))
                    return false;
                (x, y, ix, iy) = (x + sx, y + sy, ix + 1, iy + 1);
            }
            else if (across < 0)
                (x, ix) = (x + sx, ix + 1);
            else
                (y, iy) = (y + sy, iy + 1);
        }
        return Open(x, y);
    }
}
