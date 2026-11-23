using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Sim.Story;

// Section 7, the Absturz (plan stufe7 §4, package E): from the Notgang to the launch.
public static partial class Prologue
{
    /// <summary>Section 7's beats and the launch of 8, from <c>notgang</c> to <c>away</c> (<see cref="Script"/> joins them in order).</summary>
    private static StoryBeat[] Section7 =>
    [
        // ---- 7 Absturz: the countdown from the Notgang, debris, the ship listing; the pod's hack under the firewall.
        // Its stages at 120, 60, 30 and 10 seconds left (and the hull breach at 90): the automatic voice, the ship listing
        // further (the view's alone), blasts where the ship tears and debris falling into the lanes, warned of by its sparks a
        // moment before it lands; nobody stands where it lies.
        new("notgang", 11, new InZone("R15"), [new Checkpoint(12), new Section(7), new Countdown(CountdownFull), new Say(Lines.Auto3),
            new Hackable(true, "pod_3_hatch"), new Objective("Reach the escape pods", "pod_3_hatch"), new Tilt(5, 0, 3), new Cue("rumble 0.4"),
            new Fall("crate_large", "debris_1", FallWarning, 0, 5)]),
        new("t120", 12, new CountdownAt(120), [new CountdownStage(), new Say(Lines.T120), new Blast("pod_1", 12, 40, 8),
            new Fall("crate_large", "debris_2", FallWarning, 0, -5), new Tilt(7, 1, 2)]),
        new("breach", 12, new CountdownAt(90), [new Say(Lines.Breach), new Blast("512,208", 8, 30, 5), new Cue("rumble 0.7"),
            new Fall("crate_large", "510,80", FallWarning, 30)]),
        new("t60", 12, new CountdownAt(60), [new CountdownStage(), new Say(Lines.T60), new Blast("pod_2", 12, 40, 8),
            new Fall("crate_large", "debris_3", FallWarning, 0, 5), new Tilt(9, -1, 2)]),
        new("t30", 12, new CountdownAt(30), [new CountdownStage(), new Say(Lines.T30), new Blast("596,108", 8, 30, 6),
            new Fall("crate_large", "538,76", FallWarning, -20), new Tilt(12, 2, 2)]),
        new("t10", 12, new CountdownAt(10), [new CountdownStage(), new Say(Lines.T10), new Cue("rumble 1"), new Blast("496,72", 8, 30, 7),
            new Fall("crate_large", "592,76", FallWarning, 35)]),
        new("crash", 12, new CountdownAt(0), [new End(StoryEnding.Crashed)]),
        new("pod_bay", 12, new InZone("R16"), [new Say(Lines.Crash), new Objective("Hack escape pod three", "pod_3_hatch")], After: ["notgang"]),
        new("pod_denied", 12, new AllOf(new Near("pod_3_hatch", 10), new Not(new HackDone("pod_3_hatch"))), [new Say(Lines.PodNo)]),
        new("pod_hacked", 12, new HackDone("pod_3_hatch"), [new Objective("Open the hatch", "pod_3_hatch")]),
        // The hatch open (the Locator's Use, GameWorld.UseForStory), the Grunts board.
        new("hatch", 12, new Flagged(StoryMarks.Done("pod_3_hatch")), [new Objective("Get everyone into the pod", "pod_zone"),
            new Assign(1, CompanionTask.Board, "pod_3_hatch"), new Assign(2, CompanionTask.Board, "pod_3_hatch")], After: ["pod_hacked"]),
        // A Grunt put back together (or one that dropped its job) boards again.
        new("reboard", 12, new Not(new InSection(8)), [new Assign(1, CompanionTask.Board, "pod_3_hatch", IfFree: true),
            new Assign(2, CompanionTask.Board, "pod_3_hatch", IfFree: true)], After: ["hatch"], Repeat: true, Every: 1),

        // ---- 8 Abwurf: the hack done, the hatch opened by the Locator, the Locator at the pod and both Grunts aboard (standing in
        // the pod's zone, or lying down in its bay: act 1 starts with one Grunt broken anyway), the pod drops (abwurf.cut); the
        // chapter is won once the bay's part is over. A Grunt lying down outside the bay must be put together first (the rally
        // above says so).
        new("launch", 12, new AllOf(new InZone("pod_zone"), new Aboard()), [new Section(8), new Countdown(null), new Objective(null),
            new Hold(true, 0, 1, 2), new Spare(0, Away + 5), new Spare(1, Away + 5), new Spare(2, Away + 5), new Cue("cutscene abwurf", "pod_3")],
            After: ["hatch"]),
        new("away", 12, new Since("launch", Away), [new End(StoryEnding.Escaped)]),
    ];

    /// <summary>Section 7's part of a checkpoint's set-up: the Notgang as a run played through has it (12).</summary>
    static partial void SetUpSection7(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do)
    {
        if (cp >= 12 && CountdownFrom[cp] is { } left)
        {
            // The crate the Notgang's beat let fall lies in the lane.
            Do([new Countdown(left), new Tilt(5, 0, 0.5f), new PlaceProp("crate_large", "debris_1", 0, 5)]);
        }
    }
}
