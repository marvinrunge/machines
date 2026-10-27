using Godot;

namespace Machines.Game;

/// <summary>
/// The ground of the natural planets in the remake look. The original's terrain tiles each carry their own picture
/// with the transitions painted in (sand to rock, grass to gravel, the shore), so neighbouring tiles met in hard
/// edges, and their corner colours left dark rectangles. Here the ground is painted with Poly Haven materials (CC0,
/// fetched by tools/terrain-textures/fetch.sh) from one material map over the whole planet, read from the old
/// pictures themselves: each texel is given the material whose colour it is nearest, as the planet's family uses
/// them (sand and sandstone in the desert; beach, gravel, grass and mossy rock on the green planets ...), and the
/// map is softened, so the original layout stays and the edges go. A shader mixes up to four materials by their
/// height maps (rock shows between the sand instead of fading into it), turns steep faces to the family's cliff
/// material seen from the side, and tints each material to the colour the original had. The old pictures' light and
/// dark (the shading of dunes and craters) stays as a soft brightness, and the tiles' corner colours as a light spread
/// wide over the ground. Water, lava and everything built keep their old pictures. The materials were picked with the
/// player on the terrain texture page. Purely for the eye.
/// </summary>
public static class TerrainMaterials
{
    /// <summary>
    /// A material: its maps' folder under textures/polyhaven, the metres one repeat covers, whether it stands on
    /// cliffs, and how many grass tufts and stones grow on a square metre of it.
    /// </summary>
    private sealed record Material(string Layer, float Size, bool Rocky, float Grass, float Stones);

    private static readonly Dictionary<string, Material> Materials = new()
    {
        ["sand"] = new("sand", 5, false, 0, 0.02f),
        ["sandstone"] = new("sandstone", 14, true, 0, 0.05f),
        ["beach"] = new("beach", 12, false, 0, 0.03f),
        ["gravel"] = new("rock", 5, false, 0, 0.15f),
        ["drygrass"] = new("grass", 4.5f, false, 0.08f, 0.03f),
        ["lushgrass"] = new("grass", 4.5f, false, 0.6f, 0.01f),
        ["mossrock"] = new("mossrock", 8, true, 0, 0.08f),
        ["regolith"] = new("regolith", 7, false, 0, 0.04f),
        ["dust"] = new("regolith", 7, false, 0, 0.01f),
        ["redsoil"] = new("redsoil", 5, false, 0, 0.06f),
        ["darkrock"] = new("darkrock", 8, true, 0, 0.1f),
        ["snow"] = new("snow", 7, false, 0, 0.02f),
        ["greysnow"] = new("snow", 7, false, 0, 0.03f),
        ["ice"] = new("ice", 12, true, 0, 0),
    };

    /// <summary>One of a family's materials, the colour the original painted it (sRGB) and how far it is tinted to it (1 full, 0.5 half).</summary>
    private sealed record Slot(string Material, string Colour, float Tint);

    /// <summary>Where the old picture stays (water, lava) in a family's colours.</summary>
    private const int Original = -1;

    /// <summary>
    /// A family of planets drawn alike: its materials (at most four), the one steep faces take, the old textures that
    /// keep their pictures whole (water, buildings), and the colours (CIE Lab) the old pictures' texels are told apart
    /// by, each standing for a material or for keeping the old picture. The colours were taken from pure stretches of
    /// the old textures (three clusters each) and checked against the transitions.
    /// </summary>
    private sealed record Family(string[] Planets, Slot[] Slots, int Cliff, string[] Keep, (int Slot, float L, float A, float B)[] Colours);

