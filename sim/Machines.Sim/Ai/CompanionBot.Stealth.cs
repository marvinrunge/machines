using System.Numerics;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// Stage 7's behaviour of a computer's Grunt for package S (section 2; design-stealth §6.3), after the Notabschaltung's restart
// and a task, before following (plan stufe7 C11): a bang it was sent to make, freezing when the Thresher comes near, creeping
// near him, going where the director sends it the sneaking way, and waiting at an exposed lane for its window. Only while
// the story's stealth is on; everything it keeps is the run's StoryStealth, in the checksum.
public sealed partial class CompanionBot
{
    /// <summary>Within his cone and this near, or within his hearing of it and 6 m more, a Grunt freezes, metres.</summary>
    private const float FreezeCone = 45, FreezeMargin = 6;

    /// <summary>A freeze ends this long after the danger passed; it is given up after <see cref="FreezeMost"/> if not in his beam, seconds.</summary>
    private const float FreezeAfter = 2, FreezeMost = 20;

    /// <summary>A Grunt that freezes on a lane with him farther off than this first creeps off it, metres.</summary>
    private const float FreezeRoll = 25;

    /// <summary>Within so many metres of him a Grunt creeps.</summary>
    private const float CreepNear = 30;

    /// <summary>The window rule: he is this far from where the Grunt crosses, and does not look within <see cref="WindowAngle"/> degrees of it.</summary>
    private const float WindowClear = 45, WindowAngle = 35;

    /// <summary>A Grunt gives up waiting for a window after so long and crosses anyway (it never waits for ever), seconds.</summary>
    private const float HoldMost = 30;

    /// <summary>How near the view's aim a noise source must be for the Locator's distraction to mean it, metres.</summary>
    private const float SourcePick = 8;

    /// <summary>The exposed lanes the Thresher walks (design-stealth §6.3): deck A's lane, deck B's north and south lanes; min and max corners.</summary>
    private static readonly (Vector2 Min, Vector2 Max)[] Lanes =
    [
        (new(120, 44), new(264, 60)),
        (new(120, 168), new(264, 184)),
        (new(120, 144), new(264, 158)),
    ];

    /// <summary>This section's behaviour for the Grunt; true when it acted this look (nothing after it runs).</summary>
    private bool ThinkStealth(StoryRun run, Companion companion, Machine grunt)
    {
        var stealth = run.Stealth;
        var bot = stealth.Bot(seat.Value);
        if (!run.Flags.Contains(StoryMarks.Stealth))
        {
            stealth.Creep.Remove(seat.Value);
            (bot.FrozenAt, bot.ClearAt, bot.Holding, bot.HoldSince) = (-1, -1, false, -1);
            return false;
        }
        var thresher = Stalker(run, grunt);
        // The Locator's distraction aimed at a valve or a source: the Grunt bangs it instead of honking.
        if (companion.Order == CompanionOrder.Distract && bot.Bang is null && SourceAt(companion.Point) is { } source)
        {
            bot.Bang = source;
            bot.Back = grunt.Position;
            companion.Order = CompanionOrder.Wait;
            companion.Step = CompanionStep.None;
            run.Flags.Remove(StoryMarks.Distracting(seat));
        }
        if (bot.Bang is { } bang)
            return Bang(run, bot, grunt, bang);
        // Near him it creeps (and when the Locator creeps).
        var near = thresher is not null && Vector2.Distance(thresher.Position, grunt.Position) <= CreepNear;
        var locatorCreeps = world.HeroOf(run.Locator) is { } locator && world.StoryDriveOf(locator.Id) is { Creeping: true };
        if (near || locatorCreeps)
            stealth.Creep.Add(seat.Value);
        else
            stealth.Creep.Remove(seat.Value);
        if (Freeze(run, bot, grunt, thresher))
            return true;
        if (bot.Goal is { } goal && StoryMarks.Locate(world.Deck, goal) is { } spot)
        {
            if (Vector2.Distance(grunt.Position, spot) <= There)
            {
                world.CompanionStand(grunt);
                return true;
            }
            if (Hold(run, bot, grunt, thresher, spot))
                return true;
            world.CompanionMove(grunt, spot);
            return true;
        }
        if (companion.Task != CompanionTask.None || companion.Order != CompanionOrder.Follow || world.HeroOf(run.Locator) is not { } leader)
            return false;
        // Following, it never stands on an exposed lane beside a Locator that stopped by it (a duct's mouth opens onto deck B's
        // north lane), nor steps onto one only to close up: it waits off the lane until the Locator has gone on.
        if (thresher is not null && Vector2.Distance(thresher.Position, grunt.Position) <= 120 && !LeaderDrives(leader))
        {
            var apart = Vector2.Distance(leader.Position, grunt.Position);
            if (OnLane(grunt.Position) && apart <= StayOff)
            {
                if (OffTheLane(grunt) || world.StoryAtRest(grunt) && OffLane(grunt, StayOff) is { } off && Go(grunt, off))
                    return true;
            }
            else if (!OnLane(grunt.Position) && apart <= WaitOff && LaneAhead(grunt, leader.Position) is not null)
            {
                world.CompanionStand(grunt);
                return true;
            }
        }
        // Following: it waits before a lane for its window as it would on its way.
        return Vector2.Distance(leader.Position, grunt.Position) > There * 2 && Hold(run, bot, grunt, thresher, leader.Position);
    }

