using System.Globalization;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Sim;

/// <summary>
/// Golden sums of commanded matches as they are played online, taken on the code before players were told apart from
/// races (P1 design §7.1, slice S0): a skirmish of two commanders, a four-way match whose leaver comes back, a commanded
/// Wired run, three battles, and three that the review of S0 added: a battle whose leavers come back (one in the tick it
/// left), a skirmish of the other orders whose player leaves and comes back in one tick, and a Wired run of the rest of the
/// hangar and the hero's powers (<see cref="GoldenCase"/>). Every match is shaped as the relay starts it (seated humans in
/// seat order, then the computer's rows in seat order) and built through <see cref="Replay.BuildWorld"/>, so the sums pin
/// the actor order of a real online match; one lockstep session plays it, every human's turn coming through the wire as
/// the relay brings it, the commands made by the players' scripts (<see cref="GoldenScript"/>). Each match's recording is
/// saved, loaded and played back to the same sums. G1 to G9 were taken before machines had fitted bodies and keep the
/// original's circles (<see cref="MatchOptions.FittedBodies"/> false, as a recording from then plays back); G10 to G12 play
/// G1, G3 and G4 in fitted bodies, as every match since does.
/// </summary>
/// <remarks>
/// The constants were copied from the printed <c>0x…UL</c> and held three runs in a row. Later changes may adapt the calls
/// to a changed API (a turn by player, the session's constructor, a leaver by player) but keep seeds, rows, scripts, ticks
/// and constants: a sum that moves means the simulation changed for matches with one player per race, which is a bug to
/// find, not a constant to copy.
/// </remarks>
public class GoldenSumTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    /// <summary>Plays a case to its ticks, prints what it saw and checks the sums; then its recording, played back, passes the same sums.</summary>
    private GoldenMatch Play(GoldenCase golden, ulong[] expected)
    {
        var match = new GoldenMatch(golden, Data);

        var sums = match.PlayTo(golden.Ticks);

        output.WriteLine(string.Join(", ", sums.Select(s => $"0x{s:X16}UL")));
        output.WriteLine($"{match.Session.Played.Count} turns with commands: " + string.Join(", ", match.Session.Played.SelectMany(t => t.Commands)
            .GroupBy(c => c.GetType().Name).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} ×{g.Count()}")));
        output.WriteLine("refused: " + string.Join("; ", match.Refused.Select(r => $"{r.Key} ×{r.Value}")));
        output.WriteLine(string.Join("; ", match.World.Races.Keys.Order().Select(race =>
            $"{race} {match.World.Machines.Count(m => m.Race == race)} machines, {match.World.Constructions.Count(c => c.Race == race)} buildings")));
        if (match.World.Battle is { } battle)
            output.WriteLine(Describe(battle.Result));
        Assert.Equal(expected, sums);

        var path = Path.Combine(Path.GetTempPath(), $"golden-{Guid.NewGuid():N}{Replay.Extension}");
        Replay loaded;
        try
        {
            match.Recording.Save(path);
            loaded = Replay.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
        var playback = loaded.Playback(Replay.BuildWorld(Data, loaded.Match));
        var again = new List<ulong>();
        foreach (var tick in golden.Ticks)
        {
            while (playback.World.Tick < tick)
                Assert.True(playback.TryStep(), $"the playback of {golden.Name} stuck at tick {playback.World.Tick}");
            again.Add(playback.World.Checksum());
        }
        Assert.Equal(expected, again);
        Assert.Equal(match.World.Battle?.Result, playback.World.Battle?.Result);
        return match;
    }

    /// <summary>A battle's result as it would be written in this file.</summary>
    private static string Describe(BattleResult? result) => result is { } r
        ? $"new BattleResult({r.Winner?.ToString(CultureInfo.InvariantCulture) ?? "null"}, BattleEnd.{r.Reason}, {r.EndedAt.ToString("R", CultureInfo.InvariantCulture)}f)"
        : "no result";

    [Fact]
    public void A_skirmish_of_two_commanders_keeps_its_sums() => Play(GoldenCase.Skirmish, SkirmishSums);

    [Fact]
    public void A_four_way_match_whose_leaver_comes_back_keeps_its_sums()
    {
        var match = Play(GoldenCase.FourWay, FourWaySums);

        Assert.Equal(2, match.Session.Presence.Count);
    }

    [Fact]
    public void A_commanded_wired_run_keeps_its_sums()
    {
        var match = Play(GoldenCase.Wired, WiredSums);

        Assert.Equal((WiredPhase.Wave, 4), (match.World.Wired!.Phase, match.World.Wired.Wave));
    }

    /// <summary>
    /// G3 without the guns' arcs (<see cref="MatchOptions.HeroGunArc"/>), as a recording from before them plays back, keeps
    /// the sums G3 had before them (as G9 does throughout).
    /// </summary>
    [Fact]
    public void A_commanded_wired_run_without_the_guns_arcs_keeps_its_sums_from_before_them()
    {
        var golden = GoldenCase.Wired;
        Play(golden with { Match = golden.Match with { Options = golden.Match.Options with { HeroGunArc = false } } }, WiredSumsBeforeTheArcs);
    }

    [Fact]
    public void A_commanded_battle_keeps_its_sums_and_result()
    {
        var match = Play(GoldenCase.Battle, BattleSums);

        Assert.Equal(BattleEnded, match.World.Battle!.Result);
    }

    [Fact]
    public void A_battle_of_bots_keeps_its_sums_and_result()
    {
        var match = Play(GoldenCase.BotBattle, BotBattleSums);

        Assert.Equal(BotBattleEnded, match.World.Battle!.Result);
    }

    [Fact]
    public void A_battle_whose_player_leaves_keeps_its_sums_and_result()
    {
        var match = Play(GoldenCase.LeaverBattle, LeaverBattleSums);

        // Asked by race through the set's own Contains, which keeps a race form when its keys become players (P1 S3), so
        // this line compiles before and after and no slice has to touch it.
        Assert.True(match.World.Battle!.Bots.Contains(Race.Green), "a bot drives the leaver's hero");
        Assert.Equal(LeaverBattleEnded, match.World.Battle.Result);
    }

    [Fact]
    public void A_battle_whose_leavers_come_back_keeps_its_sums_and_result()
    {
        var match = Play(GoldenCase.ReturningBattle, ReturningBattleSums);

        Assert.Equal(4, match.Session.Presence.Count);
        Assert.False(match.World.Battle!.Bots.Contains(Race.Green) || match.World.Battle.Bots.Contains(Race.Red), "both players drive their heroes again");
        Assert.Equal(ReturningBattleEnded, match.World.Battle.Result);
    }

    [Fact]
    public void A_skirmish_of_many_orders_keeps_its_sums()
    {
        var match = Play(GoldenCase.Orders, OrdersSums);

        Assert.Equal(3, match.Session.Presence.Count);
        Assert.Equal((Controller.Player, Controller.Ai), (match.World.Races[Race.Red].Controller, match.World.Races[Race.Blue].Controller));
    }

    [Fact]
    public void A_wired_run_of_many_orders_keeps_its_sums()
    {
        var match = Play(GoldenCase.WiredOrders, WiredOrdersSums);

        Assert.Equal((WiredPhase.Wave, 5), (match.World.Wired!.Phase, match.World.Wired.Wave));
    }

    [Fact]
    public void A_skirmish_in_fitted_bodies_keeps_its_sums() => Play(GoldenCase.FittedSkirmish, FittedSkirmishSums);

    [Fact]
    public void A_wired_run_in_fitted_bodies_keeps_its_sums() => Play(GoldenCase.FittedWired, FittedWiredSums);

    [Fact]
    public void A_battle_in_fitted_bodies_keeps_its_sums_and_result()
    {
        var match = Play(GoldenCase.FittedBattle, FittedBattleSums);

        Assert.Equal(FittedBattleEnded, match.World.Battle!.Result);
    }

    // Taken at e915579 (branch online, P0 committed, nothing of P1 built).
    private static readonly ulong[] SkirmishSums = [0x4267B178A96649D3UL, 0x9E440FFF9F1190A5UL];
    private static readonly ulong[] FourWaySums = [0xCE853B3BB625E2ADUL, 0xE9C8E3683C9106F2UL, 0x640F9E9E5D6727C5UL];
    // G3 taken again for the guns' arcs (protocol 16), its Grunt 1 hero having no upper body; the sums before them, which G3
    // without the arcs keeps, as a recording from before them plays.
    private static readonly ulong[] WiredSums = [0x881BB78F4E3B5B32UL, 0x962F60FCCF9D1B19UL, 0x6845D6853EB81E97UL];
    private static readonly ulong[] WiredSumsBeforeTheArcs = [0x881BB78F4E3B5B32UL, 0xEF7D6E9EEBF36063UL, 0xC4224CC350D3CA13UL];
    private static readonly ulong[] BattleSums = [0xE142FCCC97986C2BUL, 0x8D3C8E387ECDB623UL, 0x66C5B8C42BC1BC49UL, 0xE10B269A928D93C7UL];
    private static readonly BattleResult BattleEnded = new(1, BattleEnd.Wipe, 569.65f);
    private static readonly ulong[] BotBattleSums = [0xF9ED19CA3D2D2464UL, 0x9236EE3B615D8E8FUL];
    private static readonly BattleResult BotBattleEnded = new(1, BattleEnd.Capture, 219.35f);
    private static readonly ulong[] LeaverBattleSums = [0x2016ADC4F82F375CUL, 0xD0E53E46D2A85EDBUL, 0x730AC7688EFC8203UL];
    private static readonly BattleResult LeaverBattleEnded = new(0, BattleEnd.Wipe, 518f);
    // Added by the review of S0, taken at a3a7c7c (sim and net still as at e915579).
    private static readonly ulong[] ReturningBattleSums =[0xD51C0428B2E51BD7UL, 0xCA5F0BC1705D7753UL, 0x91527D299E29F339UL];
    private static readonly BattleResult ReturningBattleEnded = new(0, BattleEnd.Wipe, 538.7f);
    private static readonly ulong[] OrdersSums = [0x8A125333E713E7BFUL, 0x6D33FD9711139C1FUL, 0x2FD0D33A99C8B818UL];
    private static readonly ulong[] WiredOrdersSums = [0x062822930B5E3B47UL, 0x7D60C49AF3349580UL, 0x021D50BFEDE74C78UL];
    // G1 to G9 keep the original's circles (MatchOptions.FittedBodies false, as a recording from before plays back); G10 to
    // G12 are G1, G3 and G4 in fitted bodies, as every match since plays, taken on fit/bodies and held three runs. G10 parts
    // from G1 after its first sum, G12 from G4 after its second, as their armies meet; G11 from the first.
    private static readonly ulong[] FittedSkirmishSums = [0x4267B178A96649D3UL, 0x3EF8C95339E9833CUL];
    private static readonly ulong[] FittedWiredSums = [0x1C76934880D9A3AEUL, 0x256447DCE64C3DB2UL, 0x0CA3C903AB0292F4UL];
    private static readonly ulong[] FittedBattleSums = [0xE142FCCC97986C2BUL, 0x8D3C8E387ECDB623UL, 0x321387CCEE7FB575UL, 0xB08337D69BEC8611UL];
    private static readonly BattleResult FittedBattleEnded = new(1, BattleEnd.Wipe, 533.10004f);
}
