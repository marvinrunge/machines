using Godot;
using Machines.Net;
using Machines.Import.Model;
using Machines.Import.Text;

namespace Machines.Game;

/// <summary>
/// The campaign's first page (MachGuiCtxCampaign): the players who play it, each with their own progress
/// (<see cref="CampaignProgress"/>), a name to type for a new one, and the picked one's status, the mission last
/// played and its highest score, the missions played and the total score. Continue goes on to the missions.
/// </summary>
public partial class CampaignPlayersPage(MenuScreens screens) : MenuPage(screens.ShowMain)
{
    // MachGuiCtxCampaign refuses these, the names the menus give to slots.
    private static readonly string[] Reserved = ["Open", "Closed", "Computer", "Unavailable"];

    private readonly CampaignProgress _progress = CampaignProgress.Load();
    private ItemList _list = null!;
    private LineEdit _name = null!;
    private Label _status = null!;
    private ChipButton _delete = null!;

    private CampaignPlayer? Picked => _list.GetSelectedItems() is [var index] && index < _progress.Players.Count ? _progress.Players[index] : null;

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading(screens.Text(7308, "Campaign")));
        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var players = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 24 };
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.ItemSelected += _ => ShowStatus();
        _list.ItemActivated += _ => Continue();
        _name = new LineEdit { PlaceholderText = screens.Text(7009, "[New Player Name]"), ContextMenuEnabled = false, MaxLength = 24, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _name.TextSubmitted += _ => New();
        var create = Button(screens.Text(7313, "New"), New, 64, 24);
        create.CustomMinimumSize = new Vector2(200, 64);
        players.AddChild(Column(18, MenuTheme.Label(screens.Text(7208, "Select Player").ToUpperInvariant(), 26, MenuTheme.Bright), _list, Line(18, _name, create)));
        columns.AddChild(players);

        var status = new ChipPanel { CustomMinimumSize = new Vector2(640, 0), Padding = 28 };
        _status = MenuTheme.Paragraph("", 25, MenuTheme.Text);
        status.AddChild(Column(22, MenuTheme.Label(screens.Text(7010, "Current Status").ToUpperInvariant(), 26, MenuTheme.Bright), _status));
        columns.AddChild(status);

        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(340, 76);
        _delete = Button(screens.Text(7314, "Delete"), AskDelete);
        _delete.CustomMinimumSize = new Vector2(340, 76);
        var go = Button(screens.Text(7312, "Continue"), Continue, 76, 30);
        go.CustomMinimumSize = new Vector2(480, 76);
        page.AddChild(Line(Gap, back, _delete, Spring(), go));

        Fill(_progress.Current);
        SwitchOn();
        Focus(_progress.Players.Count > 0 ? _list : _name);
    }

    private void Fill(string? select)
    {
        _list.Clear();
        foreach (var player in _progress.Players)
            _list.AddItem(player.Name);
        var at = _progress.Players.FindIndex(p => p.Name == select);
        if (at < 0 && _progress.Players.Count > 0)
            at = 0;
        if (at >= 0)
            _list.Select(at);
        ShowStatus();
    }

    private void ShowStatus()
    {
        var player = Picked;
        _delete.Disabled = player is null;
        if (player is null)
        {
            _status.Text = _progress.Players.Count == 0 ? "Type a name and press New to start the campaign." : "";
            return;
        }
        var total = Campaign.Catalogue.CampaignScenarios.Count();
        var played = player.Missions.Count(m => m.Value.Played > 0);
        var last = player.LastPlayed is { } name && Campaign.Entry(name) is { } entry ? entry : null;
        _status.Text = (last is null
                ? screens.Text(7076, "No Scenarios Played")
                : $"{screens.Text(7025, "Scenario:")}  {screens.Text(last.StringId, last.Name)}\n" +
                  $"{screens.Text(7026, "Highest Score:")}  {player.Missions[last.Name].BestScore}") +
            $"\n\n{screens.Text(7075, "Scenarios Played:")}  {played} / {total}\n" +
            Lang.T("Scenarios Won:") + $"  {player.Missions.Count(m => m.Value.Won)} / {total}\n" +
            $"{screens.Text(7074, "Total Score:")}  {player.TotalScore}";
    }

    /// <summary>A player of the name typed, or the one of that name already there.</summary>
    private void New()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            Stage.Message("To create a new player, type in the player's name, then press New.");
            return;
        }
        if (Reserved.Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Stage.Message(screens.Text(7103, "Open, Closed, Computer and Unavailable are not valid names. Please use a different name."));
            return;
        }
        if (!_progress.Players.Any(p => p.Name == name))
        {
            _progress.Players.Add(new CampaignPlayer { Name = name });
            if (!Save())
                return;
        }
        _name.Text = "";
        Fill(name);
        Focus(_list);
    }

    private void AskDelete()
    {
        if (Picked is not { } player)
            return;
        Stage.Message(Lang.T("Delete {0}, how far they have come in the campaign and their saved games?", player.Name), ("Delete", () =>
        {
            foreach (var save in Replay.CampaignSaves(Game.SaveFolder, Game.SaveExtension, player.Name))
                File.Delete(save);
            _progress.Players.Remove(player);
            if (_progress.Current == player.Name)
                _progress.Current = null;
            Save();
            Fill(null);
        }), ("Keep", null));
    }

    private void Continue()
    {
        if (_name.Text.Trim().Length > 0)
            New();
        if (Picked is not { } player)
        {
            Stage.Message(screens.Text(7111, "To continue to the next screen you must enter a [New Player Name] or select a previously-created player."));
            return;
        }
        _progress.Current = player.Name;
        if (Save())
            screens.ShowMissions();
    }

    private bool Save()
    {
        try
        {
            _progress.Save();
            return true;
        }
        catch (IOException exception)
        {
            Stage.Message(Lang.T("The campaign progress cannot be saved: {0}", exception.Message));
            return false;
        }
    }
}

