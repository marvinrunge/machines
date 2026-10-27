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
/// wide over the ground. Each material has three looks, laid in patches by chance and by what is around (packed sand
/// at the foot of the rock, wet sand by the water, drier grass on the slopes ...). Water, lava and everything built
/// keep their old pictures. The materials and how they vary were picked with the player on the terrain texture and
/// planet pages. Purely for the eye.
/// </summary>
public static class TerrainMaterials
{
    /// <summary>Where a material's variant goes rather than by chance (the player chose terrain and chance together).</summary>
    private enum Role { Main, Chance, Foot, Open, Water, Slope }

    /// <summary>One look of a material: its maps' folder under textures/polyhaven, the metres one repeat covers, and where it lies.</summary>
    private sealed record Variant(string Layer, float Size, Role Role);

    /// <summary>
    /// A material: its three looks (the one picked on the terrain texture page first, then the two other suggestions),
    /// whether it stands on cliffs, and how many grass tufts and stones grow on a square metre of it.
    /// </summary>
    private sealed record Material(Variant[] Variants, bool Rocky, float Grass, float Stones);

    private static readonly Dictionary<string, Material> Materials = new()
    {
        // Ripples out in the open, packed sand at the foot of the rock.
        ["sand"] = new([new("sand", 5, Role.Main), new("aerial_sand", 10, Role.Open), new("dense_sand", 5, Role.Foot)], false, 0, 0.02f),
        ["sandstone"] = new([new("sandstone", 14, Role.Main), new("marble_cliff_02", 14, Role.Chance), new("rock_06", 10, Role.Chance)], true, 0, 0.05f),
        // Wet sand by the water, rippled beach away from the cliffs.
        ["beach"] = new([new("beach", 12, Role.Main), new("aerial_beach_01", 20, Role.Open), new("damp_beach_sand", 8, Role.Water)], false, 0, 0.03f),
        // River pebbles by the water, finer gravel out in the open.
        ["gravel"] = new([new("rock", 5, Role.Main), new("dry_river_pebbles", 4, Role.Water), new("gravel", 5, Role.Open)], false, 0, 0.15f),
        // Muddy, leafy ground by the water, drier grass on the slopes.
        ["drygrass"] = new([new("grass", 4.5f, Role.Main), new("brown_mud_leaves_01", 4, Role.Water), new("grass_ground", 4.5f, Role.Slope)], false, 0.08f, 0.03f),
        // Forest floor under the cliffs.
        ["lushgrass"] = new([new("grass", 4.5f, Role.Main), new("leafy_grass", 4.5f, Role.Chance), new("forrest_ground_01", 4.5f, Role.Foot)], false, 0.6f, 0.01f),
        // Mossier by the water.
        ["mossrock"] = new([new("mossrock", 8, Role.Main), new("aerial_rocks_02", 16, Role.Water), new("lichen_rock", 6, Role.Chance)], true, 0, 0.08f),
        ["regolith"] = new([new("regolith", 7, Role.Main), new("moon_meteor_01", 7, Role.Chance), new("moon_02", 7, Role.Foot)], false, 0, 0.04f),
        ["dust"] = new([new("regolith", 7, Role.Main), new("moon_meteor_01", 7, Role.Chance), new("moon_02", 7, Role.Foot)], false, 0, 0.01f),
        // Scorched, cracked ground by the lava.
        ["redsoil"] = new([new("redsoil", 5, Role.Main), new("laterite", 5, Role.Chance), new("cracked_red_ground", 5, Role.Water)], false, 0, 0.06f),
        ["darkrock"] = new([new("darkrock", 8, Role.Main), new("rock_face_04", 8, Role.Chance), new("dark_rock", 8, Role.Chance)], true, 0, 0.1f),
        // Patchy snow with rock showing at the foot of the cliffs.
        ["snow"] = new([new("snow", 7, Role.Main), new("snow_field_aerial", 20, Role.Foot), new("snow_01", 6, Role.Chance)], false, 0, 0.02f),
        ["greysnow"] = new([new("snow", 7, Role.Main), new("snow_field_aerial", 20, Role.Foot), new("snow_01", 6, Role.Chance)], false, 0, 0.03f),
        ["ice"] = new([new("ice", 12, Role.Main), new("marble_cliff_01", 12, Role.Chance), new("rock_face_04", 10, Role.Chance)], true, 0, 0),
    };

