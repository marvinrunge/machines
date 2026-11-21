using System.Numerics;

namespace Machines.Import.Model;

/// <summary>What a cell of a <see cref="Deck"/> is.</summary>
public enum DeckCell : byte
{
    /// <summary><c>#</c>: a wall, 8 m high.</summary>
    Wall,
    /// <summary><c>.</c>: open floor.</summary>
    Floor,
    /// <summary><c>v</c>: inside a duct, where only a machine that crawls goes.</summary>
    Duct,
    /// <summary><c>^</c>: the raised lip at a duct's mouth (why wheels cannot get in); crawl only, like the duct.</summary>
    Lip,
    /// <summary><c>g</c>: a grate wall: nothing passes, but shots and sight go through.</summary>
    Grate,
    /// <summary><c>c</c>: high cover, 2 m (crates, consoles): it blocks the way and hides a machine behind it.</summary>
    Cover,
    /// <summary><c>=</c>: a catwalk or ramp; floor to the sim, raised only to the eye.</summary>
    Catwalk,
    /// <summary><c>A</c>–<c>Z</c>: the slot of a door or gate named in <see cref="Deck.Doors"/>.</summary>
    Door,
}

/// <summary>A door or gate of a deck: its letter in the grid, its name, kind (door, blast, gate, vent) and how it starts (open, closed, locked).</summary>
public sealed record DeckDoor(char Letter, string Name, string Kind, string State, int LineNumber);

/// <summary>A named point: spawns, clamp slots, patrol points, barricade slots, pickups, pods. <paramref name="Angle"/> in degrees, 0 east, 90 north.</summary>
public sealed record DeckMarker(string Name, Vector2 Position, float Angle, IReadOnlyList<string> Tags, int LineNumber);

/// <summary>A prop that moves or breaks, made an artefact of <paramref name="Type"/> in the scenario with the id <paramref name="Id"/>.</summary>
public sealed record DeckProp(string Name, string Type, Vector2 Position, float Angle, int Id, int LineNumber);

/// <summary>A named rectangle [Min, Max]: a room (tag <c>room</c>, with <c>height=</c> and <c>section=</c>), a trigger, a lit zone.</summary>
public sealed record DeckZone(string Name, Vector2 Min, Vector2 Max, IReadOnlyList<string> Tags, int LineNumber)
{
    public bool Contains(Vector2 p) => p.X >= Min.X && p.X < Max.X && p.Y >= Min.Y && p.Y < Max.Y;

    /// <summary>The value of a <c>key=value</c> tag, or null.</summary>
    public string? Tag(string key) => Tags.FirstOrDefault(t => t.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
}

/// <summary>A group of lamps lighting a zone, and how it starts: on, off or emergency.</summary>
public sealed record DeckLight(string Name, string Zone, string State, int LineNumber);

/// <summary>A camera for a cutscene: where it stands (z up, metres) and where it looks (degrees; yaw 0 east, 90 north).</summary>
public sealed record DeckCamera(string Name, Vector3 Position, float Yaw, float Pitch, int LineNumber);

/// <summary>A camera's path for a cutscene, through its points (z up).</summary>
public sealed record DeckRail(string Name, IReadOnlyList<Vector3> Points, int LineNumber);

/// <summary>
/// A ship deck (<c>&lt;planet&gt;.deck</c>, the remake's own): a grid of 1 m cells for walls, floors, ducts, grates, cover
/// and door slots, and the named markers, props, zones, lights and cameras a story is played with. One plane, x east and
/// y north from the south-west corner. The sim reads all of it but the cameras and rails, which only the view uses.
/// </summary>
public sealed class Deck
{
    private readonly char[] _cells;

    public Deck(string name, int width, int depth, char[] cells, IReadOnlyList<DeckDoor> doors, IReadOnlyList<DeckMarker> markers,
        IReadOnlyList<DeckProp> props, IReadOnlyList<DeckZone> zones, IReadOnlyList<DeckLight> lights, IReadOnlyList<DeckCamera> cameras,
        IReadOnlyList<DeckRail> rails, int gapScale = 1)
    {
        if (cells.Length != width * depth)
            throw new ArgumentException($"a {width} x {depth} deck needs {width * depth} cells, not {cells.Length}");
        (Name, Width, Depth, _cells) = (name, width, depth, cells);
        (Doors, Markers, Props, Zones, Lights, Cameras, Rails) = (doors, markers, props, zones, lights, cameras, rails);
        GapScale = Math.Max(1, gapScale);
        Fingerprint = Hash();
    }

    public string Name { get; }

