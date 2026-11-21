using System.Globalization;
using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    /// <summary>Minimum BMUs in the bank to place a construction, as in the original.</summary>
    public const int MinimumBmuToPlace = 5;

    /// <returns>null when applied, otherwise why it was refused.</returns>
    private string? Apply(Command command)
    {
        // A player speaks only for their own race (the lockstep session already drops other turns; this guards commands made in code).
        if (command.Issuer is { } speaker && !Plays(speaker, command.Race))
            return "not your race";
        if (Battle is not null)
        {
            if (WhyNotInBattle(command) is { } refused)
                return refused;
            if (command is DriveCommand held)
                command = HeldInOpening(held);
        }
        if (Story is not null && command is DriveCommand hacking)
            command = HeldWhileHacking(hacking);
        if (Wired is not null && WhyNotInWired(command) is { } barred)
            return barred;
        switch (command)
        {
            case StoryCommand story:
                return ApplyStory(story);

            case MoveCommand move:
            {
                var group = Own<Machine>(move, move.Machines).ToList();
                var formation = GroupMove.Formation(group);
                foreach (var (machine, destination) in GroupMove.Destinations(group, move.Target, Nav))
                {
                    Send(machine, new MoveOrder(destination), destination, move.Target, formation[machine.Id]);
                    if (move.Via is { Count: > 0 } via)
                        RouteThrough(machine, via, destination);
                }
                return null;
            }

            case FollowCommand follow:
            {
                if (Find<Machine>(follow.Target) is not { IsDead: false } leader)
                    return "nothing to follow there";
                foreach (var machine in Own<Machine>(follow, follow.Machines).Where(m => m.Id != leader.Id))
                    Send(machine, new FollowOrder(leader.Id), leader.Position);
                return null;
            }

            case SeriesCommand series:
                return ApplySeries(series);

            case AttackMoveCommand attackMove:
            {
                var group = Own<Machine>(attackMove, attackMove.Machines).Where(m => m.CanFight).ToList();
                var formation = GroupMove.Formation(group);
                foreach (var (machine, destination) in GroupMove.Destinations(group, attackMove.Target, Nav))
                    Send(machine, new AttackMoveOrder(destination), destination, attackMove.Target, formation[machine.Id]);
                return null;
            }

            case AttackCommand attack:
            {
                var target = FindTarget(attack.Target);
                if (target is null || !CanAttack(attack.Race, target))
                    return "no enemy there";
                foreach (var turret in Own<Construction>(attack, attack.Machines).Where(TakesOrders))
                    _turretOrders[turret.Id] = target.Id;
                var attackers = Own<Machine>(attack, attack.Machines).Where(m => m.CanFight).ToList();
                var places = GroupMove.Formation(attackers);
                foreach (var machine in attackers)
                {
                    Send(machine, new AttackOrder(target.Id), target.Position);
                    if (Options.OriginalMotion && attackers.Count > 1)
                        _formation[machine.Id] = (machine.Order, places[machine.Id].Offset, places[machine.Id].Radius);
                }
                return null;
            }

            case StopCommand stop:
                foreach (var actor in Own<Actor>(stop, stop.Machines))
                {
                    actor.SelfDestructAt = null;
                    _turretOrders.Remove(actor.Id);
                    if (actor is Machine machine)
                        Send(machine, Order.Idle, null);
                }
                return null;

            case AssemblyPointCommand assembly:
                foreach (var factory in Own<Construction>(assembly, assembly.Factories).Where(c => c.IsType("FACTORY")))
                    factory.AssemblyPoint = assembly.Point;
                return null;

            case PatrolCommand patrol:
                if (patrol.Points.Count == 0)
                    return "a patrol needs points";
                foreach (var machine in Own<Machine>(patrol, patrol.Machines))
                    Send(machine, new PatrolOrder(patrol.Points, 0), patrol.Points[0]);
                return null;

            case PlaceConstructionCommand place:
                return Place(place);

            case ConstructCommand construct:
            {
                var site = Find<Construction>(construct.Construction);
                if (site is null || site.Race != construct.Race || site.Complete)
                    return "nothing to build there";
                foreach (var constructor in Own<Machine>(construct, construct.Constructors).Where(m => m.Spec.Type == "CONSTRUCTOR"))
                    Send(constructor, new ConstructOrder(site.Id), site.Position);
                return null;
            }

            case LocateCommand locate:
                foreach (var locator in Own<Machine>(locate, locate.Locators).Where(m => m.Spec.Type == "GEO_LOCATOR"))
                {
                    var sites = SitesOnTheWay(locator, locate.Target);
                    Send(locator, new LocateOrder(locate.Target, sites), sites.Count > 0 ? Sites[sites[0]].Position : locate.Target);
                }
                return null;

            case TransportCommand transport:
            {
                var supplier = Find<Construction>(transport.Mine);
                var smelter = Find<Construction>(transport.Smelter);
                if (supplier is null || !Supplies(supplier, transport.Race))
                    return "ore comes from a mine or another race's smelter";
                if (transport.Smelter != 0 && (smelter is null || smelter.Race != transport.Race || smelter.Spec.Type is not ("SMELTER" or "POD")))
                    return "ore can only go to your smelter or pod";
                var stops = (transport.More ?? []).Prepend(supplier.Id).Distinct()
                    .Where(id => Find<Construction>(id) is { } stop && Supplies(stop, transport.Race)).ToList();
                // The AI's carriers only have suppliers set and find the smelter themselves (assignResourceCarrierTask).
                var named = Races[transport.Race].Controller == Controller.Ai ? null : smelter;
                // Scavengers gather wreckage, not ore (MachLogResourceCarrier::isNormalResourceCarrier).
                foreach (var carrier in Own<Machine>(transport, transport.Carriers).Where(m => m.Spec.Type == "RESOURCE_CARRIER" && !m.IsScavenger))
                    GiveRound(carrier, stops, named, transport.Adding);
                return null;
            }

            case EnterLabCommand enter:
            {
                var lab = Find<Construction>(enter.Lab);
                if (lab is null || !lab.IsType("HARDWARE_LAB") || !lab.Complete)
                    return "that is not a finished lab";
                // Technicians work in their own labs; spies slip into other races' to steal research (MachLogSpyLocator).
                var own = lab.Race == enter.Race;
                var entering = Own<Machine>(enter, enter.Technicians).Where(m => own ? m.Spec.Type == "TECHNICIAN" : m.IsSpy).ToList();
                if (entering.Count == 0)
                    return own ? "only technicians work in labs" : "only spies get into enemy labs";
                foreach (var machine in entering)
                    Send(machine, new EnterLabOrder(lab.Id), lab.Position);
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
                // The factory builds for everyone of its race; the machine is the orderer's.
                Enqueue(factory, build.Machine, build.Issuer ?? PlayerId.Of(build.Race));
                return null;
            }

            case CancelProductionCommand cancel:
            {
                var factory = Find<Construction>(cancel.Factory);
                if (factory is null || factory.Race != cancel.Race || cancel.Index < 0 || cancel.Index >= factory.ProductionQueue.Count)
                    return "nothing to cancel there";
                Dequeue(factory, cancel.Index);
                if (cancel.Index == 0)
                {
                    // Only the machine under way has cost anything; the original pays back three quarters of it
                    // (MachLogFactory::cancelProductionUnit).
                    AddBmu(cancel.Race, factory.ProductionSpent * (Options.OriginalBank ? CancelRefund : 1));
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
                if (lab.ResearchQueue.Count == 1)
                    AnnounceResearch(lab, Data.ResearchTree.Get(research.Item));
                return null;
            }

            case HealCommand heal:
                return OrderWork(heal, heal.Machines, heal.Target, m => m.Weapons.Any(w => w.IsHealer), "no healer selected",
                    t => t is Machine ? null : "only machines can be healed", id => new HealOrder(id));

            case RepairCommand repair:
                return OrderWork(repair, repair.Constructors, repair.Building, IsConstructor, "no constructor selected",
                    t => t is Construction { Complete: true } ? null : "only finished buildings can be repaired",
                    id => new RepairOrder(id));

            case CaptureCommand capture:
                return OrderWork(capture, capture.Constructors, capture.Building, IsConstructor, "no constructor selected",
                    t => t is Construction { Complete: true } && t.Race != capture.Race ? null : "only finished buildings of other races can be captured",
                    id => new CaptureOrder(id));

            case DeconstructCommand deconstruct:
                return OrderWork(deconstruct, deconstruct.Constructors, deconstruct.Building, IsConstructor, "no constructor selected",
                    t => t is Construction ? null : "only buildings can be deconstructed", id => new DeconstructOrder(id));

            case RecycleCommand recycle:
                return Recycle(recycle);

            case StandGroundCommand stand:
                foreach (var machine in Own<Machine>(stand, stand.Machines))
                    Send(machine, new StandGroundOrder(), null);
                return null;

            case DefConCommand defCon:
                foreach (var machine in Own<Machine>(defCon, defCon.Machines))
                    machine.DefCon = defCon.DefCon;
                return null;

            case DropLandMinesCommand drop:
            {
                if (drop.Points.Count == 0)
                    return "no place for the mines";
                var spies = Own<Machine>(drop, drop.Spies).Where(m => m.IsSpy && m.Mines > 0).ToList();
                if (spies.Count == 0)
                    return "no spy with mines selected";
                foreach (var spy in spies)
                    Send(spy, new DropMinesOrder(drop.Points), drop.Points[0]);
                return null;
            }

            case RefillLandMinesCommand refill:
                foreach (var spy in Own<Machine>(refill, refill.Spies).Where(m => m.IsSpy))
                {
                    var garrison = Constructions.Where(c => c.Race == refill.Race && c.Complete && c.IsType("GARRISON"))
                        .MinBy(c => Vector2.DistanceSquared(c.Position, spy.Position));
                    if (garrison is null)
                        return "no garrison to fetch mines from";
                    Send(spy, new RefillMinesOrder(garrison.Id, spy.Position), ClosestPointOutside(garrison, spy.Position));
                }
                return null;

            case CamouflageCommand camouflage:
                foreach (var spy in Own<Machine>(camouflage, camouflage.Spies).Where(m => m.IsSpy))
                    spy.Camouflaged = !spy.Camouflaged;
                return null;

            case SquadronCommand squadron:
                return squadron.Order is SquadronCommand || squadron.Order.Race != squadron.Race
                    ? "not an order for a squadron"
                    : ApplySquadron(squadron);

            case FormSquadronCommand form:
            {
                if (form.Squad is < 0 or > 9)
                    return "squadrons are numbered 0 to 9";
                // In a race of several commanders each has squadrons of their own, of their own machines: what a teammate
                // keeps for someone away keeps its squadron.
                var scope = form.Issuer is { } issuer ? SquadScope(form.Race, issuer) : null;
                foreach (var member in SquadMembers(form.Race, scope, form.Squad))
                    member.Squad = null;
                foreach (var machine in Own<Machine>(form, form.Machines).Where(m => scope is not { } owner || m.Owner == owner))
                    machine.Squad = form.Squad;
                return null;
            }

            case ScavengeCommand scavenge:
            {
                var wrecks = scavenge.Debris.Where(id => FindDebris(id) is not null).Distinct().ToList();
                if (wrecks.Count == 0)
                    return "no wreckage there";
                var scavengers = Own<Machine>(scavenge, scavenge.Scavengers).Where(m => m.IsScavenger).ToList();
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
                return OrderWork(garrison, garrison.Machines, garrison.Garrison, m => building is null || FitsThrough(m, building),
                    "too big for the building's doors",
                    t => t is Construction { Complete: true, HasInterior: true } g && !AreEnemies(garrison.Race, g.Race) ? null : "that is not one of your or your allies' buildings to go into",
                    id => new EnterGarrisonOrder(id));
            }

            case DriveCommand drive:
            {
                // One lost with its last orders still on their way: the view is already leaving it.
                if (Own<Machine>(drive, [drive.Machine]).FirstOrDefault() is not { } driven)
                    return null;
                // In a race of several commanders one machine has one driver: nobody else takes the wheel or lets go of it.
                if (drive.Issuer is { } issuer && Players.Shared(driven.Race) && driven is { DrivenByPlayer: true, Driver: { } driver } && driver != issuer)
                    return "someone else drives it";
                if (!drive.Driving)
                {
                    LetGoOfWheel(driven);
                    return null;
                }
                driven.DrivenByPlayer = true;
                driven.Driver = drive.Issuer ?? PlayerId.Of(drive.Race);
                DriveInput[driven.Id] = (Math.Clamp(drive.Forward, -1, 1), Math.Clamp(drive.Turn, -1, 1),
                    driven.CanStrafe ? Math.Clamp(drive.Strafe, -1, 1) : 0, drive.Slow);
                PlayerWeapons[driven.Id] = drive.Weapons;
                if (drive.Buttons)
                    PlayerButtons.Add(driven.Id);
                else
                    PlayerButtons.Remove(driven.Id);
                PlayerHeads[driven.Id] = drive.Head;
                // Only Wired turns a gun to the view; a yaw that is no number is none.
                if (Wired is not null && drive.Yaw is { } yaw && float.IsFinite(yaw))
                    PlayerYaws[driven.Id] = MathF.IEEERemainder(yaw, 2 * MathF.PI);
                else
                    PlayerYaws.Remove(driven.Id);
                if (drive.Aim is { } aim)
                    PlayerAims[driven.Id] = (aim, drive.AimHeight);
                else
                    PlayerAims.Remove(driven.Id);
                return null;
            }

            case BoardCommand board:
                return OrderWork(board, board.Machines, board.Apc, m => !m.IsType("APC"), "nothing that can board",
                    t => t is Machine apc && apc.IsType("APC") && apc.Race == board.Race ? null : "that is not one of your APCs",
                    id => new BoardOrder(id));

            case DeployCommand deploy:
            {
                var apcs = Own<Machine>(deploy, deploy.Apcs).Where(m => m.IsType("APC")).ToList();
                if (apcs.Count == 0)
                    return "no APC selected";
                foreach (var apc in apcs)
                    Send(apc, new DeployOrder(deploy.Target), deploy.Target);
                return null;
            }

            case SelfDestructCommand selfDestruct:
                foreach (var actor in Own<Actor>(selfDestruct, selfDestruct.Actors))
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
                if (weapon.Spec.TypeName == "NUCLEAR_MISSILE")
                {
                    // A player's launch costs BMUs (MachGuiNukeAttackCommand), the AI's nothing.
                    if (Races[strike.Race].Controller == Controller.Player && launcher.LaunchAt is null)
                    {
                        if (Races[strike.Race].Bmu < NukeLaunchCost)
                        {
                            SendVoiceMail(strike.Race, "VID_POD_INSUFFICIENT_CASH_FOR_NUKE");
                            return $"{NukeLaunchCost} BMUs required to launch";
                        }
                        SpendBmu(strike.Race, NukeLaunchCost);
                    }
                    AimSilo(launcher, weapon, strike.Target);
                }
                else
                    LaunchStrike(launcher, weapon, strike.Target);
                return null;
            }

            case AllianceCommand alliance:
            {
                if (alliance.Target == alliance.Race || !Races.ContainsKey(alliance.Target) || !Races.TryGetValue(alliance.Race, out var state))
                    return "no such player";
                // As in the original, the players hear of it only when it changes.
                if (alliance.Ally ? state.Allies.Add(alliance.Target) : state.Allies.Remove(alliance.Target))
                    Raise(new GameEvent(GameEventKind.AllianceChanged, (int)alliance.Race, Vector2.Zero, (int)alliance.Target, alliance.Ally ? "ALLY" : "NEUTRAL"));
                return null;
            }

            case GiveCommand give:
                return Give(give);

            case WiredCommand wired:
                return ApplyWired(wired);

            default:
                return $"unknown command {command.GetType().Name}";
        }
    }

    /// <summary>
    /// The live actors among the ids that the command's issuer may order (<see cref="MayOrder"/>): the race's, and in a race of
    /// several commanders only the issuer's machines and those they keep. Technicians in labs stay put; garrisoned machines
    /// come out.
    /// </summary>
    private IEnumerable<T> Own<T>(Command command, IEnumerable<int> ids) where T : Actor =>
        ids.Select(Find<T>).OfType<T>().Where(a => a.Race == command.Race && !a.IsDead
            && (a is not Machine { Inside: { } building } || Find<Construction>(building)?.Garrisoned.Contains(a.Id) == true)
            && MayOrder(command.Issuer, a));

    /// <summary>
    /// A commander hands machines to a teammate of their race who is there (P1 design §2.6): the machines the issuer may order
    /// among those named become the teammate's, leave their squadrons and the rest of their series, and a driven one is let go
    /// of. Not in a battle or a Wired run, where heroes are not given.
    /// </summary>
    private string? Give(GiveCommand give)
    {
        if (Battle is not null || Wired is not null)
            return "heroes are not given";
        if (!Players.Contains(give.To) || Players[give.To] is not { Kind: PlayerKind.Human } to || to.Race != give.Race || Players.Away(give.To))
            return "no commander of your race there to take them";
        if (give.Issuer == give.To)
            return "they are yours already";
        var given = Own<Machine>(give, give.Machines).Distinct().Where(m => m.Owner != give.To).ToList();
        if (given.Count == 0)
            return "none of these are yours to give";
        foreach (var machine in given)
        {
            if (machine.DrivenByPlayer)
                LetGoOfWheel(machine);
            machine.GiveTo(give.To);
            machine.Squad = null;
            _series.Remove(machine.Id);
        }
        Raise(new GameEvent(GameEventKind.MachinesGiven, given[0].Id, given[0].Position, Detail: given.Count.ToString(CultureInfo.InvariantCulture),
            Player: give.To, By: give.Issuer));
        return null;
    }

    /// <summary>As a <see cref="DriveCommand"/> that lets go of the wheel: the machine is nobody's to drive, and its driver's last input goes with it.</summary>
    private void LetGoOfWheel(Machine machine)
    {
        machine.DrivenByPlayer = false;
        machine.Driver = null;
        LetGoInside(machine);
        DriveInput.Remove(machine.Id);
        PlayerAims.Remove(machine.Id);
        PlayerWeapons.Remove(machine.Id);
        PlayerButtons.Remove(machine.Id);
        PlayerHeads.Remove(machine.Id);
        PlayerYaws.Remove(machine.Id);
    }

    // Commands numbered as they are applied: the one being applied, else 0 (orders the world gives of its own accord
    // get a number each).
    private int _commands, _commandId;

    /// <summary>Gives a machine a new order and plans its route.</summary>
    internal void Send(Machine machine, Order order, Vector2? destination, Vector2? groupTarget = null, (Vector2 Offset, float Radius)? formation = null)
    {
        LeaveGarrison(machine);
        machine.Doorway = null;
        machine.Order = order;
        machine.OrderedAt = Time;
        machine.CommandId = _commandId != 0 ? _commandId : ++_commands;
        // The first to go for a target has probably just spotted it, and warns its friends (MachLogAttackOperation).
        if (order is AttackOrder attack && !_threatsTo.ContainsKey(attack.Target))
            CaveeCall(machine);
        // A new order from the player takes the rest of a series away; what the machine takes on by itself does not.
        if (_commandId != 0)
        {
            _series.Remove(machine.Id);
            ManualCommand(machine);
        }
        machine.Path.Clear();
        machine.PathIndex = 0;
        // With the original's motion an attacker, healer or constructor picks its own place at the target.
        if (Options.OriginalMotion && order is AttackOrder or HealOrder or ConstructOrder or RepairOrder or CaptureOrder or DeconstructOrder)
            return;
        if (destination is { } goal)
            machine.Path.AddRange(groupTarget is { } shared && shared != goal
                ? PathVia(machine, shared, goal, formation)
                : Nav.FindPath(machine.Position, goal, MobilityOf(machine)));
    }

    /// <summary>
    /// A group member's path: the search the group shares towards its target, with the last stretch bent to the
    /// member's own place around it.
    /// </summary>
    private List<Vector2> PathVia(Machine machine, Vector2 shared, Vector2 goal, (Vector2 Offset, float Radius)? formation)
    {
        var mobility = MobilityOf(machine);
        var path = Nav.FindPath(machine.Position, shared, mobility);
        if (path.Count == 0)
            return Nav.FindPath(machine.Position, goal, mobility);
        // With the original's motion each keeps its place in the group round the corners.
        if (Options.OriginalMotion && formation is { } place)
            KeepPlace(machine, path, place.Offset, place.Radius);
        var before = path.Count > 1 ? path[^2] : machine.Position;
        if (Nav.LineOfSight(before, goal, mobility))
            path[^1] = goal;
        else
            path.AddRange(Nav.FindPath(path[^1], goal, mobility));
        return path;
    }

    public static bool CrossesWater(Machine machine) => machine.Stats.Locomotion is Locomotion.Hover or Locomotion.Glider;

    /// <summary>
    /// How a machine gets about, for the navigation grid: across water if it hovers or glides, and on a ship deck how much
    /// room it needs and whether it crawls through ducts (<see cref="Navigation.Mobility"/>). The original planets' grid
    /// asks only the water of it.
    /// </summary>
    public static Navigation.Mobility MobilityOf(Machine machine) => Navigation.Mobility.Of(machine.Stats, machine.Radius);

    // ---- constructions --------------------------------------------------------------------------

    /// <summary>
    /// The buildings a race's constructors can raise: everything in <c>global.cst</c> up to the most advanced
    /// entry of the best constructor the race owns or has owned (MachLogConstructionItem::activated), less the
    /// entries the scenario locks for the race.
    /// </summary>
    public IReadOnlyList<ConstructionOrderEntry> Buildable(Race race)
    {
        Races.TryGetValue(race, out var state);
        var best = Math.Max(BestConstructor(race), state?.ConstructionsReached ?? -1);
        var entries = Data.ConstructionOrder.Entries.Take(best + 1);
        if (Options.RestrictConstruction && state?.Locked.Count > 0)
            entries = entries.Where(e => !state.Locked.Contains(e));
        return entries.ToList();
    }

    /// <summary>The place in <c>global.cst</c> of the most advanced entry the race's constructors reach, or -1.</summary>
    internal int BestConstructor(Race race) => Machines
        .Where(m => m.Race == race && m.Stats.MostAdvancedConstruction is not null)
        .Select(m => Data.ConstructionOrder.IndexOf(m.Stats.MostAdvancedConstruction!))
        .DefaultIfEmpty(-1)
        .Max();

    /// <summary>
    /// What a Wired match leaves out of the original's orders: nobody places, builds, mends, takes or pulls down buildings
    /// there (a constructor hero raises its towers by <see cref="WiredTowerCommand"/>), and a spy hero lays its mines by
    /// <see cref="WiredSpecialCommand"/>, fetches none, does not hide (camouflage would take it out of every fight unseen
    /// by the checksum) and breaks into no building; a geo locator hero sends out its pulse by
    /// <see cref="WiredSpecialCommand"/> and goes looking for no ore. The spies and geo locators of anybody else keep all of it.
    /// </summary>
    private string? WhyNotInWired(Command command) => command switch
    {
        PlaceConstructionCommand or ConstructCommand or RepairCommand or CaptureCommand or DeconstructCommand => NotInWired,
        DropLandMinesCommand drop when HoldsWiredHero(drop.Spies) => NotInWired,
        RefillLandMinesCommand refill when HoldsWiredHero(refill.Spies) => NotInWired,
        CamouflageCommand camouflage when HoldsWiredHero(camouflage.Spies) => NotInWired,
        EnterLabCommand enter when HoldsWiredHero(enter.Technicians) => NotInWired,
        LocateCommand locate when HoldsWiredHero(locate.Locators) => NotInWired,
        _ => null,
    };

    private const string NotInWired = "not in Wired";

    /// <summary>Whether any of these machines is a hero of the Wired match.</summary>
    private bool HoldsWiredHero(IEnumerable<int> machines) => Wired is { } run && machines.Any(id => run.Heroes.Values.Any(s => s.Hero == id));

    /// <returns>null if the building may go there, otherwise why not.</returns>
    public string? WhyCannotPlace(Race race, string type, string? subType, int level, Vector2 position, int orientation)
    {
        // A Wired match has no building of the original's kind; its towers go by WiredTowerCommand. A series bypasses Apply.
        if (Wired is not null)
            return NotInWired;
        if (!Buildable(race).Any(e => e.Type == type && e.SubType == subType && e.HardwareLevel == level))
            return "your constructors cannot build that yet";
        if (Races[race].Bmu < MinimumBmuToPlace)
            return $"{MinimumBmuToPlace} BMUs required to place a construction";

        var spec = Data.Parm.Construction(subType is null ? type : $"{type} {subType}");
        // The player cannot place another at the limit; the AI's own building goes by its plans (MachGuiConstructCommand::isValidPosition).
        if (Races[race].Controller != Controller.Ai && MaxUnitsExist(race))
            return "too many units";
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

    private string? Place(PlaceConstructionCommand place) => Place(place, out _);

    private string? Place(PlaceConstructionCommand place, out Construction? placed)
    {
        placed = null;
        var why = WhyCannotPlace(place.Race, place.Type, place.SubType, place.HardwareLevel, place.Position, place.Orientation);
        if (why is not null)
            return why;

        // The site is the placing player's (it matters only for counting a hero's towers); buildings obey the whole race.
        var site = placed = AddConstruction(place.Race, place.Position, place.Type, place.SubType, place.HardwareLevel, place.Orientation, complete: false,
            owner: place.Issuer);
        var constructors = Own<Machine>(place, place.Constructors).Where(m => m.Spec.Type == "CONSTRUCTOR").ToList();
        // A disguised constructor's building wears the same disguise (MachLogConstructOperation).
        site.DisplayAs = constructors.FirstOrDefault()?.DisplayAs;
        foreach (var constructor in constructors)
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
