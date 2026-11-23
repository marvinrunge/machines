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
        // Shut out by the shutter, a job beyond it (J4's valve) waits too; a chock in the hold it still pushes.
        if (run.Hazards.Shutter == ShutterState.Shut && GameWorld.OnHoldSide(grunt.Position) && companion.Task != CompanionTask.Chock)
            return WaitAtShutter(run, grunt);
        switch (companion.Task)
        {
            case CompanionTask.HoldFrame:
                return HoldFrame(run, companion, grunt);
            case CompanionTask.Valve:
                return Valve(run, companion, grunt);
            case CompanionTask.Chock:
                return Chock(run, companion, grunt);
        }
        if (world.HeroOf(run.Locator) is { } leader && WaitOutHazards(run, grunt, leader.Position))
            return true;
        // Fallen behind the Locator, or badly hurt: it runs after it rather than stand and fire at the pack (design-escape §10:
        // an armed Grunt fires when not on a job, and keeping with L-7 comes first). It fires again once it has caught up.
        if (world.HeroOf(run.Locator) is { } locator && (Vector2.Distance(locator.Position, grunt.Position) > KeepUp
            || grunt.HitPoints < grunt.MaxHitPoints * HurtShare))
        {
            world.CarryOut(run, companion, grunt);
            return true;
        }
        return false;
    }

    /// <summary>How far ahead a Grunt reads a jet's timetable before it crosses it, ticks.</summary>
    private const int JetLead = GameWorld.TicksPerSecond / 2;

    /// <summary>The gap a Grunt wants before it crosses a container's lane, ticks (design-escape §10: a predicted gap of 2 s).</summary>
    private const int CrossingGap = 2 * GameWorld.TicksPerSecond;

    /// <summary>
    /// Whether a container may be at <paramref name="y"/> (within <paramref name="clear"/> metres) within the next
    /// <see cref="CrossingGap"/>: where it stands, stretched by the most it can slide its way in that time (its slide starting
    /// after its telegraph). A container far off up or down its lane leaves the crossing free; one waited out wholesale kept
    /// the Grunts standing in the pack's fire.
    /// </summary>
    private bool RailSoonAt(StoryRail rail, float y, float clear)
    {
        var centre = rail.Centre / 1000f;
        var half = rail.HalfLength / 1000f;
        var (low, high) = (centre - half, centre + half);
        if (rail.State != RailState.Rest)
        {
            var starts = rail.State == RailState.Telegraph ? Math.Max(0, rail.SlideAt - world.Tick) : 0;
            var travel = Math.Max(0, CrossingGap - starts) * (rail.IsRack ? StoryHazards.RackSpeed : StoryHazards.TopSpeed) / 1000f;
            var way = rail.Way != 0 ? rail.Way : Math.Sign(rail.Speed);
            if (way >= 0)
                high += travel;
            if (way <= 0)
                low -= travel;
        }
        return y + clear > low && y - clear < high;
    }

    /// <summary>Where Grunts shut out by the shutter wait for it to rise: in its frame, on the hold's side, 4 m either side of this.</summary>
    private static readonly Vector2 ShutterWait = new(357, 144);

    /// <summary>
    /// Shut out by the shutter: it goes to the shutter and waits there, firing at the pack, until the Locator hacks the panel
    /// beside it (hack_x) and it rises (design-escape §3 C3). Following the Locator it looked for another way round, and
    /// that way led back through the hold into the pack.
    /// </summary>
    private bool WaitAtShutter(StoryRun run, Machine grunt)
    {
        // Each Grunt to a place of its own in the frame (the same one kept the second standing short of it).
        var spot = ShutterWait + new Vector2(0, seat.Value % 2 == 0 ? -4 : 4);
        if (Vector2.Distance(grunt.Position, spot) > 3)
        {
            if (!WaitOutHazards(run, grunt, spot))
                world.CompanionMove(grunt, spot);
            return true;
        }
        if (!FireIfArmed(run, grunt))
            world.CompanionStand(grunt);
        return true;
    }

    /// <summary>Below this share of its hit points a Grunt in the escape no longer stops to fight: it runs with the Locator.</summary>
    private const float HurtShare = 0.4f;

    /// <summary>How far a Grunt in the escape may fall behind the Locator before it stops fighting and runs after it, metres.</summary>
    private const float KeepUp = 12;

    /// <summary>Into the shutter's frame, and stands there until the rest of the trio is through; then after them.</summary>
    private bool HoldFrame(StoryRun run, Companion companion, Machine grunt)
    {
        if (world.Deck?.Zone("x_frame") is not { } frame || run.Hazards.Shutter is ShutterState.Shut or ShutterState.Broken)
        {
            world.AssignCompanion(seat, CompanionTask.None);
            return false;
        }
        // The rest of the trio, a downed Grunt too: one lying on the pack's side keeps the frame held until the Locator has
        // put it together and both are through (letting go shut it out of the chapter).
        var others = run.Seats.Select(world.HeroOf).OfType<Machine>().Where(h => h != grunt && h.Inside is null && !h.IsDead).ToList();
        // Through: in the gallery, or on beyond it (a Locator already on the
        // footbridge kept it standing in the frame for good).
        if (others.All(h => h.Position.X < frame.Min.X - 2 && h.Position.Y < 160 || h.Position.Y < 100))
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
    /// warns between it and where it goes (the Locator, or the frame it is sent to hold), and of a lane whose container is sliding; it waits on the free side and goes on
    /// when it is clear. True while it waits.
    /// </summary>
    private bool WaitOutHazards(StoryRun run, Machine grunt, Vector2 toward)
    {
        if (world.Deck is not { } deck)
            return false;
        var hazards = run.Hazards;
        var ahead = toward - grunt.Position;
        if (ahead.LengthSquared() < 1)
            return false;
        var look = grunt.Position + Vector2.Normalize(ahead) * MathF.Min(HazardLook, ahead.Length());
        for (var jet = 1; jet <= 5; jet++)
        {
            // Only a jet that burns now or within half a second: a Grunt crosses a jet's few metres in a moment, and one that
            // stood out every flicker stood in the pack's fire at its back.
            if (hazards.Phase(jet, world.Tick) != StoryHazards.JetPhase.Fire && hazards.Phase(jet, world.Tick + JetLead) != StoryHazards.JetPhase.Fire)
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
                && RailSoonAt(rail, grunt.Position.Y, grunt.Radius + 1))
            {
                world.CompanionStand(grunt);
                return true;
            }
        }
        return false;
    }
}
