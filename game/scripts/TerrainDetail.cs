using Godot;

namespace Machines.Game;

/// <summary>
/// The remake's ground detail: each terrain tile keeps the original's picture as its colour, and a ground texture
/// from Poly Haven (<c>textures/polyhaven</c>, fetched by tools/terrain-textures/fetch.sh) adds the grain, pebbles
/// and cracks of sand, soil, grass, rock, mud, snow or metal close up, with their relief and roughness. The detail
/// only brightens and darkens the original's colours, so the planets keep their look. Steep faces take rock
/// whatever the tile, projected from the side so it does not stretch; far away the detail fades out. Which layer a
/// tile gets is read from its texture's name, else from its colour. Without the textures the ground stays as it is.
/// </summary>
public static class TerrainDetail
{
    /// <summary>The layers, in the order of the texture arrays, and how many metres one repeat of each covers.</summary>
    private static readonly (string Name, float Size)[] Layers =
    [
        ("sand", 3), ("soil", 3), ("grass", 2.5f), ("rock", 4), ("cliff", 6), ("mud", 4), ("snow", 4), ("metal", 4),
        ("gravel", 2.5f), ("laterite", 3),
    ];

    private static int Layer(string name) => Array.FindIndex(Layers, l => l.Name == name);

    private static string Folder => Path.Combine(Paths.Root, "textures", "polyhaven");

    private sealed record Arrays(Texture2DArray Albedo, Texture2DArray Normal, Texture2DArray Roughness, float[] Brightness);

    private static Arrays? _arrays;
    private static bool _tried;

    /// <summary>The detail textures, loaded once; null when they have not been fetched.</summary>
    private static Arrays? Load()
    {
        if (_tried)
            return _arrays;
        _tried = true;
        // The thirty maps load and compress side by side.
        var maps = new Image?[Layers.Length, 3];
        var brightness = new float[Layers.Length];
        Image.CompressSource[] sources = [Image.CompressSource.Srgb, Image.CompressSource.Normal, Image.CompressSource.Generic];
        string[] files = ["albedo.jpg", "normal.jpg", "roughness.jpg"];
        Parallel.For(0, Layers.Length * 3, k =>
        {
            var (i, m) = (k / 3, k % 3);
            maps[i, m] = Read(Path.Combine(Folder, Layers[i].Name, files[m]), sources[m], out var mean);
            if (m == 0)
                brightness[i] = Mathf.Max(mean, 0.05f);
        });
        var albedo = new Godot.Collections.Array<Image>();
        var normal = new Godot.Collections.Array<Image>();
        var roughness = new Godot.Collections.Array<Image>();
        for (var i = 0; i < Layers.Length; i++)
        {
            if (maps[i, 0] is not { } a || maps[i, 1] is not { } n || maps[i, 2] is not { } r)
                return null;
            albedo.Add(a);
            normal.Add(n);
            roughness.Add(r);
        }
        var albedoArray = new Texture2DArray();
        var normalArray = new Texture2DArray();
        var roughnessArray = new Texture2DArray();
        if (albedoArray.CreateFromImages(albedo) != Error.Ok || normalArray.CreateFromImages(normal) != Error.Ok
            || roughnessArray.CreateFromImages(roughness) != Error.Ok)
            return null;
        return _arrays = new Arrays(albedoArray, normalArray, roughnessArray, brightness);
    }

    /// <summary>A map at 1024 pixels with mipmaps, compressed when the build can; <paramref name="mean"/> is its average brightness.</summary>
    private static Image? Read(string path, Image.CompressSource source, out float mean)
    {
        mean = 0.5f;
        if (!File.Exists(path) || Image.LoadFromFile(path) is not { } image || image.IsEmpty())
            return null;
        image.Convert(Image.Format.Rgb8);
        if (image.GetWidth() != 1024 || image.GetHeight() != 1024)
            image.Resize(1024, 1024, Image.Interpolation.Cubic);
        var small = (Image)image.Duplicate();
        small.Resize(8, 8, Image.Interpolation.Bilinear);
        var sum = 0f;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                sum += small.GetPixel(x, y).Luminance;
        mean = sum / 64;
        image.GenerateMipmaps();
        image.Compress(Image.CompressMode.S3Tc, source);
        return image;
    }

