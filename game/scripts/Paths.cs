using Godot;
using Machines.Sim;

namespace Machines.Game;

/// <summary>
/// Where the game finds its files, and the data loaded once for every match. In the editor that is the
/// repository; an exported build carries the same layout next to its executable: <c>data/original</c>,
/// <c>tools/classic-export</c> and, once imported, <c>classic</c>.
/// </summary>
public static class Paths
{
    private static GameData? _data;

    public static string Root => OS.HasFeature("editor")
        ? System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."))
        : System.IO.Path.GetDirectoryName(OS.GetExecutablePath())!;

    public static string OriginalData => System.IO.Path.Combine(Root, "data", "original");

    /// <summary>Models, terrain and the game folder path imported from the player's own copy (never shipped).</summary>
    public static string Classic => System.IO.Path.Combine(Root, "classic");

    public static GameData Data => _data ??= GameData.Load(OriginalData);
}
