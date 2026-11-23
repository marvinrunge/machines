using System.Globalization;
using System.Numerics;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// The escape's pack on the world's ticks (package E, design-escape §4; its state is <see cref="StoryPack"/>): the eight come
/// out of the freight lift, run after the trio on the leash (their pace written into the pace table, C12), fire by the
/// story's fair rule when they see a hero (the sight, unchanged), are blind to the ship's hazards, catch up quietly at the
/// last door behind the trio when they fall too far back, close in on a shut-down Locator to delete it, and after the bridge
/// go round through the Notgang to door Y. The view hears <c>thresher near &lt;m&gt;</c> and <c>thresher state &lt;s&gt;</c>
/// for the music (package M).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The pack's live members, not benched, in id order.</summary>
    public IEnumerable<Machine> PackAlive(StoryRun run)
    {
        foreach (var id in run.Pack.Members.Keys)
        {
            if (!run.Pack.Benched.Contains(id) && Find<Machine>(id) is { IsDead: false } member)
                yield return member;
        }
    }

    /// <summary>Counts the machines of the pack's roles the director cast as its members (after each spawn).</summary>
    public void MusterPack(StoryRun run)
    {
        foreach (var (role, kind) in StoryPack.Roster)
        {
            foreach (var id in run.Director.Cast.GetValueOrDefault(role) ?? [])
                run.Pack.Members.TryAdd(id, kind);
        }
    }

    /// <summary>The role a member of the pack was cast as.</summary>
    private static string? PackRole(StoryRun run, int id)
    {
        foreach (var (role, _) in StoryPack.Roster)
        {
            if (run.Director.Cast.TryGetValue(role, out var ids) && ids.Contains(id))
                return role;
        }
        return null;
    }

    private void UpdatePack(StoryRun run)
    {
        var pack = run.Pack;
        if (!pack.Any)
            return;
        var heroes = EscapeHeroes(run).Where(h => CompanionSeat(h) is not { } s || !CompanionDown(s)).ToList();
        if (heroes.Count == 0)
            return;
        var rear = heroes.OrderBy(h => StoryPack.Progress(h.Position)).ThenBy(h => h.Id).First();
        var rearmost = StoryPack.Progress(rear.Position);
        var heroSpeed = MotionOf(rear).Speed;
        var locator = HeroOf(run.Locator);
        var down = run.Flags.Contains(StoryMarks.L7Down) && locator is not null;
        foreach (var (id, kind) in pack.Members)
        {
            if (Find<Machine>(id) is not { IsDead: false } member)
                continue;
            if (pack.Benched.Contains(id))
            {
                HoldForStory(id);
                continue;
            }
            if (PackRole(run, id) is not { } role)
                continue;
            if (kind is PackKind.Heavy or PackKind.Thresher)
                ShoveDebris(member);
            var blind = AimGate(run, member, heroes, down);
            var behind = rearmost - StoryPack.Progress(member.Position);
            var top = MathF.Max(0.1f, member.Stats.Speed);
            float speed;
            if (down)
            {
                speed = StoryPack.ConfirmSpeed;
                // The pack closes in on the shut-down Locator and deletes it (design-escape §4, §6).
                if (kind != PackKind.Thresher && Vector2.Distance(member.Position, locator!.Position) <= StoryPack.ConfirmReach + member.Radius + locator.Radius
                    && !run.Over)
                {
                    run.Ending = StoryEnding.LocatorLost;
                    StoryEvents.Cue(this, "l7 lost pack", locator.Position, member.Id);
                    return;
                }
            }
            else
                speed = StoryPack.LeashSpeed(kind, behind, heroSpeed, top);
            // In steps of a tenth, so a run is not planned again every tick for a hair's change of pace.
            var share = MathF.Round(Math.Clamp(speed / top, 0.05f, 1) * 10) / 10;
            if (share >= 1)
                run.Drives.Paces.Remove(role);
            else
                run.Drives.Paces[role] = MathF.Max(0.05f, share);
            if ((Tick + id) % 10 != 0 || run.Hazards.Staggered.ContainsKey(id) || run.Director.Posts.ContainsKey(role)
                || run.Director.Held.Contains(id))
                continue;
            CatchUp(run, member, behind, heroes, rearmost);
            if (member.Order is AttackOrder && !down && !blind)
                continue;
            var goal = down ? locator!.Position : rear.Position;
            if (member.Order is MoveOrder move && Vector2.Distance(move.Target, goal) < 4)
                continue;
            SendForStory(member, new MoveOrder(goal), goal);
        }
        TellThresher(run, heroes);
    }

    /// <summary>
    /// Whether a member of the pack may aim now (design-escape §4): standing (staggered, or hardly moving) or with a hero within
    /// <see cref="StoryPack.AimReach"/>, it sees by the fair rule; running further off, or with the Locator shut down, it is
    /// blind to the trio. Members the director holds (waiting at the collapse) keep what the director gave them. True when blind.
    /// </summary>
    private bool AimGate(StoryRun run, Machine member, List<Machine> heroes, bool down)
    {
        var eyes = StoryWatch(member);
        if (run.Director.Held.Contains(member.Id))
            return eyes.Blind;
        var stands = run.Hazards.Staggered.ContainsKey(member.Id) || MotionOf(member).Speed < 1;
        var near = heroes.Any(h => Vector2.Distance(h.Position, member.Position) <= StoryPack.AimReach);
        var blind = down || !(stands || near);
        if (blind && !eyes.Blind)
            eyes.Sees.Clear();
        eyes.Blind = blind;
        return blind;
    }

    /// <summary>
    /// The big ones' dozer blades (design-escape §4): debris the ship dropped into the Notgang (the slalom's crates) that lies
    /// within reach of a big Grunt or the Thresher is shoved aside, out of the way, so the Notgang stays wide enough for them.
    /// The view hears <c>shove &lt;placement&gt; &lt;id&gt;</c>.
    /// </summary>
    private void ShoveDebris(Machine member)
    {
        if (Deck?.Zone("R15") is not { } notgang)
            return;
        foreach (var piece in Artefacts.Where(a => !a.IsDead && a.Tag is null && a.Id >= Artefact.IdBase + StoryDirector.PlacedBase
                     && notgang.Contains(a.Position) && Vector2.Distance(a.Position, member.Position) <= member.Radius + 14).ToList())
        {
            RemoveArtefact(piece.Id);
            StoryEvents.Cue(this, FormattableString.Invariant($"shove {piece.Id - Artefact.IdBase} {member.Id}"), piece.Position, member.Id);
        }
    }

    /// <summary>
    /// A straggler far behind and out of every hero's sight for a while is set at the last door behind the trio that no hero
    /// sees (P, the shutter's east side, the gallery's head), never ahead of the trio, never in a hero's view.
    /// </summary>
    private void CatchUp(StoryRun run, Machine member, float behind, List<Machine> heroes, float rearmost)
    {
        var pack = run.Pack;
        if (behind <= StoryPack.CatchUp || heroes.Any(h => Vector2.Distance(h.Position, member.Position) <= StoryPack.OutOfSight))
        {
            pack.Lost.Remove(member.Id);
            return;
        }
        var lost = pack.Lost[member.Id] = pack.Lost.GetValueOrDefault(member.Id) + 10;
        if (lost < StoryPack.CatchUpTicks)
            return;
        Vector2? at = null;
        foreach (var gate in StoryPack.Gates)
        {
            var along = StoryPack.Progress(gate);
            if (along > StoryPack.Progress(member.Position) && along < rearmost - 40
                && heroes.All(h => Vector2.Distance(h.Position, gate) > 45))
                at = gate;
        }
        pack.Lost.Remove(member.Id);
        if (at is not { } spot || !Nav.IsFree(spot, MobilityOf(member)))
            return;
        var to = FreeSpotFor(spot, MathF.Max(1, member.Radius), MobilityOf(member), member);
        StartAfresh(member);
        member.Position = to;
        StoryEvents.Cue(this, FormattableString.Invariant($"pack catch up {member.Id}"), to, member.Id);
    }

    /// <summary>What the music hears of the Thresher in the escape (package M): how near the nearest hero, and what he does.</summary>
    private void TellThresher(StoryRun run, List<Machine> heroes)
    {
        if (Tick % 10 != 0 || run.Director.Of(this, Prologue.ThresherRole).FirstOrDefault() is not { } thresher
            || !run.Pack.Members.ContainsKey(thresher.Id))
            return;
        var near = (int)MathF.Round(heroes.Min(h => Vector2.Distance(h.Position, thresher.Position)));
        if (Math.Abs(near - run.Pack.ToldNear) >= 2)
        {
            run.Pack.ToldNear = near;
            StoryEvents.Cue(this, "thresher near " + near.ToString(CultureInfo.InvariantCulture), thresher.Position, thresher.Id);
        }
        var state = run.Hazards.BridgeFallen ? "lost" : thresher.Order is AttackOrder ? "fire" : run.Hazards.Staggered.ContainsKey(thresher.Id) ? "dwell" : "wary";
        if (state != run.Pack.ToldState)
        {
            run.Pack.ToldState = state;
            StoryEvents.Cue(this, "thresher state " + state, thresher.Position, thresher.Id);
        }
    }

    /// <summary>
    /// The bridge has fallen (design-escape §3 C7, §4): the survivors go round. At most two Reapers (the two-armed first) and
    /// one big Grunt wait north of the collapse, out of sight; the rest stay behind (benched: held, blind, out of the show).
    /// The Thresher stays at the bridgehead.
    /// </summary>
    public void SendPackRound(StoryRun run, params Vector2[] waits)
    {
        var pack = run.Pack;
        if (pack.GoneRound)
            return;
        pack.GoneRound = true;
        var order = new[] { StoryPack.R3a, StoryPack.R2c, StoryPack.R3b, StoryPack.R2a, StoryPack.R2b, StoryHeavy.PackRole2, StoryHeavy.PackRole1 };
        var reapers = 0;
        var heavies = 0;
        var placed = 0;
        foreach (var role in order)
        {
            foreach (var member in run.Director.Of(this, role).ToList())
            {
                var heavy = pack.Members.GetValueOrDefault(member.Id) == PackKind.Heavy;
                var keep = heavy ? heavies++ < 1 : reapers++ < 2;
                if (!keep || placed >= waits.Length)
                {
                    Bench(run, member);
                    continue;
                }
                StartAfresh(member);
                member.Position = FreeSpotFor(waits[placed++], MathF.Max(1, member.Radius), MobilityOf(member), member);
                run.Director.Held.Add(member.Id);
                var eyes = StoryWatch(member);
                eyes.Blind = true;
                eyes.Sees.Clear();
            }
        }
    }

    /// <summary>The survivors waiting at the collapse come on: they see again and make for door Y.</summary>
    public void LetPackThrough(StoryRun run, Vector2 to)
    {
        foreach (var member in PackAlive(run).ToList())
        {
            if (!run.Director.Held.Remove(member.Id))
                continue;
            StoryWatch(member).Blind = false;
            SendForStory(member, new MoveOrder(to), to);
        }
    }

    /// <summary>A member taken out of the show: blind, held, set down in the freight lift where nobody comes any more.</summary>
    private void Bench(StoryRun run, Machine member)
    {
        if (!run.Pack.Benched.Add(member.Id))
            return;
        var eyes = StoryWatch(member);
        eyes.Blind = true;
        eyes.Sees.Clear();
        StartAfresh(member);
        member.Position = FreeSpotFor(new Vector2(520, 272), MathF.Max(1, member.Radius), MobilityOf(member), member);
        HoldForStory(member.Id);
    }

    /// <summary>
    /// Door Y under the pack (design-escape §3 C7): while a hero holds its switch it stays shut; unheld with the pack at it, it
    /// gives after 12 s. True on the tick it gives.
    /// </summary>
    public bool PressY(StoryRun run)
    {
        if (!DoorClosed('Y'))
            return false;
        var y = new Vector2(520, 124);
        var at = PackAlive(run).Any(m => Vector2.Distance(m.Position, y) <= 14);
        var held = run.Crew.Holding.Values.Contains("y_switch")
            || Deck?.Marker("y_switch") is { } sw && EscapeHeroes(run).Any(h => CompanionSeat(h) is not null && Vector2.Distance(h.Position, sw.Position) <= UseReach + h.Radius);
        if (!at || held)
            return false;
        if (++run.Pack.YPressed % 40 == 0)
            StoryEvents.Cue(this, "door Y ram", y);
        return run.Pack.YPressed >= 12 * TicksPerSecond;
    }
}
