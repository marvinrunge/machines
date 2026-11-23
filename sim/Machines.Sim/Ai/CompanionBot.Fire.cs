using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// Armed Grunts (plan stufe7 C11): a computer's Grunt with a weapon fires at the nearest hostile within FireReach when it is
// on no task. Today's Grunts are unarmed, so this does nothing yet; package W mounts the Bolters and fills it in.
public sealed partial class CompanionBot
{
    /// <summary>How far an armed Grunt looks for a hostile to fire at, metres.</summary>
    public const float FireReach = 35;

    /// <summary>Fires at the nearest hostile in reach when the Grunt is armed; true when it did (a stub until W arms the Grunts).</summary>
    private bool FireIfArmed(StoryRun run, Machine grunt) => false;
}