    /// <summary>
    /// How far each look goes to the colour the original painted its material: the picked one most of the way, the two
    /// others keep a quarter of their own colour, so the patches show (the player chose the livelier ground).
    /// </summary>
    private const float MainTint = 0.95f, OtherTint = 0.75f;

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
            // The shore (wated1) is painted like the other water edges: its sand gets the beach, its water stays.
            ["watflw", "watr00", "watfal"],
            [(0, 31.1f, 5.4f, 17.7f), (0, 49.6f, 8.4f, 28), (0, 55, 8.7f, 29),
             (1, 43.8f, 7.9f, 20), (1, 56.1f, 8.6f, 26.9f), (1, 48.5f, 8.4f, 27.9f),
             (2, 28.5f, 1.5f, 33.1f), (2, 17, 4.8f, 21.1f), (2, 27.6f, 10, 32.5f),
             (3, 18.4f, -6.4f, 8), (3, 28.5f, -8.1f, 10.1f), (3, 39.6f, -8.8f, 12.5f),
             (Original, 17.6f, -4, -19), (Original, 30, -12, -6)]),
        // Hills: sand, grey gravel, lush grass, dark rock.
        new(["hilly", "talonth8"],
            [new("beach", "#9c804c", 1), new("gravel", "#484941", 1), new("lushgrass", "#3e461a", 1), new("mossrock", "#443f32", 1)], 3,
            ["wat00", "metal", "metal2", "ductf_t"],
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
            // The lava pools (wat00, wat06) are painted too: their melt keeps to the old picture and is drawn as lava.
            ["tower2", "waltop"],
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
    private const float Softening = 3f;

    /// <summary>
    /// The material map's noise picture, along a side; and how far the borders wander over a couple of hundred metres
    /// (the original's tiles laid them in steps tens of metres long), in metres.
    /// </summary>
    private const int WarpSize = 256;
    private const float BorderWander = 16;

    /// <summary>
    /// The road tiles (the hills' asphalt with hazard stripes along it): their grass is painted like the ground around,
    /// their road drawn anew (photographed asphalt, sharp stripes), and their borders keep straight.
    /// </summary>
    private static readonly HashSet<string> Roads = ["sroad", "sroad2"];

    /// <summary>
    /// The families (by their first planet) whose old pictures hold lava, and sulphur: drawn anew where they show, the
    /// lava flowing and glowing (<see cref="LavaGlsl"/>), the sulphur a crust of crystals.
    /// </summary>
    private static readonly HashSet<string> Lava = ["m_moon", "dogbar"], Sulphur = ["m_moon"];

    /// <summary>
    /// Lava (ambientCG's Lava001 and Lava004, CC0, fetched by tools/terrain-textures/fetch.sh into textures/ambientcg),
    /// for the ground and for the pools the scenarios set (<see cref="Scenery.Lava"/>): a dark crust seamed with glowing
    /// cracks where it has cooled, open melt where it is hot (molten, 0 to 1, and slow noise decide), both flowing slowly
    /// and the glow breathing a little. The maps are <see cref="LavaMaps"/> (crust colour and glow, melt colour and
    /// glow); the noise is the repeating picture of <see cref="WarpNoise"/>. Returns the colour; the glow goes out
    /// through the last argument.
    /// </summary>
    public const string LavaGlsl = """
        vec3 lava_at(sampler2DArray maps, sampler2D noise, vec3 p, float molten, float time, out vec3 glow) {
            // Twelve metres to a repeat, read through a slow drifting wobble, so the lava creeps rather than slides.
            vec2 uv = p.xz / 12.0 + (vec2(texture(noise, p.xz / 60.0 + time * 0.003).r, texture(noise, p.zx / 60.0 - time * 0.002).r) - 0.5) * 0.5;
            vec2 crust_uv = uv + vec2(time * 0.0025, time * 0.0015);
            vec2 melt_uv = uv * 0.8 - vec2(time * 0.006, -time * 0.004);
            // Plates of crust tens of metres across, drifting slowly over the melt.
            float plates = texture(noise, p.xz / 70.0 + 0.4 + vec2(time * 0.0012, time * 0.0008)).r * 0.7 + texture(noise, p.zx / 19.0 + 0.1).r * 0.3;
            float open = clamp((molten - 0.5) * 2.0 + (plates - 0.5) * 2.4 + 0.5, 0.0, 1.0);
            open = smoothstep(0.2, 0.8, open);
            vec3 colour = mix(texture(maps, vec3(crust_uv, 0.0)).rgb, texture(maps, vec3(melt_uv, 2.0)).rgb, open);
            vec3 heat = mix(texture(maps, vec3(crust_uv, 1.0)).rgb, texture(maps, vec3(melt_uv, 3.0)).rgb, open);
            float breath = 0.88 + 0.12 * sin(time * 0.9 + texture(noise, p.xz / 9.0).r * 10.0);
            // Deeper red than the maps: bright orange light turns pale and yellowish on screen.
            glow = heat * vec3(1.0, 0.55, 0.35) * breath * 1.4;
            return colour * 0.35;
        }
        """;

    /// <summary>
    /// The lava's maps as one array (crust colour, crust glow, melt colour, melt glow), or null when they were not
    /// fetched: the lava then keeps the original's picture.
    /// </summary>
    public static Texture2DArray? LavaMaps => LavaMapsLoaded.Value;

    private static readonly Lazy<Texture2DArray?> LavaMapsLoaded = new(() =>
    {
        var folder = Path.Combine(Paths.Root, "textures", "ambientcg");
        var images = new Godot.Collections.Array<Image>();
        foreach (var (id, map) in new[] { ("Lava001", "albedo"), ("Lava001", "emission"), ("Lava004", "albedo"), ("Lava004", "emission") })
        {
            if (Read(Path.Combine(folder, id, map + ".jpg"), Image.Format.Rgb8, Image.CompressSource.Srgb) is not { } image)
                return null;
            images.Add(image);
        }
        var maps = new Texture2DArray();
        return maps.CreateFromImages(images) == Error.Ok ? maps : null;
    });

    /// <summary>The ground's wandering noise (its borders and patches wander by it, its lava creeps by it).</summary>
    public static Image WarpNoise() => new FastNoiseLite { Frequency = 1 / 24f, FractalOctaves = 3, Seed = 3 }.GetSeamlessImage(WarpSize, WarpSize);

    /// <summary>The asphalt the roads are drawn in (textures/polyhaven/asphalt), or null when it was not fetched.</summary>
    private static readonly Lazy<Texture2D?> Asphalt = new(() =>
        Read(Path.Combine(Folder, "asphalt", "albedo.jpg"), Image.Format.Rgb8, Image.CompressSource.Srgb) is { } image ? ImageTexture.CreateFromImage(image) : null);

    /// <summary>The cells of the light map (the tiles' corner colours), and how far it is spread, in metres.</summary>
    private const float LightCell = 2, LightSpread = 6;

    /// <summary>
    /// The cells of the context map (how near the rock and how near the water or lava are), and how far each reaches,
    /// in metres.
    /// </summary>
    private const float ContextCell = 2, FootReach = 10, WaterReach = 14;

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

        private readonly float[] _warp;

        internal Ground(HashSet<int> surfaces, byte[] weights, int columns, int rows, float cell, (float Grass, float Stones, Color Colour)[] slots,
            byte[] light, int lightColumns, int lightRows, float[] warp)
        {
            Surfaces = surfaces;
            (_weights, _columns, _rows, _cell, _slots) = (weights, columns, rows, cell, slots);
            (_light, _lightColumns, _lightRows, _warp) = (light, lightColumns, lightRows, warp);
        }

        /// <summary>The wandering noise as the shader reads it: repeating, blended between its pixels.</summary>
        private float Warp(float u, float v)
        {
            var (x, y) = (u * WarpSize - 0.5f, v * WarpSize - 0.5f);
            var (x0, y0) = ((int)Mathf.Floor(x), (int)Mathf.Floor(y));
            var (fx, fy) = (x - x0, y - y0);
            float At(int px, int py) => _warp[Mathf.PosMod(py, WarpSize) * WarpSize + Mathf.PosMod(px, WarpSize)];
            return Mathf.Lerp(Mathf.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Mathf.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
        }

        /// <summary>The terrain surfaces drawn from the material map.</summary>
        public IReadOnlySet<int> Surfaces { get; }

        /// <summary>Those of them that are roads: nothing grows on them.</summary>
        public IReadOnlySet<int> Roads { get; init; } = new HashSet<int>();

        /// <summary>
        /// Grass tufts and stones per square metre at a point of the ground, the colour of its grass and of its stones
        /// (sRGB); none where the old picture stays.
        /// </summary>
        public (float Grass, float Stones, Color GrassColour, Color StoneColour) At(float x, float z)
        {
            // Where the shader reads the map for this point (its borders wander), between the cells as it blends them:
            // grass and stones keep to the painted ground and thin out over its soft edges.
            var (wx, wz) = (x, z);
            x += (Warp(wx / 61, wz / 61) - 0.5f) * 5 + (Warp(wx / 190 + 0.23f, wz / 190 + 0.23f) - 0.5f) * BorderWander;
            z += (Warp(wz / 61 + 0.37f, wx / 61 + 0.37f) - 0.5f) * 5 + (Warp(wz / 190 + 0.61f, wx / 190 + 0.61f) - 0.5f) * BorderWander;
            var (fx, fz) = (Mathf.Clamp(x / _cell - 0.5f, 0, _columns - 1), Mathf.Clamp(z / _cell - 0.5f, 0, _rows - 1));
            var (cx, cz) = ((int)fx, (int)fz);
            var (cx1, cz1) = (Math.Min(cx + 1, _columns - 1), Math.Min(cz + 1, _rows - 1));
            var (tx, tz) = (fx - cx, fz - cz);
            float Weight(int s) => Mathf.Lerp(Mathf.Lerp(_weights[(cz * _columns + cx) * 4 + s], _weights[(cz * _columns + cx1) * 4 + s], tx),
                Mathf.Lerp(_weights[(cz1 * _columns + cx) * 4 + s], _weights[(cz1 * _columns + cx1) * 4 + s], tx), tz) / 255f;
            float grass = 0, stones = 0, grassWeight = 0;
            Color grassColour = new(0, 0, 0), stoneColour = new(0, 0, 0);
            for (var s = 0; s < _slots.Length; s++)
            {
                var w = Weight(s);
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
        var roads = new HashSet<int>();
        for (var s = 0; s < terrain.GetSurfaceCount(); s++)
        {
            if (terrain.SurfaceGetMaterial(s) is not StandardMaterial3D { Transparency: BaseMaterial3D.TransparencyEnum.Disabled, AlbedoTexture: { } picture } material)
                continue;
            var name = Path.GetFileNameWithoutExtension(material.GetMeta("texture", "").AsString());
            if (name.EndsWith("_b", StringComparison.Ordinal))
                name = name[..^2];
            var road = Roads.Contains(name);
            if (family.Keep.Contains(name) || (!road && TerrainDetail.LayerOf(material) == "metal"))
                continue;
            natural[s] = material;
            if (road)
                roads.Add(s);
            if (!pictures.ContainsKey(picture))
                pictures[picture] = Classify(picture, family, road);
        }
        if (natural.Count == 0)
            return null;

        var cell = Mathf.Max(FinestCell, Mathf.Max(width, depth) / MaxCells);
        var (columns, rows) = (Mathf.CeilToInt(width / cell), Mathf.CeilToInt(depth / cell));
        var (weights, shade, context) = Paint(terrain, natural, pictures, cell, columns, rows, family.Slots.Length);
        if (System.Environment.GetEnvironmentVariable("MACHINES_MATERIAL_MAP") is { Length: > 0 } dump)
        {
            Dump(dump, weights, shade, columns, rows);
            GD.Print($"material map of {planet}: {width} x {depth} m");
        }

        var map = ImageTexture.CreateFromImage(Image.CreateFromData(columns, rows, false, Image.Format.Rgba8, weights));
        var shadeMap = ImageTexture.CreateFromImage(Image.CreateFromData(columns, rows, false, Image.Format.L8, shade));
        var contextMap = ImageTexture.CreateFromImage(Image.CreateFromData(context.Columns, context.Rows, false, Image.Format.Rg8, context.Data));
        var (lights, lightColumns, lightRows) = Light(ground, natural.Keys, width, depth);
        var lightMap = ImageTexture.CreateFromImage(Image.CreateFromData(lightColumns, lightRows, false, Image.Format.Rgb8, lights));
        var warpImage = WarpNoise();
        var warp = ImageTexture.CreateFromImage(warpImage);

        // Each slot's three looks: their layers, sizes and tints, and how much each is drawn to the foot of the rock,
        // the open, the water (or lava) and the slopes. The picked look starts ahead, so it covers about half the ground.
        var slotRocky = new float[4];
        var (variantLayer, variantSize, variantBias) = (new Vector3[4], new Vector3[4], new Vector3[4]);
        var (variantFoot, variantOpen, variantWater, variantSlope) = (new Vector3[4], new Vector3[4], new Vector3[4], new Vector3[4]);
        var variantTint = new Vector3[12];
        for (var s = 0; s < family.Slots.Length; s++)
        {
            var slot = family.Slots[s];
            var material = Materials[slot.Material];
            slotRocky[s] = material.Rocky ? 1 : 0;
            var target = Color.FromHtml(slot.Colour).SrgbToLinear();
            for (var v = 0; v < 3; v++)
            {
                var variant = material.Variants[v];
                var layer = Array.IndexOf(layers.Names, variant.Layer);
                variantLayer[s][v] = layer;
                variantSize[s][v] = variant.Size;
                variantBias[s][v] = variant.Role == Role.Main ? 0.3f : 0;
                variantFoot[s][v] = variant.Role == Role.Foot ? 0.9f : 0;
                variantOpen[s][v] = variant.Role == Role.Open ? 0.5f : 0;
                variantWater[s][v] = variant.Role == Role.Water ? 0.9f : 0;
                variantSlope[s][v] = variant.Role == Role.Slope ? 0.8f : 0;
                // Each look, on average, takes most of the colour the original had (fully, or halfway, as picked).
                var mean = layers.Means[layer];
                var gain = new Vector3(target.R / mean.X, target.G / mean.Y, target.B / mean.Z);
                var share = slot.Tint * (variant.Role == Role.Main ? MainTint : OtherTint);
                variantTint[s * 3 + v] = new Vector3(Mathf.Pow(gain.X, share), Mathf.Pow(gain.Y, share), Mathf.Pow(gain.Z, share)).Clamp(Vector3.One * 0.1f, Vector3.One * 8);
            }
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
                shader.SetShaderParameter("lava_ground", Lava.Contains(family.Planets[0]) && LavaMaps is not null);
                if (LavaMaps is { } lavaMaps)
                    shader.SetShaderParameter("lava_maps", lavaMaps);
                shader.SetShaderParameter("sulphur_ground", Sulphur.Contains(family.Planets[0]));
                if (roads.Contains(s) && Asphalt.Value is { } asphalt)
                {
                    shader.SetShaderParameter("road", true);
                    shader.SetShaderParameter("asphalt", asphalt);
                }
                shader.SetShaderParameter("tint", new Vector3(original.AlbedoColor.R, original.AlbedoColor.G, original.AlbedoColor.B));
                shader.SetShaderParameter("vertex_colours", original.VertexColorUseAsAlbedo);
                shader.SetShaderParameter("material_map", map);
                shader.SetShaderParameter("shade_map", shadeMap);
                shader.SetShaderParameter("light_map", lightMap);
                shader.SetShaderParameter("light_size", new Vector2(lightColumns * LightCell, lightRows * LightCell));
                shader.SetShaderParameter("map_size", new Vector2(columns * cell, rows * cell));
                shader.SetShaderParameter("warp", warp);
                shader.SetShaderParameter("border_wander", BorderWander);
                shader.SetShaderParameter("layer_albedo", layers.Albedo);
                shader.SetShaderParameter("layer_normal", layers.Normal);
                shader.SetShaderParameter("layer_rough", layers.Roughness);
                shader.SetShaderParameter("slot_rocky", new Vector4(slotRocky[0], slotRocky[1], slotRocky[2], slotRocky[3]));
                shader.SetShaderParameter("cliff", new Vector4(cliff[0], cliff[1], cliff[2], cliff[3]));
                shader.SetShaderParameter("variant_layer", variantLayer);
                shader.SetShaderParameter("variant_size", variantSize);
                shader.SetShaderParameter("variant_bias", variantBias);
                shader.SetShaderParameter("variant_foot", variantFoot);
                shader.SetShaderParameter("variant_open", variantOpen);
                shader.SetShaderParameter("variant_water", variantWater);
                shader.SetShaderParameter("variant_slope", variantSlope);
                shader.SetShaderParameter("variant_tint", variantTint);
                shader.SetShaderParameter("context_map", contextMap);
                shader.SetShaderParameter("context_size", new Vector2(context.Columns * ContextCell, context.Rows * ContextCell));
            }
            terrain.SurfaceSetMaterial(s, shader);
        }
        var slots = family.Slots.Select(s => (Materials[s.Material].Grass, Materials[s.Material].Stones, Color.FromHtml(s.Colour))).ToArray();
        return new Ground([.. natural.Keys], weights, columns, rows, cell, slots, lights, lightColumns, lightRows,
            Enumerable.Range(0, WarpSize * WarpSize).Select(i => warpImage.GetPixel(i % WarpSize, i / WarpSize).R).ToArray()) { Roads = roads };
    }

    /// <summary>
    /// What an old picture shows, texel by texel at <see cref="Grid"/> × <see cref="Grid"/>: the slot of the nearest
    /// colour (4 where the old picture stays), and how light the texel is against the picture's other texels of that
    /// material (128 = as light; 0 to 255 = none to twice).
    /// </summary>
    private static (byte[] Class, byte[] Shade) Classify(Texture2D picture, Family family, bool road = false)
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
            // On a road tile the grey asphalt and kerbs and the yellow stripes are the road (drawn anew); the grass is ground.
            if (road && ((lab.Y * lab.Y + lab.Z * lab.Z < 225 && lab.X < 75) || lab.Z > 50))
                nearest = 0;
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
    /// Four bytes a cell for the slots' shares (what is left keeps the old picture), and one for the brightness; and
    /// the context map (see <see cref="Context"/>).
    /// </summary>
    private static (byte[] Weights, byte[] Shade, (byte[] Data, int Columns, int Rows) Context) Paint(Mesh terrain, Dictionary<int, StandardMaterial3D> natural,
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

        var context = Context(classOf, flatOf, cell, columns, rows);

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
        return (weights, shade, context);
    }

    /// <summary>
    /// The context map, two bytes a cell of <see cref="ContextCell"/>: how near the rock is (steep faces, out to
    /// <see cref="FootReach"/>) and how near the water or lava (what keeps the old picture, out to
    /// <see cref="WaterReach"/>), from the material map's cells before they were filled in.
    /// </summary>
    private static (byte[] Data, int Columns, int Rows) Context(byte[] classOf, float[] flatOf, float cell, int columns, int rows)
    {
        var step = Math.Max(1, Mathf.RoundToInt(ContextCell / cell));
        var (outColumns, outRows) = ((columns + step - 1) / step, (rows + step - 1) / step);
        var steep = new byte[outColumns * outRows];
        var kept = new byte[outColumns * outRows];
        Parallel.For(0, outRows, cz =>
        {
            for (var cx = 0; cx < outColumns; cx++)
            {
                int steepCount = 0, keptCount = 0, count = 0;
                for (var z = cz * step; z < Math.Min(rows, (cz + 1) * step); z++)
                {
                    for (var x = cx * step; x < Math.Min(columns, (cx + 1) * step); x++)
                    {
                        var o = z * columns + x;
                        count++;
                        if (flatOf[o] >= 0 && flatOf[o] < 0.8f)
                            steepCount++;
                        if (classOf[o] == 4 || classOf[o] == 255)
                            keptCount++;
                    }
                }
                var c = cz * outColumns + cx;
                (steep[c], kept[c]) = ((byte)(255 * steepCount / Math.Max(count, 1)), (byte)(255 * keptCount / Math.Max(count, 1)));
            }
        });
        // Spread out to their reach; anywhere within it counts fully, fading at its end.
        Soften(steep, outColumns, outRows, 1, Mathf.RoundToInt(FootReach / ContextCell));
        Soften(kept, outColumns, outRows, 1, Mathf.RoundToInt(WaterReach / ContextCell));
        var data = new byte[outColumns * outRows * 2];
        for (var c = 0; c < steep.Length; c++)
            (data[c * 2], data[c * 2 + 1]) = ((byte)Math.Min(255, steep[c] * 4), (byte)Math.Min(255, kept[c] * 4));
        return (data, outColumns, outRows);
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

    /// <summary>The colours <see cref="Dump"/> marks the slots with (Okabe and Ito's, told apart by everyone).</summary>
    private static readonly Color[] Legend = [new(0.9f, 0.62f, 0), new(0.34f, 0.71f, 0.91f), new(0, 0.62f, 0.45f), new(0.8f, 0.47f, 0.65f)];

    /// <summary>
    /// For the texture pages (MACHINES_MATERIAL_MAP): the material map seen from above, north up, each slot in its
    /// <see cref="Legend"/> colour, dark grey where the old picture stays, lit by the old pictures' light and dark.
    /// </summary>
    private static void Dump(string path, byte[] weights, byte[] shade, int columns, int rows)
    {
        var image = Image.CreateEmpty(columns, rows, false, Image.Format.Rgb8);
        for (var z = 0; z < rows; z++)
            for (var x = 0; x < columns; x++)
            {
                var o = z * columns + x;
                var (colour, left) = (Colors.Black, 1f);
                for (var s = 0; s < 4; s++)
                {
                    var w = weights[o * 4 + s] / 255f;
                    (colour, left) = (colour + Legend[s] * w, left - w);
                }
                colour += new Color(0.22f, 0.22f, 0.22f) * Mathf.Max(left, 0);
                image.SetPixel(x, z, (colour * Mathf.Clamp(0.55f + 0.45f * shade[o] / 128f, 0.5f, 1.2f)) with { A = 1 });
            }
        var scale = Mathf.Min(1, 1024f / Mathf.Max(columns, rows));
        image.Resize(Mathf.RoundToInt(columns * scale), Mathf.RoundToInt(rows * scale), Image.Interpolation.Bilinear);
        image.SavePng(path);
    }

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
        var names = family.Slots.SelectMany(s => Materials[s.Material].Variants).Select(v => v.Layer).Distinct().ToArray();
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
        Code = $$"""
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
            uniform float border_wander;
            // A road tile: its road drawn anew in the photographed asphalt with sharp hazard stripes, its borders straight.
            uniform bool road = false;
            uniform sampler2D asphalt : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            // Lava and sulphur in the old pictures, drawn anew (on the planets that have them).
            uniform bool lava_ground = false;
            uniform sampler2DArray lava_maps : source_color, filter_linear_mipmap, repeat_enable;
            uniform bool sulphur_ground = false;
            uniform sampler2DArray layer_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform sampler2DArray layer_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform sampler2DArray layer_rough : filter_linear_mipmap_anisotropic, repeat_enable;
            uniform vec4 slot_rocky = vec4(0.0);
            uniform vec4 cliff = vec4(1.0, 0.0, 0.0, 0.0);
            uniform vec3 variant_layer[4];
            uniform vec3 variant_size[4];
            uniform vec3 variant_bias[4];
            uniform vec3 variant_foot[4];
            uniform vec3 variant_open[4];
            uniform vec3 variant_water[4];
            uniform vec3 variant_slope[4];
            uniform vec3 variant_tint[12];
            uniform sampler2D context_map : filter_linear;
            uniform vec2 context_size = vec2(1600.0);
            uniform float patches = 0.12;
            global uniform float weather_wetness;
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
            // The colour from above: close up the size and the 3.7 times larger copy, farther off that copy and the 14
            // times larger one; the middle copy is read once for both, so no point reads more than three times.
            vec4 top_colour(vec2 p, float size, float index, float mixer, float far) {
                vec4 middle = texture(layer_albedo, vec3(mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * p / (size * 3.7), index));
                vec4 near_value = far < 0.99 ? mix(texture(layer_albedo, vec3(p / size, index)), middle, mixer) : middle;
                vec4 far_value = far > 0.01 ? mix(middle, texture(layer_albedo, vec3(mat2(vec2(-0.28, 0.96), vec2(-0.96, -0.28)) * p / (size * 14.0), index)), 0.5) : middle;
                return mix(near_value, far_value, far);
            }

            // Seen from above, and from the two sides by how much the face looks each way, so slopes do not stretch it;
            // the sides at two scales too. The relief and roughness read fewer copies than the colour: they show less.
            Layer layer(float index, float size, vec3 p, vec3 n, float wobble, float mixer, float far) {
                Layer l;
                l.albedo = vec4(0.0);
                l.bend = vec3(0.0);
                l.rough = 0.0;
                vec3 f = pow(abs(n), vec3(6.0));
                f /= f.x + f.y + f.z;
                float top = f.y;
                if (top > 0.02) {
                    l.albedo += top_colour(p.xz, size, index, mixer, far) * top;
                    vec3 a = vec3(p.xz / size, index);
                    vec3 b = vec3(mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * p.xz / (size * 3.7), index);
                    vec3 t = mix(texture(layer_normal, a).xyz, texture(layer_normal, b).xyz, max(mixer * 0.5, far)) * 2.0 - 1.0;
                    // The maps' x runs east, their y (OpenGL, up the picture) north, against the picture's rows.
                    l.bend += vec3(t.x, 0.0, -t.y) * top;
                    l.rough += texture(layer_rough, b).r * top;
                }
                if (top < 0.98) {
                    vec2 w = f.xz / max(f.x + f.z, 1e-4) * (1.0 - top);
                    float second = mix(0.3, 0.6, far);
                    // Turned the way the face looks, so the relief stands out of it on either side.
                    if (w.x > 0.02) {
                        vec3 sx = vec3(p.zy / size + vec2(0.0, wobble), index);
                        vec3 bx = vec3(p.zy / (size * 3.1) + vec2(0.37, wobble * 0.3), index);
                        l.albedo += mix(texture(layer_albedo, sx), texture(layer_albedo, bx), second) * w.x;
                        vec3 tx = texture(layer_normal, sx).xyz * 2.0 - 1.0;
                        l.bend += vec3(0.0, -tx.y, tx.x * sign(n.x)) * w.x;
                        l.rough += texture(layer_rough, sx).r * w.x;
                    }
                    if (w.y > 0.02) {
                        vec3 sz = vec3(p.xy / size + vec2(0.0, wobble), index);
                        vec3 bz = vec3(p.xy / (size * 3.1) + vec2(0.61, wobble * 0.3), index);
                        l.albedo += mix(texture(layer_albedo, sz), texture(layer_albedo, bz), second) * w.y;
                        vec3 tz = texture(layer_normal, sz).xyz * 2.0 - 1.0;
                        l.bend += vec3(tz.x * -sign(n.z), -tz.y, 0.0) * w.y;
                        l.rough += texture(layer_rough, sz).r * w.y;
                    }
                }
                return l;
            }

            {{LavaGlsl}}

            // One material at a point: its three looks are scored by slow noise (patches of 20 to 60 m) and by what is
            // around (the foot of the rock, the open, the water, the slope); the best two are sampled, tinted each to
            // its share of the original's colour, and mixed by their heights where they are close.
            Layer material(int s, vec3 p, vec3 n, float wobble, float mixer, float far, vec2 ctx, float slope) {
                vec3 noise = vec3(texture(warp, p.xz / 400.0 + vec2(0.11, 0.37) * float(s)).r,
                    texture(warp, p.zx / 430.0 + vec2(0.53, 0.21) + vec2(0.17) * float(s)).r,
                    texture(warp, p.xz / 370.0 + vec2(0.77, 0.69) + vec2(0.29) * float(s)).r);
                vec3 score = (noise - 0.5) * 1.6 + variant_bias[s] + variant_foot[s] * ctx.x + variant_open[s] * (1.0 - ctx.x)
                    + variant_water[s] * ctx.y + variant_slope[s] * slope;
                int first = score.x >= score.y && score.x >= score.z ? 0 : (score.y >= score.z ? 1 : 2);
                vec3 rest = score;
                rest[first] = -1e3;
                int second = rest.x >= rest.y && rest.x >= rest.z ? 0 : (rest.y >= rest.z ? 1 : 2);
                Layer a = layer(variant_layer[s][first], variant_size[s][first], p, n, wobble, mixer, far);
                a.albedo.rgb *= variant_tint[s * 3 + first];
                float gap = score[first] - score[second];
                if (gap > 0.25)
                    return a;
                Layer b = layer(variant_layer[s][second], variant_size[s][second], p, n, wobble, mixer, far);
                b.albedo.rgb *= variant_tint[s * 3 + second];
                float wa = max(gap + a.albedo.a * 0.5 - b.albedo.a * 0.5 + 0.12, 0.0);
                float wb = max(b.albedo.a * 0.5 - a.albedo.a * 0.5 - gap + 0.12, 0.0);
                float t = wb / max(wa + wb, 1e-4);
                Layer l;
                l.albedo = mix(a.albedo, b.albedo, t);
                l.bend = mix(a.bend, b.bend, t);
                l.rough = mix(a.rough, b.rough, t);
                return l;
            }

            void fragment() {
                vec3 n = normalize(world_normal) * (FRONT_FACING ? 1.0 : -1.0);
                float steep = smoothstep(0.85, 0.65, n.y);
                vec2 drift = vec2(texture(warp, world_pos.xz / 61.0).r, texture(warp, world_pos.zx / 61.0 + 0.37).r) - 0.5;
                // Rock layers rise and fall a little along the cliffs.
                float wobble = (texture(warp, world_pos.xz / 260.0).r - 0.5) * 0.25;
                // The borders wander twice: a few metres close up, and more over a couple of hundred metres, so the
                // original's steps turn into bends (Ground.At reads the map the same way).
                vec2 broad = vec2(texture(warp, world_pos.xz / 190.0 + 0.23).r, texture(warp, world_pos.zx / 190.0 + 0.61).r) - 0.5;
                vec2 at = (world_pos.xz + (road ? vec2(0.0) : drift * 5.0 + broad * border_wander)) / map_size;
                // How the two close scales mix here, and how far off the ground is.
                float mixer = clamp(0.5 + (texture(warp, world_pos.zx / 37.0 + 0.71).r - 0.5) * 1.6, 0.15, 0.85);
                float dist = length(world_pos - CAMERA_POSITION_WORLD);
                float far = smoothstep(40.0, 180.0, dist);
                vec4 w = texture(material_map, at);
                float keep = clamp(1.0 - dot(w, vec4(1.0)), 0.0, 1.0);
                // Steep faces: only cliff materials; the share the others give up goes to the family's cliff material.
                float given = dot(w, vec4(1.0) - slot_rocky) * steep;
                w = w * mix(vec4(1.0), slot_rocky, steep) + cliff * given;

                // What is around: how near the rock and the water (or lava) are, and how sloped the ground is here.
                vec2 ctx = texture(context_map, world_pos.xz / context_size).rg;
                float slope = smoothstep(0.98, 0.88, n.y);

                Layer l0 = Layer(vec4(0.0), vec3(0.0), 0.0);
                Layer l1 = l0;
                Layer l2 = l0;
                Layer l3 = l0;
                vec4 score = vec4(-1.0);
                if (w.x > 0.01) {
                    l0 = material(0, world_pos, n, wobble, mixer, far, ctx, slope);
                    score.x = w.x + l0.albedo.a * 0.6;
                }
                if (w.y > 0.01) {
                    l1 = material(1, world_pos, n, wobble, mixer, far, ctx, slope);
                    score.y = w.y + l1.albedo.a * 0.6;
                }
                if (w.z > 0.01) {
                    l2 = material(2, world_pos, n, wobble, mixer, far, ctx, slope);
                    score.z = w.z + l2.albedo.a * 0.6;
                }
                if (w.w > 0.01) {
                    l3 = material(3, world_pos, n, wobble, mixer, far, ctx, slope);
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
                if (b.x > 0.0) { colour += l0.albedo.rgb * mix(1.0, 0.45 + 1.1 * l0.albedo.a, hollows.x) * b.x; bent += l0.bend * depth.x * b.x; roughness += l0.rough * b.x; }
                if (b.y > 0.0) { colour += l1.albedo.rgb * mix(1.0, 0.45 + 1.1 * l1.albedo.a, hollows.y) * b.y; bent += l1.bend * depth.y * b.y; roughness += l1.rough * b.y; }
                if (b.z > 0.0) { colour += l2.albedo.rgb * mix(1.0, 0.45 + 1.1 * l2.albedo.a, hollows.z) * b.z; bent += l2.bend * depth.z * b.z; roughness += l2.rough * b.z; }
                if (b.w > 0.0) { colour += l3.albedo.rgb * mix(1.0, 0.45 + 1.1 * l3.albedo.a, hollows.w) * b.w; bent += l3.bend * depth.w * b.w; roughness += l3.rough * b.w; }
                // Large patches a little lighter and darker, so the ground does not look evenly made.
                colour *= 1.0 + patches * (texture(warp, world_pos.zx / 310.0 + 0.13).r - 0.5) * 2.0;
                // The old pictures' light and dark: dunes, the rims of craters.
                float shade = texture(shade_map, at).r * (255.0 / 128.0);
                colour *= mix(1.0, shade, shade_strength * (1.0 - steep));
                // The tiles' corner colours, as the original lit its ground by them, spread wide so no tile shows.
                colour *= texture(light_map, world_pos.xz / light_size).rgb;
                vec3 old = texture(albedo_tex, UV).rgb;
                if (road) {
                    // The dark, colourless stretches are asphalt: the photograph at its own scale, as light as the old
                    // road was there. The hazard bands (yellow and black blurred together they lean yellow) get sharp
                    // slanting stripes; the kerbs keep their picture.
                    float lum = dot(old, vec3(0.2126, 0.7152, 0.0722));
                    float chroma = max(old.r, max(old.g, old.b)) - min(old.r, min(old.g, old.b));
                    vec3 tar = texture(asphalt, world_pos.xz / 4.0).rgb;
                    float tar_lum = dot(textureLod(asphalt, vec2(0.5), 12.0).rgb, vec3(0.2126, 0.7152, 0.0722));
                    float paved = smoothstep(0.1, 0.04, chroma) * smoothstep(0.3, 0.18, lum);
                    old = mix(old, tar * (lum / max(tar_lum, 0.01)), paved);
                    vec3 blurred = textureLod(albedo_tex, UV, 3.0).rgb;
                    float band = smoothstep(0.09, 0.13, blurred.r - blurred.b) * smoothstep(0.12, 0.2, blurred.r)
                        // ... and there only where it is yellow or black itself, not where the road fades into the grass.
                        * max(smoothstep(0.18, 0.28, old.r - old.b), smoothstep(0.14, 0.08, lum));
                    float slant = fract((UV.x + UV.y) * 24.0);
                    float edge = fwidth((UV.x + UV.y) * 24.0);
                    float yellow = smoothstep(0.5 - edge, 0.5 + edge, slant) * (1.0 - smoothstep(1.0 - edge, 1.0, slant));
                    old = mix(old, mix(vec3(0.03), vec3(0.78, 0.58, 0.04), yellow), band);
                }
                vec3 glow = vec3(0.0);
                // Told apart by the old picture's colour: lava is red to orange (and its brightest spots, yellow, lie
                // in red), sulphur yellow.
                float lava = lava_ground ? smoothstep(0.1, 0.25, old.r - max(old.g, old.b) * 1.1) : 0.0;
                if (lava > 0.0) {
                    // Hot and open where the old picture was brightest, crusted towards its rim.
                    vec3 heat_glow;
                    float bright = dot(textureLod(albedo_tex, UV, 3.0).rgb, vec3(0.5, 0.4, 0.1));
                    vec3 melt = lava_at(lava_maps, warp, world_pos, 0.2 + 0.9 * bright, TIME, heat_glow);
                    old = mix(old, melt, lava);
                    glow = heat_glow * lava;
                }
                float sulphur = sulphur_ground
                    ? smoothstep(0.1, 0.22, min(old.r, old.g) - old.b) * (1.0 - smoothstep(0.25, 0.4, abs(old.r - old.g))) * (1.0 - lava) : 0.0;
                if (sulphur > 0.0) {
                    // A crust of small crystals, bright where they catch the light, stained brown in patches, as light
                    // or dark at large as the old picture was there.
                    float grain = texture(warp, world_pos.xz / 4.0).r;
                    float fine = texture(warp, world_pos.zx / 1.1 + 0.5).r;
                    float stain = texture(warp, world_pos.xz / 17.0 + 0.2).r;
                    vec3 crystals = mix(vec3(0.6, 0.42, 0.04), vec3(0.88, 0.72, 0.1), smoothstep(0.3, 0.7, grain));
                    crystals = mix(crystals, vec3(0.97, 0.9, 0.45), smoothstep(0.7, 0.88, fine) * 0.45);
                    crystals = mix(crystals, vec3(0.34, 0.22, 0.05), smoothstep(0.55, 0.75, stain) * 0.4);
                    float broad_light = dot(textureLod(albedo_tex, UV, 4.0).rgb, vec3(0.3, 0.6, 0.1));
                    old = mix(old, crystals * (0.55 + 0.7 * broad_light), sulphur);
                }
                old *= tint * (vertex_colours ? COLOR.rgb : vec3(1.0));
                ALBEDO = colour + old * bk;
                EMISSION = glow * bk;
                // The relief fades only far off (the far scale is larger, so it still reads there).
                float near = 1.0 - smoothstep(300.0, 700.0, dist);
                NORMAL = normalize((VIEW_MATRIX * vec4(normalize(n + bent * near), 0.0)).xyz);
                ROUGHNESS = roughness + 0.92 * bk;
                // Rain darkens the ground and makes it glisten, more where it lies flat and in its hollows.
                float wet = weather_wetness * (1.0 - steep) * mix(0.75, 1.0, 1.0 - (l0.albedo.a * b.x + l1.albedo.a * b.y + l2.albedo.a * b.z + l3.albedo.a * b.w));
                ALBEDO *= mix(1.0, 0.55, wet);
                ROUGHNESS = mix(ROUGHNESS, 0.15, wet * 0.85);
                SPECULAR = 0.35;
            }
            """,
    });
}
