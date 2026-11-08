using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// How a run of Wired ended, on the board over the world that stands still: the hero as it stood at the end, the score as it
/// came about, the records and what the run opened, each in its turn, and the way on: again, to the hangar or to the menu.
/// Over a recording it shows what the run did and changes nothing, and there is only the way out. Wired is the remake's own
/// addition.
/// </summary>
public partial class WiredEndScreen(Game game, ClassicAssets assets) : MenuPage(null)
{
    /// <summary>Seconds in which a press does nothing at first: the hands are still at the fight, on the keys that would start the next run.</summary>
    private const double HandsOff = 1;

    /// <summary>The same after a press has cut the show short.</summary>
    private const double Settling = 0.5;

    /// <summary>Seconds between two lines of the score.</summary>
    private const double Beat = 0.26;

    private const int GoalsWithUnlocks = 3, GoalsAlone = 5, GoalGap = 5;

    /// <summary>How long the shelf keeps the newest card in view; then it is the player's to scroll.</summary>
    private const double Following = 0.6;

    /// <summary>The frames a new card takes to have its place and the shelf to follow it.</summary>
    private const int SparkFrames = 3;

    /// <summary>What of the stage's height the goals can never have: the margins, head, foot, records and the frames of the right column.</summary>
    private const float Surroundings = 642;

    /// <summary>What the shelf above the goals keeps for itself, with cards on it and with only its one line.</summary>
    private const float ShelfWithUnlocks = 130, ShelfAlone = 34;

    private static readonly float[] SumStops = [0.5f, 0.72f, 1], FactStops = [0.72f, 1], WeaponStops = [0.49f, 0.66f, 0.8f, 1], RecordStops = [0.42f, 0.71f, 1];

    private readonly List<(double Wait, Action Step)> _show = [];
    private readonly List<EndRow> _sums = [], _rows = [], _records = [];
    private readonly List<EndCard> _cards = [], _sparking = [];
    private readonly List<EndGoal> _goals = [];
    private WiredRun _run = null!;
    private WiredState _state = null!;
    private WiredReport _report = null!;
    private WiredRecordNews? _news;
    private HangarScreen _screen = null!;
    private EndFigure _score = null!, _waves = null!, _kills = null!;
    private EndStamp _stamp = null!;
    private EndFanfare _fanfare = null!;
    private ScrollContainer _shelf = null!;
    private VBoxContainer _cardColumn = null!, _goalColumn = null!;
    private Label _recordNote = null!, _nothing = null!;
    private Label? _allOpen;
    private bool _goalsShown;
    private ChipButton? _again;
    private ChipButton _leave = null!;
    private int _next;
    private double _wait, _settle = HandsOff, _following;
    private int _sparkIn;
    private bool _done, _quiet, _framed, _waiting;

    /// <summary>The screen that is up, for automated checks.</summary>
    public static WiredEndScreen? Current { get; private set; }

    /// <summary>For automated checks: the size the page is laid out for.</summary>
    public Vector2 StageSize => Stage.Size;

    /// <summary>For automated checks: the record has gone off.</summary>
    public bool Cheered { get; private set; }

    /// <summary>The show is over and the buttons wait.</summary>
    public bool Done => _done;

    /// <summary>For automated checks: what it says, as figures.</summary>
    public string Figures => $"score {_report.Score}, waves {_state.Stats.WavesCleared}, kills {_state.Stats.Kills}, ending {_run.Ending}, news {_news?.ToString() ?? "none"}, " +
                             $"cards [{string.Join(", ", _cards.Select(c => c.Id))}], goals {_goals.Count}, buttons {(_again is null ? "menu only" : "all")}";

    /// <summary>For automated checks: the button with this word on it.</summary>
    public ChipButton? ButtonOf(string text) => FindChildren("*", "", true, false).OfType<ChipButton>().FirstOrDefault(b => b.Text == text);

