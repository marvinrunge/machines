using System.Numerics;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The geo locators' pulse, the remake's own addition: a hero on Geo Locator 1 or 3 sends it out with the special's key
/// (<see cref="WiredSpecialCommand"/>) every <see cref="WiredBalance.LocateCooldown"/> seconds, and every enemy machine
/// within its scanner's reach as things stand (<see cref="ScannerReach"/>, which the dark shortens) is located for
/// <see cref="WiredBalance.LocateTime"/> seconds. Meanwhile a hit on it from any enemy of its counts
/// <see cref="WiredBalance.LocateShare"/> times – the hero's, an escort's, a tower's, a team-mate's in a battle: the scout
/// marks, the squad hits harder – and a located Phantom has no shadow (GameWorld.WiredShadow). The pulse is no shot and
/// gives nothing away. What is located is kept in <see cref="WiredRun.Located"/>, which only <see cref="PruneLocated"/>
/// thins out and only <see cref="Locate"/> fills; every question here changes nothing. A Wired hero never locates ore as
/// the original's geo locators do (GameWorld.WhyNotInWired). Nothing here runs without <see cref="Wired"/>.
/// </summary>
public sealed partial class GameWorld
{
    /// <returns>
    /// null if the hero can send out its pulse now, otherwise why not: it is down, inside the pod, the battle has not begun
    /// or is over, the chassis has no pulse, or the last one went out less than <see cref="WiredBalance.LocateCooldown"/>
    /// ago. Nothing in reach is no reason: that answer would tell more than the scanner shows. The HUD asks the same.
    /// </returns>
    public string? WhyNoLocate(WiredState state)
    {
        if (HeroOf(state.Race) is not { } hero)
            return "the hero is down";
        if (hero.Inside is not null)
            return "not from inside the pod";
        if (Battle is { } battle)
        {
            if (battle.InOpening(Time))
                return "the battle has not begun yet";
            if (battle.Over)
                return "the battle is over";
        }
        if ((WornSpecial(state) & ChassisSpecial.Locate) == 0)
            return "this chassis has nothing to use";
        return LocateLeft(state) > 0 ? "that is not ready yet" : null;
    }

    /// <summary>
    /// The seconds until the hero's next pulse, 0 once it is ready. Times are sums of ticks, so the tick within half a tick
    /// of the end counts as the end.
    /// </summary>
    private float LocateLeft(WiredState state) => Time >= state.LocateReadyAt - Dt / 2 ? 0 : state.LocateReadyAt - Time;

    /// <summary>
    /// The pulse: every enemy machine standing and out in the open within the hero's scanner reach, centre to centre, is
    /// located from now on for <see cref="WiredBalance.LocateTime"/> seconds; one located for longer already keeps its time.
    /// Flyers count; buildings, towers and artefacts are no machines. The next pulse waits its time from now, whatever this
    /// one found.
    /// </summary>
    private string? Locate(WiredState state)
    {
        if (WhyNoLocate(state) is { } why)
            return why;
        var run = Wired!;
        var hero = HeroOf(state.Race)!;
        var reach = ScannerReach(hero);
        var until = Time + WiredBalance.LocateTime;
        var located = 0;
        foreach (var machine in Machines.Where(m => !m.IsDead && m.Inside is null && AreEnemies(hero.Race, m.Race)
                     && Vector2.DistanceSquared(m.Position, hero.Position) <= reach * reach).OrderBy(m => m.Id).ToList())
        {
            run.Located[machine.Id] = run.Located.TryGetValue(machine.Id, out var old) && old > until ? old : until;
            located++;
        }
        state.LocateReadyAt = Time + WiredBalance.LocateCooldown;
        Raise(new GameEvent(GameEventKind.WiredLocated, hero.Id, hero.Position, located));
        return null;
    }

    /// <summary>
    /// The share of a hit a located machine takes from <paramref name="dealer"/>: <see cref="WiredBalance.LocateShare"/>
    /// while it is located and the dealer, still standing, is an enemy of its; exactly 1 otherwise, also for a hit without
    /// a dealer (a mine, a strike from the sky, a round whose shooter is gone), so that no chance is drawn for it.
    /// </summary>
    private float LocatedShare(Actor target, Actor? dealer) =>
        dealer is { IsDead: false } && Wired!.Located.TryGetValue(target.Id, out var until) && StillLocated(until) && AreEnemies(dealer.Race, target.Race)
            ? WiredBalance.LocateShare
            : 1;

    /// <summary>Forgets the located machines that are gone, have fallen or whose time is up. Only the run's tick does it.</summary>
    private void PruneLocated(WiredRun run)
    {
        if (run.Located.Count == 0)
            return;
        List<int>? gone = null;
        foreach (var (id, until) in run.Located)
        {
            if (!StillLocated(until) || Find<Machine>(id) is not { IsDead: false })
                (gone ??= []).Add(id);
        }
        if (gone is null)
            return;
        foreach (var id in gone)
            run.Located.Remove(id);
    }

    /// <summary>The seconds a machine stays located, 0 when it is not: for the view's marks and the Phantom's shadow.</summary>
    public float LocatedFor(int actor) =>
        Wired is { } run && run.Located.TryGetValue(actor, out var until) && StillLocated(until) ? until - Time : 0;

    /// <summary>
    /// Whether a machine located until then still is. Times are sums of ticks, so the tick within half a tick of the end is
    /// its end: a pulse locates for just as many ticks as its seconds come to, and never one more.
    /// </summary>
    private bool StillLocated(float until) => until - Time > Dt / 2;

    /// <summary>
    /// What the HUD shows of the pulse: the wait for the next one and how far it has come, and the hero's enemies that
    /// stand located now.
    /// </summary>
    private WiredSpecialView LocateView(WiredState state)
    {
        var left = LocateLeft(state);
        var located = 0;
        foreach (var (id, until) in Wired!.Located)
        {
            if (StillLocated(until) && Find<Machine>(id) is { IsDead: false } machine && AreEnemies(state.Race, machine.Race))
                located++;
        }
        var why = WhyNoSpecial(state);
        return new WiredSpecialView(WornSpecial(state), "Locate", null, left, 1 - MathF.Min(1, left / WiredBalance.LocateCooldown),
            located, 0, why is null, why);
    }
}
