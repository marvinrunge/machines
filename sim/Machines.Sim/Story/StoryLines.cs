using System.Text.Json;

namespace Machines.Sim.Story;

/// <summary>Where a line comes from: the ship's loudspeakers, a Grunt's radio, or the Locator's own head (the whisper, the HUD).</summary>
public enum StoryLineChannel
{
    Pa,
    Radio,
    Thought,
}

/// <summary>A line of the story: its id, who says it (ship, auto, ike, bob, whisper, hud), how it comes, and its English text.</summary>
public sealed record StoryLine(string Id, string Speaker, StoryLineChannel Channel, string English);

/// <summary>
/// The game's line table (game/assets/story/prologue.lines.json, made by tools/voice/runtime_lines.py from tools/voice/lines.json):
/// what the subtitles show for a line the director or a cutscene says. The German is the English's translation in de.po.
/// </summary>
public sealed class StoryLines
{
    private readonly Dictionary<string, StoryLine> _lines;

    private StoryLines(Dictionary<string, StoryLine> lines) => _lines = lines;

    public static StoryLines Empty { get; } = new([]);

    /// <summary>Every line, in the table's order of ids.</summary>
    public IEnumerable<StoryLine> All => _lines.Values;

    public int Count => _lines.Count;

    /// <summary>The line with an id; null when the table has none.</summary>
    public StoryLine? Find(string id) => _lines.GetValueOrDefault(id);

    public bool Has(string id) => _lines.ContainsKey(id);

    /// <summary>Reads the table's JSON text; throws <see cref="FormatException"/> when it is not one.</summary>
    public static StoryLines Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var lines = new Dictionary<string, StoryLine>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.GetProperty("lines").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString() ?? throw new FormatException("a line without an id");
                var channel = entry.GetProperty("channel").GetString() switch
                {
                    "pa" => StoryLineChannel.Pa,
                    "radio" => StoryLineChannel.Radio,
                    "thought" => StoryLineChannel.Thought,
                    var other => throw new FormatException($"{id}: unknown channel '{other}'"),
                };
                lines[id] = new StoryLine(id, entry.GetProperty("speaker").GetString() ?? "", channel, entry.GetProperty("en").GetString() ?? "");
            }
            return new StoryLines(lines);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException($"not a line table: {e.Message}", e);
        }
    }
}
