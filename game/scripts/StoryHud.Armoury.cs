using System.Globalization;
using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Package W's HUD (plan stufe7 §4 W, design-waves §8.1): under the objective the barracks' bar ("BARRACKS · UPDATE BATCH
// 2: 64 %") with the door the next batch comes by, "BATCH n: DEPLOYED" while it fights; the heavy Grunt's bar (OLD IRON,
// its mass); the Bolter L-7 carries; the prompts of mend, mount and strip; and a mark over each wreck that has something to
// strip, greyed once bare. Drawn under the shutdown screen and the death card (C21).
public partial class StoryHud
{
    private void DrawArmoury(StoryReadout now)
    {
        if (_game.World is not { Story: { Section: 6 } run } world)
            return;
        var waves = run.Waves;
        var middle = Left + (_size.X - Left) / 2;
        var y = 70 * _u;
        if (run.Flags.Contains(StoryMarks.ArmourySealed) && waves.Wave > 0)
            y = DrawBatch(world, run, middle, y);
        if (waves.Boss != 0 && world.Find<Machine>(waves.Boss) is { IsDead: false } boss)
            DrawBoss(boss, middle, y);
        DrawWrecks(world, run);
        if (now.IsLocator)
        {
            if (waves.Carried is not null)
                _ink.Prompt(new Vector2(Left + 120 * _u, _size.Y - 150 * _u), Lang.T("BOLTER CARRIED"), HudInk.Gold, 15);
            if (Driving && now.Ending is null && ArmouryPrompt(world, run) is { } prompt)
                _ink.Prompt(new Vector2(middle, _size.Y * 0.64f), prompt, MenuTheme.Glow, 24);
        }
        else if (world.BeingWorkedOn(run, Game.Me) && run.Crew.Holding.TryGetValue(run.Locator.Value, out var what)
            && what.StartsWith(GameWorld.MountTarget, StringComparison.Ordinal))
            _ink.Prompt(new Vector2(middle, _size.Y * 0.64f), Lang.T("Hold still: L-7 is mounting"), MenuTheme.Glow, 22);
    }

    private float DrawBatch(GameWorld world, StoryRun run, float middle, float y)
    {
        var waves = run.Waves;
        var box = new Rect2(middle - 230 * _u, y, 460 * _u, 54 * _u);
        _ink.Plate(box, HudInk.Plating, MenuTheme.Edge, 10);
        var share = waves.Bar(world.Time);
        var title = waves.Fighting
            ? Lang.T("BATCH {0}: DEPLOYED", waves.Wave)
            : Lang.T("BARRACKS · UPDATE BATCH {0}: {1} %", waves.Wave, Mathf.FloorToInt(share * 100));
        _ink.Say(box.Position + new Vector2(14, 20) * _u, title, 15, waves.Fighting ? HudInk.Danger : MenuTheme.Text);
        _ink.Say(new Vector2(box.End.X - 14 * _u, box.Position.Y + 20 * _u), $"{waves.Wave}/{StoryWaves.Plan.Count}", 13, MenuTheme.Dim, 1);
        _ink.Leds(new Rect2(box.Position + new Vector2(14, 28) * _u, new Vector2(240, 16) * _u), share, waves.Fighting ? HudInk.Danger : HudInk.Warning, (float)_time, 20);
        if (!waves.Fighting && waves.Wave <= StoryWaves.Plan.Count)
        {
            var doors = string.Join(" + ", StoryWaves.DoorsOf(waves.Wave).Select(d => Lang.T(d switch { 'R' => "WEST DOOR", 'T' => "EAST DOOR", _ => "FREIGHT GATE" })));
            var next = waves.Wave == StoryWaves.Plan.Count
                ? Lang.T("BATCH {0}: 1 UNIT · MASS {1} t", waves.Wave, Mass())
                : Lang.T("NEXT BATCH: {0}", doors);
            _ink.Say(new Vector2(box.End.X - 14 * _u, box.Position.Y + 44 * _u), next, 13, HudInk.Gold, 1);
        }
        return box.End.Y + 6 * _u;
    }

    /// <summary>The heavy Grunt's mass as the language writes it (3,2 in German).</summary>
    private static string Mass() =>
        StoryHeavy.MassTonnes.ToString("0.0", Lang.Code == "de" ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture);

    private void DrawBoss(Machine boss, float middle, float y)
    {
        var box = new Rect2(middle - 230 * _u, y, 460 * _u, 46 * _u);
        _ink.Plate(box, HudInk.Plating, HudInk.Danger, 10);
        _ink.Say(box.Position + new Vector2(14, 19) * _u, Lang.T("OLD IRON"), 16, HudInk.Danger);
        _ink.Say(new Vector2(box.End.X - 14 * _u, box.Position.Y + 19 * _u), Lang.T("MASS {0} t", Mass()), 13, MenuTheme.Dim, 1);
        _ink.Bar(new Rect2(box.Position + new Vector2(14, 28) * _u, new Vector2(432, 10) * _u), Mathf.Clamp((float)boss.HitPoints / Mathf.Max(1, boss.MaxHitPoints), 0, 1), HudInk.Danger);
    }

    /// <summary>The prompt of the armoury's Use the Locator stands at (mend, mount, strip), with the key; null when none.</summary>
    private string? ArmouryPrompt(GameWorld world, StoryRun run)
    {
        if (world.StoryUseTarget(Game.Me) is not { } target)
            return null;
        var key = KeyMap.Name(KeyMap.Act.Use);
        if (target.StartsWith(GameWorld.MendTarget, StringComparison.Ordinal) && Seat(target[GameWorld.MendTarget.Length..]) is { } mend
            && world.HeroOf(mend) is { } hurt)
            return Lang.T("Mend ({0}): {1} {2} %", key, StoryReadout.NameOf(world, run, mend), Mathf.RoundToInt(100f * hurt.HitPoints / Mathf.Max(1, hurt.MaxHitPoints)));
        if (target.StartsWith(GameWorld.MountTarget, StringComparison.Ordinal) && Seat(target[GameWorld.MountTarget.Length..]) is { } mount)
            return Lang.T("Mount ({0}): {1}", key, StoryReadout.NameOf(world, run, mount));
        if (target.StartsWith(StoryMarks.WreckTarget, StringComparison.Ordinal)
            && int.TryParse(target[StoryMarks.WreckTarget.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && run.Waves.Wrecks.TryGetValue(id, out var wreck))
            return Lang.T("Strip ({0}): {1}", key, WreckName(run, wreck));
        return null;
    }

    private static PlayerId? Seat(string digits) =>
        int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? new PlayerId(id) : null;

    /// <summary>What a wreck is called on its prompt and its mark: what stripping it yields now.</summary>
    private static string WreckName(StoryRun run, StoryWreck wreck) => GameWorld.StripYield(run, wreck) switch
    {
        StoryWaves.Grunt2Drive => Lang.T("Grunt 2 drive"),
        StoryWaves.ElectricCharge => Lang.T("electric charge"),
        _ => Lang.T(wreck.Kind is StoryWaveKind.Tank or StoryWaveKind.TankZap ? "Grunt 2 (Bolter)" : "Grunt 1 (Bolter)"),
    };

    /// <summary>A mark over each wreck within 45 m that has something to strip (gold), or that was stripped bare (grey).</summary>
    private void DrawWrecks(GameWorld world, StoryRun run)
    {
        if (GetViewport().GetCamera3D() is not { } camera || world.HeroOf(Game.Me) is not { } me)
            return;
        foreach (var wreck in run.Waves.Wrecks.Values)
        {
            if (NVector2.Distance(wreck.Position, me.Position) > 45)
                continue;
            var yields = GameWorld.StripYield(run, wreck) is not null;
            if (!yields && !wreck.Stripped)
                continue;
            var at = ActorView.World(wreck.Position) + Vector3.Up * 3;
            if (camera.IsPositionBehind(at))
                continue;
            var screen = camera.UnprojectPosition(at);
            var colour = yields ? HudInk.Gold : new Color(MenuTheme.Dim, 0.6f);
            DrawPolygon([screen + new Vector2(0, -7) * _u, screen + new Vector2(7, 0) * _u, screen + new Vector2(0, 7) * _u, screen + new Vector2(-7, 0) * _u], [colour]);
            if (yields)
                _ink.Say(screen + new Vector2(0, -12) * _u, WreckName(run, wreck), 12, colour, 0.5f, 2);
        }
    }

    /// <summary>How far the Locator's armoury hold has come (mend: the Grunt's hit points; mount and strip: their time), null when it holds none.</summary>
    private float? ArmouryHoldShare(StoryRun run, string what)
    {
        if (!run.Waves.Holds.TryGetValue(Game.Me.Value, out var hold) || hold.Target != what)
            return null;
        if (what.StartsWith(GameWorld.MendTarget, StringComparison.Ordinal))
            return Seat(what[GameWorld.MendTarget.Length..]) is { } seat && _game.World.HeroOf(seat) is { } grunt
                ? (float)grunt.HitPoints / Mathf.Max(1, grunt.MaxHitPoints) : null;
        var seconds = what.StartsWith(GameWorld.MountTarget, StringComparison.Ordinal) ? StoryWaves.MountSeconds : StoryWaves.StripSeconds;
        return Math.Min(1, hold.Ticks / (seconds * GameWorld.TicksPerSecond));
    }
}
