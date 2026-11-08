using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    private readonly Dictionary<int, int> _turretTargets = [];

    // What the player told missile emplacements to shoot at.
    private readonly Dictionary<int, int> _turretOrders = [];

    /// <summary>What each machine fighting on its own last chose to fire at; for the view only.</summary>
    private readonly Dictionary<int, int> _machineAims = [];

    /// <summary>
    /// What a tower or a machine fighting on its own (not under an attack order) is firing at, for its head or
    /// upper body to turn to; null when nothing. The simulation itself does not read it.
    /// </summary>
    public Actor? AimOf(int id) =>
        _turretTargets.TryGetValue(id, out var target) || _machineAims.TryGetValue(id, out target) ? Find(target) : null;

    /// <summary>
    /// Where a machine driven in first person is firing while the trigger is held: the point under the crosshair,
    /// whether or not anything stands there, and how high it lies (null for the middle of what stands there, as
    /// older clients aimed); set by the client.
    /// </summary>
    public Dictionary<int, (Vector2 Point, float? Height)> PlayerAims { get; } = [];

    /// <summary>Which weapons of a machine driven in first person may fire: a bit for each, in mounting order.</summary>
    public Dictionary<int, int> PlayerWeapons { get; } = [];

    /// <summary>
    /// Machines driven with a button for each weapon: <see cref="PlayerWeapons"/> then says only which triggers are held,
    /// and the others go on reloading. In a Wired run that holds for every machine, with or without this.
    /// </summary>
    public HashSet<int> PlayerButtons { get; } = [];

    /// <summary>
    /// Where the upper body of a machine driven in first person is turned, radians anticlockwise from its hull; for
    /// the view only.
    /// </summary>
    public Dictionary<int, float> PlayerHeads { get; } = [];

    /// <summary>
    /// The ground's heights, when every player has the same (the original engine's, from the game copy); null
    /// leaves shots unhindered by hills.
    /// </summary>
    public TerrainHeights? Terrain { get; set; }

    /// <summary>
    /// Whether a shot from one actor at another has a clear way: the straight line between their middles
    /// (MachLogCanAttack::IHaveClearWeaponPathToHim, testActorBehindCover). The ground, buildings, artefacts and the shooter's own
    /// and its allies' machines stand in the way; an enemy's machines do not, it is glad to hit those (MachLogAttackFilter).
    /// </summary>
    public bool ClearShot(Actor shooter, Actor target)
    {
        var (from, to) = (AimPoint(shooter), AimPoint(target));
        if (Terrain is { } ground && ground.FirstBlocked(from, to, ends: 3) is not null)
            return false;
        return FirstOnLine(from, to, actor => actor != shooter && actor != target && (actor is not Machine || !AreEnemies(shooter.Race, actor.Race))) is null;
    }

    /// <summary>
    /// An actor's middle in the air, as the original aimed between its bounding box's corners: a little over a
    /// machine's own height, higher for a flyer, a few metres up a building.
    /// </summary>
    public Vector3 AimPoint(Actor actor)
    {
        var ground = Terrain?.At(actor.Position) ?? 0;
        var up = actor switch
        {
            Machine { Stats.Locomotion: Locomotion.Glider } => 10,
            Machine machine => MathF.Max(1.2f, machine.Radius * 0.6f),
            Construction building => MathF.Min(6, 1 + building.Radius * 0.3f),
            Artefact artefact => MathF.Max(1, (artefact.Type.Height ?? 2) * 0.5f),
            _ => 1,
        };
        return new Vector3(actor.Position, ground + up);
    }

    /// <summary>What a machine is firing at: only an attack fires (MachLogAttackOperation), and one standing its ground at the nearest it can hit; nothing while it is busy.</summary>
    private Actor? FiringAt(Machine machine) => Busy(machine) ? null : machine.Order switch
    {
        AttackOrder attack => FindTarget(attack.Target),
        StandGroundOrder => NearestEnemy(machine, MaximumWeaponRange(machine), clearShot: true),
        _ => null,
    };

    private void UpdateCombat()
    {
        UpdateDarkness();
        foreach (var machine in Machines.Where(m => m.CanFight && !m.IsDead && m.Inside is null).ToList())
        {
            // MachLogCanAttack::diminishAlertnessAndInaccuracy.
            GunneryOf(machine).Settle(Dt / 4);
            if (machine.DrivenByPlayer)
            {
                var selected = PlayerWeapons.GetValueOrDefault(machine.Id, -1);
                (Vector2 Point, float? Height)? sight = PlayerAims.TryGetValue(machine.Id, out var aim) ? aim : null;
                if (Wired is not null || PlayerButtons.Contains(machine.Id))
                    Fire(machine, machine.Weapons, null, sight, selected);
                else
                    Fire(machine, machine.Weapons.Where((_, i) => (selected & (1 << i)) != 0).ToList(), null, sight);
                continue;
            }
            var target = FiringAt(machine);
            if (target is null)
                _machineAims.Remove(machine.Id);
            else
                _machineAims[machine.Id] = target.Id;
            TurnHead(machine, target);
            Fire(machine, machine.Weapons, target);
        }

        ArmPods();
        foreach (var turret in Constructions.Where(c => c.Complete && c.Weapons.Count > 0 && !c.IsDead).ToList())
        {
            // Strike weapons only charge here; they fire on command.
            foreach (var strike in turret.Weapons.Where(w => w.IsStrike && w.Reload > 0))
            {
                strike.Reload -= Dt;
                if (strike.Reload <= 0 && strike.Spec.TypeName == "ION_ORBITAL_CANNON")
                    SendVoiceMail(turret.Race, "VID_POD_ION_CANNON_CHARGED");
            }
            UpdateDome(turret);
            if (turret.Weapons.All(w => w.IsStrike))
                continue;

            // A tower told what to shoot at sticks to that; otherwise it looks for itself.
            GunneryOf(turret).Settle(Dt / 4);
            var range = turret.Weapons.Where(w => !w.IsStrike).Max(w => w.Spec.Range);
            var ordered = OrderedTarget(turret, range);
            if (ordered is null)
                LookOut(turret);
            var target = ordered ?? TowerTarget(turret);
            if (target is null)
                _turretTargets.Remove(turret.Id);
            else
                _turretTargets[turret.Id] = target.Id;
            TurnHead(turret, target);
            Fire(turret, turret.Weapons.Where(w => !w.IsStrike).ToList(), target);
        }

        UpdateProjectiles();
        UpdateSprays();
        UpdateBlasts();
    }

    /// <summary>Whether an emplacement is firing at what it was told to.</summary>
    public bool HasTurretOrder(Construction turret) => _turretOrders.ContainsKey(turret.Id);

    /// <summary>Whether a building takes attack orders: the missile emplacements, all but the silo (MachGuiAttackCommand::canActorEverExecute).</summary>
    public static bool TakesOrders(Construction building) => building.IsType("MISSILE_EMPLACEMENT") && building.Spec.SubType != "ICBM" && building.Complete;

    /// <summary>How far a turret's scanner reaches past its weapons (MachLogStats's default for TURRET_SCANNER_RANGE_MULT).</summary>
    private const float TurretScanner = 1.4f;

    /// <summary>How close a sound launcher gives up on a target (MachLogMissileEmplacementAttackOperation::doIsFinished).</summary>
    private const float LauncherMinimum = 25;

    /// <summary>
    /// What a missile emplacement was told to shoot at, for as long as its scanner sees it; it stands waiting while
    /// the target is out of range (MachLogMissileEmplacementAttackOperation).
    /// </summary>
    private Actor? OrderedTarget(Construction turret, float range)
    {
        if (!_turretOrders.TryGetValue(turret.Id, out var id))
            return null;
        if (FindTarget(id) is { IsDead: false } target && CanAttack(turret.Race, target) && turret.DistanceTo(target) <= range * TurretScanner
            && !(turret.Spec.SubType == "LAUNCHER" && turret.HitPoints > turret.MaxHitPoints * 0.25f && turret.DistanceTo(target) < LauncherMinimum))
            return target;
        _turretOrders.Remove(turret.Id);
        return null;
    }

    /// <summary>
    /// Applies damage with the original armour rule, remembers who did it, for the score, and lets the target react to
    /// the <paramref name="shooter"/> where there is one. A blast has none to react to; <paramref name="blaster"/> is the
    /// machine that set it off, for a run's chips and statistics.
    /// </summary>
    public void Damage(Actor target, int damage, Race? by, Actor? shooter = null, string? weapon = null, int? blaster = null)
    {
        if (target.IsDead || target is Artefact && !Options.DestructibleArtefacts)
            return;
        var dealer = shooter ?? (Wired is not null && blaster is { } id ? Find(id) : null);
        if (Wired is not null && !WiredHit(target, ref damage, dealer, weapon))
            return;
        var before = target.HitPoints;
        var armour = target.Armour;
        target.Durability = ArmourModel.ApplyHit(target.Durability, damage);
        if (Wired is not null)
        {
            WiredStruck(target, armour, shooter, weapon);
            NoteDamage(target, before + armour - Math.Max(0, target.HitPoints) - target.Armour, by, dealer, weapon);
        }
        if (target is Machine { CanFight: true } shaken)
            Shake(shaken, before - target.HitPoints);
        if (by is not null)
            target.LastHitBy = by;
        if (by is { } foe && foe != target.Race && !target.IsDead)
            ReportHit(target);
        ReactToHit(target, shooter);
    }

    /// <summary>
    /// Runs each weapon's burst and reload cycle: RELOAD_TIME seconds from the start of one burst to the next, its rounds
    /// spread over the burst's duration. A weapon opens fire at what lies in its reach and its arc, not behind cover, and
    /// all the while a machine has that it goes on aiming. A machine driven in first person fires at
    /// <paramref name="aimPoint"/> instead, at whatever is there or not. Only weapons with their bit in
    /// <paramref name="triggers"/> open fire; the others reload and finish their bursts all the same.
    /// </summary>
    private void Fire(Actor shooter, List<WeaponMount> weapons, Actor? target, (Vector2 Point, float? Height)? aimPoint = null, int triggers = -1)
    {
        var inSights = aimPoint is null && target is { IsDead: false } && CanAttack(shooter.Race, target) && CanFireAt(shooter, target)
            && weapons.Any(w => w.FiresAt(target) && InReach(shooter, w, target) && InArc(shooter, w, target)) && ClearShot(shooter, target);
        if (inSights && shooter is Machine)
        {
            Steady(shooter, target!);
            GunneryOf(shooter).LastAimed = Time;
        }
        FireData? aim = null;
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            if (weapon.Reload > 0)
                weapon.Reload -= Dt;
            if (weapon.RoundsLeft > 0)
            {
                weapon.RoundTimer -= Dt;
                if (weapon.RoundTimer > 0)
                    continue;

                weapon.RoundsLeft--;
                weapon.RoundTimer = weapon.Spec.BurstDuration / Math.Max(1, weapon.Spec.BurstRounds);
                if (aimPoint is { } sight)
                    FireAtPoint(shooter, weapon, sight.Point, sight.Height);
                else if (target is { IsDead: false } && shooter.DistanceTo(target) <= weapon.Spec.Range && CanAttack(shooter.Race, target))
                    FireRound(shooter, weapon, target);
                continue;
            }
            if (weapon.Reload > 0 || (triggers & (1 << i)) == 0)
                continue;

            var triggered = aimPoint is not null
                ? weapon.DealsDamage || weapon.IsTreachery
                : weapon.FiresAt(target) && target is { IsDead: false } && (weapon.Spec.TypeName == "GORILLA_PUNCH"
                    ? shooter.DistanceTo(target) <= weapon.Spec.Range && CanFireAt(shooter, target) && PunchesHere(shooter)
                    : inSights && InReach(shooter, weapon, target) && InArc(shooter, weapon, target));
            if (triggered)
            {
                // Fired at a point, a weapon gives its full burst and its rounds go where they are sent, in a Wired run about there.
                weapon.Burst = aimPoint is not null ? new FireData(0.9999f) : aim ??= TakeAim(shooter, target!);
                weapon.RoundsLeft = RoundsIn(weapon.Spec, weapon.Burst);
                weapon.RoundTimer = 0;
                weapon.Reload = weapon.Spec.ReloadTime;
            }
        }
    }

    /// <summary>A round fired at a target: what becomes of it depends on the weapon (each MachLogWeapon's doFire).</summary>
    private void FireRound(Actor shooter, WeaponMount weapon, Actor target)
    {
        NoteFiredAt(shooter.Race, target);
        Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, target.Id, weapon.Spec.TypeName));
        if (Flies(weapon))
        {
            var aimed = Aimed(shooter, weapon, target);
            Launch(shooter, weapon, new Vector2(aimed.X, aimed.Y), aimed.Z);
        }
        // The punch goes off in front of the shooter once its fists come down.
        else if (weapon.Spec.TypeName == "GORILLA_PUNCH")
            Punch(shooter, weapon.Spec, target.Position - shooter.Position);
        else if (weapon.Spec.TypeName == "VORTEX")
            FireVortex(shooter, weapon.Spec, target);
        // Area weapons go off where the target is and hit everyone the blast reaches, friend or foe, as in the original.
        else if (weapon.IsArea)
            StartBlast(weapon.Spec, target.Position, shooter.Race, shooter: shooter.Id);
        else if (Sprays(weapon.Spec))
            Spray(shooter, weapon.Spec, Aimed(shooter, weapon, target));
        else
            Bolt(shooter, weapon, target);
    }

    /// <summary>
    /// A round fired at a point in first person: it goes for the point, or with no <paramref name="height"/> given (as older
    /// clients aimed) for the enemy that stands within reach of it, and does there what it does, to whatever is there or to nothing.
    /// </summary>
    private void FireAtPoint(Actor shooter, WeaponMount weapon, Vector2 point, float? height = null)
    {
        var offset = point - shooter.Position;
        if (offset.Length() > weapon.Spec.Range)
        {
            // Beyond the weapon's reach: as far along the line of sight as it reaches.
            if (height is { } beyond)
                height = AimPoint(shooter).Z + (beyond - AimPoint(shooter).Z) * weapon.Spec.Range / offset.Length();
            point = shooter.Position + Vector2.Normalize(offset) * weapon.Spec.Range;
        }
        float Off(Actor a) => a is Construction building ? building.DistanceFrom(point) : MathF.Max(0, Vector2.Distance(a.Position, point) - a.Radius);
        var there = Actors.Where(a => !a.IsDead && AreEnemies(shooter.Race, a.Race) && a is not Machine { Inside: not null } && Off(a) <= 3)
            .OrderBy(Off).ThenBy(a => a.Id).FirstOrDefault();
        if (height is null && there is not null)
            point = there.Position;
        // What the crosshair rests on: the point itself where its height is known, else the middle of the enemy
        // there, or the ground (a little over it).
        var aimHeight = height ?? (there is not null ? AimPoint(there).Z : Terrain?.At(point) ?? 0) + 0.5f;
        // In a Wired run the round strays from there; an area weapon goes off where it is sent, as a machine's on orders does.
        Vector3? strayed = null;
        if (Wired is not null && !weapon.IsArea && shooter is Machine driven)
        {
            (point, aimHeight) = Strayed(driven, point, aimHeight);
            strayed = new Vector3(point, aimHeight);
        }
        if (!Flies(weapon) && !weapon.IsArea && Terrain is { } ground
            && ground.FirstBlocked(AimPoint(shooter), new Vector3(point, aimHeight), ends: 3) is { } share)
        {
            Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, null, weapon.Spec.TypeName));
            Raise(new GameEvent(GameEventKind.Impact, shooter.Id, Vector2.Lerp(shooter.Position, point, share), null, weapon.Spec.TypeName));
            return;
        }
        Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, there?.Id, weapon.Spec.TypeName, strayed));
        if (Flies(weapon))
            Launch(shooter, weapon, point, aimHeight, sighted: height is not null);
        else if (weapon.Spec.TypeName == "GORILLA_PUNCH")
            Punch(shooter, weapon.Spec, point - shooter.Position);
        else if (weapon.Spec.TypeName == "VORTEX")
            Vortex(shooter, weapon.Spec, point);
        else if (weapon.IsArea)
            StartBlast(weapon.Spec, point, shooter.Race, shooter: shooter.Id);
        else if (Sprays(weapon.Spec))
            Spray(shooter, weapon.Spec, new Vector3(point, aimHeight));
        else
            Burst(InWorld(new Vector3(point, aimHeight)), weapon.Spec, shooter.Id, shooter.Race);
    }

    /// <summary>How long a gorilla does nothing else once it punches (MachLogPunchWeapon::doFire's busy period).</summary>
    public const float PunchBusy = 2.5f;

    // Until when each machine that punched is busy with it (MachActor::busyUntil_).
    private readonly Dictionary<int, float> _busyUntil = [];

    /// <summary>
    /// Whether a machine is busy punching: it stands where it stopped, neither moving nor turning nor taking up its
    /// orders, driven in first person too (MachLogStrategy::update and MachGuiFirstPerson wait while it is busy).
    /// </summary>
    public bool Busy(Machine machine) => _busyUntil.TryGetValue(machine.Id, out var until) && Time < until;

    // The gorilla stops to punch and stays busy until it has stood up again; its blast goes off in front of where it
    // stood once its fists come down (MachLogCanAttack::attackWithPunch, MachPhysPunchWeapon::createPunchBlast).
    private void Punch(Actor shooter, WeaponSpec punch, Vector2 toward)
    {
        if (shooter is Machine gorilla)
            _busyUntil[gorilla.Id] = Time + PunchBusy;
        ScheduleBlast(Time + Blast.PunchLanding, punch, shooter.Position, shooter.Race, toward, shooter.Id);
    }

    // Only an attack punches (MachLogCanAttack::attack leaves the punch out, MachLogAttackOperation punches): a
    // gorilla attacking or standing, not one on its way somewhere.
    private static bool PunchesHere(Actor shooter) =>
        shooter is not Machine { Order: not (AttackOrder or IdleOrder or StandGroundOrder) };
}
