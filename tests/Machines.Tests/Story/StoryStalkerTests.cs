using System.Numerics;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

// Section 2's Thresher as his eye has it (design-stealth §3.3, §13 Tests: eye height, close rise,
// shadow, camouflage, healing after 25 s) and as the view reads and hears him (StoryStalker: his state, IM LICHT, his
// plants through the deck's walls and grates, the view's shake).
public class StoryStalkerTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Thresher(GameWorld world) => world.Story!.Director.Of(world, Prologue.ThresherRole).Single();

    private static void Place(Machine machine, Vector2 at, float facing = 0)
    {
        machine.Position = at;
        machine.Facing = facing;
        machine.Path.Clear();
        machine.PathIndex = 0;
    }

    /// <summary>Him standing at <paramref name="at"/> with his beam along <paramref name="heading"/> (radians), and the hero's signature as given.</summary>
    private static (Machine Him, Machine Hero) Pose(GameWorld world, Vector2 at, float heading, Vector2 hero, float signature, bool lamp = false)
    {
        var him = Thresher(world);
        Place(him, at);
        him.Facing = heading - (world.GunHeading(him) - him.Facing);
        var locator = world.HeroOf(world.Story!.Locator)!;
        Place(locator, hero);
        world.SetStoryLamp(locator.Id, lamp);
        world.Story.Sight.Signatures[locator.Id] = signature;
        return (him, locator);
    }

    // ---- his eye -----------------------------------------------------------------------------------------

    [Fact]
    public void His_eye_sees_over_crates_and_grates_but_not_past_a_high_stack_or_a_wall()
    {
        var world = Chapter(1);
        var deck = world.Deck!;
        var him = Thresher(world);
        var hero = world.HeroOf(world.Story!.Locator)!;
        // Somewhere along a row of the deck: floor, one kind of cell for 1 to 4 m, floor again.
        (Vector2 From, Vector2 To)? Across(DeckCell kind)
        {
            for (var y = 0; y < deck.Depth; y++)
            {
                for (var x = 3; x < deck.Width - 10; x++)
                {
                    if (deck.Cell(x, y) != kind || deck.Cell(x - 1, y) != DeckCell.Floor || deck.Cell(x - 2, y) != DeckCell.Floor || deck.Cell(x - 3, y) != DeckCell.Floor)
                        continue;
                    var end = x;
                    while (end < x + 5 && deck.Cell(end, y) == kind)
                        end++;
                    if (end - x > 4 || deck.Cell(end, y) != DeckCell.Floor || deck.Cell(end + 1, y) != DeckCell.Floor || deck.Cell(end + 2, y) != DeckCell.Floor)
                        continue;
                    return (new Vector2(x - 2.5f, y + 0.5f), new Vector2(end + 2.5f, y + 0.5f));
                }
            }
            return null;
        }
        foreach (var (kind, clear) in new[] { (DeckCell.High, false), (DeckCell.Cover, true), (DeckCell.Grate, true) })
        {
            if (Across(kind) is not { } line)
            {
                output.WriteLine($"no {kind} between floor on the deck");
                Assert.NotEqual(DeckCell.High, kind);
                continue;
            }
            Place(him, line.From);
            Place(hero, line.To);
            output.WriteLine($"{kind}: {line.From} to {line.To}");
            Assert.Equal(clear, world.StoryEyeClear(him, hero));
        }
    }

    [Fact]
    public void In_his_beam_close_up_suspicion_rises_three_a_second_and_further_off_one_and_a_half()
    {
        var world = Chapter(1);
        var (him, hero) = Pose(world, new Vector2(150, 52), 0, new Vector2(165, 52), 0.05f, lamp: true);
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(him, hero));
        Assert.Equal(StorySight.CloseRise, 3);
        Assert.True(StorySight.CloseBeam >= 15);
        Assert.Equal(1.5f, StorySight.RiseSeen);
    }

    [Fact]
    public void Still_and_dark_in_a_shadow_a_hero_is_lit_only_within_ten_metres()
    {
        var world = Chapter(1);
        var shadow = world.Deck!.Zones.First(z => z.Name == "A1");
        var spot = (shadow.Min + shadow.Max) / 2;
        // He stands 20 m off, his beam straight at it: still and dark it stays unseen; moving, or outside the shade, it is seen.
        foreach (var heading in new[] { 0f, MathF.PI / 2, MathF.PI, -MathF.PI / 2 })
        {
            var from = spot - 20 * new Vector2(MathF.Cos(heading), MathF.Sin(heading));
            var (him, hero) = Pose(world, from, heading, spot, StorySight.ShadowStill);
            if (world.Deck.Cell((int)from.X, (int)from.Y) is not DeckCell.Floor || !world.StoryEyeClear(him, hero) || !world.StoryLampOn(him.Id))
                continue;
            output.WriteLine($"from {from} at {heading:0.00}");
            Assert.Equal(StoryGlimpse.None, world.StoryPerceives(him, hero));
            world.Story!.Sight.Signatures[hero.Id] = 0.3f;
            Assert.NotEqual(StoryGlimpse.None, world.StoryPerceives(him, hero));
            return;
        }
        output.WriteLine("no clear line of 20 m into A1: nothing to check");
    }

    [Fact]
    public void A_grunt_still_and_dark_in_a_clamp_row_is_taken_for_one_of_the_rows_and_the_locator_never()
    {
        var world = Chapter(1);
        var run = world.Story!;
        var row = world.Deck!.Zones.First(z => z.Name == "rowA_s");
        var ike = world.HeroOf(run.Seats[1])!;
        Place(ike, (row.Min + row.Max) / 2);
        world.SetStoryLamp(ike.Id, false);
        run.Sight.Signatures[ike.Id] = StorySight.Still;
        Assert.True(world.StoryCamouflaged(ike));
        world.SetStoryLamp(ike.Id, true);
        Assert.False(world.StoryCamouflaged(ike));
        world.SetStoryLamp(ike.Id, false);
        run.Sight.Signatures[ike.Id] = 0.5f;
        Assert.False(world.StoryCamouflaged(ike));
        var locator = world.HeroOf(run.Locator)!;
        Place(locator, (row.Min + row.Max) / 2 + new Vector2(4, 0));
        run.Sight.Signatures[locator.Id] = StorySight.Still;
        world.SetStoryLamp(locator.Id, false);
        Assert.False(world.StoryCamouflaged(locator));
    }

    [Fact]
    public void What_he_did_to_a_hero_heals_once_he_left_it_alone_for_twenty_five_seconds()
    {
        var world = Chapter(1);
        var run = world.Story!;
        var hero = world.HeroOf(run.Locator)!;
        var full = hero.HitPoints;
        run.Stealth.HealTo[hero.Id] = full;
        run.Stealth.ThreatAt[hero.Id] = world.Time;
        hero.HitPoints = full / 2;
        void Blind(float seconds)
        {
            for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            {
                foreach (var seat in run.Seats)
                    run.Stealth.Unseen.Add(seat.Value);
                world.Step();
            }
        }
        Blind(StorySight.HealAfter - 1);
        Assert.True(hero.HitPoints < full, "healed before 25 s");
        Blind(2);
        Assert.Equal(full, hero.HitPoints);
    }

    // ---- the view's reading of him ------------------------------------------------------------------------

    [Fact]
    public void The_view_reads_him_calm_on_his_round_and_not_at_all_once_section_twos_rule_is_off()
    {
        var world = Chapter(1);
        for (var i = 0; i < 10 * GameWorld.TicksPerSecond; i++)
        {
            foreach (var seat in world.Story!.Seats)
                world.Story.Stealth.Unseen.Add(seat.Value);
            world.Step();
        }
        var look = StoryStalker.Look(world);
        Assert.NotNull(look);
        Assert.Equal(Thresher(world).Id, look.Value.Id);
        Assert.Contains(look.Value.State, new[] { StalkerState.Calm, StalkerState.Dwell });
        Assert.Equal(world.GunHeading(Thresher(world)), look.Value.Heading);
        world.Story!.Flags.Remove(StoryMarks.Stealth);
        Assert.Null(StoryStalker.Look(world));
    }

    [Fact]
    public void A_lure_makes_him_wary_and_a_hunt_he_lost_reads_lost()
    {
        var world = Chapter(1);
        var run = world.Story!;
        var him = Thresher(world);
        world.StoryLure(him.Position + new Vector2(10, 0), 5, "test");
        world.Step();
        Assert.IsType<SearchOrder>(him.Order);
        Assert.Equal(StalkerState.Wary, StoryStalker.StateOf(world, him));
        var eyes = world.StorySeekerOf(him.Id)!;
        eyes.Quarry = world.HeroOf(run.Locator)!.Id;
        eyes.SeenAt = world.Time;
        Assert.Equal(StalkerState.Lost, StoryStalker.StateOf(world, him));
        Assert.Equal("lost", StoryStalker.Word(StalkerState.Lost));
    }

    [Fact]
    public void A_hero_stands_in_his_light_only_inside_the_beam_within_forty_metres_with_his_eye_clear()
    {
        var world = Chapter(1);
        var (him, hero) = Pose(world, new Vector2(150, 52), 0, new Vector2(180, 52), 0.05f);
        Assert.True(world.StoryLampOn(him.Id));
        Assert.True(StoryStalker.InLight(world, him, hero));
        Place(hero, new Vector2(195, 52));
        Assert.False(StoryStalker.InLight(world, him, hero));
        Place(hero, new Vector2(150 + 20 * MathF.Cos(0.7f), 52 + 20 * MathF.Sin(0.7f)));
        Assert.False(StoryStalker.InLight(world, him, hero));
    }

    [Fact]
    public void His_plants_are_quieter_and_dull_through_walls_less_so_through_grates_and_the_near_samples_fade_with_distance()
    {
        var open = StoryStalker.Mix(20, 0, 0);
        Assert.Equal(0, open.Db);
        Assert.Equal(StoryStalker.OpenLowPass, open.LowPassHz);
        Assert.Equal(1, open.Near);
        Assert.True(open.Sub && open.Rattle);
        var wall = StoryStalker.Mix(20, 1, 0);
        Assert.Equal(-6, wall.Db);
        Assert.Equal(800, wall.LowPassHz);
        Assert.Equal(-10, StoryStalker.Mix(20, 3, 0).Db);
        Assert.Equal(-14, StoryStalker.Mix(20, 9, 0).Db);
        Assert.Equal(-2, StoryStalker.Mix(20, 0, 1).Db);
        Assert.Equal(0.5f, StoryStalker.Mix(60, 0, 0).Near, 3);
        Assert.Equal(0, StoryStalker.Mix(80, 0, 0).Near);
        Assert.False(StoryStalker.Mix(130, 0, 0).Sub);
        Assert.False(StoryStalker.Mix(31, 0, 0).Rattle);
        // The view's shake: (1 - d/40)², nothing from 30 m on.
        Assert.Equal(1, StoryStalker.ShakeStrength(0));
        Assert.Equal(0.25f, StoryStalker.ShakeStrength(20), 3);
        Assert.Equal(0, StoryStalker.ShakeStrength(30));
    }

    [Fact]
    public void Between_deck_a_and_the_duct_a_plant_is_heard_through_a_wall_and_along_the_lane_through_none()
    {
        var world = Chapter(1);
        var deck = world.Deck!;
        // Along deck A's lane: open.
        Assert.Equal((0, 0), StoryStalker.Occlusion(deck, new Vector2(140, 52), new Vector2(200, 52)));
        // From the lane down into the duct V1aw: at least one wall or stack.
        var (walls, grates) = StoryStalker.Occlusion(deck, new Vector2(180, 52), new Vector2(180, 92));
        output.WriteLine($"lane to V1aw: walls {walls}, grates {grates}");
        Assert.True(walls >= 1);
    }
}
