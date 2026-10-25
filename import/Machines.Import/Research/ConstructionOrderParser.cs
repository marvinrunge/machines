using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Research;

/// <summary>Reads <c>global.cst</c>: one <c>TYPE [SUBTYPE] LEVEL</c> per line, in build order.</summary>
public static class ConstructionOrderParser
{
    public static ConstructionOrder Load(string path) => Parse(File.ReadAllText(path));

    public static ConstructionOrder Parse(string text)
    {
        var entries = TextLines.Read(text).Select(line => line.Args.Count switch
        {
            1 => new ConstructionOrderEntry(line.Keyword, null, ParmDataParser.ParseInt(line.Args[0], line)),
            2 => new ConstructionOrderEntry(line.Keyword, line.Args[0], ParmDataParser.ParseInt(line.Args[1], line)),
            _ => throw new ParmFormatException("expected TYPE [SUBTYPE] LEVEL", line.LineNumber),
        }).ToList();

        return new ConstructionOrder(entries);
    }
}
