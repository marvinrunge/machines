using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// A Wired run of two to four heroes of one race (P1 design §3.6, slice S3): each hero its player's, with a bank of its
/// own, its own escorts, mines and towers, credited with what it does; a hero that falls in a wave comes back when the
/// wave is over; the waves grow with the heroes. A run of one hero is played exactly as before.
/// </summary>
public class WiredCoopTests
{
    private static GameData Data => Battles.Data;

    private static PlayerId P(int value) => new(value);

    /// <summary>The players of a run of <paramref name="heroes"/> Red heroes: the race's first, then 4, 5 and 6.</summary>
    private static PlayerId[] Mates(int heroes) => [P(0), .. Enumerable.Range(4, heroes - 1).Select(P)];

    /// <summary>A run of <paramref name="heroes"/> Red heroes against the computer's Blue on the Desert.</summary>
    private static GameWorld Coop(int heroes, int seed = 7, WiredOptions? options = null)
    {
        var rows = new List<PlayerSeat> { new(P(0), Race.Red, PlayerKind.Human, "Player"), new(P(1), Race.Blue, PlayerKind.Ai, "AI") };
        rows.AddRange(Mates(heroes).Skip(1).Select(id => new PlayerSeat(id, Race.Red, PlayerKind.Human, $"Mate {id.Value}")));
        return MatchSetup.Create(Data, "m_desert", "m_desert", new PlayerTable(rows), seed, new MatchOptions { Wired = options ?? new WiredOptions() });
    }

