using Godot;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Import.Text;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The skirmish setup: every map of the original skirmish and multiplayer menus (sceninfo.dat), with the
/// original settings — players, colour, start positions, starting BMUs, mineral resources, technology level
/// and victory condition.
/// </summary>
public partial class SkirmishMenu : Control
{
    private static readonly string[] SizeNames = ["Small", "Medium", "Large"];
    private static readonly Race[] Colours = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    // The last settings, kept while the game runs so returning to the menu shows them again.
    private static (string Scenario, string Planet)? _lastMap;
    private static int _lastPlayers = 2, _lastColour, _lastStarts = 1, _lastBmu, _lastResources = 2, _lastTech, _lastVictory;

    private ClassicAssets _assets = null!;
    private StringTable? _strings;
    private readonly Dictionary<string, Texture2D?> _maps = [];

    private Tree _tree = null!;
    private Label _title = null!;
    private TextureRect _preview = null!;
    private Label _description = null!;
    private OptionButton _players = null!, _colour = null!, _starts = null!, _bmu = null!, _resources = null!, _tech = null!, _victory = null!;
    private Button _start = null!;

    private CatalogueScenario? _selected;
    private List<Race> _slots = [];
    private int? _mapBmu;
    private double _runTime;

    public override void _Ready()
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_MENU_SCREENSHOT") is null
            && (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT") is not null || System.Environment.GetEnvironmentVariable("MACHINES_MAP") is not null))
        {
            // Automated checks of a match skip the menu.
            CallDeferred(MethodName.StartMatch, false);
            return;
        }

        _assets = new ClassicAssets(Paths.Repository);
        var stringsPath = System.IO.Path.Combine(Paths.OriginalData, "machstrg.xml");
        _strings = File.Exists(stringsPath) ? StringTableParser.Load(stringsPath) : null;
        BuildLayout();
        FillTree(ScenarioCatalogueParser.Load(System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat")));
    }

    public override void _Process(double delta)
    {
        var path = System.Environment.GetEnvironmentVariable("MACHINES_MENU_SCREENSHOT");
        if (string.IsNullOrEmpty(path))
            return;
        _runTime += delta;
        if (_runTime < 2)
            return;
        GetViewport().GetTexture().GetImage().SavePng(path);
        GetTree().Quit();
    }

    // ---- layout ---------------------------------------------------------------------------------

    private void BuildLayout()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0.06f, 0.07f, 0.09f), AnchorRight = 1, AnchorBottom = 1 });

        var margin = new MarginContainer { AnchorRight = 1, AnchorBottom = 1 };
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 24);
        AddChild(margin);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 16);
        margin.AddChild(page);

        var heading = new Label { Text = "MACHINES  ·  Skirmish" };
        heading.AddThemeFontSizeOverride("font_size", 30);
        heading.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.3f));
        page.AddChild(heading);

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        page.AddChild(columns);

        _tree = new Tree { CustomMinimumSize = new Vector2(360, 0), HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        _tree.ItemSelected += OnMapSelected;
        _tree.ItemActivated += () => StartMatch(true);
        columns.AddChild(_tree);

        var middle = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        middle.AddThemeConstantOverride("separation", 10);
        columns.AddChild(middle);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 22);
        middle.AddChild(_title);
        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(320, 320),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        middle.AddChild(_preview);
        _description = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320, 0) };
        middle.AddChild(_description);

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        right.AddThemeConstantOverride("separation", 12);
        columns.AddChild(right);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 10);
        right.AddChild(grid);

        _players = Option(grid, "Players", [], _ => { });
        _colour = Option(grid, "Your colour", Colours.Select(c => c.ToString()), i => _lastColour = i);
        _starts = Option(grid, "Start positions", ["Fixed", "Random"], i => _lastStarts = i);
        _bmu = Option(grid, "Starting BMUs", [], i => _lastBmu = i);
        _resources = Option(grid, "Mineral resources", ["Low", "Medium", "High"], i => _lastResources = i);
        _tech = Option(grid, "Technology", ["Map default", "Low", "Medium", "High"], i => _lastTech = i);
        _victory = Option(grid, "Victory", new[] { "Annihilation", "Destroy pod" }
            .Concat(MatchOptions.TimeLimits.Select(s => $"Timed: {FormatMinutes(s)}")), i => _lastVictory = i);
        _players.ItemSelected += i => _lastPlayers = (int)i + 2;

        _colour.Selected = _lastColour;
        _starts.Selected = _lastStarts;
        _resources.Selected = _lastResources;
        _tech.Selected = _lastTech;
        _victory.Selected = _lastVictory;

        right.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _start = new Button { Text = "Start", CustomMinimumSize = new Vector2(0, 44) };
        _start.Pressed += () => StartMatch(true);
        right.AddChild(_start);
        var quit = new Button { Text = "Quit", CustomMinimumSize = new Vector2(0, 32) };
        quit.Pressed += () => GetTree().Quit();
        right.AddChild(quit);

        var hint = new Label { Text = "In the game: F10 returns here.", Modulate = new Color(1, 1, 1, 0.6f) };
        right.AddChild(hint);
    }

    private static OptionButton Option(GridContainer grid, string label, IEnumerable<string> items, Action<int> changed)
    {
        grid.AddChild(new Label { Text = label });
        var option = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
        foreach (var item in items)
            option.AddItem(item);
        option.ItemSelected += i => changed((int)i);
        grid.AddChild(option);
        return option;
    }

    private static string FormatMinutes(int seconds) => seconds < 3600 ? $"{seconds / 60} min" : $"{seconds / 3600.0:0.#} h";

    // ---- maps -----------------------------------------------------------------------------------

    private void FillTree(ScenarioCatalogue catalogue)
    {
        var root = _tree.CreateItem();
        TreeItem? first = null, remembered = null;
        foreach (var (section, terrains) in new[] { ("Skirmish", catalogue.Skirmish), ("Multiplayer maps", catalogue.Multiplayer) })
        {
            var sectionItem = _tree.CreateItem(root);
            sectionItem.SetText(0, section);
            sectionItem.SetSelectable(0, false);
            foreach (var size in terrains.GroupBy(t => t.Size))
            {
                var sizeItem = _tree.CreateItem(sectionItem);
                sizeItem.SetText(0, SizeNames[(int)size.Key]);
                sizeItem.SetSelectable(0, false);
                foreach (var terrain in size)
                {
                    var terrainItem = _tree.CreateItem(sizeItem);
                    terrainItem.SetText(0, Text(terrain.StringId, terrain.Name));
                    terrainItem.SetSelectable(0, false);
                    terrainItem.Collapsed = section != "Skirmish";
                    foreach (var scenario in terrain.Scenarios)
                    {
                        var item = _tree.CreateItem(terrainItem);
                        item.SetText(0, Text(scenario.StringId, scenario.Name));
                        item.SetMetadata(0, Variant.From(Entries.Count));
                        Entries.Add(scenario);
                        first ??= item;
                        if (_lastMap == (scenario.Name, scenario.Planet) && remembered is null)
                            remembered = item;
                    }
                }
            }
        }

        var preselect = System.Environment.GetEnvironmentVariable("MACHINES_MENU_SELECT");
        if (preselect is not null)
            remembered = FindItem(root, preselect) ?? remembered;
        var select = remembered ?? first;
        if (select is not null)
        {
            for (var parent = select.GetParent(); parent is not null; parent = parent.GetParent())
                parent.Collapsed = false;
            select.Select(0);
            _tree.ScrollToItem(select);
        }
    }

    private List<CatalogueScenario> Entries { get; } = [];

    private TreeItem? FindItem(TreeItem item, string wanted)
    {
        for (var child = item.GetFirstChild(); child is not null; child = child.GetNext())
        {
            if (child.GetMetadata(0).VariantType == Variant.Type.Int)
            {
                var entry = Entries[child.GetMetadata(0).AsInt32()];
                if ($"{entry.Name},{entry.Planet}" == wanted)
                    return child;
            }
            if (FindItem(child, wanted) is { } found)
                return found;
        }
        return null;
    }

    private string Text(int id, string fallback) =>
        _strings is not null && _strings.Strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text : fallback;

    private void OnMapSelected()
    {
        var item = _tree.GetSelected();
        if (item?.GetMetadata(0).VariantType != Variant.Type.Int)
            return;
        _selected = Entries[item.GetMetadata(0).AsInt32()];
        _lastMap = (_selected.Name, _selected.Planet);

        var scenario = Paths.Data.LoadScenario(_selected.Name);
        _slots = scenario.Races.Select(r => r.Race).OrderBy(r => r).ToList();
        _mapBmu = scenario.Races.Select(r => r.Bmu).FirstOrDefault(b => b is not null);
        var maxPlayers = Math.Min(_selected.MaxPlayers ?? _slots.Count, _slots.Count);

        _title.Text = $"{item.GetText(0)}   ·   {_selected.Planet}   ·   up to {maxPlayers} players";
        if (!_maps.TryGetValue(_selected.Planet, out var map))
            _maps[_selected.Planet] = map = _assets.PlanetMap(_selected.Planet);
        _preview.Texture = map;
        _description.Text = _assets.MenuText(_selected.Text)
            ?? (_assets.HasClassicModels ? "" : "Map previews and descriptions come from your own game copy (see tools/classic-export).");

        _players.Clear();
        for (var n = Math.Min(2, maxPlayers); n <= maxPlayers; n++)
            _players.AddItem(n == 1 ? "1 (no opponent)" : n.ToString());
        _players.Selected = Math.Clamp(_lastPlayers - Math.Min(2, maxPlayers), 0, _players.ItemCount - 1);

        _bmu.Clear();
        _bmu.AddItem(_mapBmu is { } bmu ? $"Map default ({bmu})" : "Map default");
        foreach (var level in Enum.GetValues<StartingBmu>().Skip(1))
            _bmu.AddItem(MatchOptions.BmuFor(level)!.Value.ToString());
        _bmu.Selected = _lastBmu;

        _start.Disabled = _slots.Count == 0;
    }

    // ---- starting -------------------------------------------------------------------------------

    private void StartMatch(bool fromMenu)
    {
        if (fromMenu)
        {
            if (_selected is null || _slots.Count == 0)
                return;
            var players = _players.GetItemText(_players.Selected) is var text && int.TryParse(text.Split(' ')[0], out var n) ? n : 2;
            var victory = _victory.Selected switch
            {
                0 => VictoryCondition.Annihilation,
                1 => VictoryCondition.Pod,
                _ => VictoryCondition.Timer,
            };
            var options = new MatchOptions
            {
                StartingBmu = (StartingBmu)_bmu.Selected,
                Resources = ResourceLevel.Low + _resources.Selected,
                TechLevel = (TechLevel)_tech.Selected,
                Victory = victory,
                TimeLimit = victory == VictoryCondition.Timer ? MatchOptions.TimeLimits[_victory.Selected - 2] : 1800,
            };
            var starts = StartPositions.Assign(_slots, players, Colours[_colour.Selected], random: _starts.Selected == 1, new Random());
            MatchRequest.Current = new MatchRequest(_selected.Name, _selected.Planet, starts, options);
        }
        GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
    }
}
