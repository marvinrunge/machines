using System.Globalization;
using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Sim.Story;

// Section 7, the Absturz-Lauf (plan stufe7 §4 package E, design-escape): four minutes from the Notgang to the pod. The pack
// bursts from the freight lift (five Reapers, two of Old Iron, the Thresher), the hull tears open ahead and the ship blows
// door P into the cargo hold; containers slide across the hold with every roll; the shutter X lowers by itself and Bob holds
// it; the gallery's jets fire; the footbridge drifts and falls behind the trio; in the pod bay the survivors blast through
// the Notgang and Bob holds door Y while the Locator hacks the pod. Checkpoints 12 (Notgang), 13 (Galerie), 14 (Bucht).
public static partial class Prologue
{
    /// <summary>The run's flag of door Y given way (the pack in the bay).</summary>
    public const string YBroken = "y_broken";

    /// <summary>Where the survivors wait north of the collapse once the bridge fell, out of sight; and where they go through Y.</summary>
    private static readonly Vector2[] RoundWaits = [new(520, 158), new(515, 166), new(525, 166)];

    private static readonly Vector2 AtY = new(520, 128);

    /// <summary>The countdown's seconds left now (0 when none runs).</summary>
    private static float SecondsLeft(GameWorld world, StoryRun run) =>
        run.Timers.TryGetValue(StoryMarks.Countdown, out var ends) ? MathF.Max(0, ends - world.Time) : 0;

    /// <summary>The cue the music anchors Pressure At Sector Nine to (package M): the seconds left when it starts.</summary>
    private static Run Score => new("score", (world, run, _) =>
        StoryEvents.Cue(world, "score escape " + ((int)MathF.Round(SecondsLeft(world, run))).ToString(CultureInfo.InvariantCulture)));

    private static Run Muster => new("muster", (world, run, _) => world.MusterPack(run));

    /// <summary>
    /// The escape's start (C0, design-escape §3): the countdown, the flags of the escape and the Notabschaltung, door Y shut
    /// and locked, the hold's rails, the pod's hack open, the lift groaning ahead. The beat <c>escape</c> does it in a run,
    /// the checkpoint 12's set-up from its first tick (the lift and the pack then play from the start, as in a run).
    /// </summary>
    private static StoryAction[] EscapeStart =>
    [
        new Section(7), new Flag(StoryMarks.Escape), new Reserve(true), new Countdown(CountdownFull), new Say(Lines.AutoT240),
        new CloseDoors("Y"), new LockDoors("Y"), new Run("rails", (world, run, _) => world.StartRails(run)),
        new Hackable(true, "pod_3_hatch"), new Objective("Run to the escape pods", "pod_3_hatch"), new Tilt(5, 0, 3),
        new Cue("rumble 0.4"), new Cue("playlist fade 2"), new Cue("lift groan", "520,262"),
    ];