    /// <summary>
    /// Gives the terrain's tiles the detail shader in place of their materials, keeping their pictures, vertex
    /// colours and tints. See-through tiles and water keep theirs.
    /// Returns each detailed surface's layer and average colour, for what grows on it (GroundScatter).
    /// </summary>
    public static IReadOnlyDictionary<int, (string Layer, Color Colour, string Texture)> Apply(Mesh terrain)
    {
        var surfaces = new Dictionary<int, (string, Color, string)>();
        if (Load() is not { } arrays)
            return surfaces;
        var materials = new Dictionary<Material, Material>();
        var classified = new Dictionary<Material, (string, Color, string)>();
        for (var s = 0; s < terrain.GetSurfaceCount(); s++)
        {
            if (terrain.SurfaceGetMaterial(s) is not StandardMaterial3D original
                || original.Transparency != BaseMaterial3D.TransparencyEnum.Disabled || original.AlbedoTexture is not { } picture)
                continue;
            if (!materials.TryGetValue(original, out var detailed))
            {
                var layer = Classify(original.GetMeta("texture", "").AsString(), picture);
                var shader = new ShaderMaterial { Shader = Shader.Value };
                shader.SetShaderParameter("albedo_tex", picture);
                shader.SetShaderParameter("tint", new Vector3(original.AlbedoColor.R, original.AlbedoColor.G, original.AlbedoColor.B));
                shader.SetShaderParameter("vertex_colours", original.VertexColorUseAsAlbedo);
                shader.SetShaderParameter("detail_albedo", arrays.Albedo);
                shader.SetShaderParameter("detail_normal", arrays.Normal);
                shader.SetShaderParameter("detail_rough", arrays.Roughness);
                shader.SetShaderParameter("layer", (float)layer);
                shader.SetShaderParameter("layer_size", Layers[layer].Size);
                shader.SetShaderParameter("layer_brightness", arrays.Brightness[layer]);
                // Metal floors keep their plates on their walls; everything else turns to rock where it is steep.
                var cliff = Layers[layer].Name == "metal" ? layer : Layer("cliff");
                shader.SetShaderParameter("cliff_layer", (float)cliff);
                shader.SetShaderParameter("cliff_size", Layers[cliff].Size);
                shader.SetShaderParameter("cliff_brightness", arrays.Brightness[cliff]);
                materials[original] = detailed = shader;
                classified[original] = (Layers[layer].Name, Average(picture) * original.AlbedoColor, original.GetMeta("texture", "").AsString());
            }
            terrain.SurfaceSetMaterial(s, detailed);
            surfaces[s] = classified[original];
        }
        return surfaces;
    }

    private static readonly Dictionary<string, Texture2D?> Albedos = [];

    /// <summary>One layer's colour map on its own (the stones of GroundScatter wear the rock), or null without it.</summary>
    public static Texture2D? LayerAlbedo(string layer)
    {
        if (Albedos.TryGetValue(layer, out var cached))
            return cached;
        Texture2D? texture = null;
        if (Read(Path.Combine(Folder, layer, "albedo.jpg"), Image.CompressSource.Srgb, out _) is { } image)
            texture = ImageTexture.CreateFromImage(image);
        return Albedos[layer] = texture;
    }

    /// <summary>
    /// Which layer suits a tile: by the name of its picture first (the original's names say what they show: rok,
    /// sand, mud, snow, spflr ...), else by its average colour (green is grass, grey rock, pale snow, yellow sand,
    /// red laterite, the rest soil).
    /// </summary>
    private static int Classify(string texture, Texture2D picture)
    {
        var name = Path.GetFileNameWithoutExtension(texture).ToLowerInvariant();
        bool Has(params string[] parts) => parts.Any(name.Contains);
        if (Has("spflr", "spwal", "spgfl", "met", "floor", "duct", "beam", "walk", "strip", "pipe", "grate", "plate", "tower"))
            return Layer("metal");
        if (Has("snow", "ice", "frost"))
            return Layer("snow");
        if (Has("rok", "rock", "cliff", "ridge", "volro", "volc", "crag", "boulder"))
            return Layer("rock");
        if (Has("mud"))
            return Layer("mud");
        if (Has("grass", "grs"))
            return Layer("grass");
        if (Has("sand", "snd", "dune"))
            return Layer("sand");

        var colour = Average(picture);
        colour.ToHsv(out var hue, out var saturation, out var value);
        hue *= 360;
        if (saturation < 0.12f && value > 0.7f)
            return Layer("snow");
        if (saturation < 0.15f)
            return Layer("rock");
        if (hue is > 65 and < 170 && saturation > 0.15f)
            return Layer("grass");
        if (hue is > 20 and < 55 && value > 0.45f)
            return Layer("sand");
        if ((hue < 18 || hue > 340) && saturation > 0.45f)
            return Layer("laterite");
        return Layer("soil");
    }

