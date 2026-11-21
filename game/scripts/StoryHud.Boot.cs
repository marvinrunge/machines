using Godot;

namespace Machines.Game;

/// <summary>
/// The HUD booting in its clamp at the start of the chapter (and whenever the director sets <see cref="Machines.Sim.Story.StoryMarks.Boot"/>):
/// the boot lines of the line table (P_HUD_BOOT) type themselves out at the left of the view, the plates come one after the
/// other, each with a flicker and a short burst of static over the view (softer with the options' Soften scares).
/// </summary>
public partial class StoryHud
{
    /// <summary>The plates in the order the boot brings them.</summary>
    private enum BootPart { Hero, Tracker, Grunts, Objective, Way }

    // When each part comes, seconds into the boot, and when the boot's lines are gone.
    private static readonly float[] BootAt = [1.6f, 2.5f, 3.3f, 4.1f, 4.6f];
    private const float BootLength = 5.4f, LineGap = 0.75f;

    // The boot lines, as P_HUD_BOOT has them ("L-7 BOOT … SENSORS 61 % … LAMP OK").
    private static readonly string[] BootLines = ["L-7 BOOT", "SENSORS {0} %", "LAMP OK"];
    private const int Sensors = 61;

    // Waiting for the hero to be driven before booting; when the boot began; how many parts have come; the flag last seen.
    private bool? _bootWaits;
    private double? _bootFrom;
    private int _bootCame;
    private bool _bootFlag;

    /// <summary>Seconds into the boot: past its end when there is none (the demo holds it at one moment).</summary>
    private double BootClock => _demo is { } demo ? demo.BootAt ?? double.MaxValue : _bootWaits is true ? 0 : _bootFrom is { } from ? _time - from : double.MaxValue;

    /// <summary>Whether a part is up: once its time has come, flickering for a moment as it does.</summary>
    private bool Shows(BootPart part)
    {
        var since = BootClock - BootAt[(int)part];
        return since >= 0 && (since > 0.3 || (int)(since * 28) % 2 == 0);
    }

    /// <summary>
    /// The boot waits at the chapter's very start (checkpoint 0, first section) until the hero is driven, and starts again
    /// whenever the director raises the flag. Each part that comes brings a burst of static.
    /// </summary>
    private void FollowBoot(StoryReadout now)
    {
        // A chapter run without its director's script (the checks of the sight and the companions) has no wake-up scene.
        _bootWaits ??= now.Checkpoint == 0 && now.Section <= 1 && now.Ending is null && _game.World.Story?.Options.Unscripted != true;
        if (now.Boot && !_bootFlag)
            _bootWaits = true;
        _bootFlag = now.Boot;
        if (_bootWaits is true && Driving && _demo is null)
        {
            (_bootWaits, _bootFrom, _bootCame) = (false, _time, 0);
            Static(0.95f, 0.7f);
        }
        if (_bootFrom is not { } from)
            return;
        var clock = _time - from;
        var came = BootAt.Count(at => clock >= at);
        if (came > _bootCame)
        {
            _bootCame = came;
            Static(0.45f, 0.16f);
        }
        if (clock > BootLength + 1)
            _bootFrom = null;
    }

    /// <summary>Static over the view, halved with Soften scares.</summary>
    private void Static(float level, float seconds) => _game.Screen?.Interference(level * (GameOptions.SoftScares ? 0.5f : 1), seconds);

    /// <summary>The boot's lines typing themselves at the left of the view, the sensors counting up, gone at the boot's end.</summary>
    private void DrawBoot()
    {
        var clock = (float)BootClock;
        // Only while a boot runs (or the demo holds one): waiting for the hero to be driven, or after, nothing is written.
        if (clock > BootLength + 0.6f || _demo is null && _bootFrom is null)
            return;
        var alpha = Mathf.Clamp((BootLength + 0.6f - clock) / 0.6f, 0, 1);
        var at = new Vector2(Left + 48 * _u, _size.Y * 0.36f);
        for (var i = 0; i < BootLines.Length; i++)
        {
            var since = clock - i * LineGap;
            if (since < 0)
                break;
            var text = i == 1 ? Lang.T(BootLines[i], Mathf.RoundToInt(Mathf.Clamp(since / 0.6f, 0, 1) * Sensors)) : Lang.T(BootLines[i]);
            text = text[..Mathf.Clamp(Mathf.CeilToInt(since / 0.03f), 0, text.Length)];
            var cursor = i == BootLines.Length - 1 || clock < (i + 1) * LineGap ? ((int)(_time * 3) % 2 == 0 ? "_" : " ") : "";
            _ink.Say(at, "> " + text + cursor, 22, new Color(MenuTheme.Glow, alpha), outline: 1);
            at.Y += 30 * _u;
        }
    }
}
