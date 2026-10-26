using System.Numerics;
using Machines.Sim.Navigation;

namespace Machines.Sim.World;

/// <summary>
/// Where each machine of a group order goes, as MachLogGroupSimpleMoveImplementation spread them: the largest
/// clump of the group (machines each within 20 m of another) keeps its shape around the target; the rest, and
/// any of the clump whose place is blocked or taken, fill a square spiral around the target, spaced at 2.1
/// times the group's average clearance.
/// </summary>
internal static class GroupMove
{
    private const float ClumpDistance = 20;

    // How far the spiral looks for a free place before a machine just heads for the target.
    private const int MostGridPlaces = 4000;

    public static List<(Machine Machine, Vector2 Destination)> Destinations(IReadOnlyList<Machine> machines, Vector2 target, NavGrid nav)
    {
        var placed = new List<(Machine Machine, Vector2 Destination)>();
        if (machines.Count == 0)
            return placed;
        var spacing = 2.1f * machines.Average(Clearance);

        var clumps = Clumps(machines);
        var largest = clumps.MaxBy(c => c.Count)!;
        var centroid = largest.Aggregate(Vector2.Zero, (sum, m) => sum + m.Position) / largest.Count;
        var unplaced = new List<Machine>();
        foreach (var machine in largest)
            if (!TryPlace(machine, target + machine.Position - centroid))
                unplaced.Insert(0, machine);

        var spiral = new SquareSpiral();
        foreach (var machine in unplaced.Concat(clumps.Where(c => c != largest).SelectMany(c => c)))
        {
            var done = false;
            for (var i = 0; i < MostGridPlaces && !done; i++)
            {
                done = TryPlace(machine, target + new Vector2(spiral.X, spiral.Y) * spacing);
                spiral.Next();
            }
            if (!done)
                placed.Add((machine, target));
        }
        return placed;

        bool TryPlace(Machine machine, Vector2 destination)
        {
            var crossesWater = GameWorld.CrossesWater(machine);
            if (!nav.IsFree(destination, crossesWater) || !SameArea(destination, crossesWater))
                return false;
            foreach (var (other, at) in placed)
            {
                var clearance = Clearance(machine) + Clearance(other);
                if (Vector2.DistanceSquared(destination, at) < clearance * clearance)
                    return false;
            }
            placed.Add((machine, destination));
            return true;
        }

        // A place cut off from the target (over a cliff, across water) would send a machine elsewhere.
        bool SameArea(Vector2 destination, bool crossesWater)
        {
            var (tx, ty) = nav.Cell(target);
            var targetArea = nav.Region(tx, ty, crossesWater);
            var (x, y) = nav.Cell(destination);
            return targetArea == 0 || nav.Region(x, y, crossesWater) == targetArea;
        }
    }

    private static float Clearance(Machine machine) => MathF.Max(1, machine.Stats.LowClearance);

    /// <summary>MachLogGroupMoverUtility::createClumps: machines chained together by being within 20 m of each other.</summary>
    private static List<List<Machine>> Clumps(IReadOnlyList<Machine> machines)
    {
        var unprocessed = machines.ToList();
        var clumps = new List<List<Machine>>();
        while (unprocessed.Count > 0)
        {
            var clump = new List<Machine> { unprocessed[^1] };
            unprocessed.RemoveAt(unprocessed.Count - 1);
            bool added;
            do
            {
                added = false;
                for (var i = 0; i < unprocessed.Count;)
                {
                    if (clump.Any(m => Vector2.DistanceSquared(m.Position, unprocessed[i].Position) < ClumpDistance * ClumpDistance))
                    {
                        clump.Add(unprocessed[i]);
                        unprocessed.RemoveAt(i);
                        added = true;
                    }
                    else
                    {
                        i++;
                    }
                }
            } while (added);
            clumps.Add(clump);
        }
        return clumps;
    }

    /// <summary>MachLogSquareSpiral: the centre, then ring after ring of grid places around it.</summary>
    private sealed class SquareSpiral
    {
        private int _edge = 1, _index;

        public int X { get; private set; }
        public int Y { get; private set; }

        public void Next()
        {
            if (++_index >= (_edge - 1) * 4)
            {
                _index = 0;
                _edge += 2;
            }
            var half = (_edge - 1) / 2;
            var along = _index % (_edge - 1);
            (X, Y) = (_index / (_edge - 1)) switch
            {
                0 => (half - along, half),
                1 => (-half, half - along),
                2 => (-half + along, -half),
                _ => (half, -half + along),
            };
        }
    }
}
