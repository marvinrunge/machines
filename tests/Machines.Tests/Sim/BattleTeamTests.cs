using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Ai;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Sim;

/// <summary>
/// Battles of a team size (P1 design §3.5, slice S3): heroes by player, five against five and up to fifteen a team, each
/// hero its player's, set down apart at its team's pod, a bot for each, the bots' roles and hold points by place, the
/// points that take a pod, kills credited to the player who dealt them, and a leaver's hero driven by a bot.
/// </summary>
public class BattleTeamTests(ITestOutputHelper output)
{
    private static GameData Data => Battles.Data;

    private static PlayerId P(int value) => new(value);

    /// <summary>The players of the Desert's two teams in a five against five, in their places.</summary>
    private static readonly PlayerId[] Reds = [P(0), P(4), P(5), P(6), P(7)], Greens = [P(2), P(8), P(9), P(10), P(11)];

    /// <summary>
    /// A battle of a team size on a map of the catalogue, every seat a <paramref name="kind"/>. People's heroes are at the
    /// wheel and stand still, as in <see cref="Battles.New"/>, so the tests say who goes where and hits whom.
    /// </summary>
    private static GameWorld Sized(PlayerKind kind = PlayerKind.Bot, int size = 5, string map = "m_desert", int seed = 7, int tier = 3, int lives = 1)
    {
        var entry = BattleCatalogue.Map(map)!;
        var options = new BattleOptions { Tier = tier, TeamSize = size, TeamA = entry.TeamA, TeamB = entry.TeamB, Lives = lives };
        var rows = BattleSeats.Of(options).Select(seat => new PlayerSeat(seat.Player, seat.Race, kind, $"Seat {seat.Player}")).ToList();
        var world = MatchSetup.Create(Data, entry.Scenario, entry.Planet, new PlayerTable(rows), seed,
            new MatchOptions { Battle = options, FogOfWar = false });
        if (kind == PlayerKind.Human)
        {
            foreach (var state in world.Wired!.Heroes.Values)
                world.HeroOf(state)!.DrivenByPlayer = true;
        }
        return world;
    }

    private static Machine Hero(GameWorld world, PlayerId player) => world.HeroOf(player)!;

    private static GameWorld Begun(GameWorld world)
    {
        Battles.Run(world, BattleBalance.Opening + GameWorld.Dt);
        Assert.False(world.Battle!.InOpening(world.Time));
        return world;
    }

    // ---- the line-up --------------------------------------------------------------------------------

    [Fact]
    public void Five_against_five_has_a_hero_and_a_bot_for_every_player()
    {
        var world = Sized();
        var run = world.Battle!;
        var everyone = Reds.Concat(Greens).Order().ToList();

        Assert.Equal(everyone, world.Wired!.Heroes.Keys);
        Assert.Equal(Reds, run.Members[0]);
        Assert.Equal(Greens, run.Members[1]);
        foreach (var (player, state) in world.Wired.Heroes)
        {
            var team = Reds.Contains(player) ? 0 : 1;
            var race = team == 0 ? Race.Red : Race.Green;
            Assert.Equal((player, race, run.Pods[team]), (state.Player, state.Race, state.Pod));
            Assert.Equal((player, race), (Hero(world, player).Owner, Hero(world, player).Race));
            Assert.Equal(team, run.TeamOf(player));
        }
        Assert.Equal(everyone, run.Tally.Keys);
        Assert.Equal(everyone, run.Bots);
        Assert.Equal(everyone, world.HeroBots.Keys);
        Assert.All(world.HeroBots, bot => Assert.Equal((bot.Key, world.Wired.Heroes[bot.Key].Race), (bot.Value.Player, bot.Value.Side)));
        Assert.Equal(everyone, run.Options.Heroes.Select(h => h.Player!.Value));
        Assert.Equal(10, run.Options.Heroes.Select(h => h.Name).Distinct().Count());

        // The bots drive the heroes off, the attackers (places 0, 2 and 3) at least.
        var attackers = new[] { Reds[0], Reds[2], Reds[3], Greens[0], Greens[2], Greens[3] };
        var starts = attackers.ToDictionary(p => p, p => Hero(world, p).Position);
        Battles.Run(world, BattleBalance.Opening + 20);
        Assert.All(attackers, p => Assert.True(world.HeroOf(p) is not { } hero || Vector2.Distance(hero.Position, starts[p]) > 10, $"{p} stood still"));
    }

