using System.Numerics;
using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>
/// How the trio's drives go in a story (the user's wish of stage 6: "langsamer, aber sprinten können"): L-7 and the Grunts
/// walk at <see cref="Walk"/> of their top speed, accelerate at that share of their acceleration (held to
/// <see cref="RampSeconds"/> from a standstill to their speed) and turn at
/// <see cref="Turn"/> of their rate, so they move heavily and carefully. Held, the sprint (Shift) gives them their whole top
/// speed, the speed they had before; it heats the drive (<see cref="Heat"/> fills in <see cref="FillSeconds"/>, cools in
/// <see cref="CoolSeconds"/>), and a full drive stutters (<see cref="StutterSeconds"/>) and will not sprint again until it has
/// cooled to <see cref="Resume"/>. The sprint is locked by the maintenance limiter until the story frees it
/// (<see cref="StoryMarks.SprintFree"/>, in the prologue as the Reapers' first rounds hit the core). Only the trio's heroes
/// have a drive; while the sprint is locked the ship's seeing hunters walk at <see cref="Walk"/> too (GameWorld.StoryPace),
/// so the dark decks keep their measure; every other machine of a story, and every machine of any other match, goes as ever.
/// </summary>
public sealed class StoryDrive
{
    /// <summary>The share of the top speed (and of the acceleration) the trio walks at.</summary>
    public const float Walk = 0.55f;

    /// <summary>
    /// The least time the trio's drives take from a standstill to their speed (walk or sprint alike): their acceleration
    /// is held to that, so they set off, stop and come off a sprint with some weight (GameWorld.StoryRate).
    /// </summary>
    public const float RampSeconds = 0.35f;

    /// <summary>The share of the turn rate the trio turns at, walking or sprinting.</summary>
    public const float Turn = 0.75f;

    /// <summary>Seconds of sprint that fill the heat from cold, and seconds a full drive takes to cool down when not sprinting.</summary>
    public const float FillSeconds = 7, CoolSeconds = 4.5f;

    /// <summary>The heat an overheated drive must cool to before it sprints again.</summary>
    public const float Resume = 1 / 3f;

    /// <summary>How long a drive that has just overheated stutters, and the beat of its stutter (seconds a pulse).</summary>
    public const float StutterSeconds = 1.2f, StutterBeat = 0.2f;

    /// <summary>The share of the walk a stuttering drive falls to on its off beats.</summary>
    public const float StutterShare = 0.3f;

    /// <summary>
    /// A computer's Grunt falls this far behind the Locator it follows before it sprints to catch up (with the sprint free),
    /// and sprints on until it is this near again.
    /// </summary>
    public const float CatchUpFrom = 12, CatchUpTo = 7;

    /// <summary>How full the drive is, 0 (cold) to 1 (overheated).</summary>
    public float Heat { get; set; }

    /// <summary>Overheated: no sprint until the heat is down to <see cref="Resume"/>.</summary>
    public bool Blocked { get; set; }

    /// <summary>The tick it overheated last (the stutter's start); -1 before.</summary>
    public int BlockedAt { get; set; } = -1;

    /// <summary>Its driver holds the sprint (the person's Shift, carried by the drive command).</summary>
    public bool Held { get; set; }

    /// <summary>A computer's Grunt catching up with the Locator (between <see cref="CatchUpFrom"/> and <see cref="CatchUpTo"/>).</summary>
    public bool CatchingUp { get; set; }

    /// <summary>Whether it sprints this tick: free, wanted, not blocked. Set at the end of each tick for the next one's moves.</summary>
    public bool Sprinting { get; set; }

    /// <summary>Where it stood at the end of the last tick, to tell whether it moved.</summary>
    public Vector2 Last { get; set; }

    /// <summary>
    /// The share of its top speed it goes at on <paramref name="tick"/>: the whole of it sprinting, <see cref="Walk"/> walking,
    /// and on the stutter's off beats less still.
    /// </summary>
    public float Pace(int tick)
    {
        if (Sprinting)
            return 1;
        if (Blocked && BlockedAt >= 0 && tick - BlockedAt < StutterTicks && (tick - BlockedAt) / BeatTicks % 2 == 1)
            return Walk * StutterShare;
        return Walk;
    }

    private static int StutterTicks => (int)MathF.Round(StutterSeconds * World.GameWorld.TicksPerSecond);

    private static int BeatTicks => Math.Max(1, (int)MathF.Round(StutterBeat * World.GameWorld.TicksPerSecond));

    internal void MixInto(WiredHash hash)
    {
        hash.Mix(BitConverter.SingleToInt32Bits(Heat));
        hash.Mix(Blocked ? 1 : 0);
        hash.Mix(BlockedAt);
        hash.Mix(Held ? 1 : 0);
        hash.Mix(CatchingUp ? 1 : 0);
        hash.Mix(Sprinting ? 1 : 0);
        hash.Mix(BitConverter.SingleToInt32Bits(Last.X));
        hash.Mix(BitConverter.SingleToInt32Bits(Last.Y));
    }
}

/// <summary>The trio's drives by hero (machine id), made as each hero takes the deck.</summary>
public sealed class StoryDrives
{
    public SortedDictionary<int, StoryDrive> Of { get; } = [];

    public StoryDrive For(int hero)
    {
        if (!Of.TryGetValue(hero, out var drive))
            Of[hero] = drive = new StoryDrive();
        return drive;
    }

    public long Fingerprint()
    {
        var hash = new WiredHash();
        foreach (var (id, drive) in Of)
        {
            hash.Mix(id);
            drive.MixInto(hash);
        }
        return hash.Value;
    }
}
