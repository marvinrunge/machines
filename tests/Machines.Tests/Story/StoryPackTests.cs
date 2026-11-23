using System.Globalization;
using System.Numerics;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using static Machines.Tests.Story.EscapeWorlds;

namespace Machines.Tests.Story;

/// <summary>
/// The escape's pack (package E, design-escape §4): eight out of the freight lift, on the leash behind the rearmost hero,
/// caught up quietly at a door behind the trio and never into a hero's view, round through the Notgang after the bridge, and
/// closing in on a shut-down Locator to delete it.
/// </summary>
public class StoryPackTests
{
    [Fact]
    public void The_lift_lets_out_five_reapers_two_big_grunts_and_the_thresher()
    {
        var world = Chapter(12);
        var run = world.Story!;
        Run(world, 12);
        var kinds = run.Pack.Members.Values.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, kinds[PackKind.Reaper3]);
        Assert.Equal(3, kinds[PackKind.Reaper2]);
        Assert.Equal(2, kinds[PackKind.Heavy]);
        Assert.Equal(1, kinds[PackKind.Thresher]);
        // The Commander is not in it (design-escape §14: only his cameo in Y's frame at the end).
        Assert.Empty(run.Director.Of(world, Prologue.CommanderRole));
    }

    [Fact]
    public void The_leash_slows_the_pack_near_the_rearmost_hero_and_hurries_it_from_far_behind()
    {
        Assert.Equal(0, StoryPack.LeashSpeed(PackKind.Thresher, StoryPack.ThresherKeeps - 1, 7, 13));
        Assert.True(StoryPack.LeashSpeed(PackKind.Reaper2, StoryPack.Near - 1, 7, 13.5f) < StoryPack.LeashSpeed(PackKind.Reaper2, StoryPack.Near + 1, 7, 13.5f));
        Assert.True(StoryPack.LeashSpeed(PackKind.Reaper3, StoryPack.Far + 1, 7, 13.5f) > 7);
        Assert.True(StoryPack.LeashSpeed(PackKind.Heavy, StoryPack.Near - 1, 7, 9) < 7);
    }

    [Fact]
    public void Running_the_route_the_nearest_of_the_pack_keeps_between_ten_and_110_metres_behind()
    {
        var world = Chapter(12);
        var run = world.Story!;
        var gaps = new List<float>();
        Carry(world, 330, each: along =>
        {
            if (along < 60 || world.Tick % 20 != 0)
                return;
            var rear = StoryPack.Progress(Hero(world, 2).Position);
            gaps.Add(world.PackAlive(run).Min(m => rear - StoryPack.Progress(m.Position)));
        });
        Assert.NotEmpty(gaps);
        Assert.All(gaps, gap => Assert.InRange(gap, 10, 110));
    }

    [Fact]
    public void A_straggler_is_caught_up_at_a_door_behind_the_trio_and_never_into_a_heros_view()
    {
        var world = Chapter(12);
        var run = world.Story!;
        var events = new List<GameEvent>();
        Carry(world, 420, each: _ => events.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue && e.Detail!.StartsWith("pack catch up", StringComparison.Ordinal))
            .Select(e =>
            {
                // Where it was set, and how far the nearest hero stood at that tick.
                var near = Heroes(world).Min(h => Vector2.Distance(h.Position, e.Position));
                Assert.True(near > 45, $"{e.Detail} at {e.Position}, a hero {near} m off");
                Assert.True(StoryPack.Progress(e.Position) < Heroes(world).Min(h => StoryPack.Progress(h.Position)), $"{e.Detail} ahead of the trio");
                return e;
            })));
        Assert.NotEmpty(events);
    }

    [Fact]
    public void After_the_bridge_at_most_two_reapers_and_one_big_grunt_go_round_and_wait_blind_at_the_collapse()
    {
        var world = Chapter(14);
        var run = world.Story!;
        var waiting = world.PackAlive(run).Where(m => run.Pack.Members[m.Id] != PackKind.Thresher).ToList();
        Assert.True(run.Pack.GoneRound);
        Assert.Equal(2, waiting.Count(m => run.Pack.Members[m.Id] is PackKind.Reaper2 or PackKind.Reaper3));
        Assert.Equal(1, waiting.Count(m => run.Pack.Members[m.Id] == PackKind.Heavy));
        Assert.All(waiting, m => Assert.Contains(m.Id, run.Director.Held));
        Run(world, 5);
        Assert.All(waiting, m => Assert.True(m.Position.Y > 150, $"{m.Id} at {m.Position}"));
        Assert.True(world.DoorClosed('Y'));
    }

    [Fact]
    public void A_shut_down_locator_the_pack_reaches_is_deleted()
    {
        var world = Chapter(13);
        var run = world.Story!;
        var locator = Hero(world, 0);
        var reaper = run.Director.Of(world, StoryPack.R3a).Single();
        run.Flags.Add(StoryMarks.L7Down);
        var events = new List<GameEvent>();
        Assert.True(RunUntil(world, () => run.Over, 3, events, () =>
        {
            SetDown(world, locator, new Vector2(372, 196));
            reaper.Position = locator.Position + new Vector2(5, 0);
        }));
        Assert.Equal(StoryEnding.LocatorLost, run.Ending);
        Assert.Contains("l7 lost pack", Cues(events));
    }

    [Fact]
    public void The_music_hears_how_near_the_thresher_is_and_what_he_does()
    {
        var world = Chapter(12);
        var events = new List<GameEvent>();
        Carry(world, 120, each: _ => events.AddRange(world.Events));
        var cues = Cues(events).ToList();
        Assert.Contains(cues, c => c.StartsWith("thresher near ", StringComparison.Ordinal)
            && int.TryParse(c["thresher near ".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
        Assert.Contains(cues, c => c.StartsWith("thresher state ", StringComparison.Ordinal));
        Assert.Contains(cues, c => c.StartsWith("score escape ", StringComparison.Ordinal));
    }
}