    public static TheoryData<string> Maps
    {
        get
        {
            var maps = new TheoryData<string>();
            foreach (var map in BattleCatalogue.Maps)
                maps.Add(map.Scenario);
            return maps;
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Fifteen_heroes_a_team_stand_apart_at_their_pod_on_every_map(string map)
    {
        var world = Sized(size: 15, map: map);
        var run = world.Battle!;

        var heroes = world.Wired!.Heroes.Values.Select(state => (State: state, Hero: world.HeroOf(state)!)).ToList();
        Assert.Equal(30, heroes.Count);
        Assert.All(heroes, h => Assert.NotNull(h.Hero));
        for (var i = 0; i < heroes.Count; i++)
        {
            var (state, hero) = heroes[i];
            Assert.True(world.Nav.IsFree(hero.Position, crossesWater: false), $"{state.Player} stands on blocked ground");
            var pod = world.BattlePod(run.TeamOf(state.Player))!;
            Assert.True(Vector2.Distance(hero.Position, pod.Position) < 160, $"{state.Player} far from its pod");
            for (var j = i + 1; j < heroes.Count; j++)
            {
                var other = heroes[j].Hero;
                Assert.True(Vector2.Distance(hero.Position, other.Position) >= hero.Radius + other.Radius - 0.01f,
                    $"{state.Player} and {heroes[j].State.Player} overlap on {map}");
            }
        }
    }

    [Fact]
    public void Twin_five_against_five_bot_battles_stay_equal()
    {
        var (a, b) = (Sized(seed: 11), Sized(seed: 11));
        var starts = a.Wired!.Heroes.Values.ToDictionary(s => s.Player, s => a.HeroOf(s)!.Position);

        for (var tick = 1; tick <= 3000; tick++)
        {
            a.Step();
            b.Step();
            if (tick % 100 == 0)
                Assert.Equal(a.Checksum(), b.Checksum());
        }
        Assert.Contains(starts, start => a.HeroOf(start.Key) is not { } hero || Vector2.Distance(hero.Position, start.Value) > 50);
    }

    // ---- the bots -----------------------------------------------------------------------------------

    [Fact]
    public void Bots_attack_or_defend_by_place_and_hold_points_apart()
    {
        var world = Begun(Sized(PlayerKind.Human));
        var (own, enemy) = (world.BattlePod(0)!, world.BattlePod(1)!);
        var goals = new List<Vector2>();
        for (var place = 0; place < Reds.Length; place++)
        {
            var player = Reds[place];
            var orders = new List<Command>();
            new BattleBot(world, player, orders.Add).Think();

            Assert.NotEmpty(orders);
            Assert.All(orders, order => Assert.Equal((Race.Red, (PlayerId?)player), (order.Race, order.Issuer)));
            var goal = Assert.Single(orders.OfType<MoveCommand>()).Target;
            // Places 1 and 4 guard their own pod from further out; the others go for the other team's circle.
            var defends = place is 1 or 4;
            var (near, far) = defends ? (own, enemy) : (enemy, own);
            Assert.True(Vector2.Distance(goal, near.Position) <= (defends ? 29 : BattleBalance.CaptureRadius),
                $"place {place} goes {Vector2.Distance(goal, near.Position)} m from the pod it should be at");
            Assert.True(Vector2.Distance(goal, far.Position) > 100);
            goals.Add(goal);
        }
        for (var i = 0; i < goals.Count; i++)
        {
            for (var j = i + 1; j < goals.Count; j++)
                Assert.True(Vector2.Distance(goals[i], goals[j]) > 2, $"places {i} and {j} hold the same point");
        }
    }

    [Fact]
    public void A_bot_orders_only_its_own_player_s_hero()
    {
        var world = Begun(Sized(PlayerKind.Human));
        var orders = new List<Command>();
        new BattleBot(world, P(6), orders.Add).Think();

        Hero(world, P(6)).DrivenByPlayer = false;
        var mine = Hero(world, P(6)).Id;
        Assert.All(orders.OfType<MoveCommand>(), move => Assert.Equal([mine], move.Machines));
        foreach (var order in orders)
            world.Issue(order);
        world.Step();
        Assert.IsType<MoveOrder>(Hero(world, P(6)).Order);
        Assert.All(Reds.Where(p => p != P(6)), p => Assert.IsNotType<MoveOrder>(Hero(world, p).Order));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Defenders_go_over_to_the_attack_once_their_attackers_have_fallen_and_the_pod_is_quiet(bool attackerStands)
    {
        // Before, each defender stayed for the other, who stood; with every attacker down, battles ran out of time.
        var world = Begun(Sized(PlayerKind.Human));
        var (own, enemy) = (world.BattlePod(0)!, world.BattlePod(1)!);
        foreach (var place in new[] { 0, 2, 3 }.Where(place => !attackerStands || place != 3))
            world.Damage(Hero(world, Reds[place]), 1_000_000, Race.Green);
        // The bot's own pod quiet for longer than a defender waits (a minute).
        Battles.Run(world, 61);

        foreach (var place in new[] { 1, 4 })
        {
            var orders = new List<Command>();
            new BattleBot(world, Reds[place], orders.Add).Think();
            var goal = Assert.Single(orders.OfType<MoveCommand>()).Target;
            var pod = attackerStands ? own : enemy;
            Assert.True(Vector2.Distance(goal, pod.Position) <= 29, $"place {place} goes {Vector2.Distance(goal, pod.Position)} m from the pod it should be at");
        }
    }

    // ---- taking a pod -------------------------------------------------------------------------------

    [Fact]
    public void A_pod_takes_more_points_the_more_heroes_a_team_has()
    {
        Assert.Equal([100f, 100f, 150f, 200f, 250f, 500f, 750f], new[] { 1, 2, 3, 4, 5, 10, 15 }.Select(BattleBalance.CaptureTargetFor));
        Assert.Equal(BattleBalance.CaptureTarget, Battles.New().Battle!.CaptureTarget);
        Assert.Equal(BattleBalance.CaptureTarget, Sized(size: 2).Battle!.CaptureTarget);
        Assert.Equal(250, Sized().Battle!.CaptureTarget);

        // Five heroes in the circle take the pod in fifty seconds, as two did with a hundred points.
        var world = Begun(Sized(PlayerKind.Human));
        var run = world.Battle!;
        var pod = world.BattlePod(1)!;
        // The other team stands aside, halfway between the pods, out of both circles.
        var halfway = (pod.Position + world.BattlePod(0)!.Position) / 2;
        for (var i = 0; i < Greens.Length; i++)
            Hero(world, Greens[i]).Position = world.Nav.NearestFreePoint(halfway + new Vector2(12 * i, 0), crossesWater: false)!.Value;
        var spots = Spots(world, pod.Position, 22, Reds.Max(p => Hero(world, p).Radius) * 2 + 1);
        for (var i = 0; i < Reds.Length; i++)
        {
            var hero = Hero(world, Reds[i]);
            (hero.Position, hero.DrivenByPlayer) = (spots[i], true);
            hero.Path.Clear();
        }

        Battles.Run(world, 45);
        Assert.False(run.Over);
        Assert.InRange(run.Progress(0), 200, 249);

        Battles.Run(world, 10);
        Assert.Equal((0, BattleEnd.Capture), (run.Result!.Winner, run.Result.Reason));
        Assert.Equal(250, run.Progress(0));
        Assert.All(Reds, p => Assert.True(run.Tally[p].Capture > 40));
    }

    /// <summary>Free points on a circle round a place, at least <paramref name="apart"/> from each other.</summary>
    private static List<Vector2> Spots(GameWorld world, Vector2 middle, float radius, float apart)
    {
        var spots = new List<Vector2>();
        for (var i = 0; i < 64; i++)
        {
            var spot = middle + new Vector2(DetMath.Cos(i * MathF.Tau / 64), DetMath.Sin(i * MathF.Tau / 64)) * radius;
            if (world.Nav.IsFree(spot, crossesWater: false) && spots.All(s => Vector2.Distance(s, spot) >= apart))
                spots.Add(spot);
        }
        Assert.True(spots.Count >= Reds.Length, "no room in the circle");
        return spots;
    }

    // ---- credit by player ---------------------------------------------------------------------------

    [Fact]
    public void A_kill_counts_for_the_player_who_dealt_it_not_the_race()
    {
        var world = Begun(Sized(PlayerKind.Human));
        var run = world.Battle!;
        var victim = Hero(world, P(9));
        var before = world.Events.Count;

        world.Damage(victim, 1_000_000, Race.Red, Hero(world, P(5)));

        var killed = Assert.Single(world.Events.Skip(before), e => e.Kind == GameEventKind.BattleHeroKilled);
        Assert.Equal(((PlayerId?)P(9), (PlayerId?)P(5), (int?)(int)Race.Red), (killed.Player, killed.By, killed.Other));
        Assert.Equal(1, run.Tally[P(5)].Kills);
        Assert.True(run.Tally[P(5)].Damage > 0);
        Assert.All(Reds.Where(p => p != P(5)), p => Assert.Equal((0, 0), (run.Tally[p].Kills, run.Tally[p].Damage)));
        Assert.Equal(P(5), world.CreditOf(victim));
        world.Step();
        Assert.Equal(1, run.Tally[P(9)].Deaths);
        Assert.All(Greens.Where(p => p != P(9)), p => Assert.Equal(0, run.Tally[p].Deaths));
    }

    [Fact]
    public void A_blast_keeps_the_kill_for_its_player_after_their_hero_fell()
    {
        var world = Begun(Sized(PlayerKind.Human));
        var run = world.Battle!;
        world.Damage(Hero(world, P(6)), 1_000_000, Race.Green, Hero(world, P(10)));
        Assert.Null(world.HeroOf(P(6)));
        var target = Hero(world, P(11));
        target.Position = world.Nav.NearestFreePoint(new Vector2(60, 60), crossesWater: false)!.Value;

        world.StartBlast(Data.Parm.Weapon("VORTEX"), target.Position, Race.Red, owner: P(6));
        Battles.Run(world, 3);

        Assert.True(run.Tally[P(6)].Damage > 0);
        Assert.All(Reds.Where(p => p != P(6)), p => Assert.Equal(0, run.Tally[p].Damage));
        Assert.Equal(P(6), world.CreditOf(target));
    }

    [Fact]
    public void Credit_without_a_player_of_the_race_goes_to_its_first_player()
    {
        var world = Begun(Sized(PlayerKind.Human));
        var (one, two) = (Hero(world, P(8)), Hero(world, P(10)));

        world.Damage(one, 10, Race.Red);
        world.Damage(two, 10, Race.Red, player: P(9));

        Assert.Equal(P(0), world.CreditOf(one));
        Assert.Equal(P(0), world.CreditOf(two));
        Assert.True(world.Battle!.Tally[P(0)].Damage > 0);
        Assert.All(Reds.Skip(1), p => Assert.Equal(0, world.Battle.Tally[p].Damage));
    }

    // ---- leavers --------------------------------------------------------------------------------------

    [Fact]
    public void A_leaver_s_hero_is_driven_by_a_bot_until_they_are_back()
    {
        var world = Begun(Sized(PlayerKind.Human));
        var run = world.Battle!;
        var hero = Hero(world, P(5));
        Assert.Null(Battles.Do(world, new DriveCommand(Race.Red, hero.Id, Driving: true, Forward: 1) { Issuer = P(5) }));
        Assert.Equal(P(5), hero.Driver);

        world.ChangePresence([new PresenceChange(P(5), Away: true)]);

        Assert.Equal([P(5)], world.HeroBots.Keys);
        Assert.Equal([P(5)], run.Bots);
        Assert.False(hero.DrivenByPlayer);
        Assert.Equal(DefCon.High, hero.DefCon);
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);
        Assert.All(Reds.Where(p => p != P(5)), p => Assert.True(Hero(world, p).DrivenByPlayer));
        var start = hero.Position;
        Battles.Run(world, 20);
        Assert.True(Vector2.Distance(hero.Position, start) > 30);

        world.ChangePresence([new PresenceChange(P(5), Away: false)]);
        Assert.Empty(world.HeroBots);
        Assert.Empty(run.Bots);
        Assert.Equal(DefCon.Normal, hero.DefCon);

        // The whole team gone, the race is the computer's, every hero a bot's; the first back takes the race again.
        world.ChangePresence([.. Reds.Select(p => new PresenceChange(p, Away: true))]);
        Assert.Equal(Reds, world.HeroBots.Keys);
        Assert.Equal(Controller.Ai, world.Races[Race.Red].Controller);
        world.ChangePresence([new PresenceChange(P(4), Away: false)]);
        Assert.Equal(Reds.Where(p => p != P(4)), world.HeroBots.Keys);
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);

        // Gone and back within a tick, a player keeps the wheel.
        var green = Hero(world, P(9));
        Assert.Null(Battles.Do(world, new DriveCommand(Race.Green, green.Id, Driving: true) { Issuer = P(9) }));
        world.ChangePresence([new PresenceChange(P(9), Away: true), new PresenceChange(P(9), Away: false)]);
        Assert.DoesNotContain(P(9), world.HeroBots.Keys);
        Assert.True(green.DrivenByPlayer);
    }

    [Fact]
    public void A_teammate_neither_drives_nor_orders_another_player_s_hero()
    {
        var world = Begun(Sized(PlayerKind.Human));
        var hero = Hero(world, P(7));
        hero.DrivenByPlayer = false;

        Battles.Do(world, new DriveCommand(Race.Red, hero.Id, Driving: true, Forward: 1) { Issuer = P(4) });
        Battles.Do(world, new MoveCommand(Race.Red, [hero.Id], world.BattlePod(1)!.Position) { Issuer = P(0) });

        Assert.False(hero.DrivenByPlayer);
        Assert.IsNotType<MoveOrder>(hero.Order);
        Assert.Null(Battles.Do(world, new MoveCommand(Race.Red, [hero.Id], world.BattlePod(1)!.Position) { Issuer = P(7) }));
        Assert.IsType<MoveOrder>(hero.Order);
    }

    // ---- for tuning -----------------------------------------------------------------------------------

    private static readonly string? LengthReport = Environment.GetEnvironmentVariable("MACHINES_BATTLE_LENGTHS") is { Length: > 0 } path ? path : null;

    private sealed class BattleLengthsAttribute : FactAttribute
    {
        public BattleLengthsAttribute()
        {
            if (LengthReport is null)
                Skip = "set MACHINES_BATTLE_LENGTHS=<report file> to play twenty 5 vs 5 bot battles per tier 1, 4 and 8 and write their mean length";
        }
    }

    /// <summary>
    /// What the user tunes the capture target and lives by (P1 design §3.5): the mean length of twenty five against five
    /// battles of bots on the Desert at tiers 1, 4 and 8, from the end of the opening, and how they ended; with one life,
    /// a battle's default, and with the lives a room proposes for five a team.
    /// </summary>
    [BattleLengths]
    public void Five_against_five_bot_battles_last_this_long()
    {
        var lines = new List<string>
        {
            $"Twenty 5 vs 5 bot battles per tier on m_desert, capture target {BattleBalance.CaptureTargetFor(5)}, time limit {new BattleOptions().TimeLimit} s, from the end of the opening:",
        };
        // With the lives a battle has by default and those a room proposes for five a team.
        foreach (var lives in new[] { 1, BattleBalance.ProposedLives(5) }.Distinct())
        {
            foreach (var tier in new[] { 1, 4, 8 })
                lines.Add($"lives {lives}, tier {tier}: {Lengths(seed => Sized(seed: seed, tier: tier, lives: lives))}");
        }
        // Today's two against two of bots, to compare with.
        foreach (var tier in new[] { 1, 4, 8 })
            lines.Add($"today's 2 vs 2, lives 1, capture target {BattleBalance.CaptureTarget}, tier {tier}: "
                + Lengths(seed => Battles.New(new BattleOptions { Tier = tier }, seed: seed, bots: [Race.Red, Race.Blue, Race.Green, Race.Yellow])));
        foreach (var line in lines)
            output.WriteLine(line);
        File.WriteAllLines(LengthReport!, lines);
    }

    /// <summary>Twenty battles played out to their end: their mean length from the end of the opening, and how they ended.</summary>
    private static string Lengths(Func<int, GameWorld> battle)
    {
        var results = new BattleResult?[20];
        var lengths = new float[20];
        Parallel.For(0, 20, i =>
        {
            var world = battle(1000 + i);
            while (!world.Battle!.Over && world.Tick < 20 * 60 * GameWorld.TicksPerSecond)
                world.Step();
            (results[i], lengths[i]) = (world.Battle.Result, world.Battle.Elapsed);
        });
        var ends = results.GroupBy(r => r?.Reason.ToString() ?? "unfinished").OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
        return $"mean {lengths.Average():F0} s (shortest {lengths.Min():F0} s, longest {lengths.Max():F0} s); {string.Join(", ", ends)}";
    }
}
