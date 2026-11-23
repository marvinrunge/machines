using System.Numerics;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using static Machines.Tests.Story.EscapeWorlds;

namespace Machines.Tests.Story;

/// <summary>
/// The escape's hazards (package E, design-escape §3, §5): the hold's containers on their rails never pin a hero, a chock stops
/// its lane, the jets always warn before they burn, the shutter is held in its frame and slams behind the trio, the Notgang
/// never caves in on a hero or cuts one off, the bridge falls under nobody of the trio and holds the Thresher at its edge, and
/// the hazard budget and floor hold however many hazards strike at once.
/// </summary>
public class StoryHazardTests
{
    private static StoryRail Lane(StoryRun run, int lane) => run.Hazards.Rails.Single(r => r.Lane == lane && !r.IsRack);

    [Fact]
    public void A_container_never_pins_or_overlaps_a_hero_standing_in_its_lane()
    {
        var world = Chapter(12);
        var run = world.Story!;
        var locator = Hero(world, 0);
        var rail = Lane(run, 1);
        // The first roll lets lane 1 go south; the Locator stands in the lane 4 m south of the container, held there.
        var spot = new Vector2((rail.X0 + rail.X1) / 2000f, (rail.Centre - rail.HalfLength) / 1000f - locator.Radius - 4);
        var start = rail.Centre;
        world.StartRolls(run);
        var events = new List<GameEvent>();
        var closest = float.MaxValue;
        Run(world, 8, events, () =>
        {
            locator.Position = spot;
            closest = MathF.Min(closest, GameWorld.RailApart(rail, locator.Position) - locator.Radius);
        });

        Assert.Contains(Cues(events), c => c == $"rail slide {rail.Name}");
        Assert.True(rail.Centre < start, "the container did not move");
        Assert.True(closest >= 0, $"the container came {-closest} m into the Locator");
        Assert.Equal(RailState.Rest, rail.State);
        Assert.False(locator.IsDead);
    }

    [Fact]
    public void A_chocked_lane_stays_put_for_two_rolls()
    {
        var world = Chapter(12);
        var run = world.Story!;
        var rail = Lane(run, 1);
        var chock = world.Artefacts.Single(a => a.Tag == 337 && !a.IsDead);
        world.PushChock(run, chock, 1);
        var start = rail.Centre;
        world.StartRolls(run);
        Run(world, 2 * StoryHazards.Cycle / (float)GameWorld.TicksPerSecond - 1);
        Assert.Equal(start, rail.Centre);
        Assert.DoesNotContain(chock.Id, world.Artefacts.Select(a => a.Id));
    }

    [Fact]
    public void Every_jet_flickers_before_it_burns()
    {
        var world = Chapter(13);
        var hazards = world.Story!.Hazards;
        // The trio at the gallery's entry: J1's show begins (the hush, its long warning).
        Run(world, 1);
        Assert.True(hazards.JetsFrom >= 0);
        Assert.True(hazards.J1From >= 0);
        for (var jet = 1; jet <= 5; jet++)
        {
            var burns = 0;
            for (var tick = hazards.JetsFrom; tick < world.Tick + 4 * StoryHazards.Cycle; tick++)
            {
                if (hazards.Phase(jet, tick) != StoryHazards.JetPhase.Fire || hazards.Phase(jet, tick - 1) == StoryHazards.JetPhase.Fire)
                    continue;
                burns++;
                // At least a second of flicker before each burn (design-escape §5.3: 1 s, J1 2.5 s).
                for (var before = 1; before <= 20; before++)
                    Assert.Equal(StoryHazards.JetPhase.Flicker, hazards.Phase(jet, tick - before));
            }
            Assert.True(burns > 0, $"jet {jet} never burns");
        }
    }

    [Fact]
    public void The_shutter_is_held_at_half_while_a_hero_stands_in_its_frame_and_slams_once_the_trio_is_through()
    {
        var world = Chapter(12);
        var run = world.Story!;
        var frame = world.Deck!.Zone("x_frame")!;
        var middle = (frame.Min + frame.Max) / 2;
        // The trio in the hold, Bob in the frame: the shutter comes down to half and stays there.
        SetDown(world, Hero(world, 0), new Vector2(372, 170));
        SetDown(world, Hero(world, 1), new Vector2(376, 170));
        world.CloseShutter(run);
        Run(world, 15, each: () => Hero(world, 2).Position = middle);
        Assert.Equal(ShutterState.Closing, run.Hazards.Shutter);
        Assert.InRange(run.Hazards.ShutterDown, StoryHazards.ShutterLower / 2, StoryHazards.ShutterLower / 2 + 10);
        Assert.False(world.DoorClosed('X'));

        // All three through into the gallery: it slams within its 2 s.
        SetDown(world, Hero(world, 0), new Vector2(340, 130));
        SetDown(world, Hero(world, 1), new Vector2(340, 124));
        SetDown(world, Hero(world, 2), new Vector2(340, 118));
        var events = new List<GameEvent>();
        Assert.True(RunUntil(world, () => world.DoorClosed('X'), 2.5f, events));
        Assert.Equal(ShutterState.Shut, run.Hazards.Shutter);
        Assert.Contains("shutter slam", Cues(events));
    }