/// <summary>
/// The choice of mission (MachGuiCtxScenario): the star systems, their planets and the planets' missions in a tree,
/// as far as the missions won open them (<c>AFTER</c>), the won ones with their highest score; beside it the film
/// and text of what is picked. It starts on the mission last chosen, or the next after the last won.
/// </summary>
public partial class CampaignMissionsPage(MenuScreens screens) : MenuPage(screens.ShowCampaign)
{
    private readonly CampaignProgress _progress = CampaignProgress.Load();
    private readonly List<object> _entries = [];
    private CampaignPlayer _player = null!;
    private Tree _tree = null!;
    private Label _title = null!;
    private RichTextLabel _text = null!;
    private Control _pictureBox = null!;
    private ChipButton _briefing = null!;
    private CatalogueScenario? _selected;

    protected override void Build()
    {
        _player = _progress.CurrentPlayer ?? new CampaignPlayer { Name = "?" };
        var page = Column(Gap);
        AddChild(page);
        var who = MenuTheme.Label(_player.Name.ToUpperInvariant(), 26, MenuTheme.Dim);
        who.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        page.AddChild(Line(Gap, Heading(screens.Text(7203, "Scenarios")), Spring(), who));
        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var list = new ChipPanel { CustomMinimumSize = new Vector2(700, 0), Padding = 16 };
        _tree = new Tree { HideRoot = true, Columns = 2 };
        _tree.SetColumnExpand(1, false);
        _tree.SetColumnCustomMinimumWidth(1, 110);
        _tree.ItemSelected += Picked;
        _tree.ItemActivated += Activated;
        list.AddChild(_tree);
        columns.AddChild(list);

        var about = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 22 };
        _pictureBox = new Control { CustomMinimumSize = new Vector2(0, 400), MouseFilter = MouseFilterEnum.Ignore };
        _title = MenuTheme.Label("", 30, MenuTheme.Bright);
        _text = Campaign.TextView(23);
        about.AddChild(Column(16, _pictureBox, _title, _text));
        columns.AddChild(about);

        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(340, 76);
        _briefing = Button(screens.Text(7304, "Briefing"), Briefing, 76, 30);
        _briefing.CustomMinimumSize = new Vector2(480, 76);
        page.AddChild(Line(Gap, back, Spring(), _briefing));

