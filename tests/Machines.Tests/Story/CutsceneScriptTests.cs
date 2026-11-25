using System.Text.Json;
using Machines.Game;
using Machines.Import.Model;
using Machines.Import.Planets;
using Machines.Import.Scenarios;

namespace Machines.Tests.Story;

/// <summary>
/// The story's cutscenes as data (plan WP-N): the script format, and the prologue's script, "Abwurf" (Erwischt is played now, not filmed),
/// checked against the deck, the voice lines, the sounds and the props they name.
/// </summary>
public class CutsceneScriptTests
{
    private static string Repo => Path.GetFullPath(Path.Combine(RepoPaths.OriginalDataFolder(), "..", ".."));

    private static string Prologue(string file) => Path.Combine(Repo, "data", "planets", "prolog", file);

    private static CutsceneScript Script(string name) => CutsceneScript.Load(Prologue(name + ".cut"));

    private static readonly Lazy<Deck> DeckLoader = new(() => DeckParser.Load(Prologue("prolog.deck")));

    private static readonly string[] Names = ["abwurf"];

    // ---- the format ------------------------------------------------------------------------------

    [Fact]
    public void A_script_reads_its_parts_takes_and_cues()
    {
        var script = CutsceneScript.Parse("""
            // a comment
            cutscene probe
            bars 0.1
            blend 0.4
            part inside deck
            take one 2 cockpit locator look=core@4 rate=2
            take two 1.5 cam cam_x fov=38 // a comment after a line
            at 1 shake core@4 12 2
            at 0.5 line P_ANN_C1
            part out outside
            take far 3 fixed 1,2,3 look=ship
            at 0 prop ship mothership 0,0,100 10,0,50 3 nose=-x glow=3
            """);
        Assert.Equal("probe", script.Name);
        Assert.Equal(0.1f, script.Bars);
        Assert.Equal(0.4f, script.Blend);
        var inside = script.Part(onDeck: true)!;
        Assert.Equal("inside", inside.Name);
        Assert.Equal(3.5f, inside.Length);
        Assert.Equal(2f, inside.StartOf(1));
        Assert.Equal(["one", "two"], inside.Takes.Select(t => t.Name));
        Assert.Equal("locator", inside.Takes[0].Subject);
        Assert.Equal("core@4", inside.Takes[0].Text("look"));
        Assert.Equal(38, inside.Takes[1].Number("fov", 0));
        // Cues are kept in the order of their moments.
        Assert.Equal(["line", "shake"], inside.Cues.Select(c => c.Kind));
        Assert.Equal(12, inside.Cues[1].Number(1));
        var outside = script.Part(onDeck: false)!;
        Assert.Equal("mothership", outside.Cues[0].Args[1]);
        Assert.Equal("-x", outside.Cues[0].Text("nose"));
    }

    [Theory]
    [InlineData("take a 1 cockpit locator", "outside a part")]
    [InlineData("part p deck\ntake a 1 dolly x", "no camera kind 'dolly'")]
    [InlineData("part p deck\ntake a 0 cam x", "more than 0 seconds")]
    [InlineData("part p deck\ntake a 1 cockpit", "names its who")]
    [InlineData("part p deck\ntake a 1 cam c\nat 1 boom x", "no cue 'boom'")]
    [InlineData("part p deck\ntake a 1 cam c\nat 1 shake core 4", "takes 3 arguments, not 2")]
    [InlineData("part p deck\ntake a 1 cam c\nat soon line X", "'soon' is not a number")]
    [InlineData("part p sky\ntake a 1 cam c", "deck|outside")]
    [InlineData("part p deck\ntake a 1 cam c fov=3 fov=4", "'fov=' twice")]
    [InlineData("frame 3", "is not a cutscene line")]
    public void A_wrong_line_is_told_with_its_number(string body, string why)
    {
        var error = Assert.Throws<FormatException>(() => CutsceneScript.Parse("cutscene probe\n" + body, "probe.cut"));
        Assert.Contains(why, error.Message);
        Assert.StartsWith("probe.cut:", error.Message);
    }

