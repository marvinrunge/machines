using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit;

namespace Machines.Tests.Story;

/// <summary>
/// The computer's Grunts in the armoury (stage 7, package W): armed, they fire at the batch by themselves; unarmed with a
/// Bolter carried, they come to the Locator; they hold still while L-7 works on them; they step out of the charge's line.
/// </summary>
public sealed class CompanionArmouryTests
{
    [Fact]
    public void Armed_grunts_fight_a_batch_by_themselves()
    {
        // Checkpoint 9: both armed; the Locator does not fire.
        var play = new ArmouryPlay(9) { Fire = 0, Works = false };
        Assert.True(play.Until(() => play.Waves.Fighting, 60));
        var seen = new HashSet<int>();
        var killed = 0;
        play.Until(() => killed >= 2, 90, each: () =>
        {
            // The Locator does nothing; it is kept whole so the Grunts fight on.
            play.Hero(0).HitPoints = play.Hero(0).MaxHitPoints;
            seen.UnionWith(play.Waves.Foes.Keys);
            killed = seen.Count(id => play.World.Find<Machine>(id) is not { IsDead: false });
        });
        Assert.True(killed >= 2, $"the Grunts downed {killed} of batch 3 (over {play.Run.Over}, t {play.World.Time:0}, Ike {play.Hero(1).Order} {play.Hero(1).Weapons.Count}, Bob {play.Hero(2).Order})");
    }

    [Fact]
    public void An_unarmed_grunt_comes_to_the_locator_for_a_carried_bolter()
    {
        var play = new ArmouryPlay(8) { Fire = 400, Works = false };
        play.Seal();
        Assert.True(play.Until(() => play.Waves.Cleared == 1, 90));
        play.Waves.Carried = StoryWaves.Bolter;
        var locator = play.Hero(0);
        play.SetDown(locator, new Vector2(455, 237));
        // A Grunt the crate downed in batch 1 stays down (nobody puts it together here).
        var near = play.Until(() => play.Run.Seats.Skip(1).Where(s => !play.World.CompanionDown(s)).All(s => Vector2.Distance(play.World.HeroOf(s)!.Position, locator.Position) <= 7), 15, play: false);
        Assert.True(near, string.Join(", ", play.Run.Seats.Skip(1).Select(s => play.World.HeroOf(s)!.Position)));
    }

    [Fact]
    public void A_grunt_holds_still_while_the_locator_mends_it()
    {
        var play = new ArmouryPlay(9) { Fire = 0, Works = false };
        var ike = play.Hero(1);
        ike.HitPoints = ike.MaxHitPoints / 2;
        play.SetDown(play.Hero(0), ike.Position + new Vector2(2.5f, 0));
        play.World.Issue(new StoryUseCommand(Race.Red, GameWorld.MendTarget + play.Run.Seats[1].Value));
        play.Step();
        var at = ike.Position;
        play.Until(() => false, 2, play: false);
        Assert.True(play.Run.Crew.Holding.ContainsKey(play.Run.Locator.Value));
        Assert.True(Vector2.Distance(at, ike.Position) < 0.2f, $"Ike moved from {at} to {ike.Position}");
    }

    [Fact]
    public void A_grunt_steps_out_of_the_charge_aimed_at_it()
    {
        var play = new ArmouryPlay(10) { Fire = 0, Works = false };
        Assert.True(play.Until(() => play.Waves.Boss != 0, 60));
        var world = play.World;
        var waves = play.Waves;
        var boss = world.Find<Machine>(waves.Boss)!;
        var ike = play.Hero(1);
        var mobility = GameWorld.MobilityOf(ike);
        // Ike and the boss set down 15 m apart on a clear line in the armoury, with room on either hand of Ike to step aside.
        (Vector2 Ike, Vector2 Boss)? spots = null;
        for (var x = 444f; x <= 478 && spots is null; x += 2)
        {
            for (var y = 228f; y <= 256 && spots is null; y += 2)
            {
                var at = new Vector2(x, y);
                if (!world.Nav.IsFree(at, mobility))
                    continue;
                for (var k = 0; k < 16 && spots is null; k++)
                {
                    var dir = new Vector2(MathF.Cos(k * MathF.PI / 8), MathF.Sin(k * MathF.PI / 8));
                    var p = at + 15 * dir;
                    var aside = new Vector2(-dir.Y, dir.X) * (boss.Radius + ike.Radius + 2);
                    if (world.Nav.OnPlanet(p) && world.Nav.IsFree(p, mobility) && world.Nav.LineOfSight(p, at, mobility)
                        && world.Nav.LineOfSight(at, at + aside, mobility) && world.Nav.LineOfSight(at, at - aside, mobility))
                        spots = (at, p);
                }
            }
        }
        Assert.NotNull(spots);
        ike.Position = spots.Value.Ike;
        boss.Position = spots.Value.Boss;
        var way = Vector2.Normalize(ike.Position - boss.Position);
        // The boss revs at Ike, as UpdateCharge begins a charge, and keeps revving (the run is not what is tested here).
        (waves.RunWay, waves.RunFrom, waves.RunTarget) = (way, boss.Position, ike.Id);
        (waves.Phase, waves.PhaseSince) = (StoryChargePhase.Telegraph, world.Time + 10);
        play.Run.Director.Held.Add(boss.Id);
        // The Locator is kept whole: a shut-down Locator calls the Grunts to its cable before all else.
        play.Until(() => false, 3, play: false, each: () =>
        {
            boss.HitPoints = boss.MaxHitPoints;
            play.Hero(0).HitPoints = play.Hero(0).MaxHitPoints;
        });
        Assert.Equal(StoryChargePhase.Telegraph, waves.Phase);
        var rel = ike.Position - boss.Position;
        Assert.True(MathF.Abs(rel.X * way.Y - rel.Y * way.X) > boss.Radius + ike.Radius,
            $"Ike at {ike.Position} (from {spots.Value.Ike}) still in the line of the boss at {boss.Position} revving {way}");
    }
}
