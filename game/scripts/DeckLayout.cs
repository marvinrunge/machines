using System.Globalization;
using System.Numerics;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>A rectangle of a deck's cells, [X0, X1) x [Y0, Y1), in metres (cells are 1 m).</summary>
public readonly record struct DeckRect(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Depth => Y1 - Y0;
    public Vector2 Centre => new((X0 + X1) / 2f, (Y0 + Y1) / 2f);
    public bool Contains(int x, int y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;
}

/// <summary>
/// A run of wall faces along one grid line, seen from the open side: from <see cref="From"/> to <see cref="To"/> (deck metres),
/// facing <see cref="Normal"/> (into the open cells), standing from <see cref="Bottom"/> to <see cref="Top"/> metres up.
/// A run over a lower neighbour (a doorway, a grate, a lower room) starts at that neighbour's ceiling: a lintel.
/// </summary>
public sealed record DeckFace(Vector2 From, Vector2 To, Vector2 Normal, float Bottom, float Top)
{
    public float Length => Vector2.Distance(From, To);
}

/// <summary>A ceiling over a rectangle of open cells at <see cref="Height"/> metres.</summary>
public sealed record DeckCeiling(DeckRect Rect, float Height);

/// <summary>A 4 m floor block, its south-west corner at (<see cref="X"/>, <see cref="Y"/>); grated where the catwalk runs through it (a quarter of its open cells or more).</summary>
public sealed record DeckFloor(int X, int Y, bool Grate);

/// <summary>
/// A door slot (its letter, kind and state from DOORS) or a grate wall (letter '\0', kind "grate") in the grid: the panel spans
/// the rectangle across the way through it, along x when <see cref="AlongX"/> (the way goes north-south), and is
/// <see cref="Height"/> tall.
/// </summary>
public sealed record DeckOpening(char Letter, string Kind, DeckRect Rect, bool AlongX, float Height)
{
    /// <summary>How wide the way through is.</summary>
    public int Width => AlongX ? Rect.Width : Rect.Depth;
}

/// <summary>
/// A set piece or a fitting of the deck: the story prop <see cref="Prop"/> (game/assets/story/props) standing with its bottom
/// centre at <see cref="Position"/> (deck metres, z up), its front facing <see cref="Angle"/> (degrees, 0 east, 90 north),
/// scaled by <see cref="Scale"/> in its own frame (x across its front, y up, z out of its front). "plate" is the view's own
/// floor plate of a switch.
/// </summary>
public sealed record DeckPiece(string Prop, Vector3 Position, float Angle, Vector3 Scale);

/// <summary>
/// The heavy frame (kit_door_frame) round a door slot, its bottom centre at <see cref="Centre"/>, its front facing
/// <see cref="Angle"/>, bent to the slot rather than scaled (<see cref="Fit"/>): its posts' inner faces just behind the slot's
/// sides (<see cref="Half"/> either side of the middle), so the wall's reveal is the jamb and the way through looks as wide as
/// the sim's slot; its lintel's underside a centimetre under the ceiling's slab in the slot (<see cref="Open"/> high); its
/// front plates a little proud of both faces of a wall <see cref="Depth"/> thick. Its posts reach along the wall
/// <see cref="PostMinus"/> and <see cref="PostPlus"/> times the kit's own width (on its -x and +x), its lintel up
/// <see cref="Lintel"/> times the kit's own height, so neither leaves the wall.
/// </summary>
public sealed record DeckFrame(char Letter, Vector2 Centre, float Angle, float Half, float Open, float Depth, float PostMinus, float PostPlus, float Lintel)
{
    // The kit's own measures (kit_door_frame, metres, x across its front, y up, z out of its front): its posts' inner sides
    // lie 1.32 to 1.39 m from the middle (Jamb a little inside them, so they fall behind the reveal), their fronts begin past
    // a bevel at Face and end at Outer; the lintel's underside is at Lintel, its top at Top; the front plates stand at Plate
    // either side of its middle. Its sill (Props.Footless) and a slab Meshy left in the opening's left are dropped.

    /// <summary>The kit's measures: the pivot of its posts, their fronts' inner edge, its outer edge.</summary>
    public const float Jamb = 1.30f, Face = 1.72f, Outer = 3.5f;

    /// <summary>The kit's measures: its lintel's underside and its top; its front plates' distance from its middle.</summary>
    public const float KitLintel = 5.548f, Top = 8, Plate = 0.577f;

    /// <summary>How wide the bevel from the jamb to the front plate is drawn, how far the plates stand proud of the wall, and the gap under the slab, metres.</summary>
    public const float Inset = 0.08f, Proud = 0.05f, Gap = 0.01f;

    /// <summary>Whether a point of the kit lies inside its opening (below the lintel, between the posts): no face may reach in there.</summary>
    public static bool InOpening(Vector3 point) => MathF.Abs(point.X) < Jamb - 0.02f && point.Y < KitLintel - 0.02f;

    /// <summary>
    /// Where a point of the kit goes on this frame, and how much its neighbourhood is stretched along each axis there (to turn
    /// its normal): the opening's span stretched to the slot, the bevel to <see cref="Inset"/>, the posts outside it at
    /// their reach; under the lintel stretched to the opening's height, the lintel above at its own; the depth to the wall's.
    /// </summary>
    public (Vector3 At, Vector3 Stretch) Fit(Vector3 kit)
    {
        var side = kit.X < 0 ? -1 : 1;
        var across = MathF.Abs(kit.X);
        float x, sx;
        if (across <= Jamb)
            (x, sx) = (across * Half / Jamb, Half / Jamb);
        else if (across <= Face)
            (x, sx) = (Half + (across - Jamb) * Inset / (Face - Jamb), Inset / (Face - Jamb));
        else
        {
            var reach = side < 0 ? PostMinus : PostPlus;
            (x, sx) = (Half + Inset + (across - Face) * reach, reach);
        }
        var (y, sy) = kit.Y <= KitLintel ? (kit.Y * Open / KitLintel, Open / KitLintel) : (Open + (kit.Y - KitLintel) * Lintel, Lintel);
        var deep = MathF.Abs(kit.Z);
        var face = Depth / 2 + Proud;
        var (z, sz) = deep <= Plate ? (deep * face / Plate, face / Plate) : (face + deep - Plate, 1f);
        return (new Vector3(side * x, y, MathF.Sign(kit.Z) * z), new Vector3(sx, sy, sz));
    }
}

public enum DeckLampKind
{
    /// <summary>A lamp under the ceiling, lit while its group is on.</summary>
    Ceiling,

    /// <summary>A red emergency lamp on a wall, lit while its group is on emergency.</summary>
    Wall,
}

/// <summary>
/// A lamp of a light group (LIGHTS): where it hangs (deck metres, z up), which way a wall lamp faces (degrees), how far its
/// cone reaches and how wide it opens (half the angle, degrees; a ceiling lamp shines straight down, a wall lamp into its
/// room and down), and whether it casts shadows. Only a few do (in the tall rooms); the others would shine through walls, so
/// each cone is kept narrow enough that it falls on no floor behind a wall (<see cref="DeckLayout.ClearReach"/>).
/// </summary>
public sealed record DeckLamp(string Group, DeckLampKind Kind, Vector3 Position, float Angle, float Range, float Spread, bool Shadowed);

/// <summary>
/// What the view of a ship deck (<see cref="Deck"/>) is built from: how high each open cell's ceiling is, the wall faces, the
/// ceilings and floors, the door slots and grate walls, the lamps of each light group, the boxes that hide what is behind
/// the walls, and the set pieces dressing the rooms. Free of Godot, so the tests can check it; DeckView puts it up with the
/// story's kit (game/assets/story/props). The story is the remake's own.
/// </summary>
public sealed class DeckLayout
{
    /// <summary>The kit's modules are this many metres square (floor, ceiling, wall width).</summary>
    public const int Module = 4;

    /// <summary>Ceilings where no room says: inside ducts, of a room the deck gives no height, and the doors by kind.</summary>
    public const float DuctHeight = 3, GrateHeight = 8, DefaultHeight = 16, DoorHeight = 8, VentHeight = 3, GateHeight = 14, BlastHeight = 14, WideBlastHeight = 16;

    /// <summary>How far apart a lit room's ceiling lamps hang, and an emergency lamp on the walls at most.</summary>
    public const float LampSpacing = 16, WallLampSpacing = 24;

    /// <summary>Rooms at least this tall get a shadowed lamp in their middle (the computer core, the pod bay).</summary>
    public const float ShadowedRoom = 30;

    private readonly float[] _height;

    public DeckLayout(Deck deck)
    {
        Deck = deck;
        _height = new float[deck.Width * deck.Depth];
        Openings = FindOpenings();
        Heights();
        Faces = FindFaces();
        Ceilings = [.. Rects(IsOpen, Height).Select(r => new DeckCeiling(r, Height(r.X0, r.Y0)))];
        Floors = FindFloors();
        Covers = Rects((x, y) => Deck.Cell(x, y) == DeckCell.Cover, (_, _) => 0);
        Stacks = Rects((x, y) => Deck.Cell(x, y) == DeckCell.High, (_, _) => 0);
        Occluders = Rects((x, y) => Deck.Cell(x, y) == DeckCell.Wall, (_, _) => 0);
        Rooms = [.. Deck.Zones.Where(z => z.Tags.Contains("room"))];
        Lamps = FindLamps();
        Pieces = Dress();
        Frames = FindFrames();
        Beacons = FindBeacons();
        CrashFires = FindCrashFires();
    }

    /// <summary>
    /// Where fires break out as the ship goes down (deck metres, on the floor), in the order they catch: the broken pods first
    /// (every pod but the one with the hatch), then by the walls of the crash's rooms (section 7: the Notgang, the pod bay),
    /// a little out from the wall every 11 m along each side.
    /// </summary>
    public IReadOnlyList<Vector3> CrashFires { get; }

    /// <summary>
    /// A pod within this many metres of the hatch is the one that launches (pod 3's hatch is 34 m from its middle, outside its hull and cradle); a broken
    /// pod burns this far north of its middle, beside its 47.5 m hull (the game's Pod at its own size).
    /// </summary>
    public const float PodHatchReach = 40, PodFireOffset = 26;

    /// <summary>How many times its 10 m the pods' cradle ring is drawn, to hold the game's Pod at its own size.</summary>
    public const float PodCradle = 5;

    private List<Vector3> FindCrashFires()
    {
        var fires = new List<Vector3>();
        var hatch = Deck.Markers.FirstOrDefault(m => m.Tags.Contains("hatch"));
        foreach (var pod in Deck.Markers.Where(m => m.Tags.Contains("pod")))
            if (hatch is null || Vector2.Distance(pod.Position, hatch.Position) > PodHatchReach)
                fires.Add(new Vector3(pod.Position + new Vector2(0, PodFireOffset), 0));
        var walls = new List<Vector3>();
        foreach (var room in Rooms.Where(r => r.Tag("section") == "7"))
            foreach (var (start, along, inward, length) in Sides(room))
                for (var t = 5f; t < length - 3; t += 11)
                    if (OnWall(start, along, inward, t, length) is { } at)
                        walls.Add(new Vector3(at + inward * 1.2f, 0));
        // Not side by side as they catch: spread over the rooms.
        fires.AddRange(walls.OrderBy(p => Hash((int)p.X, (int)p.Y, 3)));
        return fires;
    }

    /// <summary>
    /// The alarm's beacons (prop <c>alarm_beacon</c>), high on the walls of every room a machine stands up in: one in a small
    /// room, two facing each other across a large one. Dark until the ship's alarm, when they turn and flash red (DeckView).
    /// </summary>
    public IReadOnlyList<DeckPiece> Beacons { get; }

    public Deck Deck { get; }

    /// <summary>The rooms (zones tagged <c>room</c>), in the deck's order.</summary>
    public IReadOnlyList<DeckZone> Rooms { get; }

    public IReadOnlyList<DeckFace> Faces { get; }
    public IReadOnlyList<DeckCeiling> Ceilings { get; }
    public IReadOnlyList<DeckFloor> Floors { get; }
    public IReadOnlyList<DeckOpening> Openings { get; }

    /// <summary>The high cover (<c>c</c>) as rectangles; each holds a set piece or crates (<see cref="Pieces"/>).</summary>
    public IReadOnlyList<DeckRect> Covers { get; }

    /// <summary>The high stacks (<c>h</c>) as rectangles; each stands as a set piece 8 m tall (<see cref="Stack"/>).</summary>
    public IReadOnlyList<DeckRect> Stacks { get; }

    /// <summary>How tall a high stack stands (the sim's wall height), and the longest one tower of it runs, metres.</summary>
    public const float StackHeight = 8, StackTile = 6;

    /// <summary>What a high stack is drawn as: a clamp gantry in a row, lashed containers in the hold, a pipe bank when thin, else crate towers.</summary>
    public string StackKind(DeckRect stack)
    {
        var centre = stack.Centre;
        bool In(string tag) => Deck.Zones.Any(z => z.Tags.Contains(tag) && z.Contains(centre));
        return In("row") ? "gantry" : In("hold") ? "containers" : Math.Min(stack.Width, stack.Depth) <= 2 ? "pipes" : "crates";
    }

    /// <summary>
    /// The set pieces of a high stack, filling its cells to 8 m: in a row (zone tag <c>row</c>) a clamp gantry, four posts and
    /// pipe beams on both faces; in the hold (zone tag <c>hold</c>) containers two high; a stack two metres thin or less a bank
    /// of pipes; anything else towers of crates three high, a tower at most <see cref="StackTile"/> metres a side.
    /// </summary>
    private IEnumerable<DeckPiece> Stack(DeckRect stack)
    {
        var alongX = stack.Width >= stack.Depth;
        var (length, across) = alongX ? (stack.Width, stack.Depth) : (stack.Depth, stack.Width);
        var facing = alongX ? 90f : 0f;
        Vector2 At(float t, float s) => alongX ? new Vector2(stack.X0 + t, stack.Y0 + s) : new Vector2(stack.X0 + s, stack.Y0 + t);
        var kind = StackKind(stack);
        if (kind == "gantry")
        {
            foreach (var (t, s) in new[] { (0.5f, 0.5f), (length - 0.5f, 0.5f), (0.5f, across - 0.5f), (length - 0.5f, across - 0.5f) })
                yield return new DeckPiece("kit_pillar", new Vector3(At(t, s), 0), facing, new Vector3(1, StackHeight / 8, 1));
            for (var level = 0; level < 3; level++)
                foreach (var s in new[] { 0.5f, across - 0.5f })
                    yield return new DeckPiece("pipe_run", new Vector3(At(length / 2, s), 1.5f + 2.2f * level), facing, new Vector3((length - 1) / 4f, 1, 1));
            yield break;
        }
        if (kind == "pipes")
        {
            var runs = Math.Max(1, (int)MathF.Round(length / 4f));
            for (var i = 0; i < runs; i++)
                for (var level = 0; level < 4; level++)
                    yield return new DeckPiece("pipe_run", new Vector3(At((i + 0.5f) * length / runs, across / 2f), 2f * level), facing,
                        new Vector3(length / (float)runs / 4f, 1, across));
            yield break;
        }
        var tiers = kind == "containers" ? 2 : 3;
        var tile = kind == "containers" ? 12f : StackTile;
        var (n, m) = ((int)MathF.Ceiling(length / tile), (int)MathF.Ceiling(across / tile));
        var (tl, ta, th) = (length / (float)n, across / (float)m, StackHeight / tiers);
        for (var i = 0; i < n; i++)
            for (var j = 0; j < m; j++)
                for (var k = 0; k < tiers; k++)
                {
                    // Each tier a little in from the one below, turned now and then: stacked by hand, not cast in one piece.
                    var shrink = 1 - 0.04f * k;
                    var flip = Hash(stack.X0 + i, stack.Y0 + j, k) % 2 == 0 ? 0 : 180;
                    yield return new DeckPiece("crate_large", new Vector3(At((i + 0.5f) * tl, (j + 0.5f) * ta), k * th), facing + flip,
                        new Vector3(tl * shrink / 3f, th / 2.078f, ta * shrink / 2f));
                }
    }

    /// <summary>The walls as rectangles, for boxes that hide what stands behind them (occlusion culling).</summary>
    public IReadOnlyList<DeckRect> Occluders { get; }

    public IReadOnlyList<DeckLamp> Lamps { get; }
    public IReadOnlyList<DeckPiece> Pieces { get; }

    /// <summary>Whether a cell is anything but wall (outside the grid is wall).</summary>
    public bool IsOpen(int x, int y) => Deck.Cell(x, y) != DeckCell.Wall;

    /// <summary>How high the ceiling over an open cell is, metres; 0 for a wall.</summary>
    public float Height(int x, int y) => IsOpen(x, y) ? _height[y * Deck.Width + x] : 0;

    /// <summary>The height a room zone gives (<c>height=</c>), or null.</summary>
    public static float? ZoneHeight(DeckZone zone) =>
        zone.Tag("height") is { } text && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : null;

    // ---- heights ------------------------------------------------------------------------------

    private void Heights()
    {
        var (w, d) = (Deck.Width, Deck.Depth);
        var set = new bool[w * d];
        // The rooms first: a cell in two takes the taller, so no ceiling cuts into a room.
        foreach (var zone in Deck.Zones.Where(z => z.Tags.Contains("room")))
        {
            var height = ZoneHeight(zone) ?? DefaultHeight;
            for (var y = Math.Max(0, (int)MathF.Floor(zone.Min.Y)); y < Math.Min(d, (int)MathF.Ceiling(zone.Max.Y)); y++)
                for (var x = Math.Max(0, (int)MathF.Floor(zone.Min.X)); x < Math.Min(w, (int)MathF.Ceiling(zone.Max.X)); x++)
                    if (Deck.Cell(x, y) is DeckCell.Floor or DeckCell.Cover or DeckCell.Catwalk or DeckCell.High)
                    {
                        _height[y * w + x] = set[y * w + x] ? Math.Max(_height[y * w + x], height) : height;
                        set[y * w + x] = true;
                    }
        }
        // Ducts are low wherever they run.
        for (var i = 0; i < w * d; i++)
            if (Deck.Cell(i % w, i / w) is DeckCell.Duct or DeckCell.Lip)
                (_height[i], set[i]) = (DuctHeight, true);
        // The floor no room covers takes the height of the nearest that has one, through open floor.
        var queue = new Queue<int>();
        for (var i = 0; i < w * d; i++)
            if (set[i])
                queue.Enqueue(i);
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            foreach (var n in Neighbours(i % w, i / w))
            {
                var j = n.Y * w + n.X;
                if (set[j] || Deck.Cell(n.X, n.Y) is not (DeckCell.Floor or DeckCell.Cover or DeckCell.Catwalk or DeckCell.High))
                    continue;
                (_height[j], set[j]) = (_height[i], true);
                queue.Enqueue(j);
            }
        }
        for (var i = 0; i < w * d; i++)
            if (!set[i] && Deck.Cell(i % w, i / w) is DeckCell.Floor or DeckCell.Cover or DeckCell.Catwalk or DeckCell.High)
                (_height[i], set[i]) = (DefaultHeight, true);
        // A door or grate is as tall as its kind, and no taller than the lowest room it opens to.
        foreach (var opening in Openings)
            for (var y = opening.Rect.Y0; y < opening.Rect.Y1; y++)
                for (var x = opening.Rect.X0; x < opening.Rect.X1; x++)
                    _height[y * w + x] = opening.Height;
    }

    private IEnumerable<(int X, int Y)> Neighbours(int x, int y)
    {
        if (x + 1 < Deck.Width) yield return (x + 1, y);
        if (x > 0) yield return (x - 1, y);
        if (y + 1 < Deck.Depth) yield return (x, y + 1);
        if (y > 0) yield return (x, y - 1);
    }

    // ---- doors and grates ------------------------------------------------------------------------

    /// <summary>
    /// Each door slot (one rectangle a letter) and grate wall (each block of <c>g</c>), its way through and its height. Called
    /// before the heights are known: what lies beside it is read from the rooms' zones.
    /// </summary>
    private List<DeckOpening> FindOpenings()
    {
        var openings = new List<DeckOpening>();
        foreach (var door in Deck.Doors)
        {
            if (Bounds((x, y) => Deck.At(x, y) == door.Letter) is not { } rect)
                continue;
            var alongX = AcrossNorthSouth(rect, (x, y) => Deck.At(x, y) == door.Letter);
            var width = alongX ? rect.Width : rect.Depth;
            var kind = door.Kind switch
            {
                "vent" => VentHeight,
                "gate" => GateHeight,
                "blast" => width >= 16 ? WideBlastHeight : BlastHeight,
                _ => DoorHeight,
            };
            openings.Add(new DeckOpening(door.Letter, door.Kind, rect, alongX, Math.Min(kind, LowestBeside(rect, alongX))));
        }
        var seen = new bool[Deck.Width * Deck.Depth];
        for (var y = 0; y < Deck.Depth; y++)
            for (var x = 0; x < Deck.Width; x++)
            {
                if (seen[y * Deck.Width + x] || Deck.Cell(x, y) != DeckCell.Grate)
                    continue;
                var block = Flood(x, y, (a, b) => Deck.Cell(a, b) == DeckCell.Grate, seen);
                var alongX = AcrossNorthSouth(block, (a, b) => Deck.Cell(a, b) == DeckCell.Grate);
                openings.Add(new DeckOpening('\0', "grate", block, alongX, Math.Min(GrateHeight, LowestBeside(block, alongX))));
            }
        return openings;
    }

    /// <summary>Whether the way through a slot runs north-south (open cells beyond its south and north edges) rather than east-west.</summary>
    private bool AcrossNorthSouth(DeckRect rect, Func<int, int, bool> member)
    {
        bool Through(int x, int y) => IsOpen(x, y) && !member(x, y);
        var northSouth = 0;
        for (var x = rect.X0; x < rect.X1; x++)
            northSouth += (Through(x, rect.Y0 - 1) ? 1 : 0) + (Through(x, rect.Y1) ? 1 : 0);
        var eastWest = 0;
        for (var y = rect.Y0; y < rect.Y1; y++)
            eastWest += (Through(rect.X0 - 1, y) ? 1 : 0) + (Through(rect.X1, y) ? 1 : 0);
        return northSouth >= eastWest;
    }

    /// <summary>The lowest ceiling of the cells the way through a slot leads to, by the rooms' zones (ducts are low).</summary>
    private float LowestBeside(DeckRect rect, bool alongX)
    {
        var lowest = float.MaxValue;
        void Try(int x, int y)
        {
            if (!IsOpen(x, y) || Deck.Cell(x, y) is DeckCell.Door or DeckCell.Grate)
                return;
            lowest = Math.Min(lowest, Deck.Cell(x, y) is DeckCell.Duct or DeckCell.Lip ? DuctHeight : RoomHeightAt(x, y));
        }
        if (alongX)
            for (var x = rect.X0; x < rect.X1; x++)
            {
                Try(x, rect.Y0 - 1);
                Try(x, rect.Y1);
            }
        else
            for (var y = rect.Y0; y < rect.Y1; y++)
            {
                Try(rect.X0 - 1, y);
                Try(rect.X1, y);
            }
        return lowest == float.MaxValue ? DoorHeight : lowest;
    }

    /// <summary>The tallest room zone over a cell, or <see cref="DefaultHeight"/>.</summary>
    private float RoomHeightAt(int x, int y)
    {
        var point = new Vector2(x + 0.5f, y + 0.5f);
        var heights = Deck.Zones.Where(z => z.Tags.Contains("room") && z.Contains(point)).Select(z => ZoneHeight(z) ?? DefaultHeight).ToList();
        return heights.Count > 0 ? heights.Max() : DefaultHeight;
    }

    private DeckRect? Bounds(Func<int, int, bool> member)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (var y = 0; y < Deck.Depth; y++)
            for (var x = 0; x < Deck.Width; x++)
                if (member(x, y))
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x + 1), Math.Max(y1, y + 1));
        return x0 == int.MaxValue ? null : new DeckRect(x0, y0, x1, y1);
    }

    private DeckRect Flood(int x, int y, Func<int, int, bool> member, bool[] seen)
    {
        var (x0, y0, x1, y1) = (x, y, x + 1, y + 1);
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        seen[y * Deck.Width + x] = true;
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            (x0, y0, x1, y1) = (Math.Min(x0, a), Math.Min(y0, b), Math.Max(x1, a + 1), Math.Max(y1, b + 1));
            foreach (var n in Neighbours(a, b))
                if (!seen[n.Y * Deck.Width + n.X] && member(n.X, n.Y))
                {
                    seen[n.Y * Deck.Width + n.X] = true;
                    stack.Push(n);
                }
        }
        return new DeckRect(x0, y0, x1, y1);
    }

    // ---- walls, ceilings, floors -------------------------------------------------------------------

    private static readonly (int Dx, int Dy)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>
    /// Every face an open cell shows: towards a wall from its floor to its ceiling, towards a lower open cell from that one's
    /// ceiling up (lintels over doors and grates, steps between rooms). Unit faces along a line with the same span join.
    /// </summary>
    private List<DeckFace> FindFaces()
    {
        // (direction, line, bottom, top) -> positions along the line
        var lines = new SortedDictionary<(int Dir, int Line, float Bottom, float Top), List<int>>();
        for (var y = 0; y < Deck.Depth; y++)
            for (var x = 0; x < Deck.Width; x++)
            {
                if (!IsOpen(x, y))
                    continue;
                var top = Height(x, y);
                for (var dir = 0; dir < 4; dir++)
                {
                    var (nx, ny) = (x + Directions[dir].Dx, y + Directions[dir].Dy);
                    float bottom;
                    if (!IsOpen(nx, ny))
                        bottom = 0;
                    else if (Height(nx, ny) < top - 0.01f)
                        bottom = Height(nx, ny);
                    else
                        continue;
                    var line = dir switch { 0 => x + 1, 1 => x, 2 => y + 1, _ => y };
                    var along = dir < 2 ? y : x;
                    if (!lines.TryGetValue((dir, line, bottom, top), out var at))
                        lines[(dir, line, bottom, top)] = at = [];
                    at.Add(along);
                }
            }
        var faces = new List<DeckFace>();
        foreach (var ((dir, line, bottom, top), at) in lines)
        {
            at.Sort();
            var normal = dir switch { 0 => new Vector2(-1, 0), 1 => new Vector2(1, 0), 2 => new Vector2(0, -1), _ => new Vector2(0, 1) };
            var start = 0;
            for (var i = 1; i <= at.Count; i++)
            {
                if (i < at.Count && at[i] == at[i - 1] + 1)
                    continue;
                var (from, to) = (at[start], at[i - 1] + 1);
                faces.Add(dir < 2
                    ? new DeckFace(new Vector2(line, from), new Vector2(line, to), normal, bottom, top)
                    : new DeckFace(new Vector2(from, line), new Vector2(to, line), normal, bottom, top));
                start = i;
            }
        }
        return faces;
    }

    /// <summary>The 4 m blocks with any open cell in them; the floor under the walls never shows.</summary>
    private List<DeckFloor> FindFloors()
    {
        var floors = new List<DeckFloor>();
        for (var by = 0; by < Deck.Depth; by += Module)
            for (var bx = 0; bx < Deck.Width; bx += Module)
            {
                int open = 0, catwalk = 0;
                for (var y = by; y < Math.Min(by + Module, Deck.Depth); y++)
                    for (var x = bx; x < Math.Min(bx + Module, Deck.Width); x++)
                        if (IsOpen(x, y))
                        {
                            open++;
                            if (Deck.Cell(x, y) == DeckCell.Catwalk)
                                catwalk++;
                        }
                if (open > 0)
                    floors.Add(new DeckFloor(bx, by, catwalk * 4 >= open));
            }
        return floors;
    }

    /// <summary>
    /// The cells <paramref name="member"/> takes as rectangles, greedily: a run along x, grown north while the whole run
    /// goes on with the same <paramref name="key"/>.
    /// </summary>
    public List<DeckRect> Rects(Func<int, int, bool> member, Func<int, int, float> key)
    {
        var (w, d) = (Deck.Width, Deck.Depth);
        var taken = new bool[w * d];
        var rects = new List<DeckRect>();
        bool Free(int x, int y, float k) => !taken[y * w + x] && member(x, y) && key(x, y) == k;
        for (var y = 0; y < d; y++)
            for (var x = 0; x < w; x++)
            {
                if (!Free(x, y, key(x, y)))
                    continue;
                var k = key(x, y);
                var x1 = x + 1;
                while (x1 < w && Free(x1, y, k))
                    x1++;
                var y1 = y + 1;
                while (y1 < d && Enumerable.Range(x, x1 - x).All(a => Free(a, y1, k)))
                    y1++;
                for (var b = y; b < y1; b++)
                    for (var a = x; a < x1; a++)
                        taken[b * w + a] = true;
                rects.Add(new DeckRect(x, y, x1, y1));
            }
        return rects;
    }

    // ---- lamps ------------------------------------------------------------------------------------

    /// <summary>
    /// The lamps of each light group (LIGHTS): ceiling lamps over its zone on a grid, lit while the group is on, and red
    /// emergency lamps on the zone's walls, lit while it is on emergency. A group that starts off has both, dark.
    /// </summary>
    private List<DeckLamp> FindLamps()
    {
        var lamps = new List<DeckLamp>();
        foreach (var light in Deck.Lights)
        {
            if (Deck.Zone(light.Zone) is not { } zone)
                continue;
            var height = ZoneHeight(zone) ?? Height((int)zone.Centre().X, (int)zone.Centre().Y);
            var (min, max) = (zone.Min, zone.Max);
            var size = max - min;
            // Ceiling lamps hang under the ceiling, in tall rooms on drop cables at most 12 m up; they reach the floor.
            var nx = Math.Max(1, (int)MathF.Round(size.X / LampSpacing));
            var ny = Math.Max(1, (int)MathF.Round(size.Y / LampSpacing));
            var hang = Math.Min(height - 0.35f, 12f);
            var ceiling = new List<DeckLamp>();
            for (var j = 0; j < ny; j++)
                for (var i = 0; i < nx; i++)
                {
                    var at = new Vector2(min.X + (i + 0.5f) * size.X / nx, min.Y + (j + 0.5f) * size.Y / ny);
                    if (!IsOpen((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y)))
                        continue;
                    // The cone opens until its edge on the floor would reach floor behind a wall.
                    var reach = ClearReach(at, hang * MathF.Tan(MaxSpread * MathF.PI / 180));
                    var spread = Math.Clamp(MathF.Atan2(reach, hang) * 180 / MathF.PI, MinSpread, MaxSpread);
                    var range = hang / MathF.Cos(spread * MathF.PI / 180) + 1;
                    ceiling.Add(new DeckLamp(light.Name, DeckLampKind.Ceiling, new Vector3(at, hang), 0, range, spread, false));
                }
            if (height >= ShadowedRoom && ceiling.Count > 0)
            {
                // The middle lamp of a tall room casts shadows, which keep it from the rooms beside: it opens wide.
                var middle = ceiling.OrderBy(l => Vector2.DistanceSquared(new Vector2(l.Position.X, l.Position.Y), zone.Centre())).First();
                ceiling[ceiling.IndexOf(middle)] = middle with
                {
                    Shadowed = true, Spread = ShadowedSpread, Range = middle.Position.Z / MathF.Cos(ShadowedSpread * MathF.PI / 180) + 2,
                };
            }
            lamps.AddRange(ceiling);
            lamps.AddRange(WallLamps(light.Name, zone, height));
        }
        return lamps;
    }

    /// <summary>Red lamps on a zone's walls, about every <see cref="WallLampSpacing"/> metres of each side, where a wall is behind.</summary>
    private IEnumerable<DeckLamp> WallLamps(string group, DeckZone zone, float height)
    {
        var up = Math.Min(3f, height - 1);
        foreach (var (start, along, inward, length) in Sides(zone))
        {
            var count = Math.Max(1, (int)MathF.Round(length / WallLampSpacing));
            for (var i = 0; i < count; i++)
                if (OnWall(start, along, inward, (i + 0.5f) * length / count, length) is { } at)
                {
                    var lamp = at + inward * 0.12f;
                    // Shining into the room: only floor in front of its wall could be behind another.
                    var range = Math.Max(3, ClearReach(lamp, WallLampRange, inward));
                    yield return new DeckLamp(group, DeckLampKind.Wall, new Vector3(lamp, up), Degrees(inward), range, WallLampSpread, false);
                }
        }
    }

    /// <summary>How wide the ceiling lamps' cones open at least and at most, the shadowed lamps' and the wall lamps' (half angles, degrees).</summary>
    public const float MinSpread = 25, MaxSpread = 70, ShadowedSpread = 65, WallLampSpread = 60;

    /// <summary>How far a wall lamp reaches at most, metres.</summary>
    public const float WallLampRange = 14;

    /// <summary>
    /// How far from <paramref name="from"/> (at most <paramref name="limit"/> metres, along the floor) light goes before it
    /// would fall on open floor that a wall hides from it: the distance to the nearest such cell. With
    /// <paramref name="front"/>, only cells on that side count. Grates and cover let it through, as they let sight through.
    /// </summary>
    public float ClearReach(Vector2 from, float limit, Vector2? front = null)
    {
        var nearest = limit;
        var reach = (int)MathF.Ceiling(limit) + 1;
        for (var y = (int)MathF.Floor(from.Y) - reach; y <= (int)MathF.Floor(from.Y) + reach; y++)
            for (var x = (int)MathF.Floor(from.X) - reach; x <= (int)MathF.Floor(from.X) + reach; x++)
            {
                if (!IsOpen(x, y))
                    continue;
                var dx = Math.Max(Math.Max(x - from.X, 0), from.X - (x + 1));
                var dy = Math.Max(Math.Max(y - from.Y, 0), from.Y - (y + 1));
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance >= nearest)
                    continue;
                var centre = new Vector2(x + 0.5f, y + 0.5f);
                if (front is { } side && Vector2.Dot(centre - from, side) <= 0)
                    continue;
                if (WallBetween(from, centre))
                    nearest = distance;
            }
        return nearest;
    }

    /// <summary>Whether a wall cell lies on the straight line between two points of the floor.</summary>
    private bool WallBetween(Vector2 a, Vector2 b)
    {
        var steps = (int)MathF.Ceiling(Vector2.Distance(a, b) / 0.25f);
        for (var i = 1; i < steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            if (Deck.Cell((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)) == DeckCell.Wall)
                return true;
        }
        return false;
    }

    /// <summary>The four sides of a zone: where each starts, which way it runs, which way is into the zone, how long it is.</summary>
    private static IEnumerable<(Vector2 Start, Vector2 Along, Vector2 Inward, float Length)> Sides(DeckZone zone)
    {
        var (min, max) = (zone.Min, zone.Max);
        yield return (new Vector2(min.X, max.Y), Vector2.UnitX, -Vector2.UnitY, max.X - min.X);
        yield return (min, Vector2.UnitX, Vector2.UnitY, max.X - min.X);
        yield return (min, Vector2.UnitY, Vector2.UnitX, max.Y - min.Y);
        yield return (new Vector2(max.X, min.Y), Vector2.UnitY, -Vector2.UnitX, max.Y - min.Y);
    }

    /// <summary>
    /// A point on a zone's side, <paramref name="at"/> metres along it or as near as a wall allows (within 3 m), with open floor in
    /// front and wall behind; null when there is none.
    /// </summary>
    private Vector2? OnWall(Vector2 start, Vector2 along, Vector2 inward, float at, float length)
    {
        foreach (var shift in new[] { 0f, 1, -1, 2, -2, 3, -3 })
        {
            var t = at + shift;
            if (t < 0.5f || t > length - 0.5f)
                continue;
            var point = start + along * t;
            var inside = point + inward * 0.5f;
            var behind = point - inward * 0.5f;
            if (Deck.Cell((int)MathF.Floor(inside.X), (int)MathF.Floor(inside.Y)) is DeckCell.Floor or DeckCell.Cover or DeckCell.Catwalk or DeckCell.Duct
                && !IsOpen((int)MathF.Floor(behind.X), (int)MathF.Floor(behind.Y)))
                return point;
        }
        return null;
    }

    /// <summary>A direction as the deck's angle, degrees (0 east, 90 north).</summary>
    public static float Degrees(Vector2 direction) => MathF.Atan2(direction.Y, direction.X) * 180 / MathF.PI;

    // ---- set pieces ---------------------------------------------------------------------------------

    /// <summary>The set pieces markers of these tags stand for, on the floor at the marker.</summary>
    private static readonly Dictionary<string, string> FloorPieces = new(StringComparer.Ordinal)
    {
        ["clamp"] = "clamp",
        ["repair"] = "repair_station",
        ["pod"] = "pod_cradle",
    };

    /// <summary>The set pieces of markers beside high cover, which stand on that cover (a console, a terminal, the weapon rack).</summary>
    private static readonly Dictionary<string, string> CoverPieces = new(StringComparer.Ordinal)
    {
        ["terminal"] = "clamp_terminal",
        ["hack"] = "core_console",
        ["pickup"] = "weapon_rack",
    };

    /// <summary>
    /// The deck's dressing: the clamps, the repair station and the pods' cradles at their markers, consoles, terminals and the
    /// weapon rack on the cover beside their markers, panels by the doors to hack, crates on the rest of the cover, screens,
    /// switch plates; in each room a loudspeaker, pipes and pillars along its long walls, cables, and a crane in the pod bay.
    /// Wall fittings hang above the machines' heads or stand on cover, so nothing the sim lets a machine drive through is
    /// solid to the eye.
    /// </summary>
    private List<DeckPiece> Dress()
    {
        var pieces = new List<DeckPiece>();
        var claimed = new HashSet<DeckRect>();
        foreach (var marker in Deck.Markers)
        {
            var tag = marker.Tags.FirstOrDefault(t => FloorPieces.ContainsKey(t) || CoverPieces.ContainsKey(t) || t is "screen" or "switch");
            if (tag is null)
                continue;
            var at = marker.Position;
            if (FloorPieces.TryGetValue(tag, out var floorPiece))
            {
                var room = Height((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y));
                // The clamp's hoist meets the ceiling: stretched a little in taller rooms.
                var stretch = tag == "clamp" ? Math.Clamp(room / 14, 1, 1.3f) : 1;
                // The pod's cradle is a ring round the game's Pod at its own size (docs/story/deck/scale.md §6).
                var scale = tag == "pod" ? new Vector3(PodCradle, PodCradle / 2, PodCradle) : new Vector3(1, stretch, 1);
                pieces.Add(new DeckPiece(floorPiece, new Vector3(at, 0), marker.Angle, scale));
            }
            else if (tag == "screen")
                pieces.Add(new DeckPiece("wall_screen", new Vector3(at, 1.4f), marker.Angle, Vector3.One));
            else if (tag == "switch")
                pieces.Add(new DeckPiece("plate", new Vector3(at, 0), marker.Angle, new Vector3(3, 0.08f, 3)));
            else if (Covers.Where(c => !claimed.Contains(c)).OrderBy(c => Distance(c, at)).FirstOrDefault() is var cover
                     && cover.Width > 0 && Distance(cover, at) <= 4)
            {
                claimed.Add(cover);
                var prop = CoverPieces[tag];
                // A console wider than its cover is too much: a hack on a small block is a junction box.
                if (prop == "core_console" && Math.Max(cover.Width, cover.Depth) < 3)
                    prop = "junction_box";
                pieces.Add(new DeckPiece(prop, new Vector3(cover.Centre, 0), marker.Angle + 180, Fit(prop, cover)));
            }
            else if (NearestWall(at, 4) is { } wall)
                pieces.Add(new DeckPiece("junction_box", new Vector3(wall.Point + wall.Inward * 0.22f, 0.6f), Degrees(wall.Inward), Vector3.One));
        }
        foreach (var cover in Covers.Where(c => !claimed.Contains(c)))
            pieces.AddRange(Crates(cover));
        foreach (var stack in Stacks)
            pieces.AddRange(Stack(stack));
        foreach (var room in Rooms)
            pieces.AddRange(Fittings(room));
        pieces.AddRange(ValvePipes());
        return pieces;
    }

    /// <summary>
    /// The pipe from each valve (a marker with <c>far=</c>) to its far end, where its clang comes out (section 2's lures): a
    /// run along x, then one along y, under the ceiling (at most 5 m up), so the player can follow where a bang will be heard.
    /// </summary>
    private IEnumerable<DeckPiece> ValvePipes()
    {
        foreach (var marker in Deck.Markers)
        {
            var far = marker.Tags.FirstOrDefault(t => t.StartsWith("far=", StringComparison.Ordinal))?[4..];
            if (far is null || Machines.Sim.Story.StoryMarks.Locate(Deck, far) is not { } end)
                continue;
            var start = marker.Position;
            var corner = new Vector2(end.X, start.Y);
            var room = Height((int)MathF.Floor(start.X), (int)MathF.Floor(start.Y));
            var z = Math.Clamp(room - 1.5f, 2.5f, 5f);
            foreach (var (a, b) in new[] { (start, corner), (corner, end) })
            {
                var length = Vector2.Distance(a, b);
                if (length < 0.5f)
                    continue;
                var alongX = MathF.Abs(b.X - a.X) >= MathF.Abs(b.Y - a.Y);
                yield return new DeckPiece("pipe_run", new Vector3((a + b) / 2, z), alongX ? 90f : 0f, new Vector3(length / 4f, 1, 1));
            }
        }
    }

    /// <summary>The heavy frames round the blast doors, doors and gates (<see cref="DeckFrame"/>).</summary>
    public IReadOnlyList<DeckFrame> Frames { get; }

    /// <summary>How thick the ceiling's slab is over open cells this high, in kit_ceiling's own metre: thinner over ducts and low doorways.</summary>
    public static float CeilingSlab(float height) => height <= 4 ? 0.4f : 1;

    /// <summary>How far along a wall a frame's posts may reach beside a slot at most, metres.</summary>
    private const int FrameReach = 6;

    /// <summary>
    /// A heavy frame (kit_door_frame) round every blast door, door and grate gate, fitted so the way through looks as wide and
    /// as tall as the sim's slot: its posts in the wall beside the slot, its lintel over the ceiling's slab in the slot, its
    /// front a little proud of both faces. The posts reach no further along the wall than the wall goes, the lintel no
    /// higher than the lower room beside. It stays when its door opens, so the way through still reads as a doorway. Vents
    /// keep their own cover.
    /// </summary>
    private List<DeckFrame> FindFrames()
    {
        var frames = new List<DeckFrame>();
        foreach (var opening in Openings.Where(o => o.Kind is "blast" or "door" or "gate"))
        {
            var rect = opening.Rect;
            var (thick, centre) = (opening.AlongX ? rect.Depth : rect.Width, rect.Centre);
            var room = Math.Min(Beside(rect, opening.AlongX, -1), Beside(rect, opening.AlongX, 1));
            var open = opening.Height - CeilingSlab(opening.Height) - DeckFrame.Gap;
            var size = Math.Clamp(opening.Width / 8f, 1, 2);
            float Post(int side) => Math.Clamp((WallBeside(rect, opening.AlongX, side) - DeckFrame.Inset - 0.05f) / (DeckFrame.Outer - DeckFrame.Face), 0, size);
            var lintel = Math.Clamp((room - open - 0.05f) / (DeckFrame.Top - DeckFrame.KitLintel), 0, size);
            frames.Add(new DeckFrame(opening.Letter, centre, opening.AlongX ? 90 : 0, opening.Width / 2f, open, thick,
                Post(-1), Post(1), lintel));
        }
        return frames;
    }

    /// <summary>
    /// How many metres of wall run on beside a slot along its line, on the frame's own side <paramref name="side"/> (-1 its -x,
    /// 1 its +x: east and west of a slot along x, south and north of one along y), as deep as the slot, up to <see cref="FrameReach"/>.
    /// </summary>
    private int WallBeside(DeckRect rect, bool alongX, int side)
    {
        for (var step = 0; step < FrameReach; step++)
        {
            for (var across = alongX ? rect.Y0 : rect.X0; across < (alongX ? rect.Y1 : rect.X1); across++)
            {
                // The frame's +x is the deck's +x for a slot along x (turned 90°), the deck's -y for one along y (turned 0°).
                var (x, y) = alongX
                    ? (side > 0 ? rect.X1 + step : rect.X0 - 1 - step, across)
                    : (across, side > 0 ? rect.Y0 - 1 - step : rect.Y1 + step);
                if (IsOpen(x, y))
                    return step;
            }
        }
        return FrameReach;
    }

    /// <summary>The ceiling of the open cell just beyond a slot on one side (<paramref name="side"/> -1 or 1) of its way through.</summary>
    private float Beside(DeckRect rect, bool alongX, int side)
    {
        var centre = rect.Centre;
        var (x, y) = alongX
            ? ((int)MathF.Floor(centre.X), side < 0 ? rect.Y0 - 1 : rect.Y1)
            : (side < 0 ? rect.X0 - 1 : rect.X1, (int)MathF.Floor(centre.Y));
        return IsOpen(x, y) && Deck.Cell(x, y) is not (DeckCell.Door or DeckCell.Grate) ? Height(x, y) : DefaultHeight;
    }

    /// <summary>The alarm beacons (see <see cref="Beacons"/>).</summary>
    private List<DeckPiece> FindBeacons()
    {
        var beacons = new List<DeckPiece>();
        foreach (var room in Rooms)
        {
            var height = ZoneHeight(room) ?? DefaultHeight;
            if (height < 5)
                continue;
            var size = room.Max - room.Min;
            var sides = Sides(room).OrderByDescending(s => s.Length).ToList();
            var wanted = size.X * size.Y >= 300 ? 2 : 1;
            // The longest side at a quarter of its length, then the side across from it at three quarters; where a door or a
            // passage leaves no wall there, the next place that has one.
            var found = 0;
            foreach (var (side, share) in new[] { (0, 0.25f), (1, 0.75f), (0, 0.5f), (1, 0.5f), (2, 0.5f), (3, 0.5f), (0, 0.75f), (1, 0.25f) })
            {
                var (start, along, inward, length) = sides[side];
                if (found == wanted || found > 0 && side == 0 && wanted == 2)
                    continue;
                if (OnWall(start, along, inward, length * share, length) is not { } at)
                    continue;
                beacons.Add(new DeckPiece("alarm_beacon", new Vector3(at + inward * 0.55f, Math.Min(height - 1.3f, 4.6f)), Degrees(inward), Vector3.One));
                found++;
            }
        }
        return beacons;
    }

    /// <summary>How a cover piece is scaled to sit on its block: across its front along the block's long side.</summary>
    private static Vector3 Fit(string prop, DeckRect cover) => prop switch
    {
        "core_console" => new Vector3(Math.Max(cover.Width, cover.Depth) / 4f, 1, 1),
        _ => Vector3.One,
    };

    private static float Distance(DeckRect rect, Vector2 at)
    {
        var dx = Math.Max(Math.Max(rect.X0 - at.X, 0), at.X - rect.X1);
        var dy = Math.Max(Math.Max(rect.Y0 - at.Y, 0), at.Y - rect.Y1);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The point of the nearest wall face within <paramref name="reach"/> metres of a point, and the way out of it.</summary>
    private (Vector2 Point, Vector2 Inward)? NearestWall(Vector2 at, float reach)
    {
        (Vector2 Point, Vector2 Inward)? best = null;
        var bestDistance = reach;
        for (var y = (int)MathF.Floor(at.Y - reach); y <= (int)MathF.Floor(at.Y + reach); y++)
            for (var x = (int)MathF.Floor(at.X - reach); x <= (int)MathF.Floor(at.X + reach); x++)
            {
                if (IsOpen(x, y))
                    continue;
                foreach (var (dx, dy) in Directions)
                {
                    if (!IsOpen(x + dx, y + dy) || Deck.Cell(x + dx, y + dy) is DeckCell.Door or DeckCell.Grate)
                        continue;
                    // The face between the wall cell and its open neighbour, the point on it nearest.
                    var inward = new Vector2(dx, dy);
                    var centre = new Vector2(x + 0.5f, y + 0.5f) + inward * 0.5f;
                    var side = new Vector2(-dy, dx);
                    var t = Math.Clamp(Vector2.Dot(at - centre, side), -0.5f, 0.5f);
                    var point = centre + side * t;
                    var distance = Vector2.Distance(point, at);
                    if (distance < bestDistance)
                        (best, bestDistance) = ((point, inward), distance);
                }
            }
        return best;
    }

    /// <summary>Crates on a block of cover: large ones along its long side; in the low store rooms a barrel on top of some.</summary>
    private IEnumerable<DeckPiece> Crates(DeckRect cover)
    {
        var alongX = cover.Width >= cover.Depth;
        var (length, across) = alongX ? (cover.Width, cover.Depth) : (cover.Depth, cover.Width);
        var count = Math.Max(1, (int)MathF.Round(length / 3f));
        var low = Height(cover.X0, cover.Y0) <= 6;
        var facingAlong = alongX ? 90f : 0f;
        // A block a metre across holds a row of coolant drums; a long block now and then a generator; the rest crates.
        if (across <= 1)
        {
            var drums = Math.Max(1, (int)(length / 1.05f));
            for (var i = 0; i < drums; i++)
            {
                var t = (i + 0.5f) * length / drums;
                var at = alongX ? new Vector2(cover.X0 + t, cover.Centre.Y) : new Vector2(cover.Centre.X, cover.Y0 + t);
                yield return new DeckPiece("barrel", new Vector3(at, 0), Hash(cover.X0, cover.Y0, i) % 360, new Vector3(1.15f, 1.35f, 1.15f));
            }
            yield break;
        }
        if (length >= 4 && Hash(cover.X0, cover.Y0, 7) % 4 == 0)
        {
            yield return new DeckPiece("generator", new Vector3(cover.Centre, 0), facingAlong + (Hash(cover.Y0, cover.X0, 7) % 2 == 0 ? 0 : 180),
                new Vector3(Math.Min(length, 6) / 4.4f, 0.75f, across / 3.66f));
            yield break;
        }
        for (var i = 0; i < count; i++)
        {
            var t = (i + 0.5f) * length / count;
            var at = alongX ? new Vector2(cover.X0 + t, cover.Centre.Y) : new Vector2(cover.Centre.X, cover.Y0 + t);
            // Local x along the block's long side: facing north puts it along x, facing east along y.
            var facing = alongX ? 90f : 0f;
            var scale = new Vector3(length / (float)count / 3f, 1, across / 2f);
            yield return new DeckPiece("crate_large", new Vector3(at, 0), facing + (Hash(cover.X0, cover.Y0, i) % 2 == 0 ? 0 : 180), scale);
            if (low && Hash(cover.X0, cover.Y0, i) % 3 != 0)
                yield return new DeckPiece("barrel", new Vector3(at, 2.08f), Hash(cover.Y0, cover.X0, i) % 360, new Vector3(0.8f, 0.8f, 0.8f));
        }
    }

    /// <summary>A room's fittings: its loudspeaker, pipes and pillars along the long walls, a cable bundle, a crane in a hall.</summary>
    private IEnumerable<DeckPiece> Fittings(DeckZone room)
    {
        var height = ZoneHeight(room) ?? DefaultHeight;
        if (height < 5)
            yield break;
        var sides = Sides(room).ToList();
        // The loudspeaker high on the first side with a wall at its middle (north first).
        foreach (var (start, along, inward, length) in sides)
            if (OnWall(start, along, inward, length / 2, length) is { } at)
            {
                yield return new DeckPiece("pa_speaker", new Vector3(at + inward * 0.5f, Math.Min(height - 1.2f, 10f)), Degrees(inward), Vector3.One);
                break;
            }
        // Pipes and pillars in turn along the long sides of the larger rooms.
        var size = room.Max - room.Min;
        foreach (var (start, along, inward, length) in sides)
        {
            if (height < 6 || length < 12 || length < Math.Max(size.X, size.Y) - 0.01f)
                continue;
            for (var t = 4f; t + 4 <= length - 4; t += 12)
                if (WallBehind(start, along, inward, t, t + 4))
                    yield return new DeckPiece("pipe_run", new Vector3(start + along * (t + 2) + inward * 0.35f, Math.Min(height - 2.5f, 9f)), Degrees(inward), Vector3.One);
            if (height < 8)
                continue;
            for (var t = 10f; t <= length - 6; t += 12)
                if (WallBehind(start, along, inward, t - 0.5f, t + 0.5f))
                    yield return new DeckPiece("kit_pillar", new Vector3(start + along * t, 0), Degrees(inward), new Vector3(1, height / 8, 1));
        }
        // A bundle of cables hanging by a wall of a small room, where it clears a machine's head.
        if (height - 3.19f >= 7f && size.X * size.Y <= 2800)
        {
            var (start, along, inward, length) = sides.OrderByDescending(s => s.Length).First();
            if (OnWall(start, along, inward, length * 0.3f, length) is { } at)
                yield return new DeckPiece("cable_hang", new Vector3(at + inward * 0.6f, height - 3.19f), Degrees(inward), Vector3.One);
        }
        // A hall gets a gantry crane across it under the ceiling.
        if (height >= 44 && Math.Min(size.X, size.Y) >= 60)
        {
            var shortAcrossX = size.X < size.Y;
            var span = Math.Min(size.X, size.Y) - 2;
            yield return new DeckPiece("bay_crane", new Vector3(room.Centre(), height - 1 - 7.55f), shortAcrossX ? 90 : 0, new Vector3(span / 20, 1, 1));
        }
    }

    /// <summary>Whether the cells behind a side from <paramref name="from"/> to <paramref name="to"/> metres along it are all wall, with floor in front.</summary>
    private bool WallBehind(Vector2 start, Vector2 along, Vector2 inward, float from, float to)
    {
        for (var t = from + 0.5f; t < to; t += 1)
        {
            var point = start + along * t;
            var inside = point + inward * 0.5f;
            var behind = point - inward * 0.5f;
            if (IsOpen((int)MathF.Floor(behind.X), (int)MathF.Floor(behind.Y)) || !IsOpen((int)MathF.Floor(inside.X), (int)MathF.Floor(inside.Y)))
                return false;
        }
        return true;
    }

    /// <summary>A small hash for the dressing's variety, the same every time.</summary>
    private static int Hash(int a, int b, int c) => (int)((uint)(a * 73856093 ^ b * 19349663 ^ c * 83492791) % 10007);
}

/// <summary>A zone's middle.</summary>
public static class DeckZoneExtensions
{
    public static Vector2 Centre(this DeckZone zone) => (zone.Min + zone.Max) / 2;
}
