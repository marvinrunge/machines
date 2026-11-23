using Machines.Sim.Story;

namespace Machines.Sim.World;

// Stage 7's Use targets of package S (section 2: valves and noise sources to bang), in the registry of GameWorld.Companions
// (plan stufe7 C9). A stub until the package takes this file over.
public sealed partial class GameWorld
{
    /// <summary>Adds what a hero of <paramref name="seat"/> could use here of this section, with its kind and distance.</summary>
    private void StoryUseTargetsStealth(StoryRun run, int seat, Machine hero, List<StoryUseOption> near)
    {
    }

    /// <summary>Uses a target of this section; <see cref="NotMine"/> for one it does not know, null when done, else why not.</summary>
    private string? StoryUseStealth(StoryRun run, PlayerId player, int seat, Machine hero, string target) => NotMine;
}
