using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>Recordings and saved games of campaign missions, and recordings from before artefacts could be destroyed.</summary>
public class CampaignReplayTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

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
        var straight = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), Race.Red, [Race.Red], match.InputDelay);
        var rng = new Random(9);
        for (var i = 0; i < 400; i++)
        {
            if (rng.Next(20) == 0 && straight.World.Machines.Where(m => m.Race == Race.Red).ToList() is { Count: > 0 } own)
                straight.Queue(new MoveCommand(Race.Red, [own[rng.Next(own.Count)].Id], new Vector2(300 + rng.Next(300), 100 + rng.Next(300))));
            straight.Pump();
            Assert.True(straight.TryStep());
        }
        Assert.NotNull(straight.World.Script);
        Assert.Contains(straight.World.Machines, m => m.Race == Race.Red && m.Spec.SubType == "KNIGHT");

        var path = Path.Combine(Path.GetTempPath(), $"campaign-{Guid.NewGuid():N}.msave");
        new Replay(match, straight.Played, straight.World.Tick).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);

        Assert.Equal(VictoryCondition.Scripted, loaded.Match.Options.Victory);
        Assert.Equal(["done"], loaded.Match.Options.Campaign!.Flags["commsat"]);
        Assert.Equal(carry.Survivors["commsat"], loaded.Match.Options.Campaign.Survivors["commsat"]);
        var resumed = loaded.Resume(Replay.BuildWorld(DataLoader.Value, loaded.Match), Race.Red);
        while (resumed.World.Tick < loaded.Ticks && resumed.TryStep())
        {
        }
        Assert.Equal(straight.World.Checksum(), resumed.World.Checksum());
    }

    [Fact]
    public void A_recording_from_before_destructible_artefacts_keeps_them_whole()
    {
        var match = new MatchInfo("m_desert", "m_desert", new MatchOptions(), 1, 1,
            [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false), new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true)]);
        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(match, [], 0).Save(path);
        Assert.True(Replay.Load(path).Match.Options.DestructibleArtefacts);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"DestructibleArtefacts\":true,", ""));
        Assert.False(Replay.Load(path).Match.Options.DestructibleArtefacts);
        File.Delete(path);
    }

    [Fact]
    public void A_recording_from_before_original_motion_moves_as_it_did()
    {
        var match = new MatchInfo("m_desert", "m_desert", new MatchOptions(), 1, 1,
            [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false), new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true)]);
        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(match, [], 0).Save(path);
        Assert.True(Replay.Load(path).Match.Options.OriginalMotion);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"OriginalMotion\":true,", "").Replace(",\"OriginalMotion\":true", ""));
        var loaded = Replay.Load(path);
        File.Delete(path);
        Assert.False(loaded.Match.Options.OriginalMotion);
        Assert.True(loaded.Match.Options.DestructibleArtefacts);
    }
}
