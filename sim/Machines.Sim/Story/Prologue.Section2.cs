using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Sim.Story;

// Section 2, Schleichen (plan stufe7 §4, package S): the dark decks past the Thresher, its round, the rows and the scare Grunt.
public static partial class Prologue
{
    /// <summary>
    /// Section 2's beats, from the Thresher's first steps to the antechamber (<see cref="Script"/> joins them in order): through
    /// the dark decks past the Thresher, the Locator by the ducts, the Grunts the long way round.
    /// </summary>
    private static StoryBeat[] Section2 =>
    [
        new("steps", 1, new Since("grunts_awake", 5), [new CueCast("steps", ThresherRole), new Say(Lines.IkeSteps, Bark, 1), new Cue("hint still")]),
        new("deck_a", 1, new InZone("R4", StoryWho.AnyHero), [new Say(Lines.Deck1),
            new Objective("Crawl through the ducts to the switch room", "hack_s"), new Cue("hint pulse")]),
        new("duct", 1, new InZone("V1a"), [new Cue("hint duct")]),
        // Ike's "I don't fit in there" is the companions' own bark, once the Locator crawls off into a duct (CompanionBot).
        // The scare Grunt glares up as the Locator comes by on its way from R4's west door to duct V1a (whose mouth is about
        // 27 m from it on the doubled deck).
        new("scare_grunt2", 1, new AllOf(new InZone("R4"), new Near("scare_grunt2", ScareReach)), [new CueCast("scare wake", ScareRole),
            new Lamp(ScareRole, true)]),
        new("scare_dark", 1, new Since("scare_grunt2", 1.5f), [new Lamp(ScareRole, null)]),
        new("dust", 1, new InZone("dust_fall"), [new Cue("scare dust", "dust_fall")]),
        new("false_alarm", 1, new AllOf(new InZone("R5"), new CastNear(ThresherRole, "false_alarm_stack", FalseAlarmReach)),
            [new HarmProp(StackTag1), new HarmProp(StackTag2), new Cue("scare crates", "false_alarm_stack")]),
        new("switch_room", 1, new InZone("R7"), [new Objective("Open the service passage for the Grunts", "hack_s")]),
        new("passage", 1, new HackDone("hack_s"), [new OpenDoors("E"), new Objective("Meet on deck B and open the blast door", "hack_h")]),
        new("found", 1, new InZone("R6"), [new Cue("scare found"), new Say(Lines.Found)], After: ["passage"]),
        new("blast_door", 1, new HackDone("hack_h"), [new OpenDoors("H"), new AutoDoors("I"), new Objective("Get into the antechamber", "R8")]),
        new("antechamber", 1, new InZone("R8"), [new Checkpoint(6), new Section(3), new Objective("Hack the main computer", "hack_core")],
            After: ["blast_door"]),

    ];

    /// <summary>The Thresher set down on deck A and on its round of both decks (the ring R4, West link, R6, East link).</summary>
    private static StoryAction[] ThresherOnPatrol =>
    [
        new Spawn(ThresherRole, Thresher, ["spawn_thresher"], Hunters),
        new Patrol(ThresherRole, "patrol_thresher"),
    ];

    /// <summary>The dark rows: switched-off machines in the clamps of both decks, and the one on deck A that wakes for a moment.</summary>
    private static StoryAction[] Rows =>
    [
        new Spawn(RowRole, Grunt2, ["clampA_s1", "clampA_s4", "clampA_s6", "clampA_n2", "clampA_n6", "clampB_5"], Inert: true),
        new Spawn(RowRole, Grunt3, ["clampA_s2", "clampA_n7", "clampB_2", "clampB_8"], Inert: true),
        new Spawn(RowRole, Reaper2, ["clampA_s5", "clampA_n1"], Inert: true),
        new Spawn(RowRole, Builder, ["clampA_s8", "clampA_n5", "clampB_4"], Inert: true),
        // The cast's rows (docs/STORY.md, Besetzung): Grunts, Reapers, Builders and Engineers (the Tech Boy), nothing else.
        new Spawn(RowRole, TechBoy, ["clampA_s7", "clampA_n8", "clampB_7"], Inert: true),
        new Spawn(ScareRole, Grunt2, ["scare_grunt2"], Inert: true),
    ];

    /// <summary>Section 2's part of a checkpoint's set-up: the dark rows everywhere, the Thresher on his round until the squad calls him off.</summary>
    static partial void SetUpSection2(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do)
    {
        Do(Rows);
        // Until package S places him at guard_h behind the shut door H at checkpoint 6.
        if (cp is >= 1 and <= 6)
            Do(ThresherOnPatrol);
    }
}