        Fill();
        SwitchOn();
        Focus(_tree);
    }

    private void Fill()
    {
        var root = _tree.CreateItem();
        TreeItem? last = null, open = null, first = null;
        TreeItem Item(TreeItem parent, object entry, int stringId, string name, Color colour)
        {
            var item = _tree.CreateItem(parent);
            item.SetText(0, screens.Text(stringId, name));
            item.SetCustomColor(0, colour);
            item.SetMetadata(0, Variant.From(_entries.Count));
            item.SetSelectable(1, false);
            _entries.Add(entry);
            return item;
        }
        foreach (var system in Campaign.Catalogue.Campaign.Where(s => _player.IsAvailable(s.After)))
        {
            var systemItem = Item(root, system, system.StringId, system.Name, MenuTheme.Bright);
            foreach (var planet in system.Planets.Where(p => _player.IsAvailable(p.After)))
            {
                var planetItem = Item(systemItem, planet, planet.StringId, planet.Name, MenuTheme.Text);
                foreach (var mission in planet.Scenarios.Where(s => _player.IsAvailable(s.After)))
                {
                    var won = _player.HasWon(mission.Name);
                    var item = Item(planetItem, mission, mission.StringId, mission.Name, won ? MenuTheme.Text : MenuTheme.Bright);
                    if (won)
                    {
                        item.SetText(1, _player.Missions[mission.Name].BestScore.ToString());
                        item.SetCustomColor(1, MenuTheme.Dim);
                        item.SetTextAlignment(1, HorizontalAlignment.Right);
                    }
                    first ??= item;
                    if (!won)
                        open ??= item;
                    if (mission.Name == _player.LastScenario)
                        last = item;
                }
            }
        }
        if ((last ?? open ?? first) is { } select)
        {
            select.Select(0);
            _tree.CallDeferred(Tree.MethodName.ScrollToItem, select, true);
        }
    }

    private object? EntryOf(TreeItem? item) =>
        item?.GetMetadata(0).VariantType == Variant.Type.Int ? _entries[item.GetMetadata(0).AsInt32()] : null;

    private void Picked()
    {
        _selected = null;
        switch (EntryOf(_tree.GetSelected()))
        {
            case StarSystem system:
                Show(screens.Text(system.StringId, system.Name), system.Flic, system.Text);
                break;
            case CampaignPlanet planet:
                Show(screens.Text(planet.StringId, planet.Name), planet.Flic, planet.Text);
                break;
            case CatalogueScenario mission:
                _selected = mission;
                Show(screens.Text(mission.StringId, mission.Name), mission.Media.GetValueOrDefault("CAMPAIGN"), mission.Text);
                if (_player.LastScenario != mission.Name && _progress.CurrentPlayer is not null)
                {
                    _player.LastScenario = mission.Name;
                    try { _progress.Save(); } catch (IOException) { }
                }
                break;
        }
        _briefing.Disabled = _selected is null;
    }

    private void Activated()
    {
        if (_selected is not null)
            Briefing();
        else if (_tree.GetSelected() is { } item)
            item.Collapsed = !item.Collapsed;
    }

    private void Show(string title, string? film, string? textFile)
    {
        _title.Text = title.ToUpperInvariant();
        _text.Text = Campaign.Rich(screens.Assets.ScenarioTexts(textFile)?.CampaignText
            ?? (screens.Assets.GameFolder is null ? "The campaign's films and texts come from your own game copy (Options, Import)." : ""));
        foreach (var old in _pictureBox.GetChildren())
            old.QueueFree();
        if (Campaign.Media(screens.Assets.GameFolder, film) is { } path)
        {
            var view = new SmackerView(path) { Loop = true };
            _pictureBox.AddChild(view);
            view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
    }

    private void Briefing()
    {
        if (_selected is not null && _progress.CurrentPlayer is not null)
            screens.ShowBriefing(_selected);
    }
}

/// <summary>
/// The briefing before a mission (MachGuiCtxBriefing): the speaker's face, the objective, spoken as it shows, with
/// the tasks known from the start, and once the mission has been played its hints. Start plays the film of the
/// landing when the mission has one (ENTRY), then loads the match.
/// </summary>
public partial class CampaignBriefingPage(MenuScreens screens, CatalogueScenario mission) : MenuPage(screens.ShowMissions)
{
    private readonly CampaignProgress _progress = CampaignProgress.Load();
    private ScenarioText? _texts;
    private RichTextLabel _text = null!;
    private ChipButton _objectives = null!, _hints = null!;

