using System.Numerics;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// The trio's drives in a story (<see cref="StoryDrive"/>): they walk at a share of their top speed and sprint at the whole
/// of it while the sprint is held and free, the drive heating as they do. Machines without a drive (every machine of any
/// other match, and the ship's machines of a story but its seeing hunters while the sprint is locked) go at their pace of 1.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The drive of one of the trio's heroes; null for any other machine, and outside a story.</summary>
    public StoryDrive? StoryDriveOf(int hero) => Story?.Drives.Of.GetValueOrDefault(hero);

    /// <summary>
    /// The share of its top speed and acceleration a machine goes at now: a hero of the trio its drive's; one of the ship's
    /// seeing hunters (the Thresher on its round) <see cref="StoryDrive.Walk"/> too while the sprint is locked, so the dark
    /// decks' rounds and chases keep the measure they had to the trio's pace (a walking Grunt still outgoes the Thresher, as it
    /// did before); 1 for every other machine, and for all of any other match.
    /// </summary>
    public float StoryPace(Machine machine)
    {
        if (Story is not { } run)
            return 1;
        if (run.Drives.Of.TryGetValue(machine.Id, out var drive))
            return drive.Pace(Tick);
        // Section 2's Thresher goes at a pace by what he does, out of the walk's coupling (design-stealth §3.1).
        if (StalkerPace(run, machine) is { } stalking)
            return stalking;
        // A role the director paced (stufe7 C12, Then.Pace) goes at its share.
        foreach (var (role, share) in run.Drives.Paces)
        {
            if (run.Director.Cast.TryGetValue(role, out var ids) && ids.Contains(machine.Id))
                return share;
        }
        return !run.Flags.Contains(StoryMarks.SprintFree) && run.Sight.Seekers.TryGetValue(machine.Id, out var eyes) && !eyes.Blind
            ? StoryDrive.Walk
            : 1;
    }

    /// <summary>The share of its turn rate a machine turns at: <see cref="StoryDrive.Turn"/> for the trio's heroes in a story, else 1.</summary>
    public float StoryTurnPace(Machine machine) =>
        Story is not { } run ? 1 : run.Drives.Of.ContainsKey(machine.Id) ? StoryDrive.Turn : StalkerTurn(run, machine) ?? 1;

    /// <summary>
    /// The acceleration of one of the trio's heroes in a story, at a pace of 1: its own, but never more than takes it from a
    /// standstill to its top speed in <see cref="StoryDrive.RampSeconds"/> (the Locator's own would in a sixtieth of a
    /// second, which feels of nothing); null for every other machine, which keeps its own.
    /// </summary>
    public float? StoryRate(Machine machine) =>
        Story is { } run && run.Drives.Of.ContainsKey(machine.Id)
            ? MathF.Min(machine.Stats.Acceleration, machine.Stats.Speed / StoryDrive.RampSeconds)
            : null;

    /// <summary>Whether the trio's drives may sprint: the story freed them (<see cref="StoryMarks.SprintFree"/>).</summary>
    public bool StorySprintFree => Story?.Flags.Contains(StoryMarks.SprintFree) == true;

    /// <summary>A hero of the trio takes the deck: its drive, cold, walking.</summary>
    private void GiveStoryDrive(StoryRun run, Machine hero) => run.Drives.For(hero.Id).Last = hero.Position;

    /// <summary>The driver's sprint key, carried by its drive command (Shift; the left stick's click).</summary>
    private void HoldSprint(Machine driven, bool held)
    {
        if (StoryDriveOf(driven.Id) is { } drive)
            drive.Held = held;
    }

    /// <summary>
    /// At the end of a tick, each drive of the trio: the heat of this tick's sprint (or its cooling), overheating at full and
    /// freed at <see cref="StoryDrive.Resume"/>, and whether it sprints on the next tick: a person's while they hold the sprint
    /// and drive, the computer's Grunt when it catches up, follows a sprinting Locator, or is on its way where the story sends it.
    /// </summary>
    private void UpdateDrives()
    {
        var run = Story!;
        var free = run.Flags.Contains(StoryMarks.SprintFree);
        var locator = HeroOf(run.Locator);
        foreach (var seat in run.Seats)
        {
            if (HeroOf(seat) is not { } hero)
                continue;
            var drive = run.Drives.For(hero.Id);
            var moved = drive.Last != hero.Position;
            drive.Last = hero.Position;
            drive.Heat = Math.Clamp(drive.Heat + (drive.Sprinting && moved ? Dt / StoryDrive.FillSeconds : -Dt / StoryDrive.CoolSeconds), 0, 1);
            if (!drive.Blocked && drive.Heat >= 1)
            {
                drive.Blocked = true;
                drive.BlockedAt = Tick;
                StoryEvents.Cue(this, "drive hot", hero.Position, hero.Id);
            }
            else if (drive.Blocked && drive.Heat <= StoryDrive.Resume)
            {
                drive.Blocked = false;
                StoryEvents.Cue(this, "drive cool", hero.Position, hero.Id);
            }
            var wants = hero.DrivenByPlayer
                ? drive.Held && DriveInput.TryGetValue(hero.Id, out var input) && (input.Forward != 0 || input.Strafe != 0)
                : StoryBotPlays(seat) && BotSprints(run, seat, hero, drive, locator);
            // The creep: a person's Ctrl (the drive command's slow flag) or the pad's creep, the computer's Grunt as it sneaks
            // (CompanionBot.Stealth); it beats the sprint.
            drive.Creeping = hero.DrivenByPlayer
                ? DriveInput.TryGetValue(hero.Id, out var held) && held.Slow
                : StoryBotPlays(seat) && run.Stealth.Creep.Contains(seat.Value);
            drive.Sprinting = free && wants && !drive.Creeping && !drive.Blocked && hero.Inside is null && !StoryLies(hero) && !HeldByStory(hero.Id);
        }
        // A hero made anew (a refit) drives on with a drive of its own; the old one's goes.
        foreach (var gone in run.Drives.Of.Keys.Where(id => !run.Seats.Any(seat => HeroOf(seat)?.Id == id)).ToList())
            run.Drives.Of.Remove(gone);
    }

    /// <summary>
    /// Whether the computer's Grunt sprints, once the sprint is free: on its way where the story sends it (after the core all of
    /// those are getaways), running back from a distraction, or following the Locator while it sprints or when fallen behind.
    /// </summary>
    private bool BotSprints(StoryRun run, PlayerId seat, Machine grunt, StoryDrive drive, Machine? locator)
    {
        var going = grunt.PathIndex < grunt.Path.Count;
        var companion = run.Crew.Of(seat);
        var following = companion.Task == CompanionTask.None && companion.Order == CompanionOrder.Follow && locator is not null;
        var apart = following ? Vector2.Distance(grunt.Position, locator!.Position) : 0;
        drive.CatchingUp = following && (apart > StoryDrive.CatchUpFrom || drive.CatchingUp && apart > StoryDrive.CatchUpTo);
        if (!going || !run.Flags.Contains(StoryMarks.SprintFree))
            return false;
        if (companion.Task is CompanionTask.Go or CompanionTask.HoldSwitch or CompanionTask.Board || companion.Step == CompanionStep.Fleeing)
            return true;
        return following && (drive.CatchingUp || StoryDriveOf(locator!.Id) is { Sprinting: true });
    }
}
