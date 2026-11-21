using System.Numerics;
using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>What a seeker of the ship's makes of a hero this tick (GameWorld.StorySight).</summary>
public enum StoryGlimpse
{
    /// <summary>Nothing: too far, behind a wall, out of its cone or in the dark.</summary>
    None,
    /// <summary>Heard, not seen: the hero's signature carried to it. It turns that way, suspicious, but cannot aim at it.</summary>
    Heard,
    /// <summary>Seen: in its sight and lit (by its beam, a lit room, its own lamp), or close enough to touch.</summary>
    Seen,
}

/// <summary>What a seeker's suspicion came to this tick, for the director to turn into the story's events.</summary>
public enum StorySightKind
{
    /// <summary>Something caught its eye or ear: it stops and turns that way (suspicion crossed <see cref="StorySight.WaryAt"/>).</summary>
    Suspicious,
    /// <summary>Its suspicion ran full with the hero in sight: it hunts it.</summary>
    Spotted,
    /// <summary>The hunt lost sight of its quarry for <see cref="StorySight.LoseAfter"/> seconds: it goes to look where it was last seen.</summary>
    Lost,
    /// <summary>Its suspicion ran full on a sound alone: it goes to look where it came from.</summary>
    Searching,
    /// <summary>It found nothing, or nothing more: its suspicion has gone, or its search is over and it takes up its round again.</summary>
    Calm,
}

/// <summary>
/// One thing a seeker's suspicion came to on the world's <paramref name="Tick"/>: who (<paramref name="Seeker"/>, an actor id), of which hero (an actor id,
/// 0 when none), where it looks, and how suspicious it is now (0 to 1).
/// </summary>
public sealed record StorySightNews(int Tick, StorySightKind Kind, int Seeker, int Hero, Vector2 Where, float Suspicion);

/// <summary>
/// A machine of the ship's that looks for the trio, and how far it has got: its suspicion of the hero it noticed, where it
/// noticed it, and its patrol's dwell. What sort of seeker it is the director may change: a searchlight (sees only in its
/// beam's cone, but far along it: the Thresher), blind (switched off in a clamp row), unkillable (the Thresher in the prologue).
/// </summary>
public sealed class StorySeeker(int id)
{
    public int Id { get; } = id;

    /// <summary>It looks along a searchlight: only inside its cone, but its beam lights what it falls on.</summary>
    public bool Searchlight { get; set; }

    /// <summary>It sees and hears nothing: a machine switched off in its clamp.</summary>
    public bool Blind { get; set; }

    /// <summary>Nothing hurts it (the prologue's Thresher: one must not meet it, and one cannot beat it).</summary>
    public bool Unkillable { get; set; }

    /// <summary>How sure it is that something is there, 0 to 1. At 1 it hunts what it sees, or searches where it heard it.</summary>
    public float Suspicion { get; set; }

    /// <summary>The hero it noticed last (an actor id, 0 for none).</summary>
    public int Quarry { get; set; }

    /// <summary>Where it saw or heard that hero last.</summary>
    public Vector2 Spot { get; set; }

    /// <summary>The game time it last saw its quarry (heard is not seen), -1 for never.</summary>
    public float SeenAt { get; set; } = -1;

    /// <summary>It has said it is suspicious and has not yet calmed down (so it says so once).</summary>
    public bool Wary { get; set; }

    /// <summary>The heroes it sees this tick, by actor id: whom it may aim at, once it has spotted them.</summary>
    public SortedSet<int> Sees { get; } = [];

    /// <summary>Its patrol's dwell: until when it stands sweeping its beam, about which heading (radians), at which point of its round.</summary>
    public float DwellUntil { get; set; }
    public float DwellFacing { get; set; }
    public int DwelledAt { get; set; } = -1;

    /// <summary>The heading (degrees, 0 east, 90 north) it sweeps about at a dwell point of its round, by the point's index; the facing it arrived with otherwise.</summary>
    public SortedDictionary<int, float> DwellAngles { get; } = [];

    internal long Fingerprint()
    {
        var hash = new WiredHash();
        hash.Mix(Id);
        hash.Mix((Searchlight ? 1 : 0) | (Blind ? 2 : 0) | (Unkillable ? 4 : 0) | (Wary ? 8 : 0));
        hash.Mix(BitConverter.SingleToInt32Bits(Suspicion));
        hash.Mix(Quarry);
        hash.Mix(BitConverter.SingleToInt32Bits(Spot.X));
        hash.Mix(BitConverter.SingleToInt32Bits(Spot.Y));
        hash.Mix(BitConverter.SingleToInt32Bits(SeenAt));
        foreach (var hero in Sees)
            hash.Mix(hero);
        hash.Mix(BitConverter.SingleToInt32Bits(DwellUntil));
        hash.Mix(BitConverter.SingleToInt32Bits(DwellFacing));
        hash.Mix(DwelledAt);
        foreach (var (index, angle) in DwellAngles)
        {
            hash.Mix(index);
            hash.Mix(BitConverter.SingleToInt32Bits(angle));
        }
        return hash.Value;
    }
}

