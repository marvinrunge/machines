using System.Runtime.CompilerServices;
using Godot;

namespace Machines.Game;

/// <summary>
/// A building's pads (the plates before a factory's doors, at a mine's or smelter's loading bays) seen from above, to
/// print onto the ground. The original laid each pad on the terrain as a flat plate, tilted to rest on it, when it put
/// a building down (MachPhysConstruction::layPadsOnTerrain); a plate still floats over a crest and sinks into a hollow.
/// Printed, the pad follows the ground wherever it is drawn. The picture is the pad as each look draws it: its own
/// triangles and material, looked at straight down, the topmost surface wherever several overlap. It is painted away
/// from the main thread, once for every pad mesh (a model's pads in one look and colour).
/// </summary>
public static class PadPrint
{
    /// <summary>The parts the original laid on the ground (MachPhysConstruction::isPadName): <c>fpad</c> and <c>pad…</c>.</summary>
    public static bool IsPad(string name) => name == "fpad" || name.StartsWith("pad", StringComparison.Ordinal);

    /// <summary>The picture of a pad and the rectangle it covers in the pad part's own x and z, in metres.</summary>
    public sealed record Print(Texture2D Texture, Rect2 Area);

    /// <summary>
    /// How finely a pad is printed, pixels per metre: as finely as its texture is laid on it, within these bounds;
    /// and the largest picture.
    /// </summary>
    private const float FinestPixelsPerMetre = 48, CoarsestPixelsPerMetre = 12;
    private const int MaxSize = 512;

    /// <summary>A print being painted, and then made.</summary>
    private sealed class Job
    {
        public required Task<Painting?> Work { get; init; }
        public Print? Print { get; set; }
        public bool Done { get; set; }
    }

    /// <summary>A painted picture, RGBA8, and the rectangle it covers.</summary>
    private sealed record Painting(byte[] Data, int Width, int Height, Rect2 Area);

    // Pads are shared by every building of a model and look, so each is printed once.
    private static readonly ConditionalWeakTable<Mesh, Job> Jobs = new();

    /// <summary>
    /// The pad <paramref name="mesh"/> seen from above. The first call starts painting it; true once that is done,
    /// with the print, or null when the pad has nothing to show. Call from the main thread.
    /// </summary>
    public static bool TryGet(Mesh mesh, out Print? print)
    {
        if (!Jobs.TryGetValue(mesh, out var job))
        {
            job = new Job { Work = Start(mesh) };
            Jobs.AddOrUpdate(mesh, job);
        }
        if (!job.Done && job.Work.IsCompleted)
        {
            // The texture is made here: the rendering server is fed from the main thread.
            if (job.Work is { IsCompletedSuccessfully: true, Result: { } painting })
                job.Print = new Print(ImageTexture.CreateFromImage(Image.CreateFromData(painting.Width, painting.Height, false, Image.Format.Rgba8, painting.Data)),
                    painting.Area);
            job.Done = true;
        }
        print = job.Print;
        return job.Done;
    }

    /// <summary>A surface's triangles and what they are painted with: texture, tint, vertex colours, how they let light through.</summary>
    private sealed record Surface(Vector3[] Vertices, Vector2[] Uvs, Color[] Shades, int[] Indices, Task<Picture?>? Texture, Color Tint,
        bool Shaded, bool SrgbShades, BaseMaterial3D.TransparencyEnum Transparency, float Threshold);

    // A texture's pixels come back from the graphics card once, and are unpacked on a worker.
    private static readonly ConditionalWeakTable<Texture2D, Task<Picture?>> Pictures = new();

    private static Task<Picture?> PictureOf(Texture2D texture)
    {
        if (Pictures.TryGetValue(texture, out var known))
            return known;
        var image = texture.GetImage();
        var unpacking = image is null || image.IsEmpty() ? Task.FromResult<Picture?>(null) : Task.Run<Picture?>(() =>
        {
            if (image.IsCompressed())
                image.Decompress();
            image.ClearMipmaps();
            image.Convert(Image.Format.Rgba8);
            return new Picture(image.GetData(), image.GetWidth(), image.GetHeight());
        });
        Pictures.AddOrUpdate(texture, unpacking);
        return unpacking;
    }

