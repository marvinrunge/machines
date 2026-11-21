using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Planets;
using Machines.Import.Scenarios;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The story's skeleton (plan WP-A): the prologue's deck and planet, a story match with its three unarmed heroes at their
/// markers and no pod, that it lasts until its run ends it, that the Locator's fall ends it, and that it is deterministic.
/// </summary>
public class StoryRunTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static Deck PrologueDeck => Data.LoadPlanet(StoryOptions.Prologue).Deck!;

    /// <summary>The prologue as a person starts it alone: the trio's race theirs, the ship the computer's.</summary>
    private static GameWorld Prologue(int seed = 3, StoryOptions? story = null) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = story ?? new StoryOptions(), FogOfWar = false });

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    // ---- the deck and the planet -----------------------------------------------------------------

    [Fact]
    public void The_prologue_planet_is_the_remakes_own_with_its_deck()
    {
        var planet = Data.LoadPlanet(StoryOptions.Prologue);

        Assert.Equal(630, planet.Surface.Width);
        Assert.Equal(420, planet.Surface.Depth);
        Assert.All(planet.Surface.Tiles, tile => Assert.Equal(0, tile.Height));
        Assert.NotEmpty(planet.ConfigSpace.Obstacles);
        Assert.NotNull(planet.Deck);
        Assert.Equal((630, 420), (planet.Deck!.Width, planet.Deck.Depth));
        // The original's planets have no deck and still load from data/original.
        Assert.Null(Data.LoadPlanet("m_desert").Deck);
    }

    [Fact]
    public void The_prologue_deck_has_every_room_door_and_marker_the_story_asks_for()
    {
        var deck = PrologueDeck;

        foreach (var room in (string[])["R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R12", "R13", "R14", "R15", "R16", "WL", "EL", "S", "K", "P1", "P2", "V1a", "V1b", "V1c", "V2"])
            Assert.Contains("room", deck.Zone(room)?.Tags ?? []);
        foreach (var marker in StorySeats.Spawns.Concat(["core", "hack_core", "pickup_bolter", "repair_station", "pod_3", "spawn_thresher", "spawn_commander"]))
            Assert.Equal(DeckCell.Floor, deck.CellAt(deck.Marker(marker).Position));
        Assert.Equal(6, deck.Markers.Count(m => m.Tags.Contains("patrol")));
        Assert.Equal(24, deck.Markers.Count(m => m.Name.StartsWith("clampA_", StringComparison.Ordinal) || m.Name.StartsWith("clampB_", StringComparison.Ordinal)));
        Assert.Equal(9, deck.Markers.Count(m => m.Tags.Contains("barricade")));
        Assert.Equal("locked", deck.Door('W')!.State);
        Assert.Contains(deck.Cameras, c => c.Name == "cam_abwurf_1");
        Assert.DoesNotContain(deck.Cameras, c => c.Name.StartsWith("cam_erwischt", StringComparison.Ordinal));
        // Each spawn stands in its own bay, and the bays are shut by locked doors.
        Assert.True(deck.Zone("R1")!.Contains(deck.Marker("spawn_l7").Position));
        Assert.True(deck.Zone("R2")!.Contains(deck.Marker("spawn_ike").Position));
        Assert.True(deck.Zone("R3")!.Contains(deck.Marker("spawn_bob").Position));
        Assert.All("ABC", letter => Assert.Equal("locked", deck.Door(letter)!.State));
        // Ducts have lips at their mouths and grates look from the escape duct into both parallel corridors.
        Assert.Equal(DeckCell.Lip, deck.Cell(228, 266));
        Assert.Equal(DeckCell.Duct, deck.Cell(296, 266));
        Assert.Equal(DeckCell.Grate, deck.Cell(240, 260));
        Assert.Equal(DeckCell.Grate, deck.Cell(240, 274));
    }

    [Fact]
    public void Every_closed_door_of_the_deck_is_an_artefact_of_the_scenario_in_its_slot()
    {
        var deck = PrologueDeck;
        var artefacts = Data.LoadScenario(StoryOptions.Prologue).Artefacts!;

        foreach (var door in deck.Doors)
        {
            var cells = Enumerable.Range(0, deck.Width).SelectMany(x => Enumerable.Range(0, deck.Depth).Select(y => (x, y)))
                .Where(c => deck.At(c.x, c.y) == door.Letter).ToList();
            var (min, max) = (new Vector2(cells.Min(c => c.x), cells.Min(c => c.y)), new Vector2(cells.Max(c => c.x) + 1, cells.Max(c => c.y) + 1));
            var placed = artefacts.Placements.Where(p => p.Position.X > min.X && p.Position.X < max.X && p.Position.Y > min.Y && p.Position.Y < max.Y).ToList();
            if (door.State == "open")
                Assert.Empty(placed);
            else
                Assert.InRange(placed.Count, 1, 2);
        }
        var core = artefacts.Placements.Single(p => p.TypeName == "core");
        Assert.Equal(deck.Marker("core").Position, new Vector2(core.Position.X, core.Position.Y));
    }

    [Theory]
    [InlineData("DECK d 3 2\nGRID\n###\n#x#\nENDGRID", "unknown grid character")]
    [InlineData("DECK d 3 2\nGRID\n###\n##\nENDGRID", "3 characters")]
    [InlineData("DECK d 3 2\nGRID\n###\n#A#\nENDGRID", "not in DOORS")]
    [InlineData("DECK d 3 2\nGRID\n###\n#.#\nENDGRID\nDOORS\nB b door open\nENDDOORS", "not in the grid")]
    [InlineData("DECK d 3 2\nGRID\n###\n#.#\nENDGRID\nLIGHTS\nl R1 on\nENDLIGHTS", "names no zone")]
    [InlineData("DECK d 3 2\nGRID\n###\n#.#\nENDGRID\nZONES\nz 2 0 1 1\nENDZONES", "low to high")]
    public void A_deck_that_does_not_hold_together_is_refused(string text, string why)
    {
        var refused = Assert.Throws<ParmFormatException>(() => DeckParser.Parse(text));

        Assert.Contains(why, refused.Message);
    }

    [Fact]
    public void A_deck_reads_north_first_and_its_fingerprint_follows_what_the_sim_reads()
    {
        const string grid = "DECK d 3 2\nGRID\n#A.\nv^c\nENDGRID\nDOORS\nA a door closed\nENDDOORS\n";
        var deck = DeckParser.Parse(grid + "MARKERS\nm 2.5 1.5 90 hero\nENDMARKERS\nCAMS\nc 1 1 2 0 0\nENDCAMS\n");

        Assert.Equal(DeckCell.Duct, deck.Cell(0, 0));
        Assert.Equal(DeckCell.Lip, deck.Cell(1, 0));
        Assert.Equal(DeckCell.Cover, deck.Cell(2, 0));
        Assert.Equal(DeckCell.Door, deck.Cell(1, 1));
        Assert.Equal(DeckCell.Floor, deck.CellAt(deck.Marker("m").Position));
        Assert.Equal(DeckCell.Wall, deck.Cell(-1, 0));
        // A camera is the view's: it does not change the fingerprint; a marker does.
        Assert.Equal(deck.Fingerprint, DeckParser.Parse(grid + "MARKERS\nm 2.5 1.5 90 hero\nENDMARKERS\nCAMS\nc 9 9 9 0 0\nENDCAMS\n").Fingerprint);
        Assert.NotEqual(deck.Fingerprint, DeckParser.Parse(grid + "MARKERS\nm 2.5 1.5 0 hero\nENDMARKERS\n").Fingerprint);
    }

    // ---- the match -------------------------------------------------------------------------------

    [Fact]
    public void A_prologue_starts_with_three_unarmed_heroes_at_their_markers_and_no_pod()
    {
        var world = Prologue();
        var deck = PrologueDeck;
        var story = world.Story!;

        Assert.Equal(VictoryCondition.Story, world.Options.Victory);
        Assert.Equal(WiredPhase.Story, world.Wired!.Phase);
        Assert.Empty(world.Constructions);
        // Besides the heroes only the ship's switched-off rows the director sets into the clamps (Prologue.Rows).
        Assert.All(world.Machines.Where(m => world.Wired.Heroes.Values.All(s => s.Hero != m.Id)),
            m => Assert.True(m.Race == Race.Blue && m.Weapons.Count == 0, m.ToString()));
        // The Locator a person's, Ike and Bob the computer's, the ship the computer's as a whole.
        Assert.Equal([(PlayerId.Of(Race.Red), PlayerKind.Human), (new PlayerId(4), PlayerKind.Bot), (new PlayerId(5), PlayerKind.Bot)],
            story.Seats.Select(id => (id, world.Players[id].Kind)));
        Assert.Equal(["Ike", "Bob"], story.Seats.Skip(1).Select(id => world.Players[id].Name));
        Assert.Equal(PlayerKind.Ai, world.Players[PlayerId.Of(Race.Blue)].Kind);
        Assert.Equal(story.Seats[0], story.Locator);
        for (var i = 0; i < StorySeats.Count; i++)
        {
            var hero = world.HeroOf(story.Seats[i])!;
            var marker = deck.Marker(StorySeats.Spawns[i]);
            Assert.Equal(marker.Position, hero.Position);
            Assert.Equal(marker.Angle * MathF.PI / 180, hero.Facing, 4);
            Assert.Empty(hero.Weapons);
            Assert.Equal(story.Seats[i], hero.Owner);
            Assert.Equal(i == 0 ? "GEO_LOCATOR" : "AGGRESSOR", hero.Spec.Type);
            Assert.Equal(1, hero.Stats.HardwareLevel);
            Assert.Null(world.Wired.Heroes[story.Seats[i]].Pod);
            // Today's 4 m grid of the greybox has room where each one starts.
            Assert.True(world.Nav.IsFree(hero.Position, crossesWater: false), StorySeats.Spawns[i]);
        }
        // The Locator wears the heroes' tune of Geo Locator 1, not the original's crab.
        var tune = WiredMachines.HeroTune("GEO_LOCATOR", 1)!;
        Assert.Equal((int)MathF.Round(tune.HitPoints * WiredBalance.HeroHitPoints), world.HeroOf(story.Locator)!.MaxHitPoints);
    }

    [Fact]
    public void The_computers_grunts_follow_the_locator_for_now()
    {
        // Once awake: before the first checkpoint the director keeps them asleep in their clamps.
        var world = Prologue(story: new StoryOptions { Checkpoint = 1 });
        var locator = world.HeroOf(world.Story!.Locator)!;

        Assert.All(world.Story.Seats.Skip(1), id => Assert.Equal(new FollowOrder(locator.Id), world.HeroOf(id)!.Order));
    }

    [Fact]
    public void A_checkpoint_and_the_grunts_names_go_with_the_story()
    {
        var world = Prologue(story: new StoryOptions { Checkpoint = 2, IkeName = "Kai", BobName = "Mo" });

        Assert.Equal(2, world.Story!.Checkpoint);
        Assert.Equal(["Kai", "Mo"], world.Story.Seats.Skip(1).Select(id => world.Players[id].Name));
    }

    [Fact]
    public void A_story_does_not_end_by_itself_in_ten_minutes_of_idling()
    {
        var world = Prologue();
        var heroes = world.Story!.Seats.Select(id => world.HeroOf(id)!.Id).ToList();

        Run(world, 600);

        Assert.False(world.Story.Over);
        Assert.Null(world.Winner);
        Assert.All(world.Races.Values, race => Assert.False(race.Defeated));
        Assert.Equal(WiredPhase.Story, world.Wired!.Phase);
        Assert.Equal(heroes, world.Story.Seats.Select(id => world.HeroOf(id)?.Id ?? -1));
        Assert.Empty(world.Constructions);
    }

    [Fact]
    public void Two_runs_of_the_same_story_keep_the_same_checksums()
    {
        var (a, b) = (Prologue(seed: 11), Prologue(seed: 11));

        for (var second = 0; second < 30; second++)
        {
            Run(a, 1);
            Run(b, 1);
            Assert.Equal(a.Checksum(), b.Checksum());
        }
        Assert.NotEqual(a.Checksum(), Prologue(seed: 11, new StoryOptions { Checkpoint = 1 }).Checksum());
    }

    [Fact]
    public void The_locators_fall_ends_the_chapter_and_a_grunts_does_not()
    {
        var world = Prologue();
        var story = world.Story!;
        world.HeroOf(story.Seats[1])!.HitPoints = 0;
        world.Step();

        Assert.False(story.Over);
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.StoryFailed);
        Assert.Null(world.HeroOf(story.Seats[1]));

        world.HeroOf(story.Locator)!.HitPoints = 0;
        world.Step();

        Assert.Equal(StoryEnding.LocatorLost, story.Ending);
        var failed = Assert.Single(world.Events, e => e.Kind == GameEventKind.StoryFailed);
        Assert.Equal((story.Locator, nameof(StoryEnding.LocatorLost)), (failed.Player, failed.Detail));
        Assert.True(world.Races[Race.Red].Defeated);
        Assert.Equal(WiredPhase.Ended, world.Wired!.Phase);
        // No body comes back: the chapter starts again from its checkpoint, as a new match.
        Run(world, 30);
        Assert.Null(world.HeroOf(story.Locator));
        Assert.DoesNotContain(world.Events, e => e.Kind == GameEventKind.StoryFailed);
    }

    private sealed record Probe(Race Race) : StoryCommand(Race)
    {
        public int Applied { get; private set; }

        protected override string? ApplyTo(GameWorld world)
        {
            Applied++;
            return world.Story is null ? "not here" : null;
        }
    }

    [Fact]
    public void A_story_command_applies_itself_in_a_story_and_is_refused_elsewhere()
    {
        var world = Prologue();
        var probe = new Probe(Race.Red);
        world.Issue(probe);
        world.Step();

        Assert.Equal(1, probe.Applied);
        Assert.Empty(world.Rejected);

        var skirmish = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai });
        var elsewhere = new Probe(Race.Red);
        skirmish.Issue(elsewhere);
        skirmish.Step();

        Assert.Equal(0, elsewhere.Applied);
        Assert.Equal("no story", Assert.Single(skirmish.Rejected).Reason);
    }

    [Fact]
    public void The_story_has_no_hangar_and_no_waves_to_ready_for()
    {
        var world = Prologue();
        var state = world.Wired!.Heroes[world.Story!.Locator];
        world.Issue(new WiredHangarCommand(Race.Red, true));
        world.Issue(new WiredReadyCommand(Race.Red));
        world.Step();

        Assert.Equal(["there is no hangar in the story", "there is no hangar in the story"], world.Rejected.Select(r => r.Reason));
        Assert.Equal("there is no hangar in the story", world.WhyNoHangar(state));
        Assert.False(state.HangarOpen);
        Assert.Equal(WiredPhase.Story, world.Wired.Phase);
    }

    [Fact]
    public void Bots_sit_only_in_a_battle_or_the_story()
    {
        var rows = new PlayerTable([new PlayerSeat(PlayerId.Of(Race.Red), Race.Red, PlayerKind.Human, "Me"), new PlayerSeat(new PlayerId(4), Race.Red, PlayerKind.Bot, "Ike")]);

        Assert.Throws<ArgumentException>(() => MatchSetup.Create(Data, "m_desert", "m_desert", rows));
        var world = MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue, rows, 1, new MatchOptions { Story = new StoryOptions() });
        Assert.Equal(StorySeats.Count + 1, world.Players.Rows.Count);
        Assert.Equal("Ike", world.Players[new PlayerId(4)].Name);
        Assert.Equal("Bob", world.Players[world.Story!.Seats[2]].Name);
    }

    [Fact]
    public void A_story_match_is_told_by_its_options_alone()
    {
        Assert.True(new MatchOptions { Victory = VictoryCondition.Story }.IsStory);
        Assert.False(new MatchOptions().IsStory);
        var world = MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player }, 1, new MatchOptions { Victory = VictoryCondition.Story });
        Assert.Equal(StoryOptions.Prologue, world.Story!.Options.Chapter);
        Assert.Equal(StorySeats.Count, world.Story.Seats.Count);
    }
}
