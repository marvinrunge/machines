using System.Numerics;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// The story's hacking (docs/STORY.md, Hacken; plan WP-E): the Locator plugs its probe into a target and turns the tiles of
/// a board (<see cref="HackPuzzle"/>) by lockstep commands, one a turn; while it does it stands still and can be hit. The
/// world keeps the boards (<see cref="StoryRun.Hacks"/>), steps their firewalls on its own clock and raises the alarm when
/// a trace-back runs full. Only in a story: nothing here runs in any other match.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>How near the Locator must stand to a target to plug in, and how far it may be pushed before the probe comes out.</summary>
    public const float HackReach = 5, HackSlack = 2.5f;

    /// <summary>World ticks between two steps of a firewall's pulse (1.2 s).</summary>
    public const int HackPulseTicks = 24;

    /// <summary>The player a hack command speaks for: its issuer, or the Locator's for the race's own authority.</summary>
    private static PlayerId Hacker(StoryRun run, StoryCommand command) => command.Issuer ?? run.Locator;

    internal string? OpenHack(StoryHackOpenCommand command)
    {
        var run = Story!;
        var player = Hacker(run, command);
        if (player != run.Locator)
            return "only the Locator hacks";
        if (HeroOf(player) is not { } hero)
            return "no hero to hack with";
        var desk = run.Hacks;
        if (desk.OpenBy(player) is not null)
            return "already at a hack";
        if (desk.Find(command.Target, Deck) is not { } found)
            return "nothing to hack there";
        if (desk.Done.Contains(command.Target))
            return "that is hacked already";
        if (desk.Locked.Contains(command.Target))
            return "that cannot be hacked yet";
        if (Vector2.Distance(hero.Position, found.Position) > HackReach)
            return "too far away to hack";
        if (!desk.Sessions.TryGetValue(command.Target, out var session))
            desk.Sessions[command.Target] = session = new HackSession(command.Target, found.Kind);
        if (session.Broken)
            return "the link is cut";
        session.Puzzle ??= HackPuzzle.Generate(session.Boards[session.Stage], Random.Next());
        session.Hacker = player;
        session.Hero = hero.Id;
        session.NextPulse = Tick + HackPulseTicks;
        HoldStill(hero.Id);
        return null;
    }

    internal string? TurnHack(StoryHackTurnCommand command)
    {
        var run = Story!;
        var player = Hacker(run, command);
        if (run.Hacks.OpenBy(player) is not { } session || session.Target != command.Target || session.Puzzle is not { } puzzle)
            return "not at that hack";
        if (session.Broken)
            return "the link is cut";
        var turn = puzzle.Turn(command.Cell, command.Clockwise);
        if (turn.Refused is { } why)
            return why;
        if (turn.Complete)
            Solved(run, session);
        else if (turn.Overflow)
            Traced(run, session);
        else if (session.Boards[session.Stage].BreakAt is > 0 and var breakAt && puzzle.Progress >= breakAt)
            session.Broken = true;
        return null;
    }

    internal string? AbortHack(StoryHackAbortCommand command)
    {
        var run = Story!;
        if (run.Hacks.OpenBy(Hacker(run, command)) is not { } session)
            return "not at a hack";
        // A broken-off hack is left at no cost: the story took it away.
        if (!session.Broken && session.Puzzle is { } puzzle && puzzle.AddTrace(HackPuzzle.AbortCost))
            Traced(run, session);
        else
            session.Hacker = null;
        return null;
    }

    /// <summary>
    /// The director breaks a hack off (the main computer's at 90 %): its board stands as it is and takes no more turns; the
    /// Locator stays plugged in until it walks away. False when there is no such hack.
    /// </summary>
    public bool BreakHack(string target)
    {
        if (Story?.Hacks.Session(target) is not { } session)
            return false;
        session.Broken = true;
        return true;
    }

    /// <summary>Whether a machine is a hacking hero, which stands still.</summary>
    public bool IsHacking(int machine) => Story?.Hacks.Sessions.Values.Any(s => s.Open && s.Hero == machine) == true;

    /// <summary>A board won: the next one comes up, or after the last the target is the Locator's.</summary>
    private void Solved(StoryRun run, HackSession session)
    {
        if (session.Stage + 1 < session.Boards.Count)
        {
            session.Stage++;
            session.Puzzle = HackPuzzle.Generate(session.Boards[session.Stage], Random.Next());
            session.NextPulse = Tick + HackPulseTicks;
            return;
        }
        session.Hacker = null;
        run.Hacks.Done.Add(session.Target);
    }

    /// <summary>The trace-back ran full: the alarm, the probe out, and a new board next time from the first.</summary>
    private void Traced(StoryRun run, HackSession session)
    {
        run.Hacks.LastAlarmTick = Tick;
        run.Hacks.LastAlarmBy = session.Hacker;
        session.Alarms++;
        session.Hacker = null;
        session.Puzzle = null;
        session.Stage = 0;
    }

    /// <summary>
    /// A tick of the hacks: a hacker gone or pushed away lets go of its board, and the firewalls step on the world's clock.
    /// </summary>
    private void UpdateHacks()
    {
        var run = Story!;
        foreach (var session in run.Hacks.Sessions.Values)
        {
            if (session is not { Hacker: { } player, Puzzle: { } puzzle })
                continue;
            if (HeroOf(player) is not { } hero || hero.Id != session.Hero
                || run.Hacks.Find(session.Target, Deck) is not { } found || Vector2.Distance(hero.Position, found.Position) > HackReach + HackSlack)
            {
                session.Hacker = null;
                continue;
            }
            HoldStill(hero.Id);
            if (!puzzle.Spec.Firewall || session.Broken || Tick < session.NextPulse)
                continue;
            session.NextPulse = Tick + HackPulseTicks;
            if (puzzle.StepFirewall() == HackPulse.Reached)
                Traced(run, session);
        }
    }

    /// <summary>The hacker's wheels are let go: what it was driving with stops.</summary>
    private void HoldStill(int machine)
    {
        if (DriveInput.TryGetValue(machine, out var input))
            DriveInput[machine] = (0, 0, 0, input.Slow);
        // No trigger held (none at all reads as every one in a run).
        if (PlayerWeapons.ContainsKey(machine) || DriveInput.ContainsKey(machine))
            PlayerWeapons[machine] = 0;
        PlayerButtons.Remove(machine);
    }

    /// <summary>A hacking hero's driver may look about but neither drive nor fire (as a battle's opening holds them).</summary>
    private DriveCommand HeldWhileHacking(DriveCommand drive) =>
        drive.Driving && (IsHacking(drive.Machine) || StoryHolds(drive.Machine))
            ? drive with { Forward = 0, Turn = 0, Strafe = 0, Weapons = 0, Buttons = false }
            : drive;
}
