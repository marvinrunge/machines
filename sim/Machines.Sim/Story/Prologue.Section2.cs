using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Sim.Story;

// Section 2, Schleichen (plan stufe7 §4, package S; design-stealth): the dark decks past the Thresher. His steps a deck away,
// the window at the niche (the book's), deck A's lane and its round, the Locator by the ducts and the Grunts the long way,
// deck B's island round, his guard at door H, the pipes' lure, the hack and H slammed behind the trio.
public static partial class Prologue
{
    /// <summary>How near the niche the Locator must come in R0 for Bob to call it, metres (R0 east of x 60).</summary>
    public const float NicheCall = 32;

    /// <summary>Where the Thresher's beam rests at the window: on the niche's mouth, Ike's tail (degrees from first_look).</summary>
    public const float NicheLook = 145;

    /// <summary>The hold on deck A's lane he walks to after the trio wakes, and where he is set down first.</summary>
    public const string ThresherHold = "160,52", ThresherSetDown = "232,52";

    /// <summary>Where he rushes as H opens: before the door, at the end of his reach.</summary>
    public const string BeforeH = "192,184";

    /// <summary>The window's beats: played once a save; a chapter started again from checkpoint 1 has him on his round at once.</summary>
    public static readonly IReadOnlyList<string> Window = ["niche", "lamp_off", "first_look", "window", "window_rest", "window_off", "quiet"];

