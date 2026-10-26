using System.Text.Json;
using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// Meshes and textures for units and buildings. Uses the models exported from the player's own copy of the
/// game (<c>classic/models</c>, see tools/classic-export) and loads textures straight from that copy; falls
/// back to simple race-coloured shapes when they are missing.
/// </summary>
public sealed class ClassicAssets
{
    private readonly string? _modelFolder;
    private readonly string? _planetFolder;
    private readonly string? _gameFolder;
    private readonly Dictionary<string, Mesh> _meshes = [];
    private readonly Dictionary<string, Material> _materials = [];
    private readonly Dictionary<string, Rig?> _rigs = [];

    public ClassicAssets(string repositoryFolder)
    {
        var models = Path.Combine(repositoryFolder, "classic", "models");
        _modelFolder = Directory.Exists(models) ? models : null;
        var planets = Path.Combine(repositoryFolder, "classic", "planets");
        _planetFolder = Directory.Exists(planets) ? planets : null;
        var gamePath = Path.Combine(repositoryFolder, "classic", "game_path.txt");
        if (File.Exists(gamePath))
        {
            var folder = File.ReadAllText(gamePath).Trim();
            _gameFolder = Directory.Exists(folder) ? folder : null;
        }
    }

    public bool HasClassicModels => _modelFolder is not null;

    /// <summary>The player's own copy of the game, when imported; sounds and textures load from it.</summary>
    public string? GameFolder => _gameFolder;

    public static Color RaceColour(Race race) => race switch
    {
        Race.Red => new Color(0.85f, 0.18f, 0.15f),
        Race.Blue => new Color(0.2f, 0.4f, 0.95f),
        Race.Green => new Color(0.2f, 0.75f, 0.25f),
        _ => new Color(0.95f, 0.85f, 0.2f),
    };

    /// <summary>The planet's original overview image (<c>map.BMP</c>), if the game copy is present.</summary>
    public Texture2D? PlanetMap(string planet)
    {
        if (_gameFolder is null)
            return null;
        var path = Path.Combine(_gameFolder, "models", "planet", planet, "map.BMP");
        return File.Exists(path) ? LoadBmp(path) : null;
    }

    /// <summary>
    /// A menu description (<c>TEXT</c> in sceninfo.dat) from the game copy's <c>pdata</c> folder: the persisted
    /// MachGuiDbTextData, whose first string after a 10-byte header is the text, length first.
    /// </summary>
    public string? MenuText(string? textFile)
    {
        if (_gameFolder is null || textFile is null)
            return null;
        var path = Path.Combine(_gameFolder, "pdata", Path.ChangeExtension(textFile, "bin"));
        if (!File.Exists(path))
            return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 14)
            return null;
        var length = BitConverter.ToInt32(bytes, 10);
        if (length <= 0 || 14 + length > bytes.Length)
            return null;
        var text = System.Text.Encoding.Latin1.GetString(bytes, 14, length);
        // <w> marks a white heading line; lines holding only a no-break space are spacers.
        var lines = text.Replace("<w>", "").Split('\n').Where(l => l.Trim(' ', ' ', '\r').Length > 0);
        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// The mesh for a model id such as <c>AGGRESSOR_1_GRUNT_R_BOLTER</c>; ids without an exact export fall back to
    /// another combo of the same type and level, then to a shape of the given size.
    /// </summary>
    public Mesh MeshFor(string id, Race race, Vector3 fallbackSize)
    {
        var key = $"{race}/{id}";
        if (_meshes.TryGetValue(key, out var cached))
            return cached;

        var mesh = LoadClassic(id, race) ?? Shape(fallbackSize, race);
        _meshes[key] = mesh;
        return mesh;
    }

    private Mesh? LoadClassic(string id, Race race) => ModelPath(id, race) is { } path ? MeshFromJson(path, race) : null;

    private string? ModelPath(string id, Race race)
    {
        if (_modelFolder is null)
            return null;
        var folder = Path.Combine(_modelFolder, race.ToString().ToLowerInvariant());
        var path = Path.Combine(folder, id + ".json");
        if (File.Exists(path))
            return path;
        // e.g. a turret placed with another combo: use any export of the same type and level.
        var prefix = string.Join('_', id.Split('_').Take(3));
        return Directory.Exists(folder) ? Directory.GetFiles(folder, prefix + "*.json").FirstOrDefault() : null;
    }

