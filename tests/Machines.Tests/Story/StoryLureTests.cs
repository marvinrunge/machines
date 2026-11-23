using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

// Section 2's lures (design-stealth §3.5, §13 Tests): a Grunt's Use at a noise source of the deck sends him to search there;
// a valve is quiet where it is struck and loud at its pipe's far end; the third bang within 90 s draws his fire and spends
// the source; and two worlds that lure alike keep the same checksum.
public class StoryLureTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Thresher(GameWorld world) => world.Story!.Director.Of(world, Prologue.ThresherRole).Single();

    private static void Blind(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && until?.Invoke() != true; i++)
        {
            foreach (var seat in world.Story!.Seats)
                world.Story.Stealth.Unseen.Add(seat.Value);
            world.Step();
        }
    }

    private static void Place(Machine hero, Vector2 at)
    {
        hero.Position = at;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    /// <summary>His guard before H at checkpoint 5, once he stands at it.</summary>
    private static (GameWorld World, Machine Thresher) AtGuard()
    {
        var world = Chapter(5);
        var thresher = Thresher(world);
        var post = StoryMarks.Locate(world.Deck, "guard_h")!.Value;
        Blind(world, 60, () => Vector2.Distance(thresher.Position, post) < 1.5f && thresher.Order is not MoveOrder);
        return (world, thresher);
    }

    [Fact]
    public void A_grunts_use_at_a_noise_source_sends_him_to_search_there()
    {
        // Bob at the noise source in the E-Nische's corner (N0), he on his round of deck A: Bob's Use bangs it, and he goes to look.
        var world = Chapter(1);
        var run = world.Story!;
        Blind(world, 30);
        var thresher = Thresher(world);
        var source = world.Deck!.Markers.First(m => m.Name == "noise_n0");
        var bob = world.HeroOf(run.Seats[2])!;
        Place(bob, source.Position + new Vector2(-2, 0));
        Blind(world, 0.2f);
        Assert.Contains(source.Name, world.StoryUseTarget(run.Seats[2]) ?? "");
        world.Issue(new StoryUseCommand(Race.Red, source.Name) { Issuer = run.Seats[2] });
        Blind(world, 1f / GameWorld.TicksPerSecond);
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.StoryCue && e.Detail == $"source bang {source.Name}");
        Assert.Single(run.Stealth.Bangs[source.Name]);
        var searched = false;
        Blind(world, 3, () => searched = thresher.Order is SearchOrder);
        output.WriteLine($"he at {thresher.Position} ({Vector2.Distance(thresher.Position, source.Position):0} m from N0), {thresher.Order.GetType().Name}");
        Assert.True(searched, "he did not go to look");
        var spot = world.StorySeekerOf(thresher.Id)!.Spot;
        Assert.True(Vector2.Distance(spot, source.Position) < 1, $"he looks at {spot}, not at N0 {source.Position}");
    }

    [Fact]
    public void A_locator_cannot_bang_a_source_only_a_grunt()
    {
        var world = Chapter(1);
        var run = world.Story!;
        var source = world.Deck!.Markers.First(m => m.Name == "noise_n0");
        Place(world.HeroOf(run.Locator)!, source.Position + new Vector2(-2, 0));
        world.Issue(new StoryUseCommand(Race.Red, source.Name) { Issuer = run.Locator });
        world.Step();
        Assert.False(run.Stealth.Bangs.ContainsKey(source.Name));
    }

    [Fact]
    public void A_valve_is_quiet_where_it_is_struck_and_loud_at_its_pipes_far_end()
    {
        // At his guard the valve by the Nordgang (valve_o, 30 m off) is too quiet for him; far_n1 draws him there.
        var (world, thresher) = AtGuard();
        var run = world.Story!;
        var bob = world.HeroOf(run.Seats[2])!;
        var valve = world.Deck!.Markers.First(m => m.Name == "valve_o");
        Place(bob, valve.Position + new Vector2(0, -2));
        Blind(world, 0.2f);
        world.Issue(new StoryUseCommand(Race.Red, "valve_o") { Issuer = run.Seats[2] });
        Blind(world, 1f / GameWorld.TicksPerSecond);
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.StoryCue && e.Detail == "valve bang valve_o far_n1");
        // The struck end: not loud enough to make him search there.
        var eyes = world.StorySeekerOf(thresher.Id)!;
        Blind(world, StorySight.PipeDelay - 0.15f);
        Assert.IsNotType<SearchOrder>(thresher.Order);
        Blind(world, 1.5f, () => thresher.Order is SearchOrder);
        var far = StoryMarks.Locate(world.Deck, "far_n1")!.Value;
        Assert.IsType<SearchOrder>(thresher.Order);
        Assert.True(Vector2.Distance(eyes.Spot, far) < 1, $"he looks at {eyes.Spot}, not at far_n1 {far}");
        Assert.True(Vector2.Distance(eyes.Spot, valve.Position) > 30);
    }

    [Fact]
    public void The_third_bang_within_ninety_seconds_draws_his_fire_and_spends_the_valve()
    {
        var (world, _) = AtGuard();
        var run = world.Story!;
        var ike = world.HeroOf(run.Seats[1])!;
        var valve = world.Deck!.Markers.First(m => m.Name == "valve_w");
        var cues = new List<string>();
        for (var bang = 0; bang < 3; bang++)
        {
            Place(ike, valve.Position + new Vector2(0, -2));
            Blind(world, 0.2f);
            world.Issue(new StoryUseCommand(Race.Red, "valve_w") { Issuer = run.Seats[1] });
            Blind(world, 1f / GameWorld.TicksPerSecond);
            cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
            if (bang < 2)
            {
                Assert.DoesNotContain("valve_w", run.Stealth.Spent);
                Blind(world, 25);
            }
        }
        Assert.Contains("valve_w", run.Stealth.Spent);
        Assert.Contains(cues, c => c.StartsWith("lure volley valve_w", StringComparison.Ordinal));
        Assert.DoesNotContain(world.StoryNoiseSources(), m => m.Name == "valve_w");
        // Spent, it refuses a fourth.
        Assert.Equal("that source is spent", world.StoryBang("valve_w", ike.Id));
    }

    [Fact]
    public void Two_worlds_that_lure_alike_keep_the_same_checksum()
    {
        var sums = new List<ulong>[2];
        for (var peer = 0; peer < 2; peer++)
        {
            var (world, _) = AtGuard();
            var run = world.Story!;
            sums[peer] = [world.Checksum()];
            var bob = world.HeroOf(run.Seats[2])!;
            Place(bob, world.Deck!.Markers.First(m => m.Name == "valve_o").Position + new Vector2(0, -2));
            for (var second = 0; second < 60; second++)
            {
                if (second == 2)
                    world.Issue(new StoryUseCommand(Race.Red, "valve_o") { Issuer = run.Seats[2] });
                if (second == 30)
                    world.StoryBang("valve_w", world.HeroOf(run.Seats[1])!.Id);
                Blind(world, 1);
                sums[peer].Add(world.Checksum());
            }
        }
        Assert.Equal(sums[0], sums[1]);
    }
}
