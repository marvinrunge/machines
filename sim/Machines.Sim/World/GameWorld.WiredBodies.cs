using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The specials of the bodies of Wired's own machines, the remake's own addition: Old Iron's plough and ram, the
/// Harvester's harvest, the Widow's lurk and the Colossus' anchor, for every machine of those levels in a Wired match,
/// hero, escort, machine of the waves and battle hero alike; and the Marshal's field and rally, the hero's. What a body
/// keeps in mind is in <see cref="WiredRun.Bodies"/>. What a special does to a weapon it does by tuning the mount
/// (<see cref="WeaponMount.Tune"/>), never by a refit, so that reloads and the driver stay. Nothing here runs without
/// <see cref="Wired"/>. A hero's driver uses its body's special by its commands; the machines of the waves and of the squad
/// use theirs by themselves (<see cref="DriveBody"/>).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The catalogue's chassis whose machine has a special of its body, by type, subtype and level, with its own dash.</summary>
    private static readonly (string Type, string? SubType, int Level, ChassisSpecial Special, float? Dash)[] SpecialBodies = WiredCatalogue.Chassis
        .Select(c => (c.Type, c.SubType, c.Level, Special: WiredMachines.SpecialOf(c.TypeName, c.Level), c.Dash))
        .Where(c => c.Special != ChassisSpecial.None)
        .ToArray();

    /// <summary>
    /// The special of a machine's body (<see cref="WiredMachines.SpecialOf"/>): none outside a Wired match and for every
    /// level of the original's. Asked for every hit and every machine each tick, so it makes no name to look up.
    /// </summary>
    private ChassisSpecial BodySpecial(Machine machine) => SpecialBody(machine)?.Special ?? ChassisSpecial.None;

    private (string Type, string? SubType, int Level, ChassisSpecial Special, float? Dash)? SpecialBody(Machine machine)
    {
        if (Wired is null)
            return null;
        var level = machine.Stats.HardwareLevel;
        foreach (var body in SpecialBodies)
        {
            if (body.Level == level && body.Type == machine.Spec.Type && body.SubType == machine.Spec.SubType)
                return body;
        }
        return null;
    }

    /// <summary>What the run keeps in mind about a machine's body, begun when first needed.</summary>
    private WiredBody BodyOf(Machine machine)
    {
        var bodies = Wired!.Bodies;
        if (!bodies.TryGetValue(machine.Id, out var body))
            bodies[machine.Id] = body = new WiredBody { StillAt = machine.Position, StillSince = Time };
        return body;
    }

    /// <summary>A body the run keeps in mind already, begun by nothing: for questions, which change nothing.</summary>
    private WiredBody? KnownBody(Machine machine) => Wired?.Bodies.GetValueOrDefault(machine.Id);

    /// <summary>Whether that many seconds have gone by since a time, give or take half a tick, as times are sums of ticks.</summary>
    private bool Past(float since, float seconds) => Time - since >= seconds - Dt / 2;

    /// <summary>
    /// A tick of the bodies, after the fighting and before the heroes': the harvests are paid out, what stands still
    /// lurks, anchors lock and come free, the machines of the waves and of the squad use their bodies by themselves, and
    /// what a body's own dash throws along rams. The bodies of machines gone, fallen or on a chassis without a special of
    /// its body are forgotten first.
    /// </summary>
    private void UpdateBodies(WiredRun run)
    {
        List<int>? gone = null;
        foreach (var id in run.Bodies.Keys)
        {
            if (Find<Machine>(id) is not { IsDead: false } machine || BodySpecial(machine) == ChassisSpecial.None)
                (gone ??= []).Add(id);
        }
        if (gone is not null)
        {
            foreach (var id in gone)
                run.Bodies.Remove(id);
        }
        PayHarvests(run);
        foreach (var machine in Machines.Where(m => !m.IsDead && BodySpecial(m) != ChassisSpecial.None).OrderBy(m => m.Id).ToList())
        {
            var special = BodySpecial(machine);
            var body = BodyOf(machine);
            if ((special & ChassisSpecial.Lurk) != 0)
                UpdateLurk(machine, body);
            if ((special & ChassisSpecial.Anchor) != 0)
                UpdateAnchor(machine, body);
            else
                // A hero that changed from a Colossus to another body special's chassis stands free.
                body.Anchor = AnchorPhase.Free;
            DriveBody(run, machine, body, special);
            if (Time < body.DashUntil && !IsWiredHero(machine))
                UpdateBodyDash(machine, body, special);
        }
    }

    // ---- Old Iron: the plough and the ram ----------------------------------------------------------

    /// <summary>
    /// The share of a hit a body takes from <paramref name="dealer"/>: Old Iron's plough takes less from the front, an
    /// anchored Colossus less while its armour holds. Exactly 1 for any other body, so that no chance is drawn for it.
    /// </summary>
    private float BodyShare(Actor target, Actor? dealer)
    {
        if (target is not Machine machine)
            return 1;
        var special = BodySpecial(machine);
        if (special == ChassisSpecial.None)
            return 1;
        var share = 1f;
        // A round whose shooter is gone, a mine or a strike from the sky comes from nowhere the plough could face.
        if ((special & ChassisSpecial.Plough) != 0 && dealer is { IsDead: false } && InFront(machine, dealer.Position))
            share *= WiredBalance.PloughShare;
        if ((special & ChassisSpecial.Anchor) != 0 && machine.Armour > 0 && AnchorPhaseOf(machine) == AnchorPhase.Anchored)
            share /= WiredBalance.AnchorArmour;
        return share;
    }

    /// <summary>Whether a point lies in the front third of a machine's hull, 60 degrees to either side of where it faces.</summary>
    private static bool InFront(Machine machine, Vector2 point)
    {
        var way = point - machine.Position;
        if (way.LengthSquared() < 1e-6f)
            return false;
        var facing = new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing));
        return Vector2.Dot(Vector2.Normalize(way), facing) >= WiredBalance.PloughCos;
    }

    /// <summary>What the ram's hits count for: a weapon of their own in the statistics, the chips and the battle's tally.</summary>
    public const string RamWeapon = "RAM";

    /// <summary>How far one step of a shove goes at most, so that a machine thrown along is never set past a wall.</summary>
    private const float ShoveStep = 1;

    /// <summary>
    /// An Old Iron on a dash meets what stands in its way. Each enemy machine on the ground and out in the open that it
    /// comes to takes the ram's damage once a dash, as a hit of the Old Iron's, and is thrown on along the dash's way by
    /// as much as they overlap and the push beyond; anchored tracks hold, though. One that is no hero stops what it was
    /// doing ("it knocks over whatever it meets"); a hero, player's or bot's, is only thrown, so that nobody loses the wheel.
    /// </summary>
    private void Ram(Machine ram, WiredBody body, Vector2 way)
    {
        var victims = Machines.Where(m => m != ram && !m.IsDead && m.Inside is null && m.Stats.Locomotion != Locomotion.Glider
                && AreEnemies(ram.Race, m.Race) && !body.Rammed.Contains(m.Id))
            .OrderBy(m => m.Id).ToList();
        foreach (var victim in victims)
        {
            var reach = ram.Radius + victim.Radius + WiredBalance.RamMargin;
            var distanceSquared = Vector2.DistanceSquared(ram.Position, victim.Position);
            if (distanceSquared >= reach * reach)
                continue;
            body.Rammed.Add(victim.Id);
            Raise(new GameEvent(GameEventKind.WiredRammed, victim.Id, victim.Position, ram.Id));
            Damage(victim, WiredBalance.RamDamage, ram.Race, ram, RamWeapon);
            if (victim.IsDead)
                continue;
            if (!Rooted(victim))
                Shove(victim, way * (reach - MathF.Sqrt(distanceSquared) + WiredBalance.RamPush));
            if (!IsWiredHero(victim))
                StartAfresh(victim);
        }
    }

    /// <summary>Throws a machine along over open ground, in short steps, sliding along what is in the way.</summary>
    private void Shove(Machine machine, Vector2 push)
    {
        var steps = (int)MathF.Ceiling(push.Length() / ShoveStep);
        for (var i = 0; i < steps; i++)
        {
            if (!SlideOn(machine, push / steps))
                return;
        }
    }

    /// <summary>
    /// A machine that is no hero dashes by its body's own dash, as a hero does on its thrusters: an Old Iron of the waves
    /// or of the squad, whose driver decides when. False if it cannot now: no dash of its own, anchored, indoors or not
    /// ready again.
    /// </summary>
    private bool BodyDash(Machine machine, Vector2 direction)
    {
        if (SpecialBody(machine) is not { Dash: { } cooldown } || IsWiredHero(machine) || machine.Inside is not null || Rooted(machine))
            return false;
        var body = BodyOf(machine);
        if (Time < body.DashReadyAt)
            return false;
        body.DashWay = direction.LengthSquared() > 1e-4f ? Vector2.Normalize(direction) : new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing));
        body.DashReadyAt = Time + cooldown;
        body.DashUntil = Time + WiredBalance.DashTime;
        body.Rammed.Clear();
        StartAfresh(machine);
        Raise(new GameEvent(GameEventKind.WiredDashed, machine.Id, machine.Position));
        return true;
    }

    /// <summary>While a body's own dash lasts it throws the machine along as the hero's does, and an Old Iron rams.</summary>
    private void UpdateBodyDash(Machine machine, WiredBody body, ChassisSpecial special)
    {
        if (machine.Inside is not null || Rooted(machine))
            return;
        SlideOn(machine, body.DashWay * (WiredBalance.DashSpeed * Dt));
        if ((special & ChassisSpecial.Plough) != 0)
            Ram(machine, body, body.DashWay);
    }

    // ---- the Harvester -----------------------------------------------------------------------------

    /// <summary>
    /// A hit has landed, <paramref name="before"/> being the target's hit points before it. A Harvester whose own hit
    /// killed an enemy machine near it, the last-ditch chips having had their say, gets a share of its armour back; a
    /// hero's harvest in a run also pulls in the wrecks there, once they lie (<see cref="PayHarvests"/>).
    /// </summary>
    private void AfterHit(Actor target, int before, Actor? dealer)
    {
        if (before <= 0 || !target.IsDead || target is not Machine victim || dealer is not Machine { IsDead: false } harvester
            || (BodySpecial(harvester) & ChassisSpecial.Harvest) == 0 || !AreEnemies(harvester.Race, victim.Race)
            || harvester.DistanceTo(victim) > WiredBalance.HarvestReach)
            return;
        var armour = Math.Min(harvester.MaxArmour, harvester.Armour + (int)MathF.Round(harvester.MaxArmour * WiredBalance.HarvestArmour));
        var gained = armour - harvester.Armour;
        harvester.Durability = harvester.Durability with { Armour = armour };
        Raise(new GameEvent(GameEventKind.WiredHarvest, harvester.Id, harvester.Position, victim.Id, gained.ToString()));
        if (Battle is null && HeroStateOf(harvester) is { } state)
            state.Harvests.Add(victim.Position);
    }

    /// <summary>
    /// The wrecks within reach of a hero's harvests are paid out to it as if it had driven over them, now that the dead
    /// have been taken off and lie as wrecks. A harvest of a hero that is down is lost; its wrecks still lie.
    /// </summary>
    private void PayHarvests(WiredRun run)
    {
        foreach (var state in run.Heroes.Values)
        {
            if (state.Harvests.Count == 0)
                continue;
            if (HeroOf(state) is { } hero)
                PayHarvest(state, hero);
            state.Harvests.Clear();
        }
    }

    private void PayHarvest(WiredState state, Machine hero)
    {
        const float reach = WiredBalance.HarvestReach * WiredBalance.HarvestReach;
        var build = state.Build(Data);
        for (var i = 0; i < Debris.Count; i++)
        {
            var wreck = Debris[i];
            var near = false;
            foreach (var at in state.Harvests)
                near |= Vector2.DistanceSquared(at, wreck.Position) <= reach;
            if (!near)
                continue;
            var bmu = (int)(wreck.Quantity * build.WreckShare);
            Debris.RemoveAt(i--);
            Earn(state, bmu);
            state.Stats.WrecksCollected++;
            Raise(new GameEvent(GameEventKind.WiredWreckCollected, hero.Id, wreck.Position, wreck.Id, bmu.ToString()));
        }
    }

    // ---- the Widow ---------------------------------------------------------------------------------

    // The weapons as the lurk and the anchor tune them, made once for each weapon as made.
    private readonly Dictionary<WeaponSpec, WeaponSpec> _lurking = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WeaponSpec, WeaponSpec> _anchored = new(ReferenceEqualityComparer.Instance);

    private WeaponSpec Lurking(WeaponSpec weapon) => _lurking.TryGetValue(weapon, out var tuned) ? tuned
        : _lurking[weapon] = weapon with { Range = weapon.Range * WiredBalance.LurkRange, Damage = weapon.Damage * WiredBalance.LurkDamage };

    private WeaponSpec Anchored(WeaponSpec weapon) => _anchored.TryGetValue(weapon, out var tuned) ? tuned
        : _anchored[weapon] = weapon with { Range = weapon.Range * WiredBalance.AnchorRange, ReloadTime = weapon.ReloadTime * WiredBalance.AnchorReload };

    private static bool IsTuned(WeaponMount weapon) => !ReferenceEquals(weapon.Spec, weapon.Base);

    /// <summary>Fires as <paramref name="tuned"/>, or as made with null; nothing changes if it does already.</summary>
    private static void TuneTo(WeaponMount weapon, WeaponSpec? tuned)
    {
        if (!ReferenceEquals(weapon.Spec, tuned ?? weapon.Base))
            weapon.Tune(tuned);
    }

    /// <summary>Whether a machine fired a round of a weapon this tick.</summary>
    private bool FiredThisTick(Machine machine, string weapon) =>
        _events.Exists(e => e.Kind == GameEventKind.Fired && e.Actor == machine.Id && e.Detail == weapon);

    /// <summary>Whether a Widow lurks: it has stood still long enough out in the open and not fired since.</summary>
    private bool Lurks(Machine machine, WiredBody body) =>
        !body.LurkSpent && machine.Inside is null && Past(body.StillSince, WiredBalance.LurkStill);

    /// <summary>
    /// The Widow's lurk, after the fighting of the tick. Its first round fired while it lurked spends the lurk; the rest
    /// of that burst flies as the first did, and every other weapon fires as made at once. Moved off where it stood, it
    /// stands anew, and lurks again once it has stood still long enough; turning on the spot is standing still. While it
    /// lurks every weapon that deals damage reaches further and hurts more.
    /// </summary>
    private void UpdateLurk(Machine machine, WiredBody body)
    {
        // Whether it lurked through this tick's fighting, as the last tick left it.
        var lurked = !body.LurkSpent && (Tick - 1) * Dt - body.StillSince >= WiredBalance.LurkStill - Dt / 2;
        var spent = lurked && machine.Weapons.Exists(w => IsTuned(w) && FiredThisTick(machine, w.Spec.TypeName));
        if (spent)
            body.LurkSpent = true;
        if (Vector2.DistanceSquared(machine.Position, body.StillAt) > WiredBalance.LurkDrift * WiredBalance.LurkDrift)
        {
            body.StillAt = machine.Position;
            body.StillSince = Time;
            body.LurkSpent = false;
        }
        var lurks = Lurks(machine, body);
        // A weapon is told by its type here, not by its mount, so that the copies of a battery triggered with the first,
        // whose first rounds follow its round by their turns of a reload, keep their part of the one first shot until
        // their bursts are out. Two weapons of one type on different slots that open fire a tick apart are one shot as well.
        foreach (var weapon in machine.Weapons)
        {
            if (lurks && weapon.DealsDamage)
                TuneTo(weapon, Lurking(weapon.Base));
            else if (!(IsTuned(weapon) && weapon.RoundsLeft > 0 && (!spent || FiredThisTick(machine, weapon.Spec.TypeName))))
                TuneTo(weapon, null);
        }
    }

    // ---- the Colossus ------------------------------------------------------------------------------

    /// <summary>Where a machine's anchor stands: free for any machine without one, and outside a Wired match.</summary>
    public AnchorPhase AnchorPhaseOf(Machine machine) =>
        KnownBody(machine) is { Anchor: not AnchorPhase.Free } body && (BodySpecial(machine) & ChassisSpecial.Anchor) != 0 ? body.Anchor : AnchorPhase.Free;

    /// <summary>Whether a machine's tracks are locked, or locking or coming free, so that it goes nowhere.</summary>
    private bool Rooted(Machine machine) => AnchorPhaseOf(machine) != AnchorPhase.Free;

    /// <summary>
    /// What a rooted machine without a driver does instead of its order, which waits for the tracks to come free: it turns
    /// its hull to what it fights, its attack's target or else the nearest enemy in its reach that it has a clear shot at,
    /// as it picks one to fire at standing its ground; with no clear shot at any, the nearest. An attack on what is gone
    /// ends as any does.
    /// </summary>
    private void HoldRooted(Machine machine)
    {
        if (machine.Order is AttackOrder attack && FindTarget(attack.Target) is var lost
            && (lost is null || lost.IsDead || !CanAttack(machine.Race, lost) || !CanFireAt(machine, lost) || Indoors(lost) || Shadowed(machine, lost)))
            UpdateAttack(machine, attack);
        var reach = MaximumWeaponRange(machine);
        var target = machine.Order is AttackOrder still && FindTarget(still.Target) is { IsDead: false } fought && CanAttack(machine.Race, fought)
            ? fought
            : NearestEnemy(machine, reach, clearShot: true) ?? NearestEnemy(machine, reach);
        if (target is not null && !machine.CanTurnUpperBody)
            Face(machine, target.Position);
    }

    /// <summary>The anchor's phases go by: locked after its time, free after its time; anchored, every weapon that deals damage reaches further and reloads sooner.</summary>
    private void UpdateAnchor(Machine machine, WiredBody body)
    {
        if (body.Anchor == AnchorPhase.Locking && Past(body.AnchorSince, WiredBalance.AnchorTime))
            SetAnchor(machine, body, AnchorPhase.Anchored, GameEventKind.WiredAnchored);
        else if (body.Anchor == AnchorPhase.Releasing && Past(body.AnchorSince, WiredBalance.AnchorTime))
            SetAnchor(machine, body, AnchorPhase.Free, GameEventKind.WiredReleased);
        TuneAnchor(machine, body);
    }

    /// <summary>Tunes the weapons as the anchor stands, again each tick: a refit's new mounts are tuned at the next.</summary>
    private void TuneAnchor(Machine machine, WiredBody body)
    {
        var anchored = body.Anchor == AnchorPhase.Anchored;
        foreach (var weapon in machine.Weapons)
            TuneTo(weapon, anchored && weapon.DealsDamage ? Anchored(weapon.Base) : null);
    }

    private void SetAnchor(Machine machine, WiredBody body, AnchorPhase phase, GameEventKind kind)
    {
        body.Anchor = phase;
        body.AnchorSince = Time;
        Raise(new GameEvent(kind, machine.Id, machine.Position));
    }

    /// <returns>
    /// null if the hero's Colossus can anchor (<paramref name="on"/>) or be released now, otherwise why not. Asking for
    /// what it is doing already is no fault: a command sent twice does nothing the second time.
    /// </returns>
    public string? WhyNoAnchor(WiredState state, bool on)
    {
        if (HeroOf(state) is not { } hero)
            return "the hero is down";
        if ((BodySpecial(hero) & ChassisSpecial.Anchor) == 0)
            return "this chassis has nothing to use";
        return on && AnchorPhaseOf(hero) == AnchorPhase.Releasing ? "the tracks are being released" : null;
    }

    /// <summary>
    /// Locks the hero's tracks, or lets them go: free it starts locking, locking it is free again at once (they had not
    /// locked yet), anchored it starts coming free. What it was rolling stops dead as the tracks begin to lock.
    /// </summary>
    private string? Anchor(WiredState state, bool on)
    {
        if (WhyNoAnchor(state, on) is { } why)
            return why;
        var hero = HeroOf(state)!;
        if (on && AnchorPhaseOf(hero) == AnchorPhase.Free)
            Lock(hero, BodyOf(hero));
        else if (!on && KnownBody(hero) is { } body)
            Release(hero, body);
        return null;
    }

    /// <summary>The tracks begin to lock, and what the machine was rolling stops dead.</summary>
    private void Lock(Machine machine, WiredBody body)
    {
        if (_motion.TryGetValue(machine.Id, out var motion))
        {
            AnnounceStop(machine, motion);
            StopDead(motion);
        }
        SetAnchor(machine, body, AnchorPhase.Locking, GameEventKind.WiredAnchoring);
    }

    /// <summary>The tracks let go: locking they are free again at once, as they had not locked yet; anchored they start coming free.</summary>
    private void Release(Machine machine, WiredBody body)
    {
        if (body.Anchor == AnchorPhase.Locking)
            SetAnchor(machine, body, AnchorPhase.Free, GameEventKind.WiredReleased);
        else if (body.Anchor == AnchorPhase.Anchored)
        {
            SetAnchor(machine, body, AnchorPhase.Releasing, GameEventKind.WiredReleasing);
            TuneAnchor(machine, body);
        }
    }

    /// <summary>A machine set down somewhere else stands free there at once, its weapons as made.</summary>
    private void CutLoose(Machine machine)
    {
        if (KnownBody(machine) is not { Anchor: not AnchorPhase.Free } body)
            return;
        SetAnchor(machine, body, AnchorPhase.Free, GameEventKind.WiredReleased);
        TuneAnchor(machine, body);
    }

    // ---- the bodies of the waves and of the squad --------------------------------------------------

    /// <summary>
    /// What a machine of the waves or of the squad does with its body by itself: a Colossus of the waves anchors at what it
    /// can hit anchored, one of the squad while it holds its ground, and an Old Iron rams what it attacks. A Widow lurks
    /// wherever it stands still and a Hornet of theirs never dashes, so neither needs more. Not in a battle, whose machines
    /// are all heroes.
    /// </summary>
    private void DriveBody(WiredRun run, Machine machine, WiredBody body, ChassisSpecial special)
    {
        if (Battle is not null || machine.Inside is not null || IsWiredHero(machine))
            return;
        if ((special & ChassisSpecial.Anchor) != 0)
        {
            if (machine.Race == run.Enemy)
                AnchorAgainst(run, machine, body);
            else
                AnchorOnHold(machine, body);
        }
        if ((special & ChassisSpecial.Plough) != 0)
            RamAhead(machine, body);
    }

    /// <summary>
    /// A Colossus of the waves anchors as soon as it has a clear shot at something within its anchored reach: a besieger
    /// only at the pod, any other at the enemy nearest, pod and towers included. Unless it attacks something there already,
    /// it then attacks that, going on afterwards to where it was bound. Its order waits while the tracks hold. Anchored
    /// with nothing to hit for <see cref="WiredBalance.AnchorIdle"/> seconds it lets go.
    /// </summary>
    private void AnchorAgainst(WiredRun run, Machine machine, WiredBody body)
    {
        var reach = AnchoredReach(machine);
        var fighting = machine.Order is AttackOrder attack && FindTarget(attack.Target) is { } fought && Marks(machine, fought, reach) ? fought : null;
        Actor? mark = run.Foes.Exists(f => f.Machine == machine && f.Plan.Role == WiredRole.Besieger)
            ? AttackedPod(run) is { } pod && Marks(machine, pod, reach) ? pod : null
            : fighting ?? NearestEnemy(machine, reach, clearShot: true);
        if (mark is null)
        {
            if (body.Anchor == AnchorPhase.Anchored && Past(body.AnchorIdleSince, WiredBalance.AnchorIdle))
                Release(machine, body);
            return;
        }
        body.AnchorIdleSince = Time;
        if (fighting is null)
            Engage(run, machine, mark);
        if (body.Anchor == AnchorPhase.Free)
            Lock(machine, body);
    }

    /// <summary>A Colossus of the squad anchors while it holds its ground, and lets go at any other order, which waits for the tracks to come free.</summary>
    private void AnchorOnHold(Machine machine, WiredBody body)
    {
        if (machine.Order is not StandGroundOrder)
            Release(machine, body);
        else if (body.Anchor == AnchorPhase.Free)
            Lock(machine, body);
    }

    /// <summary>How far a Colossus hits anchored: its furthest weapon that deals damage as made, as the anchor tunes it, at the share of the range it opens fire at.</summary>
    private static float AnchoredReach(Machine machine) =>
        machine.Weapons.Where(w => w.DealsDamage).Select(w => w.Base.Range).DefaultIfEmpty(0).Max() * WiredBalance.AnchorRange * FiringRangeShare;

    /// <summary>Whether a machine could fight a target from where it stands, within <paramref name="reach"/> and with a clear shot.</summary>
    private bool Marks(Machine machine, Actor target, float reach) =>
        !target.IsDead && CanAttack(machine.Race, target) && CanFireAt(machine, target) && !Indoors(target) && !Shadowed(machine, target)
        && machine.DistanceTo(target) <= reach && ClearShot(machine, target);

    /// <summary>A machine of the waves attacks its mark, and goes on afterwards to where it was bound, the pod's door at the least.</summary>
    private void Engage(WiredRun run, Machine machine, Actor mark)
    {
        var then = machine.Order switch
        {
            AttackMoveOrder move => move.Target,
            AttackOrder attack => attack.Then,
            _ => AttackedPod(run) is { } pod ? Doorstep(pod) : (Vector2?)null,
        };
        Send(machine, new AttackOrder(mark.Id, Then: then), null);
    }

    /// <summary>
    /// An Old Iron of the waves or of the squad dashes at the enemy machine it attacks once that is within
    /// <see cref="WiredBalance.RamReach"/> in the front third of its hull and its dash is ready, so the dash rams it. Its
    /// attack goes on.
    /// </summary>
    private void RamAhead(Machine machine, WiredBody body)
    {
        if (Time < body.DashReadyAt || machine.Order is not AttackOrder attack
            || Find<Machine>(attack.Target) is not { IsDead: false, Inside: null } target || target.Stats.Locomotion == Locomotion.Glider
            || !AreEnemies(machine.Race, target.Race)
            || Vector2.DistanceSquared(machine.Position, target.Position) > WiredBalance.RamReach * WiredBalance.RamReach
            || !InFront(machine, target.Position))
            return;
        if (BodyDash(machine, target.Position - machine.Position))
            machine.Order = attack;
    }

    // ---- the Marshal -------------------------------------------------------------------------------

    /// <summary>The accuracy a Marshal hero near a machine gives it.</summary>
    private float FieldAim(Machine machine) => InField(machine) ? WiredBalance.FieldAim : 0;

    /// <summary>The share of its repair a Marshal hero near a machine adds to it.</summary>
    private float FieldRepair(Machine machine) => InField(machine) ? WiredBalance.FieldRepair : 0;

    /// <summary>
    /// Whether a Marshal hero out in the open stands near a machine of its own: in a run an escort of its squad, in a
    /// battle the hero of a team-mate, of its own colour too. Never the Marshal itself, and two near one count as one.
    /// </summary>
    private bool InField(Machine machine)
    {
        if (Wired is not { } run)
            return false;
        if (Battle is not { } battle)
            return run.Heroes.GetValueOrDefault(machine.Owner) is { } state && state.EscortIds.Contains(machine.Id) && NearMarshal(state, machine);
        var team = battle.TeamOf(machine.Owner);
        if (team < 0 || HeroStateOf(machine) is null)
            return false;
        foreach (var (player, mate) in run.Heroes)
        {
            if (player != machine.Owner && battle.TeamOf(player) == team && NearMarshal(mate, machine))
                return true;
        }
        return false;
    }

    private bool NearMarshal(WiredState state, Machine machine) =>
        (WornSpecial(state) & ChassisSpecial.Field) != 0 && HeroOf(state) is { Inside: null } marshal && marshal != machine
        && Vector2.DistanceSquared(marshal.Position, machine.Position) <= WiredBalance.FieldReach * WiredBalance.FieldReach;

    /// <summary>The escorts a rally would call in: those standing and out in the open, not aboard a building or a carrier.</summary>
    private IEnumerable<Machine> Callable(WiredState state) => Escorts(state).Where(e => e.Inside is null);

    /// <returns>null if the hero's Marshal can call its squad in now, otherwise why not.</returns>
    public string? WhyNoRally(WiredState state)
    {
        if (Battle is not null)
            return "there is no squad in a battle";
        if (HeroOf(state) is null)
            return "the hero is down";
        if ((WornSpecial(state) & ChassisSpecial.Field) == 0)
            return "this chassis has nothing to use";
        if (!Callable(state).Any())
            return "there is no squad to call";
        return Time < state.RallyReadyAt ? "that is not ready yet" : null;
    }

    /// <summary>
    /// The squad teleports to the Marshal, the escorts in the order of their ids to free spots round it, and follows it
    /// from there, as a stuck machine of the waves is fetched. The next rally waits its time from now.
    /// </summary>
    private string? Rally(WiredState state)
    {
        if (WhyNoRally(state) is { } why)
            return why;
        var hero = HeroOf(state)!;
        var moved = 0;
        foreach (var escort in Callable(state).OrderBy(e => e.Id).ToList())
        {
            var old = escort.Position;
            StartAfresh(escort);
            CutLoose(escort);
            escort.Position = FreeSpotNear(hero.Position, MathF.Max(1, escort.Stats.LowClearance));
            Send(escort, new FollowOrder(hero.Id), hero.Position);
            Raise(new GameEvent(GameEventKind.WiredSquadRallied, escort.Id, old));
            moved++;
        }
        if (moved > 0)
            state.RallyReadyAt = Time + WiredBalance.RallyCooldown;
        return null;
    }

    // ---- what the HUD shows ------------------------------------------------------------------------

    /// <summary>
    /// What the HUD shows of the special of the hero's body, or of its dash for a chassis whose special that is. Ready and
    /// Why always answer for the key that works it; the lurk and the harvest have none, so they say what the special key
    /// says, and the lurk shows where it stands as its phase.
    /// </summary>
    private WiredSpecialView? BodyView(WiredState state)
    {
        var special = WornSpecial(state);
        var hero = HeroOf(state);
        if ((special & ChassisSpecial.Anchor) != 0)
            return AnchorView(state, special, hero);
        if ((special & ChassisSpecial.Field) != 0)
            return RallyView(state, special, hero);
        if ((special & ChassisSpecial.Lurk) != 0)
            return LurkView(state, special, hero);
        if ((special & ChassisSpecial.Plough) != 0)
            return DashView(state, special, "Ram");
        if ((special & ChassisSpecial.Afterburner) != 0)
            return DashView(state, special, "Afterburner");
        if ((special & ChassisSpecial.Harvest) != 0)
        {
            var why = WhyNoSpecial(state);
            return new WiredSpecialView(special, "Harvest", null, 0, 0, 0, 0, why is null, why);
        }
        return null;
    }

    private WiredSpecialView AnchorView(WiredState state, ChassisSpecial special, Machine? hero)
    {
        var phase = hero is null ? AnchorPhase.Free : AnchorPhaseOf(hero);
        var since = hero is not null && KnownBody(hero) is { } body ? MathF.Max(0, Time - body.AnchorSince) : 0;
        var done = MathF.Min(1, since / WiredBalance.AnchorTime);
        var (left, share) = phase switch
        {
            AnchorPhase.Locking => (MathF.Max(0, WiredBalance.AnchorTime - since), done),
            AnchorPhase.Anchored => (0f, 1f),
            AnchorPhase.Releasing => (MathF.Max(0, WiredBalance.AnchorTime - since), 1 - done),
            _ => (0f, 0f),
        };
        // The key anchors a free Colossus and lets go of one that is locking or anchored.
        var why = WhyNoSpecial(state, phase is AnchorPhase.Free or AnchorPhase.Releasing);
        return new WiredSpecialView(special, "Anchor", phase.ToString(), left, share, 0, 0, why is null, why);
    }

    private WiredSpecialView RallyView(WiredState state, ChassisSpecial special, Machine? hero)
    {
        var left = MathF.Max(0, state.RallyReadyAt - Time);
        var squad = Escorts(state).ToList();
        var near = hero is null ? 0 : squad.Count(e => e.Inside is null
            && Vector2.DistanceSquared(e.Position, hero.Position) <= WiredBalance.FieldReach * WiredBalance.FieldReach);
        var why = WhyNoSpecial(state);
        return new WiredSpecialView(special, "Rally", null, left, 1 - MathF.Min(1, left / WiredBalance.RallyCooldown), near, squad.Count, why is null, why);
    }

    /// <summary>
    /// The lurk light: Standing while it fills (Share, the seconds left), Lurking once lit, Spent when the Widow has fired
    /// from where it stands.
    /// </summary>
    private WiredSpecialView LurkView(WiredState state, ChassisSpecial special, Machine? hero)
    {
        var body = hero is null ? null : KnownBody(hero);
        var lurks = hero is not null && body is not null && Lurks(hero, body);
        var spent = body?.LurkSpent == true;
        var standing = body is null ? 0 : MathF.Max(0, Time - body.StillSince);
        var left = lurks || spent ? 0 : MathF.Max(0, WiredBalance.LurkStill - standing);
        var share = lurks ? 1 : spent ? 0 : MathF.Min(1, standing / WiredBalance.LurkStill);
        var why = WhyNoSpecial(state);
        return new WiredSpecialView(special, "Lurk", lurks ? "Lurking" : spent ? "Spent" : "Standing", left, share, 0, 0, why is null, why);
    }

    private WiredSpecialView DashView(WiredState state, ChassisSpecial special, string name)
    {
        var cooldown = state.Build(Data).DashCooldown ?? 0;
        var left = MathF.Max(0, state.DashReadyAt - Time);
        var why = WhyNoDash(state);
        return new WiredSpecialView(special, name, null, left, cooldown > 0 ? 1 - MathF.Min(1, left / cooldown) : 1, 0, 0, why is null, why);
    }
}
