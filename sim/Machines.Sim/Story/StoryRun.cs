using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>How a story chapter came to its end.</summary>
public enum StoryEnding
{
    /// <summary>The Locator fell: the chapter starts again from its last checkpoint.</summary>
    LocatorLost,
}

/// <summary>
/// A chapter of the story under way: where it stands (its section and checkpoint), what the director has set (flags and
/// timers) and who sits where. The heroes themselves are Wired's, borrowed through a <see cref="WiredRun"/> in phase
/// <see cref="WiredPhase.Story"/>, as a battle borrows them. Everything here changes on the world's ticks or by commands,
/// so co-op peers, recordings and skipped cutscenes agree; all of it goes into the checksum (<see cref="Fingerprint"/>).
/// </summary>
public sealed class StoryRun(StoryOptions options)
{
    public StoryOptions Options { get; } = options;

    /// <summary>The checkpoint the match started from (<see cref="StoryOptions.Checkpoint"/>), and the last one reached since.</summary>
    public int Checkpoint { get; set; } = options.Checkpoint;

    /// <summary>The section of the chapter the trio is in (docs/STORY.md numbers them from 1).</summary>
    public int Section { get; set; } = 1;

    /// <summary>The seats, in order: the Locator, Ike, Bob (<see cref="StorySeats"/>).</summary>
    public List<PlayerId> Seats { get; } = [];

    /// <summary>The Locator's player: the host's person.</summary>
    public PlayerId Locator => Seats[0];

    /// <summary>
    /// The Grunts' seats the computer plays now, in id order: a bot's seat nobody joined, and one whose person left (co-op,
    /// GameWorld.StoryCoop). Their heroes are the companions' (plan WP-F); a person who joins or comes back takes theirs over.
    /// </summary>
    public SortedSet<PlayerId> Bots { get; } = [];

    /// <summary>What the director has noted (a door hacked, a scare played), by name.</summary>
    public SortedSet<string> Flags { get; } = new(StringComparer.Ordinal);

    /// <summary>The director's clocks, by name: the game time each runs out.</summary>
    public SortedDictionary<string, float> Timers { get; } = new(StringComparer.Ordinal);

    /// <summary>How the chapter ended; null while it lasts.</summary>
    public StoryEnding? Ending { get; set; }

    public bool Over => Ending is not null;

    /// <summary>The chapter's hacks: what can be hacked, the boards under way, what is done (<see cref="HackDesk"/>).</summary>
    public HackDesk Hacks { get; } = new();

    /// <summary>The companions: each Grunt's orders and job, rams' stuns, noises (<see cref="StoryCrew"/>; plan WP-F).</summary>
    public StoryCrew Crew { get; } = new();

    /// <summary>
    /// The Grunts' radio barks, the newest last (line id, speaker, tick): only the computer's Grunts bark. The director turns
    /// those of each tick into the story's lines; <see cref="CompanionBarks.English"/> has their subtitles.
    /// </summary>
    public IReadOnlyList<StoryBark> Barks => Crew.Barks;

    /// <summary>A fingerprint of the run for the world's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        hash.Mix(Options.Chapter);
        hash.Mix(Checkpoint);
        hash.Mix(Section);
        hash.Mix(Ending is { } ending ? (int)ending : -1);
        foreach (var seat in Seats)
            hash.Mix(seat.Value);
        hash.Mix(Bots.Count);
        foreach (var bot in Bots)
            hash.Mix(bot.Value);
        foreach (var flag in Flags)
            hash.Mix(flag);
        foreach (var (name, at) in Timers)
        {
            hash.Mix(name);
            hash.Mix(BitConverter.SingleToInt32Bits(at));
        }
        hash.Mix(Hacks.Fingerprint());
        hash.Mix(Crew.Fingerprint());
        return hash.Value;
    }
}
