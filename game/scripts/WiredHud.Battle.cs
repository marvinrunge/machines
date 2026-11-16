using Godot;
using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Game;

// The screen of a battle, on the run's plates: the clock between the two teams at the top, each hero with its name, chassis,
// tier, what is left of it and its lives, the capture of both pods under them, the ways to both pods, the abilities, the
// thrusters and the chassis' special in the foot, the opening's countdown, the wait for a new body and whom a player out of
// the fight looks on through. Everything it says comes from BattleReadout. Battle is the remake's own addition.
public partial class WiredHud
{
    // Sizes in pixels of a window 1080 high: a team's block, the clock between them, a hero's row, the team's heading, the
    // capture bars under the plate and the plate round them.
    private const float TeamWidth = 340, ClockWidth = 150, HeroRow = 40, TeamHead = 24, CaptureHeight = 32, PlatePad = 12, PlateTop = 6;

    private BattleReadout? _battle;
    private readonly Dictionary<Race, float> _healthTrail = [];
    private readonly Dictionary<Race, double> _heroStruck = [];
    private int _openingSeen = -1, _clockSeen = -1;

    /// <summary>The battle as the HUD shows it now; null in any other match.</summary>
    public BattleReadout? Battle => _battle;

    private float BattlePlateHeight => PlateTop + TeamHead + HeroRow * Math.Max(_battle?.Ours.Heroes.Count ?? 2, _battle?.Theirs.Heroes.Count ?? 2) + PlatePad;

    /// <summary>How far down the battle's plate and the capture bars under it reach.</summary>
    private float BattleHanging => (BattlePlateHeight + (_battle is { Capture: true } ? CaptureHeight + 6 : 0) + Gap) * _u;

    private void FollowBattle(BattleReadout now, float delta)
    {
        // The opening's last seconds and the clock's last ten tick.
        if (now.Opening is { } opening && opening != _openingSeen && opening is > 0 and <= 5)
            _game.Audio.PlayGui("gui/sounds/navkey.wav");
        _openingSeen = now.Opening ?? -1;
        if (now.Outcome is null && now.Opening is null && now.Seconds != _clockSeen && now.Seconds is > 0 and <= 10)
            _game.Audio.PlayGui("gui/sounds/navkey.wav");
        _clockSeen = now.Seconds;

        foreach (var hero in now.Ours.Heroes.Concat(now.Theirs.Heroes))
        {
            var trail = _healthTrail.GetValueOrDefault(hero.Race, hero.Health);
            if (hero.Health < trail - 0.001f && _heroStruck.GetValueOrDefault(hero.Race, -10) < _time - 0.12)
                _heroStruck[hero.Race] = _time;
            _healthTrail[hero.Race] = Mathf.Max(hero.Health, Mathf.MoveToward(trail, hero.Health, 0.4f * delta));
        }

        foreach (var ability in now.Abilities)
        {
            if (ability.Ready && !_readyAt.ContainsKey(ability.Slot))
                _readyAt[ability.Slot] = _time;
            else if (!ability.Ready)
                _readyAt.Remove(ability.Slot);
        }
        var dashReady = now.Dash is { Ready: true };
        if (dashReady && !_dashWasReady)
            _dashReadyAt = _time;
        _dashWasReady = dashReady;
        FollowSpecial(now.SpecialView);
    }

    private void DrawBattle(BattleReadout now)
    {
        DrawDanger();
        // Once the battle is over its banner says what came of it: no pod pointer, hero mark or located enemy is drawn into it.
        if (_game.World.Battle is not { Over: true })
        {
            DrawLocated();
            DrawPodPointers(now);
            DrawHeroMarks();
        }
        DrawFloats();
        DrawRefused();
        DrawCall();
        DrawTeams(now);
        if (!now.Out)
            DrawBattleFoot(now);
        DrawBattleDown(now);
        DrawWatching(now);
        DrawOpening(now);
    }

    /// <summary>What a battle ended in, called out over the view.</summary>
    public void BattleEnds(BattleOutcome outcome, BattleEnd reason) => Announce(BattleEndScreen.Head(outcome).Caps(), BattleEndScreen.Why(outcome, reason),
        outcome switch { BattleOutcome.Won => MenuTheme.Glow, BattleOutcome.Lost => Danger, _ => Warning }, 5);

    /// <summary>A capture began: of the team's own pod, or of the enemy's by the team.</summary>
    public void CaptureBegins(bool ours) => Announce(Lang.T(ours ? "Taking the enemy pod" : "Your pod is being taken").Caps(),
        Lang.T(ours ? "Stay in the circle: a hit costs your share" : "Drive them out of the circle or hit them"), ours ? MenuTheme.Glow : Danger, 2.6f);

