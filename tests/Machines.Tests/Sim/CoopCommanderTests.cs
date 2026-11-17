using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Two commanders of one race in a skirmish (P1 design §1.5, §2, slice S2, the skirmish part of test T7): each orders only
/// their own machines while the race's buildings obey both, a factory's machine is whoever's ordered it, squadrons are each
/// commander's own and the whole race's again under the stand-in AI, machines are handed to a teammate, a leaver's machines
/// are kept by the teammate and the computer takes the race only once its last commander is away, and a machine has one
/// driver. Twin co-op worlds stay equal, and the golden skirmishes' orders given by their players play exactly as given by
/// their races.
/// </summary>
public class CoopCommanderTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    /// <summary>Anna and Ben command Red, Cleo Blue.</summary>
    private static readonly PlayerId Anna = PlayerId.Of(Race.Red), Ben = new(4), Cleo = PlayerId.Of(Race.Blue);

    private static PlayerTable Coop => new(
    [
        new PlayerSeat(Anna, Race.Red, PlayerKind.Human, "Anna"),
        new PlayerSeat(Cleo, Race.Blue, PlayerKind.Human, "Cleo"),
        new PlayerSeat(Ben, Race.Red, PlayerKind.Human, "Ben"),
    ]);

    private static GameWorld NewCoop() => MatchSetup.Create(Data, "m_desert", "m_desert", Coop, 7);

    /// <summary>Open ground away from every building.</summary>
    private static Vector2 OpenGround(GameWorld world) => Enumerable.Range(0, 400).Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
        .First(p => world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));

    private static Machine Grunt(GameWorld world, Vector2 at, PlayerId owner) => world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER", owner);

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    private static PresenceChange Away(PlayerId player) => new(player, Away: true);

    private static PresenceChange Back(PlayerId player) => new(player, Away: false);

    // ---- who orders what ------------------------------------------------------------------------------

    [Fact]
    public void Each_commander_orders_only_their_own_machines()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var hers = Grunt(world, at, Anna);
        var his = Grunt(world, at + new Vector2(0, 12), Ben);
        var blue = world.Machines.First(m => m.Race == Race.Blue);
        var target = at + new Vector2(60, 0);

        Assert.Equal((Anna, Ben), (hers.Owner, his.Owner));
        Assert.Equal((null, (PlayerId?)Ben), (hers.OwnerOverride, his.OwnerOverride));
        Assert.True(world.MayOrder(Anna, hers) && world.MayOrder(Ben, his) && world.MayOrder(null, hers));
        Assert.False(world.MayOrder(Ben, hers) || world.MayOrder(Anna, his));

        world.Issue(new MoveCommand(Race.Red, [hers.Id, his.Id], target) { Issuer = Ben });
        world.Step();
        Assert.IsType<IdleOrder>(hers.Order);
        Assert.IsType<MoveOrder>(his.Order);

        world.Issue(new StopCommand(Race.Red, [hers.Id, his.Id]) { Issuer = Anna });
        world.Step();
        Assert.IsType<MoveOrder>(his.Order);

        // The race's own authority, as the AI, the scripts and these tests give orders, orders all of it.
        world.Issue(new MoveCommand(Race.Red, [hers.Id, his.Id], target));
        world.Step();
        Assert.IsType<MoveOrder>(hers.Order);
        Assert.Empty(world.Rejected);

        // Nobody speaks for another race.
        world.Issue(new StopCommand(Race.Blue, [blue.Id]) { Issuer = Ben });
        world.Issue(new StopCommand(Race.Red, [his.Id]) { Issuer = new PlayerId(9) });
        world.Step();
        Assert.Equal(["not your race", "not your race"], world.Rejected.Select(r => r.Reason));
        Assert.IsType<MoveOrder>(his.Order);
    }

    [Fact]
    public void The_race_s_buildings_obey_both_and_a_factory_s_machine_is_whoever_s_ordered_it()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var factory = world.AddConstruction(Race.Red, at, "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var key = world.Data.FactoryOffer("CIVILIAN", 1).First();
        world.Races[Race.Red].Researched.Add(key);
        world.Races[Race.Red].Bmu = 1000;
        var point = at + new Vector2(0, 60);

        world.Issue(new AssemblyPointCommand(Race.Red, [factory.Id], point) { Issuer = Ben });
        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key) { Issuer = Ben });
        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key) { Issuer = Anna });
        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key) { Issuer = Ben });
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.Equal(point, factory.AssemblyPoint);
        Assert.Equal([Ben, Anna, Ben], factory.ProductionOwners);
        Assert.Null(factory.OwnerOverride);

        // Anna takes Ben's last one off.
        world.Issue(new CancelProductionCommand(Race.Red, factory.Id, 2) { Issuer = Anna });
        world.Step();
        Assert.Equal([Ben, Anna], factory.ProductionOwners);

        Assert.Equal([Ben, Anna], Produce(world, factory, 2).Select(m => m.Owner));
        Assert.Empty(factory.ProductionQueue);
        Assert.Empty(factory.ProductionOwners);

        // The race's own authority queues for the race's first player.
        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key));
        world.Step();
        Assert.Equal([Anna], factory.ProductionOwners);
        Assert.Null(Assert.Single(Produce(world, factory, 1)).OwnerOverride);
    }

    [Fact]
    public void A_queue_changed_another_way_builds_for_the_race_s_first_player_and_keeps_going()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var factory = world.AddConstruction(Race.Red, at, "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var key = world.Data.FactoryOffer("CIVILIAN", 1).First();
        world.Races[Race.Red].Researched.Add(key);
        world.Races[Race.Red].Bmu = 1000;

        factory.ProductionQueue.Add(key);
        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key) { Issuer = Ben });
        world.Step();
        Assert.Equal([Anna, Anna], Enumerable.Range(0, 2).Select(factory.ProductionOwner));

        Assert.All(Produce(world, factory, 2), m => Assert.Equal(Anna, m.Owner));

        // Empty again, the queue keeps its owners.
        world.Issue(new BuildMachineCommand(Race.Red, factory.Id, key) { Issuer = Ben });
        world.Step();
        Assert.Equal(Ben, Assert.Single(Produce(world, factory, 1)).Owner);
    }

    /// <summary>The factory's next machines, each paid at once, as soon as each is out.</summary>
    private static List<Machine> Produce(GameWorld world, Construction factory, int count)
    {
        var made = new List<Machine>();
        for (var i = 0; i < count; i++)
        {
            var known = world.Machines.Select(m => m.Id).ToHashSet();
            var item = world.Data.ResearchTree.Find(factory.ProductionQueue[0])!;
            factory.ProductionSpent = ResearchTree.FactoryPrice(item, world.Data.Parm);
            Machine? fresh = null;
            Run(world, 30, () => (fresh = world.Machines.FirstOrDefault(m => !known.Contains(m.Id))) is not null);
            made.Add(fresh ?? throw new InvalidOperationException("the factory made nothing"));
        }
        return made;
    }

    // ---- squadrons ------------------------------------------------------------------------------------

    [Fact]
    public void Squadrons_are_each_commander_s_own_and_the_whole_race_s_again_under_the_stand_in_ai()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var level = world.Data.Parm.Machines.First(m => m.Type == "ADMINISTRATOR" && m.SubType == "BOSS").Levels[0].HardwareLevel;
        var herBoss = world.AddMachine(Race.Red, at, "ADMINISTRATOR", "BOSS", level, null, Anna);
        var hers = Grunt(world, at + new Vector2(0, 12), Anna);
        var hisBoss = world.AddMachine(Race.Red, at + new Vector2(30, 0), "ADMINISTRATOR", "BOSS", level, null, Ben);
        var his = Grunt(world, at + new Vector2(30, 12), Ben);

        world.Issue(new FormSquadronCommand(Race.Red, [herBoss.Id, hers.Id, his.Id], 1) { Issuer = Anna });
        world.Issue(new FormSquadronCommand(Race.Red, [hisBoss.Id, his.Id], 1) { Issuer = Ben });
        world.Step();
        Assert.Equal([1, 1, 1, 1], new[] { herBoss, hers, hisBoss, his }.Select(m => m.Squad));
        Assert.Equal((Anna, Ben), (world.SquadScope(Race.Red, Anna), world.SquadScope(Race.Red, Ben)));
        Assert.Equal((herBoss, hisBoss), (world.CommanderFor(hers), world.CommanderFor(his)));
        Assert.Equal((herBoss, hisBoss), (world.CommanderOf(Race.Red, Anna, 1), world.CommanderOf(Race.Red, Ben, 1)));
        // The race's squadron, as the AI commands it, is all of them.
        Assert.Equal(herBoss, world.CommanderOf(Race.Red, 1));

        // Anna's squadron 1 is hers alone.
        world.Issue(new SquadronCommand(Race.Red, 1, new MoveCommand(Race.Red, [], at + new Vector2(0, 60))) { Issuer = Anna });
        world.Step();
        Assert.IsType<MoveOrder>(hers.Order);
        Assert.IsType<IdleOrder>(his.Order);
        Assert.IsType<IdleOrder>(hisBoss.Order);

        // Forming her squadron 1 anew leaves Ben's as it is.
        world.Issue(new FormSquadronCommand(Race.Red, [herBoss.Id], 1) { Issuer = Anna });
        world.Step();
        Assert.Null(hers.Squad);
        Assert.Equal([1, 1, 1], new[] { herBoss, hisBoss, his }.Select(m => m.Squad));

        // Both away, the stand-in AI plays the whole race, and so its squadrons hold every machine of the race.
        world.ChangePresence([Away(Anna), Away(Ben)]);
        Assert.Contains(world.AiControllers, ai => ai.Race == Race.Red);
        Assert.Null(world.SquadScope(Race.Red, Ben));
        Assert.Equal(herBoss, world.CommanderFor(his));
        world.Issue(new SquadronCommand(Race.Red, 1, new MoveCommand(Race.Red, [], at + new Vector2(30, -60))));
        world.Step();
        Assert.IsType<MoveOrder>(his.Order);
        Assert.IsType<MoveOrder>(hisBoss.Order);

        // Back, Ben commands his own again.
        world.ChangePresence([Back(Ben)]);
        Assert.Equal(Ben, world.SquadScope(Race.Red, Ben));
        Assert.Equal(hisBoss, world.CommanderFor(his));
    }

    // ---- handing machines over ------------------------------------------------------------------------

    [Fact]
    public void A_commander_gives_machines_to_a_teammate()
    {
        var world = NewCoop();
        var twin = NewCoop();
        var at = OpenGround(world);
        var (first, second, driven, his) = Four(world, at);
        Four(twin, at);
        // The second has a series: a short way, then a long one.
        var near = at + new Vector2(20, 12);
        var far = at + new Vector2(60, -60);
        foreach (var w in new[] { world, twin })
        {
            w.Find<Machine>(first.Id)!.Squad = 3;
            w.Issue(new SeriesCommand(Race.Red, [second.Id], [new MoveCommand(Race.Red, [], near), new MoveCommand(Race.Red, [], far)]) { Issuer = Anna });
            w.Issue(new DriveCommand(Race.Red, driven.Id, Driving: true) { Issuer = Anna });
            w.Step();
        }
        Assert.Equal((true, (PlayerId?)Anna), (driven.DrivenByPlayer, driven.Driver));
        Assert.Equal(world.Checksum(), twin.Checksum());

        world.Issue(new GiveCommand(Race.Red, [first.Id, second.Id, driven.Id, his.Id, first.Id], Ben) { Issuer = Anna });
        world.Step();
        twin.Step();

        Assert.Empty(world.Rejected);
        Assert.All(new[] { first, second, driven, his }, m => Assert.Equal(Ben, m.Owner));
        Assert.Null(first.Squad);
        Assert.False(driven.DrivenByPlayer);
        Assert.Null(driven.Driver);
        var given = Assert.Single(world.Events, e => e.Kind == GameEventKind.MachinesGiven);
        Assert.Equal((first.Id, "3", (PlayerId?)Ben, (PlayerId?)Anna), (given.Actor, given.Detail, given.Player, given.By));
        // Whose a machine is counts in the checksum once a race is shared.
        Assert.NotEqual(world.Checksum(), twin.Checksum());

        // They obey Ben now, and no longer Anna.
        world.Issue(new MoveCommand(Race.Red, [first.Id], at + new Vector2(-40, 0)) { Issuer = Anna });
        world.Step();
        Assert.IsType<IdleOrder>(first.Order);
        world.Issue(new MoveCommand(Race.Red, [first.Id], at + new Vector2(-40, 0)) { Issuer = Ben });
        world.Step();
        Assert.IsType<MoveOrder>(first.Order);

        // The rest of the series went with the gift: the second stops where its first step ends.
        Run(world, 20, () => second.Order is IdleOrder);
        Run(world, 2);
        Assert.IsType<IdleOrder>(second.Order);
        Assert.True(Vector2.Distance(second.Position, near) < 10, $"{second.Position}");
    }

    [Fact]
    public void The_steps_a_series_keeps_for_later_are_its_giver_s()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var hers = Grunt(world, at, Anna);
        var his = Grunt(world, at + new Vector2(0, 12), Ben);

        // After a short way her machine forms a squadron of hers and his: she may not order his, so it stays out.
        world.Issue(new SeriesCommand(Race.Red, [hers.Id],
            [new MoveCommand(Race.Red, [], at + new Vector2(20, 0)), new FormSquadronCommand(Race.Red, [hers.Id, his.Id], 5)]) { Issuer = Anna });
        Run(world, 20, () => hers.Squad is not null);
        Assert.Equal(5, hers.Squad);
        Assert.Null(his.Squad);

        // Ben, keeping her machines while she is away, gives hers a series; once she is back, the rest of it is passed over.
        world.ChangePresence([Away(Anna)]);
        var near = at + new Vector2(20, -25);
        world.Issue(new SeriesCommand(Race.Red, [hers.Id],
            [new MoveCommand(Race.Red, [], near), new MoveCommand(Race.Red, [], at + new Vector2(-40, 40))]) { Issuer = Ben });
        world.Step();
        Assert.IsType<MoveOrder>(hers.Order);
        world.ChangePresence([Back(Anna)]);
        Run(world, 20, () => hers.Order is IdleOrder);
        Run(world, 2);
        Assert.IsType<IdleOrder>(hers.Order);
        Assert.True(Vector2.Distance(hers.Position, near) < 10, $"{hers.Position}");
    }

    private static (Machine, Machine, Machine, Machine) Four(GameWorld world, Vector2 at) =>
        (Grunt(world, at, Anna), Grunt(world, at + new Vector2(0, 12), Anna), Grunt(world, at + new Vector2(0, 24), Anna),
        Grunt(world, at + new Vector2(0, 36), Ben));

    [Fact]
    public void Machines_go_only_to_a_teammate_who_is_there_and_only_from_who_may_order_them()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var hers = Grunt(world, at, Anna);
        var his = Grunt(world, at + new Vector2(0, 12), Ben);

        string? Refusal(GiveCommand give)
        {
            world.Issue(give);
            world.Step();
            return world.Rejected.SingleOrDefault()?.Reason;
        }

        Assert.Equal("they are yours already", Refusal(new GiveCommand(Race.Red, [hers.Id], Anna) { Issuer = Anna }));
        Assert.Equal("no commander of your race there to take them", Refusal(new GiveCommand(Race.Red, [hers.Id], Cleo) { Issuer = Anna }));
        Assert.Equal("no commander of your race there to take them", Refusal(new GiveCommand(Race.Red, [hers.Id], new PlayerId(9)) { Issuer = Anna }));
        Assert.Equal("none of these are yours to give", Refusal(new GiveCommand(Race.Red, [his.Id], Ben) { Issuer = Anna }));
        world.ChangePresence([Away(Ben)]);
        Assert.Equal("no commander of your race there to take them", Refusal(new GiveCommand(Race.Red, [hers.Id], Ben) { Issuer = Anna }));
        Assert.Equal(Anna, hers.Owner);

        // Ben's machines, kept by Anna while he is away, are hers to order but not to give back to him.
        Assert.True(world.MayOrder(Anna, his));
        world.ChangePresence([Back(Ben)]);
        Assert.Null(Refusal(new GiveCommand(Race.Red, [hers.Id], Ben) { Issuer = Anna }));
        Assert.Equal(Ben, hers.Owner);
    }

    [Fact]
    public void Heroes_are_not_given()
    {
        var wired = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7, new MatchOptions { Wired = new WiredOptions() });
        var battle = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player }, 5,
            new MatchOptions { Battle = new BattleOptions() });
        Run(battle, 30, () => !battle.Battle!.InOpening(battle.Time));

        foreach (var world in new[] { wired, battle })
        {
            var mine = world.Machines.First(m => m.Race == Race.Red);
            world.Issue(new GiveCommand(Race.Red, [mine.Id], Ben));
            world.Step();
            Assert.Equal("heroes are not given", Assert.Single(world.Rejected).Reason);
            Assert.Null(mine.OwnerOverride);
        }
    }

    // ---- leaving and coming back ----------------------------------------------------------------------

    [Fact]
    public void A_leaver_s_machines_are_kept_by_the_teammate_until_they_are_back()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var dozer = world.AddMachine(Race.Red, at, "CONSTRUCTOR", "DOZER", 1, null, Anna);
        var hers = Grunt(world, at + new Vector2(20, 0), Anna);
        var driven = Grunt(world, at + new Vector2(40, 0), Anna);
        world.Issue(new DriveCommand(Race.Red, driven.Id, Driving: true, Forward: 1) { Issuer = Anna });
        world.Step();
        var computers = world.AiControllers.Count;

        world.ChangePresence([Away(Anna)]);

        Assert.Equal(Ben, world.Players.KeeperOf(Anna));
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);
        Assert.Equal(computers, world.AiControllers.Count);
        // Her machine is let go of, or it would drive on with her last input.
        Assert.False(driven.DrivenByPlayer);
        Assert.True(world.MayOrder(Ben, hers) && world.MayOrder(Ben, dozer));

        // Ben orders her machines, and what he has them build is his.
        var (entry, spot) = world.Buildable(Race.Red).Where(e => e.Type != "MINE")
            .SelectMany(e => Enumerable.Range(0, 64).Select(i => (Entry: e, Spot: at + new Vector2(i % 8 * 20 - 70, i / 8 * 20 - 70))))
            .First(p => world.WhyCannotPlace(Race.Red, p.Entry.Type, p.Entry.SubType, p.Entry.HardwareLevel, p.Spot, 0) is null);
        world.Issue(new MoveCommand(Race.Red, [hers.Id], at + new Vector2(20, -60)) { Issuer = Ben });
        world.Issue(new PlaceConstructionCommand(Race.Red, [dozer.Id], entry.Type, entry.SubType, entry.HardwareLevel, spot, 0) { Issuer = Ben });
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.IsType<MoveOrder>(hers.Order);
        var site = world.Constructions.Single(c => c.Race == Race.Red && !c.Complete && Vector2.Distance(c.Position, spot) < 1);
        Assert.Equal(Ben, site.Owner);
        Assert.Equal(site.Id, Assert.IsType<ConstructOrder>(dozer.Order).Construction);
        Assert.Equal((Anna, Anna), (hers.Owner, dozer.Owner));

        // Back, her machines are hers alone again.
        world.ChangePresence([Back(Anna)]);
        Assert.Null(world.Players.KeeperOf(Anna));
        Assert.False(world.MayOrder(Ben, hers));
        world.Issue(new StopCommand(Race.Red, [hers.Id]) { Issuer = Ben });
        world.Step();
        Assert.IsType<MoveOrder>(hers.Order);
    }

    [Fact]
    public void The_computer_plays_the_race_only_once_its_last_commander_is_away()
    {
        var world = NewCoop();
        var owners = world.Machines.Where(m => m.Race == Race.Red).ToDictionary(m => m.Id, m => m.Owner);
        Assert.Contains(Ben, owners.Values);
        Assert.Contains(Race.Red, world.StandIns.Keys);
        Assert.DoesNotContain(world.AiControllers, ai => ai.Race == Race.Red);

        world.ChangePresence([Away(Ben)]);
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);
        Assert.DoesNotContain(world.AiControllers, ai => ai.Race == Race.Red);
        Assert.Equal(Anna, world.Players.KeeperOf(Ben));

        world.ChangePresence([Away(Anna)]);
        Assert.Equal(Controller.Ai, world.Races[Race.Red].Controller);
        Assert.Single(world.AiControllers, ai => ai.Race == Race.Red);
        Assert.Empty(world.Players.Keepers);
        // The machines stay whose they were while the computer plays them.
        Run(world, 20);
        Assert.All(world.Machines.Where(m => owners.ContainsKey(m.Id)), m => Assert.Equal(owners[m.Id], m.Owner));

        // The first back takes the race from the computer, and keeps the machines of the one still away.
        world.ChangePresence([Back(Ben)]);
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);
        Assert.DoesNotContain(world.AiControllers, ai => ai.Race == Race.Red);
        Assert.Equal(Ben, world.Players.KeeperOf(Anna));
        Assert.Null(world.Players.KeeperOf(Ben));
    }

    [Fact]
    public void One_commander_leaving_as_the_other_comes_back_hands_the_race_straight_over()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var hers = Grunt(world, at, Anna);
        world.ChangePresence([Away(Ben)]);
        world.Issue(new DriveCommand(Race.Red, hers.Id, Driving: true) { Issuer = Anna });
        world.Step();
        var computers = world.AiControllers.ToList();

        world.ChangePresence([Away(Anna), Back(Ben)]);

        // No stand-in came and went: the race went from Anna to Ben.
        Assert.Equal(computers, world.AiControllers);
        Assert.Equal(Controller.Player, world.Races[Race.Red].Controller);
        Assert.Equal(Ben, world.Players.KeeperOf(Anna));
        Assert.Null(world.Players.KeeperOf(Ben));
        Assert.False(hers.DrivenByPlayer);
        Assert.True(world.MayOrder(Ben, hers));

        // One change after the other, the computer would have played the race in between.
        var twin = NewCoop();
        twin.ChangePresence([Away(Ben)]);
        twin.ChangePresence([Away(Anna)]);
        Assert.Contains(twin.AiControllers, ai => ai.Race == Race.Red);
        twin.ChangePresence([Back(Ben)]);
        Assert.DoesNotContain(twin.AiControllers, ai => ai.Race == Race.Red);
        Assert.Equal(Ben, twin.Players.KeeperOf(Anna));
    }

    [Fact]
    public void A_machine_has_one_driver()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var hers = Grunt(world, at, Anna);
        world.ChangePresence([Away(Anna)]);

        world.Issue(new DriveCommand(Race.Red, hers.Id, Driving: true, Forward: 1) { Issuer = Ben });
        world.Step();
        Assert.Equal((true, (PlayerId?)Ben), (hers.DrivenByPlayer, hers.Driver));

        // Her own command, made while she is away, takes the wheel from nobody.
        world.Issue(new DriveCommand(Race.Red, hers.Id, Driving: false) { Issuer = Anna });
        world.Step();
        Assert.Equal("someone else drives it", Assert.Single(world.Rejected).Reason);
        Assert.Equal((true, (PlayerId?)Ben), (hers.DrivenByPlayer, hers.Driver));

        // Back, she may order it and he no longer may: his hands are off the wheel, and hers take it.
        world.ChangePresence([Back(Anna)]);
        Assert.False(hers.DrivenByPlayer);
        Assert.Null(hers.Driver);
        world.Issue(new DriveCommand(Race.Red, hers.Id, Driving: true) { Issuer = Anna });
        world.Issue(new DriveCommand(Race.Red, hers.Id, Driving: true, Forward: 1) { Issuer = Ben });
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.Equal((true, (PlayerId?)Anna), (hers.DrivenByPlayer, hers.Driver));
        Assert.Equal(0, world.DriveInput[hers.Id].Forward);
    }

    // ---- after the review of S2 ---------------------------------------------------------------------

    [Fact]
    public void Nobody_may_order_another_race_s_actors()
    {
        var world = NewCoop();
        var hers = world.Machines.First(m => m.Race == Race.Red && m.Owner == Anna);
        var blue = world.Machines.First(m => m.Race == Race.Blue);
        var bluePod = world.Constructions.First(c => c.Race == Race.Blue);
        Assert.False(world.MayOrder(Anna, blue) || world.MayOrder(Ben, blue) || world.MayOrder(Ben, bluePod));
        Assert.False(world.MayOrder(Cleo, hers));
        Assert.True(world.MayOrder(Cleo, blue) && world.MayOrder(Cleo, bluePod) && world.MayOrder(null, blue));

        // In a match of one player per race too: what the game may pick for its player is the race's alone.
        var classic = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        Assert.True(classic.MayOrder(Anna, classic.Machines.First(m => m.Race == Race.Red)));
        Assert.False(classic.MayOrder(Anna, classic.Machines.First(m => m.Race == Race.Blue)));
        Assert.False(classic.MayOrder(Anna, classic.Constructions.First(c => c.Race == Race.Blue)));
    }

    [Fact]
    public void A_squadron_s_attack_is_its_own_commander_s_alone()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var level = world.Data.Parm.Machines.First(m => m.Type == "ADMINISTRATOR" && m.SubType == "BOSS").Levels[0].HardwareLevel;
        var herBoss = world.AddMachine(Race.Red, at, "ADMINISTRATOR", "BOSS", level, null, Anna);
        var hers = Grunt(world, at + new Vector2(0, 12), Anna);
        var hisBoss = world.AddMachine(Race.Red, at + new Vector2(30, 0), "ADMINISTRATOR", "BOSS", level, null, Ben);
        var his = Grunt(world, at + new Vector2(30, 12), Ben);
        // Blue's base, far off: the squadron goes after it.
        var target = world.Constructions.Where(c => c.Race == Race.Blue).MaxBy(c => Vector2.Distance(c.Position, at))!;
        Assert.True(world.AreEnemies(Race.Red, Race.Blue));
        Assert.True(Vector2.Distance(target.Position, at) > 300);

        world.Issue(new FormSquadronCommand(Race.Red, [herBoss.Id, hers.Id], 1) { Issuer = Anna });
        world.Issue(new FormSquadronCommand(Race.Red, [hisBoss.Id, his.Id], 1) { Issuer = Ben });
        world.Step();
        world.Issue(new SquadronCommand(Race.Red, 1, new AttackCommand(Race.Red, [], target.Id)) { Issuer = Anna });
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.IsType<MoveOrder>(hers.Order);
        Assert.IsType<IdleOrder>(his.Order);
        Assert.IsType<IdleOrder>(hisBoss.Order);

        // Her squadron's attack goes on every five seconds, still without his.
        Run(world, 12);
        Assert.IsNotType<IdleOrder>(hers.Order);
        Assert.IsType<IdleOrder>(his.Order);
        Assert.IsType<IdleOrder>(hisBoss.Order);

        // His squadron's attack is his own and leaves hers going.
        world.Issue(new SquadronCommand(Race.Red, 1, new AttackCommand(Race.Red, [], target.Id)) { Issuer = Ben });
        world.Step();
        Assert.IsType<MoveOrder>(his.Order);
        Assert.IsNotType<IdleOrder>(hers.Order);
    }

    /// <summary>
    /// As in a race of one player, where the player's squadron order ends the attack the computer gave that squadron while
    /// it played the race: a commander back from the computer orders her squadron, and its attack, which went for the whole
    /// race's squadron of the number, no longer drags her machines after its target.
    /// </summary>
    [Fact]
    public void A_commander_s_squadron_order_ends_the_attack_the_computer_left_it()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var level = world.Data.Parm.Machines.First(m => m.Type == "ADMINISTRATOR" && m.SubType == "BOSS").Levels[0].HardwareLevel;
        var herBoss = world.AddMachine(Race.Red, at, "ADMINISTRATOR", "BOSS", level, null, Anna);
        var hers = Grunt(world, at + new Vector2(0, 12), Anna);
        // A Blue grunt far off, on open ground, that Cleo will drive further away.
        var far = Enumerable.Range(0, 2500).Select(i => new Vector2(100 + i % 50 * 30, 100 + i / 50 * 30))
            .First(p => Vector2.Distance(p, at) is > 380 and < 450 && world.Nav.IsAreaFree((p - new Vector2(20, 20), p + new Vector2(20, 20))));
        var quarry = world.AddMachine(Race.Blue, far, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        Assert.DoesNotContain(world.Machines, m => m.Squad == 9);
        world.Issue(new FormSquadronCommand(Race.Red, [herBoss.Id, hers.Id], 9) { Issuer = Anna });
        Run(world, 1, () => world.Tick % GameWorld.TicksPerSecond == 1);

        // Both away, the computer sends Red's squadron 9 after the quarry.
        world.ChangePresence([Away(Anna), Away(Ben)]);
        world.Issue(new SquadronCommand(Race.Red, 9, new AttackCommand(Race.Red, [], quarry.Id)));
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.IsType<MoveOrder>(hers.Order);

        // Back, she calls her squadron home, and the quarry comes halfway: far enough off for her to stay put on her own.
        world.ChangePresence([Back(Anna)]);
        world.Issue(new SquadronCommand(Race.Red, 9, new MoveCommand(Race.Red, [], at)) { Issuer = Anna });
        world.Issue(new MoveCommand(Race.Blue, [quarry.Id], at + (far - at) * 0.5f) { Issuer = Cleo });
        world.Step();
        Assert.Empty(world.Rejected);
        Run(world, 40);
        Assert.True(Vector2.Distance(hers.Position, at) < 40, $"her grunt went after the quarry: {hers.Position}, {hers.Order}");
        Assert.IsNotType<MoveOrder>(hers.Order);
    }

    /// <summary>The other way round: an order of the whole race's squadron (the AI's, a script's) ends the attack a commander gave hers.</summary>
    [Fact]
    public void An_order_of_the_race_s_squadron_ends_a_commander_s_attack()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var level = world.Data.Parm.Machines.First(m => m.Type == "ADMINISTRATOR" && m.SubType == "BOSS").Levels[0].HardwareLevel;
        var herBoss = world.AddMachine(Race.Red, at, "ADMINISTRATOR", "BOSS", level, null, Anna);
        var hers = Grunt(world, at + new Vector2(0, 12), Anna);
        var far = Enumerable.Range(0, 2500).Select(i => new Vector2(100 + i % 50 * 30, 100 + i / 50 * 30))
            .First(p => Vector2.Distance(p, at) is > 380 and < 450 && world.Nav.IsAreaFree((p - new Vector2(20, 20), p + new Vector2(20, 20))));
        var quarry = world.AddMachine(Race.Blue, far, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Issue(new FormSquadronCommand(Race.Red, [herBoss.Id, hers.Id], 9) { Issuer = Anna });
        world.Step();

        world.Issue(new SquadronCommand(Race.Red, 9, new AttackCommand(Race.Red, [], quarry.Id)) { Issuer = Anna });
        world.Step();
        Assert.IsType<MoveOrder>(hers.Order);

        world.Issue(new SquadronCommand(Race.Red, 9, new MoveCommand(Race.Red, [], at)));
        world.Issue(new MoveCommand(Race.Blue, [quarry.Id], at + (far - at) * 0.5f) { Issuer = Cleo });
        world.Step();
        Assert.Empty(world.Rejected);
        Run(world, 40);
        Assert.True(Vector2.Distance(hers.Position, at) < 40, $"her grunt went after the quarry: {hers.Position}, {hers.Order}");
        Assert.IsNotType<MoveOrder>(hers.Order);
    }

    [Fact]
    public void A_keeper_s_squadrons_leave_the_kept_machines_squadrons_alone()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var hers = Grunt(world, at, Anna);
        var his = Grunt(world, at + new Vector2(0, 12), Ben);
        world.Issue(new FormSquadronCommand(Race.Red, [hers.Id], 2) { Issuer = Anna });
        world.Step();
        world.ChangePresence([Away(Anna)]);

        // Ben may order her machine, but his squadrons are of his own machines.
        world.Issue(new FormSquadronCommand(Race.Red, [hers.Id, his.Id], 3) { Issuer = Ben });
        world.Step();
        Assert.Equal((2, 3), (hers.Squad, his.Squad));
        world.Issue(new FormSquadronCommand(Race.Red, [hers.Id, his.Id], 2) { Issuer = Ben });
        world.Step();
        Assert.Equal((2, 2), (hers.Squad, his.Squad));
        world.Issue(new SquadronCommand(Race.Red, 2, new MoveCommand(Race.Red, [], at + new Vector2(0, 60))) { Issuer = Ben });
        world.Step();
        Assert.IsType<MoveOrder>(his.Order);
        Assert.IsType<IdleOrder>(hers.Order);
        // He orders her machine by itself.
        world.Issue(new MoveCommand(Race.Red, [hers.Id], at + new Vector2(30, 60)) { Issuer = Ben });
        world.Step();
        Assert.IsType<MoveOrder>(hers.Order);
    }

    [Fact]
    public void A_squadron_is_given_by_its_commander_alone()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var level = world.Data.Parm.Machines.First(m => m.Type == "ADMINISTRATOR" && m.SubType == "BOSS").Levels[0].HardwareLevel;
        var herBoss = world.AddMachine(Race.Red, at, "ADMINISTRATOR", "BOSS", level, null, Anna);
        var hers = Grunt(world, at + new Vector2(0, 12), Anna);
        var his = Grunt(world, at + new Vector2(30, 12), Ben);
        world.Issue(new FormSquadronCommand(Race.Red, [herBoss.Id, hers.Id], 4) { Issuer = Anna });
        world.Issue(new FormSquadronCommand(Race.Red, [his.Id], 4) { Issuer = Ben });
        world.Step();

        world.Issue(new SquadronCommand(Race.Red, 4, new GiveCommand(Race.Red, [], Ben)) { Issuer = Anna });
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.Equal((Ben, Ben), (herBoss.Owner, hers.Owner));
        Assert.Equal(((int?)null, (int?)null, (int?)4), (herBoss.Squad, hers.Squad, his.Squad));

        // Nor does a squadron of hers hand on what she may not order.
        world.Issue(new SquadronCommand(Race.Red, 4, new GiveCommand(Race.Red, [], Anna)) { Issuer = Anna });
        world.Step();
        Assert.Equal("that squadron has no machines", Assert.Single(world.Rejected).Reason);
        Assert.Equal(Ben, his.Owner);
    }

    [Fact]
    public void A_building_a_commander_s_constructor_captures_is_theirs_and_obeys_the_race()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var garrison = world.AddConstruction(Race.Blue, at, "GARRISON", null, 1, 0, complete: true);
        var dozer = world.AddMachine(Race.Red, at + new Vector2(0, 50), "CONSTRUCTOR", "DOZER", 1, null, Ben);

        world.Issue(new CaptureCommand(Race.Red, [dozer.Id], garrison.Id) { Issuer = Ben });
        Run(world, 600, () => garrison.Race == Race.Red);

        Assert.Equal(Race.Red, garrison.Race);
        Assert.Equal(Ben, garrison.Owner);
        var hers = Grunt(world, at + new Vector2(50, 0), Anna);
        world.Issue(new EnterGarrisonCommand(Race.Red, [hers.Id], garrison.Id) { Issuer = Anna });
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.IsType<EnterGarrisonOrder>(hers.Order);
    }

    [Fact]
    public void Every_wheel_is_let_go_of_as_its_driver_leaves()
    {
        var world = NewCoop();
        var at = OpenGround(world);
        var hers = Grunt(world, at, Anna);
        var his = Grunt(world, at + new Vector2(0, 12), Ben);
        world.Issue(new DriveCommand(Race.Red, hers.Id, Driving: true, Forward: 1) { Issuer = Anna });
        world.Issue(new DriveCommand(Race.Red, his.Id, Driving: true, Forward: 1) { Issuer = Ben });
        world.Step();
        Assert.True(hers.DrivenByPlayer && his.DrivenByPlayer);

        world.ChangePresence([Away(Ben)]);
        Assert.Equal((true, false), (hers.DrivenByPlayer, his.DrivenByPlayer));
        Assert.DoesNotContain(his.Id, world.DriveInput.Keys);

        // Anna keeps his machine and takes its wheel; then she leaves too, and the computer drives nothing of theirs.
        world.Issue(new DriveCommand(Race.Red, his.Id, Driving: true, Forward: 1) { Issuer = Anna });
        world.Step();
        Assert.Equal((PlayerId?)Anna, his.Driver);
        world.ChangePresence([Away(Anna)]);
        Assert.Equal(Controller.Ai, world.Races[Race.Red].Controller);
        Assert.False(hers.DrivenByPlayer || his.DrivenByPlayer);
        Assert.Null(hers.Driver ?? his.Driver);
        Assert.DoesNotContain(world.DriveInput.Keys, id => id == hers.Id || id == his.Id);
    }

    [Fact]
    public void Of_three_commanders_the_lowest_present_keeps_and_hands_on_as_others_come_back()
    {
        var dan = new PlayerId(5);
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", new PlayerTable(
        [
            new PlayerSeat(Anna, Race.Red, PlayerKind.Human, "Anna"),
            new PlayerSeat(Cleo, Race.Blue, PlayerKind.Human, "Cleo"),
            new PlayerSeat(Ben, Race.Red, PlayerKind.Human, "Ben"),
            new PlayerSeat(dan, Race.Red, PlayerKind.Human, "Dan"),
        ]), 7);
        var at = OpenGround(world);
        var his = Grunt(world, at, Ben);

        world.ChangePresence([Away(Anna), Away(Ben)]);
        Assert.Equal((dan, dan), (world.Players.KeeperOf(Anna), world.Players.KeeperOf(Ben)));
        world.Issue(new DriveCommand(Race.Red, his.Id, Driving: true) { Issuer = dan });
        world.Step();
        Assert.Equal((PlayerId?)dan, his.Driver);

        // Anna back: she, the lowest there, keeps Ben's, and Dan's hands are off his wheel.
        world.ChangePresence([Back(Anna)]);
        Assert.Equal((PlayerId?)Anna, world.Players.KeeperOf(Ben));
        Assert.Null(world.Players.KeeperOf(Anna));
        Assert.False(his.DrivenByPlayer);
        Assert.False(world.MayOrder(dan, his));
        Assert.True(world.MayOrder(Anna, his));

        // One leaving and coming back within a tick changes nothing.
        world.Issue(new DriveCommand(Race.Red, his.Id, Driving: true) { Issuer = Anna });
        world.Step();
        var sum = world.Checksum();
        world.ChangePresence([Away(Anna), Back(Anna)]);
        Assert.Equal(sum, world.Checksum());
        Assert.Equal((true, (PlayerId?)Anna), (his.DrivenByPlayer, his.Driver));
        Assert.DoesNotContain(world.AiControllers, ai => ai.Race == Race.Red);
    }

    // ---- determinism ----------------------------------------------------------------------------------

    [Fact]
    public void Twin_co_op_worlds_stay_equal()
    {
        var one = new CoopPlay(NewCoop());
        var two = new CoopPlay(NewCoop());

        for (var tick = 100; tick <= 2400; tick += 100)
            Assert.Equal(one.PlayTo(tick), two.PlayTo(tick));

        // They were commanded by both, kept, played by the computer and handed over on the way.
        Assert.True(one.Given > 0, "nothing was given");
        Assert.Contains(one.World.Machines, m => m.Race == Race.Red && m.Owner == Ben);
        Assert.Contains(one.World.Machines, m => m.Race == Race.Red && m.Owner == Anna);
        Assert.Equal(0, one.Refused.GetValueOrDefault("not your race"));
    }

    /// <summary>
    /// A co-op skirmish played into the world as the lockstep session will once it stamps each turn's player: Anna and Ben
    /// each give Red's orders of an <see cref="OrdersScript"/> of their own, Cleo Blue's; Anna gives Ben half her fighters at
    /// 15 s, leaves at 30 s, Ben at 50 s (the computer takes Red), Anna is back at 70 s, Ben at 90 s.
    /// </summary>
    private sealed class CoopPlay(GameWorld world)
    {
        private readonly (PlayerId Player, GoldenScript Script)[] _players =
            [(Anna, new OrdersScript(Race.Red, 31)), (Cleo, new OrdersScript(Race.Blue, 32)), (Ben, new OrdersScript(Race.Red, 33))];

        public GameWorld World { get; } = world;

        public int Given { get; private set; }

        public Dictionary<string, int> Refused { get; } = [];

        public ulong PlayTo(int tick)
        {
            while (World.Tick < tick)
            {
                switch (World.Tick)
                {
                    case 300:
                    {
                        var fighters = World.Machines.Where(m => m.Race == Race.Red && m.Owner == Anna && m.CanFight && !m.IsDead).OrderBy(m => m.Id).ToList();
                        World.Issue(new GiveCommand(Race.Red, [.. fighters.Take(fighters.Count / 2 + 1).Select(m => m.Id)], Ben) { Issuer = Anna });
                        break;
                    }
                    case 600:
                        World.ChangePresence([Away(Anna)]);
                        break;
                    case 1000:
                        World.ChangePresence([Away(Ben)]);
                        break;
                    case 1400:
                        World.ChangePresence([Back(Anna)]);
                        break;
                    case 1800:
                        World.ChangePresence([Back(Ben)]);
                        break;
                }
                foreach (var (player, script) in _players)
                {
                    if (World.Players.Away(player))
                        continue;
                    foreach (var command in script.Next(World))
                        World.Issue(command with { Issuer = player });
                }
                World.Step();
                Given += World.Events.Count(e => e.Kind == GameEventKind.MachinesGiven);
                foreach (var refused in World.Rejected)
                    Refused[refused.Reason] = Refused.GetValueOrDefault(refused.Reason) + 1;
            }
            return World.Checksum();
        }
    }

    /// <summary>The golden skirmishes, whose players' orders and presence the next slices check machine by machine.</summary>
    public static TheoryData<string> Skirmishes => [GoldenCase.Skirmish.Name, GoldenCase.Orders.Name];

    /// <summary>The other golden matches with players' orders: the four-way skirmish with a leaver, the Wired runs and the commanded battles.</summary>
    public static TheoryData<string> OtherModes =>
    [
        GoldenCase.FourWay.Name, GoldenCase.Wired.Name, GoldenCase.WiredOrders.Name, GoldenCase.Battle.Name, GoldenCase.LeaverBattle.Name,
        GoldenCase.ReturningBattle.Name,
    ];

    /// <summary>
    /// In a race of one player an order given by the player is an order given by the race (P1 design §7.2, I7): each golden
    /// match played straight into two worlds, once with the race's authority and once with each order stamped with its
    /// race's player, as the lockstep session will stamp it, plays to the same checksum every hundred ticks.
    /// </summary>
    [Theory]
    [MemberData(nameof(Skirmishes))]
    [MemberData(nameof(OtherModes))]
    public void An_order_of_a_race_s_only_player_is_the_race_s_order(string name)
    {
        var golden = GoldenCase.All.Single(c => c.Name == name);
        var byRace = (World: Replay.BuildWorld(Data, golden.Match), Scripts: golden.Scripts());
        var byPlayer = (World: Replay.BuildWorld(Data, golden.Match), Scripts: golden.Scripts());
        var issued = 0;

        for (var tick = 0; tick < 3000; tick++)
        {
            foreach (var notice in golden.Notices)
            {
                foreach (var world in new[] { byRace.World, byPlayer.World })
                {
                    if (notice is PlayerLeft { FromTick: var left, Player: var leaver } && left == tick)
                        world.HandOverToAi(leaver);
                    if (notice is PlayerReturned { FromTick: var back, Player: var returner } && back == tick)
                        world.HandBackFromAi(returner);
                }
            }
            foreach (var (world, scripts, stamped) in new[] { (byRace.World, byRace.Scripts, false), (byPlayer.World, byPlayer.Scripts, true) })
            {
                foreach (var script in scripts.Where(s => !world.Players.Away(PlayerId.Of(s.Race))))
                {
                    foreach (var command in script.Next(world))
                    {
                        world.Issue(stamped ? command with { Issuer = PlayerId.Of(command.Race) } : command);
                        issued += stamped ? 1 : 0;
                    }
                }
            }
            byRace.World.Step();
            byPlayer.World.Step();
            Assert.Equal(byRace.World.Rejected.Select(r => r.Reason), byPlayer.World.Rejected.Select(r => r.Reason));
            if (tick % 100 == 99)
                Assert.Equal(byRace.World.Checksum(), byPlayer.World.Checksum());
        }
        Assert.True(issued > 100, $"only {issued} orders");
        Assert.False(byPlayer.World.Players.AnyShared);
        Assert.DoesNotContain(byPlayer.World.Actors, a => a.OwnerOverride is not null);
    }
}
