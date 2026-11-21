using Godot;

namespace Machines.Game;

/// <summary>
/// Screen overlay: the control panel down the left (see <see cref="ClassicPanel"/>), the game's figures and
/// messages beside it, banners, the selection rectangle and the first-person crosshair.
/// </summary>
public partial class Hud : CanvasLayer
{
    private readonly Game _game;
    private readonly Texture2D? _map;
    private readonly List<(string Text, double Until)> _messages = [];

    private ClassicPanel _panel = null!;
    private Label _figures = null!;
    private Label _messageLabel = null!;
    private Label _banner = null!;
    private TextureRect _picture = null!;
    private Control _dragBox = null!;
    /// <summary>The first-person crosshair.</summary>
    public Crosshair Crosshair { get; private set; } = null!;

    /// <summary>The first-person weapon panels.</summary>
    public WeaponDisplay Weapons { get; private set; } = null!;

    /// <summary>The first-person radar, with the driven machine's own health and armour.</summary>
    public Radar Radar { get; private set; } = null!;

    /// <summary>The players' messages in a multiplayer match, and the line to type one in.</summary>
    public MatchChat Chat { get; private set; } = null!;

    /// <summary>The screen of a Wired run; null in any other match.</summary>
    public WiredHud? Wired { get; private set; }

    /// <summary>The screen of a story chapter; null in any other match.</summary>
    public StoryHud? Story { get; private set; }

    /// <summary>The story's lines at the foot of the view; null in any other match.</summary>
    public Subtitles? Subtitles { get; private set; }

    public Hud(Game game, Texture2D? map)
    {
        _game = game;
        _map = map;
        Name = "Hud";
    }

    public Rect2? DragRect { get; set; }

    /// <summary>The control panel, with the prompt line beside it.</summary>
    public ClassicPanel Panel => _panel;

    /// <summary>Driving in first person: a crosshair, and no control panel over the view.</summary>
    public bool FirstPerson
    {
        set
        {
            _firstPerson = value;
            Crosshair.Visible = value;
            if (value)
            {
                Crosshair.Start();
                Weapons.Start();
                Radar.Start();
            }
            Weapons.Visible = value;
            // The story has the motion tracker in the radar's place: the radar would show the hunters through the walls.
            Radar.Visible = value && Story is null;
            _panel.Visible = !value;
        }
    }

    // In first person, with the hero down too, when the sight and the guns are gone from it.
    private bool _firstPerson;

    /// <summary>The hero is down with the view still on it: there is nothing to sight, fire or scan with until the next body.</summary>
    public void HeroDown()
    {
        Crosshair.Visible = false;
        Weapons.Visible = false;
        Radar.Driven = null;
    }

    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // Across the view and about its middle, wrapped where it is longer than the view is wide: anchored at the screen's
        // centre alone it began there and ran off the right edge ("Waiting for …", "Reconnecting to the relay…").
        _banner = new Label
        {
            Visible = false, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            GrowVertical = Control.GrowDirection.Both, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _banner.AddThemeFontSizeOverride("font_size", 64);
        _banner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.HcenterWide, Control.LayoutPresetMode.KeepHeight);
        root.AddChild(_banner);

        _picture = new TextureRect { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
        root.AddChild(_picture);

        _dragBox = new DragBox(this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _dragBox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_dragBox);

        Crosshair = Crosshair.Create(_game.Audio);
        Crosshair.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(Crosshair);
        Weapons = WeaponDisplay.Create();
        Weapons.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(Weapons);
        Radar = Radar.Create(_game, _game.Audio, _map);
        Radar.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(Radar);

        // The story borrows Wired's heroes but none of its plates: it has its own screen.
        if (_game.World.Story is not null)
        {
            Story = new StoryHud(_game, this);
            root.AddChild(Story);
            var names = _game.World.Story.Options;
            Subtitles = new Subtitles { IkeName = names.IkeName, BobName = names.BobName };
            root.AddChild(Subtitles);
        }
        else if (_game.World.Wired is not null)
        {
            Wired = new WiredHud(_game, this);
            root.AddChild(Wired);
        }
        _figures = new Label { Modulate = new Color(1, 1, 1, 0.85f), Visible = Wired is null && Story is null };
        root.AddChild(_figures);
        _messageLabel = new Label();
        // Over a moving view, on whatever ground, without the plates' backing.
        if (Wired is not null || Story is not null)
        {
            _messageLabel.AddThemeConstantOverride("outline_size", 6);
            _messageLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        }
        root.AddChild(_messageLabel);

        _panel = new ClassicPanel(_game, _map) { Name = "ControlPanel" };
        root.AddChild(_panel);
        Chat = new MatchChat(_game, _panel);
        root.AddChild(Chat);
        // The banner over all of it: put below a Wired run's plates it reaches the hero's radar at the foot of the view, which
        // drawn over it cut its words.
        root.MoveChild(_banner, -1);
    }

    public void Message(string text)
    {
        _messages.Add((Lang.T(text), Time.GetTicksMsec() / 1000.0 + 5));
        if (_messages.Count > 5)
            _messages.RemoveAt(0);
    }

