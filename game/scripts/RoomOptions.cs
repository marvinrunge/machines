using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The rules of a multiplayer room's settings that the room pages follow, free of Godot so the tests can compile them: a
/// room's options are changed from those it has, so that what the settings page does not show (a Wired run's or a battle's
/// own settings, the rules a recording keeps) comes through a change unharmed; which hero a battle room takes, in the
/// relay's words; and how big a side or a team may be. Battle is the remake's own addition.
/// </summary>
public static class RoomOptions
{
    /// <summary>
    /// The team sizes a battle offers, in the room's settings and the pod: 0 for today's layout, a hero for each race of the
    /// map's two teams (two against two), and <see cref="BattleBalance.MostTeamSize"/> heroes a team on each team's first
    /// race (five against five). The relay's beat (online plan P2) adds ten and fifteen.
    /// </summary>
    public static IReadOnlyList<int> TeamSizes { get; } = [0, BattleBalance.MostTeamSize];

    /// <summary>The commanders a side may have in a normal room: one, as in the original, or two sharing the side.</summary>
    public static IReadOnlyList<int> CommandersPerSide { get; } = [.. Enumerable.Range(1, RelayServer.MostSeatsPerSlot)];

    /// <summary>
    /// The heroes each team has with a team size on a map: the size itself, or in today's layout one for each race of the
    /// larger team (two on every battle map).
    /// </summary>
    public static int HeroesPerTeam(int teamSize, BattleMap? map) =>
        teamSize > 0 ? teamSize : map is null ? 2 : Math.Max(map.TeamA.Count, map.TeamB.Count);

    /// <summary>A team size in the menus' words: "2 vs 2", "5 vs 5" (the heroes a team, <see cref="HeroesPerTeam"/>).</summary>
    public static string TeamSizeName(int teamSize, BattleMap? map) =>
        HeroesPerTeam(teamSize, map) is var heroes ? $"{heroes} vs {heroes}" : "";

    /// <summary>The lives a battle of a team size is offered with, as a room proposes them (<see cref="BattleBalance.ProposedLives"/>).</summary>
    public static int ProposedLives(int teamSize, BattleMap? map) => BattleBalance.ProposedLives(HeroesPerTeam(teamSize, map));

    /// <summary>
    /// A battle room made of <paramref name="kept"/>: the bracket topped by <paramref name="tier"/>, the lives and the victory
    /// chosen, and the <paramref name="teamSize"/> (one of <see cref="TeamSizes"/>; null keeps the room's), everything else of
    /// an earlier battle (its wait for a body, its time) kept. A battle is fought without fog, in first person, with fixed
    /// teams and nothing of Wired. The relay seats the teams by the size (only each team's first race with a size).
    /// </summary>
    public static MatchOptions Battle(MatchOptions kept, int tier, int lives, BattleWin win, int? teamSize = null) => kept with
    {
        Victory = VictoryCondition.Battle,
        FogOfWar = false,
        BroadcastAlliances = false,
        DisableFirstPerson = false,
        Wired = null,
        Battle = (kept.Battle ?? new BattleOptions()) with
        {
            Tier = Math.Clamp(tier, 1, BattleCatalogue.MostTier),
            Lives = Math.Clamp(lives, 1, 5),
            Win = Enum.IsDefined(win) ? win : BattleWin.Both,
            TeamSize = Math.Clamp(teamSize ?? kept.Battle?.TeamSize ?? 0, 0, BattleBalance.MostTeamSize),
        },
    };

    /// <summary>
    /// A normal room made of <paramref name="kept"/>: the settings the page shows from <paramref name="chosen"/>, everything
    /// else (a Wired run, a campaign's carry, the rules a recording keeps) as it was; it is no battle any more.
    /// </summary>
    public static MatchOptions Normal(MatchOptions kept, MatchOptions chosen) => kept with
    {
        Battle = null,
        StartingBmu = chosen.StartingBmu,
        Resources = chosen.Resources,
        TechLevel = chosen.TechLevel,
        Victory = chosen.Victory,
        TimeLimit = chosen.TimeLimit,
        AiLevel = chosen.AiLevel,
        FogOfWar = chosen.FogOfWar,
        BroadcastAlliances = chosen.BroadcastAlliances,
        DisableFirstPerson = chosen.DisableFirstPerson,
    };

    /// <returns>
    /// Why a hero may not play in a battle room whose bracket is topped by <paramref name="tier"/>, in the relay's words (so
    /// the page refuses it before the relay would), or null.
    /// </returns>
    public static string? WhyNotHere(BattleHero hero, int tier) =>
        !BattleCatalogue.InBracket(hero.Chassis, tier) ? "that hero is not in the room's bracket"
        : BattleBots.WhyNot(hero, tier) is not null ? "that hero does not fit together"
        : null;
}

