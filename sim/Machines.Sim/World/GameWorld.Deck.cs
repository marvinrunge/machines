using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// A leaf of a deck door sliding open (<see cref="GameWorld.OpenDoor"/>): it left the map as an artefact the moment the door
/// began to open (so the way through counts as open for every plan, and the director sees the door open), but until
/// <see cref="Until"/> it still stands in the doorway as it slides into the wall, and nobody drives through it. Where it stands
/// at a game time is its closed place moved along <see cref="Slide"/> by <see cref="Share"/>; the view slides its model the same way.
/// </summary>
/// <param name="Leaf">The leaf's artefact id (Artefact.IdBase + its place in the scenario's list).</param>
/// <param name="Boundary">Its ground while closed, the artefact's boundary.</param>
/// <param name="Slide">How far and which way it slides to stand open, metres (along the door, into the wall beside it).</param>
public sealed record DoorSwing(char Letter, int Leaf, IReadOnlyList<Vector2> Boundary, Vector2 Slide, float From, float Until)
{
    /// <summary>How far open the leaf stands at a game time, 0 to 1: it starts slowly, runs and slows to its stop (smoothstep).</summary>
    public float Share(float time)
    {
        if (time <= From)
            return 0;
        if (time >= Until)
            return 1;
        var u = (time - From) / (Until - From);
        return u * u * (3 - 2 * u);
    }

    /// <summary>Whether a circle (a machine's middle and radius) meets the leaf where it stands at a game time.</summary>
    public bool Blocks(Vector2 point, float radius, float time) => Distance(point, time) < radius;

    /// <summary>
    /// How far a point is from the leaf where it stands at a game time, metres: 0 inside it, infinite once it stands open.
    /// </summary>
    public float Distance(Vector2 point, float time)
    {
        var share = Share(time);
        if (share >= 1)
            return float.PositiveInfinity;
        var p = point - Slide * share;
        // Inside the leaf's (convex) outline, or so far from its nearest edge.
        var inside = true;
        var sign = 0f;
        var nearest = float.MaxValue;
        for (var i = 0; i < Boundary.Count; i++)
        {
            var (a, b) = (Boundary[i], Boundary[(i + 1) % Boundary.Count]);
            var edge = b - a;
            var cross = edge.X * (p.Y - a.Y) - edge.Y * (p.X - a.X);
            if (cross != 0)
            {
                if (sign == 0)
                    sign = cross;
                else if (cross * sign < 0)
                    inside = false;
            }
            var length = edge.LengthSquared();
            var t = length > 0 ? Math.Clamp(Vector2.Dot(p - a, edge) / length, 0, 1) : 0;
            nearest = MathF.Min(nearest, Vector2.DistanceSquared(p, a + edge * t));
        }
        return inside ? 0 : MathF.Sqrt(nearest);
    }
}

/// <summary>
/// A ship deck's doors (plan WP-B): a closed door is an active artefact on its slot (tools/deck/deck.mjs writes them into
/// the scenario, tagged 1000 + 10 × the door's place in the deck's DOORS, the second leaf one more). Opening one takes it off
/// the map whole: its cells are free again, and the room machines need round them is measured anew. Its leaves then slide
/// into the walls over a second or two (<see cref="DoorSwing"/>), and only as far as they have slid can a machine pass.
/// </summary>
public sealed partial class GameWorld
{
    private readonly List<DoorSwing> _doorSwings = [];

    /// <summary>The deck doors' leaves sliding open now, in the order they began.</summary>
    public IReadOnlyList<DoorSwing> DoorSwings => _doorSwings;

    /// <summary>
    /// How long a deck door of a kind (DOORS: door, blast, gate, vent) takes to slide open, seconds: a Grunts' door a little
    /// over a second, the Reapers' blast doors and the gates slower, a vent's cover quicker.
    /// </summary>
    public static float SwingSeconds(string kind) => kind switch
    {
        "blast" => 2.2f,
        "gate" => 2f,
        "vent" => 0.9f,
        _ => 1.2f,
    };

    /// <summary>The slide of a door leaf (by its artefact id) while it is under way, or null.</summary>
    public DoorSwing? SwingOf(int leaf)
    {
        foreach (var swing in _doorSwings)
        {
            if (swing.Leaf == leaf)
                return swing;
        }
        return null;
    }

    /// <summary>The scenario tag of a deck door's first leaf (the second's is one more), or null if the deck has no such door.</summary>
    public static int? DoorTag(Deck deck, char letter)
    {
        for (var i = 0; i < deck.Doors.Count; i++)
        {
            if (deck.Doors[i].Letter == letter)
                return global::Machines.Import.Planets.DeckParser.DoorTagBase + 10 * i;
        }
        return null;
    }

