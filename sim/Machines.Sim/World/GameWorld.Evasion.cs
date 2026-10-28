using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- evasion (MachLogMachine::checkAndDoOnDefCon, MachLogEvadeOperation) --------------------
    //
    // A machine under attack it fears gets out of the way by its evasion plan (EPP): into a garrison, beside the
    // strongest friendly fighter or tower, near a pod, or simply 60 m away from its strongest attacker. When the
    // danger is past it takes up again what it was doing.

    // Who is attacking whom this check: machines under an attack order or fighting on their own, and towers.
    private readonly Dictionary<int, List<Actor>> _threatsTo = [];

    /// <summary>The distance within which friendly fighters add to a machine's local strength (localStrengthMaximumRadius).</summary>
    private const float LocalStrengthRadius = 100;

    /// <summary>Looked at once a second (MachActor::dealWithThreats); an evasion under way twice as often.</summary>
    private void UpdateEvasion()
    {
        if (Tick % 10 != 0)
            return;
        _threatsTo.Clear();
        foreach (var machine in Machines)
        {
            if (machine.IsDead || machine.Inside is not null)
                continue;
            var target = machine.Order is AttackOrder attack ? attack.Target : _machineAims.TryGetValue(machine.Id, out var aim) ? aim : (int?)null;
            if (target is { } id)
                ThreatsTo(id).Add(machine);
        }
        foreach (var (tower, target) in _turretTargets)
        {
            if (Find<Construction>(tower) is { IsDead: false } turret)
                ThreatsTo(target).Add(turret);
        }

        foreach (var machine in Machines.ToList())
        {
            if (machine.IsDead || machine.DrivenByPlayer)
                continue;
            if (machine.Order is EvadeOrder evade)
            {
                if (Tick % 40 == machine.Id % 4 * 10)
                    ContinueEvading(machine, evade);
            }
            else if (Tick % 20 == machine.Id % 2 * 10 && _threatsTo.ContainsKey(machine.Id))
                ConsiderEvading(machine);
        }
    }

    private List<Actor> ThreatsTo(int id)
    {
        if (!_threatsTo.TryGetValue(id, out var list))
            _threatsTo[id] = list = [];
        return list;
    }

    /// <summary>
    /// The machines attacking this one that count (MachLogMachine::populateStrongThreats): enemies outside, within
    /// its scanner range (half as far again while it evades). Inside a building only enemies inside with it count,
    /// which in the remake never happens, unless it is evading.
    /// </summary>
    private List<Machine> StrongThreats(Machine machine)
    {
        var evading = machine.Order is EvadeOrder;
        if (machine.Inside is not null && !evading)
            return [];
        var range = machine.Stats.ScannerRange * (evading ? 1.7f : 1);
        if (evading)
        {
            // Evading, it watches every enemy machine in range, not only those that have it in their sights.
            return Machines.Where(m => !m.IsDead && m.Inside is null && AreEnemies(machine.Race, m.Race) && m.CanFight
                && Vector2.Distance(m.Position, machine.Position) <= range).ToList();
        }
        return _threatsTo.TryGetValue(machine.Id, out var threats)
            ? threats.OfType<Machine>().Where(m => !m.IsDead && m.Inside is null && AreEnemies(machine.Race, m.Race)
                && Vector2.Distance(m.Position, machine.Position) <= range).ToList()
            : [];
    }

    /// <summary>MachLogMachine::checkAndDoOnDefCon: whether a machine under attack starts to evade.</summary>
    private void ConsiderEvading(Machine machine)
    {
        var plan = Data.Parm.EvasionStrategies.FirstOrDefault(e => e.Name == machine.Stats.Evasion);
        if (plan is null || plan.GarrisonsPriority == -2 || machine.Order is StandGroundOrder || VirtualDefCon(machine) == DefCon.High)
            return;
        if (machine.Inside is { } inside && Find<Construction>(inside) is { } building && building.IsType("GARRISON")
            && building.Race == machine.Race && machine.HitPoints < 0.2f * machine.MaxHitPoints)
            return;
        var threats = StrongThreats(machine);
        if (threats.Count == 0)
            return;

        // A fighter shooting back at one of them stands its ground; an APC letting its passengers out, or a
        // constructor building a tower, sees its job through.
        if (machine.CanFight)
        {
            var target = machine.Order is AttackOrder attack ? attack.Target : _machineAims.TryGetValue(machine.Id, out var aim) ? aim : (int?)null;
            if (target is { } id && threats.Any(t => t.Id == id))
                return;
        }
        else if (machine.Order is DeployOrder
            || machine.Order is ConstructOrder build && Find<Construction>(build.Construction)?.IsType("MISSILE_EMPLACEMENT") == true)
            return;

        // At normal DefCon a machine heading somewhere out of all their scanner ranges carries on.
        if (machine.DefCon == DefCon.Normal && machine.PathIndex < machine.Path.Count
            && threats.All(t => Vector2.Distance(t.Position, machine.Path[^1]) > t.Stats.ScannerRange))
            return;
        if (NotAfraid(machine, threats) || WontFindAnywhereSafer(machine, plan))
            return;

        var resumeTo = machine.PathIndex < machine.Path.Count ? machine.Path[^1] : (Vector2?)null;
        machine.Order = new EvadeOrder(machine.Order, resumeTo, Time, EvadeGoal.None, null);
        machine.Path.Clear();
        machine.PathIndex = 0;
        ContinueEvading(machine, (EvadeOrder)machine.Order);
    }

    /// <summary>
    /// The real DefCon, except that for a while after an order (VIRTUAL_DEFCON_INTERVAL) a busy machine behaves as
    /// at high DefCon (MachLogMachine::virtualDefCon).
    /// </summary>
    private DefCon VirtualDefCon(Machine machine) =>
        Time - machine.OrderedAt < Data.Parm.General.VirtualDefconInterval && machine.Order is not IdleOrder ? DefCon.High : machine.DefCon;

    /// <summary>
    /// MachLogMachine::notAfraidOfStrongThreats: a machine fears an attacker that is over 70% as strong as it and
    /// its fighters around it together, or that is not itself outgunned by what is attacking it.
    /// </summary>
    private bool NotAfraid(Machine machine, List<Machine> threats)
    {
        var local = LocalStrength(machine);
        return threats.All(threat => MilitaryStrength(threat) <= local * 0.7f && OutGunned(threat));
    }

    /// <summary>MachActor::isOutGunned: what is attacking it is worth over 1.3 times its own strength.</summary>
    private bool OutGunned(Actor actor) =>
        (_threatsTo.TryGetValue(actor.Id, out var attackers) ? attackers.Where(a => !a.IsDead).Sum(MilitaryStrength) : 0)
        > MilitaryStrength(actor) * 1.3f;

    /// <summary>
    /// MachActor::militaryStrength: a fighting machine or a tower is worth its cost by the share of its hit points
    /// left, a garrison its hit points times its level over 20, a pod over 30; the rest nothing.
    /// </summary>
    private static int MilitaryStrength(Actor actor) => actor switch
    {
        Machine machine when machine.Spec.Type is "AGGRESSOR" or "ADMINISTRATOR" => (int)(HitPointShare(machine) * machine.Cost),
        Construction { Complete: false } => 0,
        Construction garrison when garrison.IsType("GARRISON") => garrison.HitPoints * garrison.Stats.HardwareLevel / 20,
        Construction pod when pod.IsType("POD") => pod.HitPoints * pod.Stats.HardwareLevel / 30,
        Construction tower when tower.IsType("MISSILE_EMPLACEMENT") && tower.Spec.SubType != "ICBM" => (int)(HitPointShare(tower) * tower.Cost),
        _ => 0,
    };

    private static float HitPointShare(Actor actor) => actor.HitPoints / (float)Math.Max(1, actor.MaxHitPoints);

    /// <summary>
    /// MachActor::updateLocalStrengthEstimate: a machine's or a tower's own strength and that of the friendly
    /// fighting machines within 100 m, those beyond 50 m counting half; for anything else its own strength.
    /// </summary>
    private int LocalStrength(Actor actor)
    {
        var own = MilitaryStrength(actor);
        if (actor is not Machine && !(actor is Construction tower && tower.IsType("MISSILE_EMPLACEMENT")))
            return own;
        var total = (float)own;
        foreach (var near in FiledNear(actor.Position, LocalStrengthRadius))
        {
            if (near is not Machine friend || friend == actor || friend.IsDead || friend.Inside is not null || AreEnemies(actor.Race, friend.Race)
                || friend.Spec.Type is not ("AGGRESSOR" or "ADMINISTRATOR"))
                continue;
            var distance = Vector2.Distance(friend.Position, actor.Position);
            if (distance <= LocalStrengthRadius)
                total += MilitaryStrength(friend) * (distance > LocalStrengthRadius / 2 ? 0.5f : 1);
        }
        return (int)total;
    }

    /// <summary>
    /// MachActor::localStrengthFromPosition: the local strength as seen from a point, less 30% at each of 40, 80,
    /// 120, 160, 250 and 500 m away.
    /// </summary>
    private int LocalStrengthFrom(Actor actor, Vector2 point)
    {
        var distance = Vector2.Distance(actor.Position, point);
        var share = 1f;
        foreach (var band in (ReadOnlySpan<float>)[40, 80, 120, 160, 250, 500])
        {
            if (distance > band)
                share *= 0.7f;
        }
        return (int)(LocalStrength(actor) * share);
    }

    /// <summary>
    /// MachLogMachine::wontFindAnywhereSafer: a machine already beside the safest friendly fighter or tower its plan
    /// would send it to stays put; so does a fighter for which its plan names none to run to.
    /// </summary>
    private bool WontFindAnywhereSafer(Machine machine, EvasionStrategy plan)
    {
        Actor? safest = null;
        for (var priority = 0; priority < 3; priority++)
        {
            var aggressives = plan.AggressivesPriority == priority;
            var turrets = plan.TurretsPriority == priority;
            if (!aggressives && !turrets)
                break;
            var fighter = aggressives ? SafestFriend(machine, towers: false) : null;
            var tower = turrets ? SafestFriend(machine, towers: true) : null;
            safest = fighter is not null && tower is not null
                ? LocalStrengthFrom(tower, machine.Position) > LocalStrengthFrom(fighter, machine.Position) ? tower : fighter
                : fighter ?? tower;
            if (safest is not null || aggressives && turrets)
                break;
        }
        return safest is not null
            ? Vector2.DistanceSquared(machine.Position, safest.Position) <= LocalStrengthRadius / 2
            : machine.CanFight;
    }

    /// <summary>
    /// MachLogRaces::findSafestFriendly: the friendly fighting machine (not itself evading) or tower that is
    /// strongest as seen from where the machine stands; null if none is worth anything.
    /// </summary>
    private Actor? SafestFriend(Machine machine, bool towers)
    {
        IEnumerable<Actor> candidates = towers
            ? Constructions.Where(c => c.IsType("MISSILE_EMPLACEMENT") && c.Complete)
            : Machines.Where(m => m.Spec.Type is "AGGRESSOR" or "ADMINISTRATOR" && m.Inside is null && m.Order is not EvadeOrder);
        Actor? best = null;
        var bestStrength = 0;
        foreach (var candidate in candidates)
        {
            if (candidate == machine || candidate.IsDead || AreEnemies(machine.Race, candidate.Race))
                continue;
            var strength = LocalStrengthFrom(candidate, machine.Position);
            if (strength > bestStrength)
                (best, bestStrength) = (candidate, strength);
        }
        return best;
    }

    /// <summary>
    /// MachLogEvadeOperation::doUpdate, every two seconds: it stops once nothing has threatened it for 16 seconds
    /// (or at once when it has nowhere to go and nothing threatens it), at high DefCon, or when it no longer fears
    /// what threatens it; otherwise, once it has got where it was going, it picks the next refuge.
    /// </summary>
    private void ContinueEvading(Machine machine, EvadeOrder evade)
    {
        var threats = StrongThreats(machine);
        if (threats.Count > 0)
            machine.Order = evade = evade with { LastThreat = Time };
        var going = evade.Goal != EvadeGoal.None;
        if (going && Time - evade.LastThreat >= 16 || !going && threats.Count == 0
            || VirtualDefCon(machine) == DefCon.High || threats.Count > 0 && NotAfraid(machine, threats))
        {
            StopEvading(machine, evade);
            return;
        }
        if (going)
            return;

        var plan = Data.Parm.EvasionStrategies.FirstOrDefault(e => e.Name == machine.Stats.Evasion);
        Actor? refuge = null;
        var kind = EvadeGoal.SaferGround;
        var bestStrength = 0;
        for (var priority = 0; plan is not null && kind == EvadeGoal.SaferGround && priority < 3; priority++)
        {
            if (plan.GarrisonsPriority == priority && NearestFreeGarrison(machine) is { } garrison)
                (refuge, kind, bestStrength) = (garrison, EvadeGoal.Garrison, LocalStrengthFrom(garrison, machine.Position));
            if (plan.AggressivesPriority == priority && SafestFriend(machine, towers: false) is { } fighter
                && LocalStrengthFrom(fighter, machine.Position) is var fighterStrength && (refuge is null || fighterStrength > bestStrength))
                (refuge, kind, bestStrength) = (fighter, EvadeGoal.Fighter, fighterStrength);
            if (plan.TurretsPriority == priority && SafestFriend(machine, towers: true) is { } tower
                && LocalStrengthFrom(tower, machine.Position) is var towerStrength && (refuge is null || towerStrength > bestStrength))
                (refuge, kind, bestStrength) = (tower, EvadeGoal.Tower, towerStrength);
            if (plan.PodPriority == priority && SafestPod(machine) is { } pod
                && LocalStrengthFrom(pod, machine.Position) is var podStrength and > 0 && (refuge is null || podStrength > bestStrength)
                && Vector2.DistanceSquared(machine.Position, pod.Position) > 3000)
                (refuge, kind, bestStrength) = (pod, EvadeGoal.Pod, podStrength);
        }

        var clearance = MathF.Max(1, machine.Stats.LowClearance);
        Vector2 goal;
        switch (kind)
        {
            case EvadeGoal.Garrison:
                machine.Order = evade with { Goal = kind, Refuge = refuge!.Id };
                return;
            case EvadeGoal.Fighter:
                // Up beside it, somewhere within 20 m either way.
                goal = refuge!.Position + new Vector2(Random.NextSingle() * 40 - 20, Random.NextSingle() * 40 - 20);
                break;
            case EvadeGoal.Tower:
            {
                // 8 m behind the tower as seen from the nearest attacker.
                var nearest = threats.MinBy(t => Vector2.DistanceSquared(t.Position, machine.Position))!;
                var away = refuge!.Position - nearest.Position;
                goal = refuge.Position + (away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX) * 8;
                break;
            }
            case EvadeGoal.Pod:
                goal = refuge!.Position + new Vector2(25, 0);
                break;
            default:
            {
                // 60 m away from the strongest attacker, straight on from it through the machine.
                var strongest = threats.MaxBy(t => LocalStrengthFrom(t, machine.Position))!;
                var away = machine.Position - strongest.Position;
                goal = machine.Position + (away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX) * 60;
                break;
            }
        }
        var edge = new Vector2(Planet.Surface.Width, Planet.Surface.Depth) - new Vector2(clearance + 1);
        goal = FreeSpotNear(Vector2.Clamp(goal, new Vector2(clearance + 1), edge), clearance);
        machine.Order = evade with { Goal = kind, Refuge = refuge?.Id };
        machine.Path.Clear();
        machine.PathIndex = 0;
        machine.Path.AddRange(Nav.FindPath(machine.Position, goal, CrossesWater(machine)));
    }

    /// <summary>
    /// MachLogRaces::findFriendlyGarrisonClosestTo with FREE_BAYS_PLUS_NO_ENEMY_CANATTACKS: the nearest complete
    /// garrison of its race or an ally's with a free bay it fits through.
    /// </summary>
    private Construction? NearestFreeGarrison(Machine machine) => Constructions
        .Where(g => g.IsType("GARRISON") && g.Complete && !g.IsDead && !AreEnemies(machine.Race, g.Race) && g.Id != machine.Inside
            && g.Garrisoned.Count < GarrisonStations(g) && FitsThrough(machine, g))
        .MinBy(g => Vector2.DistanceSquared(g.EntranceOutside ?? g.Position, machine.Position));

    private Construction? SafestPod(Machine machine) => Constructions
        .Where(p => p.IsType("POD") && p.Complete && !p.IsDead && !AreEnemies(machine.Race, p.Race))
        .MaxBy(p => LocalStrengthFrom(p, machine.Position));

    /// <summary>An evading machine on its way: into its garrison, or along its path; at the end it waits.</summary>
    private void UpdateEvade(Machine machine, EvadeOrder evade)
    {
        if (evade.Goal == EvadeGoal.Garrison)
        {
            if (Find<Construction>(evade.Refuge ?? 0) is not { Complete: true } garrison || garrison.Garrisoned.Count >= GarrisonStations(garrison))
            {
                machine.Order = evade with { Goal = EvadeGoal.None, Refuge = null };
                return;
            }
            if (!ThroughDoor(machine, garrison))
                return;
            machine.Doorway = null;
            machine.Inside = garrison.Id;
            machine.Position = garrison.Position;
            garrison.Garrisoned.Add(machine.Id);
            machine.Order = evade with { Goal = EvadeGoal.None, Refuge = null };
            return;
        }
        if (evade.Goal != EvadeGoal.None && !FollowPath(machine))
            machine.Order = evade with { Goal = EvadeGoal.None, Refuge = null };
    }

    /// <summary>The evasion is over: the machine takes up again what it was doing before (the cached operation).</summary>
    private void StopEvading(Machine machine, EvadeOrder evade)
    {
        var orderedAt = machine.OrderedAt;
        switch (evade.Resume)
        {
            case IdleOrder:
                machine.Order = Order.Idle;
                machine.Path.Clear();
                machine.PathIndex = 0;
                break;
            case var resume:
                Send(machine, resume, evade.ResumeTo);
                break;
        }
        machine.OrderedAt = orderedAt;
    }
}

/// <summary>Where an evading machine is going (MachLogEvadeOperation's evade op types).</summary>
public enum EvadeGoal { None, Garrison, Fighter, Tower, Pod, SaferGround }
