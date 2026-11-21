namespace Machines.Game;

/// <summary>Who speaks a line of the story, which colours its subtitle: the line table's <c>speaker</c> (tools/voice/lines.json).</summary>
public enum StorySpeaker
{
    /// <summary>The ship's friendly announcements.</summary>
    Ship,
    /// <summary>The automatic voice that takes over once the core is gone.</summary>
    Auto,
    Ike,
    Bob,
    /// <summary>The update whispering the Locator's name.</summary>
    Whisper,
    /// <summary>A line the HUD writes, never spoken.</summary>
    Hud,
    Locator,
}

/// <summary>A subtitle: who says it, the name it is shown under (none for the whisper and the HUD), the words, and when it shows.</summary>
public sealed record SubtitleLine(StorySpeaker Speaker, string? Name, string Text, double From, double Until);

/// <summary>
/// The story's subtitles as they come and go, Godot-free so that the tests can time them. A line shows as long as its voice
/// file plays and a little after, or, without one (the voices are recorded later; until then many lines have none), for
/// <see cref="Least"/> seconds and <see cref="PerCharacter"/> more for every character, enough to read it. A line waits for the
/// one before of the same speaker to end; at most <see cref="Most"/> show at once, the oldest giving way.
/// </summary>
public sealed class SubtitleQueue
{
    /// <summary>The shortest a line without a voice stays, and what each character adds, in seconds.</summary>
    public const double Least = 1.5, PerCharacter = 0.07;

    /// <summary>How long a line with a voice stays after it, to finish reading; how long a line fades in and out.</summary>
    public const double After = 0.4, Fade = 0.25;

    /// <summary>How many lines show at once.</summary>
    public const int Most = 2;

    private readonly List<SubtitleLine> _lines = [];

    /// <summary>How long a line stays: its voice's length and a little, or by its length in characters.</summary>
    public static double Seconds(string text, double? clip) => clip is > 0 ? clip.Value + After : Least + PerCharacter * text.Length;

    /// <summary>The speaker of the line table's word for it, or null.</summary>
    public static StorySpeaker? Parse(string? speaker) => speaker?.ToLowerInvariant() switch
    {
        "ship" => StorySpeaker.Ship,
        "auto" => StorySpeaker.Auto,
        "ike" => StorySpeaker.Ike,
        "bob" => StorySpeaker.Bob,
        "whisper" => StorySpeaker.Whisper,
        "hud" => StorySpeaker.Hud,
        "locator" or "l7" => StorySpeaker.Locator,
        _ => null,
    };

    /// <summary>Adds a line said at <paramref name="now"/>; it begins once the speaker's line before it has ended.</summary>
    public SubtitleLine Add(StorySpeaker speaker, string? name, string text, double now, double? clip = null)
    {
        var from = _lines.Where(l => l.Speaker == speaker).Select(l => l.Until).DefaultIfEmpty(now).Max();
        from = Math.Max(from, now);
        var line = new SubtitleLine(speaker, name, text, from, from + Seconds(text, clip));
        _lines.Add(line);
        return line;
    }

    /// <summary>Every line at once is gone (a cutscene skipped, a checkpoint).</summary>
    public void Clear() => _lines.Clear();

    /// <summary>The lines showing at <paramref name="now"/>, the oldest first, each with how much of it shows (0 to 1); ended ones are dropped.</summary>
    public IReadOnlyList<(SubtitleLine Line, float Alpha)> Shown(double now)
    {
        _lines.RemoveAll(l => l.Until <= now);
        var showing = _lines.Where(l => l.From <= now).OrderBy(l => l.From).ToList();
        // The newest stay: an older line that would make one too many gives way at once.
        if (showing.Count > Most)
        {
            foreach (var old in showing.Take(showing.Count - Most))
                _lines.Remove(old);
            showing = showing.Skip(showing.Count - Most).ToList();
        }
        return showing.Select(l => (l, (float)Math.Clamp(Math.Min((now - l.From) / Fade, (l.Until - now) / Fade), 0, 1))).ToList();
    }

    /// <summary>Whether a line still shows or waits to.</summary>
    public bool Any => _lines.Count > 0;
}
