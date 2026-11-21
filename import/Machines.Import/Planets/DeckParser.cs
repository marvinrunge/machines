using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Planets;

/// <summary>
/// Reads a ship deck (<c>&lt;planet&gt;.deck</c>), the remake's own format for the story's interiors:
/// <code>
/// DECK name width depth
/// GRID
/// ...      one row of width characters per metre, the north edge (y = depth - 1) first, the south edge last
/// ENDGRID
/// DOORS    letter name kind state             ENDDOORS
/// MARKERS  name x y angle [tags]              ENDMARKERS
/// PROPS    name type x y angle id             ENDPROPS
/// ZONES    name x0 y0 x1 y1 [tags]            ENDZONES
/// LIGHTS   name zone on|off|emergency         ENDLIGHTS
/// CAMS     name x y z yaw pitch               ENDCAMS
/// RAILS    name x y z x y z ...               ENDRAILS
/// </code>
/// The grid's characters are <see cref="DeckCell"/>'s; <c>//</c> starts a comment. Every letter in the grid must be a
/// door, every door in the grid, and every light's zone a zone. tools/deck/deck.mjs reads the same file.
/// </summary>
public static class DeckParser
{
    public static Deck Load(string path) => Planet.WithFileName(path, () => Parse(File.ReadAllText(path)));

    public static Deck Parse(string text)
    {
        var lines = TextLines.Read(text).ToList();
        string? name = null;
        int width = 0, depth = 0, gapScale = 1;
        char[]? cells = null;
        List<DeckDoor> doors = [];
        List<DeckMarker> markers = [];
        List<DeckProp> props = [];
        List<DeckZone> zones = [];
        List<DeckLight> lights = [];
        List<DeckCamera> cameras = [];
        List<DeckRail> rails = [];

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            switch (line.Keyword)
            {
                case "DECK":
                    Count(line, 3, 4);
                    name = line.Args[0];
                    (width, depth) = (Int(line, 1), Int(line, 2));
                    gapScale = line.Args.Count > 3 ? Int(line, 3) : 1;
                    if (gapScale < 1)
                        throw new ParmFormatException("a deck's scale is 1 or more", line.LineNumber);
                    if (width <= 0 || depth <= 0)
                        throw new ParmFormatException("a deck needs a size above 0", line.LineNumber);
                    break;

                case "GRID":
                    if (name is null)
                        throw new ParmFormatException("GRID before DECK", line.LineNumber);
                    cells = new char[width * depth];
                    for (var row = 0; row < depth; row++)
                    {
                        if (++i >= lines.Count || lines[i].Keyword == "ENDGRID")
                            throw new ParmFormatException($"the grid has {row} rows, not {depth}", line.LineNumber);
                        var cellsRow = lines[i];
                        if (cellsRow.Args.Count != 0 || cellsRow.Keyword.Length != width)
                            throw new ParmFormatException($"a grid row must be {width} characters without spaces", cellsRow.LineNumber);
                        var y = depth - 1 - row;
                        for (var x = 0; x < width; x++)
                        {
                            var c = cellsRow.Keyword[x];
                            if (!Deck.Known(c))
                                throw new ParmFormatException($"unknown grid character '{c}'", cellsRow.LineNumber);
                            cells[y * width + x] = c;
                        }
                    }
                    if (++i >= lines.Count || lines[i].Keyword != "ENDGRID")
                        throw new ParmFormatException($"expected ENDGRID after {depth} rows", line.LineNumber);
                    break;

                case "DOORS":
                    foreach (var entry in Section(lines, ref i, "ENDDOORS"))
                    {
                        Count(entry, 3, 3);
                        if (entry.Keyword.Length != 1 || entry.Keyword[0] is < 'A' or > 'Z')
                            throw new ParmFormatException($"a door's letter is one of A to Z, not '{entry.Keyword}'", entry.LineNumber);
                        if (entry.Args[2] is not ("open" or "closed" or "locked"))
                            throw new ParmFormatException($"a door is open, closed or locked, not '{entry.Args[2]}'", entry.LineNumber);
                        doors.Add(new DeckDoor(entry.Keyword[0], entry.Args[0], entry.Args[1], entry.Args[2], entry.LineNumber));
                    }
                    break;

                case "MARKERS":
                    foreach (var entry in Section(lines, ref i, "ENDMARKERS"))
                    {
                        Count(entry, 3, int.MaxValue);
                        markers.Add(new DeckMarker(entry.Keyword, new Vector2(Float(entry, 0), Float(entry, 1)), Float(entry, 2), entry.Args.Skip(3).ToList(), entry.LineNumber));
                    }
                    break;

                case "PROPS":
                    foreach (var entry in Section(lines, ref i, "ENDPROPS"))
                    {
                        Count(entry, 5, 5);
                        props.Add(new DeckProp(entry.Keyword, entry.Args[0], new Vector2(Float(entry, 1), Float(entry, 2)), Float(entry, 3), Int(entry, 4), entry.LineNumber));
                    }
                    break;

                case "ZONES":
                    foreach (var entry in Section(lines, ref i, "ENDZONES"))
                    {
                        Count(entry, 4, int.MaxValue);
                        var (min, max) = (new Vector2(Float(entry, 0), Float(entry, 1)), new Vector2(Float(entry, 2), Float(entry, 3)));
                        if (min.X >= max.X || min.Y >= max.Y)
                            throw new ParmFormatException("a zone must run from low to high", entry.LineNumber);
                        zones.Add(new DeckZone(entry.Keyword, min, max, entry.Args.Skip(4).ToList(), entry.LineNumber));
                    }
                    break;

                case "LIGHTS":
                    foreach (var entry in Section(lines, ref i, "ENDLIGHTS"))
                    {
                        Count(entry, 2, 2);
                        if (entry.Args[1] is not ("on" or "off" or "emergency"))
                            throw new ParmFormatException($"a light is on, off or emergency, not '{entry.Args[1]}'", entry.LineNumber);
                        lights.Add(new DeckLight(entry.Keyword, entry.Args[0], entry.Args[1], entry.LineNumber));
                    }
                    break;

                case "CAMS":
                    foreach (var entry in Section(lines, ref i, "ENDCAMS"))
                    {
                        Count(entry, 5, 5);
                        cameras.Add(new DeckCamera(entry.Keyword, new Vector3(Float(entry, 0), Float(entry, 1), Float(entry, 2)), Float(entry, 3), Float(entry, 4), entry.LineNumber));
                    }
                    break;

                case "RAILS":
                    foreach (var entry in Section(lines, ref i, "ENDRAILS"))
                    {
                        if (entry.Args.Count < 6 || entry.Args.Count % 3 != 0)
                            throw new ParmFormatException("a rail needs two or more points of x y z", entry.LineNumber);
                        rails.Add(new DeckRail(entry.Keyword,
                            Enumerable.Range(0, entry.Args.Count / 3).Select(k => new Vector3(Float(entry, 3 * k), Float(entry, 3 * k + 1), Float(entry, 3 * k + 2))).ToList(),
                            entry.LineNumber));
                    }
                    break;

                default:
                    throw new ParmFormatException($"unexpected '{line.Keyword}'", line.LineNumber);
            }
        }

