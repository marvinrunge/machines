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
        _regions[0] = _regions[1] = null;
        ReleaseFields();
    }

    /// <summary>
    /// Marks (or unmarks) the cells a polygon covers as blocked, as building footprints are: an active artefact's
    /// boundary (MachLogArtefact::addObstaclePolygon).
    /// </summary>
    public void SetArea(IReadOnlyList<Vector2> polygon, bool present)
    {
        var (x0, y0) = Cell(new Vector2(polygon.Min(p => p.X), polygon.Min(p => p.Y)));
        var (x1, y1) = Cell(new Vector2(polygon.Max(p => p.X), polygon.Max(p => p.Y)));
        var covered = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (Contains(polygon, Centre(x, y)))
                {
                    _buildings[y * Width + x] += present ? 1 : -1;
                    covered++;
                }
        // A polygon smaller than a cell (a palm's trunk) still blocks the cell it stands in.
        if (covered == 0)
        {
            var middle = polygon.Aggregate(Vector2.Zero, (sum, p) => sum + p) / polygon.Count;
            var (x, y) = Cell(middle);
            _buildings[y * Width + x] += present ? 1 : -1;
        }
        _regions[0] = _regions[1] = null;
        ReleaseFields();
    }

    // ---- regions --------------------------------------------------------------------------------

    // For each way of moving (on land; across water too), the connected area each free cell belongs to,
    // numbered from 1 (0: blocked). Worked out again after buildings change.
    private readonly int[]?[] _regions = new int[]?[2];

    /// <summary>The connected area a free cell belongs to (0 for a blocked one), moving as the path search does.</summary>
    public int Region(int x, int y, bool crossesWater)
    {
        var regions = _regions[crossesWater ? 1 : 0] ??= Label(crossesWater);
        return regions[y * Width + x];
    }

    private int[] Label(bool crossesWater)
    {
        var regions = new int[Width * Depth];
        var stack = new Stack<int>();
        var next = 0;
        for (var start = 0; start < regions.Length; start++)
        {
            if (regions[start] != 0 || !IsFree(start % Width, start / Width, crossesWater))
                continue;
            regions[start] = ++next;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var (cx, cy) = (current % Width, current / Width);
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (cx + dx, cy + dy);
                        if ((dx == 0 && dy == 0) || !IsFree(nx, ny, crossesWater) || regions[ny * Width + nx] != 0)
                            continue;
                        if (dx != 0 && dy != 0 && (!IsFree(cx + dx, cy, crossesWater) || !IsFree(cx, cy + dy, crossesWater)))
                            continue;
                        regions[ny * Width + nx] = next;
                        stack.Push(ny * Width + nx);
                    }
                }
            }
        }
        return regions;
    }

    /// <summary>The cell nearest to <paramref name="cell"/> in a region, searching ring by ring.</summary>
    private (int X, int Y)? NearestIn((int X, int Y) cell, int region, bool crossesWater) =>
        Nearest(cell, (x, y) => Region(x, y, crossesWater) == region, Math.Max(Width, Depth));

    /// <summary>The free cell nearest to <paramref name="cell"/>, at most 40 cells away.</summary>
    private (int X, int Y)? NearestFree((int X, int Y) cell, bool crossesWater) =>
        Nearest(cell, (x, y) => IsFree(x, y, crossesWater), 40);

    private (int X, int Y)? Nearest((int X, int Y) cell, Func<int, int, bool> accept, int rings)
    {
        for (var r = 0; r < rings; r++)
        {
            (int, int)? best = null;
            var bestDistance = int.MaxValue;
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;
                    var (x, y) = (cell.X + dx, cell.Y + dy);
                    if (x < 0 || y < 0 || x >= Width || y >= Depth || !accept(x, y))
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
    /// A path from start to goal as a list of waypoints (excluding the start). If the goal cannot be reached
    /// (blocked, or cut off by water or cliffs), the path ends at the nearest cell that can. Returns an empty list
    /// when nothing is reachable.
    /// </summary>
    public List<Vector2> FindPath(Vector2 start, Vector2 goal, bool crossesWater, int maxExpanded = 60_000)
    {
        Calls++;
        var from = Cell(start);

        // A machine standing in a blocked cell (next to a fresh building, or set down on rock) first walks out to
        // the nearest free cell.
        if (!IsFree(from.X, from.Y, crossesWater))
        {
            if (NearestFree(from, crossesWater) is not { } exit)
                return [];
            var outside = Centre(exit.X, exit.Y);
            return [outside, .. FindPath(outside, goal, crossesWater, maxExpanded)];
        }

        // A goal in another area would have the search comb through everything reachable; aim for the nearest
        // reachable cell instead.
        var reachable = Region(from.X, from.Y, crossesWater);
        var goalCell = Cell(goal);
        var goalReachable = Region(goalCell.X, goalCell.Y, crossesWater) == reachable;
        if (NearestIn(goalCell, reachable, crossesWater) is not { } to)
            return [];
        if (from == to)
            return goalReachable || !IsFree(goalCell.X, goalCell.Y, crossesWater) ? [goal] : [];

        // In the open, the search and straightening below end in the straight line anyway.
        if (goalReachable && LineOfSight(start, goal, crossesWater))
            return [goal];

        var goalIndex = Index(to);
        var cells = FieldPath(from, to, crossesWater) ?? SearchPath(from, to, crossesWater, maxExpanded);
        if (cells.Count == 0)
            return [];

        var points = cells.Select(c => Centre(c % Width, c / Width)).ToList();
        if (cells[^1] == goalIndex && goalReachable)
            points[^1] = goal;
        return Smooth(start, points, crossesWater);
    }

    /// <summary>A* from one cell towards another: the cells walked (without the start), ending as near as it got.</summary>
    private List<int> SearchPath((int X, int Y) from, (int X, int Y) to, bool crossesWater, int maxExpanded)
    {
        // The search's bookkeeping lives in arrays reused from search to search; a cell's entries count only
        // when it was touched in this search.
        _search++;
        var open = _open;
        open.Clear();
        SetCost(Index(from), 0);
        open.Enqueue(Index(from), Heuristic(from, to));
        var goalIndex = Index(to);
        var expanded = 0;
        var best = Index(from);
        var bestH = Heuristic(from, to);

        Searches++;
        while (open.Count > 0 && expanded++ < maxExpanded)
        {
            var current = open.Dequeue();
            if (current == goalIndex)
            {
                best = current;
                break;
            }

            var (cx, cy) = (current % Width, current / Width);
            var currentCost = _cost[current];
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
                    if (!IsFree(nx, ny, crossesWater))
                        continue;
                    // No cutting corners past blocked cells.
                    if (dx != 0 && dy != 0 && (!IsFree(cx + dx, cy, crossesWater) || !IsFree(cx, cy + dy, crossesWater)))
                        continue;

                    var next = ny * Width + nx;
                    var nextCost = currentCost + (dx != 0 && dy != 0 ? 1.4142f : 1f);
                    if (_touched[next] == _search && _cost[next] <= nextCost)
                        continue;
                    SetCost(next, nextCost);
                    _cameFrom[next] = current;
                    open.Enqueue(next, nextCost + Heuristic((nx, ny), to));
                }
            }
        }

        Expanded += expanded;
        if (expanded >= maxExpanded) GaveUp++;
        var cells = new List<int>();
        for (var c = best; c != Index(from); c = _cameFrom[c])
            cells.Add(c);
        cells.Reverse();
        return cells;
    }

    // ---- shared searches ------------------------------------------------------------------------

    // Many machines sent to one place in the same tick (a group order, or attackers closing on one target)
    // share a search. From the second request for a goal on, a search spreads out from the goal (Dijkstra)
    // just far enough to settle the asking machine's cell, and keeps its frontier for the next one; each machine
    // walks down the costs: as short a path as its own A* would find, for a fraction of the work.
    private sealed class SharedField(float[] cost, bool[] settled)
    {
        public readonly float[] Cost = cost;
        public readonly bool[] Settled = settled;
        public readonly PriorityQueue<int, float> Open = new();
    }

    private readonly Dictionary<(int Goal, bool Water), int> _asked = [];
    private readonly Dictionary<(int Goal, bool Water), SharedField> _fields = [];

    /// <summary>Forgets the shared searches; the world calls it every tick.</summary>
    public void NewTick()
    {
        _asked.Clear();
        ReleaseFields();
    }

    private void ReleaseFields()
    {
        foreach (var field in _fields.Values)
        {
            System.Buffers.ArrayPool<float>.Shared.Return(field.Cost);
            System.Buffers.ArrayPool<bool>.Shared.Return(field.Settled);
        }
        _fields.Clear();
    }

    private List<int>? FieldPath((int X, int Y) from, (int X, int Y) to, bool crossesWater)
    {
        var key = (Index(to), crossesWater);
        _asked[key] = _asked.GetValueOrDefault(key) + 1;
        if (_asked[key] < 2)
            return null;
        if (!_fields.TryGetValue(key, out var field))
        {
            field = new SharedField(System.Buffers.ArrayPool<float>.Shared.Rent(Width * Depth), System.Buffers.ArrayPool<bool>.Shared.Rent(Width * Depth));
            Array.Fill(field.Cost, float.PositiveInfinity, 0, Width * Depth);
            Array.Clear(field.Settled, 0, Width * Depth);
            field.Cost[Index(to)] = 0;
            field.Open.Enqueue(Index(to), 0);
            _fields[key] = field;
            Searches++;
        }
        Spread(field, Index(from), crossesWater);
        var cost = field.Cost;
        if (!field.Settled[Index(from)])
            return null;

        var cells = new List<int>();
        var current = from;
        for (var guard = 0; Index(current) != Index(to); guard++)
        {
            // Step to the neighbour that is cheapest to finish from; every cell nearer the goal is settled.
            (int X, int Y)? next = null;
            var nextCost = float.MaxValue;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (nx, ny) = (current.X + dx, current.Y + dy);
                    if ((dx == 0 && dy == 0) || !IsFree(nx, ny, crossesWater))
                        continue;
                    if (dx != 0 && dy != 0 && (!IsFree(current.X + dx, current.Y, crossesWater) || !IsFree(current.X, current.Y + dy, crossesWater)))
                        continue;
                    var through = cost[ny * Width + nx] + (dx != 0 && dy != 0 ? 1.4142f : 1f);
                    if (through < nextCost)
                    {
                        nextCost = through;
                        next = (nx, ny);
                    }
                }
            }
            if (next is not { } step || nextCost >= cost[Index(current)] + 0.001f || guard > Width * Depth)
                return null;   // no way down the costs; let A* decide
            cells.Add(Index(step));
            current = step;
        }
        return cells;
    }

    /// <summary>Carries a shared search on (Dijkstra from its goal) until a cell's cost is final.</summary>
    private void Spread(SharedField field, int until, bool crossesWater)
    {
        var (cost, settled, open) = (field.Cost, field.Settled, field.Open);
        while (!settled[until] && open.TryDequeue(out var current, out var known))
        {
            if (settled[current] || known > cost[current])
                continue;
            settled[current] = true;
            Expanded++;
            var (cx, cy) = (current % Width, current / Width);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (nx, ny) = (cx + dx, cy + dy);
                    if ((dx == 0 && dy == 0) || !IsFree(nx, ny, crossesWater))
                        continue;
                    if (dx != 0 && dy != 0 && (!IsFree(cx + dx, cy, crossesWater) || !IsFree(cx, cy + dy, crossesWater)))
                        continue;
                    var next = ny * Width + nx;
                    var nextCost = known + (dx != 0 && dy != 0 ? 1.4142f : 1f);
                    if (nextCost >= cost[next])
                        continue;
                    cost[next] = nextCost;
                    open.Enqueue(next, nextCost);
                }
            }
        }
    }


    /// <summary>True when a straight walk between two points crosses no blocked cell.</summary>
    public bool LineOfSight(Vector2 a, Vector2 b, bool crossesWater)
    {
        var distance = Vector2.Distance(a, b);
        var steps = (int)(distance / (CellSize * 0.5f)) + 1;
        SightSteps += steps;
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
            // Skip ahead to a far point still in a straight line from the anchor: leaps doubling until one is
            // out of sight, then halving back between the last point in sight and that one.
            var furthest = i;
            var leap = 1;
            var blocked = points.Count;
            while (furthest + leap < points.Count && LineOfSight(anchor, points[furthest + leap], crossesWater))
            {
                furthest += leap;
                leap *= 2;
            }
            blocked = Math.Min(blocked, furthest + leap);
            while (blocked - furthest > 1)
            {
                var middle = (furthest + blocked) / 2;
                if (LineOfSight(anchor, points[middle], crossesWater))
                    furthest = middle;
                else
                    blocked = middle;
            }
            result.Add(points[furthest]);
            anchor = points[furthest];
            i = furthest + 1;
        }
        return result;
    }

    private int Index((int X, int Y) c) => c.Y * Width + c.X;

    /// <summary>For profiling (tools/sim-bench): searches run, cells expanded, searches that hit the limit.</summary>
    public long Searches, Expanded, GaveUp, Calls, SightSteps;

    private readonly PriorityQueue<int, float> _open = new();
    private float[] _cost = [];
    private int[] _cameFrom = [];
    private int[] _touched = [];
    private int _search;

    private void SetCost(int cell, float cost)
    {
        if (_cost.Length != Width * Depth)
        {
            _cost = new float[Width * Depth];
            _cameFrom = new int[Width * Depth];
            _touched = new int[Width * Depth];
        }
        _cost[cell] = cost;
        _touched[cell] = _search;
    }

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
