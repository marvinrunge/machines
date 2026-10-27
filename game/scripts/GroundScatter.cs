using Godot;

namespace Machines.Game;

/// <summary>
/// What lies and grows on the ground in the remake look: tufts of grass swaying in the wind on grass and soil, and
/// pebbles and stones on rock, sand, gravel, mud and snow, each in the colours of the ground beneath it.
/// They are scattered over the terrain's triangles by what the material map has where each triangle lies
/// (<see cref="TerrainMaterials"/>), or else by the layer the ground detail gave its tile (<see cref="TerrainDetail"/>),
/// the same way every time on a planet, never on steep slopes, water, roads or metal, and drawn in 64 m pieces that
/// fade out with distance. Buildings clear the ground they stand on (<see cref="ClearUnder"/>).
/// </summary>
public partial class GroundScatter : Node3D
{
    private const float ChunkSize = 64;

    /// <summary>What a layer carries: grass tufts and stones per square metre.</summary>
    private static (float Grass, float Stones) Density(string layer) => layer switch
    {
        "grass" => (0.45f, 0.02f),
        "soil" => (0.12f, 0.05f),
        "sand" => (0, 0.03f),
        "laterite" => (0.03f, 0.06f),
        "rock" => (0.0f, 0.12f),
        "cliff" => (0.0f, 0.08f),
        "gravel" => (0.0f, 0.15f),
        "mud" => (0.02f, 0.05f),
        "snow" => (0.0f, 0.03f),
        _ => (0, 0),
    };

    private readonly List<MultiMeshInstance3D> _pieces = [];

