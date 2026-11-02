using Godot;
using Machines.Net;

namespace Machines.Game;

/// <summary>
/// A page of files kept next to the game, newest first: saved games to load (MachGuiCtxLoad) or to save over
/// (MachGuiCtxSave, with a new one at the top of the list), and recorded matches to watch. Double clicking one, or
/// its button, does what the page is for; Delete asks first.
/// </summary>
public partial class FilesPage : MenuPage
{
    private enum Kind { Load, Save, Replays }

    private readonly Kind _kind;
    private readonly string _folder, _extension;
    private readonly Func<string, string?> _act;
    private List<string> _files = [];
    private ItemList _list = null!;
    private LineEdit? _name;
    private Label _empty = null!;
    private ChipButton _go = null!, _delete = null!;

    private FilesPage(Kind kind, string folder, string extension, Action back, Func<string, string?> act) : base(back) =>
        (_kind, _folder, _extension, _act) = (kind, folder, extension, act);

    /// <summary>The saved games to load; <paramref name="load"/> opens one and says why not when it cannot.</summary>
    public static FilesPage Load(Action back, Func<string, string?> load) => new(Kind.Load, Game.SaveFolder, Game.SaveExtension, back, load);

    /// <summary>The saved games to save over, or a new one; <paramref name="save"/> saves under a name and says why not when it cannot.</summary>
    public static FilesPage Save(Action back, Func<string, string?> save) => new(Kind.Save, Game.SaveFolder, Game.SaveExtension, back, save);

    /// <summary>The recorded matches in <c>replays/</c>; <paramref name="watch"/> plays one back.</summary>
    public static FilesPage Replays(Action back, Func<string, string?> watch) =>
        new(Kind.Replays, System.IO.Path.Combine(Paths.Root, "replays"), Replay.Extension, back, watch);

    /// <summary>Leaves the menus for a saved game, carried on from where it was saved.</summary>
    public static string? OpenSaved(SceneTree tree, string path)
    {
        try
        {
            var saved = Replay.Load(path);
            var local = saved.Match.Players.FirstOrDefault(p => !p.Ai)?.Slot;
            MatchRequest.Current = MatchRequest.FromMatch(saved.Match, clientId: -1, local) with { Saved = saved };
            LoadingScreen.Show(tree, System.IO.Path.GetFileNameWithoutExtension(path).ToUpperInvariant(),
                $"{Lang.T("SAVED GAME")}   ·   {LoadingScreen.MapName(MatchRequest.Current)}");
            return null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return exception.Message;
        }
    }

    /// <summary>Leaves the menus to watch a recorded match.</summary>
    public static string? OpenReplay(SceneTree tree, string path)
    {
        try
        {
            var replay = Replay.Load(path);
            MatchRequest.Current = MatchRequest.FromMatch(replay.Match, clientId: -1) with { Replay = replay };
            LoadingScreen.Show(tree, System.IO.Path.GetFileNameWithoutExtension(path).ToUpperInvariant(),
                $"{Lang.T("REPLAY")}   ·   {LoadingScreen.MapName(MatchRequest.Current)}");
            return null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return exception.Message;
        }
    }

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading(_kind switch { Kind.Load => "Load game", Kind.Save => "Save game", _ => "Replays" }));
        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var chip = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 22 };
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.ItemSelected += Picked;
        _list.ItemActivated += _ => Go();
        _empty = MenuTheme.Label(_kind == Kind.Replays ? "No recorded matches yet: every match keeps one when it ends." : "No saved games yet.",
            24, MenuTheme.Dim, HorizontalAlignment.Center);
        var listColumn = Column(16, _list);
        if (_kind == Kind.Save)
        {
            _name = new LineEdit { PlaceholderText = "Name of the save", ContextMenuEnabled = false };
            _name.TextSubmitted += _ => Go();
            listColumn.AddChild(Setting("Name", _name, 100));
        }
        var layers = new Control();
        foreach (var layer in new Control[] { listColumn, new CenterContainer { MouseFilter = MouseFilterEnum.Ignore } })
        {
            layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            layers.AddChild(layer);
        }
        layers.GetChild(1).AddChild(_empty);
        chip.AddChild(layers);
        columns.AddChild(chip);

        _go = Button(_kind switch { Kind.Load => "Load", Kind.Save => "Save", _ => "Watch" }, Go, 80, 30);
        _delete = Button("Delete", AskDelete);
        var buttons = Column(26, _go, _delete);
        buttons.CustomMinimumSize = new Vector2(400, 0);
        columns.AddChild(buttons);

        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, back));

        Fill(0);
        SwitchOn();
        Focus(_files.Count > 0 || _kind == Kind.Save ? _list : back);
    }

    private void Fill(int select)
    {
        _files = Directory.Exists(_folder)
            ? Directory.GetFiles(_folder, "*" + _extension).OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : [];
        _list.Clear();
        if (_kind == Kind.Save)
            _list.AddItem("New save game");
        foreach (var file in _files)
            _list.AddItem($"{System.IO.Path.GetFileNameWithoutExtension(file)}     {File.GetLastWriteTime(file).ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)}");
        _empty.Visible = _list.ItemCount == 0;
        if (_list.ItemCount > 0)
        {
            _list.Select(Math.Clamp(select, 0, _list.ItemCount - 1));
            Picked(_list.GetSelectedItems()[0]);
        }
        else
        {
            _go.Disabled = _delete.Disabled = true;
        }
    }

    /// <summary>The file the list's pick stands for; null for a new save.</summary>
    private string? PickedFile =>
        _list.GetSelectedItems() is [var index] && (_kind == Kind.Save ? index - 1 : index) is var file and >= 0 && file < _files.Count ? _files[file] : null;

    private void Picked(long index)
    {
        var file = PickedFile;
        _delete.Disabled = file is null;
        _go.Disabled = file is null && _kind != Kind.Save;
        if (_name is not null)
            _name.Text = file is null
                ? $"{DateTime.Now:yyyy-MM-dd HH-mm} {MatchRequest.Current.Scenario}"
                : System.IO.Path.GetFileNameWithoutExtension(file);
    }

    private void Go()
    {
        if (_go.Disabled)
            return;
        string? why;
        if (_kind == Kind.Save)
        {
            if (string.IsNullOrWhiteSpace(_name!.Text))
                return;
            why = _act(_name.Text.Trim());
        }
        else
        {
            if (PickedFile is not { } file)
                return;
            why = _act(file);
        }
        if (why is not null)
            Stage.Message(_kind switch { Kind.Save => "Not saved: ", Kind.Load => "Cannot load: ", _ => "That replay cannot be read: " } + why);
    }

    private void AskDelete()
    {
        if (PickedFile is not { } file)
            return;
        Stage.Message(Lang.T("Delete {0}?", System.IO.Path.GetFileNameWithoutExtension(file)), ("Delete", () =>
        {
            var at = _list.GetSelectedItems()[0];
            File.Delete(file);
            Fill(at);
        }), ("Keep", null));
    }
}
