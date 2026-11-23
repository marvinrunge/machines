using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// The Notabschaltung on the HUD (plan stufe7 C7, F item 8): for the Locator's player the boot run backwards over the whole
// view (its systems going dark, "CORE SECURED – AWAITING EXTERNAL POWER", the seconds left) and the restart once a cable
// is in; for a Grunt's player the Locator's marker and the cable prompt; and for everyone, once the reserve is spent, its
// pip in red. Drawn over everything (C21: the shutdown screen, then the death card, the boss bar, the rest).
public partial class StoryHud
{
    /// <summary>The systems the shutdown switches off, last booted first (the boot run backwards), one every 0.35 s.</summary>
    private static readonly string[] ShutdownSystems = ["LAMP", "RADIO", "DRIVE", "OPTICS", "SENSORS", "MEMORY"];

    private double? _downSince;

    private void DrawShutdown(StoryReadout now)
    {
        var world = _game.World;
        if (world.Story is not { } run)
            return;
        var down = world.LocatorShutDown;
        _downSince = down ? _downSince ?? _time : null;
        var seat = run.Seats.IndexOf(Game.Me);
        var middle = Left + (_size.X - Left) / 2;
        if (run.Flags.Contains(StoryMarks.ReserveUsed) && run.Flags.Contains(StoryMarks.Reserve) && !down)
            DrawReservePip(new Vector2(_size.X - 28 * _u, 92 * _u));
        if (!down)
            return;
        if (seat == 0)
        {
            DrawShutdownScreen(middle, world.CableShare, world.ShutdownLeft ?? 0);
            return;
        }
        // A Grunt's player: where the Locator lies and what to do there.
        var key = KeyMap.Name(KeyMap.Act.Use);
        var prompt = world.StoryUseTarget(Game.Me) is { } target && target.StartsWith(Machines.Sim.World.GameWorld.SeatTarget, StringComparison.Ordinal)
            && world.CableShare is null
            ? CableText(key) : Lang.T("L-7 SHUT DOWN – GET TO IT: {0} s", Mathf.CeilToInt(world.ShutdownLeft ?? 0));
        _ink.Prompt(new Vector2(middle, _size.Y * 0.58f), prompt, HudInk.Warning, 22);
    }

    /// <summary>The Grunt's prompt at the shut-down Locator (P_HUD_CABLE).</summary>
    public static string CableText(string key) => Lang.T("Hold [{0}]: plug in the cable", key);

    /// <summary>The Locator's own view while it lies shut down: black, its systems listed going dark, then the restart.</summary>
    private void DrawShutdownScreen(float middle, float? cable, float left)
    {
        var since = _time - (_downSince ?? _time);
        DrawRect(new Rect2(Vector2.Zero, _size), new Color(0, 0, 0, Mathf.Clamp((float)since * 2, 0, 0.92f)));
        var y = _size.Y * 0.32f;
        for (var i = 0; i < ShutdownSystems.Length; i++)
        {
            var off = since >= 0.35 * (i + 1);
            _ink.Say(new Vector2(middle, y + i * 26 * _u), $"{ShutdownSystems[i]} … {(off ? "OFF" : "OK")}", 17, off ? MenuTheme.Dim : MenuTheme.Text, 0.5f);
        }
        y += (ShutdownSystems.Length + 1) * 26 * _u;
        if (cable is { } share)
        {
            _ink.Say(new Vector2(middle, y), Lang.T("EXTERNAL POWER DETECTED … RESTARTING"), 24, MenuTheme.Glow, 0.5f);
            _ink.Bar(new Rect2(middle - 160 * _u, y + 22 * _u, 320 * _u, 8 * _u), share, MenuTheme.Glow);
            return;
        }
        var beat = 0.7f + 0.3f * MathF.Sin((float)_time * 3);
        _ink.Say(new Vector2(middle, y), Lang.T("EMERGENCY SHUTDOWN — CORE SECURED — AWAITING EXTERNAL POWER"), 22, new Color(HudInk.Danger, beat), 0.5f);
        _ink.Say(new Vector2(middle, y + 34 * _u), $"{Mathf.CeilToInt(left)} s", 20, MenuTheme.Text, 0.5f);
    }

    /// <summary>The reserve spent since the last checkpoint: a red pip by the hero's plate, the next shutdown is the end (P_HUD_RESERVE).</summary>
    private void DrawReservePip(Vector2 at)
    {
        DrawCircle(at, 7 * _u, HudInk.Danger);
        _ink.Say(at + new Vector2(-14 * _u, 5 * _u), Lang.T("RESERVE SPENT: NEXT SHUTDOWN = DELETION"), 13, HudInk.Danger, 1);
    }
}