    /// <summary>Section 7's beats and the launch of 8 (<see cref="Script"/> joins them in order).</summary>
    private static StoryBeat[] Section7 =>
    [
        // ---- C0, R13a: "vier Minuten". The countdown, the lift groaning ahead, the Thresher's steps in its shaft.
        new("escape", 11, new AllOf(new AnyOf(new InZone("R13a"), new InZone("R15")), new Not(new InSection(8))), [new Checkpoint(12), .. EscapeStart]),
        // The escape's hazards and its pack, every tick while it runs (GameWorld.StoryHazards, GameWorld.StoryPack).
        new("escape_tick", 12, new Flagged(StoryMarks.Escape), [new Run("escape", (world, run, _) => world.UpdateEscape(run))], Repeat: true),
        new("lift_bulge", 12, new Since("escape", 3), [new Cue("lift bulge", "520,258"), new Say(Lines.IkeThresher, Bark, 1),
            new Say(Lines.L7Thresher, StoryAudience.Inner, 0)]),
        // The lift's door bursts, the music starts on it; the Reapers first, the big Grunts, last the Thresher.
        new("lift_burst", 12, new Since("escape", 6), [new BlowDoor('S'), new Blast("520,266", 3, 0, 6), Score,
            new Spawn(StoryPack.R3a, Reaper3, ["516,266"], Hunters), new Spawn(StoryPack.R3b, Reaper3, ["524,266"], Hunters),
            new Spawn(StoryPack.R2a, Reaper2, ["516,274"], Hunters), new Spawn(StoryPack.R2b, Reaper2, ["524,274"], Hunters),
            new Spawn(StoryPack.R2c, Reaper2, ["520,280"], Hunters), Muster, new Say(Lines.BobLift, Bark, 2), new Cue("hint sprint")]),
        new("heavies_out", 12, new Since("lift_burst", 2), [new SpawnHeavy(StoryHeavy.PackRole1, StoryHeavyPart.Pack, ["516,272"]),
            new SpawnHeavy(StoryHeavy.PackRole2, StoryHeavyPart.Pack, ["524,272"]), Muster]),
        new("thresher_out", 12, new Since("lift_burst", 4), [new Run("thresher", (world, run, director) => PlaceThresher(world, run, director, new Vector2(520, 270))),
            Muster, new Say(Lines.BobThresher, Bark, 2), new Cue("thresher roar", "520,262")]),
        // C1, the Notgang: debris ahead (a show, beside the lanes, never on anyone), plates behind on the pack.
        new("slalom", 12, new Since("lift_burst", 0.5f), [new Fall("crate_large", "526,204", FallWarning, 0, 0, 0),
            new Fall("crate_large", "514,186", FallWarning + 1.2f, 0, 0, 0)]),
        new("chain_r2c", 12, new Since("lift_burst", 4), [new Run("plate", (world, run, _) => world.FallOn(run, StoryPack.R2c))]),
        new("chain_heavy", 12, new Since("heavies_out", 4), [new Run("plate", (world, run, _) => world.FallOn(run, StoryHeavy.PackRole1))]),
        // The hull breach, by position: sparks race along the ceiling ahead, then the Notgang caves in south of P, the fire face
        // burns at its edge and the ship blows door P into the hold: the way on.
        new("breach_warn", 12, new Near("520,192", 9, StoryWho.AnyHero), [new Cue("breach warn", "520,170")], After: ["escape"]),
        new("breach", 12, new AllOf(new Since("breach_warn", 2), new Check("nobody beyond", (world, run) => world.Collapse(run))),
            [new BlowDoor('P'), new Blast("520,140", 4, 0, 8), new Say(Lines.Breach), new Cue("rumble 0.8"), new Tilt(7, 1, 2),
            new Objective("Through the cargo hold", "x_frame")]),
        new("crash_call", 12, new Since("breach", 3), [new Say(Lines.Crash)]),
        new("podbay_call", 12, new Since("crash_call", 4), [new Say(Lines.L7PodBay, StoryAudience.Everyone, 0)]),
        // C2, the hold: the rolls start; the hint once.
        new("hold_entry", 12, new InZone("R17", StoryWho.AnyHero), [new Run("rolls", (world, run, _) => world.StartRolls(run)), new Say(Lines.HudLanes)],
            After: ["breach"]),
        // C3, the shutter X: it lowers by itself as the leader reaches the west landing; Bob holds it in its frame.
        new("shut_x", 12, new Check("west landing", (world, run) => world.EscapeHeroes(run).Any(h => h.Position.X < 384 && h.Position.Y > 132 && h.Position.Y < 212)),
            [new Say(Lines.AutoShut), new Run("shutter", (world, run, _) => { world.CloseShutter(run); world.StartJets(run); }),
            new Cue("shutter klaxon", "350,144"), new Assign(2, CompanionTask.HoldFrame, "x_frame"), new Say(Lines.HudHoldX, StoryAudience.Private, 2)],
            After: ["hold_entry"]),
        new("galerie", 12, new InZone("R18", StoryWho.All), [new Checkpoint(13), new Objective("Through the gallery", "thresher_edge")]),
        // C4, the gallery: J1's show at the entry (the hush), the jets' rhythm, Ike on J4's valve; P_AUTO_3 at its true time.
        new("j1", 13, new InZone("R18", StoryWho.AnyHero), [new Run("j1", (world, run, _) => world.ShowJ1(run)), new Cue("hush 1.5 -20"),
            new Say(Lines.HudJets)]),
        new("t180", 12, new CountdownAt(180), [new CountdownStage(), new Say(Lines.Auto3)]),
        new("valve_job", 13, new Near("jet_4", 26), [new Assign(1, CompanionTask.Valve, "valve_j4"), new Say(Lines.IkeValve, Bark, 1)]),
        new("thresher_fire", 13, new Check("thresher in fire", (world, run) => ThresherInFire(world, run)), [new Say(Lines.IkeFire, Bark, 1)]),
        // C5, the footbridge: it drifts everyone toward the north railing; the Thresher stops at its head; it falls behind the trio.
        new("bridge", 13, new InZone("R19", StoryWho.AnyHero), [new Run("drift", (_, run, _) => run.Hazards.Drift = true), new Tilt(12, 0, 2)]),
        new("bridge_edge", 13, new CastNear(ThresherRole, "thresher_edge", 14), [new HoldAt(ThresherRole, "thresher_edge"), new Say(Lines.IkeBridge, Bark, 1)],
            After: ["bridge"]),
        new("bridge_fall", 13, new InZone("R16", StoryWho.All), [new Cue("hush 1 -20"), new Run("bridge", (world, run, _) => world.LetBridgeFall(run))]),
        new("bucht", 13, new AllOf(new InZone("R16", StoryWho.All), new Not(new HackDone("pod_3_hatch"))), [new Checkpoint(14), new Objective("Hack escape pod three", "pod_3_hatch")]),
        new("pack_round", 13, new Check("bridge fallen", (_, run) => run.Hazards.BridgeFallen), [new HoldAt(ThresherRole, "thresher_edge"),
            new Run("round", (world, run, _) => world.SendPackRound(run, RoundWaits))]),
        // C6, the bay: pod 1 fails, off the lane.
        new("pod1", 14, new InZone("pod1_pass", StoryWho.AnyHero), [new Say(Lines.AutoPod1), new Cue("pod1 alarm", "pod_1")]),
        new("pod1_drop", 14, new Since("pod1", 3), [new Blast("pod_1", 10, 0, 8), new Cue("pod1 drop", "pod_1")]),
        // The countdown's stages by the clock: lines, the lean, blasts off the route.
        new("t120", 14, new CountdownAt(120), [new CountdownStage(), new Say(Lines.T120), new Tilt(7, 1, 2)]),
        new("t90", 14, new CountdownAt(90), [new CountdownStage(), new Say(Lines.T90), new Cue("rumble 0.6")]),
        new("t60", 14, new CountdownAt(60), [new CountdownStage(), new Say(Lines.T60), new Tilt(9, -1, 2)]),
        new("t30", 14, new CountdownAt(30), [new CountdownStage(), new Say(Lines.T30), new Blast("596,108", 6, 0, 6), new Tilt(12, 2, 2)]),
        new("t10", 14, new CountdownAt(10), [new CountdownStage(), new Say(Lines.T10), new Cue("rumble 1")]),
        new("crash", 14, new CountdownAt(0), [new End(StoryEnding.Crashed)]),
        new("pod_bay", 13, new AllOf(new InZone("R16"), new Not(new HackDone("pod_3_hatch"))), [new Objective("Hack escape pod three", "pod_3_hatch")], After: ["escape"]),
        new("pod_denied", 14, new AllOf(new Near("pod_3_hatch", 10), new Not(new HackDone("pod_3_hatch"))), [new Say(Lines.PodNo)]),
        // C7: at a quarter of the hack the survivors blast through the Notgang and come to Y; Bob holds it, Ike barricades.
        new("blast_collapse", 14, new HackAt("pod_3_hatch", 0.25f), [new Blast("520,146", 4, 0, 8), new Cue("rumble 0.6"),
            new Run("reopen", (world, run, _) => { world.Reopen(run); world.LetPackThrough(run, AtY); }), new Say(Lines.BobBlast, Bark, 2)],
            After: ["pack_round"]),
        new("at_y", 14, new Check("pack at Y", (world, run) => world.PackAlive(run).Any(m => Vector2.Distance(m.Position, AtY) <= 16)),
            [new Say(Lines.IkeReapers, Bark, 1), new Assign(2, CompanionTask.HoldSwitch, "y_switch"), new Assign(1, CompanionTask.Barricade, "Y")],
            After: ["blast_collapse"]),
        new("hold_call", 14, new Since("at_y", 2.5f), [new Say(Lines.L7Hold, StoryAudience.Everyone, 0)]),
        new("again", 14, new Since("hold_call", 1.5f), [new Say(Lines.IkeAgain, Bark, 1)]),
        new("door_held", 14, new Check("Y held", (world, run) => world.HeroOf(run.Seats[^1]) is { } bob
            && Vector2.Distance(bob.Position, world.Deck!.Marker("y_switch").Position) <= GameWorld.UseReach + bob.Radius),
            [new Say(Lines.BobDoorY, Bark, 2)], After: ["at_y"]),
        new("y_press", 14, new Check("Y gives", (world, run) => world.PressY(run)), [new BlowDoor('Y'), new Flag(YBroken), new Cue("door Y burst", "520,122")],
            After: ["blast_collapse"]),
        new("pod_hacked", 14, new HackDone("pod_3_hatch"), [new Objective("Open the hatch", "pod_3_hatch")]),
        // The hatch open (the Locator's Use, GameWorld.UseForStory), the Grunts board; Bob lets go of Y and it gives 3 s later.
        new("hatch", 14, new Flagged(StoryMarks.Done("pod_3_hatch")), [new Objective("Get everyone into the pod", "pod_zone"),
            new Assign(1, CompanionTask.Board, "pod_3_hatch"), new Assign(2, CompanionTask.Board, "pod_3_hatch")], After: ["pod_hacked"]),
        new("y_gives", 14, new AllOf(new Since("hatch", 3), new Not(new Flagged(YBroken)), new Check("pack there", (world, run) => run.Pack.GoneRound)),
            [new BlowDoor('Y'), new Flag(YBroken), new Cue("door Y burst", "520,122")]),
        new("y_broken", 14, new Flagged(YBroken), []),
        new("pod2", 14, new AnyOf(new Since("y_broken", 2), new CountdownAt(45)), [new Blast("pod_2", 10, 0, 8), new Cue("pod2 drop", "pod_2")]),
        // The Commander steps into Y's frame and raises his cannons; he never fires (design-escape §14, question 5).
        new("commander", 14, new AllOf(new Flagged(YBroken), new Flagged(StoryMarks.Done("pod_3_hatch"))),
            [new Spawn(CommanderRole, Commander, ["commander_y"], Hunters, Inert: true), new MoveTo(CommanderRole, "520,121"), new Cue("commander cameo", "520,124")]),
        // A Grunt put back together (or one that dropped its job) boards again.
        new("reboard", 14, new Not(new InSection(8)), [new Assign(1, CompanionTask.Board, "pod_3_hatch", IfFree: true),
            new Assign(2, CompanionTask.Board, "pod_3_hatch", IfFree: true)], After: ["hatch"], Repeat: true, Every: 1),

        // ---- 8 Abwurf: the hack done, the hatch opened by the Locator, the Locator at the pod and both Grunts aboard (standing in
        // the pod's zone, or lying down in its bay: act 1 starts with one Grunt broken anyway), the pod drops (abwurf.cut); the
        // chapter is won once the bay's part is over. A Grunt lying down outside the bay must be put together first (the rally
        // above says so).
        new("launch", 14, new AllOf(new InZone("pod_zone"), new Aboard()), [new Section(8), new Countdown(null), new Objective(null),
            new Flag(StoryMarks.Escape, false), new Reserve(false), new Cue("launch"),
            new Hold(true, 0, 1, 2), new Spare(0, Away + 5), new Spare(1, Away + 5), new Spare(2, Away + 5), new Cue("cutscene abwurf", "pod_3")],
            After: ["hatch"]),
        new("away", 14, new Since("launch", Away), [new End(StoryEnding.Escaped)]),
    ];