/// <summary>
/// A seat as the room's list shows it: its place in the room's seats (<paramref name="Index"/>, what <see cref="TakeSeat"/>
/// and <see cref="SetSeat"/> name), the race it plays, its number among that race's seats (1 on), and who sits there.
/// </summary>
public sealed record SeatLine(int Index, Race Race, int Number, RoomSeat Seat, LobbyPlayer? Player)
{
    /// <summary>Nobody sits there and it waits for a player: anyone in the room may take it.</summary>
    public bool Free => Seat.ClientId is null && Seat.Use == SeatUse.Open;
}

/// <summary>
/// A group of the room's list: in a normal room one race (<paramref name="Team"/> -1) and its seats, in a battle room a team
/// (0 or 1) with its races and their seats, each in the room's seat order.
/// </summary>
public sealed record SeatGroup(int Team, IReadOnlyList<Race> Races, IReadOnlyList<SeatLine> Seats);

/// <summary>
/// How a room's list sets out its seats (<see cref="RoomState.Seats"/>, each knowing its race), free of Godot so the tests can
/// compile it: grouped by race in a normal room, where two commanders may share a side, and by team in a battle room, where
/// a team may have up to fifteen heroes; what the host may make of a seat nobody of theirs sits at; and which seat a player
/// may take.
/// </summary>
public static class RoomSeating
{
    /// <summary>
    /// The room's seats in groups: a battle room's two teams (<see cref="BattleSeats.Teams"/> of its options and map, so with
    /// a team size each team is its first race's seats), a normal room's races in seat order. A seat of a race no team holds
    /// (none the relay sends) is a group of its own after them.
    /// </summary>
    public static IReadOnlyList<SeatGroup> Groups(RoomState state)
    {
        var lines = Lines(state);
        if (state.Options.Battle is not { } battle)
            return [.. lines.GroupBy(l => l.Race).Select(race => new SeatGroup(-1, [race.Key], [.. race]))];
        var (a, b) = BattleSeats.Teams(battle, BattleCatalogue.Map(state.Scenario));
        var groups = new List<SeatGroup>();
        foreach (var (team, races) in new[] { (0, a), (1, b) })
            groups.Add(new SeatGroup(team, [.. races.Distinct().Order()], [.. lines.Where(l => races.Contains(l.Race))]));
        groups.AddRange(lines.Where(l => !a.Contains(l.Race) && !b.Contains(l.Race)).GroupBy(l => l.Race)
            .Select(race => new SeatGroup(-1, [race.Key], [.. race])));
        return groups;
    }

    /// <summary>Every seat of the room with its number in its race and who sits there, in seat order.</summary>
    public static IReadOnlyList<SeatLine> Lines(RoomState state) => [.. state.Seats.Select((seat, i) => new SeatLine(i, seat.Race,
        state.Seats.Take(i).Count(s => s.Race == seat.Race) + 1, seat,
        seat.ClientId is { } id ? state.Players.FirstOrDefault(p => p.ClientId == id) : null))];

    /// <summary>
    /// Whether the list numbers the seats: where a race has more than one (two commanders a side, a team size), as the match
    /// numbers the heroes and commanders of a race it shares.
    /// </summary>
    public static bool Numbered(RoomState state) => state.Seats.GroupBy(s => s.Race).Any(race => race.Count() > 1);

    /// <summary>
    /// What the host may make of a seat (MachGuiPlayerListItem's drop-down): in a battle Open or Computer, as every seat has a
    /// hero; in a normal room Open or Closed, and Computer only at a race's first seat, since a side the computer plays is
    /// the computer's alone (the relay gives it to the computer only when nobody sits at any of its seats).
    /// </summary>
    public static IReadOnlyList<SeatUse> Uses(RoomState state, int seat)
    {
        if (seat < 0 || seat >= state.Seats.Count)
            return [];
        if (state.Options.Battle is not null)
            return [SeatUse.Open, SeatUse.Computer];
        var race = state.Seats[seat].Race;
        var first = state.Seats.Select((s, i) => (s, i)).First(s => s.s.Race == race).i == seat;
        return first ? [SeatUse.Open, SeatUse.Closed, SeatUse.Computer] : [SeatUse.Open, SeatUse.Closed];
    }

    /// <summary>Whether the player <paramref name="clientId"/> may take a seat: one nobody sits at that waits for a player (<see cref="TakeSeat"/>).</summary>
    public static bool MayTake(RoomState state, int clientId, int seat) =>
        seat >= 0 && seat < state.Seats.Count && state.Seats[seat] is { ClientId: null, Use: SeatUse.Open }
        && state.Players.Any(p => p.ClientId == clientId);
}