    /// <summary>
    /// The planet's original terrain tiles (<c>classic/planets/&lt;planet&gt;/terrain.json</c>, see
    /// tools/classic-export), or null when they have not been exported.
    /// </summary>
    public Mesh? PlanetTerrain(string planet)
    {
        var path = _planetFolder is null ? null : Path.Combine(_planetFolder, planet, "terrain.json");
        return path is not null && File.Exists(path) ? MeshFromJson(path) : null;
    }

    /// <summary>The terrain heights the original engine reported for the planet (<c>heights.bin</c>), if exported.</summary>
    public byte[]? PlanetHeights(string planet)
    {
        var path = _planetFolder is null ? null : Path.Combine(_planetFolder, planet, "heights.bin");
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private Mesh? MeshFromJson(string path, Race? race = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return MeshFromParts(document.RootElement.GetProperty("parts"), race);
    }

    /// <summary>
    /// A machine's parts as the original animates them (the <c>rig</c> of its export), or null for buildings,
    /// older exports and the shape fallback.
    /// </summary>
    public Rig? RigFor(string id, Race race)
    {
        var key = $"{race}/{id}";
        if (_rigs.TryGetValue(key, out var cached))
            return cached;
        Rig? rig = null;
        if (ModelPath(id, race) is { } path)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("rig", out var element))
                rig = Rig.Read(element, parts => MeshFromParts(parts, race));
        }
        _rigs[key] = rig;
        return rig;
    }

    /// <summary>
    /// A race's version of a texture (MachPhysRaceChangerBody::texturesVec): textures named in
    /// <c>data/logos.dat</c> come in four variants numbered 1 to 4 for Red, Blue, Green and Yellow, e.g.
    /// <c>pod1_b.bmp</c> and <c>pod2_b.bmp</c>. The exporter writes the Red ones; the swap happens here.
    /// </summary>
    private string RaceTexture(string texture, Race race)
    {
        var file = Path.GetFileName(texture).ToLowerInvariant();
        if (!RaceTextures.Value.TryGetValue(file, out var variant) || _gameFolder is null)
            return texture;
        var swapped = Path.Combine(Path.GetDirectoryName(texture) ?? "", $"{variant.Root}{(int)race + 1}{variant.Extension}").Replace('\\', '/');
        return File.Exists(Path.Combine(_gameFolder, swapped)) ? swapped : texture;
    }

    /// <summary>The race-coloured textures of <c>logos.dat</c>, by their Red (first) name.</summary>
    private static readonly Lazy<Dictionary<string, (string Root, string Extension)>> RaceTextures = new(() =>
    {
        var map = new Dictionary<string, (string, string)>();
        var path = Path.Combine(Paths.OriginalData, "logos.dat");
        if (!File.Exists(path))
            return map;
        foreach (var raw in File.ReadLines(path))
        {
            var name = raw.Split("//")[0].Trim().ToLowerInvariant();
            // As the original reads the list: the root drops the "1" before the extension.
            var extension = name.EndsWith("_b.bmp", StringComparison.Ordinal) ? "_b.bmp"
                : name.EndsWith("_bt.bmp", StringComparison.Ordinal) ? "_bt.bmp"
                : name.EndsWith(".bmp", StringComparison.Ordinal) ? ".bmp" : null;
            if (extension is null || name.Length <= extension.Length + 1)
                continue;
            map[name] = (name[..^(extension.Length + 1)], extension);
        }
        return map;
    });

    private Mesh? MeshFromParts(JsonElement parts, Race? race = null)
    {
        var mesh = new ArrayMesh();
        foreach (var part in parts.EnumerateArray())
        {
            var positions = part.GetProperty("positions").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var uvs = part.GetProperty("uvs").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var count = positions.Length / 3;
            if (count == 0)
                continue;

            // Original axes are z-up; Godot is y-up: (x, y, z) -> (x, z, y). The swap mirrors the model,
            // so each triangle's winding is reversed to keep its front face outward.
            var vertices = new Vector3[count];
            var texCoords = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                vertices[i] = new Vector3(positions[i * 3], positions[i * 3 + 2], positions[i * 3 + 1]);
                texCoords[i] = new Vector2(uvs[i * 2], uvs[i * 2 + 1]);
            }
            var indices = new int[count];
            for (var t = 0; t + 2 < count; t += 3)
                (indices[t], indices[t + 1], indices[t + 2]) = (t, t + 2, t + 1);

            // The original's own vertex normals, as it lit them. Older exports have none (nor has a vertex
            // the original stored none for); those triangles take the normal of their front face.
            var normals = new Vector3[count];
            if (part.TryGetProperty("normals", out var stored) && stored.GetArrayLength() == positions.Length)
            {
                var n = stored.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                for (var i = 0; i < count; i++)
                    normals[i] = new Vector3(n[i * 3], n[i * 3 + 2], n[i * 3 + 1]).Normalized();
            }
            for (var t = 0; t + 2 < count; t += 3)
            {
                // Godot's front faces wind clockwise. A two-sided material lights a triangle's back with its
                // normal turned round, so a triangle wound against its normals (some original tiles are)
                // would face away from the light: it is wound the other way.
                var front = (vertices[t + 1] - vertices[t]).Cross(vertices[t + 2] - vertices[t]);
                var sum = normals[t] + normals[t + 1] + normals[t + 2];
                if (sum.LengthSquared() < 0.01f)
                    normals[t] = normals[t + 1] = normals[t + 2] = front.Normalized();
                else if (front.Dot(sum) < 0)
                    (indices[t + 1], indices[t + 2]) = (t + 1, t + 2);
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = texCoords;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var diffuse = part.GetProperty("diffuse").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, MaterialFor(part.GetProperty("texture").GetString() ?? "", diffuse, race));
        }
        return mesh.GetSurfaceCount() > 0 ? mesh : null;
    }

    private Material MaterialFor(string texture, float[] diffuse, Race? race = null)
    {
        if (race is { } r)
            texture = RaceTexture(texture, r);
        var key = $"{texture}|{string.Join(',', diffuse)}";
        if (_materials.TryGetValue(key, out var cached))
            return cached;

        // As the original renderer did, the texture is tinted by the material's colour: the race colour of a
        // machine sits in the diffuse colour of its grey textures.
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(diffuse[0], diffuse[1], diffuse[2]),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            // The original's winding convention is not confirmed yet; draw both sides.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        if (texture.Length > 0 && _gameFolder is not null)
        {
            var path = Path.Combine(_gameFolder, texture);
            if (File.Exists(path) && LoadBmp(path) is { } image)
                material.AlbedoTexture = image;
        }
        _materials[key] = material;
        return material;
    }

    private static Texture2D? LoadBmp(string path)
    {
        var image = DecodeBmp(File.ReadAllBytes(path));
        if (image is null)
            return null;
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Decodes the game's uncompressed 8-, 24- and 32-bit BMPs. Godot's own loader rejects some of them
    /// (their headers disagree with their size); this reads what is there.
    /// </summary>
    public static Image? DecodeBmp(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
            return null;
        var pixelOffset = BitConverter.ToInt32(data, 10);
        var headerSize = BitConverter.ToInt32(data, 14);
        var width = BitConverter.ToInt32(data, 18);
        var rawHeight = BitConverter.ToInt32(data, 22);
        var bpp = BitConverter.ToUInt16(data, 28);
        var compression = BitConverter.ToInt32(data, 30);
        var colours = BitConverter.ToInt32(data, 46);
        if (compression != 0 || width <= 0 || rawHeight == 0 || bpp is not (4 or 8 or 24 or 32))
            return null;

        var bottomUp = rawHeight > 0;
        var height = Math.Abs(rawHeight);
        var paletteStart = 14 + headerSize;
        if (colours <= 0 || colours > 256)
            colours = 256;
        var stride = (width * bpp + 31) / 32 * 4;
        var rgba = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var row = pixelOffset + (bottomUp ? height - 1 - y : y) * stride;
            for (var x = 0; x < width; x++)
            {
                byte r = 0, g = 0, b = 0;
                switch (bpp)
                {
                    case 4:
                    case 8:
                        var index = bpp == 8
                            ? (row + x < data.Length ? data[row + x] : 0)
                            : (row + x / 2 < data.Length ? (x % 2 == 0 ? data[row + x / 2] >> 4 : data[row + x / 2] & 0xF) : 0);
                        var entry = paletteStart + Math.Min(index, colours - 1) * 4;
                        if (entry + 2 < data.Length)
                            (b, g, r) = (data[entry], data[entry + 1], data[entry + 2]);
                        break;
                    default:
                        var p = row + x * (bpp / 8);
                        if (p + 2 < data.Length)
                            (b, g, r) = (data[p], data[p + 1], data[p + 2]);
                        break;
                }
                var o = (y * width + x) * 4;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (r, g, b, 255);
            }
        }
        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
    }

    private Mesh Shape(Vector3 size, Race race)
    {
        var box = new BoxMesh { Size = size };
        box.Material = new StandardMaterial3D { AlbedoColor = RaceColour(race) };
        return box;
    }
}
