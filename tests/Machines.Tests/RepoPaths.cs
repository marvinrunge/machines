namespace Machines.Tests;

internal static class RepoPaths
{
    /// <summary>The <c>data/original</c> folder, found by walking up from the test binaries.</summary>
    public static string OriginalDataFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", "original");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"data/original not found above {AppContext.BaseDirectory}");
    }

    /// <summary>Path of a file in <c>data/original</c>.</summary>
    public static string OriginalData(string fileName)
    {
        var path = Path.Combine(OriginalDataFolder(), fileName);
        return File.Exists(path) ? path : throw new FileNotFoundException($"data/original/{fileName} not found", path);
    }
}
