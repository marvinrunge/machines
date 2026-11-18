using System.Reflection;
using System.Text.Json.Nodes;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Sim;

/// <summary>
/// The golden cases played in bundle mode (P2 design §8.2, T2): the same matches, the same scripts and the same notices as
/// <see cref="GoldenSumTests"/>, but every tick's turns and comings and goings come as one bundle of a relay on time
/// (<see cref="GoldenMatch"/> with <see cref="SessionMode.Bundles"/>). Every case must give the very constants pinned in
/// <see cref="GoldenSumTests"/> (read from there, so there is one copy of them), keep their results, and its recording must
/// play back in turn mode to the same sums: a session in bundle mode runs a tick with the same calls on the world as one in
/// turn mode (D2), and leaving empty turns out changes nothing (D7).
/// </summary>
public class GoldenBundleTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    /// <summary>The field of <see cref="GoldenSumTests"/> holding each case's pinned sums, and the one holding its battle's result.</summary>
    private static readonly Dictionary<string, (string Sums, string? Result)> Pinned = new()
    {
        [GoldenCase.Skirmish.Name] = ("SkirmishSums", null),
        [GoldenCase.FourWay.Name] = ("FourWaySums", null),
        [GoldenCase.Wired.Name] = ("WiredSums", null),
        [GoldenCase.Battle.Name] = ("BattleSums", "BattleEnded"),
        [GoldenCase.BotBattle.Name] = ("BotBattleSums", "BotBattleEnded"),
        [GoldenCase.LeaverBattle.Name] = ("LeaverBattleSums", "LeaverBattleEnded"),
        [GoldenCase.ReturningBattle.Name] = ("ReturningBattleSums", "ReturningBattleEnded"),
        [GoldenCase.Orders.Name] = ("OrdersSums", null),
        [GoldenCase.WiredOrders.Name] = ("WiredOrdersSums", null),
    };

    private static T PinnedIn<T>(string field) =>
        (T)typeof(GoldenSumTests).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    public static TheoryData<string> Cases => [.. GoldenCase.All.Select(c => c.Name)];

    [Fact]
    public void Every_golden_case_has_its_pinned_sums_here() =>
        Assert.Equal(GoldenCase.All.Select(c => c.Name).Order(), Pinned.Keys.Order());

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_golden_case_played_on_bundles_keeps_its_pinned_sums_and_plays_back_in_turn_mode(string name)
    {
        var golden = GoldenCase.All.Single(c => c.Name == name);
        var (sumsField, resultField) = Pinned[name];
        var expected = PinnedIn<ulong[]>(sumsField);
        var match = new GoldenMatch(golden, Data, SessionMode.Bundles);

        var sums = match.PlayTo(golden.Ticks);

        output.WriteLine(string.Join(", ", sums.Select(s => $"0x{s:X16}UL")));
        Assert.Equal(SessionMode.Bundles, match.Session.Mode);
        Assert.Equal(expected, sums);
        Assert.Equal(golden.Notices.Length, match.Session.Presence.Count);
        if (resultField is not null)
            Assert.Equal(PinnedIn<BattleResult>(resultField), match.World.Battle!.Result);
        // What GoldenSumTests asks of the cases beside their sums.
        if (golden.Name == GoldenCase.Orders.Name)
            Assert.Equal((Controller.Player, Controller.Ai), (match.World.Races[Race.Red].Controller, match.World.Races[Race.Blue].Controller));
        if (golden.Name == GoldenCase.Wired.Name)
            Assert.Equal((WiredPhase.Wave, 4), (match.World.Wired!.Phase, match.World.Wired.Wave));
        if (golden.Name == GoldenCase.WiredOrders.Name)
            Assert.Equal((WiredPhase.Wave, 5), (match.World.Wired!.Phase, match.World.Wired.Wave));
        // The turns ran with commands, each recorded at the tick it ran on, without what only a bundle says of it.
        if (golden.Scripts().Length > 0)
            Assert.NotEmpty(match.Session.Played);
        Assert.All(match.Session.Played, turn => Assert.Equal((-1L, 0, -1L), (turn.Seen, turn.Held, turn.Asked)));

        // Its recording, saved and loaded, plays back in turn mode to the same sums and result.
        var loaded = SaveAndLoad(match.Recording);
        Assert.Equal(NetJson.Protocol, loaded.Format);
        var (again, playback) = PlayBack(loaded, golden.Ticks);
        Assert.Equal(expected, again);
        Assert.Equal(match.World.Battle?.Result, playback.World.Battle?.Result);
    }

    /// <summary>
    /// A recording stamped 13, the format before bundles, still loads and plays (<see cref="Replay.OldestFormat"/> stays 13):
    /// the shape of its turns and comings and goings is today's.
    /// </summary>
    [Fact]
    public void A_recording_stamped_13_still_plays_to_its_sums()
    {
        var golden = GoldenCase.FourWay;
        var match = new GoldenMatch(golden, Data);
        var sums = match.PlayTo(golden.Ticks);
        var path = Path.Combine(Path.GetTempPath(), $"golden13-{Guid.NewGuid():N}{Replay.Extension}");
        Replay loaded;
        try
        {
            match.Recording.Save(path);
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["Format"] = 13;
            // Nothing of a turn in a recording names what bundles add.
            Assert.All(node["Turns"]!.AsArray(), turn => Assert.False(turn!.AsObject().ContainsKey("Seen") || turn.AsObject().ContainsKey("Held") || turn.AsObject().ContainsKey("Asked")));
            File.WriteAllText(path, node.ToJsonString());
            loaded = Replay.Load(path);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(13, loaded.Format);
        Assert.Equal(13, Replay.OldestFormat);
        Assert.Equal(PinnedIn<ulong[]>("FourWaySums"), sums);
        Assert.Equal(sums, PlayBack(loaded, golden.Ticks).Sums);
    }

    private static Replay SaveAndLoad(Replay recording)
    {
        var path = Path.Combine(Path.GetTempPath(), $"golden-bundles-{Guid.NewGuid():N}{Replay.Extension}");
        try
        {
            recording.Save(path);
            return Replay.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (ulong[] Sums, LockstepSession Playback) PlayBack(Replay recording, int[] ticks)
    {
        var playback = recording.Playback(Replay.BuildWorld(Data, recording.Match));
        Assert.Equal(SessionMode.Turns, playback.Mode);
        var sums = new List<ulong>();
        foreach (var tick in ticks)
        {
            while (playback.World.Tick < tick)
                Assert.True(playback.TryStep(), $"the playback stuck at tick {playback.World.Tick}");
            sums.Add(playback.World.Checksum());
        }
        return ([.. sums], playback);
    }
}
