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
    private Control _dragBox = null!;
    /// <summary>The first-person crosshair.</summary>
    public Crosshair Crosshair { get; private set; } = null!;

    /// <summary>The first-person weapon panels.</summary>
    public WeaponDisplay Weapons { get; private set; } = null!;

    /// <summary>The first-person radar, with the driven machine's own health and armour.</summary>
    public Radar Radar { get; private set; } = null!;

    public Hud(Game game, Texture2D? map)
    {
        _game = game;
        _map = map;
        Name = "Hud";
    }

    public Rect2? DragRect { get; set; }

    /// <summary>Driving in first person: a crosshair, and no control panel over the view.</summary>
    public bool FirstPerson
    {
        set
        {
            Crosshair.Visible = value;
            if (value)
            {
                Crosshair.Start();
                Weapons.Start();
                Radar.Start();
            }
            Weapons.Visible = value;
            Radar.Visible = value;
            _panel.Visible = !value;
        }
    }

    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _banner = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _banner.AddThemeFontSizeOverride("font_size", 64);
        _banner.SetAnchorsPreset(Control.LayoutPreset.Center);
        root.AddChild(_banner);

        _dragBox = new DragBox(this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _dragBox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_dragBox);

        Crosshair = Crosshair.Create(_game.Audio);
        Crosshair.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(Crosshair);
        Weapons = WeaponDisplay.Create();
        Weapons.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(Weapons);
        Radar = Radar.Create(_game, _game.Audio);
        Radar.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(Radar);

        _figures = new Label { Modulate = new Color(1, 1, 1, 0.85f) };
        root.AddChild(_figures);
        _messageLabel = new Label();
        root.AddChild(_messageLabel);

        _panel = new ClassicPanel(_game, _map) { Name = "ControlPanel" };
        root.AddChild(_panel);
    }

    public void Message(string text)
    {
        _messages.Add((text, Time.GetTicksMsec() / 1000.0 + 5));
        if (_messages.Count > 5)
            _messages.RemoveAt(0);
    }

    /// <summary>Shows a large message across the screen; null hides it.</summary>
    public void Banner(string? text)
    {
        _banner.Text = text ?? "";
        _banner.Visible = text is not null;
    }

    /// <summary>Called every frame.</summary>
    public void Refresh()
    {
        var world = _game.World;
        var state = world.Races[Game.Player];
        _panel.Refresh();

        // Beside the panel: score, machines and the clock along the top, messages under them.
        var left = (_panel.Visible ? _panel.ScreenWidth : 0) + 12;
        // In first person the weapon panels take the top left; the text goes below them.
        var top = Weapons.Visible ? GetViewport().GetVisibleRect().Size.Y * 0.05f + 90 * Math.Max(1, (int)Math.Round(GetViewport().GetVisibleRect().Size.Y / 480f)) : 0;
        _figures.Position = new Vector2(left, 8 + top);
        _figures.Text = $"Machines {world.Machines.Count(m => m.Race == Game.Player)}     Score {state.Score.Gross}     " +
                        (world.TimeLeft is { } timeLeft ? $"Time left {TimeSpan.FromSeconds(timeLeft):h\\:mm\\:ss}" : $"Time {TimeSpan.FromSeconds(world.Time):h\\:mm\\:ss}") +
                        (_game.Paused ? "     PAUSED" : "");
        _messageLabel.Position = new Vector2(left, 34 + top);
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
