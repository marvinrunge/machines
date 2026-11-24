using System.Numerics;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

// Section 2's Thresher (plan stufe7 §4 S, design-stealth §3.6, §8, §13): his rounds take the time the design gives them,
// the guard looks back, no checkpoint starts him near the trio or looking at it, he cannot get where the trio hides, and two
// peers playing it keep the same checksum.
public class PrologueThresherTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Thresher(GameWorld world) => world.Story!.Director.Of(world, Prologue.ThresherRole).Single();

    /// <summary>Steps with the trio kept out of his sight (the offscreen grace for every seat), so only his round plays.</summary>
    private static void Blind(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && until?.Invoke() != true; i++)
        {
            foreach (var seat in world.Story!.Seats)
                world.Story.Stealth.Unseen.Add(seat.Value);
            world.Step();
        }
    }

    // The design's 100, 150 and 95 s, slowed with his round's pace (StorySight.PatrolPace 0.265 → 0.16) for his heavy gait;
    // as a crab walker he turns less at the ring's corners.
    [Theory]
    [InlineData(1, "a", 155)]
    [InlineData(2, "ring", 230)]
    [InlineData(4, "b", 141)]
    public void His_rounds_take_the_time_the_design_gives_them(int checkpoint, string round, float seconds)
    {
        var world = Chapter(checkpoint);
        var run = world.Story!;
        var thresher = Thresher(world);
        // The trio waits in the clamp hall, out of his way.
        for (var seat = 0; seat < run.Seats.Count; seat++)
        {
            var hero = world.HeroOf(run.Seats[seat])!;
            hero.Position = new Vector2(30 + 6 * seat, 74);
            hero.Path.Clear();
            hero.PathIndex = 0;
        }
        Blind(world, 400, () => run.Stealth.Laps.GetValueOrDefault(thresher.Id) >= 1);
        Assert.Equal(round, run.Stealth.RoundOf[thresher.Id]);
        // The first lap from where the checkpoint set him down differs; from the second on they are the same.
        Blind(world, 400, () => run.Stealth.Laps.GetValueOrDefault(thresher.Id) >= 2);
        var first = world.Time;
        Blind(world, 400, () => run.Stealth.Laps.GetValueOrDefault(thresher.Id) >= 3);
        var lap = world.Time - first;
        output.WriteLine($"round {round}: {lap:0.0} s (design {seconds} s)");
        Assert.InRange(lap, seconds - 5, seconds + 5);
        // Every lap the same to the tick.
        Blind(world, 400, () => run.Stealth.Laps.GetValueOrDefault(thresher.Id) >= 4);
        Assert.InRange(MathF.Abs(world.Time - first - 2 * lap), 0, 0.1f);
    }

    [Theory]
    [InlineData(160, 52)]
    [InlineData(224, 52)]
    public void A_grunt_standing_on_his_round_does_not_stop_him_for_good(float x, float y)
    {
        // A computer's Grunt hidden by the offscreen grace and standing still on his lane once held him up for ever.
        var world = Chapter(1);
        var run = world.Story!;
        var thresher = Thresher(world);
        for (var seat = 0; seat < run.Seats.Count; seat++)
        {
            var hero = world.HeroOf(run.Seats[seat])!;
            hero.Position = seat == 1 ? new Vector2(x, y) : new Vector2(30 + 6 * seat, 74);
            hero.Path.Clear();
            hero.PathIndex = 0;
            if (seat > 0)
                run.Crew.Of(run.Seats[seat]).Order = CompanionOrder.Wait;
        }
        // Frozen where it stands (as a Grunt that freezes when he comes near): it makes way for nobody.
        run.Director.Held.Add(world.HeroOf(run.Seats[1])!.Id);
        Blind(world, 300, () => run.Stealth.Laps.GetValueOrDefault(thresher.Id) >= 2);
        output.WriteLine($"laps {run.Stealth.Laps.GetValueOrDefault(thresher.Id)} at {world.Time:0} s, he at {thresher.Position}, Ike at {world.HeroOf(run.Seats[1])!.Position}");
        Assert.True(run.Stealth.Laps.GetValueOrDefault(thresher.Id) >= 2, $"he stands at {thresher.Position} ({thresher.Order})");
    }

    [Fact]
    public void At_checkpoint_four_a_locator_left_idle_in_its_duct_is_not_touched()
    {
        // Set down 3 m from V1c's mouth, an idle Locator was touched and shut down as he passed (about 58 s in). With its lamp
        // on he sees it down the duct (the window taught to switch it off); dark, it is safe there.
        var world = Chapter(4);
        var run = world.Story!;
        var locator = world.HeroOf(run.Locator)!;
        world.SetStoryLamp(locator.Id, false);
        for (var i = 0; i < 120 * GameWorld.TicksPerSecond && !run.Over; i++)
            world.Step();
        Assert.DoesNotContain(StoryMarks.L7Down, run.Flags);
        Assert.False(run.Over, $"over: {run.Ending} at {world.Time:0.0} s, L at {locator.Position} hp {locator.HitPoints} dead {locator.IsDead}, he at {Thresher(world).Position}, flags {string.Join(",", run.Flags)}, beats {string.Join(",", run.Director.Fired.OrderBy(kv => kv.Value).Select(kv => kv.Key).TakeLast(4))}");
    }

    [Fact]
    public void At_his_guard_he_looks_back_at_the_nordgang_once_in_forty_seconds()
    {
        var world = Chapter(5);
        var thresher = Thresher(world);
        // Bob bangs his valve on his own a minute after his offer, which draws him off his post: not here.
        world.Story!.Director.Fired["auto_bang"] = 0;
        var post = StoryMarks.Locate(world.Deck, "guard_h")!.Value;
        Blind(world, 60, () => Vector2.Distance(thresher.Position, post) < 1.5f && thresher.Order is not MoveOrder);
        var backs = new List<float>();
        var wasBack = false;
        for (var i = 0; i < 90 * GameWorld.TicksPerSecond; i++)
        {
            Blind(world, 1f / GameWorld.TicksPerSecond);
            // His back to the Nordgang (north, 90°) means looking south (his head's way: he walks as a crab).
            var back = MathF.Sin(world.GunHeading(thresher)) < -0.7f;
            if (back && !wasBack)
                backs.Add(world.Time);
            wasBack = back;
        }
        output.WriteLine($"at {thresher.Position} ({thresher.Order.GetType().Name}), post {post}");
        output.WriteLine($"looks back at {string.Join(", ", backs.Select(t => t.ToString("0.0")))}");
        Assert.InRange(backs.Count, 2, 3);
        Assert.InRange(backs[1] - backs[0], StorySight.GuardEvery - 1, StorySight.GuardEvery + 1);
        Assert.True(Vector2.Distance(thresher.Position, post) < 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void No_checkpoint_starts_him_near_the_trio_or_looking_at_it(int checkpoint)
    {
        var world = Chapter(checkpoint);
        var run = world.Story!;
        var thresher = Thresher(world);
        foreach (var seat in run.Seats)
        {
            var hero = world.HeroOf(seat)!;
            var to = hero.Position - thresher.Position;
            Assert.True(to.Length() >= 40, $"cp {checkpoint}: seat {seat.Value} {to.Length():0} m from him");
            var off = MathF.Abs(MathF.IEEERemainder(MathF.Atan2(to.Y, to.X) - thresher.Facing, MathF.Tau)) * 180 / MathF.PI;
            Assert.True(to.Length() > StorySight.LampSight || off > StorySight.BeamHalfAngle, $"cp {checkpoint}: seat {seat.Value} in his cone");
        }
        Assert.Contains(StoryMarks.Stealth, run.Flags);
    }

    [Theory]
    [InlineData("graben")]
    [InlineData("nordgang")]
    [InlineData("A1")]
    [InlineData("niche_l7")]
    [InlineData("ENi")]
    public void He_cannot_get_where_the_trio_hides(string place)
    {
        var world = Chapter(1);
        var thresher = Thresher(world);
        var at = StoryMarks.Locate(world.Deck, place)!.Value;
        var path = world.Nav.FindPath(thresher.Position, at, GameWorld.MobilityOf(thresher));
        var end = path.Count > 0 ? path[^1] : thresher.Position;
        Assert.True(Vector2.Distance(end, at) > 3, $"he gets within {Vector2.Distance(end, at):0.0} m of {place}");
    }

    [Fact]
    public void From_checkpoint_six_door_h_is_shut_and_locked_and_he_is_out_of_section_two()
    {
        foreach (var cp in new[] { 6, 7 })
        {
            var world = Chapter(cp);
            Assert.True(world.DoorClosed('H'));
            Assert.Contains('H', world.Story!.Director.Locked);
            Assert.DoesNotContain(StoryMarks.Stealth, world.Story.Flags);
        }
    }

    [Fact]
    public void The_armourys_grunts_keep_the_old_sight()
    {
        // Section 2's rules hang on the stealth flag and on the searchlight; the armoury's Grunts have neither.
        var world = Chapter(8);
        Assert.DoesNotContain(StoryMarks.Stealth, world.Story!.Flags);
        Assert.All(world.Story.Director.Of(world, Prologue.HostileRole), m => Assert.False(world.StorySeekerOf(m.Id)?.Searchlight ?? false));
    }

    [Fact]
    public void A_third_bang_within_ninety_seconds_makes_him_fire_into_the_source()
    {
        var world = Chapter(1);
        var run = world.Story!;
        var source = world.StoryNoiseSources().First(m => !m.Tags.Any(t => t.StartsWith("far=", StringComparison.Ordinal)));
        var grunt = world.HeroOf(run.Seats[1])!;
        Assert.Null(world.StoryBang(source.Name, grunt.Id));
        Blind(world, 20);
        Assert.Null(world.StoryBang(source.Name, grunt.Id));
        Assert.DoesNotContain(source.Name, run.Stealth.Spent);
        Blind(world, 20);
        Assert.Null(world.StoryBang(source.Name, grunt.Id));
        Assert.Contains(source.Name, run.Stealth.Spent);
        Assert.DoesNotContain(world.StoryNoiseSources(), m => m.Name == source.Name);
    }

    [Fact]
    public void A_valve_sounds_at_the_far_end_of_its_pipe()
    {
        // At his guard before H the valve by the Nordgang (40 m off, behind the row) is too quiet for him; its pipe's far end
        // at N1 (72 m, in the open) draws him off 0.8 s later.
        var world = Chapter(5);
        var run = world.Story!;
        var thresher = Thresher(world);
        var post = StoryMarks.Locate(world.Deck, "guard_h")!.Value;
        Blind(world, 60, () => Vector2.Distance(thresher.Position, post) < 1.5f && thresher.Order is not MoveOrder);
        var far = StoryMarks.Locate(world.Deck, "far_n1")!.Value;
        Assert.Null(world.StoryBang("valve_o", world.HeroOf(run.Seats[2])!.Id));
        Assert.Single(run.Stealth.Pipes);
        var cues = new List<GameEvent>();
        var searchedAt = -1f;
        var bangAt = world.Time;
        for (var i = 0; i < 2 * GameWorld.TicksPerSecond; i++)
        {
            Blind(world, 1f / GameWorld.TicksPerSecond);
            cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue && e.Detail!.StartsWith("pipe far", StringComparison.Ordinal)));
            if (searchedAt < 0 && thresher.Order is SearchOrder)
                searchedAt = world.Time;
        }
        Assert.Empty(run.Stealth.Pipes);
        Assert.Equal("pipe far far_n1", Assert.Single(cues).Detail);
        Assert.Equal(far, cues[0].Position);
        Assert.InRange(searchedAt - bangAt, StorySight.PipeDelay - 0.1f, StorySight.PipeDelay + 0.2f);
        Assert.Equal(far, world.StorySeekerOf(thresher.Id)!.Spot);
        Assert.Contains(thresher.Id, run.Stealth.Guards.Keys);
        // The window it opens for the hack (design-stealth §3.5: about 44 s): off to N1, a sweep there, back to his post.
        Blind(world, 120, () => thresher.Order is not SearchOrder && Vector2.Distance(thresher.Position, post) < 1.5f);
        var window = world.Time - bangAt;
        output.WriteLine($"away from his post {window:0.0} s");
        Assert.InRange(window, 35, 60);
    }

    [Fact]
    public void At_checkpoint_four_the_computers_grunts_wait_off_deck_bs_lane()
    {
        // The Locator stands at V1c's mouth, which opens onto deck B's north lane: its Grunts wait in S, off the lane he walks,
        // and he passes by without hurting them.
        var world = Chapter(4);
        var run = world.Story!;
        for (var i = 0; i < 50 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            foreach (var seat in run.Seats.Skip(1))
            {
                var grunt = world.HeroOf(seat)!;
                Assert.False(grunt.Position.X is >= 120 and <= 264 && grunt.Position.Y is >= 144 and <= 158, $"seat {seat.Value} on the lane at {grunt.Position}, {world.Time:0.0} s");
                Assert.Equal(grunt.MaxHitPoints, grunt.HitPoints);
            }
        }
    }

    [Fact]
    public void Two_peers_playing_section_two_keep_the_same_checksum()
    {
        var sums = new List<ulong>[2];
        for (var peer = 0; peer < 2; peer++)
        {
            var world = Chapter(1);
            sums[peer] = [];
            var source = world.StoryNoiseSources().First();
            for (var second = 0; second < 90; second++)
            {
                if (second == 30)
                    world.StoryBang(source.Name, world.HeroOf(world.Story!.Seats[2])!.Id);
                for (var i = 0; i < GameWorld.TicksPerSecond; i++)
                    world.Step();
                sums[peer].Add(world.Checksum());
            }
        }
        Assert.Equal(sums[0], sums[1]);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Each_checkpoint_of_section_two_sets_up_the_same_world_for_two_peers(int checkpoint)
    {
        var sums = new List<ulong>[2];
        for (var peer = 0; peer < 2; peer++)
        {
            var world = Chapter(checkpoint);
            sums[peer] = [world.Checksum()];
            for (var second = 0; second < 40; second++)
            {
                for (var i = 0; i < GameWorld.TicksPerSecond; i++)
                    world.Step();
                sums[peer].Add(world.Checksum());
            }
        }
        Assert.Equal(sums[0], sums[1]);
    }
}
