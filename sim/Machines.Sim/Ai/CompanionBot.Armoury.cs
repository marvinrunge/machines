using System.Numerics;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// Stage 7's behaviour of a computer's Grunt for package W (section 6, the armoury; plan stufe7 C11, design-waves §6 "Die
// Gefährten"): after the Notabschaltung's restart and a task, before following. It holds still while the Locator mends or
// arms it, gets out of the heavy Grunt's line when it revs at it, comes to the Locator for a Bolter it carries, and, armed,
// fights a batch from the nearest free defence post.
public sealed partial class CompanionBot
{
    /// <summary>The defence posts of the armoury (deck markers), and how far from another hero a Grunt takes one.</summary>
    private static readonly string[] DefencePosts = ["def_1", "def_2", "def_3", "def_4"];
    private const float PostApart = 4, Dodge = 6, ComeNear = 5;

    /// <summary>This section's behaviour for the Grunt; true when it acted this look (nothing after it runs).</summary>
    /// <summary>Before any job (a barricade, a post): held still while L-7 works on it, out of the charge's line, to L-7 for a carried Bolter.</summary>
    private bool ArmouryFirst(StoryRun run, Machine grunt)
    {
        if (run.Section != 6)
            return false;
        var waves = run.Waves;
        // Held still while the Locator mends it or mounts a Bolter on it.
        if (world.BeingWorkedOn(run, seat))
        {
            world.CompanionMove(grunt, grunt.Position);
            return true;
        }
        // The heavy Grunt revs at it, or runs down a line it stands on: sideways out of it.
        if (DodgeCharge(run, grunt))
            return true;
        // A Bolter carried and none on it: to the Locator, to be armed.
        if (run.Flags.Contains(StoryMarks.ArmourySealed) && !waves.Fighting && waves.Carried is not null && grunt.Weapons.Count == 0
            && world.HeroOf(run.Locator) is { } locator)
        {
            world.CompanionMove(grunt, Vector2.Distance(grunt.Position, locator.Position) > ComeNear ? locator.Position : grunt.Position);
            return true;
        }
        return false;
    }

    private bool ThinkArmoury(StoryRun run, Companion companion, Machine grunt)
    {
        if (run.Section != 6 || !run.Flags.Contains(StoryMarks.ArmourySealed))
            return false;
        var waves = run.Waves;
        if (!waves.Fighting || grunt.Weapons.Count == 0)
            return false;
        // Armed in a batch: fire at what it sees, else to the nearest free defence post.
        if (FireIfArmed(run, grunt))
            return true;
        if (Post(run, grunt) is { } post)
            world.CompanionMove(grunt, post);
        return true;
    }

    private bool DodgeCharge(StoryRun run, Machine grunt)
    {
        var waves = run.Waves;
        if (waves.Boss == 0 || waves.Phase is not (StoryChargePhase.Telegraph or StoryChargePhase.Run) || world.Find<Machine>(waves.Boss) is not { IsDead: false } boss)
            return false;
        var rel = grunt.Position - boss.Position;
        var ahead = Vector2.Dot(rel, waves.RunWay);
        var across = rel.X * waves.RunWay.Y - rel.Y * waves.RunWay.X;
        if (ahead < 0 || ahead > GameWorld.ChargeLength + boss.Radius || MathF.Abs(across) > boss.Radius + grunt.Radius + 1 && waves.RunTarget != grunt.Id)
            return false;
        var side = new Vector2(waves.RunWay.Y, -waves.RunWay.X) * (across >= 0 ? 1 : -1);
        var to = grunt.Position + side * Dodge;
        world.CompanionMove(grunt, world.Nav.IsFree(to, GameWorld.MobilityOf(grunt)) ? to : grunt.Position - side * Dodge);
        return true;
    }

    /// <summary>The nearest defence post to the Locator no other hero of the trio stands within <see cref="PostApart"/> m of; null when none.</summary>
    private Vector2? Post(StoryRun run, Machine grunt)
    {
        if (world.HeroOf(run.Locator) is not { } locator || world.Deck is not { } deck)
            return null;
        Vector2? best = null;
        foreach (var name in DefencePosts)
        {
            if (deck.Markers.FirstOrDefault(m => m.Name == name) is not { } marker)
                continue;
            var taken = run.Seats.Any(s => s != seat && world.HeroOf(s) is { } other && Vector2.Distance(other.Position, marker.Position) < PostApart);
            if (taken)
                continue;
            if (best is null || Vector2.Distance(marker.Position, locator.Position) < Vector2.Distance(best.Value, locator.Position))
                best = marker.Position;
        }
        return best;
    }
}
