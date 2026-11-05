using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

/// <summary>
/// Plays one race from its original <c>.spl</c> script: squads ask for machines, the factories build them,
/// and each squad carries out its operation (locate, construct, attack, drop mines, patrol).
/// </summary>
/// <remarks>
/// The operations follow the script's parameters. Construction follows MachLogAIController: a prioritised list of
/// the scenario's plans, mines on found sites and destroyed REBUILD buildings. Without the original's free
/// BMUs the AI saves up before building and puts mines first when its bank runs dry. Which carriers serve which
/// mine is a simple rule here. A squad's attack is in AiController.Attack.cs.
/// </remarks>
public sealed partial class AiController(Race race, AiScript script, RaceSetup setup)
{
    private readonly Dictionary<int, SquadState> _squads = script.Squads.ToDictionary(s => s.Number, s => new SquadState(s));

    public Race Race { get; } = race;
    public AiScript Script { get; } = script;

    private sealed class SquadState(AiSquad squad)
    {
        public AiSquad Squad { get; } = squad;
        public int SearchStep { get; set; }

        /// <summary>When its attack or its mining is next looked at (the operation's update intervals).</summary>
        public float NextUpdate { get; set; }

        /// <summary>Set loose on the enemy (RAMPAGING); otherwise it is assembling.</summary>
        public bool Rampaging { get; set; }

        /// <summary>The machines that were there when it was set loose (MachLogMachine::rampaging, which nothing clears).</summary>
        public HashSet<int> Rampagers { get; } = [];

        /// <summary>Which of the race's assembly points it gathers at; one it does not have is replaced once it needs it.</summary>
        public int AssemblyPoint { get; set; } = squad.Operations.OfType<AttackOperation>().FirstOrDefault()?.AssemblyPoint ?? 0;

        /// <summary>The attack's DO: how many more looks at the attack before the squad stops being rebuilt; -1 for no limit.</summary>
        public int DoLeft { get; set; } = squad.Operations.OfType<AttackOperation>().FirstOrDefault()?.DoCount ?? -1;

        /// <summary>The squad wants no more machines (MachLogSquadron::removeAllDesiredMachines): what it loses stays lost.</summary>
        public bool Abandoned { get; set; }
    }

    // How often an easy AI has been asked to think: it does every other time (MatchOptions.AiLevel).
    private long _asked;

    /// <summary>Called once a second.</summary>
    public void Think(GameWorld world)
    {
        if (world.Races[Race].Defeated)
            return;
        if (world.Options.AiLevel == AiLevel.Easy && _asked++ % 2 == 1)
            return;

        var mine = world.Machines.Where(m => m.Race == Race && !m.IsDead).ToList();
        AssignToSquads(mine);
        SetDefCons(mine);
        StaffLabs(world, mine);
        Research(world);
        Garrison(world, mine);
        Produce(world, mine);
        PlanConstructions(world);
        UpdateAllies(world);

        foreach (var state in _squads.Values)
        {
            // Machines inside a building, or on their way into a garrison, sit the squad's orders out.
            var members = mine.Where(m => m.Squad == state.Squad.Number && m.Inside is null && m.Order is not EnterGarrisonOrder).ToList();
            foreach (var operation in state.Squad.Operations)
            {
                switch (operation)
                {
                    case LocateOperation locate: Locate(world, state, locate, members); break;
                    case ConstructOperation: Construct(world, members); break;
                    case AttackOperation attack: Attack(world, state, attack, members); break;
                    case DropMinesOperation drop: DropMines(world, state, drop, members); break;
                    case PatrolOperation patrol: Patrol(world, patrol, members); break;
                }
            }
        }

        Transport(world, mine.Where(m => m.Spec.Type == "RESOURCE_CARRIER" && !m.IsScavenger).ToList());
    }

    // ---- squads and production ------------------------------------------------------------------