    /// <summary>The artefacts standing in a deck door's slot (both leaves of a double door), in id order; none when it is open.</summary>
    public IReadOnlyList<Artefact> DoorLeaves(char letter) =>
        Deck is { } deck && DoorTag(deck, letter) is { } tag
            ? _artefacts.Values.Where(a => a.Tag == tag || a.Tag == tag + 1).OrderBy(a => a.Id).ToList()
            : [];

    /// <summary>Whether a deck door stands closed: an artefact still stands in its slot.</summary>
    public bool DoorClosed(char letter) => DoorLeaves(letter).Count > 0;

    /// <summary>
    /// Opens a deck door: its leaves are taken off the map (<see cref="RemoveArtefact"/>) and slide into the walls over
    /// <see cref="SwingSeconds"/> (<see cref="DoorSwings"/>); with <paramref name="instant"/> they are simply gone (a
    /// checkpoint's world, which has its doors open from the start). False when it was open already.
    /// </summary>
    public bool OpenDoor(char letter, bool instant = false)
    {
        var leaves = DoorLeaves(letter);
        if (!instant && leaves.Count > 0)
        {
            var seconds = SwingSeconds(Deck!.Doors.First(d => d.Letter == letter).Kind);
            var middle = leaves.Aggregate(Vector2.Zero, (sum, leaf) => sum + leaf.Position) / leaves.Count;
            foreach (var leaf in leaves)
            {
                var along = LeafAlong(leaf, middle, leaves.Count);
                var (low, high) = (float.MaxValue, float.MinValue);
                foreach (var corner in leaf.Boundary)
                {
                    var at = Vector2.Dot(corner, along);
                    (low, high) = (MathF.Min(low, at), MathF.Max(high, at));
                }
                _doorSwings.Add(new DoorSwing(letter, leaf.Id, leaf.Boundary.ToList(), along * (high - low), Time, Time + seconds));
            }
        }
        foreach (var leaf in leaves)
            RemoveArtefact(leaf.Id);
        return leaves.Count > 0;
    }

    /// <summary>
    /// The way a leaf slides open: a leaf of a double door away from the door's middle, a door of one leaf (a vent's cover)
    /// along its long side.
    /// </summary>
    private static Vector2 LeafAlong(Artefact leaf, Vector2 middle, int leaves)
    {
        var away = leaf.Position - middle;
        if (leaves > 1 && away.LengthSquared() > 1e-6f)
            return Vector2.Normalize(away);
        var outline = leaf.Boundary;
        if (outline.Count < 3)
            return Vector2.UnitX;
        var (first, second) = (outline[1] - outline[0], outline[2] - outline[1]);
        var side = first.LengthSquared() >= second.LengthSquared() ? first : second;
        return side.LengthSquared() > 1e-6f ? Vector2.Normalize(side) : Vector2.UnitX;
    }

    /// <summary>The leaves whose slide is over stand open for good.</summary>
    private void SettleDoorSwings()
    {
        if (_doorSwings.Count > 0)
            _doorSwings.RemoveAll(s => Time >= s.Until);
    }

    /// <summary>
    /// Whether a sliding door leaf stands where a machine's step from <paramref name="from"/> would take it: the machine
    /// waits before it until the leaf has slid far enough. One that touches a leaf already (it stood pressed against the
    /// closed door) may move off it, but not further into it.
    /// </summary>
    internal bool LeafInWay(Machine machine, Vector2 from, Vector2 to)
    {
        if (_doorSwings.Count == 0)
            return false;
        foreach (var swing in _doorSwings)
        {
            // Nearer to it than its radius, and nearer than it was: one that touches it already may back off, not press on.
            var near = swing.Distance(to, Time);
            if (near < machine.Radius && near < swing.Distance(from, Time))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Takes an artefact off the map whole (a door that opens, a crate carried away): no wreckage, no blast, no linked damage.
    /// Its ground is free again and nothing on a line meets it any more. False when there is no such artefact.
    /// </summary>
    public bool RemoveArtefact(int id)
    {
        if (!_artefacts.Remove(id, out var artefact))
            return false;
        if (artefact.Boundary.Count >= 3)
            Nav.SetArea(artefact.Boundary, present: false);
        // What stands on a line is filed by count; one gone and another set down in the same tick would leave it stale.
        _artefactsFiled = -1;
        Raise(new GameEvent(GameEventKind.ArtefactRemoved, artefact.Id, artefact.Position, artefact.Tag));
        return true;
    }
}
