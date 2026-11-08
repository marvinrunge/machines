using Machines.Game;

namespace Machines.Tests.Import;

public class GameLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"locator-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A folder with the game's models.bin and pdata, as Steam and GOG install it.</summary>
    private string Copy(params string[] parts)
    {
        var folder = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.Combine(folder, "pdata"));
        File.WriteAllText(Path.Combine(folder, "models.bin"), "");
        return folder;
    }

    [Fact]
    public void Steam_libraries_come_from_todays_and_the_older_file()
    {
        const string today = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"apps"
            		{
            			"228980"		"352423212"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;
        const string older = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1612345678"
            	"1"		"E:\\Games\\Steam"
            }
            """;

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameLocator.SteamLibraries(today));
        Assert.Equal([@"E:\Games\Steam"], GameLocator.SteamLibraries(older));
    }

    [Fact]
    public void Copies_are_found_in_steams_libraries_the_registry_drives_and_own_folders()
    {
        var steam = Path.Combine(_root, "Steam");
        var library = Path.Combine(_root, "Library");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{library.Replace(@"\", @"\\")}\"\n\t}}\n}}\n");
        var inLibrary = Copy("Library", "steamapps", "common", "Machines Wired for War", "Game");
        var registered = Copy("Registered");
        var onDrive = Copy("Drive", "GOG Games", "Machines");
        var downloaded = Copy("Downloads", "Machines");
        // Only a folder with both models.bin and pdata is the game, and each copy is found once.
        Directory.CreateDirectory(Path.Combine(_root, "Downloads", "Half", "pdata"));

        var found = GameLocator.Copies([steam, steam], [registered, onDrive], [Path.Combine(_root, "Drive")], [Path.Combine(_root, "Downloads")]);

        Assert.Equal([inLibrary, registered, onDrive, downloaded], found);
    }

    [Fact]
    public void Only_folders_named_after_the_game_are_looked_into()
    {
        Copy("Games", "Machines", "Copy");
        Copy("Games", "Other", "Copy");

        Assert.Equal([Path.Combine(_root, "Games", "Machines", "Copy")], GameLocator.Within(Path.Combine(_root, "Games")));
        Assert.Empty(GameLocator.Within(Path.Combine(_root, "Missing")));
    }
}