    [Fact]
    public void A_script_without_a_name_or_a_take_is_refused()
    {
        Assert.Throws<FormatException>(() => CutsceneScript.Parse("part p deck\ntake a 1 cam c"));
        Assert.Throws<FormatException>(() => CutsceneScript.Parse("cutscene probe\npart p deck"));
    }

    [Fact]
    public void Points_are_names_lifted_names_or_metres_with_alternatives()
    {
        Assert.Equal([new CutscenePoint("core", 0, 0, 4)], CutscenePoint.Parse("core@4"));
        Assert.Equal([new CutscenePoint(null, 88, 133.5f, -2)], CutscenePoint.Parse("88,133.5,-2"));
        Assert.Equal([new CutscenePoint("reaper", 0, 0, 3.2f), new CutscenePoint(null, 80, 125, 3)], CutscenePoint.Parse("reaper@3.2|80,125,3"));
        // A point on a prop in its own metres, turning with it.
        Assert.Equal([new CutscenePoint("ship", 150, -200, -60, Local: true)], CutscenePoint.Parse("ship@150,-200,-60"));
        Assert.Throws<FormatException>(() => CutscenePoint.Parse("1,2"));
        Assert.Throws<FormatException>(() => CutscenePoint.Parse("ship@1,2"));
        Assert.Throws<FormatException>(() => CutscenePoint.Parse("core@high"));
    }

    [Fact]
    public void Space_parts_play_with_the_map_part_beside_them()
    {
        var script = CutsceneScript.Parse("""
            cutscene probe
            part before space planet=0,9000,-9000 radius=9000 sun=200,30
            take a 2 chase ship offset=1,2,3 to=4,5,6 look=ship@-10,0,0
            at 0 prop ship space/mothership 0,0,0 0,100,0 5 nose=-x
            at 1 explosion ship@1,2,3 40
            part bay deck
            take b 3 cam cam_x
            at 0.5 line P_ANN_C1
            part after space
            take c 4 fixed ship@10,-20,30 look=pod|ship
            at 1 impact 0,10,-20 300
            at 2 bowshock ship 2 ease=1
            part out outside
            take d 1 fixed 1,2,3
            """);
        // The space part before the deck's plays first, the one after it next: one run, as one part.
        var run = script.Part(onDeck: true)!;
        Assert.Equal("bay", run.Name);
        Assert.True(run.HasSpace);
        Assert.Equal(["a", "b", "c"], run.Takes.Select(t => t.Name));
        Assert.Equal([true, false, true], run.Takes.Select(t => t.Space));
        Assert.Equal(9, run.Length);
        // Cues keep their moments within the run, and whether they belong to space.
        Assert.Equal([(0f, true), (1f, true), (2.5f, false), (6f, true), (7f, true)], run.Cues.Select(c => (c.At, c.Space)));
        Assert.Equal("200,30", run.Options["sun"]);
        // The outside part has no space beside it.
        var outside = script.Part(onDeck: false)!;
        Assert.False(outside.HasSpace);
        Assert.Equal(1, outside.Length);
    }

    [Theory]
    [InlineData("part p space\ntake a 1 cam c", "space")]
    [InlineData("part p space\ntake a 1 fixed 0,0,0", "goes with a deck or outside part")]
    public void Space_parts_are_filmed_fixed_or_chasing_beside_a_map_part(string body, string why)
    {
        var error = Assert.Throws<FormatException>(() => CutsceneScript.Parse("cutscene probe\n" + body, "probe.cut"));
        Assert.Contains(why, error.Message);
    }

    // ---- the prologue's scripts --------------------------------------------------------------------

