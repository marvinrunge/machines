using System.Globalization;
using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

/// <summary>
/// The armoury's five batches (stage 7, package W, design-waves §14): their timeline under a modelled player, two peers alike
/// through all five, checkpoints 9 and 10 built alike twice, the cap of five alive, the press, the workbench's rules (strip,
/// mount, mend) and the salvage after the room unseals.
/// </summary>
public sealed class PrologueWaveTests(ITestOutputHelper output)
{
    /// <summary>A batch that takes longer than this under the modelled player fails (the driver's rule too).</summary>
    private const float LongestBatch = 150;

    [Fact]
    public void The_five_batches_and_the_boss_play_out_in_time()
    {
        var play = new ArmouryPlay(8);
        play.Seal();
        Assert.True(play.Until(() => play.Run.Director.Has("cleared"), 900), $"not cleared: batch {play.Waves.Wave}, cleared {play.Waves.Cleared}");
        foreach (var line in play.Log)
            output.WriteLine(line);
        output.WriteLine($"presses {play.Waves.Presses} fetched {play.Waves.Fetched} revived {play.Revived} stripped {play.Waves.Stripped} mounted {play.Waves.Mounted.Count} mended {play.Waves.Mends}");

        Assert.Equal(StoryWaves.Plan.Count, play.Waves.Cleared);
        Assert.Equal(StoryWaves.Plan.Count, play.Batches.Count);
        foreach (var (wave, (start, end)) in play.Batches)
            Assert.True(end - start <= LongestBatch, $"batch {wave} took {end - start:0} s");
        // Both Grunts armed from stripped Bolters, the boss charged and hit somebody at least once.
        Assert.Equal(2, play.Waves.Mounted.Count);
        Assert.True(play.Waves.Charges >= 1, "the boss never charged");
        Assert.NotNull(play.BossDown);
        // Checkpoints on the way: 9 after batch 2, 10 after batch 4.
        Assert.True(play.Run.Checkpoint >= 10);
    }

