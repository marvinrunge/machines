using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// The settings of a story match, the remake's own: which chapter, from which checkpoint, and what the host named the two
/// Grunts. Part of the match options, so that a recording plays the same. The chapter is the scenario and the planet:
/// <c>data/maps/&lt;chapter&gt;.scn</c> and <c>data/planets/&lt;chapter&gt;/</c> with its deck.
/// </summary>
public sealed record StoryOptions
{
    /// <summary>The prologue, "Fehler": the Locator's escape from the mothership (docs/STORY.md, Prolog im Detail).</summary>
    public const string Prologue = "prolog";

    public string Chapter { get; init; } = Prologue;

    /// <summary>The checkpoint the chapter is played from: 0 is its start. A checkpoint is a fresh match, set up for it.</summary>
    public int Checkpoint { get; init; }

    /// <summary>The names of the two Grunts the Locator wakes, as the host gave them (the lines never speak them).</summary>
    public string IkeName { get; init; } = "Ike";

    public string BobName { get; init; } = "Bob";

    /// <summary>The trio's race; every seat of the story plays it.</summary>
    public Race Race { get; init; } = Race.Red;

    /// <summary>The ship's race: every machine that hunts the trio.</summary>
    public Race Ship { get; init; } = Race.Blue;
}

/// <summary>
/// The story's three seats: the Locator first (player 1, always a person on the host), then Ike and Bob, each a person or the
/// computer (<see cref="PlayerKind.Bot"/>). They are the trio race's rows in player order.
/// </summary>
public static class StorySeats
{
    /// <summary>How many heroes a story chapter has: the Locator and the two Grunts.</summary>
    public const int Count = 3;

    /// <summary>The marker each seat starts at at checkpoint 0, in seat order.</summary>
    public static readonly IReadOnlyList<string> Spawns = ["spawn_l7", "spawn_ike", "spawn_bob"];

    /// <summary>The chassis (WiredCatalogue) each seat wears, in seat order: Geo Locator 1 (as heroes have it tuned), then Grunt 1 twice.</summary>
    public static readonly IReadOnlyList<string> Chassis = ["geo1", "grunt1", "grunt1"];

    /// <summary>The seats of the story, in order: the trio race's rows by id.</summary>
    public static IReadOnlyList<PlayerId> Of(PlayerTable players, Race race) => players.PlayersOf(race).Take(Count).ToList();

    /// <summary>
    /// The players of a story match: the given rows of the trio's race (a person or the computer each, at most three), a bot
    /// named after its Grunt for every seat no row fills, and the ship's race as the computer's; rows of other races are left out.
    /// </summary>
    /// <exception cref="ArgumentException">More than three rows of the trio's race, or none that is a person.</exception>
    public static PlayerTable Seated(PlayerTable given, StoryOptions story)
    {
        var trio = given.Rows.Where(r => r.Race == story.Race).OrderBy(r => r.Id).ToList();
        if (trio.Count > Count)
            throw new ArgumentException($"a story has {Count} seats, not {trio.Count}");
        if (trio.Count > 0 && trio[0].Kind != PlayerKind.Human)
            throw new ArgumentException("the Locator's seat is a person's");
        var rows = trio.Select(r => r with { Kind = r.Kind == PlayerKind.Ai ? PlayerKind.Bot : r.Kind }).ToList();
        var taken = given.Rows.Select(r => r.Id).ToHashSet();
        string[] names = ["L-7", story.IkeName, story.BobName];
        while (rows.Count < Count)
        {
            var id = rows.Count == 0 && !taken.Contains(PlayerId.Of(story.Race)) ? PlayerId.Of(story.Race) : NextFree(taken);
            taken.Add(id);
            rows.Add(new PlayerSeat(id, story.Race, rows.Count == 0 ? PlayerKind.Human : PlayerKind.Bot, names[rows.Count]));
        }
        rows.Add(given.Rows.FirstOrDefault(r => r.Race == story.Ship) is { } ship
            ? ship with { Kind = PlayerKind.Ai }
            : new PlayerSeat(PlayerId.Of(story.Ship), story.Ship, PlayerKind.Ai, "Ship"));
        return new PlayerTable(rows);
    }

    /// <summary>The lowest id from 4 up that nobody has (ids 0 to 3 are each race's first player's).</summary>
    private static PlayerId NextFree(HashSet<PlayerId> taken)
    {
        for (var id = 4; id < PlayerId.Most; id++)
        {
            if (!taken.Contains(new PlayerId(id)))
                return new PlayerId(id);
        }
        throw new ArgumentException("no player id is left");
    }

    /// <summary>The Grunt names a story may be given: up to <see cref="BattleHero.MostName"/> characters, never blank.</summary>
    public static string Named(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim()[..Math.Min(name.Trim().Length, BattleHero.MostName)];
}
