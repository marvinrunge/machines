using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The survival mode Wired, the remake's own addition: a hero made of parts defends a pod. This part holds the hero, its
/// life and death, what it takes up, its refits and the hangar's commands; the waves are in GameWorld.WiredWaves. Nothing
/// here runs without <see cref="Wired"/>.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The run, in a Wired match; null in every other.</summary>
    public WiredRun? Wired { get; private set; }

    private int _nextPickupId = 1;

    /// <summary>The crates on the ground, oldest first.</summary>
    public List<WiredPickup> WiredPickups { get; } = [];

    /// <summary>The machine that is a race's hero, while it stands.</summary>
    public Machine? HeroOf(Race race) =>
        Wired?.Heroes.GetValueOrDefault(race) is { Hero: { } id } && Find<Machine>(id) is { IsDead: false } hero ? hero : null;

    /// <summary>The pod a race's hero defends, while it stands.</summary>
    public Construction? WiredPod(Race race) =>
        Wired?.Heroes.GetValueOrDefault(race) is { Pod: { } id } && Find<Construction>(id) is { IsDead: false } pod ? pod : null;

    /// <summary>Whether a machine is a hero, in a Wired run: nothing may turn it.</summary>
    private bool IsWiredHero(Machine machine) => Wired is { } run && run.Heroes.Values.Any(s => s.Hero == machine.Id);

    /// <summary>Whether a machine was bought for a squad, in a Wired run, whichever squadron it stands in and whichever side it is on by now.</summary>
    private bool IsWiredEscort(Machine machine) => Wired is { } run && run.Heroes.Values.Any(s => s.EscortIds.Contains(machine.Id));

    /// <summary>What a machine of the waves is worth against its cost: less for one of the first waves' weaker crowd (WiredFoePlan.Worth).</summary>
    private float WiredWorth(Machine machine) => Wired?.Foes.FirstOrDefault(f => f.Machine == machine)?.Plan.Worth ?? 1;

    /// <summary>Whether a machine is a boss of the waves, in a Wired run: it cannot be turned either.</summary>
    private bool IsWiredBoss(Machine machine) => Wired is { } run && run.Foes.Any(f => f.IsBoss && f.Machine == machine);

    /// <summary>
    /// Whether a target has gone indoors, in a Wired run. No round reaches it there and nobody comes in after it, so its
    /// attackers take up what they would do with it gone, or the pod would be a bunker that holds up the wave outside.
    /// </summary>
    private bool Indoors(Actor target) => Wired is not null && target is Machine { Inside: not null };

    /// <summary>
    /// Starts the run: each player gets a hero by the options, at its pod, and a first offer in the shop, and the waves get
    /// a race and places to arrive at, tried at <paramref name="starts"/> first.
    /// </summary>
    internal void StartWired(WiredOptions options, IEnumerable<Vector2> starts)
    {
        var run = Wired = new WiredRun(options) { Enemy = WiredEnemy() };
        foreach (var race in Races.Values.Where(r => r.Controller == Controller.Player).OrderBy(r => r.Race))
        {
            var state = WiredState.Create(Data, race.Race, options, out var bank);
            race.Bmu = bank;
            run.Heroes[race.Race] = state;
            var pod = Constructions.Where(c => c.Race == race.Race && c.IsType("POD")).OrderBy(c => c.Id).FirstOrDefault();
            state.Pod = pod?.Id;
            if (pod is not null)
                state.Hero = AddHero(race.Race, pod.EntranceOutside ?? FreePointNear(pod), HeroBuild.Resolve(Data, state.Loadout)).Id;
            RollOffer(state);
        }
        FindSpawnPoints(run, starts);
    }

    /// <summary>Sets a new hero on the map, with its own stats and its own copies of its weapons.</summary>
    private Machine AddHero(Race race, Vector2 position, HeroBuild build)
    {
        var hero = new Machine(_nextId++, race, position, build.Spec, build.Stats, build.WeaponCombo);
        hero.Alert.NextLook = Time + hero.Id % 10 * Dt;
        hero.Weapons.AddRange(build.Weapons.Select(w => new WeaponMount(w.Mounting, w.Spec)));
        _actors.Add(hero.Id, hero);
        return hero;
    }

    /// <summary>
    /// A tick of the run: its heroes first, then the wave under way, then the clock. A hangar closes when its hero leaves,
    /// and an open one holds the countdown.
    /// </summary>
    private void UpdateWired()
    {
        var run = Wired!;
        if (run.Phase == WiredPhase.Ended)
            return;
        NoteShots(run);
        foreach (var hero in run.Heroes.Values)
            UpdateHero(run, hero);
        if (run.Phase == WiredPhase.Wave)
            UpdateWave(run);
        foreach (var state in run.Heroes.Values.Where(s => s.HangarOpen && WhyNoHangar(s) is not null))
            state.HangarOpen = false;
        if (!run.BetweenWaves || run.Heroes.Values.Any(s => s.HangarOpen))
            return;
        run.Countdown = MathF.Max(0, run.Countdown - Dt);
        if (run.Countdown <= 0)
            BeginWave();
    }

    /// <summary>The wave is over: the wait for the next one starts, with a new offer for that wave and a reroll to spend.</summary>
    public void BeginIntermission()
    {
        if (Wired is not { } run)
            return;
        run.Phase = WiredPhase.Intermission;
        run.Countdown = WiredBalance.IntermissionTime;
        run.IntermissionEnds = Time + WiredBalance.IntermissionTime;
        run.Groups.Clear();
        run.Boss = null;
        EndSpecial(run);
        foreach (var state in run.Heroes.Values)
        {
            state.Rerolled = false;
            RollOffer(state);
        }
        if (WiredDirector.IsSpecialWave(run.Wave + 1))
            run.NextSpecial = WiredDirector.RollSpecial(run.Wave + 1, run.LastSpecial, Random);
    }

    /// <summary>The run ends with the last pod, or with the last body of the last hero. Nobody wins it.</summary>
    private void CheckWiredEnd()
    {
        if (Wired is not { Phase: not WiredPhase.Ended } run)
            return;
        var podStands = run.Heroes.Keys.Any(race => WiredPod(race) is not null);
        if (podStands && run.Heroes.Values.Any(s => s.Hero is not null || s.RespawnAt is not null))
            return;
        run.Phase = WiredPhase.Ended;
        run.Ending = podStands ? WiredEnding.OutOfBodies : WiredEnding.PodLost;
        // A hero who fell before the pod did waits for no body: none comes now.
        foreach (var state in run.Heroes.Values)
            (state.HangarOpen, state.RespawnAt) = (false, null);
        foreach (var race in run.Heroes.Keys.Where(r => !Races[r].Defeated))
        {
            Races[race].Defeated = true;
            Raise(new GameEvent(GameEventKind.RaceDefeated, 0, Vector2.Zero, Detail: race.ToString()));
        }
        Raise(new GameEvent(GameEventKind.WiredRunEnded, 0, Vector2.Zero, Detail: run.Ending.ToString()));
    }

    /// <summary>The run's state for the checksum: all a hero owns and the machine it makes, and the crates on the ground.</summary>
    private long WiredFingerprint()
    {
        var hash = Wired!.Fingerprint();
        foreach (var race in Wired.Heroes.Keys)
        {
            if (HeroOf(race) is not { } hero)
                continue;
            hash = hash * 31 + hero.Refits;
            hash = hash * 31 + hero.MaxHitPoints;
            hash = hash * 31 + hero.MaxArmour;
            hash = hash * 31 + BitConverter.SingleToInt32Bits(hero.Stats.Speed);
            hash = hash * 31 + hero.Weapons.Count;
        }
        foreach (var crate in WiredPickups)
        {
            hash = hash * 31 + crate.Id;
            hash = hash * 31 + crate.Wave;
            hash = hash * 31 + BitConverter.SingleToInt32Bits(crate.Position.X);
            hash = hash * 31 + BitConverter.SingleToInt32Bits(crate.Position.Y);
        }
        // The world's checksum has the wrecks' ids and quantities; where they lie decides what a hero drives over.
        foreach (var wreck in Debris)
        {
            hash = hash * 31 + BitConverter.SingleToInt32Bits(wreck.Position.X);
            hash = hash * 31 + BitConverter.SingleToInt32Bits(wreck.Position.Y);
        }
        hash = hash * 31 + BitConverter.SingleToInt32Bits(ForcedDarkness ?? -1);
        return hash;
    }

    // ---- the hero's life --------------------------------------------------------------------------

    /// <summary>What a hero does by itself each tick: it falls, comes back, takes up what it drives over and mends at the pod.</summary>
    private void UpdateHero(WiredRun run, WiredState state)
    {
        if (state.Hero is { } id && HeroOf(state.Race) is null)
            HeroDown(state, id);
        if (state.RespawnAt is { } back && Time >= back && WiredPod(state.Race) is { } pod)
            Respawn(state, pod);
        if (HeroOf(state.Race) is { } alive)
            WiredChips.Ticking(this, state, alive);
        if (HeroOf(state.Race) is { Inside: null } hero)
        {
            UpdateDash(state, hero);
            Collect(state, hero);
        }
        if (run.BetweenWaves && WiredPod(state.Race) is { } home)
            MendAt(home, state);
    }

    /// <summary>The hero fell: the bank goes, all but what the Savings bank keeps, and a spare body is called up if there is one.</summary>
    private void HeroDown(WiredState state, int id)
    {
        var fell = _events.Find(e => e.Kind == GameEventKind.Destroyed && e.Actor == id);
        state.Hero = null;
        state.HangarOpen = false;
        state.Stats.Deaths++;
        state.Stats.CleanStreak = 0;
        Races[state.Race].Bmu *= WiredChips.Kept(state);
        if (state.SpareBodies > 0)
        {
            state.SpareBodies--;
            state.RespawnAt = Time + WiredBalance.RespawnTime;
        }
        Raise(new GameEvent(GameEventKind.WiredHeroDied, id, fell?.Position ?? Vector2.Zero, (int)state.Race, state.SpareBodies.ToString()));
    }

    /// <summary>
    /// A spare body stands at the pod in the same loadout: a new actor, whom the escorts follow as they did the old one.
    /// That goes for all that is the machine's own doing, a fight it picked, its way back from one and what it returns to
    /// after evading, and not for orders, whose series may go back to following the old body: that step means the new one.
    /// </summary>
    private void Respawn(WiredState state, Construction pod)
    {
        state.RespawnAt = null;
        var door = pod.EntranceOutside is { } entrance && PadClear(entrance) ? entrance : FreePointNear(pod);
        var hero = AddHero(state.Race, door, state.Build(Data));
        state.Hero = hero.Id;
        foreach (var escort in Escorts(state.Race))
        {
            if (escort.Order is IdleOrder or FollowOrder or ReturnOrder)
                Send(escort, new FollowOrder(hero.Id), hero.Position);
            else
                escort.Order = AfterTheNewBody(escort.Order, hero.Id);
            if (_series.TryGetValue(escort.Id, out var steps))
                _series[escort.Id] = new Queue<Command>(steps.Select(step => FollowsTheFallen(step) is { } follow ? follow with { Target = hero.Id } : step));
        }
        Raise(new GameEvent(GameEventKind.WiredHeroRespawned, hero.Id, door, (int)state.Race, state.SpareBodies.ToString()));
    }

    /// <summary>A step of a series that follows a machine no longer there.</summary>
    private FollowCommand? FollowsTheFallen(Command step) =>
        step is FollowCommand follow && Find<Machine>(follow.Target) is null or { IsDead: true } ? follow : null;

    /// <summary>An escort's order with what it goes back to following the new body; an order of the player's stays.</summary>
    private static Order AfterTheNewBody(Order order, int hero) => order switch
    {
        IdleOrder or FollowOrder or ReturnOrder => new FollowOrder(hero),
        AttackOrder { Back: { } back } attack => attack with { Back = back with { Order = AfterTheNewBody(back.Order, hero) } },
        EvadeOrder evade => evade with { Resume = AfterTheNewBody(evade.Resume, hero) },
        _ => order,
    };

    /// <summary>
    /// An escort goes on following a hero that is only away, behind the pod's door or between two bodies: it stands and
    /// fights as a follower does, and sets off again when the hero is back.
    /// </summary>
    private bool WaitsForHero(Machine escort, FollowOrder follow) =>
        Wired!.Heroes.GetValueOrDefault(escort.Race) is { } state && state.EscortIds.Contains(escort.Id)
        && (state.Hero == follow.Target || state.RespawnAt is not null);

    /// <summary>Wrecks pay out as the hero drives over them. A crate stays on the ground while the inventory is full.</summary>
    private void Collect(WiredState state, Machine hero)
    {
        var build = state.Build(Data);
        var reach = hero.Radius + build.PickupReach;
        for (var i = 0; i < Debris.Count; i++)
        {
            var wreck = Debris[i];
            if (Vector2.DistanceSquared(hero.Position, wreck.Position) > reach * reach)
                continue;
            var bmu = (int)(wreck.Quantity * build.WreckShare);
            Debris.RemoveAt(i--);
            Earn(state, bmu);
            state.Stats.WrecksCollected++;
            Raise(new GameEvent(GameEventKind.WiredWreckCollected, hero.Id, wreck.Position, wreck.Id, bmu.ToString()));
        }
        for (var i = 0; i < WiredPickups.Count; i++)
        {
            var crate = WiredPickups[i];
            if (Vector2.DistanceSquared(hero.Position, crate.Position) > reach * reach || state.Inventory.Count() >= WiredBalance.InventoryLimit)
                continue;
            var (catalogue, mk) = WiredDirector.Crate(crate.Wave, ChipsToFind(state), Random);
            state.AddLoot(catalogue, mk);
            WiredPickups.RemoveAt(i--);
            state.Stats.CratesCollected++;
            Raise(new GameEvent(GameEventKind.WiredCrateCollected, hero.Id, crate.Position, crate.Id, catalogue));
        }
    }

    /// <summary>Puts a crate on the ground.</summary>
    private void DropCrate(Vector2 position, int wave) => WiredPickups.Add(new WiredPickup(_nextPickupId++, OnFreeGround(position), wave));

    /// <summary>Where something falling at a place comes to lie for a hero to reach it: on the land the pod's door stands on.</summary>
    private Vector2 OnFreeGround(Vector2 position)
    {
        if (AttackedPod(Wired!) is not { } pod)
            return position;
        var door = Nav.Cell(Doorstep(pod));
        return Nav.NearestPointIn(position, Nav.Region(door.X, door.Y, crossesWater: false), crossesWater: false) ?? position;
    }

    /// <summary>Between waves the hero and its escorts mend for free near the pod.</summary>
    private void MendAt(Construction pod, WiredState state)
    {
        foreach (var machine in Escorts(state.Race).Append(HeroOf(state.Race)).OfType<Machine>())
        {
            if (machine.DistanceTo(pod) > WiredBalance.PodReach)
                continue;
            AddHitPoints(machine, machine.MaxHitPoints * WiredBalance.FreeRepair * Dt);
            if (machine.Armour < machine.MaxArmour)
                machine.ArmourRegen += machine.MaxArmour * WiredBalance.FreeRepair * Dt;
        }
    }

    /// <summary>BMUs that came in by the hero's work: wrecks and bonuses, not what the hangar refunds.</summary>
    private void Earn(WiredState state, int bmu)
    {
        AddBmu(state.Race, bmu);
        state.Stats.BmusEarned += bmu;
    }

    /// <summary>
    /// The hero's rounds of this tick go into its statistics. An ability's blast, orb or lightning is shown as its weapon's
    /// shot, but it is no round of a weapon: it counts for none, nor for the time the hero has been firing.
    /// </summary>
    private void NoteShots(WiredRun run)
    {
        List<(int Hero, string? Weapon)>? abilities = null;
        foreach (var used in _events.Where(e => e.Kind == GameEventKind.WiredAbilityUsed && e.Detail is not null))
            (abilities ??= []).Add((used.Actor, WiredCatalogue.FindAbility(used.Detail!)?.Based));
        foreach (var shot in _events.Where(e => e.Kind == GameEventKind.Fired && e.Detail is not null))
        {
            if (abilities?.Remove((shot.Actor, shot.Detail)) == true)
                continue;
            foreach (var state in run.Heroes.Values.Where(s => s.Hero == shot.Actor))
            {
                state.Stats.Weapon(shot.Detail!).Rounds++;
                if (HeroOf(state.Race) is { } hero && Time - state.LastShotAt > WiredChips.FireGap(hero))
                    state.FiringSince = Time;
                state.LastShotAt = Time;
            }
        }
    }

    /// <summary>What belongs to the waves. Artefacts are always red, which the waves can be as well, and are nobody's.</summary>
    private bool OnTheWavesSide(Actor target) => target.Race == Wired!.Enemy && target is not Artefact;

    /// <summary>
    /// What a hit comes to with the chips of the hero that dealt it or takes it, in <paramref name="damage"/>. A hero
    /// nothing can hurt at the moment takes none at all, and false says so.
    /// </summary>
    private bool WiredHit(Actor target, ref int damage, Actor? shooter, string? weapon)
    {
        var run = Wired!;
        var share = 1f;
        if (OnTheWavesSide(target) && shooter is Machine dealing && run.Heroes.GetValueOrDefault(shooter.Race) is { } own && own.Hero == shooter.Id)
            share *= WiredChips.Dealing(this, own, dealing, new ChipHit(target, shooter, weapon, target.Armour));
        if (target is Machine taking && run.Heroes.GetValueOrDefault(target.Race) is { } hit && hit.Hero == target.Id)
        {
            if (Time < hit.InvulnerableUntil)
                return false;
            share *= WiredChips.Taking(this, hit, taking, new ChipHit(target, shooter, weapon, target.Armour));
        }
        if (share != 1 && damage > 0)
            damage = Math.Max(1, WholeDamage(damage * share));
        return true;
    }

    /// <summary>
    /// A hit in whole points, as armour counts them. In a run a part of a point is that chance of a whole one, so that what
    /// Mks and chips add to a small gun lands on average, as the hangar shows it: 3.6 is a 4 three times in five. In
    /// any other match it is rounded.
    /// </summary>
    private int WholeDamage(float damage)
    {
        if (Wired is null)
            return (int)MathF.Round(damage);
        var whole = MathF.Floor(damage);
        return (int)whole + (damage > whole && Random.NextSingle() < damage - whole ? 1 : 0);
    }

    /// <summary>
    /// How far a driven machine's rounds can stray from the crosshair in a run, in degrees: by its inaccuracy, with what
    /// hits have shaken up, and what the dark takes from its aim. So accuracy and night vision count in first person
    /// too, where a round would otherwise go to the very point. A crosshair can show this.
    /// </summary>
    public float AimSpread(Machine machine) =>
        MathF.Max(0, GunneryOf(machine).Inaccuracy + AimPenalty(machine, null)) * WiredBalance.StrayPerPoint;

    /// <summary>
    /// The point in what stops rounds that a run's driver means by a sight resting on an actor's picture: a model stands higher
    /// and wider than the box of <see cref="BoxOf"/>, a flyer's above it altogether, and a round sent to the picture would
    /// pass through nothing. A point in the box stays as it is. Only a question: the answer goes into the DriveCommand.
    /// </summary>
    public (Vector2 Point, float Height) AimInside(Actor actor, Vector2 seen, float height)
    {
        var (min, max) = BoxOf(actor);
        var point = Vector2.Clamp(seen, new Vector2(min.X, min.Y), new Vector2(max.X, max.Y));
        return (point, height >= min.Z && height <= max.Z ? height : AimPoint(actor).Z);
    }

    /// <summary>
    /// Where a round fired at a point goes: somewhere round it across the line of fire, within the cone of
    /// <see cref="AimSpread"/>, as likely near the crosshair as at the cone's edge. At long range a small target is then
    /// missed the more the further off it stands, but not hopelessly.
    /// </summary>
    private (Vector2 Point, float Height) Strayed(Machine shooter, Vector2 point, float height)
    {
        var way = point - shooter.Position;
        var distance = way.Length();
        if (distance < 0.01f)
            return (point, height);
        var off = distance * Tan(AimSpread(shooter) * MathF.PI / 180) * Random.NextSingle();
        var angle = Random.NextSingle() * MathF.Tau;
        return (point + new Vector2(-way.Y, way.X) / distance * (off * DetMath.Cos(angle)), height + off * DetMath.Sin(angle));
    }

    /// <summary>A hit has landed: a hero's chips answer it, one may yet save it, and the time of the hit is noted.</summary>
    private void WiredStruck(Actor target, int armourBefore, Actor? shooter, string? weapon)
    {
        if (target is not Machine hero || Wired!.Heroes.GetValueOrDefault(target.Race) is not { } state || state.Hero != target.Id)
            return;
        WiredChips.Struck(this, state, hero, new ChipHit(target, shooter, weapon, armourBefore));
        state.LastHitAt = Time;
    }

    /// <summary>A machine of the waves bursts: what stands round it of the enemy takes a part of what it was.</summary>
    internal void Shrapnel(Machine victim, int damage, float reach, Race by)
    {
        Raise(new GameEvent(GameEventKind.WiredShrapnel, victim.Id, victim.Position));
        foreach (var machine in FiledNear(victim.Position, reach + _spatialReach).OfType<Machine>().ToList())
        {
            if (machine != victim && machine.Race == Wired!.Enemy && machine.Inside is null && Vector2.Distance(machine.Position, victim.Position) - machine.Radius <= reach)
                Damage(machine, damage, by);
        }
    }

    /// <summary>
    /// What a hit took goes into the statistics: from the hero, or from the enemy by a hero's race. It counts for a weapon
    /// when the hero itself dealt it, with a blast of its own as well.
    /// </summary>
    private void NoteDamage(Actor target, int lost, Race? by, Actor? shooter, string? weapon)
    {
        var run = Wired!;
        if (run.Heroes.GetValueOrDefault(target.Race) is { } hit && hit.Hero == target.Id)
            hit.Stats.DamageTaken += lost;
        if (!OnTheWavesSide(target) || by is not { } race || run.Heroes.GetValueOrDefault(race) is not { } state)
            return;
        state.Stats.DamageDealt += lost;
        if (weapon is null || shooter is null || shooter.Id != state.Hero)
            return;
        var use = state.Stats.Weapon(weapon);
        use.Damage += lost;
        if (target.IsDead)
            use.Kills++;
    }

    // ---- refits -----------------------------------------------------------------------------------

    /// <summary>
    /// Puts a loadout on the hero. Machines never change otherwise, so the world's own notes on the machine go too: its
    /// aim was made from the old accuracy, its run from the old speed, its upper body and places from the old rig, and
    /// the bits of its driver's weapons and sideways input from the old weapons and legs. It stands and takes new orders.
    /// </summary>
    private void Wear(WiredState state, HeroLoadout loadout)
    {
        state.Loadout = loadout;
        if (HeroOf(state.Race) is not { } hero)
            return;
        var build = HeroBuild.Resolve(Data, loadout);
        if (build.Spec == hero.Spec && build.Stats == hero.Stats && build.WeaponCombo == hero.WeaponCombo
            && build.Weapons.Select(w => w.Spec).SequenceEqual(hero.Weapons.Select(w => w.Spec)))
            return;
        hero.Refit(build.Spec, build.Stats, build.WeaponCombo, build.Weapons.Select(w => new WeaponMount(w.Mounting, w.Spec)));
        StartAfresh(hero);
    }

    /// <summary>Takes from a machine all the world has noted on its way, its aim and its driver: it stands and takes new orders.</summary>
    private void StartAfresh(Machine machine)
    {
        if (_motion.TryGetValue(machine.Id, out var motion))
            AnnounceStop(machine, motion);
        machine.Order = Order.Idle;
        machine.Path.Clear();
        machine.PathIndex = 0;
        _series.Remove(machine.Id);
        _motion.Remove(machine.Id);
        _aside.Remove(machine.Id);
        _approaching.Remove(machine.Id);
        _formation.Remove(machine.Id);
        _busyUntil.Remove(machine.Id);
        _gunnery.Remove(machine.Id);
        _heads.Remove(machine.Id);
        _machineAims.Remove(machine.Id);
        ForgetDriver(machine);
    }

    /// <summary>Takes from a machine what its driver last said.</summary>
    private void ForgetDriver(Machine machine)
    {
        DriveInput.Remove(machine.Id);
        PlayerAims.Remove(machine.Id);
        PlayerWeapons.Remove(machine.Id);
        PlayerButtons.Remove(machine.Id);
        PlayerHeads.Remove(machine.Id);
    }

    // ---- the shop ---------------------------------------------------------------------------------

    /// <summary>Fills the shop with new offers: as many as there are of what the wave allows, each once, at random.</summary>
    private void RollOffer(WiredState state)
    {
        var pool = WiredCatalogue.ForSale(Wired!.ShopWave);
        state.Offer.Clear();
        while (state.Offer.Count < WiredBalance.OfferSize && pool.Count > 0)
        {
            var pick = Random.Next(pool.Count);
            state.Offer.Add(pool[pick]);
            pool.RemoveAt(pick);
        }
    }

    /// <summary>Offers chips to choose from, out of the run's pool, leaving out those the hero owns; a boss wave's end calls this.</summary>
    public void OfferChips(WiredState state)
    {
        state.ChipChoice.Clear();
        var pool = ChipsToFind(state);
        while (state.ChipChoice.Count < WiredChips.ChoiceSize && pool.Count > 0)
        {
            var pick = Random.Next(pool.Count);
            state.ChipChoice.Add(pool[pick]);
            pool.RemoveAt(pick);
        }
    }

    /// <summary>The chips of the run's pool the hero neither owns yet nor has to choose from.</summary>
    private List<string> ChipsToFind(WiredState state)
    {
        var options = Wired!.Options.ChipPool;
        return (options.Count > 0 ? options : WiredChips.BasePool)
            .Where(id => WiredChips.Find(id) is not null && !state.ChipChoice.Contains(id) && state.Items.All(i => i.Catalogue != id)).Distinct().ToList();
    }

    /// <returns>null if the hangar can be opened now, otherwise why not.</returns>
    public string? WhyNoHangar(WiredState state)
    {
        if (!Wired!.Waiting)
            return "the hangar opens only between waves";
        if (HeroOf(state.Race) is not { } hero)
            return "the hero is down";
        if (WiredPod(state.Race) is not { } pod)
            return "the pod is gone";
        return hero.DistanceTo(pod) > WiredBalance.PodReach ? "too far from the pod" : null;
    }

    /// <returns>null if the item can be bought now, otherwise why not.</returns>
    public string? WhyCannotBuy(WiredState state, string id)
    {
        var kind = WiredCatalogue.KindOf(id);
        if (kind is null or WiredKind.Chip)
            return "that is not for sale";
        if (WiredCatalogue.FindChassis(id) is { } chassis)
        {
            if (state.Items.Any(i => i.Catalogue == id))
                return "you own that chassis already";
            if (chassis.Needs.Count > 0 && !chassis.Needs.Any(state.Reached.Contains))
                return $"needs {string.Join(" or ", chassis.Needs)} first";
        }
        else if (!state.Offer.Contains(id))
            return "that is not on offer";
        return WhyNoRoom(state) ?? (Races[state.Race].Bmu < state.Priced(WiredCatalogue.Price(id)) ? "not enough BMUs" : null);
    }

    /// <returns>null if the inventory takes one more, otherwise why not.</returns>
    public string? WhyNoRoom(WiredState state) => state.Inventory.Count() >= WiredBalance.InventoryLimit ? "the inventory is full" : null;

    /// <returns>null if the offer can be rolled anew now, otherwise why not.</returns>
    public string? WhyNoReroll(WiredState state)
    {
        if (state.Rerolled)
            return "only one reroll per intermission";
        return Races[state.Race].Bmu < state.Priced(WiredBalance.RerollCost) ? "not enough BMUs" : null;
    }

    /// <returns>null if the item can go up a Mk now, otherwise why not.</returns>
    public string? WhyNoUpgrade(WiredState state, WiredItem item)
    {
        if (state.UpgradePrice(item) is not { } price)
            return "that has no higher Mk";
        return Races[state.Race].Bmu < price ? "not enough BMUs" : null;
    }

    /// <returns>null if the hangar can mend the pod now, otherwise why not.</returns>
    public string? WhyNoPodRepair(WiredState state)
    {
        if (WiredPod(state.Race) is not { } pod)
            return "the pod is gone";
        if (pod.MaxHitPoints - pod.HitPoints <= 0)
            return "the pod needs no repair";
        return Races[state.Race].Bmu < 1 ? "not enough BMUs" : null;
    }

    /// <summary>What an escort of a type costs this hero.</summary>
    public int EscortPrice(WiredState state, ResearchKey key) =>
        state.Priced((Data.Parm.Machine(key.TypeName).Level(key.HardwareLevel).Cost * WiredBalance.EscortPercent + 50) / 100);

    /// <returns>null if the escort can be bought now, otherwise why not.</returns>
    public string? WhyNoEscort(WiredState state, ResearchKey key)
    {
        var wave = Wired!.ShopWave;
        if (!WiredCatalogue.Tiers.Any(t => t.Wave <= wave && t.Offers(key)))
            return "that is not on offer yet";
        if (Escorts(state.Race).Count() >= WiredBalance.SquadLimit(wave, WiredChips.SquadExtra(state)))
            return "the squad is full";
        if (WiredPod(state.Race) is null)
            return "the pod is gone";
        return Races[state.Race].Bmu < EscortPrice(state, key) ? "not enough BMUs" : null;
    }

    /// <summary>What Ready pays now: the seconds saved of the intermission under way. The time before the first wave pays nothing.</summary>
    public int ReadyPays => Wired is { Phase: WiredPhase.Intermission } run ? (int)(MathF.Max(0, run.IntermissionEnds - Time) * WiredBalance.ReadyBonus) : 0;

    /// <summary>Takes what the hangar charges from the bank; shopping is no cause for the pod's warnings of a low bank.</summary>
    private void Charge(WiredState state, int bmu) => Races[state.Race].Bmu -= bmu;

    private string? ApplyWired(WiredCommand command)
    {
        if (Wired?.Heroes.GetValueOrDefault(command.Race) is not { } state)
            return "this is not a Wired match";
        return command switch
        {
            WiredHangarCommand hangar => SetHangar(state, hangar.Open),
            WiredReadyCommand => Ready(state),
            WiredChipChoiceCommand choice => ChooseChip(state, choice.Choice, choice.Socket),
            WiredAbilityCommand ability => UseAbility(state, ability.Slot, ability.Aim),
            WiredDashCommand dash => Dash(state, dash.Direction),
            _ when !state.HangarOpen => "the hangar is closed",
            WiredBuyCommand buy => Buy(state, buy.Item, buy.Slot),
            WiredSellCommand sell => Sell(state, sell.Item),
            WiredEquipCommand equip => Equip(state, equip.Item, equip.Slot),
            WiredUnequipCommand unequip => Unequip(state, unequip.Item),
            WiredChassisCommand chassis => ChangeChassis(state, chassis.Item),
            WiredUpgradeCommand upgrade => Upgrade(state, upgrade.Item),
            WiredRerollCommand => Reroll(state),
            WiredPodRepairCommand repair => RepairPod(state, repair.Bmu),
            WiredChipCommand chip => PlaceChip(state, chip.Item, chip.Socket),
            WiredSquadBuyCommand escort => BuyEscort(state, escort.Machine),
            _ => $"unknown command {command.GetType().Name}",
        };
    }

    private string? SetHangar(WiredState state, bool open)
    {
        if (open && WhyNoHangar(state) is { } why)
            return why;
        state.HangarOpen = open;
        if (open)
            state.HangarOpens++;
        return null;
    }

    /// <summary>
    /// Ends the wait for all, paying for the seconds saved. That is once: the wave comes at the end of the tick, and until
    /// then a second Ready is refused, as is a hangar, which would hold back a wave already called.
    /// </summary>
    private string? Ready(WiredState state)
    {
        var run = Wired!;
        if (!run.Waiting)
            return "there is no wave to wait for";
        if (run.Phase == WiredPhase.Intermission)
            Earn(state, ReadyPays);
        foreach (var hero in run.Heroes.Values)
            hero.HangarOpen = false;
        run.Countdown = 0;
        return null;
    }

    private string? Buy(WiredState state, string id, WiredSlot? slot)
    {
        if (WhyCannotBuy(state, id) is { } why)
            return why;
        var kind = WiredCatalogue.KindOf(id)!.Value;
        // What cannot go onto the slot is not paid for.
        if (slot is not null && WhyNotOnto(state, new WiredItem(-1, kind, id), slot) is { } misfit)
            return misfit;
        var price = state.Priced(WiredCatalogue.Price(id));
        Charge(state, price);
        var item = state.Add(kind, id, price);
        // An offer goes when it is taken; the chassis tree stays as it is.
        var offer = state.Offer.IndexOf(id);
        if (offer >= 0)
            state.Offer[offer] = null;
        return slot is null ? null : TryWear(state, item, slot);
    }

    private string? Sell(WiredState state, int id)
    {
        if (state.Item(id) is not { } item)
            return "you do not own that";
        if (state.Loadout.Holds(id))
            return "take it off first";
        state.Items.Remove(item);
        Races[state.Race].Bmu += state.SellValue(item);
        return null;
    }

    private string? Equip(WiredState state, int id, WiredSlot slot)
    {
        if (state.Item(id) is not { } item)
            return "you do not own that";
        if (item.Kind is WiredKind.Chip or WiredKind.Chassis)
            return "that does not go into this slot";
        return TryWear(state, item, slot);
    }

    private string? PlaceChip(WiredState state, int? id, int socket)
    {
        var slot = WiredSlot.Chip(socket);
        if (socket is < 0 or >= WiredBalance.ChipSockets)
            return "no such socket";
        if (id is null)
            return state.Loadout.At(slot) is null ? "that socket is empty" : TryWear(state, null, slot);
        return state.Item(id.Value) is { } chip ? TryWear(state, chip, slot) : "you do not own that";
    }

    private string? TryWear(WiredState state, WiredItem? item, WiredSlot slot)
    {
        if (WhyNotOnto(state, item, slot) is { } why)
            return why;
        Wear(state, state.Loadout.With(slot, item));
        return null;
    }

    /// <returns>null if the item can go into the slot, or the slot be emptied by null, otherwise why not.</returns>
    private string? WhyNotOnto(WiredState state, WiredItem? item, WiredSlot slot) =>
        (item is null ? null : state.Loadout.WhyNotFit(item, slot)) ?? state.WhyNotWear(Data, state.Loadout.With(slot, item));

    private string? Unequip(WiredState state, int id)
    {
        if (state.Item(id) is null || state.Loadout.SlotOf(id) is not { } slot)
            return "that is not on the hero";
        return TryWear(state, null, slot);
    }

    private string? ChangeChassis(WiredState state, int id)
    {
        if (state.Item(id) is not { Kind: WiredKind.Chassis } chassis)
            return "you own no such chassis";
        if (state.Loadout.Chassis.Id == id)
            return "the hero is on that chassis";
        var loadout = state.Loadout.OnChassis(chassis);
        if (state.WhyNotWear(Data, loadout) is { } why)
            return why;
        Wear(state, loadout);
        if (HeroOf(state.Race) is { } hero)
            OutOfDoorTooSmall(hero);
        return null;
    }

    /// <summary>A hero that has grown too big for the door it stands in or behind comes out by it, as it could not drive through it any more.</summary>
    private void OutOfDoorTooSmall(Machine hero)
    {
        if ((hero.Inside ?? hero.Doorway) is not { } id || Find<Construction>(id) is not { } building || FitsThrough(hero, building))
            return;
        building.Garrisoned.Remove(hero.Id);
        (hero.Inside, hero.Doorway, hero.Position) = (null, null, DoorOf(building, hero));
    }

    private string? Upgrade(WiredState state, int id)
    {
        if (state.Item(id) is not { } item)
            return "you do not own that";
        if (WhyNoUpgrade(state, item) is { } why)
            return why;
        var price = state.UpgradePrice(item)!.Value;
        Charge(state, price);
        state.Replace(item with { Mk = item.Mk + 1, Paid = item.Paid + price });
        Wear(state, state.Loadout);
        return null;
    }

    private string? Reroll(WiredState state)
    {
        if (WhyNoReroll(state) is { } why)
            return why;
        Charge(state, state.Priced(WiredBalance.RerollCost));
        state.Rerolled = true;
        RollOffer(state);
        return null;
    }

    private string? RepairPod(WiredState state, int bmu)
    {
        if (WhyNoPodRepair(state) is { } why)
            return why;
        var pod = WiredPod(state.Race)!;
        var missing = pod.MaxHitPoints - pod.HitPoints;
        var whole = state.PodRepairPrice(missing);
        var spent = Math.Min(bmu > 0 ? Math.Min(bmu, whole) : whole, (int)Races[state.Race].Bmu);
        Charge(state, spent);
        pod.HitPoints += Math.Min(missing, state.PodRepairFor(spent));
        return null;
    }

    private string? ChooseChip(WiredState state, int choice, int? socket)
    {
        if (choice < 0 || choice >= state.ChipChoice.Count)
            return "no such chip to choose";
        if (WhyNoRoom(state) is { } why)
            return why;
        var chip = state.Add(WiredKind.Chip, state.ChipChoice[choice], 0);
        state.ChipChoice.Clear();
        // A chip is placed with the hangar open; otherwise it waits in the inventory.
        return socket is { } at && state.HangarOpen ? PlaceChip(state, chip.Id, at) : null;
    }

    /// <summary>The bought escorts still standing and on their side, oldest first, in whatever squadrons the player has put them.</summary>
    public IEnumerable<Machine> Escorts(Race race) => Wired?.Heroes.GetValueOrDefault(race) is { } state
        ? state.EscortIds.Select(Find<Machine>).OfType<Machine>().Where(m => m.Race == race && !m.IsDead)
        : [];

    private string? BuyEscort(WiredState state, ResearchKey key)
    {
        if (WhyNoEscort(state, key) is { } why)
            return why;
        var pod = WiredPod(state.Race)!;
        Charge(state, EscortPrice(state, key));
        key = WiredChips.Veteran(state, key);
        var spawn = FreePointNear(pod);
        var machine = AddMachine(state.Race, spawn, key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo);
        machine.Squad = WiredBalance.EscortSquad;
        state.EscortIds.Add(machine.Id);
        state.Stats.LargestSquad = Math.Max(state.Stats.LargestSquad, Escorts(state.Race).Count());
        machine.Alert.Raise(FullAlert);
        if (HeroOf(state.Race) is { } hero)
            Send(machine, new FollowOrder(hero.Id), hero.Position);
        var score = Races[state.Race].Score;
        score.MachinesBuilt++;
        score.MilitaryMachinesBuilt++;
        Raise(new GameEvent(GameEventKind.MachineBuilt, machine.Id, spawn, pod.Id, key.ToString()));
        return null;
    }
}
