using Godot;

namespace Machines.Game;

/// <summary>
/// The keys the player can choose (the options' Keys page, <see cref="KeysPage"/>): the moves of the ground camera and
/// of first person, and the commands with a key of their own. Each has the original's key, or the remake's where the
/// original had none, until the player picks another; the choices are kept in <c>user://settings.cfg</c> under
/// <c>[keys]</c>. The rest stay where they are (<see cref="Reserved"/>): the arrows beside the moves, Esc, the numbers
/// of the squadrons, F1 to F4 of the chat, Enter and Ctrl, Shift and Alt with what they change. The options' Classic
/// Keys set the original's whole layout over them (<see cref="Original"/>) without touching what the player picked.
/// The triggers of the weapons and the keys of a Wired run are the remake's own, and Classic Keys leave those as they are.
/// They alone may also lie on a mouse button, which stands in the map as a key no keyboard has (<see cref="Mouse"/>).
/// </summary>
public static class KeyMap
{
    private const string Settings = "user://settings.cfg";

    /// <summary>What a key does.</summary>
    public enum Act
    {
        Forward,
        Back,
        Left,
        Right,
        TurnLeft,
        TurnRight,
        FirstPerson,
        Weapons,
        CentreView,
        TurnBuilding,
        Initiative,
        AssemblyPoint,
        Pause,
        Menu,
        ZenithView,
        GroundView,
        Hangar,
        FireLeft,
        FireRight,
        FireTop,
        SquadFollow,
        SquadAttack,
        SquadHold,
        SquadBack,
        Ability1,
        Ability2,
        Dash,
    }

    /// <summary>Where an action's key works. Two actions that work in the same place cannot share a key.</summary>
    [Flags]
    private enum Where
    {
        Ground = 1,
        FirstPerson = 2,
        Both = Ground | FirstPerson,

        /// <summary>Only in a run of Wired, where the panel has no use for the letter: everywhere else the key stays free.</summary>
        Run = 4,

        /// <summary>Only while each weapon has its own trigger (the options' Mouse button per weapon, always in Wired).</summary>
        Buttons = 8,
    }

    /// <summary>
    /// An action's name on the page, its key until the player picks another, where it works, and a second key of the
    /// original's or the remake's that does the same while no action has it.
    /// </summary>
    private sealed record Entry(string Name, Key Default, Where Where, Key Also = Key.None);

    private static readonly Dictionary<Act, Entry> Entries = new()
    {
        [Act.Forward] = new("Forward", Key.W, Where.Both),
        [Act.Back] = new("Back", Key.S, Where.Both),
        [Act.Left] = new("Left", Key.A, Where.Both),
        [Act.Right] = new("Right", Key.D, Where.Both),
        [Act.TurnLeft] = new("Turn left", Key.Q, Where.Both),
        [Act.TurnRight] = new("Turn right", Key.E, Where.Both),
        // The original went into first person with 1 on the number pad.
        [Act.FirstPerson] = new("First person", Key.F, Where.Both, Key.Kp1),
        [Act.Weapons] = new("Next weapons", Key.Tab, Where.FirstPerson),
        // The original centred the upper body with keypad 5.
        [Act.CentreView] = new("Upper body forward", Key.C, Where.FirstPerson, Key.Kp5),
        // The original turned a building with Space; R is the remake's.
        [Act.TurnBuilding] = new("Turn building", Key.Space, Where.Ground, Key.R),
        [Act.Initiative] = new("Initiative", Key.Tab, Where.Ground),
        [Act.AssemblyPoint] = new("Assembly point", Key.B, Where.Ground),
        // The original paused with Pause; P was its patrol.
        [Act.Pause] = new("Pause", Key.P, Where.Ground, Key.Pause),
        [Act.Menu] = new("Menus", Key.F10, Where.Ground),
        [Act.ZenithView] = new("Zenith view", Key.Kp2, Where.Ground),
        [Act.GroundView] = new("Ground view", Key.Kp0, Where.Ground),
        // Wired is the remake's own, and so is this key; in any other match I stays the panel's.
        [Act.Hangar] = new("Hangar", Key.I, Where.Run),
        // A mouse button for each weapon: left, right, and the wheel for the top one.
        [Act.FireLeft] = new("Left weapon", Mouse(MouseButton.Left), Where.FirstPerson | Where.Buttons),
        [Act.FireRight] = new("Right weapon", Mouse(MouseButton.Right), Where.FirstPerson | Where.Buttons),
        [Act.FireTop] = new("Top weapon", Mouse(MouseButton.Middle), Where.FirstPerson | Where.Buttons),
        // B is the assembly point's, and R is the second key of turning a building: both stay out of it.
        [Act.SquadFollow] = new("Squad: follow", Key.Z, Where.FirstPerson | Where.Run),
        [Act.SquadAttack] = new("Squad: attack my target", Key.X, Where.FirstPerson | Where.Run),
        [Act.SquadHold] = new("Squad: hold", Key.V, Where.FirstPerson | Where.Run),
        [Act.SquadBack] = new("Squad: back to the pod", Key.T, Where.FirstPerson | Where.Run),
        [Act.Ability1] = new("Ability 1", Key.G, Where.FirstPerson | Where.Run),
        [Act.Ability2] = new("Ability 2", Key.H, Where.FirstPerson | Where.Run),
        // In a run Space no longer fires: that is what the triggers are for.
        [Act.Dash] = new("Dash", Key.Space, Where.FirstPerson | Where.Run),
    };

