using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.Navigation;

/// <summary>
/// A walkability grid over the planet, built from the original obstacle polygons (<c>.csp</c>) and the
/// footprints of buildings. Paths are found with A* and then straightened.
/// </summary>
/// <remarks>
/// The original used a visibility graph over the same polygons; a grid gives the same walls with far less code.
/// Water obstacles block everything except hover and glider machines (a remake rule).
/// </remarks>
public sealed class NavGrid
{
    public const float CellSize = 4f;

    private readonly byte[] _static;   // 0 free, 1 obstacle, 2 water
    private readonly int[] _buildings; // number of building footprints covering the cell

    public NavGrid(float width, float depth, IEnumerable<Obstacle> obstacles)
    {
        Width = (int)MathF.Ceiling(width / CellSize);
        Depth = (int)MathF.Ceiling(depth / CellSize);
        _static = new byte[Width * Depth];
        _buildings = new int[Width * Depth];

        foreach (var obstacle in obstacles)
            Rasterise(obstacle);
    }

    public int Width { get; }
    public int Depth { get; }

    public (int X, int Y) Cell(Vector2 p) =>
        (Math.Clamp((int)(p.X / CellSize), 0, Width - 1), Math.Clamp((int)(p.Y / CellSize), 0, Depth - 1));

    public Vector2 Centre(int x, int y) => new((x + 0.5f) * CellSize, (y + 0.5f) * CellSize);

    public bool IsFree(int x, int y, bool crossesWater) =>
        x >= 0 && y >= 0 && x < Width && y < Depth
        && _buildings[y * Width + x] == 0
        && _static[y * Width + x] switch { 0 => true, 2 => crossesWater, _ => false };

    public bool IsFree(Vector2 p, bool crossesWater)
    {
        var (x, y) = Cell(p);
        return IsFree(x, y, crossesWater);
    }

    /// <summary>Marks (or unmarks) a building footprint as blocked.</summary>
    public void SetBuilding((Vector2 Min, Vector2 Max) footprint, bool present)
    {
        var (x0, y0) = Cell(footprint.Min);
        var (x1, y1) = Cell(footprint.Max);
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                _buildings[y * Width + x] += present ? 1 : -1;
    }

    /// <summary>True when nothing in the static map or buildings blocks the footprint.</summary>
    public bool IsAreaFree((Vector2 Min, Vector2 Max) area)
    {
        var (x0, y0) = Cell(area.Min);
        var (x1, y1) = Cell(area.Max);
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (!IsFree(x, y, crossesWater: false))
                    return false;
        return area.Min.X >= 0 && area.Min.Y >= 0 && area.Max.X <= Width * CellSize && area.Max.Y <= Depth * CellSize;
    }

