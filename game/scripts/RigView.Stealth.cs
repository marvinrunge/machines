using Godot;

namespace Machines.Game;

/// <summary>A foot of a walking rig coming down: its part, the point of the walk cycle it plants at (0 to 1), and whether it is a front foot.</summary>
public readonly record struct RigPlant(int Node, float Phase, bool Front);

// Package S's gait for the Thresher (design-stealth §9.1, §13 View): a walk that may run slower than half its pace (his patrol
// is a quarter of his top speed: a plant about every 1.1 s), the cycle's time warped so each foot lingers and then comes down
// hard (p - w·sin(4πp)/4π), and his plants, found once from the walk cycle itself: the lowest moving parts of each leg, and the
// point of the cycle where each reaches the floor. ActorView.Stealth reads them each frame.
public partial class RigView
{
    /// <summary>The slowest a walk cycle runs, as a share of its own pace (0.5 for every machine but the Thresher on his round).</summary>
    public float SlowestWalk { get; set; } = 0.5f;

    /// <summary>How much the walk's time is warped towards heavy plants, 0 (none) to below 1 (design-stealth: 0.6).</summary>
    public float WalkWarp { get; set; }

    // The point of the cycle the parts are posed at: the walk's time warped, every other cycle as it runs.
    private float Warped(float phase) =>
        WalkWarp > 0 && _cycle == "walk" ? phase - WalkWarp * Mathf.Sin(4 * Mathf.Pi * phase) / (4 * Mathf.Pi) : phase;

    /// <summary>Where the walk cycle's parts are posed now (0 to 1), or null while the rig does not walk.</summary>
    public float? WalkPhase => _cycle == "walk" && _rig.Animations.ContainsKey("walk") ? Warped(_phase) : null;

    private RigPlant[]? _plants;

    /// <summary>The rig's plants, found from its walk cycle the first time they are asked for; none for a rig without legs.</summary>
    public IReadOnlyList<RigPlant> Plants => _plants ??= FindPlants();

    /// <summary>Where a part of the rig stands in the world now.</summary>
    public Vector3 PartPosition(int node) => node >= 0 && node < _nodes.Length && _nodes[node].IsInsideTree() ? _nodes[node].GlobalPosition : GlobalPosition;

    private const int PlantSamples = 48;

    private RigPlant[] FindPlants()
    {
        if (!_rig.Animations.TryGetValue("walk", out var walk) || walk.Tracks.Length == 0)
            return [];
        var nodes = _rig.Nodes;
        var tracks = new Dictionary<int, RigTrack>();
        foreach (var track in walk.Tracks)
            if (track.Node >= 0 && track.Node < nodes.Length)
                tracks[track.Node] = track;
        bool Under(int part, int above)
        {
            for (var up = nodes[part].Parent; up >= 0 && up < nodes.Length && up != part; up = nodes[up].Parent)
            {
                if (up == above)
                    return true;
                part = up;
            }
            return false;
        }
        Transform3D At(int part, float phase)
        {
            var own = tracks.TryGetValue(part, out var track) ? track.At(phase) : nodes[part].Rest;
            var parent = nodes[part].Parent;
            return parent >= 0 && parent < nodes.Length && parent != part ? At(parent, phase) * own : own;
        }
        // The lowest point of a part's mesh (its box's corners) in the machine's own axes, at a point of the cycle.
        float Low(int part, float phase)
        {
            var at = At(part, phase);
            if (nodes[part].Mesh is not { } mesh)
                return at.Origin.Y;
            var box = mesh.GetAabb();
            var low = float.MaxValue;
            for (var corner = 0; corner < 8; corner++)
                low = Mathf.Min(low, (at * box.GetEndpoint(corner)).Y);
            return low;
        }
        // The legs' last moving parts: moved by the walk, with a mesh, nothing moved below them.
        var leaves = tracks.Keys.Where(part => nodes[part].Mesh is not null && !tracks.Keys.Any(other => other != part && nodes[other].Mesh is not null && Under(other, part))).ToList();
        var heights = leaves.ToDictionary(part => part, part => Enumerable.Range(0, PlantSamples).Select(i => Low(part, i / (float)PlantSamples)).ToArray());
        var moving = leaves.Where(part => heights[part].Max() - heights[part].Min() > 0.05f).ToList();
        if (moving.Count == 0)
            return [];
        var floor = moving.Min(part => heights[part].Min());
        var top = moving.Max(part => heights[part].Max());
        var feet = moving.Where(part => heights[part].Min() <= floor + Mathf.Max(0.3f, 0.15f * (top - floor))).ToList();
        var middle = feet.Average(part => nodes[part].Rest.Origin.X + At(part, 0).Origin.X);
        var plants = new List<RigPlant>();
        foreach (var part in feet)
        {
            var h = heights[part];
            var (lo, hi) = (h.Min(), h.Max());
            var down = lo + 0.15f * (hi - lo);
            var front = nodes[part].Rest.Origin.X + At(part, 0).Origin.X > middle;
            for (var i = 0; i < PlantSamples; i++)
            {
                if (h[i] <= down && h[(i + PlantSamples - 1) % PlantSamples] > down)
                    plants.Add(new RigPlant(part, i / (float)PlantSamples, front));
            }
        }
        return [.. plants.OrderBy(p => p.Phase)];
    }
}
