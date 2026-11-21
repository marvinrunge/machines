using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// The Locator tells the Grunts the computer plays what to do (docs/STORY.md, Koop: "Hier warten", "Folgen", "Ablenken"):
/// follow it, wait where they stand, or drive to <paramref name="Point"/>, make a noise there and go back. <paramref name="Seat"/>
/// names one Grunt; without it the order goes to every Grunt that can take it. Only the Locator gives orders, and a Grunt a
/// person plays takes none from it.
/// </summary>
public sealed record StoryOrderCommand(Race Race, CompanionOrder Order, Vector2 Point, PlayerId? Seat = null) : StoryCommand(Race)
{
    protected internal override string? ApplyTo(GameWorld world) => world.OrderCompanions(this);
}

/// <summary>
/// A Grunt rams (docs/STORY.md, Erste Gegenwehr: "Sie rammen die Gegner um, damit er treffen kann"): it lunges at the enemy
/// machine <paramref name="Target"/> (or the nearest one in front of it) within reach, which takes the ram's damage, is thrown
/// back and holds its fire for a moment. Given by a Grunt's person for their own Grunt (the race's own authority names the
/// Grunt by <paramref name="Seat"/>); the computer's Grunts ram by themselves.
/// </summary>
public sealed record StoryRamCommand(Race Race, int? Target = null, PlayerId? Seat = null) : StoryCommand(Race)
{
    protected internal override string? ApplyTo(GameWorld world) => world.Ram(this);
}

/// <summary>
/// A hero uses what is in front of it (the story's Use key): the Locator holds a downed Grunt together, takes up a weapon from
/// its rack or opens the pod's hatch; anyone of the trio holds a gate's switch. <paramref name="Target"/> is a deck marker or
/// a Grunt's seat as "seat:&lt;id&gt;"; without it the world picks what is nearest in reach. <paramref name="Held"/> false lets go
/// of the key, which ends a hold (a revive not yet done starts again from nothing).
/// </summary>
public sealed record StoryUseCommand(Race Race, string? Target = null, bool Held = true) : StoryCommand(Race)
{
    protected internal override string? ApplyTo(GameWorld world) => world.Use(this);
}
