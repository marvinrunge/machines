using System.Numerics;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The special waves of a Wired run and the machines with a part of their own in them, the remake's own addition.
/// <see cref="WiredDirector"/> says what a special wave brings; here it starts and ends, and its machines get their orders.
/// Also here: machines the hero turned, which still belong to the wave.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>What lasts for a whole special wave starts, and the HUD is told.</summary>
    private void BeginSpecial(WiredRun run)
    {
        run.Special = run.NextSpecial;
        run.NextSpecial = WiredSpecial.None;
        if (run.Special == WiredSpecial.None)
            return;
        run.LastSpecial = run.Special;
        if (run.Special == WiredSpecial.Night)
            Darkness = (ForcedDarkness = WiredBalance.NightDarkness).Value;
        if (run.Special == WiredSpecial.IonStorm)
            run.NextStrike = Time + WiredBalance.IonEvery - WiredBalance.IonWarning;
        Raise(new GameEvent(GameEventKind.WiredSpecialWave, 0, Vector2.Zero, run.Wave, run.Special.ToString()));
    }

    /// <summary>The wave is over, and with it the night and the strikes not yet down.</summary>
    private void EndSpecial(WiredRun run)
    {
        run.Special = WiredSpecial.None;
        run.Strikes.Clear();
        ForcedDarkness = null;
    }

    // ---- the ion storm ----------------------------------------------------------------------------

    /// <summary>Every so often a circle marks where the hero or the pod is, and a real ion strike comes down on it a few seconds later.</summary>
    private void UpdateStrikes(WiredRun run)
    {
        if (run.Special == WiredSpecial.IonStorm && Time >= run.NextStrike)
        {
            run.NextStrike = Time + WiredBalance.IonEvery;
            if (StrikeTarget(run) is { } place)
            {
                run.Strikes.Add(new WiredStrike(place, WiredBalance.IonRadius, Time + WiredBalance.IonWarning));
                Raise(new GameEvent(GameEventKind.WiredIonWarning, 0, place));
            }
        }
        while (run.Strikes.Count > 0 && Time >= run.Strikes[0].At)
        {
            StartBlast(Data.Parm.Weapon("ION_ORBITAL_CANNON"), run.Strikes[0].Position, run.Enemy);
            run.Strikes.RemoveAt(0);
        }
    }

    /// <summary>Where a strike goes: at the hero more often than not, and at the pod while the hero is down.</summary>
    private Vector2? StrikeTarget(WiredRun run)
    {
        var hero = run.Heroes.Keys.Select(HeroOf).OfType<Machine>().FirstOrDefault(m => m.Inside is null);
        if (hero is not null && Random.Next(100) < WiredBalance.IonOnHeroPercent)
            return hero.Position;
        return AttackedPod(run)?.Position ?? hero?.Position;
    }

    // ---- machines with a part of their own ---------------------------------------------------------

    /// <summary>Gives a machine its orders if it has a part to play; false for one that is left to the usual orders.</summary>
    private bool TakesPart(WiredRun run, WiredFoe foe, Construction pod)
    {
        var machine = foe.Machine;
        switch (foe.Plan.Role)
        {
            case WiredRole.Spy:
                // Out of mines it has nothing to fight with, so it cannot march as the others do; it comes to be found.
                if (machine.Mines == 0)
                    Send(machine, new MoveOrder(Doorstep(pod)), Doorstep(pod));
                else if (machine.Order is not DropMinesOrder)
                    LayMines(machine, pod);
                return true;
            case WiredRole.Besieger:
                Send(machine, new AttackOrder(pod.Id), pod.Position);
                return true;
            case WiredRole.Turncoat when NearestEscort(run, machine) is { } victim:
                Send(machine, new AttackOrder(victim.Id, Then: Doorstep(pod)), victim.Position);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether a turncoat is after something else while there is an escort left to turn.</summary>
    private bool WantsNextEscort(WiredRun run, WiredFoe foe) =>
        foe.Plan.Role == WiredRole.Turncoat && NearestEscort(run, foe.Machine) is not null
        && !(foe.Machine.Order is AttackOrder attack && Find<Machine>(attack.Target) is { } mark && mark.Race != run.Enemy && IsWiredEscort(mark));

    /// <summary>The escort nearest a machine, of any hero.</summary>
    private Machine? NearestEscort(WiredRun run, Machine machine) => run.Heroes.Keys.SelectMany(Escorts)
        .OrderBy(e => Vector2.DistanceSquared(e.Position, machine.Position)).ThenBy(e => e.Id).FirstOrDefault();

    /// <summary>
    /// A spy sets off to lay its mines: some on the last stretch of its way, to catch what goes out to meet the wave, and
    /// the others in a ring round the pod.
    /// </summary>
    private void LayMines(Machine spy, Construction pod)
    {
        var points = new List<Vector2>();
        var onTheWay = spy.Mines * WiredBalance.MinesOnTheWayPercent / 100;
        var door = Doorstep(pod);
        var back = Vector2.Normalize(spy.Position - door);
        for (var i = onTheWay; i > 0; i--)
            points.Add(OnFreeGround(door + back * (WiredBalance.MineRingFar + i * WiredBalance.MineRoadGap)));
        for (var i = onTheWay; i < spy.Mines; i++)
        {
            var angle = Random.NextSingle() * MathF.Tau;
            var distance = WiredBalance.MineRingNear + Random.NextSingle() * (WiredBalance.MineRingFar - WiredBalance.MineRingNear);
            points.Add(OnFreeGround(pod.Position + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * distance));
        }
        Send(spy, new DropMinesOrder(points), points[0]);
    }

    // ---- machines the hero turned ------------------------------------------------------------------

    /// <summary>
    /// A machine of the waves changed sides. It fights for its new side of its own accord for a while, and the wave does
    /// not end before it has been dealt with.
    /// </summary>
    private void Turned(WiredRun run, WiredFoe foe)
    {
        foe.Machine.DefCon = DefCon.Low;
        foe.Machine.Alert.Raise(FullAlert);
        run.Turned.Add(new WiredTurned(foe, Time + WiredBalance.TreacheryTime));
    }

    /// <summary>Turned machines that fell are off the list, and those whose time is up go back. With nobody left to fight they go back at once.</summary>
    private void BringBackTurned(WiredRun run)
    {
        for (var i = 0; i < run.Turned.Count; i++)
        {
            var (foe, backAt) = run.Turned[i];
            if (foe.Machine.IsDead || Find<Machine>(foe.Machine.Id) is null)
                run.Turned.RemoveAt(i--);
            else if (Time >= backAt || run.Foes.Count == 0 && run.Groups.Count == 0)
            {
                run.Turned.RemoveAt(i--);
                GoBack(run, foe);
            }
        }
    }

    private void GoBack(WiredRun run, WiredFoe foe)
    {
        var machine = foe.Machine;
        Defect(machine, run.Enemy);
        machine.DefCon = DefCon.High;
        foe.StirredAt = Time;
        run.Foes.Add(foe);
        Raise(new GameEvent(GameEventKind.WiredFoeWentBack, machine.Id, machine.Position));
        GiveOrders(run, [foe]);
    }
}
