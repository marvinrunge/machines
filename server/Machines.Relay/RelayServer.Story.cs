using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Relay;

/// <summary>
/// Story rooms (docs/STORY.md, "Koop"; decision 5 of the prologue's build): a chapter played by up to three people, the host
/// the Locator at the room's first seat, Ike and Bob at the other two, each a person or the computer's bot. Only the trio's
/// seats are in the room; the ship's race is the computer's, a row of its own at the start. A seat nobody sits at when the
/// host starts is a bot's, which a player may join late as in a battle (<see cref="JoinLate"/>), taking its hero over as it
/// stands; one who leaves hands it back to the computer in every world and may come back to it (<see cref="Rejoin"/>).
/// </summary>
public sealed partial class RelayServer
{
    /// <summary>The chapters a story room may play: those whose deck and scenario every game copy has.</summary>
    public static IReadOnlyList<string> StoryChapters { get; } = [StoryOptions.Prologue];

    /// <summary>Whether a room has a hero at every seat, a seat nobody sits at being a bot's: a battle and the story.</summary>
    private static bool HeroesEverySeat(MatchOptions options) => options.Battle is not null || options.IsStory;

    /// <summary>
    /// A story's options as a room plays them: its chapter, checkpoint and Grunts' names within what the sim takes, and
    /// nothing of a battle or Wired; known ground, as the deck is (no fog), heroes driven in first person.
    /// </summary>
    public static MatchOptions AsStory(MatchOptions options)
    {
        var story = options.Story ?? new StoryOptions();
        story = story with
        {
            Checkpoint = Math.Max(0, story.Checkpoint),
            IkeName = StorySeats.Named(story.IkeName, "Ike"),
            BobName = StorySeats.Named(story.BobName, "Bob"),
        };
        return options with
        {
            Story = story,
            Victory = VictoryCondition.Story,
            Battle = null,
            Wired = null,
            FogOfWar = false,
            DisableFirstPerson = false,
        };
    }

    /// <returns>
    /// Why a room cannot play the story so, or null: a chapter there is (<see cref="StoryChapters"/>), the trio and the ship
    /// two races there are and not the same, and no battle besides.
    /// </returns>
    private static string? WhyNotStory(MatchOptions options)
    {
        var story = options.Story ?? new StoryOptions();
        if (!StoryChapters.Contains(story.Chapter))
            return "no such chapter";
        if (options.Battle is not null || options.Victory == VictoryCondition.Battle)
            return "a story is no battle";
        if (!Enum.IsDefined(story.Race) || !Enum.IsDefined(story.Ship) || story.Race == story.Ship)
            return "no such side";
        return null;
    }

    /// <summary>
    /// A story room as it will be played: its chapter is its scenario and planet, its one start position the trio's race with
    /// the story's three seats, filled with bots at the start, and its options as <see cref="AsStory(MatchOptions)"/> makes
    /// them. Any other room as it is.
    /// </summary>
    private static CreateRoom AsStory(CreateRoom create) => create.Options.IsStory && AsStory(create.Options) is var options
        ? create with
        {
            Scenario = options.Story!.Chapter, Planet = options.Story.Chapter, Slots = [options.Story.Race], Options = options,
            FillWithAi = true, SeatsPerSlot = StorySeats.Count,
        }
        : create;

    /// <inheritdoc cref="AsStory(CreateRoom)"/>
    private static ChangeRoom AsStory(ChangeRoom change) => change.Options.IsStory && AsStory(change.Options) is var options
        ? change with
        {
            Scenario = options.Story!.Chapter, Planet = options.Story.Chapter, Slots = [options.Story.Race], Options = options,
            FillWithAi = true, SeatsPerSlot = StorySeats.Count,
        }
        : change;

    /// <summary>
    /// The seats of a story room: the Locator's, Ike's and Bob's, all of the trio's race, as the players the story's seats are
    /// (<see cref="StorySeats.Of"/>: the race's own number, then 4 and 5).
    /// </summary>
    private static List<Seat> StorySeatPlan(MatchOptions options)
    {
        var race = (options.Story ?? new StoryOptions()).Race;
        var players = PlayerTable.Number([.. Enumerable.Repeat(race, StorySeats.Count)]);
        return [.. players.Select((player, place) => new Seat(race, place, player))];
    }

    /// <returns>
    /// Why a member may not take a seat of a story room, or null (any other room, and the Grunts' seats to anyone but the
    /// host): the Locator's seat is the host's, and the host keeps it.
    /// </returns>
    private static string? WhyNotStorySeat(Room room, Member member, int seat) =>
        !room.Setup.Options.IsStory ? null
        : seat == 0 || member.Id == room.HostId ? "the Locator's seat is the host's"
        : null;

    /// <summary>
    /// Starts a story room: a row for each of the three seats in seat order, each the seat's player (the Locator, Ike, Bob):
    /// the member sitting there, the host at the Locator's, or the computer's bot named after its Grunt as the host named it;
    /// then one for the ship, the computer's. Every row of the trio shows the host's colour; the ship its own, or the first
    /// no row shows.
    /// </summary>
    private void StartStory(Room room)
    {
        var story = room.Setup.Options.Story ?? new StoryOptions();
        // The host is the Locator: at the first seat whatever happened in the room before.
        if (room.Members.FirstOrDefault(m => m.Id == room.HostId) is { } host && room.Seats[0].Member != host)
            Sit(room, host, room.Seats[0], keepColour: true);
        var colour = room.Seats[0].Member?.Colour ?? story.Race;
        string[] names = ["L-7", story.IkeName, story.BobName];
        var rows = new List<MatchPlayer>();
        for (var i = 0; i < room.Seats.Count; i++)
        {
            var seat = room.Seats[i];
            var player = seat.Player!.Value;
            if (seat.Member is { } member)
            {
                member.Player = player;
                room.HelloNames[player] = member.Name;
                rows.Add(new MatchPlayer(member.Id, member.RoomName, seat.Race, colour, Ai: false, player));
            }
            else
                rows.Add(new MatchPlayer(0, names[Math.Min(i, names.Length - 1)], seat.Race, colour, Ai: true, player));
        }
        var ship = story.Ship != colour ? story.Ship : Enum.GetValues<Race>().First(c => c != colour);
        rows.Add(new MatchPlayer(0, "Ship", story.Ship, ship, Ai: true, PlayerId.Of(story.Ship)));
        var terrain = room.Members.Select(m => m.Terrain).Distinct().ToList() is [{ } same] ? same : (long?)null;
        room.Match = new MatchInfo(room.Setup.Scenario, room.Setup.Planet, room.Setup.Options, _random.Next(1, int.MaxValue), StartDelay, rows, terrain);
        Broadcast(room, new MatchStarted(room.Match));
        Log?.Invoke($"room {room.Id} started the story's {story.Chapter} at checkpoint {story.Checkpoint} with {room.Members.Count} players");
        BeginBeat(room);
    }

    /// <summary>The seat a late joiner takes in a story: the Grunt's seat a bot plays with the lowest player (Ike before Bob); null when none is.</summary>
    private static PlayerId? StoryJoinSeat(Room room) =>
        room.Beat!.Seats.Where(s => s.State == SeatState.Bot).Select(s => (PlayerId?)s.Player).Min();
}
