using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Sim;

/// <summary>Battle leaves the other modes alone: a normal match and a Wired run reach the same checksums they reached
/// before Battle was built (the constants were taken from the code without Battle).</summary>
public class BattleRegressionTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static ulong[] Checksums(GameWorld world, params int[] ticks)
    {
        var sums = new List<ulong>();
        foreach (var tick in ticks)
        {
            while (world.Tick < tick)
                world.Step();
            sums.Add(world.Checksum());
        }
        return [.. sums];
    }

    private static GameWorld Normal() => MatchSetup.Create(Data, "m_desert", "m_desert",
        new Dictionary<Race, Controller> { [Race.Red] = Controller.Ai, [Race.Blue] = Controller.Ai }, seed: 11);

    private static GameWorld FourWay() => MatchSetup.Create(Data, "fortmult", "fortmult",
        new Dictionary<Race, Controller>
        {
            [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai, [Race.Green] = Controller.Ai,
            [Race.Yellow] = Controller.Ai,
        }, seed: 3);

    private static GameWorld Wired() => MatchSetup.Create(Data, "m_desert", "m_desert",
        new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7,
        new MatchOptions { Wired = new WiredOptions() });

    [Fact]
    public void A_normal_match_keeps_its_checksums()
    {
        var sums = Checksums(Normal(), 1000, 6000);
        output.WriteLine(string.Join(", ", sums.Select(s => $"0x{s:X16}UL")));
        Assert.Equal(NormalSums, sums);
    }

    [Fact]
    public void A_four_way_match_keeps_its_checksums()
    {
        var sums = Checksums(FourWay(), 1000, 4000, 12000);
        output.WriteLine(string.Join(", ", sums.Select(s => $"0x{s:X16}UL")));
        Assert.Equal(FourWaySums, sums);
    }

    [Fact]
    public void A_wired_run_keeps_its_checksums()
    {
        var sums = Checksums(Wired(), 1000, 4000, 8000);
        output.WriteLine(string.Join(", ", sums.Select(s => $"0x{s:X16}UL")));
        Assert.Equal(WiredSums, sums);
    }

    private static readonly ulong[] NormalSums = [0x15ED6D85BAFD7946UL, 0x2C0995F78A7F8DD5UL];
    private static readonly ulong[] FourWaySums = [0xA07B3CABDCD17C0FUL, 0xA7F7B019709129B7UL, 0x17F44E9695745550UL];
    private static readonly ulong[] WiredSums = [0xD8973FE23BBC0964UL, 0xDF15F9BCE165A255UL, 0x87313A62CADB4BC8UL];
}