    private static readonly Family[] Families =
    [
        // Desert: dunes and layered sandstone.
        new(["m_desert", "daynight", "desspy"],
            [new("sand", "#c68440", 1), new("sandstone", "#846b47", 0.5f)], 1, [],
            [(0, 32.5f, 11.4f, 27.7f), (0, 54.4f, 16.4f, 44.5f), (0, 64.6f, 20.8f, 47.2f),
             (1, 32.5f, 3.4f, 17.8f), (1, 46.8f, 5.5f, 28.4f), (1, 59.6f, 3.2f, 24.5f)]),
        // Green planets and dales: beaches, gravel, dry grass, mossy cliffs, water.
        new(["m_green", "mgreendn", "m_dales", "mdalesdn", "finger"],
            [new("beach", "#9a764c", 1), new("gravel", "#8f6f4d", 1), new("drygrass", "#573c0c", 1), new("mossrock", "#40452d", 1)], 3,
            ["watflw", "wated1", "watr00", "watfal"],
            [(0, 31.1f, 5.4f, 17.7f), (0, 49.6f, 8.4f, 28), (0, 55, 8.7f, 29),
             (1, 43.8f, 7.9f, 20), (1, 56.1f, 8.6f, 26.9f), (1, 48.5f, 8.4f, 27.9f),
             (2, 28.5f, 1.5f, 33.1f), (2, 17, 4.8f, 21.1f), (2, 27.6f, 10, 32.5f),
             (3, 18.4f, -6.4f, 8), (3, 28.5f, -8.1f, 10.1f), (3, 39.6f, -8.8f, 12.5f),
             (Original, 17.6f, -4, -19), (Original, 30, -12, -6)]),
        // Hills: sand, grey gravel, lush grass, dark rock.
        new(["hilly", "talonth8"],
            [new("beach", "#9c804c", 1), new("gravel", "#484941", 1), new("lushgrass", "#3e461a", 1), new("mossrock", "#443f32", 1)], 3,
            ["wat00", "metal", "metal2", "sroad", "sroad2", "ductf_t"],
            [(0, 30.3f, 1.9f, 18.8f), (0, 53, 2.7f, 31.7f), (0, 57.1f, 4.1f, 32.8f),
             (1, 24.6f, -1.9f, 4.9f), (1, 30.2f, -4, 9.2f), (1, 35.4f, -1, 2.8f),
             (2, 15.8f, -7, 14.9f), (2, 26.1f, -10.4f, 22.7f), (2, 30.9f, -11.9f, 27.5f),
             (3, 14.8f, -0.1f, 6.4f), (3, 25.5f, -0.1f, 8.5f), (3, 34.8f, 0.8f, 9.1f)]),
        // Forts: grassy earth, pebbly soil and pale rock between the walls.
        new(["fortmdn", "fortmult", "fortsdn", "fortseig", "seige", "5o1"],
            [new("drygrass", "#80764c", 1), new("mossrock", "#96806d", 1), new("gravel", "#7f644b", 1)], 1,
            ["wall01", "wall02", "walk01", "grnd1", "rampy1", "road1", "pipes1", "floor1", "gwalk", "edge1"],
            [(0, 41, -2.4f, 20.9f), (0, 50, -7.9f, 26.7f), (0, 52, 2.4f, 24.2f),
             (1, 30.3f, 3.1f, 5.1f), (1, 53.4f, 6.5f, 16.6f), (1, 70.9f, 5.2f, 14),
             (2, 32.6f, 6.5f, 13.3f), (2, 43.1f, 6.8f, 18.1f), (2, 53.4f, 9, 22.4f)]),
        // Grey moons: dust and rock ridges (the pale floors of craters stay pale by the old pictures' light); lava and sulphur keep their pictures.
        new(["m_moon", "mmoony", "1o1", "1o2", "1o3", "seekndes"],
            [new("regolith", "#484f45", 1), new("darkrock", "#35382e", 1)], 1, [],
            [(0, 16.2f, -3, 2.7f), (0, 31, -5.2f, 4.7f), (0, 35.3f, -5.6f, 5.3f),
             (0, 45.7f, -2.7f, 3.3f), (0, 50, -3.1f, 3.8f), (0, 53.8f, -3, 4.1f), (0, 44.1f, -1.9f, 10.3f), (0, 53.2f, -0.3f, 15.1f),
             (1, 4.9f, -0.8f, 1.5f), (1, 14.5f, -2.3f, 5.6f), (1, 24.6f, -3.2f, 8.6f),
             (Original, 18.2f, 30.3f, 25.7f), (Original, 32.3f, 36.6f, 39.1f), (Original, 37.8f, 40.6f, 43.9f),
             (Original, 32.2f, 45, 39.1f), (Original, 40.7f, 59.1f, 52.8f), (Original, 50, 69.2f, 62.4f),
             (Original, 51.3f, -3, 35.5f), (Original, 59.9f, -2.5f, 45.1f), (Original, 70.2f, -2.5f, 55.9f)]),
        // Red moons: red soil, black rock, pale crater floors, lava.
        new(["dogbar", "multidog", "commsat"],
            [new("redsoil", "#492212", 1), new("darkrock", "#3e1f12", 1), new("dust", "#757971", 1)], 1,
            ["wat00", "wat06", "tower2", "waltop"],
            [(0, 16.7f, 16.1f, 17.1f), (0, 21.3f, 19.1f, 21.1f), (0, 34.9f, 21.8f, 28.5f), (0, 41.3f, 25.4f, 33.6f), (0, 49.3f, 28.5f, 38.8f),
             (1, 5.9f, 2.3f, 1.9f), (1, 11.3f, 8, 8.7f),
             (2, 45.4f, -2.8f, 3.9f), (2, 49.9f, -3.1f, 4), (2, 53.9f, -3.5f, 4.6f),
             (Original, 21.7f, 38.9f, 31.8f), (Original, 29.2f, 48.1f, 41.4f), (Original, 42.2f, 61.3f, 55.2f),
             (Original, 18.2f, 38.6f, 27.9f), (Original, 20.9f, 41.5f, 31.8f), (Original, 29.5f, 51.4f, 42.6f)]),
        // Gracko: rust-orange soil and bands of dark rock.
        new(["gracko", "mgrack"],
            [new("redsoil", "#6a3205", 1), new("darkrock", "#474a4e", 1)], 1, [],
            [(0, 16.2f, 10.4f, 17.6f), (0, 25.6f, 19, 32.7f), (0, 29.3f, 24.6f, 39.1f),
             (1, 15.9f, 2.1f, 10.2f), (1, 11.3f, 1.1f, 0), (1, 19.4f, -1.2f, -0.7f)]),
        // Ice: snow, grey snow, rock, glacier walls.
        new(["ice", "icedn", "micey", "miceydn", "resfac"],
            [new("snow", "#ccd5e8", 1), new("darkrock", "#383939", 1), new("ice", "#768b9d", 1), new("greysnow", "#a6b0c3", 1)], 2, [],
            [(0, 80.8f, 0.1f, -10.2f), (0, 86.6f, 0.9f, -10.5f), (0, 85, 0.9f, -10.7f), (0, 78.7f, 0.2f, -10.3f),
             (3, 50, 0.6f, -6.3f), (3, 40, 0.8f, -7), (3, 69.9f, 0.5f, -10.8f), (3, 46.1f, 0.7f, -6.5f), (3, 68.3f, 1.2f, -12.1f),
             (1, 7.2f, 0.5f, 0.1f), (1, 21.5f, -0.2f, 1.4f), (1, 35.1f, 0.1f, -0.3f), (1, 5.1f, -0.2f, -0.9f), (1, 12.4f, -2, -1.4f), (1, 27.4f, -2.6f, -5.9f),
             (2, 45.7f, -4.9f, -10.9f), (2, 57.9f, -4.8f, -13.1f), (2, 69.5f, -3.7f, -13.7f)]),
    ];

    private static string Folder => Path.Combine(Paths.Root, "textures", "polyhaven");

    /// <summary>The material map's finest cell, and its most cells along a side.</summary>
    private const float FinestCell = 0.5f;
    private const int MaxCells = 3072;

    /// <summary>How far the map is softened, in metres.</summary>
    private const float Softening = 2f;

    /// <summary>The cells of the light map (the tiles' corner colours), and how far it is spread, in metres.</summary>
    private const float LightCell = 2, LightSpread = 6;

    /// <summary>The texels each old picture is read at, along a side.</summary>
    private const int Grid = 64;

    /// <summary>What lies where on a planet's natural ground, for what grows on it (GroundScatter).</summary>
    public sealed class Ground
    {
        private readonly byte[] _weights;
        private readonly int _columns, _rows;
        private readonly float _cell;
        private readonly (float Grass, float Stones, Color Colour)[] _slots;
        private readonly byte[] _light;
        private readonly int _lightColumns, _lightRows;

        internal Ground(HashSet<int> surfaces, byte[] weights, int columns, int rows, float cell, (float Grass, float Stones, Color Colour)[] slots,
            byte[] light, int lightColumns, int lightRows)
        {
            Surfaces = surfaces;
            (_weights, _columns, _rows, _cell, _slots) = (weights, columns, rows, cell, slots);
            (_light, _lightColumns, _lightRows) = (light, lightColumns, lightRows);
        }

