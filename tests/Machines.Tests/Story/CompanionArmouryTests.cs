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
        var boss = world.Find<Machine>(play.Waves.Boss)!;
        var dodged = 0;
        var aimed = 0;
        Machine? target = null;
        var lastPhase = play.Waves.Phase;
        play.Until(() => aimed >= 3, 120, play: false, each: () =>
        {
            boss.HitPoints = boss.MaxHitPoints;
            if (play.Waves.Phase == StoryChargePhase.Telegraph && lastPhase != StoryChargePhase.Telegraph)
                target = play.Run.Seats.Skip(1).Select(s => world.HeroOf(s)).FirstOrDefault(h => h?.Id == play.Waves.RunTarget);
            if (play.Waves.Phase == StoryChargePhase.Run && lastPhase == StoryChargePhase.Telegraph && target is not null)
            {
                aimed++;
                var rel = target.Position - boss.Position;
                if (MathF.Abs(rel.X * play.Waves.RunWay.Y - rel.Y * play.Waves.RunWay.X) > boss.Radius + target.Radius)
                    dodged++;
                target = null;
            }
            lastPhase = play.Waves.Phase;
        });
        Assert.True(aimed == 0 || dodged >= 1, $"{dodged} of {aimed} charges at a Grunt dodged");
    }
}
