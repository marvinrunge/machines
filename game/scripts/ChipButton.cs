using Godot;

namespace Machines.Game;

/// <summary>
/// A button of the menus, as the original's were (MachGuiMenuButton): a chip with its name on the screen, which lights
/// up green under the mouse or the keyboard's focus and goes dark again after. It draws itself entirely, chip and text,
/// so it scales with the menus.
/// </summary>
public partial class ChipButton : BaseButton
{
    private string _text = "";
    private float _light;

    public ChipButton(string text = "")
    {
        _text = text;
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            UpdateMinimumSize();
            QueueRedraw();
        }
    }

    public int FontSize { get; set; } = 30;

    /// <summary>Menu pixels per pixel of the chip texture: buttons have finer pins than panels.</summary>
    public float ArtScale { get; set; } = 0.42f;

    public override void _Process(double delta)
    {
        var target = !Disabled && (IsHovered() || HasFocus()) ? 1f : 0f;
        if (_light == target)
            return;
        _light = Mathf.MoveToward(_light, target, (float)delta * (target > _light ? 10 : 3.5f));
        QueueRedraw();
    }

    /// <summary>How wide the text needs the screen to be.</summary>
    protected virtual float TextWidth(Font font) => font.GetStringSize(Lang.T(_text), HorizontalAlignment.Left, -1, FontSize).X;

    public override Vector2 _GetMinimumSize()
    {
        var frame = ChipArt.FrameWidth(ArtScale);
        return new Vector2(TextWidth(GetThemeDefaultFont()) + 2 * (frame + 30), FontSize * 1.5f + 2 * frame + 16);
    }

    public override void _Draw()
    {
        ChipArt.Draw(this, new Rect2(Vector2.Zero, Size), ArtScale, _light, dim: Disabled);
        var font = GetThemeDefaultFont();
        var sink = GetDrawMode() == DrawMode.Pressed ? 2f : 0f;
        var colour = Disabled ? MenuTheme.Dim : MenuTheme.Text.Lerp(MenuTheme.OnLight, _light);
        var baseline = (Size.Y + font.GetAscent(FontSize) - font.GetDescent(FontSize)) / 2 + sink;
        DrawString(font, new Vector2(0, baseline), Lang.T(_text), HorizontalAlignment.Center, Size.X, FontSize, colour);
        DrawMore(colour, sink);
    }

    /// <summary>What a kind of button draws on its screen besides its name, in the name's colour.</summary>
    protected virtual void DrawMore(Color colour, float sink)
    {
    }
}

/// <summary>
/// A setting picked from a few choices, as the original's drop-down lists were: a chip showing the choice between
/// two arrows. A click on its left half steps back through the choices, on its right half on; with the keyboard on
/// it Left and Right step, Enter steps on.
/// </summary>
public partial class ChipChoice : ChipButton
{
    private const float ArrowRoom = 34;

    private string[] _items = [];
    private int _selected;
    private float? _clickedAt;

    public event Action<int>? ItemSelected;

    public ChipChoice(IEnumerable<string> items, int selected = 0)
    {
        FontSize = 22;
        SetItems(items, selected);
    }

    public IReadOnlyList<string> Items => _items;

    public int Selected
    {
        get => _selected;
        set
        {
            _selected = Math.Clamp(value, 0, Math.Max(0, _items.Length - 1));
            Text = _items.Length > 0 ? _items[_selected] : "";
        }
    }

    public void SetItems(IEnumerable<string> items, int selected)
    {
        _items = items.ToArray();
        Selected = selected;
        UpdateMinimumSize();
    }

    private void Step(int by)
    {
        if (_items.Length < 2)
            return;
        Selected = (_selected + by + _items.Length) % _items.Length;
        ItemSelected?.Invoke(_selected);
    }

    public override void _GuiInput(InputEvent @event)
    {
        _clickedAt = @event is InputEventMouseButton { ButtonIndex: MouseButton.Left } click ? click.Position.X
            : @event is InputEventMouse ? _clickedAt : null;
        if (Disabled)
            return;
        var by = @event.IsActionPressed("ui_left", true) ? -1 : @event.IsActionPressed("ui_right", true) ? 1 : 0;
        if (by == 0)
            return;
        Step(by);
        MenuSounds.Click();
        AcceptEvent();
    }

    public override void _Pressed() => Step(_clickedAt is { } x && x < Size.X / 2 ? -1 : 1);

    protected override float TextWidth(Font font) =>
        _items.Select(item => font.GetStringSize(Lang.T(item), HorizontalAlignment.Left, -1, FontSize).X).DefaultIfEmpty(0).Max() + 2 * ArrowRoom;

    // The arrows stand in the room a button keeps either side of its name.
    public override Vector2 _GetMinimumSize() => base._GetMinimumSize() - new Vector2(2 * 26, 0);

    protected override void DrawMore(Color colour, float sink)
    {
        if (_items.Length < 2)
            return;
        var middle = Size.Y / 2 + sink;
        var inset = ChipArt.FrameWidth(ArtScale) + 18;
        const float half = 8;
        DrawColoredPolygon([new(inset, middle), new(inset + half * 1.3f, middle - half), new(inset + half * 1.3f, middle + half)], colour);
        var right = Size.X - inset;
        DrawColoredPolygon([new(right, middle), new(right - half * 1.3f, middle - half), new(right - half * 1.3f, middle + half)], colour);
    }
}
