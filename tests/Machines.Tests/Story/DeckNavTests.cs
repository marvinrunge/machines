using System.Numerics;
using System.Text;
using Machines.Import.Model;
using Machines.Import.Planets;
using Machines.Sim;
using Machines.Sim.Navigation;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The deck planet's fine navigation and walls (plan WP-B): who fits through which gap and duct, that driven machines slide
/// along walls, that walls stop shots and sight while grates let them through, that doors open, that the prologue's rooms
/// are reached by whom the story wants, and that the original planets' coarse grid answers as it always did.
/// </summary>
public class DeckNavTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly Lazy<Planet> ProloguePlanet = new(() => Data.LoadPlanet(StoryOptions.Prologue));

    // ---- worlds and machines ----------------------------------------------------------------------

    /// <summary>A world on the prologue planet whose deck is replaced by one drawn here (rows north first).</summary>
    private static GameWorld OnDeck(params string[] rows)
    {
        var text = new StringBuilder($"DECK test {rows[0].Length} {rows.Length}\nGRID\n");
        foreach (var row in rows)
            text.Append(row).Append('\n');
        text.Append("ENDGRID\n");
        var letters = rows.SelectMany(r => r).Where(c => c is >= 'A' and <= 'Z').Distinct().Order().ToList();
        if (letters.Count > 0)
        {
            text.Append("DOORS\n");
            foreach (var letter in letters)
                text.Append($"{letter} door_{letter} door closed\n");
            text.Append("ENDDOORS\n");
        }
        var deck = DeckParser.Parse(text.ToString());
        return new GameWorld(Data, ProloguePlanet.Value with { Deck = deck }, 1);
    }

    /// <summary>A room 40 x 24 cut in two by a wall two cells thick at x 19-20, with a gap of <paramref name="gap"/> cells.</summary>
    private static GameWorld TwoRooms(int gap, char inGap = '.')
    {
        var rows = new List<string>();
        for (var y = 23; y >= 0; y--)
        {
            var row = new char[40];
            for (var x = 0; x < 40; x++)
            {
                var edge = x < 2 || y < 2 || x >= 38 || y >= 22;
                var wall = x is 19 or 20 && !(y >= 12 - gap / 2 && y < 12 - gap / 2 + gap);
                var slot = x is 19 or 20 && !wall;
                row[x] = edge || wall ? '#' : slot ? inGap : '.';
            }
            rows.Add(new string(row));
        }
        return OnDeck([.. rows]);
    }

    private static readonly Vector2 West = new(8.5f, 12.5f), East = new(30.5f, 12.5f);

    private static Machine Locator(GameWorld world, Vector2 at, Race race = Race.Red) => world.AddMachine(race, at, "GEO_LOCATOR", null, 1, null);

    private static Machine Grunt(GameWorld world, Vector2 at, Race race = Race.Red) => world.AddMachine(race, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

    private static Machine Reaper(GameWorld world, Vector2 at, Race race = Race.Blue) => world.AddMachine(race, at, "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");

    private static Machine Commander(GameWorld world, Vector2 at, Race race = Race.Blue) =>
        world.AddMachine(race, at, "ADMINISTRATOR", "COMMANDER", 5, "LR_PULSE_CANNON_X2");

    private static Machine Thresher(GameWorld world, Vector2 at, Race race = Race.Blue) =>
        world.AddMachine(race, at, "AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");

    /// <summary>Whether a machine could go from one point to another: both open to it and in one connected area.</summary>
    private static bool Passes(GameWorld world, Machine machine, Vector2 from, Vector2 to)
    {
        var mobility = GameWorld.MobilityOf(machine);
        var (a, b) = (world.Nav.Cell(from), world.Nav.Cell(to));
        var region = world.Nav.Region(a.X, a.Y, mobility);
        return region != 0 && region == world.Nav.Region(b.X, b.Y, mobility)
            && world.Nav.FindPath(from, to, mobility) is { Count: > 0 } path && Vector2.Distance(path[^1], to) < 0.01f;
    }

    // ---- mobility -----------------------------------------------------------------------------

    [Fact]
    public void The_casts_mobility_classes_are_those_of_the_plan()
    {
        var world = TwoRooms(8);

        Assert.Equal(new Mobility(false, 1, true), GameWorld.MobilityOf(Locator(world, West)));
        Assert.Equal(new Mobility(false, 1, false), GameWorld.MobilityOf(Grunt(world, West)));
        Assert.Equal(new Mobility(false, 2, false), GameWorld.MobilityOf(Reaper(world, East)));
        Assert.Equal(new Mobility(true, 2, false), GameWorld.MobilityOf(Commander(world, East)));
        Assert.Equal(new Mobility(false, 3, false), GameWorld.MobilityOf(Thresher(world, East)));
        Assert.Equal([0f, 1.5f, 2.5f, 3.5f], Enumerable.Range(0, 4).Select(Mobility.Room));
    }

    [Fact]
    public void Only_the_locator_crawls_through_a_duct_with_lips()
    {
        // A duct four cells wide through the wall, a lip at each mouth.
        var world = TwoRooms(4, 'v');
        foreach (var y in Enumerable.Range(10, 4))
        {
            Assert.Equal(DeckCell.Duct, world.Deck!.Cell(19, y));
        }
        var lipped = OnDeck(
            [.. Enumerable.Range(0, 24).Reverse().Select(y => new string(Enumerable.Range(0, 40).Select(x =>
                x < 2 || y < 2 || x >= 38 || y >= 22 ? '#'
                : x is >= 17 and <= 22 ? (y is >= 10 and < 14 ? (x is 17 or 22 ? '^' : 'v') : '#')
                : '.').ToArray()))]);

        foreach (var deck in new[] { world, lipped })
        {
            Assert.True(Passes(deck, Locator(deck, West), West, East));
            Assert.False(Passes(deck, Grunt(deck, West), West, East));
            Assert.False(Passes(deck, Reaper(deck, West), West, East));
            Assert.False(Passes(deck, Commander(deck, West), West, East));
        }
        // Inside, the Locator has room; the ducts are no ground to a wheeled Grunt at all.
        Assert.True(lipped.Nav.IsFree(new Vector2(19.5f, 12.5f), GameWorld.MobilityOf(Locator(lipped, West))));
        Assert.False(lipped.Nav.IsFree(new Vector2(17.5f, 12.5f), Mobility.Land));
    }

    [Fact]
    public void A_four_metre_gap_lets_grunts_through_and_stops_reapers_and_the_commander()
    {
        var world = TwoRooms(4);

        Assert.True(Passes(world, Locator(world, West), West, East));
        Assert.True(Passes(world, Grunt(world, West), West, East));
        Assert.False(Passes(world, Reaper(world, West), West, East));
        Assert.False(Passes(world, Commander(world, West), West, East));
        // Three metres is the least a Grunt passes; two stop it.
        var three = TwoRooms(3);
        Assert.True(Passes(three, Grunt(three, West), West, East));
        var two = TwoRooms(2);
        Assert.False(Passes(two, Grunt(two, West), West, East));
    }

    [Fact]
    public void A_seven_metre_corridor_lets_reapers_through_and_a_six_metre_one_stops_the_thresher()
    {
        var seven = TwoRooms(7);
        Assert.True(Passes(seven, Reaper(seven, West), West, East));
        Assert.True(Passes(seven, Commander(seven, West), West, East));
        Assert.True(Passes(seven, Thresher(seven, West), West, East));

        var six = TwoRooms(6);
        Assert.True(Passes(six, Reaper(six, West), West, East));
        Assert.False(Passes(six, Thresher(six, West), West, East));

        // Five metres is the least a Reaper passes.
        var five = TwoRooms(5);
        Assert.True(Passes(five, Reaper(five, West), West, East));
        var four = TwoRooms(4);
        Assert.False(Passes(four, Reaper(four, West), West, East));
    }

    [Fact]
    public void An_opened_door_frees_its_slot_and_the_room_round_it()
    {
        // A six-metre doorway with a door artefact in it.
        var world = TwoRooms(6, 'D');
        var type = new ArtefactType("door", true, 0, 30000, 100, null, 7, []);
        var leaf = world.AddArtefact(0, new Vector2(20, 12), type, 0, [new(19, 9), new(21, 9), new(21, 15), new(19, 15)]);
        leaf.Tag = 100;
        var reaper = Reaper(world, West);
        Assert.False(Passes(world, reaper, West, East));
        Assert.True(world.DoorClosed('D'));

        Assert.True(world.OpenDoor('D'));

        Assert.Contains(world.Events, e => e.Kind == GameEventKind.ArtefactRemoved && e.Actor == leaf.Id && e.Other == 100);
        Assert.True(Passes(world, reaper, West, East));
        Assert.False(world.DoorClosed('D'));
        Assert.False(world.OpenDoor('D'));
        Assert.False(world.RemoveArtefact(leaf.Id));
        Assert.DoesNotContain(leaf, world.Artefacts);
        world.Step();
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.Destroyed && e.Actor == leaf.Id);
        Assert.Empty(world.Debris);
    }

    // ---- driving --------------------------------------------------------------------------------

    [Fact]
    public void A_driven_locator_slides_along_a_wall_without_entering_it()
    {
        var world = TwoRooms(4);
        var locator = Locator(world, new Vector2(4.5f, 6.5f));
        var mobility = GameWorld.MobilityOf(locator);
        // Driven east and a little south, into the south wall (its face at y 2).
        locator.Facing = -0.35f;
        locator.DrivenByPlayer = true;
        world.DriveInput[locator.Id] = (1, 0, 0, false);
        var lowest = float.MaxValue;
        for (var tick = 0; tick < 10 * GameWorld.TicksPerSecond; tick++)
        {
            world.Step();
            Assert.True(world.Nav.IsFree(locator.Position, mobility), $"tick {tick}: in the wall at {locator.Position}");
            lowest = MathF.Min(lowest, locator.Position.Y);
        }
        // It got along the wall (to the dividing wall at x 19) and kept its body out of the south wall.
        Assert.True(locator.Position.X > 14, $"stuck at {locator.Position}");
        Assert.True(lowest >= 2 + 1, $"came within {lowest - 2} m of the wall's face");
    }

    // ---- shots and sight ------------------------------------------------------------------------

    [Fact]
    public void A_wall_blocks_a_clear_shot_and_a_grate_does_not()
    {
        var world = Prologue();
        // From the second parallel corridor (P2) up into the escape duct: through the grate window at x 112-114, or through the
        // wall beside it at x 117.
        var throughGrate = (Reaper(world, new Vector2(113.5f, 109.5f)), Locator(world, new Vector2(113.5f, 117.5f)));
        var throughWall = (Reaper(world, new Vector2(117.5f, 109.5f)), Locator(world, new Vector2(117.5f, 117.5f)));
        Assert.Equal(DeckCell.Grate, world.Deck!.Cell(113, 114));
        Assert.Equal(DeckCell.Wall, world.Deck.Cell(117, 114));

        Assert.True(world.ClearShot(throughGrate.Item1, throughGrate.Item2));
        Assert.True(world.ClearShot(throughGrate.Item2, throughGrate.Item1));
        Assert.False(world.ClearShot(throughWall.Item1, throughWall.Item2));
        Assert.False(world.ClearShot(throughWall.Item2, throughWall.Item1));
    }

    [Fact]
    public void Shots_stop_at_walls_one_cell_thick_and_at_high_cover_but_pass_grates()
    {
        var world = OnDeck(
            "#########",
            "#.......#",
            "#.......#",
            "####g####",
            "#.......#",
            "###.#c###",
            "#.......#",
            "#########");
        var ground = world.Terrain!;
        Vector3 At(float x, float y, float z = 1.4f) => new(x, y, z);
        // A projectile's flight (1.5 m steps, nothing left out at the ends) and a clear shot's (2 m steps, half a metre left out).
        foreach (var (step, ends) in new[] { (1.5f, 0f), (2f, 0.5f) })
        {
            // Through the grate at (4, 4): clear; through the wall one cell thick beside it: stopped.
            Assert.Null(ground.FirstBlocked(At(4.5f, 6.5f), At(4.5f, 3.5f), step, ends));
            Assert.NotNull(ground.FirstBlocked(At(2.5f, 6.5f), At(2.5f, 3.5f), step, ends));
            // Through the gap at (3, 2): clear; across the high cover at (5, 2) at a machine's height: stopped.
            Assert.Null(ground.FirstBlocked(At(3.5f, 3.5f), At(3.5f, 1.5f), step, ends));
            Assert.NotNull(ground.FirstBlocked(At(5.5f, 3.5f), At(5.5f, 1.5f), step, ends));
            // Over the cover from higher up: clear.
            Assert.Null(ground.FirstBlocked(At(5.5f, 3.5f, 3), At(5.5f, 1.5f, 3), step, ends));
        }
        // The floor is flat right up to a wall's face.
        Assert.Equal(0, ground.At(new Vector2(1.01f, 1.5f)));
        Assert.Equal(0, ground.At(new Vector2(3.5f, 5.01f)));
        Assert.Equal(TerrainHeights.DeckWallHeight, ground.At(new Vector2(2.5f, 4.5f)));
    }

    [Fact]
    public void Machines_either_side_of_a_wall_do_not_hurt_each_other_and_through_a_grate_they_do()
    {
        // Two corridors side by side, a wall between them with a grate window at the west end.
        var world = OnDeck(
            "##############################",
            "#............................#",
            "#............................#",
            "#............................#",
            "##ggg#########################",
            "##ggg#########################",
            "#............................#",
            "#............................#",
            "#............................#",
            "##############################");
        var shooter = world.AddMachine(Race.Red, new Vector2(20.5f, 7.5f), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var behindWall = world.AddMachine(Race.Blue, new Vector2(20.5f, 2.5f), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Races[Race.Red] = new RaceState(Race.Red, Controller.Player);
        world.Races[Race.Blue] = new RaceState(Race.Blue, Controller.Player);
        // Grunt 1 has no upper body to turn: they face each other across the wall.
        (shooter.Facing, behindWall.Facing) = (-MathF.PI / 2, MathF.PI / 2);
        world.Issue(new StandGroundCommand(Race.Red, [shooter.Id]));
        world.Issue(new StandGroundCommand(Race.Blue, [behindWall.Id]));

        for (var i = 0; i < 10 * GameWorld.TicksPerSecond; i++)
            world.Step();
        Assert.Equal(behindWall.MaxHitPoints, behindWall.HitPoints);
        Assert.Equal(shooter.MaxHitPoints, shooter.HitPoints);

        // Moved to stand either side of the grate, they fight.
        (shooter.Position, behindWall.Position) = (new Vector2(3.5f, 7.5f), new Vector2(3.5f, 2.5f));
        for (var i = 0; i < 10 * GameWorld.TicksPerSecond; i++)
            world.Step();
        Assert.True(behindWall.HitPoints < behindWall.MaxHitPoints || shooter.HitPoints < shooter.MaxHitPoints);
    }

    // ---- the prologue's deck --------------------------------------------------------------------

    private static GameWorld Prologue() =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3,
            new MatchOptions { Story = new StoryOptions(), FogOfWar = false });

    /// <summary>The rooms (ZONES tagged room) a way of moving reaches from a marker, in the deck's order.</summary>
    private static List<string> Reached(GameWorld world, Mobility mobility, string from)
    {
        var deck = world.Deck!;
        var start = deck.Marker(from).Position;
        var nav = world.Nav;
        var at = nav.Cell(start);
        if (!nav.IsFree(at.X, at.Y, mobility) && nav.NearestFreePoint(start, mobility) is { } near && Vector2.Distance(near, start) <= 3)
            at = nav.Cell(near);
        var region = nav.Region(at.X, at.Y, mobility);
        Assert.True(region != 0, $"{mobility} has no room at {from}");
        return deck.Zones.Where(z => z.Tags.Contains("room") && Covers(z, (x, y) => nav.Region(x, y, mobility) == region)).Select(z => z.Name).ToList();
    }

    private static bool Covers(DeckZone zone, Func<int, int, bool> test)
    {
        for (var y = (int)zone.Min.Y; y < (int)zone.Max.Y; y++)
            for (var x = (int)zone.Min.X; x < (int)zone.Max.X; x++)
                if (test(x, y))
                    return true;
        return false;
    }

    private static (Mobility Locator, Mobility Grunt, Mobility Reaper, Mobility Commander, Mobility Thresher) Cast(GameWorld world)
    {
        var seats = world.Story!.Seats;
        var (l7, ike) = (world.HeroOf(seats[0])!, world.HeroOf(seats[1])!);
        var spare = new Vector2(88, 139);
        return (GameWorld.MobilityOf(l7), GameWorld.MobilityOf(ike), GameWorld.MobilityOf(Reaper(world, spare)),
            GameWorld.MobilityOf(Commander(world, spare)), GameWorld.MobilityOf(Thresher(world, spare)));
    }

    private static readonly string[] Ducts = ["V1a", "V1b", "V1c", "V2"];

    [Fact]
    public void The_prologue_heroes_crawl_and_walk_as_the_plan_has_them()
    {
        var world = Prologue();
        var cast = Cast(world);

        Assert.Equal(new Mobility(false, 1, true), cast.Locator);
        Assert.Equal(new Mobility(false, 1, false), cast.Grunt);
        Assert.Equal(2, cast.Reaper.Size);
        Assert.Equal(2, cast.Commander.Size);
        Assert.Equal(3, cast.Thresher.Size);
        Assert.True(world.Nav.Fine);
        Assert.Equal(1, world.Nav.CellSize);
        Assert.NotNull(world.Terrain);
    }

    [Fact]
    public void With_every_door_open_each_mobility_reaches_the_rooms_of_the_plan()
    {
        var world = Prologue();
        var cast = Cast(world);
        foreach (var door in world.Deck!.Doors)
            world.OpenDoor(door.Letter);
        var all = world.Deck.Zones.Where(z => z.Tags.Contains("room")).Select(z => z.Name).ToList();

        var locator = Reached(world, cast.Locator, "spawn_l7");
        var grunt = Reached(world, cast.Grunt, "spawn_ike");
        var reaper = Reached(world, cast.Reaper, "spawn_reaper_1");
        var commander = Reached(world, cast.Commander, "spawn_commander");
        var thresher = Reached(world, cast.Thresher, "spawn_thresher");

        Assert.Equal(all, locator);
        Assert.Empty(grunt.Intersect(Ducts));
        Assert.Equal(all.Except(Ducts).Except(["R7"]), grunt);
        string[] neverBig = ["K", "S", "R0", "R1", "R2", "R3", "R5", "R7", "R13", "R13a", "R14", "R15", "R15a", "R16", .. Ducts];
        Assert.Empty(reaper.Intersect(neverBig));
        Assert.Empty(commander.Intersect(neverBig));
        Assert.Equal(["R4", "WL", "EL", "R6"], thresher);
    }

    [Fact]
    public void Behind_the_blast_doors_and_gates_the_assault_keeps_to_the_core_the_spine_and_the_parallel_corridors()
    {
        var world = Prologue();
        var cast = Cast(world);
        // The story opens the core's doors to the spine and to P2 for the assault; the blast doors to the decks (H, I) and the
        // gates into the crossing (G, O) stay shut.
        foreach (var door in world.Deck!.Doors.Where(d => d.Letter is not ('H' or 'I' or 'G' or 'O')))
            world.OpenDoor(door.Letter);
        string[] confined = ["R9", "R10", "R10a", "P1", "P1s", "P2"];

        Assert.Equal(confined.Order(StringComparer.Ordinal), Reached(world, cast.Reaper, "spawn_reaper_1").Order(StringComparer.Ordinal));
        Assert.Equal(confined.Order(StringComparer.Ordinal), Reached(world, cast.Commander, "spawn_commander").Order(StringComparer.Ordinal));
        // From the core the Grunts take the cable passage (K) to the crossing, where the Reapers cannot follow.
        Assert.Contains("K", Reached(world, cast.Grunt, "hack_core"));
        Assert.Contains("R12", Reached(world, cast.Grunt, "hack_core"));

        // A gate opens: the crossing is theirs too.
        world.OpenDoor('G');
        Assert.Contains("R12", Reached(world, cast.Reaper, "spawn_reaper_1"));
    }

    [Fact]
    public void With_the_doors_as_they_start_each_hero_is_shut_in_its_bay()
    {
        var world = Prologue();
        var cast = Cast(world);

        Assert.Equal(["R1"], Reached(world, cast.Locator, "spawn_l7"));
        Assert.Equal(["R2"], Reached(world, cast.Grunt, "spawn_ike"));
        Assert.Equal(["R3"], Reached(world, cast.Grunt, "spawn_bob"));
        Assert.Equal(["R4", "WL", "EL", "R6"], Reached(world, cast.Thresher, "spawn_thresher"));
    }

    [Fact]
    public void The_heroes_stand_on_ground_open_to_them()
    {
        var world = Prologue();
        foreach (var seat in world.Story!.Seats)
        {
            var hero = world.HeroOf(seat)!;
            Assert.True(world.Nav.IsFree(hero.Position, GameWorld.MobilityOf(hero)), $"{seat} at {hero.Position}");
        }
    }

    [Fact]
    public void A_story_world_on_the_deck_is_deterministic_and_its_checksum_knows_the_deck()
    {
        ulong Run(int seed)
        {
            var world = Prologue();
            for (var i = 0; i < 20 * GameWorld.TicksPerSecond; i++)
                world.Step();
            return world.Checksum();
        }
        Assert.Equal(Run(3), Run(3));

        // The same tick on another deck (a door cell walled up) gives another checksum.
        var planet = ProloguePlanet.Value;
        var text = File.ReadAllText(Path.Combine(RepoPaths.OriginalDataFolder(), "..", "planets", StoryOptions.Prologue, StoryOptions.Prologue + ".deck"));
        var other = DeckParser.Parse(text.Replace("spawn_l7 27 23.5 90 hero", "spawn_l7 27 24.5 90 hero", StringComparison.Ordinal));
        Assert.NotEqual(planet.Deck!.Fingerprint, other.Fingerprint);
        Assert.NotEqual(new GameWorld(Data, planet, 1).Checksum(), new GameWorld(Data, planet with { Deck = other }, 1).Checksum());
    }

    /// <summary>
    /// A picture of the prologue's fine grid as the sim walks it, doors as they start: for each cell, which size class has room
    /// there (yellow A: Locator and Grunts; orange B: Reapers and the Commander; green C: the Thresher; red: nobody), ducts in
    /// violet (where the Locator crawls), grates blue, closed doors and props white, walls dark. 3 pixels a metre, north up.
    /// MACHINES_DECK_NAV_PNG=&lt;file.png&gt; writes it (the check for the merge: compare with docs/story/deck/prolog.png).
    /// </summary>
    [Fact]
    public void The_prologue_nav_map_has_room_for_every_class_and_can_be_drawn()
    {
        var world = Prologue();
        var (nav, deck) = (world.Nav, world.Deck!);
        var counts = new int[Mobility.Largest + 1];
        const int scale = 3;
        var (width, height) = (nav.Width * scale, nav.Depth * scale);
        var pixels = new byte[height * (1 + width * 3)];
        for (var y = 0; y < nav.Depth; y++)
        {
            for (var x = 0; x < nav.Width; x++)
            {
                var cell = deck.Cell(x, y);
                var room = nav.RoomAt(x, y, crawl: false);
                if (cell is DeckCell.Floor or DeckCell.Catwalk or DeckCell.Door && nav.IsFree(x, y, Mobility.Land))
                    counts[room]++;
                (byte R, byte G, byte B) colour = cell switch
                {
                    DeckCell.Wall => (38, 40, 44),
                    DeckCell.Grate => (80, 150, 210),
                    DeckCell.Cover => (140, 100, 60),
                    DeckCell.Duct or DeckCell.Lip => nav.RoomAt(x, y, crawl: true) > 0 ? ((byte)170, (byte)90, (byte)220) : ((byte)90, (byte)40, (byte)120),
                    _ when !nav.IsFree(x, y, Mobility.Land) => (235, 235, 235),
                    _ => room switch { 3 => (70, 190, 80), 2 => (240, 150, 40), 1 => (235, 210, 50), _ => (200, 50, 50) },
                };
                for (var py = 0; py < scale; py++)
                {
                    var row = (nav.Depth - 1 - y) * scale + py;
                    for (var px = 0; px < scale; px++)
                    {
                        var at = row * (1 + width * 3) + 1 + (x * scale + px) * 3;
                        (pixels[at], pixels[at + 1], pixels[at + 2]) = colour;
                    }
                }
            }
        }
        Assert.All(counts.Skip(1), count => Assert.True(count > 0));
        if (Environment.GetEnvironmentVariable("MACHINES_DECK_NAV_PNG") is { Length: > 0 } file)
            File.WriteAllBytes(file, Png(width, height, pixels));
    }

    /// <summary>An RGB picture as a PNG: rows already led by their filter byte (0).</summary>
    private static byte[] Png(int width, int height, byte[] rows)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(BigEndian((uint)data.Length));
            output.Write(body);
            var crc = 0xFFFFFFFFu;
            foreach (var b in body)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            output.Write(BigEndian(~crc));
        }
        static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
        Chunk("IHDR", [.. BigEndian((uint)width), .. BigEndian((uint)height), 8, 2, 0, 0, 0]);
        using (var packed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(rows);
            Chunk("IDAT", packed.ToArray());
        }
        Chunk("IEND", []);
        return output.ToArray();
    }

    // ---- the original planets -------------------------------------------------------------------

    [Theory]
    [InlineData("m_green")]
    [InlineData("m_desert")]
    [InlineData("deepspac")]
    public void On_an_original_planet_the_grid_is_coarse_and_only_the_water_counts(string planetName)
    {
        var world = new GameWorld(Data, Data.LoadPlanet(planetName), 1);
        var nav = world.Nav;

        Assert.False(nav.Fine);
        Assert.Equal(NavGrid.CoarseCellSize, nav.CellSize);
        Assert.Null(world.Terrain);
        Mobility[] land = [new(false, 1, true), new(false, 2, false), new(false, 3, true)];
        Mobility[] water = [new(true, 2, false), new(true, 3, true)];
        for (var y = 0; y < nav.Depth; y += 3)
        {
            for (var x = 0; x < nav.Width; x += 3)
            {
                Assert.All(land, m => Assert.Equal(nav.IsFree(x, y, false), nav.IsFree(x, y, m)));
                Assert.All(water, m => Assert.Equal(nav.IsFree(x, y, true), nav.IsFree(x, y, m)));
                Assert.All(land, m => Assert.Equal(nav.Region(x, y, false), nav.Region(x, y, m)));
            }
        }
    }
}
