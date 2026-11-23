using System.Numerics;
using Machines.Sim.Story;
using Machines.Sim.Navigation;

namespace Machines.Sim.World;

/// <summary>
/// The heavy Grunt's charge in the armoury (plan stufe7 §4 W, design-waves §6), modelled on Wired's dash and ram: every
/// <see cref="ChargeEvery"/> seconds (fewer once it is hurt) it picks a hero 12–35 m off in a clear line, stops and roars for
/// <see cref="ChargeTelegraph"/> s on the line it locked at once, then runs straight down it at <see cref="ChargeSpeed"/> m/s
/// for up to <see cref="ChargeLength"/> m without turning. A hero in its way loses a share of its hit points, is thrown
/// <see cref="ChargeThrow"/> m to the side onto free ground (never into a wall, never pinned) and the boss runs on; a wall,
/// a cover block, a crate or a door ends the run: it stands stunned for <see cref="ChargeStun"/> s with its armour off (the
/// radiator open), and a crate it hit breaks. A Grunt's ram on it then adds a second, once a stun. Also the armoury's
/// self-repair of L-7 and the small hands the waves need of the world.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>Seconds between charges: whole, below half, below a quarter.</summary>
    public const float ChargeEvery = 12, ChargeEveryHalf = 10, ChargeEveryQuarter = 8;

    /// <summary>The telegraph's length, the run's speed (m/s) and longest way (m), the crash's stun (s).</summary>
    public const float ChargeTelegraph = 1.5f, ChargeSpeed = 9.5f, ChargeLength = 30, ChargeStun = 3;

    /// <summary>How far a hero may be for a charge at it (m).</summary>
    public const float ChargeNearest = 12, ChargeFurthest = 35;

    /// <summary>The share of a hero's hit points the charge takes: a Grunt's, the Locator's.</summary>
    public const float ChargeHitGrunt = 0.3f, ChargeHitLocator = 0.35f;

    /// <summary>How far a hero hit is thrown aside (m), how long it stands stunned (s), and what a ram adds to a crashed boss's stun (s).</summary>
    public const float ChargeThrow = 5, ChargeHeroStun = 1, ChargeRamStun = 1;

    /// <summary>The seconds to the next charge for a boss so hurt.</summary>
    public static float ChargeCadence(Machine boss) =>
        boss.HitPoints * 4 < boss.MaxHitPoints ? ChargeEveryQuarter : boss.HitPoints * 2 < boss.MaxHitPoints ? ChargeEveryHalf : ChargeEvery;

    /// <summary>A tick of the boss's charge (StoryArmoury.Look).</summary>
    internal void UpdateCharge(StoryRun run)
    {
        var waves = run.Waves;
        if (waves.Boss == 0)
            return;
        var director = run.Director;
        if (Find<Machine>(waves.Boss) is not { IsDead: false } boss)
        {
            director.Held.Remove(waves.Boss);
            waves.Phase = StoryChargePhase.Cruise;
            return;
        }
        var since = Time - waves.PhaseSince;
        switch (waves.Phase)
        {
            case StoryChargePhase.Cruise:
                // From a few seconds before its charge is due it lines up for one (CloseIn, once a second).
                if (Time < waves.NextCharge - LineUpLead)
                    break;
                if (Time < waves.NextCharge || ChargeTarget(run, boss) is not { } target)
                {
                    if (Tick % TicksPerSecond == 0)
                        CloseIn(run, boss);
                    break;
                }
                var way = target.Position - boss.Position;
                (waves.RunWay, waves.RunFrom, waves.RunTarget) = (Vector2.Normalize(way), boss.Position, target.Id);
                (waves.Phase, waves.PhaseSince) = (StoryChargePhase.Telegraph, Time);
                waves.Dodges.Clear();
                director.Held.Add(boss.Id);
                HoldForStory(boss.Id);
                boss.Facing = DetMath.Atan2(way.Y, way.X);
                waves.Charges++;
                StoryEvents.Cue(this, $"boss rev {target.Id}", boss.Position, boss.Id);
                break;

            case StoryChargePhase.Telegraph:
                DodgeForStory(run, boss);
                boss.Facing = DetMath.Atan2(waves.RunWay.Y, waves.RunWay.X);
                if (since < ChargeTelegraph)
                    break;
                (waves.Phase, waves.PhaseSince, waves.RunFrom) = (StoryChargePhase.Run, Time, boss.Position);
                waves.RunHits.Clear();
                StoryEvents.Cue(this, "boss run", boss.Position, boss.Id);
                break;

            case StoryChargePhase.Run:
                DodgeForStory(run, boss);
                ChargeRun(run, boss);
                break;

            case StoryChargePhase.Stunned:
                // A Grunt's ram on the crashed boss keeps it down a second longer, once a stun.
                if (!waves.StunRammed && Stunned(boss))
                {
                    waves.StunRammed = true;
                    waves.PhaseSince += ChargeRamStun;
                    since -= ChargeRamStun;
                }
                if (since < ChargeStun)
                    break;
                boss.Armour = Math.Max(boss.Armour, waves.StunArmour);
                EndCharge(run, boss);
                StoryEvents.Cue(this, "boss recover", boss.Position, boss.Id);
                break;
        }
    }

    private void EndCharge(StoryRun run, Machine boss)
    {
        var waves = run.Waves;
        (waves.Phase, waves.PhaseSince) = (StoryChargePhase.Cruise, Time);
        waves.NextCharge = Time + ChargeCadence(boss);
        run.Director.Held.Remove(boss.Id);
        if (StoryArmouryQuarry(run, boss.Position) is { } hero)
            SendForStory(boss, new AttackOrder(hero.Id), hero.Position);
    }

    private Machine? StoryArmouryQuarry(StoryRun run, Vector2 from) => StoryArmoury.Quarry(this, run, from);

    /// <summary>
    /// A boss due to charge with no hero in a clear line lines up for one, as a bull backs off to run: it drives to the
    /// nearest place it can reach 16-28 m from its quarry (the Locator while it stands, else the nearest Grunt) with a clear
    /// line to it, out of a corner the Locator holds or back from a hero it stands on top of. With no such place (the quarry
    /// in a nook only a Grunt fits) it drives at the quarry, into the armoury, until a line opens. Once a second at most (the
    /// caller's look); on its way its guns are quiet.
    /// </summary>
    private void CloseIn(StoryRun run, Machine boss)
    {
        if (StoryArmouryQuarry(run, boss.Position) is not { } quarry)
            return;
        var mobility = MobilityOf(boss);
        Vector2 goal;
        if (LineUpSpot(boss, quarry, mobility) is { } spot)
            goal = spot;
        else if (Vector2.Distance(quarry.Position, boss.Position) > ChargeNearest)
            goal = quarry.Position;
        else
            return;
        // Not sent again while it is on its way to about the same place: each new order would set it off from rest again.
        if (!(boss.Order is MoveOrder { Target: var going } && boss.PathIndex < boss.Path.Count && Vector2.Distance(going, goal) < 4))
            SendForStory(boss, new MoveOrder(goal), goal);
        // On its way it neither turns to shoot what it sees nor answers fire (a hunter would take the Locator on from the
        // gate at its guns' range and stand there).
        boss.Alert.NoChecksBefore = run.Waves.ClosingUntil = Time + CloseInFocus;
    }

    /// <summary>How long a boss sent to line up keeps its mind on the way (s): longer than the second between its looks.</summary>
    private const float CloseInFocus = 1.5f;

    /// <summary>How long before its charge is due the boss starts lining up for it (s): Old Iron turns slowly on the spot.</summary>
    public const float LineUpLead = 4;

    /// <summary>The distances from its quarry a boss lines up at (m), nearest first.</summary>
    private static ReadOnlySpan<float> LineUpReach => [16, 20, 24, 28];

    /// <summary>The nearest place to the boss, in its own reach, from which it has a charge line at a hero; null when none.</summary>
    private Vector2? LineUpSpot(Machine boss, Machine hero, Mobility mobility)
    {
        var region = Nav.Region((int)MathF.Floor(boss.Position.X), (int)MathF.Floor(boss.Position.Y), mobility);
        Vector2? best = null;
        var bestDistance = float.MaxValue;
        foreach (var reach in LineUpReach)
        {
            for (var k = 0; k < 32; k++)
            {
                var angle = k * MathF.PI / 16;
                var p = hero.Position + reach * new Vector2(DetMath.Cos(angle), DetMath.Sin(angle));
                if (!Nav.OnPlanet(p) || !Nav.IsFree(p, mobility)
                    || Nav.Region((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), mobility) != region || !ChargeLine(p, hero.Position, mobility))
                    continue;
                var distance = Vector2.Distance(p, boss.Position);
                if (distance < bestDistance)
                    (best, bestDistance) = (p, distance);
            }
        }
        return best;
    }

    /// <summary>
    /// Whether a boss at <paramref name="from"/> has a charge line at a hero at <paramref name="to"/>: ground its own class
    /// drives all the way to <see cref="ChargeShort"/> m before the hero. What stands right by the hero (the crate it hides
    /// behind) does not count: the boss runs into it and crashes, stunned.
    /// </summary>
    private bool ChargeLine(Vector2 from, Vector2 to, Mobility mobility)
    {
        var way = to - from;
        var length = way.Length();
        if (length <= ChargeShort)
            return true;
        return Nav.LineOfSight(from, from + way * ((length - ChargeShort) / length), mobility);
    }

    /// <summary>How far before its target a charge line must be clear (m).</summary>
    private const float ChargeShort = 4;

    /// <summary>
    /// Whether the boss revs or runs at a hero, or down a line it stands on (its body's width and a metre to spare, up to the
    /// run's length ahead): a computer's Grunt then sprints out of it before anything else (CompanionBot.DodgeCharge).
    /// </summary>
    public bool InChargeLine(StoryRun run, Machine hero)
    {
        var waves = run.Waves;
        if (waves.Boss == 0 || waves.Phase is not (StoryChargePhase.Telegraph or StoryChargePhase.Run) || Find<Machine>(waves.Boss) is not { IsDead: false } boss)
            return false;
        var rel = hero.Position - boss.Position;
        var ahead = Vector2.Dot(rel, waves.RunWay);
        var across = rel.X * waves.RunWay.Y - rel.Y * waves.RunWay.X;
        return ahead >= 0 && ahead <= ChargeLength + boss.Radius && (MathF.Abs(across) <= boss.Radius + hero.Radius + 1 || waves.RunTarget == hero.Id);
    }

    /// <summary>
    /// The computer's Grunts out of a charge's line, each tick of the rev and the run: once a charge, each picks the nearer of
    /// the two places beside the line clear of the boss's body (and a metre more) on open ground it can drive to straight,
    /// and goes there at its top speed (the sprint's), without turning on the spot first as its own drive would (that ate
    /// the rev's 1.5 s).
    /// </summary>
    private void DodgeForStory(StoryRun run, Machine boss)
    {
        var waves = run.Waves;
        foreach (var seat in run.Seats.Skip(1))
        {
            if (!StoryBotPlays(seat) || HeroOf(seat) is not { IsDead: false } grunt || CompanionDown(seat) || CompanionDormant(seat)
                || Stunned(grunt) || StoryInert(grunt) || waves.RunHits.Contains(grunt.Id))
                continue;
            if (!waves.Dodges.TryGetValue(grunt.Id, out var to))
            {
                if (DodgeSpot(grunt, boss, waves.RunWay) is not { } spot)
                    continue;
                waves.Dodges[grunt.Id] = to = spot;
                // Its own way is dropped, or its drive would pull it back to where it was going.
                StartAfresh(grunt);
            }
            var left = to - grunt.Position;
            var distance = left.Length();
            if (distance < 1e-3f)
                continue;
            var step = MathF.Min(grunt.Stats.Speed * Dt, distance);
            grunt.Position += left / distance * step;
        }
    }

    /// <summary>
    /// Where a Grunt in a charge's line gets out of it: the nearest place out of the boss's way (beside the line clear of its
    /// body and a metre more, beyond the run's end, or behind the boss) on open ground it can drive to in a straight line,
    /// looked for on rings up to <see cref="DodgeFurthest"/> m round it; null when it is clear already or has no way out
    /// (cornered: the plough takes it).
    /// </summary>
    private Vector2? DodgeSpot(Machine grunt, Machine boss, Vector2 way)
    {
        var clear = boss.Radius + grunt.Radius + 1;
        if (!InWay(grunt.Position))
            return null;
        var mobility = MobilityOf(grunt);
        for (var reach = 1f; reach <= DodgeFurthest; reach += 0.5f)
        {
            Vector2? best = null;
            var bestAcross = -1f;
            for (var k = 0; k < 24; k++)
            {
                var angle = k * MathF.PI / 12;
                var p = grunt.Position + reach * new Vector2(DetMath.Cos(angle), DetMath.Sin(angle));
                if (InWay(p) || !Nav.OnPlanet(p) || !Nav.IsFree(p, mobility) || !Nav.LineOfSight(grunt.Position, p, mobility))
                    continue;
                // Of a ring's places the one furthest from the line.
                var rel = p - boss.Position;
                var across = MathF.Abs(rel.X * way.Y - rel.Y * way.X);
                if (across > bestAcross)
                    (best, bestAcross) = (p, across);
            }
            if (best is not null)
                return best;
        }
        return null;

        bool InWay(Vector2 p)
        {
            var rel = p - boss.Position;
            var ahead = Vector2.Dot(rel, way);
            return ahead >= -clear && ahead <= ChargeLength + clear && MathF.Abs(rel.X * way.Y - rel.Y * way.X) <= clear;
        }
    }

    /// <summary>How far a Grunt looks for a place out of a charge's way (m): what it covers at its top speed in the rev.</summary>
    private const float DodgeFurthest = 12;

    /// <summary>The hero the boss charges: of the trio standing 12–35 m off in a clear line, the Locator first, else the nearest.</summary>
    private Machine? ChargeTarget(StoryRun run, Machine boss)
    {
        Machine? best = null;
        foreach (var seat in run.Seats)
        {
            if (HeroOf(seat) is not { IsDead: false } hero || StoryInert(hero))
                continue;
            var distance = Vector2.Distance(hero.Position, boss.Position);
            // A clear line the boss itself can run (its own class, which never crawls), up to the hero's cover.
            if (distance < ChargeNearest || distance > ChargeFurthest || !ChargeLine(boss.Position, hero.Position, MobilityOf(boss)))
                continue;
            if (seat == run.Locator)
                return hero;
            if (best is null || distance < Vector2.Distance(best.Position, boss.Position))
                best = hero;
        }
        return best;
    }

    /// <summary>One tick of the run: heroes in the way hit and thrown aside, a wall or a crate ends it in a crash.</summary>
    private void ChargeRun(StoryRun run, Machine boss)
    {
        var waves = run.Waves;
        var step = waves.RunWay * (ChargeSpeed * Dt);
        var next = boss.Position + step;
        foreach (var seat in run.Seats)
        {
            if (HeroOf(seat) is not { IsDead: false } hero || StoryInert(hero) || waves.RunHits.Contains(hero.Id))
                continue;
            // The plough hits what is before it, not a hero beside or behind where it set off.
            if (DistanceToSegment(hero.Position, boss.Position, next) > boss.Radius + hero.Radius
                || Vector2.Dot(hero.Position - boss.Position, waves.RunWay) < 0)
                continue;
            waves.RunHits.Add(hero.Id);
            ChargeHit(run, boss, hero, seat == run.Locator);
        }
        if (Vector2.Distance(boss.Position, waves.RunFrom) >= ChargeLength)
        {
            EndCharge(run, boss);
            StoryEvents.Cue(this, "boss stop", boss.Position, boss.Id);
            return;
        }
        if (CanDriveTo(boss, next))
        {
            boss.Position = next;
            return;
        }
        // The crash: a crate in the way breaks, the boss stands stunned with its radiator open (its armour off).
        foreach (var crate in Artefacts.Where(a => !a.IsDead && a.Type.Name.Contains("crate", StringComparison.OrdinalIgnoreCase)
            && Vector2.Distance(a.Position, next) <= boss.Radius + a.Radius + 1).OrderBy(a => a.Id).ToList())
        {
            run.Director.Floors.Remove(crate.Id);
            Damage(crate, crate.HitPoints + crate.Armour + 1000, boss.Race, boss);
        }
        waves.StunArmour = boss.Armour;
        boss.Armour = 0;
        waves.StunRammed = false;
        (waves.Phase, waves.PhaseSince) = (StoryChargePhase.Stunned, Time);
        waves.Crashes++;
        StoryEvents.Cue(this, "boss crash", boss.Position, boss.Id);
    }

    /// <summary>A hero in the boss's way: its share of hit points gone (outside the harm share), thrown aside onto free ground, stunned a moment.</summary>
    private void ChargeHit(StoryRun run, Machine boss, Machine hero, bool locator)
    {
        var waves = run.Waves;
        waves.ChargeShare = locator ? ChargeHitLocator : ChargeHitGrunt;
        try
        {
            Damage(hero, 1, boss.Race, boss, "CHARGE");
        }
        finally
        {
            waves.ChargeShare = null;
        }
        waves.ChargeHits++;
        if (ThrowAside(hero, boss, waves.RunWay) is { } landed)
            hero.Position = landed;
        run.Crew.Stunned[hero.Id] = Tick + (int)(ChargeHeroStun * TicksPerSecond);
        StoryEvents.Cue(this, $"boss hit {hero.Id}", hero.Position, boss.Id);
    }

    /// <summary>
    /// Where a hero hit by the charge lands: <see cref="ChargeThrow"/> m to its side of the line, or nearer, on ground it fits
    /// on and clear of the boss's way; the other side if that side is a wall; null (it stays) if neither has room.
    /// </summary>
    public Vector2? ThrowAside(Machine hero, Machine boss, Vector2 way)
    {
        var mobility = MobilityOf(hero);
        var side = new Vector2(-way.Y, way.X);
        var rel = hero.Position - boss.Position;
        var sign = rel.X * side.X + rel.Y * side.Y >= 0 ? 1f : -1f;
        // As far as clears the boss's body (its 3 m and the hero's own) if that is more than the throw.
        var furthest = MathF.Max(ChargeThrow, ThrowFurthest(hero, boss));
        foreach (var s in (ReadOnlySpan<float>)[sign, -sign])
        {
            for (var reach = furthest; reach >= 1; reach -= 0.5f)
            {
                var p = hero.Position + side * (s * reach);
                // Clear of the boss's body where it runs on, on ground that holds the hero.
                if (DistanceToLine(p, boss.Position, way) < boss.Radius + hero.Radius + 0.5f)
                    continue;
                if (Nav.OnPlanet(p) && Nav.IsFree(p, mobility) && Nav.LineOfSight(hero.Position, p, mobility))
                    return p;
            }
        }
        return null;
    }

    /// <summary>The furthest a hero is thrown: enough to clear the boss's body from its line.</summary>
    public static float ThrowFurthest(Machine hero, Machine boss) => boss.Radius + hero.Radius + 1;

    private static float DistanceToLine(Vector2 p, Vector2 a, Vector2 way)
    {
        var rel = p - a;
        return MathF.Abs(rel.X * way.Y - rel.Y * way.X);
    }

    // ---- the armoury's other hands ------------------------------------------------------------------

    /// <summary>L-7 mends itself while the armoury is sealed and no batch is in the room (design-waves §5.3); the Grunts never do.</summary>
    internal void UpdateArmouryRepair(StoryRun run)
    {
        if (!run.Flags.Contains(StoryMarks.ArmourySealed) || run.Waves.Fighting || run.Flags.Contains(StoryMarks.L7Down)
            || HeroOf(run.Locator) is not { IsDead: false } hero || hero.HitPoints >= hero.MaxHitPoints)
            return;
        AddHitPoints(hero, hero.MaxHitPoints * StoryWaves.SelfRepair * Dt);
    }

    /// <summary>A cast machine's hit points set (the boss's co-op share), its health kept in proportion.</summary>
    internal void TuneForStory(Machine machine, int hitPoints) =>
        machine.Refit(machine.Spec, machine.Stats with { HitPoints = Math.Max(1, hitPoints) }, machine.WeaponCombo, machine.Weapons.ToList());

    /// <summary>A hung wave unit set back behind its door, out of sight, its order gone.</summary>
    internal void SetBackForStory(Machine machine, Vector2 at)
    {
        machine.Position = FreeSpotNear(at, MathF.Max(1, machine.Radius));
        StartAfresh(machine);
    }

    /// <summary>Whether a machine is a hero of the story's trio.</summary>
    internal bool InTrioForStory(Machine machine) => Story is { } run && InTrio(run, machine);
}