    /// <summary>A following Grunt on a lane within so many metres of a Locator that stopped goes off it; one off the lanes within <see cref="WaitOff"/> waits there.</summary>
    private const float StayOff = 16, WaitOff = 25;

    /// <summary>Whether the Locator drives (its driver holds a key), so its Grunts keep up with it across a lane.</summary>
    private bool LeaderDrives(Machine leader) =>
        world.DriveInput.TryGetValue(leader.Id, out var input) && (input.Forward != 0 || input.Strafe != 0);

    /// <summary>Whether the Grunt is on its way off a lane (sent to a point off the lanes).</summary>
    private static bool OffTheLane(Machine grunt) => grunt.Order is MoveOrder { Target: var to } && !OnLane(to);

    private bool Go(Machine grunt, Vector2 to)
    {
        world.CompanionMove(grunt, to);
        return true;
    }

    /// <summary>
    /// The nearest point off the exposed lanes the Grunt can reach within <paramref name="most"/> metres: rings of 2 m,
    /// sixteen headings each, in a fixed order (deterministic); null for none.
    /// </summary>
    private Vector2? OffLane(Machine grunt, float most)
    {
        var mobility = GameWorld.MobilityOf(grunt);
        for (var r = 2f; r <= most; r += 2)
        {
            for (var k = 0; k < 16; k++)
            {
                var angle = k * MathF.PI / 8;
                var at = grunt.Position + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * r;
                if (!OnLane(at) && world.Nav.IsFree(at, mobility) && world.CompanionReaches(grunt, at, 1.5f))
                    return at;
            }
        }
        return null;
    }

    /// <summary>The nearest searchlight seeker (the Thresher) that is up, null for none.</summary>
    private Machine? Stalker(StoryRun run, Machine grunt)
    {
        Machine? best = null;
        foreach (var eyes in run.Sight.Seekers.Values)
        {
            if (!eyes.Searchlight || eyes.Blind || world.Find<Machine>(eyes.Id) is not { IsDead: false } machine)
                continue;
            if (best is null || Vector2.DistanceSquared(machine.Position, grunt.Position) < Vector2.DistanceSquared(best.Position, grunt.Position))
                best = machine;
        }
        return best;
    }

    /// <summary>The noise source of the deck nearest a point, within <see cref="SourcePick"/> of it; null for none.</summary>
    private string? SourceAt(Vector2 point) =>
        world.StoryNoiseSources().Where(m => Vector2.Distance(m.Position, point) <= SourcePick)
            .OrderBy(m => Vector2.DistanceSquared(m.Position, point)).ThenBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault()?.Name;

