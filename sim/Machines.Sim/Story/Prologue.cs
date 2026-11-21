using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Sim.Story;

/// <summary>The scripts of the story's chapters, by chapter: its beats, and how a match started from a checkpoint is set up.</summary>
public static class StoryScripts
{
    /// <summary>The beats of a chapter; none for a chapter without a script.</summary>
    public static IReadOnlyList<StoryBeat> Of(string chapter) => chapter == StoryOptions.Prologue ? Prologue.Script : [];

    /// <summary>Sets the world up for the checkpoint the chapter starts from (<see cref="StoryRun.Checkpoint"/>).</summary>
    internal static void SetUp(GameWorld world, StoryRun run)
    {
        if (run.Options.Chapter == StoryOptions.Prologue)
            Prologue.SetUp(world, run, run.Checkpoint);
    }
}

/// <summary>
/// The prologue, "Fehler" (docs/STORY.md, Prolog im Detail; the rooms of plan §1.2, the lines of plan §5.1), as the director's
/// script: its eight sections as beats, and one set-up per checkpoint that builds the world as the chapter has it there
/// (where the trio stands, which Grunts are awake, which doors are open and hacks done, which machines exist, the lights,
/// the alarm, the countdown). Seats: 0 the Locator, 1 Ike, 2 Bob.
/// <para>
/// The hunters see by the story's sight and fight by its difficulty (GameWorld.StorySight: they draw before they fire, hit
/// the trio by half, the Thresher whole; the Commander leads and calls, his squad shoots). After the core the Commander
/// splits his Reapers into the parallel corridors (WP-H), they fire through the grates and force the gates unless a Grunt
/// holds a switch; the workshop's armed Grunts fight for real once the Locator has its Bolter (WP-G). A Grunt lying down is
/// never left behind: the objective points at it, and the pod does not launch without it.
/// </para>
/// </summary>
public static class Prologue
{
    // ---- the cast ---------------------------------------------------------------------------------

