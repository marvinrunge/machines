using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Story;

// Section 2's director pieces (plan stufe7 §4 S, design-stealth §13): the Thresher's rounds, guard, scripted looks and rush,
// the lures, the offscreen grace, and the triggers that read him. Their state is the run's StoryStealth, in the checksum.

public static partial class Then
{
    /// <summary>
    /// The machines of a role take up one of section 2's rounds (<see cref="StoryStealth.Rounds"/>): at the next point of
    /// the round they are on (never mid-lane), or at once with <paramref name="Now"/> or when they are on none; from the
    /// round's point <paramref name="From"/> (-1: the nearest).
    /// </summary>
    public sealed record Round(string Role, string Name, int From = -1, bool Now = false) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var machine in director.Of(world, Role).ToList())
            {
                if (Now || !run.Stealth.RoundOf.ContainsKey(machine.Id) || machine.Order is not PatrolOrder)
                    world.StoryRound(machine, Name, From);
                else
                    run.Stealth.NextRound[machine.Id] = (Name, From);
            }
        }
    }

    /// <summary>The machines of a role go to a post at the search's pace and guard it (sweeping, looking back now and then); without a place they leave it.</summary>
    public sealed record Guard(string Role, string? Place) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var machine in director.Of(world, Role).ToList())
            {
                if (Place is null)
                    run.Stealth.Guards.Remove(machine.Id);
                else
                    world.StoryGuardAt(machine, Place);
            }
        }
    }

    /// <summary>The machines of a role stand looking along a heading (degrees, 0 east, 90 north) for so many seconds (the window's rest on the niche).</summary>
    public sealed record Look(string Role, float Angle, float Seconds) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var machine in director.Of(world, Role).ToList())
                run.Stealth.Looks[machine.Id] = new StoryLook(world.Time + Seconds, Angle * MathF.PI / 180);
        }
    }

    /// <summary>The machines of a role rush to a place at the hunt's pace (his gallop to door H), off any round or guard.</summary>
    public sealed record Rush(string Role, string Place) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            var at = Where(world, Place);
            foreach (var machine in director.Of(world, Role).ToList())
            {
                run.Stealth.Guards.Remove(machine.Id);
                run.Stealth.RoundOf.Remove(machine.Id);
                run.Stealth.NextRound.Remove(machine.Id);
                run.Stealth.Looks.Remove(machine.Id);
                run.Stealth.Rushing.Add(machine.Id);
                world.SendForStory(machine, new MoveOrder(at), at);
            }
        }
    }

    /// <summary>A noise at a place as loud as <paramref name="Loud"/> (F rattling as the noise terminal is hacked): the Thresher goes to look (<see cref="GameWorld.StoryLure"/>).</summary>
    public sealed record Lure(string Place, float Loud) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            var at = Where(world, Place);
            StoryEvents.Cue(world, $"source bang {Place}", at);
            world.StoryLure(at, Loud, Place);
        }
    }

    /// <summary>The director keeps a seat out of the Thresher's sight (the offscreen grace, by hand), or gives it back.</summary>
    public sealed record Unseen(int Seat, bool On) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat >= run.Seats.Count)
                return;
            if (On)
                run.Stealth.Unseen.Add(run.Seats[Seat].Value);
            else
                run.Stealth.Unseen.Remove(run.Seats[Seat].Value);
        }
    }

    /// <summary>The computer's Grunt of a seat goes to a noise source and bangs it (Bob on his own when the Locator waits too long).</summary>
    public sealed record BangBy(int Seat, string Source) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat < 1 || Seat >= run.Seats.Count || !world.StoryBotPlays(run.Seats[Seat]) || world.HeroOf(run.Seats[Seat]) is not { } grunt)
                return;
            var bot = run.Stealth.Bot(run.Seats[Seat].Value);
            bot.Bang = Source;
            bot.Back = grunt.Position;
        }
    }
}

public static partial class Then
{
    /// <summary>
    /// The computer's Grunt of a seat goes to a place the sneaking way (CompanionBot.Stealth: freezing when he comes near,
    /// waiting at the lanes for its window) and waits there; without a place it follows the Locator again.
    /// </summary>
    public sealed record SneakTo(int Seat, string? Place) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            if (Seat < 1 || Seat >= run.Seats.Count)
                return;
            run.Stealth.Bot(run.Seats[Seat].Value).Goal = Place;
        }
    }
}

public static partial class When
{
    /// <summary>A machine of a role stands within <paramref name="Radius"/> metres of a place.</summary>
    public sealed record CastAt(string Role, string Place, float Radius = 4) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            StoryMarks.Locate(world.Deck, Place) is { } at && director.Of(world, Role).Any(m => Vector2.Distance(m.Position, at) <= Radius);
    }

    /// <summary>A machine of a role has gone round one of section 2's rounds so many times.</summary>
    public sealed record RoundDone(string Role, string Name, int Laps = 1) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            director.Of(world, Role).Any(m => run.Stealth.RoundOf.GetValueOrDefault(m.Id) == Name && run.Stealth.Laps.GetValueOrDefault(m.Id) >= Laps);
    }

    /// <summary>A searchlight seeker (the Thresher) stands within so many metres of the Locator.</summary>
    public sealed record ThreatWithin(float Metres) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            world.HeroOf(run.Locator) is { } locator && run.Sight.Seekers.Values.Any(e => e.Searchlight && !e.Blind
                && world.Find<Machine>(e.Id) is { IsDead: false } m && Vector2.Distance(m.Position, locator.Position) <= Metres);
    }

    /// <summary>No Grunt of the trio lies down (section 2's checkpoints wait for it).</summary>
    public sealed record AllUp : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            !run.Seats.Skip(1).Any(seat => run.Flags.Contains(StoryMarks.Down(seat)));
    }

    /// <summary>A Grunt of the trio lies down.</summary>
    public sealed record AnyDown : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            run.Seats.Skip(1).Any(seat => run.Flags.Contains(StoryMarks.Down(seat)));
    }

    /// <summary>The Locator's lamp is on.</summary>
    public sealed record LampLit : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            world.HeroOf(run.Locator) is { } locator && world.StoryLampOn(locator.Id);
    }

    /// <summary>The Locator is as quiet as so much signature or quieter (standing still and dark is 0.05).</summary>
    public sealed record Quiet(float Signature) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            world.HeroOf(run.Locator) is { } locator && run.Sight.Signatures.TryGetValue(locator.Id, out var signature) && signature <= Signature;
    }

    /// <summary>A deck door is shut.</summary>
    public sealed record DoorShut(char Letter) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => world.DoorClosed(Letter);
    }

    /// <summary>A machine of a role is searching (a lure drew it off, or it lost what it hunted).</summary>
    public sealed record Searching(string Role) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            director.Of(world, Role).Any(m => m.Order is SearchOrder);
    }

    /// <summary>A noise source of the deck has been banged at least once within the escalation's window.</summary>
    public sealed record Banged(string Source) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => run.Stealth.BangsAt(Source, world.Time) > 0;
    }
}