    /// <summary>To the source and bang it; then the job is done and it goes back by its goal or its order.</summary>
    private bool Bang(StoryRun run, StoryBotStealth bot, Machine grunt, string source)
    {
        if (world.StoryNoiseSources().FirstOrDefault(m => m.Name == source) is not { } marker)
        {
            bot.Bang = null;
            return false;
        }
        if (Vector2.Distance(marker.Position, grunt.Position) <= GameWorld.BangReach + grunt.Radius - 0.5f || GameWorld.Arrived(grunt) && grunt.Order is not IdleOrder
            && Vector2.Distance(marker.Position, grunt.Position) <= GameWorld.BangReach + grunt.Radius)
        {
            world.CompanionStand(grunt);
            world.StoryBang(source, grunt.Id);
            bot.Bang = null;
            return true;
        }
        world.CompanionMove(grunt, marker.Position);
        return true;
    }

    /// <summary>
    /// Freezes when he comes near: within his hearing of its signature and a little more, or in his cone within
    /// <see cref="FreezeCone"/>; it stands still, its lamp off with the Locator's, and says so once. It goes on
    /// <see cref="FreezeAfter"/> seconds after the danger passed, and gives a freeze up after <see cref="FreezeMost"/> if not in his beam.
    /// </summary>
    private bool Freeze(StoryRun run, StoryBotStealth bot, Machine grunt, Machine? thresher)
    {
        var danger = thresher is not null && Danger(run, grunt, thresher, out _);
        if (danger)
        {
            var beam = thresher is not null && InCone(thresher, grunt.Position, StorySight.BeamHalfAngle) && Vector2.Distance(thresher.Position, grunt.Position) <= StorySight.BeamLength;
            if (bot.FrozenAt < 0)
            {
                bot.FrozenAt = world.Time;
                Say(run, seat.Value == run.Seats[1].Value ? Prologue.Lines.IkeFreeze : Prologue.Lines.BobFreeze);
            }
            bot.ClearAt = -1;
            if (world.Time - bot.FrozenAt < FreezeMost || beam)
            {
                run.Stealth.Creep.Add(seat.Value);
                // Caught on a lane with him still more than 25 m off: it creeps to cover within 10 m first (design-stealth §6.3).
                if (OnLane(grunt.Position) && thresher is not null && Vector2.Distance(thresher.Position, grunt.Position) > FreezeRoll
                    && (OffTheLane(grunt) || OffLane(grunt, 10) is { } cover && Go(grunt, cover)))
                    return true;
                world.CompanionStand(grunt);
                return true;
            }
            return false;
        }
        if (bot.FrozenAt < 0)
            return false;
        if (bot.ClearAt < 0)
            bot.ClearAt = world.Time;
        if (world.Time - bot.ClearAt < FreezeAfter)
        {
            world.CompanionStand(grunt);
            return true;
        }
        (bot.FrozenAt, bot.ClearAt) = (-1, -1);
        return false;
    }

    /// <summary>Whether he is a danger to the Grunt now: near enough to hear it walk, or looking its way within <see cref="FreezeCone"/>; never while it is out of his sight by the grace.</summary>
    private bool Danger(StoryRun run, Machine grunt, Machine thresher, out float distance)
    {
        distance = Vector2.Distance(thresher.Position, grunt.Position);
        // The walk's signature (design-stealth §3.2): it freezes before he would hear it walk.
        var walking = StorySight.Still + StorySight.Moving * StorySight.Wheels;
        if (distance <= StorySight.Hearing * walking * 0.5f + FreezeMargin)
            return world.StoryEyeClear(thresher, grunt) || distance <= StorySight.Hearing * walking * 0.25f + FreezeMargin;
        return distance <= FreezeCone && InCone(thresher, grunt.Position, StorySight.BeamHalfAngle + 10) && world.StoryEyeClear(thresher, grunt);
    }

    /// <summary>Whether a point lies within so many degrees of where his beam points.</summary>
    private bool InCone(Machine thresher, Vector2 point, float degrees)
    {
        var way = point - thresher.Position;
        if (way.LengthSquared() < 0.01f)
            return true;
        var off = MathF.IEEERemainder(DetMath.Atan2(way.Y, way.X) - world.GunHeading(thresher), 2 * MathF.PI);
        return MathF.Abs(off) <= degrees * MathF.PI / 180;
    }

