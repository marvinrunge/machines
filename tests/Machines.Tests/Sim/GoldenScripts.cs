using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// One golden case of <see cref="GoldenSumTests"/> (P1 design §7.1): a match shaped as the relay starts it, the scripts of
/// its human players, who leaves and comes back when, and the ticks its sums are taken at. Its seeds, rows, scripts and
/// ticks stay as they are, so its sums stay comparable.
/// </summary>
internal sealed record GoldenCase(string Name, MatchInfo Match, int[] Ticks, Func<GoldenScript[]> Scripts, params NetMessage[] Notices)
{
    /// <summary>The relay's input delay (<c>RelayServer.InputDelay</c>) when the sums were taken.</summary>
    public const int InputDelay = 4;

    private static readonly string[] Names = ["Anna", "Ben", "Cleo", "Dan"];

    /// <summary>G1: two commanders on Canyon Attack, Red and Blue.</summary>
    public static GoldenCase Skirmish => new("G1 skirmish", Relayed("m_desert", "m_desert", new MatchOptions(), 11, [Race.Red, Race.Blue], []),
        [1000, 3000], () => [new SkirmishScript(Race.Red, 101), new SkirmishScript(Race.Blue, 102)]);

    /// <summary>G2: four races on Central Dominance, Red and Yellow commanded (with alliances), Blue and Green the computer's; Yellow leaves and comes back.</summary>
    public static GoldenCase FourWay => new("G2 four-way", Relayed("fortmult", "fortmult", new MatchOptions(), 3, [Race.Red, Race.Yellow], [Race.Blue, Race.Green]),
        [1000, 2000, 4000], () => [new SkirmishScript(Race.Red, 201, alliances: true), new SkirmishScript(Race.Yellow, 202, alliances: true)],
        new PlayerLeft(PlayerId.Of(Race.Yellow), 600), new PlayerReturned(PlayerId.Of(Race.Yellow), 1400));

    /// <summary>G3: a Wired run on Canyon Attack, Red's hero commanded, Blue the computer's.</summary>
    public static GoldenCase Wired => new("G3 Wired", Relayed("m_desert", "m_desert", new MatchOptions { Wired = new WiredOptions() }, 7, [Race.Red], [Race.Blue]),
        [1000, 4000, 8000], () => [new WiredScript(Race.Red, 301)]);

    /// <summary>
    /// G4: a battle on Canyon Attack at tier III with two lives, Red's and Green's heroes commanded, Blue's and Yellow's bots;
    /// its last sum is taken after its end.
    /// </summary>
    public static GoldenCase Battle => new("G4 battle", CommandedBattle(), [400, 2000, 4000, 12000],
        () => [new BattleScript(Race.Red, 401), new BattleScript(Race.Green, 402)]);

    /// <summary>G5: a battle of bots only on Central Dominance at tier V, one life each; it ends before the last sum.</summary>
    public static GoldenCase BotBattle => new("G5 bot battle", RelayedBattle("fortmult", new BattleOptions { Tier = 5 }, 9), [2000, 6000], () => []);

    /// <summary>G6: G4 whose Green player leaves at tick 800, a bot driving their hero from then on; its last sum is taken after its end.</summary>
    public static GoldenCase LeaverBattle => new("G6 battle with a leaver", CommandedBattle(), [2000, 4000, 12000],
        () => [new BattleScript(Race.Red, 401), new BattleScript(Race.Green, 402)], new PlayerLeft(PlayerId.Of(Race.Green), 800));

    /// <summary>
    /// G7: G4 whose Green player leaves at tick 800 and comes back at 1600, the bot letting go of the hero again, and whose
    /// Red player leaves and comes back in the same tick, 2400, as the relay has it when nobody's turn passed the leave
    /// (P1 design §2.8: a race with one player takes both changes one by one, as today); its last sum is taken after its end.
    /// </summary>
    public static GoldenCase ReturningBattle => new("G7 battle whose leavers come back", CommandedBattle(), [2000, 4000, 12000],
        () => [new BattleScript(Race.Red, 701), new BattleScript(Race.Green, 702)],
        new PlayerLeft(PlayerId.Of(Race.Green), 800), new PlayerReturned(PlayerId.Of(Race.Green), 1600),
        new PlayerLeft(PlayerId.Of(Race.Red), 2400), new PlayerReturned(PlayerId.Of(Race.Red), 2400));

    /// <summary>
    /// G8: two commanders on Canyon Attack who give the orders a skirmish has beyond G1's (<see cref="OrdersScript"/>): every
    /// order the next slices check machine by machine; Red leaves and comes back in the same tick, 1200, Blue leaves for good
    /// at 2600.
    /// </summary>
    public static GoldenCase Orders => new("G8 skirmish of many orders", Relayed("m_desert", "m_desert", new MatchOptions(), 17, [Race.Red, Race.Blue], []),
        [1000, 2000, 4000], () => [new OrdersScript(Race.Red, 801), new OrdersScript(Race.Blue, 802)],
        new PlayerLeft(PlayerId.Of(Race.Red), 1200), new PlayerReturned(PlayerId.Of(Race.Red), 1200), new PlayerLeft(PlayerId.Of(Race.Blue), 2600));

