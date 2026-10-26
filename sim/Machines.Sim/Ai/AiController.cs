using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

/// <summary>
/// Plays one race from its original <c>.spl</c> script: squads ask for machines, the factories build them,
/// and each squad carries out its operation (locate, construct, attack, patrol).
/// </summary>
/// <remarks>
/// The operations follow the script's parameters. Construction follows MachLogAIController: a prioritised list of
/// the scenario's plans, mines on found sites and destroyed REBUILD buildings. Without the original's free
/// BMUs the AI saves up before building and puts mines first when its bank runs dry. Staffing labs and which
/// carriers serve which mine use simple rules here.
/// </remarks>
public sealed class AiController(Race race, AiScript script, RaceSetup setup)
{
    private readonly Dictionary<int, SquadState> _squads = script.Squads.ToDictionary(s => s.Number, s => new SquadState(s));

    public Race Race { get; } = race;
    public AiScript Script { get; } = script;

    private sealed class SquadState(AiSquad squad)
    {
        public AiSquad Squad { get; } = squad;
        public int AttacksDone { get; set; }
        public bool Attacking { get; set; }
        public int SearchStep { get; set; }
    }

    /// <summary>Called once a second.</summary>
    public void Think(GameWorld world)
    {
        if (world.Races[Race].Defeated)
            return;

        _busyLabs.Clear();
        var mine = world.Machines.Where(m => m.Race == Race && !m.IsDead).ToList();
        AssignToSquads(mine);
        StaffLabs(world, mine);
        Garrison(world, mine);
        Produce(world, mine);
        PlanConstructions(world);

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
                    case PatrolOperation patrol: Patrol(world, patrol, members); break;
                }
            }
        }

        Transport(world, mine.Where(m => m.Spec.Type == "RESOURCE_CARRIER").ToList());
    }

    // ---- squads and production ------------------------------------------------------------------

    private void AssignToSquads(List<Machine> machines)
    {
        // Each squad's members that fill one of its places, counted once and kept up as machines join.
        var have = _squads.Values.ToDictionary(s => s.Squad.Number,
            s => machines.Count(m => m.Squad == s.Squad.Number && s.Squad.Members.Any(o => Matches(o.Machine, m))));
        foreach (var machine in machines.Where(m => m.Squad is null || !_squads.ContainsKey(m.Squad.Value)))
        {
            foreach (var state in _squads.Values)
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

        foreach (var squad in _squads.Values.OrderBy(s => s.Squad.Number))
        {
            foreach (var order in squad.Squad.Members.OrderBy(o => o.Priority))
            {
                var have = machines.Count(m => Matches(order.Machine, m)) + queued.Count(k => k == order.Machine);
                var wanted = _squads.Values.Where(s => s.Squad.Number <= squad.Squad.Number)
                    .SelectMany(s => s.Squad.Members).Where(o => o.Machine == order.Machine).Sum(o => o.Count);
                for (var i = have; i < wanted; i++)
                    wants.Add(order.Machine);
            }
        }

        foreach (var key in wants.Distinct())
        {
            if (!state.Researched.Contains(key))
            {
                ResearchTowards(world, key, labs);
                continue;
            }

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

    /// <summary>Researches the item, or the first unresearched step on the way to it.</summary>
    private void ResearchTowards(GameWorld world, ResearchKey key, List<Construction> labs)
    {
        var state = world.Races[Race];
        var item = world.Data.ResearchTree.Find(key);
        while (item?.Parent is { } parent && !state.Researched.Contains(parent))
            item = world.Data.ResearchTree.Find(parent);
        if (item is null || labs.Any(l => l.ResearchQueue.Contains(item.Key)))
            return;

        var lab = labs.FirstOrDefault(l => l.ResearchQueue.Count == 0 && !_busyLabs.Contains(l.Id) && l.Technicians.Count > 0
            && world.WhyCannotResearch(Race, item.Key, l) is null);
        if (lab is not null)
        {
            world.Issue(new ResearchCommand(Race, lab.Id, item.Key));
            _busyLabs.Add(lab.Id);
        }
    }

    /// <summary>Labs given research this second; the command only reaches the lab on the next tick.</summary>
    private readonly HashSet<int> _busyLabs = [];

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
        foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Contains(Race) && s.Ore > 100 && s.Mine is null))
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

    private void Strike(GameWorld world, AttackOperation attack)
    {
        foreach (var launcher in world.Constructions.Where(c => c.Race == Race && c.Complete))
        {
            var weapon = launcher.Weapons.FirstOrDefault(w => w.IsStrike && w.Reload <= 0);
            if (weapon is null || (weapon.Spec.TypeName == "NUCLEAR_MISSILE" ? !attack.UseNuke : !attack.UseIonCannon))
                continue;
            if (MostValuableTarget(world) is { } target)
                world.Issue(new StrikeCommand(Race, launcher.Id, target));
        }
    }

    /// <summary>
    /// Where a strike does the most harm: the enemy actor with the most enemy BMUs within 30 m of it
    /// (a simpler stand-in for MachLogRaces::findMostValuableTarget).
    /// </summary>
    private Vector2? MostValuableTarget(GameWorld world)
    {
        var enemies = world.Actors.Where(a => world.AreEnemies(Race, a.Race) && !a.IsDead).ToList();
        return enemies.Count == 0
            ? null
            : enemies.MaxBy(a => enemies.Where(b => Vector2.DistanceSquared(a.Position, b.Position) <= 900).Sum(b => b.Cost))!.Position;
    }

    private void Attack(GameWorld world, SquadState state, AttackOperation attack, List<Machine> members)
    {
        var fighters = members.Where(m => m.CanFight).ToList();
        if (state.Attacking)
        {
            if (fighters.Count == 0 || fighters.All(m => m.Order is IdleOrder))
            {
                state.Attacking = false;
                state.AttacksDone++;
            }
            return;
        }

        if (attack.DoCount is { } limit && state.AttacksDone >= limit)
            return;
        if (world.Time < attack.DelayMinutes * 60)
        {
            Gather(world, attack, fighters);
            return;
        }

        // optskatt.cpp: squads told to use the ion cannon or nukes fire every charged one at the most
        // valuable target, whether or not the squad itself is ready.
        if (attack.UseIonCannon || attack.UseNuke)
            Strike(world, attack);

        // As in the original (optskatt.cpp), a missing MINIMUM is 0: attack with whoever is there.
        if (fighters.Count < (attack.Minimum ?? 0) || fighters.Count == 0)
        {
            Gather(world, attack, fighters);
            return;
        }

        var centre = fighters.Aggregate(Vector2.Zero, (sum, m) => sum + m.Position) / fighters.Count;
        var target = PickTarget(world, attack, centre);
        if (target is null)
            return;

        world.Issue(new AttackMoveCommand(Race, fighters.Select(m => m.Id).ToList(), target.Position));
        state.Attacking = true;
    }

    private void Gather(GameWorld world, AttackOperation attack, List<Machine> fighters)
    {
        var points = setup.AggressorAssemblyPoints;
        if (points.Count == 0)
            return;
        var point = points[Math.Clamp(attack.AssemblyPoint ?? 0, 0, points.Count - 1)];
        var idle = fighters.Where(m => m.Order is IdleOrder && Vector2.Distance(m.Position, point) > 25).Select(m => m.Id).ToList();
        if (idle.Count > 0)
            world.Issue(new MoveCommand(Race, idle, point));
    }

    private Actor? PickTarget(GameWorld world, AttackOperation attack, Vector2 from)
    {
        IEnumerable<Actor> enemies = world.Actors.Where(a => !a.IsDead && world.AreEnemies(Race, a.Race) && a is not Machine { Inside: not null });
        var preferred = attack.Target switch
        {
            AttackTarget.Resource => enemies.Where(a => a is Construction { Spec.Type: "MINE" or "SMELTER" } or Machine { Spec.Type: "RESOURCE_CARRIER" }),
            AttackTarget.Research => enemies.Where(a => a is Construction { Spec.Type: "HARDWARE_LAB" }),
            AttackTarget.Object => enemies.Where(a => a is Construction c && c.Spec.Type == attack.ObjectType || a is Machine m && m.Spec.Type == attack.ObjectType),
            _ => enemies.Where(a => a is Construction),
        };
        return preferred.MinBy(a => Vector2.Distance(a.Position, from)) ?? enemies.MinBy(a => Vector2.Distance(a.Position, from));
    }

    private void Patrol(GameWorld world, PatrolOperation patrol, List<Machine> members)
    {
        if (world.Time < patrol.DelayMinutes * 60)
            return;
        var idle = members.Where(m => m.Order is IdleOrder).Select(m => m.Id).ToList();
        if (idle.Count > 0)
            world.Issue(new PatrolCommand(Race, idle, patrol.Path));
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
}
