using System.Text.Json;
using Machines.Game;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// The room pages' rules (<see cref="RoomOptions"/>): a room's settings changed on the settings page keep what the page does
/// not show, a room switches between normal and battle cleanly, and a hero is refused in the relay's words.
/// </summary>
public class BattleRoomOptionsTests
{
    private static MatchOptions Chosen => new()
    {
        StartingBmu = StartingBmu.High,
        Resources = ResourceLevel.Medium,
        TechLevel = TechLevel.Low,
        Victory = VictoryCondition.Pod,
        TimeLimit = 1800,
        AiLevel = AiLevel.Hard,
        FogOfWar = false,
        BroadcastAlliances = true,
        DisableFirstPerson = true,
    };

    /// <summary>The room's options as every player gets them: through the relay's JSON.</summary>
    private static MatchOptions Wired(MatchOptions options) =>
        JsonSerializer.Deserialize<MatchOptions>(JsonSerializer.Serialize(options, NetJson.Options), NetJson.Options)!;

    [Fact]
    public void A_normal_room_changed_on_the_page_keeps_its_wired_run_and_the_rules_a_recording_keeps()
    {
        var kept = Wired(new MatchOptions { Wired = new WiredOptions { Arena = "m_green" }, OriginalMotion = false, DestructibleArtefacts = false });

        var changed = RoomOptions.Normal(kept, Chosen);

        Assert.Equal("m_green", changed.Wired?.Arena);
        Assert.False(changed.OriginalMotion);
        Assert.False(changed.DestructibleArtefacts);
        Assert.Null(changed.Battle);
        Assert.Equal((StartingBmu.High, ResourceLevel.Medium, TechLevel.Low, VictoryCondition.Pod, AiLevel.Hard),
            (changed.StartingBmu, changed.Resources, changed.TechLevel, changed.Victory, changed.AiLevel));
        Assert.Equal((false, true, true), (changed.FogOfWar, changed.BroadcastAlliances, changed.DisableFirstPerson));
    }

    [Fact]
    public void A_new_normal_room_is_what_the_page_chose_as_before()
    {
        var made = RoomOptions.Normal(new MatchOptions(), Chosen);

        Assert.Equal(JsonSerializer.Serialize(Chosen, NetJson.Options), JsonSerializer.Serialize(made, NetJson.Options));
    }

    [Fact]
    public void A_battle_room_changed_on_the_page_keeps_its_wait_and_its_time()
    {
        var map = BattleCatalogue.Map("m_desert")!;
        var kept = Wired(new MatchOptions
        {
            Victory = VictoryCondition.Battle,
            FogOfWar = false,
            OriginalMotion = false,
            Battle = new BattleOptions { Tier = 3, Lives = 2, RespawnWait = 4, TimeLimit = 300, Win = BattleWin.Capture, TeamA = map.TeamA, TeamB = map.TeamB },
        });

        var changed = Wired(RoomOptions.Battle(kept, 5, 3, BattleWin.Wipe));

        var battle = changed.Battle!;
        Assert.Equal((5, 3, BattleWin.Wipe), (battle.Tier, battle.Lives, battle.Win));
        Assert.Equal((4f, 300), (battle.RespawnWait, battle.TimeLimit));
        Assert.Equal(map.TeamA, battle.TeamA);
        Assert.Equal(map.TeamB, battle.TeamB);
        Assert.Equal(VictoryCondition.Battle, changed.Victory);
        Assert.False(changed.OriginalMotion);
    }

    [Fact]
    public void A_room_turned_into_a_battle_has_nothing_of_wired_and_its_settings_within_what_the_sim_plays()
    {
        var kept = new MatchOptions { Wired = new WiredOptions { Arena = "m_desert" }, FogOfWar = true, DisableFirstPerson = true, BroadcastAlliances = true };

        var low = RoomOptions.Battle(kept, 0, 0, (BattleWin)42);
        var high = RoomOptions.Battle(kept, 9, 7, BattleWin.Both);

        Assert.Null(low.Wired);
        Assert.Equal((VictoryCondition.Battle, false, false, false), (low.Victory, low.FogOfWar, low.DisableFirstPerson, low.BroadcastAlliances));
        Assert.Equal((1, 1, BattleWin.Both), (low.Battle!.Tier, low.Battle.Lives, low.Battle.Win));
        Assert.Equal((BattleCatalogue.MostTier, 5), (high.Battle!.Tier, high.Battle.Lives));
    }

    [Fact]
    public void A_battle_room_turned_normal_is_no_battle_any_more()
    {
        var battle = RoomOptions.Battle(new MatchOptions(), 2, 1, BattleWin.Both);

        var normal = RoomOptions.Normal(battle, Chosen);

        Assert.Null(normal.Battle);
        Assert.Equal(VictoryCondition.Pod, normal.Victory);
    }

    [Theory]
    [InlineData(1, 1, null)]
    [InlineData(1, 2, null)]
    [InlineData(2, 2, null)]
    [InlineData(1, 3, "that hero is not in the room's bracket")]
    [InlineData(4, 3, "that hero is not in the room's bracket")]
    [InlineData(8, 8, null)]
    public void A_hero_is_taken_in_the_bracket_and_refused_outside_it_in_the_relays_words(int heroTier, int roomTier, string? why)
    {
        var hero = BattleBots.Bot(heroTier, Race.Red, 0) with { Name = "Rivet", Bot = false };

        Assert.Equal(why, RoomOptions.WhyNotHere(hero, roomTier));
    }

    [Fact]
    public void A_hero_that_does_not_fit_together_is_refused_in_the_relays_words()
    {
        var hero = BattleBots.Bot(1, Race.Red, 0);
        var stacked = hero with { Items = [.. hero.Items, new BattleItem("BOLTER", null, 3)] };
        var nothing = hero with { Items = [new BattleItem("NO_SUCH_THING", null)] };

        Assert.Equal("that hero does not fit together", RoomOptions.WhyNotHere(stacked, 1));
        Assert.Equal("that hero does not fit together", RoomOptions.WhyNotHere(nothing, 1));
    }
}
