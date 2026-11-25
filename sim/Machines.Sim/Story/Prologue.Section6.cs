using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Then;
using static Machines.Sim.Story.When;

namespace Machines.Sim.Story;

// Section 6 (plan stufe7 §4 W, design-waves): the armoury. L-7 takes the Bolter from its rack first; that trips the ship's
// security, the room bolts shut and the barracks sends its freshly updated recruits down in five batches, the last a heavy
// Grunt (Old Iron) that bursts gate N. Between batches L-7 mends and puts together its Grunts, strips the dead for their
// Bolters and mounts them on Ike and Bob; at the end it marks a Grunt 2's drive and an electric charge for act 2.
public static partial class Prologue
{
    /// <summary>The batches' electric units: a Grunt 1 and a Grunt 2 with an electric charge (WiredCatalogue's own combos).</summary>
    public static readonly StoryUnit GruntZap = new("AGGRESSOR", "GRUNT", 1, "R_ELECTRIC_CHARGE");
    public static readonly StoryUnit Grunt2Zap = new("AGGRESSOR", "GRUNT", 2, "L_ELECTRIC_CHARGE");

    /// <summary>The armoury's zone (the defence area) and the trio's defence posts at checkpoints 9 and 10.</summary>
    public const string ArmouryZone = "armoury";

    /// <summary>The armoury's hints for the Locator alone (Game.StoryArmoury shows them only to its player).</summary>
    private static Cue LocatorHint(string word) => new($"armoury hint {word}");