    /// <summary>
    /// A path from start to goal as a list of waypoints (excluding the start). If the goal is blocked, the path
    /// ends at the nearest free cell. Returns an empty list when nothing is reachable.
    /// </summary>
    public List<Vector2> FindPath(Vector2 start, Vector2 goal, bool crossesWater, int maxExpanded = 60_000)
    {
        var from = Cell(start);
        var to = NearestFree(Cell(goal), crossesWater) ?? from;
        if (from == to)
            return [goal];

        // Let a machine standing in a blocked cell (e.g. next to a fresh building) walk out.
        var startBlocked = !IsFree(from.X, from.Y, crossesWater);

        var open = new PriorityQueue<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var cost = new Dictionary<int, float> { [Index(from)] = 0 };
        open.Enqueue(Index(from), Heuristic(from, to));
        var goalIndex = Index(to);
        var expanded = 0;
        var best = Index(from);
        var bestH = Heuristic(from, to);

        while (open.Count > 0 && expanded++ < maxExpanded)
        {
            var current = open.Dequeue();
            if (current == goalIndex)
            {
                best = current;
                break;
            }

            var (cx, cy) = (current % Width, current / Width);
            var currentCost = cost[current];
            var h = Heuristic((cx, cy), to);
            if (h < bestH)
            {
                bestH = h;
                best = current;
            }

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    var (nx, ny) = (cx + dx, cy + dy);
                    var leavingStart = startBlocked && current == Index(from);
                    if (!IsFree(nx, ny, crossesWater) && !leavingStart)
                        continue;
                    // No cutting corners past blocked cells.
                    if (dx != 0 && dy != 0 && (!IsFree(cx + dx, cy, crossesWater) || !IsFree(cx, cy + dy, crossesWater)))
                        continue;

                    var next = ny * Width + nx;
                    var nextCost = currentCost + (dx != 0 && dy != 0 ? 1.4142f : 1f);
                    if (cost.TryGetValue(next, out var known) && known <= nextCost)
                        continue;
                    cost[next] = nextCost;
                    cameFrom[next] = current;
                    open.Enqueue(next, nextCost + Heuristic((nx, ny), to));
                }
            }
        }

        var cells = new List<int>();
        for (var c = best; c != Index(from); c = cameFrom[c])
            cells.Add(c);
        cells.Reverse();
        if (cells.Count == 0)
            return [];

        var points = cells.Select(c => Centre(c % Width, c / Width)).ToList();
        if (best == goalIndex && IsFree(goal, crossesWater))
            points[^1] = goal;
        return Smooth(start, points, crossesWater);
    }

    /// <summary>True when a straight walk between two points crosses no blocked cell.</summary>
    public bool LineOfSight(Vector2 a, Vector2 b, bool crossesWater)
    {
        var distance = Vector2.Distance(a, b);
        var steps = (int)(distance / (CellSize * 0.5f)) + 1;
        for (var i = 1; i <= steps; i++)
        {
            if (!IsFree(Vector2.Lerp(a, b, i / (float)steps), crossesWater))
                return false;
        }
        return true;
    }

    private List<Vector2> Smooth(Vector2 start, List<Vector2> points, bool crossesWater)
    {
        var result = new List<Vector2>();
        var anchor = start;
        var i = 0;
        while (i < points.Count)
        {
            // Skip ahead to the furthest point still in a straight line from the anchor.
            var furthest = i;
            for (var j = points.Count - 1; j > i; j--)
            {
                if (LineOfSight(anchor, points[j], crossesWater))
                {
                    furthest = j;
                    break;
                }
            }
            result.Add(points[furthest]);
            anchor = points[furthest];
            i = furthest + 1;
        }
        return result;
    }

    private (int X, int Y)? NearestFree((int X, int Y) cell, bool crossesWater)
    {
        if (IsFree(cell.X, cell.Y, crossesWater))
            return cell;
        for (var r = 1; r < 40; r++)
        {
            (int, int)? best = null;
            var bestDistance = float.MaxValue;
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;
                    var (x, y) = (cell.X + dx, cell.Y + dy);
                    if (!IsFree(x, y, crossesWater))
                        continue;
                    var d = dx * dx + dy * dy;
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = (x, y);
                    }
                }
            }
            if (best is not null)
                return best;
        }
        return null;
    }

    private int Index((int X, int Y) c) => c.Y * Width + c.X;

    private static float Heuristic((int X, int Y) a, (int X, int Y) b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return Math.Max(dx, dy) + 0.4142f * Math.Min(dx, dy);
    }

    private void Rasterise(Obstacle obstacle)
    {
        var polygon = obstacle.Polygon;
        var min = new Vector2(polygon.Min(p => p.X), polygon.Min(p => p.Y));
        var max = new Vector2(polygon.Max(p => p.X), polygon.Max(p => p.Y));
        var (x0, y0) = Cell(min);
        var (x1, y1) = Cell(max);
        var value = obstacle.Water ? (byte)2 : (byte)1;

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                // A cell is blocked when its centre or any corner is inside the polygon.
                var c = Centre(x, y);
                var h = CellSize / 2;
                if (Contains(polygon, c) || Contains(polygon, c + new Vector2(-h, -h)) || Contains(polygon, c + new Vector2(h, -h))
                    || Contains(polygon, c + new Vector2(-h, h)) || Contains(polygon, c + new Vector2(h, h)))
                {
                    var index = y * Width + x;
                    // Solid wins over water where they overlap.
                    if (_static[index] != 1)
                        _static[index] = value;
                }
            }
        }
    }

    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
