using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Planets;

/// <summary>
/// Reads a planet's pathfinding data (<c>.csp</c>). Follows <c>MachLogPlanet</c>:
/// <code>
/// DOMAIN id xmin ymin xmax ymax
/// NEWDOMAIN id n          followed by n lines "x y"
/// PORTAL domainA domainB x1 y1 x2 y2
/// OBSTACLE n height       followed by optional WATER / LOW_OBSTACLE lines, then n lines "x y"
/// </code>
/// Domain ids must count up from 0, as the original asserts.
/// </summary>
public static class ConfigSpaceParser
{
    public static ConfigSpace Load(string path) => Planet.WithFileName(path, () => Parse(File.ReadAllText(path)));

    public static ConfigSpace Parse(string text)
    {
        var lines = TextLines.Read(text).ToList();
        var domains = new List<Domain>();
        var portals = new List<Portal>();
        var obstacles = new List<Obstacle>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            switch (line.Keyword)
            {
                case "DOMAIN":
                {
                    Require(line, 5);
                    var id = Id(line, domains.Count);
                    var (x0, y0, x1, y1) = (F(line, 1), F(line, 2), F(line, 3), F(line, 4));
                    if (x0 >= x1 || y0 >= y1)
                        throw new ParmFormatException("DOMAIN box must run from low to high", line.LineNumber);
                    domains.Add(new Domain(id, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], line.LineNumber));
                    break;
                }

                case "NEWDOMAIN":
                {
                    Require(line, 2);
                    var id = Id(line, domains.Count);
                    domains.Add(new Domain(id, Points(lines, ref i, Count(line, 1)), line.LineNumber));
                    break;
                }

                case "PORTAL":
                {
                    Require(line, 6);
                    var a = ParmDataParser.ParseInt(line.Args[0], line);
                    var b = ParmDataParser.ParseInt(line.Args[1], line);
                    if (a >= domains.Count || b >= domains.Count)
                        throw new ParmFormatException($"PORTAL joins domain {Math.Max(a, b)}, which is not defined yet", line.LineNumber);
                    portals.Add(new Portal(a, b, new(F(line, 2), F(line, 3)), new(F(line, 4), F(line, 5)), line.LineNumber));
                    break;
                }

                case "OBSTACLE":
                {
                    Require(line, 2);
                    var count = Count(line, 0);
                    var height = F(line, 1);
                    if (height <= 0)
                        throw new ParmFormatException("an obstacle needs a height above 0", line.LineNumber);

                    bool water = false, low = false;
                    while (i + 1 < lines.Count && IsFlagLine(lines[i + 1]))
                    {
                        var flags = lines[++i];
                        foreach (var word in flags.Args.Prepend(flags.Keyword))
                        {
                            water |= word == "WATER";
                            low |= word == "LOW_OBSTACLE";
                        }
                    }

                    obstacles.Add(new Obstacle(Points(lines, ref i, count), height, water, low, line.LineNumber));
                    break;
                }

                default:
                    throw new ParmFormatException($"unexpected '{line.Keyword}'", line.LineNumber);
            }
        }

        return new ConfigSpace(domains, portals, obstacles);
    }

    private static bool IsFlagLine(ParmLine line) =>
        line.Args.Prepend(line.Keyword).All(w => w is "WATER" or "LOW_OBSTACLE");

    private static List<Vector2> Points(List<ParmLine> lines, ref int i, int count)
    {
        var header = lines[i];
        var points = new List<Vector2>(count);
        for (var n = 0; n < count; n++)
        {
            if (++i >= lines.Count)
                throw new ParmFormatException($"{header.Keyword} needs {count} points, the file ends after {n}", header.LineNumber);
            var point = lines[i];
            if (point.Args.Count != 1)
                throw new ParmFormatException($"{header.Keyword} point {n + 1} of {count} must be 'x y', found '{point}'", point.LineNumber);
            points.Add(new Vector2(ParmDataParser.ParseFloat(point.Keyword, point), ParmDataParser.ParseFloat(point.Args[0], point)));
        }
        return points;
    }

    private static int Id(ParmLine line, int expected)
    {
        var id = ParmDataParser.ParseInt(line.Args[0], line);
        return id == expected ? id : throw new ParmFormatException($"domain id {id} is out of sequence, expected {expected}", line.LineNumber);
    }

    private static int Count(ParmLine line, int index)
    {
        var count = ParmDataParser.ParseInt(line.Args[index], line);
        return count > 2 ? count : throw new ParmFormatException($"a polygon needs at least 3 points, found {count}", line.LineNumber);
    }

    private static float F(ParmLine line, int index) => ParmDataParser.ParseFloat(line.Args[index], line);

    private static void Require(ParmLine line, int args)
    {
        if (line.Args.Count != args)
            throw new ParmFormatException($"{line.Keyword} needs {args} values, found {line.Args.Count}", line.LineNumber);
    }
}