    [Fact]
    public void Abwurf_burns_drops_and_strikes_in_space_around_the_bay_then_lands_outside()
    {
        var script = Script("abwurf");
        var bay = script.Part(onDeck: true)!;
        Assert.True(bay.HasSpace);
        Assert.All(bay.Takes.Where(t => !t.Space), t => Assert.Equal("cam_abwurf_1", t.Subject));
        // The burning ship first (cut 1), then the bay, then the fall (cuts 2 to 4).
        Assert.True(bay.Takes[0].Space && bay.Takes[^1].Space);
        var first = bay.Takes.ToList().FindIndex(t => !t.Space);
        Assert.InRange(bay.StartOf(first), 8, 15);
        var ship = bay.Cues.First(c => c is { Kind: "prop", Space: true } && c.Args[1] == "space/mothership");
        Assert.Equal(0, ship.At);
        var from = CutscenePoint.Parse(ship.Args[2]).Single();
        var to = CutscenePoint.Parse(ship.Args[3]).Single();
        var descentAngle = Math.Atan2(from.Up - to.Up, to.Y - from.Y) * 180 / Math.PI;
        Assert.InRange(descentAngle, 4, 7);
        Assert.Contains(bay.Cues, c => c is { Kind: "explosion", Space: true } && c.At < bay.StartOf(first));
        // It races into the planet: glowing, trailing, in its bow shock, and strikes.
        var dive = bay.Cues.Last(c => c is { Kind: "prop" } && c.Args[1] == "space/mothership");
        Assert.True(dive.Number("glow", 0) > 0 && dive.Number("trail", 0) > 0);
        Assert.Contains(bay.Cues, c => c is { Kind: "bowshock" } && c.Args[0] == "ship");
        Assert.Contains(bay.Cues, c => c.Kind == "impact" && c.At >= dive.At);
        // The pod's own entry in its shock.
        Assert.Contains(bay.Cues, c => c is { Kind: "bowshock" } && c.Args[0] == "pod");
        // The run ends black: the story loads the outside then.
        var black = bay.Cues.Last(c => c.Kind == "fade");
        Assert.Equal(1, black.Number(0));
        Assert.True(black.At + black.Number(1) <= bay.Length);

        var outside = script.Part(onDeck: false)!;
        Assert.False(outside.HasSpace);
        Assert.InRange(outside.Length, 10, 20);
        // The mothership's smoke has had time to rise since the strike, but is not already at the end of its plume.
        var smoke = Assert.Single(outside.Cues, c => c.Kind == "smoke" && c.At == 0);
        var impact = Assert.Single(bay.Cues, c => c.Kind == "impact");
        Assert.InRange(Math.Abs(smoke.Number("early", 0) - (bay.Length - impact.At)), 0, 1);
        var pod = outside.Cues.Last(c => c is { Kind: "prop" } && c.Args[1] == "pod_drop");
        Assert.True(pod.Number("land", 0) > 0, "the pod lands hard");
        // Dusk, and darker than the planet's own dusk (FilmCamera's dim): the ship's glow has to carry the picture.
        var dusk = Assert.Single(outside.Cues, c => c.Kind == "dusk");
        Assert.InRange(dusk.Number(0), 0.7f, 1);
        Assert.InRange(dusk.Number("dim", 0), 0.3f, 0.9f);
        Assert.Contains(outside.Cues, c => c.Kind == "silence" && c.At > pod.At + pod.Number(4));
        var end = outside.Cues.Last(c => c.Kind == "fade");
        Assert.Equal(1, end.Number(0));
        Assert.True(end.At + end.Number(1) <= outside.Length);
        Assert.True(File.Exists(Path.Combine(Repo, "game", "assets", "story", "space", "mothership.glb")));
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Every_name_a_script_uses_is_there(string name)
    {
        var script = Script(name);
        var deck = DeckLoader.Value;
        var lines = VoiceLines();
        // Each run as it plays: a space part with the map part it goes with, so its points may name that part's props.
        foreach (var part in new[] { script.Part(onDeck: true), script.Part(onDeck: false) }.OfType<CutscenePart>())
        {
            var props = part.Cues.Where(c => c.Kind == "prop").Select(c => c.Args[0]).ToHashSet();
            bool Known(string text) => CutscenePoint.Parse(text).All(p => p.Name is null
                || CutsceneScript.Special.Contains(p.Name) || CutsceneScript.Roles.ContainsKey(p.Name) || props.Contains(p.Name)
                || (part.OnDeck && (deck.Markers.Any(m => m.Name == p.Name) || deck.Cameras.Any(c => c.Name == p.Name)
                    || deck.Zones.Any(z => z.Name == p.Name))));
            foreach (var take in part.Takes)
            {
                var where = $"{name}.cut:{take.Line}";
                switch (take.Kind)
                {
                    case "cam":
                        Assert.True(part.OnDeck && deck.Cameras.Any(c => c.Name == take.Subject), $"{where}: no deck camera {take.Subject}");
                        break;
                    case "rail":
                        Assert.True(part.OnDeck && deck.Rails.Any(r => r.Name == take.Subject), $"{where}: no deck rail {take.Subject}");
                        break;
                    case "cockpit":
                        Assert.True(CutsceneScript.Special.Contains(take.Subject!) || CutsceneScript.Roles.ContainsKey(take.Subject!), $"{where}: nobody called {take.Subject}");
                        break;
                    case "fixed":
                        Assert.True(Known(take.Subject!), $"{where}: no point {take.Subject}");
                        break;
                    case "chase":
                        Assert.True(props.Contains(take.Subject!), $"{where}: no prop {take.Subject}");
                        break;
                }
                if (take.Text("look") is { } look)
                    Assert.True(Known(look), $"{where}: no point {look}");
            }
            foreach (var cue in part.Cues)
            {
                var where = $"{name}.cut:{cue.Line}";
                Assert.InRange(cue.At, 0, part.Length);
                var points = cue.Kind switch
                {
                    "shake" or "explosion" or "sparks" or "fire" or "smoke" or "flash" or "impact" => [cue.Args[0]],
                    "shot" => [cue.Args[1], cue.Args[2]],
                    "prop" => [cue.Args[2], cue.Args[3]],
                    _ => Array.Empty<string>(),
                };
                foreach (var point in points.Concat(new[] { cue.Text("at"), cue.Text("toward") }.OfType<string>()))
                    Assert.True(Known(point), $"{where}: no point {point}");
                if (cue.Kind == "line")
                    Assert.True(lines.ContainsKey(cue.Args[0]), $"{where}: no voice line {cue.Args[0]}");
                if (cue.Kind == "sound")
                    Assert.True(File.Exists(Path.Combine(Repo, "game", "assets", "story", "sfx", cue.Args[0] + ".ogg")), $"{where}: no sound {cue.Args[0]}");
                if (cue.Kind == "bowshock")
                    Assert.True(props.Contains(cue.Args[0]), $"{where}: no prop {cue.Args[0]}");
                // A model by its bare name is a prop (game/assets/story/props); one with a folder is under game/assets/story.
                if (cue.Kind == "prop" && cue.Args[1] != "pod")
                    Assert.True(File.Exists(Path.Combine(Repo, "game", "assets", "story", cue.Args[1].Contains('/') ? cue.Args[1] + ".glb" : Path.Combine("props", cue.Args[1] + ".glb"))), $"{where}: no prop model {cue.Args[1]}");
            }
        }
    }

    public static TheoryData<string> Scripts() => new(Names);

    [Fact]
    public void Abwurf_is_also_a_film_on_its_own_desert_map_outside_the_menus()
    {
        var films = File.ReadAllLines(Path.Combine(Repo, "tools", "cinematic", "films.txt"));
        var film = Assert.Single(films, l => l.StartsWith("prolog-abwurf ")).Split(' ');
        Assert.Equal("prolog-abwurf,m_desert", film[1]);
        var scenario = ScenarioParser.Load(Path.Combine(Repo, "data", "maps", "prolog-abwurf.scn"));
        Assert.Equal(2, scenario.Races.Count);
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(Repo, "data", "maps", "maps.txt")), l => l.Contains("prolog-abwurf"));
    }

    // The voice lines by id, with their German voice's seconds (game/assets/story/voice/manifest.json).
    private static Dictionary<string, float> VoiceLines()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "game", "assets", "story", "voice", "manifest.json")));
        return json.RootElement.GetProperty("lines").EnumerateObject()
            .ToDictionary(l => l.Name, l => l.Value.TryGetProperty("de", out var de) ? de.GetProperty("seconds").GetSingle() : 0);
    }
}
