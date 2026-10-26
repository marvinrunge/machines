using Godot;
using Machines.Sim;

namespace Machines.Game;

/// <summary>Where the repository and its original data are, and the data loaded once for every match.</summary>
public static class Paths
{
    private static GameData? _data;

    public static string Repository => System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));

    public static string OriginalData => System.IO.Path.Combine(Repository, "data", "original");

    public static GameData Data => _data ??= GameData.Load(OriginalData);
}
