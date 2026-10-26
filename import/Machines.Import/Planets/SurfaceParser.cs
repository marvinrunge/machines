using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Planets;

/// <summary>Reads a planet surface (<c>.psf</c>). Follows <c>MachPhysPlanetSurface</c>.</summary>
public static class SurfaceParser
{
    public static PlanetSurface Load(string path) => Planet.WithFileName(path, () => Parse(File.ReadAllText(path)));

    public static PlanetSurface Parse(string text)
    {
        using var lines = TextLines.Read(text).GetEnumerator();

        ParmLine Next(string keyword, int args)
        {
            if (!lines.MoveNext())
                throw new ParmFormatException($"file ends, expected {keyword}", 0);
            var line = lines.Current;
            if (line.Keyword != keyword || line.Args.Count != args)
                throw new ParmFormatException($"expected {keyword} with {args} values, found '{line}'", line.LineNumber);
            return line;
        }

        var size = Next("PLANET_SIZE", 2);
        var tileSize = Next("TILE_SIZE", 1);
        var cells = Next("TILE_CELLS", 2);
        var bounds = Next("TILE_BOUNDS", 4);

        if (!lines.MoveNext() || lines.Current.Keyword != "BACKGROUND_FILE")
            throw new ParmFormatException("expected BACKGROUND_FILE", bounds.LineNumber);
        var background = lines.Current.Args.Count == 1 && lines.Current.Args[0] != "none" ? lines.Current.Args[0] : null;

        var tilesX = ParmDataParser.ParseInt(size.Args[0], size);
        var tilesY = ParmDataParser.ParseInt(size.Args[1], size);
        var min = new Vector2(ParmDataParser.ParseFloat(bounds.Args[0], bounds), ParmDataParser.ParseFloat(bounds.Args[1], bounds));
        var max = new Vector2(ParmDataParser.ParseFloat(bounds.Args[2], bounds), ParmDataParser.ParseFloat(bounds.Args[3], bounds));
        if (min.X >= max.X || min.Y >= max.Y)
            throw new ParmFormatException("TILE_BOUNDS must run from low to high", bounds.LineNumber);

        float? gravity = null, minMachineHeight = null;
        var tiles = new List<TerrainTile>();

        while (lines.MoveNext())
        {
            var line = lines.Current;
            switch (line.Keyword)
            {
                case "GRAVITY":
                    gravity = SingleFloat(line);
                    break;

                case "MIN_MACHINE_HEIGHT":
                    minMachineHeight = SingleFloat(line);
                    break;

                case "ROTATION":
                    var turns = SingleInt(line);
                    if (turns is < 0 or > 3)
                        throw new ParmFormatException($"ROTATION must be 0 to 3 quarter turns, found {turns}", line.LineNumber);
                    var height = SingleFloat(Next("HEIGHT", 1));
                    var mesh = Next("TILE", 1).Args[0];
                    var index = tiles.Count;
                    tiles.Add(new TerrainTile(index % tilesX, index / tilesX, turns, height, mesh));
                    break;

                default:
                    throw new ParmFormatException($"unexpected '{line.Keyword}'", line.LineNumber);
            }
        }

        if (tiles.Count != tilesX * tilesY)
            throw new ParmFormatException($"PLANET_SIZE {tilesX} x {tilesY} needs {tilesX * tilesY} tiles, found {tiles.Count}", size.LineNumber);

        return new PlanetSurface(
            tilesX, tilesY,
            SingleFloat(tileSize),
            ParmDataParser.ParseInt(cells.Args[0], cells), ParmDataParser.ParseInt(cells.Args[1], cells),
            min, max, background, gravity, minMachineHeight, tiles);
    }

    private static float SingleFloat(ParmLine line) =>
        line.Args.Count == 1 ? ParmDataParser.ParseFloat(line.Args[0], line) : throw new ParmFormatException($"{line.Keyword} needs one value", line.LineNumber);

    private static int SingleInt(ParmLine line) =>
        line.Args.Count == 1 ? ParmDataParser.ParseInt(line.Args[0], line) : throw new ParmFormatException($"{line.Keyword} needs one value", line.LineNumber);
}
