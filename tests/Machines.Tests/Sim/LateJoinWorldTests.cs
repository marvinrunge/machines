using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The world's side of a late join and of the lag mark (P2 design §6.2, §2.6, T6): a person takes over a bot's seat of a
/// battle (<see cref="PresenceChange.Joins"/>, <see cref="PlayerTable.Join"/>), its row reads as theirs from then on and the
/// checksum says so once there is a join; a lagging player's wheels are let go of (<see cref="GameWorld.LetGo"/>). Both are
/// world events every world applies at the same tick, so twin worlds stay equal through them.
/// </summary>
public class LateJoinWorldTests
{
    private static GameData Data => Battles.Data;

    private static PlayerId P(int value) => new(value);

    /// <summary>
    /// A five against five on the Desert, every seat a bot but those in <paramref name="people"/>, whose heroes stand at the
    /// wheel, as in <see cref="Battles.New"/>, so that the tests say who drives.
    /// </summary>
    private static GameWorld FiveAgainstFive(int seed = 7, params int[] people)
    {
        var entry = BattleCatalogue.Map("m_desert")!;
        var options = new BattleOptions { Tier = 3, TeamSize = 5, TeamA = entry.TeamA, TeamB = entry.TeamB, Lives = 2 };
        var rows = BattleSeats.Of(options)
            .Select(seat => new PlayerSeat(seat.Player, seat.Race, people.Contains(seat.Player.Value) ? PlayerKind.Human : PlayerKind.Bot, $"Seat {seat.Player}"))
            .ToList();
        var world = MatchSetup.Create(Data, entry.Scenario, entry.Planet, new PlayerTable(rows), seed, new MatchOptions { Battle = options, FogOfWar = false });
        foreach (var person in people)
            world.HeroOf(P(person))!.DrivenByPlayer = true;
        return world;
    }

    /// <summary>
    /// A two against two on the Desert (a race a seat), Blue the <paramref name="blue"/>'s and the other three people standing
    /// at the wheel.
    /// </summary>
    private static GameWorld TwoAgainstTwo(PlayerKind blue)
    {
        var entry = BattleCatalogue.Map("m_desert")!;
        var options = BattleBots.Compose(new BattleOptions { Tier = 3, TeamA = entry.TeamA, TeamB = entry.TeamB }, [], entry.Scenario);
        var rows = new[] { Race.Red, Race.Blue, Race.Green, Race.Yellow }
            .Select(race => new PlayerSeat(PlayerId.Of(race), race, race == Race.Blue ? blue : PlayerKind.Human, race.ToString()))
            .ToList();
        var world = MatchSetup.Create(Data, entry.Scenario, entry.Planet, new PlayerTable(rows), 7, new MatchOptions { Battle = options, FogOfWar = false });
        foreach (var race in new[] { Race.Red, Race.Green, Race.Yellow })
            world.HeroOf(PlayerId.Of(race))!.DrivenByPlayer = true;
        return world;
    }

    private static PresenceChange Join(PlayerId player, string name) => new(player, Away: false, Joins: name);

    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    [Fact]
    public void Twin_worlds_stay_equal_through_a_join_and_a_let_go()
    {
        var (one, two) = (FiveAgainstFive(people: 0), FiveAgainstFive(people: 0));
        var hero = one.HeroOf(P(5))!.Id;

        for (var tick = 0; tick < 700; tick++)
        {
            foreach (var world in new[] { one, two })
            {
                if (tick == 250)
                    world.ChangePresence([Join(P(5), "Ben")]);
                if (tick is >= 260 and < 400 && tick % 10 == 0)
                    world.Issue(new DriveCommand(Race.Red, hero, true, Forward: 1, Turn: tick % 20 == 0 ? 1 : 0, Buttons: true) { Issuer = P(5) });
                if (tick == 330)
                    world.LetGo(P(5));
                if (tick == 400)
                    world.LetGo(P(0));
                world.Step();
            }
            Assert.Equal(one.Checksum(), two.Checksum());
        }
        Assert.Equal([P(5)], one.Players.Joined);
        Assert.Equal(700, one.Tick);
    }

