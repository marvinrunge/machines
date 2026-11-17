using Godot;
using Machines.Net;
using Machines.Sim.World;

namespace Machines.Game;

// Online recovery in the match (P2 design §5.4, S4): what a player had in hand carried over the reload of a rebuild, and the
// automated checks' switches that make a world fall behind or go out of step on purpose.
public partial class Game
{
    // The machine driven in first person before the rebuild, driven again once the player is back; whether the carried things
    // were taken (once, after the warm-up).
    private int? _carriedFirstPerson;
    private bool _carryTaken;

    /// <summary>What this player has in hand goes over the reload of a rebuild of the same player (<see cref="ResyncCarry"/>).</summary>
    private void CarryOver()
    {
        var camera = _rig.Snapshot with { Ground = !ZenithCamera };
        ResyncCarry.Keep(new ResyncCarry.Kept(Me, camera, (CameraRig.View?[])_savedViews.Clone(), [.. _selection], _driven?.Id,
            [.. _hud.Chat.Lines], Fog?.Export()));
        GD.Print($"net: kept for the rebuild: camera at {camera.Position.X:0},{camera.Position.Z:0}, selection [{string.Join(", ", _selection)}], " +
                 $"first person {_driven?.Id.ToString() ?? "no"}, {_hud.Chat.Lines.Count} chat lines");
    }

    /// <summary>
    /// Once the rebuilt scene is warmed up, what was kept before the reload comes back: the camera and its kept places, the
    /// selection, the chat and the fog; first person once the player is back in the match. A run's view is its hero's, which
    /// <see cref="BeginRun"/> sees to.
    /// </summary>
    private void TakeCarry()
    {
        if (_carryTaken)
            return;
        _carryTaken = true;
        if (MatchRequest.Current.Rebuild is null || ResyncCarry.Take(Me) is not { } kept)
            return;
        Array.Copy(kept.Views, _savedViews, Math.Min(kept.Views.Length, _savedViews.Length));
        if (World.Wired is null)
        {
            ShowView(kept.Camera);
            SelectQuietly(kept.Selection);
            _carriedFirstPerson = kept.FirstPerson;
        }
        _hud.Chat.Carry(kept.Chat);
        if (kept.Fog is { } fog)
            Fog?.Import(fog);
        GD.Print($"net: carried over the rebuild: camera at {kept.Camera.Position.X:0},{kept.Camera.Position.Z:0}, selection [{string.Join(", ", _selection)}], " +
                 $"first person {kept.FirstPerson?.ToString() ?? "no"}, {kept.Chat.Count} chat lines, fog {(kept.Fog is null ? "none" : "kept")}");
    }

    /// <summary>The selection set without the first machine's answer: nothing was chosen anew.</summary>
    private void SelectQuietly(IReadOnlyList<int> ids)
    {
        foreach (var id in _selection)
            if (_views.TryGetValue(id, out var view)) view.Selected = false;
        _selection.Clear();
        _selection.AddRange(ids.Where(id => World.Find(id) is not null));
        foreach (var id in _selection)
            if (_views.TryGetValue(id, out var view)) view.Selected = true;
    }

    // ---- automated checks ------------------------------------------------------------------------

    // With MACHINES_BANNER_SHOTS: pictures of the recovery's moments still to be kept, by when.
    private readonly List<(string Name, double At)> _momentShots = [];

    /// <summary>
    /// For automated checks with MACHINES_BANNER_SHOTS=&lt;folder&gt;[,&lt;name&gt;]: a picture of a moment of the recovery (out of step on
    /// purpose, away, back in the match, another's coming or going) <paramref name="seconds"/> later, as &lt;name&gt;-&lt;moment&gt;.png.
    /// </summary>
    private void PictureSoon(string moment, double seconds)
    {
        if (BannerShots is { Length: > 0 })
            _momentShots.Add((moment, Time.GetTicksMsec() / 1000.0 + seconds));
    }

    /// <summary>The moments' pictures that are due, over the match itself.</summary>
    private void TakeMomentShots()
    {
        if (_momentShots.Count == 0 || Hidden || LoadingScreen.Busy || BannerShots is not { Length: > 0 } setting)
            return;
        var now = Time.GetTicksMsec() / 1000.0;
        foreach (var (moment, _) in _momentShots.Where(shot => shot.At <= now).ToList())
        {
            _momentShots.RemoveAll(shot => shot.Name == moment);
            var words = setting.Split(',');
            System.IO.Directory.CreateDirectory(words[0]);
            var file = System.IO.Path.Combine(words[0], $"{(words.Length > 1 ? words[1] : "game")}-{moment}.png");
            GetViewport().GetTexture().GetImage().SavePng(file);
            GD.Print($"net: moment {moment} at tick {World.Tick} pictured");
        }
    }

