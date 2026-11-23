using System.Globalization;
using System.Numerics;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// The Notabschaltung (plan stufe7 C7, <see cref="StoryReserve"/>): while the director's flag <see cref="StoryMarks.Reserve"/>
/// is set, the Locator at 0 hit points shuts down at 1 instead of falling. It lies inert (nothing aims at it or harms it,
/// it neither drives nor fires; its hack is unplugged with its solved tiles kept) until a Grunt standing within
/// <see cref="StoryReserve.Reach"/> holds Use on it ("seat:&lt;the Locator's player&gt;") for <see cref="StoryReserve.PlugSeconds"/>.
/// Then it comes back with half its hit points and <see cref="StoryReserve.Grace"/> seconds nothing harms it. The view hears
/// <c>l7 shutdown</c>, <c>l7 cable &lt;seat&gt;</c>, <c>l7 cable off</c> and <c>l7 restart &lt;seat&gt;</c>.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The Locator lies shut down, waiting for a Grunt's cable (the Notabschaltung).</summary>
    public bool LocatorShutDown => Story is { } run && run.Flags.Contains(StoryMarks.L7Down);

    /// <summary>The cable's progress, 0 to 1, while a Grunt holds it in the shut-down Locator; null while none does.</summary>
    public float? CableShare => Story is { } run && run.Flags.Contains(StoryMarks.L7Down) && run.Reserve.Plugger != 0
        ? Math.Min(1, run.Reserve.PlugTicks / (StoryReserve.PlugSeconds * TicksPerSecond)) : null;

    /// <summary>The seconds the shut-down Locator still waits for a cable before it is lost; null while it is up.</summary>
    public float? ShutdownLeft => Story is { } run && run.Flags.Contains(StoryMarks.L7Down)
        ? Math.Max(0, StoryReserve.Wait - (Tick - run.Reserve.DownTick) / (float)TicksPerSecond) : null;

    /// <summary>The Use target of the Locator for a Grunt's player: its seat (<see cref="SeatTarget"/>).</summary>
    private static string LocatorTarget(StoryRun run) => SeatTarget + run.Locator.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A hit took the Locator to 0 or below: with the reserve on and not spent since the last checkpoint, it shuts down at 1
    /// hit point (true); else it falls as before (false). Its hack is unplugged where it stood, its hold on a Grunt let go.
    /// </summary>
    private bool ShutDownLocator(StoryRun run, Machine locator)
    {
        if (!run.Flags.Contains(StoryMarks.Reserve) || run.Flags.Contains(StoryMarks.ReserveUsed) || run.Flags.Contains(StoryMarks.L7Down))
            return false;
        locator.HitPoints = 1;
        locator.Armour = 0;
        locator.ArmourRegen = 0;
        run.Flags.Add(StoryMarks.L7Down);
        run.Flags.Add(StoryMarks.ReserveUsed);
        run.Reserve.DownTick = Tick;
        run.Reserve.LetGo();
        foreach (var (_, session) in run.Hacks.Sessions)
        {
            if (session is { Open: true } && session.Hacker == run.Locator)
                session.Hacker = null;
        }
        LetGoOfUse(run, run.Locator);
        StartAfresh(locator);
        HoldStill(locator.Id);
        StoryEvents.Cue(this, "l7 shutdown", locator.Position);
        return true;
    }

    /// <summary>
    /// A tick of the Notabschaltung: the cable held in counts on while its Grunt stands up and in reach (a hit does not break
    /// it), and lets go otherwise; the Locator comes back once it is in. Lost with no cable after <see cref="StoryReserve.Wait"/>
    /// seconds, or with both Grunts down.
    /// </summary>
    private void UpdateReserve(StoryRun run)
    {
        if (!run.Flags.Contains(StoryMarks.L7Down) || run.Over)
            return;
        if (HeroOf(run.Locator) is not { } locator)
            return;
        HoldStill(locator.Id);
        var reserve = run.Reserve;
        var target = LocatorTarget(run);
        var plugger = run.Seats.Skip(1).Where(s => run.Crew.Holding.TryGetValue(s.Value, out var what) && what == target)
            .Where(s => HeroOf(s) is { } grunt && !CompanionDown(s) && !CompanionDormant(s)
                && Vector2.Distance(grunt.Position, locator.Position) <= StoryReserve.Reach + grunt.Radius + locator.Radius)
            .Select(s => (PlayerId?)s).FirstOrDefault();
        if (plugger is not { } seat)
        {
            if (reserve.Plugger != 0)
                StoryEvents.Cue(this, "l7 cable off", locator.Position);
            reserve.LetGo();
        }
        else
        {
            if (reserve.Plugger != seat.Value)
            {
                (reserve.Plugger, reserve.PlugTicks) = (seat.Value, 0);
                StoryEvents.Cue(this, "l7 cable " + run.Seats.IndexOf(seat).ToString(CultureInfo.InvariantCulture), locator.Position);
                CableLine(run, seat, Prologue.Lines.IkeCable, Prologue.Lines.BobCable);
            }
            HoldStill(HeroOf(seat)!.Id);
            if (++reserve.PlugTicks >= (int)(StoryReserve.PlugSeconds * TicksPerSecond))
            {
                Restart(run, locator, seat);
                return;
            }
        }
        var bothDown = run.Seats.Skip(1).All(s => HeroOf(s) is null || CompanionDown(s));
        var waited = Tick - reserve.DownTick >= (long)(StoryReserve.Wait * TicksPerSecond);
        if (bothDown || waited && reserve.PlugTicks == 0)
        {
            run.Ending = StoryEnding.LocatorLost;
            StoryEvents.Cue(this, bothDown ? "l7 lost grunts" : "l7 lost wait", locator.Position);
        }
    }

    /// <summary>The cable is in: the Locator comes back with half its hit points and a moment nothing harms it.</summary>
    private void Restart(StoryRun run, Machine locator, PlayerId seat)
    {
        run.Flags.Remove(StoryMarks.L7Down);
        locator.HitPoints = Math.Max(locator.HitPoints, Math.Max(1, (int)(locator.MaxHitPoints * StoryReserve.BackShare)));
        if (Wired?.Heroes.TryGetValue(run.Locator, out var state) == true)
            state.InvulnerableUntil = Time + StoryReserve.Grace;
        run.Reserve.LetGo();
        run.Reserve.DownTick = -1;
        run.Reserve.Restarts++;
        run.Crew.Holding.Remove(seat.Value);
        StoryEvents.Cue(this, "l7 restart " + run.Seats.IndexOf(seat).ToString(CultureInfo.InvariantCulture), locator.Position);
        StoryEvents.Line(this, Prologue.Lines.L7Restart, StoryAudience.Inner, run.Locator, locator.Id, locator.Position);
        CableLine(run, seat, Prologue.Lines.IkeCableDone, Prologue.Lines.BobCableDone);
    }

    /// <summary>The Grunt with the cable says so over the radio: Ike's line or Bob's by its seat.</summary>
    private void CableLine(StoryRun run, PlayerId seat, string ike, string bob)
    {
        if (HeroOf(seat) is { } grunt)
            StoryEvents.Line(this, run.Seats.IndexOf(seat) == 1 ? ike : bob, StoryAudience.Companion, seat, grunt.Id, grunt.Position);
    }
}
