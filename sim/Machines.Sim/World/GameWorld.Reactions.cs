using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- alertness, calls for help, counterattacks (MachLogCanAttack, MachActor) ------------------
    //
    // How readily a machine or tower looks for a target hangs on its alertness: anything going on round it raises that,
    // and it falls off again with time. What it finds, or what shoots at it, it fights as a counterattack: the fight
    // puts aside what it was doing, and when the fight is over it takes that up again, or goes back to where it stood.

    /// <summary>The alertness at which every look finds time for a check (MachLogCanAttack::setMinimumAlertness).</summary>
    private const int FullAlert = 125;

    /// <summary>How far an enemy can be and still be taken on: what fires from further off is left be (sqrMaximumEngagementDistance).</summary>
    private const float MaximumEngagementDistance = 150;

    /// <summary>Seconds a machine leaves targets be after picking one or giving one up (suppressAttackingUrges).</summary>
    private const float AttackingUrgeSuppression = 3;

    /// <summary>How often a counterattack sees whether to go on (MachLogCounterattackOperation::doUpdate), in ticks.</summary>
    private const int CounterattackCheckTicks = 2 * TicksPerSecond;

    /// <summary>The seconds between checks that a chase is closing on its quarry (distanceBeyondWeaponRangeCheckInterval).</summary>
    private const float ClosingCheckInterval = 15;

    /// <summary>Looks about those that can fight, each as often as its alertness says (MachLogAggressor::update, MachLogAdministrator::update).</summary>
    private void UpdateReactions()
    {
        foreach (var machine in Machines.ToList())
        {
            if (machine.IsScavenger && !machine.IsDead)
                SeekDebris(machine);
            if (!machine.IsDead && machine.CanFight && machine.Inside is null && !machine.DrivenByPlayer && Time >= machine.Alert.NextLook)
                Look(machine);
        }
    }

    private void Look(Machine machine)
    {
        var alert = machine.Alert;
        if (machine.Order is not EvadeOrder && WillCheckForTargets(alert))
            CheckAndAttackCloserTarget(machine);
        Diminish(alert, machine.Stats.HardwareLevel * 4 + 10);
        // Moving it keeps its eyes open, on patrol or standing its ground they are wide open (MachLogPatrolOperation, MachLogStandGroundOperation).
        if (machine.PathIndex < machine.Path.Count)
            alert.Raise(45);
        if (machine.Order is PatrolOrder or StandGroundOrder)
            alert.Raise(FullAlert);
        var interval = 4 - Math.Min(100, alert.Level) / 30f;
        alert.NextLook = Time + (_threatsTo.ContainsKey(machine.Id) ? MathF.Min(interval, 1) : interval);
    }

    /// <summary>MachLogCanAttack::willCheckForTargets: a look comes to something as often as its alertness says, unless it has just picked a target.</summary>
    private bool WillCheckForTargets(Alert alert) => Random.Next(100) < alert.Level && Time >= alert.NoChecksBefore;

    /// <summary>
    /// MachLogCanAttack::diminishAlertnessAndInaccuracy: alertness falls by four a second, down to what the actor
    /// always keeps. Whole points only, so the time goes on counting between them.
    /// </summary>
    private void Diminish(Alert alert, int minimum)
    {
        var fall = (int)((Time - alert.DiminishedAt) * 4);
        if (fall <= 0)
            return;
        alert.DiminishedAt = Time;
        alert.Level = Math.Max(minimum, alert.Level - fall);
    }

    /// <summary>What a machine has its sights on: the target of its attack, while that lasts (MachLogCanAttack::currentTarget).</summary>
    private Actor? CurrentTarget(Machine machine) =>
        machine.Order is AttackOrder attack && FindTarget(attack.Target) is { IsDead: false } target ? target : null;

    /// <summary>MachLogMachine::isFreeToAttack: it has nothing else to do but fight.</summary>
    private static bool FreeToAttack(Machine machine) => machine.Order is IdleOrder or StandGroundOrder or AttackMoveOrder;

    /// <summary>MachLogCanAttack::actorIsDirectThreat: what can shoot back, unless it is a pod, a silo or a tower not yet built.</summary>
    private static bool DirectThreat(Actor actor) => actor switch
    {
        Machine machine => machine.Spec.Type is "AGGRESSOR" or "ADMINISTRATOR",
        Construction tower => tower.IsType("MISSILE_EMPLACEMENT") && tower.Spec.SubType != "ICBM" && tower.Complete,
        _ => false,
    };

    /// <summary>The range of an actor's longest weapon (MachLogCanAttack::getMaximumWeaponRange).</summary>
    internal static float MaximumWeaponRange(Actor actor) => (actor switch
    {
        Machine machine => machine.Weapons,
        Construction building => building.Weapons,
        _ => [],
    }).Where(w => w.DealsDamage || w.IsTreachery).Select(w => w.Spec.Range).DefaultIfEmpty(0).Max();

    /// <summary>MachLogCanAttack::canTurnToAttack: within the range it opens fire at.</summary>
    private static bool InWeaponRange(Actor actor, Actor other) => actor.DistanceTo(other) <= MaximumWeaponRange(actor) * 0.95f;

    /// <summary>MachLogCanAttack::canTurnToAttackAndNoCover.</summary>
    private bool ShootableFromHere(Actor actor, Actor other) => InWeaponRange(actor, other) && ClearShot(actor, other);

    /// <summary>
    /// MachLogCanAttack::checkAndAttackCloserTarget for a machine: with no target it takes the nearest it would fight
    /// or what just shot at it (<paramref name="firedAtMe"/>, beyond its scanner too, though never beyond 150 m); with
    /// a target it now and then, the likelier the higher its level, weighs that up against another.
    /// </summary>
    private void CheckAndAttackCloserTarget(Machine machine, Actor? firedAtMe = null)
    {
        if (machine.DrivenByPlayer || machine.Order is EvadeOrder or StandGroundOrder || machine.Inside is not null
            || machine.Doorway is not null)
            return;
        var defCon = VirtualDefCon(machine);
        if (defCon == DefCon.High && !FreeToAttack(machine))
            return;

        var current = CurrentTarget(machine);
        Actor? candidate;
        if (firedAtMe is not null)
            candidate = firedAtMe;
        else if (current is null)
            candidate = InitiativeTarget(machine);
        else if (Random.Next(6) >= machine.Stats.HardwareLevel)
        {
            machine.Alert.Raise(FullAlert);
            return;
        }
        else
            candidate = AlternativeTarget(machine, current);
        if (candidate is null or Machine { Inside: not null })
            return;

        var distance = Vector2.Distance(machine.Position, candidate.Position);
        if (distance > MaximumEngagementDistance || firedAtMe is null && machine.DistanceTo(candidate) > ScannerReach(machine))
            return;
        machine.Alert.Raise(FullAlert);

        // At a higher DefCon a machine goes out of its way for nothing, unless shot at.
        if (defCon != DefCon.Low && !(DirectThreat(candidate) ? firedAtMe is not null || WorthTakingOn(machine, candidate) : ShootableFromHere(machine, candidate)))
            return;
        if (current is not null && !SwitchesTarget(machine, current, ref candidate, firedAtMe))
            return;
        if (candidate.Id == current?.Id)
            return;
        Counterattack(machine, candidate);
        machine.Alert.NoChecksBefore = Time + AttackingUrgeSuppression;
    }

    /// <summary>The nearest enemy but the one it has (MachLogRaces::findAlternativeTargetClosestTo).</summary>
    private Actor? AlternativeTarget(Machine machine, Actor current) =>
        NearestEnemy(machine, MathF.Min(ScannerReach(machine), MaximumEngagementDistance), accept: enemy => enemy.Id != current.Id);

    /// <summary>
    /// MachLogCanAttack::switchFromExistingTargetToThisOne: a player's busy machine at normal DefCon leaves what is
    /// stronger than it alone. With its target in reach it changes only if that has gone behind cover, or for
    /// something that can hurt it when its target cannot; with its target out of reach it takes what it can shoot now
    /// (not something harmless, while it stands in reach of an armed target), or what shot at it instead of a harmless target.
    /// </summary>
    private bool SwitchesTarget(Machine machine, Actor current, ref Actor alternative, Actor? firedAtMe)
    {
        if (Races[machine.Race].Controller != Controller.Ai && VirtualDefCon(machine) == DefCon.Normal && !FreeToAttack(machine)
            && alternative is Machine && MilitaryStrength(alternative) > MilitaryStrength(machine))
            return false;

        var threatening = DirectThreat(current);
        if (!InWeaponRange(machine, current))
        {
            if (!ShootableFromHere(machine, alternative))
                return firedAtMe is not null && !threatening;
            return DirectThreat(alternative) || !threatening || !InWeaponRange(current, machine);
        }
        var hidden = !ClearShot(machine, current);
        if (!hidden && threatening)
            return false;
        if (alternative.Id == current.Id)
        {
            if (AlternativeTarget(machine, current) is not { } other)
                return false;
            alternative = other;
        }
        return ShootableFromHere(machine, alternative) && (hidden || DirectThreat(alternative));
    }

    /// <summary>
    /// MachLogStrategy::changeToCounterattackMode: the machine puts aside what it is doing to fight. One that was doing
    /// nothing will come back to where it stands now, looking the way the enemy came from, after a pause that is
    /// shorter the worse it was hurt, so a group wheels back one by one. One already in a fight just turns to the new target.
    /// </summary>
    internal void Counterattack(Machine machine, Actor target)
    {
        var orderedAt = machine.OrderedAt;
        if (machine.Order is AttackOrder { Back: not null } fight)
        {
            Send(machine, fight with { Target = target.Id }, target.Position);
        }
        else
        {
            var (order, to) = machine.Order is IdleOrder
                ? (new ReturnOrder(machine.Position, target.Position, 2 * HitPointShare(machine) + Random.NextSingle() * 2), (Vector2?)null)
                : (machine.Order, machine.PathIndex < machine.Path.Count ? machine.Path[^1] : (Vector2?)null);
            Send(machine, new AttackOrder(target.Id, Back: new Interrupted(order, to, Chases: true, Time, Time + ClosingCheckInterval, BeyondWeaponRange(machine, target))), target.Position);
        }
        // The fight is its own idea, not a new order.
        machine.OrderedAt = orderedAt;
    }

    /// <summary>MachLogCanAttack::distanceBeyondMaximumWeaponRange.</summary>
    private static float BeyondWeaponRange(Machine machine, Actor target) =>
        MathF.Max(0, Vector2.Distance(machine.Position, target.Position) - MaximumWeaponRange(machine));

    /// <summary>
    /// MachLogCounterattackOperation::shouldBreakOffAttack: a machine gives up a quarry that has run from it for ten
    /// seconds, five if the quarry is much the faster, five more at low DefCon, or one that lies over 35 m beyond its
    /// weapons and that it has not closed on by 30% in 15 seconds. Having given up it leaves targets be for a moment,
    /// or it would pick the same one up again.
    /// </summary>
    private bool ChaseOver(Machine machine, AttackOrder attack, Actor target)
    {
        if (attack.Back is not { Chases: true } back || target is not Machine quarry || Tick % CounterattackCheckTicks != machine.Id % CounterattackCheckTicks)
            return false;
        var notEvading = quarry.Order is EvadeOrder ? back.TargetLastNotEvading : Time;
        var patience = MathF.Max(5, 10 - (quarry.Stats.Speed - machine.Stats.Speed)) + (VirtualDefCon(machine) == DefCon.Low ? 5 : 0);
        var over = Time - notEvading >= patience;
        var (nextCheck, beyond) = (back.NextClosingCheck, back.BeyondRange);
        if (!over && Time > nextCheck)
        {
            var now = BeyondWeaponRange(machine, target);
            over = now > 35 && now > beyond * 0.7f;
            (nextCheck, beyond) = (Time + ClosingCheckInterval, now);
        }
        if (over)
            machine.Alert.NoChecksBefore = Time + AttackingUrgeSuppression;
        else
            machine.Order = attack with { Back = back with { TargetLastNotEvading = notEvading, NextClosingCheck = nextCheck, BeyondRange = beyond } };
        return over;
    }

    /// <summary>The fight is over: the machine takes up again what it put aside for it.</summary>
    private void TakeUpAgain(Machine machine, Interrupted back)
    {
        var orderedAt = machine.OrderedAt;
        if (back.Order is ReturnOrder going)
            Send(machine, going with { Until = Time + going.Pause }, null);
        else
            Send(machine, back.Order, back.To);
        machine.OrderedAt = orderedAt;
    }

    /// <summary>A machine back from a fight it picked: it waits, surveying the field, drives back and turns to where the enemy came from (MachLogMoveAndTurnOperation).</summary>
    private void UpdateReturn(Machine machine, ReturnOrder going)
    {
        if (Time < going.Until)
            return;
        if (!going.Moving)
        {
            var orderedAt = machine.OrderedAt;
            Send(machine, going with { Moving = true }, going.To);
            machine.OrderedAt = orderedAt;
            return;
        }
        if (FollowPath(machine))
            return;
        Face(machine, going.Facing);
        machine.Order = Order.Idle;
    }

    // ---- being hit ------------------------------------------------------------------------------

    /// <summary>
    /// What a hit sets off (MachActor::beHit, doHitButStillAliveStuff, the beHit of MachLogAggressor,
    /// MachLogAdministrator and MachLogMissileEmplacement): the dead warn their friends; an AI's machine tells its
    /// controller; the living go on full alert, call for help against a player driving the shooter, and try to turn on it.
    /// </summary>
    private void ReactToHit(Actor target, Actor? shooter)
    {
        if (target is Machine { Inside: null } struck)
            HandleEnemyContact(struck);
        if (target is Machine { Passengers.Count: > 0, IsDead: false } apc)
            ConsiderEvacuating(apc);
        if (target.IsDead)
        {
            CaveeCall(target);
            return;
        }
        if (shooter is null)
            return;
        var enemy = AreEnemies(target.Race, shooter.Race);
        if (enemy && shooter is Machine { DrivenByPlayer: true } && Time > target.Alert.NextFirstPersonSos)
        {
            DispatchSos(target, [shooter]);
            target.Alert.NextFirstPersonSos = Time + 4;
        }
        if (shooter.Race != target.Race)
            target.Alert.Raise(FullAlert);
        if (target is Construction tower && TakesOrders(tower))
            TowerHit(tower, shooter);
        if (enemy && target is Machine { CanFight: true } machine && Time >= machine.Alert.NoChecksBefore && CanFireAt(machine, shooter)
            && WillTestHitBy(machine, shooter))
            CheckAndAttackCloserTarget(machine, shooter);
    }

    /// <summary>MachLogCanAttack::willTestHitByCandidate: at most every ten seconds a hit from anything but its own target makes it think again.</summary>
    private bool WillTestHitBy(Machine machine, Actor shooter)
    {
        var alert = machine.Alert;
        var current = CurrentTarget(machine);
        var test = current is null || current.Id != shooter.Id && Time >= alert.NextHitByTest;
        if (test || current?.Id == shooter.Id)
            alert.NextHitByTest = Time + 10;
        return test;
    }

    /// <summary>
    /// MachLogAIController::handleEnemyContact: for each hit on an AI's machine outside a building, its first idle
    /// aggressor within 100 m, else its first such administrator, heads there. What it does then is up to its own eyes.
    /// </summary>
    private void HandleEnemyContact(Machine caller)
    {
        if (!Races.TryGetValue(caller.Race, out var race) || race.Controller != Controller.Ai)
            return;
        var near = FiledNear(caller.Position, 100).OfType<Machine>()
            .Where(m => m.Race == caller.Race && !m.IsDead && m.Order is IdleOrder && m.Inside is null
                && Vector2.DistanceSquared(m.Position, caller.Position) < 100 * 100)
            .OrderBy(m => m.Id).ToList();
        var helper = near.FirstOrDefault(m => m.Spec.Type == "AGGRESSOR") ?? near.FirstOrDefault(m => m.Spec.Type == "ADMINISTRATOR");
        if (helper is null)
            return;
        var orderedAt = helper.OrderedAt;
        var spot = FreeSpotNear(caller.Position, MathF.Max(1, helper.Stats.HighClearance));
        Send(helper, new MoveOrder(spot), spot);
        helper.OrderedAt = orderedAt;
    }

    // ---- calling for help -----------------------------------------------------------------------

    /// <summary>Friends that can fight within <paramref name="range"/>, beyond <paramref name="beyond"/>, not carried in an APC (MachLogRaces::allFriendlyAggressiveMachinesWithinRange).</summary>
    private List<Machine> FriendlyFighters(Actor actor, float range, float beyond = 0) => FiledNear(actor.Position, range).OfType<Machine>()
        .Where(m => m != actor && !m.IsDead && m.Spec.Type is "AGGRESSOR" or "ADMINISTRATOR" && !AreEnemies(actor.Race, m.Race)
            && (m.Inside is not { } inside || Find<Machine>(inside) is null)
            && Vector2.DistanceSquared(m.Position, actor.Position) is var distance && distance > beyond * beyond && distance <= range * range)
        .OrderBy(m => m.Id).ToList();

    /// <summary>MachActor::dispatchCaveeCall: every friend within 100 m goes on full alert.</summary>
    private void CaveeCall(Actor actor)
    {
        foreach (var friend in FriendlyFighters(actor, LocalStrengthRadius))
            friend.Alert.Raise(FullAlert);
    }

    /// <summary>
    /// MachActor::dealWithThreats, once a second for what is under attack: any threat puts it on medium alert, a
    /// strong one on full alert and every six seconds has it call for help.
    /// </summary>
    private void DealWithThreats(Actor actor, IReadOnlyList<Actor> strongThreats)
    {
        actor.Alert.Raise(60);
        if (strongThreats.Count == 0)
            return;
        actor.Alert.Raise(FullAlert);
        if (Time > actor.Alert.NextSos)
        {
            DispatchSos(actor, strongThreats);
            actor.Alert.NextSos = Time + 6;
        }
    }

    /// <summary>
    /// MachActor::populateStrongThreats for a building: enemies attacking it within 100 m, machines outside and towers
    /// that have it in their reach.
    /// </summary>
    private List<Actor> StrongThreats(Construction building) => _threatsTo.TryGetValue(building.Id, out var threats)
        ? threats.Where(t => !t.IsDead && t is not Machine { Inside: not null } && AreEnemies(building.Race, t.Race)
            && Vector2.DistanceSquared(t.Position, building.Position) < 100 * 100
            && (t is not Construction tower || tower.DistanceTo(building) <= MaximumWeaponRange(tower))).ToList()
        : [];

    /// <summary>
    /// MachActor::dispatchSOS: first the friends within 150 m are called on the threats in turn, leaving out any already
    /// outgunned or that the caller has no clear line to, then, if some are left, the friends out to 250 m; three
    /// times as far for a pod. Even a friend that cannot come is alerted.
    /// </summary>
    private void DispatchSos(Actor caller, IReadOnlyList<Actor> threats)
    {
        var reach = caller is Construction pod && pod.IsType("POD") ? 3 : 1;
        var (first, second) = ((LocalStrengthRadius + 50) * reach, (LocalStrengthRadius * 2 + 50) * reach);
        var next = 0;
        next = CallUp(caller, threats, next, FriendlyFighters(caller, first), FullAlert);
        CallUp(caller, threats, next, FriendlyFighters(caller, second, first), 70);
    }

    private int CallUp(Actor caller, IReadOnlyList<Actor> threats, int next, List<Machine> friends, int alertness)
    {
        var called = 0;
        while (next < threats.Count && called < friends.Count)
        {
            var threat = threats[next];
            if (threat.IsDead || OutGunned(threat) || !ClearShot(caller, threat))
            {
                next++;
                continue;
            }
            var friend = friends[called++];
            if (!AreEnemies(friend.Race, threat.Race))
                continue;
            friend.Alert.Raise(alertness);
            if (VirtualDefCon(friend) != DefCon.High && CurrentTarget(friend) is null && WillingToRespond(friend)
                && friend.Order is not (StandGroundOrder or EvadeOrder) && friend.Inside is null && friend.Doorway is null
                && !friend.DrivenByPlayer && CanFireAt(friend, threat))
                Counterattack(friend, threat);
        }
        return next;
    }

    /// <summary>MachLogCanAttack::willingToRespondToSOS: below 60% of its hit points a machine comes only by the dice, the worse hurt the less likely.</summary>
    private bool WillingToRespond(Machine friend)
    {
        var share = HitPointShare(friend);
        return share > 0.6f || Random.Next(101) <= (int)(share * 100);
    }
}