        /// <summary>The terrain surfaces drawn from the material map.</summary>
        public IReadOnlySet<int> Surfaces { get; }

        /// <summary>
        /// Grass tufts and stones per square metre at a point of the ground, the colour of its grass and of its stones
        /// (sRGB); none where the old picture stays.
        /// </summary>
        public (float Grass, float Stones, Color GrassColour, Color StoneColour) At(float x, float z)
        {
            var cx = Math.Clamp((int)(x / _cell), 0, _columns - 1);
            var cz = Math.Clamp((int)(z / _cell), 0, _rows - 1);
            var o = (cz * _columns + cx) * 4;
            float grass = 0, stones = 0, grassWeight = 0;
            Color grassColour = new(0, 0, 0), stoneColour = new(0, 0, 0);
            for (var s = 0; s < _slots.Length; s++)
            {
                var w = _weights[o + s] / 255f;
                var (slotGrass, slotStones, colour) = _slots[s];
                grass += w * slotGrass;
                stones += w * slotStones;
                grassColour += colour * (w * slotGrass);
                grassWeight += w * slotGrass;
                stoneColour += colour * w;
            }
            var lo = (Math.Clamp((int)(z / LightCell), 0, _lightRows - 1) * _lightColumns + Math.Clamp((int)(x / LightCell), 0, _lightColumns - 1)) * 3;
            var light = new Color(_light[lo] / 255f, _light[lo + 1] / 255f, _light[lo + 2] / 255f);
            // Tufts stand up out of the light the ground lies in: a little lighter, or they show as dark specks from afar.
            return (grass, stones, (grassWeight > 0 ? grassColour / grassWeight : Colors.Olive) * light * 1.5f, stoneColour * light * 0.85f);
        }
    }

    /// <summary>
    /// Paints a planet's natural ground: gives the terrain's natural tiles the material shader in place of their
    /// materials, and returns what lies where. Null for planets of no family (stations, industrial worlds) or when
    /// the textures have not been fetched; their ground keeps its detail (TerrainDetail).
    /// </summary>
    public static Ground? Apply(Mesh terrain, TerrainGeometry.Part[] ground, string planet, float width, float depth)
    {
        if (Array.Find(Families, f => f.Planets.Contains(planet, StringComparer.OrdinalIgnoreCase)) is not { } family
            || Load(family) is not { } layers)
            return null;

        // The natural tiles and what their pictures show.
        var natural = new Dictionary<int, StandardMaterial3D>();
        var pictures = new Dictionary<Texture2D, (byte[] Class, byte[] Shade)>();
        for (var s = 0; s < terrain.GetSurfaceCount(); s++)
        {
            if (terrain.SurfaceGetMaterial(s) is not StandardMaterial3D { Transparency: BaseMaterial3D.TransparencyEnum.Disabled, AlbedoTexture: { } picture } material)
                continue;
            var name = Path.GetFileNameWithoutExtension(material.GetMeta("texture", "").AsString());
            if (name.EndsWith("_b", StringComparison.Ordinal))
                name = name[..^2];
            if (family.Keep.Contains(name) || TerrainDetail.LayerOf(material) == "metal")
                continue;
            natural[s] = material;
            if (!pictures.ContainsKey(picture))
                pictures[picture] = Classify(picture, family);
        }
        if (natural.Count == 0)
            return null;

        var cell = Mathf.Max(FinestCell, Mathf.Max(width, depth) / MaxCells);
        var (columns, rows) = (Mathf.CeilToInt(width / cell), Mathf.CeilToInt(depth / cell));
        var (weights, shade) = Paint(terrain, natural, pictures, cell, columns, rows, family.Slots.Length);

        var map = ImageTexture.CreateFromImage(Image.CreateFromData(columns, rows, false, Image.Format.Rgba8, weights));
        var shadeMap = ImageTexture.CreateFromImage(Image.CreateFromData(columns, rows, false, Image.Format.L8, shade));
        var (lights, lightColumns, lightRows) = Light(ground, natural.Keys, width, depth);
        var lightMap = ImageTexture.CreateFromImage(Image.CreateFromData(lightColumns, lightRows, false, Image.Format.Rgb8, lights));
        var warp = ImageTexture.CreateFromImage(new FastNoiseLite { Frequency = 1 / 24f, FractalOctaves = 3, Seed = 3 }.GetSeamlessImage(256, 256));

        var slotLayer = new float[4];
        var slotSize = new float[4];
        var slotRocky = new float[4];
        var slotTint = new Vector3[4];
        for (var s = 0; s < family.Slots.Length; s++)
        {
            var slot = family.Slots[s];
            var material = Materials[slot.Material];
            var layer = Array.IndexOf(layers.Names, material.Layer);
            (slotLayer[s], slotSize[s], slotRocky[s]) = (layer, material.Size, material.Rocky ? 1 : 0);
            // The material, on average, takes the colour the original had (fully, or halfway).
            var target = Color.FromHtml(slot.Colour).SrgbToLinear();
            var mean = layers.Means[layer];
            var gain = new Vector3(target.R / mean.X, target.G / mean.Y, target.B / mean.Z);
            slotTint[s] = new Vector3(Mathf.Pow(gain.X, slot.Tint), Mathf.Pow(gain.Y, slot.Tint), Mathf.Pow(gain.Z, slot.Tint)).Clamp(Vector3.One * 0.1f, Vector3.One * 8);
        }
        var cliff = new float[4];
        cliff[family.Cliff] = 1;

        var shaders = new Dictionary<StandardMaterial3D, ShaderMaterial>();
        foreach (var (s, original) in natural)
        {
            if (!shaders.TryGetValue(original, out var shader))
            {
                shaders[original] = shader = new ShaderMaterial { Shader = Shader.Value };
                shader.SetShaderParameter("albedo_tex", original.AlbedoTexture);
                shader.SetShaderParameter("tint", new Vector3(original.AlbedoColor.R, original.AlbedoColor.G, original.AlbedoColor.B));
                shader.SetShaderParameter("vertex_colours", original.VertexColorUseAsAlbedo);
                shader.SetShaderParameter("material_map", map);
                shader.SetShaderParameter("shade_map", shadeMap);
                shader.SetShaderParameter("light_map", lightMap);
                shader.SetShaderParameter("light_size", new Vector2(lightColumns * LightCell, lightRows * LightCell));
                shader.SetShaderParameter("map_size", new Vector2(columns * cell, rows * cell));
                shader.SetShaderParameter("warp", warp);
                shader.SetShaderParameter("layer_albedo", layers.Albedo);
                shader.SetShaderParameter("layer_normal", layers.Normal);
                shader.SetShaderParameter("layer_rough", layers.Roughness);
                shader.SetShaderParameter("slot_layer", new Vector4(slotLayer[0], slotLayer[1], slotLayer[2], slotLayer[3]));
                shader.SetShaderParameter("slot_size", new Vector4(slotSize[0], slotSize[1], slotSize[2], slotSize[3]));
                shader.SetShaderParameter("slot_rocky", new Vector4(slotRocky[0], slotRocky[1], slotRocky[2], slotRocky[3]));
                shader.SetShaderParameter("cliff", new Vector4(cliff[0], cliff[1], cliff[2], cliff[3]));
                shader.SetShaderParameter("slot_tint", slotTint);
            }
            terrain.SurfaceSetMaterial(s, shader);
        }
        var slots = family.Slots.Select(s => (Materials[s.Material].Grass, Materials[s.Material].Stones, Color.FromHtml(s.Colour))).ToArray();
        return new Ground([.. natural.Keys], weights, columns, rows, cell, slots, lights, lightColumns, lightRows);
    }