    /// <summary>Section 6's beats, from <c>together</c> to <c>repaired</c> (<see cref="Script"/> joins them in order).</summary>
    private static StoryBeat[] Section6 =>
    [
        new("together", 7, new InZone("R12", StoryWho.All), [new Checkpoint(8), new Section(6), new AutoDoors("R"),
            new Objective("Get through the workshop", "pickup_bolter")], After: ["d5"]),
        // The waves look every tick of the section (StoryArmoury): the pause's bar, the batch's spawns, the boss's charge.
        new("armoury", 8, new InSection(6), [new Armoury()], Repeat: true),

        // ---- 4.0 Ankunft: the room lit, quiet and empty; one gun on a rack. Obviously bait.
        new("workshop", 8, new InZone("R13"), [new SayToSeat(Lines.BobGlitch, 2), new Objective("Take the weapon from the rack", "pickup_bolter"),
            new Assign(1, CompanionTask.None), new Assign(2, CompanionTask.None)]),
        new("empty", 8, new Since("workshop", 5), [new SayToSeat(Lines.IkeEmpty, 1)]),
        new("sealed", 8, new InZone("R13", StoryWho.Everyone), [new Flag(Sealed), new StopGates("GO"),
            new Withdraw(ReaperRole), new Withdraw(CommanderRole)], After: ["workshop"]),

        // ---- 4.1 The weapon, then the lockdown: the room bolts shut, the Notabschaltung is on (question 4), batch 1's bar runs.
        new("weapon", 8, new Flagged(StoryMarks.Done("pickup_bolter")), [new Cue("pickup bolter", "pickup_bolter")]),
        new("lockdown", 8, new AnyOf(new InZone(ArmouryZone, StoryWho.Everyone), new Since("weapon", 20)), [new Say(Lines.AutoArmory),
            new Say(Lines.HudSealed), new Cue("lockdown", "R13"), new CloseDoors("RTU"), new LockDoors("RTU"), new Flag(StoryMarks.ArmourySealed),
            new Reserve(true), new Objective("Hold the armoury", "R13"), new Batch(1)], After: ["weapon", "sealed"]),
        new("pipe", 8, new Since("lockdown", 2.5f), [new SayToSeat(Lines.IkePipe, 1)]),

        // ---- 4.2 Batch 1, the lesson: a crate off a shelf downs a Grunt, L-7 learns to put it together under fire.
        new("wave_1", 8, new WaveOn(1), []),
        new("knocked", 8, new AllOf(new Since("wave_1", 6), new InZone(ArmouryZone, StoryWho.AnyCompanion)), [new KnockDown("crate_small", 1, ArmouryZone, 2, 1)]),
        new("us", 8, new AllOf(new Since("wave_1", 16), new Not(new WaveClear(1))), [new SayToSeat(Lines.IkeUs, 1)]),
        // ---- 4.3 Pause 1, the workbench's lesson: strip, mount, mend.
        new("strip_hint", 8, new WaveClear(1), [new SayToSeat(Lines.IkeStripHint, 1), LocatorHint("strip")]),
        new("mount_hint", 8, new Flagged(CarriedOnce), [LocatorHint("mount")]),
        new("mend_hint", 8, new Since("strip_hint", 10), [LocatorHint("mend")]),
        // ---- 4.4 Batch 2: the first electric unit.
        new("zap", 8, new Flagged(StoryArmoury.ZapSeen), [new SayToSeat(Lines.IkeZap, 1), new Cue("hint zap")]),
        new("zap_bob", 8, new Since("zap", 1.5f), [new SayToSeat(Lines.BobZap, 2)]),

        // ---- 4.5–4.8 Pause 2 (checkpoint 9), batch 3 through gate N, pause 3, batch 4 by all three doors.
        new("gate_lights", 9, new BarAt(3, 0.7f), [new Cue("gate N lights", "wave_n_1")]),
        new("wave_3", 9, new WaveOn(3), []),
        new("gate_n", 9, new Since("wave_3", 2), [new SayToSeat(Lines.IkeGateN, 1)]),
        new("wave_4", 9, new WaveOn(4), []),
        new("lurch", 9, new AllOf(new Since("wave_4", 20), new Not(new WaveClear(4))), [new Tilt(3, 1, 2), new Cue("rumble 0.5")]),

        // ---- 4.9 Pause 4 (checkpoint 10), the dread: gate N bulges, the mass is named, the Grunts get clear of it.
        new("strain_1", 10, new BarAt(5, 0.5f), [new Cue("gate N strain 1", "wave_n_1"), new SayToSeat(Lines.BobBig, 2)]),
        new("strain_2", 10, new BarAt(5, 0.75f), [new Cue("gate N strain 2", "wave_n_1")]),
        new("strain_3", 10, new BarAt(5, 0.9f), [new Cue("gate N strain 3", "wave_n_1"), new Assign(1, CompanionTask.Go, "def_2", IfFree: true),
            new Assign(2, CompanionTask.Go, "def_4", IfFree: true)]),
        // ---- 4.10 Batch 5, the heavy Grunt: gate N bursts (StoryArmoury), its charge (GameWorld.StoryCharge).
        new("boss_in", 10, new WaveOn(5), [new SayToSeat(Lines.IkeBig, 1), new Cue("hint charge"), new Cue("boss in", "boss_n"),
            new Assign(1, CompanionTask.None), new Assign(2, CompanionTask.None)]),
        new("boss_half", 10, new AllOf(new WaveOn(5), new Not(new WaveClear(5)), new CastHurt(StoryHeavy.BossRole, 0.5f)), [new Escort(), new SayToSeat(Lines.BobTwo, 2)]),
        new("boss_quarter", 10, new AllOf(new WaveOn(5), new Not(new WaveClear(5)), new CastHurt(StoryHeavy.BossRole, 0.75f)), [new CueCast("boss smoke", StoryHeavy.BossRole)]),
        new("boss_down", 10, new AllOf(new WaveOn(5), new Dead(StoryHeavy.BossRole)), [new Cue("boss down")]),

        // ---- 4.11 Afterwards: the power goes in section six, the bolts let go; strip the marked wrecks and out.
        new("unseal", 10, new WaveClear(5), [new Say(Lines.AutoUnseal), new Cue("power dip", "R13"), new UnlockDoors("RTU"),
            new Flag(StoryMarks.ArmourySealed, false), new MarkWrecks()]),
        new("clear_bob", 10, new Since("unseal", 4), [new SayToSeat(Lines.BobClear, 2)]),
        // The old flow keys on "cleared": the doors on open as the trio comes, the repair station next.
        new("cleared", 10, new Since("unseal", 0), [new AutoDoors("TU"), new Objective("Reach the repair station", "repair_station"),
            LocatorHint("salvage")], After: ["unseal"]),
        new("glitch_2", 10, new Since("cleared", 8), [new SayToSeat(Lines.BobGlitch2, 2)]),
        new("stash", 10, new Salvaged(), [new Say(Lines.HudSalvage), new Say(Lines.L7Stash, StoryAudience.Inner, 0)], After: ["cleared"]),
        new("repaired", 10, new InZone("repair"), [new Checkpoint(11), new Objective("Get to the escape pods", "R15")], After: ["cleared"]),
    ];

