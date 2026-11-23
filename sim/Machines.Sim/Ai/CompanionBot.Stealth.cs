using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// Stage 7's behaviour of a computer's Grunt for package S (section 2: freeze, SneakGo, Lure) (plan stufe7 C11: after the
// Notabschaltung's restart and a task, before following). A stub until the package takes this file over.
public sealed partial class CompanionBot
{
    /// <summary>This section's behaviour for the Grunt; true when it acted this look (nothing after it runs).</summary>
    private bool ThinkStealth(StoryRun run, Companion companion, Machine grunt) => false;
}
