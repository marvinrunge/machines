using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Navigation;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The ways over the prologue's deck (stage 5): a machine sent to a spot behind a wall never comes out on the other side of
/// it (stage 4's finding 20, a Reaper set down in the cable duct by the end of its run), and the ways the trio has to take
/// stay open whatever falls or lies about.
/// </summary>
public class StoryWayTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld Chapter(int checkpoint, int seed = 3) =>
        MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static Machine Hero(GameWorld world, int seat) => world.HeroOf(world.Story!.Seats[seat])!;

    private static bool Inside(Vector2 at, float x0, float y0, float x1, float y1) => at.X >= x0 && at.X <= x1 && at.Y >= y0 && at.Y <= y1;

    // ---- finding 20: through the wall -------------------------------------------------------------------

    /// <summary>
    /// The Reaper of the finding: it stands in Parallelgang 2 at the wall of the cable duct, the spot it wants is in the duct
    /// behind that wall. The nearest cell it can reach is its own, and the old search took the wall and the duct's low cells
    /// for the goal's own blocked ground (a building one drives up to) and handed back the spot itself, across the wall.
    /// </summary>
    [Theory]
    [InlineData(357f, 249f, 357.6f, 236f)]
    [InlineData(357f, 251f, 357.6f, 237f)]
    [InlineData(277f, 251f, 277f, 238f)]
    public void A_way_from_the_parallel_corridor_never_ends_in_the_cable_duct_behind_its_wall(float x, float y, float toX, float toY)
    {
        var world = Chapter(7);
        var reaper = world.AddMachine(Race.Blue, new Vector2(x, y), "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        var mobility = GameWorld.MobilityOf(reaper);
        Assert.Equal(2, mobility.Size);

        var path = world.Nav.FindPath(reaper.Position, new Vector2(toX, toY), mobility);

        Assert.DoesNotContain(path, p => Inside(p, 228, 232, 380, 241.98f));
    }

    /// <summary>
    /// The same in the world, on the prologue's deck with nobody else about: a Reaper sent onto the spot in the duct as the
    /// world sends a machine (its way from the grid's search, no search's clamp in between) drives up to the wall and stays on
    /// its side. Before, the end of its run set it down in the duct in one tick.
    /// </summary>
    [Theory]
    [InlineData(357f, 249f, 357.6f, 236f)]
    [InlineData(277f, 251f, 277f, 238f)]
    public void A_reaper_sent_into_the_cable_duct_from_the_corridor_beside_it_stays_on_its_side_of_the_wall(float x, float y, float toX, float toY)
    {
        var world = new GameWorld(DataLoader.Value, DataLoader.Value.LoadPlanet(StoryOptions.Prologue), 1);
        var reaper = world.AddMachine(Race.Blue, new Vector2(x, y), "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        var spot = new Vector2(toX, toY);
        var jumps = new List<string>();
        var inside = new List<string>();
        var last = reaper.Position;
        for (var tick = 0; tick < 15 * GameWorld.TicksPerSecond; tick++)
        {
            if (tick % (3 * GameWorld.TicksPerSecond) == 0)
            {
                reaper.Order = new MoveOrder(spot);
                reaper.Path.Clear();
                reaper.PathIndex = 0;
                reaper.Path.AddRange(world.Nav.FindPath(reaper.Position, spot, GameWorld.MobilityOf(reaper)));
            }
            world.Step();
            if (Vector2.Distance(reaper.Position, last) > 1)
                jumps.Add($"{last} -> {reaper.Position} t={world.Time:0.00}");
            if (Inside(reaper.Position, 228, 232, 380, 241.98f))
                inside.Add($"{reaper.Position} t={world.Time:0.00}");
            last = reaper.Position;
        }

        Assert.Empty(jumps);
        Assert.Empty(inside);
    }

    /// <summary>
    /// A Reaper at the cable duct's door that sees a Grunt far down the duct, beyond its fire reach and where it cannot go,
    /// does not stand at the door aiming without a shot for as long as the Grunt stays (stage 4): it goes to look from the
    /// nearest place it can get to, along the corridor beside the duct, and never fires from beyond its reach.
    /// </summary>
    [Fact]
    public void A_reaper_that_sees_a_grunt_far_down_the_cable_duct_does_not_stand_aiming_at_the_door()
    {
        var world = Chapter(7);
        var run = world.Story!;
        // Checkpoint 3 is the break-in: the hunt is on once the core has blown.
        for (var i = 0; i < (Prologue.CoreBlows + 15) * GameWorld.TicksPerSecond && !run.Director.Has("core_blows"); i++)
            world.Step();
        Assert.True(run.Director.Has("core_blows"));
        // The Locator through to the crossing already, out of the hunt's way.
        Hero(world, 0).Position = new Vector2(401, 241);
        var bob = Hero(world, 2);
        var spot = new Vector2(360, 237);
        var door = new Vector2(218, 237);
        var reaper = world.AddMachine(Race.Blue, door, "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        reaper.Facing = 0;
        world.Step();
        world.Issue(new AttackCommand(Race.Blue, [reaper.Id], bob.Id));
        var far = 0;
        var aimingAtTheDoor = 0f;
        for (var i = 0; i < 25 * GameWorld.TicksPerSecond && !run.Over; i++)
        {
            bob.Position = spot;
            world.Issue(new DriveCommand(Race.Red, Hero(world, 0).Id, Driving: true));
            world.Step();
            far += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == reaper.Id && Vector2.Distance(reaper.Position, bob.Position) > StorySight.FireReach);
            if (reaper.Order is AttackOrder && Vector2.Distance(reaper.Position, door) < 3)
                aimingAtTheDoor += GameWorld.Dt;
        }

        Assert.Equal(0, far);
        Assert.True(aimingAtTheDoor < 6, $"stood aiming at the door {aimingAtTheDoor:0.0} s; at {reaper.Position}, {reaper.Order}");
        Assert.True(Vector2.Distance(reaper.Position, door) > 10, $"still at {reaper.Position}, {reaper.Order}");
    }

    // ---- the way to the Notgang ---------------------------------------------------------------------------

    /// <summary>
    /// The crate the workshop's fight brings down on a Grunt lies beside it, but never where it would shut a way of the trio's:
    /// with Bob in the lane between the cover and door T's wall, or in the mouth of door U, the workshop, the repair niche's
    /// door and door T stay one area for the Locator and the Grunts (before, a crate there left the Notgang out of reach).
    /// </summary>
    [Theory]
    [InlineData(475f, 235f)]
    [InlineData(477f, 243f)]
    [InlineData(485f, 227f)]
    public void The_crate_knocked_onto_a_grunt_in_the_workshop_never_shuts_the_way_on(float x, float y)
    {
        var world = Chapter(8);
        var run = world.Story!;
        var locator = Hero(world, 0);
        var bob = Hero(world, 2);
        world.OpenDoor('T');
        world.OpenDoor('U');
        var rack = world.Deck!.Marker("pickup_bolter").Position;
        locator.Position = rack + new Vector2(-1.5f, -1.5f);
        Hero(world, 1).Position = rack + new Vector2(-6, -4);
        world.Issue(new StoryOrderCommand(Race.Red, CompanionOrder.Wait, Vector2.Zero, run.Seats[2]));
        bob.Position = new Vector2(x, y);
        world.Step();
        world.Issue(new StoryUseCommand(Race.Red, "pickup_bolter"));
        // The armoury bolts shut (stage 7), the first batch comes, and the crate falls a few seconds into it.
        for (var i = 0; i < 60 * GameWorld.TicksPerSecond && !run.Over && !run.Director.Has("knocked"); i++)
        {
            // Bob comes in with the others (the room seals with all three in it), then stands where the test puts him.
            if (!world.CompanionDown(run.Seats[2]))
                bob.Position = run.Director.Has("lockdown") ? new Vector2(x, y) : rack + new Vector2(-4, 2);
            world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true));
            world.Step();
        }
        for (var i = 0; i < 3 * GameWorld.TicksPerSecond && !run.Over; i++)
        {
            if (!world.CompanionDown(run.Seats[2]))
                bob.Position = new Vector2(x, y);
            world.Step();
        }

        Assert.True(run.Director.Has("knocked"));
        // The lockdown shut T and U again; the way is measured with them open, as after the batches.
        world.OpenDoor('T', instant: true);
        world.OpenDoor('U', instant: true);
        var crate = Assert.Single(world.Artefacts, a => a.Id >= Artefact.IdBase + StoryDirector.PlacedBase);
        Assert.True(Vector2.Distance(crate.Position, bob.Position) >= bob.Radius + StoryFall.Lies, $"crate at {crate.Position}, Bob at {bob.Position}");
        Vector2[] places = [new(451, 249), new(475, 247), new(477, 227), new(493, 249), new(505, 227)];
        foreach (var mobility in new[] { GameWorld.MobilityOf(locator), GameWorld.MobilityOf(bob) })
        {
            var regions = places.Select(p => world.Nav.Cell(p)).Select(c => world.Nav.Region(c.X, c.Y, mobility)).ToList();
            Assert.True(regions.All(r => r != 0 && r == regions[0]), $"{mobility}: crate at {crate.Position} leaves regions {string.Join(",", regions)}");
        }
    }

    /// <summary>A blocked goal right beside a machine (a crate it is sent at) is still driven up to across the open floor.</summary>
    [Fact]
    public void A_crate_beside_a_machine_on_the_deck_is_still_driven_up_to()
    {
        var world = Chapter(7);
        var grunt = world.AddMachine(Race.Blue, new Vector2(401, 257), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var crate = world.PlaceArtefact("crate_small", new Vector2(405, 257), 0);
        Assert.NotNull(crate);
        var mobility = GameWorld.MobilityOf(grunt);
        Assert.False(world.Nav.IsFree(new Vector2(405, 257), mobility));

        var path = world.Nav.FindPath(grunt.Position, new Vector2(405, 257), mobility);

        Assert.NotEmpty(path);
        Assert.True(Vector2.Distance(path[^1], new Vector2(405, 257)) < 4, $"ends at {path[^1]}");
    }
}
