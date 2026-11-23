using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>
/// The armoury's waves (package W): batches, pauses, wrecks, the boss; and its share of the story's harm. Stage 7's foundation (plan stufe7 §4, F item 2) made it empty and in the checksum; its package fills it.
/// Everything here changes on ticks and goes into <see cref="Fingerprint"/>.
/// </summary>
public sealed class StoryWaves
{
    /// <summary>A fingerprint of what this part keeps, for the run's checksum.</summary>
    public long Fingerprint() => new WiredHash().Value;

    /// <summary>The share of a hit of <paramref name="shooter"/> on <paramref name="target"/> this part takes over (GameWorld.StoryHarm); null: not its hit.</summary>
    public float? Harm(Machines.Sim.World.Actor target, Machines.Sim.World.Actor? shooter) => null;
}
