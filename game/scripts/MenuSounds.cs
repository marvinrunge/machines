using Godot;

namespace Machines.Game;

/// <summary>
/// The original menus' sounds on the remake's menu controls, from the player's game copy: a button pointed at
/// sounds <c>highligh.wav</c> and clicks with <c>click.wav</c>, or <c>clickbad.wav</c> when it cannot be pressed
/// (MachGuiMenuButton, MachGuiStartupScreens::buttonAction); a list entry pointed at sounds <c>listhigh.wav</c> and
/// picked <c>listclik.wav</c> (MachGuiSingleSelectionListBoxItem). Every control under the menu gets them, those
/// added later too. The sounds play from the scene tree's root, so a click that starts a match is heard out.
/// </summary>
public static class MenuSounds
{
    private static GameAudio? _audio;

    public static void Attach(Control menu, string? gameFolder)
    {
        if (_audio is null || !GodotObject.IsInstanceValid(_audio))
        {
            _audio = GameAudio.Create(gameFolder);
            _audio.Name = "MenuAudio";
            menu.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _audio);
        }
        foreach (var node in menu.FindChildren("*", "", true, false))
            Hook(node);
        var tree = menu.GetTree();
        void Added(Node node)
        {
            if (menu.IsAncestorOf(node))
                Hook(node);
        }
        tree.NodeAdded += Added;
        menu.TreeExiting += () => tree.NodeAdded -= Added;
    }

    private static void Play(string sound) => _audio?.PlayGui($"gui/sounds/{sound}");

    /// <summary>A chat message has come in (MachGuiMessageBroker, chatmsg.wav).</summary>
    public static void Chat() => Play("chatmsg.wav");

    private static void Hook(Node node)
    {
        // The keyboard moving the focus to another control clicks with navkey.wav (MachGuiStartupScreens' keys).
        if (node is Control { FocusMode: not Control.FocusModeEnum.None } focusable)
            focusable.FocusEntered += () =>
            {
                if (Input.IsAnythingPressed() && !Input.IsMouseButtonPressed(MouseButton.Left) && !Input.IsMouseButtonPressed(MouseButton.Right))
                    Play("navkey.wav");
            };
        switch (node)
        {
            case OptionButton option:
                option.MouseEntered += () => Highlight(option);
                option.Pressed += () => Play("click.wav");
                option.ItemSelected += _ => Play("listclik.wav");
                option.GetPopup().IdFocused += _ => Play("listhigh.wav");
                break;
            case BaseButton button:
                button.MouseEntered += () => Highlight(button);
                button.Pressed += () => Play("click.wav");
                button.GuiInput += input =>
                {
                    if (button.Disabled && input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                        Play("clickbad.wav");
                };
                break;
            case Tree list:
                HookList(list, at => list.GetItemAtPosition(at));
                break;
            case ItemList list:
                HookList(list, at => list.GetItemAtPosition(at, exact: true) is var index and >= 0 ? index : null);
                break;
        }
    }

    private static void Highlight(BaseButton button)
    {
        if (!button.Disabled)
            Play("highligh.wav");
    }

    /// <summary>
    /// Sounds <c>listhigh.wav</c> each time the mouse comes onto another entry of a list and <c>listclik.wav</c> when
    /// one is clicked (the click itself: a list the menu selects by itself stays quiet).
    /// </summary>
    private static void HookList(Control list, Func<Vector2, object?> entryAt)
    {
        object? pointedAt = null;
        list.GuiInput += input =>
        {
            switch (input)
            {
                case InputEventMouseMotion motion:
                    var entry = entryAt(motion.Position);
                    if (entry is not null && !Equals(entry, pointedAt))
                        Play("listhigh.wav");
                    pointedAt = entry;
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click when entryAt(click.Position) is not null:
                    Play("listclik.wav");
                    break;
            }
        };
        list.MouseExited += () => pointedAt = null;
    }
}
