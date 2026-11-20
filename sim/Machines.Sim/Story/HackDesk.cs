using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// A hack under way or left off, at one target: its boards, the one being played, who is at it (null while nobody is) and
/// with which hero, and when the firewall steps next. A hack walked away from keeps its board and its trace-back, so it
/// goes on where it was left; one traced (the alarm) starts again on a new board.
/// </summary>
public sealed class HackSession(string target, HackKind kind)
{
    public string Target { get; } = target;

    public HackKind Kind { get; } = kind;

    public IReadOnlyList<HackSpec> Boards { get; } = HackSpec.Boards(kind);

    /// <summary>The board being played, from 0.</summary>
    public int Stage { get; internal set; }

    /// <summary>The board being played; null until the hack is first opened, and again after an alarm.</summary>
    public HackPuzzle? Puzzle { get; internal set; }

    /// <summary>The player at the board, or null while nobody is.</summary>
    public PlayerId? Hacker { get; internal set; }

    /// <summary>The hacking hero's actor id, while someone is at it.</summary>
    public int Hero { get; internal set; }

    /// <summary>The world's tick of the firewall's next step.</summary>
    public int NextPulse { get; internal set; }

    /// <summary>The story broke the hack off (the main computer's alarm at 90 %): the board stands, no turn goes in.</summary>
    public bool Broken { get; internal set; }

    /// <summary>How many times its trace-back ran full.</summary>
    public int Alarms { get; internal set; }

    public bool Open => Hacker is not null;

    /// <summary>How far the whole hack has come, its boards counted: 0 to 1.</summary>
    public float Progress => Math.Clamp((Stage + (Puzzle?.Progress ?? 0)) / Boards.Count, 0, 1);

    internal long Fingerprint()
    {
        var hash = new WiredHash();
        hash.Mix(Target);
        hash.Mix((int)Kind);
        hash.Mix(Stage);
        hash.Mix(Puzzle?.Fingerprint() ?? -1);
        hash.Mix(Hacker?.Value ?? -1);
        hash.Mix(Hero);
        hash.Mix(NextPulse);
        hash.Mix(Broken ? 1 : 0);
        hash.Mix(Alarms);
        return hash.Value;
    }
}

/// <summary>
/// The hacks of a story chapter (docs/STORY.md, Hacken): what can be hacked and what it is (the deck's markers by their
/// names and tags, or what the director registers), the hacks under way, those done, and the last alarm, which raises the
/// hacker's signature (plan WP-D). Kept in the <see cref="StoryRun"/>, changed only on the world's ticks and by the story's
/// hack commands, so co-op peers, recordings and saves agree.
/// </summary>
public sealed class HackDesk
{
    /// <summary>The prologue's targets by their deck markers' names (data/planets/prolog/prolog.deck).</summary>
    public static readonly IReadOnlyDictionary<string, HackKind> Prologue = new Dictionary<string, HackKind>(StringComparer.Ordinal)
    {
        ["clamp_l7"] = HackKind.Clamp,
        ["terminal_ike"] = HackKind.Grunt,
        ["terminal_bob"] = HackKind.Grunt,
        ["hack_s"] = HackKind.Door,
        ["hack_h"] = HackKind.Door,
        ["hack_d5"] = HackKind.Door,
        ["hack_core"] = HackKind.Core,
        ["pod_3_hatch"] = HackKind.Pod,
    };

    /// <summary>Targets the director added or changed, with where they are; they win over the deck's markers.</summary>
    public SortedDictionary<string, (HackKind Kind, Vector2 Position)> Registered { get; } = new(StringComparer.Ordinal);

    /// <summary>Targets the director has closed for now (a door already open, a Grunt not yet reachable).</summary>
    public SortedSet<string> Locked { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, HackSession> Sessions { get; } = new(StringComparer.Ordinal);

    /// <summary>The targets hacked, in the order of their names.</summary>
    public SortedSet<string> Done { get; } = new(StringComparer.Ordinal);

    /// <summary>The world's tick of the last alarm (a trace-back run full), -1 before any.</summary>
    public int LastAlarmTick { get; internal set; } = -1;

    /// <summary>Who set off the last alarm.</summary>
    public PlayerId? LastAlarmBy { get; internal set; }

    /// <summary>The hack a player is at, if any.</summary>
    public HackSession? OpenBy(PlayerId player) => Sessions.Values.FirstOrDefault(s => s.Hacker == player);

    public HackSession? Session(string target) => Sessions.GetValueOrDefault(target);

    /// <summary>What a target is and where, or null when it is none (registered first, then the prologue's markers on the deck).</summary>
    public (HackKind Kind, Vector2 Position)? Find(string target, Deck? deck)
    {
        if (Registered.TryGetValue(target, out var known))
            return known;
        if (deck is null || !Prologue.TryGetValue(target, out var kind) || deck.Markers.FirstOrDefault(m => m.Name == target) is not { } marker)
            return null;
        return (kind, marker.Position);
    }

    /// <summary>Every target there is to hack on the deck, with where it is: for the client's prompt and its scripted checks.</summary>
    public IEnumerable<(string Target, HackKind Kind, Vector2 Position)> Targets(Deck? deck)
    {
        var names = new SortedSet<string>(Registered.Keys, StringComparer.Ordinal);
        if (deck is not null)
            names.UnionWith(Prologue.Keys.Where(name => deck.Markers.Any(m => m.Name == name)));
        foreach (var name in names)
            if (Find(name, deck) is { } found)
                yield return (name, found.Kind, found.Position);
    }

    internal long Fingerprint()
    {
        var hash = new WiredHash();
        foreach (var (name, (kind, at)) in Registered)
        {
            hash.Mix(name);
            hash.Mix((int)kind);
            hash.Mix(BitConverter.SingleToInt32Bits(at.X));
            hash.Mix(BitConverter.SingleToInt32Bits(at.Y));
        }
        foreach (var name in Locked)
            hash.Mix(name);
        foreach (var session in Sessions.Values)
            hash.Mix(session.Fingerprint());
        foreach (var name in Done)
            hash.Mix(name);
        hash.Mix(LastAlarmTick);
        hash.Mix(LastAlarmBy?.Value ?? -1);
        return hash.Value;
    }
}
