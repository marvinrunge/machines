using System.Text.RegularExpressions;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The prologue's opening (Erwachen): the Locator boots in its clamp for about a hundred seconds, system after system with
/// its thoughts between, on the world's ticks; the clamp's hack opens only at its end; the Locator's player may skip it by a
/// Use command (every peer the same tick), a Grunt's not; the thoughts are for the Locator's screen and anyone looking on,
/// never a Grunt's player's; every checkpoint after the start has no boot; with the manifest's voices no thought runs into
/// the next; and no line of the prologue names the builders (the book's canon: "Mensch" is never said in the prologue).
/// </summary>
public class PrologueBootTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly PlayerId Locator = new(0), Ike = new(4), Bob = new(5);

    private static GameWorld Chapter(int checkpoint = 0) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 3,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    /// <summary>The prologue from its start with Ike a person's (co-op), Bob the computer's.</summary>
    private static GameWorld Coop() =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue, new PlayerTable([
            new PlayerSeat(Locator, Race.Red, PlayerKind.Human, "Host"),
            new PlayerSeat(Ike, Race.Red, PlayerKind.Human, "Anna"),
            new PlayerSeat(Bob, Race.Red, PlayerKind.Bot, "Bolt"),
            new PlayerSeat(PlayerId.Of(Race.Blue), Race.Blue, PlayerKind.Ai, "Ship")]), 5,
            new MatchOptions { Story = new StoryOptions { IkeName = "Anna", BobName = "Bolt" }, FogOfWar = false });

    private static string Repo(string path) => Path.Combine(RepoPaths.OriginalDataFolder(), "..", "..", path);

    /// <summary>What the director told on the ticks stepped: cues and lines, each with the seconds into the boot.</summary>
    private sealed class Log(GameWorld world)
    {
        public List<(float At, string Cue)> Cues { get; } = [];
        public List<(float At, string Line, StoryAudience Audience, int Seat)> Lines { get; } = [];

        private float Boot => world.Story!.Director.Fired.GetValueOrDefault("boot");

        public void Step(int ticks = 1)
        {
            for (var i = 0; i < ticks; i++)
            {
                world.Step();
                foreach (var e in world.Events)
                {
                    if (e.Kind == GameEventKind.StoryCue)
                        Cues.Add((world.Time - Boot, e.Detail!));
                    else if (StoryEvents.LineOf(world, e) is { } line)
                        Lines.Add((world.Time - Boot, line.Id, line.Audience, line.Seat));
                }
            }
        }

        public string? Do(Command command)
        {
            world.Issue(command);
            Step();
            return world.Rejected.FirstOrDefault(r => ReferenceEquals(r.Command, command))?.Reason;
        }
    }

    private const float Tick = 1f / GameWorld.TicksPerSecond;

    [Fact]
    public void The_boot_brings_its_steps_and_thoughts_in_order_at_their_times_and_only_then_the_clamp()
    {
        var world = Chapter();
        var run = world.Story!;
        var log = new Log(world);
        log.Step();
        Assert.Contains(StoryMarks.Boot, run.Flags);
        var ticks = (int)((Prologue.BootLength + 1) * GameWorld.TicksPerSecond);
        for (var i = 0; i < ticks && !run.Director.Has("boot_done"); i++)
        {
            Assert.Contains("clamp_l7", run.Hacks.Locked);
            log.Step();
        }
        Assert.True(run.Director.Has("boot_done"));
        Assert.DoesNotContain("clamp_l7", run.Hacks.Locked);
        Assert.DoesNotContain(StoryMarks.Boot, run.Flags);

        // Every step's cue at its time, in the table's order, then the end.
        var steps = log.Cues.Where(c => c.Cue.StartsWith("boot ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["boot dark", .. Prologue.BootSteps.Select(s => "boot " + s.Step), "boot done"], steps.Select(c => c.Cue));
        foreach (var (at, step) in Prologue.BootSteps)
            Assert.InRange(steps.First(c => c.Cue == "boot " + step).At, at, at + Tick + 0.001f);
        Assert.InRange(steps[^1].At, Prologue.BootLength, Prologue.BootLength + Tick + 0.001f);

        // The thoughts at their times, the Locator's inner voice; the ship's call and the decryption between them.
        var thoughts = log.Lines.Where(l => l.Line.StartsWith("P_L7_", StringComparison.Ordinal)).ToList();
        Assert.Equal(Prologue.BootThoughts.Select(t => t.Line), thoughts.Select(t => t.Line));
        Assert.All(thoughts, t => Assert.Equal((StoryAudience.Inner, 0), (t.Audience, t.Seat)));
        for (var i = 0; i < thoughts.Count; i++)
            Assert.InRange(thoughts[i].At, Prologue.BootThoughts[i].At, Prologue.BootThoughts[i].At + Tick + 0.001f);
        Assert.InRange(log.Lines.Single(l => l.Line == Prologue.Lines.Wake1).At, Prologue.WakeCallAt, Prologue.WakeCallAt + Tick + 0.001f);
        Assert.InRange(log.Lines.Single(l => l.Line == Prologue.Lines.HudDecrypt).At, Prologue.DecryptAt, Prologue.DecryptAt + Tick + 0.001f);
        // The objective and the lamp's hint come with the end; the ship's round of announcements only well after it.
        Assert.Equal("clamp_l7", StoryMarks.Objective(run)!.Value.Place);
        Assert.Contains(log.Cues, c => c.Cue == "hint lamp" && c.At >= Prologue.BootLength);
        Assert.DoesNotContain(log.Lines, l => Prologue.Announcements.Contains(l.Line));

        // Free of the clamp: the last thought of the opening.
        var hero = world.HeroOf(run.Locator)!;
        hero.Position = world.Deck!.Marker("clamp_l7").Position;
        Assert.Null(log.Do(new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        var session = run.Hacks.Session("clamp_l7")!;
        for (var turns = 0; turns < 500 && !run.Hacks.Done.Contains("clamp_l7"); turns++)
        {
            var next = session.Puzzle!.SolutionTurns().First();
            log.Do(new StoryHackTurnCommand(Race.Red, "clamp_l7", next.Cell, next.Clockwise));
        }
        log.Step();
        Assert.Equal(Prologue.Lines.L7Legs, log.Lines.Last(l => l.Line.StartsWith("P_L7_", StringComparison.Ordinal)).Line);
    }

    [Fact]
    public void The_locators_player_skips_the_boot_and_the_call_the_decryption_and_the_objective_still_come()
    {
        var world = Chapter();
        var run = world.Story!;
        var log = new Log(world);
        log.Step(10 * GameWorld.TicksPerSecond);
        Assert.Contains(log.Lines, l => l.Line == Prologue.Lines.L7Hum);

        // A Grunt cannot skip it.
        Assert.Equal("only the Locator skips its boot", log.Do(new StoryUseCommand(Race.Red, StoryMarks.SkipBoot) { Issuer = run.Seats[1] }));
        Assert.Contains(StoryMarks.Boot, run.Flags);

        var before = log.Lines.Count;
        Assert.Null(log.Do(new StoryUseCommand(Race.Red, StoryMarks.SkipBoot)));
        // On the same tick: the boot over, the clamp open, the call and the decryption said, the objective set.
        Assert.True(run.Director.Has("boot_done"));
        Assert.Contains(StoryMarks.BootSkip, run.Flags);
        Assert.DoesNotContain(StoryMarks.Boot, run.Flags);
        Assert.DoesNotContain("clamp_l7", run.Hacks.Locked);
        Assert.Equal([Prologue.Lines.Wake1, Prologue.Lines.HudDecrypt], log.Lines.Skip(before).Select(l => l.Line));
        Assert.Equal("boot done", log.Cues.Last(c => c.Cue.StartsWith("boot ", StringComparison.Ordinal)).Cue);
        Assert.Equal("clamp_l7", StoryMarks.Objective(run)!.Value.Place);
        Assert.Equal("no boot to skip", log.Do(new StoryUseCommand(Race.Red, StoryMarks.SkipBoot)));

        // Nothing of the boot comes after.
        log.Step((int)(Prologue.BootLength * GameWorld.TicksPerSecond));
        Assert.DoesNotContain(log.Lines.Skip(before), l => l.Line.StartsWith("P_L7_", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Cues, c => c.At > 11 && c.Cue.StartsWith("boot ", StringComparison.Ordinal) && c.Cue != "boot done");
    }

    [Fact]
    public void In_coop_the_grunts_player_cannot_skip_and_never_hears_the_locators_thoughts()
    {
        var world = Coop();
        var run = world.Story!;
        var log = new Log(world);
        log.Step();
        Assert.Equal("only the Locator skips its boot", log.Do(new StoryUseCommand(Race.Red, StoryMarks.SkipBoot) { Issuer = Ike }));

        Assert.True(StoryAudiences.Shows(world, StoryAudience.Inner, 0, Locator));
        Assert.False(StoryAudiences.Shows(world, StoryAudience.Inner, 0, Ike));
        Assert.False(StoryAudiences.Shows(world, StoryAudience.Inner, 0, Bob));
        // A replay or someone looking on, with no seat of the story.
        Assert.True(StoryAudiences.Shows(world, StoryAudience.Inner, 0, new PlayerId(9)));
        Assert.Null(log.Do(new StoryUseCommand(Race.Red, StoryMarks.SkipBoot) { Issuer = Locator }));
        Assert.True(run.Director.Has("boot_done"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Every_checkpoint_after_the_start_has_no_boot(int checkpoint)
    {
        var world = Chapter(checkpoint);
        var run = world.Story!;
        var log = new Log(world);
        log.Step(3 * GameWorld.TicksPerSecond);
        Assert.True(run.Director.Has("boot_done"));
        Assert.DoesNotContain(StoryMarks.Boot, run.Flags);
        Assert.DoesNotContain(log.Cues, c => c.Cue.StartsWith("boot ", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Line.StartsWith("P_L7_", StringComparison.Ordinal) || l.Line == Prologue.Lines.Wake1);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void With_the_manifests_voices_no_thought_runs_into_the_next(string lang)
    {
        var voices = StoryVoiceManifest.Parse(File.ReadAllText(Repo("game/assets/story/voice/manifest.json")));
        var said = Prologue.BootThoughts.Select(t => (At: t.At, Line: t.Line)).Append((At: Prologue.WakeCallAt, Line: Prologue.Lines.Wake1)).OrderBy(l => l.At).ToList();
        var queue = new StoryLineQueue();
        var started = new Dictionary<string, double>();
        var ended = new Dictionary<string, double>();
        var next = 0;
        for (var now = 0.0; now < Prologue.BootLength + 15; now += 0.05)
        {
            var events = new List<StoryQueueEvent>();
            while (next < said.Count && said[next].At <= now)
            {
                var (_, id) = said[next++];
                var file = voices.Find(id, lang);
                Assert.True(file is { Seconds: > 0 }, $"{id} has no {lang} voice");
                var preset = voices.Preset(id);
                events.AddRange(queue.Add(new StoryQueuedLine(id, StorySoundscape.Channel(id, preset), file!.Seconds, now)
                    { Gong = StorySoundscape.Gong(id, preset) }, now));
            }
            events.AddRange(queue.Update(now));
            foreach (var e in events)
            {
                Assert.NotEqual(StoryQueueChange.Dropped, e.Change);
                Assert.NotEqual(StoryQueueChange.Interrupted, e.Change);
                if (e.Change == StoryQueueChange.Started)
                    started[e.Line.Id] = now;
                else if (e.Change == StoryQueueChange.Ended)
                    ended[e.Line.Id] = now;
            }
        }
        // Each starts when it is said (within a little; the gong goes before the call), and the last is over by the boot's end.
        foreach (var (at, id) in said)
            Assert.True(started[id] - at <= 0.75, $"{lang} {id} waited {started[id] - at:0.00} s for the line before it");
        Assert.True(ended.Values.Max() <= Prologue.BootLength + 0.5, $"{lang}: the last thought ends at {ended.Values.Max():0.0} s");
    }

    /// <summary>Whether a text names the builders: "Mensch", "Menschen", "Menschheit", "human(s)", "humanity".</summary>
    private static bool NamesThem(string text) => Regex.IsMatch(text, @"mensch|\bhuman", RegexOptions.IgnoreCase);

    [Fact]
    public void No_line_text_or_objective_of_the_prologue_names_the_builders()
    {
        // The lines, both languages, and the game's line table.
        var source = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Repo("tools/voice/lines.json")));
        foreach (var line in source.RootElement.GetProperty("lines").EnumerateArray())
            foreach (var lang in new[] { "de", "en" })
                Assert.False(NamesThem(line.GetProperty(lang).GetString()!), $"{line.GetProperty("id").GetString()} ({lang})");
        Assert.False(NamesThem(File.ReadAllText(Repo("game/assets/story/prologue.lines.json"))));
        // The story's block of de.po (the HUD, the menus, the objectives, the subtitles), both sides.
        var po = File.ReadAllText(Repo("game/lang/de.po"));
        var story = po[po.IndexOf("# Story: prologue", StringComparison.Ordinal)..];
        Assert.DoesNotContain(Regex.Matches(story, @"^msg(?:id|str) ""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value), NamesThem);
        // The objectives of the script and the HUD's own words.
        Assert.False(NamesThem(string.Join("\n", Regex.Matches(File.ReadAllText(Repo("sim/Machines.Sim/Story/Prologue.cs")), @"""([^""]*)""").Select(m => m.Groups[1].Value))));
        foreach (var file in Directory.GetFiles(Repo("game/scripts"), "StoryHud*.cs"))
            Assert.False(NamesThem(string.Join("\n", Regex.Matches(File.ReadAllText(file), @"""([^""]*)""").Select(m => m.Groups[1].Value))), file);
    }

    [Fact]
    public void The_boots_hud_words_have_their_german()
    {
        var po = File.ReadAllText(Repo("game/lang/de.po"));
        var known = Regex.Matches(po, @"^msgid ""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline).Select(m => Regex.Unescape(m.Groups[1].Value)).ToHashSet();
        var code = File.ReadAllText(Repo("game/scripts/StoryHud.Boot.cs"));
        var words = Regex.Matches(code, @"\(""\w+"", [\d.]+f?, ""([^""]+)"", (?:""([^""]+)""|null)").SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value })
            .Where(w => w.Length > 0 && w != "--").Distinct().ToList();
        Assert.True(words.Count >= 30, $"found {words.Count}");
        Assert.All(words, word => Assert.Contains(word, known));
        Assert.Contains("Hold Esc to skip the boot", known);
        Assert.Contains("{0} · STANDBY · UPDATE PENDING {1} %", known);
    }
}
