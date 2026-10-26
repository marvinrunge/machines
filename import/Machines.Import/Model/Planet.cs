using System.Numerics;

namespace Machines.Import.Model;

/// <summary>
/// A planet surface (<c>.psf</c>): a grid of square terrain tiles. Each tile names a mesh from the
/// original art (not included), plus its rotation and height.
/// </summary>
/// <param name="TileSize">Edge length of one tile in metres.</param>
/// <param name="TileCellsX">Height-map cells per tile edge; a tile has one more vertex than cells.</param>
/// <param name="TileMin">Lower corner of a tile's mesh, relative to its centre.</param>
/// <param name="BackgroundFile">Distant backdrop, or null for none.</param>
/// <param name="Tiles">Row by row: x runs fastest, so tile (x, y) is at index y * TilesX + x.</param>
public sealed record PlanetSurface(
    int TilesX,
    int TilesY,
    float TileSize,
    int TileCellsX,
    int TileCellsY,
    Vector2 TileMin,
    Vector2 TileMax,
    string? BackgroundFile,
    float? Gravity,
    float? MinMachineHeight,
    IReadOnlyList<TerrainTile> Tiles)
{
    public float Width => TilesX * TileSize;
    public float Depth => TilesY * TileSize;

    public TerrainTile Tile(int x, int y) => Tiles[y * TilesX + x];
}

/// <param name="QuarterTurns">Rotation about the vertical axis in steps of 90 degrees (0 to 3).</param>
/// <param name="Mesh">Path of the tile mesh, relative to the planet folder, e.g. <c>lod\ariz06.lod</c>.</param>
public sealed record TerrainTile(int X, int Y, int QuarterTurns, float Height, string Mesh);

/// <summary>
/// The pathfinding data of a planet (<c>.csp</c>): walkable domains joined by portals, and obstacles.
/// </summary>
public sealed record ConfigSpace(IReadOnlyList<Domain> Domains, IReadOnlyList<Portal> Portals, IReadOnlyList<Obstacle> Obstacles);

/// <summary>A walkable region. <c>DOMAIN</c> lines give a box, <c>NEWDOMAIN</c> blocks a polygon.</summary>
public sealed record Domain(int Id, IReadOnlyList<Vector2> Boundary, int LineNumber);

/// <summary>A doorway between two domains, along the line from <see cref="From"/> to <see cref="To"/>.</summary>
public sealed record Portal(int DomainA, int DomainB, Vector2 From, Vector2 To, int LineNumber);

/// <summary>An impassable convex polygon, optionally flagged as water or as a low obstacle.</summary>
public sealed record Obstacle(IReadOnlyList<Vector2> Polygon, float Height, bool Water, bool Low, int LineNumber);

/// <summary>
/// The sky, lighting and fog of a planet (<c>.env</c>). Colour tables map a key (sun elevation for
/// tables driven by the sun) to a colour; orbits move the sun and moon over the day.
/// </summary>
public sealed record PlanetEnvironment(
    IReadOnlyDictionary<string, IReadOnlyList<ColourKey>> ColourTables,
    IReadOnlyDictionary<string, Orbit> Orbits,
    IReadOnlyList<Satellite> Satellites,
    Fog? Fog,
    IReadOnlyList<EnvBlock> OtherBlocks);

/// <param name="Colour">Red, green, blue, alpha in 0..1; alpha is 1 when the file gives three values.</param>
public sealed record ColourKey(float Key, Vector4 Colour);

/// <param name="StartTime">Seconds into the day at which the scenario starts.</param>
/// <param name="Period">Length of one day in seconds.</param>
/// <param name="SetHeading">Compass heading of the orbit in degrees, when given.</param>
public sealed record Orbit(float MinElevation, float MaxElevation, float Radius, float StartTime, float Period, float? SetHeading);

public sealed record Satellite(
    string Name,
    string Orbit,
    string? MeshFile,
    string? MeshColourTable,
    string? DirectionalColourTable,
    string? AmbientColourTable);

public sealed record Fog(float MaxVisibility, string ColourTable, string? Controller);

/// <summary>
/// A block the importer keeps as written: <c>dynamic sky</c>, <c>uniform sky</c>, <c>stars</c>,
/// <c>skyline</c>, <c>nvg</c>, <c>day phase</c>.
/// </summary>
/// <param name="Properties">Each <c>key: value</c> line; a key like <c>colour table @ 20</c> keeps its <c>@</c> part.</param>
public sealed record EnvBlock(string Kind, string? Name, IReadOnlyList<(string Key, EnvValue Value)> Properties, int LineNumber);

/// <summary>A value in an <c>.env</c> file: a quoted string, a number or a tuple <c>(a, b, c[, d])</c>.</summary>
public sealed record EnvValue(string? Text, float? Number, IReadOnlyList<float>? Tuple)
{
    public override string ToString() =>
        Text is not null ? $"\"{Text}\"" : Number is not null ? Number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"({string.Join(", ", Tuple!)})";
}
