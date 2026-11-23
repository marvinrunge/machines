using System.Globalization;
using System.Numerics;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

// The Notabschaltung's part of a computer's Grunt (plan stufe7 C7, C11): restarting the shut-down Locator comes before
// every task and order. The other stage-7 behaviours hook in by their own files (CompanionBot.Stealth, .Armoury, .Escape).
public sealed partial class CompanionBot
{
    /// <summary>How near the Locator a Grunt stops to plug its cable in, metres from its middle (within StoryReserve.Reach of its hull).</summary>
    private const float PlugStand = 2.5f;

    /// <summary>
    /// The Locator lies shut down: unless the other Grunt already holds the cable in, this one drives to it and holds Use on
    /// it (the cable) until it is back. True while it is busy with that, so it does nothing else.
    /// </summary>
    private bool RestartLocator(StoryRun run, Machine grunt)
    {
        if (!run.Flags.Contains(StoryMarks.L7Down) || world.HeroOf(run.Locator) is not { } locator)
            return false;
        var target = GameWorld.SeatTarget + run.Locator.Value.ToString(CultureInfo.InvariantCulture);
        if (run.Seats.Skip(1).Any(s => s != seat && run.Crew.Holding.TryGetValue(s.Value, out var what) && what == target))
            return false;
        var reach = StoryReserve.Reach + grunt.Radius + locator.Radius - 0.5f;
        if (Vector2.Distance(grunt.Position, locator.Position) > reach)
        {
            world.CompanionMove(grunt, locator.Position + (Vector2.Distance(grunt.Position, locator.Position) > 0.01f
                ? Vector2.Normalize(grunt.Position - locator.Position) * (locator.Radius + grunt.Radius + PlugStand - 2) : Vector2.Zero));
            return true;
        }
        world.CompanionStand(grunt);
        run.Crew.Holding[seat.Value] = target;
        return true;
    }
}