    /// <summary>
    /// G9: a Wired run on Canyon Attack whose hero starts as a spy, whose special lays mines, and whose player, beside G3's
    /// shopping and driving, uses the rest of the hangar and the hero's own powers (<see cref="WiredOrdersScript"/>); Blue
    /// the computer's.
    /// </summary>
    public static GoldenCase WiredOrders => new("G9 Wired of many orders",
        Relayed("m_desert", "m_desert", new MatchOptions { Wired = new WiredOptions { Chassis = "spy3" } }, 19, [Race.Red], [Race.Blue]),
        [2000, 6000, 10000], () => [new WiredOrdersScript(Race.Red, 901)]);

    public static IReadOnlyList<GoldenCase> All => [Skirmish, FourWay, Wired, Battle, BotBattle, LeaverBattle, ReturningBattle, Orders, WiredOrders];

    private static MatchInfo CommandedBattle() => RelayedBattle("m_desert", new BattleOptions { Tier = 3, Lives = 2 }, 5, (Race.Red, 3), (Race.Green, 2));

    /// <summary>
    /// A match as the relay starts it (<c>RelayServer.Start</c>): its seats are the slots in race order; the seated humans
    /// come first, in seat order, then an AI row for each seat the computer fills, in seat order; everyone plays in their
    /// slot's colour.
    /// </summary>
    private static MatchInfo Relayed(string scenario, string planet, MatchOptions options, int seed, IEnumerable<Race> humans, IEnumerable<Race> computer) =>
        new(scenario, planet, options, seed, InputDelay,
        [
            .. humans.Order().Select((race, i) => new MatchPlayer(i + 1, Names[i], race, race, Ai: false)),
            .. computer.Order().Select(race => new MatchPlayer(0, "AI", race, race, Ai: true)),
        ]);

    /// <summary>
    /// A battle room as the relay starts it (<c>RelayServer.AsBattle</c>, <c>StartBattle</c>) from the options a pod sends:
    /// the map's planet and teams, the seated players' heroes on the races of their seats, a bot hero composed for every
    /// other race of the teams. Each human brings a hero of the given tier, as a pod sends one.
    /// </summary>
    private static MatchInfo RelayedBattle(string scenario, BattleOptions settings, int seed, params (Race Race, int Tier)[] humans)
    {
        var map = BattleCatalogue.Map(scenario)!;
        var seated = humans.OrderBy(h => h.Race).ToList();
        var heroes = seated.Select((h, i) => BattleBots.Bot(scenario, h.Tier, Race.Red, i) with { Race = h.Race, Name = Names[i], Bot = false }).ToList();
        var battle = BattleBots.Compose(settings with { TeamA = map.TeamA, TeamB = map.TeamB, Heroes = [] }, heroes, map.Scenario);
        var options = new MatchOptions { Victory = VictoryCondition.Battle, FogOfWar = false, Battle = battle };
        var races = map.TeamA.Concat(map.TeamB).ToList();
        return Relayed(scenario, map.Planet, options, seed, seated.Select(h => h.Race), races.Where(r => seated.All(h => h.Race != r)));
    }

    public override string ToString() => Name;
}

/// <summary>
/// A golden case as an online client plays it: one lockstep session over the world its match records (built through
/// <see cref="Replay.BuildWorld"/>, or a world given for the same match); at every tick each present human's turn for
/// the tick <see cref="GoldenCase.InputDelay"/> ahead comes through the wire, its commands made by the player's script
/// from the world as it stands; a leaver's or returner's notice comes when their turn for its tick would.
/// </summary>
internal sealed class GoldenMatch
{
    private readonly GoldenCase _case;
    private readonly GoldenScript[] _scripts;
    private readonly List<PlayerId> _humans;

    public GoldenMatch(GoldenCase golden, GameData data)
        : this(golden, Replay.BuildWorld(data, golden.Match))
    {
    }

    public GoldenMatch(GoldenCase golden, GameWorld world)
    {
        _case = golden;
        _scripts = golden.Scripts();
        _humans = golden.Match.Table().Rows.Where(p => p.Kind == PlayerKind.Human).Select(p => p.Id).ToList();
        Session = new LockstepSession(world, local: null, golden.Match.InputDelay);
    }

    public LockstepSession Session { get; }

    public GameWorld World => Session.World;

    /// <summary>How often the world refused a command, by reason.</summary>
    public SortedDictionary<string, int> Refused { get; } = new(StringComparer.Ordinal);

    /// <summary>The recording a client would save.</summary>
    public Replay Recording => new(_case.Match, Session.Played, World.Tick, Session.Presence);

