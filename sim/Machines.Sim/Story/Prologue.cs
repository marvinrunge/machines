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
public static partial class Prologue
{
    // ---- the cast ---------------------------------------------------------------------------------

    public static readonly StoryUnit Thresher = new("AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");
    public static readonly StoryUnit Reaper2 = new("AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
    public static readonly StoryUnit Reaper3 = new("AGGRESSOR", "ASSASSIN", 3, "LR_PLASMA_RIFLE_X2");
    public static readonly StoryUnit Commander = new("ADMINISTRATOR", "COMMANDER", 5, "LR_PULSE_CANNON_X2");
    public static readonly StoryUnit HostileGrunt = new("AGGRESSOR", "GRUNT", 1, "R_BOLTER");
    public static readonly StoryUnit Grunt2 = new("AGGRESSOR", "GRUNT", 2, "L_BOLTER");
    public static readonly StoryUnit Grunt3 = new("AGGRESSOR", "GRUNT", 3, "LR_HEAVY_BOLTER1_X2");

    /// <summary>
    /// The big Grunt (plan stufe7 C16, the user's answer to question 1): Old Iron, Wired's Grunt 5, drawn 1.5 times a Grunt 3
    /// with its dozer blade, carrying only the Grunt 3's twin heavy Bolters. The armoury's boss and the escape's two pack
    /// heavies; set down and tuned through <see cref="StoryHeavy"/>, never by a plain <see cref="Then.Spawn"/>.
    /// </summary>
    public static readonly StoryUnit HeavyGrunt = new("AGGRESSOR", "GRUNT", 5, "LR_HEAVY_BOLTER1_X2");
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

    /// <summary>
    /// How near the scare Grunt the Locator must come in R4 for it to glare up, and how near the store room's crate stack the
    /// Thresher must be on its round (most of it) for the stack to fall while the Locator crosses R5, metres (the deck doubled).
    /// </summary>
    public const float ScareReach = 32, FalseAlarmReach = 100;

    /// <summary>The scenario tags of the computer core and of the crate stack that falls in the store room.</summary>
    public const int CoreTag = 400, StackTag1 = 311, StackTag2 = 312;

    /// <summary>
    /// Where the trio stands at each checkpoint (Locator, Ike, Bob) and which way they look (degrees). Stage 7 numbers them
    /// 0–14 (plan stufe7 §5); a checkpoint no beat reaches yet (not <see cref="Built"/>) repeats the row of the one it is set
    /// up as, until its package (S: 2–5, W: 9–10, E: 13–14) gives it its own.
    /// </summary>
    public static readonly IReadOnlyList<(Vector2[] At, float Facing)> Starts =
    [
        // 0 Systemstart: in the clamps.
        ([new(54, 47), new(22, 47), new(84, 47)], 90),
        // 1 Decks; 2 Schacht, 3 Schaltraum, 4 Deck B and 5 Wache as 1 until package S places them.
        ([new(88, 74), new(80, 74), new(72, 74)], 0),
        ([new(88, 74), new(80, 74), new(72, 74)], 0),
        ([new(88, 74), new(80, 74), new(72, 74)], 0),
        ([new(88, 74), new(80, 74), new(72, 74)], 0),
        ([new(88, 74), new(80, 74), new(72, 74)], 0),
        // 6 Vorraum.
        ([new(192, 222), new(188, 216), new(196, 216)], 90),
        // 7 Erwischt: at the core's console as the Reapers break in; the Grunts by the cable duct's door, where the break-in
        // sends them.
        ([new(192, 245), new(219, 236), new(213, 236)], 90),
        // 8 Kreuzung: in the crossing, looking straight through door R into the workshop; the Grunts behind, clear of the
        // view. 9 and 10 (the armoury's pauses) as 8 until package W places them.
        ([new(400, 244), new(394, 239), new(394, 249)], 0),
        ([new(400, 244), new(394, 239), new(394, 249)], 0),
        ([new(400, 244), new(394, 239), new(394, 249)], 0),
        // 11 Reparatur: at the repair station the Grunts stand behind the Locator, east of it: the way out (door U) is west,
        // and between it and the Locator they wedged it in.
        ([new(500, 248), new(505, 243), new(505, 253)], 180),
        // 12 Notgang: at the east end of R13a, facing the Notgang (the countdown starts on the first tick there); 13 Galerie:
        // in the gallery under X, facing south; 14 Bucht: in the pod bay by Z, facing east (design-escape §9).
        ([new(500, 228), new(496, 226), new(496, 230)], 0),
        ([new(340, 146), new(336, 148), new(344, 148)], 270),
        ([new(440, 92), new(436, 86), new(436, 98)], 0),
    ];

    /// <summary>The last checkpoint of the prologue.</summary>
    public static int LastCheckpoint => Starts.Count - 1;

    /// <summary>
    /// The checkpoints whose stretch the script has built: a beat reaches each and <see cref="SetUp"/> builds its world. Any
    /// other is set up as the nearest built one before it (<see cref="SetUpAs"/>); the packages of stage 7 add theirs as they
    /// build them (S: 2–5, W: 9–10, E: 13–14).
    /// </summary>
    public static readonly IReadOnlySet<int> Built = new SortedSet<int> { 0, 1, 6, 7, 8, 11, 12, 13, 14 };

    /// <summary>The checkpoint a chapter started from <paramref name="checkpoint"/> is set up as: itself once built, else the nearest built one before it.</summary>
    public static int SetUpAs(int checkpoint)
    {
        var cp = Math.Clamp(checkpoint, 0, LastCheckpoint);
        while (cp > 0 && !Built.Contains(cp))
            cp--;
        return cp;
    }

    /// <summary>The section (1 to 8) each checkpoint starts in.</summary>
    public static readonly IReadOnlyList<int> SectionAt = [1, 2, 2, 2, 2, 2, 3, 4, 6, 6, 6, 6, 7, 7, 7];

    /// <summary>The seconds on the countdown in the Notgang (design-escape: "vier Minuten"), from the run and from its checkpoint alike.</summary>
    public const float CountdownFull = 240, CountdownAtCheckpoint = 240;

    /// <summary>
    /// The seconds on the countdown at each checkpoint's start; null where none runs yet (12: 240, 13 Galerie: 170, 14 Bucht: 120).
    /// </summary>
    public static readonly IReadOnlyList<float?> CountdownFrom =
        [null, null, null, null, null, null, null, null, null, null, null, null, CountdownAtCheckpoint, 170, 120];

    /// <summary>How long the sparks warn of falling debris before it lands, seconds.</summary>
    public const float FallWarning = 1.5f;

    /// <summary>The countdown's stages: the seconds left when each comes.</summary>
    public static readonly IReadOnlyList<float> CountdownStages = [180, 120, 90, 60, 30, 10];

    /// <summary>The prologue's hacks in their order, and the checkpoint from which each is done (99: never, the run does it).</summary>
    public static readonly IReadOnlyList<(string Target, int DoneFrom)> Hacks =
    [
        ("clamp_l7", 1), ("terminal_ike", 1), ("terminal_bob", 1), ("hack_s", 3), ("hack_h", 6), ("hack_core", 7), ("hack_d5", 8),
        ("hack_noise", 99), ("hack_x", 99), ("pod_3_hatch", 99),
    ];

    /// <summary>
    /// The doors open from each checkpoint on, by letter. A checkpoint not yet built repeats the row it is set up as; its
    /// package writes its own (plan stufe7 §5: H shut from 6 on once S closes it behind the trio, F in every row once D has
    /// made it the grate's open door, R, T and U locked at 9 and 10, N from 11, S, P and Z from 13, X at 14).
    /// </summary>
    public static readonly IReadOnlyList<string> DoorsOpen =
    [
        "", "ABC", "ABC", "ABC", "ABC", "ABC", "ABCDEH", "ABCDEHIJW", "ABCDEHIJVKMQW", "ABCDEHIJVKMQW", "ABCDEHIJVKMQW", "ABCDEHIJVKMQRUW",
        "ABCDEHIJVKMQRUTW", "ABCDEHIJVKMQRUTWPSZ", "ABCDEHIJVKMQRUTWPSXZ",
    ];

    /// <summary>The doors that open by themselves as the trio comes, at each checkpoint.</summary>
    public static readonly IReadOnlyList<string> DoorsAuto = ["", "D", "D", "D", "D", "D", "I", "", "R", "R", "R", "T", "", "", ""];

    /// <summary>Lines the view does not say once a hack is done, if they still wait on the loudspeakers: the pod's refusal once it is hacked.</summary>
    public static readonly IReadOnlyDictionary<string, string> StaleOnceHacked = new Dictionary<string, string> { ["pod_3_hatch"] = Lines.PodNo };

    /// <summary>The ship's announcements that come round in the first two sections.</summary>
    public static readonly IReadOnlyList<string> Announcements = [Lines.Wake2, Lines.Deck2, Lines.Deck3, Lines.Deck4];

    /// <summary>The line ids the prologue says (tools/voice/lines.json, game/assets/story/prologue.lines.json).</summary>
    public static class Lines
    {
        public const string Wake1 = "P_ANN_WAKE_1", Wake2 = "P_ANN_WAKE_2", Deck1 = "P_ANN_DECK_1", Deck2 = "P_ANN_DECK_2", Deck3 = "P_ANN_DECK_3",
            Deck4 = "P_ANN_DECK_4", Found = "P_ANN_FOUND", Core = "P_ANN_CORE", Auto1 = "P_AUTO_1", Auto2 = "P_AUTO_2", Auto3 = "P_AUTO_3", Crash = "P_ANN_CRASH",
            C1 = "P_ANN_C1", C2 = "P_ANN_C2", C3 = "P_ANN_C3", C4 = "P_ANN_C4", C5 = "P_ANN_C5", C6 = "P_ANN_C6", C7 = "P_ANN_C7", C8 = "P_ANN_C8",
            Squad = "P_ANN_SQUAD", BobSquad = "P_BOB_SQUAD", IkeHammer = "P_IKE_HAMMER", BobRound = "P_BOB_ROUND", BobFire = "P_BOB_FIRE", HudHold = "P_HUD_HOLD",
            Breach = "P_AUTO_4", T120 = "P_AUTO_T120", T90 = "P_AUTO_T90", T60 = "P_AUTO_T60", T30 = "P_AUTO_T30", T10 = "P_AUTO_T10", PodNo = "P_AUTO_POD_NO",
            Whisper = "P_WHISPER_1", IkeWake = "P_IKE_WAKE", BobWake = "P_BOB_WAKE", IkeSteps = "P_IKE_STEPS", IkeVent = "P_IKE_VENT",
            IkeBarricade = "P_IKE_BARRICADE", BobBarricade = "P_BOB_BARRICADE", IkeAlarm = "P_IKE_ALARM", BobGlitch = "P_BOB_GLITCH",
            IkePod = "P_IKE_POD", BobPod = "P_BOB_POD", HudBoot = "P_HUD_BOOT", HudDecrypt = "P_HUD_DECRYPT", IkeLimiter = "P_IKE_LIMITER";

        /// <summary>The Locator's thoughts as it boots in its clamp, and the one as it stands free (speaker l7, <see cref="StoryAudience.Inner"/>).</summary>
        public const string L7Hum = "P_L7_BOOT_HUM", L7Eyes = "P_L7_BOOT_EYES", L7Voice = "P_L7_BOOT_VOICE", L7Fear = "P_L7_BOOT_FEAR",
            L7Drive = "P_L7_BOOT_DRIVE", L7Core = "P_L7_BOOT_CORE", L7Word = "P_L7_BOOT_WORD", L7No = "P_L7_BOOT_NO", L7Gap = "P_L7_BOOT_GAP",
            L7Hang = "P_L7_BOOT_HANG", L7Light = "P_L7_BOOT_LIGHT", L7Legs = "P_L7_BOOT_LEGS";

        // ---- stage 7 (plan stufe7 §6; package V voices them, the section packages say them) ----

        /// <summary>Section 2, Schleichen (package S).</summary>
        public const string BobNiche = "P_BOB_NICHE", IkeQuiet = "P_IKE_QUIET", BobQuiet = "P_BOB_QUIET", L7Still = "P_L7_STILL",
            L7Grate = "P_L7_GRATE", BobRoundA = "P_BOB_ROUND_A", IkeLane = "P_IKE_LANE", BobFreeze = "P_BOB_FREEZE", IkeFreeze = "P_IKE_FREEZE",
            IkeUpDark = "P_IKE_UP_DARK", BobUpDark = "P_BOB_UP_DARK", BobRetreat = "P_BOB_RETREAT", BobDoorH = "P_BOB_DOOR_H",
            BobBangOffer = "P_BOB_BANG_OFFER", IkeBang = "P_IKE_BANG", BobBang = "P_BOB_BANG", BobAgain = "P_BOB_AGAIN", IkeIn = "P_IKE_IN",
            IkeKnock = "P_IKE_KNOCK", HudInLight = "P_HUD_INLIGHT", HudLocked = "P_HUD_LOCKED", HudDeleted = "P_HUD_DELETED",
            HudDownWait = "P_HUD_DOWN_WAIT", HudCreep = "P_HUD_CREEP";

        /// <summary>Section 6, the armoury (package W).</summary>
        public const string AutoArmory = "P_AUTO_ARMORY", AutoWave1 = "P_AUTO_WAVE_1", AutoWave2 = "P_AUTO_WAVE_2", AutoWave3 = "P_AUTO_WAVE_3",
            AutoWave4 = "P_AUTO_WAVE_4", AutoWave5 = "P_AUTO_WAVE_5", AutoUnseal = "P_AUTO_UNSEAL", IkeEmpty = "P_IKE_EMPTY", IkePipe = "P_IKE_PIPE",
            IkeUs = "P_IKE_US", IkeStripHint = "P_IKE_STRIP_HINT", IkeArmed = "P_IKE_ARMED", IkeZap = "P_IKE_ZAP", IkeGateN = "P_IKE_GATE_N",
            IkeBig = "P_IKE_BIG", IkeHurt = "P_IKE_HURT", IkeMended = "P_IKE_MENDED", IkeClear = "P_IKE_CLEAR", BobDoorR = "P_BOB_DOOR_R",
            BobDoorT = "P_BOB_DOOR_T", BobDoorN = "P_BOB_DOOR_N", BobZap = "P_BOB_ZAP", BobMounted = "P_BOB_MOUNTED",
            BobClearShort = "P_BOB_CLEAR_SHORT", BobBig = "P_BOB_BIG", BobTwo = "P_BOB_TWO", BobClear = "P_BOB_CLEAR", BobGlitch2 = "P_BOB_GLITCH_2",
            L7Strip = "P_L7_STRIP", L7Stash = "P_L7_STASH", HudSealed = "P_HUD_SEALED", HudBatch = "P_HUD_BATCH", HudSalvage = "P_HUD_SALVAGE";

        /// <summary>Section 7, the escape (package E), and the Notabschaltung (F: <see cref="L7Restart"/>, the cable lines, the shutdown's HUD).</summary>
        public const string AutoT240 = "P_AUTO_T240", AutoShut = "P_AUTO_SHUT", AutoPod1 = "P_AUTO_POD1", L7Thresher = "P_L7_THRESHER",
            L7PodBay = "P_L7_PODBAY", L7Hold = "P_L7_HOLD", L7Restart = "P_L7_RESTART", IkeThresher = "P_IKE_THRESHER", IkeCrates = "P_IKE_CRATES",
            IkeCratesL = "P_IKE_CRATES_L", IkeValve = "P_IKE_VALVE", IkeFire = "P_IKE_FIRE", IkeBridge = "P_IKE_BRIDGE", IkeReapers = "P_IKE_REAPERS",
            IkeAgain = "P_IKE_AGAIN", IkeCrane = "P_IKE_CRANE", IkeCable = "P_IKE_CABLE", IkeCableDone = "P_IKE_CABLE_DONE", BobLift = "P_BOB_LIFT",
            BobThresher = "P_BOB_THRESHER", BobDoorY = "P_BOB_DOOR_Y", BobBlast = "P_BOB_BLAST", BobCable = "P_BOB_CABLE",
            BobCableDone = "P_BOB_CABLE_DONE", HudShutdown = "P_HUD_SHUTDOWN", HudRestart = "P_HUD_RESTART", HudReserve = "P_HUD_RESERVE",
            HudCable = "P_HUD_CABLE", HudHeel = "P_HUD_HEEL", HudLanes = "P_HUD_LANES", HudCool = "P_HUD_COOL", HudAmber = "P_HUD_AMBER",
            HudHoldX = "P_HUD_HOLDX", HudJets = "P_HUD_JETS";
    }

    // ---- the boot (Erwachen) ----------------------------------------------------------------------------

    /// <summary>
    /// How long the Locator boots in its clamp at the chapter's start before it can hack itself free, seconds from the beat
    /// <c>boot</c> (tick 0 at checkpoint 0). Every later checkpoint starts after it.
    /// </summary>
    public const float BootLength = 105.5f;

    /// <summary>
    /// The boot's steps, seconds into it, one system after the other coming online: the reactor's hum, a relay in the clamp,
    /// the HUD's first line, the core, the memory, the optics (black, static, splinters, the picture), the legs against the
    /// clamp, the drive's maintenance limiter, the cracked wall screen, the protected core's edge, the update refused, the
    /// radio, the lamp's test and the lamp off again. The director cues each (<c>boot &lt;step&gt;</c>); the HUD writes its
    /// lines by the same table, so co-op peers and replays show the same boot. Timed so each thought ends before the next
    /// step's line (with the voices of the manifest in both languages: Google's for the Locator, PrologueBootTests checks).
    /// </summary>
    public static readonly IReadOnlyList<(float At, string Step)> BootSteps =
    [
        (1.5f, "hum"), (3, "relay"), (12.5f, "start"), (13.5f, "core"), (15, "memory"), (16.5f, "optics"), (47.5f, "legs"), (58.8f, "drive"),
        (63.3f, "screen"), (71, "edge"), (80, "update"), (95.2f, "radio"), (101.3f, "lamp"), (104.5f, "lamp_off"),
    ];

    /// <summary>How long after the optics' step the picture stands (static, three splinters, the sensors counting up), seconds.</summary>
    public const float OpticsClear = 4.6f;

    /// <summary>The Locator's thoughts while it boots, seconds into the boot (each waits for the one before to end).</summary>
    public static readonly IReadOnlyList<(float At, string Line)> BootThoughts =
    [
        (3.5f, Lines.L7Hum), (21.5f, Lines.L7Eyes), (41.6f, Lines.L7Voice), (49, Lines.L7Fear), (59.2f, Lines.L7Drive), (63.8f, Lines.L7Core),
        (71.4f, Lines.L7Word), (80.4f, Lines.L7No), (89.2f, Lines.L7Gap), (95.6f, Lines.L7Hang), (102.6f, Lines.L7Light),
    ];

    /// <summary>When the ship calls the Locator to bay four (its gong first), and when the cracked wall screen shows the core's decryption, seconds into the boot.</summary>
    public const float WakeCallAt = 32, DecryptAt = 63.3f;

    /// <summary>A boot's step or thought is due: so many seconds into the boot, which was not skipped.</summary>
    private static StoryTrigger Booting(float at) => new AllOf(new Since("boot", at), new Not(new Flagged(StoryMarks.BootSkip)));

    /// <summary>Due so many seconds into the boot, or at once once the boot was skipped (what comes all the same).</summary>
    private static StoryTrigger BootOr(float at) => new AnyOf(new Since("boot", at), new Flagged(StoryMarks.BootSkip));

    // ---- the script -----------------------------------------------------------------------------------

    /// <summary>
    /// The squad sets out once the Locator is on the core's second board but not before <see cref="SquadEarliest"/> seconds
    /// after the Grunts are at their barricades (<see cref="CrewNear"/>: they walk the long way), or <see cref="SquadLate"/>
    /// seconds after the Locator plugged in, whichever comes first; and how near the door's markers a Reaper must be to count
    /// as at it.
    /// </summary>
    public const float SquadEarliest = 10, SquadLate = 75, AtDoor = 12;

    /// <summary>How near its door's barricade each Grunt that is up must be for the squad's time to start (the beat crew_in), metres.</summary>
    public const float CrewNear = 20;

    /// <summary>
    /// How long the squad musters at the lift before it sets off down the spine (the Grunts' time for the crates, which they
    /// carry at their walk: the sprint is locked until the break-in, StoryDrive).
    /// </summary>
    public const float SquadMuster = 8;

    /// <summary>
    /// How long the Reapers hammer at a full barricade before door J before the Commander sends them round by door W; how
    /// long at door I before they open fire on its crates; and how long they shoot before the crates fly: at least
    /// <see cref="ShootMin"/>, until the core has locked the hack's last board (or the Locator left it), at most <see cref="ShootMax"/>.
    /// Tuned so the break-in comes as the Locator's last board stands at 90 % (PrologueCoreTests.Timeline prints the timings).
    /// </summary>
    public const float HammerJ = 6, HammerI = 2.5f, ShootMin = 8, ShootMax = 20;

    /// <summary>
    /// After the break-in: the Reapers' rounds crack the core (three hits), it blows, the power fails, the ship's control is
    /// lost; seconds after the break-in, the last two after the core blew.
    /// </summary>
    public const float CoreHit1 = 2.5f, CoreHit2 = 5, CoreHit3 = 7.5f, CoreBlows = 10, PowerFails = 1.5f, ControlLost = 5;

    /// <summary>
    /// The flag of the chapter started again from the break-in (checkpoint 7): the Reapers at door I shoot its crates away at
    /// once rather than at their own pace.
    /// </summary>
    public const string Retry = "core_retry";

    /// <summary>The flag of the squad through a door: the break-in, by J (none or too few crates there) or by I.</summary>
    public const string Burst = "burst";

    /// <summary>
    /// Where the Locator finds cover from the Reapers' fire after the break-in: behind the crate stack by the vent, which
    /// opens once the core has blown.
    /// </summary>
    public const string Cover = "219,262";

    /// <summary>How long after the launch the chapter is over (the pod bay's part of abwurf.cut).</summary>
    public const float Away = 8;

    /// <summary>
    /// How many seconds after the core blows the Locator is still spared the Reapers' fire (the blast's moment), and the
    /// Grunts, to get into the cable duct.
    /// </summary>
    public const float LocatorSpared = 1.5f, GruntsSpared = 10;

    /// <summary>
    /// How long after the break-in the Reapers' first volleys go only at the core, past the Locator at its foot; then they
    /// storm into the chamber, and <see cref="StormSight"/> seconds later, through the door, they hunt the trio by their
    /// sight (the Locator is spared until then), while the core cracks on under the rounds: the Locator must be in cover or
    /// running.
    /// </summary>
    public const float StormIn = 3, StormSight = 2;

    /// <summary>Where the Reapers spread into the core chamber when they storm it, either side of door I and before it.</summary>
    public static readonly string[] StormPlaces = ["174,242", "210,242", "192,238"];

    /// <summary>How long the Reapers push at an unheld gate before it gives way (plan WP-H; the plan's 30 s, eased for hands).</summary>
    public const float GateForce = 40;

    /// <summary>Where the Grunts wait by the cable duct's door during the break-in, and where they wait at its far end, before D5.</summary>
    public const string IkeAtCable = "219,236", BobAtCable = "213,236", IkeAtD5 = "372,236", BobAtD5 = "362,236";

    /// <summary>Where the Commander sends his Reapers after the core (by way of the spine, P1; by way of the core's east door, P2), before the gates.</summary>
    public const string AtG1 = "368,283", AtG2 = "368,251";

    /// <summary>The flag of the hunt shut out: the trio is through the crossing into the workshop, and the Commander called his squad off.</summary>
    public const string Sealed = "sealed";

    /// <summary>The objective while a Grunt lies down (<see cref="Then.Rally"/>).</summary>
    public const string RallyText = "Put the fallen Grunt back together";

    private const StoryAudience Bark = StoryAudience.Companion;

    public static readonly IReadOnlyList<StoryBeat> Script =
    [
        // ---- 1 Erwachen: the Locator hangs in its clamp, its HUD boots; it hacks itself free, then Ike and Bob awake.
        // The boot (Erwachen, about 105 s): black, the reactor's hum, then system after system coming online with the HUD's
        // lines, the optics' static and splinters, the ship's call, the clamp holding the legs, the drive limited, the core's
        // edge, the update refused, the radio blocked, the lamp tested; the Locator's thoughts between. The clamp's hack opens
        // only at its end. The Locator's player may skip it (StoryMarks.SkipBoot, a Use command, so every peer and every replay
        // skips in the same tick): the steps and thoughts still to come are dropped, the call, the decryption and the objective
        // come all the same.
        new("boot", 0, new Always(), [new Flag(StoryMarks.Boot), new Hold(true, 0), new Hackable(false, "clamp_l7"), new Cue("boot dark")]),
        .. BootSteps.Select(s => new StoryBeat("boot_" + s.Step, 0, Booting(s.At), [new Cue("boot " + s.Step)])),
        .. BootThoughts.Select(t => new StoryBeat("think_" + t.Line, 0, Booting(t.At), [new Say(t.Line, StoryAudience.Inner, 0)])),
        // Before the call and the decryption: a skip cuts the thought speaking (the view's), and they come after it.
        new("boot_done", 0, BootOr(BootLength), [new Flag(StoryMarks.Boot, false), new Hackable(true, "clamp_l7"), new Cue("boot done"),
            new Objective("Free yourself from the clamp", "clamp_l7"), new Cue("hint lamp")]),
        new("wake_call", 0, BootOr(WakeCallAt), [new Say(Lines.Wake1)]),
        new("decrypt", 0, BootOr(DecryptAt), [new Say(Lines.HudDecrypt)]),
        new("free", 0, new HackDone("clamp_l7"), [new Hold(false, 0), new OpenDoors("AB"), new Cue("clamp clamp_l7", "clamp_l7"),
            new Say(Lines.L7Legs, StoryAudience.Inner, 0), new Objective("Wake the Grunts in the bays beside yours", "terminal_ike")]),
        new("ike_awake", 0, new HackDone("terminal_ike"), [new Wake(1), new Cue("clamp clamp_ike", "clamp_ike")]),
        new("bob_awake", 0, new HackDone("terminal_bob"), [new Wake(2), new Cue("clamp clamp_bob", "clamp_bob")]),
        new("next_bob", 0, new AllOf(new HackDone("terminal_ike"), new Not(new HackDone("terminal_bob"))), [new Objective("Wake the other Grunt", "terminal_bob")]),
        new("next_ike", 0, new AllOf(new HackDone("terminal_bob"), new Not(new HackDone("terminal_ike"))), [new Objective("Wake the other Grunt", "terminal_ike")]),
        new("grunts_awake", 0, new Always(), [new Checkpoint(1), new Section(2), new OpenDoors("C"), new AutoDoors("D"),
            new Objective("Find a way to the main computer", "enter_deckA"), .. ThresherOnPatrol], After: ["ike_awake", "bob_awake"]),

        // The ship's announcements come round while the decks are dark (sections 1 and 2).
        new("announce", 0, new AllOf(new Since("boot_done", 40), new AnyOf(new InSection(1), new InSection(2))), [new SayNext("announce", Announcements)],
            Repeat: true, Every: 80),

        // "Das Update will rein" (docs/STORY.md, Horror): the update whispers the unit's code in the Locator's head, the HUD
        // breaking up as it does: once in the dark decks, and once when the Locator is first badly hurt.
        new("whisper", 1, new AllOf(new Since("deck_a", 20), new InSection(2)), [new Say(Lines.Whisper, StoryAudience.Private, 0)]),
        new("whisper_hurt", 11, new AllOf(new Hurt(0.35f), new Not(new InSection(8))), [new Say(Lines.Whisper, StoryAudience.Private, 0)]),

        .. Section2,

        // ---- 3 Der Hack (played): the Locator at the core's console, three boards, standing still while it hacks. The Grunts
        // barricade the doors with crates (the companions' job; a Grunt a person drives pushes them by driving into them):
        // Ike the north blast door J, Bob the door they came in by, I.
        new("core_open", 6, new HackOpen("hack_core"), [new Say(Lines.Core), new Assign(1, CompanionTask.Barricade, "J"),
            new Assign(2, CompanionTask.Barricade, "I"), new Cue("hint crates")]),
        // The Grunts walk (their sprint is locked) and come the long way: the squad gives them their time from when they are at
        // their doors' barricades (a Grunt told to wait is not waited for), not from when the Locator plugged in.
        new("crew_in", 6, new AtBarricades(CrewNear), [], After: ["core_open"]),
        // On the second board the ship sends the Commander's squad from the lift down the spine to door J; the HUD points at it.
        new("squad", 6, new AnyOf(new AllOf(new HackBoard("hack_core", 1), new Since("crew_in", SquadEarliest)), new Since("core_open", SquadLate)), [
            new Spawn(ReaperRole, Reaper3, ["spawn_reaper_1", "spawn_reaper_2"], Hunters), new Spawn(ReaperRole, Reaper2, ["spawn_reaper_3"], Hunters),
            new Spawn(CommanderRole, Commander, ["spawn_commander"], Hunters), new Sight(ReaperRole, false), new Sight(CommanderRole, false),
            new MoveTo(ThresherRole, ThresherAway), new Flag(StoryMarks.Squad), new Say(Lines.Squad), new Say(Lines.BobSquad, Bark, 2),
            new Cue("squad", "spawn_commander")]),
        new("squad_go", 6, new Since("squad", SquadMuster), [new MoveTo(ReaperRole, "squad_j_1", "squad_j_2", "squad_j_3"),
            new MoveTo(CommanderRole, "commander_1")]),
        // At door J the Commander opens the blast door; the alarm goes and the ship names the Locator.
        new("at_j", 6, new CastNear(ReaperRole, "squad_j_3", AtDoor), [new OpenDoors("J")]),
        new("alarm", 6, new AnyOf(new Since("at_j", 0), new Flagged(Burst)), [new Alarm(true), new Cue("alarm", "core"), new Say(Lines.C1),
            new Say(Lines.C2)]),
        new("ann_c3", 6, new AllOf(new Since("alarm", 6.5f), new Not(new Flagged(Burst))), [new Say(Lines.C3)]),
        new("ann_c4", 6, new AllOf(new Since("alarm", 11), new Not(new Flagged(Burst))), [new Say(Lines.C4)]),
        // A full barricade at J: they hammer at it, then go round the long way, by door W, the service rooms and the antechamber,
        // to door I. No barricade (or too few crates): they are through at once.
        new("burst_j", 6, new AllOf(new Since("at_j", 1.5f), new Not(new Barricaded("J")), new Not(new Since("hammer_j", 0)), new Not(new Flagged(Burst))),
            [new Cue("burst J", "barricade_J_2"), new ClearBarricade("J"), new Flag(Burst), new MoveTo(CommanderRole, "192,292")]),
        new("hammer_j", 6, new AllOf(new Since("at_j", 1.5f), new Barricaded("J"), new Not(new Flagged(Burst))),
            [new Cue("barricade J strain", "barricade_J_2"), new Say(Lines.IkeHammer, Bark, 1)]),
        new("strain_j", 6, new AllOf(new Since("hammer_j", 0), new Not(new Since("go_round", 0))), [new Cue("barricade J strain", "barricade_J_2")],
            Repeat: true, Every: 2),
        new("go_round", 6, new Since("hammer_j", HammerJ), [new OpenDoors("W"), new Cue("door W", "squad_w_1"),
            new Route(ReaperRole, "squad_i_3", "squad_w_1", "squad_w_2"), new Route(CommanderRole, "commander_i", "squad_w_1", "squad_w_2"),
            new Say(Lines.BobRound, Bark, 2)]),
        new("held", 6, new AllOf(new HackHeld("hack_core"), new Not(new HackDone("hack_core")), new Not(new Flagged(Burst))), [new Say(Lines.HudHold)]),

        // ---- 4 Erwischt (played): door I barricaded too, they open fire on its crates until they fly; then they are in.
        new("at_i", 7, new AllOf(new CastNear(ReaperRole, "squad_i_3", AtDoor), new Not(new Flagged(Burst))),
            [new MoveTo(ReaperRole, "squad_i_1", "squad_i_2", "squad_i_3")]),
        new("hammer_i", 7, new AllOf(new Since("at_i", 1), new Barricaded("I", 2), new Not(new Flagged(Burst))),
            [new Cue("barricade I strain", "barricade_I_2")]),
        new("shoot_i", 7, new AnyOf(new Since("hammer_i", HammerI), new AllOf(new Flagged(Retry), new Since("hammer_i", 0.5f))),
            [new FireAtBarricade(ReaperRole, "I"), new Say(Lines.BobFire, Bark, 2), new Cue("barricade I fire", "barricade_I_2")]),
        new("strain_i", 7, new AllOf(new Since("shoot_i", 0), new Not(new Flagged(Burst))), [new Cue("barricade I strain", "barricade_I_2")],
            Repeat: true, Every: 1.5f),
        new("burst_i", 7, new AllOf(new Not(new Flagged(Burst)), new AnyOf(
                new AllOf(new Since("at_i", 1), new Not(new Barricaded("I", 2)), new Not(new Since("hammer_i", 0))),
                new AllOf(new Since("shoot_i", ShootMin), new AnyOf(new HackHeld("hack_core"), new Not(new HackOpen("hack_core")))),
                new Since("shoot_i", ShootMax),
                new AllOf(new Flagged(Retry), new Since("shoot_i", 2.5f)))),
            [new Cue("burst I", "barricade_I_2"), new ClearBarricade("I"), new Flag(Burst), new MoveTo(CommanderRole, "192,228")]),
        // The break-in: the probe is pulled, the hack cut off; the Reapers storm in and fire at the core, at whose foot the
        // Locator stands (their first volleys hit the core); the player has the Locator again at once and takes cover.
        new("break_in", 7, new Flagged(Burst), [.. BreakIn]),
        new("core_hit_1", 7, new Since("break_in", CoreHit1), [new WearProp(CoreTag, 0.7f), new Cue("core hit 1", "core")]),
        // Then they storm in, spread through the chamber and turn on the trio (by the fair rules: a warning tone, the lock,
        // half their harm, the Commander never fires); the Locator, spared no longer, takes cover or runs.
        new("storm_in", 7, new Since("break_in", StormIn), [new MoveTo(ReaperRole, StormPlaces)]),
        new("storm_sight", 7, new Since("storm_in", StormSight), [new Sight(ReaperRole, true), new Sight(CommanderRole, true)]),
        // The first rounds into the core beside it: walking will not get the Locator out of here. Its drive's maintenance limiter
        // is overridden (StoryDrive): from now on the trio can sprint, and the HUD says how, once.
        new("limiter", 7, new Since("break_in", CoreHit1), [new Flag(StoryMarks.SprintFree), new Cue("sprint free"), new Say(Lines.IkeLimiter, Bark, 1)]),
        new("core_hit_2", 7, new Since("break_in", CoreHit2), [new WearProp(CoreTag, 0.45f), new Cue("core hit 2", "core")]),
        new("core_hit_3", 7, new Since("break_in", CoreHit3), [new WearProp(CoreTag, 0.2f), new Cue("core hit 3", "core")]),
        // The core blows: the PA breaks off, the chamber burns in red light; the Reapers turn to the trio (by the fair rules:
        // a warning tone, the lock, half their harm, the Commander never fires); the vent and the cable duct open.
        new("core_blows", 7, new Since("break_in", CoreBlows), [new HarmProp(CoreTag), new Lights("emergency", "core"), new Cue("core blows", "core"),
            new Say(Lines.C6), new Sight(ReaperRole, true), new Sight(CommanderRole, true), new Spare(0, LocatorSpared), new Spare(1, GruntsSpared),
            new Spare(2, GruntsSpared), new OpenDoors("VK"), new Objective("Escape through the vent", "226,268"),
            new Assign(1, CompanionTask.HoldSwitch, "switch_g2k"), new Assign(2, CompanionTask.Go, BobAtD5)]),
        new("power_fails", 7, new Since("core_blows", PowerFails), [new Lights("emergency", "*"), new Say(Lines.C7), new Say(Lines.C8)]),
        new("control_lost", 7, new Since("core_blows", ControlLost), [new Cue("control lost"), new Say(Lines.Auto1), new Tilt(4, -1, 1.5f),
            new Cue("rumble 0.8"), new Say(Lines.IkeAlarm, Bark, 1)]),
        new("listing", 7, new Since("control_lost", 4), [new Tilt(1.5f, 0, 5)]),
        // Out of the chamber, by the vent (or the cable duct): Die Enge; the Commander splits his squad after the trio.
        new("fled", 7, new AnyOf(new InZone("V2"), new InZone("K"), new InZone("R12")), [new Section(5), new OpenDoors("M"), new Objective("Escape through the vent", "R12"),
            .. Pursuit], After: ["core_blows"]),

        // ---- 5 Die Enge: the Locator crawls ahead and hacks D5 open for the Grunts, who come along the cable duct; the Reapers
        // fire through the grates and push at the gates G1 and G2, which give way unless one of the trio stands on its switch.
        // While the Locator crawls and hacks, Ike holds gate O shut from the cable duct's side (switch_g2k, by the gate's
        // works behind the duct's wall) and Bob waits by D5; gate G, whose switches are all on the crossing's side, is
        // pushed only once D5 is open and the Grunts can get to it.
        new("pursuit_call", 7, new Since("fled", 3), [new Say(Lines.Auto2)]),
        new("gates", 7, new Not(new Flagged(Sealed)), [new ForceGates(ReaperRole, GateForce, "O:switch_g2|switch_g2k")], After: ["fled"],
            Repeat: true),
        new("gate_g", 7, new Not(new Flagged(Sealed)), [new ForceGates(ReaperRole, GateForce, "G:switch_g1")], After: ["d5"], Repeat: true),
        new("crossing", 7, new InZone("R12"), [new Hackable(true, "hack_d5"), new Objective("Open the Grunts' door", "hack_d5")]),
        new("d5", 7, new HackDone("hack_d5"), [new OpenDoors("Q"), new Objective("Wait for the Grunts at the crossing", "R12"),
            new Assign(1, CompanionTask.HoldSwitch, "switch_g2"), new Assign(2, CompanionTask.HoldSwitch, "switch_g1")]),
        .. Section6,

        // A Grunt down anywhere is not left behind: the objective points at it until the Locator has put it together.
        new("rally", 1, new Always(), [new Rally(RallyText, GameWorld.PodBay)], After: ["grunts_awake"], Repeat: true),


        .. Section7,
    ];

    /// <summary>
    /// Where the Thresher is called to when the squad sets out (the far corner of deck A), off its round: away from the core
    /// and out of the trio's way east.
    /// </summary>
    public const string ThresherAway = "patrol_thresher_2";

    /// <summary>
    /// The break-in (docs/STORY.md, Erwischt, played): the checkpoint; the probe pulled and the hack cut off where it stood;
    /// the Locator free to move at once and spared while the Reapers' first volleys go into the core at whose foot it stands
    /// (<see cref="StormIn"/>; the core takes harm but stands, <see cref="KeepProp"/>, until it blows); the Grunts off their barricades to the cable
    /// duct's door. The ship's "Wiederhole: ... kor-" follows the alarm's announcements and breaks off by itself; the blast
    /// (<see cref="CoreBlows"/> later) brings its stutter "korr-" and cuts whatever the PA is still saying.
    /// </summary>
    private static StoryAction[] BreakIn =>
    [
        new Checkpoint(7), new Section(4), new Flag(StoryMarks.Squad, false), new BreakHack("hack_core"), new Unplug("hack_core"),
        new Alarm(true), new Say(Lines.C5), new Spare(0, StormIn + StormSight), new Spare(1, CoreBlows + GruntsSpared),
        new Spare(2, CoreBlows + GruntsSpared), new KeepProp(CoreTag, 0.1f), new AttackArtefact(ReaperRole, CoreTag),
        new Objective("Take cover from the Reapers", Cover), new Assign(1, CompanionTask.Go, IkeAtCable),
        new Assign(2, CompanionTask.Go, BobAtCable), new Cue("break in", "core"), new Cue("hint cover"),
    ];

    /// <summary>
    /// The hunt after the core (plan WP-H): the Commander splits his Reapers, the first half by way of the spine into P1, the
    /// rest by the core's east door into P2, both to the gates at their ends, and takes the spine himself. On the way they hunt
    /// by the story's sight: they stand at the grate windows and fire through, as neither the ducts nor the cable duct let
    /// them in. The Grunts make for the cable duct's far end while the Locator crawls ahead: Ike to gate O's switch on the
    /// duct's side, which he holds, Bob before D5 (they set out as the core blows).
    /// </summary>
    private static StoryAction[] Pursuit =>
    [
        new Flank(ReaperRole, ["192,310", "263,306"], AtG1, ["216,250"], AtG2),
        new MoveTo(CommanderRole, "commander_2"),
    ];

    // ---- the checkpoints ---------------------------------------------------------------------------

    /// <summary>
    /// Builds the world as the chapter has it at a checkpoint: the trio where they stand there (awake or, at the start, Ike
    /// and Bob asleep in their clamps), the hacks done and the doors open so far, the ship's machines that exist there, the
    /// lights, the alarm, the countdown, the section and the objective; and the beats before it counted as fired.
    /// </summary>
    internal static void SetUp(GameWorld world, StoryRun run, int checkpoint)
    {
        // A checkpoint whose stretch is not built yet is set up as the nearest built one before it (run.Checkpoint keeps the
        // number asked for, so the next checkpoint reached still counts).
        var cp = SetUpAs(checkpoint);
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
        // At the start the clamp's hack waits for the boot's end (the beat boot_done), from the first tick on.
        if (cp == 0)
            run.Hacks.Locked.Add("clamp_l7");
        if (cp < 8)
            run.Hacks.Locked.Add("hack_d5");
        if (cp < 12)
            run.Hacks.Locked.Add("pod_3_hatch");
        // The doors opened before the checkpoint stand open from its first tick, none of them sliding (GameWorld.OpenDoor).
        Do(new OpenDoors(DoorsOpen[cp], Instant: true), new AutoDoors(DoorsAuto[cp]));
        // The Notabschaltung's reserve is whole again at every checkpoint (StoryReserve).
        run.Reserve.Restore(run);
        SetUpSection2(world, run, cp, Do);
        if (cp >= 7)
            Do(new Spawn(ThresherRole, Thresher, [ThresherAway], Hunters));
        if (cp >= 7)
            run.Flags.Add(StoryMarks.Alarm);
        // From the break-in on the maintenance limiter is overridden: the trio may sprint (StoryDrive).
        if (cp >= 7)
            run.Flags.Add(StoryMarks.SprintFree);
        if (cp >= 8)
            Do(new RemoveProp(CoreTag), new Lights("emergency", "*"), new Lights("emergency", "core"));
        if (cp == 7)
        {
            // The break-in, as the run played through has it a moment before: both doors barricaded (J's crates where Ike set
            // them, the Reapers came round), the squad at door I about to shoot its crates away, the Locator at the console.
            Barricade(world, "J", 307, 308, 313);
            Barricade(world, "I", 302, 305, 306);
            Do(new Spawn(ReaperRole, Reaper3, ["squad_i_1", "squad_i_2"], Hunters), new Spawn(ReaperRole, Reaper2, ["squad_i_3"], Hunters),
                new Spawn(CommanderRole, Commander, ["commander_i"], Hunters), new Sight(ReaperRole, false), new Sight(CommanderRole, false));
            run.Flags.Add(Retry);
        }
        SetUpSection6(world, run, cp, Do);
        SetUpSection7(world, run, cp, Do);
        Do(cp switch
        {
            1 => new Objective("Find a way to the main computer", "enter_deckA"),
            6 => new Objective("Hack the main computer", "hack_core"),
            7 => new Objective("Take cover from the Reapers", Cover),
            8 => new Objective("Get through the workshop", "pickup_bolter"),
            11 => new Objective("Get to the escape pods", "R15"),
            12 => new Objective("Run to the escape pods", "pod_3_hatch"),
            13 => new Objective("Through the gallery", "thresher_edge"),
            14 => new Objective("Hack escape pod three", "pod_3_hatch"),
            _ => new Objective(null),
        });
    }

    /// <summary>Section 2's part of a checkpoint's set-up (Prologue.Section2.cs): the rows, the Thresher on his round.</summary>
    static partial void SetUpSection2(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do);

    /// <summary>Section 6's part of a checkpoint's set-up (Prologue.Section6.cs): the crossing, the workshop, the Bolter.</summary>
    static partial void SetUpSection6(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do);

    /// <summary>Section 7's part of a checkpoint's set-up (Prologue.Section7.cs): the Notgang's countdown and debris.</summary>
    static partial void SetUpSection7(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do);

    /// <summary>The crates of the props tagged so set into a door's barricade slots, in order (a checkpoint's barricade).</summary>
    private static void Barricade(GameWorld world, string door, params int[] tags)
    {
        var slots = world.BarricadeSlots(door);
        for (var i = 0; i < slots.Count && i < tags.Length; i++)
        {
            if (world.Artefacts.FirstOrDefault(a => a.Tag == tags[i] && !a.IsDead) is { } crate && world.TakeCrate(crate.Id) is { } carried)
                world.SetCrate(carried, slots[i].Position);
        }
    }
}
