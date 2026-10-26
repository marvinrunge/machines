using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    /// <summary>Minimum BMUs in the bank to place a construction, as in the original.</summary>
    public const int MinimumBmuToPlace = 5;

    /// <returns>null when applied, otherwise why it was refused.</returns>
    private string? Apply(Command command)
    {
        switch (command)
        {
            case MoveCommand move:
                foreach (var machine in Own<Machine>(move.Race, move.Machines))
                    Send(machine, new MoveOrder(move.Target), move.Target);
                return null;

            case AttackMoveCommand attackMove:
                foreach (var machine in Own<Machine>(attackMove.Race, attackMove.Machines).Where(m => m.CanFight))
                    Send(machine, new AttackMoveOrder(attackMove.Target), attackMove.Target);
                return null;

            case AttackCommand attack:
            {
                var target = Find(attack.Target);
                if (target is null || !AreEnemies(attack.Race, target.Race))
                    return "no enemy there";
                foreach (var machine in Own<Machine>(attack.Race, attack.Machines).Where(m => m.CanFight))
                    Send(machine, new AttackOrder(target.Id), target.Position);
                return null;
            }

            case StopCommand stop:
                foreach (var actor in Own<Actor>(stop.Race, stop.Machines))
                {
                    actor.SelfDestructAt = null;
                    if (actor is Machine machine)
                        Send(machine, Order.Idle, null);
                }
                return null;

            case PatrolCommand patrol:
                if (patrol.Points.Count == 0)
                    return "a patrol needs points";
                foreach (var machine in Own<Machine>(patrol.Race, patrol.Machines))
                    Send(machine, new PatrolOrder(patrol.Points, 0), patrol.Points[0]);
                return null;

            case PlaceConstructionCommand place:
                return Place(place);

            case ConstructCommand construct:
            {
                var site = Find<Construction>(construct.Construction);
                if (site is null || site.Race != construct.Race || site.Complete)
                    return "nothing to build there";
                foreach (var constructor in Own<Machine>(construct.Race, construct.Constructors).Where(m => m.Spec.Type == "CONSTRUCTOR"))
                    Send(constructor, new ConstructOrder(site.Id), site.Position);
                return null;
            }

            case LocateCommand locate:
                foreach (var locator in Own<Machine>(locate.Race, locate.Locators).Where(m => m.Spec.Type == "GEO_LOCATOR"))
                    Send(locator, new LocateOrder(locate.Target), locate.Target);
                return null;

            case TransportCommand transport:
            {
                var mine = Find<Construction>(transport.Mine);
                var smelter = Find<Construction>(transport.Smelter);
                if (mine is null || mine.Race != transport.Race || !mine.IsType("MINE"))
                    return "that is not one of your mines";
                if (smelter is null || smelter.Race != transport.Race || smelter.Spec.Type is not ("SMELTER" or "POD"))
                    return "ore can only go to your smelter or pod";
                foreach (var carrier in Own<Machine>(transport.Race, transport.Carriers).Where(m => m.Spec.Type == "RESOURCE_CARRIER"))
                    Send(carrier, new TransportOrder(mine.Id, smelter.Id), mine.Position);
                return null;
            }

            case EnterLabCommand enter:
            {
                var lab = Find<Construction>(enter.Lab);
                if (lab is null || lab.Race != enter.Race || !lab.IsType("HARDWARE_LAB") || !lab.Complete)
                    return "that is not one of your finished labs";
                foreach (var technician in Own<Machine>(enter.Race, enter.Technicians).Where(m => m.Spec.Type == "TECHNICIAN"))
                    Send(technician, new EnterLabOrder(lab.Id), lab.Position);
                return null;
            }

            case BuildMachineCommand build:
            {
                var factory = Find<Construction>(build.Factory);
                if (factory is null || factory.Race != build.Race || !factory.IsType("FACTORY") || !factory.Complete)
                    return "that is not one of your finished factories";
                if (!Data.FactoryOffer(factory.Spec.SubType!, factory.Stats.HardwareLevel).Contains(build.Machine))
                    return "this factory cannot build that";
                if (!Races[build.Race].Researched.Contains(build.Machine))
                    return "not researched yet";
                factory.ProductionQueue.Add(build.Machine);
                return null;
            }

            case ResearchCommand research:
            {
                var lab = Find<Construction>(research.Lab);
                if (lab is null || lab.Race != research.Race || !lab.IsType("HARDWARE_LAB") || !lab.Complete)
                    return "that is not one of your finished labs";
                var why = WhyCannotResearch(research.Race, research.Item, lab);
                if (why is not null)
                    return why;
                if (Constructions.Any(c => c.Race == research.Race && c.ResearchQueue.Contains(research.Item)))
                    return "already being researched";
                lab.ResearchQueue.Add(research.Item);
                return null;
            }

            case HealCommand heal:
                return OrderWork(heal.Race, heal.Machines, heal.Target, m => m.Weapons.Any(w => w.IsHealer), "no healer selected",
                    t => t is Machine && t.Race == heal.Race ? null : "only your own machines can be healed", id => new HealOrder(id));

            case RepairCommand repair:
                return OrderWork(repair.Race, repair.Constructors, repair.Building, IsConstructor, "no constructor selected",
                    t => t is Construction { Complete: true } && t.Race == repair.Race ? null : "only your own finished buildings can be repaired",
                    id => new RepairOrder(id));

            case CaptureCommand capture:
                return OrderWork(capture.Race, capture.Constructors, capture.Building, IsConstructor, "no constructor selected",
                    t => t is Construction { Complete: true } && AreEnemies(capture.Race, t.Race) ? null : "only finished enemy buildings can be captured",
                    id => new CaptureOrder(id));

            case DeconstructCommand deconstruct:
                return OrderWork(deconstruct.Race, deconstruct.Constructors, deconstruct.Building, IsConstructor, "no constructor selected",
                    t => t is Construction ? null : "only buildings can be deconstructed", id => new DeconstructOrder(id));

            case RecycleCommand recycle:
                return Recycle(recycle);

            case EnterGarrisonCommand garrison:
            {
                // machine.cpp: a machine fits through a building's door when its access size allows it.
                var building = Find<Construction>(garrison.Garrison);
                return OrderWork(garrison.Race, garrison.Machines, garrison.Garrison, m => building is null || FitsThrough(m, building),
                    "too big for the garrison's doors",
                    t => t is Construction { Complete: true } g && g.IsType("GARRISON") && g.Race == garrison.Race ? null : "that is not one of your garrisons",
                    id => new EnterGarrisonOrder(id));
            }

            case BoardCommand board:
                return OrderWork(board.Race, board.Machines, board.Apc, m => !m.IsType("APC"), "nothing that can board",
                    t => t is Machine apc && apc.IsType("APC") && apc.Race == board.Race ? null : "that is not one of your APCs",
                    id => new BoardOrder(id));

            case DeployCommand deploy:
            {
                var apcs = Own<Machine>(deploy.Race, deploy.Apcs).Where(m => m.IsType("APC")).ToList();
                if (apcs.Count == 0)
                    return "no APC selected";
                foreach (var apc in apcs)
                    Send(apc, new DeployOrder(deploy.Target), deploy.Target);
                return null;
            }

            case SelfDestructCommand selfDestruct:
                foreach (var actor in Own<Actor>(selfDestruct.Race, selfDestruct.Actors))
                    actor.SelfDestructAt ??= Time + SelfDestructDelay;
                return null;

            case StrikeCommand strike:
            {
                var launcher = Find<Construction>(strike.Launcher);
                if (launcher is null || launcher.Race != strike.Race || !launcher.Complete)
                    return "that is not one of your finished buildings";
                var weapon = launcher.Weapons.FirstOrDefault(w => w.IsStrike);
                if (weapon is null)
                    return "that building has no ion cannon or nuclear missile";
                if (weapon.Reload > 0)
                    return $"{(weapon.Spec.TypeName == "NUCLEAR_MISSILE" ? "nuclear missile" : "ion cannon")} still charging ({weapon.ChargePercent:F0}%)";
                LaunchStrike(launcher, weapon, strike.Target);
                return null;
            }

            default:
                return $"unknown command {command.GetType().Name}";
        }
    }

    /// <summary>The race's live actors among the ids. Technicians in labs stay put; garrisoned machines come out.</summary>
    private IEnumerable<T> Own<T>(Race race, IEnumerable<int> ids) where T : Actor =>
        ids.Select(Find<T>).OfType<T>().Where(a => a.Race == race && !a.IsDead
            && (a is not Machine { Inside: { } building } || Find<Construction>(building)?.IsType("GARRISON") == true));

    /// <summary>Gives a machine a new order and plans its route.</summary>
    internal void Send(Machine machine, Order order, Vector2? destination)
    {
        LeaveGarrison(machine);
        machine.Order = order;
        machine.Path.Clear();
        machine.PathIndex = 0;
        if (destination is { } goal)
            machine.Path.AddRange(Nav.FindPath(machine.Position, goal, CrossesWater(machine)));
    }

    public static bool CrossesWater(Machine machine) => machine.Stats.Locomotion is Locomotion.Hover or Locomotion.Glider;

    // ---- constructions --------------------------------------------------------------------------

    /// <summary>
    /// The buildings a race's constructors can raise: everything in <c>global.cst</c> up to the most advanced
    /// entry of the best constructor the race owns.
    /// </summary>
    public IReadOnlyList<ConstructionOrderEntry> Buildable(Race race)
    {
        var best = Machines
            .Where(m => m.Race == race && m.Stats.MostAdvancedConstruction is not null)
            .Select(m => Data.ConstructionOrder.IndexOf(m.Stats.MostAdvancedConstruction!))
            .DefaultIfEmpty(-1)
            .Max();
        return Data.ConstructionOrder.Entries.Take(best + 1).ToList();
    }

    /// <returns>null if the building may go there, otherwise why not.</returns>
    public string? WhyCannotPlace(Race race, string type, string? subType, int level, Vector2 position, int orientation)
    {
        if (!Buildable(race).Any(e => e.Type == type && e.SubType == subType && e.HardwareLevel == level))
            return "your constructors cannot build that yet";
        if (Races[race].Bmu < MinimumBmuToPlace)
            return $"{MinimumBmuToPlace} BMUs required to place a construction";

        var spec = Data.Parm.Construction(subType is null ? type : $"{type} {subType}");
        var probe = new Construction(0, race, position, spec, spec.Level(level), orientation);
        if (!Nav.IsAreaFree(probe.Footprint))
            return "obstacles or uneven terrain";

        if (type == "MINE")
        {
            var site = Sites.Where(s => s.DiscoveredBy.Contains(race) && s.Mine is null)
                .FirstOrDefault(s => Vector2.DistanceSquared(s.Position, position) <= (probe.Stats.MineralDistance ?? 900));
            if (site is null)
                return "mine not on a found mineral site";
            if (Constructions.Any(c => c.Spec.Type is "SMELTER" or "POD" && TooClose(c, position)))
                return "mine too close to a smelter or pod";
        }
        if (type is "SMELTER" or "POD" && Constructions.Any(c => c.IsType("MINE") && TooClose(probe, c.Position)))
            return "smelter too close to a mine";
        return null;
    }

    /// <summary>Smelters and pods keep mines at a distance: <c>MINE_DISTANCE</c> (squared) in parmdata.</summary>
    private static bool TooClose(Construction smelter, Vector2 point) =>
        smelter.Stats.MineDistance is { } squared && Vector2.DistanceSquared(smelter.Position, point) < squared;

    private string? Place(PlaceConstructionCommand place)
    {
        var why = WhyCannotPlace(place.Race, place.Type, place.SubType, place.HardwareLevel, place.Position, place.Orientation);
        if (why is not null)
            return why;

        var site = AddConstruction(place.Race, place.Position, place.Type, place.SubType, place.HardwareLevel, place.Orientation, complete: false);
        foreach (var constructor in Own<Machine>(place.Race, place.Constructors).Where(m => m.Spec.Type == "CONSTRUCTOR"))
            Send(constructor, new ConstructOrder(site.Id), site.Position);
        return null;
    }

    // ---- research -------------------------------------------------------------------------------

    /// <returns>null if the lab can research the item now, otherwise why not.</returns>
    public string? WhyCannotResearch(Race race, ResearchKey key, Construction lab)
    {
        var state = Races[race];
        var item = Data.ResearchTree.Find(key);
        if (item is null || !state.Available.Contains(key))
            return "not available in this scenario";
        if (state.Researched.Contains(key))
            return "already researched";
        if (item.Parent is { } parent && !state.Researched.Contains(parent))
            return $"needs {parent} first";
        if ((item.Lab == LabType.Military) != (lab.Spec.SubType == "MILITARY"))
            return item.Lab == LabType.Military ? "needs a military lab" : "needs a civilian lab";
        if (item.TechLevel > HighestTechLevel(race))
            return item.TechLevel switch { 1 => "needs a Technician", 2 => "needs an Engineer", _ => "needs a Brainbox" };
        return null;
    }

    /// <summary>The best technician a race owns: 1 Technician, 2 Engineer, 3 Brainbox (0 for none).</summary>
    public int HighestTechLevel(Race race) => Machines
        .Where(m => m.Race == race && m.Spec.Type == "TECHNICIAN")
        .Select(m => m.Spec.SubType switch { "LAB_TECH" => 1, "TECH_BOY" => 2, "BRAIN_BOX" => 3, _ => 0 })
        .DefaultIfEmpty(0)
        .Max();

    public int ResearchCost(Race race, ResearchItem item) =>
        Races[race].ResearchCostOverrides.TryGetValue(item.Key, out var cost) ? cost : item.ResearchCost;
}
