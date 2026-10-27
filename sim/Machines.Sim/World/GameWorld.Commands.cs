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
                foreach (var (machine, destination) in GroupMove.Destinations(Own<Machine>(move.Race, move.Machines).ToList(), move.Target, Nav))
                    Send(machine, new MoveOrder(destination), destination, move.Target);
                return null;

            case AttackMoveCommand attackMove:
                foreach (var (machine, destination) in GroupMove.Destinations(Own<Machine>(attackMove.Race, attackMove.Machines).Where(m => m.CanFight).ToList(), attackMove.Target, Nav))
                    Send(machine, new AttackMoveOrder(destination), destination, attackMove.Target);
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

            case AssemblyPointCommand assembly:
                foreach (var factory in Own<Construction>(assembly.Race, assembly.Factories).Where(c => c.IsType("FACTORY")))
                    factory.AssemblyPoint = assembly.Point;
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
                {
                    var sites = SitesOnTheWay(locator, locate.Target);
                    Send(locator, new LocateOrder(locate.Target, sites), sites.Count > 0 ? Sites[sites[0]].Position : locate.Target);
                }
                return null;

            case TransportCommand transport:
            {
                var mine = Find<Construction>(transport.Mine);
                var smelter = Find<Construction>(transport.Smelter);
                if (mine is null || mine.Race != transport.Race || !mine.IsType("MINE"))
                    return "that is not one of your mines";
                if (smelter is null || smelter.Race != transport.Race || smelter.Spec.Type is not ("SMELTER" or "POD"))
                    return "ore can only go to your smelter or pod";
                // Scavengers gather wreckage, not ore (MachLogResourceCarrier::isNormalResourceCarrier).
                foreach (var carrier in Own<Machine>(transport.Race, transport.Carriers).Where(m => m.Spec.Type == "RESOURCE_CARRIER" && !m.IsScavenger))
                    // A carrier with ore on board drops it first (MachLogResourceCarrier::START_WITH_PUTDOWN).
                    Send(carrier, new TransportOrder(mine.Id, smelter.Id, Delivering: carrier.Ore > 0), carrier.Ore > 0 ? smelter.Position : mine.Position);
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

            case CancelProductionCommand cancel:
            {
                var factory = Find<Construction>(cancel.Factory);
                if (factory is null || factory.Race != cancel.Race || cancel.Index < 0 || cancel.Index >= factory.ProductionQueue.Count)
                    return "nothing to cancel there";
                factory.ProductionQueue.RemoveAt(cancel.Index);
                if (cancel.Index == 0)
                {
                    AddBmu(cancel.Race, factory.ProductionSpent);
                    factory.ProductionSpent = 0;
                }
                return null;
            }

            case CancelResearchCommand cancel:
            {
                var lab = Find<Construction>(cancel.Lab);
                if (lab is null || lab.Race != cancel.Race || cancel.Index < 0 || cancel.Index >= lab.ResearchQueue.Count)
                    return "nothing to cancel there";
                lab.ResearchQueue.RemoveAt(cancel.Index);
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

            case StandGroundCommand stand:
                foreach (var machine in Own<Machine>(stand.Race, stand.Machines))
                    Send(machine, new StandGroundOrder(), null);
                return null;

            case DefConCommand defCon:
                foreach (var machine in Own<Machine>(defCon.Race, defCon.Machines))
                    machine.DefCon = defCon.DefCon;
                return null;

            case DropLandMinesCommand drop:
            {
                if (drop.Points.Count == 0)
                    return "no place for the mines";
                var spies = Own<Machine>(drop.Race, drop.Spies).Where(m => m.IsSpy && m.Mines > 0).ToList();
                if (spies.Count == 0)
                    return "no spy with mines selected";
                foreach (var spy in spies)
                    Send(spy, new DropMinesOrder(drop.Points), drop.Points[0]);
                return null;
            }

            case RefillLandMinesCommand refill:
                foreach (var spy in Own<Machine>(refill.Race, refill.Spies).Where(m => m.IsSpy))
                {
                    var garrison = Constructions.Where(c => c.Race == refill.Race && c.Complete && c.IsType("GARRISON"))
                        .MinBy(c => Vector2.DistanceSquared(c.Position, spy.Position));
                    if (garrison is null)
                        return "no garrison to fetch mines from";
                    Send(spy, new RefillMinesOrder(garrison.Id, spy.Position), ClosestPointOutside(garrison, spy.Position));
                }
                return null;

            case CamouflageCommand camouflage:
                foreach (var spy in Own<Machine>(camouflage.Race, camouflage.Spies).Where(m => m.IsSpy))
                    spy.Camouflaged = !spy.Camouflaged;
                return null;

            case FormSquadronCommand form:
            {
                if (form.Squad is < 0 or > 9)
                    return "squadrons are numbered 0 to 9";
                foreach (var member in Machines.Where(m => m.Race == form.Race && m.Squad == form.Squad))
                    member.Squad = null;
                foreach (var machine in Own<Machine>(form.Race, form.Machines))
                    machine.Squad = form.Squad;
                return null;
            }

            case ScavengeCommand scavenge:
            {
                var wrecks = scavenge.Debris.Where(id => FindDebris(id) is not null).Distinct().ToList();
                if (wrecks.Count == 0)
                    return "no wreckage there";
                var scavengers = Own<Machine>(scavenge.Race, scavenge.Scavengers).Where(m => m.IsScavenger).ToList();
                if (scavengers.Count == 0)
                    return "only scavengers gather wreckage";
                foreach (var scavenger in scavengers)
                    Send(scavenger, new ScavengeOrder(wrecks), FindDebris(wrecks[0])!.Position);
                return null;
            }

            case EnterGarrisonCommand garrison:
            {
                // machine.cpp: a machine fits through a building's door when its access size allows it.
                var building = Find<Construction>(garrison.Garrison);
                return OrderWork(garrison.Race, garrison.Machines, garrison.Garrison, m => building is null || FitsThrough(m, building),
                    "too big for the garrison's doors",
                    t => t is Construction { Complete: true } g && g.IsType("GARRISON") && g.Race == garrison.Race ? null : "that is not one of your garrisons",
                    id => new EnterGarrisonOrder(id));
            }

            case DriveCommand drive:
            {
                if (Own<Machine>(drive.Race, [drive.Machine]).FirstOrDefault() is not { } driven)
                    return "that machine cannot be driven";
                driven.DrivenByPlayer = drive.Driving;
                if (!drive.Driving)
                {
                    DriveInput.Remove(driven.Id);
                    PlayerAims.Remove(driven.Id);
                    PlayerWeapons.Remove(driven.Id);
                    PlayerHeads.Remove(driven.Id);
                    return null;
                }
                DriveInput[driven.Id] = (Math.Clamp(drive.Forward, -1, 1), Math.Clamp(drive.Turn, -1, 1));
                PlayerWeapons[driven.Id] = drive.Weapons;
                PlayerHeads[driven.Id] = drive.Head;
                if (drive.Aim is { } aim)
                    PlayerAims[driven.Id] = aim;
                else
                    PlayerAims.Remove(driven.Id);
                return null;
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
    internal void Send(Machine machine, Order order, Vector2? destination, Vector2? groupTarget = null)
    {
        LeaveGarrison(machine);
        machine.Doorway = null;
        machine.Order = order;
        machine.OrderedAt = Time;
        machine.Path.Clear();
        machine.PathIndex = 0;
        if (destination is { } goal)
            machine.Path.AddRange(groupTarget is { } shared && shared != goal
                ? PathVia(machine, shared, goal)
                : Nav.FindPath(machine.Position, goal, CrossesWater(machine)));
    }

    /// <summary>
    /// A group member's path: the search the group shares towards its target, with the last stretch bent to the
    /// member's own place around it.
    /// </summary>
    private List<Vector2> PathVia(Machine machine, Vector2 shared, Vector2 goal)
    {
        var crossesWater = CrossesWater(machine);
        var path = Nav.FindPath(machine.Position, shared, crossesWater);
        if (path.Count == 0)
            return Nav.FindPath(machine.Position, goal, crossesWater);
        var before = path.Count > 1 ? path[^2] : machine.Position;
        if (Nav.LineOfSight(before, goal, crossesWater))
            path[^1] = goal;
        else
            path.AddRange(Nav.FindPath(path[^1], goal, crossesWater));
        return path;
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
            // Anywhere within MINERAL_DISTANCE of a found site with ore left, whoever found it, mined or not
            // (MachLogMine::validMineSite).
            if (!Sites.Any(s => InMineReach(s, position, MineSiteReachSquared(probe))))
                return "mine too far from a found mineral site";
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