    /// <summary>Reads the mesh and its materials here, and paints on a worker.</summary>
    private static Task<Painting?> Start(Mesh mesh)
    {
        var surfaces = new List<Surface>();
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not StandardMaterial3D material)
                continue;
            var arrays = mesh.SurfaceGetArrays(s);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (vertices.Length < 3)
                continue;
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var shades = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            if (indices.Length == 0)
                indices = Enumerable.Range(0, vertices.Length).ToArray();
            // Vertex colours tint as the material says: given in sRGB, or linear (the remade models' occlusion).
            var texture = material.AlbedoTexture is { } albedo && uvs.Length == vertices.Length ? PictureOf(albedo) : null;
            surfaces.Add(new Surface(vertices, uvs, shades, indices, texture, material.AlbedoColor,
                material.VertexColorUseAsAlbedo && shades.Length == vertices.Length, material.VertexColorIsSrgb, material.Transparency,
                material.AlphaScissorThreshold));
        }
        return surfaces.Count == 0 ? Task.FromResult<Painting?>(null) : Task.Run(() => Paint(surfaces));
    }

    /// <summary>A texture's pixels, RGBA8.</summary>
    private sealed record Picture(byte[] Data, int Width, int Height);

    private static Painting? Paint(List<Surface> surfaces)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var v in surfaces.SelectMany(s => s.Vertices))
            (minX, maxX, minZ, maxZ) = (Math.Min(minX, v.X), Math.Max(maxX, v.X), Math.Min(minZ, v.Z), Math.Max(maxZ, v.Z));
        if (maxX - minX < 0.1f || maxZ - minZ < 0.1f)
            return null;

        // How many of the textures' pixels fall on a square metre of ground.
        double texels = 0, ground = 0;
        foreach (var surface in surfaces)
        {
            if (surface.Texture?.Result is not { } picture)
                continue;
            for (var t = 0; t + 2 < surface.Indices.Length; t += 3)
            {
                var (a, b, c) = (surface.Indices[t], surface.Indices[t + 1], surface.Indices[t + 2]);
                var (pa, pb, pc) = (surface.Vertices[a], surface.Vertices[b], surface.Vertices[c]);
                var (ua, ub, uc) = (surface.Uvs[a], surface.Uvs[b], surface.Uvs[c]);
                ground += Math.Abs((pb.X - pa.X) * (pc.Z - pa.Z) - (pc.X - pa.X) * (pb.Z - pa.Z)) / 2;
                texels += Math.Abs((ub.X - ua.X) * (uc.Y - ua.Y) - (uc.X - ua.X) * (ub.Y - ua.Y)) / 2 * picture.Width * picture.Height;
            }
        }
        var perMetre = ground > 0.01 ? Math.Clamp((float)Math.Sqrt(texels / ground), CoarsestPixelsPerMetre, FinestPixelsPerMetre) : CoarsestPixelsPerMetre;
        var width = Math.Clamp((int)MathF.Ceiling((maxX - minX) * perMetre), 8, MaxSize);
        var height = Math.Clamp((int)MathF.Ceiling((maxZ - minZ) * perMetre), 8, MaxSize);
        var (scaleX, scaleZ) = (width / (maxX - minX), height / (maxZ - minZ));

        // Red, green, blue and alpha of each pixel, and the height of the surface it shows.
        var rgba = new float[width * height * 4];
        var top = new float[width * height];
        Array.Fill(top, float.NegativeInfinity);
        foreach (var surface in surfaces)
        {
            var (vertices, uvs, shades, indices) = (surface.Vertices, surface.Uvs, surface.Shades, surface.Indices);
            var picture = surface.Texture?.Result;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
                // Pixel coordinates of the corners: x across the picture, z down it.
                float ax = (vertices[a].X - minX) * scaleX, ay = (vertices[a].Z - minZ) * scaleZ;
                float bx = (vertices[b].X - minX) * scaleX, by = (vertices[b].Z - minZ) * scaleZ;
                float cx = (vertices[c].X - minX) * scaleX, cy = (vertices[c].Z - minZ) * scaleZ;
                var area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                if (Math.Abs(area) < 1e-6f)
                    continue;
                var x0 = Math.Max(0, (int)MathF.Floor(Math.Min(ax, Math.Min(bx, cx))));
                var x1 = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
                var y0 = Math.Max(0, (int)MathF.Floor(Math.Min(ay, Math.Min(by, cy))));
                var y1 = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(ay, Math.Max(by, cy))));
                // The corners' weights at a pixel's middle change by these a pixel to the right.
                var stepA = (by - cy) / area;
                var stepB = (cy - ay) / area;
                for (var y = y0; y <= y1; y++)
                {
                    var (px, py) = (x0 + 0.5f, y + 0.5f);
                    var wa = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / area;
                    var wb = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / area;
                    for (var x = x0; x <= x1; x++, wa += stepA, wb += stepB)
                    {
                        var wc = 1 - wa - wb;
                        if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f)
                            continue;
                        var i = y * width + x;
                        var up = wa * vertices[a].Y + wb * vertices[b].Y + wc * vertices[c].Y;
                        if (up < top[i])
                            continue;
                        var colour = surface.Tint;
                        if (picture is not null)
                            colour *= Sample(picture, uvs[a] * wa + uvs[b] * wb + uvs[c] * wc);
                        if (surface.Shaded)
                        {
                            var shade = shades[a] * wa + shades[b] * wb + shades[c] * wc;
                            colour *= surface.SrgbShades ? shade : shade.LinearToSrgb();
                        }
                        if (surface.Transparency == BaseMaterial3D.TransparencyEnum.Disabled)
                            colour.A = 1;
                        else if (surface.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor)
                            colour.A = colour.A >= surface.Threshold ? 1 : 0;
                        if (colour.A <= 0.01f)
                            continue;
                        top[i] = up;
                        (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = (colour.R, colour.G, colour.B, colour.A);
                    }
                }
            }
        }

        var shown = 0;
        for (var i = 0; i < top.Length; i++)
            if (rgba[4 * i + 3] > 0)
                shown++;
        if (shown == 0)
            return null;
        if (shown < top.Length)
            Bleed(rgba, width, height);
        var bytes = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i++)
            bytes[i] = (byte)Math.Clamp((int)(rgba[i] * 255 + 0.5f), 0, 255);
        return new Painting(bytes, width, height, new Rect2(minX, minZ, maxX - minX, maxZ - minZ));
    }

    /// <summary>The picture's colour at <paramref name="uv"/>, between its four nearest pixels, repeating.</summary>
    private static Color Sample(Picture picture, Vector2 uv)
    {
        var (w, h, data) = (picture.Width, picture.Height, picture.Data);
        var x = uv.X * w - 0.5f;
        var y = uv.Y * h - 0.5f;
        var (ix, iy) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
        var (fx, fy) = (x - ix, y - iy);
        var (x0, x1) = (Wrap(ix, w), Wrap(ix + 1, w));
        var (row0, row1) = (Wrap(iy, h) * w, Wrap(iy + 1, h) * w);
        int o00 = 4 * (row0 + x0), o10 = 4 * (row0 + x1), o01 = 4 * (row1 + x0), o11 = 4 * (row1 + x1);
        float w00 = (1 - fx) * (1 - fy) / 255, w10 = fx * (1 - fy) / 255, w01 = (1 - fx) * fy / 255, w11 = fx * fy / 255;
        return new Color(
            data[o00] * w00 + data[o10] * w10 + data[o01] * w01 + data[o11] * w11,
            data[o00 + 1] * w00 + data[o10 + 1] * w10 + data[o01 + 1] * w01 + data[o11 + 1] * w11,
            data[o00 + 2] * w00 + data[o10 + 2] * w10 + data[o01 + 2] * w01 + data[o11 + 2] * w11,
            data[o00 + 3] * w00 + data[o10 + 3] * w10 + data[o01 + 3] * w01 + data[o11 + 3] * w11);

        static int Wrap(int i, int n) => i >= 0 && i < n ? i : ((i % n) + n) % n;
    }

    /// <summary>
    /// Gives the clear pixels the colour of their nearest coloured neighbours, a few pixels out, so the edge of the
    /// print does not darken where the picture is filtered.
    /// </summary>
    private static void Bleed(float[] rgba, int width, int height)
    {
        // A pixel is coloured when it is shown or has been given a colour by an earlier pass.
        var coloured = new bool[width * height];
        for (var i = 0; i < coloured.Length; i++)
            coloured[i] = rgba[4 * i + 3] > 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var given = new List<(int Index, float R, float G, float B)>();
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (coloured[y * width + x])
                        continue;
                    float r = 0, g = 0, b = 0;
                    var count = 0;
                    for (var ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                        for (var nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                        {
                            var n = ny * width + nx;
                            if (!coloured[n])
                                continue;
                            (r, g, b) = (r + rgba[4 * n], g + rgba[4 * n + 1], b + rgba[4 * n + 2]);
                            count++;
                        }
                    if (count > 0)
                        given.Add((y * width + x, r / count, g / count, b / count));
                }
            }
            foreach (var (i, r, g, b) in given)
            {
                (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2]) = (r, g, b);
                coloured[i] = true;
            }
        }
    }
}
