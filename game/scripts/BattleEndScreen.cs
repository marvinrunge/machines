using Godot;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// How a battle ended, on the board over the world that stands still: won, lost or a draw and why, the hero on the turntable,
/// what it did and what that brought (XP on its chassis, free XP, credits with the salvage), every hero of both teams, the
/// chassis that came within reach and how far the next ones are, and the way on: again, to the pod or to the menu, online to
/// the room. Over a recording it changes nothing and leads only out. Battle is the remake's own addition.
/// </summary>
public partial class BattleEndScreen(Game game, ClassicAssets assets) : MenuPage(null)
{
    /// <summary>Seconds in which a press does nothing at first: the hands are still at the fight.</summary>
    private const double HandsOff = 1;

    private const double Settling = 0.5, Beat = 0.26;

    private const int MostGoals = 3;

    private static readonly float[] SumStops = [0.56f, 0.78f, 1], BoardStops = [0.34f, 0.6f, 0.74f, 0.87f, 1], FactStops = [0.72f, 1];

    private readonly List<(double Wait, Action Step)> _show = [];
    private readonly List<EndRow> _sums = [], _rows = [];
    private readonly List<EndCard> _cards = [];
    private readonly List<EndGoal> _goals = [];
    private BattleRun _run = null!;
    private WiredState _state = null!;
    private BattleReward _reward = null!;
    private BattleTally _tally = null!;
    private BattleOutcome _outcome;
    private HangarScreen _screen = null!;
    private EndFigure _xp = null!, _free = null!, _credits = null!;
    private EndStamp _stamp = null!;
    private EndFanfare _fanfare = null!;
    private VBoxContainer _cardColumn = null!, _goalColumn = null!;
    private Label _note = null!, _nothing = null!;
    private ChipButton? _again;
    private ChipButton _leave = null!;
    private int _next;
    private double _wait, _settle = HandsOff;
    private bool _done, _quiet, _framed, _waiting, _goalsShown;
    // Online: whether the battle's end still waits for the relay's word that it holds (Game.BattleConfirming); whether the note
    // under the figures has been shown.
    private bool _confirming, _noted;

    /// <summary>The screen that is up, for automated checks.</summary>
    public static BattleEndScreen? Current { get; private set; }

    /// <summary>The show is over and the buttons wait.</summary>
    public bool Done => _done;

    /// <summary>For automated checks: what it says, as figures.</summary>
    public string Figures => $"{_outcome} by {_run.Result?.Reason}, xp {_reward.Xp}, free xp {_reward.FreeXp}, credits {_reward.Credits} (salvage {_reward.Salvage}), " +
                             $"damage {_tally.Damage}, kills {_tally.Kills}, capture {Points(_tally.Capture)}, deaths {_tally.Deaths}, " +
                             $"cards [{string.Join(", ", _cards.Select(c => c.Id))}], goals {_goals.Count}, counted {game.BattleCounted}, " +
                             $"buttons [{string.Join(", ", FindChildren("*", "", true, false).OfType<ChipButton>().Select(b => b.Text))}]";

    /// <summary>For automated checks: the XP, free XP and credits as the figures show them now.</summary>
    public (long Xp, long FreeXp, long Credits) Shown => (_xp.Shown, _free.Shown, _credits.Shown);

    /// <summary>For automated checks: the button with this word on it.</summary>
    public ChipButton? ButtonOf(string text) => FindChildren("*", "", true, false).OfType<ChipButton>().FirstOrDefault(b => b.Text == text);

    /// <summary>The head of the end: Victory, Defeat or Draw.</summary>
    public static string Head(BattleOutcome outcome) => Lang.T(outcome switch
    {
        BattleOutcome.Won => "Victory",
        BattleOutcome.Lost => "Defeat",
        _ => "Draw",
    });

