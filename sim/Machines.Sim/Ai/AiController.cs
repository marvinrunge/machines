using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

/// <summary>
/// Plays one race from its original <c>.spl</c> script: squads ask for machines, the factories build them,
/// and each squad carries out its operation (locate, construct, attack, patrol).
/// </summary>
/// <remarks>
/// The operations follow the script's parameters. Things the 1999 AI did in code that the script does not
/// describe (where to put mines, staffing labs, which carriers serve which mine) use simple rules here.
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
        Produce(world, mine);

        foreach (var state in _squads.Values)
        {
            var members = mine.Where(m => m.Squad == state.Squad.Number && m.Inside is null).ToList();
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
        foreach (var machine in machines.Where(m => m.Squad is null || !_squads.ContainsKey(m.Squad.Value)))
        {
            foreach (var state in _squads.Values)
            {
                var wanted = state.Squad.Members.Where(o => Matches(o.Machine, machine)).Sum(o => o.Count);
                var have = machines.Count(m => m.Squad == state.Squad.Number && state.Squad.Members.Any(o => Matches(o.Machine, m)));
                if (wanted > have)
                {
                    machine.Squad = state.Squad.Number;
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
            var target = pod.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            target = Vector2.Clamp(target, new Vector2(10, 10), new Vector2(world.Planet.Surface.Width - 10, world.Planet.Surface.Depth - 10));
            world.Issue(new LocateCommand(Race, [locator.Id], target));
        }
    }

    private void Construct(GameWorld world, List<Machine> members)
    {
        var idle = members.Where(m => m.Spec.Type == "CONSTRUCTOR" && m.Order is IdleOrder).ToList();
        if (idle.Count == 0)
            return;

        // Help finish anything already started.
        var unfinished = world.Constructions.FirstOrDefault(c => c.Race == Race && !c.Complete);
        if (unfinished is not null)
        {
            world.Issue(new ConstructCommand(Race, idle.Select(m => m.Id).ToList(), unfinished.Id));
            return;
        }

        var state = world.Races[Race];
        if (state.Bmu < GameWorld.MinimumBmuToPlace * 4)
            return;

        // Keep two mines going on the nearest found sites, then follow the scenario's building plans in order.
        var pod = Pod(world);
        var mines = world.Constructions.Count(c => c.Race == Race && c.IsType("MINE"));
        if (mines < 2 && pod is not null)
        {
            var site = world.Sites.Where(s => s.DiscoveredBy.Contains(Race) && s.Mine is null && s.Ore > 0)
                .OrderBy(s => Vector2.Distance(s.Position, pod.Position))
                .FirstOrDefault(s => world.WhyCannotPlace(Race, "MINE", null, 1, s.Position, 0) is null);
            if (site is not null)
            {
                world.Issue(new PlaceConstructionCommand(Race, idle.Select(m => m.Id).ToList(), "MINE", null, 1, site.Position, 0));
                return;
            }
        }

        foreach (var plan in world.BuildingPlans[Race])
        {
            var position = new Vector2(plan.Position.X, plan.Position.Y);
            var built = world.Constructions.Any(c => c.Race == Race && c.TypeName == plan.TypeName
                && c.Stats.HardwareLevel == plan.HardwareLevel && Vector2.Distance(c.Position, position) < 1);
            if (built)
                continue;
            if (world.WhyCannotPlace(Race, plan.Type, plan.SubType, plan.HardwareLevel, position, plan.Orientation) is not null)
                continue;
            if (state.Bmu < world.Data.Parm.Construction(plan.TypeName).Level(plan.HardwareLevel).Cost / 4f)
                return; // save up for the next plan rather than skipping it
            world.Issue(new PlaceConstructionCommand(Race, idle.Select(m => m.Id).ToList(), plan.Type, plan.SubType, plan.HardwareLevel, position, plan.Orientation));
            return;
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
