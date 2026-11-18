using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>Recordings and saved games of campaign missions, and recordings and saves from before players apart from races.</summary>
public class CampaignReplayTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    [Fact]
    public void A_campaign_players_saved_games_are_found_and_no_others()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"saves-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            void Save(string name, CampaignCarry? carry) => new Replay(new MatchInfo("sat", "sat",
                new MatchOptions { Victory = VictoryCondition.Scripted, Campaign = carry }, 4, 1, []), [], 0).Save(Path.Combine(folder, name));
            Save("anna.msave", new CampaignCarry { Player = "Anna" });
            Save("ben.msave", new CampaignCarry { Player = "Ben" });
            Save("old.msave", new CampaignCarry());
            Save("skirmish.msave", null);
            File.WriteAllText(Path.Combine(folder, "broken.msave"), "not a save");

            var saves = Replay.CampaignSaves(folder, ".msave", "Anna");

            Assert.Equal("anna.msave", Path.GetFileName(Assert.Single(saves)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_saved_campaign_mission_comes_back_with_what_it_carried_over()
    {
        var carry = new CampaignCarry
        {
            Flags = { ["commsat"] = ["done"] },
            Survivors = { ["commsat"] = [new SurvivingMachine(Race.Red, "AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2")] },
        };
        var match = new MatchInfo("sat", "sat", new MatchOptions { Victory = VictoryCondition.Scripted, Campaign = carry }, 4, 1,
        [
            new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false),
            new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true),
            new MatchPlayer(0, "AI", Race.Green, Race.Green, Ai: true),
        ]);
        var straight = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), PlayerId.Of(Race.Red), match.InputDelay);
        var rng = new Random(9);
        // It came, though the walks it is sent on may well take it to its end among the enemy's guns.
        var carried = false;
        for (var i = 0; i < 400; i++)
        {
            if (rng.Next(20) == 0 && straight.World.Machines.Where(m => m.Race == Race.Red).ToList() is { Count: > 0 } own)
                straight.Queue(new MoveCommand(Race.Red, [own[rng.Next(own.Count)].Id], new Vector2(300 + rng.Next(300), 100 + rng.Next(300))));
            straight.Pump();
            Assert.True(straight.TryStep());
            carried |= straight.World.Machines.Any(m => m.Race == Race.Red && m.Spec.SubType == "KNIGHT");
        }
        Assert.NotNull(straight.World.Script);
        Assert.True(carried);

        var path = Path.Combine(Path.GetTempPath(), $"campaign-{Guid.NewGuid():N}.msave");
        new Replay(match, straight.Played, straight.World.Tick).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);

        Assert.Equal(VictoryCondition.Scripted, loaded.Match.Options.Victory);
        Assert.Equal(["done"], loaded.Match.Options.Campaign!.Flags["commsat"]);
        Assert.Equal(carry.Survivors["commsat"], loaded.Match.Options.Campaign.Survivors["commsat"]);
        var resumed = loaded.Resume(Replay.BuildWorld(DataLoader.Value, loaded.Match), PlayerId.Of(Race.Red));
        while (resumed.World.Tick < loaded.Ticks && resumed.TryStep())
        {
        }
        Assert.Equal(straight.World.Checksum(), resumed.World.Checksum());
    }

    /// <summary>
    /// A file as the game wrote it before players were told apart from races: today's, its stamp taken away (as every file
    /// before had none) or set to <paramref name="format"/>, and its turns named by race.
    /// </summary>
    private static string Older(string path, int? format)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("Format");
        if (format is { } stamp)
            node["Format"] = stamp;
        foreach (var turn in node["Turns"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
        {
            turn.Remove("Player");
            turn["Race"] = "Red";
        }
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    private static MatchInfo Skirmish(MatchOptions options) => new("m_desert", "m_desert", options, 1, 1,
        [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false), new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true)]);

    [Fact]
    public void A_file_saved_today_carries_the_protocol_as_its_stamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"today-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(Skirmish(new MatchOptions()), [new Turn(1, PlayerId.Of(Race.Red), [])], 3).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);

        Assert.Equal(13, Replay.OldestFormat);
        Assert.Equal(NetJson.Protocol, loaded.Format);
        Assert.Equal(PlayerId.Of(Race.Red), loaded.Turns.Single().Player);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(12)]
    public void A_recording_from_before_players_apart_from_races_is_refused_and_says_why(int? format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(Skirmish(new MatchOptions()), [new Turn(1, PlayerId.Of(Race.Red), [])], 3).Save(path);
        try
        {
            var refusal = Assert.Throws<RecordingTooOldException>(() => Replay.Load(Older(path, format)));
            Assert.Equal("This recording is from an older version of the game and can no longer be played.", refusal.Message);
            Assert.IsAssignableFrom<System.Text.Json.JsonException>(refusal);
            Assert.True(File.Exists(path), "the refused file is kept");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_saved_game_from_before_players_apart_from_races_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}.msave");
        new Replay(Skirmish(new MatchOptions()), [new Turn(1, PlayerId.Of(Race.Red), [])], 30,
            [new PlayerLeft(PlayerId.Of(Race.Red), 10)]).Save(path);
        try
        {
            Assert.Throws<RecordingTooOldException>(() => Replay.Load(Older(path, format: null)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_campaign_save_from_before_players_apart_from_races_is_refused_and_passed_over()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"saves-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            void Save(string name) => new Replay(new MatchInfo("sat", "sat",
                    new MatchOptions { Victory = VictoryCondition.Scripted, Campaign = new CampaignCarry { Player = "Anna" } }, 4, 1,
                    [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false)]), [new Turn(1, PlayerId.Of(Race.Red), [])], 3)
                .Save(Path.Combine(folder, name));
            Save("today.msave");
            Save("old.msave");
            Older(Path.Combine(folder, "old.msave"), format: null);

            Assert.Throws<RecordingTooOldException>(() => Replay.Load(Path.Combine(folder, "old.msave")));
            Assert.Equal("today.msave", Path.GetFileName(Assert.Single(Replay.CampaignSaves(folder, ".msave", "Anna"))));
            Assert.True(File.Exists(Path.Combine(folder, "old.msave")), "the refused save is kept");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_recording_from_before_the_ai_level_plays_its_ais_as_normal()
    {
        var match = new MatchInfo("m_desert", "m_desert", new MatchOptions { AiLevel = AiLevel.Hard }, 1, 1,
            [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false), new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true)]);
        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(match, [], 0).Save(path);
        Assert.Equal(AiLevel.Hard, Replay.Load(path).Match.Options.AiLevel);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"AiLevel\":\"Hard\",", "").Replace(",\"AiLevel\":\"Hard\"", ""));
        var loaded = Replay.Load(path);
        File.Delete(path);
        Assert.Equal(AiLevel.Normal, loaded.Match.Options.AiLevel);
    }

    [Fact]
    public void A_recording_keeps_its_construction_restrictions_and_without_them_everything_is_buildable()
    {
        // Dogbar keeps the level 5 military factory from Red; with the restrictions off it can be built.
        var match = new MatchInfo("dogbar", "dogbar", new MatchOptions { Victory = VictoryCondition.Scripted }, 3, 1,
            [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false), new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true)]);
        var path = Path.Combine(Path.GetTempPath(), $"restricted-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(match, [], 0).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);
        Assert.True(loaded.Match.Options.RestrictConstruction);

        var factory = new ConstructionOrderEntry("FACTORY", "MILITARY", 5);
        foreach (var (options, buildable) in new[] { (loaded.Match.Options, false), (match.Options with { RestrictConstruction = false }, true) })
        {
            var world = Replay.BuildWorld(DataLoader.Value, match with { Options = options });
            var dozer = world.Machines.First(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR"));
            world.AddMachine(Race.Red, dozer.Position + new Vector2(30, 0), "CONSTRUCTOR", "BEHEMOTH", 5, null);
            Assert.Equal(buildable, world.Buildable(Race.Red).Contains(factory));
        }
    }
}
