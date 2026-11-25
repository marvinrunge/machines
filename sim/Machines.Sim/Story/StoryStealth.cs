using System.Numerics;
using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>The Thresher's guard at a post (<see cref="Then.Guard"/>): where, and since when it holds it (the sweep and the look back count from then).</summary>
public sealed record StoryGuard(string Place, float Since);

/// <summary>A scripted look (<see cref="Then.Look"/>): until when it stands looking along a heading (radians).</summary>
public sealed record StoryLook(float Until, float Heading);

/// <summary>A valve's clang on its way along the pipe: when it comes out at the far end, which source it came from, where it sounds, how loud.</summary>
public sealed record StoryPipe(float Due, string Source, string Far, float Loud);

/// <summary>What a computer's Grunt keeps of its sneaking (CompanionBot.Stealth), by seat.</summary>
public sealed class StoryBotStealth
{
    /// <summary>Since when it stands frozen (the game time, -1 when it does not); since when no danger was near while frozen.</summary>
    public float FrozenAt { get; set; } = -1;
    public float ClearAt { get; set; } = -1;

    /// <summary>It holds before an exposed lane, waiting for the window (the crossing discipline), since when (-1: it does not).</summary>
    public bool Holding { get; set; }
    public float HoldSince { get; set; } = -1;

    /// <summary>A source it is sent to bang (the Locator's Distract on a valve, the director's <see cref="Then.BangBy"/>), null for none.</summary>
    public string? Bang { get; set; }

    /// <summary>Where it goes back to after the bang (its wait place).</summary>
    public Vector2 Back { get; set; }

    /// <summary>Where the director sends it the sneaking way (<see cref="Then.SneakTo"/>: the niche, the E-Nische, S, its wait place); null: it follows.</summary>
    public string? Goal { get; set; }

    /// <summary>It said its lane line already (P_IKE_LANE), or its freeze line (once each).</summary>
    public bool SaidLane { get; set; }

    internal void MixInto(WiredHash hash)
    {
        hash.Mix(BitConverter.SingleToInt32Bits(FrozenAt));
        hash.Mix(BitConverter.SingleToInt32Bits(ClearAt));
        hash.Mix(Holding ? 1 : 0);
        hash.Mix(BitConverter.SingleToInt32Bits(HoldSince));
        hash.Mix(Bang ?? "");
        hash.Mix(BitConverter.SingleToInt32Bits(Back.X));
        hash.Mix(BitConverter.SingleToInt32Bits(Back.Y));
        hash.Mix(Goal ?? "");
        hash.Mix(SaidLane ? 1 : 0);
    }
}