    private void AssignToSquads(List<Machine> machines)
    {
        // Each squad's members that fill one of its places, counted once and kept up as machines join.
        var have = _squads.Values.ToDictionary(s => s.Squad.Number,
            s => machines.Count(m => m.Squad == s.Squad.Number && s.Squad.Members.Any(o => Matches(o.Machine, m))));
        foreach (var machine in machines.Where(m => m.Squad is null || !_squads.ContainsKey(m.Squad.Value)))
        {
            foreach (var state in _squads.Values.Where(s => !s.Abandoned))
            {
                var wanted = state.Squad.Members.Where(o => Matches(o.Machine, machine)).Sum(o => o.Count);
                if (wanted > have[state.Squad.Number])
                {
                    machine.Squad = state.Squad.Number;
                    have[state.Squad.Number]++;
                    break;
                }
            }
        }
    }

    /// <summary>A squad's DEFCON line holds for every machine that joins it (MachLogSquadron::autoSetDefCon, addToControl).</summary>
    private void SetDefCons(List<Machine> machines)
    {
        foreach (var state in _squads.Values.Where(s => s.Squad.Defcon is not null))
        {
            var level = state.Squad.Defcon switch { "LOW" => DefCon.Low, "HIGH" => DefCon.High, _ => DefCon.Normal };
            foreach (var machine in machines.Where(m => m.Squad == state.Squad.Number))
                machine.DefCon = level;
        }
    }

    private static bool Matches(ResearchKey wanted, Machine machine) =>
        wanted.Type == machine.Spec.Type && wanted.SubType == machine.Spec.SubType
        && wanted.HardwareLevel == machine.Stats.HardwareLevel && (wanted.WeaponCombo is null || wanted.WeaponCombo == machine.WeaponCombo);

    private void Produce(GameWorld world, List<Machine> machines)
    {
        var state = world.Races[Race];
        var factories = world.Constructions.Where(c => c.Race == Race && c.Complete && c.IsType("FACTORY")).ToList();
        var labs = world.Constructions.Where(c => c.Race == Race && c.Complete && c.IsType("HARDWARE_LAB")).ToList();
        var queued = factories.SelectMany(f => f.ProductionQueue).ToList();

        // A technician per lab first: without them nothing can be researched.
        var technician = new ResearchKey("TECHNICIAN", "LAB_TECH", 2, null);
        var wants = new List<ResearchKey>();
        var technicians = machines.Count(m => m.Spec.Type == "TECHNICIAN") + queued.Count(k => k.Type == "TECHNICIAN");
        for (var i = technicians; i < labs.Count; i++)
            wants.Add(technician);

        // Each squad sees to its own places (MachLogSquadron::update); what is on order goes to the first that needs it.
        var onOrder = queued.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        foreach (var squad in _squads.Values.Where(s => !s.Abandoned).OrderBy(s => s.Squad.Number))
        {
            foreach (var places in squad.Squad.Members.OrderBy(o => o.Priority).GroupBy(o => o.Machine))
            {
                var have = machines.Count(m => m.Squad == squad.Squad.Number && Matches(places.Key, m));
                for (var i = have; i < places.Sum(o => o.Count); i++)
                {
                    if (onOrder.GetValueOrDefault(places.Key) > 0)
                        onOrder[places.Key]--;
                    else
                        wants.Add(places.Key);
                }
            }
        }

        foreach (var key in wants.Distinct())
        {
            if (!state.Researched.Contains(key))
                continue;

            var factory = factories
                .Where(f => f.ProductionQueue.Count < 2 && world.Data.FactoryOffer(f.Spec.SubType!, f.Stats.HardwareLevel).Contains(key))
                .MinBy(f => f.ProductionQueue.Count);
            if (factory is not null)
            {
                world.Issue(new BuildMachineCommand(Race, factory.Id, key));
                queued.Add(key);
            }
        }
    }