    /// <summary>MACHINES_DESYNC_AT=&lt;tick&gt;: the world goes out of step on purpose after that tick, once a run of the game.</summary>
    private static readonly long? DesyncAt = long.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_DESYNC_AT"), out var tick) ? tick : null;

    private static bool _desynced;

    /// <summary>
    /// For automated checks (S4): MACHINES_DESYNC_AT=&lt;tick&gt; takes a hit point off this player's hero (outside a run, off
    /// their machine with the lowest id, which is also selected and looked at so that a check sees both carried over the
    /// rebuild; without one, a BMU goes to their race) in this world alone after that tick, outside lockstep, once a run of the
    /// game: the relay finds the world out of step at its next checksum and rebuilds it (P2 design §5.2).
    /// </summary>
    private void GoOutOfStepIfAsked()
    {
        if (_desynced || DesyncAt is not { } at || World.Tick <= at)
            return;
        _desynced = true;
        var victim = World.HeroOf(Me) is { IsDead: false, HitPoints: > 1 } hero ? hero
            : World.Machines.Where(m => m.Owner == Me && !m.IsDead && m.HitPoints > 1).MinBy(m => m.Id);
        if (victim is not null)
            victim.HitPoints -= 1;
        else
            World.Races[Player].Bmu += 1;
        GD.Print($"net: out of step on purpose at tick {World.Tick}: " +
                 (victim is not null ? $"machine {victim.Id} has {victim.HitPoints} hit points in this world alone" : "a BMU more in this world alone"));
        if (victim is not null && World.Wired is null && !Watching)
        {
            SelectQuietly([victim.Id]);
            LookAtActor(victim);
        }
        // At once: the relay finds the world out of step within a second, and the scene is loaded anew.
        PictureSoon("desync", 0.2);
    }

    /// <summary>
    /// MACHINES_THROTTLE=&lt;from tick&gt;,&lt;seconds&gt;,&lt;ticks per second&gt;: from that tick the world runs at most that many ticks a
    /// second for that long, catching up included, once a run of the game, as a computer that cannot keep up would.
    /// </summary>
    private static readonly (long From, double Seconds, double Rate)? Throttle = ParseThrottle(System.Environment.GetEnvironmentVariable("MACHINES_THROTTLE"));

    private static double? _throttledSince;
    private static bool _throttleOver;
    private double _throttleTokens;

    private static (long, double, double)? ParseThrottle(string? setting)
    {
        var words = (setting ?? "").Split(',', StringSplitOptions.TrimEntries);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return words.Length == 3 && long.TryParse(words[0], out var from) && double.TryParse(words[1], culture, out var seconds)
               && double.TryParse(words[2], culture, out var rate) && rate > 0
            ? (from, seconds, rate)
            : null;
    }

    /// <summary>The throttle's allowance grows with the frame's time (<see cref="MayStep"/>).</summary>
    private void FillThrottle(double delta)
    {
        if (Throttle is { } throttle)
            _throttleTokens = Math.Min(_throttleTokens + delta * throttle.Rate, 2);
    }

    /// <summary>Whether the world may run its next tick now: always, but while MACHINES_THROTTLE holds it back.</summary>
    private bool MayStep()
    {
        if (Throttle is not { } throttle || _throttleOver || World.Tick < throttle.From)
            return true;
        var now = Time.GetTicksMsec() / 1000.0;
        if (_throttledSince is null)
        {
            _throttledSince = now;
            GD.Print($"net: throttled to {throttle.Rate:0.#} ticks a second for {throttle.Seconds:0.#} s from tick {World.Tick}");
        }
        if (now - _throttledSince > throttle.Seconds)
        {
            _throttleOver = true;
            GD.Print($"net: throttle over at tick {World.Tick} ({_session.NewestBundle + 1} bundles in hand)");
            return true;
        }
        if (_throttleTokens < 1)
            return false;
        _throttleTokens -= 1;
        return true;
    }
}
