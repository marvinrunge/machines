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
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Each_checkpoint_sets_the_world_up_as_the_chapter_has_it_there(int checkpoint)
    {
        var world = Chapter(checkpoint);
        var run = world.Story!;
        var director = run.Director;
        // A checkpoint whose stretch is not built yet is set up as the nearest built one before it.
        var cp = Prologue.SetUpAs(checkpoint);

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
            Assert.Equal(cp >= from, run.Hacks.Done.Contains(target));
        Assert.Equal(cp < 8, run.Hacks.Locked.Contains("hack_d5"));
        Assert.Equal(cp < 12, run.Hacks.Locked.Contains("pod_3_hatch"));
        foreach (var letter in "ABCDEHIJKMNQRTUV")
            Assert.True(Prologue.DoorsOpen[checkpoint].Contains(letter) != world.DoorClosed(letter), $"door {letter}");
        Assert.All(Prologue.DoorsAuto[checkpoint], letter => Assert.Contains(letter, director.AutoDoors));
        // The ship's machines that exist there.
        // Stage 7's rows leave the gaps the Grunts cross by empty (design-stealth §2.1, §2.3).
        Assert.Equal(15, Count(world, Prologue.RowRole) + Count(world, Prologue.ScareRole));
        Assert.All(director.Of(world, Prologue.RowRole), m => Assert.Empty(m.Weapons));
        Assert.Equal(checkpoint >= 1 ? 1 : 0, Count(world, Prologue.ThresherRole));
        Assert.Equal(cp is 7 or 8 ? 3 : 0, Count(world, Prologue.ReaperRole));
        Assert.Equal(cp is 7 or 8 ? 1 : 0, Count(world, Prologue.CommanderRole));
        // No posted Grunts in the armoury any more (its batches come once the Bolter is taken); the Locator wears the Bolter
        // once it was taken, and from the armoury's second pause the Grunts the Bolters it stripped (C17).
        Assert.Equal(0, Count(world, Prologue.HostileRole));
        Assert.Equal(cp >= 9, world.HeroOf(run.Locator)!.Weapons.Count > 0);
        Assert.All(run.Seats.Skip(1), seat => Assert.Equal(cp >= 9, world.HeroOf(seat)!.Weapons.Count > 0));
        Assert.Equal(cp >= 9, run.Flags.Contains(Prologue.Sealed));
        Assert.Equal(cp is 9 or 10, run.Flags.Contains(StoryMarks.ArmourySealed));
        Assert.All(world.Machines.Where(m => run.Seats.All(s => world.HeroOf(s)?.Id != m.Id)), m => Assert.Equal(Race.Blue, m.Race));
        // From the break-in the alarm is on; after Erwischt the core is gone and the lights at emergency.
        Assert.Equal(cp < 8, world.Artefacts.Any(a => a.Tag == Prologue.CoreTag && !a.IsDead));
        Assert.Equal(cp >= 7, run.Flags.Contains(StoryMarks.Alarm));
        Assert.Equal(cp >= 8 ? "emergency" : "on", world.StoryLightState("core"));
        Assert.Equal(cp >= 9, run.Flags.Contains(StoryMarks.Done("pickup_bolter")));
        if (Prologue.CountdownFrom[cp] is { } left)
            Assert.Equal(left, run.Timers[StoryMarks.Countdown] - world.Time, 2);
        else
            Assert.DoesNotContain(StoryMarks.Countdown, run.Timers.Keys);
        // The beats of the stretches before the checkpoint count as fired; none after.
        // (A checkpoint 1 start has section 2's window played: it plays once a save.)
        Assert.All(Prologue.Script.Where(b => !b.Repeat), b => Assert.Equal(b.Span < cp || cp == 1 && Prologue.Window.Contains(b.Name), director.Has(b.Name)));
        Assert.Equal(checkpoint > 0, StoryMarks.Objective(run) is not null);

        // It plays on from there (at the start the Locator boots in its clamp, with no objective until its end).
        for (var i = 0; i < 2 * GameWorld.TicksPerSecond; i++)
            world.Step();
        Assert.False(run.Over);
        Assert.Equal(checkpoint == 0, run.Flags.Contains(StoryMarks.Boot));
        Assert.Equal(checkpoint > 0, StoryMarks.Objective(run) is not null);
    }

    [Fact]
    public void At_the_start_the_locator_boots_in_its_clamp_and_then_hacks_itself_free()
    {
        var world = Chapter();
        var run = world.Story!;
        var locator = world.HeroOf(run.Locator)!;
        var rider = new Rider(world);
        rider.Step();

        // The boot: held, the clamp's hack shut, no objective, until its end.
        Assert.True(world.HeldByStory(locator.Id));
        Assert.Contains(StoryMarks.Boot, run.Flags);
        Assert.Contains("clamp_l7", run.Hacks.Locked);
        Assert.Null(StoryMarks.Objective(run));
        Assert.Equal("that cannot be hacked yet", Refused(world, new StoryHackOpenCommand(Race.Red, "clamp_l7")));
        rider.Until(() => run.Director.Has("boot_done"), Prologue.BootLength + 1);
        Assert.InRange(world.Time - run.Director.Fired["boot"], Prologue.BootLength - 0.01f, Prologue.BootLength + 0.2f);
        Assert.DoesNotContain(StoryMarks.Boot, run.Flags);
        Assert.DoesNotContain("clamp_l7", run.Hacks.Locked);
        Assert.Equal(("Free yourself from the clamp", (string?)"clamp_l7"), StoryMarks.Objective(run));
        Assert.True(world.HeldByStory(locator.Id));

        rider.Hack("clamp_l7");
        Assert.False(world.HeldByStory(locator.Id));
        Assert.False(world.DoorClosed('A'));
        Assert.Equal("terminal_ike", StoryMarks.Objective(run)!.Value.Place);
    }

    /// <summary>The world's reason for refusing a command given and stepped once; null when it took it.</summary>
    private static string? Refused(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault(r => ReferenceEquals(r.Command, command))?.Reason;
    }

    [Fact]
    public void The_checkpoint_tables_have_a_row_for_each_of_the_fifteen_and_agree()
    {
        Assert.Equal(15, Prologue.Starts.Count);
        Assert.Equal(14, Prologue.LastCheckpoint);
        foreach (var table in new[] { Prologue.SectionAt.Count, Prologue.DoorsOpen.Count, Prologue.DoorsAuto.Count, Prologue.CountdownFrom.Count })
            Assert.Equal(15, table);
        Assert.Equal([1, 2, 2, 2, 2, 2, 3, 4, 6, 6, 6, 6, 7, 7, 7], Prologue.SectionAt);
        // The sections only go forward; the countdown runs from the Notgang on and only there.
        Assert.True(Prologue.SectionAt.Zip(Prologue.SectionAt.Skip(1)).All(p => p.First <= p.Second));
        for (var cp = 0; cp <= Prologue.LastCheckpoint; cp++)
        {
            Assert.Equal(Prologue.SectionAt[cp] == 7, Prologue.CountdownFrom[cp] is not null);
            // A door open at a checkpoint stays open at every later one, unless a stage-7 package shuts or locks it (H, R, T, U).
            if (cp > 0)
                Assert.All(Prologue.DoorsOpen[cp - 1].Except("HRTU"), letter => Assert.Contains(letter, Prologue.DoorsOpen[cp]));
            // A checkpoint not built yet repeats the rows of the one it is set up as.
            var built = Prologue.SetUpAs(cp);
            Assert.True(Prologue.Built.Contains(built) && built <= cp);
            Assert.Equal(Prologue.Starts[built].At, Prologue.Starts[cp].At);
            Assert.Equal((Prologue.Starts[built].Facing, Prologue.DoorsOpen[built], Prologue.DoorsAuto[built]),
                (Prologue.Starts[cp].Facing, Prologue.DoorsOpen[cp], Prologue.DoorsAuto[cp]));
        }
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], Prologue.Built);
    }

    [Fact]
    public void Every_beats_span_and_every_checkpoint_it_reaches_is_one_of_the_fifteen_and_built()
    {
        foreach (var beat in Prologue.Script)
        {
            Assert.InRange(beat.Span, 0, Prologue.LastCheckpoint);
            Assert.Contains(beat.Span, Prologue.Built);
            foreach (var reached in beat.Then.OfType<Then.Checkpoint>())
            {
                Assert.Contains(reached.Number, Prologue.Built);
                // A beat reaches its own stretch's checkpoint (the break-in) or a later one.
                Assert.True(reached.Number >= beat.Span, beat.Name);
            }
        }
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 11, 12], Prologue.Script.SelectMany(b => b.Then.OfType<Then.Checkpoint>()).Select(c => c.Number).Order());
    }

    [Fact]
    public void From_the_decks_checkpoint_the_thresher_steps_first()
    {
        // Checkpoint 1's stretch begins with his steps a few seconds in (the rider covers 1 from the start).
        var world = Chapter(1);
        for (var i = 0; i < 6 * GameWorld.TicksPerSecond; i++)
            world.Step();
        Assert.True(world.Story!.Director.Has("steps"));
    }

    // ---- a run of the chapter ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "boot")]
    [InlineData(2, "switch_room")]
    [InlineData(3, "ring_b")]
    [InlineData(6, "core_open")]
    [InlineData(7, "at_i")]
    [InlineData(8, "workshop")]
    [InlineData(11, "notgang")]
    [InlineData(12, "pod_bay")]
    public void Scripted_inputs_move_through_every_section_to_the_escape(int checkpoint, string first)
    {
        var world = Chapter(checkpoint);
        var rider = new Rider(world);

        rider.Play();

        var run = world.Story!;
        // From every built checkpoint the first beat of its stretch fires first (S, W and E add theirs as they build 2–5, 9–10, 13–14).
        Assert.Equal(first, Prologue.Script.Where(b => b.Span == checkpoint && !b.Repeat && run.Director.Has(b.Name))
            .OrderBy(b => run.Director.Fired[b.Name]).First().Name);
        Assert.Equal(StoryEnding.Escaped, run.Ending);
        Assert.Equal(Race.Red, world.Winner);
        Assert.Equal(Prologue.Built.Max(), run.Checkpoint);
        // Every section from the checkpoint's on, in order, and the built checkpoints after it.
        Assert.Equal(Enumerable.Range(Prologue.SectionAt[checkpoint] + 1, 8 - Prologue.SectionAt[checkpoint]), rider.Sections.Select(s => s.Section));
        Assert.Equal(Prologue.Built.Where(c => c > checkpoint), rider.Checkpoints);
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
        var code = string.Concat(Directory.GetFiles(Repo("sim/Machines.Sim/Story"), "Prologue*.cs").Order(StringComparer.Ordinal).Select(File.ReadAllText));
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
        /// The armoury's five batches, cut short (their own fights are PrologueWaveTests'): every unit is put down as it
        /// comes in; in the pauses a Grunt down is put together and a hurt one mended with the use key, so the early call
        /// brings the next batch on; until the bolts let go and the old flow's "cleared" fires.
        /// </summary>
        public void Armoury()
        {
            for (var i = 0; i < 600 * GameWorld.TicksPerSecond && !Run.Over && !Run.Director.Has("cleared"); i++)
            {
                if (world.HeroOf(Run.Locator) is not { } locator || world.LocatorShutDown)
                {
                    Step();
                    continue;
                }
                foreach (var id in Run.Waves.Foes.Keys.ToList())
                {
                    if (world.Find<Machine>(id) is { IsDead: false } foe)
                        world.Damage(foe, 100000, Race.Red, locator, "BOLTER");
                }
                var seat = Run.Seats.Skip(1).FirstOrDefault(s => world.CompanionDown(s));
                var hurt = Run.Seats.Skip(1).FirstOrDefault(s => !world.CompanionDown(s) && world.HeroOf(s) is { } g && g.HitPoints < g.MaxHitPoints * 0.95f);
                var target = seat.Value != 0 ? GameWorld.SeatTarget + seat.Value : !Run.Waves.Fighting && hurt.Value != 0 ? GameWorld.MendTarget + hurt.Value : null;
                if (target is not null && !Run.Crew.Holding.ContainsKey(Run.Locator.Value))
                {
                    var grunt = world.HeroOf(seat.Value != 0 ? seat : hurt)!;
                    SetDown(locator, grunt.Position + Vector2.Normalize(locator.Position - grunt.Position + new Vector2(0.01f, 0)) * (grunt.Radius + locator.Radius + 1));
                    world.Issue(new StoryUseCommand(Race.Red, target));
                }
                Step();
            }
            Assert.True(Run.Director.Has("cleared") || Run.Over, $"the armoury not cleared (batch {Run.Waves.Wave}, cleared {Run.Waves.Cleared})");
        }

        /// <summary>The chapter from where it stands to its end, leg by leg.</summary>
        public void Play()
        {
            var director = Run.Director;
            bool Done(string target) => Run.Hacks.Done.Contains(target);
            // The Locator's boot in its clamp is skipped, as a player who has seen it may (its own tests: PrologueBootTests).
            if (Run.Checkpoint == 0 && !director.Has("boot_done"))
            {
                Step();
                Assert.Null(Do(new StoryUseCommand(Race.Red, StoryMarks.SkipBoot)));
                Assert.True(director.Has("boot_done"));
            }
            for (var legs = 0; legs < 60 && !Run.Over; legs++)
            {
                // The rider goes from hack to hack where a player sneaks: section 2's Thresher looks the other way for it (his own
                // tests: StorySightTests, PrologueThresherTests).
                foreach (var eyes in Run.Sight.Seekers.Values.Where(e => e.Searchlight))
                    eyes.Blind = Run.Section == 2;
                if (!Done("clamp_l7"))
                    Hack("clamp_l7");
                else if (!Done("terminal_ike"))
                    Hack("terminal_ike");
                else if (!Done("terminal_bob"))
                    Hack("terminal_bob");
                else if (!director.Has("deck_a"))
                    Go(Zone("R4"));
                else if (!director.Has("schacht"))
                    Go(Zone("V1am"));
                else if (!Done("hack_s"))
                    Hack("hack_s");
                else if (!director.Has("deck_b"))
                    Go(Zone("R6"));
                else if (!director.Has("guard"))
                    All("nordgang");
                else if (!Done("hack_h"))
                    Hack("hack_h");
                else if (!director.Has("antechamber"))
                    Go(Zone("R8"));
                else if (!director.Has("break_in") && !Done("hack_core"))
                    Hack("hack_core");
                else if (!director.Has("break_in"))
                    Until(() => director.Has("break_in"), Prologue.ShootMax + 10);
                else if (!director.Has("core_blows") && Vector2.Distance(Locator.Position, new Vector2(219, 262)) > 7)
                    // In cover from the Reapers' fire by the vent, before they storm in.
                    Go(new Vector2(219, 262));
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
                else if (!director.Has("lockdown"))
                    All("armoury");
                else if (!director.Has("cleared"))
                    Armoury();
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
