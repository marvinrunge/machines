using Machines.Sim.Survival;

namespace Machines.Sim.Story;

/// <summary>
/// The harm budget (package E): what the escape may take from the trio in a stretch, so it stays fair. Stage 7's foundation (plan stufe7 §4, F item 2) made it empty and in the checksum; its package fills it.
/// Everything here changes on ticks and goes into <see cref="Fingerprint"/>.
/// </summary>
public sealed class StoryBudget
{
    /// <summary>A fingerprint of what this part keeps, for the run's checksum.</summary>
    public long Fingerprint() => new WiredHash().Value;

    /// <summary>A hit of so much harm on the trio as the budget lets it through (GameWorld.StoryHarm); the stub lets all of it through.</summary>
    public int Filter(Machines.Sim.World.Actor target, int damage) => damage;
}
