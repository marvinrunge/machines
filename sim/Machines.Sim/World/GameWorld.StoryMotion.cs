using System.Numerics;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// How the story's Grunts keep up with the Locator (docs/STORY.md, Die beiden Grunts): they flow along behind it instead
/// of going a metre at a time. Only a story's companions do this; every other follower keeps <see cref="UpdateFollow"/>.
/// </summary>
public sealed partial class GameWorld
{
    // A story follower plans its way again this often while the place it keeps to moves on (a skirmish follower: 1.5 s).
    private const float StoryRepath = 0.25f;

    // ... once that place has moved this far from the end of the way it is on.
    private const float StoryRepathShift = 0.75f;

    /// <summary>
    /// A companion of the story following someone, in a queue: the one nearer the leader keeps two clearances behind the
    /// leader, the other the same behind its mate, so the two never want the same spot and wait for each other (two Grunts
    /// after the same point stop dead by turns). The way is planned again as soon as the spot has moved on, so a Grunt
    /// does not drive up to where the spot was a second and a half ago, stop there and turn on the spot before it sets off
    /// again. False when <paramref name="machine"/> is no story companion: the caller goes on as for any follower.
    /// </summary>
    private bool StoryFollow(Machine machine, FollowOrder follow, Machine leader)
    {
        if (Story is not { } run || CompanionSeat(machine) is null)
            return false;
        var ahead = leader;
        var mine = Vector2.DistanceSquared(machine.Position, leader.Position);
        for (var i = 1; i < run.Seats.Count; i++)
        {
            if (HeroOf(run.Seats[i]) is not { } mate || mate == machine || mate.Inside is not null || StoryLies(mate)
                || mate.Order is not FollowOrder theirs || theirs.Target != follow.Target)
                continue;
            // In the escape only a mate on its own side of the shutter X: one still in the hold is nearer a Locator on the
            // footbridge as the crow flies, and the Grunt in the gallery turned back through the shutter after it.
            if (run.Flags.Contains(StoryMarks.Escape) && OnHoldSide(mate.Position) != OnHoldSide(machine.Position))
                continue;
            var nearer = Vector2.DistanceSquared(mate.Position, leader.Position);
            if (nearer > mine || nearer == mine && mate.Id > machine.Id)
                continue;
            if (ahead == leader || Vector2.DistanceSquared(mate.Position, machine.Position) < Vector2.DistanceSquared(ahead.Position, machine.Position))
                ahead = mate;
        }

        var gap = 2 * (machine.Stats.HighClearance + ahead.Stats.HighClearance);
        var way = ahead.Position - machine.Position;
        var distance = way.Length();
        if (distance <= gap)
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            return true;
        }
        var goal = ahead.Position - way / distance * gap;
        // Only while it rolls: one standing turns on the spot to its way first (PlanRun), and a new way would start that
        // turn over, so a Grunt turning after a Locator that drives on would turn for ever. A rolling one keeps its speed
        // on a new way that bends off by less than a sharp corner. One held up keeps its way, or its own way out
        // (FollowPath plans again after a long wait).
        _nextRepath.TryGetValue(machine.Id, out var due);
        var motion = MotionOf(machine);
        if (machine.PathIndex >= machine.Path.Count
            || Time >= due && motion is { Turning: false, Speed: > 0 }
                && Vector2.DistanceSquared(machine.Path[^1], goal) > StoryRepathShift * StoryRepathShift)
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            machine.Path.AddRange(Nav.FindPath(machine.Position, goal, MobilityOf(machine)));
            _nextRepath[machine.Id] = Time + StoryRepath;
        }
        FollowPath(machine);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="machine"/> follows <paramref name="other"/> in a story: then it does not look half a second
    /// ahead for the one driven in first person (<see cref="Blocker"/>), as it keeps two clearances behind it anyway, and
    /// at its own top speed that half second reaches past the spot it keeps to, so it stopped dead behind a Locator
    /// driving on.
    /// </summary>
    private bool FollowsInStory(Machine machine, Machine other) =>
        Story is not null && machine.Order is FollowOrder follow && follow.Target == other.Id;

    /// <summary>
    /// Whether a machine in the way of one driven in first person steps aside for it in a story: a companion the computer
    /// drives, standing up (not downed), as a resting machine of one's own side does for one on an order. Asked only when
    /// the driven one runs into it; <see cref="MakeWay"/> still wants it at rest.
    /// </summary>
    private bool MakesWayInStory(Machine blocker) =>
        !blocker.DrivenByPlayer && CompanionSeat(blocker) is { } seat && !CompanionDown(seat);

    /// <summary>
    /// Whether the story holds a machine where it stands: a Grunt asleep in its clamp or lying downed, or one the director
    /// holds (the Locator in its clamp, the trio as it boards the pod). It neither makes way for anyone nor evades: a Grunt
    /// the computer plays drove out of its clamp when the Locator ran into it or Ike's way led past it.
    /// </summary>
    private bool StoryStill(Machine machine) => Story is not null && (StoryInert(machine) || HeldByStory(machine.Id));
}