    public static readonly StoryUnit Thresher = new("AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");
    public static readonly StoryUnit Reaper2 = new("AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
    public static readonly StoryUnit Reaper3 = new("AGGRESSOR", "ASSASSIN", 3, "LR_PLASMA_RIFLE_X2");
    public static readonly StoryUnit Commander = new("ADMINISTRATOR", "COMMANDER", 5, "LR_PULSE_CANNON_X2");
    public static readonly StoryUnit HostileGrunt = new("AGGRESSOR", "GRUNT", 1, "R_BOLTER");
    public static readonly StoryUnit Grunt2 = new("AGGRESSOR", "GRUNT", 2, "L_BOLTER");
    public static readonly StoryUnit Grunt3 = new("AGGRESSOR", "GRUNT", 3, "LR_HEAVY_BOLTER1_X2");
    public static readonly StoryUnit Builder = new("CONSTRUCTOR", "BUILDER", 3, null);
    public static readonly StoryUnit TechBoy = new("TECHNICIAN", "TECH_BOY", 3, null);

    /// <summary>The roles the director casts.</summary>
    public const string ThresherRole = "thresher", ReaperRole = "reaper", CommanderRole = "commander", HostileRole = "hostile",
        RowRole = "row", ScareRole = "scare";

    /// <summary>
    /// How ready the Thresher, the Reapers and the Commander stand. The story's sight decides when they see the trio (they pick a
    /// hero out only once they spotted it, GameWorld.StoryHides) and sends them hunting by itself; High keeps the original's
    /// own reactions to what they cannot see out of it. The workshop's armed Grunts stand at Low: they go for what they see.
    /// </summary>
    public const Machines.Sim.World.DefCon Hunters = Machines.Sim.World.DefCon.High;

    // ---- the deck ----------------------------------------------------------------------------------

    /// <summary>The scenario tags of the computer core and of the crate stack that falls in the store room.</summary>
    public const int CoreTag = 400, StackTag1 = 311, StackTag2 = 312;

    /// <summary>Where the trio stands at each checkpoint (Locator, Ike, Bob) and which way they look (degrees).</summary>
    public static readonly IReadOnlyList<(Vector2[] At, float Facing)> Starts =
    [
        ([new(27, 23.5f), new(11, 23.5f), new(42, 23.5f)], 90),
        ([new(44, 37), new(40, 37), new(36, 37)], 0),
        ([new(88, 95), new(86, 92), new(90, 92)], 90),
        ([new(101, 118), new(100, 102), new(96, 102)], 0),
        ([new(180, 104), new(178, 108), new(182, 108)], 0),
        // At the repair station the Grunts stand behind the Locator, east of it: the way out (door U) is west, and between it
        // and the Locator they wedged it in.
        ([new(230, 108), new(232.5f, 105.5f), new(232.5f, 110.5f)], 180),
        ([new(240, 92), new(237.8f, 94), new(242.2f, 93)], 270),
    ];

    /// <summary>The last checkpoint of the prologue.</summary>
    public static int LastCheckpoint => Starts.Count - 1;

    /// <summary>The section (1 to 8) each checkpoint starts in.</summary>
    public static readonly IReadOnlyList<int> SectionAt = [1, 2, 3, 5, 6, 6, 7];

    /// <summary>The seconds on the countdown when the chapter starts again from the last checkpoint (it starts at 180 in the Notgang).</summary>
    public const float CountdownFull = 180, CountdownAtCheckpoint = 150;

    /// <summary>How long the sparks warn of falling debris before it lands, seconds.</summary>
    public const float FallWarning = 1.5f;

    /// <summary>The countdown's stages: the seconds left when each comes.</summary>
    public static readonly IReadOnlyList<float> CountdownStages = [120, 60, 30, 10];

    /// <summary>The prologue's hacks in their order, and the checkpoint from which each is done.</summary>
    public static readonly IReadOnlyList<(string Target, int DoneFrom)> Hacks =
    [
        ("clamp_l7", 1), ("terminal_ike", 1), ("terminal_bob", 1), ("hack_s", 2), ("hack_h", 2), ("hack_core", 3), ("hack_d5", 4),
        ("pod_3_hatch", 99),
    ];

    /// <summary>The doors open from each checkpoint on, by letter.</summary>
    public static readonly IReadOnlyList<string> DoorsOpen = ["", "ABC", "ABCDEH", "ABCDEHIJVKM", "ABCDEHIJVKMQ", "ABCDEHIJVKMQRU", "ABCDEHIJVKMQRUT"];

    /// <summary>The doors that open by themselves as the trio comes, at each checkpoint.</summary>
    public static readonly IReadOnlyList<string> DoorsAuto = ["", "D", "I", "", "R", "T", ""];

    /// <summary>Lines the view does not say once a hack is done, if they still wait on the loudspeakers: the pod's refusal once it is hacked.</summary>
    public static readonly IReadOnlyDictionary<string, string> StaleOnceHacked = new Dictionary<string, string> { ["pod_3_hatch"] = Lines.PodNo };

    /// <summary>The ship's announcements that come round in the first two sections.</summary>
    public static readonly IReadOnlyList<string> Announcements = [Lines.Wake2, Lines.Deck2, Lines.Deck3, Lines.Deck4];

    /// <summary>The line ids the prologue says (tools/voice/lines.json, game/assets/story/prologue.lines.json).</summary>
    public static class Lines
    {
        public const string Wake1 = "P_ANN_WAKE_1", Wake2 = "P_ANN_WAKE_2", Deck1 = "P_ANN_DECK_1", Deck2 = "P_ANN_DECK_2", Deck3 = "P_ANN_DECK_3",
            Deck4 = "P_ANN_DECK_4", Found = "P_ANN_FOUND", Core = "P_ANN_CORE", Auto2 = "P_AUTO_2", Auto3 = "P_AUTO_3", Crash = "P_ANN_CRASH",
            Breach = "P_AUTO_4", T120 = "P_AUTO_T120", T90 = "P_AUTO_T90", T60 = "P_AUTO_T60", T30 = "P_AUTO_T30", T10 = "P_AUTO_T10", PodNo = "P_AUTO_POD_NO",
            Whisper = "P_WHISPER_1", IkeWake = "P_IKE_WAKE", BobWake = "P_BOB_WAKE", IkeSteps = "P_IKE_STEPS", IkeVent = "P_IKE_VENT",
            IkeBarricade = "P_IKE_BARRICADE", BobBarricade = "P_BOB_BARRICADE", IkeAlarm = "P_IKE_ALARM", BobGlitch = "P_BOB_GLITCH",
            IkePod = "P_IKE_POD", BobPod = "P_BOB_POD", HudBoot = "P_HUD_BOOT", HudDecrypt = "P_HUD_DECRYPT";
    }

    // ---- the script -----------------------------------------------------------------------------------

    /// <summary>When the core's alarm breaks the hack off, and how long after it the world's part of "Erwischt" comes (erwischt.cut).</summary>
    public const float DoorBursts = 4.8f, ReapersFire = 9, CoreBlows = 25.5f, PowerFails = 27.4f, ControlLost = 31;

    /// <summary>How long after the launch the chapter is over (the pod bay's part of abwurf.cut).</summary>
    public const float Away = 8;

    /// <summary>
    /// How many seconds after the control is lost the Locator (to reach the vent) and the Grunts (to get into the cable duct)
    /// are still spared the Reapers' fire.
    /// </summary>
    public const float LocatorSpared = 3, GruntsSpared = 8;

    /// <summary>How long the Reapers push at an unheld gate before it gives way (plan WP-H; the plan's 30 s, eased for hands).</summary>
    public const float GateForce = 40;

    /// <summary>
    /// Where the Reapers stand in the core chamber after bursting in through J, firing at the core: round both its sides, where
    /// L-7 at its console sees them past the core (behind it, they were hidden from its eyes for the whole cutscene).
    /// </summary>
    public static readonly string[] ReapersAtCore = ["78,119", "98,119", "99,113"];

    /// <summary>Where the Grunts wait by the cable duct's door during Erwischt, and where they wait at its far end, before D5.</summary>
    public const string IkeAtCable = "100,102", BobAtCable = "96,102", IkeAtD5 = "166,102", BobAtD5 = "161,102";

    /// <summary>Where the Commander sends his Reapers after the core (by way of the spine, P1; by way of the core's east door, P2), before the gates.</summary>
    public const string AtG1 = "164,125.5", AtG2 = "164,109.5";

    /// <summary>The flag of the hunt shut out: the trio is through the crossing into the workshop, and the Commander called his squad off.</summary>
    public const string Sealed = "sealed";

    /// <summary>The objective while a Grunt lies down (<see cref="Then.Rally"/>).</summary>
    public const string RallyText = "Put the fallen Grunt back together";

    private const StoryAudience Bark = StoryAudience.Companion;

    public static readonly IReadOnlyList<StoryBeat> Script =
    [
        // ---- 1 Erwachen: the Locator hangs in its clamp, its HUD boots; it hacks itself free, then Ike and Bob awake.
        new("boot", 0, new Always(), [new Flag(StoryMarks.Boot), new Hold(true, 0), new Say(Lines.HudBoot),
            new Objective("Free yourself from the clamp", "clamp_l7")]),
        new("boot_done", 0, new Since("boot", 6), [new Flag(StoryMarks.Boot, false), new Cue("hint lamp")]),
        new("wake_call", 0, new Since("boot", 3), [new Say(Lines.Wake1)]),
        new("decrypt", 0, new Since("boot", 9), [new Say(Lines.HudDecrypt)]),
        new("free", 0, new HackDone("clamp_l7"), [new Hold(false, 0), new OpenDoors("AB"), new Cue("clamp clamp_l7", "clamp_l7"),
            new Objective("Wake the Grunts in the bays beside yours", "terminal_ike")]),
        new("ike_awake", 0, new HackDone("terminal_ike"), [new Wake(1), new Cue("clamp clamp_ike", "clamp_ike")]),
        new("bob_awake", 0, new HackDone("terminal_bob"), [new Wake(2), new Cue("clamp clamp_bob", "clamp_bob")]),
        new("next_bob", 0, new AllOf(new HackDone("terminal_ike"), new Not(new HackDone("terminal_bob"))), [new Objective("Wake the other Grunt", "terminal_bob")]),
        new("next_ike", 0, new AllOf(new HackDone("terminal_bob"), new Not(new HackDone("terminal_ike"))), [new Objective("Wake the other Grunt", "terminal_ike")]),
        new("grunts_awake", 0, new Always(), [new Checkpoint(1), new Section(2), new OpenDoors("C"), new AutoDoors("D"),
            new Objective("Find a way to the main computer", "enter_deckA"), .. ThresherOnPatrol], After: ["ike_awake", "bob_awake"]),

        // The ship's announcements come round while the decks are dark (sections 1 and 2).
        new("announce", 0, new AllOf(new Since("boot", 45), new AnyOf(new InSection(1), new InSection(2))), [new SayNext("announce", Announcements)],
            Repeat: true, Every: 80),

        // "Das Update will rein" (docs/STORY.md, Horror): the update whispers the unit's code in the Locator's head, the HUD
        // breaking up as it does: once in the dark decks, and once when the Locator is first badly hurt.
        new("whisper", 1, new AllOf(new Since("deck_a", 20), new InSection(2)), [new Say(Lines.Whisper, StoryAudience.Private, 0)]),
        new("whisper_hurt", 5, new AllOf(new Hurt(0.35f), new Not(new InSection(8))), [new Say(Lines.Whisper, StoryAudience.Private, 0)]),

        // ---- 2 Schleichen: through the dark decks past the Thresher, the Locator by the ducts, the Grunts the long way round.
        new("steps", 1, new Since("grunts_awake", 5), [new CueCast("steps", ThresherRole), new Say(Lines.IkeSteps, Bark, 1), new Cue("hint still")]),
        new("deck_a", 1, new InZone("R4", StoryWho.AnyHero), [new Say(Lines.Deck1),
            new Objective("Crawl through the ducts to the switch room", "hack_s"), new Cue("hint pulse")]),
        new("duct", 1, new InZone("V1a"), [new Cue("hint duct")]),
        // Ike's "I don't fit in there" is the companions' own bark, once the Locator crawls off into a duct (CompanionBot).
        new("scare_grunt2", 1, new Near("scare_grunt2", 9), [new CueCast("scare wake", ScareRole), new Lamp(ScareRole, true)]),
        new("scare_dark", 1, new Since("scare_grunt2", 1.5f), [new Lamp(ScareRole, null)]),
        new("dust", 1, new InZone("dust_fall"), [new Cue("scare dust", "dust_fall")]),
        new("false_alarm", 1, new AllOf(new InZone("R5"), new CastNear(ThresherRole, "false_alarm_stack", 30)),
            [new HarmProp(StackTag1), new HarmProp(StackTag2), new Cue("scare crates", "false_alarm_stack")]),
        new("switch_room", 1, new InZone("R7"), [new Objective("Open the service passage for the Grunts", "hack_s")]),
        new("passage", 1, new HackDone("hack_s"), [new OpenDoors("E"), new Objective("Meet on deck B and open the blast door", "hack_h")]),
        new("found", 1, new InZone("R6"), [new Cue("scare found"), new Say(Lines.Found)], After: ["passage"]),
        new("blast_door", 1, new HackDone("hack_h"), [new OpenDoors("H"), new AutoDoors("I"), new Objective("Get into the antechamber", "R8")]),
        new("antechamber", 1, new InZone("R8"), [new Checkpoint(2), new Section(3), new Objective("Hack the main computer", "hack_core")],
            After: ["blast_door"]),

        // ---- 3 Der Hack: the Locator at the core's console, the Grunts barricade the doors with crates and themselves (their
        // barks are the companions'): Ike the north blast door, where the assault comes, Bob the door they came in by.
        new("core_open", 2, new HackOpen("hack_core"), [new Say(Lines.Core), new Assign(1, CompanionTask.Barricade, "J"),
            new Assign(2, CompanionTask.Barricade, "I")]),

        // ---- 4 Erwischt: at 90 % the alarm; the cutscene films what the director does on these ticks (erwischt.cut). The core
        // chamber keeps its light until the core blows; the Grunts leave their barricades for the cable duct's door, spared
        // until they are well inside it.
        new("alarm", 2, new HackAt("hack_core", 1), [new BreakHack("hack_core"), new Alarm(true), new Section(4), new Objective(null),
            new Hold(true, 0), new Spare(0, ControlLost + LocatorSpared), new Spare(1, ControlLost + GruntsSpared), new Spare(2, ControlLost + GruntsSpared),
            new ShieldProp(CoreTag), new Assign(1, CompanionTask.Go, IkeAtCable), new Assign(2, CompanionTask.Go, BobAtCable),
            new Cue("cutscene erwischt", "core"),
            new MoveTo(ThresherRole, ThresherAway), new Spawn(ReaperRole, Reaper3, ["82,137", "94,137"], Hunters),
            new Spawn(ReaperRole, Reaper2, ["100,139"], Hunters),
            new Spawn(CommanderRole, Commander, ["commander_1"], Hunters)]),
        new("unplug", 2, new Since("alarm", 1.2f), [new Unplug("hack_core")]),
        new("door_j", 2,new Since("alarm", DoorBursts), [new OpenDoors("J"), new ClearBarricade("J"), new MoveTo(ReaperRole, ReapersAtCore),
            new MoveTo(CommanderRole, "88,130")]),
        new("fire_core", 2, new Since("alarm", ReapersFire), [new AttackArtefact(ReaperRole, CoreTag)]),
        new("core_blows", 2, new Since("alarm", CoreBlows), [new HarmProp(CoreTag), new Lights("emergency", "core")]),
        new("power_fails", 2, new Since("alarm", PowerFails), [new Lights("emergency", "*")]),
        new("control_lost", 2, new Since("alarm", ControlLost), [new Section(5), new Checkpoint(3), new Hold(false, 0), new OpenDoors("VKM"),
            new Objective("Escape through the vent", "105,118"), new Say(Lines.IkeAlarm, Bark, 1), .. Pursuit]),

        // ---- 5 Die Enge: the Locator crawls ahead and hacks D5 open for the Grunts, who come along the cable duct; the Reapers
        // fire through the grates and push at the gates G1 and G2, which give way unless one of the trio stands on its switch.
        // While the Locator crawls and hacks, Ike holds gate O shut from the cable duct's side (switch_g2k, by the gate's
        // works behind the duct's wall) and Bob waits by D5; gate G, whose switches are all on the crossing's side, is
        // pushed only once D5 is open and the Grunts can get to it.
        new("pursuit_call", 3, new Since("control_lost", 3), [new Say(Lines.Auto2)]),
        new("gates", 3, new Not(new Flagged(Sealed)), [new ForceGates(ReaperRole, GateForce, "O:switch_g2|switch_g2k")], After: ["control_lost"],
            Repeat: true),
        new("gate_g", 3, new Not(new Flagged(Sealed)), [new ForceGates(ReaperRole, GateForce, "G:switch_g1")], After: ["d5"], Repeat: true),
        new("crossing", 3, new InZone("R12"), [new Hackable(true, "hack_d5"), new Objective("Open door D5 for the Grunts", "hack_d5")]),
        new("d5", 3, new HackDone("hack_d5"), [new OpenDoors("Q"), new Objective("Wait for the Grunts at the crossing", "R12"),
            new Assign(1, CompanionTask.HoldSwitch, "switch_g2"), new Assign(2, CompanionTask.HoldSwitch, "switch_g1")]),
        new("together", 3, new InZone("R12", StoryWho.All), [new Checkpoint(4), new Section(6), new AutoDoors("R"),
            new Objective("Get through the workshop", "pickup_bolter"), .. Hostiles], After: ["d5"]),

        // ---- 6 Erste Gegenwehr: the Grunts let go of the gates once the Locator is through; the door shuts the hunt out behind
        // the trio; the weapon on its rack (Use), the armed Grunts, then the repair station.
        new("workshop", 4, new InZone("R13"), [new SayToSeat(Lines.BobGlitch, 2), new Objective("Take the weapon from the rack", "pickup_bolter"),
            new Assign(1, CompanionTask.None), new Assign(2, CompanionTask.None)]),
        new("sealed", 4, new InZone("R13", StoryWho.Everyone), [new Flag(Sealed), new StopGates("GO"),
            new Withdraw(ReaperRole), new Withdraw(CommanderRole)], After: ["workshop"]),
        new("weapon", 4, new Flagged(StoryMarks.Done("pickup_bolter")), [new Cue("pickup bolter", "pickup_bolter"),
            new Objective("Stop the armed Grunts", "R13")]),
        // The firefight brings a crate down off the shelves onto a Grunt: the Locator learns here to put a fallen Grunt together.
        new("knocked", 4, new AllOf(new Since("weapon", 3), new InZone("R13", StoryWho.AnyCompanion)), [new KnockDown("crate_small", 1, "R13", 2, 1)]),
        new("cleared", 4, new Dead(HostileRole), [new AutoDoors("TU"), new Objective("Reach the repair station", "repair_station")], After: ["weapon"]),
        new("repaired", 4, new InZone("repair"), [new Checkpoint(5), new Objective("Get to the escape pods", "R15")]),

        // A Grunt down anywhere is not left behind: the objective points at it until the Locator has put it together.
        new("rally", 1, new Always(), [new Rally(RallyText, GameWorld.PodBay)], After: ["grunts_awake"], Repeat: true),

        // ---- 7 Absturz: the countdown from the Notgang, debris, the ship listing; the pod's hack under the firewall.
        // Its stages at 120, 60, 30 and 10 seconds left (and the hull breach at 90): the automatic voice, the ship listing
        // further (the view's alone), blasts where the ship tears and debris falling into the lanes, warned of by its sparks a
        // moment before it lands; nobody stands where it lies.
        new("notgang", 5, new InZone("R15"), [new Checkpoint(6), new Section(7), new Countdown(CountdownFull), new Say(Lines.Auto3),
            new Hackable(true, "pod_3_hatch"), new Objective("Reach the escape pods", "pod_3_hatch"), new Tilt(5, 0, 3), new Cue("rumble 0.4"),
            new Fall("crate_large", "debris_1", FallWarning, 0, 2.5f)]),
        new("t120", 6, new CountdownAt(120), [new CountdownStage(), new Say(Lines.T120), new Blast("pod_1", 6, 40, 3),
            new Fall("crate_large", "debris_2", FallWarning, 0, -2.5f), new Tilt(7, 1, 2)]),
        new("breach", 6, new CountdownAt(90), [new Say(Lines.Breach), new Blast("236,88", 4, 30, 2.5f), new Cue("rumble 0.7"),
            new Fall("crate_large", "235,40", FallWarning, 30)]),
        new("t60", 6, new CountdownAt(60), [new CountdownStage(), new Say(Lines.T60), new Blast("pod_2", 5, 40, 3),
            new Fall("crate_large", "debris_3", FallWarning, 0, 2.5f), new Tilt(9, -1, 2)]),
        new("t30", 6, new CountdownAt(30), [new CountdownStage(), new Say(Lines.T30), new Blast("254,46", 4, 30, 3),
            new Fall("crate_large", "249,38", FallWarning, -20), new Tilt(12, 2, 2)]),
        new("t10", 6, new CountdownAt(10), [new CountdownStage(), new Say(Lines.T10), new Cue("rumble 1"), new Blast("228,36", 4, 30, 3.5f),
            new Fall("crate_large", "252,38", FallWarning, 35)]),
        new("crash", 6, new CountdownAt(0), [new End(StoryEnding.Crashed)]),
        new("pod_bay", 6, new InZone("R16"), [new Say(Lines.Crash), new Objective("Hack escape pod three", "pod_3_hatch")], After: ["notgang"]),
        new("pod_denied", 6, new AllOf(new Near("pod_3_hatch", 6), new Not(new HackDone("pod_3_hatch"))), [new Say(Lines.PodNo)]),
        new("pod_hacked", 6, new HackDone("pod_3_hatch"), [new Objective("Open the hatch", "pod_3_hatch")]),
        // The hatch open (the Locator's Use, GameWorld.UseForStory), the Grunts board.
        new("hatch", 6, new Flagged(StoryMarks.Done("pod_3_hatch")), [new Objective("Get everyone into the pod", "pod_zone"),
            new Assign(1, CompanionTask.Board, "pod_3_hatch"), new Assign(2, CompanionTask.Board, "pod_3_hatch")], After: ["pod_hacked"]),
        // A Grunt put back together (or one that dropped its job) boards again.
        new("reboard", 6, new Not(new InSection(8)), [new Assign(1, CompanionTask.Board, "pod_3_hatch", IfFree: true),
            new Assign(2, CompanionTask.Board, "pod_3_hatch", IfFree: true)], After: ["hatch"], Repeat: true, Every: 1),

        // ---- 8 Abwurf: the hack done, the hatch opened by the Locator, the Locator at the pod and both Grunts aboard (standing in
        // the pod's zone, or lying down in its bay: act 1 starts with one Grunt broken anyway), the pod drops (abwurf.cut); the
        // chapter is won once the bay's part is over. A Grunt lying down outside the bay must be put together first (the rally
        // above says so).
        new("launch", 6, new AllOf(new InZone("pod_zone"), new Aboard()), [new Section(8), new Countdown(null), new Objective(null),
            new Hold(true, 0, 1, 2), new Spare(0, Away + 5), new Spare(1, Away + 5), new Spare(2, Away + 5), new Cue("cutscene abwurf", "pod_3")],
            After: ["hatch"]),
        new("away", 6, new Since("launch", Away), [new End(StoryEnding.Escaped)]),
    ];

    /// <summary>The Thresher set down on deck A and on its round of both decks (the ring R4, West link, R6, East link).</summary>
    private static StoryAction[] ThresherOnPatrol =>
    [
        new Spawn(ThresherRole, Thresher, ["spawn_thresher"], Hunters),
        new Patrol(ThresherRole, "patrol_thresher"),
    ];

    /// <summary>
    /// Where the Thresher is called to when the alarm goes (the far corner of deck A), off its round: away from the core, so
    /// the cutscene's "reaper" is one of the Reapers coming through J, and out of the trio's way east.
    /// </summary>
    public const string ThresherAway = "patrol_thresher_2";

    /// <summary>
    /// The hunt after the core (plan WP-H): the Commander splits his Reapers, the first half by way of the spine into P1, the
    /// rest by the core's east door into P2, both to the gates at their ends, and takes the spine himself. On the way they hunt
    /// by the story's sight: they stand at the grate windows and fire through, as neither the ducts nor the cable duct let
    /// them in. The Grunts make for the cable duct's far end while the Locator crawls ahead: Ike to gate O's switch on the
    /// duct's side, which he holds, Bob before D5.
    /// </summary>
    private static StoryAction[] Pursuit =>
    [
        new Flank(ReaperRole, ["88,139", "123.5,137"], AtG1, ["100,109"], AtG2),
        new MoveTo(CommanderRole, "commander_2"),
        new Assign(1, CompanionTask.HoldSwitch, "switch_g2k"),
        new Assign(2, CompanionTask.Go, BobAtD5),
    ];

    /// <summary>
    /// The three armed Grunts of the workshop (plan WP-G): already updated, painted as the trio's own, ready to go for what
    /// they see (by the story's sight); two stand their posts, the third walks the room.
    /// </summary>
    private static StoryAction[] Hostiles =>
    [
        new Spawn(HostileRole, HostileGrunt, ["hostile_grunt_1", "hostile_grunt_2", "hostile_grunt_3"], DefCon.Low, Looks: Race.Red),
        new StandFast(HostileRole),
        new PatrolRoute(HostileRole, 2, "hostile_grunt_3", "209,103"),
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

    // ---- the checkpoints ---------------------------------------------------------------------------

    /// <summary>
    /// Builds the world as the chapter has it at a checkpoint: the trio where they stand there (awake or, at the start, Ike
    /// and Bob asleep in their clamps), the hacks done and the doors open so far, the ship's machines that exist there, the
    /// lights, the alarm, the countdown, the section and the objective; and the beats before it counted as fired.
    /// </summary>
    internal static void SetUp(GameWorld world, StoryRun run, int checkpoint)
    {
        var cp = Math.Clamp(checkpoint, 0, LastCheckpoint);
        var director = run.Director;
        void Do(params StoryAction[] actions)
        {
            foreach (var action in actions)
                action.Do(world, run, director);
        }

        director.SkipTo(cp);
        run.Section = SectionAt[cp];
        var (at, facing) = Starts[cp];
        for (var seat = 0; seat < run.Seats.Count; seat++)
        {
            if (world.HeroOf(run.Seats[seat]) is not { } hero)
                continue;
            world.SetHeroForStory(hero, at[seat], facing);
            if (cp == 0 && seat > 0)
            {
                // Asleep in its clamp until the Locator hacks it awake.
                new Sleep(seat).Do(world, run, director);
            }
            else if (seat > 0)
                new Wake(seat).Do(world, run, director);
        }
        foreach (var (target, from) in Hacks)
        {
            if (cp >= from)
                run.Hacks.Done.Add(target);
        }
        if (cp < 4)
            run.Hacks.Locked.Add("hack_d5");
        if (cp < 6)
            run.Hacks.Locked.Add("pod_3_hatch");
        Do(new OpenDoors(DoorsOpen[cp]), new AutoDoors(DoorsAuto[cp]));
        Do(Rows);
        if (cp is 1 or 2)
            Do(ThresherOnPatrol);
        if (cp >= 3)
            Do(new Spawn(ThresherRole, Thresher, [ThresherAway], Hunters));
        if (cp >= 3)
        {
            Do(new RemoveProp(CoreTag), new Lights("emergency", "*"), new Lights("emergency", "core"));
            run.Flags.Add(StoryMarks.Alarm);
        }
        if (cp == 3)
        {
            // As the control is lost: the squad in the core chamber, the trio given its moment to get going.
            Do(new Spawn(ReaperRole, Reaper3, ReapersAtCore[..2], Hunters), new Spawn(ReaperRole, Reaper2, ReapersAtCore[2..], Hunters),
                new Spawn(CommanderRole, Commander, ["88,139"], Hunters));
            Do(Pursuit);
            Do(new Spare(0, LocatorSpared), new Spare(1, GruntsSpared), new Spare(2, GruntsSpared));
        }
        if (cp == 4)
        {
            // All three at the crossing, the Grunts holding the gates, the Reapers at the gates' far side.
            Do(new Spawn(ReaperRole, Reaper3, [AtG1, "154,125.5"], Hunters), new Spawn(ReaperRole, Reaper2, [AtG2], Hunters),
                new Spawn(CommanderRole, Commander, ["commander_2"], Hunters));
            Do(new Assign(1, CompanionTask.HoldSwitch, "switch_g2"), new Assign(2, CompanionTask.HoldSwitch, "switch_g1"));
            Do(Hostiles);
        }
        if (cp >= 5)
        {
            // The Bolter taken from its rack before.
            world.TakeUpForStory(run.Locator, "pickup_bolter");
            run.Flags.Add(Sealed);
        }
        if (cp == LastCheckpoint)
        {
            // The Notgang as a run played through has it: the crate its beat let fall lies in the lane.
            Do(new Countdown(CountdownAtCheckpoint), new Tilt(5, 0, 0.5f), new PlaceProp("crate_large", "debris_1", 0, 2.5f));
        }
        Do(cp switch
        {
            1 => new Objective("Find a way to the main computer", "enter_deckA"),
            2 => new Objective("Hack the main computer", "hack_core"),
            3 => new Objective("Escape through the vent", "105,118"),
            4 => new Objective("Get through the workshop", "pickup_bolter"),
            5 => new Objective("Get to the escape pods", "R15"),
            6 => new Objective("Reach the escape pods", "pod_3_hatch"),
            _ => new Objective(null),
        });
    }
}