    /// <summary>Plays on to each tick in turn and takes the world's checksum there.</summary>
    public ulong[] PlayTo(params int[] ticks)
    {
        var sums = new List<ulong>();
        foreach (var tick in ticks)
        {
            while (World.Tick < tick)
            {
                Send();
                Assert.True(Session.TryStep(), $"{_case.Name} stuck at tick {World.Tick}");
                foreach (var refused in World.Rejected)
                    Refused[refused.Reason] = Refused.GetValueOrDefault(refused.Reason) + 1;
            }
            sums.Add(World.Checksum());
        }
        return [.. sums];
    }

    private void Send()
    {
        var due = World.Tick + _case.Match.InputDelay;
        foreach (var notice in _case.Notices.Where(n => n is PlayerLeft { FromTick: var left } && left == due || n is PlayerReturned { FromTick: var back } && back == due))
            Session.Receive(Wire(notice));
        foreach (var player in _humans)
        {
            if (Session.Away(player, due))
                continue;
            var race = World.Players.RaceOf(player);
            var script = _scripts.FirstOrDefault(s => s.Race == race);
            Session.Receive(Wire(new Turn(due, player, script?.Next(World) ?? [])));
        }
    }

    private static NetMessage Wire(NetMessage message) => NetJson.Decode(NetJson.Encode(message));
}

/// <summary>
/// What a player of a golden case does: a script makes the commands of one player's turn from the world as it stands and
/// its own <see cref="System.Random"/>, so the same world always gets the same orders. A script only reads the world;
/// what it makes goes into the player's turn, as a game would send it.
/// </summary>
internal abstract class GoldenScript(Race race, int seed)
{
    public Race Race { get; } = race;

    protected Random Random { get; } = new(seed);

    /// <summary>The commands of the turn the player sends at the world's present tick.</summary>
    public abstract IReadOnlyList<Command> Next(GameWorld world);

    protected T Pick<T>(IReadOnlyList<T> list) => list[Random.Next(list.Count)];

    /// <summary>Up to <paramref name="most"/> of the list, each at most once, in the order drawn.</summary>
    protected List<T> Some<T>(IReadOnlyList<T> list, int most)
    {
        var left = list.ToList();
        var some = new List<T>();
        while (some.Count < most && left.Count > 0)
        {
            var at = Random.Next(left.Count);
            some.Add(left[at]);
            left.RemoveAt(at);
        }
        return some;
    }

    /// <summary>A point within <paramref name="reach"/> of another, kept on the map.</summary>
    protected Vector2 Near(GameWorld world, Vector2 from, int reach)
    {
        var at = from + new Vector2(Random.Next(-reach, reach + 1), Random.Next(-reach, reach + 1));
        return Vector2.Clamp(at, new Vector2(10, 10), new Vector2(world.Planet.Surface.Width - 10, world.Planet.Surface.Depth - 10));
    }
}

/// <summary>
/// A skirmish commander: about once a second it moves a machine other than a builder, sends fighters on an attack move or
/// at an enemy, queues a machine at a factory, staffs a lab or starts research, places a building or goes on with one,
/// forms a squadron or orders one, and with <c>alliances</c> also offers or ends an alliance.
/// </summary>
internal sealed class SkirmishScript(Race race, int seed, bool alliances = false) : GoldenScript(race, seed)
{
    public override IReadOnlyList<Command> Next(GameWorld world)
    {
        // Each race acts on a tick of its own within the second.
        if (world.Tick % GameWorld.TicksPerSecond != 1 + 4 * (int)Race)
            return [];
        var own = world.Machines.Where(m => m.Race == Race && !m.IsDead).OrderBy(m => m.Id).ToList();
        var fighters = own.Where(m => m.CanFight).ToList();
        var buildings = world.Constructions.Where(c => c.Race == Race && !c.IsDead).OrderBy(c => c.Id).ToList();
        var enemies = world.Actors.Where(a => a.Race != Race && !a.IsDead && world.AreEnemies(Race, a.Race)).OrderBy(a => a.Id).ToList();
        switch (Random.Next(alliances ? 10 : 9))
        {
            // Builders are left to their work.
            case 0 or 1 when own.Where(m => m.Spec.Type is not ("CONSTRUCTOR" or "TECHNICIAN")).ToList() is { Count: > 0 } movers:
            {
                var machine = Pick(movers);
                return [new MoveCommand(Race, [machine.Id], Near(world, machine.Position, 200))];
            }
            case 2 when fighters.Count > 0:
            {
                var target = enemies.Count > 0 ? Near(world, Pick(enemies).Position, 30) : Near(world, Pick(fighters).Position, 300);
                return [new AttackMoveCommand(Race, Some(fighters, 4).Select(m => m.Id).ToList(), target)];
            }
            case 3 when fighters.Count > 0 && enemies.Count > 0:
                return [new AttackCommand(Race, Some(fighters, 3).Select(m => m.Id).ToList(), Pick(enemies).Id)];
            case 4:
                return Build(world, own, buildings);
            case 5:
                return Research(world, own, buildings);
            case 6:
                return Place(world, own, buildings);
            case 7 when fighters.Count > 0:
                return [new FormSquadronCommand(Race, Some(fighters, 5).Select(m => m.Id).ToList(), 1 + Random.Next(3))];
            case 8 when fighters.Count > 0:
            {
                var target = enemies.Count > 0 ? Pick(enemies) : null;
                Command order = target is not null && Random.Next(2) == 0
                    ? new AttackCommand(Race, [], target.Id)
                    : new AttackMoveCommand(Race, [], target is not null ? Near(world, target.Position, 30) : Near(world, Pick(fighters).Position, 300));
                return [new SquadronCommand(Race, 1 + Random.Next(3), order)];
            }
            case 9:
            {
                var others = world.Races.Keys.Where(r => r != Race).Order().ToList();
                return others.Count > 0 ? [new AllianceCommand(Race, Pick(others), Random.Next(2) == 0)] : [];
            }
            default:
                return [];
        }
    }

