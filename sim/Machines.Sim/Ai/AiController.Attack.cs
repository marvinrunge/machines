using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// A squad's attack (MachLogTaskAttackOperation), what it goes for (MachLogRaces::findTargetClosestTo,
// findMostValuableTarget) and where its machines wait (MachLogCanAdminister::moveOutOfTheWay).
public sealed partial class AiController
{
    // Distances are compared squared, as in the original.
    private const float NearEnoughToPoint = 20 * 20, AssembledRange = 50 * 50, StragglerRange = 70 * 70, NearEnoughToPod = 60 * 60, DefaultScanner = 50 * 50, GoodTargetRange = 150 * 150;

    // What the original hands findMostValuableTarget: it compares them with the squared distance too, so a strike keeps
    // about 7 and 10 m from its launcher, not 50 and 100 m.
    private const float IonMinimum = 50, NukeMinimum = 100;

    private void Attack(GameWorld world, SquadState state, AttackOperation attack, List<Machine> members)
    {
        if (world.Time < state.NextUpdate)
            return;
        state.NextUpdate = world.Time + UpdateAttack(world, state, attack, members);
    }

    /// <summary>MachLogTaskAttackOperation::doUpdate.</summary>
    /// <returns>The seconds until the attack is looked at again.</returns>
    private float UpdateAttack(GameWorld world, SquadState state, AttackOperation attack, List<Machine> members)
    {
        var number = state.Squad.Number;

        // Waiting, every member goes to the assembly point, unarmed ones too: finger's APC drives to its drop-off so,
        // where the script lands the troops it carries. A mission script may call the squad in early
        // (ACTIVATE_SQUADRONS sets the attack's initial delay to 0).
        var activated = world.Script?.ActivatedSquadrons.Contains((Race, number)) == true;
        if (world.Time < attack.DelayMinutes * 60 * MatchOptions.AiDelay(world.Options.AiLevel) && !activated)
        {
            MoveToAssembly(world, state, members.Where(m => m.Order is IdleOrder));
            return 30;
        }

        // Squads told to use the ion cannon or nukes fire every charged one, whether or not the squad itself is ready.
        if (attack.UseIonCannon || attack.UseNuke)
            Strike(world, attack);

        // A missing MINIMUM is 0: attack with whoever is there. The minimum counts the whole squadron.
        if (world.Machines.Count(m => m.Race == Race && m.Squad == number && !m.IsDead) < (attack.Minimum ?? 0))
        {
            MoveToAssembly(world, state, members.Where(m => m.Order is IdleOrder));
            if (!state.Rampaging)
                return 30;
        }

        // DO counts these looks, not attacks, and ends nothing but the squad's production: setCompleteState leaves the
        // operation running, so what the squad has left fights on.
        if (state.DoLeft > -1 && --state.DoLeft <= 0)
            state.Abandoned = true;

        return world.CommanderOf(Race, number) is { } commander
            ? UpdateWithCommander(world, attack, number, commander, members)
            : UpdateWithoutCommander(world, state, attack, members);
    }

    /// <summary>With an administrator the squad goes where it leads: it seeks out the next target once it has none.</summary>
    private float UpdateWithCommander(GameWorld world, AttackOperation attack, int squad, Machine commander, List<Machine> members)
    {
        if (commander.Order is AttackOrder || FindTargetClosestTo(world, commander, attack, commander.Position) is not { } target)
            return 30;
        world.Issue(target is Artefact
            ? new AttackCommand(Race, members.Where(m => m.CanFight).Select(m => m.Id).ToList(), target.Id)
            : new SquadronCommand(Race, squad, new AttackCommand(Race, [], target.Id)));
        return 30;
    }