    /// <summary>
    /// Every lab with a technician in it takes on all that is open to it, whatever the squads want
    /// (MachLogAIController::handleIdleTechnician, MachLogHardwareLab::update). The remake's labs do not share an
    /// item, so each goes to the lab with the least to do.
    /// </summary>
    private void Research(GameWorld world)
    {
        var state = world.Races[Race];
        var labs = world.Constructions.Where(c => c.Race == Race && c.Complete && c.IsType("HARDWARE_LAB") && c.Technicians.Count > 0).ToList();
        if (labs.Count == 0)
            return;
        var load = labs.ToDictionary(l => l.Id, l => l.ResearchQueue.Count);
        var queued = labs.SelectMany(l => l.ResearchQueue).ToHashSet();
        foreach (var item in world.Data.ResearchTree.Items.Where(i => state.Available.Contains(i.Key) && !queued.Contains(i.Key)))
        {
            var lab = labs.Where(l => world.WhyCannotResearch(Race, item.Key, l) is null).MinBy(l => load[l.Id]);
            if (lab is null)
                continue;
            world.Issue(new ResearchCommand(Race, lab.Id, item.Key));
            load[lab.Id]++;
        }
    }

    private void StaffLabs(GameWorld world, List<Machine> machines)
    {
        var labs = world.Constructions.Where(c => c.Race == Race && c.Complete && c.IsType("HARDWARE_LAB")).ToList();
        foreach (var technician in machines.Where(m => m.Spec.Type == "TECHNICIAN" && m.Inside is null && m.Order is IdleOrder))
        {
            var lab = labs.Where(l => l.Technicians.Count < GameWorld.ResearchStations(l))
                .MinBy(l => Vector2.Distance(l.Position, technician.Position));
            if (lab is not null)
                world.Issue(new EnterLabCommand(Race, [technician.Id], lab.Id));
        }
    }

    /// <summary>
    /// MachLogEvadeOperation, as far as garrisons go: a machine badly hurt with an enemy in scanner range runs to
    /// the nearest garrison of its race with a free bay it fits through, if its evasion plan (EPP) considers
    /// garrisons at all; inside it is repaired, and once whole it comes out again.
    /// </summary>
    private void Garrison(GameWorld world, List<Machine> machines)
    {
        var garrisons = world.Constructions.Where(c => c.Race == Race && c.Complete && c.IsType("GARRISON")).ToList();
        if (garrisons.Count == 0)
            return;
        var bays = garrisons.ToDictionary(g => g.Id, g => GameWorld.GarrisonStations(g) - g.Garrisoned.Count);
        foreach (var machine in machines)
        {
            // A machine evading (GameWorld.UpdateEvasion) goes its own way.
            if (machine.Order is EvadeOrder)
                continue;
            if (machine.Inside is { } inside)
            {
                if (bays.ContainsKey(inside) && machine.HitPoints >= machine.MaxHitPoints)
                    world.Issue(new StopCommand(Race, [machine.Id]));
                continue;
            }
            if (machine.Order is EnterGarrisonOrder || machine.HitPoints * 2 > machine.MaxHitPoints
                || world.Data.Parm.EvasionStrategies.FirstOrDefault(e => e.Name == machine.Stats.Evasion) is not { GarrisonsPriority: >= 0 }
                || world.NearestEnemy(machine, machine.Stats.ScannerRange) is null)
                continue;
            var refuge = garrisons.Where(g => bays[g.Id] > 0 && GameWorld.FitsThrough(machine, g))
                .MinBy(g => Vector2.DistanceSquared(g.Position, machine.Position));
            if (refuge is null)
                continue;
            bays[refuge.Id]--;
            world.Issue(new EnterGarrisonCommand(Race, [machine.Id], refuge.Id));
        }
    }

    // ---- operations -----------------------------------------------------------------------------

    private void Locate(GameWorld world, SquadState state, LocateOperation locate, List<Machine> members)
    {
        var found = world.Sites.Count(s => s.DiscoveredBy.Contains(Race));
        if (found >= locate.DesiredSites)
            return;

        var pod = Pod(world);
        if (pod is null)
            return;

        foreach (var locator in members.Where(m => m.Spec.Type == "GEO_LOCATOR" && m.Order is IdleOrder))
        {
            // Search outward from the pod in a spiral, within RANGE_POD.
            var range = MathF.Min(locate.MaxRangeFromPod, MathF.Max(world.Planet.Surface.Width, world.Planet.Surface.Depth));
            var step = state.SearchStep++;
            var angle = step * 2.4f;
            var radius = MathF.Min(range, 80 + step * 40);
            var target = pod.Position + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * radius;
            target = Vector2.Clamp(target, new Vector2(10, 10), new Vector2(world.Planet.Surface.Width - 10, world.Planet.Surface.Depth - 10));
            world.Issue(new LocateCommand(Race, [locator.Id], target));
        }
    }