    /// <summary>A machine its factory offers and the race has researched: half the time a technician while the labs are short of them.</summary>
    private List<Command> Build(GameWorld world, List<Machine> own, List<Construction> buildings)
    {
        var factories = buildings.Where(c => c.Complete && c.IsType("FACTORY") && c.ProductionQueue.Count < 3).ToList();
        if (factories.Count == 0)
            return [];
        var factory = Pick(factories);
        var offer = world.Data.FactoryOffer(factory.Spec.SubType!, factory.Stats.HardwareLevel).Where(world.Races[Race].Researched.Contains).ToList();
        if (offer.Count == 0)
            return [];
        var technicians = own.Count(m => m.Spec.Type == "TECHNICIAN") + buildings.Sum(c => c.ProductionQueue.Count(k => k.Type == "TECHNICIAN"));
        var technician = offer.FirstOrDefault(k => k.Type == "TECHNICIAN");
        var key = technician is not null && technicians < buildings.Count(c => c.IsType("HARDWARE_LAB")) && Random.Next(2) == 0 ? technician : Pick(offer);
        return [new BuildMachineCommand(Race, factory.Id, key)];
    }

    /// <summary>An idle technician into a lab short of them, or an item into a staffed lab that can take it.</summary>
    private List<Command> Research(GameWorld world, List<Machine> own, List<Construction> buildings)
    {
        var labs = buildings.Where(c => c.Complete && c.IsType("HARDWARE_LAB")).ToList();
        if (labs.Count == 0)
            return [];
        var idle = own.Where(m => m.Spec.Type == "TECHNICIAN" && m.Inside is null && m.Order is IdleOrder).ToList();
        var wanting = labs.Where(l => l.Technicians.Count < GameWorld.ResearchStations(l)).ToList();
        if (idle.Count > 0 && wanting.Count > 0)
            return [new EnterLabCommand(Race, [Pick(idle).Id], Pick(wanting).Id)];
        var staffed = labs.Where(l => l.Technicians.Count > 0).ToList();
        if (staffed.Count == 0)
            return [];
        var lab = Pick(staffed);
        var queued = labs.SelectMany(l => l.ResearchQueue).ToHashSet();
        var open = world.Data.ResearchTree.Items.Where(i => !queued.Contains(i.Key) && world.WhyCannotResearch(Race, i.Key, lab) is null)
            .Select(i => i.Key).ToList();
        return open.Count > 0 ? [new ResearchCommand(Race, lab.Id, Pick(open))] : [];
    }

    /// <summary>
    /// A building the race can raise, other than a mine, placed near its pod (or a constructor) by its idle constructors:
    /// mostly the first of a civilian lab, a military factory and a military lab the race lacks, else any.
    /// </summary>
    private List<Command> Place(GameWorld world, List<Machine> own, List<Construction> buildings)
    {
        var constructors = own.Where(m => m.Spec.Type == "CONSTRUCTOR" && m.Order is IdleOrder).ToList();
        var entries = world.Buildable(Race).Where(e => e.Type != "MINE").ToList();
        if (constructors.Count == 0 || entries.Count == 0)
            return [];
        // Half the time idle constructors go on with a site left unfinished.
        if (buildings.Where(c => !c.Complete).ToList() is { Count: > 0 } sites && Random.Next(2) == 0)
            return [new ConstructCommand(Race, constructors.Select(m => m.Id).ToList(), Pick(sites).Id)];
        var lacking = entries.FirstOrDefault(e => (e.Type, e.SubType) is ("HARDWARE_LAB", "CIVILIAN") or ("FACTORY", "MILITARY") or ("HARDWARE_LAB", "MILITARY")
            && !buildings.Any(c => c.IsType(e.Type, e.SubType)));
        var entry = lacking is not null && Random.Next(3) != 0 ? lacking : Pick(entries);
        var anchor = buildings.FirstOrDefault(c => c.IsType("POD"))?.Position ?? constructors[0].Position;
        for (var tries = 0; tries < 8; tries++)
        {
            var at = Near(world, anchor, 250);
            var orientation = 90 * Random.Next(4);
            if (world.WhyCannotPlace(Race, entry.Type, entry.SubType, entry.HardwareLevel, at, orientation) is null)
            {
                return [new PlaceConstructionCommand(Race, constructors.Select(m => m.Id).ToList(), entry.Type, entry.SubType,
                    entry.HardwareLevel, at, orientation)];
            }
        }
        return [];
    }
}