    protected override void Build()
    {
        _texts = screens.Assets.ScenarioTexts(mission.Text);
        var name = screens.Text(mission.StringId, mission.Name);
        var page = Column(Gap);
        AddChild(page);
        var title = MenuTheme.Label(name.ToUpperInvariant(), 28, MenuTheme.Text);
        title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        page.AddChild(Line(Gap, Heading(screens.Text(7304, "Briefing")), Spring(), title));
        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var face = new ChipPanel { Padding = 12, CustomMinimumSize = new Vector2(420, 420) };
        var faceBox = new Control { MouseFilter = MouseFilterEnum.Ignore };
        face.AddChild(faceBox);
        if (Campaign.Media(screens.Assets.GameFolder, mission.Media.GetValueOrDefault("BRIEFING")) is { } film)
        {
            var view = new SmackerView(film) { Loop = true };
            faceBox.AddChild(view);
            view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
        _objectives = Button(screens.Text(7316, "Objectives"), ShowObjectives);
        _hints = Button(screens.Text(7315, "Hints"), ShowHints);
        var left = Column(26, face, _objectives, _hints);
        left.CustomMinimumSize = new Vector2(420, 0);
        columns.AddChild(left);

        var about = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 28 };
        _text = Campaign.TextView(25);
        about.AddChild(_text);
        columns.AddChild(about);

        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(340, 76);
        var start = Button(screens.Text(7323, "Start"), Start, 76, 30);
        start.CustomMinimumSize = new Vector2(480, 76);
        page.AddChild(Line(Gap, back, Spring(), start));

        ShowObjectives();
        // The objective is spoken as the page comes up, and stops when it goes.
        if (_texts?.ObjectiveVoiceMails.FirstOrDefault() is { Length: > 0 } voice)
            MenuSounds.Speak(voice);
        TreeExiting += MenuSounds.Hush;
        SwitchOn();
        Focus(start);
    }

    private void ShowObjectives()
    {
        _text.Text = _texts is null
            ? "The briefing comes from your own game copy (Options, Import)."
            : Campaign.Rich(Campaign.Objectives(_texts, null, screens.Text));
        _objectives.Disabled = true;
        // As in the original, the hints are there once the mission has been played.
        _hints.Disabled = _texts is not { Hint.Length: > 0 } || _progress.CurrentPlayer?.Missions.GetValueOrDefault(mission.Name) is not { Played: > 0 };
    }

    private void ShowHints()
    {
        _text.Text = Campaign.Rich(_texts?.Hint ?? "");
        _hints.Disabled = true;
        _objectives.Disabled = false;
    }

    private void Start()
    {
        if (LoadingScreen.Busy || _progress.CurrentPlayer is not { } player)
            return;
        MenuSounds.Hush();
        MatchRequest.Current = Campaign.Request(mission, player);
        var tree = GetTree();
        var name = screens.Text(mission.StringId, mission.Name);
        void Load() => LoadingScreen.Show(tree, name, $"{mission.Planet.ToUpperInvariant()}   ·   {Lang.T("CAMPAIGN")}");
        if (Campaign.Media(screens.Assets.GameFolder, mission.Media.GetValueOrDefault("ENTRY")) is { } film)
            FilmScreen.Play(tree, film, Load);
        else
            Load();
    }
}

/// <summary>
/// A cut-scene of the campaign over everything, on black: the landing before a mission (ENTRY) and the films after
/// one (WIN, LOSE). A click or Escape skips it; then it goes and <c>done</c> follows.
/// </summary>
public partial class FilmScreen : CanvasLayer
{
    public static void Play(SceneTree tree, string path, Action done)
    {
        var screen = new FilmScreen { Layer = 50, Name = "Film" };
        var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Stop };
        black.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(black);
        var view = new SmackerView(path) { Skippable = true };
        black.AddChild(view);
        view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        black.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true })
                view.Skip();
        };
        view.Finished += () =>
        {
            screen.QueueFree();
            done();
        };
        tree.Root.AddChild(screen);
    }
}