    private static Color Average(Texture2D picture)
    {
        var image = picture.GetImage();
        if (image is null)
            return Colors.Gray;
        image = (Image)image.Duplicate();
        if (image.IsCompressed())
            image.Decompress();
        image.ClearMipmaps();
        image.Resize(4, 4, Image.Interpolation.Bilinear);
        float r = 0, g = 0, b = 0;
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var c = image.GetPixel(x, y);
                (r, g, b) = (r + c.R, g + c.G, b + c.B);
            }
        return new Color(r / 16, g / 16, b / 16);
    }

    /// <summary>
    /// The tile's picture times its vertex colours and tint, brightened and darkened by the detail (divided by the
    /// detail's own average, so the colours stay the original's); the detail's relief bends the normal in world space
    /// and its roughness replaces the matt default. The detail is sampled twice, at its size and a turned 3.7 times
    /// larger copy, so its repeats do not show; steep faces sample the cliff layer from the side (triplanar).
    /// </summary>
    private static readonly Lazy<Shader> Shader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode cull_disabled;
            uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
            uniform vec3 tint = vec3(1.0);
            uniform bool vertex_colours = false;
            uniform sampler2DArray detail_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform sampler2DArray detail_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform sampler2DArray detail_rough : filter_linear_mipmap_anisotropic, repeat_enable;
            uniform float layer = 0.0;
            uniform float layer_size = 3.0;
            uniform float layer_brightness = 0.5;
            uniform float cliff_layer = 4.0;
            uniform float cliff_size = 6.0;
            uniform float cliff_brightness = 0.5;
            uniform float strength = 0.75;
            uniform float relief = 0.9;
            varying vec3 world_pos;
            varying vec3 world_normal;

            void vertex() {
                world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
            }

            // Two samples at different scales and angles, so the repeats break up.
            vec4 two_scales(sampler2DArray tex, vec2 p, float size, float index) {
                vec2 a = p / size;
                vec2 b = mat2(vec2(0.8, 0.6), vec2(-0.6, 0.8)) * p / (size * 3.7);
                return mix(texture(tex, vec3(a, index)), texture(tex, vec3(b, index)), 0.4);
            }

            void fragment() {
                vec4 picture = texture(albedo_tex, UV);
                vec3 base = picture.rgb * tint * (vertex_colours ? COLOR.rgb : vec3(1.0));

                float dist = length(world_pos - CAMERA_POSITION_WORLD);
                float near = 1.0 - smoothstep(160.0, 420.0, dist);
                vec3 n = normalize(world_normal) * (FRONT_FACING ? 1.0 : -1.0);
                float steep = smoothstep(0.45, 0.75, 1.0 - abs(n.y));

                // Flat ground: the tile's layer from above.
                vec3 flat_colour = two_scales(detail_albedo, world_pos.xz, layer_size, layer).rgb;
                vec3 flat_bump = two_scales(detail_normal, world_pos.xz, layer_size, layer).xyz * 2.0 - 1.0;
                float flat_rough = two_scales(detail_rough, world_pos.xz, layer_size, layer).r;
                float flat_light = dot(flat_colour, vec3(0.299, 0.587, 0.114)) / layer_brightness;

                // Steep faces: the cliff layer from the two sides, by how much the face looks each way.
                vec2 w = pow(abs(n.xz), vec2(4.0));
                w /= max(w.x + w.y, 1e-4);
                vec3 side_x = texture(detail_albedo, vec3(world_pos.zy / cliff_size, cliff_layer)).rgb;
                vec3 side_z = texture(detail_albedo, vec3(world_pos.xy / cliff_size, cliff_layer)).rgb;
                vec3 cliff_colour = side_x * w.x + side_z * w.y;
                float cliff_light = dot(cliff_colour, vec3(0.299, 0.587, 0.114)) / cliff_brightness;
                float cliff_rough = texture(detail_rough, vec3(world_pos.zy / cliff_size, cliff_layer)).r * w.x
                    + texture(detail_rough, vec3(world_pos.xy / cliff_size, cliff_layer)).r * w.y;

                float light = mix(flat_light, cliff_light, steep);
                ALBEDO = base * mix(1.0, light, strength * near);

                // The relief bends the normal in world space: the maps' x runs east, their y (OpenGL, up the
                // picture) north, against the picture's rows.
                vec3 bent = normalize(n + vec3(flat_bump.x, 0.0, -flat_bump.y) * relief * near * (1.0 - steep));
                NORMAL = normalize((VIEW_MATRIX * vec4(bent, 0.0)).xyz);
                ROUGHNESS = mix(0.92, mix(flat_rough, cliff_rough, steep), near);
                SPECULAR = 0.35;
            }
            """,
    });
}
