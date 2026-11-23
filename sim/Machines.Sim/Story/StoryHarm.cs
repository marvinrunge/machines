using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>What a story's harm seam did with a hit (GameWorld.StoryHarm): left it to the usual shares, set it whole, or vetoed it.</summary>
public enum StoryHarmVerdict
{
    /// <summary>The hit goes on as any other (the chips', the body's and the difficulty's shares).</summary>
    Pass,

    /// <summary>The seam set the hit's harm itself, armour ignored; no share changes it any more.</summary>
    Fixed,

    /// <summary>The hit does nothing (friendly fire within the trio, a round of a burst after the counted one).</summary>
    Veto,
}

/// <summary>
/// The story's one rule for the Thresher's fire (plan stufe7 C4) and what the harm seam keeps: after his draw (0.6 s, the
/// sight's), single shots every <see cref="Gap"/>, each taking <see cref="ThresherShare"/> of the target's hit points with
/// its armour ignored, so three from full shut the Locator down (or delete it in section 2) and down a Grunt. It holds while
/// the run is in a section that uses it (flag <see cref="StoryMarks.Stealth"/> or <see cref="StoryMarks.Escape"/>);
/// elsewhere the Thresher hits as he always did.
/// </summary>
public sealed class StoryHarm
{
    /// <summary>The share of the target's hit points one of the Thresher's shots takes.</summary>
    public const float ThresherShare = 0.34f;

    /// <summary>Seconds between the Thresher's counted shots: a round of the same burst sooner than this glances off.</summary>
    public const float Gap = 0.5f;

    /// <summary>The roles whose hits on the trio follow the rule.</summary>
    public static readonly IReadOnlyList<string> ShotRoles = [Prologue.ThresherRole];

    /// <summary>The game time of each shooter's last counted shot under the rule, by its machine id.</summary>
    public SortedDictionary<int, float> LastShot { get; } = [];

    /// <summary>Whether it keeps anything (the run's checksum mixes it in only then, so a run without it keeps its sums).</summary>
    public bool Any => LastShot.Count > 0;

    /// <summary>A fingerprint of what it keeps, for the run's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        foreach (var (id, at) in LastShot)
        {
            hash.Mix(id);
            hash.Mix(BitConverter.SingleToInt32Bits(at));
        }
        return hash.Value;
    }
}