    /// <summary>The words for how a run ended.</summary>
    public static string Why(WiredEnding? ending) => Lang.T(ending == WiredEnding.PodLost ? "The pod has fallen" : "No spare bodies left");

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this)
            Current = null;
    }

    protected override void Build()
    {
        _run = game.World.Wired!;
        _state = _run.Heroes.GetValueOrDefault(Game.Player) ?? _run.Heroes.Values.First();
        _report = new WiredReport(_state.Stats, _run.Options.Difficulty);
        if (game.WiredBefore is { } before)
            _news = new WiredRecordNews(before, _report.Score, _state.Stats.WavesCleared, _state.Stats.Kills);
        _waiting = !game.Watching && WiredProfile.Runs.Waiting;

        foreach (var (side, value) in new[] { ("left", 30), ("right", 30), ("top", 24), ("bottom", 24) })
            AddThemeConstantOverride($"margin_{side}", value);
        AddChild(new HangarIcons(assets, () => Game.Look(Game.Player)));

        var compact = Stage.Size.X < 1700;
        var column = Column(12);
        AddChild(column);
        column.AddChild(BuildHead());
        var row = Line(14);
        row.SizeFlagsVertical = SizeFlags.ExpandFill;
        column.AddChild(row);
        row.AddChild(BuildHero(compact ? 372 : 470));
        row.AddChild(BuildScore(compact));
        row.AddChild(BuildRight(compact ? 410 : 490));
        column.AddChild(BuildFoot());
        AddChild(_fanfare = new EndFanfare());

        SwitchOn();
        PlanShow();
    }

    private static Label Title(string text) => MenuTheme.Label(Lang.T(text).ToUpperInvariant(), 18, MenuTheme.Dim.Lightened(0.35f));

    private string Number(long figure) => WiredReport.Figure(figure, Lang.Code == "de");

    private Control BuildHead()
    {
        var options = _run.Options;
        var chip = new ChipPanel { Padding = 6 };
        var row = Line(24);
        row.AddChild(MenuTheme.Label(Lang.T("Run over").ToUpperInvariant(), 40, MenuTheme.Bright));
        var why = MenuTheme.Label(Why(_run.Ending), 25, MenuTheme.Text);
        row.AddChild(why);
        row.AddChild(Spring());
        var where = MenuTheme.Label($"{HangarText.Arena(options.Arena)}   ·   {Lang.T(options.Difficulty.ToString())}", 25, MenuTheme.Text, HorizontalAlignment.Right);
        where.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        row.AddChild(where);
        chip.AddChild(row);
        return chip;
    }

    /// <summary>The hero as it stood at the end, on the hangar's turntable, with its plate.</summary>
    private Control BuildHero(float width)
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
        var name = MenuTheme.Label(_run.Options.HeroName is { Length: > 0 } hero ? hero : Lang.T("Hero"), 30, MenuTheme.Bright, HorizontalAlignment.Center);
        name.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        var loadout = _state.Loadout;
        var chassis = MenuTheme.Label(HangarText.Name(loadout.Chassis.Catalogue), 22, MenuTheme.Text, HorizontalAlignment.Center);
        chassis.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        var weapons = MenuTheme.Paragraph(string.Join("  ·  ", loadout.Weapons.OfType<WiredItem>().Select(w => HangarText.Name(w.Catalogue))), 17, MenuTheme.Dim.Lightened(0.35f));
        weapons.HorizontalAlignment = HorizontalAlignment.Center;
        weapons.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        plate.AddChild(Column(0, name, chassis, weapons));
        column.AddChild(plate);
        return column;
    }

    /// <summary>The middle: the three big numbers, what the score is made of and the run in figures.</summary>
    private Control BuildScore(bool compact)
    {
        var column = Column(10);
        column.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var top = new ChipPanel { Padding = 8 };
        var figures = Line(16);
        _score = new EndFigure("Score", compact ? 76 : 92) { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2.4f };
        _waves = new EndFigure("Waves", compact ? 56 : 68) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _kills = new EndFigure("Kills", compact ? 56 : 68) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        figures.AddChild(_score);
        figures.AddChild(_waves);
        figures.AddChild(_kills);
        top.AddChild(figures);
        top.AddChild(_stamp = new EndStamp());
        column.AddChild(top);

        var sums = new ChipPanel { Padding = 8 };
        var rows = Column(1);
        foreach (var line in _report.Lines)
        {
            var row = new EndRow { Cells = SumCells(line), Stops = SumStops, Last = MenuTheme.Bright };
            _sums.Add(row);
            rows.AddChild(row);
        }
        sums.AddChild(rows);
        column.AddChild(sums);

        var more = new ChipPanel { Padding = 8, SizeFlagsVertical = SizeFlags.ExpandFill };
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var inner = Column(3);
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 26);
        grid.AddThemeConstantOverride("v_separation", 1);
        foreach (var (name, figure) in Facts())
        {
            var fact = new EndRow { Cells = [Lang.T(name), figure], Stops = FactStops, FontSize = 19, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _rows.Add(fact);
            grid.AddChild(fact);
        }
        inner.AddChild(grid);
        if (_report.Weapons.Count > 0)
        {
            var heading = new EndRow { Cells = [Lang.T("Weapon"), Lang.T("Rounds"), Lang.T("Kills"), Lang.T("Damage")], Stops = WeaponStops, FontSize = 19, Heading = true };
            _rows.Add(heading);
            inner.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6), MouseFilter = MouseFilterEnum.Ignore });
            inner.AddChild(heading);
            foreach (var (weapon, use) in _report.Weapons)
            {
                var row = new EndRow { Cells = [HangarText.WeaponName(weapon), Number(use.Rounds), Number(use.Kills), Number((long)use.Damage)], Stops = WeaponStops, FontSize = 19 };
                _rows.Add(row);
                inner.AddChild(row);
            }
        }
        scroll.AddChild(inner);
        more.AddChild(scroll);
        column.AddChild(more);
        return column;
    }

    private string[] SumCells(WiredScoreLine line)
    {
        var points = Number(line.Points);
        return line.Kind switch
        {
            WiredScoreKind.Machines => [Lang.T("Machines destroyed"), Number(line.Amount), points],
            WiredScoreKind.Waves => [Lang.T("Waves cleared"), $"{line.Amount} × {WiredBalance.ScorePerWave}", points],
            WiredScoreKind.Bosses => [Lang.T("Bosses destroyed"), $"{line.Amount} × {WiredBalance.ScorePerBoss}", points],
            WiredScoreKind.Bmus => [Lang.T("BMUs earned"), $"{Number(line.Amount)} ÷ 2", points],
            _ => [Lang.T("Difficulty"), $"× {(line.Amount / 100f).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', Lang.Code == "de" ? ',' : '.')}", $"+{points}"],
        };
    }

    private IEnumerable<(string Name, string Figure)> Facts()
    {
        var stats = _state.Stats;
        yield return ("Damage dealt", Number((long)stats.DamageDealt));
        yield return ("Damage taken", Number((long)stats.DamageTaken));
        yield return ("Wrecks collected", Number(stats.WrecksCollected));
        yield return ("Crates collected", Number(stats.CratesCollected));
        yield return ("Bodies lost", Number(stats.Deaths));
        yield return ("Largest squad", Number(stats.LargestSquad));
        yield return ("Waves without a death", Number(stats.CleanWaves));
    }

    /// <summary>The right: the records, what was opened and what is next.</summary>
    private Control BuildRight(float width)
    {
        var column = Column(10);
        column.CustomMinimumSize = new Vector2(width, 0);
        column.AddChild(BuildRecords());

        var opened = new ChipPanel { Padding = 8, SizeFlagsVertical = SizeFlags.ExpandFill };
        var rows = Column(6, Title("Unlocked"));
        _shelf = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        _cardColumn = Column(4);
        _cardColumn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _nothing = MenuTheme.Label("Nothing new this run", 20, MenuTheme.Dim.Lightened(0.2f));
        _nothing.Visible = false;
        _cardColumn.AddChild(_nothing);
        _shelf.AddChild(_cardColumn);
        rows.AddChild(_shelf);
        opened.AddChild(rows);
        column.AddChild(opened);

        var next = new ChipPanel { Padding = 8 };
        _goalColumn = Column(GoalGap, Title("Next to open"));
        next.AddChild(_goalColumn);
        column.AddChild(next);
        return column;
    }

    private Control BuildRecords()
    {
        var chip = new ChipPanel { Padding = 8 };
        var rows = Column(1, Title("Records"));
        var was = _news?.Before ?? new WiredRecord();
        if (_news is not null)
        {
            rows.AddChild(new EndRow { Cells = ["", Lang.T("This run"), Lang.T("Best so far")], Stops = RecordStops, FontSize = 19, Heading = true });
            _records.Add((EndRow)rows.GetChild(1));
            foreach (var (name, now, best) in new[] { ("Score", _report.Score, was.Score), ("Waves", _state.Stats.WavesCleared, was.Waves), ("Kills", (long)_state.Stats.Kills, was.Kills) })
            {
                var row = new EndRow { Cells = [Lang.T(name), Number(now), best > 0 ? Number(best) : "–"], Stops = RecordStops, Last = MenuTheme.Dim.Lightened(0.35f) };
                _records.Add(row);
                rows.AddChild(row);
            }
        }
        _recordNote = MenuTheme.Paragraph(" ", 19, MenuTheme.Text);
        _recordNote.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        rows.AddChild(_recordNote);
        chip.AddChild(rows);
        return chip;
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
        if (!game.Watching)
        {
            _again = Wide(Button("Again", Again, 76, 32));
            Wide(Button("Hangar", () => game.ExitToMenus(), 76, 32));
        }
        _leave = Wide(Button("Menu", () => game.ExitToMenus(home: true), 76, 32));
        chip.AddChild(row);
        return chip;
    }

    /// <summary>The same hero on the same arena once more, by the same way a run comes up from the hangar.</summary>
    private void Again()
    {
        var options = _run.Options;
        LoadingScreen.Show(game.GetTree(), HangarText.Arena(options.Arena), $"{Lang.T("Wired")}   ·   {Lang.T(options.Difficulty.ToString()).ToUpperInvariant()}", game.RunAgain);
    }

    /// <summary>Esc: first the show, then what the button in the middle does.</summary>
    public override void Back()
    {
        if (game.Watching)
            game.ExitToMenus(home: true);
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

    /// <summary>Line by line the score climbs, then the records have their say, then what was opened comes on the board, one by one.</summary>
    private void PlanShow()
    {
        Then(0.5, () => Sound("stats.wav"));
        var score = 0L;
        foreach (var (row, line) in _sums.Zip(_report.Lines))
        {
            score += line.Points;
            var sum = score;
            Then(Beat, () =>
            {
                row.Arrive();
                Sound("listclik.wav");
                _score.To(sum, Beat * 0.9);
                if (line.Kind == WiredScoreKind.Machines)
                    _kills.To(_state.Stats.Kills, Beat * 2);
                else if (line.Kind == WiredScoreKind.Waves)
                    _waves.To(_state.Stats.WavesCleared, Beat * 2);
            });
        }
        Then(0.35, () =>
        {
            _kills.To(_state.Stats.Kills, 0.01);
            _waves.To(_state.Stats.WavesCleared, 0.01);
            foreach (var row in _rows)
                row.Arrive();
        });
        Then(_news is { Beaten: true } ? 1.1 : 0.5, ShowRecords);
        foreach (var id in game.WiredOpened)
            Then(0.75, () => Open(id));
        Then(0.4, ShowGoals);
    }

    /// <summary>A best beaten comes down on the score with a flash, sparks and the hero's guns; the first run here gets its own plain stamp; a run that fell short says by how much.</summary>
    private void ShowRecords()
    {
        foreach (var row in _records)
            row.Arrive();
        if (_news is not { } news)
        {
            _recordNote.Text = Lang.T(_waiting ? "The run is not in the profile yet: the file cannot be written just now." : game.Watching ? "A recording changes no records." : "This run has been counted.");
            return;
        }
        if (news.NewScore)
            _records[1].Tag();
        if (news.NewWaves)
            _records[2].Tag();
        if (news.NewKills)
            _records[3].Tag();
        _recordNote.Text = news.First ? Lang.T("The first record on this arena at this difficulty.")
            : news.Beaten ? Lang.T("{0} points above your best.", Number(news.Score - news.Before.Score))
            : news.Short > 0 ? Lang.T("{0} points short of your best.", Number(news.Short))
            : news.Score > 0 ? Lang.T("Level with your best.") : Lang.T("Not a point on the board.");
        Cheered = news.Beaten || news.First;
        if (news.Beaten)
        {
            _stamp.Land(Lang.T("New record").ToUpperInvariant(), MenuTheme.Bright, 34);
            _score.Flare(MenuTheme.Bright);
            _fanfare.Flash(MenuTheme.Bright, 0.45f);
            var at = _score.GetGlobalTransform() * (_score.Size / 2);
            _fanfare.Burst(at, MenuTheme.Bright, 90, 760);
            _fanfare.Burst(_screen.GetGlobalTransform() * (_screen.Size / 2), MenuTheme.Glow, 40);
            MenuSounds.Gui("taskmach.wav");
            Salute();
        }
        else if (news.First)
        {
            _stamp.Land(Lang.T("First record").ToUpperInvariant(), MenuTheme.Glow, 26);
            _score.Flare(MenuTheme.Glow);
            MenuSounds.Gui("listclik.wav");
        }
    }

    /// <summary>The hero fires what it carries into the air of the turntable.</summary>
    private void Salute()
    {
        foreach (var weapon in _state.Build(Paths.Data).Weapons)
        {
            _screen.Fire(weapon.Mounting, weapon.Spec);
            if (_screen.MountPoint(weapon.Mounting) is { } mount)
                _screen.Spark(mount);
        }
    }

    /// <summary>What the run opened comes on the board, with the voice of the technician on the first.</summary>
    private void Open(string id)
    {
        if (_cards.Any(c => c.Id == id))
            return;
        var card = new EndCard { Id = id, Map = WiredProfile.Arenas.Any(a => a.Scenario == id) ? HangarCreation.Map(id, assets) : null };
        var first = _cards.Count == 0;
        _nothing.Visible = false;
        _cards.Add(card);
        _cardColumn.AddChild(card);
        card.Arrive();
        MenuSounds.Gui("ctrlpon.wav");
        if (first)
            game.Audio.Say("VID_TECHNICIAN_RESEARCH_COMPLETE", queue: true);
        _sparking.Add(card);
        _sparkIn = SparkFrames;
        _following = Following;
    }

    /// <summary>The newest card comes into view and, once it has its place, what has just come throws its sparks.</summary>
    private void FollowShelf(double delta)
    {
        if (_following > 0 && _cards.Count > 0)
        {
            _following -= delta;
            var newest = _cards[^1];
            _shelf.ScrollVertical = Math.Max(0, (int)(newest.Position.Y + newest.Size.Y - _shelf.Size.Y));
        }
        if (_sparkIn <= 0 || --_sparkIn > 0)
            return;
        foreach (var card in _sparking)
            _fanfare.Burst(card.GetGlobalTransform() * new Vector2(card.Size.Y / 2, card.Size.Y / 2), MenuTheme.Bright, 26, 340);
        _sparking.Clear();
    }

    private void ShowGoals()
    {
        if (_cards.Count == 0)
            _nothing.Visible = true;
        if (_goalsShown)
            return;
        _goalsShown = true;
        _following = Following;
        FillGoals();
    }

    /// <summary>The goals as the profile has them now, in place of any shown before.</summary>
    private void FillGoals()
    {
        List<Control> shown = [.. _goals];
        if (_allOpen is not null)
            shown.Add(_allOpen);
        foreach (var old in shown)
        {
            _goalColumn.RemoveChild(old);
            old.QueueFree();
        }
        _goals.Clear();
        _allOpen = null;
        var goals = WiredProfile.Load().Goals().Take(GoalRoom()).ToList();
        if (goals.Count == 0)
            _goalColumn.AddChild(_allOpen = MenuTheme.Label("Everything is open", 20, MenuTheme.Glow));
        foreach (var goal in goals)
        {
            var row = new EndGoal(goal);
            _goals.Add(row);
            _goalColumn.AddChild(row);
            row.Arrive();
        }
    }

    /// <summary>As many goals as the right column can hold: a large interface size leaves room for fewer.</summary>
    private int GoalRoom()
    {
        var shelf = _cards.Count > 0 ? ShelfWithUnlocks : ShelfAlone;
        return Math.Clamp((int)((Stage.Size.Y - Surroundings - shelf) / (EndGoal.Height + GoalGap)), 1, _cards.Count > 0 ? GoalsWithUnlocks : GoalsAlone);
    }

    private void Finish()
    {
        _done = true;
        Focus(_again ?? _leave);
    }

    /// <summary>A press cuts the show short: all of it stands at once. What was earned still goes off, only the ticking stays away.</summary>
    private void Skip()
    {
        _quiet = true;
        while (_next < _show.Count)
            _show[_next++].Step();
        _score.Land();
        _waves.Land();
        _kills.Land();
        foreach (var row in _sums.Concat(_rows).Concat(_records))
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
            _screen.Present(HeroRig.Compose(assets, Game.Look(Game.Player), _state.Loadout));
            _screen.KeepOff([]);
        }
        WatchProfile();
        FollowShelf(delta);
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

    /// <summary>The profile was held at the end of the run and has come free: what the run opened arrives now, the show or no show.</summary>
    private void WatchProfile()
    {
        if (!_waiting || WiredProfile.Runs.Waiting)
            return;
        _waiting = false;
        _recordNote.Text = Lang.T("The run is in the profile.");
        foreach (var id in game.WiredOpened)
            Open(id);
        // What is still to open was read from the profile without this run: it is read again.
        if (_goalsShown)
            FillGoals();
    }
}