    /// <summary>
    /// A squad with a MINIMUM (and no NO_CLUSTER_CHECK) gathers at its assembly point, and is set loose once everyone
    /// is within 50 m of it; once all it set out with are gone, it gathers again. Any other squad is always on the rampage.
    /// </summary>
    private float UpdateWithoutCommander(GameWorld world, SquadState state, AttackOperation attack, List<Machine> members)
    {
        if (members.Count == 0)
        {
            state.Rampaging = false;
            return 10;
        }

        var proximity = ProximityCheck(attack);
        if (proximity)
        {
            if (state.Rampaging && !members.Any(m => state.Rampagers.Contains(m.Id)))
                state.Rampaging = false;
            if (!state.Rampaging && AllAssembled(world, state, members))
                SetLoose(state, members);
        }
        else
            SetLoose(state, members);

        if (!state.Rampaging)
            return 10;
        if (members.FirstOrDefault(m => m.CanFight && state.Rampagers.Contains(m.Id)) is not { } attacker)
            return 30;
        return FindTargetClosestTo(world, attacker, attack, attacker.Position) is { } target
            ? AttackSpecificTarget(world, state, attack, members, target, proximity)
            : 30;
    }

    private static bool ProximityCheck(AttackOperation attack) => attack.Minimum > 0 && !attack.NoClusterCheck;

    private static void SetLoose(SquadState state, List<Machine> members)
    {
        state.Rampaging = true;
        state.Rampagers.UnionWith(members.Select(m => m.Id));
    }

    /// <summary>
    /// checkAllCloseEnoughAndDoAssemble: whether the whole squad is near its assembly point. A point the race does not
    /// have is replaced by one it has; a race without any has nothing to gather at.
    /// </summary>
    private bool AllAssembled(GameWorld world, SquadState state, List<Machine> members)
    {
        var points = setup.AggressorAssemblyPoints;
        if (points.Count == 0)
            return true;
        if (state.AssemblyPoint >= points.Count)
            state.AssemblyPoint = points.Count == 1 ? 0 : world.Random.Next(points.Count);
        var point = points[state.AssemblyPoint];
        var away = members.Where(m => Vector2.DistanceSquared(m.Position, point) > AssembledRange).ToList();
        MoveToAssembly(world, state, away.Where(m => m.Order is IdleOrder));
        return away.Count == 0;
    }

    /// <summary>
    /// attackSpecificTarget: the squad heads for the target together, each one that can shoot from where it is starts
    /// at once. With a proximity check it waits for its stragglers: while anyone is more than 70 m from the machine
    /// nearest the target, that machine stops and the others make for it.
    /// </summary>
    private float AttackSpecificTarget(GameWorld world, SquadState state, AttackOperation attack, List<Machine> members, Actor target, bool proximity)
    {
        var interval = 25f;
        var destination = target.Position;
        if (proximity)
        {
            var nearest = members.Where(m => state.Rampagers.Contains(m.Id)).MinBy(m => Vector2.DistanceSquared(m.Position, target.Position))!.Position;
            if (members.Any(m => Vector2.DistanceSquared(m.Position, nearest) > StragglerRange))
            {
                destination = nearest;
                interval = 7;
            }
        }

        var chargers = new List<int>();
        var followers = new List<int>();
        foreach (var machine in members.Where(m => m.Order is not EvadeOrder))
        {
            if (!machine.CanFight || !state.Rampagers.Contains(machine.Id))
            {
                if (machine.Order is not MoveOrder { Target: var going } || going != destination)
                    followers.Add(machine.Id);
                continue;
            }
            if (machine.Order is AttackOrder)
                continue;

            var range = GameWorld.MaximumWeaponRange(machine);
            if (Vector2.DistanceSquared(target.Position, machine.Position) <= range * range || target is Artefact)
            {
                if (FindTargetClosestTo(world, machine, attack, machine.Position) is { } nearest)
                    world.Issue(new AttackCommand(Race, [machine.Id], nearest.Id));
            }
            else if (machine.Position == destination)
            {
                // The leader stops in its tracks.
                if (machine.Order is not IdleOrder)
                    world.Issue(new StopCommand(Race, [machine.Id]));
            }
            else if (machine.Order is not AttackMoveOrder { Target: var heading } || heading != destination)
                chargers.Add(machine.Id);
        }

        // What it meets on the way it takes on by itself (AttackMoveOrder), as the original's machines did on the move.
        if (chargers.Count > 0)
            world.Issue(new AttackMoveCommand(Race, chargers, destination));
        if (followers.Count > 0)
            world.Issue(new MoveCommand(Race, followers, destination));
        return interval;
    }

    // ---- what to go for -------------------------------------------------------------------------

