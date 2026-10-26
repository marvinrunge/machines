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

    /// <summary>A weapon effect from the original (a projectile or an impact splat, <c>classic/models/fx</c>), or null.</summary>
    public Mesh? EffectMesh(string name)
    {
        var key = "fx/" + name;
        if (_meshes.TryGetValue(key, out var cached))
            return cached;
        var path = _modelFolder is null ? null : Path.Combine(_modelFolder, "fx", name + ".json");
        var mesh = path is not null && File.Exists(path) ? MeshFromJson(path) : null;
        if (mesh is not null)
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

    /// <summary>
    /// A satellite's picture in the planet's sky (<c>classic/planets/&lt;planet&gt;/sky/&lt;name&gt;.json</c>): its
    /// texture and its half size, a square seen from the original's 80 km orbit radius. Null when not exported.
    /// </summary>
    public SkySprite? PlanetSky(string planet, string satellite)
    {
        var path = _planetFolder is null ? null : Path.Combine(_planetFolder, planet, "sky", satellite + ".json");
        if (path is null || !File.Exists(path))
            return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.GetProperty("parts").EnumerateArray().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } part
            || LoadTexture(part.GetProperty("texture").GetString() ?? "").Texture is not { } texture)
            return null;
        var extent = part.GetProperty("positions").EnumerateArray().Select(e => Mathf.Abs(e.GetSingle())).DefaultIfEmpty(0).Max();
        return new SkySprite(texture, extent);
    }

    public sealed record SkySprite(Texture2D Texture, float HalfSize);

    /// <summary>
    /// The planet's sky dome (<c>classic/planets/&lt;planet&gt;/sky/sky.json</c>): its mesh, its texture and its
    /// radius, or null when the planet has none exported.
    /// </summary>
    public (Mesh Mesh, Texture2D Texture, float Radius)? PlanetSkyDome(string planet)
    {
        var path = _planetFolder is null ? null : Path.Combine(_planetFolder, planet, "sky", "sky.json");
        if (path is null || !File.Exists(path))
            return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.GetProperty("parts").EnumerateArray().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } part
            || LoadTexture(part.GetProperty("texture").GetString() ?? "").Texture is not { } texture
            || MeshFromParts(document.RootElement) is not { } mesh)
            return null;
        var radius = part.GetProperty("positions").EnumerateArray().Select(e => Mathf.Abs(e.GetSingle())).DefaultIfEmpty(1).Max();
        return (mesh, texture, radius);
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
        return MeshFromParts(document.RootElement, race);
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
                rig = Rig.Read(element, node => MeshFromParts(node, race));
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

    /// <summary>
    /// The original's turn-to-face polygons (RenTTFRectangle): each a rectangle around a centre that always faces
    /// the camera — the glowing eyes and lamps of machines, flares, leaves. Every corner sits at the centre and
    /// carries its offset across the screen in UV2; the sprite shader spreads it out in view space, as the
    /// original's compose2DGeometry did. Glowing sprites add their light; the others are lit and see-through
    /// where their texture is magenta.
    /// </summary>
    private void AddSprites(ArrayMesh mesh, JsonElement sprites, Race? race)
    {
        // Corners in the original's order (a triangle fan): right-down, right-up, left-up, left-down.
        Vector2[] corners = [new(1, -1), new(1, 1), new(-1, 1), new(-1, -1)];
        foreach (var group in sprites.EnumerateArray().GroupBy(s => s.GetProperty("texture").GetString() + "|"
                     + s.GetProperty("diffuse").GetRawText() + "|" + s.GetProperty("emissive").GetRawText()))
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var offsets = new List<Vector2>();
            var indices = new List<int>();
            foreach (var sprite in group)
            {
                var c = sprite.GetProperty("centre").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                var half = new Vector2(sprite.GetProperty("width").GetSingle(), sprite.GetProperty("height").GetSingle()) / 2;
                var spriteUvs = sprite.GetProperty("uvs").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                var first = vertices.Count;
                for (var i = 0; i < 4; i++)
                {
                    vertices.Add(new Vector3(c[0], c[2], c[1]));
                    uvs.Add(spriteUvs.Length >= 8 ? new Vector2(spriteUvs[i * 2], spriteUvs[i * 2 + 1]) : (corners[i] * new Vector2(1, -1) + Vector2.One) / 2);
                    offsets.Add(corners[i] * half);
                }
                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV2] = offsets.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var sample = group.First();
            var diffuse = sample.GetProperty("diffuse").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var emissive = sample.GetProperty("emissive").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var glowing = emissive.Max() > 0.03f;
            var material = new ShaderMaterial { Shader = glowing ? GlowSpriteShader.Value : LitSpriteShader.Value };
            var texture = sample.GetProperty("texture").GetString() ?? "";
            if (race is { } r)
                texture = RaceTexture(texture, r);
            if (LoadTexture(texture).Texture is { } image)
                material.SetShaderParameter("sprite", image);
            material.SetShaderParameter("colour", glowing ? new Vector3(emissive[0], emissive[1], emissive[2]) : new Vector3(diffuse[0], diffuse[1], diffuse[2]));
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
    }

    /// <summary>
    /// Spin-to-face polygons (RenSpinTFPolygon: gun barrels, pipes): each vertex sits on its polygon's axis base,
    /// carries the axis as its normal and its corner (along, across the axis) in UV2; the shader turns the shape
    /// about the axis to face the camera, as the original did every frame.
    /// </summary>
    private void AddSpinners(ArrayMesh mesh, JsonElement spinners, Race? race)
    {
        foreach (var group in spinners.EnumerateArray().GroupBy(s => s.GetProperty("texture").GetString() + "|"
                     + s.GetProperty("diffuse").GetRawText() + "|" + s.GetProperty("emissive").GetRawText()))
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var corners = new List<Vector2>();
            var indices = new List<int>();
            foreach (var spinner in group)
            {
                var b = spinner.GetProperty("base").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                var a = spinner.GetProperty("axis").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                var c = spinner.GetProperty("corners").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                var t = spinner.GetProperty("uvs").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                var first = vertices.Count;
                var n = c.Length / 2;
                for (var i = 0; i < n; i++)
                {
                    // Original z up; the swap to Godot's y up mirrors, which flips the across direction too.
                    vertices.Add(new Vector3(b[0], b[2], b[1]));
                    normals.Add(new Vector3(a[0], a[2], a[1]));
                    corners.Add(new Vector2(c[i * 2], -c[i * 2 + 1]));
                    uvs.Add(t.Length >= n * 2 ? new Vector2(t[i * 2], t[i * 2 + 1]) : Vector2.Zero);
                }
                for (var i = 1; i + 1 < n; i++)
                    indices.AddRange([first, first + i, first + i + 1]);
            }
            if (indices.Count == 0)
                continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV2] = corners.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var sample = group.First();
            var diffuse = sample.GetProperty("diffuse").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var emissive = sample.GetProperty("emissive").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var material = new ShaderMaterial { Shader = SpinnerShader.Value };
            var texture = sample.GetProperty("texture").GetString() ?? "";
            if (race is { } r)
                texture = RaceTexture(texture, r);
            if (LoadTexture(texture).Texture is { } image)
                material.SetShaderParameter("sprite", image);
            material.SetShaderParameter("colour", new Vector3(diffuse[0], diffuse[1], diffuse[2]));
            material.SetShaderParameter("glow", new Vector3(emissive[0], emissive[1], emissive[2]));
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
    }

    /// <summary>A spin-to-face polygon: lit, see-through where its texture is, turned about its axis to the camera.</summary>
    private static readonly Lazy<Shader> SpinnerShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode cull_disabled;
            uniform sampler2D sprite : source_color, filter_linear_mipmap;
            uniform vec3 colour = vec3(1.0);
            uniform vec3 glow = vec3(0.0);
            void vertex() {
                // In view space: along the axis, and across it square to the line of sight (RenSpinTFPolygon::spinPlan).
                vec3 base = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
                vec3 along = (MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
                float scale = length(along);
                along = normalize(along);
                vec3 across = cross(-base, along);
                across = length(across) > 1e-5 ? normalize(across) : normalize(cross(along, vec3(0.0, 0.0, 1.0)));
                vec3 position = base + (along * UV2.x + across * UV2.y) * scale;
                POSITION = PROJECTION_MATRIX * vec4(position, 1.0);
                NORMAL = normalize(cross(along, across));
            }
            void fragment() {
                vec4 c = texture(sprite, UV);
                ALBEDO = c.rgb * colour;
                EMISSION = c.rgb * glow;
                ALPHA = c.a;
                ALPHA_SCISSOR_THRESHOLD = 0.5;
            }
            """,
    });

    private const string SpriteVertex = """
        void vertex() {
            // Every corner starts at the sprite's centre and moves by its offset across the screen.
            vec4 centre = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
            centre.xy += UV2;
            POSITION = PROJECTION_MATRIX * centre;
            NORMAL = normalize((inverse(MODELVIEW_MATRIX) * vec4(0.0, 0.0, 1.0, 0.0)).xyz);
        }
        """;

    /// <summary>A sprite that gives light: added on top, its black background vanishing.</summary>
    private static readonly Lazy<Shader> GlowSpriteShader = new(() => new Shader
    {
        Code = $$"""
            shader_type spatial;
            render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
            uniform sampler2D sprite : source_color, filter_linear_mipmap;
            uniform vec3 colour = vec3(1.0);
            {{SpriteVertex}}
            void fragment() {
                vec4 c = texture(sprite, UV);
                ALBEDO = c.rgb * c.a * colour * 2.0;
            }
            """,
    });

    /// <summary>A lit sprite (leaves and the like), see-through where its texture is magenta.</summary>
    private static readonly Lazy<Shader> LitSpriteShader = new(() => new Shader
    {
        Code = $$"""
            shader_type spatial;
            render_mode cull_disabled;
            uniform sampler2D sprite : source_color, filter_linear_mipmap;
            uniform vec3 colour = vec3(1.0);
            {{SpriteVertex}}
            void fragment() {
                vec4 c = texture(sprite, UV);
                ALBEDO = c.rgb * colour;
                ALPHA = c.a;
                ALPHA_SCISSOR_THRESHOLD = 0.5;
            }
            """,
    });

    /// <summary>A model's mesh from its export: the triangles of its <c>parts</c> and the <c>sprites</c> that face the camera.</summary>
    /// <summary>How far a model's plates on the ground are lifted above it, metres.</summary>
    private const float GroundPlateLift = 0.12f;

    private Mesh? MeshFromParts(JsonElement model, Race? race = null)
    {
        var mesh = new ArrayMesh();
        foreach (var part in model.GetProperty("parts").EnumerateArray())
        {
            var positions = part.GetProperty("positions").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var uvs = part.GetProperty("uvs").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var count = positions.Length / 3;
            if (count == 0)
                continue;
            var diffuse = part.GetProperty("diffuse").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            if (IsPaintedShadow(part, diffuse, positions))
                continue;

            // Original axes are z-up; Godot is y-up: (x, y, z) -> (x, z, y). The swap mirrors the model,
            // so each triangle's winding is reversed to keep its front face outward.
            // Flat plates lying on the ground (the pads before a building's doors) sit a little above it, or the
            // ground would show through them where the two meet.
            var lift = race is not null && Enumerable.Range(0, count).All(i => Mathf.Abs(positions[i * 3 + 2]) < 0.02f) ? GroundPlateLift : 0;
            var vertices = new Vector3[count];
            var texCoords = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                vertices[i] = new Vector3(positions[i * 3], positions[i * 3 + 2] + lift, positions[i * 3 + 1]);
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

            // Vertices with a material of their own (terrain tiles shade their corners that way, see
            // RenIIlluminator::applyVertexMaterials) bring their colour; it tints the texture instead of the part's.
            var vertexColours = part.TryGetProperty("colours", out var colourList) && colourList.GetArrayLength() == positions.Length;
            if (vertexColours)
            {
                var c = colourList.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                arrays[(int)Mesh.ArrayType.Color] = Enumerable.Range(0, count).Select(i => new Color(c[i * 3], c[i * 3 + 1], c[i * 3 + 2])).ToArray();
                diffuse = [1, 1, 1, diffuse.Length > 3 ? diffuse[3] : 1];
            }
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            var emissive = part.TryGetProperty("emissive", out var glow) ? glow.EnumerateArray().Select(e => e.GetSingle()).ToArray() : [0f, 0f, 0f];
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, MaterialFor(part.GetProperty("texture").GetString() ?? "", diffuse, race, emissive, vertexColours));
        }
        if (model.TryGetProperty("sprites", out var sprites))
            AddSprites(mesh, sprites, race);
        if (model.TryGetProperty("spinners", out var spinners))
            AddSpinners(mesh, spinners, race);
        return mesh.GetSurfaceCount() > 0 ? mesh : null;
    }

    /// <summary>
    /// The flat, half-clear black plate the original laid under flying machines as their shadow. Real shadows
    /// replace it, so it is left out.
    /// </summary>
    private static bool IsPaintedShadow(JsonElement part, float[] diffuse, float[] positions)
    {
        if (part.GetProperty("texture").GetString() is { Length: > 0 } || diffuse.Length < 4 || diffuse[3] > 0.99f || diffuse[0] + diffuse[1] + diffuse[2] > 0.03f)
            return false;
        float low = float.MaxValue, high = float.MinValue;
        for (var i = 2; i < positions.Length; i += 3)
            (low, high) = (Mathf.Min(low, positions[i]), Mathf.Max(high, positions[i]));
        return high - low < 0.1f;
    }

    private Material MaterialFor(string texture, float[] diffuse, Race? race = null, float[]? emissive = null, bool vertexColours = false)
    {
        if (race is { } r)
            texture = RaceTexture(texture, r);
        emissive ??= [0, 0, 0];
        var key = $"{texture}|{string.Join(',', diffuse)}|{string.Join(',', emissive)}|{race is not null}|{vertexColours}";
        if (_materials.TryGetValue(key, out var cached))
            return cached;

        // As the original renderer did, the texture is tinted by the material's colour: the race colour of a
        // machine sits in the diffuse colour of its grey textures.
        var remake = GraphicsSettings.Remake;
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(diffuse[0], diffuse[1], diffuse[2]),
            // The remake filters textures seen at a slant (the ground, mostly) anisotropically: far sharper.
            TextureFilter = remake ? BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic : BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            // The original's winding convention is not confirmed yet; draw both sides.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // Vertex colours are material colours, given as the albedo colour is.
            VertexColorUseAsAlbedo = vertexColours,
            VertexColorIsSrgb = vertexColours,
        };
        // Machines and buildings get a little sheen; the ground stays matt.
        if (remake && race is not null)
        {
            material.Roughness = 0.55f;
            material.Metallic = 0.2f;
            material.MetallicSpecular = 0.6f;
        }
        var (loaded, clearness) = LoadTexture(texture);
        material.AlbedoTexture = loaded;
        // Which original texture it shows (lower case, no folder): material plans find their parts by it.
        material.SetMeta("texture", Path.GetFileName(texture).ToLowerInvariant());
        // Glowing parts (the green of plasma weapons, lamps): the original added the emissive colour to the
        // lighting before the texture tinted it.
        if (emissive.Max() > 0.01f)
        {
            material.EmissionEnabled = true;
            material.Emission = new Color(emissive[0], emissive[1], emissive[2]);
            material.EmissionTexture = loaded;
            // Tinted by the texture, not added to it: the eyes (black, glowing white) show their white circles on
            // black instead of a white box.
            material.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
            material.EmissionEnergyMultiplier = GraphicsSettings.Remake ? 1.6f : 1;
        }
        // See-through textures: cut out where keyed, blended where they have an alpha map.
        if (clearness == Clearness.Keyed)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.5f;
        }
        else if (clearness == Clearness.Soft)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }
        // A see-through colour: the flat shadow that flying machines drag over the ground is half-clear black.
        if (diffuse.Length > 3 && diffuse[3] < 0.99f)
        {
            material.AlbedoColor = material.AlbedoColor with { A = diffuse[3] };
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        }

        // The planets' water tiles (watr*, and the falling and flowing watfal / watflw) move in the remake.
        var name = Path.GetFileName(texture).ToLowerInvariant();
        if (remake && race is null && material.AlbedoTexture is { } water && (name.StartsWith("watr") || name.StartsWith("watfal") || name.StartsWith("watflw")))
        {
            var moving = new ShaderMaterial { Shader = WaterShader.Value };
            moving.SetShaderParameter("albedo_tex", water);
            moving.SetShaderParameter("tint", new Vector3(diffuse[0], diffuse[1], diffuse[2]));
            moving.SetShaderParameter("flow", name.StartsWith("watr") ? Vector2.Zero : new Vector2(0, 0.35f));
            _materials[key] = moving;
            return moving;
        }
        _materials[key] = material;
        return material;
    }

    /// <summary>
    /// Water: the original texture sampled twice, drifting and rippling against each other, on a smooth surface
    /// that catches the sun and mirrors its surroundings (screen-space reflections).
    /// </summary>
    private static readonly Lazy<Shader> WaterShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode cull_disabled;
            uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform vec3 tint = vec3(1.0);
            uniform vec2 flow = vec2(0.0);
            varying vec3 world;
            void vertex() {
                world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            }
            void fragment() {
                float t = TIME;
                // Waves in metres, so they look the same however the tiles repeat their texture.
                vec2 p = world.xz;
                vec2 drift = vec2(sin(p.y * 0.35 + t * 0.9), cos(p.x * 0.3 + t * 0.8)) * 0.006;
                vec3 water = texture(albedo_tex, UV + flow * t + drift).rgb;
                ALBEDO = water * tint * 0.9;
                ROUGHNESS = 0.05;
                SPECULAR = 0.7;
                float r1 = sin(p.x * 0.9 + p.y * 0.4 + t * 1.6) + sin(p.x * 0.31 - p.y * 0.77 - t * 1.1);
                float r2 = sin(p.y * 0.85 - p.x * 0.35 + t * 1.4) + sin(p.y * 0.27 + p.x * 0.61 + t * 0.9);
                NORMAL_MAP = vec3(0.5 + r1 * 0.04, 0.5 + r2 * 0.04, 1.0);
            }
            """,
    });

    private static Texture2D? LoadBmp(string path)
    {
        var image = DecodeBmp(File.ReadAllBytes(path));
        if (image is null)
            return null;
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>How a texture lets things through: not at all, cut out by a key colour, or softly by an alpha map.</summary>
    private enum Clearness { Opaque, Keyed, Soft }

    /// <summary>
    /// A model texture as the original loaded it (RenITexBody::read). Names ending in <c>_t</c> or <c>_bt</c> are
    /// see-through: where a colour map (<c>_c</c> / <c>_bc</c>) and an alpha map (<c>_a</c> / <c>_ba</c>) lie beside
    /// them, the colour comes from the one and the transparency from the other's brightness; otherwise magenta is
    /// cut out.
    /// </summary>
    private (Texture2D? Texture, Clearness Clearness) LoadTexture(string relative)
    {
        if (_gameFolder is null || relative.Length == 0)
            return (null, Clearness.Opaque);
        var path = Path.Combine(_gameFolder, relative);
        var name = Path.GetFileName(relative).ToLowerInvariant();
        var transparent = name.EndsWith("_t.bmp") || name.EndsWith("_bt.bmp");
        if (!transparent)
            return (File.Exists(path) ? LoadBmp(path) : null, Clearness.Opaque);

        string Sibling(char letter) => path[..^5] + letter + path[^4..];
        var colourPath = Sibling('c');
        var alphaPath = Sibling('a');
        if (File.Exists(colourPath) && File.Exists(alphaPath)
            && DecodeBmp(File.ReadAllBytes(colourPath)) is { } colour && DecodeBmp(File.ReadAllBytes(alphaPath)) is { } alpha
            && colour.GetSize() == alpha.GetSize())
        {
            for (var y = 0; y < colour.GetHeight(); y++)
                for (var x = 0; x < colour.GetWidth(); x++)
                {
                    var c = colour.GetPixel(x, y);
                    colour.SetPixel(x, y, new Color(c.R, c.G, c.B, alpha.GetPixel(x, y).R));
                }
            colour.GenerateMipmaps();
            return (ImageTexture.CreateFromImage(colour), Clearness.Soft);
        }
        if (!File.Exists(path) || DecodeBmp(File.ReadAllBytes(path)) is not { } keyed)
            return (null, Clearness.Opaque);
        ClassicGui.KeyOut(keyed, black: false);
        keyed.GenerateMipmaps();
        return (ImageTexture.CreateFromImage(keyed), Clearness.Keyed);
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