    /// <summary>
    /// The scatter for the ground's surfaces: those the ground detail sorted into layers, as their layer has it, and those
    /// painted from the material map, as the map has it where each triangle lies; null for none.
    /// </summary>
    public static GroundScatter? Create(TerrainGeometry.Part[] ground, IReadOnlyDictionary<int, (string Layer, Color Colour, string Texture)> surfaces,
        TerrainMaterials.Ground? painted, string planet, int seed)
    {
        // Scanned stones where the planet has them (Scenery), else coarse balls; and the bushes and dead trees between.
        var scans = Scenery.Stones(planet);
        var decoration = Scenery.Decoration(planet);
        // What grows on a triangle, from its middle and the shade of its corners: grass tufts and stones per square
        // metre, and their colours.
        var work = new List<(int Surface, Func<Vector3, Color, (float Grass, float Stones, Color GrassColour, Color StoneColour)> Cover)>();
        foreach (var (surface, (layer, colour, texture)) in surfaces)
        {
            var (grassDensity, stoneDensity) = Density(layer);
            if (grassDensity + stoneDensity == 0 || texture.Contains("road") || texture.Contains("walk") || texture.Contains("strip"))
                continue;
            if (painted is not null)
                // On a painted planet the tiles it left (shores, tiles with see-through edges) follow its map too: by
                // their own layer they stood out as rectangles thick with stones.
                work.Add((surface, (middle, _) => painted.At(middle.X, middle.Z)));
            else
                // The tile's corners shade its colour as they shade its picture.
                work.Add((surface, (_, shade) => (grassDensity, stoneDensity, colour * shade, colour * shade * 0.85f)));
        }
        foreach (var surface in painted?.Surfaces ?? new HashSet<int>())
            if (!painted!.Roads.Contains(surface))
                work.Add((surface, (middle, _) => painted.At(middle.X, middle.Z)));

        // Each surface is scattered on its own, side by side, with dice of its own: the same each time.
        var grassOf = new Dictionary<Vector2I, List<(Transform3D, Color)>>[work.Count];
        var stonesOf = new Dictionary<Vector2I, List<(Transform3D, Color)>>[work.Count];
        var plantsOf = new Dictionary<Vector2I, List<(Transform3D, Color)>>[work.Count, decoration.Length];
        Parallel.For(0, work.Count, w =>
        {
            var (surface, cover) = work[w];
            var random = new Random(seed + surface);
            var (grass, stones) = (grassOf[w] = [], stonesOf[w] = []);
            for (var d = 0; d < decoration.Length; d++)
                plantsOf[w, d] = [];
            var (vertices, indices, colours) = (ground[surface].Vertices, ground[surface].Indices, ground[surface].Colours);
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var (a, b, c) = (vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
                var normal = (b - a).Cross(c - a);
                var area = normal.Length() / 2;
                normal = normal.Normalized();
                if (Mathf.Abs(normal.Y) < 0.88f || area < 0.01f)
                    continue;
                if (normal.Y < 0)
                    normal = -normal;
                var shade = colours is null ? Colors.White : (colours[indices[t]] + colours[indices[t + 1]] + colours[indices[t + 2]]) / 3;
                // The most the triangle carries anywhere on it (a big one may cross a border); each point is then kept
                // by what grows just there, so the cover thins out over the ground's soft edges instead of stopping at
                // the triangle's.
                var corners = new[] { cover(a, shade), cover(b, shade), cover(c, shade), cover((a + b + c) / 3, shade) };
                var (grassMost, stonesMost) = (corners.Max(k => k.Grass), corners.Max(k => k.Stones));
                for (var d = 0; d < decoration.Length; d++)
                {
                    var (_, perGrass, perGround) = decoration[d];
                    float Density((float Grass, float Stones, Color, Color) k) => perGrass * k.Grass + (k.Stones > 0 ? perGround : 0);
                    var most = corners.Max(Density);
                    Scatter(random, a, b, c, Count(random, area * most), plantsOf[w, d], (at, r) =>
                        new Transform3D(new Basis(Vector3.Up, (float)r.NextDouble() * Mathf.Tau).Scaled(Vector3.One * (0.8f + 0.5f * (float)r.NextDouble())), at - Vector3.Up * 0.1f),
                        (at, r) => r.NextDouble() * most < Density(cover(at, shade)) ? Colors.White : null);
                }
                var (grassCount, stoneCount) = (Count(random, area * grassMost), Count(random, area * stonesMost));
                if (grassCount + stoneCount == 0)
                    continue;
                Scatter(random, a, b, c, grassCount, grass, (at, r) =>
                    new Transform3D(new Basis(Vector3.Up, (float)r.NextDouble() * Mathf.Tau).Scaled(Vector3.One * (0.7f + 0.6f * (float)r.NextDouble())), at),
                    (at, r) => cover(at, shade) is var here && r.NextDouble() * grassMost < here.Grass ? here.GrassColour.SrgbToLinear() : null);
                Scatter(random, a, b, c, stoneCount, stones, (at, r) =>
                {
                    // Mostly pebbles, now and then a stone; lying along the ground, sunk a little into it.
                    var size = r.NextDouble() < 0.85 ? 0.12f + 0.25f * (float)r.NextDouble() : 0.4f + 0.35f * (float)r.NextDouble();
                    var up = normal;
                    var side = up.Cross(Vector3.Forward).Normalized();
                    var basis = new Basis(side, up, side.Cross(up)).Rotated(up, (float)r.NextDouble() * Mathf.Tau);
                    // A scan keeps its own shape; a ball is flattened into a pebble.
                    var shape = scans.Length > 0 ? Vector3.One * size
                        : new Vector3(size, size * (0.5f + 0.3f * (float)r.NextDouble()), size * (0.8f + 0.4f * (float)r.NextDouble()));
                    return new Transform3D(basis.Scaled(shape), at - up * size * 0.15f);
                }, (at, r) => cover(at, shade) is var here && r.NextDouble() * stonesMost < here.Stones ? here.StoneColour : null);
            }
        });
        var (grass, stones) = (Merge(grassOf), Merge(stonesOf));
        var plants = Enumerable.Range(0, decoration.Length).Select(d => Merge(Enumerable.Range(0, work.Count).Select(w => plantsOf[w, d]).ToArray())).ToArray();
        if (grass.Count == 0 && stones.Count == 0 && plants.All(p => p.Count == 0))
            return null;

        var scatter = new GroundScatter { Name = "GroundScatter" };
        var tuft = Tuft();
        var grassMaterial = new ShaderMaterial { Shader = GrassShader.Value };
        grassMaterial.SetShaderParameter("blades", Blades.Value);
        foreach (var (chunk, items) in grass)
            scatter.AddPiece(tuft, grassMaterial, items, 230, shadows: false);
        if (scans.Length > 0)
        {
            // The scans in their own pictures, taking a third of the ground's colour; each brought to a unit across first.
            // Stones cast no shadows: thousands of them, smaller than a shadow map's texel further off, flickered in and
            // out as the camera moved (the ambient occlusion darkens the ground at their feet).
            foreach (var (chunk, items) in stones)
                for (var v = 0; v < scans.Length; v++)
                {
                    var unit = scans[v].Unit;
                    scatter.AddPiece(scans[v].Mesh, null, items.Where((_, i) => i % scans.Length == v)
                        .Select(s => (new Transform3D(s.Item1.Basis.Scaled(Vector3.One * unit), s.Item1.Origin), s.Item2)).ToList(), 320, shadows: false);
                }
        }
        else
        {
            var stoneMeshes = Enumerable.Range(0, 3).Select(i => Stone(new Random(seed + i))).ToArray();
            var stoneMaterial = new StandardMaterial3D
            {
                AlbedoTexture = TerrainDetail.LayerAlbedo("rock"),
                Uv1Triplanar = true,
                Uv1Scale = Vector3.One * 0.7f,
                VertexColorUseAsAlbedo = true,
                VertexColorIsSrgb = true,
                Roughness = 0.9f,
            };
            foreach (var (chunk, items) in stones)
                for (var v = 0; v < stoneMeshes.Length; v++)
                    scatter.AddPiece(stoneMeshes[v], stoneMaterial, items.Where((_, i) => i % stoneMeshes.Length == v).ToList(), 320, shadows: false);
        }
        // Bushes are seen a few hundred metres off, dead trees as far as the trees.
        for (var d = 0; d < decoration.Length; d++)
        {
            var model = decoration[d].Model;
            foreach (var (chunk, items) in plants[d])
                scatter.AddPiece(model.Mesh, null, items, model.Bounds.Size.Y > 4 ? 900 : 300, shadows: true);
        }
        return scatter;
    }