    [Fact]
    public void A_joined_bot_seat_s_hero_is_the_person_s_from_the_join_and_a_bot_s_again_when_they_leave()
    {
        var world = FiveAgainstFive();
        var run = world.Battle!;
        var hero = world.HeroOf(P(5))!;
        Battles.Run(world, 12);
        Assert.Contains(P(5), world.HeroBots.Keys);
        Assert.Contains(P(5), run.Bots);
        Assert.Equal(PlayerKind.Bot, world.Players[P(5)].Kind);
        Assert.Equal(Controller.Ai, world.Races[Race.Red].Controller);
        Assert.Equal(DefCon.High, hero.DefCon);
        Assert.Empty(world.Players.Joined);

        world.ChangePresence([Join(P(5), "Ben")]);

        Assert.DoesNotContain(P(5), world.HeroBots.Keys);
        Assert.DoesNotContain(P(5), run.Bots);
        Assert.Equal((PlayerKind.Human, "Ben"), (world.Players[P(5)].Kind, world.Players[P(5)].Name));
        Assert.Equal((PlayerKind.Human, "Ben"), (world.Players.ById.Single(r => r.Id == P(5)).Kind, world.Players.ById.Single(r => r.Id == P(5)).Name));
        Assert.Equal(PlayerKind.Bot, world.Players.Rows.Single(r => r.Id == P(5)).Kind);
        Assert.Equal([P(5)], world.Players.Humans);
        Assert.Equal([P(5)], world.Players.Joined);
        Assert.True(world.Players.HasHuman(Race.Red));
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);
        // The hero as it stands, its orders the bot's no longer.
        Assert.Same(hero, world.HeroOf(P(5)));
        Assert.Equal(DefCon.Normal, hero.DefCon);
        Assert.IsType<IdleOrder>(hero.Order);
        // The person drives it; the other bots of the race go on.
        Assert.Null(Do(world, new DriveCommand(Race.Red, hero.Id, true, Forward: 1) { Issuer = P(5) }));
        Assert.Equal((true, P(5)), (hero.DrivenByPlayer, hero.Driver));
        Assert.All(new[] { P(0), P(4), P(6), P(7) }, bot => Assert.Contains(bot, world.HeroBots.Keys));

        // Gone again, a bot drives the hero; the seat stays the person's.
        world.ChangePresence([new PresenceChange(P(5), Away: true)]);

        Assert.Contains(P(5), world.HeroBots.Keys);
        Assert.Contains(P(5), run.Bots);
        Assert.False(hero.DrivenByPlayer);
        Assert.Equal(PlayerKind.Human, world.Players[P(5)].Kind);
        Assert.Equal(Controller.Ai, world.Races[Race.Red].Controller);
        Assert.Equal([P(5)], world.Players.Joined);

