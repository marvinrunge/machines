using System.Numerics;
using System.Text.RegularExpressions;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The prologue's script and its director (plan WP-C): each checkpoint's set-up gives the world the chapter has there; a
/// run of scripted inputs (the Locator set down where it must go, the hacks played by the board's own solution through the
/// lockstep commands) passes every section's triggers from any checkpoint to the escape (the core fight itself is
/// PrologueCoreTests'); the same inputs give the same checksum; and
/// every line the director says is in the line table the subtitles read, every objective in de.po.
/// </summary>
public class PrologueScriptTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld Chapter(int checkpoint = 0, int seed = 3) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });

    private static string Repo(string path) => Path.Combine(RepoPaths.OriginalDataFolder(), "..", "..", path);

    private static int Count(GameWorld world, string role) => world.Story!.Director.Of(world, role).Count();

    // ---- the checkpoints -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Each_checkpoint_sets_the_world_up_as_the_chapter_has_it_there(int checkpoint)
    {
        var world = Chapter(checkpoint);
        var run = world.Story!;
        var director = run.Director;

        Assert.Equal((checkpoint, Prologue.SectionAt[checkpoint]), (run.Checkpoint, run.Section));
        // The trio where it stands there, each on ground its room lets it stand on; Ike and Bob asleep only at the start.
        var (at, facing) = Prologue.Starts[checkpoint];
        for (var seat = 0; seat < run.Seats.Count; seat++)
        {
            var hero = world.HeroOf(run.Seats[seat])!;
            Assert.Equal(at[seat], hero.Position);
            Assert.Equal(facing * MathF.PI / 180, hero.Facing, 4);
            Assert.True(world.Nav.IsFree(hero.Position, GameWorld.MobilityOf(hero)), $"seat {seat} at {hero.Position} is not free ground");
            Assert.Equal(checkpoint == 0 && seat > 0, run.Flags.Contains(StoryMarks.Dormant(run.Seats[seat])));
            Assert.Equal(checkpoint == 0 && seat > 0, director.Held.Contains(hero.Id));
            if (checkpoint > 0 && seat > 0)
                Assert.Equal(new FollowOrder(world.HeroOf(run.Locator)!.Id), hero.Order);
        }
        // The hacks done and the doors open so far; the rest shut.
        foreach (var (target, from) in Prologue.Hacks)
            Assert.Equal(checkpoint >= from, run.Hacks.Done.Contains(target));
        Assert.Equal(checkpoint < 4, run.Hacks.Locked.Contains("hack_d5"));
        Assert.Equal(checkpoint < 6, run.Hacks.Locked.Contains("pod_3_hatch"));
        foreach (var letter in "ABCDEHIJKMQRTUV")
            Assert.True(Prologue.DoorsOpen[checkpoint].Contains(letter) != world.DoorClosed(letter), $"door {letter}");
        Assert.All(Prologue.DoorsAuto[checkpoint], letter => Assert.Contains(letter, director.AutoDoors));
        // The ship's machines that exist there.
        Assert.Equal(19, Count(world, Prologue.RowRole) + Count(world, Prologue.ScareRole));
        Assert.All(director.Of(world, Prologue.RowRole), m => Assert.Empty(m.Weapons));
        Assert.Equal(checkpoint >= 1 ? 1 : 0, Count(world, Prologue.ThresherRole));
        Assert.Equal(checkpoint is 3 or 4 ? 3 : 0, Count(world, Prologue.ReaperRole));
        Assert.Equal(checkpoint is 3 or 4 ? 1 : 0, Count(world, Prologue.CommanderRole));
        Assert.Equal(checkpoint == 4 ? 3 : 0, Count(world, Prologue.HostileRole));
        // The workshop's Grunts armed and painted as the trio's own; the Locator wears the Bolter once it was taken.
        Assert.All(director.Of(world, Prologue.HostileRole), m => Assert.Equal((Race?)Race.Red, m.DisplayAs));
        Assert.All(director.Of(world, Prologue.HostileRole), m => Assert.NotEmpty(m.Weapons));
        Assert.Equal(checkpoint >= 5, world.HeroOf(run.Locator)!.Weapons.Count > 0);
        Assert.Equal(checkpoint >= 5, run.Flags.Contains(Prologue.Sealed));
        Assert.All(world.Machines.Where(m => run.Seats.All(s => world.HeroOf(s)?.Id != m.Id)), m => Assert.Equal(Race.Blue, m.Race));
        // From the break-in the alarm is on; after Erwischt the core is gone and the lights at emergency.
        Assert.Equal(checkpoint < 4, world.Artefacts.Any(a => a.Tag == Prologue.CoreTag && !a.IsDead));
        Assert.Equal(checkpoint >= 3, run.Flags.Contains(StoryMarks.Alarm));
        Assert.Equal(checkpoint >= 4 ? "emergency" : "on", world.StoryLightState("core"));
        Assert.Equal(checkpoint >= 5, run.Flags.Contains(StoryMarks.Done("pickup_bolter")));
        if (checkpoint == Prologue.LastCheckpoint)
            Assert.Equal(Prologue.CountdownAtCheckpoint, run.Timers[StoryMarks.Countdown] - world.Time, 2);
        else
            Assert.DoesNotContain(StoryMarks.Countdown, run.Timers.Keys);
        // The beats of the stretches before the checkpoint count as fired; none after.
        Assert.All(Prologue.Script.Where(b => !b.Repeat), b => Assert.Equal(b.Span < checkpoint, director.Has(b.Name)));
        Assert.Equal(checkpoint > 0, StoryMarks.Objective(run) is not null);

        // It plays on from there.
        for (var i = 0; i < 2 * GameWorld.TicksPerSecond; i++)
            world.Step();
        Assert.False(run.Over);
        Assert.NotNull(StoryMarks.Objective(run));
    }

    [Fact]
    public void At_the_start_the_locator_hangs_in_its_clamp_until_it_hacks_itself_free()
    {
        var world = Chapter();
        var run = world.Story!;
        var locator = world.HeroOf(run.Locator)!;
        world.Step();

        Assert.True(world.HeldByStory(locator.Id));
        Assert.Contains(StoryMarks.Boot, run.Flags);
        Assert.Equal(("Free yourself from the clamp", (string?)"clamp_l7"), StoryMarks.Objective(run));
        var rider = new Rider(world);
        rider.Hack("clamp_l7");

        Assert.False(world.HeldByStory(locator.Id));
        Assert.False(world.DoorClosed('A'));
        Assert.Equal("terminal_ike", StoryMarks.Objective(run)!.Value.Place);
    }

    // ---- a run of the chapter ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void Scripted_inputs_move_through_every_section_to_the_escape(int checkpoint)
    {
        var world = Chapter(checkpoint);
        var rider = new Rider(world);

        rider.Play();

        var run = world.Story!;
        Assert.Equal(StoryEnding.Escaped, run.Ending);
        Assert.Equal(Race.Red, world.Winner);
        Assert.Equal(Prologue.LastCheckpoint, run.Checkpoint);
        // Every section from the checkpoint's on, in order, and the checkpoints after it.
        Assert.Equal(Enumerable.Range(Prologue.SectionAt[checkpoint] + 1, 8 - Prologue.SectionAt[checkpoint]), rider.Sections.Select(s => s.Section));
        Assert.Equal(Enumerable.Range(checkpoint + 1, Prologue.LastCheckpoint - checkpoint), rider.Checkpoints);
        Assert.All(run.Seats, seat => Assert.NotNull(world.HeroOf(seat)));
    }

    [Fact]
    public void The_same_inputs_give_the_same_run_and_checksum()
    {
        (ulong Checksum, int Tick, string Sections) Play()
        {
            var world = Chapter(seed: 17);
            var rider = new Rider(world);
            rider.Play();
            return (world.Checksum(), world.Tick, string.Join(" ", rider.Sections));
        }

        var (a, b) = (Play(), Play());
        Assert.Equal(a, b);
    }

    [Fact]
    public void The_director_keeps_out_of_every_match_but_a_story()
    {
        Assert.Empty(StoryScripts.Of("no such chapter"));
        Assert.Same(Prologue.Script, StoryScripts.Of(StoryOptions.Prologue));
    }

    // ---- lines and words ---------------------------------------------------------------------------------

    /// <summary>Every line id the script says.</summary>
    private static IEnumerable<string> ScriptLines() => Prologue.Script.SelectMany(b => b.Then).SelectMany(a => a switch
    {
        Then.Say say => [say.Line],
        Then.SayToSeat say => [say.Line],
        Then.SayNext next => next.Lines,
        _ => (IEnumerable<string>)[],
    });

    [Fact]
    public void Every_line_the_director_says_is_in_the_line_table_and_the_table_is_made_from_the_voice_lines()
    {
        var table = StoryLines.Parse(File.ReadAllText(Repo("game/assets/story/prologue.lines.json")));
        Assert.All(ScriptLines(), id => Assert.True(table.Has(id), id));

        // The game's table is tools/voice/lines.json's, as tools/voice/runtime_lines.py makes it.
        var source = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Repo("tools/voice/lines.json")));
        var lines = source.RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(lines.Select(l => l.GetProperty("id").GetString()), table.All.Select(l => l.Id));
        foreach (var line in lines)
        {
            var made = table.Find(line.GetProperty("id").GetString()!)!;
            Assert.Equal((line.GetProperty("speaker").GetString(), line.GetProperty("en").GetString()), (made.Speaker, made.English));
            Assert.Equal(made.Speaker switch { "ship" or "auto" => StoryLineChannel.Pa, "ike" or "bob" => StoryLineChannel.Radio, _ => StoryLineChannel.Thought }, made.Channel);
        }
        Assert.Throws<FormatException>(() => StoryLines.Parse("{\"lines\": [{\"id\": \"X\"}]}"));
    }

    [Fact]
    public void Every_objective_of_the_script_has_its_german()
    {
        var po = File.ReadAllText(Repo("game/lang/de.po"));
        var known = Regex.Matches(po, @"^msgid ""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline).Select(m => Regex.Unescape(m.Groups[1].Value)).ToHashSet();
        var code = File.ReadAllText(Repo("sim/Machines.Sim/Story/Prologue.cs"));
        var objectives = Regex.Matches(code, @"Objective\(""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(objectives.Count >= 15);
        Assert.All(objectives, text => Assert.Contains(text, known));
    }

    // ---- the rider ------------------------------------------------------------------------------------

    /// <summary>
    /// Plays the chapter by scripted inputs: the Locator (and, where all three must be, the Grunts) set down where the next
    /// leg ends, as the tests set heroes down; the hacks played by the board's own solution, a command a tick. Which leg is
    /// next it reads from the world, as the game's StoryDriver does.
    /// </summary>
    private sealed class Rider(GameWorld world)
    {
        private StoryRun Run => world.Story!;

        public List<(int Section, int Tick)> Sections { get; } = [];

        public List<int> Checkpoints { get; } = [];

        /// <summary>The director's cues for the view, in order.</summary>
        public List<string> Cues { get; } = [];

        private Machine Locator => world.HeroOf(Run.Locator)!;

        public void Step()
        {
            var (section, checkpoint) = (Run.Section, Run.Checkpoint);
            world.Step();
            Cues.AddRange(world.Events.Where(e => e.Kind == GameEventKind.StoryCue).Select(e => e.Detail!));
            if (Run.Section != section)
                Sections.Add((Run.Section, world.Tick));
            if (Run.Checkpoint != checkpoint)
                Checkpoints.Add(Run.Checkpoint);
        }

        public void Until(Func<bool> done, float seconds)
        {
            for (var i = 0; i < seconds * GameWorld.TicksPerSecond && !done(); i++)
                Step();
            Assert.True(done(), $"not done in {seconds} s (section {Run.Section}, tick {world.Tick})");
        }

        private string? Do(Command command)
        {
            world.Issue(command);
            Step();
            return world.Rejected.FirstOrDefault(r => ReferenceEquals(r.Command, command))?.Reason;
        }

        /// <summary>The Locator driven as a player drives it in first person.</summary>
        public void Drive(float forward, float turn) =>
            world.Issue(new DriveCommand(Race.Red, Locator.Id, Driving: true, Forward: forward, Turn: turn));

        /// <summary>A hero set down on free ground for it nearest a point.</summary>
        private void SetDown(Machine hero, Vector2 at)
        {
            var mobility = GameWorld.MobilityOf(hero);
            for (var r = 0f; r <= 6; r += 0.5f)
            {
                for (var a = 0; a < 16; a++)
                {
                    var spot = at + r * new Vector2(MathF.Cos(a * MathF.PI / 8), MathF.Sin(a * MathF.PI / 8));
                    if (world.Nav.IsFree(spot, mobility))
                    {
                        hero.Position = spot;
                        return;
                    }
                }
            }
            hero.Position = at;
        }

        private void Go(Vector2 at)
        {
            SetDown(Locator, at);
            Step();
        }

        private void All(string zone)
        {
            var area = world.Deck!.Zones.First(z => z.Name == zone);
            var middle = (area.Min + area.Max) / 2;
            for (var i = 0; i < Run.Seats.Count; i++)
            {
                if (world.HeroOf(Run.Seats[i]) is { } hero)
                    SetDown(hero, middle + new Vector2((i - 1) * 2.5f, 0));
            }
            Step();
        }

        private Vector2 Zone(string name) => world.Deck!.Zones.First(z => z.Name == name) is var z ? (z.Min + z.Max) / 2 : default;

        /// <summary>To a hack's target, plugged in, the board's own solution turn by turn until it is won or broken off.</summary>
        public void Hack(string target)
        {
            Locator.Position = world.Deck!.Marker(target).Position;
            Assert.Null(Do(new StoryHackOpenCommand(Race.Red, target)));
            var session = Run.Hacks.Session(target)!;
            for (var turns = 0; turns < 3000 && session.Open && !session.Broken; turns++)
            {
                var next = session.Puzzle!.SolutionTurns().Take(1).ToList();
                // The core locked its last board: the Locator waits at it until the Reapers are in.
                if (next.Count == 0 || session.Held)
                {
                    Step();
                    continue;
                }
                Do(new StoryHackTurnCommand(Race.Red, target, next[0].Cell, next[0].Clockwise));
                // Traced (the firewall ran full): plugged in again.
                if (!session.Open && !Run.Hacks.Done.Contains(target))
                    Assert.Null(Do(new StoryHackOpenCommand(Race.Red, target)));
            }
            Assert.True(Run.Hacks.Done.Contains(target) || session.Broken, $"hack {target} not won");
            Step();
        }

        /// <summary>To a weapon's rack, used (the use key's command).</summary>
        public void TakeUp(string rack)
        {
            Go(world.Deck!.Marker(rack).Position + new Vector2(-1.5f, -1.5f));
            Assert.Null(Do(new StoryUseCommand(Race.Red, rack)));
        }

        /// <summary>To a marker (the pod's hatch), used.</summary>
        public void Use(string marker)
        {
            Go(world.Deck!.Marker(marker).Position + new Vector2(0, 1.5f));
            Assert.Null(Do(new StoryUseCommand(Race.Red, marker)));
        }

        /// <summary>
        /// The workshop's armed Grunts shot at, the nearest first, with the trigger held on it as a player aims (the Grunts
        /// fight along), until none is left standing.
        /// </summary>
        public void Fight()
        {
            var hostiles = Run.Director.Of(world, Prologue.HostileRole);
            for (var i = 0; i < 60 * GameWorld.TicksPerSecond && !Run.Over && hostiles.Any(m => !m.IsDead); i++)
            {
                var foe = hostiles.Where(m => !m.IsDead).OrderBy(m => Vector2.DistanceSquared(m.Position, Locator.Position)).ThenBy(m => m.Id).First();
                world.Issue(new DriveCommand(Race.Red, Locator.Id, Driving: true, Aim: foe.Position));
                Step();
            }
            // A Locator lost in the firefight ends the chapter (StoryEnding.LocatorLost): nothing left to drive.
            if (world.HeroOf(Run.Locator) is { } locator)
                world.Issue(new DriveCommand(Race.Red, locator.Id, Driving: true));
            Step();
        }

        /// <summary>The chapter from where it stands to its end, leg by leg.</summary>
        public void Play()
        {
            var director = Run.Director;
            bool Done(string target) => Run.Hacks.Done.Contains(target);
            for (var legs = 0; legs < 60 && !Run.Over; legs++)
            {
                if (!Done("clamp_l7"))
                    Hack("clamp_l7");
                else if (!Done("terminal_ike"))
                    Hack("terminal_ike");
                else if (!Done("terminal_bob"))
                    Hack("terminal_bob");
                else if (!director.Has("deck_a"))
                    Go(Zone("R4"));
                else if (!Done("hack_s"))
                    Hack("hack_s");
                else if (!Done("hack_h"))
                    Hack("hack_h");
                else if (!director.Has("antechamber"))
                    Go(Zone("R8"));
                else if (!director.Has("break_in") && !Done("hack_core"))
                    Hack("hack_core");
                else if (!director.Has("break_in"))
                    Until(() => director.Has("break_in"), Prologue.ShootMax + 10);
                else if (!director.Has("core_blows"))
                    Until(() => director.Has("core_blows"), Prologue.CoreBlows + 1);
                else if (!director.Has("fled"))
                    Go(Zone("V2"));
                else if (Run.Hacks.Locked.Contains("hack_d5"))
                    Go(Zone("R12"));
                else if (!Done("hack_d5"))
                    Hack("hack_d5");
                else if (!director.Has("together"))
                    All("R12");
                else if (!director.Has("sealed"))
                    All("R13");
                else if (!director.Has("weapon"))
                    TakeUp("pickup_bolter");
                else if (!director.Has("cleared"))
                    Fight();
                else if (!director.Has("repaired"))
                    Go(Zone("repair"));
                else if (!director.Has("notgang"))
                    Go(new Vector2(520, 232));
                else if (!Done("pod_3_hatch"))
                    Hack("pod_3_hatch");
                else if (!Run.Flags.Contains(StoryMarks.Done("pod_3_hatch")))
                    Use("pod_3_hatch");
                else if (!director.Has("launch"))
                    All("pod_zone");
                else
                    Until(() => Run.Over, Prologue.Away + 1);
            }
            Assert.True(Run.Over, $"the chapter did not end (section {Run.Section})");
        }
    }
}
