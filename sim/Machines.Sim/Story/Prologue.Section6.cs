using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Sim.Story;

// Section 6 (plan stufe7 §4, package W): from the trio together at the crossing to the repair station.
public static partial class Prologue
{
    /// <summary>Section 6's beats, from <c>together</c> to <c>repaired</c> (<see cref="Script"/> joins them in order).</summary>
    private static StoryBeat[] Section6 =>
    [
        new("together", 7, new InZone("R12", StoryWho.All), [new Checkpoint(8), new Section(6), new AutoDoors("R"),
            new Objective("Get through the workshop", "pickup_bolter"), .. Hostiles], After: ["d5"]),

        // ---- 6 Erste Gegenwehr: the Grunts let go of the gates once the Locator is through; the door shuts the hunt out behind
        // the trio; the weapon on its rack (Use), the armed Grunts, then the repair station.
        new("workshop", 8, new InZone("R13"), [new SayToSeat(Lines.BobGlitch, 2), new Objective("Take the weapon from the rack", "pickup_bolter"),
            new Assign(1, CompanionTask.None), new Assign(2, CompanionTask.None)]),
        new("sealed", 8, new InZone("R13", StoryWho.Everyone), [new Flag(Sealed), new StopGates("GO"),
            new Withdraw(ReaperRole), new Withdraw(CommanderRole)], After: ["workshop"]),
        new("weapon", 8, new Flagged(StoryMarks.Done("pickup_bolter")), [new Cue("pickup bolter", "pickup_bolter"),
            new Objective("Stop the armed Grunts", "R13")]),
        // The firefight brings a crate down off the shelves onto a Grunt: the Locator learns here to put a fallen Grunt together.
        new("knocked", 8, new AllOf(new Since("weapon", 3), new InZone("R13", StoryWho.AnyCompanion)), [new KnockDown("crate_small", 1, "R13", 2, 1)]),
        new("cleared", 8, new Dead(HostileRole), [new AutoDoors("TU"), new Objective("Reach the repair station", "repair_station")], After: ["weapon"]),
        new("repaired", 8, new InZone("repair"), [new Checkpoint(11), new Objective("Get to the escape pods", "R15")]),
    ];

    /// <summary>
    /// The three armed Grunts of the workshop (plan WP-G): already updated, painted as the trio's own, ready to go for what
    /// they see (by the story's sight); two stand their posts, the third walks the room.
    /// </summary>
    private static StoryAction[] Hostiles =>
    [
        new Spawn(HostileRole, HostileGrunt, ["hostile_grunt_1", "hostile_grunt_2", "hostile_grunt_3"], DefCon.Low, Looks: Race.Red),
        new StandFast(HostileRole),
        new PatrolRoute(HostileRole, 2, "hostile_grunt_3", "458,238"),
    ];

    /// <summary>Section 6's part of a checkpoint's set-up: the crossing (8) with the gates held and the workshop's Grunts; the Bolter taken (from 11).</summary>
    static partial void SetUpSection6(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do)
    {
        if (cp == 8)
        {
            // All three at the crossing, the Grunts holding the gates, the Reapers at the gates' far side.
            Do([new Spawn(ReaperRole, Reaper3, [AtG1, "348,283"], Hunters), new Spawn(ReaperRole, Reaper2, [AtG2], Hunters),
                new Spawn(CommanderRole, Commander, ["commander_2"], Hunters)]);
            Do([new Assign(1, CompanionTask.HoldSwitch, "switch_g2"), new Assign(2, CompanionTask.HoldSwitch, "switch_g1")]);
            Do(Hostiles);
        }
        if (cp >= 11)
        {
            // The Bolter taken from its rack before (package W moves this to 9, and mounts the Grunts' Bolters from 9 on).
            world.TakeUpForStory(run.Locator, "pickup_bolter");
            run.Flags.Add(Sealed);
        }
    }
}
