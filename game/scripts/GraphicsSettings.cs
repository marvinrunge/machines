using Godot;

namespace Machines.Game;

/// <summary>
/// The player's choice between the remake's graphics (tone mapping, glow, ambient occlusion, soft shadows,
/// sharper textures, slightly glossy machines) and the plain classic look; kept in <c>user://settings.cfg</c>.
/// </summary>
public static class GraphicsSettings
{
    private const string File = "user://settings.cfg";

    private static bool? _remake;

    public static bool Remake
    {
        get
        {
            if (_remake is null)
            {
                var config = new ConfigFile();
                _remake = config.Load(File) != Error.Ok || (bool)config.GetValue("graphics", "remake", true);
            }
            return _remake.Value;
        }
        set
        {
            _remake = value;
            var config = new ConfigFile();
            config.Load(File);
            config.SetValue("graphics", "remake", value);
            config.Save(File);
        }
    }
}