    /// <summary>
    /// The original's layout, for the options' Classic Keys (MachCameras, MachInGameScreen, MachGuiFirstPerson): the
    /// arrows move and turn, and the letters stay free for the commands.
    /// </summary>
    private static readonly Dictionary<Act, Key> Original = new()
    {
        [Act.Forward] = Key.Up,
        [Act.Back] = Key.Down,
        [Act.Left] = Key.Left,
        [Act.Right] = Key.Right,
        [Act.TurnLeft] = Key.None,
        [Act.TurnRight] = Key.None,
        [Act.FirstPerson] = Key.Kp1,
        [Act.Weapons] = Key.Tab,
        [Act.CentreView] = Key.Kp5,
        [Act.TurnBuilding] = Key.Space,
        [Act.Initiative] = Key.Tab,
        [Act.AssemblyPoint] = Key.B,
        [Act.Pause] = Key.Pause,
        [Act.Menu] = Key.F10,
        [Act.ZenithView] = Key.Kp2,
        [Act.GroundView] = Key.Kp0,
    };

    // Beyond every key of the keyboard, modifiers included.
    private const long MouseKeys = 1L << 40;

    private static Dictionary<Act, Key>? _keys;
    private static bool _classic;

    /// <summary>Every action, in the page's order.</summary>
    public static IReadOnlyList<Act> All { get; } = Enum.GetValues<Act>();

    /// <summary>The moves, which work on the ground and in first person alike.</summary>
    public static bool IsMove(Act act) => act <= Act.TurnRight;

    /// <summary>What came with Wired and its triggers, on a page of its own.</summary>
    public static bool IsRun(Act act) => (Entries[act].Where & (Where.Run | Where.Buttons)) != 0;

    /// <summary>A mouse button as the map keeps it.</summary>
    public static Key Mouse(MouseButton button) => (Key)(MouseKeys + (long)button);

    public static bool IsMouse(Key key) => (long)key > MouseKeys;

    private static MouseButton ButtonOf(Key key) => (MouseButton)((long)key - MouseKeys);

    /// <summary>Whether the action may lie on a mouse button: only what works in first person, where the mouse is free for it.</summary>
    public static bool TakesMouse(Act act) => IsRun(act) && (Entries[act].Where & Where.FirstPerson) != 0;

    public static string Title(Act act) => Lang.T(Entries[act].Name);

    /// <summary>The action's key.</summary>
    public static Key Of(Act act) => GameOptions.ClassicKeys && Original.TryGetValue(act, out var original) ? original : Keys[act];

    /// <summary>The action's key's name as the keyboard shows it.</summary>
    public static string Name(Act act) => Name(Of(act));

    /// <summary>The name of the key the player picked for the action, which the Classic Keys may be standing over.</summary>
    public static string ChosenName(Act act) => Name(Keys[act]);

