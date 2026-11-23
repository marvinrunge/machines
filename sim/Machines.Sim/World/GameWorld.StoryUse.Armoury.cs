using Machines.Sim.Story;

namespace Machines.Sim.World;

// Stage 7's Use targets of package W (section 6: mend, mount, strip), in the registry of GameWorld.Companions
// (plan stufe7 C9). A stub until the package takes this file over.
public sealed partial class GameWorld
{
    /// <summary>Adds what a hero of <paramref name="seat"/> could use here of this section, with its kind and distance.</summary>
    private void StoryUseTargetsArmoury(StoryRun run, int seat, Machine hero, List<StoryUseOption> near)
    {
    }

    /// <summary>Uses a target of this section; <see cref="NotMine"/> for one it does not know, null when done, else why not.</summary>
    private string? StoryUseArmoury(StoryRun run, PlayerId player, int seat, Machine hero, string target) => NotMine;
}
