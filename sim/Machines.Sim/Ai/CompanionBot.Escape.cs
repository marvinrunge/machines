using System.Numerics;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// Stage 7's behaviour of a computer's Grunt for package E (section 7, design-escape §10): its jobs, holding the shutter X in
// its frame (HoldFrame), standing on J4's valve (Valve) and pushing a chock (Chock); and the section's own, reading the
// hazards' timetables: it waits out a jet that burns or warns ahead of it on the free side, and a lane whose container
// slides (plan stufe7 C11: after the Notabschaltung's restart and a task, before following).
public sealed partial class CompanionBot
{
    /// <summary>How far ahead a Grunt looks for a jet or a sliding container before it goes on, metres.</summary>
    private const float HazardLook = 7;

    /// <summary>This section's behaviour for the Grunt; true when it acted this look (nothing after it runs).</summary>
    private bool ThinkEscape(StoryRun run, Companion companion, Machine grunt)
    {
        if (!run.Flags.Contains(StoryMarks.Escape))
            return false;
        switch (companion.Task)
        {
            case CompanionTask.HoldFrame:
                return HoldFrame(run, companion, grunt);
            case CompanionTask.Valve:
                return Valve(run, companion, grunt);
            case CompanionTask.Chock:
                return Chock(run, companion, grunt);
        }
        return WaitOutHazards(run, grunt);
    }

    /// <summary>Into the shutter's frame, and stands there until the rest of the trio is through; then after them.</summary>
    private bool HoldFrame(StoryRun run, Companion companion, Machine grunt)
    {
        if (world.Deck?.Zone("x_frame") is not { } frame || run.Hazards.Shutter is ShutterState.Shut or ShutterState.Broken)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return false;
        }
        var others = world.EscapeHeroes(run).Where(h => h != grunt).ToList();
        if (others.All(h => h.Position.X < frame.Min.X - 2 && h.Position.Y < 160))
        {
            // The others are through: it leaves the frame last, and the shutter slams behind it.
            world.AssignCompanion(seat, CompanionTask.None);
            world.CompanionMove(grunt, new Vector2(340, 140));
            return true;
        }
        var spot = (frame.Min + frame.Max) / 2;
        if (!GameWorld.Overlaps(frame, grunt.Position, grunt.Radius) || Vector2.Distance(grunt.Position, spot) > 3)
        {
            world.CompanionMove(grunt, spot);
            return true;
        }
        world.CompanionStand(grunt);
        if (companion.Step != CompanionStep.Standing)
        {
            companion.Step = CompanionStep.Standing;
            world.CompanionBark(run, seat, CompanionBarks.Cue.Gate, urgent: true);
        }
        return true;
    }

    /// <summary>On J4's valve until the others are past the jet, then across it last (it stays off a while after).</summary>
    private bool Valve(StoryRun run, Companion companion, Machine grunt)
    {
        if (world.Deck?.Markers.FirstOrDefault(m => m.Name == "valve_j4") is not { } valve)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return false;
        }
        var others = world.EscapeHeroes(run).Where(h => h != grunt).ToList();
        if (others.All(h => h.Position.Y < 98 || h.Position.X > 348))
        {
            world.AssignCompanion(seat, CompanionTask.None);
            world.CompanionMove(grunt, new Vector2(340, 92));
            return true;
        }
        if (Vector2.Distance(grunt.Position, valve.Position) > GameWorld.UseReach)
        {
            world.CompanionMove(grunt, valve.Position);
            return true;
        }
        world.CompanionStand(grunt);
        companion.Step = CompanionStep.Standing;
        return true;
    }

    /// <summary>To the chock named by the job's place, and pushes it into its lane.</summary>
    private bool Chock(StoryRun run, Companion companion, Machine grunt)
    {
        var target = companion.Place;
        var chock = world.Artefacts.FirstOrDefault(a => !a.IsDead && a.Tag is 337 or 338 or 339 && (target is null || $"chock_{a.Tag - 336}" == target));
        if (chock is null)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return false;
        }
        if (Vector2.Distance(grunt.Position, chock.Position) > 4 + grunt.Radius)
        {
            world.CompanionMove(grunt, chock.Position);
            return true;
        }
        world.PushChock(run, chock, chock.Tag!.Value - 336);
        world.AssignCompanion(seat, CompanionTask.None);
        return true;
    }

    /// <summary>
    /// Reading the hazards' timetables (design-escape §10): a Grunt following the Locator stops short of a jet that burns or
    /// warns between it and the Locator, and of a lane whose container is sliding; it waits on the free side and goes on
    /// when it is clear. True while it waits.
    /// </summary>
    private bool WaitOutHazards(StoryRun run, Machine grunt)
    {
        if (world.HeroOf(run.Locator) is not { } locator || world.Deck is not { } deck)
            return false;
        var hazards = run.Hazards;
        var ahead = locator.Position - grunt.Position;
        if (ahead.LengthSquared() < 1)
            return false;
        var look = grunt.Position + Vector2.Normalize(ahead) * MathF.Min(HazardLook, ahead.Length());
        for (var jet = 1; jet <= 5; jet++)
        {
            if (hazards.Phase(jet, world.Tick) == StoryHazards.JetPhase.Off && hazards.Phase(jet, world.Tick + 20) == StoryHazards.JetPhase.Off)
                continue;
            if (deck.Zone($"jet_j{jet}") is not { } zone || GameWorld.Overlaps(zone, grunt.Position, grunt.Radius))
                continue;
            if (GameWorld.Overlaps(zone, look, grunt.Radius))
            {
                world.CompanionStand(grunt);
                return true;
            }
        }
        foreach (var rail in hazards.Rails)
        {
            if (rail.State == RailState.Rest)
                continue;
            var laneMiddle = (rail.X0 + rail.X1) / 2000f;
            var outside = MathF.Abs(grunt.Position.X - laneMiddle) > rail.HalfWidth + grunt.Radius;
            if (outside && MathF.Abs(look.X - laneMiddle) <= rail.HalfWidth + grunt.Radius
                && MathF.Abs(rail.Centre / 1000f - grunt.Position.Y) < 26)
            {
                world.CompanionStand(grunt);
                return true;
            }
        }
        return false;
    }
}
