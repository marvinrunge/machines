using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Story;

// The computer's Grunts in section 2 (design-stealth §6.3, §13 Tests): they freeze when he comes near, keep the window rule
// before a lane he could see, arrive where the director sneaks them, and a Grunt put back together in the dark says the
// dark decks' line (P_IKE_UP_DARK), not the workshop's.
public class CompanionStealthTests(ITestOutputHelper output)
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Thresher(GameWorld world) => world.Story!.Director.Of(world, Prologue.ThresherRole).Single();

    private static void Place(Machine hero, Vector2 at)
    {
        hero.Position = at;
        hero.Path.Clear();
        hero.PathIndex = 0;
    }

    /// <summary>Steps with the trio out of his sight (the grace), so he hurts nobody while the bots think.</summary>
    private static void Blind(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond && until?.Invoke() != true; i++)
        {
            foreach (var seat in world.Story!.Seats)
                world.Story.Stealth.Unseen.Add(seat.Value);
            world.Step();
        }
    }

    [Fact]
    public void A_grunt_freezes_when_he_comes_near_and_says_so()
    {
        var world = Chapter(1);
        var run = world.Story!;
        Blind(world, 2);
        var thresher = Thresher(world);
        var ike = world.HeroOf(run.Seats[1])!;
        // On deck A's open lane, 10 m from him: he would hear it walk.
        var lane = new Vector2(thresher.Position.X + (thresher.Position.X < 190 ? 10 : -10), thresher.Position.Y);
        Place(ike, lane);
        Blind(world, 1.5f);
        var bot = run.Stealth.Bot(run.Seats[1].Value);
        output.WriteLine($"thresher at {thresher.Position}, Ike at {ike.Position}, frozen at {bot.FrozenAt}");
        Assert.True(bot.FrozenAt >= 0, "Ike did not freeze 10 m from him");
        Assert.Contains(run.Seats[1].Value, run.Stealth.Creep);
        Assert.Contains(run.Barks, b => b.Line == Prologue.Lines.IkeFreeze);
        // It stands still while frozen.
        var at = ike.Position;
        Blind(world, 1);
        Assert.True(Vector2.Distance(at, ike.Position) < 0.5f || !OnLaneA(ike.Position), $"Ike moved on from {at} to {ike.Position} while frozen on the lane");
    }

    private static bool OnLaneA(Vector2 point) => point.X is >= 120 and <= 264 && point.Y is >= 44 and <= 60;

    [Fact]
    public void A_grunt_sent_across_deck_as_lane_only_steps_onto_it_when_he_is_far_or_it_waited_long()
    {
        // In the Graben by g4, sent to the E-Nische: its way leads across deck A's lane (g4 down, g5 up).
        var world = Chapter(1);
        var run = world.Story!;
        Blind(world, 1);
        var thresher = Thresher(world);
        var ike = world.HeroOf(run.Seats[1])!;
        Place(ike, StoryMarks.Locate(world.Deck, "x_g4")!.Value);
        var bot = run.Stealth.Bot(run.Seats[1].Value);
        bot.Goal = "hide_e";
        var goal = StoryMarks.Locate(world.Deck, "hide_e")!.Value;
        var held = false;
        float? enteredNear = null;
        var heldFor = 0f;
        for (var i = 0; i < 240 * GameWorld.TicksPerSecond && Vector2.Distance(ike.Position, goal) > 2.5f; i++)
        {
            var was = OnLaneA(ike.Position);
            Blind(world, 1f / GameWorld.TicksPerSecond);
            if (bot.Holding)
            {
                held = true;
                heldFor = world.Time - bot.HoldSince;
            }
            if (!was && OnLaneA(ike.Position) && enteredNear is null)
                enteredNear = Vector2.Distance(thresher.Position, ike.Position);
        }
        output.WriteLine($"held {held} ({heldFor:0.0} s), stepped onto the lane with him {enteredNear:0.0} m off, at the goal {Vector2.Distance(ike.Position, goal) <= 2.5f} at {world.Time:0.0} s");
        Assert.True(Vector2.Distance(ike.Position, goal) <= 2.5f, $"Ike did not reach hide_e (at {ike.Position})");
        Assert.NotNull(enteredNear);
        Assert.True(enteredNear >= 30 || heldFor >= 29, $"Ike stepped onto the lane with him {enteredNear:0.0} m off after holding {heldFor:0.0} s");
    }

    [Fact]
    public void The_directors_sneak_brings_both_grunts_where_they_are_sent()
    {
        // Checkpoint 2 (Schacht): Ike to the hide by the E-Nische, Bob into it, both the long way along the Graben.
        var world = Chapter(2);
        var run = world.Story!;
        var goals = new[] { "hide_e", "ENi" };
        for (var i = 0; i < 2; i++)
            run.Stealth.Bot(run.Seats[i + 1].Value).Goal = goals[i];
        bool There(int i)
        {
            var at = StoryMarks.Locate(world.Deck, goals[i])!.Value;
            var grunt = world.HeroOf(run.Seats[i + 1])!;
            return goals[i] == "ENi" ? world.Deck!.Zones.First(z => z.Name == "ENi").Contains(grunt.Position) : Vector2.Distance(grunt.Position, at) <= 3;
        }
        Blind(world, 300, () => There(0) && There(1));
        output.WriteLine($"Ike at {world.HeroOf(run.Seats[1])!.Position}, Bob at {world.HeroOf(run.Seats[2])!.Position}, {world.Time:0.0} s");
        Assert.True(There(0), "Ike did not reach hide_e");
        Assert.True(There(1), "Bob did not reach the E-Nische");
        Assert.All(run.Seats.Skip(1), seat => Assert.False(world.CompanionDown(seat)));
    }

    [Fact]
    public void A_grunt_put_back_together_in_the_dark_says_the_dark_line()
    {
        var world = Chapter(1);
        var run = world.Story!;
        Blind(world, 1);
        var ike = world.HeroOf(run.Seats[1])!;
        world.Damage(ike, 100000, Race.Blue);
        Blind(world, 0.5f);
        Assert.True(world.CompanionDown(run.Seats[1]));
        var locator = world.HeroOf(run.Locator)!;
        Place(locator, ike.Position + new Vector2(3, 0));
        world.Issue(new StoryUseCommand(Race.Red, $"seat:{run.Seats[1].Value}") { Issuer = run.Locator });
        Blind(world, 6, () => !world.CompanionDown(run.Seats[1]));
        Assert.False(world.CompanionDown(run.Seats[1]), "Ike is still down");
        Blind(world, 1);
        Assert.Contains(run.Barks, b => b.Line == Prologue.Lines.IkeUpDark);
        Assert.DoesNotContain(run.Barks, b => b.Line == "P_IKE_UP");
    }
}
