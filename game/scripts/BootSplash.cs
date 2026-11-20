using Godot;

namespace Machines.Game;

/// <summary>
/// The splash Godot shows while the game starts, before it draws a frame of its own: the first frame of the loading
/// screen (<see cref="StartupFilms.FirstFrame"/>), the original's loading picture with its bar at the start, so the one
/// follows the other without a seam. The picture comes from the player's own copy of the game and the project cannot
/// carry it, so the game writes it to its user folder along with the settings that show it, which Godot reads at the
/// next start (application/config/project_settings_override in project.godot). Without a copy that shows the loading
/// screen the splash stays black, as the films' background is. The window opens as the player chose it, full screen
/// or not, so it does not jump from the splash's window to the game's (<see cref="OptionsPage.ApplyWindow"/>).
/// project.godot holds the rest, the same in both: black behind, the picture scaled whole into the middle of the
/// window as <see cref="StartupFilms"/> fits its screen, filtered as the menus' textures are.
/// </summary>
public static class BootSplash
{
    private static string Settings => ProjectSettings.GlobalizePath("user://boot.cfg");
    private static string Picture => ProjectSettings.GlobalizePath("user://boot_splash.png");

    private static string? _gameFolder;
    private static bool _known;

    /// <summary>Writes the settings for the next start, for the game copy the menus found (null without one).</summary>
    public static void Update(string? gameFolder)
    {
        (_gameFolder, _known) = (gameFolder, true);
        Write();
    }

    /// <summary>Writes them again, as the window's choice changed; nothing before the menus have said where the game copy is.</summary>
    public static void Write()
    {
        if (!_known)
            return;
        var shown = StartupFilms.LoadingPicture(_gameFolder) is { } picture && Made(picture);
        var config = new ConfigFile();
        config.SetValue("application", "boot_splash/show_image", shown);
        if (shown)
            config.SetValue("application", "boot_splash/image", Picture.Replace('\\', '/'));
        // Only for the exported game (the "template" feature): runs from Godot itself, the automated ones among them
        // (screenshots, films, bots), keep opening in a window of the project's size and go full screen as before.
        config.SetValue("display", "window/size/mode.template", (int)(OptionsPage.FullScreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed));
        var text = config.EncodeToText();
        if (!File.Exists(Settings) || File.ReadAllText(Settings) != text)
            File.WriteAllText(Settings, text);
    }

    /// <summary>The splash picture, made once: it is the same in every copy of the game.</summary>
    private static bool Made(string picture)
    {
        if (File.Exists(Picture))
            return true;
        using var frame = StartupFilms.FirstFrame(picture);
        return frame is not null && frame.SavePng(Picture) == Error.Ok;
    }
}
