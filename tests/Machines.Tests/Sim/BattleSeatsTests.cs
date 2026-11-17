using System.Text.Json;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Who sits where in a battle (P1 design §3.5, slice S1): seats and their players in today's layout and with a team size,
/// the teams narrowed to the anchors for every way a battle's options come in, the race a colour plays for, the heroes
/// composed per seat, and today's composition unchanged.
/// </summary>
public class BattleSeatsTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static PlayerId P(int value) => new(value);

    private static BattleMap Desert => BattleCatalogue.Map("m_desert")!;

    // ---- seats ------------------------------------------------------------------------------------------

    [Fact]
    public void Today_s_layout_seats_each_race_of_a_team_as_its_own_player()
    {
        var seats = BattleSeats.Of(new BattleOptions { TeamA = [Race.Blue, Race.Red], TeamB = [Race.Yellow, Race.Green] });

        Assert.Equal(
        [
            new BattleSeat(0, 0, Race.Red, P(0)), new BattleSeat(0, 1, Race.Blue, P(1)),
            new BattleSeat(1, 0, Race.Green, P(2)), new BattleSeat(1, 1, Race.Yellow, P(3)),
        ], seats);
        Assert.Empty(BattleSeats.Of(new BattleOptions()));
    }

    [Fact]
    public void Five_against_five_seats_the_anchors_five_times_numbered_team_a_first()
    {
        var seats = BattleSeats.Of(new BattleOptions { TeamA = Desert.TeamA, TeamB = Desert.TeamB, TeamSize = 5 });

        Assert.Equal([Race.Red, Race.Red, Race.Red, Race.Red, Race.Red, Race.Green, Race.Green, Race.Green, Race.Green, Race.Green],
            seats.Select(s => s.Race));
        Assert.Equal([P(0), P(4), P(5), P(6), P(7), P(2), P(8), P(9), P(10), P(11)], seats.Select(s => s.Player));
        Assert.Equal([0, 1, 2, 3, 4, 0, 1, 2, 3, 4], seats.Select(s => s.Place));
        Assert.Equal([0, 0, 0, 0, 0, 1, 1, 1, 1, 1], seats.Select(s => s.Team));
    }

    [Fact]
    public void Fifteen_a_team_is_the_most_the_sim_seats()
    {
        var fifteen = BattleSeats.Of(new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Blue], TeamSize = 15 });
        var more = BattleSeats.Of(new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Blue], TeamSize = 40 });

        Assert.Equal(30, fifteen.Select(s => s.Player).Distinct().Count());
        Assert.True(fifteen.Max(s => s.Player.Value) < PlayerId.Most);
        Assert.Equal(fifteen, more);
        Assert.Equal(BattleSeats.Of(new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Blue] }),
            BattleSeats.Of(new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Blue], TeamSize = -3 }));
    }

    // ---- teams ------------------------------------------------------------------------------------------

    /// <summary>
    /// The options a battle comes in with, by the way it comes: a room's or a test's with no teams (the sim fills in the
    /// map's), the relay's and the pod's with the map's teams, and options already narrowed.
    /// </summary>
    public static TheoryData<string, int> Ways => new()
    {
        { "no teams", 0 }, { "no teams", 5 }, { "the map's teams", 0 }, { "the map's teams", 5 }, { "narrowed", 5 },
    };

    private static BattleOptions Way(string way, int size) => way switch
    {
        "no teams" => new BattleOptions { TeamSize = size },
        "the map's teams" => new BattleOptions { TeamA = Desert.TeamA, TeamB = Desert.TeamB, TeamSize = size },
        _ => new BattleOptions { TeamA = [Desert.TeamA[0]], TeamB = [Desert.TeamB[0]], TeamSize = size },
    };

    [Theory]
    [MemberData(nameof(Ways))]
    public void A_team_size_narrows_the_teams_to_the_anchors_whichever_way_they_come(string way, int size)
    {
        var (a, b) = BattleSeats.Teams(Way(way, size), Desert);

        Assert.Equal(size == 0 ? Desert.TeamA : [Race.Red], a);
        Assert.Equal(size == 0 ? Desert.TeamB : [Race.Green], b);
    }

    [Fact]
    public void Without_a_map_or_teams_there_are_none()
    {
        Assert.Equal((0, 0), (BattleSeats.Teams(new BattleOptions(), null).A.Count, BattleSeats.Teams(new BattleOptions { TeamSize = 5 }, null).B.Count));
        var given = new BattleOptions { TeamA = [Race.Yellow, Race.Blue], TeamB = [Race.Green] };
        Assert.Same(given.TeamA, BattleSeats.Teams(given, Desert).A);
    }

    [Fact]
    public void A_colour_plays_for_itself_in_today_s_layout_and_for_its_team_s_anchor_with_a_team_size()
    {
        var classic = new BattleOptions();
        var five = new BattleOptions { TeamSize = 5 };
        var narrowed = new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Green], TeamSize = 5 };
        var apart = new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Green] };

        Assert.Equal([Race.Red, Race.Blue, Race.Green, Race.Yellow], Enum.GetValues<Race>().Select(c => BattleSeats.Side(classic, Desert, c)));
        Assert.Equal([Race.Red, Race.Red, Race.Green, Race.Green], Enum.GetValues<Race>().Select(c => BattleSeats.Side(five, Desert, c)));
        Assert.Equal([Race.Red, Race.Red, Race.Green, Race.Green], Enum.GetValues<Race>().Select(c => BattleSeats.Side(narrowed, Desert, c)));
        Assert.Equal([Race.Red, Race.Red, Race.Green, Race.Red], Enum.GetValues<Race>().Select(c => BattleSeats.Side(apart, Desert, c)));
    }

    // ---- composing per seat --------------------------------------------------------------------------------

    [Fact]
    public void Five_against_five_composes_a_hero_for_every_seat()
    {
        var player = BattleBots.Bot("m_desert", 4, Race.Red, 0) with { Name = "Anna", Bot = false, Player = P(4) };
        var blue = BattleBots.Bot("m_desert", 5, Race.Red, 0) with { Name = "Ben", Bot = false, Race = Race.Blue, Player = P(2) };

        var options = BattleBots.Compose(new BattleOptions { Tier = 5, TeamA = Desert.TeamA, TeamB = Desert.TeamB, TeamSize = 5 }, [player, blue],
            Desert.Scenario);

        Assert.Equal([Race.Red], options.TeamA);
        Assert.Equal([Race.Green], options.TeamB);
        Assert.Equal([P(0), P(2), P(4), P(5), P(6), P(7), P(8), P(9), P(10), P(11)], options.Heroes.Select(h => h.Player!.Value));
        Assert.All(options.Heroes, h => Assert.Equal(h.Player!.Value.Value is 0 or >= 4 and <= 7 ? Race.Red : Race.Green, h.Race));
        Assert.Equal(10, options.Heroes.Select(h => h.Name).Distinct().Count());
        // The player's own heroes, the one brought in Blue playing for Green's anchor.
        Assert.Equal(("Anna", false), (options.Heroes.Single(h => h.Player == P(4)).Name, options.Heroes.Single(h => h.Player == P(4)).Bot));
        Assert.Equal(("Ben", Race.Green), (options.Heroes.Single(h => h.Player == P(2)).Name, options.Heroes.Single(h => h.Player == P(2)).Race));
        // The other seats' bots, named by team and place; the one across place 1 from the player mirrors its tier.
        Assert.Equal(BattleBots.TeamName(0, 0), options.Heroes.Single(h => h.Player == P(0)).Name);
        Assert.Equal(BattleBots.TeamName(1, 4), options.Heroes.Single(h => h.Player == P(11)).Name);
        Assert.All(options.Heroes.Where(h => h.Player != P(4) && h.Player != P(2)), h => Assert.True(h.Bot));
        Assert.Equal(4, BattleCatalogue.Tier(options.Heroes.Single(h => h.Player == P(8)).Chassis));
        Assert.Equal(5, BattleCatalogue.Tier(options.Heroes.Single(h => h.Player == P(9)).Chassis));
        Assert.Equal(5, BattleCatalogue.Tier(options.Heroes.Single(h => h.Player == P(0)).Chassis));
    }

    [Fact]
    public void No_two_bots_of_a_full_match_share_a_name()
    {
        var names = Enumerable.Range(0, 2).SelectMany(team => Enumerable.Range(0, BattleSeats.MostPerTeam).Select(place => BattleBots.TeamName(team, place))).ToList();

        Assert.Equal(30, names.Distinct().Count());
        Assert.Equal(["Rivet", "Flux", "Cinder", "Gauge", "Torque", "Ember", "Static", "Piston"],
            Enum.GetValues<Race>().SelectMany(race => new[] { BattleBots.Name(race, 0), BattleBots.Name(race, 1) }));
        Assert.Equal("Rivet", BattleBots.Name(Race.Yellow, 2));
    }

    public static TheoryData<string> Maps => [.. BattleCatalogue.Maps.Select(m => m.Scenario)];

    [Theory]
    [MemberData(nameof(Maps))]
    public void Today_s_layout_composes_as_it_always_did(string scenario)
    {
        var map = BattleCatalogue.Map(scenario)!;
        for (var tier = 1; tier <= BattleCatalogue.MostTier; tier++)
        {
            var options = new BattleOptions { Tier = tier, Lives = 2, TeamA = map.TeamA, TeamB = map.TeamB };
            var red = BattleBots.Bot(scenario, Math.Max(1, tier - 1), Race.Red, 1) with { Name = "Anna", Bot = false, Race = map.TeamA[0] };
            var green = BattleBots.Bot(scenario, tier, Race.Red, 0) with { Name = "Ben", Bot = false, Race = map.TeamB[1] };
            foreach (var given in new BattleHero[][] { [], [red], [red, green] })
            {
                Assert.Equal(Json(TodaysCompose(options, given, scenario)), Json(BattleBots.Compose(options, given, scenario)));
                Assert.Equal(Json(TodaysCompose(options, given, "")), Json(BattleBots.Compose(options, given)));
            }
        }
    }

    /// <summary>
    /// A hero brought to today's layout plays the race it is put on, as before the seats, whatever player it names: the
    /// relay puts each member's hero on the member's race but passes on the rest of what the client sent.
    /// </summary>
    [Fact]
    public void A_hero_brought_to_today_s_layout_plays_its_race_whatever_player_it_names()
    {
        var options = new BattleOptions { Tier = 3, TeamA = Desert.TeamA, TeamB = Desert.TeamB };
        var red = BattleBots.Bot(Desert.Scenario, 3, Race.Red, 0) with { Name = "Anna", Bot = false, Player = P(1) };
        var green = BattleBots.Bot(Desert.Scenario, 3, Race.Red, 1) with { Name = "Ben", Bot = false, Race = Race.Green, Player = P(9) };

        var composed = BattleBots.Compose(options, [red, green], Desert.Scenario);

        Assert.Equal(Json(TodaysCompose(options, [red with { Player = null }, green with { Player = null }], Desert.Scenario)), Json(composed));
        var world = MatchSetup.Create(Data, Desert.Scenario, Desert.Planet,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Green] = Controller.Player }, 5,
            new MatchOptions { Battle = composed });
        Assert.Equal(["Anna", "Ben"], world.Battle!.Options.Heroes.Where(h => !h.Bot).Select(h => h.Name));
        Assert.Equal(4, world.Wired!.Heroes.Count);
    }

    private static string Json(BattleOptions options) => JsonSerializer.Serialize(options, NetJson.Options);

    /// <summary>
    /// <see cref="BattleBots.Compose"/> as it was before the seats (at a225dbd): a hero for every race of the two teams in
    /// race order, the given one by race, else a bot mirroring the tier of the other team's k-th.
    /// </summary>
    private static BattleOptions TodaysCompose(BattleOptions options, IReadOnlyList<BattleHero> given, string scenario)
    {
        var teams = new[] { options.TeamA.Order().ToList(), options.TeamB.Order().ToList() };
        var heroes = new List<BattleHero>();
        for (var team = 0; team < 2; team++)
        {
            for (var k = 0; k < teams[team].Count; k++)
            {
                var race = teams[team][k];
                if (given.FirstOrDefault(h => h.Race == race) is { } own)
                {
                    heroes.Add(own);
                    continue;
                }
                var other = teams[1 - team];
                var mirror = k < other.Count ? given.FirstOrDefault(h => h.Race == other[k]) : null;
                var tier = mirror is not null && BattleCatalogue.InBracket(mirror.Chassis, options.Tier)
                    ? BattleCatalogue.Tier(mirror.Chassis)
                    : options.Tier;
                heroes.Add(BattleBots.Bot(scenario, tier, race, k));
            }
        }
        return options with { Heroes = heroes.OrderBy(h => h.Race).ToList() };
    }

    // ---- worlds -------------------------------------------------------------------------------------------

    [Fact]
    public void A_five_against_five_world_has_two_races_and_a_row_for_every_seat()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller>(), 3,
            new MatchOptions { Battle = new BattleOptions { Tier = 3, TeamSize = 5 } });

        Assert.Equal([Race.Red, Race.Green], world.Races.Keys);
        Assert.Equal([Race.Red], world.Options.Battle!.TeamA);
        Assert.Equal([Race.Green], world.Options.Battle.TeamB);
        Assert.Equal(BattleSeats.Of(world.Options.Battle).Select(s => (s.Player, s.Race)).Order(), world.Players.ById.Select(r => (r.Id, r.Race)));
        Assert.All(world.Players.Rows, r => Assert.Equal(PlayerKind.Bot, r.Kind));
        Assert.Equal(10, world.Players.Rows.Select(r => r.Name).Distinct().Count());
        Assert.True(world.Players.Shared(Race.Red) && world.Players.Shared(Race.Green));
        for (var tick = 0; tick < 300; tick++)
            world.Step();
        Assert.Equal(300, world.Tick);
    }

    [Fact]
    public void A_battle_leaves_out_the_rows_of_races_outside_its_teams()
    {
        var table = new PlayerTable(
        [
            new PlayerSeat(P(1), Race.Blue, PlayerKind.Human, "Anna"), new PlayerSeat(P(0), Race.Red, PlayerKind.Bot, "Rivet"),
        ]);

        var world = MatchSetup.Create(Data, "m_desert", "m_desert", table, 3,
            new MatchOptions { Battle = new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Green] } });

        Assert.Equal([P(0), P(2)], world.Players.ById.Select(r => r.Id));
        Assert.Equal([Race.Red, Race.Green], world.Races.Keys);
    }

    public static TheoryData<string, int, PlayerSeat[]> Strays => new()
    {
        { "a second player of a race in today's layout", 0, [new(P(0), Race.Red, PlayerKind.Human, "Anna"), new(P(4), Race.Red, PlayerKind.Human, "Ben")] },
        { "a player numbered as another team's seat", 5, [new(P(0), Race.Red, PlayerKind.Human, "Anna"), new(P(2), Race.Green, PlayerKind.Human, "Ben"),
            new(P(4), Race.Green, PlayerKind.Human, "Cleo")] },
        { "a player past the last seat", 5, [new(P(0), Race.Red, PlayerKind.Human, "Anna"), new(P(12), Race.Red, PlayerKind.Human, "Ben")] },
    };

    /// <summary>A battle's players are its seats: a row of a team's race that sits nowhere would be a player without a hero.</summary>
    [Theory]
    [MemberData(nameof(Strays))]
    public void A_battle_refuses_a_player_of_its_teams_without_a_seat(string what, int teamSize, PlayerSeat[] rows)
    {
        Assert.False(string.IsNullOrEmpty(what));
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 3, TeamSize = teamSize } };

        Assert.Throws<ArgumentException>(() => MatchSetup.Create(Data, "m_desert", "m_desert", new PlayerTable(rows), 3, options));
    }
}