    [Fact]
    public void Two_peers_keep_the_same_world_through_all_five_batches()
    {
        var one = new ArmouryPlay(8);
        var two = new ArmouryPlay(8);
        one.Seal();
        two.Seal();
        Assert.Equal(one.World.Checksum(), two.World.Checksum());
        for (var i = 0; i < 900 * GameWorld.TicksPerSecond && !one.Run.Director.Has("cleared"); i++)
        {
            one.Play();
            two.Play();
            one.Step();
            two.Step();
            if (i % GameWorld.TicksPerSecond == 0)
                Assert.True(one.World.Checksum() == two.World.Checksum(), $"apart at tick {one.World.Tick} (batch {one.Waves.Wave})");
        }
        Assert.True(one.Run.Director.Has("cleared"));
        Assert.Equal(one.World.Checksum(), two.World.Checksum());
        Assert.Equal(one.Log, two.Log);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void Checkpoints_nine_and_ten_build_the_same_world_twice(int checkpoint)
    {
        var one = new ArmouryPlay(checkpoint);
        var two = new ArmouryPlay(checkpoint);
        Assert.Equal(one.World.Checksum(), two.World.Checksum());
        // Sealed, both Grunts armed, the next batch's bar running, the wrecks of the batches before lying there.
        Assert.Contains(StoryMarks.ArmourySealed, one.Run.Flags);
        Assert.All(new[] { 1, 2 }, seat => Assert.NotEmpty(one.Hero(seat).Weapons));
        Assert.Equal(checkpoint == 9 ? 3 : 5, one.Waves.Wave);
        Assert.False(one.Waves.Fighting);
        Assert.Equal(checkpoint == 9 ? 2 : 4, one.Waves.Wrecks.Count);
        for (var i = 0; i < 40 * GameWorld.TicksPerSecond; i++)
        {
            one.Play();
            two.Play();
            one.Step();
            two.Step();
        }
        Assert.True(one.Waves.Fighting || one.Waves.Cleared > (checkpoint == 9 ? 2 : 4), "the batch never came");
        Assert.Equal(one.World.Checksum(), two.World.Checksum());
    }

    [Fact]
    public void No_more_than_five_of_a_batch_stand_at_once()
    {
        // Batch 4 has six units: with nobody firing, the sixth waits until one falls.
        var play = new ArmouryPlay(9);
        Assert.True(play.Until(() => play.Waves.Wave == 4 && play.Waves.Fighting, 400));
        play.Fire = 0;
        foreach (var seat in new[] { 1, 2 })
            play.World.HeroOf(play.Run.Seats[seat])!.Weapons.Clear();
        var most = 0;
        var held = false;
        play.Until(() => false, 40, each: () =>
        {
            var alive = play.Waves.Alive(play.World);
            most = Math.Max(most, alive);
            held |= alive == StoryWaves.MostAlive && play.Waves.Due.Count > 0;
        });
        Assert.True(most <= StoryWaves.MostAlive, $"{most} alive at once");
        Assert.True(held, "the sixth unit never had to wait");
    }

    [Fact]
    public void A_foe_that_hangs_back_is_pressed_onto_the_trio()
    {
        var play = new ArmouryPlay(8) { Fire = 0, Works = false };
        play.Seal();
        Assert.True(play.Until(() => play.Waves.Fighting, 30));
        play.Until(() => play.Waves.Presses > 0, 3 * StoryWaves.PressEvery);
        Assert.True(play.Waves.Presses > 0, "nobody was pressed");
        Assert.Contains(play.Waves.Foes.Keys, id => play.World.Find<Machine>(id) is { Order: AttackOrder attack } && play.World.Find<Machine>(attack.Target) is { } aim
            && play.Run.Seats.Any(s => play.World.HeroOf(s)?.Id == aim.Id));
    }

    [Fact]
    public void The_workbench_strips_one_bolter_at_a_time_mounts_it_and_mends()
    {
        var play = new ArmouryPlay(8) { Fire = 400, Works = false };
        play.Seal();
        Assert.True(play.Until(() => play.Waves.Cleared == 1, 90));
        var world = play.World;
        var run = play.Run;
        var wrecks = play.Waves.Wrecks.Where(w => w.Value.Wave == 1).Select(w => w.Key).ToList();
        Assert.True(wrecks.Count >= 2, $"{wrecks.Count} wrecks of batch 1");
        Assert.All(wrecks, id => Assert.Equal(StoryWaves.Bolter, GameWorld.StripYield(run, play.Waves.Wrecks[id])));

        // Strip: 3 s held at the wreck; then a Bolter is carried and no second one comes off while it is.
        var locator = play.Hero(0);
        play.SetDown(locator, play.Waves.Wrecks[wrecks[0]].Position + new Vector2(2, 0));
        var strip = StoryMarks.WreckTarget + wrecks[0].ToString(CultureInfo.InvariantCulture);
        world.Issue(new StoryUseCommand(Race.Red, strip));
        play.Step();
        play.Until(() => false, StoryWaves.StripSeconds - 0.5f, play: false);
        Assert.Null(play.Waves.Carried);
        play.Until(() => play.Waves.Carried is not null, 1, play: false);
        Assert.Equal(StoryWaves.Bolter, play.Waves.Carried);
        Assert.True(play.Waves.Wrecks[wrecks[0]].Stripped);
        Assert.Null(GameWorld.StripYield(run, play.Waves.Wrecks[wrecks[1]]));

        // Mount on Ike: 2 s, then Ike carries it and the Locator nothing.
        var ike = play.Hero(1);
        Assert.Empty(ike.Weapons);
        play.Until(() => !run.Crew.Holding.ContainsKey(run.Locator.Value), 2, play: false);
        play.SetDown(locator, ike.Position + new Vector2(2.5f, 0));
        world.Issue(new StoryUseCommand(Race.Red, GameWorld.MountTarget + run.Seats[1].Value));
        play.Until(() => ike.Weapons.Count > 0, StoryWaves.MountSeconds + 1, play: false);
        Assert.NotEmpty(ike.Weapons);
        Assert.Null(play.Waves.Carried);
        Assert.Contains(run.Seats[1].Value, play.Waves.Mounted);
        Assert.NotNull(GameWorld.StripYield(run, play.Waves.Wrecks[wrecks[1]]));

        // Mend: a hurt Grunt gains a tenth of its hit points a second while held, and the Grunts never mend by themselves.
        play.Until(() => !run.Crew.Holding.ContainsKey(run.Locator.Value), 2, play: false);
        ike.HitPoints = ike.MaxHitPoints / 2;
        var hurt = ike.HitPoints;
        play.Until(() => false, 2, play: false);
        Assert.Equal(hurt, ike.HitPoints);
        play.SetDown(locator, ike.Position + new Vector2(2.5f, 0));
        world.Issue(new StoryUseCommand(Race.Red, GameWorld.MendTarget + run.Seats[1].Value));
        play.Until(() => false, 2, play: false);
        Assert.True(run.Crew.Holding.ContainsKey(run.Locator.Value), "the mend stopped");
        Assert.True(ike.HitPoints - hurt >= ike.MaxHitPoints * StoryWaves.MendRate && ike.HitPoints - hurt <= ike.MaxHitPoints * StoryWaves.MendRate * 2.5f,
            $"mended {hurt} to {ike.HitPoints} of {ike.MaxHitPoints} in 2 s");
        Assert.InRange(ike.HitPoints - hurt, ike.MaxHitPoints * StoryWaves.MendRate, ike.MaxHitPoints * StoryWaves.MendRate * 2.5f);
    }

    [Fact]
    public void The_marked_parts_are_salvaged_after_the_room_unseals()
    {
        var play = new ArmouryPlay(10) { Fire = 2000 };
        Assert.True(play.Until(() => play.Run.Director.Has("unseal"), 200), $"batch {play.Waves.Wave} cleared {play.Waves.Cleared}");
        var marked = play.Waves.Wrecks.Values.Where(w => w.Marked is not null).Select(w => w.Marked!).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { StoryWaves.Grunt2Drive, StoryWaves.ElectricCharge }.OrderBy(m => m, StringComparer.Ordinal), marked);
        Assert.True(play.Until(() => play.Run.Director.Has("stash"), 60));
        Assert.Equal(new SortedSet<string>(marked, StringComparer.Ordinal), play.Run.Salvage);
        // The room is open again, gate N burst for good.
        Assert.DoesNotContain(StoryMarks.ArmourySealed, play.Run.Flags);
        Assert.Contains('N', play.Run.Director.Blown);
    }

    [Fact]
    public void The_lockdown_shuts_the_three_doors_and_the_reserve_comes_on()
    {
        var play = new ArmouryPlay(8);
        play.Seal();
        foreach (var letter in "RTU")
        {
            Assert.Contains(letter, play.Run.Director.Locked);
        }
        play.Until(() => false, 3, play: false);
        Assert.All("RTU", letter => Assert.True(play.World.DoorClosed(letter), $"door {letter}"));
        Assert.Contains(StoryMarks.ArmourySealed, play.Run.Flags);
        Assert.Equal(1, play.Waves.Wave);
    }
}
