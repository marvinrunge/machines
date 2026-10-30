using Godot;

namespace Machines.Game;

/// <summary>
/// The stage the menus play on, before a match (<see cref="MenuScreens"/>) and over one (<see cref="InGameMenu"/>): the
/// circuit board filling the window (<see cref="MenuBoard"/>), one page on it at a time and, over the page, a message
/// when there is one. It is laid out for a window <see cref="DesignHeight"/> menu pixels high, as wide as the window's
/// shape makes that, and scaled to the real window and the player's interface size, so it fills any window and stays
/// sharp: the letters are drawn at the stage's scale, the art rendered at twice the menus' scale. Esc closes the
/// message, or goes back from the page.
/// </summary>
public partial class MenuStage : Control
{
    public const float DesignHeight = 1080;

    private Theme _theme = null!;
    private Control? _page;
    private MenuMessage? _message;
    private Control? _focusBefore;

    public Control? Page => _page;

    public override void _Ready()
    {
        Theme = _theme = MenuTheme.Build();
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new MenuBoard());
        if (GetParent() is Control parent)
            parent.Resized += Fit;
        else
            GetViewport().SizeChanged += Fit;
        Fit();
    }

    public override void _ExitTree()
    {
        if (GetParent() is not Control)
            GetViewport().SizeChanged -= Fit;
    }

    /// <summary>Scales the stage so the menus' design height fills the window's height.</summary>
    public void Fit()
    {
        var size = GetParentAreaSize();
        if (size.Y <= 0)
            return;
        var scale = size.Y / DesignHeight * MenuTheme.UiScale;
        Position = Vector2.Zero;
        Scale = new Vector2(scale, scale);
        Size = size / scale;
        MenuTheme.FitPopups(_theme, scale);
        MenuTheme.FitFont(scale);
    }

    /// <summary>Puts <paramref name="page"/> on the stage in place of the one there.</summary>
    public void Show(Control page)
    {
        CloseMessage();
        _page?.QueueFree();
        _page = page;
        AddChild(page);
    }

    /// <summary>
    /// A message over the page with buttons to answer it (OK alone when none are given); each closes the message
    /// before it does what it says, and Esc presses the last.
    /// </summary>
    public void Message(string text, params (string Text, Action? Act)[] buttons)
    {
        CloseMessage();
        _focusBefore = GetViewport().GuiGetFocusOwner();
        if (_page is not null)
        {
            _page.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
            _page.MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled;
        }
        _message = new MenuMessage(text, buttons.Length > 0 ? buttons : [("OK", null)], CloseMessage);
        AddChild(_message);
    }

    private void CloseMessage()
    {
        if (_message is null)
            return;
        _message.QueueFree();
        _message = null;
        if (_page is not null && IsInstanceValid(_page))
        {
            _page.FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Inherited;
            _page.MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Inherited;
        }
        if (_focusBefore is not null && IsInstanceValid(_focusBefore) && _focusBefore.IsVisibleInTree())
            _focusBefore.CallDeferred(Control.MethodName.GrabFocus);
        _focusBefore = null;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed("ui_cancel"))
            return;
        GetViewport().SetInputAsHandled();
        if (_message is not null)
            _message.Cancel();
        else
            (_page as MenuPage)?.Back();
    }
}

