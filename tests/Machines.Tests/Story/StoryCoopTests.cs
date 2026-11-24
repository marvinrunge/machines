using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The story played together (plan WP-M, decision 5; docs/STORY.md "Koop"), in the world alone: three seats, the Locator a
/// person's, Ike and Bob a person's or the computer's companion; a person who joins a Grunt's seat takes its hero over as it
/// stands (the same actor), one who leaves hands it back to the computer and the Grunt carries the host's name again, one who
/// comes back takes it again; the Locator's hero stands while its person is away. Bob's private lines show only on his
/// player's screen, the Grunts' barks only while the computer plays them, and only the Locator's game keeps the progress. A
/// three-seat story with its comings and goings is deterministic, through a recording too, and its match goes over the wire.
/// </summary>
public class StoryCoopTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static PlayerId P(int value) => new(value);

    private static readonly PlayerId Locator = P(0), Ike = P(4), Bob = P(5);

    // From the first checkpoint, where Ike and Bob are awake (the director keeps them asleep in their clamps before it).
    private static readonly StoryOptions Named = new() { IkeName = "Rust", BobName = "Bolt", Checkpoint = 1 };

    /// <summary>The prologue with the Locator a person's and Ike and Bob as <paramref name="ike"/> and <paramref name="bob"/> say, the ship the computer's.</summary>
    private static GameWorld Prologue(PlayerKind ike = PlayerKind.Bot, PlayerKind bob = PlayerKind.Bot, int seed = 5) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue, Table(ike, bob), seed, new MatchOptions { Story = Named, FogOfWar = false });

    private static PlayerTable Table(PlayerKind ike, PlayerKind bob) => new([
        new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
        new PlayerSeat(Ike, Race.Red, ike, ike == PlayerKind.Human ? "Anna" : "Rust"),
        new PlayerSeat(Bob, Race.Red, bob, bob == PlayerKind.Human ? "Ben" : "Bolt"),
        new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]);

    private static void Run(GameWorld world, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            world.Step();
    }

    /// <summary>The person of <paramref name="seat"/> drives its hero forward for a tick, as the lockstep issues it.</summary>
    private static void Drive(GameWorld world, PlayerId seat, float forward = 1)
    {
        world.Issue(new DriveCommand(Race.Red, world.HeroOf(seat)!.Id, true, forward) { Issuer = seat });
        world.Step();
    }

    private static Machine Hero(GameWorld world, PlayerId seat) => world.HeroOf(seat)!;

    private static FollowOrder Following(GameWorld world) => new(Hero(world, Locator).Id);

    // ---- seats --------------------------------------------------------------------------------------

    [Fact]
    public void Three_people_play_the_three_heroes_and_no_companion_follows()
    {
        var world = Prologue(PlayerKind.Human, PlayerKind.Human);
        var run = world.Story!;

        Assert.Equal([Locator, Ike, Bob], run.Seats);
        Assert.Empty(run.Bots);
        Assert.All(run.Seats, seat => Assert.False(world.StoryBotPlays(seat)));
        Assert.All(run.Seats, seat => Assert.Equal(Order.Idle, Hero(world, seat).Order));
        Assert.Equal(["Host", "Anna", "Ben"], run.Seats.Select(s => world.StorySeatName(s)!));
    }

    [Fact]
    public void Alone_the_computer_plays_both_grunts_under_the_hosts_names()
    {
        var world = Prologue();
        var run = world.Story!;

        Assert.Equal([Ike, Bob], run.Bots);
        Assert.Equal(["Host", "Rust", "Bolt"], run.Seats.Select(s => world.StorySeatName(s)!));
        Assert.All(run.Bots, seat => Assert.Equal(Following(world), Hero(world, seat).Order));
        // The Locator's seat is never the computer's, and nobody else is a seat.
        Assert.False(world.StoryBotPlays(Locator));
        Assert.False(world.StoryBotPlays(PlayerId.Of(Race.Blue)));
        Assert.Null(world.StorySeatName(PlayerId.Of(Race.Blue)));
    }

    [Fact]
    public void A_joiner_takes_a_bots_grunt_over_as_it_stands_and_keeps_the_actor()
    {
        var world = Prologue();
        Run(world, 60);
        var grunt = Hero(world, Ike);
        var (id, at) = (grunt.Id, grunt.Position);

        world.ChangePresence([new PresenceChange(Ike, Away: false, Joins: "Zoe")]);

        Assert.Same(grunt, Hero(world, Ike));
        Assert.Equal((id, at), (grunt.Id, grunt.Position));
        Assert.Equal(new PlayerSeat(Ike, Race.Red, PlayerKind.Human, "Zoe"), world.Players[Ike]);
        Assert.Equal([Ike], world.Players.Joined);
        Assert.Equal([Bob], world.Story!.Bots);
        Assert.Equal("Zoe", world.StorySeatName(Ike));
        // The computer's order is gone: the hero stands until its person drives it, and then it is theirs.
        Assert.Equal(Order.Idle, grunt.Order);
        Drive(world, Ike);
        Assert.Empty(world.Rejected);
        Assert.True(grunt.DrivenByPlayer);
        Assert.Equal(Ike, grunt.Driver);
        Assert.Equal(Following(world), Hero(world, Bob).Order);
    }

    [Fact]
    public void A_leaver_hands_the_grunt_back_to_the_computer_which_carries_the_hosts_name_again()
    {
        var world = Prologue(ike: PlayerKind.Human);
        Drive(world, Ike);
        var grunt = Hero(world, Ike);
        Assert.True(grunt.DrivenByPlayer);
        Assert.Equal("Anna", world.StorySeatName(Ike));

        world.ChangePresence([new PresenceChange(Ike, Away: true)]);

        // The same hero, let go of and following the Locator, under the name the host gave it.
        Assert.Same(grunt, Hero(world, Ike));
        Assert.False(grunt.DrivenByPlayer);
        Assert.Equal(Following(world), grunt.Order);
        Assert.Equal([Ike, Bob], world.Story!.Bots);
        Assert.True(world.StoryBotPlays(Ike));
        Assert.Equal("Rust", world.StorySeatName(Ike));
        // The row stays Anna's: she may come back to it, and takes it over again as it stands.
        Assert.Equal("Anna", world.Players[Ike].Name);
        Run(world, 40);

        world.ChangePresence([new PresenceChange(Ike, Away: false)]);

        Assert.Same(grunt, Hero(world, Ike));
        Assert.Equal(Order.Idle, grunt.Order);
        Assert.Equal([Bob], world.Story.Bots);
        Assert.Equal("Anna", world.StorySeatName(Ike));
    }

    [Fact]
    public void A_late_joiner_who_leaves_hands_the_grunt_back_too()
    {
        var world = Prologue();
        world.ChangePresence([new PresenceChange(Bob, Away: false, Joins: "Zoe")]);
        Drive(world, Bob);

        world.ChangePresence([new PresenceChange(Bob, Away: true)]);

        Assert.Equal([Ike, Bob], world.Story!.Bots);
        Assert.False(Hero(world, Bob).DrivenByPlayer);
        Assert.Equal(Following(world), Hero(world, Bob).Order);
        Assert.Equal("Bolt", world.StorySeatName(Bob));
    }

    [Fact]
    public void The_locators_hero_stands_while_the_host_is_away_and_is_no_companion()
    {
        var world = Prologue(ike: PlayerKind.Human);
        Drive(world, Locator);
        var locator = Hero(world, Locator);
        Assert.True(locator.DrivenByPlayer);

        world.ChangePresence([new PresenceChange(Locator, Away: true)]);

        Assert.False(locator.DrivenByPlayer);
        Assert.DoesNotContain(Locator, world.Story!.Bots);
        Assert.Equal([Bob], world.Story.Bots);
        Assert.False(world.StoryBotPlays(Locator));
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);
    }

    [Fact]
    public void Comings_and_goings_of_one_tick_settle_once_in_seat_order()
    {
        var world = Prologue(ike: PlayerKind.Human);
        Drive(world, Ike);

        // Anna leaves as Zoe joins Bob's seat, in the same tick.
        world.ChangePresence([new PresenceChange(Ike, Away: true), new PresenceChange(Bob, Away: false, Joins: "Zoe")]);

        Assert.Equal([Ike], world.Story!.Bots);
        Assert.Equal(Following(world), Hero(world, Ike).Order);
        Assert.Equal(Order.Idle, Hero(world, Bob).Order);
        // A seat a person plays cannot be joined; one the table does not know, neither.
        Assert.Throws<ArgumentException>(() => world.ChangePresence([new PresenceChange(Bob, Away: false, Joins: "Yan")]));
    }

    // ---- who sees what, who keeps what ---------------------------------------------------------------

    [Fact]
    public void Bobs_private_lines_show_only_on_his_players_screen_and_the_barks_only_while_the_computer_plays()
    {
        var alone = Prologue();
        var together = Prologue(PlayerKind.Human, PlayerKind.Human);

        // Alone: the Grunts are the computer's, so their barks are heard; Bob has no player to whisper to.
        Assert.True(StoryAudiences.Shows(alone, StoryAudience.Everyone, 0, Locator));
        Assert.True(StoryAudiences.Shows(alone, StoryAudience.Companion, 2, Locator));
        Assert.False(StoryAudiences.Shows(alone, StoryAudience.Private, 2, Locator));
        Assert.False(StoryAudiences.Shows(alone, StoryAudience.Private, 2, Bob));

        // Together: no barks for the people's Grunts, and Bob's foreign messages on Bob's screen only.
        Assert.False(StoryAudiences.Shows(together, StoryAudience.Companion, 1, Locator));
        Assert.True(StoryAudiences.Shows(together, StoryAudience.Private, 2, Bob));
        Assert.False(StoryAudiences.Shows(together, StoryAudience.Private, 2, Locator));
        Assert.False(StoryAudiences.Shows(together, StoryAudience.Private, 2, Ike));
        Assert.True(StoryAudiences.Shows(together, StoryAudience.Everyone, 2, Ike));

        // Bob's player leaves: the computer plays him, his barks come back and his private lines go nowhere.
        together.ChangePresence([new PresenceChange(Bob, Away: true)]);
        Assert.True(StoryAudiences.Shows(together, StoryAudience.Companion, 2, Locator));
        Assert.False(StoryAudiences.Shows(together, StoryAudience.Private, 2, Bob));

        // Outside a story, or for a seat there is not, only the lines for everyone.
        var skirmish = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai });
        Assert.True(StoryAudiences.Shows(skirmish, StoryAudience.Everyone, 0, Locator));
        Assert.False(StoryAudiences.Shows(skirmish, StoryAudience.Companion, 1, Locator));
        Assert.False(StoryAudiences.Shows(together, StoryAudience.Private, 3, Bob));
    }

    [Fact]
    public void Only_the_locators_game_keeps_the_progress()
    {
        var players = Prologue(PlayerKind.Human, PlayerKind.Human).Players;

        Assert.True(StoryCoop.KeepsProgress(players, Named, Locator));
        Assert.False(StoryCoop.KeepsProgress(players, Named, Ike));
        Assert.False(StoryCoop.KeepsProgress(players, Named, Bob));
        Assert.False(StoryCoop.KeepsProgress(players, Named, null));
        Assert.Equal([0, 1, 2, -1], new[] { Locator, Ike, Bob, PlayerId.Of(Race.Blue) }.Select(p => StoryCoop.SeatOf(players, Named, p)));
    }

    // ---- determinism and the wire ---------------------------------------------------------------------

    /// <summary>
    /// A three-seat story as the relay plays it: Anna at Ike's seat from the start, Bob the computer's; Zoe joins Bob's seat at
    /// tick 150, Anna leaves at 300 and comes back at 450, every person driving now and then. Returns the checksums every 50 ticks.
    /// </summary>
    private static List<ulong> Played(int seed, bool zoeJoins = true)
    {
        var world = Prologue(ike: PlayerKind.Human, seed: seed);
        var random = new Random(seed);
        var sums = new List<ulong>();
        for (var tick = 0; tick < 600; tick++)
        {
            if (tick == 150 && zoeJoins)
                world.ChangePresence([new PresenceChange(Bob, Away: false, Joins: "Zoe")]);
            if (tick == 300)
                world.ChangePresence([new PresenceChange(Ike, Away: true)]);
            if (tick == 450)
                world.ChangePresence([new PresenceChange(Ike, Away: false)]);
            foreach (var seat in world.Story!.Seats.Where(s => !world.StoryBotPlays(s) && !world.Players.Away(s)))
            {
                if (random.Next(6) == 0 && world.HeroOf(seat) is { } hero)
                    world.Issue(new DriveCommand(Race.Red, hero.Id, random.Next(8) != 0, random.Next(3) - 1, random.Next(3) - 1) { Issuer = seat });
            }
            world.Step();
            if (tick % 50 == 49)
                sums.Add(world.Checksum());
        }
        return sums;
    }

    [Fact]
    public void A_three_seat_story_with_its_comings_and_goings_keeps_the_same_checksums()
    {
        var first = Played(9);

        Assert.Equal(first, Played(9));
        // The join is in the checksum from its tick on: a world that missed it differs.
        var without = Played(9, zoeJoins: false);
        Assert.Equal(first.Take(3), without.Take(3));
        Assert.NotEqual(first.Skip(3), without.Skip(3));
    }

    [Fact]
    public void A_recording_of_a_three_seat_story_with_a_join_and_a_leave_plays_back_the_same()
    {
        // From the repair station, where no hunter is near: a Grunt driven about on deck A would meet the Thresher.
        var match = new MatchInfo(StoryOptions.Prologue, StoryOptions.Prologue, new MatchOptions { Story = Named with { Checkpoint = 11 }, FogOfWar = false }, 21, 2,
        [
            new MatchPlayer(1, "Host", Race.Red, Race.Red, Ai: false, Locator),
            new MatchPlayer(2, "Anna", Race.Red, Race.Red, Ai: false, Ike),
            new MatchPlayer(0, "Bolt", Race.Red, Race.Red, Ai: true, Bob),
            new MatchPlayer(0, "Ship", Race.Blue, Race.Blue, Ai: true, PlayerId.Of(Race.Blue)),
        ]);
        // The match's table: the empty Grunt's seat is the computer's bot, the ship the computer's race.
        Assert.Equal([PlayerKind.Human, PlayerKind.Human, PlayerKind.Bot, PlayerKind.Ai], match.Table().Rows.Select(r => r.Kind));

        var world = Replay.BuildWorld(Data, match);
        var hero = world.HeroOf(Ike)!.Id;
        var turns = new List<Turn>();
        for (var tick = 0L; tick < 400; tick++)
        {
            if (tick % 20 == 3 && tick is < 200 or >= 300)
                turns.Add(new Turn(tick, Ike, [new DriveCommand(Race.Red, hero, true, 1, tick % 40 == 3 ? 1 : 0)]));
            if (tick % 30 == 7)
                turns.Add(new Turn(tick, Locator, [new DriveCommand(Race.Red, world.HeroOf(Locator)!.Id, true, -1)]));
        }
        NetMessage[] presence = [new PlayerJoined(Bob, 120, "Zoe"), new PlayerLeft(Ike, 200), new PlayerReturned(Ike, 300)];
        var replay = new Replay(match, turns, 400, presence);

        // The recording goes over the wire and to a file as JSON, and plays the same each time.
        var copy = JsonSerializer.Deserialize<Replay>(JsonSerializer.SerializeToUtf8Bytes(replay, NetJson.Options), NetJson.Options)!;
        var sums = new[] { replay, copy }.Select(r =>
        {
            var playback = r.Playback(Replay.BuildWorld(Data, r.Match));
            var seen = new List<ulong>();
            while (playback.World.Tick < r.Ticks)
            {
                Assert.True(playback.TryStep(), $"stuck at tick {playback.World.Tick}");
                if (playback.World.Tick % 50 == 0)
                    seen.Add(playback.World.Checksum());
            }
            var story = playback.World.Story!;
            Assert.Empty(story.Bots);
            Assert.Equal("Zoe", playback.World.StorySeatName(Bob));
            Assert.Equal(hero, playback.World.HeroOf(Ike)!.Id);
            return seen;
        }).ToList();
        Assert.Equal(sums[0], sums[1]);
        Assert.Equal(8, sums[0].Count);
    }

    [Fact]
    public void Story_commands_go_through_the_lockstep_and_the_protocol_is_nineteen()
    {
        // 15 brought the story, 16 the fitted bodies of every mode, 17 the computers' skirmish rules and the heroes' gun arcs.
        // 18 story stage 7 (checkpoints 0–14), 19 the play test's Thresher (slower round, crab walk).
        Assert.Equal(19, NetJson.Protocol);
        // Every command of the sim, a story command among them however deep it derives, is known to the JSON by its name.
        var known = NetJson.Options.GetTypeInfo(typeof(Command)).PolymorphismOptions!.DerivedTypes.Select(d => d.DerivedType).ToHashSet();
        var commands = typeof(Command).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Command)) && !t.IsAbstract).ToList();
        Assert.All(commands, type => Assert.Contains(type, known));
        Assert.All(commands.Where(t => t.IsSubclassOf(typeof(StoryCommand))), type => Assert.Contains(type, known));
        Assert.DoesNotContain(typeof(StoryCommand), known);

        // A story's match goes to every client whole: its options, and a table every world builds alike.
        var started = new MatchStarted(new MatchInfo(StoryOptions.Prologue, StoryOptions.Prologue,
            new MatchOptions { Story = Named with { Checkpoint = 6 } }, 3, 2,
            [new MatchPlayer(1, "Host", Race.Red, Race.Red, Ai: false, Locator), new MatchPlayer(0, "Rust", Race.Red, Race.Red, Ai: true, Ike)]));
        var back = (MatchStarted)NetJson.Decode(NetJson.Encode(started));
        Assert.Equal(started.Match.Options.Story, back.Match.Options.Story);
        Assert.Equal(started.Match.Table().Rows, back.Match.Table().Rows);
        var world = Replay.BuildWorld(Data, back.Match);
        Assert.Equal(6, world.Story!.Checkpoint);
        Assert.Equal([Ike, Bob], world.Story.Bots);
        Assert.Equal(["Host", "Rust", "Bolt"], world.Story.Seats.Select(s => world.StorySeatName(s)!));
    }
}
