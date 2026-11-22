using System.Numerics;
using System.Text.Json;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>A Wired run as lockstep sees it: its commands and options on the wire, and saved games and recordings of it.</summary>
public class WiredReplayTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static readonly WiredOptions Options = new()
    {
        Difficulty = WiredDifficulty.Hard,
        HeroName = "Anna",
        Arena = "m_desert",
        Chassis = "grunt2",
        Items = [new WiredStartItem("plates", WiredSlot.Module(0))],
        ChipPool = ["overload", "tough", "vein", "cover"],
    };

    private static MatchInfo Run() => new("m_desert", "m_desert", new MatchOptions { Wired = Options }, 4, 1,
    [
        new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false),
        new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true),
    ]);

    [Fact]
    public void The_protocol_went_up_for_the_skirmish_ai() => Assert.Equal(16, NetJson.Protocol);

    [Fact]
    public void Every_wired_command_comes_through_the_wire_as_it_went_in()
    {
        Command[] commands =
        [
            new WiredHangarCommand(Race.Red, true),
            new WiredBuyCommand(Race.Red, "reaper2"),
            new WiredBuyCommand(Race.Red, "BOLTER", WiredSlot.Weapon(Mounting.Left)),
            new WiredBuyCommand(Race.Red, "BOLTER", WiredSlot.Weapon(Mounting.Left), Add: true),
            new WiredSellCommand(Race.Red, 3),
            new WiredEquipCommand(Race.Red, 4, WiredSlot.Weapon(Mounting.Top)),
            new WiredEquipCommand(Race.Red, 4, WiredSlot.Weapon(Mounting.Top), Add: true),
            new WiredUnequipCommand(Race.Red, 5),
            new WiredChassisCommand(Race.Red, 6),
            new WiredUpgradeCommand(Race.Red, 7),
            new WiredRerollCommand(Race.Red),
            new WiredPodRepairCommand(Race.Red, 12),
            new WiredReadyCommand(Race.Red),
            new WiredChipCommand(Race.Red, null, 2),
            new WiredChipCommand(Race.Red, 8, 3),
            new WiredChipChoiceCommand(Race.Red, 1),
            new WiredChipChoiceCommand(Race.Red, 1, 2),
            new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER")),
            new WiredAbilityCommand(Race.Red, 1, new Vector2(120, 80.5f)),
            new WiredDashCommand(Race.Red, new Vector2(0, -1)),
            new WiredSpecialCommand(Race.Red),
            new WiredSpecialCommand(Race.Red, false),
            new WiredTowerCommand(Race.Red, "turret2", new Vector2(310.25f, 92), 3),
            new WiredScrapTowerCommand(Race.Red, 41),
        ];

        // Each sealed kind of command is in the list above.
        var kinds = typeof(WiredCommand).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(WiredCommand))).ToHashSet();
        Assert.Equal(kinds, commands.Select(c => c.GetType()).ToHashSet());

        foreach (var command in commands)
        {
            var json = JsonSerializer.Serialize(command, NetJson.Options);
            Assert.Equal(command, JsonSerializer.Deserialize<Command>(json, NetJson.Options));
        }
    }

    /// <summary>
    /// Something on the wire as it was sent or saved before <paramref name="field"/> was added to it: that field left out.
    /// A command goes as a <see cref="Command"/> (<c>Old&lt;Command&gt;</c>), as a recording holds it.
    /// </summary>
    internal static T? Old<T>(T value, string field)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value, NetJson.Options))!.AsObject();
        foreach (var key in json.Select(p => p.Key).Where(k => k.Equals(field, StringComparison.OrdinalIgnoreCase)).ToList())
            json.Remove(key);
        return JsonSerializer.Deserialize<T>(json.ToJsonString(), NetJson.Options);
    }

    [Fact]
    public void What_was_recorded_before_a_buy_could_name_a_slot_still_loads()
    {
        Assert.Equal(new WiredBuyCommand(Race.Red, "reaper2"), Old<Command>(new WiredBuyCommand(Race.Red, "reaper2"), "slot"));
        Assert.Equal(new WiredChipChoiceCommand(Race.Red, 1), Old<Command>(new WiredChipChoiceCommand(Race.Red, 1), "socket"));
    }

    [Fact]
    public void What_was_recorded_before_batteries_still_loads_as_a_put_of_one()
    {
        var left = WiredSlot.Weapon(Mounting.Left);
        Assert.Equal(new WiredBuyCommand(Race.Red, "BOLTER", left), Old<Command>(new WiredBuyCommand(Race.Red, "BOLTER", left, Add: true), "add"));
        Assert.Equal(new WiredEquipCommand(Race.Red, 4, left), Old<Command>(new WiredEquipCommand(Race.Red, 4, left, Add: true), "add"));
        Assert.Equal(new WiredStartItem("BOLTER", left), Old(new WiredStartItem("BOLTER", left, 3), "count"));
        Assert.Equal(new BattleItem("BOLTER", left, 2), Old(new BattleItem("BOLTER", left, 2, 3), "count"));
    }

    [Fact]
    public void A_drive_with_a_yaw_comes_through_the_wire()
    {
        var drive = new DriveCommand(Race.Red, 12, true, 1, -1, new Vector2(120, 80.5f), 0b11, AimHeight: 3.25f, Yaw: -2.75f);
        var json = JsonSerializer.Serialize<Command>(drive, NetJson.Options);

        Assert.Equal(drive, JsonSerializer.Deserialize<Command>(json, NetJson.Options));
    }

    [Fact]
    public void A_drive_recorded_without_a_yaw_decodes_with_none()
    {
        var drive = new DriveCommand(Race.Red, 12, true, 1, -1, new Vector2(120, 80.5f), 0b11, AimHeight: 3.25f);

        Assert.Equal(drive, Old<Command>(drive with { Yaw = 1.5f }, "yaw"));
        Assert.Null(((DriveCommand)Old<Command>(drive with { Yaw = 1.5f }, "yaw")!).Yaw);
    }

    [Fact]
    public void A_battery_in_the_options_comes_through_the_wire()
    {
        var left = WiredSlot.Weapon(Mounting.Left);
        var match = Run() with { Options = new MatchOptions { Wired = Options with { Chassis = "knight4", Items = [new WiredStartItem("BOLTER", left, 3), new WiredStartItem("plates", WiredSlot.Module(0))] } } };

        var back = JsonSerializer.Deserialize<MatchInfo>(JsonSerializer.Serialize(match, NetJson.Options), NetJson.Options)!;

        Assert.Equal(match.Options.Wired!.Items, back.Options.Wired!.Items);
        Assert.Equal([3, 1], back.Options.Wired.Items.Select(i => i.Count));
        var hero = new BattleHero(Race.Red, "Anna", "knight4", [new BattleItem("BOLTER", left, 2, 3)]);
        Assert.Equal(hero.Items, JsonSerializer.Deserialize<BattleHero>(JsonSerializer.Serialize(hero, NetJson.Options), NetJson.Options)!.Items);
    }

    [Fact]
    public void The_options_of_a_run_come_through_the_wire()
    {
        var json = JsonSerializer.Serialize(Run(), NetJson.Options);

        var match = JsonSerializer.Deserialize<MatchInfo>(json, NetJson.Options)!;

        var wired = match.Options.Wired!;
        Assert.Equal((WiredDifficulty.Hard, "Anna", "m_desert", "grunt2"), (wired.Difficulty, wired.HeroName, wired.Arena, wired.Chassis));
        Assert.Equal(Options.Items, wired.Items);
        Assert.Equal(Options.ChipPool, wired.ChipPool);
    }

    [Fact]
    public void A_saved_run_comes_back_from_its_commands_alone()
    {
        var match = Run() with { Options = new MatchOptions { Wired = Options with { Difficulty = WiredDifficulty.Normal, Chassis = "grunt1", Items = [] } } };
        var live = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), PlayerId.Of(Race.Red), match.InputDelay);
        var state = live.World.Wired!.Heroes[Race.Red];

        void Play(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                live.Pump();
                Assert.True(live.TryStep());
                Assert.Empty(live.World.Rejected);
            }
        }

        live.Queue(new WiredHangarCommand(Race.Red, true));
        Play(5);
        live.Queue(new WiredBuyCommand(Race.Red, "grunt2"));
        live.Queue(new WiredRerollCommand(Race.Red));
        Play(5);
        live.Queue(new WiredChassisCommand(Race.Red, state.Items.First(i => i.Catalogue == "grunt2").Id));
        Play(5);
        live.Queue(new WiredHangarCommand(Race.Red, false));
        live.Queue(new WiredReadyCommand(Race.Red));
        Play(100);
        Assert.Equal((WiredPhase.Wave, 1), (live.World.Wired.Phase, live.World.Wired.Wave));
        Assert.Equal(150 - 110 - WiredBalance.RerollCost, (int)live.World.Races[Race.Red].Bmu);
        Assert.Equal(1, live.World.HeroOf(Race.Red)!.Refits);

        var path = Path.Combine(Path.GetTempPath(), $"wired-{Guid.NewGuid():N}.mreplay");
        new Replay(match, live.Played, live.World.Tick).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);
        Assert.Equal(Options.ChipPool, loaded.Match.Options.Wired!.ChipPool);

        var resumed = loaded.Resume(Replay.BuildWorld(DataLoader.Value, loaded.Match), PlayerId.Of(Race.Red));
        while (resumed.World.Tick < loaded.Ticks && resumed.TryStep())
        {
        }

        Assert.Equal(live.World.Tick, resumed.World.Tick);
        Assert.Equal(live.World.Checksum(), resumed.World.Checksum());
        var back = resumed.World.Wired!.Heroes[Race.Red];
        Assert.Equal(state.Items, back.Items);
        Assert.Equal(state.Offer, back.Offer);
        Assert.Equal(state.Loadout.Worn, back.Loadout.Worn);
        var hero = resumed.World.HeroOf(Race.Red)!;
        Assert.Equal((1, "L_BOLTER"), (hero.Refits, hero.WeaponCombo));

        // A recording shows the same, played from the start.
        var shown = loaded.Playback(Replay.BuildWorld(DataLoader.Value, loaded.Match));
        while (shown.World.Tick < loaded.Ticks && shown.TryStep())
        {
        }
        Assert.Equal(live.World.Checksum(), shown.World.Checksum());
    }

    /// <summary>What a simple player does in a run: it goes for the enemy and the wrecks in a wave, and between waves it buys an escort at the pod and is ready.</summary>
    private static void PlayOn(LockstepSession live, int tick)
    {
        var world = live.World;
        var run = world.Wired!;
        var state = run.Heroes[Race.Red];
        if (tick % 50 != 0 || world.HeroOf(Race.Red) is not { } hero || world.WiredPod(Race.Red) is not { } pod)
            return;
        if (run.BetweenWaves)
        {
            if (hero.DistanceTo(pod) > WiredBalance.PodReach)
                live.Queue(new MoveCommand(Race.Red, [hero.Id], pod.EntranceOutside!.Value));
            else
            {
                live.Queue(new WiredHangarCommand(Race.Red, true));
                live.Queue(new WiredPodRepairCommand(Race.Red, 20));
                live.Queue(new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER")));
                live.Queue(new WiredHangarCommand(Race.Red, false));
                live.Queue(new WiredReadyCommand(Race.Red));
            }
        }
        else if (world.Debris.Count > 0)
            live.Queue(new MoveCommand(Race.Red, [hero.Id], world.Debris[0].Position));
        else if (run.Foes.Count > 0)
            live.Queue(new AttackCommand(Race.Red, [hero.Id], run.Foes[0].Machine.Id));
    }

    [Fact]
    public void A_whole_run_comes_back_from_its_recording()
    {
        var match = Run() with { Options = new MatchOptions { Wired = Options with { Difficulty = WiredDifficulty.Normal, Chassis = "grunt1", Items = [] } } };
        var live = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), PlayerId.Of(Race.Red), match.InputDelay);
        var run = live.World.Wired!;
        var state = run.Heroes[Race.Red];

        for (var tick = 0; tick < 20 * 3600 && run.Phase != WiredPhase.Ended; tick++)
        {
            PlayOn(live, tick);
            live.Pump();
            Assert.True(live.TryStep());
        }

        Assert.Equal(WiredPhase.Ended, run.Phase);
        Assert.True(run.Wave >= 3, $"wave {run.Wave}");
        Assert.True(state.Stats.Deaths >= 1 && state.Stats.Kills >= 1 && state.Stats.WrecksCollected >= 1,
            $"{state.Stats.Deaths} deaths, {state.Stats.Kills} kills, {state.Stats.WrecksCollected} wrecks");

        var path = Path.Combine(Path.GetTempPath(), $"wired-{Guid.NewGuid():N}.mreplay");
        new Replay(match, live.Played, live.World.Tick).Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);

        foreach (var session in new[]
        {
            loaded.Resume(Replay.BuildWorld(DataLoader.Value, loaded.Match), PlayerId.Of(Race.Red)),
            loaded.Playback(Replay.BuildWorld(DataLoader.Value, loaded.Match)),
        })
        {
            while (session.World.Tick < loaded.Ticks && session.TryStep())
            {
            }

            Assert.Equal(live.World.Tick, session.World.Tick);
            Assert.Equal(live.World.Checksum(), session.World.Checksum());
            var back = session.World.Wired!;
            Assert.Equal((run.Phase, run.Wave, run.Ending), (back.Phase, back.Wave, back.Ending));
            Assert.Equal(state.Score, back.Heroes[Race.Red].Score);
        }
    }

    [Fact]
    public void A_run_saved_in_the_middle_of_a_wave_goes_on_as_it_would_have()
    {
        var match = Run() with { Options = new MatchOptions { Wired = Options with { Difficulty = WiredDifficulty.Normal, Chassis = "grunt1", Items = [] } } };
        var live = new LockstepSession(Replay.BuildWorld(DataLoader.Value, match), PlayerId.Of(Race.Red), match.InputDelay);
        var run = live.World.Wired!;
        for (var tick = 0; tick < 20 * 200 && !(run.Phase == WiredPhase.Wave && run.Wave == 2 && run.Foes.Count > 0); tick++)
        {
            PlayOn(live, tick);
            live.Pump();
            Assert.True(live.TryStep());
        }
        Assert.Equal((WiredPhase.Wave, 2), (run.Phase, run.Wave));

        var saved = new Replay(match, live.Played, live.World.Tick);
        var resumed = saved.Resume(Replay.BuildWorld(DataLoader.Value, match), PlayerId.Of(Race.Red));
        while (resumed.World.Tick < saved.Ticks && resumed.TryStep())
        {
        }
        Assert.Equal(live.World.Checksum(), resumed.World.Checksum());

        for (var i = 0; i < 20 * 30; i++)
        {
            live.Pump();
            resumed.Pump();
            Assert.True(live.TryStep());
            Assert.True(resumed.TryStep());
        }
        Assert.Equal(live.World.Checksum(), resumed.World.Checksum());
    }

    [Fact]
    public void A_run_recorded_before_brisk_waves_plays_back_without_them()
    {
        Assert.True(Run().Options.Wired!.BriskWaves);
        var path = Path.Combine(Path.GetTempPath(), $"brisk-{Guid.NewGuid():N}.mreplay");
        try
        {
            new Replay(Run(), [], 0).Save(path);
            Assert.True(Replay.Load(path).Match.Options.Wired!.BriskWaves);

            // The file as one from before reads without the field: its waves were planned without brisk ones.
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.True(json["Match"]!["Options"]!["Wired"]!.AsObject().Remove(nameof(WiredOptions.BriskWaves)));
            File.WriteAllText(path, json.ToJsonString());
            var old = Replay.Load(path);
            Assert.False(old.Match.Options.Wired!.BriskWaves);
            Assert.Equal((true, WiredDifficulty.Hard, "grunt2"), (old.Match.Options.FittedBodies, old.Match.Options.Wired.Difficulty, old.Match.Options.Wired.Chassis));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_recording_from_before_wired_loads_without_a_run()
    {
        var match = new MatchInfo("m_desert", "m_desert", new MatchOptions(), 4, 1, [new MatchPlayer(0, "Anna", Race.Red, Race.Red, Ai: false)]);
        var path = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}.mreplay");
        new Replay(match, [], 0).Save(path);
        var text = File.ReadAllText(path).Replace("\"Wired\":null,", "").Replace(",\"Wired\":null", "");
        Assert.DoesNotContain("\"Wired\"", text);
        File.WriteAllText(path, text);

        var loaded = Replay.Load(path);
        File.Delete(path);

        Assert.Null(loaded.Match.Options.Wired);
        Assert.NotEqual(VictoryCondition.Wired, loaded.Match.Options.Victory);
    }
}