/// <summary>
/// A skirmish commander who, beside what a <see cref="SkirmishScript"/> does, once a second gives one of the other orders a
/// skirmish has: takes a machine off a factory's queue or an item off a lab's, stops, follows, patrols, sets the initiative,
/// holds ground, sets a factory's assembly point, gives a series of orders, sends constructors to repair, build on, take
/// down or capture, recycles or blows up a machine now and then, gives a squadron an order of these, and sends carriers,
/// locators, healers, garrisons and APCs to their work when it has them.
/// </summary>
internal sealed class OrdersScript(Race race, int seed) : GoldenScript(race, seed)
{
    private readonly SkirmishScript _commander = new(race, seed + 1000);

    public override IReadOnlyList<Command> Next(GameWorld world) =>
        world.Tick % GameWorld.TicksPerSecond == 3 + 4 * (int)Race ? More(world) : _commander.Next(world);

    private List<int> Ids(IReadOnlyList<Actor> actors, int most) => Some(actors, most).Select(a => a.Id).ToList();

    private List<Command> More(GameWorld world)
    {
        var own = world.Machines.Where(m => m.Race == Race && !m.IsDead).OrderBy(m => m.Id).ToList();
        var movers = own.Where(m => m.Spec.Type is not ("CONSTRUCTOR" or "TECHNICIAN")).ToList();
        var fighters = own.Where(m => m.CanFight).ToList();
        var constructors = own.Where(m => m.Spec.Type == "CONSTRUCTOR").ToList();
        var buildings = world.Constructions.Where(c => c.Race == Race && !c.IsDead).OrderBy(c => c.Id).ToList();
        var factories = buildings.Where(c => c.Complete && c.IsType("FACTORY")).ToList();
        var sites = buildings.Where(c => !c.Complete).ToList();
        switch (Random.Next(20))
        {
            case 0 when factories.Where(f => f.ProductionQueue.Count > 0).ToList() is { Count: > 0 } busy:
            {
                var factory = Pick(busy);
                return [new CancelProductionCommand(Race, factory.Id, Random.Next(factory.ProductionQueue.Count))];
            }
            case 1 when buildings.Where(c => c.ResearchQueue.Count > 0).ToList() is { Count: > 0 } studying:
            {
                var lab = Pick(studying);
                return [new CancelResearchCommand(Race, lab.Id, Random.Next(lab.ResearchQueue.Count))];
            }
            case 2 when movers.Count > 0:
                return [new StopCommand(Race, Ids(movers, 3))];
            case 3 when movers.Count > 1:
                return [new FollowCommand(Race, Ids(movers, 2), Pick(own).Id)];
            case 4 when fighters.Count > 0:
            {
                var from = Pick(fighters).Position;
                return [new PatrolCommand(Race, Ids(fighters, 3), [Near(world, from, 150), Near(world, from, 150), Near(world, from, 150)])];
            }
            case 5 when own.Count > 0:
                return [new DefConCommand(Race, Ids(own, 4), (DefCon)Random.Next(3))];
            case 6 when fighters.Count > 0:
                return [new StandGroundCommand(Race, Ids(fighters, 3))];
            case 7 when factories.Count > 0:
            {
                var factory = Pick(factories);
                return [new AssemblyPointCommand(Race, [factory.Id], Random.Next(3) == 0 ? null : Near(world, factory.Position, 120))];
            }
            case 8 when movers.Count > 0:
            {
                var from = Pick(movers).Position;
                return [new SeriesCommand(Race, Ids(movers, 2),
                    [new MoveCommand(Race, [], Near(world, from, 150)), new AttackMoveCommand(Race, [], Near(world, from, 250)), new DefConCommand(Race, [], DefCon.High)])];
            }
            case 9 when constructors.Count > 0 && buildings.Where(c => c.Complete).ToList() is { Count: > 0 } standing:
            {
                var damaged = standing.Where(c => c.HitPoints < c.MaxHitPoints).ToList();
                return [new RepairCommand(Race, Ids(constructors, 2), Pick(damaged.Count > 0 ? damaged : standing).Id)];
            }
            case 10 when constructors.Count > 0 && sites.Count > 0:
                return [new ConstructCommand(Race, Ids(constructors, 2), Pick(sites).Id)];
            case 11 when constructors.Count > 0 && sites.Count > 0 && Random.Next(2) == 0:
                return [new DeconstructCommand(Race, Ids(constructors, 1), Pick(sites).Id)];
            case 12 when constructors.Count > 1 && Random.Next(2) == 0
                && world.Constructions.Where(c => c.Race != Race && c.Complete && !c.IsDead).OrderBy(c => c.Id).ToList() is { Count: > 0 } theirs:
                return [new CaptureCommand(Race, Ids(constructors, 1), Pick(theirs).Id)];
            case 13 when movers.Count > 3 && Random.Next(3) == 0:
                return [new RecycleCommand(Race, [Pick(movers).Id])];
            case 14 when movers.Count > 2 && Random.Next(2) == 0:
            {
                // Now and then called off at once by a stop in the same turn.
                var doomed = Pick(movers).Id;
                return Random.Next(2) == 0 ? [new SelfDestructCommand(Race, [doomed])] : [new SelfDestructCommand(Race, [doomed]), new StopCommand(Race, [doomed])];
            }
            case 15 when fighters.Count > 0:
            {
                Command order = Random.Next(3) switch
                {
                    0 => new StopCommand(Race, []),
                    1 => new DefConCommand(Race, [], (DefCon)Random.Next(3)),
                    _ => new PatrolCommand(Race, [], [Near(world, Pick(fighters).Position, 200), Near(world, Pick(fighters).Position, 200)]),
                };
                return [new SquadronCommand(Race, 1 + Random.Next(3), order)];
            }
            case 16 when own.Where(m => m.IsType("RESOURCE_CARRIER") && !m.IsScavenger).ToList() is { Count: > 0 } carriers
                && world.Constructions.Where(c => c.Race == Race && c.Complete && c.IsType("MINE")).OrderBy(c => c.Id).ToList() is { Count: > 0 } mines:
                return [new TransportCommand(Race, Ids(carriers, 2), Pick(mines).Id, 0)];
            case 17 when own.Where(m => m.IsType("GEO_LOCATOR")).ToList() is { Count: > 0 } locators:
            {
                var locator = Pick(locators);
                return [new LocateCommand(Race, [locator.Id], Near(world, locator.Position, 300))];
            }
            case 18 when own.Where(m => m.Weapons.Any(w => w.IsHealer)).ToList() is { Count: > 0 } healers && own.Count > 1:
                return [new HealCommand(Race, Ids(healers, 1), Pick(own).Id)];
            case 19 when movers.Count > 0:
            {
                if (own.Where(m => m.IsType("APC")).ToList() is { Count: > 0 } apcs)
                {
                    var apc = Pick(apcs);
                    return Random.Next(2) == 0
                        ? [new BoardCommand(Race, Ids(movers, 2), apc.Id)]
                        : [new DeployCommand(Race, [apc.Id], Near(world, apc.Position, 200))];
                }
                var shelters = buildings.Where(c => c.Complete && c.HasInterior).ToList();
                return shelters.Count > 0 ? [new EnterGarrisonCommand(Race, Ids(movers, 2), Pick(shelters).Id)] : [];
            }
            default:
                return [];
        }
    }
}

