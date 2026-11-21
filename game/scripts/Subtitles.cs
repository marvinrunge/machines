using Godot;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The story's subtitles, at the foot of the view in the middle: each line on a soft dark band, the speaker's name in its
/// colour before the words (the ship's announcements green as the menus' glow, Ike gold, Bob ice blue, the automatic voice red,
/// the whisper pale and nameless, the HUD's own lines plain), in the menus' face, at the size the options choose
/// (<see cref="GameOptions.Subtitles"/>, none while they are off). They are timed by <see cref="SubtitleQueue"/>, so a line shows
/// with or without its voice file. A line meant for one player alone (what only Bob's player hears, docs/STORY.md "Die beiden
/// Grunts") shows on that player's screen only.
/// </summary>
public partial class Subtitles : Control
{
    private readonly SubtitleQueue _queue = new();
    private readonly HudInk _ink;
    private double _time;

    /// <summary>The names the Grunts go by in this match, for their lines; the host's names until a person plays one.</summary>
    public string IkeName { get; set; } = "Ike";

    public string BobName { get; set; } = "Bob";

    /// <summary>How far from the foot of the view the lowest line stands, in the HUD's pixels at 1080 (above the tracker and prompts).</summary>
    public float Foot { get; set; } = 150;

    public Subtitles()
    {
        Name = "Subtitles";
        MouseFilter = MouseFilterEnum.Ignore;
        _ink = new HudInk(this);
    }

    public override void _Ready() => SetAnchorsPreset(LayoutPreset.FullRect);

    /// <summary>
    /// A line of the story: <paramref name="english"/> is the line table's English (translated here), <paramref name="clip"/>
    /// its voice file's length in seconds if one plays. With <paramref name="only"/> it shows for that player alone.
    /// </summary>
    public void Say(StorySpeaker speaker, string english, double? clip = null, PlayerId? only = null)
    {
        if (only is { } player && player != Game.Me)
            return;
        _queue.Add(speaker, NameOf(speaker), Lang.T(english), _time, clip);
    }

    /// <summary>Every line at once is gone.</summary>
    public void Clear() => _queue.Clear();

    /// <summary>Whether a line is shown or waiting.</summary>
    public bool Speaking => _queue.Any;

    /// <summary>The colour a speaker's name is shown in.</summary>
    public static Color ColourOf(StorySpeaker speaker) => speaker switch
    {
        StorySpeaker.Ship => MenuTheme.Glow,
        StorySpeaker.Ike => HudInk.Gold,
        StorySpeaker.Bob => HudInk.Ice,
        StorySpeaker.Auto => HudInk.Danger,
        StorySpeaker.Whisper => new Color(0.78f, 0.72f, 0.95f),
        StorySpeaker.Locator => HudInk.Warning,
        _ => MenuTheme.Text,
    };

    private string? NameOf(StorySpeaker speaker) => speaker switch
    {
        StorySpeaker.Ship => Lang.T("Ship"),
        StorySpeaker.Auto => Lang.T("Automatic"),
        StorySpeaker.Ike => IkeName,
        StorySpeaker.Bob => BobName,
        StorySpeaker.Locator => StoryReadout.LocatorName,
        _ => null,
    };

    public override void _Process(double delta)
    {
        _time += delta;
        if (_queue.Any)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var scale = GameOptions.SubtitleScale;
        var size = GetViewportRect().Size;
        _ink.U = HudInk.ScaleFor(size);
        var shown = _queue.Shown(_time);
        if (scale <= 0 || shown.Count == 0)
            return;
        var text = 26 * scale;
        var width = size.X * 0.62f / _ink.U;
        var lineHeight = text * 1.35f * _ink.U;
        // Bottom up: the newest line lowest.
        var bottom = size.Y - Foot * _ink.U;
        foreach (var (line, alpha) in shown.Reverse())
        {
            var name = line.Name is { } who ? who.Caps() + ":" : "";
            var rows = Wrap(name.Length > 0 ? name + " " + line.Text : line.Text, text, width);
            var band = new Rect2(size.X / 2 - width * _ink.U / 2 - 24 * _ink.U, bottom - rows.Count * lineHeight - 14 * _ink.U, width * _ink.U + 48 * _ink.U, rows.Count * lineHeight + 18 * _ink.U);
            DrawBand(band, alpha);
            var y = band.Position.Y + 9 * _ink.U + text * 1.05f * _ink.U;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var start = size.X / 2 - _ink.Width(row, text) / 2;
                var at = new Vector2(start, y);
                var body = line.Speaker == StorySpeaker.Whisper ? new Color(0.86f, 0.82f, 1f, alpha * 0.85f) : new Color(0.95f, 0.96f, 0.93f, alpha);
                if (i == 0 && name.Length > 0 && row.StartsWith(name, StringComparison.Ordinal))
                {
                    _ink.Say(at, name, text, new Color(ColourOf(line.Speaker), alpha), outline: 1);
                    at.X += _ink.Width(name, text);
                    row = row[name.Length..];
                }
                _ink.Say(at, row, text, body, outline: 1);
                y += lineHeight;
            }
            bottom = band.Position.Y - 6 * _ink.U;
        }
    }

    /// <summary>Dark across the middle, clear towards the ends, as the HUD's calls are: it reads on any ground.</summary>
    private void DrawBand(Rect2 band, float alpha)
    {
        var (clear, dark) = (new Color(0, 0, 0, 0), new Color(0, 0, 0, 0.55f * alpha));
        var middle = band.GetCenter().X;
        var (top, foot) = (band.Position.Y, band.End.Y);
        DrawPolygon([new Vector2(band.Position.X, top), new Vector2(middle, top), new Vector2(middle, foot), new Vector2(band.Position.X, foot)], [clear, dark, dark, clear]);
        DrawPolygon([new Vector2(middle, top), new Vector2(band.End.X, top), new Vector2(band.End.X, foot), new Vector2(middle, foot)], [dark, clear, clear, dark]);
    }

    /// <summary>The text broken into rows no wider than <paramref name="width"/> interface pixels, at word breaks.</summary>
    private List<string> Wrap(string text, float size, float width)
    {
        var rows = new List<string>();
        var row = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tried = row.Length == 0 ? word : row + " " + word;
            if (row.Length > 0 && _ink.Width(tried, size) > width * _ink.U)
            {
                rows.Add(row);
                row = word;
            }
            else
                row = tried;
        }
        if (row.Length > 0)
            rows.Add(row);
        return rows;
    }
}