    /// <summary>Flag: L-7 carried a stripped Bolter for the first time (the mount hint).</summary>
    public const string CarriedOnce = "armoury:carried";

    /// <summary>
    /// Section 6's part of a checkpoint's set-up: the crossing (8) with the gates held; the armoury's pauses 2 and 4 (9, 10)
    /// sealed, with both Grunts armed as a played run has them and the wrecks of the batches before; from 9 on the Bolter
    /// taken from its rack and mounted on Ike and Bob (C17); from 11 on gate N burst.
    /// </summary>
    static partial void SetUpSection6(GameWorld world, StoryRun run, int cp, Action<StoryAction[]> Do)
    {
        if (cp == 8)
        {
            // All three at the crossing, the Grunts holding the gates, the Reapers at the gates' far side.
            Do([new Spawn(ReaperRole, Reaper3, [AtG1, "348,283"], Hunters), new Spawn(ReaperRole, Reaper2, [AtG2], Hunters),
                new Spawn(CommanderRole, Commander, ["commander_2"], Hunters)]);
            Do([new Assign(1, CompanionTask.HoldSwitch, "switch_g2"), new Assign(2, CompanionTask.HoldSwitch, "switch_g1")]);
        }
        if (cp >= 9)
        {
            // The Bolter taken from its rack, and the two the Locator stripped mounted on its Grunts (C17).
            world.TakeUpForStory(run.Locator, "pickup_bolter");
            run.Flags.Add(Sealed);
            for (var i = 1; i < run.Seats.Count; i++)
            {
                if (!world.MountBolter(run.Seats[i]))
                    continue;
                run.Waves.Mounted.Add(run.Seats[i].Value);
                // The refit took its order: it follows again, as at any checkpoint.
                Do([new Wake(i)]);
            }
        }
        if (cp is 9 or 10)
        {
            // At the defence posts (Starts). Sealed: R, T and U shut and locked, N shut (it opened in batches 3 and 4), the reserve whole.
            Do([new LockDoors("RTU"), new Flag(StoryMarks.ArmourySealed), new Reserve(true)]);
            // The wrecks of the batches before, their Bolters stripped (a Grunt 2 and an electric one left for the stash at 10).
            world.StoryWreckAt(StoryWaveKind.Bolter, 1, StoryAction.Where(world, "wreck_cp_1"), stripped: true);
            world.StoryWreckAt(StoryWaveKind.Bolter, 2, StoryAction.Where(world, "wreck_cp_2"), stripped: true);
            if (cp == 10)
            {
                world.StoryWreckAt(StoryWaveKind.Tank, 4, StoryAction.Where(world, "wreck_cp_3"), stripped: true);
                world.StoryWreckAt(StoryWaveKind.Zap, 4, StoryAction.Where(world, "wreck_cp_4"), stripped: false);
            }
            run.Waves.Cleared = cp == 9 ? 2 : 4;
            Do([new Batch(cp == 9 ? 3 : 5)]);
        }
        if (cp >= 11)
        {
            // The boss burst gate N; the batches are over.
            Do([new BlowDoor('N')]);
            run.Waves.Cleared = StoryWaves.Plan.Count;
        }
    }
}

public static partial class When
{
    /// <summary>The pause before batch <paramref name="Wave"/> runs and its bar stands at <paramref name="Share"/> or more.</summary>
    public sealed record BarAt(int Wave, float Share) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            !run.Waves.Fighting && run.Waves.Wave == Wave && run.Waves.Cleared == Wave - 1 && run.Flags.Contains(StoryMarks.ArmourySealed)
            && run.Waves.Bar(world.Time) >= Share;
    }
}