    /// <summary>
    /// The crossing discipline (design-stealth §6.3): a Grunt not on an exposed lane whose way there leads onto one holds
    /// until the window is open: he is on his round (or searching far off), not wary, at least <see cref="WindowClear"/> from
    /// where it crosses, not looking there, and going away from it or standing at a far point. It says its lane line once,
    /// and never waits more than <see cref="HoldMost"/> seconds. True while it holds.
    /// </summary>
    private bool Hold(StoryRun run, StoryBotStealth bot, Machine grunt, Machine? thresher, Vector2 to)
    {
        if (thresher is null || Vector2.Distance(thresher.Position, grunt.Position) > 120 || OnLane(grunt.Position) || LaneAhead(grunt, to) is not { } crossing || WindowOpen(run, thresher, crossing)
            || bot.HoldSince >= 0 && world.Time - bot.HoldSince > HoldMost)
        {
            if (bot.Holding && (thresher is null || !OnLane(grunt.Position)) && bot.HoldSince >= 0 && world.Time - bot.HoldSince > HoldMost)
                return false;
            (bot.Holding, bot.HoldSince) = (false, -1);
            return false;
        }
        if (!bot.Holding)
            (bot.Holding, bot.HoldSince) = (true, world.Time);
        world.CompanionStand(grunt);
        if (!bot.SaidLane && seat.Value == run.Seats[1].Value)
        {
            bot.SaidLane = true;
            Say(run, Prologue.Lines.IkeLane);
        }
        return true;
    }

    private static bool OnLane(Vector2 point) => Lanes.Any(l => point.X >= l.Min.X && point.X <= l.Max.X && point.Y >= l.Min.Y && point.Y <= l.Max.Y);

    /// <summary>The first point of the Grunt's way to <paramref name="to"/> that lies on an exposed lane, within 30 m of it; null for none.</summary>
    private Vector2? LaneAhead(Machine grunt, Vector2 to)
    {
        var path = world.Nav.FindPath(grunt.Position, to, GameWorld.MobilityOf(grunt));
        var from = grunt.Position;
        var gone = 0f;
        foreach (var point in path)
        {
            var step = Vector2.Distance(from, point);
            for (var t = 1f; t <= step; t += 1)
            {
                var at = Vector2.Lerp(from, point, t / step);
                if (OnLane(at))
                    return gone + t <= 30 ? at : null;
            }
            gone += step;
            if (gone > 30)
                return null;
            from = point;
        }
        return null;
    }

    /// <summary>The window rule: whether the Grunt may cross at <paramref name="crossing"/> now.</summary>
    private bool WindowOpen(StoryRun run, Machine thresher, Vector2 crossing)
    {
        if (run.Sight.Seekers.GetValueOrDefault(thresher.Id) is not { } eyes || eyes.Suspicion >= StorySight.WaryAt && eyes.Quarry != 0)
            return false;
        var distance = Vector2.Distance(thresher.Position, crossing);
        if (thresher.Order is SearchOrder)
            return distance > WindowClear + 5;
        if (distance <= WindowClear || InCone(thresher, crossing, WindowAngle))
            return false;
        if (thresher.Order is not PatrolOrder patrol)
            return distance > WindowClear * 1.5f && !run.Stealth.Guards.ContainsKey(thresher.Id);
        // Going away from the crossing, or standing at a point of his round far from it.
        var ahead = patrol.Points[patrol.Next] - thresher.Position;
        return eyes.DwellUntil > world.Time || Vector2.Dot(ahead, crossing - thresher.Position) <= 0;
    }

    /// <summary>The Grunt says a line of section 2 over the radio, once.</summary>
    private void Say(StoryRun run, string line)
    {
        var index = run.Seats.IndexOf(seat);
        if (index < 1 || !run.Crew.Of(seat).Said.Add(line))
            return;
        run.Crew.Of(seat).LastBark = world.Tick;
        run.Crew.Bark(new StoryBark(line, CompanionBarks.Speaker(index), world.Tick, seat));
    }
}