    [Fact]
    public void The_notgang_never_caves_in_with_a_hero_in_it_or_beyond_it()
    {
        var world = Chapter(12);
        var run = world.Story!;
        var locator = Hero(world, 0);
        var zone = world.Deck!.Zone("collapse_notgang")!;
        var inside = (zone.Min + zone.Max) / 2;

        SetDown(world, locator, inside);
        Assert.False(world.Collapse(run));
        locator.Position = new Vector2(520, 124);
        Assert.False(world.Collapse(run));
        Assert.False(run.Hazards.Collapsed);
        Assert.True(world.Nav.IsFree(inside, GameWorld.MobilityOf(locator)));

        // The trio behind it (in the hold): it falls, its ground wall, its fire face burning.
        SetDown(world, locator, new Vector2(470, 170));
        Assert.True(world.Collapse(run));
        Assert.True(run.Hazards.Collapsed);
        Assert.False(world.Nav.IsFree(inside, GameWorld.MobilityOf(locator)));
        world.Reopen(run);
        Assert.True(world.Nav.IsFree(inside, GameWorld.MobilityOf(locator)));
    }

    [Fact]
    public void The_bridge_falls_under_nobody_of_the_trio_and_holds_the_thresher_at_its_edge()
    {
        var world = Chapter(13);
        var run = world.Story!;
        var bridge = world.Deck!.Zone("R19")!;
        var thresher = run.Director.Of(world, Prologue.ThresherRole).Single();
        var reaper = run.Director.Of(world, StoryPack.R2c).Single();
        // The Locator still on the bridge: it creaks but stands.
        SetDown(world, Hero(world, 0), new Vector2(400, 92));
        SetDown(world, Hero(world, 1), new Vector2(452, 92));
        SetDown(world, Hero(world, 2), new Vector2(456, 98));
        world.LetBridgeFall(run);
        Run(world, 4, each: () =>
        {
            thresher.Position = new Vector2(370, 92);
            reaper.Position = new Vector2(380, 92);
            Hero(world, 0).Position = new Vector2(400, 92);
        });
        Assert.False(run.Hazards.BridgeFallen);

        // Off it: it falls; the Reaper on it goes down with it, the Thresher stays at the edge, the bridge's ground gone.
        SetDown(world, Hero(world, 0), new Vector2(448, 92));
        Assert.True(RunUntil(world, () => run.Hazards.BridgeFallen, 1));
        Assert.True(reaper.IsDead);
        Assert.False(thresher.IsDead);
        Assert.Equal(world.Deck.Marker("thresher_edge").Position, thresher.Position);
        Assert.False(world.Nav.IsFree((bridge.Min + bridge.Max) / 2, GameWorld.MobilityOf(Hero(world, 0))));
        // He never gets across.
        Run(world, 15, each: () => Assert.True(thresher.Position.X < bridge.Min.X + 2, $"the Thresher at {thresher.Position}"));
    }

    [Fact]
    public void The_budget_and_the_floor_hold_under_four_hazards_at_once()
    {
        // The Bucht, the Locator by the pods: the pack waits blind behind the collapse, nobody aims at him.
        var world = Chapter(14);
        var run = world.Story!;
        var locator = Hero(world, 0);
        SetDown(world, locator, new Vector2(560, 104));
        var max = locator.MaxHitPoints;
        var hits = new List<(long Tick, int Harm)>();
        // Four hazards strike the Locator every half second for 30 s (a jet, a container's brush, a plate, the fire face).
        for (var i = 0; i < 30 * GameWorld.TicksPerSecond; i++)
        {
            var before = locator.HitPoints;
            locator.Position = new Vector2(560, 104);
            if (i % 10 == 0)
            {
                for (var k = 0; k < 4; k++)
                    world.HazardHit(run, locator, 0.15f);
            }
            world.Step();
            if (locator.HitPoints < before)
                hits.Add((world.Tick, before - locator.HitPoints));
            Assert.True(locator.HitPoints >= MathF.Ceiling(StoryBudget.Floor * max) - 1, $"below the floor at {locator.HitPoints}/{max}");
        }
        // Never more than the window's share in any 10 s, never a hit of more than one event's share.
        foreach (var (tick, _) in hits)
        {
            var window = hits.Where(h => h.Tick >= tick && h.Tick < tick + StoryBudget.Window).Sum(h => h.Harm);
            Assert.True(window <= MathF.Ceiling(StoryBudget.WindowShare(StoryDifficulty.Normal) * max) + 1, $"{window} of {max} in 10 s");
        }
        Assert.All(hits, h => Assert.True(h.Harm <= MathF.Ceiling(StoryBudget.EventShare * max) * 4, $"{h.Harm}"));
        Assert.DoesNotContain(StoryMarks.L7Down, run.Flags);
        Assert.True(run.Budget.HeldBack > 0);
    }
}
