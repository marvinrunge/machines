using Godot;

namespace Machines.Game;

/// <summary>The looks the player can choose at once (see <see cref="GraphicsSettings.Look"/>), each setting every part.</summary>
public enum Look
{
    /// <summary>The original as it was: its models, ground, lighting and effects.</summary>
    Classic,
    /// <summary>
    /// The original sharpened: its models with rounded edges and baked occlusion, its pictures of the ground upscaled,
    /// HD textures, and the remake's lighting, sky, effects, weather, trees and rocks.
    /// </summary>
    Overhauled,
    /// <summary>
    /// The remade models (rebuilt in Blender or the Meshy remakes, in the original rigs) and the painted ground, with
    /// the rest of the remake; whatever has no remade model yet (buildings) shows overhauled.
    /// </summary>
    Hd,
    /// <summary>The parts chosen one by one, as no look has them.</summary>
    Custom,
}

/// <summary>How the ground looks (see <see cref="TerrainBuilder"/>).</summary>
public enum TerrainLook
{
    /// <summary>The original's tiles and pictures.</summary>
    Original,
    /// <summary>The original's tiles, their pictures upscaled (<see cref="HdTextures"/>) with relief and roughness.</summary>
    Sharpened,
    /// <summary>A finer shape, the natural planets painted with the remake's materials (<see cref="TerrainMaterials"/>).</summary>
    Painted,
}

/// <summary>How the machines and buildings look (see <see cref="ClassicAssets"/>).</summary>
public enum ModelLook
{
    /// <summary>The original's models and textures.</summary>
    Original,
    /// <summary>The original's models with rounded edges, baked occlusion, HD textures, bare metal and a little sheen.</summary>
    Overhauled,
    /// <summary>The machines rebuilt in Blender or the Meshy remakes, in the original rigs; overhauled where there is none.</summary>
    Hd,
}

/// <summary>
/// The six parts of the look, each chosen on its own: the ground, the machines and buildings, the lighting and sky
/// (tone mapping, glow, ambient occlusion, soft shadows, the photographed skies, lamps at night), the effects
/// (explosions, machines flying apart, smoke, tracks, scorch marks, lights of fire and plasma), the weather, and the
/// trees and rocks (the remade scenery, grass and stones on the painted ground).
/// </summary>
public sealed record GraphicsParts(TerrainLook Terrain, ModelLook Models, bool Lighting, bool Effects, bool Weather, bool Scenery)
{
    public static readonly GraphicsParts Classic = new(TerrainLook.Original, ModelLook.Original, false, false, false, false);
    public static readonly GraphicsParts Overhauled = new(TerrainLook.Sharpened, ModelLook.Overhauled, true, true, true, true);
    public static readonly GraphicsParts Hd = new(TerrainLook.Painted, ModelLook.Hd, true, true, true, true);

    /// <summary>The parts a look sets; the custom one keeps <paramref name="custom"/>.</summary>
    public static GraphicsParts Of(Look look, GraphicsParts custom) => look switch
    {
        Look.Classic => Classic,
        Look.Overhauled => Overhauled,
        Look.Hd => Hd,
        _ => custom,
    };

    /// <summary>The look these parts make, or the custom one when no look has them all.</summary>
    public Look Look => this == Classic ? Look.Classic : this == Overhauled ? Look.Overhauled : this == Hd ? Look.Hd : Look.Custom;
}

/// <summary>
/// The player's choice of look, kept in <c>user://settings.cfg</c>: a whole look (<see cref="Look"/>) or each part on
/// its own (<see cref="GraphicsParts"/>). MACHINES_LOOK sets it for one run (screenshots, checks), unsaved: a look,
/// then parts to change, such as <c>Overhauled,terrain=painted,lighting=off</c> or <c>Classic,models=hd</c>.
/// </summary>
public static class GraphicsSettings
{
    private const string File = "user://settings.cfg";

    /// <summary>The section that keeps the parts last chosen one by one, so the custom look comes back as it was.</summary>
    private const string CustomSection = "graphics_custom";

    /// <summary>The custom look before any part was chosen: the original's ground under the overhauled models.</summary>
    private static readonly GraphicsParts FirstCustom = GraphicsParts.Overhauled with { Terrain = TerrainLook.Original };

    private static GraphicsParts? _parts;