    /// <summary>
    /// What an old picture shows, texel by texel at <see cref="Grid"/> × <see cref="Grid"/>: the slot of the nearest
    /// colour (4 where the old picture stays), and how light the texel is against the picture's other texels of that
    /// material (128 = as light; 0 to 255 = none to twice).
    /// </summary>
    private static (byte[] Class, byte[] Shade) Classify(Texture2D picture, Family family)
    {
        var classes = new byte[Grid * Grid];
        var shade = new byte[Grid * Grid];
        Array.Fill(shade, (byte)128);
        if (picture.GetImage() is not { } source || source.IsEmpty())
        {
            Array.Fill(classes, (byte)4);
            return (classes, shade);
        }
        var image = (Image)source.Duplicate();
        if (image.IsCompressed())
            image.Decompress();
        image.ClearMipmaps();
        image.Convert(Image.Format.Rgb8);
        image.Resize(Grid, Grid, Image.Interpolation.Bilinear);
        var data = image.GetData();
        var light = new float[Grid * Grid];
        var sum = new float[5];
        var count = new int[5];
        for (var i = 0; i < Grid * Grid; i++)
        {
            var (r, g, b) = (data[i * 3] / 255f, data[i * 3 + 1] / 255f, data[i * 3 + 2] / 255f);
            var lab = Lab(r, g, b);
            var (best, nearest) = (4, float.MaxValue);
            foreach (var (slot, l, a, bb) in family.Colours)
            {
                var (dl, da, db) = ((lab.X - l) * 0.7f, lab.Y - a, lab.Z - bb);
                var d = dl * dl + da * da + db * db;
                if (d < nearest)
                    (nearest, best) = (d, slot == Original ? 4 : slot);
            }
            classes[i] = (byte)best;
            light[i] = 0.2126f * Linear(r) + 0.7152f * Linear(g) + 0.0722f * Linear(b);
            sum[best] += light[i];
            count[best]++;
        }
        for (var i = 0; i < Grid * Grid; i++)
        {
            var mean = sum[classes[i]] / Math.Max(count[classes[i]], 1);
            shade[i] = (byte)Math.Clamp(Mathf.RoundToInt(light[i] / Mathf.Max(mean, 1e-3f) * 128), 0, 255);
        }
        return (classes, shade);
    }

    /// <summary>
    /// The material map: each cell takes what the old picture shows above it (the flattest tile there, where faces
    /// overlap seen from above), cells no natural tile covers take their neighbours', and the whole is softened.
    /// Four bytes a cell for the slots' shares (what is left keeps the old picture), and one for the brightness.
    /// </summary>
    private static (byte[] Weights, byte[] Shade) Paint(Mesh terrain, Dictionary<int, StandardMaterial3D> natural,
        Dictionary<Texture2D, (byte[] Class, byte[] Shade)> pictures, float cell, int columns, int rows, int slots)
    {
        var triangles = new List<(Vector2 A, Vector2 B, Vector2 C, Vector2 Ua, Vector2 Ub, Vector2 Uc, float Flat, byte[] Class, byte[] Shade)>();
        foreach (var (s, material) in natural)
        {
            var arrays = terrain.SurfaceGetArrays(s);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var (classes, lightness) = pictures[material.AlbedoTexture];
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var (a, b, c) = (vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
                var flat = Mathf.Abs((b - a).Cross(c - a).Normalized().Y);
                triangles.Add((new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), new Vector2(c.X, c.Z),
                    uvs[indices[t]], uvs[indices[t + 1]], uvs[indices[t + 2]], flat, classes, lightness));
            }
        }