    /// <summary>tryIonAttack, tryNukeAttack: every charged cannon and open silo fires at the most valuable target.</summary>
    private void Strike(GameWorld world, AttackOperation attack)
    {
        foreach (var launcher in world.Constructions.Where(c => c.Race == Race && c.Complete))
        {
            var weapon = launcher.Weapons.FirstOrDefault(w => w.IsStrike && w.Reload <= 0);
            if (weapon is null)
                continue;
            var nuke = weapon.Spec.TypeName == "NUCLEAR_MISSILE";
            // A silo launches once its dome is open.
            if (nuke ? !attack.UseNuke || !launcher.DomeOpen : !attack.UseIonCannon)
                continue;
            if (MostValuableTarget(world, launcher, attack, nuke) is { } target)
                world.Issue(new StrikeCommand(Race, launcher.Id, target.Position));
        }
    }

    /// <summary>
    /// MachLogRaces::findMostValuableTarget: with no target system given, the enemy with the highest local strength, a
    /// silo worth 500 to an ion cannon and other towers but a launcher half. A nuke favours what stands still: a
    /// building is worth its cost by the share of its hit points left, what can drive away half its strength.
    /// With nothing of value, or a target system given, the nearest will do.
    /// </summary>
    private Actor? MostValuableTarget(GameWorld world, Construction launcher, AttackOperation attack, bool favourBuildings)
    {
        var minimum = favourBuildings ? NukeMinimum : IonMinimum;
        Actor? best = null;
        var highest = 0;
        if (attack.Target == AttackTarget.Default)
        {
            foreach (var race in world.Races.Keys.Order().Where(r => world.AreEnemies(Race, r)))
            {
                foreach (var target in world.Actors.Where(a => a.Race == race && !a.IsDead && a is not Machine { Camouflaged: true }))
                {
                    var value = world.LocalStrength(target);
                    if (favourBuildings)
                        value = target is Construction ? (int)(target.HitPoints / (float)target.MaxHitPoints * target.Cost) : value / 2;
                    else if (target is Construction tower && tower.IsType("MISSILE_EMPLACEMENT"))
                        value = tower.Spec.SubType == "ICBM" ? 500 : tower.Spec.SubType == "LAUNCHER" ? value : value / 2;
                    if (value > highest && Vector2.DistanceSquared(launcher.Position, target.Position) >= minimum)
                        (best, highest) = (target, value);
                }
            }
        }
        return best ?? FindTargetClosestTo(world, launcher, attack, launcher.Position, minimum);
    }

    /// <summary>
    /// MachLogRaces::findTargetClosestTo, race by race. TARGET_RESOURCE takes the nearest carrier, mine or smelter; an
    /// OBJECT only that kind, the artefact the nearest standing one (POINT was read but never used). Otherwise the
    /// nearest of everything, with fighters and towers looked at first: with one within 150 m and the scanner, nine
    /// times in ten the rest of that race is not looked at. RESEARCH names no object, so the original's default stands
    /// and only aggressors are found.
    /// </summary>
    private Actor? FindTargetClosestTo(GameWorld world, Actor actor, AttackOperation attack, Vector2 from, float minimum = 0)
    {
        Actor? closest = null;
        var closestDistance = float.MaxValue;
        void Nearest(IEnumerable<Actor> actors)
        {
            foreach (var target in actors)
            {
                var distance = Vector2.DistanceSquared(from, target.Position);
                if (distance < closestDistance && distance >= minimum && !target.IsDead && world.CanFireAt(actor, target))
                    (closest, closestDistance) = (target, distance);
            }
        }

        var normal = attack.Target == AttackTarget.Default;
        var kind = attack.Target == AttackTarget.Object ? attack.ObjectType : "AGGRESSOR";
        if (!normal && kind is "ARTIFACT" or "ARTEFACT")
        {
            Nearest(world.Artefacts.Where(a => world.CanAttack(Race, a)));
            return closest;
        }

        var enemies = world.Actors.Where(a => world.AreEnemies(Race, a.Race) && a is not Machine { Inside: not null } and not Machine { Camouflaged: true }).ToLookup(Kind);
        var races = world.Races.Keys.Order().Where(r => world.AreEnemies(Race, r));
        if (attack.Target == AttackTarget.Resource)
        {
            foreach (var race in races)
                Nearest(new[] { "RESOURCE_CARRIER", "MINE", "SMELTER" }.SelectMany(k => enemies[k]).Where(a => a.Race == race));
            return closest;
        }

        var scanner = actor is Machine machine ? machine.Stats.ScannerRange * machine.Stats.ScannerRange : DefaultScanner;
        var turret = actor is Construction building && building.IsType("MISSILE_EMPLACEMENT");
        foreach (var race in races)
        {
            void Look(params string[] kinds)
            {
                foreach (var name in kinds.Where(k => normal || k == kind || k == "ICBM" && kind == "MISSILE_EMPLACEMENT"))
                    Nearest(enemies[name].Where(a => a.Race == race));
            }

            Look("AGGRESSOR", "ADMINISTRATOR", "MISSILE_EMPLACEMENT");
            if (closest is not null && world.Random.Next(10) != 0 && !turret && actor is not Machine { Inside: not null }
                && closestDistance < MathF.Min(scanner, GoodTargetRange))
                continue;
            Look("TECHNICIAN", "CONSTRUCTOR", "GEO_LOCATOR", "SPY_LOCATOR", "RESOURCE_CARRIER", "APC",
                "BEACON", "FACTORY", "GARRISON", "HARDWARE_LAB", "POD", "MINE", "SMELTER", "ICBM");
        }
        return closest;
    }