/// <summary>
/// The player of a Wired run: between waves it goes to the pod, opens the hangar, buys from the offer, puts what it bought
/// on, buys an escort or mends the pod, closes the hangar and says it is ready; in a wave it drives the hero in first person
/// for a while (forward, turning, aiming at a foe, firing), then lets go and sends it at the wrecks and the foes, as
/// <c>WiredReplayTests.PlayOn</c> does.
/// </summary>
internal sealed class WiredScript(Race race, int seed) : GoldenScript(race, seed)
{
    private bool _driving;

    /// <summary>The next step of the hangar's work in this pause between waves.</summary>
    private int _step;

    public override IReadOnlyList<Command> Next(GameWorld world)
    {
        var run = world.Wired!;
        var state = run.Heroes[Race];
        if (!run.BetweenWaves)
            _step = 0;
        if (world.HeroOf(Race) is not { } hero || world.WiredPod(Race) is not { } pod)
        {
            _driving = false;
            return [];
        }
        if (run.BetweenWaves)
            return _driving ? LetGo(hero) : Shop(world, state, hero, pod);
        // Drive for fifteen seconds, then leave the hero to its orders for fifteen.
        if (world.Tick / 300 % 2 == 0)
            return world.Tick % 10 == 0 ? Drive(world, run, hero) : [];
        if (_driving)
            return LetGo(hero);
        if (world.Tick % 50 != 0)
            return [];
        if (world.Debris.Count > 0)
            return [new MoveCommand(Race, [hero.Id], world.Debris[Random.Next(world.Debris.Count)].Position)];
        if (run.Foes.Count > 0)
            return [new AttackCommand(Race, [hero.Id], Pick(run.Foes).Machine.Id)];
        return [];
    }

