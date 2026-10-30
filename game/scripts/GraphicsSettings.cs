using Godot;

namespace Machines.Game;

/// <summary>The three looks the player can choose between (see <see cref="GraphicsSettings.Look"/>).</summary>
public enum Look
{
    /// <summary>The original as it was: its models, textures and plain lighting.</summary>
    Classic,
    /// <summary>The original's models with rounded edges and baked occlusion, HD textures and the remake's lighting.</summary>
    Overhauled,
    /// <summary>
    /// The remade models (the Meshy remakes in the original rigs) on top of the overhauled look; whatever has no
    /// remade model yet (buildings, the terrain) shows overhauled.
    /// </summary>
    Hd,
}

/// <summary>
/// The player's choice of look (<see cref="Look"/>), kept in <c>user://settings.cfg</c>. Everything but the classic
/// look counts as the remake (<see cref="Remake"/>: tone mapping, glow, ambient occlusion, soft shadows, sharper
/// textures, slightly glossy machines).
/// </summary>
public static class GraphicsSettings
{
    private const string File = "user://settings.cfg";

    private static Look? _look;

    public static Look Look
    {
        get
        {
            if (_look is null)
            {
                var config = new ConfigFile();
                // MACHINES_LOOK=Classic, Overhauled or Hd sets the look for one run (screenshots, checks), unsaved.
                if (System.Enum.TryParse<Look>(System.Environment.GetEnvironmentVariable("MACHINES_LOOK"), true, out var asked))
                    _look = asked;
                else if (config.Load(File) != Error.Ok)
                    _look = Look.Overhauled;
                // Settings from before the choice of three kept only whether the remake was on.
                else if (System.Enum.TryParse<Look>((string)config.GetValue("graphics", "look", ""), out var kept))
                    _look = kept;
                else
                    _look = (bool)config.GetValue("graphics", "remake", true) ? Look.Overhauled : Look.Classic;
            }
            return _look.Value;
        }
        set
        {
            _look = value;
            var config = new ConfigFile();
            config.Load(File);
            config.SetValue("graphics", "look", value.ToString());
            config.SetValue("graphics", "remake", value != Look.Classic);
            config.Save(File);
        }
    }

    /// <summary>The remake's lighting and textures: every look but the classic one.</summary>
    public static bool Remake => Look != Look.Classic;

    /// <summary>How the look is called in the menus.</summary>
    public static string Name(Look look) => look switch
    {
        Look.Classic => "Classic",
        Look.Overhauled => "Overhauled classic",
        _ => "HD",
    };
}