    private void Construct(GameWorld world, List<Machine> members)
    {
        var state = world.Races[Race];

        // The 1999 AI could not run dry: it had free BMUs (a cheat the remake leaves out). So an AI with an empty
        // bank takes its constructors off buildings it cannot pay for whenever a mine is waiting to be built,
        // because only mines bring money in.
        var broke = state.Bmu < 1;
        var mineWaiting = Plans(world).Any(p => p.Type == "MINE");
        var idle = members.Where(m => m.Spec.Type == "CONSTRUCTOR" && (m.Order is IdleOrder
            || broke && mineWaiting && m.Order is ConstructOrder work && world.Find<Construction>(work.Construction) is { } site && !site.IsType("MINE")))
            .ToList();
        if (idle.Count == 0)
            return;

        // Help finish what is already started: mines first, anything else only while there is money for it.
        var unfinished = world.Constructions.Where(c => c.Race == Race && !c.Complete && (c.IsType("MINE") || !broke))
            .OrderByDescending(c => c.IsType("MINE"))
            .FirstOrDefault();
        if (unfinished is not null && !(broke && mineWaiting && !unfinished.IsType("MINE")))
        {
            world.Issue(new ConstructCommand(Race, idle.Select(m => m.Id).ToList(), unfinished.Id));
            return;
        }
        if (state.Bmu < GameWorld.MinimumBmuToPlace * 4)
            return;

        // MachLogAIController::getHighestPriorityConstruction: the highest priority entry the race can build and
        // place, the first of equals; it leaves the list once started.
        var buildable = world.Buildable(Race);
        Planned? best = null;
        (Vector2 Position, int Orientation) bestSite = default;
        foreach (var plan in Plans(world))
        {
            if (best is not null && plan.Priority <= best.Priority)
                continue;
            if (!buildable.Any(e => e.Type == plan.Type && e.SubType == plan.SubType && e.HardwareLevel == plan.Level))
                continue;
            if (world.Constructions.Any(c => c.Race == Race && c.IsType(plan.Type, plan.SubType) && plan.Sites.Any(s => Vector2.Distance(c.Position, s.Position) < 1)))
                continue; // already standing there (MachLogRaces::noAlternativeAlreadyBuilt)
            var site = plan.Sites.FirstOrDefault(s => world.WhyCannotPlace(Race, plan.Type, plan.SubType, plan.Level, s.Position, s.Orientation) is null);
            if (site == default)
                continue;
            best = plan;
            bestSite = site;
        }
        if (best is null)
            return;
        var typeName = best.SubType is null ? best.Type : $"{best.Type} {best.SubType}";
        // Save up rather than start something else: a mine once a quarter of it is in the bank, anything
        // else only when it can be paid for in full, so the AI does not strand itself.
        var cost = world.Data.Parm.Construction(typeName).Level(best.Level).Cost;
        if (state.Bmu < (best.Type == "MINE" ? cost / 4f : cost))
            return;
        _plans!.Remove(best);
        if (best.Rebuild)
            _rebuildAt.Add((best.Type, best.SubType, bestSite.Position));
        world.Issue(new PlaceConstructionCommand(Race, idle.Select(m => m.Id).ToList(), best.Type, best.SubType, best.Level, bestSite.Position, bestSite.Orientation));
    }

    // ---- the construction list ------------------------------------------------------------------

    /// <summary>
    /// A construction the AI means to build (MachLogProductionUnit): where it may go (the plan's site, then its
    /// ALTERNATIVE_SITEs), its priority, and whether it is rebuilt when destroyed.
    /// </summary>
    private sealed record Planned(string Type, string? SubType, int Level, IReadOnlyList<(Vector2 Position, int Orientation)> Sites, int Priority, bool Rebuild);

    /// <summary>Mines on found sites come before the scenario's plans (priority 1 against 0).</summary>
    private const int MinePriority = 1;