    /// <summary>Whether the Thresher walks through a jet that burns (Ike's line).</summary>
    private static bool ThresherInFire(GameWorld world, StoryRun run)
    {
        if (run.Director.Of(world, ThresherRole).FirstOrDefault() is not { } thresher || world.Deck is not { } deck)
            return false;
        for (var jet = 1; jet <= 5; jet++)
        {
            if (run.Hazards.Burns(jet, world.Tick) && deck.Zone($"jet_j{jet}") is { } zone && GameWorld.Overlaps(zone, thresher.Position, thresher.Radius))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The Thresher joins the pack (C5): the same cast throughout, set down at <paramref name="at"/> (spawned only if he is
    /// missing), seeing, hunting, unkillable as before.
    /// </summary>
    private static void PlaceThresher(GameWorld world, StoryRun run, StoryDirector director, Vector2 at)
    {
        if (director.Of(world, ThresherRole).FirstOrDefault() is { } thresher)
        {
            world.SetHeroForStory(thresher, world.FreeSpotNear(at, MathF.Max(1, thresher.Radius)), 270);
            world.StoryWatch(thresher).Blind = false;
        }
        else
            director.Act(world, run, new Spawn(ThresherRole, Thresher, [FormattableString.Invariant($"{at.X},{at.Y}")], Hunters));
        director.Posts.Remove(ThresherRole);
        foreach (var t in director.Of(world, ThresherRole))
            t.DefCon = Hunters;
    }

    /// <summary>Section 7's part of a checkpoint's set-up: the Galerie (13) and the Bucht (14) as a run that reached them has them.</summary>
    static partial void SetUpSection7(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do)
    {
        if (cp == 12)
            Do(EscapeStart);
        if (cp < 13)
            return;
        var director = run.Director;
        run.Flags.Add(StoryMarks.Escape);
        Do([new Reserve(true), new Countdown(CountdownFrom[cp]), new CloseDoors("Y"), new LockDoors("Y"),
            new Run("rails", (w, r, _) => w.StartRails(r)), new Hackable(true, "pod_3_hatch"), Score]);
        world.CloseDoor('Y');
        world.Collapse(run);
        world.StartJets(run);
        if (cp == 13)
        {
            // Behind the shut shutter: the pack as a run that reached the gallery has it; the Thresher rams X in a moment.
            world.CloseDoor('X');
            run.Hazards.Shutter = ShutterState.Shut;
            run.Hazards.ShutterDown = StoryHazards.ShutterLower;
            Do([new Spawn(StoryPack.R3a, Reaper3, ["372,192"], Hunters), new Spawn(StoryPack.R3b, Reaper3, ["378,198"], Hunters),
                new Spawn(StoryPack.R2b, Reaper2, ["370,182"], Hunters), new Spawn(StoryPack.R2c, Reaper2, ["378,176"], Hunters),
                new SpawnHeavy(StoryHeavy.PackRole1, StoryHeavyPart.Pack, ["368,204"]), new SpawnHeavy(StoryHeavy.PackRole2, StoryHeavyPart.Pack, ["376,150"])]);
            Do([new Run("thresher", (w, r, d) => PlaceThresher(w, r, d, new Vector2(364, 146))), Muster]);
            foreach (var heavy in director.Of(world, StoryHeavy.PackRole1))
                heavy.HitPoints = (int)(heavy.MaxHitPoints * 0.55f);
            return;
        }
        // The Bucht: the bridge down, the Thresher held at its head, the survivors at the collapse waiting for the hack.
        run.Hazards.Shutter = ShutterState.Broken;
        run.Hazards.BridgeFallAt = world.Tick;
        run.Hazards.BridgeFallen = true;
        if (world.Deck?.Zone("R19") is { } bridge)
            world.Nav.SetArea([bridge.Min, new(bridge.Max.X, bridge.Min.Y), bridge.Max, new(bridge.Min.X, bridge.Max.Y)], present: true);
        Do([new Spawn(StoryPack.R3a, Reaper3, ["520,158"], Hunters), new Spawn(StoryPack.R2c, Reaper2, ["515,166"], Hunters),
            new SpawnHeavy(StoryHeavy.PackRole2, StoryHeavyPart.Pack, ["525,166"])]);
        Do([new Run("thresher", (w, r, d) => PlaceThresher(w, r, d, world.Deck!.Marker("thresher_edge").Position)), Muster,
            new HoldAt(ThresherRole, "thresher_edge")]);
        run.Pack.GoneRound = true;
        foreach (var member in world.PackAlive(run).Where(m => run.Pack.Members[m.Id] != PackKind.Thresher).ToList())
        {
            director.Held.Add(member.Id);
            world.StoryWatch(member).Blind = true;
        }
    }
}

public static partial class When
{
    /// <summary>A test of the escape's own, said in code (the trio past a line, the bridge fallen, door Y giving way).</summary>
    public sealed record Check(string What, Func<GameWorld, StoryRun, bool> Test) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => Test(world, run);
    }
}