    private static Dictionary<Vector2I, List<(Transform3D, Color)>> Merge(Dictionary<Vector2I, List<(Transform3D, Color)>>[] parts)
    {
        var all = new Dictionary<Vector2I, List<(Transform3D, Color)>>();
        foreach (var part in parts)
        {
            foreach (var (chunk, items) in part)
            {
                if (all.TryGetValue(chunk, out var list))
                    list.AddRange(items);
                else
                    all[chunk] = items;
            }
        }
        return all;
    }

    /// <summary>About <paramref name="expected"/>: as many whole, and one more as often as the rest says.</summary>
    private static int Count(Random random, float expected) => (int)expected + (random.NextDouble() < expected - (int)expected ? 1 : 0);

    /// <summary><paramref name="count"/> random points on a triangle; those kept, with their colour, sorted into chunks.</summary>
    private static void Scatter(Random random, Vector3 a, Vector3 b, Vector3 c, int count, Dictionary<Vector2I, List<(Transform3D, Color)>> into,
        Func<Vector3, Random, Transform3D> place, Func<Vector3, Random, Color?> keep)
    {
        for (var i = 0; i < count; i++)
        {
            var (u, v) = ((float)random.NextDouble(), (float)random.NextDouble());
            if (u + v > 1)
                (u, v) = (1 - u, 1 - v);
            var at = a + (b - a) * u + (c - a) * v;
            if (keep(at, random) is not { } tint)
                continue;
            var chunk = new Vector2I(Mathf.FloorToInt(at.X / ChunkSize), Mathf.FloorToInt(at.Z / ChunkSize));
            if (!into.TryGetValue(chunk, out var list))
                into[chunk] = list = [];
            var variation = 0.85f + 0.3f * (float)random.NextDouble();
            list.Add((place(at, random), new Color(tint.R * variation, tint.G * variation, tint.B * variation)));
        }
    }

