using Godot;

namespace Machines.Game;

/// <summary>
/// The keys the player can choose (<see cref="KeyMap"/>), opened from the options, the same before a match and over
/// one: the moves on the left, the commands on the right, each with a button that shows its key. A click on one waits
/// for the new key, Esc keeps the old; an action that had the key where this one works takes the old one in
/// exchange. Defaults puts every key back. A second page holds the weapons' triggers and the keys of a Wired run, which
/// can lie on a mouse button as well: there the button pressed while it waits is taken. MACHINES_MENU_SCREENSHOT=&lt;png&gt;,keys
/// or keys-run shows them.
/// </summary>
public partial class KeysPage(Action back, bool run = false) : MenuPage(back)
{
    private readonly Action _back = back;
    private readonly Dictionary<KeyMap.Act, ChipButton> _buttons = [];
    private Label _status = null!;
    private KeyMap.Act? _waiting;

    private const string Hint = "Click a key to change it.";

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading(run ? "Keys of Wired" : "Keys"));

        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);
        if (run)
            BuildRun(columns);
        else
            BuildMain(columns);

        _status = MenuTheme.Label(run && KeyMap.GaveWay.Count > 0 ? KeyMap.GaveWayNote() : Hint, 24, MenuTheme.Bright);
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
        var foot = Line(Gap, backButton, defaults);
        if (!run)
        {
            var stage = Stage;
            var more = Button("Keys of Wired", () => stage.Show(new KeysPage(() => stage.Show(new KeysPage(_back)), run: true)));
            more.CustomMinimumSize = new Vector2(400, 76);
            foot.AddChild(more);
        }
        page.AddChild(foot);
        Refresh();
        SwitchOn();
        Focus(_buttons[run ? KeyMap.Act.FireLeft : KeyMap.Act.Forward]);
    }

    private void BuildMain(Control columns)
    {
        columns.AddChild(List("MOVING", KeyMap.All.Where(KeyMap.IsMove).Append(KeyMap.Act.FirstPerson).Append(KeyMap.Act.Weapons).Append(KeyMap.Act.CentreView),
            "The moves scroll and turn the ground camera and drive in first person, where left and right step sideways or turn as the options' first-person keys say."));
        columns.AddChild(List("COMMANDS", KeyMap.All.Where(act => !KeyMap.IsMove(act) && !KeyMap.IsRun(act) && act is not (KeyMap.Act.FirstPerson or KeyMap.Act.Weapons or KeyMap.Act.CentreView)),
            "These keep their use: the arrows scroll too, and with Ctrl and Shift tilt the camera or, with Shift, look in first person; " +
            "Ctrl with S stops, Shift with A or S selects all machines or buildings, Alt with them looks at the next; the numbers select a squadron and Ctrl with one forms it; F1 to F4 chat; Alt with the arrows moves the panel; " +
            "Esc cancels or opens the menus. R turns a building, keypad 5 centres the upper body, keypad 1 goes into first person and " +
            "Pause pauses too while no action has them. While the options' Classic Keys are on, the original's keys stand in for these."));
    }

    private void BuildRun(Control columns)
    {
        columns.AddChild(List("WEAPONS", [KeyMap.Act.FireLeft, KeyMap.Act.FireRight, KeyMap.Act.FireTop],
            "One trigger for each weapon in first person, with the options' Mouse button per weapon and always in Wired. Held ones fire together. A key or a mouse button will do; while these are on the mouse, the right button neither turns the upper body nor leaves by a double click."));
        columns.AddChild(List("WIRED", [KeyMap.Act.SquadFollow, KeyMap.Act.SquadAttack, KeyMap.Act.SquadHold, KeyMap.Act.SquadBack, KeyMap.Act.Ability1, KeyMap.Act.Ability2, KeyMap.Act.Dash, KeyMap.Act.Hangar],
            "These work in the hero, in first person; the hangar also from above, at the pod between the waves. The options' Classic Keys leave them as they are."));
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
        _status.Text = Lang.T(KeyMap.TakesMouse(act) ? "Press the key or mouse button for {0}, Esc keeps {1}." : "Press the key for {0}, Esc keeps {1}.", KeyMap.Title(act), KeyMap.ChosenName(act));
    }

    // What is waited for comes before the buttons and the menus' Esc see it: a key, or a mouse button where the action takes one.
    // The wheel's turns are no buttons to hold.
    public override void _Input(InputEvent @event)
    {
        if (_waiting is not { } act)
            return;
        Key pressed;
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
            pressed = key.Keycode;
        else if (@event is InputEventMouseButton { Pressed: true } mouse && KeyMap.TakesMouse(act)
            && mouse.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle or MouseButton.Xbutton1 or MouseButton.Xbutton2)
            pressed = KeyMap.Mouse(mouse.ButtonIndex);
        else
            return;
        GetViewport().SetInputAsHandled();
        _waiting = null;
        if (pressed == Key.Escape)
            _status.Text = Hint;
        else if (pressed is Key.None or Key.Unknown)
            _status.Text = Lang.T("That key cannot be used; pick another.");
        else if (KeyMap.Refuses(act, pressed))
            _status.Text = Lang.T("{0} keeps its own use; pick another key.", KeyMap.Name(pressed));
        else
        {
            var old = KeyMap.ChosenName(act);
            var swapped = KeyMap.Set(act, pressed, out var clash);
            _status.Text = clash is (var holder, var blocker)
                ? Lang.T("{0} is {1}'s, which cannot take {2} while {3} has it; change one of them first.", KeyMap.Name(pressed), KeyMap.Title(holder), old, KeyMap.Title(blocker))
                : swapped.Count > 0
                    ? Lang.T("{0} is now on {1}; {2} took {3}.", KeyMap.Title(act), KeyMap.ChosenName(act), string.Join(Lang.T(" and "), swapped.Select(KeyMap.Title)), old)
                    : Lang.T("{0} is now on {1}.", KeyMap.Title(act), KeyMap.ChosenName(act));
        }
        Refresh();
        _buttons[act].GrabFocus();
    }

    private void Refresh()
    {
        foreach (var (act, button) in _buttons)
            button.Text = KeyMap.ChosenName(act).ToUpperInvariant();
    }
}