/// <summary>
/// A message over the page, as the original's message box was (MachGuiMessageBox): the page behind it dims and a chip
/// with the text and its buttons switches on in the middle, the keyboard on its last button.
/// </summary>
public partial class MenuMessage(string text, (string Text, Action? Act)[] buttons, Action close) : Control
{
    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0.02f, 0, 0.62f), MouseFilter = MouseFilterEnum.Ignore };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(shade);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(centre);

        var chip = new ChipPanel { Padding = 36 };
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) };
        column.AddThemeConstantOverride("separation", 34);
        var label = MenuTheme.Paragraph(text, 26, MenuTheme.Text);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(label);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 28);
        foreach (var (name, act) in buttons)
        {
            var button = new ChipButton(name.ToUpperInvariant()) { CustomMinimumSize = new Vector2(220, 74), FontSize = 26 };
            button.Pressed += () =>
            {
                close();
                act?.Invoke();
            };
            row.AddChild(button);
        }
        column.AddChild(row);
        chip.AddChild(column);
        centre.AddChild(chip);
        chip.SwitchOn(0);
        // The keyboard starts on the last button, the one that says no: Enter alone changes nothing.
        row.GetChild<Control>(row.GetChildCount() - 1).CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>Esc: the last button, which says no or closes.</summary>
    public void Cancel()
    {
        close();
        buttons[^1].Act?.Invoke();
    }
}

/// <summary>
/// A page of the menus: it fills the stage inside the board's margin, its chips switch on one after the other as it
/// comes up, and Esc does what its Back button does.
/// </summary>
public abstract partial class MenuPage(Action? back) : MarginContainer
{
    public const float Gap = 32;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var (side, value) in new[] { ("left", 64), ("right", 64), ("top", 52), ("bottom", 52) })
            AddThemeConstantOverride($"margin_{side}", value);
        Build();
    }

    protected abstract void Build();

    public virtual void Back() => back?.Invoke();

    protected MenuStage Stage => GetParent<MenuStage>();

    /// <summary>A menu button: a chip with its name in capitals.</summary>
    public static ChipButton Button(string text, Action pressed, float height = 76, int fontSize = 28)
    {
        var button = new ChipButton(text.ToUpperInvariant()) { CustomMinimumSize = new Vector2(0, height), FontSize = fontSize };
        button.Pressed += pressed;
        return button;
    }

    /// <summary>The page's title on a chip of its own, as the original's screens had theirs.</summary>
    public static ChipPanel Heading(string text)
    {
        var chip = new ChipPanel { Padding = 14, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        var label = MenuTheme.Label(text.ToUpperInvariant(), 40, MenuTheme.Bright);
        label.CustomMinimumSize = new Vector2(300, 0);
        chip.AddChild(label);
        return chip;
    }

    /// <summary>A labelled setting: its name, then the control that sets it.</summary>
    public static HBoxContainer Setting(string name, Control control, float nameWidth = 220)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        var label = MenuTheme.Label(name, 22, MenuTheme.Text);
        label.CustomMinimumSize = new Vector2(nameWidth, 0);
        label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(label);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        control.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(control);
        return row;
    }

    /// <summary>A box of controls, one under the other, <paramref name="separation"/> apart.</summary>
    public static VBoxContainer Column(float separation, params Control[] children)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", (int)separation);
        foreach (var child in children)
            column.AddChild(child);
        return column;
    }

    public static HBoxContainer Line(float separation, params Control[] children)
    {
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", (int)separation);
        foreach (var child in children)
            line.AddChild(child);
        return line;
    }

    public static Control Spring(bool across = true) =>
        new() { SizeFlagsHorizontal = across ? SizeFlags.ExpandFill : SizeFlags.Fill, SizeFlagsVertical = across ? SizeFlags.Fill : SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };

    /// <summary>The page's chips switch on one after the other, as the original's screens did when they came up.</summary>
    public void SwitchOn()
    {
        var delay = 0.04;
        foreach (var chip in FindChildren("*", "", true, false).OfType<ChipPanel>())
        {
            chip.SwitchOn(delay);
            delay += 0.07;
        }
    }

    /// <summary>The keyboard starts on <paramref name="control"/>.</summary>
    public static void Focus(Control control) => control.CallDeferred(Control.MethodName.GrabFocus);
}

/// <summary>A page built by a function, for the simple ones; its chips switch on once it is built.</summary>
public partial class BuiltPage(Action? back, Action<BuiltPage> build) : MenuPage(back)
{
    protected override void Build()
    {
        build(this);
        SwitchOn();
    }
}
