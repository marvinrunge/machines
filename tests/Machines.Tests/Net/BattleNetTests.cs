using System.Numerics;
using System.Text.Json;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>A battle as lockstep sees it: its options and heroes on the wire, and saved games and recordings of it.</summary>
public class BattleNetTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    /// <summary>A player's hero of a tier, as their pod would send it: a bot's of Canyon Attack.</summary>
    internal static BattleHero Brought(string name, int tier, int line = 0) =>
        BattleBots.Bot("m_desert", tier, Race.Red, line) with { Name = name, Bot = false };

    /// <summary>A battle on Canyon Attack at tier III: Anna plays Red, Ben Green, the computer Blue and Yellow.</summary>
    private static MatchInfo Battle(int seed = 5)
    {
        var map = BattleCatalogue.Map("m_desert")!;
        var options = BattleBots.Compose(new BattleOptions { Tier = 3, Lives = 2, TeamA = map.TeamA, TeamB = map.TeamB },
            [Brought("Anna", 3) with { Race = Race.Red }, Brought("Ben", 2) with { Race = Race.Green }], map.Scenario);
        return new MatchInfo("m_desert", "m_desert", new MatchOptions { Battle = options, FogOfWar = false }, seed, 2,
        [
            new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false),
            new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true),
            new MatchPlayer(2, "Ben", Race.Green, Race.Green, Ai: false),
            new MatchPlayer(0, "AI", Race.Yellow, Race.Yellow, Ai: true),
        ]);
    }

    internal static void SameHero(BattleHero expected, BattleHero actual)
    {
        Assert.Equal((expected.Race, expected.Name, expected.Chassis, expected.Bot), (actual.Race, actual.Name, actual.Chassis, actual.Bot));
        Assert.Equal(expected.Items, actual.Items);
    }

    [Fact]
    public void The_protocol_went_up_for_the_guns_arcs() => Assert.Equal(16, NetJson.Protocol);

    [Fact]
    public void The_options_of_a_battle_and_its_heroes_come_through_the_wire()
    {
        var sent = Battle();
        var json = JsonSerializer.Serialize(sent, NetJson.Options);

        var match = JsonSerializer.Deserialize<MatchInfo>(json, NetJson.Options)!;

        var (went, came) = (sent.Options.Battle!, match.Options.Battle!);
        Assert.Equal((went.Tier, went.Lives, went.RespawnWait, went.Win, went.TimeLimit), (came.Tier, came.Lives, came.RespawnWait, came.Win, came.TimeLimit));
        Assert.Equal(went.TeamA, came.TeamA);
        Assert.Equal(went.TeamB, came.TeamB);
        Assert.Equal(4, came.Heroes.Count);
        for (var i = 0; i < went.Heroes.Count; i++)
            SameHero(went.Heroes[i], came.Heroes[i]);
        Assert.Contains(came.Heroes, h => h.Items.Any(item => item.Slot is not null && item.Mk == 1));
        Assert.Null(match.Options.Wired);
    }

    [Fact]
    public void A_hero_in_the_room_and_a_battle_room_in_the_list_come_through_the_wire()
    {
        var hero = Brought("Anna", 4);

        var set = (SetPlayer)NetJson.Decode(NetJson.Encode(new SetPlayer(Race.Green, Ready: true, Terrain: 7, Hero: hero)));
        var player = (RoomState)NetJson.Decode(NetJson.Encode(new RoomState(1, "r", "m_desert", "m_desert", [Race.Red], new MatchOptions(), false, 1,
            [new LobbyPlayer(1, "Anna", Race.Red, true, null, hero)], [new RoomSeat(1, SeatUse.Open, Race.Red)])));
        var list = (RoomList)NetJson.Decode(NetJson.Encode(new RoomList([new RoomSummary(1, "r", "m_desert", "m_desert", 1, 4, false, RoomSummary.Battle, 4)])));

        Assert.Equal((Race.Green, true, 7L), (set.Colour, set.Ready, set.Terrain));
        SameHero(hero, set.Hero!);
        SameHero(hero, player.Players.Single().Hero!);
        Assert.Equal(new RoomSummary(1, "r", "m_desert", "m_desert", 1, 4, false, "battle", 4), list.Rooms.Single());
    }

    [Fact]
    public void What_clients_before_battles_sent_still_reads()
    {
        // As those clients wrote them: no hero, no mode.
        var set = NetJson.Decode("""{"$type":"setPlayer","Colour":"Blue","Ready":true}"""u8);
        var list = (RoomList)NetJson.Decode("""{"$type":"roomList","Rooms":[{"Id":3,"Name":"r","Scenario":"s","Planet":"p","Players":1,"MaxPlayers":2,"Started":false}]}"""u8);

        Assert.Equal(new SetPlayer(Race.Blue, true), set);
        Assert.Equal(new RoomSummary(3, "r", "s", "p", 1, 2, false), list.Rooms.Single());
        Assert.Null(list.Rooms.Single().Mode);
        Assert.Equal(0, list.Rooms.Single().Tier);
    }

    [Fact]
    public void A_saved_battle_comes_back_from_its_commands_alone_resumed_or_played_back()
    {
        var match = Battle();
        var live = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), PlayerId.Of(Race.Red), match.InputDelay);
        Assert.Equal(new[] { PlayerId.Of(Race.Red), PlayerId.Of(Race.Green) }, live.Humans);
        Assert.NotNull(live.World.Battle);
        Assert.Equal(new[] { PlayerId.Of(Race.Blue), PlayerId.Of(Race.Yellow) }, live.World.HeroBots.Keys);
        var rng = new Random(9);
        DriveCommand Drive(Race race) => new(race, live.World.HeroOf(race)?.Id ?? 0, true, rng.Next(3) - 1, rng.Next(3) - 1,
            live.World.HeroOf(race) is { } hero ? hero.Position + new Vector2(rng.Next(-60, 60), rng.Next(-60, 60)) : (Vector2?)null,
            Weapons: rng.Next(4), Buttons: rng.Next(2) == 0);
        for (var i = 0; i < 400; i++)
        {
            if (rng.Next(4) == 0)
                live.Queue(Drive(Race.Red));
            live.Pump();
            // Ben's turns, as the relay would bring them.
            if (live.World.Tick >= match.InputDelay)
                live.Receive(new Turn(live.World.Tick, PlayerId.Of(Race.Green), rng.Next(4) == 0 ? [Drive(Race.Green)] : []));
            Assert.True(live.TryStep(), $"stuck at tick {live.World.Tick}");
        }
        Assert.True(live.World.Tick >= 400);
        Assert.Contains(live.Played, t => t.Player == PlayerId.Of(Race.Green));

        var path = Path.Combine(Path.GetTempPath(), $"battle-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(match, live.Played, live.World.Tick).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);
        SameHero(match.Options.Battle!.Heroes[0], loaded.Match.Options.Battle!.Heroes[0]);

        var resumed = loaded.Resume(Replay.BuildWorld(DataLoader.Value, loaded.Match), PlayerId.Of(Race.Red));
        while (resumed.World.Tick < loaded.Ticks && resumed.TryStep())
        {
        }
        var playback = loaded.Playback(Replay.BuildWorld(DataLoader.Value, loaded.Match));
        while (playback.World.Tick < loaded.Ticks && playback.TryStep())
        {
        }
        Assert.Equal(live.World.Tick, resumed.World.Tick);
        Assert.Equal(live.World.Tick, playback.World.Tick);
        Assert.Equal(live.World.Checksum(), resumed.World.Checksum());
        Assert.Equal(live.World.Checksum(), playback.World.Checksum());
        Assert.Equal(live.World.Battle!.Fingerprint(), playback.World.Battle!.Fingerprint());
    }

    [Fact]
    public void A_recording_from_before_battles_loads_without_one()
    {
        var match = new MatchInfo("m_desert", "m_desert", new MatchOptions(), 4, 1, [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false)]);
        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}.mreplay");
        new Replay(match, [], 0).Save(path);
        var text = File.ReadAllText(path).Replace("\"Battle\":null,", "").Replace(",\"Battle\":null", "");
        Assert.DoesNotContain("\"Battle\"", text);
        File.WriteAllText(path, text);

        var loaded = Replay.Load(path);
        File.Delete(path);

        Assert.Null(loaded.Match.Options.Battle);
        Assert.NotEqual(VictoryCondition.Battle, loaded.Match.Options.Victory);
        Assert.Null(Replay.BuildWorld(DataLoader.Value, loaded.Match).Battle);
    }
}
