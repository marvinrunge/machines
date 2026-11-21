using Godot;

namespace Machines.Game;

/// <summary>
/// The HUD's own lines at the left of the view, under where the boot writes (docs/STORY.md: "Das Tutorial steckt in der
/// Handlung"): the hints the director gives as the chapter goes (the lamp after the boot, keeping still and the scanner's
/// pulse on the dark decks, the ducts), and the core's decryption with its bar running up to 37 % (P_HUD_DECRYPT, which
/// act 1 takes up again). Each types itself out and fades after a while.
/// </summary>
public partial class StoryHud
{
    /// <summary>How far the decryption gets before the Locator breaks free (docs/STORY.md, Erwachen).</summary>
    public const int DecryptShare = 37;

    /// <summary>The hints by the director's word ("hint lamp"): the key they name, if any, and the English with {0} for it.</summary>
    public static readonly IReadOnlyDictionary<string, (KeyMap.Act? Key, string Text)> Hints = new Dictionary<string, (KeyMap.Act?, string)>
    {
        ["lamp"] = (KeyMap.Act.Light, "[{0}] Lamp: lights your way, and gives you away"),
        ["still"] = (null, "Standing still in the dark, you are all but unseen"),
        ["pulse"] = (KeyMap.Act.Scan, "[{0}] Scanner pulse: shows the hunters near you, and they hear it"),
        ["duct"] = (null, "Only you fit through the ducts: the Grunts take the long way"),
        // For a Grunt's player only (Game.StoryCue), as the Locator starts on the core.
        ["crates"] = (null, "Drive into a crate to push it; at a door's barricade it locks into place. Back up to set it down"),
        // For the Locator's player only, at the break-in.
        ["cover"] = (null, "Into cover: the Reapers take aim for a moment before they fire"),
    };

    private sealed record ConsoleLine(string Text, double Since, float Seconds, float? Bar);

    private readonly List<ConsoleLine> _console = [];

    /// <summary>A hint by its word (<see cref="Hints"/>); none for a word it does not know.</summary>
    public void Hint(string word)
    {
        if (!Hints.TryGetValue(word, out var hint))
            return;
        var text = hint.Key is { } key ? Lang.T(hint.Text, KeyMap.Name(key)) : Lang.T(hint.Text);
        _console.Add(new ConsoleLine(text, _time, 9, null));
    }

    /// <summary>The core's decryption: its line, the share counting up to <see cref="DecryptShare"/> % on a bar under it.</summary>
    public void Decrypt() => _console.Add(new ConsoleLine(Lang.T("Decrypting protected core: {0} %"), _time, 10, DecryptShare / 100f));

    private void DrawConsole()
    {
        _console.RemoveAll(line => _time - line.Since > line.Seconds);
        if (_console.Count == 0)
            return;
        // Below the boot's three lines, one under the other.
        var at = new Vector2(Left + 48 * _u, _size.Y * 0.36f + 110 * _u);
        foreach (var line in _console)
        {
            var age = (float)(_time - line.Since);
            var alpha = Mathf.Clamp(Mathf.Min(age / 0.2f, (line.Seconds - age) / 0.6f), 0, 1);
            var text = line.Text;
            var share = 0f;
            if (line.Bar is { } full)
            {
                share = Mathf.Clamp(age / 3f, 0, 1) * full;
                text = string.Format(text, Mathf.RoundToInt(share * 100));
            }
            var typed = text[..Mathf.Clamp(Mathf.CeilToInt(age / 0.025f), 0, text.Length)];
            var cursor = typed.Length < text.Length || (int)(_time * 3) % 2 == 0 ? "_" : " ";
            _ink.Say(at, "> " + typed + cursor, 20, new Color(MenuTheme.Glow, alpha), outline: 1);
            at.Y += 26 * _u;
            if (line.Bar is not null)
            {
                var bar = new Rect2(at + new Vector2(18, -14) * _u, new Vector2(300, 9) * _u);
                _ink.Bar(bar, share, new Color(HudInk.Warning, alpha));
                at.Y += 16 * _u;
            }
        }
    }
}