        if (name is null || cells is null)
            throw new ParmFormatException("a deck needs DECK and GRID", lines.Count > 0 ? lines[^1].LineNumber : 0);
        Check(cells, doors, zones, lights);
        return new Deck(name, width, depth, cells, doors, markers, props, zones, lights, cameras, rails, gapScale);
    }

    private static void Check(char[] cells, List<DeckDoor> doors, List<DeckZone> zones, List<DeckLight> lights)
    {
        var letters = cells.Where(c => c is >= 'A' and <= 'Z').ToHashSet();
        if (letters.FirstOrDefault(l => doors.All(d => d.Letter != l)) is var stray and not '\0')
            throw new ParmFormatException($"door {stray} in the grid is not in DOORS", 0);
        if (doors.FirstOrDefault(d => !letters.Contains(d.Letter)) is { } missing)
            throw new ParmFormatException($"door {missing.Letter} ({missing.Name}) is not in the grid", missing.LineNumber);
        if (doors.GroupBy(d => d.Letter).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new ParmFormatException($"door {twice.Key} is listed twice", twice.Last().LineNumber);
        if (lights.FirstOrDefault(l => zones.All(z => z.Name != l.Zone)) is { } dark)
            throw new ParmFormatException($"light {dark.Name} names no zone {dark.Zone}", dark.LineNumber);
    }

    private static IEnumerable<ParmLine> Section(List<ParmLine> lines, ref int i, string end)
    {
        var start = lines[i].LineNumber;
        var entries = new List<ParmLine>();
        while (++i < lines.Count && lines[i].Keyword != end)
            entries.Add(lines[i]);
        if (i >= lines.Count)
            throw new ParmFormatException($"expected {end}", start);
        return entries;
    }

    private static void Count(ParmLine line, int least, int most)
    {
        if (line.Args.Count < least || line.Args.Count > most)
            throw new ParmFormatException($"'{line.Keyword}' has {line.Args.Count} values", line.LineNumber);
    }

    private static float Float(ParmLine line, int index) => ParmDataParser.ParseFloat(line.Args[index], line);

    private static int Int(ParmLine line, int index) => ParmDataParser.ParseInt(line.Args[index], line);
}
