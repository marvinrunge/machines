using System.Numerics;
using System.Text.RegularExpressions;
using Machines.Game;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The story's HUD (plan WP-J), as far as it is Godot-free: what the readout says of a prologue (the Locator is L-7, the
/// Grunts by their names, their marks, the objective and its way through the deck, the alarm and the countdown, what can be
/// done where the hero stands, the tracker's contacts), the way through the deck, the subtitles' timing, and that every word
/// the story's screens say has its German in de.po.
/// </summary>
public class StoryHudTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static Deck PrologueDeck => Data.LoadPlanet(StoryOptions.Prologue).Deck!;

    private static GameWorld Prologue(StoryOptions? story = null) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3,
            new MatchOptions { Story = story ?? new StoryOptions(), FogOfWar = false });

    private static StoryReadout Readout(GameWorld world, int seat = 0, DeckRoute? route = null) =>
        StoryReadout.Of(world, world.Story!.Seats[seat], route) ?? throw new InvalidOperationException("no readout");

    // ---- the readout ----------------------------------------------------------------------------------

    [Fact]
    public void The_Locator_is_L7_whoever_plays_it_and_the_Grunts_go_by_their_names()
    {
        var world = Prologue(new StoryOptions { IkeName = "Rusty", BobName = "Bolt" });
        var now = Readout(world);

        Assert.Equal(StoryReadout.LocatorName, now.HeroName);
        Assert.True(now.IsLocator);
        Assert.Equal(["Rusty", "Bolt"], now.Grunts.Select(g => g.Name));
        Assert.All(now.Grunts, g => Assert.Equal(1, g.Health));
        // A Grunt's own readout names the Locator L-7 too, never the host's name.
        var ike = Readout(world, 1);
        Assert.Equal("Rusty", ike.HeroName);
        Assert.False(ike.IsLocator);
        Assert.Equal(StoryReadout.LocatorName, ike.Grunts[0].Name);
        // Nothing is outside a story.
        Assert.Null(StoryReadout.Of(MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3), PlayerId.Of(Race.Red)));
    }

    [Fact]
    public void The_Grunts_cards_follow_their_orders_and_the_directors_marks()
    {
        var world = Prologue(new StoryOptions { Checkpoint = 1 });
        var run = world.Story!;
        // The computer's Grunts follow the Locator once they are awake (the first checkpoint).
        Assert.All(Readout(world).Grunts, g => Assert.Equal(GruntDoing.Following, g.Doing));

        run.Flags.Add(StoryMarks.Distracting(run.Seats[1]));
        run.Flags.Add(StoryMarks.Down(run.Seats[2]));
        var now = Readout(world);
        Assert.Equal(GruntDoing.Distracting, now.Grunts[0].Doing);
        Assert.Equal(GruntDoing.Down, now.Grunts[1].Doing);
        Assert.Equal(0, now.Grunts[1].Health);

        run.Flags.Add(StoryMarks.Dormant(run.Seats[1]));
        Assert.Equal(GruntDoing.Dormant, Readout(world).Grunts[0].Doing);
    }

    [Fact]
    public void The_objective_is_one_at_a_time_and_found_on_the_deck()
    {
        var world = Prologue();
        var run = world.Story!;
        Assert.Null(Readout(world).Objective);

        StoryMarks.SetObjective(run, "Get out of the bay", "terminal_bob");
        StoryMarks.SetObjective(run, "Reach maintenance deck A", "R4");
        Assert.Equal(("Reach maintenance deck A", "R4"), StoryMarks.Objective(run));
        Assert.Single(run.Flags, f => f.StartsWith("objective:", StringComparison.Ordinal));
        var now = Readout(world);
        Assert.Equal("Reach maintenance deck A", now.Objective);
        Assert.True(PrologueDeck.Zone("R4")!.Contains(now.ObjectivePlace!.Value));

        Assert.Equal(new Vector2(12.5f, 30), StoryMarks.Locate(PrologueDeck, "12.5,30"));
        Assert.Equal(PrologueDeck.Marker("hack_s").Position, StoryMarks.Locate(PrologueDeck, "hack_s"));
        Assert.Null(StoryMarks.Locate(PrologueDeck, "nowhere"));

        StoryMarks.SetObjective(run, null);
        Assert.Null(StoryMarks.Objective(run));
    }

    [Fact]
    public void The_way_to_the_objective_goes_through_the_doors_not_the_walls()
    {
        var world = Prologue();
        var deck = PrologueDeck;
        StoryMarks.SetObjective(world.Story!, "Reach maintenance deck A", "hack_s");
        var route = new DeckRoute(deck);
        var now = Readout(world, route: route);

        Assert.NotEmpty(now.Waypoints);
        Assert.True(now.Waypoints.Count <= DeckRoute.Longest);
        // Out of bay 7 its first waypoint is in sight of the Locator, and every leg after it is clear of walls.
        var from = world.HeroOf(world.Story!.Locator)!.Position;
        var points = now.Waypoints.Prepend(from).ToList();
        for (var i = 1; i < points.Count; i++)
            Assert.True(route.Clear(Cell(points[i - 1]), Cell(points[i])), $"leg {i} runs through a wall");
        // The way is longer than the straight line, which runs through the bay's walls.
        Assert.True(now.WayLength > Vector2.Distance(from, deck.Marker("hack_s").Position));
        Assert.True(now.ObjectiveDistance > 0);
    }

    private static (int X, int Y) Cell(Vector2 p) => ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

    [Fact]
    public void A_goal_in_sight_needs_no_waypoints_and_one_walled_in_has_no_way()
    {
        var route = new DeckRoute(PrologueDeck);
        var spawn = PrologueDeck.Marker("spawn_l7").Position;

        Assert.Empty(route.Chain(spawn, spawn + new Vector2(0, 3)));
        // The middle of solid wall far from any floor.
        Assert.Empty(route.Chain(spawn, new Vector2(2, 170)));
        Assert.Null(route.Steps(spawn));
        Assert.True(DeckRoute.Open(DeckCell.Door));
        Assert.False(DeckRoute.Open(DeckCell.Wall));
        Assert.False(DeckRoute.Open(DeckCell.Grate));
    }

    [Fact]
    public void The_alarm_and_the_countdown_come_from_the_runs_flags_and_timers()
    {
        var world = Prologue();
        var run = world.Story!;
        Assert.False(Readout(world).Alarm);
        Assert.Null(Readout(world).CountdownLeft);

        run.Flags.Add(StoryMarks.Alarm);
        run.Flags.Add(StoryMarks.Spotted);
        run.Timers[StoryMarks.CountdownFrom] = world.Time;
        run.Timers[StoryMarks.Countdown] = world.Time + 180;
        var now = Readout(world);
        Assert.True(now.Alarm);
        Assert.True(now.Spotted);
        Assert.Equal(180, now.CountdownLeft!.Value, 3);
        Assert.Equal(1, now.CountdownShare!.Value, 3);

        run.Timers[StoryMarks.Countdown] = world.Time - 1;
        Assert.Equal(0, Readout(world).CountdownLeft);
    }

    [Fact]
    public void The_Locator_in_its_clamp_may_hack_its_way_out_until_that_is_done()
    {
        var world = Prologue();
        var run = world.Story!;
        var now = Readout(world);
        Assert.Contains(now.Prompts, p => p is { Kind: StoryPromptKind.Hack, Target: StoryReadout.OwnClamp });
        // A Grunt hacks nothing.
        Assert.DoesNotContain(Readout(world, 1).Prompts, p => p.Kind == StoryPromptKind.Hack);

        run.Flags.Add(StoryMarks.Done(StoryReadout.OwnClamp));
        Assert.DoesNotContain(Readout(world).Prompts, p => p.Target == StoryReadout.OwnClamp);
    }

    [Fact]
    public void A_Grunt_down_beside_the_Locator_can_be_put_together()
    {
        var world = Prologue();
        var run = world.Story!;
        var locator = world.HeroOf(run.Locator)!;
        var ike = world.HeroOf(run.Seats[1])!;
        ike.Position = locator.Position + new Vector2(2, 0);
        run.Flags.Add(StoryMarks.Down(run.Seats[1]));

        var revive = Assert.Single(Readout(world).Prompts, p => p.Kind == StoryPromptKind.Revive);
        Assert.Equal("Ike", revive.Target);
        Assert.Equal(2, revive.Distance, 3);
        // The other Grunt cannot.
        Assert.DoesNotContain(Readout(world, 2).Prompts, p => p.Kind == StoryPromptKind.Revive);
    }

    [Fact]
    public void The_tracker_hears_the_ships_machines_that_move_within_its_reach_nearest_first()
    {
        var world = Prologue();
        var locator = world.HeroOf(world.Story!.Locator)!;
        Assert.Empty(Readout(world).Contacts);
        var near = world.AddMachine(Race.Blue, locator.Position + new Vector2(0, 8), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var far = world.AddMachine(Race.Blue, locator.Position + new Vector2(0, 30), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var beyond = world.AddMachine(Race.Blue, locator.Position + new Vector2(0, 60), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        // Standing still they are not heard.
        Assert.Empty(Readout(world).Contacts);

        foreach (var machine in new[] { far, near, beyond })
            world.DriveInput[machine.Id] = (Forward: 1f, Turn: 0f, Strafe: 0f, Slow: false);
        var contacts = Readout(world).Contacts;
        Assert.Equal([8f, 30f], contacts.Select(c => MathF.Round(c.Distance)));
        Assert.Equal(near.Position, contacts[0].Position);
    }

    [Fact]
    public void The_signature_rises_with_speed_and_a_shot()
    {
        var world = Prologue();
        var run = world.Story!;
        var locator = world.HeroOf(run.Locator)!;
        var still = StoryReadout.SignatureOf(world, run.Locator, locator);
        world.DriveInput[locator.Id] = (Forward: 1f, Turn: 0f, Strafe: 0f, Slow: true);
        var creeping = StoryReadout.SignatureOf(world, run.Locator, locator);
        world.DriveInput[locator.Id] = (Forward: 1f, Turn: 0f, Strafe: 0f, Slow: false);
        var driving = StoryReadout.SignatureOf(world, run.Locator, locator);

        Assert.True(still < creeping && creeping < driving, $"{still} {creeping} {driving}");
        Assert.InRange(still, 0, 0.1f);
    }

    // ---- subtitles ---------------------------------------------------------------------------------------

    [Fact]
    public void A_line_lasts_as_its_voice_or_by_its_length_and_waits_for_its_speakers_last()
    {
        var queue = new SubtitleQueue();
        Assert.Equal(SubtitleQueue.Least + SubtitleQueue.PerCharacter * 10, SubtitleQueue.Seconds("0123456789", null), 6);
        Assert.Equal(3 + SubtitleQueue.After, SubtitleQueue.Seconds("whatever", 3), 6);

        var first = queue.Add(StorySpeaker.Ike, "IKE", "Tell me that was just the ventilation.", 10, clip: 2);
        var second = queue.Add(StorySpeaker.Ike, "IKE", "Out of the way!", 10.5);
        var ship = queue.Add(StorySpeaker.Ship, "SHIP", "Found.", 10.5);
        Assert.Equal(first.Until, second.From);
        Assert.Equal(10.5, ship.From);

        var shown = queue.Shown(11);
        Assert.Equal([first, ship], shown.Select(s => s.Line));
        Assert.All(shown, s => Assert.InRange(s.Alpha, 0, 1));
        // Ike's first gives way to his second when it ends.
        Assert.Equal([second], queue.Shown(first.Until + 0.5).Where(s => s.Line.Speaker == StorySpeaker.Ike).Select(s => s.Line));
        Assert.False(queue.Shown(1000).Any());
        Assert.False(queue.Any);
    }

    [Fact]
    public void No_more_than_two_lines_show_at_once_and_the_newest_stay_once_the_oldest_was_read()
    {
        var queue = new SubtitleQueue();
        var one = queue.Add(StorySpeaker.Ship, null, "One.", 0);
        var two = queue.Add(StorySpeaker.Auto, null, "Two.", 0.1);
        queue.Add(StorySpeaker.Whisper, null, "Three.", 0.2);

        // The oldest has not been up long enough to read: the third waits.
        var shown = queue.Shown(0.5);
        Assert.Equal([one, two], shown.Select(s => s.Line));
        // Then the oldest gives way, and the third shows for as long as it would have.
        shown = queue.Shown(SubtitleQueue.Least + 0.1);
        Assert.Equal(SubtitleQueue.Most, shown.Count);
        Assert.Equal(two, shown[0].Line);
        var three = shown[^1].Line;
        Assert.Equal(StorySpeaker.Whisper, three.Speaker);
        Assert.Equal(SubtitleQueue.Least, three.From, 6);
        Assert.Equal(SubtitleQueue.Seconds("Three.", null), three.Until - three.From, 6);
    }

    /// <summary>
    /// As Erwischt ends the ship's last fragment, the automatic voice and Ike's bark all start within a frame: the automatic
    /// voice's line, the chapter's turn, was swept away the moment it came. Now the last to come waits.
    /// </summary>
    [Fact]
    public void Three_lines_starting_at_once_lose_none()
    {
        var queue = new SubtitleQueue();
        queue.Add(StorySpeaker.Ship, "SCHIFF", "Kurs- … Kurs- …", 30.57, clip: 1.11);
        var auto = queue.Add(StorySpeaker.Auto, "AUTOMATIK", "Steuerung verloren. Notabstieg eingeleitet.", 30.8, clip: 4.12);
        queue.Add(StorySpeaker.Ike, "IKE", "Das waren wir, oder? Das waren wir.", 31.0, clip: 3.32);
        var seen = new HashSet<StorySpeaker>();
        for (var t = 30.6; t < 40; t += 1 / 30.0)
        {
            var shown = queue.Shown(t);
            Assert.True(shown.Count <= SubtitleQueue.Most);
            foreach (var (line, _) in shown)
                seen.Add(line.Speaker);
            if (t >= auto.From && t < auto.Until - 0.1)
                Assert.Contains(shown, s => s.Line.Speaker == StorySpeaker.Auto);
        }
        Assert.Equal([StorySpeaker.Ship, StorySpeaker.Auto, StorySpeaker.Ike], seen.OrderBy(s => s));
    }

    [Fact]
    public void Every_speaker_of_the_line_table_is_known()
    {
        var table = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Repo("tools/voice/lines.json")));
        foreach (var line in table.RootElement.GetProperty("lines").EnumerateArray())
            Assert.NotNull(SubtitleQueue.Parse(line.GetProperty("speaker").GetString()));
    }

    // ---- German ------------------------------------------------------------------------------------------

    /// <summary>The story's screens: every English word they put through the translation is in de.po.</summary>
    private static readonly string[] StoryScreens =
        ["StoryHud.cs", "StoryHud.Boot.cs", "StoryHud.Demo.cs", "StoryPage.cs", "StoryProgress.cs", "StoryFilm.cs", "Subtitles.cs"];

    [Fact]
    public void Every_word_of_the_storys_screens_and_every_line_of_the_table_has_its_German()
    {
        var known = Msgids();
        var missing = new List<string>();
        foreach (var file in StoryScreens)
        {
            var code = File.ReadAllText(Repo("game/scripts/" + file));
            foreach (Match match in Regex.Matches(code, @"(?:Lang\.T|Button|Heading|Setting)\(""((?:[^""\\]|\\.)*)"""))
                if (!known.Contains(Regex.Unescape(match.Groups[1].Value)))
                    missing.Add($"{file}: {match.Groups[1].Value}");
        }
        foreach (var word in (string[])["Following", "Waiting", "On the way", "Distracting", "Knocked down", "Switched off", "Standing", "Follow", "Wait", "Distract",
                     "Ship", "Automatic", "L-7 BOOT", "SENSORS {0} %", "LAMP OK", "Subtitles", "Soften scares", "Keys of the story", "Story"])
            if (!known.Contains(word))
                missing.Add(word);
        var table = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Repo("tools/voice/lines.json")));
        foreach (var line in table.RootElement.GetProperty("lines").EnumerateArray())
            if (!known.Contains(line.GetProperty("en").GetString()!))
                missing.Add(line.GetProperty("id").GetString()!);
        // The story's keys, by the names KeyMap gives them (KeyMap is Godot's: read from its source).
        var keys = Regex.Matches(File.ReadAllText(Repo("game/scripts/KeyMap.cs")), @"new\(""([^""]+)"", Key\.\w+, [^)]*Where\.Story\)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(6, keys.Count);
        missing.AddRange(keys.Where(key => !known.Contains(key)));
        Assert.Empty(missing);
    }

    [Fact]
    public void The_storys_section_of_de_po_has_no_msgid_twice()
    {
        var po = File.ReadAllText(Repo("game/lang/de.po"));
        var start = po.IndexOf("# Story: prologue", StringComparison.Ordinal);
        Assert.True(start > 0);
        var before = Msgids(po[..start]);
        var ids = Regex.Matches(po[start..], @"^msgid ""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Where(id => id.Length > 0).ToList();
        Assert.Empty(ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.DoesNotContain(ids, id => before.Contains(Regex.Unescape(id)));
    }

    private static HashSet<string> Msgids() => Msgids(File.ReadAllText(Repo("game/lang/de.po")));

    private static HashSet<string> Msgids(string po)
    {
        return Regex.Matches(po, @"^msgid ""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline).Select(m => Regex.Unescape(m.Groups[1].Value)).ToHashSet();
    }

    private static string Repo(string path) => Path.Combine(RepoPaths.OriginalDataFolder(), "..", "..", path);
}
