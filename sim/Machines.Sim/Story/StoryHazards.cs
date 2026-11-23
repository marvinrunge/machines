using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>
/// The escape's hazards (package E): rolls, rails, jets, falling debris. Stage 7's foundation (plan stufe7 §4, F item 2) made it empty and in the checksum; its package fills it.
/// Everything here changes on ticks and goes into <see cref="Fingerprint"/>.
/// </summary>
public sealed class StoryHazards
{
    /// <summary>A fingerprint of what this part keeps, for the run's checksum.</summary>
    public long Fingerprint() => new WiredHash().Value;
}