    public static string Name(Key key) => IsMouse(key) ? ButtonName(ButtonOf(key))
        : key is Key.Left or Key.Right or Key.Up or Key.Down ? Lang.T(OS.GetKeycodeString(key)) : OS.GetKeycodeString(key).Replace("Kp ", "Num ");

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Left => Lang.T("Left mouse"),
        MouseButton.Right => Lang.T("Right mouse"),
        MouseButton.Middle => Lang.T("Wheel click"),
        _ => Lang.T("Mouse {0}", ButtonNumber(button)),
    };

    // The thumb buttons are the mouse's fourth and fifth: Godot counts the wheel's turns in between.
    private static int ButtonNumber(MouseButton button) => button switch
    {
        MouseButton.Xbutton1 => 4,
        MouseButton.Xbutton2 => 5,
        _ => (int)button,
    };

    /// <summary>Whether the action's key is held down.</summary>
    public static bool Held(Act act) => Of(act) is var key && IsMouse(key) ? Input.IsMouseButtonPressed(ButtonOf(key)) : Input.IsKeyPressed(key);

    /// <summary>The mouse button the action lies on, if it does.</summary>
    public static MouseButton? Button(Act act) => Of(act) is var key && IsMouse(key) ? ButtonOf(key) : null;

    /// <summary>Whether <paramref name="key"/> is the action's key, or its second key while no action has that.</summary>
    public static bool Is(InputEventKey key, Act act) =>
        key.Keycode == Of(act) || !GameOptions.ClassicKeys && Entries[act].Also is var also && also != Key.None && key.Keycode == also && !Keys.ContainsValue(also);

    /// <summary>Whether the event is the action's key or mouse button coming up.</summary>
    public static bool IsUp(InputEvent @event, Act act) => @event switch
    {
        InputEventKey { Pressed: false } key => Is(key, act),
        InputEventMouseButton { Pressed: false } button => Of(act) == Mouse(button.ButtonIndex),
        _ => false,
    };

    /// <summary>Whether the event is the action's key or mouse button going down.</summary>
    public static bool Is(InputEvent @event, Act act) => @event switch
    {
        InputEventKey { Pressed: true, Echo: false } key => Is(key, act),
        InputEventMouseButton { Pressed: true } button => Of(act) == Mouse(button.ButtonIndex),
        _ => false,
    };

    /// <summary>
    /// Whether an action has the key where the player is: on the ground, or with <paramref name="firstPerson"/> driving.
    /// Those of a run count only in <paramref name="run"/>, the hangar's in both views, the weapons' triggers only with
    /// <paramref name="buttons"/>.
    /// </summary>
    public static bool Taken(Key key, bool firstPerson, bool run = false, bool buttons = false) =>
        All.Any(act => Of(act) == key && (Views(act) & (firstPerson ? Where.FirstPerson : Where.Ground)) != 0
            && (run || (Entries[act].Where & Where.Run) == 0) && (buttons || (Entries[act].Where & Where.Buttons) == 0));

    /// <summary>Whether the key keeps its own use and no action can have it.</summary>
    public static bool Reserved(Key key) => key is Key.None or Key.Unknown or Key.Escape or Key.Enter or Key.KpEnter
        or Key.Shift or Key.Ctrl or Key.Alt or Key.Meta or Key.Capslock or Key.Up or Key.Down or Key.Left or Key.Right
        or >= Key.Key0 and <= Key.Key9 or >= Key.F1 and <= Key.F4;

    /// <summary>
    /// Whether first person or the view of a run has the key wired in, whatever the map says: the number pad, the goggles'
    /// N, F5 to F8 and Backspace. What came before Wired may keep a choice of these, as ever; a run's actions would lose out.
    /// </summary>
    public static bool HardWired(Key key) => key is Key.N or Key.Backspace or Key.Kp0 or Key.Kp1 or Key.Kp2 or Key.Kp4 or Key.Kp5
        or Key.Kp6 or Key.Kp8 or >= Key.F5 and <= Key.F8;

    /// <summary>Whether the action cannot have the key. With the Classic Keys a run's action keeps off the original's keys as well.</summary>
    public static bool Refuses(Act act, Key key) => Reserved(key) || IsRun(act) && (HardWired(key) || GameOptions.ClassicKeys && ClassicHas(act, key));

    /// <summary>Whether the original's layout puts the key on an action that works where <paramref name="act"/> does.</summary>
    private static bool ClassicHas(Act act, Key key) => Original.Any(original => original.Value == key && Crosses(original.Key, act));

    /// <summary>The key an action has in <paramref name="keys"/>, or the original's with the Classic Keys.</summary>
    private static Key Standing(Act act, Dictionary<Act, Key> keys) =>
        GameOptions.ClassicKeys && Original.TryGetValue(act, out var original) ? original : keys[act];

    /// <summary>
    /// Gives <paramref name="act"/> the key. An action that had it where this one works takes this one's old key in
    /// exchange, unless a third has that one there too; then nothing changes, and <paramref name="clash"/> tells the
    /// action that had the key and the one in its way. Returns the actions that took the old key.
    /// </summary>
    public static List<Act> Set(Act act, Key key, out (Act Holder, Act Blocker)? clash)
    {
        clash = null;
        var keys = new Dictionary<Act, Key>(Keys);
        var old = keys[act];
        var swapped = All.Where(other => other != act && keys[other] == key && Crosses(other, act)).ToList();
        keys[act] = key;
        foreach (var other in swapped)
            keys[other] = old;
        foreach (var other in swapped)
        {
            // What has no use for the mouse cannot take a mouse button in exchange, nor a run's action a key it cannot have.
            if (IsMouse(old) && !TakesMouse(other) || Refuses(other, old))
            {
                clash = (other, act);
                return [];
            }
            if (All.Where(third => third != other && keys[third] == old && Crosses(third, other)).Select(third => (Act?)third).FirstOrDefault() is { } blocker)
            {
                clash = (other, blocker);
                return [];
            }
        }
        _keys = keys;
        _gaveWay.RemoveAll(moved => moved == act || swapped.Contains(moved));
        Save();
        return swapped;
    }

    /// <summary>Every action back on its first key.</summary>
    public static void Reset()
    {
        _keys = All.ToDictionary(act => act, act => Entries[act].Default);
        _gaveWay = [];
        Save();
    }

    private static List<Act> _gaveWay = [];

    /// <summary>What of a run stands on another key than its own for now, because an older choice of the player's has that one.</summary>
    public static IReadOnlyList<Act> GaveWay
    {
        get
        {
            _ = Keys;
            return _gaveWay;
        }
    }

    /// <summary>What the player is told of it.</summary>
    public static string GaveWayNote() => Lang.T("Your own keys stand where a run's were: {0}.",
        string.Join(", ", GaveWay.Select(act => Lang.T("{0} is on {1}", Title(act), ChosenName(act)))));

    /// <summary>What a run's actions may move to when an older choice has their key.</summary>
    private static readonly Key[] Spare = [Key.J, Key.K, Key.L, Key.U, Key.Y, Key.O, Key.M];

    /// <summary>
    /// A choice from before Wired may hold a key a run's action came with, and where both work one of them would be lost or
    /// both would happen. The player's choice stands: the run's action moves to a free key, until the player says otherwise.
    /// </summary>
    private static void MakeWay(Dictionary<Act, Key> keys)
    {
        foreach (var act in All.Where(IsRun))
        {
            if (!All.Any(other => !IsRun(other) && Standing(other, keys) == keys[act] && Crosses(other, act)))
                continue;
            if (Spare.Where(key => !keys.ContainsValue(key)).Select(key => (Key?)key).FirstOrDefault() is { } free)
            {
                keys[act] = free;
                _gaveWay.Add(act);
            }
        }
    }

    /// <summary>What was there before Wired: a key it shares with what came later does not count against a player's old choice.</summary>
    private static bool Overlap(Act a, Act b) => !IsRun(a) && !IsRun(b) && (Entries[a].Where & Entries[b].Where) != 0;

    // The views an action's key works in; the hangar's in both.
    private static Where Views(Act act) => (Entries[act].Where & Where.Both) is var views && views != 0 ? views : Where.Both;

    /// <summary>A run has the ground and first person too: what is picked now keeps out of the way of its key, though old choices that share it stand.</summary>
    private static bool Crosses(Act a, Act b) => (Views(a) & Views(b)) != 0;

    private static string Setting(Act act) =>
        string.Concat(act.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static Dictionary<Act, Key> Keys
    {
        get
        {
            // The Classic Keys change what a run's actions can have: switched, the keys are read again.
            if (_keys is not null && _classic == GameOptions.ClassicKeys)
                return _keys;
            _classic = GameOptions.ClassicKeys;
            _gaveWay = [];
            _keys = All.ToDictionary(act => act, act => Entries[act].Default);
            var config = new ConfigFile();
            if (config.Load(Settings) != Error.Ok)
                return _keys;
            var chosen = new Dictionary<Act, Key>(_keys);
            foreach (var act in All)
                if ((string)config.GetValue("keys", Setting(act), "") is { Length: > 0 } name && Parsed(name) is var key && !Refuses(act, key) && (!IsMouse(key) || TakesMouse(act)))
                    chosen[act] = key;
            // Keys that clash (a file written by hand) leave every action on its first key; a clash among a run's own
            // actions only those.
            if (All.Any(a => All.Any(b => a < b && chosen[a] == chosen[b] && Overlap(a, b))))
                return _keys;
            if (All.Any(a => All.Any(b => a < b && chosen[a] == chosen[b] && IsRun(a) && IsRun(b) && Crosses(a, b))))
            {
                foreach (var act in All.Where(IsRun))
                    chosen[act] = Entries[act].Default;
            }
            MakeWay(chosen);
            _keys = chosen;
            return _keys;
        }
    }

    // Keeps only the keys the player changed, so a new first key reaches everyone who left theirs.
    private static void Save()
    {
        var config = new ConfigFile();
        config.Load(Settings);
        if (config.HasSection("keys"))
            config.EraseSection("keys");
        foreach (var act in All.Where(act => _keys![act] != Entries[act].Default))
            config.SetValue("keys", Setting(act), Written(_keys![act]));
        config.Save(Settings);
    }

    // A mouse button in the settings reads "Mouse 3", by Godot's number for it.
    private static string Written(Key key) => IsMouse(key) ? $"Mouse {(long)ButtonOf(key)}" : OS.GetKeycodeString(key);

    private static Key Parsed(string name) => name.StartsWith("Mouse ") && int.TryParse(name[6..], out var button) && button > 0
        ? Mouse((MouseButton)button) : OS.FindKeycodeFromString(name);
}
