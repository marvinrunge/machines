using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// A player of a match, apart from the race (team colour) they play. The first player of each race has the race's number
/// (<see cref="Of"/>); every other player gets 4, 5, 6 … (<see cref="PlayerTable.Number"/>), so a match with one player per
/// race numbers its players exactly as its races.
/// </summary>
/// <remarks>A struct rather than an int, so that a machine's id can never be passed for a player, and without a conversion from <see cref="Race"/>.</remarks>
public readonly record struct PlayerId(int Value) : IComparable<PlayerId>
{
    /// <summary>Ids run from 0 to 31 (<see cref="Most"/> of them); a match has at most <see cref="MostPlayers"/> players.</summary>
    public const int Most = 32, MostPlayers = 30;

    /// <summary>
    /// The diagnostic of the race forms ("the race's first player") kept for tests and tools: the sim and the net treat it
    /// as an error, so their own code always names the player.
    /// </summary>
    public const string RaceForm = "MACHINES0001";

    /// <summary>The id of a race's first player, which is the race's number.</summary>
    public static PlayerId Of(Race race) => new((int)race);

    public int CompareTo(PlayerId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Who plays a seat: a person, the computer driving one hero (<see cref="Bot"/>), or the computer playing a whole race (<see cref="Ai"/>).</summary>
public enum PlayerKind
{
    Human,
    /// <summary>The computer drives one hero (a battle's bot).</summary>
    Bot,
    /// <summary>The computer plays the whole race: the skirmish AI, the waves.</summary>
    Ai,
}

/// <summary>One player of the match as it started: id, race, kind and the name shown for them.</summary>
public sealed record PlayerSeat(PlayerId Id, Race Race, PlayerKind Kind, string Name);

/// <summary>One player leaving (<paramref name="Away"/>) or coming back, handed to <see cref="GameWorld.ChangePresence"/>.</summary>
public readonly record struct PresenceChange(PlayerId Player, bool Away);

/// <summary>
/// The players of a match: its rows as the match records them (never changed after the start), and who is away and who
/// keeps an away player's machines, which change during the match and are set only by <see cref="GameWorld.ChangePresence"/>.
/// </summary>
/// <remarks>
/// A table is refused (<see cref="ArgumentException"/>) unless its ids are unique and below <see cref="PlayerId.Most"/>, it
/// has at most <see cref="PlayerId.MostPlayers"/> rows, each race's lowest id is the race's own and every other id of the
/// race is 4 or more, and an <see cref="PlayerKind.Ai"/> row is the only row of its race. The relay never sends such a table.
/// </remarks>
public sealed class PlayerTable
{
    private readonly Dictionary<PlayerId, PlayerSeat> _byId = [];
    private readonly Dictionary<Race, List<PlayerId>> _byRace = [];
    private readonly SortedSet<PlayerId> _away = [];
    private readonly SortedDictionary<PlayerId, PlayerId> _keepers = [];

    public PlayerTable(IEnumerable<PlayerSeat> rows)
    {
        Rows = rows.ToList();
        if (Rows.Count > PlayerId.MostPlayers)
            throw new ArgumentException($"a match has at most {PlayerId.MostPlayers} players, not {Rows.Count}");
        var races = new List<Race>();
        foreach (var row in Rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (row.Id.Value is < 0 or >= PlayerId.Most)
                throw new ArgumentException($"player {row.Id} is not between 0 and {PlayerId.Most - 1}");
            if (!_byId.TryAdd(row.Id, row))
                throw new ArgumentException($"player {row.Id} is in the table twice");
            if (!_byRace.TryGetValue(row.Race, out var ids))
            {
                _byRace[row.Race] = ids = [];
                races.Add(row.Race);
            }
            ids.Add(row.Id);
        }
        foreach (var (race, ids) in _byRace)
        {
            ids.Sort();
            if (ids[0] != PlayerId.Of(race))
                throw new ArgumentException($"{race}'s first player must be {PlayerId.Of(race)}, not {ids[0]}");
            if (ids.Skip(1).Where(id => id.Value < 4).ToList() is [var low, ..])
                throw new ArgumentException($"{race}'s player {low} has the number of another race's first player");
            if (ids.Count > 1 && ids.Any(id => _byId[id].Kind == PlayerKind.Ai))
                throw new ArgumentException($"the computer plays {race} as a whole, so the race has no other player");
        }
        Races = races;
        ById = [.. Rows.OrderBy(r => r.Id)];
        Humans = [.. ById.Where(r => r.Kind == PlayerKind.Human).Select(r => r.Id)];
        AnyShared = _byRace.Values.Any(ids => ids.Count > 1);
    }

    /// <summary>A table without rows, for worlds built without <see cref="MatchSetup"/>: no race is shared.</summary>
    public static PlayerTable Empty => new([]);

    /// <summary>The rows in the order the match records them; <see cref="MatchSetup"/> creates the races in the order of their first row.</summary>
    public IReadOnlyList<PlayerSeat> Rows { get; }

    /// <summary>The rows by id: the order every loop over players goes in.</summary>
    public IReadOnlyList<PlayerSeat> ById { get; }

    /// <summary>The races of the rows, in the order of their first row.</summary>
    public IReadOnlyList<Race> Races { get; }

    /// <summary>The ids of the <see cref="PlayerKind.Human"/> rows, sorted.</summary>
    public IReadOnlyList<PlayerId> Humans { get; }

    /// <summary>Whether any race has more than one player: the gate of everything the table adds to the checksum.</summary>
    public bool AnyShared { get; }

    public PlayerSeat this[PlayerId id] => _byId.TryGetValue(id, out var row) ? row : throw new KeyNotFoundException($"player {id} is not in the table");

    public bool Contains(PlayerId id) => _byId.ContainsKey(id);

    /// <summary>The race's players, sorted; none for a race without rows.</summary>
    public IReadOnlyList<PlayerId> PlayersOf(Race race) => _byRace.TryGetValue(race, out var ids) ? ids : [];

    /// <summary>Whether the race has more than one player: the gate of every rule the players of one race bring.</summary>
    public bool Shared(Race race) => _byRace.TryGetValue(race, out var ids) && ids.Count > 1;

    /// <summary>Whether a race has a <see cref="PlayerKind.Human"/> row.</summary>
    public bool HasHuman(Race race) => PlayersOf(race).Any(id => _byId[id].Kind == PlayerKind.Human);

    /// <summary>The race a player plays; an id from 0 to 3 without a row is that race's first player (as the waves' race that no row names).</summary>
    public Race RaceOf(PlayerId id) => _byId.TryGetValue(id, out var row) ? row.Race
        : id.Value is >= 0 and < 4 ? (Race)id.Value
        : throw new KeyNotFoundException($"player {id} is not in the table");

    /// <summary>The player's place among its race's players, from 0; the number shown for it is one more.</summary>
    public int PlaceInRace(PlayerId id) => Math.Max(0, PlayersOf(RaceOf(id)).ToList().IndexOf(id));

    /// <summary>Whether the player has left the match and not come back.</summary>
    public bool Away(PlayerId id) => _away.Contains(id);

    /// <summary>The player present who looks after an away player's machines, if anyone does.</summary>
    public PlayerId? KeeperOf(PlayerId id) => _keepers.TryGetValue(id, out var keeper) ? keeper : null;

    /// <summary>The players away, in id order.</summary>
    public IReadOnlyCollection<PlayerId> AwayPlayers => _away;

    /// <summary>Who keeps whose machines, by the away player's id.</summary>
    public IReadOnlyDictionary<PlayerId, PlayerId> Keepers => _keepers;

    internal void SetAway(PlayerId id, bool away)
    {
        if (away)
            _away.Add(id);
        else
            _away.Remove(id);
    }

    internal void SetKeeper(PlayerId id, PlayerId? keeper)
    {
        if (keeper is { } who)
            _keepers[id] = who;
        else
            _keepers.Remove(id);
    }

    /// <summary>The same rows, with everyone present: what a new world starts from.</summary>
    public PlayerTable Fresh() => new(Rows);

    /// <summary>
    /// The ids of seats in their order: the first seat of each race gets the race's own number, every later seat the next
    /// of 4, 5, 6 … Ids 0 to 3 never go to a second player, even of a race not in the match, so <see cref="PlayerId.Of"/>
    /// is always that race's first player.
    /// </summary>
    public static IReadOnlyList<PlayerId> Number(IReadOnlyList<Race> seatsInOrder)
    {
        if (seatsInOrder.Count > PlayerId.MostPlayers)
            throw new ArgumentException($"a match has at most {PlayerId.MostPlayers} players, not {seatsInOrder.Count}");
        var first = new HashSet<Race>();
        var next = 4;
        var ids = new List<PlayerId>(seatsInOrder.Count);
        foreach (var race in seatsInOrder)
        {
            var id = first.Add(race) ? PlayerId.Of(race) : new PlayerId(next++);
            if (id.Value >= PlayerId.Most)
                throw new ArgumentException($"too many players for one race: ids end at {PlayerId.Most - 1}");
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// The table a match keyed by race means: one row per entry in the dictionary's order, the race's own id, a person for
    /// <see cref="Controller.Player"/> and otherwise a bot in a battle and the AI elsewhere (<paramref name="battle"/>:
    /// <see cref="MatchOptions.IsBattle"/>).
    /// </summary>
    public static PlayerTable OnePerRace(IReadOnlyDictionary<Race, Controller> players, bool battle) =>
        new(players.Select(entry => new PlayerSeat(PlayerId.Of(entry.Key), entry.Key,
            entry.Value == Controller.Player ? PlayerKind.Human : battle ? PlayerKind.Bot : PlayerKind.Ai,
            entry.Value == Controller.Player ? "Player" : "AI")));
}
