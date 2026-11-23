using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

/// <summary>
/// Old Iron's charge in the armoury (stage 7, package W, design-waves §6): it telegraphs, runs a straight line, throws a hero
/// it hits aside onto free ground (never into a wall, never pinned), and a crash stands it stunned with its armour off.
/// </summary>
public sealed class StoryChargeTests(ITestOutputHelper output)
{
    /// <summary>Checkpoint 10 played on until the boss is in, nobody shooting it.</summary>
    private static ArmouryPlay BossIn()
    {
        var play = new ArmouryPlay(10) { Fire = 0 };
        Assert.True(play.Until(() => play.Waves.Boss != 0, 60), "the boss never came");
        return play;
    }

    [Fact]
    public void The_charge_never_pins_a_hero()
    {
        var play = BossIn();
        var world = play.World;
        var boss = world.Find<Machine>(play.Waves.Boss)!;
        var hits = 0;
        var phases = new HashSet<StoryChargePhase>();
        // A minute and a half of charges, the trio left to itself; each hero after each tick on ground it can drive on.
        play.Until(() => false, 90, play: false, each: () =>
        {
            phases.Add(play.Waves.Phase);
            boss.HitPoints = boss.MaxHitPoints;
            foreach (var seat in play.Run.Seats)
            {
                if (world.HeroOf(seat) is not { IsDead: false } hero || world.CompanionDown(seat))
                    continue;
                Assert.False(Pinned(world, hero), $"seat {seat.Value} pinned at {hero.Position} (tick {world.Tick})");
            }
            hits = play.Waves.ChargeHits;
        });
        output.WriteLine($"charges {play.Waves.Charges} hits {hits} crashes {play.Waves.Crashes}");
        Assert.True(play.Waves.Charges >= 3, $"{play.Waves.Charges} charges in 90 s");
        Assert.Contains(StoryChargePhase.Telegraph, phases);
        Assert.Contains(StoryChargePhase.Run, phases);
    }

    /// <summary>Pinned: no free ground at it or a metre around it.</summary>
    private static bool Pinned(GameWorld world, Machine hero)
    {
        var mobility = GameWorld.MobilityOf(hero);
        for (var k = -1; k < 8; k++)
        {
            var p = k < 0 ? hero.Position : hero.Position + new Vector2(MathF.Cos(k * MathF.PI / 4), MathF.Sin(k * MathF.PI / 4));
            if (world.Nav.IsFree(p, mobility))
                return false;
        }
        return true;
    }

    [Fact]
    public void A_hero_thrown_aside_lands_on_free_ground_clear_of_the_line()
    {
        var play = BossIn();
        var world = play.World;
        var boss = world.Find<Machine>(play.Waves.Boss)!;
        var locator = play.Hero(0);
        var mobility = GameWorld.MobilityOf(locator);
        var landed = 0;
        // Every free spot of the armoury, the boss charging at it from 15 m in eight ways.
        for (var x = 440f; x <= 480; x += 2)
        {
            for (var y = 226f; y <= 258; y += 2)
            {
                var at = new Vector2(x, y);
                if (!world.Nav.IsFree(at, mobility))
                    continue;
                for (var k = 0; k < 8; k++)
                {
                    var way = new Vector2(MathF.Cos(k * MathF.PI / 4), MathF.Sin(k * MathF.PI / 4));
                    locator.Position = at;
                    boss.Position = at - way * 3;
                    if (world.ThrowAside(locator, boss, way) is not { } p)
                        continue;
                    landed++;
                    Assert.True(world.Nav.IsFree(p, mobility), $"thrown from {at} onto {p}, not free");
                    Assert.True(Vector2.Distance(p, at) <= MathF.Max(GameWorld.ChargeThrow, GameWorld.ThrowFurthest(locator, boss)) + 0.01f);
                    var rel = p - boss.Position;
                    Assert.True(MathF.Abs(rel.X * way.Y - rel.Y * way.X) >= boss.Radius + locator.Radius, $"thrown from {at} onto {p}, still in the line");
                }
            }
        }
        Assert.True(landed > 100, $"only {landed} throws landed (boss radius {boss.Radius}, hero {locator.Radius})");
    }