    private static string Kind(Actor actor) => actor switch
    {
        Machine machine => machine.Spec.Type,
        Construction { Spec.SubType: "ICBM" } => "ICBM",
        Construction building => building.Spec.Type,
        _ => "",
    };

    // ---- getting out of the way -----------------------------------------------------------------

    /// <summary>moveOutOfTheWay( machine, assemblyPoint ): to the squad's assembly point if the race has it, else where its kind waits.</summary>
    private void MoveToAssembly(GameWorld world, SquadState state, IEnumerable<Machine> machines)
    {
        var points = setup.AggressorAssemblyPoints;
        if (state.AssemblyPoint >= points.Count)
        {
            foreach (var machine in machines)
                MoveOutOfTheWay(world, machine);
            return;
        }
        var point = points[state.AssemblyPoint];
        var away = machines.Where(m => Vector2.DistanceSquared(m.Position, point) > NearEnoughToPoint).Select(m => m.Id).ToList();
        if (away.Count > 0)
            world.Issue(new MoveCommand(Race, away, point));
    }

    /// <summary>
    /// moveOutOfTheWay( machine ): fighters to any of their kind's assembly points. Others to the nearest garrison
    /// with a free bay; one time in ten, or always for a geo locator or without a garrison, to the pod's door.
    /// </summary>
    private void MoveOutOfTheWay(GameWorld world, Machine machine)
    {
        if (machine.Spec.Type is "AGGRESSOR" or "ADMINISTRATOR")
        {
            var points = machine.Spec.Type == "AGGRESSOR" ? setup.AggressorAssemblyPoints : setup.AdministratorAssemblyPoints;
            if (points.Count == 0)
                return;
            var point = points[world.Random.Next(points.Count)];
            if (Vector2.DistanceSquared(machine.Position, point) > NearEnoughToPoint)
                world.Issue(new MoveCommand(Race, [machine.Id], point));
            return;
        }

        var garrison = world.Constructions
            .Where(c => c.Race == Race && c.Complete && c.IsType("GARRISON") && c.Garrisoned.Count < GameWorld.GarrisonStations(c) && GameWorld.FitsThrough(machine, c))
            .MinBy(c => Vector2.DistanceSquared(c.Position, machine.Position));
        if (garrison is not null && world.Random.Next(10) >= (machine.Spec.Type == "GEO_LOCATOR" ? 10 : 1))
            world.Issue(new EnterGarrisonCommand(Race, [machine.Id], garrison.Id));
        else if (Pod(world) is { } pod)
        {
            var door = pod.EntranceOutside ?? pod.Position;
            if (Vector2.DistanceSquared(machine.Position, door) > NearEnoughToPod)
                world.Issue(new MoveCommand(Race, [machine.Id], door));
        }
        else if (setup.AdministratorAssemblyPoints.Count > 0)
            world.Issue(new MoveCommand(Race, [machine.Id], setup.AdministratorAssemblyPoints[0]));
    }
}
