using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Research;

/// <summary>What one factory type and level can build, in menu order.</summary>
public sealed record FactoryBuildList(string FactoryType, int HardwareLevel, IReadOnlyList<ResearchKey> Machines, int LineNumber);

/// <summary>
/// Reads <c>factory.bld</c>. Follows <c>MachLogFactory::loadGame</c>:
/// <code>
/// MILITARY 1
/// AGGRESSOR 1 GRUNT R_BOLTER
/// SPY_LOCATOR 3
/// END
/// </code>
/// Each line is <c>type level [subtype] [weapon combo]</c>.
/// </summary>
public static class FactoryBuildListParser
{
    public static IReadOnlyList<FactoryBuildList> Load(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<FactoryBuildList> Parse(string text)
    {
        var lists = new List<FactoryBuildList>();
        using var lines = TextLines.Read(text).GetEnumerator();

        while (lines.MoveNext())
        {
            var header = lines.Current;
            if (header.Keyword is not ("MILITARY" or "CIVILIAN") || header.Args.Count != 1)
                throw new ParmFormatException($"expected MILITARY n or CIVILIAN n, found '{header}'", header.LineNumber);

            var machines = new List<ResearchKey>();
            while (true)
            {
                if (!lines.MoveNext())
                    throw new ParmFormatException($"{header.Keyword} {header.Args[0]} is never closed with END", header.LineNumber);
                var line = lines.Current;
                if (line.Keyword == "END")
                    break;
                if (line.Args.Count is < 1 or > 3)
                    throw new ParmFormatException("expected type level [subtype] [combo]", line.LineNumber);

                machines.Add(new ResearchKey(
                    line.Keyword,
                    line.Args.Count > 1 ? line.Args[1] : null,
                    ParmDataParser.ParseInt(line.Args[0], line),
                    line.Args.Count > 2 ? line.Args[2] : null));
            }

            var level = ParmDataParser.ParseInt(header.Args[0], header);
            if (lists.Any(l => l.FactoryType == header.Keyword && l.HardwareLevel == level))
                throw new ParmFormatException($"{header.Keyword} {level} appears twice", header.LineNumber);
            lists.Add(new FactoryBuildList(header.Keyword, level, machines, header.LineNumber));
        }
        return lists;
    }
}
