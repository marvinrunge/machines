using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Machines.Game;

/// <summary>
/// Finds the copies of Machines: Wired for War on this computer, so that the first start can offer one instead of a
/// folder to look for: Steam's libraries, GOG's games, the installers' entries in Windows' list of programs, and the
/// usual folders on each drive. A copy is a folder with <c>models.bin</c> and <c>pdata</c>, what
/// <see cref="ClassicImport"/> imports from. Nothing here needs Godot, so the tests run it on folders of their own.
/// </summary>
public static partial class GameLocator
{
    /// <summary>The copies found, Steam's and GOG's first, each once.</summary>
    public static IReadOnlyList<string> Find()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var drives = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady).Select(drive => drive.RootDirectory.FullName);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Copies(SteamRoots(), RegistryFolders(), drives, [Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), Path.Combine(home, "Documents")]);
    }

    /// <summary>
    /// The copies in Steam's libraries (each Steam folder and the libraries its <c>libraryfolders.vdf</c> names), in
    /// the folders the registry names, under the drives' usual game folders and in the player's own folders.
    /// </summary>
    public static IReadOnlyList<string> Copies(IEnumerable<string> steam, IEnumerable<string> registry, IEnumerable<string> drives, IEnumerable<string> own)
    {
        var found = new List<string>();
        void Add(IEnumerable<string> copies) => found.AddRange(copies.Where(copy => !found.Contains(copy, StringComparer.OrdinalIgnoreCase)));

        foreach (var root in steam)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            var libraries = File.Exists(vdf) ? SteamLibraries(Read(vdf)).Prepend(root) : [root];
            foreach (var library in libraries)
                Add(Within(Path.Combine(library, "steamapps", "common")));
        }
        foreach (var folder in registry)
            Add(Holds(folder) ? [Normal(folder)] : Within(folder));
        foreach (var drive in drives)
            foreach (var games in new[] { "GOG Games", "Games", @"Program Files (x86)\GOG Galaxy\Games", "Program Files (x86)", "Program Files", "" })
                Add(Within(Path.Combine(drive, games)));
        foreach (var folder in own)
            Add(Within(folder));
        return found;
    }

    /// <summary>The folder holds the game: its <c>models.bin</c> and its planets' <c>pdata</c>.</summary>
    public static bool Holds(string folder) =>
        File.Exists(Path.Combine(folder, "models.bin")) && Directory.Exists(Path.Combine(folder, "pdata"));

    /// <summary>
    /// The copies in the folders right under <paramref name="folder"/>, and one level further in those named after the
    /// game (Steam's and GOG's folders may keep it in a folder of its own).
    /// </summary>
    public static IEnumerable<string> Within(string folder)
    {
        foreach (var child in Children(folder))
        {
            if (Holds(child))
                yield return Normal(child);
            else if (Path.GetFileName(child).Contains("machines", StringComparison.OrdinalIgnoreCase))
                foreach (var inner in Children(child).Where(Holds))
                    yield return Normal(inner);
        }
    }

    /// <summary>
    /// The libraries in Steam's <c>libraryfolders.vdf</c>: each <c>"path"</c> in today's file, and in the older one
    /// the numbered entries that name a folder (the numbered entries of today's file count apps and their sizes). An
    /// entry starts its line, so a value that is a number is not taken for one.
    /// </summary>
    public static IEnumerable<string> SteamLibraries(string vdf) =>
        VdfEntry().Matches(vdf)
            .Select(match => match.Groups[1].Value.Replace(@"\\", @"\"))
            .Where(path => path.Length > 2 && (path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal)));

    [GeneratedRegex("""
        ^\s*"(?:path|\d+)"[ \t]+"((?:[^"\\]|\\.)*)"
        """, RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VdfEntry();

    /// <summary>Steam's folders: where the registry says it is, else where it installs.</summary>
    private static IEnumerable<string> SteamRoots()
    {
        if (!OperatingSystem.IsWindows())
            yield break;
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
            if (Value(hive, key, value) is { } folder)
                yield return folder.Replace('/', '\\');
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    }

    /// <summary>
    /// The folders GOG's games are in, and those of the installed programs named after the game (GOG's and the
    /// original CD's installers enter themselves there).
    /// </summary>
    private static IEnumerable<string> RegistryFolders()
    {
        if (!OperatingSystem.IsWindows())
            yield break;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
                foreach (var game in SubKeys(hive, key))
                    if (Value(hive, $@"{key}\{game}", "path") is { } folder)
                        yield return folder;
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                foreach (var program in SubKeys(hive, key))
                    if (Value(hive, $@"{key}\{program}", "DisplayName") is { } name && name.Contains("Machines", StringComparison.OrdinalIgnoreCase)
                        && Value(hive, $@"{key}\{program}", "InstallLocation") is { } folder)
                        yield return folder;
        }
    }

    private static string? Value(RegistryKey hive, string key, string name)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var open = hive.OpenSubKey(key);
            return open?.GetValue(name) is string { Length: > 0 } value ? value.Trim('"') : null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] SubKeys(RegistryKey hive, string key)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        try
        {
            using var open = hive.OpenSubKey(key);
            return open?.GetSubKeyNames() ?? [];
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The folders in <paramref name="folder"/>, none where it is missing or closed.</summary>
    private static string[] Children(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Read(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Normal(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/');
}
