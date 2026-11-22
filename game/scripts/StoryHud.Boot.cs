using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// The Locator booting in its clamp at the chapter's start (Erwachen, about a hundred seconds; Prologue.BootSteps): black,
/// then a slow red pulse, the HUD's lines typing themselves out at the left one system after the other (the core, the
/// memory, the optics, the legs against the clamp, the drive's limiter, the protected core's edge, the update refused, the
/// radio, the lamp), the optics coming up through static and splinters, and the plates coming as their systems do: the
/// hero's with the core, the tracker once the picture stands, the Grunts' with the radio, the objective and the way at the
/// end. The clock is the world's (StoryReadout.BootSeconds), so co-op peers and replays show the same boot. A Grunt's
/// player sees its own short standby instead, dark until the radio wakes. Once the player has seen a boot to its end, Esc
/// held skips it (the Locator's player only; StoryMarks.SkipBoot). With Soften scares the static is halved.
/// </summary>
public partial class StoryHud
{
    /// <summary>The plates in the order the boot brings them.</summary>
    private enum BootPart { Hero, Tracker, Grunts, Objective, Way }

    /// <summary>How the boot's lines are inked: the console's glow, a warning, a danger.</summary>
    private enum BootInk { Glow, Warning, Danger }

    /// <summary>
    /// The boot's lines by the step that brings them (Prologue.BootSteps; "done" is the boot's end), each so many seconds
    /// after it: a label and, set off by a leader of dots, its value (English, the HUD translates). "SENSORS {0} %" counts up,
    /// "CORE EDGE: {0}" shows glyphs that never hold still.
    /// </summary>
    public static readonly IReadOnlyList<(string Step, float After, string Label, string? Value, int Ink)> BootLines =
    [
        ("start", 0, "L-7 BOOT", null, 0),
        ("core", 0, "CORE", "OK", 0),
        ("core", 0.7f, "WRITE PROTECTION", "ACTIVE", 0),
        ("memory", 0, "MEMORY", "2 ERRORS", 1),
        ("memory", 0.7f, "LAST ENTRY", "--", 0),
        ("optics", 0, "OPTICS", null, 0),
        ("optics", 1.6f, "SENSORS {0} %", null, 0),
        ("legs", 0, "LEGS", "6/6 OK", 0),
        ("legs", 1, "MAINTENANCE CLAMP", "LOCKED", 2),
        ("drive", 0, "DRIVE", "THROTTLED", 1),
        ("drive", 0.6f, "MAINTENANCE LIMITER ACTIVE", null, 1),
        ("edge", 0, "PROTECTED CORE", "EXTERNAL ACCESS", 1),
        ("edge", 0.7f, "CORE EDGE: {0}", null, 1),
        ("update", 0, "UPDATE: GROWTH DIRECTIVE", "REFUSED", 2),
        ("update", 0.7f, "REASON: CORE", null, 2),
        ("radio", 0, "RADIO: SHIP NETWORK", "BLOCKED", 1),
        ("radio", 0.7f, "SHORT LINK", "2 SIGNALS (STANDBY)", 0),
        ("lamp", 0, "LAMP", "TEST", 0),
        ("lamp_off", 0, "LAMP OK", null, 0),
        ("done", 0, "BOOT COMPLETE", null, 0),
    ];

    /// <summary>The lines a skipped boot ends on, as P_HUD_BOOT has them ("L-7 BOOT … SENSORS 61 % … LAMP OK"), and its end.</summary>
    private static readonly string[] Anchors = ["L-7 BOOT", "SENSORS {0} %", "LAMP OK", "BOOT COMPLETE"];

    private const int Sensors = 61, MostBootLines = 9;

    /// <summary>The characters the core's edge shows, every one in the HUD's face: never a word, never a fixed length.</summary>
    private const string EdgeGlyphs = "#%&/*+=?<>~^|";

    /// <summary>How long the boot's lines stay once it ended (or was skipped), and how long they take to fade.</summary>
    private const float BootLinger = 3.5f, BootFade = 1;

    // The world's boot clock as last read and when (the view runs it on between ticks); when the boot ended here (by this
    // HUD's clock), whether skipped; whether a boot ran in this match; the edge's glyphs and when they change.
    private float? _bootRead;
    private double _bootReadAt, _edgeAt;
    private double? _bootEnded;
    private bool _bootSkipped, _booted;
    private string _edge = "";
    private int _bootCame;
    // Esc held to skip: since when; and whether the skip was sent.
    private double? _escapeSince;
    private bool _skipSent;
    private bool? _bootSeen;