        // A new world of the same match starts from the rows as they were.
        var fresh = world.Players.Fresh();
        Assert.Empty(fresh.Joined);
        Assert.Equal(PlayerKind.Bot, fresh[P(5)].Kind);
        Assert.Empty(fresh.Humans);
    }

    [Fact]
    public void A_bot_race_of_two_against_two_joined_comes_back_through_its_race()
    {
        var world = TwoAgainstTwo(PlayerKind.Bot);
        Battles.Run(world, 12);
        Assert.False(world.Players.AnyShared);
        Assert.Equal(Controller.Ai, world.Races[Race.Blue].Controller);
        Assert.Contains(PlayerId.Of(Race.Blue), world.Battle!.Bots);

        world.ChangePresence([Join(PlayerId.Of(Race.Blue), "Ben")]);

        Assert.Equal(Controller.Player, world.Races[Race.Blue].Controller);
        Assert.DoesNotContain(PlayerId.Of(Race.Blue), world.Battle.Bots);
        Assert.DoesNotContain(PlayerId.Of(Race.Blue), world.HeroBots.Keys);
        Assert.Equal(PlayerKind.Human, world.Players[PlayerId.Of(Race.Blue)].Kind);
        Assert.Null(Do(world, new DriveCommand(Race.Blue, world.HeroOf(PlayerId.Of(Race.Blue))!.Id, true, Forward: 1) { Issuer = PlayerId.Of(Race.Blue) }));
    }

    /// <summary>
    /// Twin matches but for a join: in one, Blue is a bot's seat a person joins at tick 300; in the other, Blue is a person's
    /// from the start who is away until 300 (the bot drives meanwhile). With a race a seat nothing of who is away goes into the
    /// checksum, so both have the same sums until the join; from there the worlds are alike, and only the joined seat, which
    /// the checksum mixes in once there is one, tells them apart.
    /// </summary>
    [Fact]
    public void The_checksum_holds_the_joined_seats_once_there_is_one_and_nothing_before()
    {
        var joined = TwoAgainstTwo(PlayerKind.Bot);
        var away = TwoAgainstTwo(PlayerKind.Human);
        away.ChangePresence([new PresenceChange(PlayerId.Of(Race.Blue), Away: true)]);
        var blue = PlayerId.Of(Race.Blue);

        for (var tick = 0; tick < 600; tick++)
        {
            if (tick == 300)
            {
                joined.ChangePresence([Join(blue, "Ben")]);
                away.ChangePresence([new PresenceChange(blue, Away: false)]);
            }
            joined.Step();
            away.Step();
            if (tick < 300)
                Assert.Equal(away.Checksum(), joined.Checksum());
            else
            {
                Assert.NotEqual(away.Checksum(), joined.Checksum());
                Assert.Equal(away.Actors.Select(a => (a.Id, a.Position, a.HitPoints, a.Armour)), joined.Actors.Select(a => (a.Id, a.Position, a.HitPoints, a.Armour)));
            }
        }
        Assert.Equal(Controller.Player, joined.Races[Race.Blue].Controller);
        Assert.Equal(away.Races[Race.Blue].Controller, joined.Races[Race.Blue].Controller);
        Assert.Empty(away.Players.Joined);
    }

    [Fact]
    public void A_join_is_refused_for_a_person_s_seat_the_computer_s_race_and_a_seat_joined_already()
    {
        var world = FiveAgainstFive(people: 0);
        var before = world.Checksum();

        Assert.Throws<ArgumentException>(() => world.ChangePresence([Join(P(0), "Ben")]));
        // None of a tick's changes is made when one of them is refused.
        Assert.Throws<ArgumentException>(() => world.ChangePresence([new PresenceChange(P(0), Away: true), Join(P(0), "Ben")]));
        Assert.Throws<KeyNotFoundException>(() => world.ChangePresence([Join(P(30), "Ben")]));
        Assert.False(world.Players.Away(P(0)));
        Assert.Empty(world.Players.Joined);
        Assert.Equal(before, world.Checksum());

        world.ChangePresence([Join(P(5), "Ben")]);
        Assert.Throws<ArgumentException>(() => world.ChangePresence([Join(P(5), "Cleo")]));
        Assert.Throws<ArgumentException>(() => world.ChangePresence([Join(P(6), "Cleo"), Join(P(6), "Dan")]));
        Assert.Equal("Ben", world.Players[P(5)].Name);
        Assert.Equal(PlayerKind.Bot, world.Players[P(6)].Kind);

        var skirmish = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7);
        Assert.Throws<ArgumentException>(() => skirmish.ChangePresence([Join(PlayerId.Of(Race.Blue), "Ben")]));
        Assert.Equal(PlayerKind.Ai, skirmish.Players[PlayerId.Of(Race.Blue)].Kind);
    }

    [Fact]
    public void A_lag_mark_lets_go_of_exactly_the_lagging_player_s_wheels_and_the_next_drive_takes_them_again()
    {
        var (world, twin) = (FiveAgainstFive(people: [0, 4]), FiveAgainstFive(people: [0, 4]));
        var (mine, theirs) = (world.HeroOf(P(4))!, world.HeroOf(P(0))!);
        foreach (var match in new[] { world, twin })
        {
            // Past the opening, which holds every drive.
            Battles.Run(match, BattleBalance.Opening + GameWorld.Dt);
            Assert.Null(Do(match, new DriveCommand(Race.Red, mine.Id, true, Forward: 1, Buttons: true) { Issuer = P(4) }));
            Assert.Null(Do(match, new DriveCommand(Race.Red, theirs.Id, true, Forward: 1) { Issuer = P(0) }));
        }
        Assert.True(world.DriveInput.ContainsKey(mine.Id) && world.PlayerButtons.Contains(mine.Id));

        world.LetGo(P(4));

        Assert.False(mine.DrivenByPlayer);
        Assert.Null(mine.Driver);
        Assert.False(world.DriveInput.ContainsKey(mine.Id) || world.PlayerButtons.Contains(mine.Id));
        Assert.Equal((true, P(0)), (theirs.DrivenByPlayer, theirs.Driver));
        Assert.True(world.DriveInput.ContainsKey(theirs.Id));
        // Nothing drawn from the world's dice: the twin, which let go of nothing, draws the same next.
        Assert.Equal(twin.Random.Next(), world.Random.Next());

        Assert.Null(Do(world, new DriveCommand(Race.Red, mine.Id, true, Forward: 1) { Issuer = P(4) }));
        Assert.Equal((true, P(4)), (mine.DrivenByPlayer, mine.Driver));
    }
}
