namespace Machines.Import.Parm;

/// <summary>
/// The small <c>KEY value</c> files: <c>zenith.dat</c> (zenith camera limits), <c>stats.ini</c> (AI and player
/// update priorities, network timing) and <c>simstats.ini</c> (simulation and render timing).
/// </summary>
public static class SettingsParser
{
    public static IReadOnlyDictionary<string, float> Load(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyDictionary<string, float> Parse(string text)
    {
        var settings = new Dictionary<string, float>();
        foreach (var line in TextLines.Read(text))
        {
            if (line.Args.Count != 1)
                throw new ParmFormatException($"{line.Keyword} needs one value", line.LineNumber);
            if (!settings.TryAdd(line.Keyword, ParmDataParser.ParseFloat(line.Args[0], line)))
                throw new ParmFormatException($"{line.Keyword} appears twice", line.LineNumber);
        }
        return settings;
    }
}

/// <summary>
/// <c>logos.dat</c>: texture bitmaps that the race-recolouring code (<c>machphys/racechbo.cpp</c>) swaps when a
/// machine changes race, one file name per line.
/// </summary>
public static class FileListParser
{
    public static IReadOnlyList<string> Load(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<string> Parse(string text) =>
        TextLines.Read(text).Select(line => line.Args.Count == 0
            ? line.Keyword
            : throw new ParmFormatException($"expected one file name, found '{line}'", line.LineNumber)).ToList();
}