    private List<Command> LetGo(Machine hero)
    {
        _driving = false;
        return [new DriveCommand(Race, hero.Id, false)];
    }

    private List<Command> Drive(GameWorld world, WiredRun run, Machine hero)
    {
        _driving = true;
        var foe = run.Foes.Select(f => f.Machine).Where(m => !m.IsDead).MinBy(m => Vector2.DistanceSquared(m.Position, hero.Position));
        var aim = foe?.Position ?? Near(world, hero.Position, 80);
        float? yaw = Random.Next(2) == 0 ? (float)(Random.NextDouble() * 8 - 4) : null;
        return [new DriveCommand(Race, hero.Id, true, Random.Next(3) - 1, Random.Next(3) - 1, aim, Weapons: Random.Next(-1, 4),
            Buttons: Random.Next(2) == 0, Yaw: yaw)];
    }

    /// <summary>
    /// One step of the hangar's work every second and a half, in turn: buy one of the offers the bank pays for (any offer
    /// when it pays for none), put something bought on, buy an escort or mend the pod, and close and be ready.
    /// </summary>
    private List<Command> Shop(GameWorld world, WiredState state, Machine hero, Construction pod)
    {
        if (world.Tick % 30 != 0)
            return [];
        if (hero.DistanceTo(pod) > WiredBalance.PodReach)
            return [new MoveCommand(Race, [hero.Id], pod.EntranceOutside!.Value)];
        if (!state.HangarOpen)
            return [new WiredHangarCommand(Race, true)];
        switch (_step++)
        {
            case 0:
            {
                var offer = state.Offer.OfType<string>().ToList();
                var paid = offer.Where(id => world.Races[Race].Bmu >= state.Priced(WiredCatalogue.Price(id))).ToList();
                return paid.Count > 0 ? [new WiredBuyCommand(Race, Pick(paid))] : offer.Count > 0 ? [new WiredBuyCommand(Race, Pick(offer))] : [];
            }
            case 1:
                return Equip(state);
            case 2:
                return Random.Next(2) == 0
                    ? [new WiredSquadBuyCommand(Race, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))]
                    : [new WiredPodRepairCommand(Race, 20)];
            default:
                return [new WiredHangarCommand(Race, false), new WiredReadyCommand(Race)];
        }
    }

    /// <summary>Something owned and not worn into the first slot of its kind it fits.</summary>
    private List<Command> Equip(WiredState state)
    {
        var loose = state.Items.Where(i => i.Kind is WiredKind.Weapon or WiredKind.Module or WiredKind.Ability && !state.Loadout.Holds(i.Id)).ToList();
        if (loose.Count == 0)
            return [];
        var item = Pick(loose);
        WiredSlot[] slots =
        [
            WiredSlot.Weapon(Mounting.Left), WiredSlot.Weapon(Mounting.Right), WiredSlot.Weapon(Mounting.Top),
            WiredSlot.Module(0), WiredSlot.Module(1), WiredSlot.Ability(0), WiredSlot.Ability(1),
        ];
        var slot = slots.FirstOrDefault(s => state.Loadout.WhyNotFit(item, s) is null);
        return slot is null ? [] : [new WiredEquipCommand(Race, item.Id, slot)];
    }
}

/// <summary>
/// The player of a Wired run who, beside what a <see cref="WiredScript"/> does, uses the rest of the hangar and the hero's
/// own powers every second and a half: in the hangar it rerolls the offer, raises an item by a Mk, sells or takes off
/// something now and then, puts its chips in and changes to another chassis it owns; in a wave it dashes, uses an ability
/// and its chassis' special; whenever a boss left chips to choose from, it takes one now and then.
/// </summary>
internal sealed class WiredOrdersScript(Race race, int seed) : GoldenScript(race, seed)
{
    private readonly WiredScript _player = new(race, seed + 1000);

    public override IReadOnlyList<Command> Next(GameWorld world)
    {
        var made = _player.Next(world);
        if (world.Tick % 30 != 15 || world.HeroOf(Race) is not { } hero)
            return made;
        return [.. made, .. More(world, world.Wired!, world.Wired!.Heroes[Race], hero)];
    }

