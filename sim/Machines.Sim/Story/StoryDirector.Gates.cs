using System.Numerics;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// Stage 7's shared director pieces (plan stufe7 §4 F item 5, C12, C13): doors that shut when their frame is free, lock and
/// are blown; named timers; a pace per role; posts a role holds. The sections' beats (S, W, E) use them; nothing of today's
/// prologue does, and with none of them in use the director's fingerprint is what it was.
/// </summary>
public sealed partial class StoryDirector
{
    /// <summary>How near a machine holding a post (<see cref="Then.HoldAt"/>) must stand to it, metres, before it is sent back.</summary>
    public const float PostReach = 2;

    /// <summary>Deck doors locked (<see cref="Then.LockDoors"/>): they do not open by themselves, however near the trio comes.</summary>
    public SortedSet<char> Locked { get; } = [];

    /// <summary>Deck doors blown out of their frames (<see cref="Then.BlowDoor"/>): gone for good.</summary>
    public SortedSet<char> Blown { get; } = [];

    /// <summary>
    /// Deck doors waiting to shut (<see cref="Then.CloseDoors"/>), by letter, with the zone the whole trio must be in first
    /// ("" for none). Each shuts on the first tick its frame is free (and the trio is in), then is struck off.
    /// </summary>
    public SortedDictionary<char, string> Closing { get; } = [];

    /// <summary>The posts the roles hold (<see cref="Then.HoldAt"/>), by role: a place as <see cref="StoryMarks.Locate"/> reads it.</summary>
    public SortedDictionary<string, string> Posts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Each tick: the doors waiting to shut shut as soon as they may, and the machines holding a post that stand idle away from
    /// it are sent back; at it, they face as its marker does.
    /// </summary>
    private void KeepGates(GameWorld world, StoryRun run)
    {
        foreach (var (letter, zone) in Closing.ToList())
        {
            if (Blown.Contains(letter) || world.DoorClosed(letter))
            {
                Closing.Remove(letter);
                continue;
            }
            if (zone.Length > 0 && !(world.Deck?.Zone(zone) is { } inside
                && StoryTrigger.Present(world, run, StoryWho.Everyone, h => inside.Contains(h.Position))))
                continue;
            if (world.CloseDoor(letter))
                Closing.Remove(letter);
        }
        foreach (var (role, place) in Posts)
        {
            if (StoryMarks.Locate(world.Deck, place) is not { } at)
                continue;
            foreach (var machine in Of(world, role).ToList())
            {
                if (machine.Order is not (IdleOrder or StandGroundOrder))
                    continue;
                if (Vector2.Distance(machine.Position, at) > PostReach)
                    world.SendForStory(machine, new MoveOrder(at), at);
                else if (world.Deck?.Markers.FirstOrDefault(m => m.Name == place) is { } marker)
                    machine.Facing = marker.Angle * MathF.PI / 180;
            }
        }
    }

    /// <summary>What the gates keep, into the director's fingerprint; nothing at all while none is in use.</summary>
    private void MixGates(WiredHash hash)
    {
        foreach (var letter in Locked)
            hash.Mix(letter | 0x100);
        foreach (var letter in Blown)
            hash.Mix(letter | 0x200);
        foreach (var (letter, zone) in Closing)
        {
            hash.Mix(letter | 0x400);
            hash.Mix(zone);
        }
        foreach (var (role, place) in Posts)
        {
            hash.Mix(role);
            hash.Mix(place);
        }
    }
}

public static partial class When
{
    /// <summary>
    /// A named timer of the run (<see cref="Then.Timer"/>) runs and has at most so many seconds left (0: it ran out). It
    /// holds on until the timer is set anew or stopped.
    /// </summary>
    public sealed record TimerAt(string Name, float Seconds) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            run.Timers.TryGetValue(Name, out var ends) && ends - world.Time <= Seconds;
    }

    /// <summary>
    /// A machine of a role the director cast has lost at least <paramref name="Share"/> of its hit points, or is gone (the
    /// boss's halves and quarters).
    /// </summary>
    public sealed record CastHurt(string Role, float Share) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            director.Cast.TryGetValue(Role, out var ids) && ids.Any(id =>
                world.Find<Machine>(id) is not { IsDead: false } machine || machine.HitPoints <= machine.MaxHitPoints * (1 - Share));
    }
}