    /// <summary>
    /// Section 2's beats, from the Thresher's first steps to the antechamber (<see cref="Script"/> joins them in order). Spans
    /// follow the checkpoints 2–5 (Schacht, Schaltraum, Deck B, Wache): a beat before a checkpoint has that checkpoint's span
    /// minus one, so a chapter started there counts it fired.
    /// </summary>
    private static StoryBeat[] Section2 =>
    [
        new("steps", 1, new Since("grunts_awake", 5), [new CueCast("steps", ThresherRole), new Say(Lines.IkeSteps, Bark, 1), new Cue("hint creep")]),

        // The window (design-stealth §5.2, the book's niche): Bob calls it, the Grunts hide, his beam comes through D and rests
        // on the niche's mouth far too long, and he walks on east.
        new("niche", 1, new AnyOf(new Near("niche_l7", NicheCall), new Since("grunts_awake", 45)), [new Say(Lines.BobNiche, Bark, 2),
            new Objective("Hide in the alcove", "niche_l7"), new SneakTo(1, "niche_ike"), new SneakTo(2, "niche_bob")], After: ["grunts_awake"]),
        new("lamp_off", 1, new AllOf(new Since("niche", 0), new LampLit()), [new Cue("hint lamp_off")], After: ["niche"]),
        new("first_look", 1, new AnyOf(new InZone("A2"), new Since("niche", 20)), [new MoveTo(ThresherRole, "first_look")], After: ["niche"]),
        new("window", 1, new CastAt(ThresherRole, "first_look", 3), [new Look(ThresherRole, 127, 5), new Cue("window", "first_look")], After: ["first_look"]),
        new("window_rest", 1, new Since("window", 5), [new Look(ThresherRole, NicheLook, 4)]),
        new("window_off", 1, new Since("window_rest", 4), [new Round(ThresherRole, "a", 1, Now: true), new SneakTo(1, null), new SneakTo(2, null)]),
        new("quiet", 1, new Since("window_off", 3), [new Say(Lines.IkeQuiet, Bark, 1), new Say(Lines.BobQuiet, Bark, 2),
            new Objective("Cross deck A", "enter_deckA"), new Cue("hint lane")]),

        new("deck_a", 1, new InZone("R4", StoryWho.AnyHero), [new Say(Lines.Deck1),
            new Objective("Crawl through the ducts to the switch room", "hack_s"), new Cue("hint pulse")]),
        new("leitstand", 1, new InZone("A1"), [new Cue("hint shadow")]),
        new("grate", 1, new AllOf(new InZone("A1"), new CastNear(ThresherRole, "patrol_a_1", 10)), [new Say(Lines.L7Grate, StoryAudience.Inner, 0)]),
        new("round_a", 1, new AnyOf(new RoundDone(ThresherRole, "a"), new Since("deck_a", 100)), [new Say(Lines.BobRoundA, Bark, 2)], After: ["window_off"]),
        new("still", 1, new AllOf(new InSection(2), new ThreatWithin(30), new Quiet(StorySight.ShadowStill)), [new Say(Lines.L7Still, StoryAudience.Inner, 0),
            new Cue("fan down")], After: ["window_off"]),
        new("duct", 1, new InZone("V1am"), [new Cue("hint duct")]),
        // The scare Grunt at n5 glares up as the Locator comes along the Graben to V1a's new mouth beside it.
        new("scare_grunt2", 1, new AllOf(new InZone("graben"), new Near("scare_grunt2", ScareReach)), [new CueCast("scare wake", ScareRole),
            new Lamp(ScareRole, true)]),
        new("scare_dark", 1, new Since("scare_grunt2", 1.5f), [new Lamp(ScareRole, null)]),
        // Checkpoint 2, Schacht: the Locator in the duct (no Grunt lying down); he takes up the ring of both decks, the Grunts
        // go the long way to the E-Nische.
        new("schacht", 1, new AllOf(new AnyOf(new InZone("V1am"), new InZone("V1aw"), new InZone("V1a")), new AllUp()), [new Checkpoint(2),
            new Round(ThresherRole, "ring"), new SneakTo(1, "hide_e"), new SneakTo(2, "ENi")]),

        new("dust", 2, new InZone("dust_fall"), [new Cue("scare dust", "dust_fall")]),
        new("false_alarm", 2, new AllOf(new InZone("R5"), new CastNear(ThresherRole, "false_alarm_stack", FalseAlarmReach)),
            [new HarmProp(StackTag1), new HarmProp(StackTag2), new Cue("scare crates", "false_alarm_stack")]),
        new("switch_room", 2, new InZone("R7"), [new Objective("Open the service passage for the Grunts", "hack_s")]),
        new("passage", 2, new HackDone("hack_s"), [new OpenDoors("E"), new Objective("Meet on deck B and open the blast door", "hack_h"),
            new SneakTo(1, "cp1b_ike"), new SneakTo(2, "cp1b_bob")]),
        new("schaltraum", 2, new AllOf(new HackDone("hack_s"), new AllUp()), [new Checkpoint(3)]),

        // The noise terminal: F rattles three times and draws him to the west side.
        new("noise_f", 3, new HackDone("hack_noise"), [new Lure("lure_f", 2.5f)]),
        new("ring_b", 3, new AnyOf(new InZone("V1c"), new InZone("S", StoryWho.AnyCompanion)), [new Round(ThresherRole, "b")], After: ["passage"]),
        new("found", 3, new InZone("R6"), [new Cue("scare found"), new Say(Lines.Found)], After: ["passage"]),
        // Checkpoint 4, Deck B: the Locator out of V1c; the Grunts follow it again, the sneaking way.
        new("deck_b", 3, new AllOf(new InZone("R6"), new AllUp()), [new Checkpoint(4), new SneakTo(1, null), new SneakTo(2, null)], After: ["passage"]),

        new("retreat", 4, new AllOf(new InZone("R6"), new CastNear(ThresherRole, "patrol_b_3", 14)), [new Say(Lines.BobRetreat, Bark, 2)], After: ["deck_b"]),
        new("door_h", 4, new CastAt(ThresherRole, "patrol_b_2"), [new Say(Lines.BobDoorH, Bark, 2)], After: ["deck_b"]),
        // Checkpoint 5, Wache: all three in the Nordgang (or the Locator, 90 s after deck B); he goes to guard H.
        new("guard", 4, new AllOf(new AnyOf(new InZone("nordgang", StoryWho.Everyone), new AllOf(new InZone("nordgang"), new Since("deck_b", 90))), new AllUp()),
            [new Checkpoint(5), new Guard(ThresherRole, "guard_h"), new SneakTo(1, "wait_w"), new SneakTo(2, "wait_o")], After: ["deck_b"]),

        new("offer", 5, new Near("wait_c", 5), [new Say(Lines.BobBangOffer, Bark, 2), new Cue("hint lure"), new Cue("hint pipe")], After: ["guard"]),
        new("ike_bang", 5, new Banged("valve_w"), [new Say(Lines.IkeBang, Bark, 1)], After: ["guard"]),
        new("bob_bang", 5, new Banged("valve_o"), [new Say(Lines.BobBang, Bark, 2)], After: ["guard"]),
        // The Locator waits a minute without ordering: Bob bangs on his own.
        new("auto_bang", 5, new AllOf(new Since("offer", 60), new Not(new Banged("valve_o")), new Not(new HackDone("hack_h"))), [new BangBy(2, "valve_o")]),
        // He comes back before the hack is done: Bob asks, Ike bangs the other valve.
        new("again", 5, new AllOf(new Since("bob_bang", 20), new Not(new Searching(ThresherRole)), new Not(new HackDone("hack_h"))),
            [new Say(Lines.BobAgain, Bark, 2), new BangBy(1, "valve_w")]),

        // "Das Plötzliche": H shrieks open, he gallops to it, the trio is in before him and H slams.
        new("blast_door", 5, new HackDone("hack_h"), [new OpenDoors("H"), new AutoDoors("I"), new Rush(ThresherRole, BeforeH), new Say(Lines.IkeIn, Bark, 1),
            new Cue("door h shriek", "190,210"), new SneakTo(1, null), new SneakTo(2, null), new Objective("Get into the antechamber", "R8")]),
        new("antechamber", 5, new InZone("R8"), [new Checkpoint(6), new Section(3), new Objective("Hack the main computer", "hack_core"),
            new CloseDoors("H", "R8"), new LockDoors("H")], After: ["blast_door"]),
        new("h_shut", 5, new AllOf(new Since("antechamber", 0), new DoorShut('H')), [new Flag(StoryMarks.Stealth, false), new Cue("door h slam", "190,210")]),
        new("knock", 5, new Since("h_shut", 2), [new Say(Lines.IkeKnock, Bark, 1)]),

        // A Grunt down in the dark: the Locator learns to put it back together (the workshop still teaches it, C10).
        new("assemble", 1, new AllOf(new InSection(2), new AnyDown()), [new Cue("hint assemble")]),
    ];

