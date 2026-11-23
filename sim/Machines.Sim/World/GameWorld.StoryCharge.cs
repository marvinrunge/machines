using System.Numerics;
using Machines.Sim.Story;

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
                if (Time < waves.NextCharge)
                    break;
                if (ChargeTarget(run, boss) is not { } target)
                {
                    waves.NextCharge = Time + 1;
                    break;
                }
                var way = target.Position - boss.Position;
                (waves.RunWay, waves.RunFrom, waves.RunTarget) = (Vector2.Normalize(way), boss.Position, target.Id);
                (waves.Phase, waves.PhaseSince) = (StoryChargePhase.Telegraph, Time);
                director.Held.Add(boss.Id);
                HoldForStory(boss.Id);
                boss.Facing = DetMath.Atan2(way.Y, way.X);
                waves.Charges++;
                StoryEvents.Cue(this, $"boss rev {target.Id}", boss.Position, boss.Id);
                break;

            case StoryChargePhase.Telegraph:
                boss.Facing = DetMath.Atan2(waves.RunWay.Y, waves.RunWay.X);
                if (since < ChargeTelegraph)
                    break;
                (waves.Phase, waves.PhaseSince, waves.RunFrom) = (StoryChargePhase.Run, Time, boss.Position);
                waves.RunHits.Clear();
                StoryEvents.Cue(this, "boss run", boss.Position, boss.Id);
                break;

            case StoryChargePhase.Run:
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

    /// <summary>The hero the boss charges: of the trio standing 12–35 m off in a clear line, the Locator first, else the nearest.</summary>
    private Machine? ChargeTarget(StoryRun run, Machine boss)
    {
        Machine? best = null;
        foreach (var seat in run.Seats)
        {
            if (HeroOf(seat) is not { IsDead: false } hero || StoryInert(hero))
                continue;
            var distance = Vector2.Distance(hero.Position, boss.Position);
            // A clear line on the ground the hero stands on (the boss's own class sees no line across the armoury's clutter).
            if (distance < ChargeNearest || distance > ChargeFurthest || !Nav.LineOfSight(boss.Position, hero.Position, MobilityOf(hero)))
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
            if (DistanceToSegment(hero.Position, boss.Position, next) > boss.Radius + hero.Radius)
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
