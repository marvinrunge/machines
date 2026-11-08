using System.Numerics;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>A rectangle of the hangar's stage, in the stage's own terms.</summary>
public readonly record struct HangarBox(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public HangarBox Grown(float by) => new(X - by, Y - by, Width + 2 * by, Height + 2 * by);

    public bool Meets(HangarBox other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>
/// Where everything stands on the hangar's stage. On a wide one the weapons and abilities stand in a column on each side of the
/// hero, on a narrow one, with the tree open beside it, in one column on the right; the modules stand in a row under them, and
/// the line of words and the test fire at the foot. Free of Godot, so the tests can compile it.
/// </summary>
public sealed record HangarLayout(HangarBox Plate, HangarBox Status, HangarBox Button, IReadOnlyDictionary<WiredSlot, HangarBox> Cards)
{
    private const float Pad = 14, Gap = 10, Foot = 50, WeaponHeight = 104, SmallHeight = 84, LeastHeight = 62, MostCard = 238, MostModule = 178, MostPlate = 330;
    private const float ButtonWidth = 170, ButtonHeight = 44, PlateHeight = 84;

    /// <summary>From this width of stage on there is room for a column on each side.</summary>
    public const float TwoColumns = 800;

    /// <summary>All that stands on the stage, which the hero has to stand clear of.</summary>
    public IEnumerable<HangarBox> Boxes => Cards.Values.Append(Plate).Append(Status).Append(Button);

    /// <summary>The stage for a chassis with a top mount or without, and so many modules.</summary>
    public static HangarLayout For(float width, float height, bool top, int modules)
    {
        var foot = height - Pad - Foot;
        var button = new HangarBox(width - Pad - ButtonWidth, foot + (Foot - ButtonHeight) / 2, ButtonWidth, ButtonHeight);
        var status = new HangarBox(Pad, foot, width - 2 * Pad - ButtonWidth - Gap, Foot);
        var cards = new Dictionary<WiredSlot, HangarBox>();
        var (row, rowEnd, plateFrom, plateTo) = (foot - Gap - SmallHeight, width - Pad, 0f, 0f);
        if (modules == 0)
            row = foot;
        if (width >= TwoColumns)
        {
            var card = Math.Min(MostCard, width * 0.25f);
            var right = width - Pad - card;
            var left = Pad + WeaponHeight + Gap;
            cards[WiredSlot.Weapon(Mounting.Right)] = new HangarBox(Pad, Pad, card, WeaponHeight);
            cards[WiredSlot.Weapon(Mounting.Left)] = new HangarBox(right, Pad, card, WeaponHeight);
            if (top)
                cards[WiredSlot.Weapon(Mounting.Top)] = new HangarBox(Pad, left, card, WeaponHeight);
            cards[WiredSlot.Ability(0)] = new HangarBox(right, left, card, SmallHeight);
            cards[WiredSlot.Ability(1)] = new HangarBox(right, left + SmallHeight + Gap, card, SmallHeight);
            (plateFrom, plateTo) = (Pad + card + Gap, right - Gap);
        }
        else
        {
            var card = Math.Clamp(width * 0.36f, 150, MostCard);
            var x = width - Pad - card;
            var stack = new List<WiredSlot> { WiredSlot.Weapon(Mounting.Right), WiredSlot.Weapon(Mounting.Left) };
            if (top)
                stack.Add(WiredSlot.Weapon(Mounting.Top));
            var weapons = stack.Count;
            stack.Add(WiredSlot.Ability(0));
            stack.Add(WiredSlot.Ability(1));
            var gaps = Gap * (stack.Count - 1);
            var room = row - Gap - Pad;
            // Lower cards first; if they still reach the modules, the row keeps to the left of the column.
            if (stack.Count * SmallHeight + gaps > room)
            {
                room = foot - Gap - Pad;
                rowEnd = x - Gap;
            }
            var (weapon, small) = (WeaponHeight, SmallHeight);
            if (weapons * WeaponHeight + 2 * SmallHeight + gaps > room)
                weapon = small = Math.Max(LeastHeight, Math.Min(SmallHeight, (room - gaps) / stack.Count));
            var y = Pad;
            for (var i = 0; i < stack.Count; i++)
            {
                var tall = i < weapons ? weapon : small;
                cards[stack[i]] = new HangarBox(x, y, card, tall);
                y += tall + Gap;
            }
            (plateFrom, plateTo) = (Pad, x - Gap);
        }
        var (rowStart, along) = (Pad, rowEnd - Pad);
        var each = modules == 0 ? 0 : Math.Min(MostModule, (along - Gap * (modules - 1)) / modules);
        var at = rowStart + (along - each * modules - Gap * Math.Max(0, modules - 1)) / 2;
        for (var i = 0; i < modules; i++)
        {
            cards[WiredSlot.Module(i)] = new HangarBox(at, row, each, SmallHeight);
            at += each + Gap;
        }
        var plate = Math.Min(MostPlate, plateTo - plateFrom);
        return new HangarLayout(new HangarBox(plateFrom + (plateTo - plateFrom - plate) / 2, Pad, plate, PlateHeight), status, button, cards);
    }
}

/// <summary>A slice of a hero between two heights over its disc, by how far it reaches from the axis it turns about.</summary>
public readonly record struct HeroBand(float Low, float High, float Radius);

/// <summary>
/// Where the camera stands for a hero: <paramref name="Distance"/> metres off, looking at <paramref name="Height"/> over the
/// disc, the picture moved by <paramref name="Across"/> and <paramref name="Up"/> as shares of the stage's height.
/// </summary>
public readonly record struct HangarShot(float Distance, float Height, float Across, float Up);

/// <summary>
/// Finds how big a hero can stand on the hangar's stage and where, clear of the cards round it. A hero is taken as what it
/// sweeps out in a whole turn, slices of it one on the other, so that it stays clear however it is turned; what is wide can
/// spread under the columns of cards while what is narrow rises between them. Free of Godot, so the tests can compile it.
/// Wired is the remake's own addition.
/// </summary>
public static class HangarFrame
{
    private const int Slices = 14, Turns = 8, Tries = 9;

    /// <summary>The distance between the places tried, in the stage's terms.</summary>
    private const float Step = 8;

    /// <summary>What stays free round the hero, and how much further off it stands than it could.</summary>
    private const float Clear = 5, Room = 1.03f;

    /// <summary>The hero's points as they stand on the disc, the axis through the origin, as slices.</summary>
    public static HeroBand[] Bands(IReadOnlyCollection<Vector3> points)
    {
        var top = Math.Max(points.Max(p => p.Y), 0.1f);
        var bands = new HeroBand[Slices];
        for (var i = 0; i < Slices; i++)
            bands[i] = new HeroBand(top * i / Slices, top * (i + 1) / Slices, 0);
        foreach (var point in points)
        {
            var at = Math.Clamp((int)(point.Y / top * Slices), 0, Slices - 1);
            var reach = MathF.Sqrt(point.X * point.X + point.Z * point.Z);
            if (reach > bands[at].Radius)
                bands[at] = bands[at] with { Radius = reach };
        }
        return bands;
    }

    /// <summary>The nearest shot of a hero that keeps it on the stage and clear of <paramref name="keepOff"/>; a stage with no room at all gets the farthest there is.</summary>
    public static HangarShot Fit(IReadOnlyList<HeroBand> bands, float width, float height, IEnumerable<HangarBox> keepOff, float fieldOfView, float elevation)
    {
        var boxes = keepOff.Select(b => b.Grown(Clear)).ToArray();
        var aim = bands[^1].High * 0.5f;
        var reach = Math.Max(bands.Max(b => b.Radius), aim);
        // From filling the stage's height to a tenth of it.
        var near = reach / MathF.Tan(fieldOfView * MathF.PI / 360) + reach;
        var (low, high) = (near, near * 10);
        for (var i = 0; i < Tries; i++)
        {
            var middle = MathF.Sqrt(low * high);
            if (Place(Slabs(bands, middle, aim, height, fieldOfView, elevation), width, height, boxes, firstFree: true) is null)
                low = middle;
            else
                high = middle;
        }
        var distance = high * Room;
        var (x, y) = Place(Slabs(bands, distance, aim, height, fieldOfView, elevation), width, height, boxes, firstFree: false) ?? (width / 2, height / 2);
        return new HangarShot(distance, aim, (x - width / 2) / height, (y - height / 2) / height);
    }

    /// <summary>What each slice takes up on a stage, seen from <paramref name="distance"/>, measured from the middle of the picture.</summary>
    private static HangarBox[] Slabs(IReadOnlyList<HeroBand> bands, float distance, float aim, float height, float fieldOfView, float elevation)
    {
        var (sin, cos) = MathF.SinCos(elevation);
        var scale = height / 2 / MathF.Tan(fieldOfView * MathF.PI / 360);
        var (eyeX, eyeY) = (cos * distance, aim + sin * distance);
        var slabs = new HangarBox[bands.Count];
        for (var i = 0; i < bands.Count; i++)
        {
            var (left, right, up, down) = (float.MaxValue, float.MinValue, float.MaxValue, float.MinValue);
            foreach (var level in new[] { bands[i].Low, bands[i].High })
            {
                for (var turn = 0; turn < Turns; turn++)
                {
                    var (along, across) = MathF.SinCos(turn * MathF.Tau / Turns);
                    var (x, z) = (bands[i].Radius * along, bands[i].Radius * across);
                    var (awayX, awayY) = (x - eyeX, level - eyeY);
                    var depth = -cos * awayX - sin * awayY;
                    var (screenX, screenY) = (-z / depth * scale, -(-sin * awayX + cos * awayY) / depth * scale);
                    (left, right, up, down) = (Math.Min(left, screenX), Math.Max(right, screenX), Math.Min(up, screenY), Math.Max(down, screenY));
                }
            }
            slabs[i] = new HangarBox(left, up, right - left, down - up);
        }
        return slabs;
    }

    /// <summary>Where on the stage the middle of the picture has to go for all the slabs to stand free, as far from everything as can be; with <paramref name="firstFree"/> only whether there is such a place.</summary>
    private static (float X, float Y)? Place(HangarBox[] slabs, float width, float height, HangarBox[] boxes, bool firstFree)
    {
        var (fromX, toX) = (-slabs.Min(s => s.X) + Clear, width - slabs.Max(s => s.Right) - Clear);
        var (fromY, toY) = (-slabs.Min(s => s.Y) + Clear, height - slabs.Max(s => s.Bottom) - Clear);
        if (fromX > toX || fromY > toY)
            return null;
        var (columns, rows) = ((int)((toX - fromX) / Step) + 1, (int)((toY - fromY) / Step) + 1);
        var blocked = new bool[columns, rows];
        foreach (var slab in slabs)
        {
            foreach (var box in boxes)
            {
                var (i0, i1) = (Cell(box.X - slab.Right - fromX, columns, up: true), Cell(box.Right - slab.X - fromX, columns, up: false));
                var (j0, j1) = (Cell(box.Y - slab.Bottom - fromY, rows, up: true), Cell(box.Bottom - slab.Y - fromY, rows, up: false));
                for (var i = i0; i <= i1; i++)
                    for (var j = j0; j <= j1; j++)
                        blocked[i, j] = true;
            }
        }
        var clearance = Clearance(blocked, columns, rows);
        var (best, widest) = (0, new List<(int I, int J)>());
        for (var i = 0; i < columns; i++)
        {
            for (var j = 0; j < rows; j++)
            {
                if (blocked[i, j])
                    continue;
                if (firstFree)
                    return (0, 0);
                if (clearance[i, j] > best)
                {
                    best = clearance[i, j];
                    widest.Clear();
                }
                if (clearance[i, j] == best)
                    widest.Add((i, j));
            }
        }
        if (widest.Count == 0)
            return null;
        // In a gap taller than it is wide all the places down its middle are as good: the one halfway along it.
        var (middleX, middleY) = (widest.Average(c => c.I), widest.Average(c => c.J));
        var chosen = widest.MinBy(c => (c.I - middleX) * (c.I - middleX) + (c.J - middleY) * (c.J - middleY));
        return (fromX + chosen.I * Step, fromY + chosen.J * Step);
    }

    private static int Cell(float at, int cells, bool up) => Math.Clamp((int)(up ? MathF.Ceiling(at / Step) : MathF.Floor(at / Step)), up ? 0 : -1, up ? cells : cells - 1);

    /// <summary>How many steps each free place lies from what is blocked or the end of the places, in all eight directions.</summary>
    private static int[,] Clearance(bool[,] blocked, int columns, int rows)
    {
        var far = new int[columns, rows];
        int At(int i, int j) => i < 0 || j < 0 || i >= columns || j >= rows ? 0 : far[i, j];
        for (var i = 0; i < columns; i++)
            for (var j = 0; j < rows; j++)
                far[i, j] = blocked[i, j] ? 0 : 1 + Math.Min(Math.Min(At(i - 1, j), At(i, j - 1)), Math.Min(At(i - 1, j - 1), At(i - 1, j + 1)));
        for (var i = columns - 1; i >= 0; i--)
            for (var j = rows - 1; j >= 0; j--)
                if (!blocked[i, j])
                    far[i, j] = Math.Min(far[i, j], 1 + Math.Min(Math.Min(At(i + 1, j), At(i, j + 1)), Math.Min(At(i + 1, j + 1), At(i + 1, j - 1))));
        return far;
    }
}