    /// <summary>One chunk's instances of a mesh, in the material given or the mesh's own; null for none.</summary>
    private MultiMeshInstance3D? AddPiece(Mesh mesh, Material? material, List<(Transform3D Transform, Color Colour)> items, float reach, bool shadows)
    {
        if (items.Count == 0)
            return null;
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = items.Count,
        };
        // All instances in one go: per instance its basis rows and origin (12 floats), then its colour (4).
        var buffer = new float[items.Count * 16];
        for (var i = 0; i < items.Count; i++)
        {
            var (t, c) = (items[i].Transform, items[i].Colour);
            var o = i * 16;
            (buffer[o], buffer[o + 1], buffer[o + 2], buffer[o + 3]) = (t.Basis.Row0.X, t.Basis.Row0.Y, t.Basis.Row0.Z, t.Origin.X);
            (buffer[o + 4], buffer[o + 5], buffer[o + 6], buffer[o + 7]) = (t.Basis.Row1.X, t.Basis.Row1.Y, t.Basis.Row1.Z, t.Origin.Y);
            (buffer[o + 8], buffer[o + 9], buffer[o + 10], buffer[o + 11]) = (t.Basis.Row2.X, t.Basis.Row2.Y, t.Basis.Row2.Z, t.Origin.Z);
            (buffer[o + 12], buffer[o + 13], buffer[o + 14], buffer[o + 15]) = (c.R, c.G, c.B, c.A);
        }
        multimesh.Buffer = buffer;
        var piece = new MultiMeshInstance3D
        {
            Multimesh = multimesh,
            MaterialOverride = material,
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeEnd = reach,
            VisibilityRangeEndMargin = reach * 0.15f,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
        };
        AddChild(piece);
        _pieces.Add(piece);
        return piece;
    }

    /// <summary>Takes away the grass and stones within a rectangle on the ground (a building's footprint, in metres).</summary>
    public void ClearUnder(Vector2 min, Vector2 max)
    {
        foreach (var piece in _pieces)
        {
            var aabb = piece.Multimesh.GetAabb();
            if (aabb.End.X < min.X || aabb.Position.X > max.X || aabb.End.Z < min.Y || aabb.Position.Z > max.Y)
                continue;
            for (var i = 0; i < piece.Multimesh.InstanceCount; i++)
            {
                var at = piece.Multimesh.GetInstanceTransform(i).Origin;
                if (at.X >= min.X && at.X <= max.X && at.Z >= min.Y && at.Z <= max.Y)
                    piece.Multimesh.SetInstanceTransform(i, new Transform3D(Basis.FromScale(Vector3.Zero), at));
            }
        }
    }

    /// <summary>
    /// A tuft: five quads of blades, 0.7 m wide, rising from its middle and leaning outwards like a fountain, so they
    /// show from the strategy camera above as well as from the side; white, for the instance colour to tint.
    /// </summary>
    private static Mesh Tuft()
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        for (var k = 0; k < 5; k++)
        {
            var angle = k * Mathf.Tau / 5;
            var outward = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            var across = new Vector3(-outward.Z, 0, outward.X) * 0.35f;
            var top = outward * 0.3f + Vector3.Up * 0.45f;
            Vector3[] corners = [-across * 0.6f, across * 0.6f, across + top, -across + top];
            Vector2[] uvs = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
            foreach (var i in new[] { 0, 1, 2, 0, 2, 3 })
            {
                tool.SetUV(uvs[i]);
                tool.SetNormal(Vector3.Up);
                tool.SetColor(Colors.White);
                tool.AddVertex(corners[i]);
            }
        }
        return tool.Commit();
    }

    /// <summary>A pebble: a coarse ball pushed in and out at random, about a unit across.</summary>
    private static Mesh Stone(Random random)
    {
        var sphere = new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 9, Rings = 5 };
        var arrays = sphere.GetMeshArrays();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        // The same direction moves the same way, so the seam stays closed.
        var bumps = new Dictionary<Vector3I, float>();
        for (var i = 0; i < vertices.Length; i++)
        {
            var key = new Vector3I(Mathf.RoundToInt(vertices[i].X * 100), Mathf.RoundToInt(vertices[i].Y * 100), Mathf.RoundToInt(vertices[i].Z * 100));
            if (!bumps.TryGetValue(key, out var bump))
                bumps[key] = bump = 0.85f + 0.25f * (float)random.NextDouble();
            vertices[i] *= bump;
        }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = new Variant();
        arrays[(int)Mesh.ArrayType.Tangent] = new Variant();
        // White, for the instance colour to tint.
        arrays[(int)Mesh.ArrayType.Color] = Enumerable.Repeat(Colors.White, vertices.Length).ToArray();
        var tool = new SurfaceTool();
        tool.CreateFromArrays(arrays);
        // Smooth across the seams, where the sphere repeats its vertices.
        tool.Deindex();
        tool.Index();
        tool.GenerateNormals();
        return tool.Commit();
    }

    /// <summary>Blades of grass on a clear picture, white at the tips and darker at the root, for the instance colour to tint.</summary>
    private static readonly Lazy<Texture2D> Blades = new(() =>
    {
        const int size = 128;
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        // The clear pixels carry the blades' colour, or the smaller mipmaps would mix black into the blades far away.
        image.Fill(new Color(0.8f, 0.8f, 0.8f, 0));
        var random = new Random(1999);
        for (var blade = 0; blade < 46; blade++)
        {
            var root = 4 + random.Next(size - 8);
            var height = size * (0.45f + 0.55f * (float)random.NextDouble());
            var lean = (float)(random.NextDouble() - 0.5) * size * 0.35f;
            var width = 2.2f + 1.6f * (float)random.NextDouble();
            for (var step = 0; step < height; step++)
            {
                var t = step / height;
                var x = root + lean * t * t;
                var half = width * (1 - t) * 0.5f + 0.3f;
                var y = size - 1 - step;
                var light = 0.55f + 0.45f * t;
                for (var px = (int)(x - half); px <= (int)(x + half); px++)
                    if (px >= 0 && px < size)
                        image.SetPixel(px, y, new Color(light, light, light, 1));
            }
        }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    });

    /// <summary>
    /// Grass: the blades cut out of their picture, tinted by the instance's colour, lit as the ground beneath them is
    /// (their normals point up) and swaying in a wind that runs across the planet; the tips move, the roots stay.
    /// </summary>
    private static readonly Lazy<Shader> GrassShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode cull_disabled;
            uniform sampler2D blades : source_color, filter_linear_mipmap;
            global uniform vec2 weather_wind;
            void vertex() {
                vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                // Leaning with the weather's wind and swaying about it, harder as it blows.
                float strength = length(weather_wind);
                vec2 along = weather_wind / max(strength, 0.001);
                float sway = sin(TIME * (1.7 + strength * 0.15) + world.x * 0.21 + world.z * 0.17) + 0.5 * sin(TIME * 3.1 + world.z * 0.43);
                vec2 bend = along * (0.02 * strength) + vec2(0.06, 0.04) * sway * (0.6 + strength * 0.08);
                VERTEX.xz += bend * min(1.0, 0.5 + strength * 0.05) * (1.0 - UV.y);
                NORMAL = vec3(0.0, 1.0, 0.0);
            }
            void fragment() {
                vec4 blade = texture(blades, UV);
                ALBEDO = blade.rgb * COLOR.rgb;
                ALPHA = blade.a;
                ALPHA_SCISSOR_THRESHOLD = 0.3;
                // Soft blade edges under MSAA (alpha to coverage), so the grass does not shimmer.
                ALPHA_ANTIALIASING_EDGE = 0.3;
                ALPHA_TEXTURE_COORDINATE = UV * vec2(textureSize(blades, 0));
                ROUGHNESS = 0.85;
                SPECULAR = 0.2;
            }
            """,
    });
}
