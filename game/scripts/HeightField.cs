using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// Ground heights from the planet surface (<c>.psf</c>). The original tile meshes are not available, but each
/// tile records the height it was placed at: tiles are flat at that height inside, and the vertices on a
/// tile edge take the average of the tiles that meet there, giving short ramps between levels.
/// </summary>
public sealed class HeightField
{
    private readonly float[,] _heights;

    public HeightField(PlanetSurface surface)
    {
        CellsPerTile = Math.Max(2, surface.TileCellsX);
        Spacing = surface.TileSize / CellsPerTile;
        Width = surface.Width;
        Depth = surface.Depth;
        _heights = new float[surface.TilesX * CellsPerTile + 1, surface.TilesY * CellsPerTile + 1];

        for (var vx = 0; vx < _heights.GetLength(0); vx++)
        {
            for (var vy = 0; vy < _heights.GetLength(1); vy++)
            {
                var sum = 0f;
                var count = 0;
                foreach (var tx in Touching(vx, surface.TilesX))
                {
                    foreach (var ty in Touching(vy, surface.TilesY))
                    {
                        sum += surface.Tile(tx, ty).Height;
                        count++;
                    }
                }
                _heights[vx, vy] = count == 0 ? 0 : sum / count;
            }
        }
    }

    /// <summary>The field of the match being played; flat until a match sets it.</summary>
    public static HeightField? Current { get; set; }

    public int CellsPerTile { get; }
    public float Spacing { get; }
    public float Width { get; }
    public float Depth { get; }
    public int VerticesX => _heights.GetLength(0);
    public int VerticesY => _heights.GetLength(1);

    public float Vertex(int vx, int vy) => _heights[vx, vy];

    /// <summary>Height at an original ground position (x, y), interpolated between vertices.</summary>
    public float At(float x, float y)
    {
        var gx = Math.Clamp(x / Spacing, 0, VerticesX - 1.001f);
        var gy = Math.Clamp(y / Spacing, 0, VerticesY - 1.001f);
        var ix = (int)gx;
        var iy = (int)gy;
        var fx = gx - ix;
        var fy = gy - iy;
        var top = Mathf.Lerp(_heights[ix, iy], _heights[ix + 1, iy], fx);
        var bottom = Mathf.Lerp(_heights[ix, iy + 1], _heights[ix + 1, iy + 1], fx);
        return Mathf.Lerp(top, bottom, fy);
    }

    /// <summary>Where a ray first meets the ground, marching in short steps and refining the last one.</summary>
    public Vector3? Hit(Vector3 origin, Vector3 direction, float maxDistance = 8000)
    {
        const float step = 2f;
        var previous = origin;
        for (var t = step; t < maxDistance; t += step)
        {
            var point = origin + direction * t;
            if (point.Y <= At(point.X, point.Z))
            {
                var low = previous;
                var high = point;
                for (var i = 0; i < 12; i++)
                {
                    var middle = (low + high) / 2;
                    if (middle.Y <= At(middle.X, middle.Z)) high = middle;
                    else low = middle;
                }
                return high;
            }
            previous = point;
            if (point.Y < -100)
                break;
        }
        return null;
    }

    private IEnumerable<int> Touching(int vertex, int tiles)
    {
        var tile = vertex / CellsPerTile;
        if (tile < tiles)
            yield return tile;
        if (vertex % CellsPerTile == 0 && tile - 1 >= 0)
            yield return tile - 1;
    }
}