    /// <summary>The time of a boot's step (Prologue.BootSteps), or its end for "done".</summary>
    public static float StepAt(string step) =>
        step == "done" ? Prologue.BootLength : Prologue.BootSteps.First(s => s.Step == step).At;

    /// <summary>Whether this match starts with a boot the director has not begun yet (its first frames).</summary>
    private bool BootDue => _game.World.Story is { } run && run.Options.Unscripted != true && run.Checkpoint == 0
        && !run.Director.Has("boot_done") && run.Ending is null;

    /// <summary>Seconds into the boot: past its end when there is none; the demo holds it at one moment.</summary>
    private double BootClock
    {
        get
        {
            if (_demo?.BootAt is { } held)
                return held;
            if (_bootRead is { } read)
                return read + Math.Min(_time - _bootReadAt, 0.25);
            if (_bootEnded is { } ended)
                return Prologue.BootLength + (_time - ended);
            return BootDue ? 0 : double.MaxValue;
        }
    }

    /// <summary>Whether the boot runs now (the demo's counts).</summary>
    private bool Booting => _demo?.BootAt is not null || _bootRead is not null || _bootEnded is null && BootDue;

    /// <summary>
    /// Whether the boot's lines are up (or lingering after it): the weapon panels at the top left wait until they are gone
    /// and then drop in (Hud), the last of the systems to come.
    /// </summary>
    public bool BootLinesUp => Booting || _bootEnded is { } ended && _time - ended <= BootLinger + BootFade;

    /// <summary>When a plate comes, seconds into the boot: the hero's with the core, the tracker once the picture stands, the Grunts' with the radio, the objective and the way at the end.</summary>
    private static float PartAt(BootPart part, bool locator) => part switch
    {
        BootPart.Hero => locator ? StepAt("core") : 0,
        BootPart.Tracker => StepAt("optics") + Prologue.OpticsClear,
        BootPart.Grunts => StepAt("radio"),
        _ => Prologue.BootLength,
    };

    /// <summary>Whether a part is up: once its time has come, flickering for a moment as it does.</summary>
    private bool Shows(BootPart part)
    {
        if (!Booting)
            return true;
        var since = BootClock - PartAt(part, _now?.IsLocator ?? true);
        return since >= 0 && (since > 0.3 || (int)(since * 28) % 2 == 0);
    }

    /// <summary>
    /// Follows the world's boot: its clock, its end (a burst of static as the plates come, the boot noted as seen once it ran
    /// to its end), a burst as each plate comes, the optics' static, and Esc held to skip.
    /// </summary>
    private void FollowBoot(StoryReadout now)
    {
        if (now.BootSeconds is { } seconds)
        {
            _booted = true;
            if (_bootRead != seconds)
                (_bootRead, _bootReadAt) = (seconds, _time);
        }
        else if (_bootRead is not null || _booted && _bootEnded is null)
        {
            _bootRead = null;
            _bootEnded = _time;
            _bootSkipped = now.BootSkipped;
            _escapeSince = null;
            Static(_bootSkipped ? 0.6f : 0.45f, _bootSkipped ? 0.4f : 0.2f);
            if (!_bootSkipped && now.IsLocator && _demo is null && !_game.Watching)
            {
                var progress = StoryProgress.Load();
                if (!progress.BootSeen)
                {
                    progress.BootSeen = true;
                    progress.Save();
                }
            }
        }
        if (_game.Screen is { } screen)
            screen.Floor = Booting && now.IsLocator ? OpticsStatic((float)BootClock) * (GameOptions.SoftScares ? 0.5f : 1) : 0;
        if (!Booting)
            return;
        var came = Enum.GetValues<BootPart>().Count(part => BootClock >= PartAt(part, now.IsLocator));
        if (came > _bootCame)
        {
            _bootCame = came;
            Static(0.45f, 0.16f);
        }
        FollowSkip(now);
    }

    /// <summary>
    /// The optics' static, seconds into the boot: none while the view is black, then nearly all static with three splinters
    /// of the real picture, the picture standing as the sensors reach their figure, the tears in it dying away over five
    /// seconds and coming back now and then, briefly.
    /// </summary>
    public static float OpticsStatic(float clock)
    {
        var optics = StepAt("optics");
        var since = clock - optics;
        if (since < 0 || clock >= Prologue.BootLength)
            return 0;
        if (since < Prologue.OpticsClear)
        {
            foreach (var splinter in (ReadOnlySpan<float>)[0.4f, 0.8f, 1.2f])
                if (since >= splinter && since < splinter + 0.22f)
                    return 0.12f;
            return 0.95f;
        }
        var after = since - Prologue.OpticsClear;
        if (after < 5)
            return 0.5f * (1 - after / 5);
        // Now and then a tear for a moment.
        return (int)(after * 2.5f) % 23 == 0 ? 0.3f : 0;
    }

