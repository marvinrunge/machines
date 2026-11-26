using Machines.Sim.Story;
using Machines.Sim.World;
using static Machines.Tests.Story.EscapeWorlds;

namespace Machines.Tests.Story;

/// <summary>
/// The gas main in the pod bay made seen (play test 2026-11-25: "Gab kein Abschuss der Gasleitung, zumindest als L-7 nicht
/// sichtbar"): once the pack is at Y Ike calls it out and marks it; if the Locator does not shoot it Ike goes for it
/// himself, and if that fails too it gives by itself, so its fire always meets the pack. Then the marker goes back to the pod.
/// </summary>
public class PrologueGasTests
{
    [Fact]
    public void Ike_calls_the_gas_main_then_shoots_it_himself_and_it_never_stays_whole()
    {
        var world = Chapter(14);
        var run = world.Story!;
        // The pack comes to Y only once the pod's hack is a quarter done (blast_collapse), the hatch left shut; first it waits
        // at the collapse (StoryPackTests).
        Run(world, 5);
        Assert.True(HackBySolution(world, Hero(world, 0), "pod_3_hatch"));
        CompanionTask? ikeTask = null;
        var done = RunUntil(world, () => run.Director.Has("gas_out"), 150, each: () =>
        {
            if (run.Director.Has("ike_gas") && ikeTask is null)
                ikeTask = run.Crew.Of(run.Seats[1]).Task;
        });
        Assert.True(done, $"gas_out never came; fired: {string.Join(",", run.Director.Fired.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key}@{kv.Value:0}"))}");
        Assert.True(run.Director.Has("gas_call"));
        Assert.True(run.Director.Has("ike_gas"));
        Assert.Equal(CompanionTask.Shoot, ikeTask);
        Assert.False(world.GasMainStands);
        // Ike's call comes once the pack is at Y and L-7 has called the hold, and the marker leaves the pipe once it is gone.
        Assert.True(run.Director.Fired["gas_call"] >= run.Director.Fired["ike_again"]);
        Assert.DoesNotContain(run.Flags, f => f.StartsWith("objective:Shoot the gas main", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gas_main_the_locator_shot_first_needs_neither_ike_nor_the_fallback()
    {
        var world = Chapter(14);
        var run = world.Story!;
        // The pack comes to Y only once the pod's hack is a quarter done (blast_collapse), the hatch left shut; first it waits
        // at the collapse (StoryPackTests).
        Run(world, 5);
        Assert.True(HackBySolution(world, Hero(world, 0), "pod_3_hatch"));
        Assert.True(RunUntil(world, () => run.Director.Has("gas_call"), 150), "gas_call never came");
        world.BreakGasMain();
        Run(world, 20);
        Assert.True(run.Director.Has("gas_out"));
        Assert.False(run.Director.Has("ike_gas"));
        Assert.False(run.Director.Has("gas_gives"));
    }
}
