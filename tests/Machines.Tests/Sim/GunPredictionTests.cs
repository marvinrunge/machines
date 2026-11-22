using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The client's foresight of a sighted driver's gun (GunPrediction): it replays the commands on their way exactly as the
/// world carries them out, and its trigger gate lets a held trigger wait for the gun while a click fires at once.
/// </summary>
public class GunPredictionTests
{
    private static float Wrap(float angle) => MathF.IEEERemainder(angle, 2 * MathF.PI);

    /// <summary>The hull turn a command's turn makes in the world, as the client works it out.</summary>
    private static float HullTurn(GameWorld world, Machine hero, float turn, bool slow) =>
        GameWorld.HullTurnOf(Math.Clamp(turn, -1, 1), world.DrivenTurnRate(hero, slow, hasYaw: true));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void The_prediction_matches_the_sim_tick_for_tick(int delay)
    {
        var tier = BattleCatalogue.Tier("grunt2");
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Turret", "grunt2", BattleBots.Loadout("grunt2", tier)),
             new BattleHero(Race.Green, "Hull", "grunt1", BattleBots.Loadout("grunt1", tier))]);
        var world = Battles.New(options);
        var heroes = new[] { Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green) };
        Assert.True(heroes[0].CanTurnUpperBody);
        Assert.False(heroes[1].CanTurnUpperBody);
        var random = new Random(11);
        var due = heroes.ToDictionary(h => h.Id, _ => new List<GunPrediction.Due>());
        var yaws = heroes.ToDictionary(h => h.Id, h => h.Facing);
        var predicted = new Dictionary<(int, long), GunPrediction.Gun>();
        var sent = new List<(long Tick, DriveCommand Command)>();
        var (compared, on, off, opening) = (0, 0, 0, 0);

        for (var step = 0; step < 300; step++)
        {
            var now = world.Tick;
            var tick = now + delay;
            foreach (var hero in heroes)
            {
                var yaw = yaws[hero.Id];
                yaw = step % 25 == 0 ? Wrap(yaw + (random.NextSingle() * 2 - 1) * 2.6f) : random.Next(3) == 0 ? Wrap(yaw + (random.NextSingle() - 0.5f) * 0.06f) : yaw;
                yaws[hero.Id] = yaw;
                var slow = random.Next(12) == 0;
                var entries = due[hero.Id];
                GunPrediction.Drop(entries, now);
                float turn;
                if (hero.CanTurnUpperBody)
                    turn = new[] { -1f, -0.5f, 0, 0, 0.3f, 1 }[random.Next(6)];
                else
                {
                    // The client's follow: the hull turns to the yaw, counting the turns on their way.
                    var most = world.DrivenTurnRate(hero, slow, hasYaw: true) * MathF.PI / 180 * GameWorld.Dt;
                    turn = Math.Clamp(Wrap(yaw - hero.Facing - GunPrediction.Turning(entries, now, tick)) / most, -1, 1);
                }
                GunPrediction.Add(entries, new GunPrediction.Due(tick, HullTurn(world, hero, turn, slow), yaw));
                (float, float)? carried = world.DriveInput.TryGetValue(hero.Id, out var input)
                    ? (GameWorld.HullTurnOf(input.Turn, world.DrivenTurnRate(hero, input.Slow, world.HasYaw(hero.Id))), world.PlayerYaws.TryGetValue(hero.Id, out var held) ? held : float.NaN)
                    : null;
                predicted[(hero.Id, tick)] = GunPrediction.Predict(hero.Facing, world.HeadOf(hero.Id), GameWorld.HeadStep(hero), world.Busy(hero),
                    hero.Inside is not null, now, entries, t => world.Battle!.InOpening(t * GameWorld.Dt), null, carried, world.GunArcOf(hero));
                sent.Add((tick, new DriveCommand(hero.Race, hero.Id, true, 0, turn, null, 0, Slow: slow, Yaw: yaw)));
            }
            foreach (var (_, command) in sent.Where(s => s.Tick == now))
                world.Issue(command);
            sent.RemoveAll(s => s.Tick == now);
            if (world.Battle!.InOpening(world.Time))
                opening++;
            world.Step();

            foreach (var hero in heroes)
            {
                if (!predicted.Remove((hero.Id, now), out var gun))
                    continue;
                Assert.True(MathF.Abs(gun.Heading - world.GunHeading(hero)) <= 1e-5f, $"tick {now}, {hero.Id}: predicted {gun.Heading}, the world's {world.GunHeading(hero)}");
                Assert.Equal(world.OnSight(hero.Id), gun.On);
                compared++;
                (on, off) = gun.On ? (on + 1, off) : (on, off + 1);
            }
        }

        Assert.Equal(2 * (300 - delay), compared);
        Assert.True(opening > 100 && opening < 300, $"{opening} ticks in the opening");
        Assert.True(on > 50 && off > 50, $"on {on}, off {off}");
    }

    [Fact]
    public void Entries_due_at_the_same_tick_collapse_to_the_last()
    {
        var due = new List<GunPrediction.Due>();
        GunPrediction.Add(due, new(10, 0.1f, 1));
        GunPrediction.Add(due, new(11, 0.2f, 2));
        GunPrediction.Add(due, new(11, 0.3f, 3));
        GunPrediction.Add(due, new(12, 0.4f, 4));

        Assert.Equal([new(10, 0.1f, 1), new(11, 0.3f, 3), new(12, 0.4f, 4)], due);
        Assert.Equal(0.3f + 0.4f, GunPrediction.Turning(due, 11, -1), 6);
        Assert.Equal(0.1f + 0.4f, GunPrediction.Turning(due, 10, 11), 6);
        GunPrediction.Drop(due, 12);
        Assert.Equal([new(12, 0.4f, 4)], due);

        // The command not yet sent replaces one due at its tick as well.
        var gun = GunPrediction.Predict(0, 0, 0, false, false, 12, due, _ => false, new GunPrediction.Due(12, 0.25f, 0.25f));
        Assert.Equal(0.25f, gun.Facing);
        Assert.True(gun.On);
    }

    [Fact]
    public void Online_a_late_command_moves_to_the_bundle_it_landed_on_and_one_the_relay_dropped_ages_out()
    {
        // Sent stamped 10, 11, 12 and 14 (turns 0.1, 0.2, 0.3 and 0.5); the world is at 12 when the bundle of 13 brings the late
        // 10 and 11 (Asked 11).
        var due = new List<GunPrediction.Due>();
        foreach (var tick in new long[] { 10, 11, 12, 14 })
            GunPrediction.Add(due, new(tick, 0.1f * (tick - 9), tick));
        GunPrediction.Drop(due, 12, grace: 20);
        Assert.Equal(4, due.Count);
        Assert.Equal(0.3f + 0.5f, GunPrediction.Turning(due, 12, -1), 6);

        GunPrediction.Land(due, 10, 13);
        GunPrediction.Land(due, 11, 13);
        // Of the two on 13 the later sent runs, as the world carries out the relay's commands in their order; it turns the hull
        // still to come, where before the world passed it and the prediction lost it.
        Assert.Equal([12L, 13L, 14L], due.Select(e => e.Tick));
        Assert.Equal([12L, 11L, 14L], due.Select(e => e.Stamp));
        Assert.Equal([false, true, false], due.Select(e => e.Landed));
        Assert.Equal(0.3f + 0.2f + 0.5f, GunPrediction.Turning(due, 12, -1), 6);
        // An earlier one never lands over one sent later on the same tick; a stamp nobody waits for changes nothing.
        GunPrediction.Land(due, 12, 14);
        Assert.Equal([13L, 14L], due.Select(e => e.Tick));
        Assert.Equal(0.5f, due[^1].Turn, 6);
        GunPrediction.Land(due, 99, 100);
        GunPrediction.Land(due, 14, 14);
        Assert.Equal([true, true], due.Select(e => e.Landed));

        // Landed ones go once the world passed them; one that never lands (the relay dropped it) stays past its tick for the grace.
        GunPrediction.Add(due, new(16, 0.7f, 16));
        GunPrediction.Drop(due, 17, grace: 20);
        Assert.Equal([16L], due.Select(e => e.Stamp));
        Assert.Equal(0f, GunPrediction.Turning(due, 17, -1));
        GunPrediction.Drop(due, 37, grace: 20);
        Assert.Empty(due);

        // Offline (no grace) everything leaves as the world passes it, as before.
        GunPrediction.Add(due, new(5, 0.1f, 0));
        GunPrediction.Drop(due, 6);
        Assert.Empty(due);
    }

    [Fact]
    public void A_held_trigger_waits_for_the_gun_and_a_click_does_not()
    {
        // Held a second, the gun off and the sight on nothing: no bit.
        Assert.False(GunPrediction.Gate(true, "BOLTER", gunOn: false, enemy: false, sincePress: 1, hullLag: 2));
        // The gun on, the sight on an enemy, a fresh click or the option off: the bit.
        Assert.True(GunPrediction.Gate(true, "BOLTER", gunOn: true, enemy: false, sincePress: 1, hullLag: 2));
        Assert.True(GunPrediction.Gate(true, "BOLTER", gunOn: false, enemy: true, sincePress: 1, hullLag: 2));
        Assert.True(GunPrediction.Gate(true, "PLASMA_CANNON2", gunOn: false, enemy: false, sincePress: 0.1, hullLag: 2));
        Assert.False(GunPrediction.Gate(true, "PLASMA_CANNON2", gunOn: false, enemy: false, sincePress: GunPrediction.FreshPress, hullLag: 2));
        Assert.True(GunPrediction.Gate(false, "BOLTER", gunOn: false, enemy: false, sincePress: 1, hullLag: 2));
        // A punch only with the hull within PunchPlay of the view, fresh click or not.
        Assert.False(GunPrediction.Gate(true, "GORILLA_PUNCH", gunOn: true, enemy: true, sincePress: 0, hullLag: WiredBalance.PunchPlay + 0.01f));
        Assert.True(GunPrediction.Gate(true, "GORILLA_PUNCH", gunOn: false, enemy: false, sincePress: 9, hullLag: -WiredBalance.PunchPlay));
        // A bee bomb always.
        Assert.True(GunPrediction.Gate(true, "BEE_BOMB", gunOn: false, enemy: false, sincePress: 9, hullLag: 3));
    }
}