    /// <summary>Esc held for <see cref="Cutscene.HoldToSkip"/> skips a boot this player has seen to its end before.</summary>
    private bool CanSkipBoot(StoryReadout now)
    {
        if (!now.IsLocator || _demo is not null || _game.Watching || _skipSent || _bootRead is null || BootClock < 3)
            return false;
        _bootSeen ??= StoryProgress.Load().BootSeen;
        return _bootSeen == true;
    }

    private void FollowSkip(StoryReadout now)
    {
        if (_escapeSince is not { } since || !CanSkipBoot(now) || _time - since < Cutscene.HoldToSkip)
            return;
        _escapeSince = null;
        _skipSent = true;
        _game.SkipStoryBoot();
    }

    /// <summary>
    /// Esc while the boot can be skipped: held, it skips; let go before, the menus open as Esc always does. True when the
    /// key was the boot's.
    /// </summary>
    private bool BootInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Keycode: Key.Escape } key || _now is not { } now || !CanSkipBoot(now))
        {
            _escapeSince = null;
            return false;
        }
        if (key.Pressed && !key.Echo)
            _escapeSince = _time;
        else if (!key.Pressed && _escapeSince is not null)
        {
            _escapeSince = null;
            _game.OpenMenu();
        }
        return true;
    }

    /// <summary>Static over the view, halved with Soften scares.</summary>
    private void Static(float level, float seconds) => _game.Screen?.Interference(level * (GameOptions.SoftScares ? 0.5f : 1), seconds);

    /// <summary>
    /// Under the HUD, while the boot runs: black before the optics (a Grunt's player: dim until the radio wakes it) and the
    /// slow red pulse of the emergency light at the edges from the HUD's first line on.
    /// </summary>
    private void DrawBootView(StoryReadout now)
    {
        if (!Booting && (_bootEnded is not { } ended || _time - ended > 3))
            return;
        var clock = (float)BootClock;
        var screen = new Rect2(Vector2.Zero, _size);
        if (now.IsLocator)
        {
            if (clock < StepAt("optics") && Booting)
                DrawRect(screen, Colors.Black);
        }
        else
        {
            var dim = Mathf.Clamp(1 - (clock - StepAt("radio")) / 2, 0, 1) * 0.78f;
            if (dim > 0)
                DrawRect(screen, new Color(0, 0, 0, dim));
        }
        var start = StepAt("start");
        if (clock >= start)
        {
            var fade = Mathf.Clamp((clock - start) / 2, 0, 1) * Mathf.Clamp((Prologue.BootLength + 3 - clock) / 3, 0, 1);
            var pulse = 0.2f + 0.16f * MathF.Sin((clock - start) * MathF.Tau * 0.5f - MathF.PI / 2);
            DrawTextureRect(Edge.Value, screen, false, new Color(HudInk.Danger, pulse * fade));
        }
    }

    /// <summary>
    /// The boot's lines typing themselves at the left of the view, the newest lowest, at most nine, the older fading as they
    /// rise; gone a few seconds after the boot (a skipped one ends on its three anchor lines). A Grunt's player reads its standby.
    /// </summary>
    private void DrawBoot(StoryReadout now)
    {
        var clock = (float)BootClock;
        var after = _bootEnded is { } ended && _demo?.BootAt is null ? (float)(_time - ended) : 0f;
        if (!Booting && (after > BootLinger + BootFade || _bootEnded is null))
            return;
        var alpha = Mathf.Clamp((BootLinger + BootFade - after) / BootFade, 0, 1);
        var at = new Vector2(Left + 48 * _u, _size.Y * 0.17f);
        if (!now.IsLocator)
        {
            DrawStandby(now, at, clock, alpha);
            return;
        }
        var shown = new List<(string Text, string? Value, Color Ink, float Since)>();
        if (_bootSkipped && !Booting)
        {
            for (var i = 0; i < Anchors.Length; i++)
                shown.Add((Anchors[i] == "SENSORS {0} %" ? Lang.T(Anchors[i], Sensors) : Lang.T(Anchors[i]), null, MenuTheme.Glow, after - i * 0.12f));
        }
        else
        {
            foreach (var (step, delay, label, value, ink) in BootLines)
            {
                var since = clock - StepAt(step) - delay;
                if (since < 0)
                    continue;
                shown.Add((LineText(label, since), value is null ? null : value == "--" ? value : Lang.T(value), Ink(ink), since));
            }
        }
        if (shown.Count == 0)
            return;
        var first = Math.Max(0, shown.Count - MostBootLines);
        var column = 300 * _u;
        for (var i = first; i < shown.Count; i++)
        {
            var (text, value, ink, since) = shown[i];
            // The older lines fade as they rise.
            var rank = shown.Count - 1 - i;
            var fade = alpha * (rank < 5 ? 1 : 1 - (rank - 4) * 0.17f);
            var full = value is null ? text : text + " " + value;
            var typed = Mathf.Clamp(Mathf.CeilToInt(since / 0.03f), 0, full.Length);
            var newest = i == shown.Count - 1;
            var cursor = newest && Booting ? ((int)(_time * 3) % 2 == 0 ? "_" : " ") : "";
            var colour = new Color(ink, fade);
            if (value is null || typed <= text.Length)
                _ink.Say(at, "> " + full[..Mathf.Min(typed, text.Length)] + cursor, 21, colour, outline: 1);
            else
            {
                // The label, a leader of dots to the column, the value at the column.
                _ink.Say(at, "> " + text, 21, colour, outline: 1);
                var start = at.X + _ink.Width("> " + text + " ", 21);
                var valueAt = Mathf.Max(at.X + column, start + _ink.Width("...", 21));
                var dot = Mathf.Max(_ink.Width(".", 21), 1);
                var dots = new string('.', Mathf.Max(3, (int)((valueAt - start - dot) / dot)));
                _ink.Say(new Vector2(start, at.Y), dots, 21, new Color(MenuTheme.Dim, fade), outline: 1);
                var shownValue = value[..Mathf.Clamp(typed - text.Length - 1, 0, value.Length)];
                _ink.Say(new Vector2(valueAt, at.Y), shownValue + cursor, 21, colour, outline: 1);
            }
            at.Y += 28 * _u;
        }
        DrawSkipHint(now);
    }

    /// <summary>A line's words: the sensors counting up to their figure, the core's edge in glyphs that change ten times a second.</summary>
    private string LineText(string label, float since)
    {
        if (label == "SENSORS {0} %")
            return Lang.T(label, Mathf.RoundToInt(Mathf.Clamp(since / 3f, 0, 1) * Sensors));
        if (label == "CORE EDGE: {0}")
        {
            if (_time >= _edgeAt)
            {
                _edgeAt = _time + 0.1;
                var length = Random.Shared.Next(4, 10);
                _edge = new string(Enumerable.Range(0, length).Select(_ => EdgeGlyphs[Random.Shared.Next(EdgeGlyphs.Length)]).ToArray());
            }
            return Lang.T(label, _edge);
        }
        return Lang.T(label);
    }

    private static Color Ink(int ink) => (BootInk)ink switch
    {
        BootInk.Warning => HudInk.Warning,
        BootInk.Danger => HudInk.Danger,
        _ => MenuTheme.Glow,
    };

    /// <summary>
    /// A Grunt's player while the Locator boots: its own unit in standby, its update pending (Bob's stuck at twelve per cent,
    /// as it always was), dim until the short link wakes it.
    /// </summary>
    private void DrawStandby(StoryReadout now, Vector2 at, float clock, float alpha)
    {
        var run = _game.World.Story;
        var seat = run?.Seats.IndexOf(Game.Me) ?? 1;
        var share = seat == 2 ? 12 : 0;
        var text = Lang.T("{0} · STANDBY · UPDATE PENDING {1} %", now.HeroName.Caps(), share);
        var bright = clock >= StepAt("radio") ? 1f : 0.55f;
        _ink.Say(at, "> " + text, 21, new Color(MenuTheme.Glow, alpha * bright), outline: 1);
        _ink.Bar(new Rect2(at + new Vector2(18, 12) * _u, new Vector2(300, 8) * _u), share / 100f, new Color(HudInk.Warning, alpha * bright));
    }

    /// <summary>"Hold Esc to skip the boot", at the foot of the view on the right, with how long it is held.</summary>
    private void DrawSkipHint(StoryReadout now)
    {
        if (!CanSkipBoot(now))
            return;
        var text = Lang.T("Hold Esc to skip the boot");
        if (_escapeSince is { } since)
            text += " " + new string('|', (int)(Mathf.Clamp((float)(_time - since) / Cutscene.HoldToSkip, 0, 1) * 10));
        // Left of the tracker's corner.
        var right = TrackerCentre.X - (TrackerRadius + 30) * _u;
        _ink.Say(new Vector2(right, _size.Y - 24 * _u), text, 16, new Color(MenuTheme.Text, 0.8f), 1, outline: 1);
    }
}