    /// <summary>Every part as chosen; parts that make no look are kept as the custom one too.</summary>
    public static GraphicsParts Parts
    {
        get => _parts ??= Asked(System.Environment.GetEnvironmentVariable("MACHINES_LOOK"), Saved());
        set
        {
            _parts = value;
            var config = new ConfigFile();
            config.Load(File);
            config.SetValue("graphics", "look", value.Look.ToString());
            Write(config, "graphics", value);
            // What builds from before the parts read: whether anything of the remake is on.
            config.SetValue("graphics", "remake", value != GraphicsParts.Classic);
            if (value.Look == Look.Custom)
                Write(config, CustomSection, value);
            config.Save(File);
        }
    }

    /// <summary>The look the parts make; setting one sets every part as it has them, the custom one as last chosen.</summary>
    public static Look Look
    {
        get => Parts.Look;
        set => Parts = GraphicsParts.Of(value, Custom);
    }

    /// <summary>The parts last chosen one by one.</summary>
    private static GraphicsParts Custom
    {
        get
        {
            var config = new ConfigFile();
            return config.Load(File) == Error.Ok ? Read(config, CustomSection, FirstCustom) : FirstCustom;
        }
    }

    public static TerrainLook Terrain => Parts.Terrain;
    public static ModelLook Models => Parts.Models;
    public static bool Lighting => Parts.Lighting;
    public static bool Effects => Parts.Effects;
    public static bool Weather => Parts.Weather;
    public static bool Scenery => Parts.Scenery;

    private static GraphicsParts Saved()
    {
        var config = new ConfigFile();
        return config.Load(File) == Error.Ok ? Read(config) : GraphicsParts.Overhauled;
    }

    /// <summary>
    /// The parts kept in settings: each as saved, those not saved as the saved look has them. Settings from before the
    /// parts kept only the look, and those from before the three looks only whether the remake was on.
    /// </summary>
    public static GraphicsParts Read(ConfigFile config)
    {
        var look = Enum.TryParse<Look>((string)config.GetValue("graphics", "look", ""), out var kept) ? kept
            : (bool)config.GetValue("graphics", "remake", true) ? Look.Overhauled : Look.Classic;
        var custom = Read(config, CustomSection, FirstCustom);
        return Read(config, "graphics", GraphicsParts.Of(look, custom));
    }

    /// <summary>The parts kept in <paramref name="section"/>, those not kept as <paramref name="from"/> has them.</summary>
    private static GraphicsParts Read(ConfigFile config, string section, GraphicsParts from)
    {
        T Part<T>(string key, T fallback) where T : struct, Enum =>
            Enum.TryParse<T>((string)config.GetValue(section, key, ""), out var value) ? value : fallback;
        bool On(string key, bool fallback) => (bool)config.GetValue(section, key, fallback);
        return new GraphicsParts(Part("terrain", from.Terrain), Part("models", from.Models),
            On("lighting", from.Lighting), On("effects", from.Effects), On("weather", from.Weather), On("scenery", from.Scenery));
    }

    private static void Write(ConfigFile config, string section, GraphicsParts parts)
    {
        config.SetValue(section, "terrain", parts.Terrain.ToString());
        config.SetValue(section, "models", parts.Models.ToString());
        config.SetValue(section, "lighting", parts.Lighting);
        config.SetValue(section, "effects", parts.Effects);
        config.SetValue(section, "weather", parts.Weather);
        config.SetValue(section, "scenery", parts.Scenery);
    }

    /// <summary>The parts MACHINES_LOOK asks for: its look, or <paramref name="saved"/>, with the parts it names changed.</summary>
    private static GraphicsParts Asked(string? asked, GraphicsParts saved)
    {
        if (string.IsNullOrWhiteSpace(asked))
            return saved;
        var words = asked.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parts = words.Length > 0 && Enum.TryParse<Look>(words[0], true, out var look) ? GraphicsParts.Of(look, saved) : saved;
        foreach (var word in words.Where(w => w.Contains('=')))
        {
            var (key, value) = (word[..word.IndexOf('=')].ToLowerInvariant(), word[(word.IndexOf('=') + 1)..]);
            var on = value.ToLowerInvariant() is "on" or "true" or "remake" or "1";
            parts = key switch
            {
                "terrain" when Enum.TryParse<TerrainLook>(value, true, out var terrain) => parts with { Terrain = terrain },
                "models" when Enum.TryParse<ModelLook>(value, true, out var models) => parts with { Models = models },
                "lighting" => parts with { Lighting = on },
                "effects" => parts with { Effects = on },
                "weather" => parts with { Weather = on },
                "scenery" => parts with { Scenery = on },
                _ => parts,
            };
        }
        return parts;
    }

    /// <summary>How the look is called in the menus.</summary>
    public static string Name(Look look) => look switch
    {
        Look.Classic => "Classic",
        Look.Overhauled => "Overhauled classic",
        Look.Hd => "HD",
        _ => "Custom",
    };
}