    /// <summary>The Thresher set down on deck A as the trio wakes: a deck away, walking towards his hold on the lane; section 2's stealth on.</summary>
    private static StoryAction[] ThresherOnPatrol =>
    [
        new Flag(StoryMarks.Stealth),
        new Spawn(ThresherRole, Thresher, [ThresherSetDown], Hunters),
        new MoveTo(ThresherRole, ThresherHold),
    ];

    /// <summary>The dark rows: switched-off machines in the clamps of both decks, and the one on deck A that wakes for a moment.</summary>
    private static StoryAction[] Rows =>
    [
        // Gaps the Grunts need (design-stealth §2.1, §2.3): n2, n3 and n5's Builder (the scare Grunt hangs there), B4 and B5 are empty;
        // the Tech Boy moved from B7 to B6.
        new Spawn(RowRole, Grunt2, ["clampA_s1", "clampA_s4", "clampA_s6", "clampA_n6"], Inert: true),
        new Spawn(RowRole, Grunt3, ["clampA_s2", "clampA_n7", "clampB_2", "clampB_8"], Inert: true),
        new Spawn(RowRole, Reaper2, ["clampA_s5", "clampA_n1"], Inert: true),
        new Spawn(RowRole, Builder, ["clampA_s8"], Inert: true),
        // The cast's rows (docs/STORY.md, Besetzung): Grunts, Reapers, Builders and Engineers (the Tech Boy), nothing else.
        new Spawn(RowRole, TechBoy, ["clampA_s7", "clampA_n8", "clampB_6"], Inert: true),
        new Spawn(ScareRole, Grunt2, ["scare_grunt2"], Inert: true),
    ];

    /// <summary>
    /// Section 2's part of a checkpoint's set-up: the dark rows everywhere; the Thresher where the played run has him, at least
    /// 40 m from the trio and not looking at it (design-stealth §8); from 6 on behind the shut, locked door H.
    /// </summary>
    static partial void SetUpSection2(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do)
    {
        Do(Rows);
        switch (cp)
        {
            case 1:
                // A retry has him on his round at once (the window plays once a save).
                foreach (var beat in Window)
                    run.Director.Fired[beat] = 0;
                Do([new Flag(StoryMarks.Stealth), new Spawn(ThresherRole, Thresher, [ThresherHold], Hunters), new Round(ThresherRole, "a", 1, Now: true)]);
                break;
            case 2:
                Do([new Flag(StoryMarks.Stealth), new Spawn(ThresherRole, Thresher, ["patrol_a_1"], Hunters), new Round(ThresherRole, "a", 1, Now: true),
                    new SneakTo(1, "hide_e"), new SneakTo(2, "ENi")]);
                break;
            case 3:
                Do([new Flag(StoryMarks.Stealth), new Spawn(ThresherRole, Thresher, ["patrol_ring_r5"], Hunters), new Round(ThresherRole, "ring", 2, Now: true),
                    new SneakTo(1, "cp1b_ike"), new SneakTo(2, "cp1b_bob")]);
                break;
            case 4:
                Do([new Flag(StoryMarks.Stealth), new Spawn(ThresherRole, Thresher, ["patrol_b_1"], Hunters), new Round(ThresherRole, "b", 1, Now: true)]);
                break;
            case 5:
                Do([new Flag(StoryMarks.Stealth), new Spawn(ThresherRole, Thresher, ["patrol_b_1"], Hunters), new Guard(ThresherRole, "guard_h"),
                    new SneakTo(1, "wait_w"), new SneakTo(2, "wait_o")]);
                break;
            case 6:
                // At his post behind the shut door, until the squad sends him off.
                Do([new Spawn(ThresherRole, Thresher, ["guard_h"], Hunters)]);
                break;
        }
        if (cp >= 6)
            run.Director.Locked.Add('H');
    }
}
