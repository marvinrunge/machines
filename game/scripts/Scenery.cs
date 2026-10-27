using Godot;

namespace Machines.Game;

/// <summary>
/// The remake's trees, rocks and rock spires, built in Blender from code and from Poly Haven scans (CC0) by
/// tools/blender/build-nature.sh into models/nature, and read from there as glTF. They stand in for the original's
/// natural artefacts as the player chose on the trees and rocks page, a model picked by chance for each one, scaled
/// to the original's height (rocks to its footprint): drawn only, as the originals are. Trees and bushes sway in the
/// weather's wind; the scans, enlarged many times, wear a finer rock picture over their own. The light versions of
/// the scans lie scattered on the ground (<see cref="GroundScatter"/>), with bushes and dead trees between.
/// Without the models (not built) everything stays as the original had it.
/// </summary>
public static class Scenery
{
    private enum Fit { Height, Footprint }

    /// <summary>The models standing in for each natural artefact of the original (by its name), and how they are fitted to it.</summary>
    private static readonly Dictionary<string, (string[] Models, Fit Fit)> Swaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["palm01"] = (["palm_a", "palm_b", "palm_c"], Fit.Height),
        ["palm02"] = (["palm_a", "palm_b", "palm_c"], Fit.Height),
        ["palm03"] = (["palm_shrub"], Fit.Height),
        ["hrock1"] = (["rock_moss_set_01_3", "rock_moss_set_01_4", "rock_moss_set_01_1", "rock_moss_set_01_6", "boulder_01", "namaqualand_boulder_02"], Fit.Footprint),
        ["baob"] = (["baobab_leafy", "baobab_bare", "dead_tree"], Fit.Height),
        ["sfir"] = (["fir_snow"], Fit.Height),
        ["snow"] = (["fir_slim_snow", "fir_snow"], Fit.Height),
        ["rok4"] = (["moon_rock_01", "moon_rock_03", "moon_rock_05"], Fit.Footprint),
        ["spike"] = (["spire_stub", "spire_cluster"], Fit.Height),
        ["spike2"] = (["spire_horn", "spire_cluster", "spire_stub"], Fit.Height),
    };

    /// <summary>Models that sway in the wind (trees and bushes), and how far their tops move per metre of height.</summary>
    private static readonly HashSet<string> Plants = ["palm_a", "palm_b", "palm_c", "palm_shrub", "fir", "fir_snow", "fir_slim_snow",
        "baobab_leafy", "baobab_bare", "dead_tree", "bush_green", "bush_dry"];
    private const float SwayPerMetre = 0.02f;

    /// <summary>The stones scattered on each kind of planet (the light scans), as chosen on the page.</summary>
    private static readonly (string[] Planets, string[] Stones)[] StoneKinds =
    [
        (["m_green", "mgreendn", "m_dales", "mdalesdn", "finger", "hilly", "talonth8", "fortmdn", "fortmult", "fortsdn", "fortseig", "seige", "5o1"],
            ["rock_moss_set_01_1", "rock_moss_set_01_2", "rock_moss_set_01_3", "rock_moss_set_01_4", "rock_moss_set_01_5", "rock_moss_set_01_6"]),
        (["m_desert", "daynight", "desspy", "dogbar", "multidog", "commsat", "gracko", "mgrack"],
            ["namaqualand_boulder_05_1", "namaqualand_boulder_05_2", "namaqualand_boulder_02", "stone_01"]),
        (["m_moon", "mmoony", "1o1", "1o2", "1o3", "seekndes", "ice", "icedn", "micey", "miceydn", "resfac"],
            ["moon_rock_01", "moon_rock_03", "moon_rock_05"]),
    ];

    private static string Folder => Path.Combine(Paths.Root, "models", "nature");

    /// <summary>A loaded model: its mesh (with levels of detail) and its size in its own metres.</summary>
    public sealed record Model(string Name, Mesh Mesh, Aabb Bounds);

    private static readonly Dictionary<string, Model?> Loaded = [];

    /// <summary>The model by its name (small: the light version for scattering), or null when it was not built.</summary>
    public static Model? Load(string name, bool small = false)
    {
        var key = small ? "small/" + name : name;
        if (Loaded.TryGetValue(key, out var cached))
            return cached;
        var path = Path.Combine(Folder, key + ".glb");
        Model? model = null;
        if (File.Exists(path))
        {
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) == Error.Ok && state.GetMeshes() is { Count: > 0 } meshes && meshes[0].Mesh is { } importer)
            {
                // The importer's levels of detail, as for an imported scene: far rocks draw a few hundred triangles.
                // Not for plants: their cards are already only a few triangles each, and simplifying tore them apart.
                if (!Plants.Contains(name))
                    importer.GenerateLods(25, 60, []);
                var mesh = importer.GetMesh();
                // Plants sway by their height; the big scans, only ever drawn enlarged many times, get the finer rock
                // picture; the light ones lie on the ground and take some of its colour.
                var height = mesh.GetAabb().Size.Y;
                var (sway, detail, tint) = Plants.Contains(name) ? (height, 0f, 0f) : name.StartsWith("spire") ? (0f, 0f, 0f) : small ? (0f, 0f, 0.35f) : (0f, 0.6f, 0f);
                for (var s = 0; s < mesh.GetSurfaceCount(); s++)
                    mesh.SurfaceSetMaterial(s, Restyle(mesh.SurfaceGetMaterial(s), sway, detail, tint));
                model = new Model(name, mesh, mesh.GetAabb());
            }
        }
        Loaded[key] = model;
        return model;
    }

    /// <summary>
    /// The scenery in place of the original's natural artefacts: a node of models for the placements it knows, and the
    /// placements left to the original (everything that is not a tree or a rock, or all when the models are missing).
    /// </summary>
    public static (Node3D Node, List<T> Left) Place<T>(IEnumerable<T> placements, Func<T, (string Name, Vector3 At, float Orientation, Mesh? Original)> read)
    {
        var root = new Node3D { Name = "Scenery" };
        var left = new List<T>();
        foreach (var placement in placements)
        {
            var (name, at, orientation, original) = read(placement);
            if (!Swaps.TryGetValue(name, out var swap) || original is null)
            {
                left.Add(placement);
                continue;
            }
            // The same model for the same place every time and for every player: by the place, not by the order (and
            // not by HashCode, which differs from run to run).
            var dice = new Random(Mathf.RoundToInt(at.X * 10) * 73856093 ^ Mathf.RoundToInt(at.Z * 10) * 19349663);
            if (Load(swap.Models[dice.Next(swap.Models.Length)]) is not { } model)
            {
                left.Add(placement);
                continue;
            }
            var (was, size) = (original.GetAabb().Size, model.Bounds.Size);
            var scale = swap.Fit == Fit.Height
                ? was.Y / Mathf.Max(size.Y, 0.01f)
                : Mathf.Sqrt(was.X * was.Z / Mathf.Max(size.X * size.Z, 0.0001f));
            scale *= 0.9f + 0.2f * (float)dice.NextDouble();
            // Rocks sink a little, so they sit in a slope rather than on it.
            var sink = swap.Fit == Fit.Footprint ? size.Y * scale * 0.12f : 0;
            root.AddChild(new MeshInstance3D
            {
                Name = name,
                Mesh = model.Mesh,
                Transform = new Transform3D(new Basis(Vector3.Up, -Mathf.DegToRad(orientation)).Scaled(Vector3.One * scale), at - Vector3.Up * sink),
            });
        }
        return (root, left);
    }

    /// <summary>The light scans scattered as stones on a planet, each with what brings it to a unit across; empty when none.</summary>
    public static (Mesh Mesh, float Unit)[] Stones(string planet)
    {
        var kind = Array.Find(StoneKinds, k => k.Planets.Contains(planet, StringComparer.OrdinalIgnoreCase));
        return kind.Stones is null ? [] : kind.Stones.Select(n => Load(n, small: true)).OfType<Model>()
            .Select(m => (m.Mesh, 1 / Mathf.Max(Mathf.Max(m.Bounds.Size.X, m.Bounds.Size.Z), 0.01f))).ToArray();
    }

    /// <summary>
    /// What grows between the grass and stones (the player chose a middling amount): bushes per square metre of grass
    /// on the green planets; dry bushes and dead trees per square metre of ground in the desert.
    /// </summary>
    public static (Model Model, float PerGrass, float PerGround)[] Decoration(string planet)
    {
        var list = new List<(Model, float, float)>();
        var green = StoneKinds[0].Planets.Contains(planet, StringComparer.OrdinalIgnoreCase);
        var desert = planet.ToLowerInvariant() is "m_desert" or "daynight" or "desspy";
        if (green && Load("bush_green") is { } bush)
            list.Add((bush, 0.008f, 0));
        if (desert && Load("bush_dry") is { } dry)
            list.Add((dry, 0, 0.00035f));
        if (desert && Load("dead_tree") is { } dead)
            list.Add((dead, 0, 0.00003f));
        return [.. list];
    }

    /// <summary>
    /// A glTF material as the scenery shader draws it: its pictures and values carried over, and the model's height in
    /// its own metres when it sways (0 when not), the share of the finer rock picture and of the ground's colour.
    /// </summary>
    private static Material Restyle(Material? from, float sway, float detail, float tint)
    {
        if (from is not BaseMaterial3D source)
            return from ?? new StandardMaterial3D();
        // Foliage cards are cut out where their picture is clear (glTF's alpha mask).
        var foliage = source.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor;
        var twoSided = source.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
        var shader = new ShaderMaterial
        {
            Shader = foliage ? Foliage.Value : twoSided ? TwoSided.Value : OneSided.Value,
            ResourceName = source.ResourceName,
        };
        if (source.AlbedoTexture is { } picture)
            shader.SetShaderParameter("albedo_tex", Mipmapped(picture));
        shader.SetShaderParameter("albedo_colour", source.AlbedoColor);
        if (source.NormalEnabled && source.NormalTexture is { } normal)
        {
            shader.SetShaderParameter("normal_tex", Mipmapped(normal));
            shader.SetShaderParameter("use_normal", true);
        }
        if (source.RoughnessTexture is { } rough)
        {
            shader.SetShaderParameter("rough_tex", Mipmapped(rough));
            shader.SetShaderParameter("use_rough", true);
        }
        shader.SetShaderParameter("roughness", source.Roughness);
        if (detail > 0 && TerrainDetail.LayerAlbedo("rock") is { } rock)
        {
            shader.SetShaderParameter("detail_tex", rock);
            shader.SetShaderParameter("detail", detail);
        }
        shader.SetShaderParameter("model_height", sway);
        shader.SetShaderParameter("ground_tint", tint);
        return shader;
    }

    private static readonly Dictionary<Texture2D, Texture2D> WithMipmaps = [];

    /// <summary>
    /// A picture read from glTF at runtime, with mipmaps: without them bark and rock shimmer far off and the leaves'
    /// edges sparkle.
    /// </summary>
    private static Texture2D Mipmapped(Texture2D texture)
    {
        if (WithMipmaps.TryGetValue(texture, out var done))
            return done;
        var result = texture;
        if (texture.GetImage() is { } image && !image.HasMipmaps())
        {
            if (image.IsCompressed())
                image.Decompress();
            image.GenerateMipmaps();
            result = ImageTexture.CreateFromImage(image);
        }
        WithMipmaps[texture] = result;
        return result;
    }

    /// <summary>
    /// A lava pool the scenarios set (the red moons' "lava"), in its own picture: flowing and glowing as the lava on
    /// the ground does.
    /// </summary>
    public static Material Lava(Mesh pool)
    {
        var material = new ShaderMaterial { Shader = LavaShader.Value };
        if ((pool.SurfaceGetMaterial(0) as BaseMaterial3D)?.AlbedoTexture is { } picture)
            material.SetShaderParameter("pool_picture", picture);
        material.SetShaderParameter("lava_noise", LavaNoise.Value);
        return material;
    }

    private static readonly Lazy<Texture2D> LavaNoise = new(() => ImageTexture.CreateFromImage(TerrainMaterials.WarpNoise()));

    private static readonly Lazy<Shader> LavaShader = new(() => new Shader
    {
        Code = $$"""
            shader_type spatial;
            uniform sampler2D pool_picture : source_color, hint_default_black, filter_linear_mipmap, repeat_enable;
            uniform sampler2D lava_noise : filter_linear, repeat_enable;
            varying vec3 world_position;
            {{TerrainMaterials.LavaGlsl}}
            void vertex() {
                world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            }
            void fragment() {
                vec3 glow;
                ALBEDO = lava_glow(texture(pool_picture, UV + lava_flow(lava_noise, world_position, TIME)).rgb, lava_noise, world_position, TIME, glow);
                EMISSION = glow;
                ROUGHNESS = 0.8;
            }
            """,
    });

    private static readonly Lazy<Shader> OneSided = new(() => new Shader { Code = Code("cull_back", foliage: false) });
    private static readonly Lazy<Shader> TwoSided = new(() => new Shader { Code = Code("cull_disabled", foliage: false) });
    private static readonly Lazy<Shader> Foliage = new(() => new Shader { Code = Code("cull_disabled", foliage: true) });

    /// <summary>
    /// The scenery's shader: the glTF pictures; for plants, the top swaying in the weather's wind (the trunk's foot
    /// stays), each at its own pace by where it stands; for enlarged scans, the ground's rock picture laid over in
    /// world metres from three sides; for scattered stones, a share of the ground's colour (the instance colour).
    /// </summary>
    private static string Code(string cull, bool foliage) => $$"""
        shader_type spatial;
        render_mode {{cull}};
        uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform vec4 albedo_colour : source_color = vec4(1.0);
        uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform bool use_normal = false;
        uniform sampler2D rough_tex : hint_default_white, filter_linear_mipmap, repeat_enable;
        uniform bool use_rough = false;
        uniform float roughness = 0.85;
        uniform sampler2D detail_tex : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
        // A plant's height in its own metres (0: it stands still); how much of the rock picture lies over a scan; how
        // much of the ground's colour (the instance colour) a scattered stone takes.
        uniform float model_height = 0.0;
        uniform float detail = 0.0;
        uniform float ground_tint = 0.0;
        global uniform vec2 weather_wind;
        varying vec3 world_position;
        varying vec3 world_normal;

        void vertex() {
            if (model_height > 0.0) {
                vec3 origin = MODEL_MATRIX[3].xyz;
                // As tall as it is drawn; the top moves so much per metre of that.
                float tall = model_height * length(MODEL_MATRIX[1].xyz);
                float sway = tall * {{SwayPerMetre.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                // The share of the height over the foot, squared, so the trunk bends rather than tilts.
                float up = clamp(((MODEL_MATRIX * vec4(VERTEX, 1.0)).y - origin.y) / tall, 0.0, 1.0);
                float strength = length(weather_wind);
                vec2 along = weather_wind / max(strength, 0.001);
                float phase = origin.x * 0.13 + origin.z * 0.11;
                float swing = sin(TIME * (0.8 + strength * 0.04) + phase) + 0.35 * sin(TIME * 2.1 + phase * 1.7 + VERTEX.x * 0.5);
                vec2 bend = (along * min(strength, 12.0) * 0.06 + vec2(0.8, 0.6) * swing * (0.35 + strength * 0.05)) * sway * up * up;
                VERTEX += inverse(mat3(MODEL_MATRIX)) * vec3(bend.x, 0.0, bend.y);
            }
            world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            world_normal = normalize(mat3(MODEL_MATRIX) * NORMAL);
        }

        void fragment() {
            vec4 picture = texture(albedo_tex, UV);
            vec3 colour = picture.rgb * albedo_colour.rgb;
            if (detail > 0.0) {
                // The rock picture from three sides, three metres to a repeat, against its own average (its smallest mipmap).
                vec3 w = pow(abs(world_normal), vec3(4.0));
                w /= w.x + w.y + w.z;
                vec3 p = world_position / 3.0;
                vec3 rock = texture(detail_tex, p.zy).rgb * w.x + texture(detail_tex, p.xz).rgb * w.y + texture(detail_tex, p.xy).rgb * w.z;
                vec3 average = textureLod(detail_tex, vec2(0.5), 12.0).rgb;
                float light = dot(rock, vec3(0.333)) / max(dot(average, vec3(0.333)), 0.02);
                colour *= mix(1.0, clamp(light, 0.3, 2.0), detail);
            }
            if (ground_tint > 0.0)
                colour *= mix(vec3(1.0), COLOR.rgb * 2.2, ground_tint);
            ALBEDO = colour;
            if (use_normal)
                NORMAL_MAP = texture(normal_tex, UV).rgb;
            ROUGHNESS = use_rough ? texture(rough_tex, UV).g : roughness;
            {{(foliage ? FoliageFragment : "")}}
        }
        """;

    /// <summary>
    /// What a foliage card adds: cut out where its picture is clear (the cut lowered as the picture's mipmaps blur its
    /// edges far off, so the crowns do not thin out with distance), its normals out of the crown on both sides alike
    /// (both faces of a card belong to the same crown), and sunlight shining through its leaves.
    /// </summary>
    private const string FoliageFragment = """
            ALPHA = picture.a;
            ALPHA_SCISSOR_THRESHOLD = clamp(0.5 - textureQueryLod(albedo_tex, UV).x * 0.07, 0.2, 0.5);
            ALPHA_ANTIALIASING_EDGE = 0.3;
            ALPHA_TEXTURE_COORDINATE = UV * vec2(textureSize(albedo_tex, 0));
            if (!FRONT_FACING)
                NORMAL = -NORMAL;
            BACKLIGHT = colour * 0.45;
            SPECULAR = 0.25;
        """;
}