    // ---- the plate at the top ---------------------------------------------------------------------

    /// <summary>The clock between the two teams, and under each team the capture of its pod.</summary>
    private void DrawTeams(BattleReadout now)
    {
        var middle = Left + (_size.X - Left) / 2;
        var height = BattlePlateHeight * _u;
        var width = (2 * TeamWidth + ClockWidth + 4 * PlatePad) * _u;
        var box = new Rect2(middle - width / 2, 0, width, height);
        // The weapons' panels of first person go below the plate (and the capture bars) where a large HUD size brings
        // them under it; its lower corners are cut 22 of its pixels in, as far as they may reach.
        if (_hud.Weapons.Visible && _hud.Weapons.Area.End.X > box.Position.X + 22 * _u)
            PanelsTop = BattleHanging;
        var last = now.Outcome is null && now.Opening is null && now.Seconds <= 30;
        Plate(Corners(box, 0, 0, 22, 22), Plating, last ? Danger : MenuTheme.Edge, 2);

        var ours = new Rect2(box.Position.X + PlatePad * _u, PlateTop * _u, TeamWidth * _u, height - PlateTop * _u);
        var theirs = new Rect2(box.End.X - (PlatePad + TeamWidth) * _u, PlateTop * _u, TeamWidth * _u, height - PlateTop * _u);
        DrawTeam(now.Ours, ours, Lang.T("Your team"), MenuTheme.Glow);
        DrawTeam(now.Theirs, theirs, Lang.T("Enemy team"), Danger);
        DrawBattleClock(now, middle, box);

        if (!now.Capture)
            return;
        // Under its team the pod's capture by the other side: the own pod reddens as the enemy takes it.
        DrawCapture(new Rect2(ours.Position.X, height + 6 * _u, ours.Size.X, CaptureHeight * _u), Lang.T("Your pod"), now.Theirs.Progress, now.Theirs.Capturing, Danger);
        DrawCapture(new Rect2(theirs.Position.X, height + 6 * _u, theirs.Size.X, CaptureHeight * _u), Lang.T("Enemy pod"), now.Ours.Progress, now.Ours.Capturing, MenuTheme.Glow);
    }

    private void DrawTeam(BattleTeamReadout team, Rect2 block, string title, Color accent)
    {
        Say(new Vector2(block.Position.X + 2 * _u, block.Position.Y + 17 * _u), title.Caps(), 14, team.InTheFight ? accent : MenuTheme.Dim);
        for (var i = 0; i < team.Heroes.Count; i++)
            DrawHeroRow(team.Heroes[i], new Rect2(block.Position.X, block.Position.Y + (TeamHead + i * HeroRow) * _u, block.Size.X, HeroRow * _u));
    }

