using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The waves of a Wired run, the remake's own addition. <see cref="WiredDirector"/> plans them; here they arrive, get their
/// orders, are kept from hanging, and end. There is no AI controller: the machines live by the normal rules of reaction
/// and gunnery, with their orders from here.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The race the waves belong to: the first the AI was to play, else the first nobody plays, which is added.</summary>
    private Race WiredEnemy()
    {
        var named = Races.Values.Where(r => r.Controller == Controller.Ai).Select(r => r.Race).OrderBy(r => r).ToList();
        if (named.Count > 0)
            return named[0];
        var free = Enum.GetValues<Race>().Where(r => !Races.ContainsKey(r)).ToList();
        if (free.Count == 0)
            throw new ArgumentException("a Wired run needs a race for its waves");
        Races[free[0]] = new RaceState(free[0], Controller.Ai);
        BuildingPlans[free[0]] = [];
        return free[0];
    }

    /// <summary>The pod the waves go for.</summary>
    private Construction? AttackedPod(WiredRun run) => run.Heroes.Keys.Select(WiredPod).OfType<Construction>().FirstOrDefault();

    /// <summary>The hero the hunters go for: one that stands out there. A hero in the pod leaves them to the pod, like one that fell.</summary>
    private Machine? Quarry(WiredRun run) => run.Heroes.Keys.Select(HeroOf).OfType<Machine>().FirstOrDefault(hero => hero.Inside is null);

    /// <summary>Where the waves march to.</summary>
    private static Vector2 Doorstep(Construction pod) => pod.EntranceOutside ?? pod.Position;

    // ---- where they arrive ------------------------------------------------------------------------

    /// <summary>
    /// Tries the given places, the map's edge and a ring nearer the pod, and keeps those far enough from the pod that a
    /// way to it was found from; from the ring only where that way goes round no great obstacle.
    /// </summary>
    private void FindSpawnPoints(WiredRun run, IEnumerable<Vector2> starts)
    {
        if (AttackedPod(run) is not { } pod)
            return;
        var goal = Doorstep(pod);
        var tries = starts.Concat(EdgePoints()).Select(p => (p, float.MaxValue)).Concat(RingPoints(goal).Select(p => (p, WiredBalance.RingDetour)));
        foreach (var (candidate, detour) in tries)
        {
            if (Nav.NearestFreePoint(candidate, crossesWater: false) is { } point
                && Vector2.Distance(point, goal) is var straight && straight >= WiredBalance.SpawnDistance
                && run.SpawnPoints.All(p => Vector2.Distance(p, point) >= WiredBalance.SpawnGap)
                && WayLength(point, goal) is { } way && way <= detour * straight)
                run.SpawnPoints.Add(point);
        }
    }

    /// <summary>Points on the ring round the pod, those off the map left out.</summary>
    private IEnumerable<Vector2> RingPoints(Vector2 goal)
    {
        var (width, depth) = (Nav.Width * NavGrid.CellSize - WiredBalance.EdgeInset, Nav.Depth * NavGrid.CellSize - WiredBalance.EdgeInset);
        for (var i = 0; i < WiredBalance.RingPoints; i++)
        {
            var angle = 2 * MathF.PI * i / WiredBalance.RingPoints;
            var point = goal + WiredBalance.RingRadius * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            if (point.X >= WiredBalance.EdgeInset && point.Y >= WiredBalance.EdgeInset && point.X <= width && point.Y <= depth)
                yield return point;
        }
    }

    /// <summary>Where a group arrives: in the first waves near the pod, so that the fight comes soon, later from ever further.</summary>
    private Vector2 SpawnPointFor(WiredRun run)
    {
        if (AttackedPod(run) is not { } pod)
            return run.SpawnPoints[Random.Next(run.SpawnPoints.Count)];
        var goal = Doorstep(pod);
        var reach = WiredBalance.SpawnReach(run.Wave);
        var choice = run.SpawnPoints.Where(p => Vector2.Distance(p, goal) <= reach).ToList();
        if (choice.Count < WiredBalance.FewestSpawnChoices)
            choice = run.SpawnPoints.OrderBy(p => Vector2.DistanceSquared(p, goal)).Take(WiredBalance.FewestSpawnChoices).ToList();
        return choice[Random.Next(choice.Count)];
    }

    /// <summary>Points along the map's four edges, a little way in.</summary>
    private IEnumerable<Vector2> EdgePoints()
    {
        var (near, width, depth) = (WiredBalance.EdgeInset, Nav.Width * NavGrid.CellSize - WiredBalance.EdgeInset, Nav.Depth * NavGrid.CellSize - WiredBalance.EdgeInset);
        for (var x = near; x < width; x += WiredBalance.EdgeSpacing)
        {
            yield return new Vector2(x, near);
            yield return new Vector2(x, depth);
        }
        for (var y = near + WiredBalance.EdgeSpacing; y < depth; y += WiredBalance.EdgeSpacing)
        {
            yield return new Vector2(near, y);
            yield return new Vector2(width, y);
        }
    }

    /// <summary>How far a ground machine walks from one place to the other, if it gets there: they lie in one region and a whole path was found.</summary>
    private float? WayLength(Vector2 from, Vector2 to)
    {
        var (start, goal) = (Nav.Cell(from), Nav.Cell(to));
        if (Nav.Region(start.X, start.Y, crossesWater: false) != Nav.Region(goal.X, goal.Y, crossesWater: false)
            || Nav.FindPath(from, to, crossesWater: false, maxExpanded: int.MaxValue) is not { Count: > 0 } path
            || Vector2.Distance(path[^1], to) > 2 * NavGrid.CellSize)
            return null;
        var (length, at) = (0f, from);
        foreach (var point in path)
            (length, at) = (length + Vector2.Distance(at, point), point);
        return length;
    }

    // ---- a wave -----------------------------------------------------------------------------------

    /// <summary>The next wave begins and the hangars close. Without a place to arrive at it ends at once.</summary>
    private void BeginWave()
    {
        var run = Wired!;
        run.Phase = WiredPhase.Wave;
        run.Wave++;
        foreach (var state in run.Heroes.Values)
            state.HangarOpen = false;
        var plan = WiredDirector.Plan(Data, run.Wave, run.Options.Difficulty, Random, run.NextSpecial);
        run.Boss = plan.Boss;
        run.WaveStarted = run.LastGroupAt = Time;
        run.NextReview = Time + WiredBalance.OrdersEvery;
        run.Groups.Clear();
        if (run.SpawnPoints.Count > 0)
            run.Groups.AddRange(plan.Groups);
        Raise(new GameEvent(GameEventKind.WiredWaveStarted, 0, Vector2.Zero, run.Wave, plan.Boss?.Name));
        BeginSpecial(run);
        run.MostEnemies = run.EnemiesLeft;
    }

    /// <summary>A tick of the wave: groups arrive, the fallen are counted, the machines are looked over now and then, and it ends when none is left.</summary>
    private void UpdateWave(WiredRun run)
    {
        while (run.Groups.Count > 0 && Time >= run.WaveStarted + run.Groups[0].After)
        {
            var group = run.Groups[0];
            run.Groups.RemoveAt(0);
            run.LastGroupAt = Time;
            Arrive(run, group.Machines, SpawnPointFor(run));
        }
        CountFoes(run);
        BringBackTurned(run);
        UpdateStrikes(run);
        if (Time >= run.NextReview)
        {
            run.NextReview = Time + WiredBalance.OrdersEvery;
            ReviewFoes(run);
        }
        // What came over to the enemy since the last look holds the wave open as much as any other.
        if (run.EnemiesLeft == 0)
            Enlist(run);
        run.MostEnemies = Math.Max(run.MostEnemies, run.EnemiesLeft);
        if (run.EnemiesLeft == 0)
            EndWave(run);
    }

    /// <summary>All is clear: the bonus is paid, a boss leaves its chips to choose from, and the intermission starts.</summary>
    private void EndWave(WiredRun run)
    {
        var (bonus, boss) = (WiredBalance.WaveBonus(run.Wave), run.Boss);
        foreach (var state in run.Heroes.Values)
        {
            state.Stats.WavesCleared++;
            state.Stats.CleanStreak++;
            state.Stats.CleanWaves = Math.Max(state.Stats.CleanWaves, state.Stats.CleanStreak);
            bonus = WiredBalance.WaveBonus(run.Wave) + WiredChips.WaveExtra(state, run.Wave);
            Earn(state, bonus);
        }
        Raise(new GameEvent(GameEventKind.WiredWaveCleared, 0, Vector2.Zero, run.Wave, bonus.ToString()));
        BeginIntermission();
        if (boss is null)
            return;
        foreach (var state in run.Heroes.Values)
        {
            OfferChips(state);
            if (state.ChipChoice.Count > 0)
                Raise(new GameEvent(GameEventKind.WiredChipChoiceReady, 0, Vector2.Zero, (int)state.Race));
        }
    }

    // ---- the machines -----------------------------------------------------------------------------

    /// <summary>Machines teleport in round a place and set off. The view fades in every new machine by itself.</summary>
    private void Arrive(WiredRun run, IReadOnlyList<WiredFoePlan> machines, Vector2 at) =>
        GiveOrders(run, machines.Select(plan => AddFoe(run, plan, at)).ToList());

    private WiredFoe AddFoe(WiredRun run, WiredFoePlan plan, Vector2 at)
    {
        var key = plan.Key;
        var clearance = MathF.Max(1, Data.Parm.Machine(key.TypeName).Level(key.HardwareLevel).LowClearance);
        var machine = AddMachine(run.Enemy, FreeSpotNear(at, clearance), key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo);
        machine.DefCon = DefCon.High;
        machine.Alert.Raise(FullAlert);
        Strengthen(machine, plan);
        var foe = new WiredFoe(machine, plan, plan.Role == WiredRole.Fighter && Random.Next(100) < WiredBalance.HunterPercent, Time);
        run.Foes.Add(foe);
        if (plan.Boss is { } boss)
            Raise(new GameEvent(GameEventKind.WiredBossAppeared, machine.Id, machine.Position, Detail: boss.Name));
        return foe;
    }

    /// <summary>A boss or an elite gets its own copy of its stats and weapons: what the others of its type share stays as it is.</summary>
    private static void Strengthen(Machine machine, WiredFoePlan plan)
    {
        if (plan is { HitPoints: 1, Damage: 1 })
            return;
        var stats = machine.Stats with { HitPoints = Math.Max(1, (int)(machine.Stats.HitPoints * plan.HitPoints)) };
        var weapons = machine.Weapons.Select(w => new WeaponMount(w.Mounting, w.Spec with { Damage = w.Spec.Damage * plan.Damage })).ToList();
        machine.Refit(machine.Spec, stats, machine.WeaponCombo, weapons);
    }

    /// <summary>
    /// Orders by each machine's part: healers stay with the boss and heal it, hunters go for the hero and on to the pod
    /// when it falls, and all others march on the pod together, fighting what they meet.
    /// </summary>
    private void GiveOrders(WiredRun run, IReadOnlyList<WiredFoe> foes)
    {
        if (AttackedPod(run) is not { } pod)
            return;
        var goal = Doorstep(pod);
        var boss = run.Bosses.FirstOrDefault()?.Machine;
        var hero = Quarry(run);
        var marching = new List<int>();
        foreach (var foe in foes)
        {
            if (TakesPart(run, foe, pod))
                continue;
            if (foe.Plan.Healer && boss is not null)
                Send(foe.Machine, boss.HitPoints < boss.MaxHitPoints ? new HealOrder(boss.Id) : new FollowOrder(boss.Id), boss.Position);
            else if (foe.HuntsHero && hero is not null)
                Send(foe.Machine, new AttackOrder(hero.Id, Then: goal), hero.Position);
            else
                marching.Add(foe.Machine.Id);
        }
        if (marching.Count == 0)
            return;
        // One command, so that they make way for each other as a group does.
        _commandId = ++_commands;
        Apply(new AttackMoveCommand(run.Enemy, marching, goal));
        _commandId = 0;
    }

    /// <summary>Strikes the fallen off the roster, with what they bring, and has a boss below half call for help.</summary>
    private void CountFoes(WiredRun run)
    {
        for (var i = 0; i < run.Foes.Count; i++)
        {
            var foe = run.Foes[i];
            var machine = foe.Machine;
            if (machine.IsDead || Find<Machine>(machine.Id) is null)
            {
                Fallen(run, foe);
                run.Foes.RemoveAt(i--);
            }
            else if (machine.Race != run.Enemy)
            {
                run.Foes.RemoveAt(i--);
                Turned(run, foe);
            }
            else if (foe is { IsBoss: true, CalledForHelp: false } && machine.HitPoints * 2 < machine.MaxHitPoints)
                CallForHelp(run, foe);
        }
    }

    /// <summary>A machine of the waves is gone: whoever hit it last gets the credit, and a crate may stay behind.</summary>
    private void Fallen(WiredRun run, WiredFoe foe)
    {
        var machine = foe.Machine;
        if (machine.LastHitBy is { } by && run.Heroes.GetValueOrDefault(by) is { } state)
        {
            state.Stats.Kills++;
            state.Stats.KillValue += (int)(machine.Stats.Cost * foe.Plan.Worth);
            if (foe.IsBoss)
                state.Stats.BossesKilled++;
            if (HeroOf(by) is { } hero)
                WiredChips.Killed(this, state, hero, machine);
        }
        var luck = run.Heroes.Values.Max(WiredChips.LootShare);
        if (foe.IsBoss || foe.Plan.Elite && Random.Next(100) < WiredBalance.EliteLootPercent * luck)
            DropCrate(machine.Position, run.Wave);
    }

    /// <summary>A small group arrives at the place nearest the boss, and the clock against hanging starts over.</summary>
    private void CallForHelp(WiredRun run, WiredFoe boss)
    {
        boss.CalledForHelp = true;
        Raise(new GameEvent(GameEventKind.WiredBossBelowHalf, boss.Machine.Id, boss.Machine.Position, Detail: boss.Plan.Boss!.Name));
        if (run.SpawnPoints.Count == 0)
            return;
        run.LastGroupAt = Time;
        var nearest = run.SpawnPoints.MinBy(p => Vector2.DistanceSquared(p, boss.Machine.Position));
        Arrive(run, WiredDirector.Reinforcements(Data, run.Wave, run.Options.Difficulty, Random), nearest);
    }

    /// <summary>Every few seconds: machines of the enemy that came over to it join the wave, the idle get new orders, and those that hang are fetched.</summary>
    private void ReviewFoes(WiredRun run)
    {
        Enlist(run);
        var hero = Quarry(run);
        var boss = run.Bosses.FirstOrDefault()?.Machine;
        var pod = AttackedPod(run);
        GiveOrders(run, run.Foes.Where(foe => foe.Machine.Order is IdleOrder or ReturnOrder
            || foe.HuntsHero && hero is not null && (foe.Machine.Order is AttackMoveOrder || foe.Machine.Order is AttackOrder attack && attack.Target == pod?.Id)
            || foe.Plan.Healer && boss is not null && boss.HitPoints < boss.MaxHitPoints && foe.Machine.Order is not HealOrder
            || WantsNextEscort(run, foe)).ToList());
        if (pod is null)
            return;
        foreach (var foe in run.Foes)
        {
            var machine = foe.Machine;
            var durability = machine.HitPoints + machine.Armour;
            if (Vector2.DistanceSquared(machine.Position, foe.LastPlace) >= 1 || durability != foe.LastDurability
                || machine.Weapons.Any(w => w.Reload > 0 || w.RoundsLeft > 0) || machine.Order is HealOrder)
                foe.StirredAt = Time;
            (foe.LastPlace, foe.LastDurability) = (machine.Position, durability);
        }
        if (run.Groups.Count > 0)
            return;
        var lastCall = Time - run.LastGroupAt >= WiredBalance.StandingTime + WiredBalance.LastCall;
        foreach (var foe in run.Foes.ToList())
        {
            var still = Time - MathF.Max(foe.StirredAt, run.LastGroupAt) >= WiredBalance.StandingTime;
            if ((still || lastCall && !foe.Fetched) && foe.Machine.DistanceTo(pod) > WiredBalance.PullDistance)
                Fetch(run, foe, pod);
        }
    }

    /// <summary>Puts on the roster what fights for the enemy without being on it: escorts turned by a traitor, for one.</summary>
    private void Enlist(WiredRun run)
    {
        var known = run.Foes.Select(f => f.Machine.Id).ToHashSet();
        known.UnionWith(run.Turned.Select(t => t.Foe.Machine.Id));
        foreach (var machine in Machines.Where(m => m.Race == run.Enemy && !m.IsDead && !known.Contains(m.Id)).OrderBy(m => m.Id).ToList())
        {
            machine.DefCon = DefCon.High;
            var key = new ResearchKey(machine.Spec.Type, machine.Spec.SubType, machine.Stats.HardwareLevel, machine.WeaponCombo);
            run.Foes.Add(new WiredFoe(machine, new WiredFoePlan(key), huntsHero: false, Time));
        }
    }

    /// <summary>A machine that hung teleports to a place near the pod that the pod can be reached from, on the side it came from if there is one.</summary>
    private void Fetch(WiredRun run, WiredFoe foe, Construction pod)
    {
        var machine = foe.Machine;
        if (FetchPlace(machine, pod) is not { } place)
            return;
        StartAfresh(machine);
        machine.Position = FreeSpotNear(place, MathF.Max(1, machine.Stats.LowClearance));
        foe.Fetched = true;
        foe.LastPlace = machine.Position;
        foe.StirredAt = Time;
        Raise(new GameEvent(GameEventKind.WiredFoePulled, machine.Id, machine.Position));
        GiveOrders(run, [foe]);
    }

    /// <summary>The first of the places round the pod, going to either side of the way the machine lies, in the door's region.</summary>
    private Vector2? FetchPlace(Machine machine, Construction pod)
    {
        const int Steps = 16;
        var door = Nav.Cell(Doorstep(pod));
        var region = Nav.Region(door.X, door.Y, crossesWater: false);
        var away = machine.Position - pod.Position;
        away = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX;
        for (var step = 0; step < Steps; step++)
        {
            var angle = (step + 1) / 2 * (step % 2 == 0 ? -1 : 1) * MathF.Tau / Steps;
            var (sin, cos) = (DetMath.Sin(angle), DetMath.Cos(angle));
            var place = pod.Position + new Vector2(away.X * cos - away.Y * sin, away.X * sin + away.Y * cos) * WiredBalance.PullDistance;
            if (!Nav.OnPlanet(place))
                continue;
            var cell = Nav.Cell(place);
            if (Nav.Region(cell.X, cell.Y, crossesWater: false) == region)
                return place;
        }
        return null;
    }
}
