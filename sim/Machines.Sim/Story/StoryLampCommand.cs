using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// A hero switches its lamp (the Lamp key): on it lights the way and the hero with it, raising its signature; off it is
/// all but gone in the dark (docs/STORY.md, Horror-Level: "Scheinwerfer an/aus"). Null <paramref name="On"/> switches it
/// over. It is the issuer's own hero's lamp, the Locator's for the race's own authority.
/// </summary>
public sealed record StoryLampCommand(Race Race, bool? On = null) : StoryCommand(Race)
{
    protected internal override string? ApplyTo(GameWorld world) => world.SwitchStoryLamp(this);
}