    /// <summary>
    /// A hero's row: its colour, name, chassis and tier on the left, on the right what is left of it over its lives; while it
    /// is down the seconds until it is back, and once it is out for good its row dims and its name is struck through.
    /// </summary>
    private void DrawHeroRow(BattleHeroReadout hero, Rect2 row)
    {
        var alpha = hero.Out ? 0.5f : 1;
        var colour = HangarCreation.Colours.FirstOrDefault(c => c.Race == Game.Look(hero.Race)).Colour;
        DrawRect(new Rect2(row.Position + new Vector2(0, 4 * _u), new Vector2(5 * _u, row.Size.Y - 10 * _u)), new Color(colour, alpha));
        var nameColour = hero.You ? MenuTheme.Bright : MenuTheme.Text;
        var name = Fit(hero.Name.Caps(), 17, 176);
        var at = row.Position + new Vector2(13 * _u, 17 * _u);
        Say(at, name, 17, new Color(nameColour, alpha));
        if (hero.Out)
        {
            var across = Width(name, 17);
            DrawLine(at + new Vector2(-2 * _u, -6 * _u), at + new Vector2(across + 2 * _u, -6 * _u), Danger, Mathf.Max(2 * _u, 1));
        }
        var chassis = Fit($"{HangarText.Name(hero.Chassis)} · {BattleDraft.Roman(hero.Tier)}", 13, 176);
        Say(row.Position + new Vector2(13 * _u, 33 * _u), chassis, 13, new Color(MenuTheme.Dim, alpha));

        var bar = new Rect2(row.Position + new Vector2(196 * _u, 6 * _u), new Vector2(row.Size.X - 198 * _u, 10 * _u));
        if (hero.Out)
            Say(new Vector2(bar.End.X, bar.Position.Y + 10 * _u), Lang.T("Destroyed").Caps(), 13, new Color(Danger, 0.85f), 1);
        else if (hero.BackIn is { } back)
            Say(new Vector2(bar.End.X, bar.Position.Y + 10 * _u), Lang.T("Back in {0} s", Mathf.CeilToInt(back)), 14, Warning, 1);
        else
        {
            var struck = Fading(_heroStruck.GetValueOrDefault(hero.Race, -10), 0.25);
            DrawRect(bar, MenuTheme.Well);
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * _healthTrail.GetValueOrDefault(hero.Race, hero.Health), bar.Size.Y)), new Color(1, 0.86f, 0.74f, 0.8f));
            var fill = hero.Health < 0.3f ? Danger : hero.Health < 0.6f ? Warning : MenuTheme.Glow;
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * hero.Health, bar.Size.Y)), fill.Lerp(Colors.White, struck * 0.6f));
            DrawRect(bar, MenuTheme.Edge, false, 1);
            DrawRect(new Rect2(bar.Position + new Vector2(0, bar.Size.Y + _u), new Vector2(bar.Size.X * hero.Armour, 2 * _u)), Ice);
        }
        // The lives: lit for each still to be had, the body it stands in or waits for counted.
        for (var i = 0; i < hero.Lives; i++)
        {
            var pip = new Rect2(new Vector2(bar.End.X - (i + 1) * 15 * _u + 3 * _u, row.Position.Y + 23 * _u), new Vector2(11, 11) * _u);
            if (i < hero.LivesLeft)
                Plate(pip, hero.Out ? MenuTheme.Dim : MenuTheme.Glow, hero.Out ? MenuTheme.Dim : MenuTheme.Glow, 3);
            else
                Plate(pip, Plating, MenuTheme.Edge, 3);
        }
        if (hero.Kills > 0)
            Say(new Vector2(bar.Position.X, row.Position.Y + 33 * _u), Lang.T("Kills: {0}", hero.Kills), 13, new Color(Gold, alpha));
    }

    /// <summary>The clock between the teams: minutes and seconds to the draw, the last half minute in red and the last ten beating.</summary>
    private void DrawBattleClock(BattleReadout now, float middle, Rect2 box)
    {
        var last = now.Outcome is null && now.Opening is null && now.Seconds <= 10;
        var beat = last ? 1 - (float)(_time % 1) : 0;
        var colour = now.Outcome is not null ? MenuTheme.Dim : now.Seconds <= 30 && now.Opening is null ? Danger.Lerp(Colors.White, beat * 0.35f) : MenuTheme.Text;
        var seconds = Math.Max(0, now.Seconds);
        Say(new Vector2(middle, box.Position.Y + (PlateTop + 18) * _u), Lang.T("Time").Caps(), 13, MenuTheme.Dim, 0.5f);
        Say(new Vector2(middle, box.Position.Y + (PlateTop + 62) * _u), $"{seconds / 60}:{seconds % 60:00}", 44 + beat * 6, colour, 0.5f, outline: last ? 3 : 0);
        var share = Mathf.Clamp(seconds / (float)Math.Max(now.TimeLimit, 1), 0, 1);
        Thin(new Rect2(middle - (ClockWidth / 2 - 10) * _u, box.End.Y - 14 * _u, (ClockWidth - 20) * _u, 3 * _u), share, colour);
    }

    /// <summary>A pod's capture: how far the other team has come, beating while one of them stands in the circle.</summary>
    private void DrawCapture(Rect2 box, string title, float progress, bool going, Color colour)
    {
        var beat = going ? (MathF.Sin((float)_time * 9) + 1) / 2 : 0;
        Plate(box, Plating, going ? colour.Lerp(Colors.White, beat * 0.4f) : MenuTheme.Edge, 8);
        Say(box.Position + new Vector2(10, 17) * _u, title.Caps(), 13, going ? colour : MenuTheme.Dim);
        Say(new Vector2(box.End.X - 10 * _u, box.Position.Y + 17 * _u), $"{Mathf.FloorToInt(progress)} %", 15, progress > 0 ? colour : MenuTheme.Dim, 1);
        var bar = new Rect2(box.Position + new Vector2(10, 22) * _u, new Vector2(box.Size.X - 20 * _u, 5 * _u));
        DrawRect(bar, MenuTheme.Well);
        DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * Mathf.Clamp(progress / BattleBalance.CaptureTarget, 0, 1), bar.Size.Y)), colour);
    }

    // ---- over the view ----------------------------------------------------------------------------

    /// <summary>How far off a hero still gets its team's mark over it, in metres.</summary>
    private const float MarkReach = 260;

    /// <summary>
    /// A small mark over each hero in view, in its team's colour (the team-mates' with the name): in a close fight the heroes
    /// of both teams look alike, and only the paint told them apart. An enemy hidden in its shadow gets none.
    /// </summary>
    private void DrawHeroMarks()
    {
        if (GetViewport().GetCamera3D() is not { } camera || _game.World.Battle is not { } run)
            return;
        var world = _game.World;
        var own = run.TeamOf(Game.Player);
        var eye = world.HeroOf(Game.Player)?.Id ?? _game.WatchedHero;
        foreach (var race in run.Teams.SelectMany(t => t.Order()))
        {
            if (world.HeroOf(race) is not { } hero || hero.Id == eye || _game.InShadow(hero))
                continue;
            var mate = run.TeamOf(race) == own;
            // An enemy only near by, where it is fought: the marks are not there to find it from afar.
            var reach = mate ? MarkReach : MarkReach / 2;
            var place = ActorView.World(hero.Position) + Vector3.Up * (hero.Radius * 1.6f + 4);
            var distance = camera.GlobalPosition.DistanceTo(place);
            if (distance > reach || camera.IsPositionBehind(place))
                continue;
            var at = camera.UnprojectPosition(place);
            if (at.X < Left || at.X > _size.X || at.Y < 0 || at.Y > _size.Y)
                continue;
            var alpha = Mathf.Lerp(0.95f, 0.45f, distance / reach);
            var colour = new Color(mate ? MenuTheme.Glow : Danger, alpha);
            var r = 7 * _u;
            Vector2[] mark = [at + new Vector2(-r, -r), at + new Vector2(r, -r), at + new Vector2(0, r * 0.4f)];
            DrawColoredPolygon(mark, colour);
            DrawPolyline([.. mark, mark[0]], new Color(Plating, alpha), 1.5f * _u, true);
            if (mate)
                Say(at + new Vector2(0, -r - 5 * _u), BattleReadout.HeroName(run, race), 13, colour, 0.5f, outline: 2);
        }
    }

    /// <summary>The ways to both pods: the own one beating while it is being taken, the enemy's while the team takes it.</summary>
    private void DrawPodPointers(BattleReadout now)
    {
        if (GetViewport().GetCamera3D() is not { } camera)
            return;
        var world = _game.World;
        var own = world.Battle!.TeamOf(Game.Player);
        // Metres from the hero, or from the one looked on through.
        var from = world.HeroOf(Game.Player) ?? (_game.WatchedHero is { } watched ? world.Find<Sim.World.Machine>(watched) : null);
        if (world.BattlePod(1 - own) is { } enemy)
        {
            var label = from is null ? Lang.T("ENEMY POD") : Lang.T("ENEMY POD {0} m", Mathf.RoundToInt(from.DistanceTo(enemy)));
            Pointer(camera, ActorView.World(enemy.Position) + Vector3.Up * 14, now.Ours.Capturing ? MenuTheme.Glow : Gold, label, now.Ours.Capturing, clearSight: true);
        }
        if (world.BattlePod(own) is { } pod)
        {
            var label = from is null ? Lang.T("POD") : Lang.T("POD {0} m", Mathf.RoundToInt(from.DistanceTo(pod)));
            Pointer(camera, ActorView.World(pod.Position) + Vector3.Up * 14, now.Theirs.Capturing ? Danger : MenuTheme.Text, label, now.Theirs.Capturing, clearSight: true);
        }
    }

    /// <summary>What was refused, on a line over the plates.</summary>
    private void DrawRefused()
    {
        if (_refused is { } refused && Fading(_refusedAt, 2.6) is > 0 and var alpha)
            Prompt(new Vector2(Left + (_size.X - Left) / 2, _size.Y - (Rise + 84) * _u), refused, new Color(Danger.Lerp(Colors.White, 0.2f), Mathf.Min(alpha * 2, 1)), 20);
    }

    /// <summary>The abilities, the thrusters and the chassis' special, left of the radar.</summary>
    private void DrawBattleFoot(BattleReadout now)
    {
        var blocks = new List<Block>();
        foreach (var ability in now.Abilities)
            blocks.Add(new Block(Foot(Rise), at => DrawAbility(ability, at)));
        if (now.Dash is { } dash)
            blocks.Add(new Block(Foot(Rise), at => DrawDash(dash, at, DashName(now.SpecialView))));
        if (OwnBlock(now.SpecialView) is { } special)
            blocks.Add(new Block(Foot(SpecialWidth), at => DrawSpecial(special, at)));
        var (x, y) = (Left + 16 * _u, _size.Y - (Rise + 8 + (Driving ? 0 : PromptLine)) * _u);
        foreach (var block in blocks)
        {
            block.Draw(new Vector2(x, y));
            x += block.Width + Gap * _u;
        }
    }

    /// <summary>The hero is down with a life left: over the static, the seconds until it is back at the pod.</summary>
    private void DrawBattleDown(BattleReadout now)
    {
        if (now.DownSeconds is not { } seconds || !_game.HeroDown)
            return;
        var middle = Left + (_size.X - Left) / 2;
        var box = new Rect2(middle - 300 * _u, _size.Y * 0.5f - 96 * _u, 600 * _u, 230 * _u);
        var beat = 1 - (float)(seconds % 1);
        Plate(box, new Color(0, 0, 0, 0.82f), Danger, 16);
        Say(new Vector2(middle, box.Position.Y + 50 * _u), Lang.T("Body lost").Caps(), 36, Danger, 0.5f);
        Say(new Vector2(middle, box.Position.Y + 136 * _u), Lang.T("Back in {0} s", Mathf.CeilToInt(seconds)), 64 + beat * 6, Colors.White, 0.5f);
        Say(new Vector2(middle, box.Position.Y + 190 * _u), Lang.T("The next body stands at the pod"), 22, MenuTheme.Text, 0.5f);
        var left = now.You.LivesLeft - 1;
        Say(new Vector2(middle, box.Position.Y + 218 * _u), left == 0 ? Lang.T("This is your last life") : Lang.T("Lives left after it: {0}", left), 19, left == 0 ? Warning : MenuTheme.Dim, 0.5f);
    }

    /// <summary>Whom the view follows while the player's hero is out of the fight, or while a bot drives it; Tab goes on to the next.</summary>
    private void DrawWatching(BattleReadout now)
    {
        if (_game.WatchedName is not { } name || now.Outcome is not null)
            return;
        var middle = Left + (_size.X - Left) / 2;
        var y = BattleHanging + 12 * _u;
        // With victory by taking the pod the battle goes on once the whole team is out, and the view follows the other team.
        var run = _game.World.Battle!;
        var enemy = _game.WatchedHero is { } id && _game.World.Find<Machines.Sim.World.Machine>(id) is { } watched && run.TeamOf(watched.Race) != run.TeamOf(Game.Player);
        var title = enemy ? Lang.T("Your team is out – watching {0}", name)
            : now.Out ? Lang.T("Destroyed – watching {0}", name) : Lang.T("Watching {0}", name);
        var colour = now.Out ? Danger : MenuTheme.Glow;
        Prompt(new Vector2(middle, y), title.Caps(), colour, 22);
        var more = _game.Watchable.Count > 1 ? enemy ? Lang.T("Tab: next hero") : Lang.T("Tab: next team-mate") : null;
        if (!now.Out && now.DownSeconds is { } back)
            more = Lang.T("Back in {0} s", Mathf.CeilToInt(back)) + (more is null ? "" : "   ·   " + more);
        if (more is not null)
            Prompt(new Vector2(middle, y + 50 * _u), more, MenuTheme.Text, 16);
    }

    /// <summary>The opening: big in the middle the seconds until the heroes may move, and how this battle is won.</summary>
    private void DrawOpening(BattleReadout now)
    {
        if (now.Opening is not { } seconds || seconds <= 0)
            return;
        var middle = Left + (_size.X - Left) / 2;
        var y = _size.Y * 0.36f;
        var beat = 1 - (float)(_time % 1);
        var dark = new Color(0, 0, 0, 0.55f);
        var clear = new Color(0, 0, 0, 0);
        var (width, top, bottom) = ((_size.X - Left) * 0.36f, y - 70 * _u, y + 150 * _u);
        DrawPolygon([new Vector2(middle - width, top), new Vector2(middle, top), new Vector2(middle, bottom), new Vector2(middle - width, bottom)], [clear, dark, dark, clear]);
        DrawPolygon([new Vector2(middle, top), new Vector2(middle + width, top), new Vector2(middle + width, bottom), new Vector2(middle, bottom)], [dark, clear, clear, dark]);
        Say(new Vector2(middle, y - 30 * _u), Lang.T("The battle begins in").Caps(), 26, MenuTheme.Text, 0.5f, outline: 2);
        Say(new Vector2(middle, y + 92 * _u), seconds.ToString(), 120 + beat * 14, seconds <= 3 ? Gold.Lerp(Colors.White, beat * 0.4f) : Colors.White, 0.5f, outline: 3);
        var goal = BattlePod.Goal(_game.World.Battle!.Options.Win);
        Say(new Vector2(middle, y + 136 * _u), goal, 22, MenuTheme.Glow, 0.5f, outline: 2);
    }
}
