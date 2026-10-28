using Godot;
using Machines.Import.Model;
using Machines.Import.Text;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The original's menus over a running match (MachGuiCtxInGameOptions and the screens it leads to), drawn as its
/// 640 by 480 screens scaled to the window: the backdrop, the buttons that glow under the mouse with their names
/// in the large menu font (MachGuiMenuButton), and lists and texts in the small one. Continue goes back to the
/// match; Briefing shows the scenario's text; Restart starts it again; Hot Keys lists the keys; Options sets the
/// volumes and the look; Save Game and Load Game keep and fetch matches; Exit Game leaves for the main menu.
/// The arrow keys move between buttons (navkey.wav), Enter presses one, Esc goes back.
/// </summary>
public partial class InGameMenu(Game game, bool statistics = false) : Control
{
    private enum Page { Main, Briefing, Hotkeys, Options, Save, Load, Statistics }

    private sealed record MenuButton(Rect2 Box, int StringId, string Fallback, Action Act, bool Enabled = true);

    private Page _page = Page.Main;
    private readonly List<MenuButton> _buttons = [];
    private int _focus = -1, _hover = -1, _pressed = -1;
    private float _scale = 1;
    private Vector2 _origin;
    private StringTable? _strings;
    private string? _note;

    // Save and Load: the saved games, newest first, and the one picked (-1: a new save).
    private List<string> _saves = [];
    private int _picked = -1;