/// <summary>
/// Section 2's stealth (plan stufe7 §4 S, design-stealth): the Thresher's rounds, guard, scripted looks and rush, the lures
/// with their escalation, the healing of what he did, the offscreen grace and the computer's Grunts' sneaking. The sight's
/// own state (suspicion, dwells) is <see cref="StorySight"/>'s; this is what section 2 adds. Changed only on ticks and by
/// commands; everything here goes into <see cref="Fingerprint"/>, and an empty one hashes as stage 7's foundation left it.
/// </summary>
public sealed class StoryStealth
{
    /// <summary>
    /// The Thresher's rounds of section 2 (design-stealth §3.6), by name: places as <see cref="StoryMarks.Locate"/> reads
    /// them. "a" is deck A's lane with its four dwells; "ring" both decks by the West and East links, dwelling at F's grate
    /// and at S's grate windows; "b" the oval round deck B's island.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Rounds = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["a"] = ["patrol_a_1", "patrol_a_2", "patrol_a_3", "patrol_a_4"],
        ["ring"] = ["136,52", "patrol_ring_r5", "128,151", "256,151", "patrol_ring_r2", "256,52"],
        ["b"] = ["patrol_b_1", "patrol_b_2", "patrol_b_3", "patrol_b_4"],
    };

    /// <summary>The round each seeker is on (by actor id), and how many times it has gone all round it.</summary>
    public SortedDictionary<int, string> RoundOf { get; } = [];
    public SortedDictionary<int, int> Laps { get; } = [];

    /// <summary>A round a seeker takes up at the next point it reaches (<see cref="Then.Round"/>), by actor id.</summary>
    public SortedDictionary<int, (string Round, int From)> NextRound { get; } = [];

    /// <summary>The seekers holding a guard post, by actor id.</summary>
    public SortedDictionary<int, StoryGuard> Guards { get; } = [];

    /// <summary>Scripted looks, by actor id.</summary>
    public SortedDictionary<int, StoryLook> Looks { get; } = [];

    /// <summary>The seekers on a scripted rush (<see cref="Then.Rush"/>): they go at the hunt's pace until they are there.</summary>
    public SortedSet<int> Rushing { get; } = [];

    /// <summary>When each noise source was banged within <see cref="StorySight.Escalate"/> seconds, by its marker's name.</summary>
    public SortedDictionary<string, List<float>> Bangs { get; } = new(StringComparer.Ordinal);

    /// <summary>The sources he has fired into: spent, they make no more noise.</summary>
    public SortedSet<string> Spent { get; } = new(StringComparer.Ordinal);

    /// <summary>The valves' clangs on their way to the far ends, in the order they were banged.</summary>
    public List<StoryPipe> Pipes { get; } = [];

    /// <summary>The seats (by value) the director keeps out of his sight (<see cref="Then.Unseen"/>), whoever plays them.</summary>
    public SortedSet<int> Unseen { get; } = [];

    /// <summary>The hit points each hero (by actor id) had before his first hit since he last healed it.</summary>
    public SortedDictionary<int, int> HealTo { get; } = [];

    /// <summary>When he last saw or hit each hero, by actor id.</summary>
    public SortedDictionary<int, float> ThreatAt { get; } = [];

    /// <summary>The seats (by value) that lay down last tick: one that gets up chirps (<see cref="StorySight.RebootLoud"/>).</summary>
    public SortedSet<int> Down { get; } = [];

    /// <summary>The computer's Grunts (seat values) that creep, set by CompanionBot.Stealth for their drives.</summary>
    public SortedSet<int> Creep { get; } = [];

    /// <summary>What each computer's Grunt keeps of its sneaking, by seat value.</summary>
    public SortedDictionary<int, StoryBotStealth> Bots { get; } = [];

    public StoryBotStealth Bot(int seat)
    {
        if (!Bots.TryGetValue(seat, out var bot))
            Bots[seat] = bot = new StoryBotStealth();
        return bot;
    }

    /// <summary>How often a source was banged within the escalation's window up to <paramref name="now"/> (this bang included once noted).</summary>
    public int BangsAt(string source, float now) =>
        Bangs.TryGetValue(source, out var times) ? times.Count(t => now - t < StorySight.Escalate) : 0;

    /// <summary>A fingerprint of what this part keeps, for the run's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        foreach (var (id, round) in RoundOf)
        {
            hash.Mix(id);
            hash.Mix(round);
            hash.Mix(Laps.GetValueOrDefault(id));
        }
        foreach (var (id, (round, from)) in NextRound)
        {
            hash.Mix(id | 0x10000);
            hash.Mix(round);
            hash.Mix(from);
        }
        foreach (var (id, guard) in Guards)
        {
            hash.Mix(id | 0x20000);
            hash.Mix(guard.Place);
            hash.Mix(BitConverter.SingleToInt32Bits(guard.Since));
        }
        foreach (var (id, look) in Looks)
        {
            hash.Mix(id | 0x30000);
            hash.Mix(BitConverter.SingleToInt32Bits(look.Until));
            hash.Mix(BitConverter.SingleToInt32Bits(look.Heading));
        }
        foreach (var id in Rushing)
            hash.Mix(id | 0x40000);
        foreach (var (source, times) in Bangs)
        {
            hash.Mix(source);
            foreach (var time in times)
                hash.Mix(BitConverter.SingleToInt32Bits(time));
        }
        foreach (var source in Spent)
            hash.Mix("spent " + source);
        foreach (var pipe in Pipes)
        {
            hash.Mix(BitConverter.SingleToInt32Bits(pipe.Due));
            hash.Mix(pipe.Source);
            hash.Mix(pipe.Far);
            hash.Mix(BitConverter.SingleToInt32Bits(pipe.Loud));
        }
        foreach (var seat in Unseen)
            hash.Mix(seat | 0x50000);
        foreach (var (id, hp) in HealTo)
        {
            hash.Mix(id | 0x60000);
            hash.Mix(hp);
        }
        foreach (var (id, at) in ThreatAt)
        {
            hash.Mix(id | 0x70000);
            hash.Mix(BitConverter.SingleToInt32Bits(at));
        }
        foreach (var seat in Down)
            hash.Mix(seat | 0x80000);
        foreach (var seat in Creep)
            hash.Mix(seat | 0x90000);
        foreach (var (seat, bot) in Bots)
        {
            hash.Mix(seat | 0xA0000);
            bot.MixInto(hash);
        }
        return hash.Value;
    }
}
