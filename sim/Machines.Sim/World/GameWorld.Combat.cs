using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;

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
    /// Where the driver of each sighted machine looks, radians anticlockwise in the simulation's axes, wrapped to ±π; set
    /// by the driver's <see cref="DriveCommand.Yaw"/> in a Wired run or battle only. Its gun turns to it at the gun's rate
    /// (<see cref="AimGuns"/>), and its rounds go where the gun points.
    /// </summary>
    public SortedDictionary<int, float> PlayerYaws { get; } = new();

    // The sighted machines whose gun stands on the line to where their driver looks, this tick; worked out from PlayerYaws
    // and the heads each tick, asked by id only.
    private readonly HashSet<int> _onSight = [];

    /// <summary>
    /// The ground's heights, when every player has the same (the original engine's, from the game copy); null
    /// leaves shots unhindered by hills.
    /// </summary>
    public TerrainHeights? Terrain { get; set; }

    /// <summary>
    /// How much of a shot's line, at either end, the ground may rise into without stopping it (where shooter and target stand
    /// on their slopes): 3 m on the planets, half a metre on a ship deck, whose walls stand right beside the machines.
    /// </summary>
    private float ShotEnds => Nav.Fine ? 0.5f : 3;

    /// <summary>
    /// Whether a shot from one actor at another has a clear way: the straight line between their middles
    /// (MachLogCanAttack::IHaveClearWeaponPathToHim, testActorBehindCover). The ground, buildings, artefacts and the shooter's own
    /// and its allies' machines stand in the way; an enemy's machines do not, it is glad to hit those (MachLogAttackFilter).
    /// A Wired hero's own building does not hide an enemy machine that has got into its footprint (a wave machine pressed
    /// into the pod): no place round that machine would give a clearer shot.
    /// </summary>
    public bool ClearShot(Actor shooter, Actor target)
    {
        var (from, to) = (AimPoint(shooter), AimPoint(target));
        if (Terrain is { } ground && ground.FirstBlocked(from, to, ends: ShotEnds) is not null)
            return false;
        var holding = Wired is not null && target is Machine && shooter is Machine hero && !Nav.IsFree(target.Position, true) && IsWiredHero(hero)
            ? Holding(target, shooter.Race) : null;
        return FirstOnLine(from, to, actor => actor != shooter && actor != target && actor != holding && (actor is not Machine || !AreEnemies(shooter.Race, actor.Race))) is null;
    }

    /// <summary>The building of a race whose footprint an actor stands in, the first by id, or null.</summary>
    private Construction? Holding(Actor actor, Race race) =>
        Constructions.Where(c => c.Race == race && !c.IsDead && c.DistanceFrom(actor.Position) <= 0).OrderBy(c => c.Id).FirstOrDefault();

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
        // A battle's opening holds fire, not the turrets.
        AimGuns();
        // In a battle's opening no hero fires, nor once it is over.
        var held = Battle is { } battle && (battle.InOpening(Time) || battle.Over);
        // In the story a machine a Grunt rammed holds its fire for a moment (GameWorld.Companions).
        foreach (var machine in Machines.Where(m => m.CanFight && !m.IsDead && m.Inside is null && !held && (Story is null || !Stunned(m))).ToList())
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
            // In the story a hunter draws on one of the trio before it fires (GameWorld.StorySight).
            if (Story is not null && StoryHoldsFire(machine, target))
                continue;
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

    /// <summary>
    /// Turns the gun of each sighted machine towards where its driver looks, one tick at its rate: an upper body at its
    /// BODY_TURN_RATE (<see cref="HeadStep"/>), keeping its bearing while the hull turns under it; a machine without one
    /// aims with its hull, which its driver turns. Then notes which guns stand on the line to the view
    /// (<see cref="OnSight"/>). A machine gone or dead leaves <see cref="PlayerYaws"/>; one inside a building waits.
    /// </summary>
    private void AimGuns()
    {
        _onSight.Clear();
        if (PlayerYaws.Count == 0)
            return;
        foreach (var id in PlayerYaws.Keys.ToList())
        {
            if (Find<Machine>(id) is not { IsDead: false } machine)
            {
                PlayerYaws.Remove(id);
                continue;
            }
            if (machine.Inside is not null)
                continue;
            var yaw = PlayerYaws[id];
            if (HeadStep(machine) is > 0 and var step)
                _heads[id] = GunStep(_heads.GetValueOrDefault(id), MathF.IEEERemainder(yaw - machine.Facing, 2 * MathF.PI), step).Head;
            if (MathF.Abs(GunLag(machine, yaw)) <= WiredBalance.GunPlay)
                _onSight.Add(id);
        }
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
    /// machine that set it off, for a run's chips and statistics. <paramref name="player"/> is the player who dealt it, as
    /// the round or blast carried it from the moment it was fired (a hero that falls before its round lands keeps the
    /// kill); without one it is the shooter's or the blaster's owner. It only picks the player within <paramref name="by"/>'s
    /// race who is credited (<see cref="CreditOf"/>).
    /// </summary>
    public void Damage(Actor target, int damage, Race? by, Actor? shooter = null, string? weapon = null, int? blaster = null, PlayerId? player = null)
    {
        if (target.IsDead || target is Artefact && !Options.DestructibleArtefacts)
            return;
        // Once a battle is over nothing is hurt any more: rounds still in the air and blasts land harmlessly.
        if (Battle is { } fought && (fought.Over || !BattleHits(target, by)))
            return;
        // In the story a Grunt lying downed or still dormant takes nothing (GameWorld.Companions).
        if (Story is not null && !StoryHit(target))
            return;
        var dealer = shooter ?? (Wired is not null && blaster is { } id ? Find(id) : null);
        if (Wired is not null && !WiredHit(target, ref damage, dealer, weapon))
            return;
        var hitter = player ?? shooter?.Owner ?? dealer?.Owner;
        var before = target.HitPoints;
        var armour = target.Armour;
        target.Durability = ArmourModel.ApplyHit(target.Durability, damage);
        // A Grunt of the story is never destroyed: it lies downed at one hit point instead.
        if (Story is not null)
            StoryStruck(target);
        if (Wired is not null)
        {
            WiredStruck(target, armour, shooter, weapon);
            AfterHit(target, before, dealer);
            NoteDamage(target, before + armour - Math.Max(0, target.HitPoints) - target.Armour, by, dealer, weapon, hitter);
        }
        if (Battle is not null)
            BattleStruck(target, before + armour - Math.Max(0, target.HitPoints) - target.Armour, by, hitter);
        if (target is Machine { CanFight: true } shaken)
            Shake(shaken, before - target.HitPoints);
        if (by is not null)
            (target.LastHitBy, target.LastHitByPlayer) = (by, hitter);
        if (by is { } foe && foe != target.Race && !target.IsDead)
            ReportHit(target);
        ReactToHit(target, shooter);
    }

    /// <summary>
    /// The player credited with what last hit an actor did: of the race that hit it last (<see cref="Actor.LastHitBy"/>, which
    /// alone says whether anyone is), the player who dealt the hit where that is one of the race, else the race's first
    /// player. Null where no race hit it. With one player per race this is always the race's own player.
    /// </summary>
    public PlayerId? CreditOf(Actor actor) => Credited(actor.LastHitBy, actor.LastHitByPlayer);

    /// <summary>The player of race <paramref name="by"/> credited with a hit <paramref name="player"/> dealt: that player where they play the race, else the race's first.</summary>
    private PlayerId? Credited(Race? by, PlayerId? player) =>
        by is { } race ? player is { } dealer && Plays(dealer, race) ? dealer : PlayerId.Of(race) : null;

    /// <summary>
    /// Runs each weapon's burst and reload cycle: RELOAD_TIME seconds from the start of one burst to the next, its rounds
    /// spread over the burst's duration. A weapon opens fire at what lies in its reach and its arc, not behind cover, and
    /// all the while a machine has that it goes on aiming. A machine driven in first person fires at
    /// <paramref name="aimPoint"/> instead, at whatever is there or not. Only weapons with their bit in
    /// <paramref name="triggers"/> open fire; the others reload and finish their bursts all the same. The copies of a
    /// Wired hero's battery take turns as one gun. Copy k of n, whenever it opens fire while its copy 0 reloads by the same
    /// reload, is timed from copy 0: its round leaves at its turn, k/n of the reload (<see cref="BatteryTurn"/>) after copy
    /// 0 opened fire, at once if that turn is past, and its reload ends that turn after copy 0's. So the battery keeps one
    /// round every n-th of a reload from one gun while the trigger is held, and finds its turns again within a reload
    /// after anything that put it out of them: a short pause, or the anchor changing the reload while it fires. Any other
    /// copy (of another Mk than copy 0, or while copy 0 is loaded and silent) that opens fire in the same tick as the copy
    /// before it, having waited for the trigger (<see cref="WeaponMount.Waited"/>), waits the turn of its place in that
    /// chain; one that has only just reloaded starts at once, as every other weapon always did, so that copies of
    /// different Mk go each at its own pace.
    /// </summary>
    private void Fire(Actor shooter, List<WeaponMount> weapons, Actor? target, (Vector2 Point, float? Height)? aimPoint = null, int triggers = -1)
    {
        // A Wired hero fighting on its own waits for its gun (InArc's play) and punches only what is in front of it.
        var hero = target is not null && shooter is Machine fighter && IsWiredHero(fighter);
        var inSights = aimPoint is null && target is { IsDead: false } && CanAttack(shooter.Race, target) && CanFireAt(shooter, target)
            && weapons.Any(w => w.FiresAt(target) && InReach(shooter, w, target) && InArc(shooter, w, target, hero)) && ClearShot(shooter, target);
        if (inSights && shooter is Machine)
        {
            Steady(shooter, target!);
            GunneryOf(shooter).LastAimed = Time;
        }
        FireData? aim = null;
        // How many copies of a battery in a row opened fire with the one before them, and whether the weapon before opened fire in this tick.
        var (chain, before) = (0, false);
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            // Only a copy that waited for the trigger takes its turn behind the one before it; one that reloads in this
            // very tick, as a faster copy of another Mk catching up does, opens fire at once rather than be set back a turn.
            var follows = before && weapon.Copy > 0 && weapon.Waited;
            before = false;
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
            if (weapon.Reload > 0)
                continue;
            if ((triggers & (1 << i)) == 0)
            {
                weapon.Waited = true;
                continue;
            }

            var triggered = aimPoint is not null
                ? weapon.DealsDamage || weapon.IsTreachery
                : weapon.FiresAt(target) && target is { IsDead: false } && (weapon.Spec.TypeName == "GORILLA_PUNCH"
                    ? shooter.DistanceTo(target) <= weapon.Spec.Range && CanFireAt(shooter, target) && PunchesHere(shooter)
                        && (!hero || InFront(shooter, target, WiredBalance.PunchPlay))
                    : inSights && InReach(shooter, weapon, target) && InArc(shooter, weapon, target, hero));
            weapon.Waited = !triggered;
            if (triggered)
            {
                // Fired at a point, a weapon gives its full burst and its rounds go where they are sent, in a Wired run about there.
                weapon.Burst = aimPoint is not null ? new FireData(0.9999f) : aim ??= TakeAim(shooter, target!);
                weapon.RoundsLeft = RoundsIn(weapon.Spec, weapon.Burst);
                chain = follows ? chain + 1 : 0;
                // The round's wait and the reload of a copy that takes a turn are set half a tick off whole ticks, so that
                // no sum of floats lands on a tick's edge.
                if (weapon.Copy > 0 && FirstCopy(weapons, i) is { Reload: > 0 } first && first.Spec.ReloadTime == weapon.Spec.ReloadTime)
                {
                    // Timed from copy 0, which opened fire earlier in this tick or before: the turn is counted from then
                    // and the reload ends that turn after copy 0's, wherever this copy's own cycle had drifted.
                    var cycle = ReloadTicks(weapon.Spec.ReloadTime, Dt);
                    var turn = BatteryTurn(weapon.Copy, BatterySize(weapons, i), weapon.Spec.ReloadTime, Dt);
                    var left = ReloadTicks(first.Reload, Dt);
                    var wait = Math.Max(0, turn - Math.Max(0, cycle - left));
                    weapon.RoundTimer = wait == 0 ? 0 : (wait + 0.5f) * Dt;
                    weapon.Reload = (left + turn - 0.5f) * Dt;
                }
                else if (chain == 0)
                {
                    weapon.RoundTimer = 0;
                    weapon.Reload = weapon.Spec.ReloadTime;
                }
                else
                {
                    // A chained copy waits its turn, and its reload with it, so that its cycle stays that much behind
                    // the first of the chain's for good.
                    var offset = BatteryOffset(chain, BatterySize(weapons, i), weapon.Spec.ReloadTime, Dt);
                    weapon.RoundTimer = offset + Dt / 2;
                    weapon.Reload = (ReloadTicks(weapon.Spec.ReloadTime, Dt) - 0.5f) * Dt + offset;
                }
                before = true;
            }
        }
    }

    /// <summary>The copy 0 of the battery the copy at <paramref name="index"/> belongs to, or null where it is not in the list.</summary>
    private static WeaponMount? FirstCopy(List<WeaponMount> weapons, int index)
    {
        var weapon = weapons[index];
        var first = index - weapon.Copy;
        return first >= 0 && weapons[first] is { Copy: 0 } copy0 && copy0.Mounting == weapon.Mounting && copy0.Spec.TypeName == weapon.Spec.TypeName
            ? copy0 : null;
    }

    /// <summary>
    /// The ticks <see cref="Fire"/> counts a reload of <paramref name="reloadTime"/> down in, from the tick a weapon opens
    /// fire to the first it may open fire again: the reload in ticks of <paramref name="dt"/>, rounded up, and one tick
    /// more where the float sum leaves a crumb above nothing, as two seconds do (41 ticks).
    /// </summary>
    public static int ReloadTicks(float reloadTime, float dt)
    {
        var ticks = 0;
        for (var left = reloadTime; left > 0 && dt > 0; left -= dt)
            ticks++;
        return ticks;
    }

    /// <summary>
    /// The turn of copy <paramref name="chain"/> of a battery of <paramref name="size"/>, or of the chain-th copy that
    /// opens fire in one tick after the first: how long after the first it fires, that share of a reload as
    /// <see cref="Fire"/> counts it, chain × <see cref="ReloadTicks"/> / size, taken down to whole ticks of
    /// <paramref name="dt"/>. Nothing for the first copy and for a battery of one, and always short of a whole reload, so
    /// that the n copies of a battery fire one after another, n times as often as one of them and a tick or less off an
    /// n-th of the reload apart.
    /// </summary>
    public static float BatteryOffset(int chain, int size, float reloadTime, float dt) => BatteryTurn(chain, size, reloadTime, dt) * dt;

    /// <summary><see cref="BatteryOffset"/> in whole ticks of <paramref name="dt"/>.</summary>
    public static int BatteryTurn(int chain, int size, float reloadTime, float dt) =>
        chain <= 0 || size <= 1 || reloadTime <= 0 ? 0 : Math.Min(chain, size - 1) * ReloadTicks(reloadTime, dt) / size;

    /// <summary>
    /// How many copies the battery of the mount at <paramref name="index"/> has: its copy 0 and the copies of its mounting
    /// that follow it in order.
    /// </summary>
    private static int BatterySize(List<WeaponMount> weapons, int index)
    {
        var weapon = weapons[index];
        var first = index - weapon.Copy;
        var size = weapon.Copy + 1;
        while (first + size < weapons.Count && weapons[first + size] is var next && next.Mounting == weapon.Mounting && next.Copy == size)
            size++;
        return size;
    }

    /// <summary>A round fired at a target: what becomes of it depends on the weapon (each MachLogWeapon's doFire).</summary>
    private void FireRound(Actor shooter, WeaponMount weapon, Actor target)
    {
        NoteFiredAt(shooter.Race, target);
        Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, target.Id, weapon.Spec.TypeName, Gun: GunOf(shooter, weapon)));
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
            StartBlast(weapon.Spec, target.Position, shooter.Race, shooter: shooter.Id, owner: shooter.Owner);
        else if (Sprays(weapon.Spec))
            Spray(shooter, weapon.Spec, Aimed(shooter, weapon, target));
        else
            Bolt(shooter, weapon, target);
    }

    /// <summary>
    /// A round fired at a point in first person: it goes for the point, or with no <paramref name="height"/> given (as older
    /// clients aimed) for the enemy that stands within reach of it, and does there what it does, to whatever is there or to nothing.
    /// A sighted machine whose gun is off the line to the point sends it along the gun line instead, to the point turned
    /// about its middle by the gun's lag (<see cref="GunPoint"/>); a bolt or a cannon's round then stops at the first actor
    /// or hill on that line, or goes off in the air at its range, and a spray is not stopped by the ground before it is
    /// thrown. A bomb is dropped and a vortex set down at the point all the same.
    /// </summary>
    private void FireAtPoint(Actor shooter, WeaponMount weapon, Vector2 point, float? height = null)
    {
        float? lag = shooter is Machine sighted && PlayerYaws.TryGetValue(sighted.Id, out var yaw) && !OnSight(sighted.Id) && !Placed(weapon.Spec)
            ? GunLag(sighted, yaw) : null;
        var offSight = lag is not null;
        if (lag is { } turned)
            point = GunPoint(shooter.Position, turned, point);
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
        if (offSight && !Flies(weapon) && !weapon.IsArea && !Sprays(weapon.Spec))
        {
            BoltAlongTheGun(shooter, weapon, point, aimHeight, there);
            return;
        }
        if (!offSight && !Flies(weapon) && !weapon.IsArea && Terrain is { } ground
            && ground.FirstBlocked(AimPoint(shooter), new Vector3(point, aimHeight), ends: ShotEnds) is { } share)
        {
            Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, null, weapon.Spec.TypeName, Gun: GunOf(shooter, weapon)));
            Raise(new GameEvent(GameEventKind.Impact, shooter.Id, Vector2.Lerp(shooter.Position, point, share), null, weapon.Spec.TypeName));
            return;
        }
        Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, there?.Id, weapon.Spec.TypeName, strayed, GunOf(shooter, weapon)));
        if (Flies(weapon))
            Launch(shooter, weapon, point, aimHeight, sighted: height is not null, offSight);
        else if (weapon.Spec.TypeName == "GORILLA_PUNCH")
            Punch(shooter, weapon.Spec, point - shooter.Position);
        else if (weapon.Spec.TypeName == "VORTEX")
            Vortex(shooter, weapon.Spec, point);
        else if (weapon.IsArea)
            StartBlast(weapon.Spec, point, shooter.Race, shooter: shooter.Id, owner: shooter.Owner);
        else if (Sprays(weapon.Spec))
            Spray(shooter, weapon.Spec, new Vector3(point, aimHeight));
        else
            Burst(InWorld(new Vector3(point, aimHeight)), weapon.Spec, shooter.Id, shooter.Race, shooter.Owner);
    }

    /// <summary>
    /// A bolt or a cannon's round that left along the gun line, off the line to where its driver looks: it goes from the
    /// shooter's middle through the strayed <paramref name="point"/> on to its weapon's range, as far across the ground as
    /// the range clamp measures, and ends at the first actor on that line, or where the ground rises into it, whichever
    /// comes first, or else in the air at its range. So nothing at the sight's distance stops it.
    /// </summary>
    private void BoltAlongTheGun(Actor shooter, WeaponMount weapon, Vector2 point, float aimHeight, Actor? there)
    {
        var from = AimPoint(shooter);
        var through = new Vector3(point, aimHeight);
        var across = Vector2.Distance(shooter.Position, point);
        var end = across < 0.01f ? through : from + (through - from) * (weapon.Spec.Range / across);
        var share = Terrain?.FirstBlocked(from, end, ends: ShotEnds);
        var met = FirstOnLine(from, end, a => a.Id != shooter.Id);
        Vector3 mark;
        if (met is { } struck && (share is not { } ground || struck.Along < ground))
            mark = InWorld(Vector3.Lerp(from, end, struck.Along));
        else if (share is { } blocked)
        {
            Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, null, weapon.Spec.TypeName, Gun: GunOf(shooter, weapon)));
            Raise(new GameEvent(GameEventKind.Impact, shooter.Id, Vector2.Lerp(shooter.Position, new Vector2(end.X, end.Y), blocked), null, weapon.Spec.TypeName));
            return;
        }
        else
            mark = end;
        Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, there?.Id, weapon.Spec.TypeName, mark, GunOf(shooter, weapon)));
        Burst(InWorld(mark), weapon.Spec, shooter.Id, shooter.Race, shooter.Owner);
    }

    /// <summary>Which of a machine's weapons fired, by its place among them, for the view; -1 for an actor that is no machine.</summary>
    private static int GunOf(Actor shooter, WeaponMount weapon) => shooter is Machine machine ? machine.Weapons.IndexOf(weapon) : -1;

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
        ScheduleBlast(Time + Blast.PunchLanding, punch, shooter.Position, shooter.Race, toward, shooter.Id, shooter.Owner);
    }

    /// <summary>Whether a target's bearing from a machine lies within <paramref name="play"/> radians of its facing.</summary>
    private static bool InFront(Actor shooter, Actor target, float play)
    {
        var way = target.Position - shooter.Position;
        return MathF.Abs(MathF.IEEERemainder(DetMath.Atan2(way.Y, way.X) - HullFacing(shooter), 2 * MathF.PI)) <= play;
    }

    // Only an attack punches (MachLogCanAttack::attack leaves the punch out, MachLogAttackOperation punches): a
    // gorilla attacking or standing, not one on its way somewhere.
    private static bool PunchesHere(Actor shooter) =>
        shooter is not Machine { Order: not (AttackOrder or IdleOrder or StandGroundOrder) };
}
