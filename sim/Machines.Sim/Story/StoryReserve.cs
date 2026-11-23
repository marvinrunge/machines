using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>
/// The Notabschaltung (plan stufe7 C7): while the director's flag <see cref="StoryMarks.Reserve"/> is set, the Locator at 0
/// hit points does not fall but shuts down at 1 (inert: nothing aims at it or harms it, its hack unplugged with its solved
/// tiles kept). A Grunt standing within <see cref="Reach"/> holds Use on it for <see cref="PlugSeconds"/> ("Kabel stecken");
/// a hit does not break the cable, the Grunt going down or more than <see cref="Reach"/> away does. The Locator comes back
/// with <see cref="BackShare"/> of its hit points and <see cref="Grace"/> seconds nothing harms it. The chapter is lost on a
/// second shutdown before the next checkpoint (the reserve is spent: <see cref="StoryMarks.ReserveUsed"/>), with no cable
/// within <see cref="Wait"/> seconds, or with both Grunts down. The reserve is whole again at every checkpoint and whenever
/// the director turns it on. The flags live in <see cref="StoryRun.Flags"/>; this keeps the clocks (GameWorld.StoryReserve).
/// </summary>
public sealed class StoryReserve
{
    /// <summary>How near a Grunt must stand to plug in, metres; how long it holds Use, seconds; how long the Locator waits for it, seconds.</summary>
    public const float Reach = 4, PlugSeconds = 3, Wait = 20;

    /// <summary>The share of its hit points the Locator comes back with, and the seconds after it that nothing harms it.</summary>
    public const float BackShare = 0.5f, Grace = 2;

    /// <summary>The tick the Locator shut down; -1 while it is up.</summary>
    public long DownTick { get; set; } = -1;

    /// <summary>How many ticks the cable has been held in so far (by whichever Grunt holds it).</summary>
    public int PlugTicks { get; set; }

    /// <summary>The seat (player value) of the Grunt holding the cable in; 0 when none.</summary>
    public int Plugger { get; set; }

    /// <summary>How many times the Locator has come back by a cable in this chapter (the view's lines, a test's count).</summary>
    public int Restarts { get; set; }

    /// <summary>The reserve is whole again (a checkpoint, the director turning it on): the next shutdown is survivable.</summary>
    internal void Restore(StoryRun run) => run.Flags.Remove(StoryMarks.ReserveUsed);

    /// <summary>The cable lets go: what was held counts from nothing next time.</summary>
    internal void LetGo() => (PlugTicks, Plugger) = (0, 0);

    public long Fingerprint()
    {
        var hash = new WiredHash();
        hash.Mix(DownTick);
        hash.Mix(PlugTicks);
        hash.Mix(Plugger);
        hash.Mix(Restarts);
        return hash.Value;
    }
}
