using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.Navigation;

/// <summary>
/// The ground's height for the simulation, on a regular grid of vertices with heights between them interpolated:
/// by default from the planet surface (<c>.psf</c>), whose tiles record the height they were laid at, flat inside
/// and averaged where tiles meet, as the client builds it without an export; or the original engine's own terrain
/// heights when every player has the same.
/// </summary>
public sealed class TerrainHeights
{
    private readonly float[,] _heights;
    private readonly float _spacing;

    public TerrainHeights(float[,] heights, float spacing)
    {
        _heights = heights;
        _spacing = spacing;
    }

    public static TerrainHeights FromSurface(PlanetSurface surface)
    {
        var cellsPerTile = Math.Max(2, surface.TileCellsX);
        var heights = new float[surface.TilesX * cellsPerTile + 1, surface.TilesY * cellsPerTile + 1];
        for (var vx = 0; vx < heights.GetLength(0); vx++)
        {
            for (var vy = 0; vy < heights.GetLength(1); vy++)
            {
                float sum = 0;
                var count = 0;
                foreach (var tx in Touching(vx, cellsPerTile, surface.TilesX))
                {
                    foreach (var ty in Touching(vy, cellsPerTile, surface.TilesY))
                    {
                        sum += surface.Tile(tx, ty).Height;
                        count++;
                    }
                }
                heights[vx, vy] = count == 0 ? 0 : sum / count;
            }
        }
        return new TerrainHeights(heights, surface.TileSize / cellsPerTile);
    }

    /// <summary>
    /// The heights the original engine reported (MachPhysPlanetSurface::terrainHeight), as tools/classic-export
    /// writes them: int32 columns, int32 rows, float32 spacing, then the heights row by row.
    /// </summary>
    public static TerrainHeights FromExport(byte[] data)
    {
        var columns = BitConverter.ToInt32(data, 0);
        var rows = BitConverter.ToInt32(data, 4);
        var spacing = BitConverter.ToSingle(data, 8);
        var heights = new float[columns, rows];
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
                heights[x, y] = BitConverter.ToSingle(data, 12 + 4 * (y * columns + x));
        return new TerrainHeights(heights, spacing);
    }

    /// <summary>The tiles a vertex belongs to along one axis: one inside a tile, two on the edge between tiles.</summary>
    private static IEnumerable<int> Touching(int vertex, int cellsPerTile, int tiles)
    {
        if (vertex % cellsPerTile == 0)
        {
            if (vertex / cellsPerTile - 1 >= 0)
                yield return vertex / cellsPerTile - 1;
            if (vertex / cellsPerTile < tiles)
                yield return vertex / cellsPerTile;
        }
        else
        {
            yield return vertex / cellsPerTile;
        }
    }

    /// <summary>A fingerprint of every height, so that players whose ground differs find out at once (a desync).</summary>
    public long Fingerprint()
    {
        var hash = 14695981039346656037UL;
        foreach (var height in _heights)
        {
            hash ^= (uint)BitConverter.SingleToInt32Bits(height);
            hash *= 1099511628211UL;
        }
        return (long)hash;
    }

    /// <summary>The ground's height at a point, between the four vertices round it.</summary>
    public float At(Vector2 p)
    {
        var (columns, rows) = (_heights.GetLength(0), _heights.GetLength(1));
        var fx = Math.Clamp(p.X / _spacing, 0, columns - 1.001f);
        var fy = Math.Clamp(p.Y / _spacing, 0, rows - 1.001f);
        var (x, y) = ((int)fx, (int)fy);
        var (tx, ty) = (fx - x, fy - y);
        var top = _heights[x, y] + (_heights[x + 1, y] - _heights[x, y]) * tx;
        var bottom = _heights[x, y + 1] + (_heights[x + 1, y + 1] - _heights[x, y + 1]) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>
    /// Where along a straight line through the air the ground first rises above it, as a share of the way
    /// (0 to 1), or null when the line stays clear; sampled every <paramref name="step"/> metres, leaving out the
    /// ends, where shooter and target stand.
    /// </summary>
    public float? FirstBlocked(Vector3 from, Vector3 to, float step = 2, float ends = 1)
    {
        var flat = new Vector2(to.X - from.X, to.Y - from.Y);
        var length = flat.Length();
        if (length <= 2 * ends)
            return null;
        var samples = (int)MathF.Ceiling(length / step);
        for (var i = 1; i <= samples; i++)
        {
            var t = i / (float)samples;
            if (t * length < ends || (1 - t) * length < ends)
                continue;
            var at = Vector3.Lerp(from, to, t);
            if (At(new Vector2(at.X, at.Y)) > at.Z)
                return t;
        }
        return null;
    }
}
