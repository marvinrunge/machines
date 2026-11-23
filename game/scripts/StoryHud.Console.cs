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

        // Stage 7 (plan stufe7 §6.4; their German comes with package V's de.po, each said once).
        // Section 2, the Thresher (package S). Creep rides on Ctrl until S binds it (Game.StoryCreepHeld).
        ["creep"] = (null, "[Ctrl] hold: creep, half as fast and barely heard"),
        ["lamp_off"] = (KeyMap.Act.Light, "[{0}] Lamp off: with it on he sees you to the end of his beam"),
        ["shadow"] = (null, "Still in the dark: his light only shows you up close"),
        ["lane"] = (null, "He stops at the ends of his round. Cross while he stands there."),
        ["lure"] = (KeyMap.Act.GruntDistract, "[{0}] at a valve: a Grunt draws him off"),
        // For a Grunt's player only.
        ["pipe"] = (KeyMap.Act.Use, "[{0}] Bang the valve: the noise comes out at the pipe's end"),
        // The armoury (package W), the Locator's.
        ["assemble"] = (KeyMap.Act.Use, "[{0}] hold: put the Grunt back together (4 s, you stand still)"),
        ["strip"] = (KeyMap.Act.Use, "[{0}] hold at a wreck: strip it for parts"),
        ["mount"] = (KeyMap.Act.Use, "[{0}] hold at an unarmed Grunt: mount the Bolter you carry"),
        ["mend"] = (KeyMap.Act.Use, "[{0}] hold at a hurt Grunt: patch it up. Between batches nobody else will"),
        ["batch"] = (null, "The next batch comes when the bar is full. Your Grunts can block its door with crates"),
        ["zap"] = (null, "It charges up before it strikes: ram it, shoot it, or sprint out of its reach"),
        ["charge"] = (null, "When the heavy one revs, sprint out of its line: it cannot turn once it runs"),
        // The escape (package E).
        ["valve"] = (null, "Stand on the valve: the jet stays off"),
        ["wheel"] = (KeyMap.Act.Use, "[{0}] Hand wheel: the jet stays off for a while"),
        ["chock"] = (KeyMap.Act.Use, "[{0}] Push the chock into the lane"),
        ["lever"] = (KeyMap.Act.Use, "[{0}] Lane lever: two lanes stop for a moment"),
    };

    private sealed record ConsoleLine(string Text, double Since, float Seconds, float? Bar)
    {
        /// <summary>Its colour: the console's glow, else a readout's warning.</summary>
        public Color? Colour { get; init; }

        /// <summary>A hint that goes once done (the sprint's, once the player sprinted), by its word.</summary>
        public string? Tag { get; init; }

        public float Seconds { get; set; } = Seconds;
    }

    /// <summary>A readout of the hero's own systems on the console (the drive's limiter, its heat), in its colour.</summary>
    public void Readout(string text, Color colour) => _console.Add(new ConsoleLine(text, _time, 5, null) { Colour = colour });

    /// <summary>How long the sprint's hint stays at most, if the player does not sprint before.</summary>
    public const float SprintHintSeconds = 12;

    /// <summary>The sprint freed: how to sprint, until the player has sprinted (or <see cref="SprintHintSeconds"/>).</summary>
    public void SprintHint()
    {
        if (_sprinted)
            return;
        _console.Add(new ConsoleLine(Lang.T("[{0}] hold: Sprint", Lang.T("Shift")), _time, SprintHintSeconds, null) { Tag = "sprint", Colour = HudInk.Gold });
    }

    // Whether the player has sprinted once this chapter: the sprint's hint then goes, and does not come again.
    private bool _sprinted;

    /// <summary>The player sprints: the sprint's hint fades out.</summary>
    private void FollowSprint(StoryReadout now)
    {
        if (!now.Sprinting || _sprinted)
            return;
        _sprinted = true;
        foreach (var line in _console.Where(l => l.Tag == "sprint"))
            line.Seconds = MathF.Min(line.Seconds, (float)(_time - line.Since) + 0.6f);
    }

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
            _ink.Say(at, "> " + typed + cursor, 20, new Color(line.Colour ?? MenuTheme.Glow, alpha), outline: 1);
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
