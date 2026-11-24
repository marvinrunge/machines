using System.Numerics;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>What a member of the escape's pack is (design-escape §4): its leash and what the hazards do to it go by this.</summary>
public enum PackKind
{
    /// <summary>A one-armed Reaper (Reaper 2): the pack's fodder.</summary>
    Reaper2,

    /// <summary>A two-armed Reaper (Reaper 3).</summary>
    Reaper3,

    /// <summary>A big Grunt (Old Iron, G's HeavyGrunt): slow, a door breaker, shoves debris aside.</summary>
    Heavy,

    /// <summary>The Thresher: unkillable, staggered at most.</summary>
    Thresher,
}

/// <summary>
/// The escape's pack (package E, design-escape §4): the eight who burst from the freight lift, their roles and kinds; the
/// leash that keeps them 20–110 m of way behind the trio (written into the pace table every tick); the catch-up that sets a
/// straggler quietly at the last door behind the trio; the confirm-kill on a shut-down Locator; and what of their hits on
/// the trio and on each other the harm seam takes over (<see cref="Harm"/>). Everything changes on ticks and goes into
/// <see cref="Fingerprint"/> once the pack is out.
/// </summary>
public sealed class StoryPack
{
    /// <summary>The roles of the pack, as the director casts them: two two-armed Reapers, three one-armed, two big Grunts, the Thresher.</summary>
    public const string R3a = "pack_r3a", R3b = "pack_r3b", R2a = "pack_r2a", R2b = "pack_r2b", R2c = "pack_r2c";

    /// <summary>The pack's roles in id order of their kind, and what each is.</summary>
    public static readonly IReadOnlyList<(string Role, PackKind Kind)> Roster =
    [
        (R3a, PackKind.Reaper3), (R3b, PackKind.Reaper3), (R2a, PackKind.Reaper2), (R2b, PackKind.Reaper2), (R2c, PackKind.Reaper2),
        (StoryHeavy.PackRole1, PackKind.Heavy), (StoryHeavy.PackRole2, PackKind.Heavy), (Prologue.ThresherRole, PackKind.Thresher),
    ];

    /// <summary>The kind of a role of the pack, or null for one not in it.</summary>
    public static PackKind? KindOf(string role)
    {
        foreach (var (r, kind) in Roster)
            if (r == role)
                return kind;
        return null;
    }

    // ---- the leash (design-escape §4, metres of way to the rearmost hero; speeds in m/s) ----------------------

    /// <summary>The leash's bands: beyond <see cref="Far"/> the pack runs, within <see cref="Near"/> it never runs a hero down.</summary>
    public const float Far = 40, Near = 28, ThresherKeeps = 25;

    /// <summary>
    /// Within this many metres of a hero (or standing: held, jammed, staggered) a member of the pack sees and aims by the fair
    /// rule; running further off it is blind to the trio (design-escape §4: wild fire while running, aimed fire only from a
    /// stand or close by). The leash's <see cref="Near"/> keeps a running pack just outside it.
    /// </summary>
    public const float AimReach = 25;

    /// <summary>A straggler this far behind and out of every hero's sight for <see cref="CatchUpTicks"/> is set at the last door behind the trio.</summary>
    public const float CatchUp = 110, OutOfSight = 70;

    public const int CatchUpTicks = 120;

    /// <summary>How near a shut-down Locator the pack comes before it deletes it, metres; how fast it closes in, m/s.</summary>
    public const float ConfirmReach = 6, ConfirmSpeed = 2;

    /// <summary>The share of a pack member's hit points its own side's fire may take over the whole run.</summary>
    public const float FriendlyCap = 0.30f;

    /// <summary>The aimed rounds' shares of a hero's hit points (design-escape §5.2): a Reaper's plasma, a big Grunt's heavy bolter.</summary>
    public const float ReaperRound = 0.08f, HeavyRound = 0.10f, FriendlyRound = 0.05f;

    /// <summary>The speed a role of the pack runs at by the leash (m/s), with its way <paramref name="behind"/> the rearmost hero and that hero's speed.</summary>
    public static float LeashSpeed(PackKind kind, float behind, float heroSpeed, float top) => kind switch
    {
        PackKind.Thresher => behind < ThresherKeeps ? 0 : behind > Far ? 13 : 9.5f,
        PackKind.Heavy => behind < Near ? 4 : top,
        _ => behind > Far ? 13.5f : behind >= Near ? 9.5f : 0.9f * heroSpeed,
    };

    // ---- state ------------------------------------------------------------------------------------------------

    /// <summary>The pack's members by machine id, with their kind.</summary>
    public SortedDictionary<int, PackKind> Members { get; } = [];

    /// <summary>Members taken out of the show (left behind when the bridge fell): held, blind, far off.</summary>
    public SortedSet<int> Benched { get; } = [];

    /// <summary>How long each member has been a straggler out of sight, ticks.</summary>
    public SortedDictionary<int, int> Lost { get; } = [];

    /// <summary>The harm each member took from its own side's fire, hit points.</summary>
    public SortedDictionary<int, int> Friendly { get; } = [];

    /// <summary>A hazard's share being dealt right now (GameWorld.StoryHazards sets it around one hit), or null.</summary>
    public float? Pending { get; set; }

    /// <summary>Whether the pack goes round to door Y (after the bridge), and whether it is let through the collapse.</summary>
    public bool GoneRound { get; set; }

