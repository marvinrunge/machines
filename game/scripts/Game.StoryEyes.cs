using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// How the machines' eyes look on the story's deck (ActorView.EyesOf): a machine switched off has dark eyes and no lamp, one the
// story made an enemy of the trio glows red. Read from the world every frame, so every client and every replay shows the same
// from the same state (who is dormant, downed, in the dark rows, which side a machine is on); nothing here changes the world.
public partial class Game
{
    /// <summary>
    /// How a machine's eyes look in the story: dark while it is switched off (the rows in the clamps, the scare Grunt until it
    /// wakes for its moment, Ike and Bob asleep in theirs or lying downed, the Locator until its optics start in the boot,
    /// anything destroyed), red when it is the trio's enemy (the Thresher, the Reapers, the Commander, the workshop's Grunts,
    /// and the scare Grunt while its lamp glares), else as the model has them.
    /// </summary>
    private EyeLook StoryEyes(int id)
    {
        if (World.Story is not { } story || World.Find<Machine>(id) is not { } machine)
            return EyeLook.Lit;
        if (machine.IsDead)
            return EyeLook.Off;
        var director = story.Director;
        if (director.Cast.TryGetValue(Prologue.RowRole, out var rows) && rows.Contains(id))
            return EyeLook.Off;
        // The one on deck A that wakes for a moment: its eyes come on red with its lamp, and go out with it.
        if (director.Cast.TryGetValue(Prologue.ScareRole, out var scare) && scare.Contains(id))
            return World.StoryLampOn(id) ? EyeLook.Hostile : EyeLook.Off;
        if (story.Seats.Count > 0 && World.HeroOf(story.Locator)?.Id == id)
            return story.Flags.Contains(StoryMarks.Boot) && !director.Has("boot_optics") ? EyeLook.Off : EyeLook.Lit;
        if (World.CompanionSeat(machine) is { } seat)
            return World.CompanionDormant(seat) || World.CompanionDown(seat) ? EyeLook.Off : EyeLook.Lit;
        return World.AreEnemies(machine.Race, story.Options.Race) ? EyeLook.Hostile : EyeLook.Lit;
    }
}
