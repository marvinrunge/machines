using Godot;

namespace Machines.Game;

/// <summary>
/// The keys the player can choose (<see cref="KeyMap"/>), opened from the options, the same before a match and over
/// one: the moves on the left, the commands on the right, each with a button that shows its key. A click on one waits
/// for the new key, Esc keeps the old; an action that had the key where this one works takes the old one in
/// exchange. Defaults puts every key back. MACHINES_MENU_SCREENSHOT=&lt;png&gt;,keys shows it.
/// </summary>
public partial class KeysPage(Action back) : MenuPage(back)
{
    private readonly Dictionary<KeyMap.Act, ChipButton> _buttons = [];
    private Label _status = null!;
    private KeyMap.Act? _waiting;

    private const string Hint = "Click a key to change it.";

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading("Keys"));

        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);
        columns.AddChild(List("MOVING", KeyMap.All.Where(KeyMap.IsMove).Append(KeyMap.Act.FirstPerson).Append(KeyMap.Act.Weapons).Append(KeyMap.Act.CentreView),
            "The moves scroll and turn the ground camera and drive in first person, where left and right step sideways or turn as the options' first-person keys say."));
        columns.AddChild(List("COMMANDS", KeyMap.All.Where(act => !KeyMap.IsMove(act) && act is not (KeyMap.Act.FirstPerson or KeyMap.Act.Weapons or KeyMap.Act.CentreView)),
            "These keep their use: the arrows scroll too, and with Ctrl and Shift tilt the camera or, with Shift, look in first person; " +
            "Ctrl or Shift with S stops; the numbers select a squadron and Ctrl with one forms it; F1 to F4 chat; Alt with the arrows moves the panel; " +
            "Esc cancels or opens the menus. R turns a building and keypad 5 centres the upper body too while no action has them."));

        _status = MenuTheme.Label(Hint, 24, MenuTheme.Bright);
        page.AddChild(_status);

        var backButton = Button("Back", Back);
        var defaults = Button("Defaults", () =>
        {
            _waiting = null;
            KeyMap.Reset();
            Refresh();
            _status.Text = "Every key is back on its first.";
        });
        backButton.CustomMinimumSize = defaults.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, backButton, defaults));
        Refresh();
        SwitchOn();
        Focus(_buttons[KeyMap.Act.Forward]);
    }

    /// <summary>A chip of actions, each a row with its name and its key's button, and a line under them.</summary>
    private ChipPanel List(string title, IEnumerable<KeyMap.Act> acts, string note)
    {
        var chip = new ChipPanel { Padding = 26, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        // Two actions to a row, or the page would run off the screen.
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 8);
        chip.AddChild(Column(14, MenuTheme.Label(title, 24, MenuTheme.Bright), grid, MenuTheme.Paragraph(note, 20, MenuTheme.Dim)));
        foreach (var act in acts)
        {
            var name = MenuTheme.Label(KeyMap.Title(act), 22, MenuTheme.Text);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            var button = Button("", () => Wait(act), 52, 22);
            button.CustomMinimumSize = new Vector2(170, 52);
            _buttons[act] = button;
            grid.AddChild(name);
            grid.AddChild(button);
        }
        return chip;
    }

    private void Wait(KeyMap.Act act)
    {
        Refresh();
        _waiting = act;
        _buttons[act].Text = "?";
        _status.Text = Lang.T("Press the key for {0}, Esc keeps {1}.", KeyMap.Title(act), KeyMap.Name(act));
    }

    // The key waited for comes before the buttons and the menus' Esc see it.
    public override void _Input(InputEvent @event)
    {
        if (_waiting is not { } act || @event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        GetViewport().SetInputAsHandled();
        _waiting = null;
        if (key.Keycode == Key.Escape)
            _status.Text = Hint;
        else if (key.Keycode is Key.None or Key.Unknown)
            _status.Text = Lang.T("That key cannot be used; pick another.");
        else if (KeyMap.Reserved(key.Keycode))
            _status.Text = Lang.T("{0} keeps its own use; pick another key.", KeyMap.Name(key.Keycode));
        else
        {
            var old = KeyMap.Name(act);
            var swapped = KeyMap.Set(act, key.Keycode, out var clash);
            _status.Text = clash is (var holder, var blocker)
                ? Lang.T("{0} is {1}'s, which cannot take {2} while {3} has it; change one of them first.", KeyMap.Name(key.Keycode), KeyMap.Title(holder), old, KeyMap.Title(blocker))
                : swapped.Count > 0
                    ? Lang.T("{0} is now on {1}; {2} took {3}.", KeyMap.Title(act), KeyMap.Name(act), string.Join(Lang.T(" and "), swapped.Select(KeyMap.Title)), old)
                    : Lang.T("{0} is now on {1}.", KeyMap.Title(act), KeyMap.Name(act));
        }
        Refresh();
        _buttons[act].GrabFocus();
    }

    private void Refresh()
    {
        foreach (var (act, button) in _buttons)
            button.Text = KeyMap.Name(act).ToUpperInvariant();
    }
}
