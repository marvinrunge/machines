using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Survival;

/// <summary>A hero's seat in a battle: its team (0 or 1), its place in the team, the race it plays for and the player it is.</summary>
public sealed record BattleSeat(int Team, int Place, Race Race, PlayerId Player);

/// <summary>
/// Who sits where in a battle. In today's layout (<see cref="BattleOptions.TeamSize"/> 0) each race of a team has one hero,
/// the race's own player; with a team size the teams are only the map's two anchors, each with that many heroes, numbered by
/// <see cref="PlayerTable.Number"/> over the seats in order (team A's first). The relay, the game and the sim seat alike.
/// </summary>
public static class BattleSeats
{
    /// <summary>The most heroes a team may have in the sim (30 players); rooms offer up to <see cref="BattleBalance.MostTeamSize"/>.</summary>
    public const int MostPerTeam = 15;

    /// <summary>
    /// The seats of a battle in (team, place) order. Today's layout: per team, its races in race order, place k the k-th, each
    /// the race's own player. With a team size n: n seats of team A's anchor, then n of team B's, numbered over that order
    /// (place 0 of each the anchor's own player, the others from 4 on, team A first). The teams are the options' own, so
    /// options for no map in particular give no seats until their teams are filled in.
    /// </summary>
    public static IReadOnlyList<BattleSeat> Of(BattleOptions options)
    {
        var size = Size(options);
        var teams = new[] { options.TeamA, options.TeamB };
        if (size == 0)
        {
            return [.. teams.SelectMany((team, t) => team.Distinct().Order()
                .Select((race, place) => new BattleSeat(t, place, race, PlayerId.Of(race))))];
        }
        var seats = new List<(int Team, int Place, Race Race)>();
        for (var t = 0; t < 2; t++)
        {
            if (teams[t].Count == 0)
                continue;
            for (var place = 0; place < size; place++)
                seats.Add((t, place, teams[t][0]));
        }
        var ids = PlayerTable.Number(seats.Select(s => s.Race).ToList());
        return [.. seats.Select((s, i) => new BattleSeat(s.Team, s.Place, s.Race, ids[i]))];
    }

    /// <summary>
    /// The teams a battle is fought in: the options' own, or the map's where the options name none; with a team size, only
    /// each team's anchor (its first race), whose pod is the team's.
    /// </summary>
    public static (IReadOnlyList<Race> A, IReadOnlyList<Race> B) Teams(BattleOptions options, BattleMap? map)
    {
        var a = options.TeamA.Count > 0 ? options.TeamA : map?.TeamA ?? [];
        var b = options.TeamB.Count > 0 ? options.TeamB : map?.TeamB ?? [];
        return Size(options) == 0 ? (a, b) : ([.. a.Take(1)], [.. b.Take(1)]);
    }

    /// <summary>
    /// The race a colour plays for: in today's layout itself when a team holds it; with a team size its team's anchor; else
    /// team A's anchor. A colour the options' teams no longer name (narrowed to the anchors) is looked up in the map's.
    /// </summary>
    public static Race Side(BattleOptions options, BattleMap map, Race colour)
    {
        var a = options.TeamA.Count > 0 ? options.TeamA : map.TeamA;
        var b = options.TeamB.Count > 0 ? options.TeamB : map.TeamB;
        if (Size(options) == 0)
            return a.Contains(colour) || b.Contains(colour) ? colour : a[0];
        return a.Contains(colour) ? a[0]
            : b.Contains(colour) ? b[0]
            : map.TeamA.Contains(colour) ? a[0]
            : map.TeamB.Contains(colour) ? b[0]
            : a[0];
    }

    /// <summary>The heroes a team has with the options' team size, 0 for today's layout; the sim takes at most <see cref="MostPerTeam"/>.</summary>
    public static int Size(BattleOptions options) => Math.Clamp(options.TeamSize, 0, MostPerTeam);
}