    /// <summary>The last distance of the Thresher the view was told (cue "thresher near"), metres; and his state.</summary>
    public int ToldNear { get; set; } = -1;

    public string ToldState { get; set; } = "";

    /// <summary>The ticks the pack has pressed on door Y unheld.</summary>
    public int YPressed { get; set; }

    public bool Any => Members.Count > 0;

    /// <summary>A fingerprint of what this part keeps, for the run's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        if (!Any)
            return hash.Value;
        foreach (var (id, kind) in Members)
        {
            hash.Mix(id);
            hash.Mix((int)kind);
        }
        foreach (var id in Benched)
            hash.Mix(id | 0x1000000);
        foreach (var (id, ticks) in Lost)
        {
            hash.Mix(id);
            hash.Mix(ticks);
        }
        foreach (var (id, harm) in Friendly)
        {
            hash.Mix(id);
            hash.Mix(harm);
        }
        hash.Mix(GoneRound ? 1 : 0);
        hash.Mix(ToldNear);
        hash.Mix(ToldState);
        hash.Mix(YPressed);
        return hash.Value;
    }

    /// <summary>
    /// The share of a hit of <paramref name="shooter"/> on <paramref name="target"/> this part takes over (GameWorld.StoryHarm);
    /// null: not its hit. A hazard's (<see cref="Pending"/>) on anyone; a member's aimed round on the trio (the Thresher's
    /// is the rule's, C4); a member's round on another member, until the friendly cap is spent (then a point).
    /// </summary>
    public float? Harm(Actor target, Actor? shooter)
    {
        if (Pending is { } hazard)
            return hazard;
        if (shooter is null || !Members.TryGetValue(shooter.Id, out var kind))
            return null;
        if (Members.ContainsKey(target.Id))
        {
            var taken = Friendly.GetValueOrDefault(target.Id);
            var cap = (int)(FriendlyCap * target.MaxHitPoints);
            var harm = Math.Min(Math.Max(1, (int)MathF.Ceiling(FriendlyRound * target.MaxHitPoints)), cap - taken);
            Friendly[target.Id] = taken + Math.Max(0, harm);
            return harm <= 0 ? 0 : FriendlyRound;
        }
        if (target is not Machine)
            return null;
        return kind switch
        {
            PackKind.Reaper2 or PackKind.Reaper3 => ReaperRound,
            PackKind.Heavy => HeavyRound,
            _ => null,
        };
    }

    // ---- the route (design-escape §2): way along the escape, metres from R13a ---------------------------------

    /// <summary>The escape's route through its rooms, the line the leash measures way along.</summary>
    public static readonly IReadOnlyList<Vector2> Route =
    [
        new(486, 228), new(520, 228), new(540, 224), new(540, 308), new(560, 308), new(560, 192), new(580, 192), new(580, 308),
        new(600, 308), new(600, 172), new(520, 172), new(520, 160), new(500, 160), new(496, 170), new(368, 170), new(358, 144), new(342, 144),
        new(340, 96), new(348, 92), new(430, 92), new(470, 92), new(566, 90),
    ];

    /// <summary>
    /// The freight lift and the Notgang below it down to the Kühlschlange's entry, where the pack comes out: a lead-in that joins
    /// the route at its second point. The first lane runs beside it behind a 4 m wall, so it is measured on its own.
    /// </summary>
    public static readonly (Vector2 From, Vector2 To) LiftLeg = (new(520, 282), new(520, 228));

    /// <summary>How far along the route a point is, metres (projected on its nearest stretch).</summary>
    public static float Progress(Vector2 p)
    {
        var best = float.MaxValue;
        var along = 0f;
        var walked = 0f;
        for (var i = 0; i + 1 < Route.Count; i++)
        {
            var (a, b) = (Route[i], Route[i + 1]);
            var ab = b - a;
            var length = ab.Length();
            var t = Math.Clamp(Vector2.Dot(p - a, ab) / (length * length), 0, 1);
            var d = Vector2.DistanceSquared(p, a + ab * t);
            if (d < best - 1e-3f)
                (best, along) = (d, walked + t * length);
            walked += length;
        }
        // The lift's leg joins the route at its second point, behind it: the pack in the lift is never taken for the lane past its wall.
        var (from, to) = LiftLeg;
        var leg = to - from;
        var legLength = leg.Length();
        var s = Math.Clamp(Vector2.Dot(p - from, leg) / (legLength * legLength), 0, 1);
        if (Vector2.DistanceSquared(p, from + leg * s) < best - 1e-3f)
            along = Vector2.Distance(Route[0], Route[1]) - (1 - s) * legLength;
        return along;
    }

    /// <summary>The point of the route so far along it, metres.</summary>
    public static Vector2 At(float progress)
    {
        var walked = 0f;
        for (var i = 0; i + 1 < Route.Count; i++)
        {
            var length = Vector2.Distance(Route[i], Route[i + 1]);
            if (progress <= walked + length)
                return Vector2.Lerp(Route[i], Route[i + 1], Math.Clamp((progress - walked) / length, 0, 1));
            walked += length;
        }
        return Route[^1];
    }

    /// <summary>The doors behind the trio a straggler is set at (the Kühlschlange's entry and turns, P, the shutter X's east side, the gallery's head), with their way along the route.</summary>
    public static readonly IReadOnlyList<Vector2> Gates = [new(540, 226), new(550, 308), new(570, 192), new(590, 308), new(600, 180), new(502, 165), new(366, 160), new(340, 132)];
}
