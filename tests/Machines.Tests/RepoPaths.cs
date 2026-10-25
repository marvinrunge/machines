namespace Machines.Tests;

internal static class RepoPaths
{
    /// <summary>Path of a file in <c>data/original</c>, found by walking up from the test binaries.</summary>
    public static string OriginalData(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", "original", fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException($"data/original/{fileName} not found above {AppContext.BaseDirectory}");
    }
}