    /// <summary>Shows a large message across the screen; null hides it.</summary>
    public void Banner(string? text)
    {
        _banner.Text = text ?? "";
        _banner.Visible = text is not null;
        // Over the view beside the control panel, as the pictures are.
        _banner.OffsetLeft = _panel.Visible ? _panel.ScreenWidth : 0;
    }

    /// <summary>
    /// The banner about the middle of the view, but below a Wired run's plates and the watching plate under them where those
    /// hang that far down (a battle of ten or fifteen a side at a large HUD size: three lines about the middle ran over the
    /// capture bars and under "Watching …"), and within the view's foot.
    /// </summary>
    private void PlaceBanner(Vector2 screen)
    {
        if (!_banner.Visible)
            return;
        var high = _banner.Size.Y;
        var middle = screen.Y / 2;
        var top = Math.Max(middle - high / 2, (Wired?.Reach ?? 0) + 8);
        top = Math.Max(0, Math.Min(top, screen.Y - high - 8));
        (_banner.OffsetTop, _banner.OffsetBottom) = (top - middle, top - middle + high);
    }

    /// <summary>
    /// A bitmap of the original's in the middle of the view beside the panel (MachWorldViewWindow::doDisplay): paused,
    /// victory, defeat; null hides it.
    /// </summary>
    public void Picture(string? bitmap)
    {
        var texture = bitmap is null ? null : ClassicGui.Bitmap(bitmap);
        _picture.Visible = texture is not null;
        if (texture is null)
            return;
        _picture.Texture = texture;
        var scale = _panel.PixelScale;
        var view = GetViewport().GetVisibleRect().Size;
        var left = _panel.Visible ? _panel.ScreenWidth : 0;
        _picture.Size = texture.GetSize() * scale;
        _picture.Position = new Vector2(left + (view.X - left - _picture.Size.X) / 2, (view.Y - _picture.Size.Y) / 2);
    }

    /// <summary>Called every frame.</summary>
    public void Refresh()
    {
        var world = _game.World;
        var state = world.Races[Game.Player];
        _panel.Refresh();

        // Beside the panel: score, machines and the clock along the top, messages under them.
        // (the camera tab beside the panel stays at its full width, 30 of the original's pixels wide)
        var left = (_panel.Visible ? Math.Max(_panel.ScreenWidth, (ClassicPanel.PanelWidth + 30) * _panel.PixelScale) : 0) + 12;
        // In first person the weapon panels take the top left; the text goes below them.
        var screen = GetViewport().GetVisibleRect().Size;
        // The panels start below a Wired run's plates where those reach over them (at a large HUD size).
        Weapons.Top = Wired?.PanelsTop ?? Story?.PanelsTop ?? 0;
        var top = Weapons.Visible ? Math.Max(Weapons.Bottom, Math.Max(screen.Y * 0.05f, Weapons.Top) + 90 * HudScale.Of(screen)) : 0;
        if (Story is { } chapter)
        {
            chapter.Left = left - 12;
            chapter.Driving = _firstPerson;
        }
        if (Wired is { } run)
        {
            run.Left = left - 12;
            run.Driving = _firstPerson;
            // The run's plates take the top: the chat and the messages start below the wave's plate and the bosses' bars.
            top = Math.Max(top, run.Hanging - 34);
        }
        PlaceBanner(screen);
        _figures.Position = new Vector2(left, 8 + top);
        // The race's machines, and in a race of several commanders the player's own in brackets after them.
        var machines = world.Machines.Count(m => m.Race == Game.Player).ToString();
        if (world.Players.Shared(Game.Player))
            machines += $" ({world.Machines.Count(m => m.Race == Game.Player && m.Owner == Game.Me)})";
        _figures.Text = Lang.T("Machines {0}     Score {1}     ", machines, state.Score.Gross) +
                        (world.TimeLeft is { } timeLeft
                            ? Lang.T("Time left {0}", TimeSpan.FromSeconds(timeLeft).ToString(@"h\:mm\:ss"))
                            : Lang.T("Time {0}", TimeSpan.FromSeconds(world.Time).ToString(@"h\:mm\:ss"))) +
                        (_game.Paused ? "     " + Lang.T("PAUSED") : "");
        // The players' chat first, the game's messages under it.
        Chat.ListAt = new Vector2(left, 34 + top);
        _messageLabel.Position = new Vector2(left, 34 + top + Chat.ListHeight);
        var now = Time.GetTicksMsec() / 1000.0;
        _messages.RemoveAll(m => m.Until < now);
        _messageLabel.Text = string.Join("\n", _messages.Select(m => m.Text));

        _dragBox.QueueRedraw();
    }

    /// <summary>The selection rectangle while dragging.</summary>
    private partial class DragBox(Hud hud) : Control
    {
        public override void _Draw()
        {
            if (hud.DragRect is { } rect)
                DrawRect(rect, new Color(0.3f, 1, 0.3f), filled: false, width: 1.5f);
        }
    }
}