    [Fact]
    public void A_crash_stands_the_boss_stunned_with_its_armour_off()
    {
        var play = BossIn();
        var world = play.World;
        var boss = world.Find<Machine>(play.Waves.Boss)!;
        var armour = -1;
        var stunned = false;
        var recovered = false;
        play.Until(() => recovered, 120, play: false, each: () =>
        {
            boss.HitPoints = boss.MaxHitPoints;
            if (play.Waves.Phase == StoryChargePhase.Stunned)
            {
                if (!stunned)
                    armour = play.Waves.StunArmour;
                stunned = true;
                Assert.True(boss.Armour == 0, $"armour {boss.Armour} while stunned ({play.Waves.StunArmour} before)");
                Assert.Contains(boss.Id, play.Run.Director.Held);
            }
            else if (stunned)
                recovered = true;
        });
        Assert.True(stunned, $"no crash in 120 s ({play.Waves.Charges} charges)");
        Assert.True(armour > 0 && armour == boss.Armour, $"armour {boss.Armour} after, {armour} before");
        Assert.True(play.Waves.Crashes >= 1);
    }

    [Fact]
    public void The_charge_comes_faster_as_the_boss_is_hurt()
    {
        var play = BossIn();
        var boss = play.World.Find<Machine>(play.Waves.Boss)!;
        Assert.Equal(GameWorld.ChargeEvery, GameWorld.ChargeCadence(boss));
        boss.HitPoints = boss.MaxHitPoints * 2 / 5;
        Assert.Equal(GameWorld.ChargeEveryHalf, GameWorld.ChargeCadence(boss));
        boss.HitPoints = boss.MaxHitPoints / 5;
        Assert.Equal(GameWorld.ChargeEveryQuarter, GameWorld.ChargeCadence(boss));
    }

    [Fact]
    public void The_boss_comes_into_the_armoury_and_lines_up_a_charge_from_inside()
    {
        var play = BossIn();
        var world = play.World;
        var boss = world.Find<Machine>(play.Waves.Boss)!;
        var room = world.Deck!.Zone("R13")!;
        var deepest = boss.Position.Y;
        float? firstRun = null;
        // Nobody shoots it, the trio stands where checkpoint 10 put it: the boss leaves gate N for the room and charges.
        play.Until(() => play.Waves.Charges >= 2, 90, play: false, each: () =>
        {
            boss.HitPoints = boss.MaxHitPoints;
            deepest = MathF.Min(deepest, boss.Position.Y);
            if (play.Waves.Phase == StoryChargePhase.Run && firstRun is null)
            {
                firstRun = world.Time;
                Assert.True(boss.Position.Y < room.Max.Y, $"the first charge starts at {boss.Position}, outside the armoury");
            }
        });
        output.WriteLine($"charges {play.Waves.Charges} hits {play.Waves.ChargeHits} crashes {play.Waves.Crashes} deepest y {deepest:0.0} first run {firstRun:0.0}");
        Assert.True(play.Waves.Charges >= 2, $"{play.Waves.Charges} charges in 90 s");
        Assert.True(deepest < room.Max.Y - 6, $"the boss got no deeper than y {deepest:0.0}");
        // Its charges are no crash at once: each runs on before it hits a hero or a wall.
        Assert.True(play.Waves.ChargeHits + play.Waves.Crashes >= 1);
    }

    [Fact]
    public void The_computers_grunts_get_out_of_its_way()
    {
        var play = BossIn();
        var world = play.World;
        var boss = world.Find<Machine>(play.Waves.Boss)!;
        var grunts = play.Run.Seats.Skip(1).Select(s => world.HeroOf(s)!.Id).ToHashSet();
        var gruntHits = 0;
        var seen = new HashSet<(int Charge, int Id)>();
        // Two minutes of charges, the Locator standing still (it takes them); the Grunts, the computer's, sprint aside.
        play.Until(() => false, 120, play: false, each: () =>
        {
            boss.HitPoints = boss.MaxHitPoints;
            foreach (var id in play.Waves.RunHits.Where(grunts.Contains))
            {
                if (seen.Add((play.Waves.Charges, id)))
                    gruntHits++;
            }
        });
        output.WriteLine($"charges {play.Waves.Charges} hits {play.Waves.ChargeHits} on the Grunts {gruntHits}");
        Assert.True(play.Waves.Charges >= 3, $"{play.Waves.Charges} charges in 120 s");
        Assert.True(gruntHits <= 1, $"the Grunts were run down {gruntHits} times in {play.Waves.Charges} charges");
    }
}
