using System.Numerics;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// Armed Grunts (plan stufe7 C11): a computer's Grunt with a weapon (the Bolters the Locator mounts in the armoury) fires at
// the nearest hostile it sees within FireReach when it is on no task. Friendly fire within the trio is vetoed by the harm
// seam (GameWorld.StoryHarm).
public sealed partial class CompanionBot
{
    public const float FireReach = 35;

    /// <summary>An armed Grunt goes for the nearest hostile it sees within <see cref="FireReach"/>; false when unarmed or none is there.</summary>
    private bool FireIfArmed(StoryRun run, Machine grunt)
    {
        if (grunt.Weapons.Count == 0 || Target(run, grunt) is not { } foe)
            return false;
        world.CompanionAttack(grunt, foe);
        return true;
    }

    /// <summary>The nearest of the ship's machines standing within <see cref="FireReach"/> in a clear line (id order on a tie); null when none.</summary>
    private Machine? Target(StoryRun run, Machine grunt)
    {
        var mobility = GameWorld.MobilityOf(grunt);
        Machine? best = null;
        var bestDistance = float.MaxValue;
        foreach (var machine in world.Machines.OrderBy(m => m.Id))
        {
            if (machine.IsDead || machine.Inside is not null || !world.AreEnemies(grunt.Race, machine.Race) || world.StoryInert(machine)
                || world.StorySeekerOf(machine.Id) is { Unkillable: true })
                continue;
            var distance = Vector2.Distance(machine.Position, grunt.Position);
            if (distance > FireReach || distance >= bestDistance || !world.Nav.LineOfSight(grunt.Position, machine.Position, mobility))
                continue;
            (best, bestDistance) = (machine, distance);
        }
        return best;
    }
}
