using System.Text.Json;
using System.Text.RegularExpressions;

namespace Machines.Tests.Story;

/// <summary>
/// Stage 7's new lines (renders/prologue/stufe7-plan.md section 6, package V) in tools/voice/lines.json: every spoken one
/// has its German and English voice, from the voice its speaker has everywhere else, none names a Grunt (the player
/// can rename them) or the builders, and the names are said as pronounce.json has them.
/// </summary>
public class StoryStage7LinesTests
{
    private static string Repo(string path) => Path.Combine(RepoPaths.OriginalDataFolder(), "..", "..", path);

    private sealed record Line(string Id, string Speaker, string? Preset, string De, string En, bool Aloud, string When);

    private static List<Line> All()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Repo("tools/voice/lines.json")));
        return json.RootElement.GetProperty("lines").EnumerateArray().Select(l => new Line(
            l.GetProperty("id").GetString()!, l.GetProperty("speaker").GetString()!,
            l.TryGetProperty("preset", out var p) ? p.GetString() : null,
            l.GetProperty("de").GetString()!, l.GetProperty("en").GetString()!,
            l.TryGetProperty("aloud", out var a) && a.GetBoolean(),
            l.GetProperty("when").GetString()!)).ToList();
    }

    /// <summary>The stage's own lines: their "when" says "stage 7".</summary>
    private static List<Line> Stage7() => All().Where(l => l.When.StartsWith("stage 7", StringComparison.Ordinal)).ToList();

    [Fact]
    public void The_stage_has_all_its_lines_once()
    {
        var lines = Stage7();
        // 95 new ids: 24 in section 2, 33 in section 6, 38 in section 7 with the Notabschaltung and the escape rework of
        // 2026-11-26 (crate walls, door P jammed, the gas main); 77 of them spoken.
        Assert.Equal(95, lines.Count);
        Assert.Equal(77, lines.Count(l => l.Speaker != "hud"));
        Assert.Equal(10, lines.Count(l => l.Speaker == "auto"));
        Assert.Equal(All().Count, All().Select(l => l.Id).Distinct().Count());
        // The plan's renamed ids, so nothing collides with the lines kept (C3).
        foreach (var id in new[] { "P_BOB_DOOR_H", "P_IKE_BANG", "P_BOB_BANG", "P_BOB_BANG_OFFER", "P_BOB_RETREAT", "P_BOB_DOOR_Y", "P_IKE_CABLE_DONE", "P_BOB_CABLE_DONE", "P_HUD_AMBER", "P_IKE_PIPE" })
            Assert.Contains(lines, l => l.Id == id);
        // Every id says who speaks it, and only L-7's two lines to the Grunts are said aloud.
        foreach (var line in lines.Where(l => l.Speaker != "hud"))
            Assert.StartsWith(line.Speaker switch { "ike" => "P_IKE_", "bob" => "P_BOB_", "l7" => "P_L7_", _ => "P_AUTO_" }, line.Id);
        Assert.Equal(["P_L7_PODBAY", "P_L7_HOLD"], lines.Where(l => l.Aloud).Select(l => l.Id));
        Assert.Equal("bob-radio", lines.Single(l => l.Id == "P_BOB_GLITCH_2").Preset);
    }

    [Fact]
    public void Every_spoken_line_has_both_voices_from_its_speakers_voice()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Repo("game/assets/story/voice/manifest.json")));
        var voices = manifest.RootElement.GetProperty("lines");
        using var cast = JsonDocument.Parse(File.ReadAllText(Repo("tools/voice/voices.json")));
        var speakers = cast.RootElement.GetProperty("speakers");
        foreach (var line in Stage7())
        {
            if (line.Speaker == "hud")
            {
                Assert.False(voices.TryGetProperty(line.Id, out _), $"{line.Id} is text only");
                continue;
            }
            foreach (var lang in new[] { "de", "en" })
            {
                var file = voices.GetProperty(line.Id).GetProperty(lang);
                Assert.True(file.GetProperty("seconds").GetDouble() > 0.3, $"{line.Id} {lang}");
                Assert.True(File.Exists(Repo("game/assets/story/voice/" + file.GetProperty("file").GetString())), $"{line.Id} {lang}");
                Assert.Equal(line.Preset ?? line.Speaker, file.GetProperty("preset").GetString());
                if (speakers.TryGetProperty(line.Speaker, out var speaker))
                {
                    // Ike, Bob and L-7: the Google voice cast for them (Sadachbia, Alnilam, Iapetus).
                    Assert.Equal("google", file.GetProperty("source").GetString());
                    Assert.Equal(speaker.GetProperty(lang).GetString(), file.GetProperty("voice").GetString());
                }
                else
                {
                    // The automatic voice: the SAPI stand-in as its other lines.
                    Assert.Equal("sapi", file.GetProperty("source").GetString());
                }
            }
        }
    }

    [Fact]
    public void No_new_line_names_a_grunt_or_the_builders()
    {
        foreach (var line in Stage7())
            foreach (var text in new[] { line.De, line.En })
            {
                Assert.False(Regex.IsMatch(text, @"\b(Ike|Bob)\b"), $"{line.Id}: {text}");
                Assert.False(Regex.IsMatch(text, @"mensch|\bhuman", RegexOptions.IgnoreCase), $"{line.Id}: {text}");
            }
    }

    [Fact]
    public void Names_are_said_as_the_user_asked()
    {
        // German: "Ike" is said "Eik" and "L-7" "Ell sieben" (the user, 2026-11-22), in every engine.
        using var table = JsonDocument.Parse(File.ReadAllText(Repo("tools/voice/pronounce.json")));
        var de = table.RootElement.GetProperty("de");
        Assert.Equal("Eik", de.GetProperty("Ike").GetString());
        Assert.Equal("Ell sieben", de.GetProperty("L-7").GetString());
    }
}