    /// <summary>The run of one hero as it was always set up.</summary>
    private static GameWorld Lone(int seed = 7) =>
        MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = new WiredOptions() });

    private static GameWorld AtWave(GameWorld world, int wave)
    {
        world.Wired!.Wave = wave - 1;
        world.Wired.Countdown = 0;
        world.Step();
        world.Step();
        Assert.Equal((WiredPhase.Wave, wave), (world.Wired.Phase, world.Wired.Wave));
        return world;
    }

    /// <summary>Kills the wave's machines as they come, by the race alone, until the wave is over.</summary>
    private static List<GameEvent> ClearWave(GameWorld world)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < 20 * 120 && world.Wired!.Phase == WiredPhase.Wave; i++)
        {
            foreach (var foe in world.Wired.Foes)
                world.Damage(foe.Machine, 1_000_000, Race.Red);
            world.Step();
            events.AddRange(world.Events);
        }
        Assert.NotEqual(WiredPhase.Wave, world.Wired!.Phase);
        return events;
    }

    private static List<GameEvent> Run(GameWorld world, float seconds) => Battles.Run(world, seconds);

    private static string? Do(GameWorld world, Command command) => Battles.Do(world, command);

    private static WiredState State(GameWorld world, PlayerId player) => world.Wired!.Heroes[player];

    private static Machine Hero(GameWorld world, PlayerId player) => world.HeroOf(player)!;

    private static Construction Pod(GameWorld world) => world.WiredPod(P(0))!;

    private static Vector2 FreeAt(GameWorld world, Vector2 near) => world.Nav.NearestFreePoint(near, crossesWater: false)!.Value;

    /// <summary>A Blue grunt's wreck out in the field, killed by the race alone.</summary>
    private static DebrisState Wreck(GameWorld world)
    {
        var grunt = world.AddMachine(Race.Blue, FreeAt(world, Pod(world).Position + new Vector2(120, 0)), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Damage(grunt, 1_000_000, Race.Red);
        world.Step();
        return world.Debris[^1];
    }

    private static readonly ResearchKey Grunt = new("AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    // ---- the heroes ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Each_player_has_a_hero_and_a_bank_of_their_own_at_the_pod(int heroes)
    {
        var world = Coop(heroes);
        var run = world.Wired!;
        var lone = Lone();
        var start = lone.Races[Race.Red].Bmu;
        var pod = Pod(world);

        Assert.Equal(Mates(heroes), run.Heroes.Keys);
        Assert.Equal(0, world.Races[Race.Red].Bmu);
        Assert.True(run.BackAfterWave);
        foreach (var (player, state) in run.Heroes)
        {
            var hero = Hero(world, player);
            Assert.Equal((player, Race.Red, true, start), (state.Player, state.Race, state.OwnBank, state.Bank));
            Assert.Equal((player, Race.Red), (hero.Owner, hero.Race));
            Assert.Equal(pod.Id, state.Pod);
            Assert.True(hero.DistanceTo(pod) <= WiredBalance.PodReach, $"{player} stands {hero.DistanceTo(pod)} m from the pod");
            Assert.True(world.Nav.IsFree(hero.Position, crossesWater: false));
            Assert.Equal(world.HeroOf(state), world.HeroOf(player));
        }
        // The race's first hero at the door, where a lone hero stands; the others beside it, nobody on another.
        Assert.Equal(lone.HeroOf(Race.Red)!.Position, Hero(world, P(0)).Position);
        var all = Mates(heroes).Select(p => Hero(world, p)).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
                Assert.True(Vector2.Distance(all[i].Position, all[j].Position) >= all[i].Radius + all[j].Radius, $"heroes {i} and {j} overlap");
        }
    }

    [Fact]
    public void A_lone_hero_s_run_is_played_as_before()
    {
        var (coop, lone) = (Coop(1), Lone());
        var state = State(coop, P(0));
        Assert.False(state.OwnBank);
        Assert.Equal(0, state.Bank);
        Assert.False(coop.Wired!.BackAfterWave);
        Assert.Equal(lone.Races[Race.Red].Bmu, coop.Races[Race.Red].Bmu);
        Assert.Equal(lone.Checksum(), coop.Checksum());

        AtWave(coop, 1);
        AtWave(lone, 1);
        for (var tick = 1; tick <= 1200; tick++)
        {
            coop.Step();
            lone.Step();
            if (tick % 100 == 0)
                Assert.Equal(lone.Checksum(), coop.Checksum());
        }

        // A lone hero that falls in a wave comes back by the clock, as it always did.
        var lonely = AtWave(Lone(), 1);
        lonely.Damage(lonely.HeroOf(Race.Red)!, 1_000_000, Race.Blue);
        lonely.Step();
        var fallen = lonely.Wired!.Heroes[P(0)];
        Assert.False(fallen.WaitsForWave);
        Assert.NotNull(fallen.RespawnAt);
    }

    [Fact]
    public void Heroes_of_two_races_keep_their_race_s_bank()
    {
        var rows = new List<PlayerSeat>
        {
            new(P(0), Race.Red, PlayerKind.Human, "Red"), new(P(1), Race.Blue, PlayerKind.Ai, "AI"), new(P(2), Race.Green, PlayerKind.Human, "Green"),
        };
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", new PlayerTable(rows), 7, new MatchOptions { Wired = new WiredOptions() });
        var lone = Lone();

        Assert.Equal([P(0), P(2)], world.Wired!.Heroes.Keys);
        Assert.All(world.Wired.Heroes.Values, state => Assert.False(state.OwnBank));
        Assert.Equal(lone.Races[Race.Red].Bmu, world.Races[Race.Red].Bmu);
        Assert.Equal(lone.Races[Race.Red].Bmu, world.Races[Race.Green].Bmu);
        Assert.Equal(Race.Green, Hero(world, P(2)).Race);
    }

    [Fact]
    public void A_teammate_neither_drives_nor_orders_another_player_s_hero()
    {
        var world = Coop(2);
        var mate = Hero(world, P(4));
        var away = FreeAt(world, Pod(world).Position + new Vector2(80, 0));

        Assert.Null(Do(world, new DriveCommand(Race.Red, mate.Id, Driving: true, Forward: 1) { Issuer = P(0) }));
        Assert.False(mate.DrivenByPlayer);
        Do(world, new MoveCommand(Race.Red, [mate.Id], away) { Issuer = P(0) });
        Assert.IsNotType<MoveOrder>(mate.Order);
        Assert.False(world.MayOrder(P(0), mate));

        Assert.Null(Do(world, new DriveCommand(Race.Red, mate.Id, Driving: true, Forward: 1) { Issuer = P(4) }));
        Assert.Equal((true, (PlayerId?)P(4)), (mate.DrivenByPlayer, mate.Driver));
        Do(world, new DriveCommand(Race.Red, mate.Id, Driving: false) { Issuer = P(0) });
        Assert.Equal((true, (PlayerId?)P(4)), (mate.DrivenByPlayer, mate.Driver));

        Assert.Equal("not your race", Do(world, new DriveCommand(Race.Red, mate.Id, Driving: true) { Issuer = P(1) }));
        Assert.Equal("you have no hero", Do(world, new WiredHangarCommand(Race.Blue, true) { Issuer = P(1) }));
    }

    [Fact]
    public void A_leaver_s_hero_stands_and_does_not_drive_on_with_their_last_input()
    {
        var world = Coop(2);
        var (mine, mate) = (Hero(world, P(0)), Hero(world, P(4)));
        Assert.Null(Do(world, new DriveCommand(Race.Red, mine.Id, Driving: true, Forward: 1) { Issuer = P(0) }));
        Assert.Null(Do(world, new DriveCommand(Race.Red, mate.Id, Driving: true, Forward: 1) { Issuer = P(4) }));
        Run(world, 1);

        world.ChangePresence([new PresenceChange(P(4), Away: true)]);

        Assert.Equal((false, (PlayerId?)null), (mate.DrivenByPlayer, mate.Driver));
        Assert.Equal((true, (PlayerId?)P(0)), (mine.DrivenByPlayer, mine.Driver));
        var at = mate.Position;
        Run(world, 3);
        Assert.True(Vector2.Distance(mate.Position, at) < 1, $"the leaver's hero went on {Vector2.Distance(mate.Position, at)} m");
        Assert.True(mine.DrivenByPlayer);

        // Back, the player takes the wheel again as anyone does.
        world.ChangePresence([new PresenceChange(P(4), Away: false)]);
        Assert.Null(Do(world, new DriveCommand(Race.Red, mate.Id, Driving: true, Forward: 1) { Issuer = P(4) }));
        Assert.Equal((true, (PlayerId?)P(4)), (mate.DrivenByPlayer, mate.Driver));
    }

    // ---- banks ----------------------------------------------------------------------------------------

    [Fact]
    public void A_wreck_pays_whoever_collects_it()
    {
        var world = Coop(2);
        world.Wired!.Countdown = 1000;
        var (first, mate) = (State(world, P(0)), State(world, P(4)));
        var wreck = Wreck(world);
        var (firstBank, mateBank) = (first.Bank, mate.Bank);

        Hero(world, P(4)).Position = wreck.Position;
        var events = Run(world, 0.05f);

        Assert.DoesNotContain(wreck, world.Debris);
        Assert.Equal(mateBank + 17, mate.Bank);
        Assert.Equal(firstBank, first.Bank);
        Assert.Equal(0, world.Races[Race.Red].Bmu);
        var collected = Assert.Single(events, e => e.Kind == GameEventKind.WiredWreckCollected);
        Assert.Equal(Hero(world, P(4)).Id, collected.Actor);
        Assert.Equal((1, 17), (mate.Stats.WrecksCollected, mate.Stats.BmusEarned));
        Assert.Equal((0, 0), (first.Stats.WrecksCollected, first.Stats.BmusEarned));
    }

    [Fact]
    public void The_sweep_is_shared_out_among_the_heroes()
    {
        var world = Coop(2);
        world.Wired!.Countdown = 1000;
        var (first, mate) = (State(world, P(0)), State(world, P(4)));
        Wreck(world);
        var (firstBank, mateBank) = (first.Bank, mate.Bank);

        world.Wired.Countdown = 0;
        var events = Run(world, 0.05f);

        // A lone hero sweeps up 8 of the wreck's 17; two get 4 each.
        Assert.Empty(world.Debris);
        Assert.Equal((firstBank + 4, mateBank + 4), (first.Bank, mate.Bank));
        var swept = events.Where(e => e.Kind == GameEventKind.WiredWrecksSwept).ToList();
        Assert.Equal([(P(0), "4"), (P(4), "4")], swept.Select(e => (e.Player!.Value, e.Detail!)));
    }

    [Fact]
    public void Each_hero_shops_from_their_own_bank_and_their_escort_is_theirs()
    {
        var world = Coop(2);
        var (first, mate) = (State(world, P(0)), State(world, P(4)));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true) { Issuer = P(4) }));
        Assert.Equal((false, true), (first.HangarOpen, mate.HangarOpen));
        Assert.Equal("the hangar is closed", Do(world, new WiredSquadBuyCommand(Race.Red, Grunt)));

        var (firstBank, mateBank, price) = (first.Bank, mate.Bank, world.EscortPrice(mate, Grunt));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, Grunt) { Issuer = P(4) }));

        Assert.Equal((firstBank, mateBank - price), (first.Bank, mate.Bank));
        var escort = Assert.Single(world.Escorts(mate));
        Assert.Empty(world.Escorts(first));
        Assert.Equal(P(4), escort.Owner);
        Assert.Equal(new FollowOrder(Hero(world, P(4)).Id), escort.Order);
        Assert.True(world.MayOrder(P(4), escort));
        Assert.False(world.MayOrder(P(0), escort));
        Do(world, new MoveCommand(Race.Red, [escort.Id], FreeAt(world, Pod(world).Position + new Vector2(80, 0))) { Issuer = P(0) });
        Assert.IsNotType<MoveOrder>(escort.Order);
    }

    [Fact]
    public void Each_hero_lays_up_to_the_most_mines_of_its_own()
    {
        var spies = new WiredOptions { Heroes = [new WiredHero(P(0), "One", "spy3", []), new WiredHero(P(4), "Two", "spy3", [])] };
        var world = Coop(2, options: spies);
        var (first, mate) = (State(world, P(0)), State(world, P(4)));
        var far = Pod(world).Position + new Vector2(300, 300);
        for (var i = 0; i < WiredBalance.MostMines; i++)
            world.LandMines.Add(new LandMineState(1000 + i, Race.Red, far + new Vector2(10 * i, 0), P(0)));

        Assert.Equal($"{WiredBalance.MostMines} mines lie already", world.WhyNoMine(first));
        Assert.Null(world.WhyNoMine(mate));
        var mines = Hero(world, P(4)).Mines;
        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red) { Issuer = P(4) }));

        var laid = world.LandMines[^1];
        Assert.Equal((P(4), Race.Red), (laid.Owner, laid.Race));
        Assert.Equal(mines - 1, Hero(world, P(4)).Mines);
        Assert.Equal($"{WiredBalance.MostMines} mines lie already", Do(world, new WiredSpecialCommand(Race.Red) { Issuer = P(0) }));
        Assert.Equal(WiredBalance.MostMines + 1, world.LandMines.Count);
    }

    // ---- falling --------------------------------------------------------------------------------------

    [Fact]
    public void A_hero_that_falls_in_a_wave_comes_back_beside_the_door_when_it_is_over()
    {
        var world = AtWave(Coop(2), 1);
        var mate = State(world, P(4));
        var spare = mate.SpareBodies;
        var old = Hero(world, P(4));

        world.Damage(old, 1_000_000, Race.Blue);
        world.Step();

        Assert.Null(world.HeroOf(P(4)));
        Assert.True(mate.WaitsForWave);
        Assert.Null(mate.RespawnAt);
        Assert.Equal(spare - 1, mate.SpareBodies);
        Assert.DoesNotContain(Run(world, WiredBalance.RespawnTime + 1), e => e.Kind == GameEventKind.WiredHeroRespawned && e.Player == P(4));
        Assert.Null(world.HeroOf(P(4)));

        var events = ClearWave(world);

        var back = Assert.Single(events, e => e.Kind == GameEventKind.WiredHeroRespawned && e.Player == P(4));
        var hero = Hero(world, P(4));
        Assert.Equal((hero.Id, P(4)), (back.Actor, hero.Owner));
        Assert.False(mate.WaitsForWave);
        Assert.True(hero.DistanceTo(Pod(world)) <= WiredBalance.PodReach);
        // Beside the door, which is the race's first hero's.
        Assert.True(Vector2.Distance(hero.Position, Pod(world).EntranceOutside!.Value) >= hero.Radius);
    }

    [Fact]
    public void With_every_hero_down_in_a_wave_the_run_goes_on_until_it_is_over()
    {
        var world = AtWave(Coop(2), 1);
        foreach (var player in Mates(2))
            world.Damage(Hero(world, player), 1_000_000, Race.Blue);
        world.Step();

        Assert.Equal(WiredPhase.Wave, world.Wired!.Phase);
        Assert.Null(world.Wired.Ending);
        Assert.All(world.Wired.Heroes.Values, state => Assert.True(state.WaitsForWave));

        ClearWave(world);

        Assert.All(Mates(2), player => Assert.NotNull(world.HeroOf(player)));
        Assert.NotEqual(WiredPhase.Ended, world.Wired.Phase);
    }

    [Fact]
    public void A_hero_that_falls_between_waves_comes_back_by_the_clock()
    {
        var world = Coop(2);
        world.Wired!.Countdown = 1000;
        var mate = State(world, P(4));

        world.Damage(Hero(world, P(4)), 1_000_000, Race.Blue);
        world.Step();

        Assert.False(mate.WaitsForWave);
        Assert.NotNull(mate.RespawnAt);
        Run(world, WiredBalance.RespawnTime + 0.2f);
        Assert.NotNull(world.HeroOf(P(4)));
        Assert.Equal(P(4), Hero(world, P(4)).Owner);
    }

    // ---- credit ---------------------------------------------------------------------------------------

    [Fact]
    public void Kills_and_damage_count_for_the_hero_who_dealt_them()
    {
        var world = AtWave(Coop(2), 1);
        var (first, mate) = (State(world, P(0)), State(world, P(4)));
        var foes = world.Wired!.Foes.Select(f => f.Machine).Take(2).ToList();
        Assert.Equal(2, foes.Count);
        var (kills, dealt) = (first.Stats.Kills, first.Stats.DamageDealt);

        world.Damage(foes[0], 1_000_000, Race.Red, Hero(world, P(4)));
        Assert.Equal(P(4), world.CreditOf(foes[0]));
        world.Step();
        Assert.Equal(1, mate.Stats.Kills);
        Assert.True(mate.Stats.DamageDealt > 0);
        Assert.Equal((kills, dealt), (first.Stats.Kills, first.Stats.DamageDealt));

        // A blow by the race alone is its first player's.
        world.Damage(foes[1], 1_000_000, Race.Red);
        Assert.Equal(P(0), world.CreditOf(foes[1]));
        world.Step();
        Assert.Equal(kills + 1, first.Stats.Kills);
        Assert.Equal(1, mate.Stats.Kills);
    }

    // ---- the waves --------------------------------------------------------------------------------

    [Fact]
    public void The_waves_grow_with_the_heroes_and_not_for_one()
    {
        Assert.Equal((1f, 1f), (WiredBalance.CoopFoes(1), WiredBalance.CoopBoss(1)));
        Assert.Equal(1.6f, WiredBalance.CoopFoes(2), 4);
        Assert.Equal(2.8f, WiredBalance.CoopFoes(4), 4);
        Assert.Equal(1.5f, WiredBalance.CoopBoss(2), 4);
        Assert.Equal(2.5f, WiredBalance.CoopBoss(4), 4);
        Assert.Equal(WiredBalance.MostInWave, WiredBalance.MostInWaveFor(1));
        Assert.Equal(96, WiredBalance.MostInWaveFor(2));
        Assert.Equal(168, WiredBalance.MostInWaveFor(4));

        foreach (var wave in new[] { 1, 4, 10, 15 })
        {
            var usual = WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(wave));
            var one = WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(wave), heroes: 1);
            Assert.Equal((usual.Budget, usual.Boss), (one.Budget, one.Boss));
            Assert.Equal(usual.Groups.SelectMany(g => g.Machines), one.Groups.SelectMany(g => g.Machines));
            Assert.Equal(usual.Groups.Select(g => g.After), one.Groups.Select(g => g.After));

            var two = WiredDirector.Plan(Data, wave, WiredDifficulty.Normal, new Random(wave), heroes: 2);
            Assert.Equal(usual.Budget * WiredBalance.CoopFoes(2), two.Budget);
            if (usual.Boss is not null)
            {
                var (alone, together) = (usual.Groups[0].Machines.First(m => m.Boss is not null), two.Groups[0].Machines.First(m => m.Boss is not null));
                Assert.Equal(alone.HitPoints * WiredBalance.CoopBoss(2), together.HitPoints);
            }
        }

        var (lone, four) = (AtWave(Lone(), 8), AtWave(Coop(4), 8));
        Assert.True(four.Wired!.EnemiesLeft > lone.Wired!.EnemiesLeft, $"four heroes face {four.Wired.EnemiesLeft}, one {lone.Wired.EnemiesLeft}");
    }

    [Fact]
    public void Twin_co_op_runs_stay_equal()
    {
        var (a, b) = (AtWave(Coop(3, seed: 11), 2), AtWave(Coop(3, seed: 11), 2));
        for (var tick = 1; tick <= 3000; tick++)
        {
            a.Step();
            b.Step();
            if (tick % 100 == 0)
                Assert.Equal(a.Checksum(), b.Checksum());
        }
    }
}