    /// <summary>Why it ended so, as the side it is told to sees it.</summary>
    public static string Why(BattleOutcome outcome, BattleEnd reason) => Lang.T((outcome, reason) switch
    {
        (BattleOutcome.Won, BattleEnd.Wipe) => "Enemy team destroyed",
        (BattleOutcome.Won, _) => "Enemy pod taken",
        (BattleOutcome.Lost, BattleEnd.Wipe) => "Your team was destroyed",
        (BattleOutcome.Lost, _) => "Your pod was taken",
        (_, BattleEnd.Wipe) => "Both teams destroyed",
        (_, BattleEnd.Capture) => "Both pods taken at once",
        _ => "Time is up",
    });

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this)
            Current = null;
    }

    private static int Points(float capture) => (int)MathF.Floor(capture + 1e-3f);

    protected override void Build()
    {
        var world = game.World;
        _run = world.Battle!;
        // The screen is the player's own: their hero, what it did and brought (one looking on sees the first hero's).
        var player = _run.Tally.ContainsKey(Game.Me) ? Game.Me : _run.Tally.Keys.First();
        var race = world.Players.RaceOf(player);
        _state = world.Wired!.Heroes.GetValueOrDefault(player) ?? world.Wired.Heroes.Values.First();
        _tally = _run.Tally[player];
        _reward = BattleRewards.Of(_run, player);
        _outcome = _run.Won(player) ? BattleOutcome.Won : _run.Lost(player) ? BattleOutcome.Lost : BattleOutcome.Draw;
        _waiting = game.BattleCounted && BattleAccounts.Ledger.Waiting;
        _confirming = game.BattleConfirming;

        foreach (var (side, value) in new[] { ("left", 30), ("right", 30), ("top", 24), ("bottom", 24) })
            AddThemeConstantOverride($"margin_{side}", value);
        AddChild(new HangarIcons(assets, () => Game.Look(race)));

        var compact = Stage.Size.X < 1700;
        var column = Column(12);
        AddChild(column);
        column.AddChild(BuildHead());
        var row = Line(14);
        row.SizeFlagsVertical = SizeFlags.ExpandFill;
        column.AddChild(row);
        row.AddChild(BuildHero(compact ? 372 : 470, player));
        row.AddChild(BuildMiddle(compact, player));
        row.AddChild(BuildRight(compact ? 410 : 490));
        column.AddChild(BuildFoot());
        AddChild(_fanfare = new EndFanfare());
        SwitchOn();
        PlanShow();
    }

    private static Label Title(string text) => MenuTheme.Label(Lang.T(text).ToUpperInvariant(), 18, MenuTheme.Dim.Lightened(0.35f));

    private static string Number(long figure) => WiredReport.Figure(figure, Lang.Code == "de");

    private Color Accent => _outcome switch
    {
        BattleOutcome.Won => MenuTheme.Bright,
        BattleOutcome.Lost => HangarPage.Red,
        _ => MenuTheme.Text,
    };

    private Control BuildHead()
    {
        var chip = new ChipPanel { Padding = 6 };
        var row = Line(24);
        row.AddChild(MenuTheme.Label(Head(_outcome).ToUpperInvariant(), 40, Accent));
        row.AddChild(MenuTheme.Label(Why(_outcome, _run.Result!.Reason), 25, MenuTheme.Text));
        row.AddChild(Spring());
        var map = BattleCatalogue.Map(MatchRequest.Current.Scenario);
        var options = _run.Options;
        var where = MenuTheme.Label($"{(map is null ? MatchRequest.Current.Scenario : BattlePod.MapName(map))}   ·   {Lang.T("Tier {0}", BattleDraft.Roman(options.Tier))}   ·   {BattlePod.LivesName(options.Lives)}",
            25, MenuTheme.Text, HorizontalAlignment.Right);
        where.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        row.AddChild(where);
        chip.AddChild(row);
        return chip;
    }

    /// <summary>The hero as it stood at the end, on the turntable, with its plate.</summary>
    private Control BuildHero(float width, PlayerId player)
    {
        var column = Column(10);
        column.CustomMinimumSize = new Vector2(width, 0);
        var chip = new ChipPanel { Padding = 4, SizeFlagsVertical = SizeFlags.ExpandFill };
        var layers = new Control();
        chip.AddChild(layers);
        _screen = new HangarScreen(assets) { Classic = !GraphicsSettings.Effects };
        _screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layers.AddChild(_screen);
        column.AddChild(chip);

        var plate = new ChipPanel { Padding = 8 };
        var sheet = BattleReadout.SheetOf(_run, player);
        var name = MenuTheme.Label(sheet?.Name is { Length: > 0 } given ? given : Lang.T("Hero"), 30, MenuTheme.Bright, HorizontalAlignment.Center);
        name.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        var loadout = _state.Loadout;
        var chassis = MenuTheme.Label($"{HangarText.Name(loadout.Chassis.Catalogue)}   ·   {Lang.T("Tier {0}", BattleDraft.Roman(BattleCatalogue.Tier(loadout.Chassis.Catalogue)))}",
            22, MenuTheme.Text, HorizontalAlignment.Center);
        chassis.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        var weapons = MenuTheme.Paragraph(string.Join("  ·  ", HangarText.Guns(loadout)), 17, MenuTheme.Dim.Lightened(0.35f));
        weapons.HorizontalAlignment = HorizontalAlignment.Center;
        weapons.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        plate.AddChild(Column(0, name, chassis, weapons));
        column.AddChild(plate);
        return column;
    }

    /// <summary>The middle: what the battle brought in three big numbers, how the XP came about, and every hero of both teams.</summary>
    private Control BuildMiddle(bool compact, PlayerId player)
    {
        var column = Column(10);
        column.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var top = new ChipPanel { Padding = 8 };
        var figures = Line(16);
        _xp = new EndFigure("XP", compact ? 76 : 92) { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.6f };
        _free = new EndFigure("Free XP", compact ? 56 : 68) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _credits = new EndFigure("Credits", compact ? 56 : 68) { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.4f };
        figures.AddChild(_xp);
        figures.AddChild(_free);
        figures.AddChild(_credits);
        top.AddChild(figures);
        top.AddChild(_stamp = new EndStamp { Place = new Vector2(0.3f, 0.2f) });
        column.AddChild(top);

        var sums = new ChipPanel { Padding = 8 };
        var rows = Column(1);
        foreach (var cells in Sums())
        {
            var row = new EndRow { Cells = cells, Stops = SumStops, Last = MenuTheme.Bright };
            _sums.Add(row);
            rows.AddChild(row);
        }
        _note = MenuTheme.Paragraph(" ", 19, MenuTheme.Text);
        _note.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        rows.AddChild(_note);
        sums.AddChild(rows);
        column.AddChild(sums);

        var board = new ChipPanel { Padding = 8, SizeFlagsVertical = SizeFlags.ExpandFill };
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var inner = Column(1);
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var own = _run.TeamOf(player);
        foreach (var team in new[] { own, 1 - own })
        {
            var heading = new EndRow
            {
                Cells = [Lang.T(team == own ? "Your team" : "Enemy team"), Lang.T("Chassis"), Lang.T("Damage"), Lang.T("Kills"), Lang.T("Captured")],
                Stops = BoardStops, FontSize = 19, Heading = true,
            };
            _rows.Add(heading);
            inner.AddChild(heading);
            // Every player of the team in their place, numbered where their race has several.
            foreach (var hero in _run.Members[team])
            {
                var tally = _run.Tally.GetValueOrDefault(hero) ?? new BattleTally();
                var name = BattleReadout.Label(BattleReadout.NumberOf(game.World, hero), BattleReadout.HeroName(_run, hero));
                var row = new EndRow
                {
                    Cells = [name, $"{HangarText.Name(tally.Chassis)} · {BattleDraft.Roman(BattleCatalogue.Tier(tally.Chassis))}",
                        Number(tally.Damage), Number(tally.Kills), Number(Points(tally.Capture))],
                    Stops = BoardStops, FontSize = 19, Last = hero == player ? MenuTheme.Bright : MenuTheme.Text,
                };
                _rows.Add(row);
                inner.AddChild(row);
            }
            inner.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8), MouseFilter = MouseFilterEnum.Ignore });
        }
        scroll.AddChild(inner);
        board.AddChild(scroll);
        column.AddChild(board);
        return column;
    }

    /// <summary>How the XP and the credits came about, each part as the rewards count it, so that they add up to the figures.</summary>
    private IEnumerable<string[]> Sums()
    {
        var kills = BattleBalance.KillXp * _tally.Kills;
        var capture = BattleBalance.CapturePointXp * Points(_tally.Capture);
        var before = _reward.Won ? (int)MathF.Round(_reward.Xp / BattleBalance.WinFactor) : _reward.Xp;
        var damage = Math.Max(0, before - BattleBalance.ParticipationXp - kills - capture);
        yield return [Lang.T("Taking part"), "", Number(BattleBalance.ParticipationXp)];
        yield return [Lang.T("Damage to enemy heroes"), Number(_tally.Damage), Number(damage)];
        yield return [Lang.T("Enemy heroes destroyed"), $"{_tally.Kills} × {BattleBalance.KillXp}", Number(kills)];
        yield return [Lang.T("Capture points"), Number(Points(_tally.Capture)), Number(capture)];
        if (_reward.Won)
            yield return [Lang.T("Victory"), $"× {BattleBalance.WinFactor.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', Lang.Code == "de" ? ',' : '.')}", $"+{Number(_reward.Xp - before)}"];
        yield return [Lang.T("XP on {0}", HangarText.Name(_tally.Chassis)), "", Number(_reward.Xp)];
        yield return [Lang.T("Credits"), $"{Number(_reward.Xp)} × {BattleBalance.CreditsPerXp}", Number(_reward.Credits - _reward.Salvage)];
        // Salvage pays for the heroes' chassis; a first-tier chassis is worth nothing, so heroes with nothing for them are not named.
        if (_reward.Salvage > 0)
            yield return [Lang.T("Salvage"), _tally.Kills switch { 0 => "", 1 => Lang.T("1 hero"), var many => Lang.T("{0} heroes", many) }, $"+{Number(_reward.Salvage)}"];
    }

    /// <summary>The right: the chassis within reach now and how far the next ones on the line are.</summary>
    private Control BuildRight(float width)
    {
        var column = Column(10);
        column.CustomMinimumSize = new Vector2(width, 0);

        var opened = new ChipPanel { Padding = 8, SizeFlagsVertical = SizeFlags.ExpandFill };
        var rows = Column(6, Title("Within reach"));
        var shelf = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        _cardColumn = Column(4);
        _cardColumn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _nothing = MenuTheme.Label("Nothing new this battle", 20, MenuTheme.Dim.Lightened(0.2f));
        _nothing.Visible = false;
        _cardColumn.AddChild(_nothing);
        shelf.AddChild(_cardColumn);
        rows.AddChild(shelf);
        opened.AddChild(rows);
        column.AddChild(opened);

        var next = new ChipPanel { Padding = 8 };
        _goalColumn = Column(5, Title("Next on the line"));
        next.AddChild(_goalColumn);
        column.AddChild(next);
        return column;
    }

    private Control BuildFoot()
    {
        var chip = new ChipPanel { Padding = 6 };
        var row = Line(14);
        ChipButton Wide(ChipButton button)
        {
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(button);
            return button;
        }
        if (game.Online)
            Wide(Button("Room", BackToRoom, 76, 32));
        else if (!game.Watching)
        {
            _again = Wide(Button("Again", Again, 76, 32));
            Wide(Button("Pod", () => game.ExitToMenus(), 76, 32));
        }
        _leave = Wide(Button("Menu", () => game.ExitToMenus(home: true), 76, 32));
        chip.AddChild(row);
        return chip;
    }

    /// <summary>The same battle once more, by the loading screen a battle comes up behind.</summary>
    private void Again()
    {
        var options = _run.Options;
        var map = BattleCatalogue.Map(MatchRequest.Current.Scenario);
        LoadingScreen.Show(game.GetTree(), map is null ? MatchRequest.Current.Scenario : BattlePod.Title(map), BattlePod.Detail(options.Tier, options.Lives, options.Win, options.TeamSize), game.BattleAgain);
    }

    /// <summary>Online: back to a room like the one the battle was started from (see <see cref="MultiplayerPage.BackToRoom"/>).</summary>
    private void BackToRoom()
    {
        MultiplayerPage.BackToRoom();
        game.ExitToMenus();
    }

    /// <summary>Esc: to the pod, online to the room, or out of a recording to the menu.</summary>
    public override void Back()
    {
        if (game.Watching)
            game.ExitToMenus(home: true);
        else if (game.Online)
            BackToRoom();
        else
            game.ExitToMenus();
    }

    // ---- the show ---------------------------------------------------------------------------------

    private void Then(double wait, Action step) => _show.Add((wait, step));

    private void Sound(string file)
    {
        if (!_quiet)
            MenuSounds.Gui(file);
    }

    /// <summary>Line by line the XP climbs and the credits after it, then the board, then what came within reach.</summary>
    private void PlanShow()
    {
        Then(0.5, () => Sound("stats.wav"));
        var xp = 0L;
        var parts = _sums.Count;
        for (var i = 0; i < parts; i++)
        {
            var row = _sums[i];
            var last = i == parts - 1;
            var credits = i == parts - 2;
            Then(Beat, () =>
            {
                row.Arrive();
                Sound("listclik.wav");
                if (credits)
                    _credits.To(_reward.Credits - _reward.Salvage, Beat * 1.5);
                else if (last)
                    _credits.To(_reward.Credits, Beat * 1.5);
                else
                {
                    xp = Math.Min(_reward.Xp, xp + ParsePoints(row));
                    _xp.To(xp, Beat * 0.9);
                }
            });
        }
        Then(0.35, () =>
        {
            _xp.To(_reward.Xp, 0.01);
            _free.To(_reward.FreeXp, Beat * 2);
            foreach (var row in _rows)
                row.Arrive();
            ShowNote();
        });
        Then(0.5, Stamp);
        foreach (var id in game.BattleOpened)
            Then(0.75, () => Open(id));
        Then(0.4, ShowGoals);
    }

    /// <summary>What a sum row adds to the XP, read back from its last cell.</summary>
    private static long ParsePoints(EndRow row) =>
        long.TryParse(new string(row.Cells[^1].Where(char.IsDigit).ToArray()), out var points) ? points : 0;

    private void ShowNote()
    {
        _noted = true;
        _note.Text = Lang.T(_waiting ? "The battle is not in the profile yet: the file cannot be written just now."
            : game.Watching ? "A recording changes nothing in the account."
            : game.BattleWatch ? "A battle the bots played alone changes nothing in the account."
            : _confirming ? "Waiting for the other players' games to confirm the end…"
            : !game.BattleCounted ? game.BattleNotCounted ?? "This battle had ended before you came back: it is not counted here."
            : "This battle has been counted.");
    }

    /// <summary>A win comes down on the XP with a flash, sparks and the hero's guns.</summary>
    private void Stamp()
    {
        if (_outcome != BattleOutcome.Won)
            return;
        _stamp.Land(Head(_outcome).ToUpperInvariant(), MenuTheme.Bright, 34);
        _xp.Flare(MenuTheme.Bright);
        _fanfare.Flash(MenuTheme.Bright, 0.4f);
        _fanfare.Burst(_xp.GetGlobalTransform() * (_xp.Size / 2), MenuTheme.Bright, 80, 700);
        MenuSounds.Gui("taskmach.wav");
        // A battery's gun fires once for each copy.
        var weapons = _state.Build(Paths.Data).Weapons;
        _screen.Fire(weapons);
        foreach (var mounting in weapons.Select(w => w.Mounting).Distinct())
        {
            if (_screen.MountPoint(mounting) is { } mount)
                _screen.Spark(mount);
        }
    }

    /// <summary>A chassis that came within reach of research comes on the board.</summary>
    private void Open(string id)
    {
        if (_cards.Any(c => c.Id == id))
            return;
        var card = new EndCard { Id = id, Kind = Lang.T("Within reach"), Meaning = Lang.T("Research it in the pod.") };
        var first = _cards.Count == 0;
        _nothing.Visible = false;
        _cards.Add(card);
        _cardColumn.AddChild(card);
        card.Arrive();
        MenuSounds.Gui("ctrlpon.wav");
        if (first)
            game.Audio.Say("VID_TECHNICIAN_RESEARCH_COMPLETE", queue: true);
    }

    private void ShowGoals()
    {
        if (_cards.Count == 0)
            _nothing.Visible = true;
        if (_goalsShown)
            return;
        _goalsShown = true;
        FillGoals();
    }

    /// <summary>The chassis after the one fought on that are not researched yet, with the XP there is towards each.</summary>
    private void FillGoals()
    {
        foreach (var old in _goals)
        {
            _goalColumn.RemoveChild(old);
            old.QueueFree();
        }
        _goals.Clear();
        var account = BattleAccounts.Load();
        var goals = WiredCatalogue.Chassis.Where(c => c.Needs.Contains(_tally.Chassis) && !account.Researched(c.Id))
            .Select(c => (Id: c.Id, Need: BattleRules.ResearchCost(c.Id), Have: BattleRules.XpTowards(account, c.Id)))
            .OrderBy(g => g.Need).ThenBy(g => g.Id, StringComparer.Ordinal).Take(MostGoals).ToList();
        if (goals.Count == 0)
        {
            // A chassis whose followers are all researched is not the top of its line, as the pod says too.
            var top = MenuTheme.Label(BattleDraft.LeadsOn(_tally.Chassis) ? "Everything after it is researched" : "The top of its line", 20, MenuTheme.Glow);
            _goalColumn.AddChild(top);
            return;
        }
        foreach (var (id, need, have) in goals)
        {
            var row = new EndGoal(new WiredGoal(id, "{0} XP to research it", need, Math.Min(have, need)));
            _goals.Add(row);
            _goalColumn.AddChild(row);
            row.Arrive();
        }
    }

    private void Finish()
    {
        _done = true;
        Focus(_again ?? _leave);
    }

    /// <summary>A press cuts the show short: all of it stands at once.</summary>
    private void Skip()
    {
        _quiet = true;
        while (_next < _show.Count)
            _show[_next++].Step();
        _xp.Land();
        _free.Land();
        _credits.Land();
        foreach (var row in _sums.Concat(_rows))
            row.Land();
        foreach (var card in _cards)
            card.Land();
        foreach (var goal in _goals)
            goal.Land();
        _settle = Settling;
        Finish();
    }

    public override void _Input(InputEvent @event)
    {
        var press = @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle }
            or InputEventKey { Pressed: true, Echo: false } or InputEventJoypadButton { Pressed: true };
        if (!press || _done && _settle <= 0)
            return;
        if (_settle <= 0)
            Skip();
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        _settle = Math.Max(0, _settle - delta);
        if (!_framed && _screen.Size.X >= 1)
        {
            _framed = true;
            _screen.Present(HeroRig.Compose(assets, Game.Look(_state.Race), _state.Loadout));
            _screen.KeepOff([]);
        }
        WatchAccount();
        if (_done)
            return;
        _wait -= delta;
        while (!_done && _wait <= 0)
        {
            if (_next >= _show.Count)
            {
                Finish();
                break;
            }
            var (wait, step) = _show[_next++];
            step();
            _wait += wait;
        }
    }

    /// <summary>The profile was held at the battle's end and has come free: what came within reach arrives now.</summary>
    private void WatchAccount()
    {
        // Online the relay's word on the end came after the screen opened: the battle counts now (what came within reach
        // arrives), or it is known that it does not.
        if (_confirming && !game.BattleConfirming)
        {
            _confirming = false;
            GD.Print($"battle: the end was settled with the end screen up: {(game.BattleCounted ? "counted" : "not counted")}");
            _waiting = game.BattleCounted && BattleAccounts.Ledger.Waiting;
            if (_noted)
                ShowNote();
            if (game.BattleCounted && !_waiting)
                Arrived();
            return;
        }
        if (!_waiting || BattleAccounts.Ledger.Waiting)
            return;
        _waiting = false;
        _note.Text = Lang.T("The battle is in the profile.");
        Arrived();
    }

    /// <summary>The battle went into the profile after the show began: what came within reach comes on the board, the goals anew.</summary>
    private void Arrived()
    {
        foreach (var id in game.BattleOpened)
            Open(id);
        if (_goalsShown)
            FillGoals();
    }
}