    /// <summary>
    /// How many times its plan the deck was scaled as a whole (DECK name width depth [scale]; docs/story/deck/scale.md): every
    /// gap but the crawl ducts' is that many times as wide, so a size class needs that many times its room
    /// (the sim's NavGrid). 1 for a deck drawn at the plan's size.
    /// </summary>
    public int GapScale { get; }

    /// <summary>The grid's size in cells, which are 1 m: east-west and south-north.</summary>
    public int Width { get; }
    public int Depth { get; }

    public IReadOnlyList<DeckDoor> Doors { get; }
    public IReadOnlyList<DeckMarker> Markers { get; }
    public IReadOnlyList<DeckProp> Props { get; }
    public IReadOnlyList<DeckZone> Zones { get; }
    public IReadOnlyList<DeckLight> Lights { get; }
    public IReadOnlyList<DeckCamera> Cameras { get; }
    public IReadOnlyList<DeckRail> Rails { get; }

    /// <summary>
    /// A fingerprint of everything on the deck the sim goes by (the grid, doors, markers, props, zones and lights; not the
    /// cameras): worlds built from different decks go apart, and the checksum says so at once.
    /// </summary>
    public long Fingerprint { get; }

    /// <summary>The character at a cell; outside the grid is wall.</summary>
    public char At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Depth ? '#' : _cells[y * Width + x];

    /// <summary>What a cell is; outside the grid is wall.</summary>
    public DeckCell Cell(int x, int y) => Kind(At(x, y));

    /// <summary>What the cell under a point is.</summary>
    public DeckCell CellAt(Vector2 p) => Cell((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

    /// <summary>What a grid character stands for.</summary>
    public static DeckCell Kind(char c) => c switch
    {
        '.' => DeckCell.Floor,
        'v' => DeckCell.Duct,
        '^' => DeckCell.Lip,
        'g' => DeckCell.Grate,
        'c' => DeckCell.Cover,
        '=' => DeckCell.Catwalk,
        >= 'A' and <= 'Z' => DeckCell.Door,
        _ => DeckCell.Wall,
    };

    /// <summary>Whether a grid character is one the format knows.</summary>
    public static bool Known(char c) => c is '#' or '.' or 'v' or '^' or 'g' or 'c' or '=' or (>= 'A' and <= 'Z');

    /// <summary>The marker of that name.</summary>
    /// <exception cref="KeyNotFoundException">The deck has none.</exception>
    public DeckMarker Marker(string name) =>
        Markers.FirstOrDefault(m => m.Name == name) ?? throw new KeyNotFoundException($"deck {Name} has no marker {name}");

    /// <summary>The zone of that name, or null.</summary>
    public DeckZone? Zone(string name) => Zones.FirstOrDefault(z => z.Name == name);

    /// <summary>The door of that letter, or null.</summary>
    public DeckDoor? Door(char letter) => Doors.FirstOrDefault(d => d.Letter == letter);

    private long Hash()
    {
        // FNV-1a, 64 bit: the same on every machine (string.GetHashCode is not).
        var hash = unchecked((long)14695981039346656037UL);
        void Mix(long value)
        {
            for (var i = 0; i < 8; i++)
                hash = unchecked((hash ^ (byte)(value >> (8 * i))) * 1099511628211L);
        }
        void Text(string text)
        {
            Mix(text.Length);
            foreach (var c in text)
                Mix(c);
        }
        void Float(float value) => Mix(BitConverter.SingleToInt32Bits(value));
        Mix(Width);
        Mix(Depth);
        if (GapScale != 1)
            Mix(GapScale);
        foreach (var c in _cells)
            Mix(c);
        foreach (var door in Doors)
        {
            Mix(door.Letter);
            Text(door.Name);
            Text(door.Kind);
            Text(door.State);
        }
        foreach (var marker in Markers)
        {
            Text(marker.Name);
            Float(marker.Position.X);
            Float(marker.Position.Y);
            Float(marker.Angle);
            foreach (var tag in marker.Tags)
                Text(tag);
        }
        foreach (var prop in Props)
        {
            Text(prop.Name);
            Text(prop.Type);
            Float(prop.Position.X);
            Float(prop.Position.Y);
            Float(prop.Angle);
            Mix(prop.Id);
        }
        foreach (var zone in Zones)
        {
            Text(zone.Name);
            Float(zone.Min.X);
            Float(zone.Min.Y);
            Float(zone.Max.X);
            Float(zone.Max.Y);
            foreach (var tag in zone.Tags)
                Text(tag);
        }
        foreach (var light in Lights)
        {
            Text(light.Name);
            Text(light.Zone);
            Text(light.State);
        }
        return hash;
    }
}