    private List<Planned>? _plans;
    private readonly Dictionary<int, Planned> _standing = [];
    private readonly List<(string Type, string? SubType, Vector2 Position)> _rebuildAt = [];
    private float _nextSiteCheck;

    /// <summary>The construction list, started from the scenario's NOT_BUILT plans in file order.</summary>
    private List<Planned> Plans(GameWorld world) => _plans ??= world.BuildingPlans[Race]
        .Select(p => new Planned(p.Type, p.SubType, p.HardwareLevel,
            [(new Vector2(p.Position.X, p.Position.Y), p.Orientation), .. p.AlternativeSites.Select(a => (new Vector2(a.Position.X, a.Position.Y), a.Orientation))],
            0, p.Rebuild))
        .ToList();

    /// <summary>
    /// What the 1999 AI added to its list itself (MachLogAIController::update, every 15 seconds): a mine on each
    /// found site with more than 100 ore and none on it yet, of level 3 or 5 when researched, rich enough and
    /// nearly affordable; and every destroyed REBUILD building, again where it stood.
    /// </summary>
    private void PlanConstructions(GameWorld world)
    {
        var plans = Plans(world);

        // Buildings put up from rebuild plans are rebuilt again when lost.
        foreach (var (type, subType, position) in _rebuildAt.ToList())
        {
            var built = world.Constructions.FirstOrDefault(c => c.Race == Race && c.IsType(type, subType) && Vector2.Distance(c.Position, position) < 1);
            if (built is null)
                continue;
            built.Rebuild = true;
            _rebuildAt.Remove((type, subType, position));
        }
        foreach (var building in world.Constructions.Where(c => c.Race == Race && c.Rebuild && !_standing.ContainsKey(c.Id)))
            _standing[building.Id] = new Planned(building.Spec.Type, building.Spec.SubType, building.Stats.HardwareLevel,
                [(building.Position, building.Orientation)], 0, true);
        foreach (var (id, plan) in _standing.ToList())
        {
            if (world.Find(id) is { } still && still.Race == Race)
                continue;
            _standing.Remove(id);
            if (world.Find(id) is null)
                plans.Add(plan); // destroyed (a captured building is not rebuilt)
        }

        if (world.Time < _nextSiteCheck)
            return;
        _nextSiteCheck = world.Time + 15;
        var bmu = world.Races[Race].Bmu;
        var buildable = world.Buildable(Race);
        // Sites it found itself, without a mine of its own yet (MachLogAIController::hasMineOnSite); a site taken by
        // someone else's mine is only skipped when that mine stands in the way.
        foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Contains(Race) && s.Ore > 100
                     && !world.Constructions.Any(c => c.Race == Race && c.MineralSite == s.Id)))
        {
            if (plans.Any(p => p.Type == "MINE" && Vector2.Distance(p.Sites[0].Position, site.Position) < 1))
                continue;
            if (world.WhyCannotPlace(Race, "MINE", null, 1, site.Position, 0) is "obstacles or uneven terrain")
                continue; // occupied
            var level = 1;
            foreach (var candidate in new[] { 3, 5 })
            {
                var cost = world.Data.Parm.Construction("MINE").Level(candidate).Cost;
                if (buildable.Any(e => e.Type == "MINE" && e.HardwareLevel == candidate) && site.Ore >= cost * 3 && bmu > cost * 0.8f)
                    level = candidate;
            }
            plans.Add(new Planned("MINE", null, level, [(site.Position, 0)], MinePriority, false));
        }
    }

    private void Patrol(GameWorld world, PatrolOperation patrol, List<Machine> members)
    {
        if (world.Time < patrol.DelayMinutes * 60 * MatchOptions.AiDelay(world.Options.AiLevel))
            return;
        var idle = members.Where(m => m.Order is IdleOrder).Select(m => m.Id).ToList();
        if (idle.Count > 0)
            world.Issue(new PatrolCommand(Race, idle, patrol.Path));
    }

    /// <summary>
    /// The squad's spies lay out all their mines round the script's point, the pod or themselves, somewhere between
    /// the two ranges from it, and fetch more once they have none (MachLogTaskDropLandMineOperation).
    /// </summary>
    private void DropMines(GameWorld world, SquadState state, DropMinesOperation drop, List<Machine> members)
    {
        if (world.Time < state.NextUpdate)
            return;
        var spies = members.Where(m => m.Spec.Type == "SPY_LOCATOR").ToList();
        state.NextUpdate = world.Time + (spies.Count == 0 ? 20 : 60);
        foreach (var spy in spies.Where(s => s.Order is IdleOrder))
        {
            if (spy.Mines == 0)
            {
                world.Issue(new RefillLandMinesCommand(Race, [spy.Id]));
                continue;
            }
            var start = drop.Start ?? Pod(world)?.Position ?? spy.Position;
            var points = new List<Vector2>();
            for (var i = 0; i < spy.Mines; i++)
            {
                var range = drop.MinRange + (float)world.Random.NextDouble() * (drop.MaxRange - drop.MinRange);
                var x = (float)world.Random.NextDouble() * 2 - 1;
                var y = MathF.Sqrt(1 - x * x) * (world.Random.Next(10) < 5 ? -1 : 1);
                points.Add(world.FreeSpotNear(start + new Vector2(x, y) * range, spy.Stats.HighClearance * 1.5f));
            }
            world.Issue(new DropLandMinesCommand(Race, [spy.Id], points));
        }
    }

    private void Transport(GameWorld world, List<Machine> carriers)
    {
        var mines = world.Constructions.Where(c => c.Race == Race && c.Complete && c.IsType("MINE") && c.MineralSite is not null).ToList();
        var stores = world.Constructions.Where(c => c.Race == Race && c.Complete && c.Spec.Type is "SMELTER" or "POD").ToList();
        if (mines.Count == 0 || stores.Count == 0)
            return;

        var i = 0;
        foreach (var carrier in carriers.Where(c => c.Order is IdleOrder))
        {
            var mine = mines[i++ % mines.Count];
            var store = stores.MinBy(s => Vector2.Distance(s.Position, mine.Position))!;
            world.Issue(new TransportCommand(Race, [carrier.Id], mine.Id, store.Id));
        }
    }

    private Construction? Pod(GameWorld world) =>
        world.Constructions.FirstOrDefault(c => c.Race == Race && c.IsType("POD"));

    // ---- alliances between computers ------------------------------------------------------------

    private const float AllyUpdateInterval = 240;
    private float _nextAllyUpdate = AllyUpdateInterval;

    /// <summary>
    /// In a skirmish or network game, about every four minutes, a computer that is not winning makes up its mind about
    /// the computers after it in the list: never with people, never with the winner, and only with three races or
    /// more left. The further the winner is ahead of the two of them, the likelier they are to stand together until
    /// the next time (MachLogAIController::update).
    /// </summary>
    private void UpdateAllies(GameWorld world)
    {
        if (world.Options.Victory == VictoryCondition.Scripted || world.Time < _nextAllyUpdate || !Stands(world, Race))
            return;
        _nextAllyUpdate += AllyUpdateInterval + world.Random.Next(120) - 60;

        var standing = world.Races.Keys.Order().Where(r => Stands(world, r)).ToList();
        var computers = standing.Where(r => world.Races[r].Controller == Controller.Ai).ToList();
        var highest = standing.Max(r => world.Races[r].Score.Gross);
        var winner = standing.First(r => world.Races[r].Score.Gross == highest);
        if (winner == Race || standing.Count <= 2 || computers.Count <= 1)
            return;
        foreach (var other in computers.Skip(computers.IndexOf(Race) + 1))
        {
            var score = world.Races[other].Score.Gross;
            var allies = false;
            if (score != highest)
            {
                var ours = Math.Max(1, score + world.Races[Race].Score.Gross);
                var twiceWinning = Math.Max(1, highest * 2);
                allies = world.Random.Next(ours + twiceWinning) < twiceWinning;
            }
            world.Issue(new AllianceCommand(Race, other, allies));
            world.Issue(new AllianceCommand(other, Race, allies));
        }
    }

    private static bool Stands(GameWorld world, Race race) => world.Actors.Any(a => a.Race == race && !a.IsDead);
}
