using Machines.Sim.Story;
using HarmRule = Machines.Sim.Story.StoryHarm;

namespace Machines.Sim.World;

/// <summary>
/// The story's harm seam (plan stufe7 §4 F item 7, C4), called once from <see cref="WiredHit"/> next to the hero's grace:
/// friendly fire within the trio is vetoed; a hit the armoury's waves (<see cref="StoryWaves.Harm"/>), the escape's pack
/// (<see cref="StoryPack.Harm"/>) or the Thresher's rule (<see cref="HarmRule"/>) takes over is set whole, as that share of
/// the target's hit points with its armour ignored; and what reaches the trio passes the escape's budget
/// (<see cref="StoryBudget.Filter"/>). Today's prologue sets none of the flags and fills none of the parts: its hits pass.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>What the story makes of a hit of <paramref name="dealer"/> on <paramref name="target"/>; <paramref name="damage"/> is set when it is <see cref="StoryHarmVerdict.Fixed"/>.</summary>
    private StoryHarmVerdict StoryHarmSeam(Actor target, ref int damage, Actor? dealer)
    {
        if (Story is not { } run)
            return StoryHarmVerdict.Pass;
        var trio = target is Machine hero && InTrio(run, hero);
        // The trio never hurts itself in the story (C11): a Grunt's Bolter round in the Locator's back, a blast of its own.
        if (trio && dealer is Machine own && InTrio(run, own))
            return StoryHarmVerdict.Veto;
        var share = run.Waves.Harm(target, dealer) ?? run.Pack.Harm(target, dealer);
        if (share is null && trio && dealer is not null && ThresherShot(run, dealer) is { } shot)
        {
            if (!shot)
                return StoryHarmVerdict.Veto;
            share = HarmRule.ThresherShare;
        }
        if (share is { } whole)
        {
            var harm = Math.Max(1, (int)MathF.Ceiling(whole * target.MaxHitPoints));
            if (trio)
                harm = run.Budget.Filter(target, harm);
            if (harm <= 0)
                return StoryHarmVerdict.Veto;
            // Armour ignored: the hit is raised by what the armour would absorb (ArmourModel), so the hit points lose the share.
            damage = harm + Math.Max(0, target.Armour);
            return StoryHarmVerdict.Fixed;
        }
        if (trio)
        {
            damage = run.Budget.Filter(target, damage);
            if (damage <= 0)
                return StoryHarmVerdict.Veto;
        }
        return StoryHarmVerdict.Pass;
    }

    /// <summary>
    /// Whether a hit by <paramref name="dealer"/> falls under the Thresher's rule: null when not (not his role, or no section
    /// uses the rule now); true for a counted shot (noted), false for a round of a burst within <see cref="HarmRule.Gap"/> of it.
    /// </summary>
    private bool? ThresherShot(StoryRun run, Actor dealer)
    {
        if (!run.Flags.Contains(StoryMarks.Stealth) && !run.Flags.Contains(StoryMarks.Escape))
            return null;
        if (!HarmRule.ShotRoles.Any(role => run.Director.Cast.TryGetValue(role, out var ids) && ids.Contains(dealer.Id)))
            return null;
        if (run.Harm.LastShot.TryGetValue(dealer.Id, out var last) && Time - last < HarmRule.Gap - Dt / 2)
            return false;
        run.Harm.LastShot[dealer.Id] = Time;
        return true;
    }

    /// <summary>Whether a machine is one of the trio's heroes.</summary>
    private bool InTrio(StoryRun run, Machine machine)
    {
        foreach (var seat in run.Seats)
        {
            if (Wired?.Heroes.GetValueOrDefault(seat)?.Hero == machine.Id)
                return true;
        }
        return false;
    }
}