    private List<Command> More(GameWorld world, WiredRun run, WiredState state, Machine hero)
    {
        if (state.ChipChoice.Count > 0 && Random.Next(3) == 0)
        {
            int? socket = state.HangarOpen && Random.Next(2) == 0 ? Random.Next(WiredBalance.ChipSockets) : null;
            return [new WiredChipChoiceCommand(Race, Random.Next(state.ChipChoice.Count), socket)];
        }
        if (run.BetweenWaves)
        {
            if (!state.HangarOpen)
                return [];
            var loose = state.Items.Where(i => !state.Loadout.Holds(i.Id)).ToList();
            switch (Random.Next(7))
            {
                case 0:
                    return [new WiredRerollCommand(Race)];
                case 1 or 2 when state.Items.Where(i => i.Kind is WiredKind.Weapon or WiredKind.Module).ToList() is { Count: > 0 } raisable:
                    return [new WiredUpgradeCommand(Race, Pick(raisable).Id)];
                case 3 when loose.Where(i => i.Kind is not WiredKind.Chassis).ToList() is { Count: > 0 } spare && Random.Next(2) == 0:
                    return [new WiredSellCommand(Race, Pick(spare).Id)];
                case 4 when state.Loadout.Worn.Where(i => i.Kind is not WiredKind.Chassis).ToList() is { Count: > 1 } worn:
                    return [new WiredUnequipCommand(Race, Pick(worn).Id)];
                case 5:
                {
                    var chips = loose.Where(i => i.Kind == WiredKind.Chip).ToList();
                    return chips.Count > 0
                        ? [new WiredChipCommand(Race, Pick(chips).Id, Random.Next(WiredBalance.ChipSockets))]
                        : [new WiredChipCommand(Race, null, Random.Next(WiredBalance.ChipSockets))];
                }
                // Now and then onto the run's first chassis, and at once back.
                case 6 when loose.Where(i => i.Kind == WiredKind.Chassis).ToList() is { Count: > 0 } bodies
                    && (state.Loadout.Chassis.Catalogue == WiredBalance.StartChassis || Random.Next(4) == 0):
                    return [new WiredChassisCommand(Race, Pick(bodies).Id)];
                default:
                    return [];
            }
        }
        var foe = run.Foes.Select(f => f.Machine).Where(m => !m.IsDead).OrderBy(m => Vector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).FirstOrDefault();
        return Random.Next(4) switch
        {
            0 => [new WiredDashCommand(Race, Random.Next(3) == 0 ? default : Vector2.Normalize(new Vector2(Random.Next(-2, 3), Random.Next(-2, 3)) + new Vector2(0.5f, 0)))],
            1 => [new WiredSpecialCommand(Race, Random.Next(3) != 0)],
            2 => [new WiredAbilityCommand(Race, Random.Next(WiredBalance.AbilitySlots), foe?.Position ?? Near(world, hero.Position, 100))],
            _ => [],
        };
    }
}

/// <summary>
/// A battle hero's player: for twenty seconds it drives the hero in first person, now and then a new drive, mostly forward
/// and turning towards the nearest enemy machine, aimed at it and firing with some of its weapons; then it lets go and for
/// twenty seconds sends the hero at that enemy, onto the other team's pod or on an attack move.
/// </summary>
internal sealed class BattleScript(Race race, int seed) : GoldenScript(race, seed)
{
    private bool _driving;

    public override IReadOnlyList<Command> Next(GameWorld world)
    {
        if (world.HeroOf(Race) is not { } hero)
        {
            _driving = false;
            return [];
        }
        var enemy = world.Machines.Where(m => !m.IsDead && world.AreEnemies(Race, m.Race))
            .OrderBy(m => Vector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).FirstOrDefault();
        var pod = world.Constructions.Where(c => c.IsType("POD") && world.AreEnemies(Race, c.Race)).OrderBy(c => c.Id).FirstOrDefault();
        if (world.Tick / 400 % 2 == 0)
        {
            if (Random.Next(4) != 0)
                return [];
            _driving = true;
            var goal = enemy?.Position ?? pod?.Position ?? Near(world, hero.Position, 200);
            var way = goal - hero.Position;
            var off = MathF.IEEERemainder(DetMath.Atan2(way.Y, way.X) - hero.Facing, 2 * MathF.PI);
            var turn = Random.Next(5) == 0 ? Random.Next(3) - 1 : off > 0.15f ? 1 : off < -0.15f ? -1 : 0;
            float? yaw = Random.Next(2) == 0 ? (float)(Random.NextDouble() * 8 - 4) : null;
            return [new DriveCommand(Race, hero.Id, true, Random.Next(5) == 0 ? -1 : 1, turn, Near(world, goal, 10), Weapons: Random.Next(4),
                Buttons: Random.Next(2) == 0, Yaw: yaw)];
        }
        if (_driving)
        {
            _driving = false;
            return [new DriveCommand(Race, hero.Id, false)];
        }
        if (world.Tick % 40 != 0)
            return [];
        return Random.Next(3) switch
        {
            0 when enemy is not null => [new AttackCommand(Race, [hero.Id], enemy.Id)],
            1 when pod is not null => [new MoveCommand(Race, [hero.Id], Near(world, pod.Position, 20))],
            _ => [new AttackMoveCommand(Race, [hero.Id], enemy is not null ? Near(world, enemy.Position, 20) : Near(world, hero.Position, 200))],
        };
    }
}