        const byte none = 255;
        var cells = columns * rows;
        var classOf = new byte[cells];
        var shadeOf = new byte[cells];
        var flatOf = new float[cells];
        Array.Fill(classOf, none);
        Array.Fill(flatOf, -1);
        // Cells that only take a neighbour's material keep the middle brightness.
        Array.Fill(shadeOf, (byte)128);
        // In bands of rows side by side, each cell written by one band only; each band looks at the triangles over it.
        const int band = 32;
        var bands = (rows + band - 1) / band;
        var over = Enumerable.Range(0, bands).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < triangles.Count; i++)
        {
            var t = triangles[i];
            var first = Math.Max(0, (int)MathF.Floor(MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)) / cell) / band);
            var last = Math.Min(bands - 1, (int)MathF.Floor(MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)) / cell) / band);
            for (var k = first; k <= last; k++)
                over[k].Add(i);
        }
        Parallel.For(0, bands, k =>
        {
            var (top, bottom) = (k * band, Math.Min(rows, (k + 1) * band));
            foreach (var i in over[k])
            {
                var t = triangles[i];
                var z0 = Math.Max(top, (int)MathF.Floor(MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)) / cell));
                var z1 = Math.Min(bottom - 1, (int)MathF.Floor(MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)) / cell));
                if (z0 > z1)
                    continue;
                var area = (t.B - t.A).Cross(t.C - t.A);
                if (MathF.Abs(area) < 1e-6f)
                    continue;
                var x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)) / cell));
                var x1 = Math.Min(columns - 1, (int)MathF.Floor(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)) / cell));
                for (var z = z0; z <= z1; z++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var p = new Vector2((x + 0.5f) * cell, (z + 0.5f) * cell);
                        var u = (t.C - t.B).Cross(p - t.B) / area;
                        var v = (t.A - t.C).Cross(p - t.C) / area;
                        var w = 1 - u - v;
                        if (u < -1e-4f || v < -1e-4f || w < -1e-4f)
                            continue;
                        var o = z * columns + x;
                        if (t.Flat < flatOf[o])
                            continue;
                        var uv = t.Ua * u + t.Ub * v + t.Uc * w;
                        var tx = Math.Clamp((int)((uv.X - MathF.Floor(uv.X)) * Grid), 0, Grid - 1);
                        var ty = Math.Clamp((int)((uv.Y - MathF.Floor(uv.Y)) * Grid), 0, Grid - 1);
                        (classOf[o], shadeOf[o], flatOf[o]) = (t.Class[ty * Grid + tx], t.Shade[ty * Grid + tx], t.Flat);
                    }
                }
            }
        });

        // Cells beside the natural tiles (under roads, walls, water) take their neighbours', so nothing fades at the edge.
        for (var pass = 0; pass < 6; pass++)
        {
            var next = (byte[])classOf.Clone();
            Parallel.For(0, rows, z =>
            {
                for (var x = 0; x < columns; x++)
                {
                    var o = z * columns + x;
                    if (classOf[o] != none)
                        continue;
                    if (x > 0 && classOf[o - 1] != none) next[o] = classOf[o - 1];
                    else if (x + 1 < columns && classOf[o + 1] != none) next[o] = classOf[o + 1];
                    else if (z > 0 && classOf[o - columns] != none) next[o] = classOf[o - columns];
                    else if (z + 1 < rows && classOf[o + columns] != none) next[o] = classOf[o + columns];
                }
            });
            classOf = next;
        }

        var weights = new byte[cells * 4];
        var shade = new byte[cells];
        Parallel.For(0, rows, z =>
        {
            for (var o = z * columns; o < (z + 1) * columns; o++)
            {
                if (classOf[o] < slots)
                    weights[o * 4 + classOf[o]] = 255;
                shade[o] = classOf[o] == none ? (byte)128 : shadeOf[o];
            }
        });
        var radius = Math.Max(1, Mathf.RoundToInt(Softening / cell));
        Soften(weights, columns, rows, 4, radius);
        Soften(shade, columns, rows, 1, radius);
        return (weights, shade);
    }

    /// <summary>
    /// The light map: the tiles' corner colours (evened out where tiles meet, see TerrainGeometry), each cell of
    /// <see cref="LightCell"/> the average of the triangles whose middles lie in it, cells without any filled from
    /// around, then spread over <see cref="LightSpread"/>. Three bytes a cell, as the colours are (sRGB).
    /// </summary>
    private static (byte[] Colours, int Columns, int Rows) Light(TerrainGeometry.Part[] ground, IEnumerable<int> surfaces, float width, float depth)
    {
        var (columns, rows) = (Mathf.CeilToInt(width / LightCell), Mathf.CeilToInt(depth / LightCell));
        // Colour times area, and area, per cell: spread alike and divided, the empty cells take what is around them.
        var sum = new float[columns * rows * 4];
        // Each surface's triangles side by side, then added up in order.
        var parts = surfaces.AsParallel().AsOrdered().Select(s =>
        {
            var (vertices, indices, colours) = (ground[s].Vertices, ground[s].Indices, ground[s].Colours);
            var found = new List<(int Cell, float R, float G, float B, float Area)>(indices.Length / 3);
            if (colours is null)
                return found;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var (a, b, c) = (vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
                var area = (b - a).Cross(c - a).Length() / 2;
                var middle = (a + b + c) / 3;
                var colour = (colours[indices[t]] + colours[indices[t + 1]] + colours[indices[t + 2]]) / 3;
                var o = (Math.Clamp((int)(middle.Z / LightCell), 0, rows - 1) * columns + Math.Clamp((int)(middle.X / LightCell), 0, columns - 1)) * 4;
                found.Add((o, colour.R * area, colour.G * area, colour.B * area, area));
            }
            return found;
        }).ToList();
        foreach (var part in parts)
        {
            foreach (var (o, r, g, b, area) in part)
                (sum[o], sum[o + 1], sum[o + 2], sum[o + 3]) = (sum[o] + r, sum[o + 1] + g, sum[o + 2] + b, sum[o + 3] + area);
        }
        var radius = Mathf.RoundToInt(LightSpread / LightCell);
        for (var pass = 0; pass < 3; pass++)
            Spread(sum, columns, rows, radius * (pass + 1));
        var bytes = new byte[columns * rows * 3];
        for (var o = 0; o < columns * rows; o++)
        {
            var area = sum[o * 4 + 3];
            for (var c = 0; c < 3; c++)
                bytes[o * 3 + c] = area > 1e-6f ? Byte(sum[o * 4 + c] / area) : (byte)255;
        }
        return (bytes, columns, rows);
    }

    /// <summary>A box blur across and down of four floats a cell (as <see cref="Soften"/> goes through the map).</summary>
    private static void Spread(float[] data, int columns, int rows, int radius)
    {
        var (width, span) = (columns * 4, 2f * radius + 1);
        var source = (float[])data.Clone();
        Parallel.For(0, rows, z =>
        {
            var line = z * width;
            for (var c = 0; c < 4; c++)
            {
                var total = 0f;
                for (var i = -radius; i <= radius; i++)
                    total += source[line + Math.Clamp(i, 0, columns - 1) * 4 + c];
                for (var x = 0; x < columns; x++)
                {
                    data[line + x * 4 + c] = total / span;
                    total += source[line + Math.Min(x + radius + 1, columns - 1) * 4 + c] - source[line + Math.Max(x - radius, 0) * 4 + c];
                }
            }
        });
        Array.Copy(data, source, data.Length);
        Parallel.For(0, (width + 255) / 256, k =>
        {
            var (first, last) = (k * 256, Math.Min(width, (k + 1) * 256));
            var total = new float[last - first];
            for (var i = -radius; i <= radius; i++)
            {
                var row = Math.Clamp(i, 0, rows - 1) * width;
                for (var x = first; x < last; x++)
                    total[x - first] += source[row + x];
            }
            for (var z = 0; z < rows; z++)
            {
                var (row, ahead, behind) = (z * width, Math.Min(z + radius + 1, rows - 1) * width, Math.Max(z - radius, 0) * width);
                for (var x = first; x < last; x++)
                {
                    data[row + x] = total[x - first] / span;
                    total[x - first] += source[ahead + x] - source[behind + x];
                }
            }
        });
    }

    private static byte Byte(float value) => (byte)Math.Clamp(Mathf.RoundToInt(value * 255), 0, 255);

    /// <summary>
    /// A box blur across and one down (the shader's height blending and wandering borders do the rest). Rows are blurred
    /// one each, side by side; columns a strip of them at a time, row after row, so both read the map in the order it
    /// lies in memory.
    /// </summary>
    private static void Soften(byte[] data, int columns, int rows, int channels, int radius)
    {
        var width = columns * channels;
        var span = 2 * radius + 1;
        void Across()
        {
            Parallel.For(0, rows, z =>
            {
                var line = z * width;
                var source = data.AsSpan(line, width).ToArray();
                for (var c = 0; c < channels; c++)
                {
                    var sum = 0;
                    for (var i = -radius; i <= radius; i++)
                        sum += source[Math.Clamp(i, 0, columns - 1) * channels + c];
                    for (var x = 0; x < columns; x++)
                    {
                        data[line + x * channels + c] = (byte)(sum / span);
                        sum += source[Math.Min(x + radius + 1, columns - 1) * channels + c] - source[Math.Max(x - radius, 0) * channels + c];
                    }
                }
            });
        }
        void Down()
        {
            var source = (byte[])data.Clone();
            const int strip = 256;
            Parallel.For(0, (width + strip - 1) / strip, k =>
            {
                var (first, last) = (k * strip, Math.Min(width, (k + 1) * strip));
                var sum = new int[last - first];
                for (var i = -radius; i <= radius; i++)
                {
                    var row = Math.Clamp(i, 0, rows - 1) * width;
                    for (var x = first; x < last; x++)
                        sum[x - first] += source[row + x];
                }
                for (var z = 0; z < rows; z++)
                {
                    var (row, ahead, behind) = (z * width, Math.Min(z + radius + 1, rows - 1) * width, Math.Max(z - radius, 0) * width);
                    for (var x = first; x < last; x++)
                    {
                        data[row + x] = (byte)(sum[x - first] / span);
                        sum[x - first] += source[ahead + x] - source[behind + x];
                    }
                }
            });
        }
        Across();
        Down();
    }

    private static float Linear(float s) => s <= 0.04045f ? s / 12.92f : Mathf.Pow((s + 0.055f) / 1.055f, 2.4f);

    /// <summary>CIE Lab (D65) of an sRGB colour.</summary>
    private static Vector3 Lab(float r, float g, float b)
    {
        (r, g, b) = (Linear(r), Linear(g), Linear(b));
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116;
        var x = F((0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f);
        var y = F(0.2126f * r + 0.7152f * g + 0.0722f * b);
        var z = F((0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f);
        return new Vector3(116 * y - 16, 500 * (x - y), 200 * (y - z));
    }

    /// <summary>A family's materials as texture arrays (colour with height in alpha, normal, roughness) and each one's average colour (linear).</summary>
    private sealed record Layers(string[] Names, Texture2DArray Albedo, Texture2DArray Normal, Texture2DArray Roughness, Vector3[] Means);

    private static readonly Dictionary<Family, Layers?> Loaded = [];

    private static Layers? Load(Family family)
    {
        if (Loaded.TryGetValue(family, out var cached))
            return cached;
        var names = family.Slots.Select(s => Materials[s.Material].Layer).Distinct().ToArray();
        var maps = new Image?[names.Length, 3];
        var means = new Vector3[names.Length];
        Parallel.For(0, names.Length * 3, k =>
        {
            var (i, m) = (k / 3, k % 3);
            var folder = Path.Combine(Folder, names[i]);
            maps[i, m] = m switch
            {
                0 => ColourAndHeight(folder, out means[i]),
                1 => Read(Path.Combine(folder, "normal.jpg"), Image.Format.Rgb8, Image.CompressSource.Normal),
                _ => Read(Path.Combine(folder, "roughness.jpg"), Image.Format.Rgb8, Image.CompressSource.Generic),
            };
        });
        var albedo = new Godot.Collections.Array<Image>();
        var normal = new Godot.Collections.Array<Image>();
        var roughness = new Godot.Collections.Array<Image>();
        for (var i = 0; i < names.Length; i++)
        {
            if (maps[i, 0] is not { } a || maps[i, 1] is not { } n || maps[i, 2] is not { } r)
                return Loaded[family] = null;
            albedo.Add(a);
            normal.Add(n);
            roughness.Add(r);
        }
        var layers = new Layers(names, new Texture2DArray(), new Texture2DArray(), new Texture2DArray(), means);
        if (layers.Albedo.CreateFromImages(albedo) != Error.Ok || layers.Normal.CreateFromImages(normal) != Error.Ok
            || layers.Roughness.CreateFromImages(roughness) != Error.Ok)
            return Loaded[family] = null;
        return Loaded[family] = layers;
    }

    /// <summary>A map at 1024 pixels with mipmaps, compressed.</summary>
    private static Image? Read(string path, Image.Format format, Image.CompressSource source)
    {
        if (!File.Exists(path) || Image.LoadFromFile(path) is not { } image || image.IsEmpty())
            return null;
        image.Convert(format);
        if (image.GetWidth() != 1024 || image.GetHeight() != 1024)
            image.Resize(1024, 1024, Image.Interpolation.Cubic);
        image.GenerateMipmaps();
        image.Compress(Image.CompressMode.S3Tc, source);
        return image;
    }

    /// <summary>
    /// The colour map with the height map in its alpha (the colour's brightness where the texture has none), and the
    /// colour's average (linear).
    /// </summary>
    private static Image? ColourAndHeight(string folder, out Vector3 mean)
    {
        mean = Vector3.One * 0.5f;
        if (Image.LoadFromFile(Path.Combine(folder, "albedo.jpg")) is not { } colour || colour.IsEmpty())
            return null;
        colour.Convert(Image.Format.Rgba8);
        if (colour.GetWidth() != 1024 || colour.GetHeight() != 1024)
            colour.Resize(1024, 1024, Image.Interpolation.Cubic);
        var data = colour.GetData();
        byte[]? height = null;
        var heightPath = Path.Combine(folder, "height.jpg");
        if (File.Exists(heightPath) && Image.LoadFromFile(heightPath) is { } h && !h.IsEmpty())
        {
            h.Convert(Image.Format.L8);
            if (h.GetWidth() != 1024 || h.GetHeight() != 1024)
                h.Resize(1024, 1024, Image.Interpolation.Cubic);
            height = h.GetData();
        }
        double r = 0, g = 0, b = 0;
        int lowest = 255, highest = 0;
        for (var i = 0; i < 1024 * 1024; i++)
        {
            var (cr, cg, cb) = (data[i * 4], data[i * 4 + 1], data[i * 4 + 2]);
            r += Linear(cr / 255f);
            g += Linear(cg / 255f);
            b += Linear(cb / 255f);
            data[i * 4 + 3] = height?[i] ?? (byte)((cr * 54 + cg * 183 + cb * 19) >> 8);
            lowest = Math.Min(lowest, data[i * 4 + 3]);
            highest = Math.Max(highest, data[i * 4 + 3]);
        }
        // The heights stretched over the whole range, so every material blends alike.
        var span = Math.Max(highest - lowest, 1);
        for (var i = 0; i < 1024 * 1024; i++)
            data[i * 4 + 3] = (byte)((data[i * 4 + 3] - lowest) * 255 / span);
        const double pixels = 1024 * 1024;
        mean = new Vector3((float)(r / pixels), (float)(g / pixels), (float)(b / pixels));
        var image = Image.CreateFromData(1024, 1024, false, Image.Format.Rgba8, data);
        image.GenerateMipmaps();
        image.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Srgb);
        return image;
    }

    /// <summary>
    /// The ground from the material map. Each of the four slots is sampled only where it has a share: from above on
    /// gentle ground, from the two sides on steep faces, where only cliff materials stand and the rest of the share
    /// goes to the family's cliff material. So that no repeat shows, even from far off, each is sampled at three
    /// scales (its size and turned copies 3.7 and 14 times larger) mixed by a slow noise and by distance. The shares
    /// are weighed against the materials' height maps, so the higher grains win at the borders. The relief bends the
    /// normal (deeper on rock), and the hollows of the height map darken the colour, most on rock. What no slot covers
    /// shows the old picture, as before. The map is looked up a little off true by a slow noise, so the borders wander.
    /// </summary>
    private static readonly Lazy<Shader> Shader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode cull_disabled;
            uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
            uniform vec3 tint = vec3(1.0);
            uniform bool vertex_colours = false;
            uniform sampler2D material_map : filter_linear;
            uniform sampler2D shade_map : filter_linear;
            uniform sampler2D light_map : filter_linear;
            uniform vec2 light_size = vec2(1600.0);
            uniform vec2 map_size = vec2(1600.0);
            uniform sampler2D warp : filter_linear, repeat_enable;
            uniform sampler2DArray layer_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform sampler2DArray layer_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform sampler2DArray layer_rough : filter_linear_mipmap_anisotropic, repeat_enable;
            uniform vec4 slot_layer = vec4(0.0);
            uniform vec4 slot_size = vec4(3.0);
            uniform vec4 slot_rocky = vec4(0.0);
            uniform vec4 cliff = vec4(1.0, 0.0, 0.0, 0.0);
            uniform vec3 slot_tint[4];
            uniform float shade_strength = 1.0;
            uniform float relief = 0.9;
            uniform float rock_relief = 1.8;
            uniform float hollow = 0.25;
            uniform float rock_hollow = 0.6;
            uniform float blend_depth = 0.35;
            varying vec3 world_pos;
            varying vec3 world_normal;

            void vertex() {
                world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
            }

            // One material: its colour (height in alpha), its relief bending the normal in world space, its roughness.
            struct Layer {
                vec4 albedo;
                vec3 bend;
                float rough;
            };

            // A map seen from above at three scales, so no repeat shows: close up the material's own size and a turned
            // copy 3.7 times larger, mixed by a slow noise; farther off that copy and one 14 times larger.
            vec4 top_sample(sampler2DArray tex, vec2 p, float size, float index, float mixer, float far) {
                vec2 a = p / size;
                vec2 b = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * p / (size * 3.7);
                vec2 c = mat2(vec2(-0.28, 0.96), vec2(-0.96, -0.28)) * p / (size * 14.0);
                vec4 near_value = far < 0.99 ? mix(texture(tex, vec3(a, index)), texture(tex, vec3(b, index)), mixer) : vec4(0.0);
                vec4 far_value = far > 0.01 ? mix(texture(tex, vec3(b, index)), texture(tex, vec3(c, index)), 0.5) : vec4(0.0);
                return mix(near_value, far_value, far);
            }

            // Seen from above, and from the two sides by how much the face looks each way, so slopes do not stretch it;
            // the sides at two scales too.
            Layer layer(float index, float size, vec3 p, vec3 n, float wobble, float mixer, float far) {
                Layer l;
                l.albedo = vec4(0.0);
                l.bend = vec3(0.0);
                l.rough = 0.0;
                vec3 f = pow(abs(n), vec3(6.0));
                f /= f.x + f.y + f.z;
                float top = f.y;
                if (top > 0.02) {
                    l.albedo += top_sample(layer_albedo, p.xz, size, index, mixer, far) * top;
                    vec3 t = top_sample(layer_normal, p.xz, size, index, mixer * 0.5, far).xyz * 2.0 - 1.0;
                    // The maps' x runs east, their y (OpenGL, up the picture) north, against the picture's rows.
                    l.bend += vec3(t.x, 0.0, -t.y) * top;
                    l.rough += top_sample(layer_rough, p.xz, size, index, mixer, far).r * top;
                }
                if (top < 0.98) {
                    vec2 w = f.xz / max(f.x + f.z, 1e-4) * (1.0 - top);
                    vec3 sx = vec3(p.zy / size + vec2(0.0, wobble), index);
                    vec3 sz = vec3(p.xy / size + vec2(0.0, wobble), index);
                    vec3 bx = vec3(p.zy / (size * 3.1) + vec2(0.37, wobble * 0.3), index);
                    vec3 bz = vec3(p.xy / (size * 3.1) + vec2(0.61, wobble * 0.3), index);
                    float second = mix(0.3, 0.6, far);
                    l.albedo += mix(texture(layer_albedo, sx), texture(layer_albedo, bx), second) * w.x
                        + mix(texture(layer_albedo, sz), texture(layer_albedo, bz), second) * w.y;
                    vec3 tx = mix(texture(layer_normal, sx).xyz, texture(layer_normal, bx).xyz, second * 0.5) * 2.0 - 1.0;
                    vec3 tz = mix(texture(layer_normal, sz).xyz, texture(layer_normal, bz).xyz, second * 0.5) * 2.0 - 1.0;
                    // Turned the way the face looks, so the relief stands out of it on either side.
                    l.bend += vec3(0.0, -tx.y, tx.x * sign(n.x)) * w.x + vec3(tz.x * -sign(n.z), -tz.y, 0.0) * w.y;
                    l.rough += texture(layer_rough, sx).r * w.x + texture(layer_rough, sz).r * w.y;
                }
                return l;
            }

            void fragment() {
                vec3 n = normalize(world_normal) * (FRONT_FACING ? 1.0 : -1.0);
                float steep = smoothstep(0.85, 0.65, n.y);
                vec2 drift = vec2(texture(warp, world_pos.xz / 61.0).r, texture(warp, world_pos.zx / 61.0 + 0.37).r) - 0.5;
                // Rock layers rise and fall a little along the cliffs.
                float wobble = (texture(warp, world_pos.xz / 260.0).r - 0.5) * 0.25;
                vec2 at = (world_pos.xz + drift * 5.0) / map_size;
                // How the two close scales mix here, and how far off the ground is.
                float mixer = clamp(0.5 + (texture(warp, world_pos.zx / 37.0 + 0.71).r - 0.5) * 1.6, 0.15, 0.85);
                float dist = length(world_pos - CAMERA_POSITION_WORLD);
                float far = smoothstep(40.0, 180.0, dist);
                vec4 w = texture(material_map, at);
                float keep = clamp(1.0 - dot(w, vec4(1.0)), 0.0, 1.0);
                // Steep faces: only cliff materials; the share the others give up goes to the family's cliff material.
                float given = dot(w, vec4(1.0) - slot_rocky) * steep;
                w = w * mix(vec4(1.0), slot_rocky, steep) + cliff * given;

                Layer l0 = Layer(vec4(0.0), vec3(0.0), 0.0);
                Layer l1 = l0;
                Layer l2 = l0;
                Layer l3 = l0;
                vec4 score = vec4(-1.0);
                if (w.x > 0.01) {
                    l0 = layer(slot_layer.x, slot_size.x, world_pos, n, wobble, mixer, far);
                    score.x = w.x + l0.albedo.a * 0.6;
                }
                if (w.y > 0.01) {
                    l1 = layer(slot_layer.y, slot_size.y, world_pos, n, wobble, mixer, far);
                    score.y = w.y + l1.albedo.a * 0.6;
                }
                if (w.z > 0.01) {
                    l2 = layer(slot_layer.z, slot_size.z, world_pos, n, wobble, mixer, far);
                    score.z = w.z + l2.albedo.a * 0.6;
                }
                if (w.w > 0.01) {
                    l3 = layer(slot_layer.w, slot_size.w, world_pos, n, wobble, mixer, far);
                    score.w = w.w + l3.albedo.a * 0.6;
                }
                // The shares weighed against the heights: the higher grains win where materials meet.
                float keep_score = keep > 0.01 ? keep + 0.3 : -1.0;
                float highest = max(max(max(score.x, score.y), max(score.z, score.w)), keep_score);
                vec4 b = max(score - (highest - blend_depth), vec4(0.0));
                float bk = max(keep_score - (highest - blend_depth), 0.0);
                float total = max(dot(b, vec4(1.0)) + bk, 1e-4);
                b /= total;
                bk /= total;

                // Rock stands out more than sand: deeper relief, and its hollows (low in the height map) darker.
                vec4 depth = mix(vec4(relief), vec4(rock_relief), slot_rocky);
                vec4 hollows = mix(vec4(hollow), vec4(rock_hollow), slot_rocky);
                vec3 colour = vec3(0.0);
                vec3 bent = vec3(0.0);
                float roughness = 0.0;
                if (b.x > 0.0) { colour += l0.albedo.rgb * slot_tint[0] * mix(1.0, 0.45 + 1.1 * l0.albedo.a, hollows.x) * b.x; bent += l0.bend * depth.x * b.x; roughness += l0.rough * b.x; }
                if (b.y > 0.0) { colour += l1.albedo.rgb * slot_tint[1] * mix(1.0, 0.45 + 1.1 * l1.albedo.a, hollows.y) * b.y; bent += l1.bend * depth.y * b.y; roughness += l1.rough * b.y; }
                if (b.z > 0.0) { colour += l2.albedo.rgb * slot_tint[2] * mix(1.0, 0.45 + 1.1 * l2.albedo.a, hollows.z) * b.z; bent += l2.bend * depth.z * b.z; roughness += l2.rough * b.z; }
                if (b.w > 0.0) { colour += l3.albedo.rgb * slot_tint[3] * mix(1.0, 0.45 + 1.1 * l3.albedo.a, hollows.w) * b.w; bent += l3.bend * depth.w * b.w; roughness += l3.rough * b.w; }
                // The old pictures' light and dark: dunes, the rims of craters.
                float shade = texture(shade_map, at).r * (255.0 / 128.0);
                colour *= mix(1.0, shade, shade_strength * (1.0 - steep));
                // The tiles' corner colours, as the original lit its ground by them, spread wide so no tile shows.
                colour *= texture(light_map, world_pos.xz / light_size).rgb;
                vec3 old = texture(albedo_tex, UV).rgb * tint * (vertex_colours ? COLOR.rgb : vec3(1.0));
                ALBEDO = colour + old * bk;
                // The relief fades only far off (the far scale is larger, so it still reads there).
                float near = 1.0 - smoothstep(300.0, 700.0, dist);
                NORMAL = normalize((VIEW_MATRIX * vec4(normalize(n + bent * near), 0.0)).xyz);
                ROUGHNESS = roughness + 0.92 * bk;
                SPECULAR = 0.35;
            }
            """,
    });
}