/// <summary>
/// The story's stealth (docs/STORY.md, Horror-Level; plan WP-D): who of the ship's looks for the trio and how sure it is,
/// how loud and visible each hero is (its signature), whose lamp shines, and how each of the deck's light groups is lit.
/// The lamps and lights are the sim's, so the signature and the seekers' eyes go by them and every client shows the same;
/// the director switches them (GameWorld.SetStoryLamp, SetStoryLights). Kept in the <see cref="StoryRun"/>, changed only on
/// the world's ticks and by commands, in the checksum.
/// </summary>
public sealed class StorySight
{
    // ---- the rule's numbers -----------------------------------------------------------------------

    /// <summary>A searchlight's cone: half its angle (degrees) and how far its beam lights what it falls on (metres).</summary>
    public const float BeamHalfAngle = 25, BeamLength = 40;

    /// <summary>How near a hero may come before it is noticed whatever the light, metres; its signature widens it (× 1 + signature).</summary>
    public const float Touch = 6;

    /// <summary>How far a lit room's dim emergency light shows a hero, metres.</summary>
    public const float Dim = 15;

    /// <summary>How far a hero is heard: metres per point of signature; through a wall half as far.</summary>
    public const float Hearing = 30;

    /// <summary>How fast suspicion rises, per second, on a hero seen or only heard; how fast it falls with nothing.</summary>
    public const float RiseSeen = 1.5f, RiseHeard = 0.5f, Fall = 0.2f;

    /// <summary>The suspicion at which a seeker stops and turns to what it noticed.</summary>
    public const float WaryAt = 0.25f;

    /// <summary>Seconds a hunt goes on without seeing its quarry before it is lost.</summary>
    public const float LoseAfter = 4;

    /// <summary>Seconds a seeker looks about where it lost or heard a hero (at least this long once there).</summary>
    public const float SearchFor = 15, SweepAtLeast = 6;

    /// <summary>A patrol's dwell: seconds it stands, and how far its beam sweeps either way, degrees.</summary>
    public const float DwellTime = 3, DwellSweep = 40;

    /// <summary>A search's sweep either way, degrees, and its period, seconds.</summary>
    public const float SearchSweep = 60, SearchPeriod = 4;

    /// <summary>A hero's signature still and dark; what moving at full speed, the lamp, a shot (3 s), the scanner's pulse (2 s) and a hack's alarm (5 s) add.</summary>
    public const float Still = 0.05f, Moving = 0.5f, Lamp = 0.4f, Shot = 1, Pulse = 1, Traced = 1;

    /// <summary>How long a shot, the pulse and a hack's alarm stay in the signature, seconds.</summary>
    public const float ShotFor = 3, PulseFor = 2, TracedFor = 5;

    /// <summary>What wheels do to the moving part of a signature: the Grunts are loud.</summary>
    public const float Wheels = 2;

    // ---- the state ------------------------------------------------------------------------------------

    /// <summary>The seekers, by actor id: every fighting machine of the ship's, from its first tick, and any the director names.</summary>
    public SortedDictionary<int, StorySeeker> Seekers { get; } = [];

    /// <summary>Each hero's signature this tick, by actor id: 0.05 still and dark, more moving, with its lamp on, after a shot.</summary>
    public SortedDictionary<int, float> Signatures { get; } = [];

    /// <summary>Where each hero stood last tick, by actor id: how fast it moves.</summary>
    public SortedDictionary<int, Vector2> Last { get; } = [];

    /// <summary>The machines whose lamp the story switched (on or off), by actor id; the rest go by <c>GameWorld.StoryLampOn</c>'s rule.</summary>
    public SortedDictionary<int, bool> Lamps { get; } = [];

    /// <summary>The light groups the story switched (LIGHTS of the deck): "on", "off" or "emergency"; the rest are as the deck starts them.</summary>
    public SortedDictionary<string, string> Lights { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// What the seekers' suspicion came to this tick: a search that ended as its machine was moved, then the sight's update
    /// in the seekers' id order. The director reads it after that update (GameWorld.UpdateStory) and turns it into the
    /// story's events (stinger, ticker, music). What an earlier tick said is dropped at the next update; not in the
    /// checksum, as it only says what the state already holds.
    /// </summary>
    public List<StorySightNews> News { get; } = [];

    internal long Fingerprint()
    {
        var hash = new WiredHash();
        foreach (var seeker in Seekers.Values)
            hash.Mix(seeker.Fingerprint());
        foreach (var (id, signature) in Signatures)
        {
            hash.Mix(id);
            hash.Mix(BitConverter.SingleToInt32Bits(signature));
        }
        foreach (var (id, at) in Last)
        {
            hash.Mix(id);
            hash.Mix(BitConverter.SingleToInt32Bits(at.X));
            hash.Mix(BitConverter.SingleToInt32Bits(at.Y));
        }
        foreach (var (id, on) in Lamps)
        {
            hash.Mix(id);
            hash.Mix(on ? 1 : 0);
        }
        foreach (var (group, state) in Lights)
        {
            hash.Mix(group);
            hash.Mix(state);
        }
        return hash.Value;
    }
}
