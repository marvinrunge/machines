using Godot;

namespace Machines.Game;

/// <summary>
/// How much of the sky each point of the ground sees, worked out from the heights once a map is loaded: in eight
/// directions, how high the land rises about it within ninety metres. The floor of a canyon, a hollow between hills, the
/// foot of a cliff see a narrow strip of sky and get less of its light; open plains and ridges see all of it. The painted
/// ground reads it as ambient occlusion (TerrainMaterials), which dims the light from the sky alone: where the sun reaches
/// into a canyon it shines as brightly as anywhere. Too large for the screen's own occlusion, which works within a few
/// metres.
/// </summary>
public static class SkyView
{
    /// <summary>Metres a texel of the map covers, at most; larger maps get coarser texels, never more than 1024 a side.</summary>
    private const float Cell = 4;

    /// <summary>How far out the land is looked at, metres, in steps that grow with the distance.</summary>
    private static readonly float[] Steps = [3, 6, 10, 16, 25, 40, 60, 90];

    private const int Directions = 8;

    private static ImageTexture? _texture;

    /// <summary>
    /// Adds the shader values the ground reads (the map, and the size of the ground it spans), once, before a shader that
    /// reads them is made: white, all sky, until a map is worked out.
    /// </summary>
    public static void EnsureGlobals()
    {
        if (_texture is not null)
            return;
        var white = Image.CreateEmpty(1, 1, false, Image.Format.R8);
        white.Fill(Colors.White);
        _texture = ImageTexture.CreateFromImage(white);
        RenderingServer.GlobalShaderParameterAdd("sky_view", RenderingServer.GlobalShaderParameterType.Sampler2D, _texture);
        RenderingServer.GlobalShaderParameterAdd("sky_view_size", RenderingServer.GlobalShaderParameterType.Vec2, Vector2.One);
    }

    /// <summary>Works the map out for <paramref name="ground"/>, or sets it back to all sky without one.</summary>
    public static void Bake(HeightField? ground)
    {
        EnsureGlobals();
        if (ground is null)
        {
            var white = Image.CreateEmpty(1, 1, false, Image.Format.R8);
            white.Fill(Colors.White);
            Set(white, Vector2.One);
            return;
        }
        var cell = Mathf.Max(Cell, Mathf.Max(ground.Width, ground.Depth) / 1024);
        var (columns, rows) = (Math.Max(1, (int)Mathf.Ceil(ground.Width / cell)), Math.Max(1, (int)Mathf.Ceil(ground.Depth / cell)));
        var open = new byte[columns * rows];
        var ways = Enumerable.Range(0, Directions).Select(d => new Vector2(Mathf.Cos(d * Mathf.Tau / Directions), Mathf.Sin(d * Mathf.Tau / Directions))).ToArray();
        Parallel.For(0, rows, row =>
        {
            for (var column = 0; column < columns; column++)
            {
                var (x, y) = ((column + 0.5f) * cell, (row + 0.5f) * cell);
                // A little above the ground, so its own bumps do not hide the sky.
                var here = ground.At(x, y) + 0.5f;
                var sum = 0f;
                foreach (var way in ways)
                {
                    var rise = 0f;
                    foreach (var step in Steps)
                    {
                        var (sx, sy) = (x + way.X * step, y + way.Y * step);
                        if (sx < 0 || sy < 0 || sx > ground.Width || sy > ground.Depth)
                            break;
                        rise = Mathf.Max(rise, (ground.At(sx, sy) - here) / step);
                    }
                    // The sky above the horizon that way: all of it on the flat, none behind a wall.
                    sum += 1 - Mathf.Sin(Mathf.Atan(rise));
                }
                open[row * columns + column] = (byte)Mathf.RoundToInt(Mathf.Clamp(sum / Directions, 0, 1) * 255);
            }
        });
        var image = Image.CreateFromData(columns, rows, false, Image.Format.R8, open);
        Set(image, new Vector2(columns * cell, rows * cell));
    }

    private static void Set(Image image, Vector2 size)
    {
        _texture = ImageTexture.CreateFromImage(image);
        RenderingServer.GlobalShaderParameterSet("sky_view", _texture);
        RenderingServer.GlobalShaderParameterSet("sky_view_size", size);
    }
}