public static partial class Then
{
    /// <summary>
    /// Deck doors shut (stufe7 C13): each as soon as its frame is free (nobody stands where a leaf goes) and, with
    /// <paramref name="WhenAllIn"/>, the whole trio (a Grunt lying down too) is inside that zone; its ground is blocked anew.
    /// Opening it again (<see cref="OpenDoors"/>) calls the shutting off.
    /// </summary>
    public sealed record CloseDoors(string Letters, string? WhenAllIn = null) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var letter in Letters.Where(l => !director.Blown.Contains(l)))
            {
                director.AutoDoors.Remove(letter);
                director.Closing[letter] = WhenAllIn ?? "";
            }
        }
    }

    /// <summary>Deck doors locked: they no longer open by themselves (<see cref="AutoDoors"/> waits); the view hears the bolts.</summary>
    public sealed record LockDoors(string Letters) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var letter in Letters.Where(l => !director.Blown.Contains(l) && director.Locked.Add(l)))
                StoryEvents.Cue(world, $"door locked {letter}", DoorMiddle(world, letter));
        }
    }

    /// <summary>Deck doors unlocked: one that opened by itself before does so again.</summary>
    public sealed record UnlockDoors(string Letters) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var letter in Letters.Where(director.Locked.Remove))
                StoryEvents.Cue(world, $"door unlocked {letter}", DoorMiddle(world, letter));
        }
    }

    /// <summary>
    /// A deck door blown out of its frame (stufe7 C13): its leaves are thrown into the room, and it never shuts, locks or opens
    /// again (<see cref="GameWorld.BlowDoor"/>).
    /// </summary>
    public sealed record BlowDoor(char Letter) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (!director.Blown.Add(Letter))
                return;
            director.AutoDoors.Remove(Letter);
            director.Closing.Remove(Letter);
            director.Locked.Remove(Letter);
            world.BlowDoor(Letter);
        }
    }

    /// <summary>
    /// A named timer of the run set to run out in so many seconds (<see cref="When.TimerAt"/>), or, with none, stopped. It is
    /// kept with the run's timers, in the checksum.
    /// </summary>
    public sealed record Timer(string Name, float? Seconds) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seconds is { } seconds)
                run.Timers[Name] = world.Time + seconds;
            else
                run.Timers.Remove(Name);
        }
    }

    /// <summary>
    /// The machines of a role go at a share of their top speed and acceleration from now on (stufe7 C12; the pace table,
    /// <see cref="StoryDrives.Paces"/>); a share of 1 or more, or none, takes the role off the table.
    /// </summary>
    public sealed record Pace(string Role, float? Share) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Share is { } share && share < 1)
                run.Drives.Paces[Role] = MathF.Max(0.05f, share);
            else
                run.Drives.Paces.Remove(Role);
        }
    }

    /// <summary>
    /// The machines of a role hold a post (a marker, a zone's middle or "x,y"): sent there now, and back there whenever they
    /// stand idle away from it (a hunt the sight gave them is over); at it they face as its marker does.
    /// </summary>
    public sealed record HoldAt(string Role, string Place) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            director.Posts[Role] = Place;
            var at = Where(world, Place);
            foreach (var machine in director.Of(world, Role).ToList())
            {
                if (Vector2.Distance(machine.Position, at) > StoryDirector.PostReach)
                    world.SendForStory(machine, new MoveOrder(at), at);
            }
        }
    }

    /// <summary>The machines of a role let go of their post (<see cref="HoldAt"/>): they stay where they are until ordered on.</summary>
    public sealed record Release(string Role) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => director.Posts.Remove(Role);
    }

    private static Vector2 DoorMiddle(GameWorld world, char letter)
    {
        var leaves = world.DoorLeaves(letter);
        return leaves.Count == 0 ? default : leaves.Aggregate(Vector2.Zero, (sum, leaf) => sum + leaf.Position) / leaves.Count;
    }
}