    // Options as set on the page, kept only on OK.
    private float _music, _sound;
    private bool _remake;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Nearest;
        var path = System.IO.Path.Combine(Paths.OriginalData, "machstrg.xml");
        _strings = File.Exists(path) ? StringTableParser.Load(path) : null;
        Show(statistics ? Page.Statistics : Page.Main);
    }

    private string Text(int id, string fallback) =>
        _strings is not null && _strings.Strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;

    private static Rect2 Box(float x0, float y0, float x1, float y1) => new(x0, y0, x1 - x0, y1 - y0);

    /// <summary>Lays a page out: its buttons in the original's places (ctxingam, ctxhotky, ctxoptns, ctxsave, ctxload, ctxbrief).</summary>
    private void Show(Page page)
    {
        _page = page;
        _buttons.Clear();
        (_focus, _hover, _pressed, _note) = (-1, -1, -1, null);
        switch (page)
        {
            case Page.Main:
                _buttons.Add(new(Box(90, 72, 281, 115), 7312, "Continue", game.CloseMenu));
                _buttons.Add(new(Box(90, 178, 281, 222), 7304, "Briefing", () => Show(Page.Briefing)));
                _buttons.Add(new(Box(90, 282, 281, 326), 7305, "Restart", game.Restart, game.CanSave));
                _buttons.Add(new(Box(90, 377, 281, 420), 7306, "Hot Keys", () => Show(Page.Hotkeys)));
                _buttons.Add(new(Box(344, 72, 536, 114), 7321, "Options", () => Show(Page.Options)));
                _buttons.Add(new(Box(344, 178, 536, 221), 7307, "Save Game", () => Show(Page.Save), game.CanSave));
                _buttons.Add(new(Box(344, 282, 536, 325), 7310, "Load Game", () => Show(Page.Load), !game.Online));
                _buttons.Add(new(Box(344, 377, 536, 419), 7303, "Exit Game", game.ExitToMenus));
                break;
            case Page.Briefing:
                _buttons.Add(new(Box(131, 363, 301, 401), 7302, "OK", game.CloseMenu));
                _buttons.Add(new(Box(132, 416, 301, 453), 7311, "Cancel", () => Show(Page.Main)));
                break;
            case Page.Hotkeys:
                _buttons.Add(new(Box(351, 420, 553, 464), 7312, "Continue", () => Show(Page.Main)));
                break;
            case Page.Options:
                (_music, _sound, _remake) = (Music.Volume, GameAudio.SoundVolume, GraphicsSettings.Remake);
                _buttons.Add(new(Box(85, 377, 291, 423), 7302, "OK", () =>
                {
                    Music.Volume = _music;
                    GameAudio.SoundVolume = _sound;
                    var lookChanged = GraphicsSettings.Remake != _remake;
                    GraphicsSettings.Remake = _remake;
                    Show(Page.Main);
                    if (lookChanged)
                        _note = "The new look shows from the next match";
                }));
                _buttons.Add(new(Box(337, 377, 543, 423), 7311, "Cancel", () => Show(Page.Main)));
                break;
            case Page.Save:
                ReadSaves();
                _picked = -1;
                _buttons.Add(new(Box(387, 111, 578, 154), 7302, "OK", () =>
                {
                    var name = _picked >= 0 ? System.IO.Path.GetFileNameWithoutExtension(_saves[_picked])
                        : $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {MatchRequest.Current.Scenario}";
                    var why = game.SaveGame(name);
                    Show(Page.Main);
                    _note = why is null ? $"Saved: {name}" : $"Not saved: {why}";
                }));
                _buttons.Add(new(Box(387, 215, 578, 258), 7314, "Delete", DeletePicked));
                _buttons.Add(new(Box(387, 312, 578, 356), 7311, "Cancel", () => Show(Page.Main)));
                break;
            case Page.Statistics:
                _statisticsSince = Time.GetTicksMsec() / 1000.0;
                _buttons.Add(new(Box(425, 406, 576, 439), 7302, "OK", game.ExitToMenus));
                break;
            case Page.Load:
                ReadSaves();
                _picked = _saves.Count > 0 ? 0 : -1;
                _buttons.Add(new(Box(364, 127, 555, 170), 7302, "OK", () =>
                {
                    if (_picked >= 0 && game.LoadGame(_saves[_picked]) is { } why)
                        _note = $"Cannot load: {why}";
                }));
                _buttons.Add(new(Box(364, 230, 555, 274), 7314, "Delete", DeletePicked));
                _buttons.Add(new(Box(364, 328, 555, 371), 7311, "Cancel", () => Show(Page.Main)));
                break;
        }
        QueueRedraw();
    }

    private void ReadSaves() =>
        _saves = Directory.Exists(Game.SaveFolder)
            ? Directory.GetFiles(Game.SaveFolder, "*" + Game.SaveExtension).OrderByDescending(File.GetLastWriteTimeUtc).ToList()
            : [];

    private void DeletePicked()
    {
        if (_picked < 0 || _picked >= _saves.Count)
            return;
        File.Delete(_saves[_picked]);
        ReadSaves();
        _picked = Math.Min(_picked, _saves.Count - 1);
        if (_page == Page.Save)
            _picked = -1;
    }

    private string Backdrop => _page switch
    {
        Page.Main => "menu/se.bmp",
        Page.Briefing => "menu/sk.bmp",
        Page.Hotkeys => "menu/so.bmp",
        Page.Options => "menu/sf.bmp",
        Page.Save => "menu/sh.bmp",
        Page.Statistics => "menu/sq.bmp",
        _ => "menu/sg.bmp",
    };

    // Where the lists lie on the save and load screens (SAVE_LB_..., LOAD_LB_...).
    private Rect2 ListBox => _page == Page.Save ? Box(98, 72, 312, 412) : Box(75, 88, 291, 429);

    private Rect2 Screen(Rect2 box) => new(_origin + box.Position * _scale, box.Size * _scale);

    private Vector2 Local(Vector2 screen) => (screen - _origin) / _scale;

    // ---- drawing --------------------------------------------------------------------------------

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        _scale = Mathf.Max(1, Mathf.Floor(Math.Min(size.X / 640f, size.Y / 480f) * 4) / 4);
        _origin = ((size - new Vector2(640, 480) * _scale) / 2).Floor();
        DrawRect(new Rect2(Vector2.Zero, size), Colors.Black);
        if (ClassicGui.Bitmap(Backdrop) is { } backdrop)
            DrawTextureRect(backdrop, new Rect2(_origin, new Vector2(640, 480) * _scale), false);

        for (var i = 0; i < _buttons.Count; i++)
            DrawButton(_buttons[i], i);

        switch (_page)
        {
            case Page.Briefing:
                DrawParagraphs(Box(23, 21, 305, 336), game.BriefingText() ?? "No briefing for this scenario.");
                break;
            case Page.Hotkeys:
                DrawHotkeys();
                break;
            case Page.Options:
                DrawOptions();
                break;
            case Page.Save or Page.Load:
                DrawSaves();
                break;
            case Page.Statistics:
                DrawStatistics();
                break;
        }
        if (_note is not null && ClassicFont.Get("menu/smallfnt.bmp") is { } note)
            note.Draw(this, _origin + new Vector2(90, 452) * _scale, _note, _scale);
    }

    /// <summary>
    /// A menu button (MachGuiMenuButton::doDisplay): under the mouse the middle of glow.bmp behind it and its name
    /// in the dark font, underlined while pressed; otherwise its name light, or yellow with the keyboard on it;
    /// a disabled one under disable.bmp.
    /// </summary>
    private void DrawButton(MenuButton button, int index)
    {
        var box = Screen(button.Box);
        var lit = button.Enabled && (index == _hover || index == _pressed);
        if (lit && ClassicGui.Bitmap("menu/glow.bmp") is { } glow)
        {
            var from = new Vector2((glow.GetWidth() - button.Box.Size.X) / 2, (glow.GetHeight() - button.Box.Size.Y) / 2).Floor();
            DrawTextureRectRegion(glow, box, new Rect2(from, button.Box.Size));
        }
        var text = Text(button.StringId, button.Fallback);
        var font = ClassicFont.Get(lit ? "menu/largdfnt.bmp" : index == _focus ? "menu/largyfnt.bmp" : "menu/largefnt.bmp");
        if (font is not null)
        {
            var at = button.Box.Position + new Vector2((button.Box.Size.X - font.Measure(text)) / 2, (button.Box.Size.Y - font.Height) / 2).Floor();
            font.Draw(this, _origin + at * _scale, text, _scale);
            if (index == _pressed)
                DrawRect(Screen(new Rect2(at + new Vector2(0, font.Height + 1), new Vector2(font.Measure(text), 1))), Colors.Black);
        }
        else
        {
            DrawRect(box, lit ? new Color(0.9f, 0.6f, 0.2f, 0.5f) : new Color(0, 0, 0, 0.4f));
            DrawString(ThemeDB.FallbackFont, box.Position + new Vector2(8, box.Size.Y / 2 + 6), text, HorizontalAlignment.Left, -1, (int)(14 * _scale));
        }
        if (!button.Enabled && ClassicGui.Bitmap("menu/disable.bmp") is { } disabled)
            DrawTextureRectRegion(disabled, box, new Rect2(Vector2.Zero, button.Box.Size));
    }

    /// <summary>Text in the small menu font, broken into lines to fit the box (MachGuiScrollableText).</summary>
    private void DrawParagraphs(Rect2 box, string text)
    {
        if (ClassicFont.Get("menu/smallfnt.bmp") is not { } font)
            return;
        var y = box.Position.Y;
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            foreach (var line in paragraph.Length == 0 ? [""] : font.Chop(paragraph, (int)box.Size.X))
            {
                if (y + font.Height > box.End.Y)
                    return;
                font.Draw(this, _origin + new Vector2(box.Position.X, y) * _scale, line, _scale);
                y += font.Height + 2;
            }
        }
    }

    // The remake's keys, in the hot keys screen's two lists (ctxhotky: actions in the small font, keys in the white one).
    private static readonly (string Action, string Keys)[] FirstPersonKeys =
    [
        ("Drive", "W A S D, arrows"), ("Look and aim", "Mouse"), ("Fire", "Left mouse button"),
        ("Pick a weapon", "1 to 4"), ("Leave first person", "F, Esc"),
    ];

    private static readonly (string Action, string Keys)[] GeneralKeys =
    [
        ("Scroll", "W A S D, arrows, edges"), ("Turn view", "Q E"), ("Zoom", "Mouse wheel"),
        ("Zenith view", "2 on number pad"), ("Ground view", "0 on number pad"), ("1st person", "F"),
        ("Select", "Left click, drag"), ("Order", "Right click"), ("Stop", "Ctrl S"),
        ("Initiative", "Tab"), ("Assembly point", "B"), ("Create squad 0-9", "Ctrl 0-9"),
        ("Select squad 0-9", "0-9"), ("Turn building", "Space, R"), ("Pause", "P"),
        ("Hide panel", "Alt Left"), ("Show panel", "Alt Right"), ("Menus", "Esc, F10"),
    ];

    private void DrawHotkeys()
    {
        if (ClassicFont.Get("menu/largefnt.bmp") is not { } large || ClassicFont.Get("menu/smallfnt.bmp") is not { } small
            || ClassicFont.Get("menu/smalwfnt.bmp") is not { } white)
            return;
        const int actionX = 128, keyX = 128 + 111 + 2;
        var y = 15f;
        void List(int heading, string fallback, (string Action, string Keys)[] keys)
        {
            large.Draw(this, _origin + new Vector2(actionX, y) * _scale, Text(heading, fallback), _scale);
            y += large.Height + 2;
            foreach (var (action, key) in keys)
            {
                small.Draw(this, _origin + new Vector2(actionX, y) * _scale, action, _scale, 111);
                white.Draw(this, _origin + new Vector2(keyX, y) * _scale, key, _scale, 131);
                y += small.Height + 2;
            }
            y += 6;
        }
        List(7064, "First Person Control", FirstPersonKeys);
        List(7065, "General Control", GeneralKeys);
    }

    // The options page's rows: a slider for each volume and the look.
    private Rect2 MusicSlider => Box(260, 120, 500, 132);
    private Rect2 SoundSlider => Box(260, 160, 500, 172);
    private Rect2 LookToggle => Box(260, 196, 500, 210);

    private void DrawOptions()
    {
        if (ClassicFont.Get("menu/largefnt.bmp") is not { } large || ClassicFont.Get("menu/smallfnt.bmp") is not { } small)
            return;
        large.Draw(this, _origin + new Vector2(90, 70) * _scale, Text(7211, "Options"), _scale);
        void Slider(string name, Rect2 box, float value)
        {
            small.Draw(this, _origin + new Vector2(90, box.Position.Y + 2) * _scale, name, _scale);
            DrawRect(Screen(box), new Color(0, 0, 0, 0.6f));
            DrawRect(Screen(new Rect2(box.Position, new Vector2(box.Size.X * value, box.Size.Y))), new Color(0.85f, 0.55f, 0.15f, 0.9f));
            DrawRect(Screen(box), new Color(0.8f, 0.8f, 0.8f), filled: false, width: _scale);
        }
        Slider("Music volume", MusicSlider, _music);
        Slider("Sound volume", SoundSlider, _sound);
        small.Draw(this, _origin + new Vector2(90, LookToggle.Position.Y + 2) * _scale, "Look", _scale);
        small.Draw(this, _origin + new Vector2(LookToggle.Position.X, LookToggle.Position.Y + 2) * _scale,
            _remake ? "Remake (click for the classic look)" : "Classic (click for the remake look)", _scale);
    }

    private void DrawSaves()
    {
        if (ClassicFont.Get("menu/smallfnt.bmp") is not { } font || ClassicFont.Get("menu/largefnt.bmp") is not { } large)
            return;
        var box = ListBox;
        large.Draw(this, _origin + (box.Position - new Vector2(0, large.Height + 6)) * _scale,
            Text(_page == Page.Save ? 7205 : 7206, _page == Page.Save ? "Save Game" : "Load Game"), _scale);
        var rows = (_page == Page.Save ? ["New save game"] : new List<string>()).Concat(_saves.Select(System.IO.Path.GetFileNameWithoutExtension)).ToList();
        var y = box.Position.Y;
        for (var i = 0; i < rows.Count && y + font.Height <= box.End.Y; i++)
        {
            var index = _page == Page.Save ? i - 1 : i;
            if (index == _picked)
                DrawRect(Screen(new Rect2(box.Position.X - 2, y - 1, box.Size.X + 4, font.Height + 2)), new Color(0.9f, 0.6f, 0.2f, 0.45f));
            font.Draw(this, _origin + new Vector2(box.Position.X, y) * _scale, rows[i]!, _scale, (int)box.Size.X);
            y += font.Height + 3;
        }
    }

    // ---- statistics (MachGuiCtxStatistics) -----------------------------------------------------

    private double _statisticsSince;
    private bool _statisticsSounded;

    private static readonly Race[] BarRaces = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    private static string BarBitmap(Race race) => race switch
    {
        Race.Red => "menu/redbar.bmp",
        Race.Blue => "menu/bluebar.bmp",
        Race.Green => "menu/greenbar.bmp",
        _ => "menu/yellobar.bmp",
    };

    /// <summary>
    /// The statistics after a match: each player's score, then blocks of four bars, red, blue, green and yellow,
    /// that grow to their figures over two seconds with stats.wav: machines and buildings destroyed and lost and
    /// BMUs mined; machines, fighting machines and buildings built, items researched and research points; and who
    /// destroyed whose machines and buildings.
    /// </summary>
    private void DrawStatistics()
    {
        if (ClassicFont.Get("menu/largefnt.bmp") is not { } large || ClassicFont.Get("menu/smallfnt.bmp") is not { } small
            || ClassicFont.Get("menu/smalwfnt.bmp") is not { } white)
            return;
        var world = game.World;
        var now = Time.GetTicksMsec() / 1000.0;
        var growth = (float)Math.Clamp((now - _statisticsSince - 0.5) / 2, 0, 1);
        if (growth > 0 && !_statisticsSounded)
        {
            _statisticsSounded = true;
            game.Audio.PlayGui("gui/sounds/stats.wav");
        }
        Score? ScoreOf(Race race) => world.Races.TryGetValue(race, out var state) ? state.Score : null;
        string NameOf(Race race) => !world.Races.ContainsKey(race) ? Text(7097, "N/A")
            : race == Game.Player ? Text(7095, "Player")
            : MatchRequest.Current.Network?.Players.FirstOrDefault(p => p.Slot == race && !p.Ai)?.Name ?? Text(7096, "Computer");

        large.Draw(this, _origin + new Vector2(53, 31) * _scale, Text(7093, "Statistics"), _scale);
        var y = 50f;
        foreach (var race in BarRaces.Where(world.Races.ContainsKey))
        {
            small.Draw(this, _origin + new Vector2(53, y) * _scale, NameOf(race), _scale, 90);
            white.Draw(this, _origin + new Vector2(53, y + small.Height + 1) * _scale, (ScoreOf(race)?.Gross ?? 0).ToString(), _scale);
            y += 2 * small.Height + 6;
        }

        white.Draw(this, _origin + new Vector2(196, 41) * _scale, Text(7091, "General"), _scale);
        white.Draw(this, _origin + new Vector2(196, 132) * _scale, Text(7094, "Production"), _scale);
        white.Draw(this, _origin + new Vector2(196, 232) * _scale, Text(7092, "Kills"), _scale);
        const int width = 76;
        // A block (MachGuiGeneralStatistics): its title centred in lines over the bars, and for the kill tables the
        // player whose losses it counts at the foot of the title.
        void Block(float x, float top, int title, string fallback, Func<Race, float> value, Func<Race, string>? bar = null, string? whose = null)
        {
            var lines = Text(title, fallback).Replace("\r", "").Split('\n').SelectMany(l => small.Chop(l.Trim(), 75)).ToList();
            for (var i = 0; i < lines.Count; i++)
                small.Draw(this, _origin + new Vector2(x + (77 - small.Measure(lines[i])) / 2f, top + 2 + i * (small.Height + 2)) * _scale, lines[i], _scale);
            if (whose is not null)
                white.Draw(this, _origin + new Vector2(x + (77 - white.Measure(whose)) / 2f, top + 33 - white.Height - 2) * _scale, whose, _scale);
            var at = new Vector2(x + 1, top + 35);
            if (ClassicGui.Bitmap("menu/barback.bmp") is { } back)
                DrawTextureRect(back, new Rect2(_origin + at * _scale, new Vector2(back.GetWidth(), back.GetHeight()) * _scale), false);
            var values = BarRaces.Select(value).ToList();
            var range = Math.Max(1, values.Max());
            for (var i = 0; i < BarRaces.Length; i++)
            {
                if (ClassicGui.Bitmap((bar ?? BarBitmap)(BarRaces[i])) is not { } texture)
                    continue;
                var share = values[i] / range * growth;
                var length = texture.GetWidth() * share;
                var row = at + new Vector2(2, 3 + i * (texture.GetHeight() + 2));
                DrawTextureRectRegion(texture, new Rect2(_origin + row * _scale, new Vector2(length, texture.GetHeight()) * _scale),
                    new Rect2(0, 0, length, texture.GetHeight()));
                if (growth >= 1)
                    small.Draw(this, _origin + (row + new Vector2(texture.GetWidth() + 2, 0)) * _scale, ((int)values[i]).ToString(), _scale);
            }
        }
        float Sum(Dictionary<Race, int>? counts) => counts?.Values.Sum() ?? 0;
        Block(196, 50, 7077, "Machine kills", r => Sum(ScoreOf(r)?.MachinesDestroyed));
        Block(196 + width, 50, 7078, "Construction kills", r => Sum(ScoreOf(r)?.ConstructionsDestroyed));
        Block(196 + width * 2, 50, 7079, "Machine losses", r => ScoreOf(r)?.MachinesLost ?? 0);
        Block(196 + width * 3, 50, 7080, "Construction losses", r => ScoreOf(r)?.ConstructionsLost ?? 0);
        Block(196 + width * 4, 50, 7081, "BMUs mined", r => ScoreOf(r)?.BmusMined ?? 0);
        Block(196, 141, 7082, "Machines built", r => ScoreOf(r)?.MachinesBuilt ?? 0);
        Block(196 + width, 141, 7083, "Military machines built", r => ScoreOf(r)?.MilitaryMachinesBuilt ?? 0);
        Block(196 + width * 2, 141, 7084, "Constructions built", r => ScoreOf(r)?.ConstructionsBuilt ?? 0);
        Block(196 + width * 3, 141, 7085, "Items researched", r => ScoreOf(r)?.ItemsResearched ?? 0);
        Block(196 + width * 4, 141, 7086, "Research points", r => ScoreOf(r)?.ResearchUnits ?? 0);
        foreach (var (victim, i) in BarRaces.Select((r, i) => (r, i)))
        {
            Block(196 + width * (i + 1), 241, 7088, $"{victim} machines", r => ScoreOf(r)?.MachinesDestroyed.GetValueOrDefault(victim) ?? 0, _ => BarBitmap(victim), NameOf(victim));
            Block(196 + width * (i + 1), 322, 7089, $"{victim} buildings", r => ScoreOf(r)?.ConstructionsDestroyed.GetValueOrDefault(victim) ?? 0, _ => BarBitmap(victim), NameOf(victim));
        }
        // The rows of the kill blocks are the players, named down the left (MachGuiPlayerNameList).
        foreach (var top in (ReadOnlySpan<float>)[241, 322])
        {
            small.Draw(this, _origin + new Vector2(196, top + 4) * _scale, Text(7087, "Players"), _scale, 75);
            for (var i = 0; i < BarRaces.Length; i++)
                small.Draw(this, _origin + new Vector2(197, top + 38 + i * 9) * _scale, NameOf(BarRaces[i]), _scale, 74);
        }
    }

    // ---- input ----------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion:
            {
                var over = _buttons.FindIndex(b => b.Enabled && b.Box.HasPoint(Local(motion.Position)));
                if (over != _hover && over >= 0)
                    game.Audio.PlayGui("gui/sounds/highligh.wav");
                _hover = over;
                if (_pressed >= 0 && _pressed != over)
                    _pressed = -1;
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
            {
                var at = Local(click.Position);
                var index = _buttons.FindIndex(b => b.Box.HasPoint(at));
                if (click.Pressed)
                {
                    if (index >= 0 && !_buttons[index].Enabled)
                        game.Audio.PlayGui("gui/sounds/clickbad.wav");
                    else if (index >= 0)
                        _pressed = index;
                    else
                        ClickPage(at);
                }
                else if (_pressed >= 0 && _pressed == index)
                {
                    _pressed = -1;
                    Press(index);
                }
                AcceptEvent();
                break;
            }
        }
    }

    /// <summary>Clicks on the page itself: a slider, the look, a saved game in the list.</summary>
    private void ClickPage(Vector2 at)
    {
        if (_page == Page.Options)
        {
            if (MusicSlider.Grow(4).HasPoint(at))
                _music = Mathf.Clamp((at.X - MusicSlider.Position.X) / MusicSlider.Size.X, 0, 1);
            else if (SoundSlider.Grow(4).HasPoint(at))
                _sound = Mathf.Clamp((at.X - SoundSlider.Position.X) / SoundSlider.Size.X, 0, 1);
            else if (LookToggle.HasPoint(at))
                _remake = !_remake;
            else
                return;
            game.Audio.PlayGui("gui/sounds/listclik.wav");
        }
        else if (_page is Page.Save or Page.Load && ListBox.HasPoint(at) && ClassicFont.Get("menu/smallfnt.bmp") is { } font)
        {
            var row = (int)((at.Y - ListBox.Position.Y) / (font.Height + 3));
            var index = _page == Page.Save ? row - 1 : row;
            if (index >= -1 && index < _saves.Count && (index >= 0 || _page == Page.Save))
            {
                _picked = index;
                game.Audio.PlayGui("gui/sounds/listclik.wav");
            }
        }
    }

    private void Press(int index)
    {
        var button = _buttons[index];
        if (!button.Enabled)
        {
            game.Audio.PlayGui("gui/sounds/clickbad.wav");
            return;
        }
        game.Audio.PlayGui("gui/sounds/click.wav");
        button.Act();
    }

    /// <summary>The keys reach the menus first, and nothing behind them.</summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key)
            return;
        GetViewport().SetInputAsHandled();
        switch (key.Keycode)
        {
            case Key.Escape:
                if (_page == Page.Main)
                    game.CloseMenu();
                else
                    Show(Page.Main);
                break;
            case Key.Enter or Key.KpEnter or Key.Space when _focus >= 0:
                Press(_focus);
                break;
            case Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab:
                MoveFocus(key.Keycode);
                break;
        }
    }

    /// <summary>The arrow keys move the keyboard's focus to the next button that way (MachGuiFocusCapableControl).</summary>
    private void MoveFocus(Key key)
    {
        if (_buttons.Count == 0)
            return;
        var from = _focus >= 0 ? _buttons[_focus].Box.GetCenter() : new Vector2(0, 0);
        var direction = key switch { Key.Up => Vector2.Up, Key.Down or Key.Tab => Vector2.Down, Key.Left => Vector2.Left, _ => Vector2.Right };
        var next = _focus < 0 ? 0 : Enumerable.Range(0, _buttons.Count)
            .Where(i => i != _focus && (_buttons[i].Box.GetCenter() - from).Dot(direction) > 0)
            .OrderBy(i => (_buttons[i].Box.GetCenter() - from).Length() + Math.Abs((_buttons[i].Box.GetCenter() - from).Cross(direction)) * 2)
            .DefaultIfEmpty(_focus).First();
        if (next != _focus)
            game.Audio.PlayGui("gui/sounds/navkey.wav");
        _focus = next;
    }
}
