using Godot;

namespace Machines.Game;

/// <summary>
/// The keys the player can choose (the options' Keys page, <see cref="KeysPage"/>): the moves of the ground camera and
/// of first person, and the commands with a key of their own. Each has the original's key, or the remake's where the
/// original had none, until the player picks another; the choices are kept in <c>user://settings.cfg</c> under
/// <c>[keys]</c>. The rest stay where they are (<see cref="Reserved"/>): the arrows beside the moves, Esc, the numbers
/// of the squadrons, F1 to F4 of the chat, Enter and Ctrl, Shift and Alt with what they change.
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
    }

    /// <summary>Where an action's key works. Two actions that work in the same place cannot share a key.</summary>
    [Flags]
    private enum Where
    {
        Ground = 1,
        FirstPerson = 2,
        Both = Ground | FirstPerson,
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
        [Act.FirstPerson] = new("First person", Key.F, Where.Both),
        [Act.Weapons] = new("Next weapons", Key.Tab, Where.FirstPerson),
        // The original centred the upper body with keypad 5.
        [Act.CentreView] = new("Upper body forward", Key.C, Where.FirstPerson, Key.Kp5),
        // The original turned a building with Space; R is the remake's.
        [Act.TurnBuilding] = new("Turn building", Key.Space, Where.Ground, Key.R),
        [Act.Initiative] = new("Initiative", Key.Tab, Where.Ground),
        [Act.AssemblyPoint] = new("Assembly point", Key.B, Where.Ground),
        [Act.Pause] = new("Pause", Key.P, Where.Ground),
        [Act.Menu] = new("Menus", Key.F10, Where.Ground),
        [Act.ZenithView] = new("Zenith view", Key.Kp2, Where.Ground),
        [Act.GroundView] = new("Ground view", Key.Kp0, Where.Ground),
    };

    private static Dictionary<Act, Key>? _keys;

    /// <summary>Every action, in the page's order.</summary>
    public static IReadOnlyList<Act> All { get; } = Enum.GetValues<Act>();

    /// <summary>The moves, which work on the ground and in first person alike.</summary>
    public static bool IsMove(Act act) => act <= Act.TurnRight;

    public static string Title(Act act) => Entries[act].Name;

    /// <summary>The action's key.</summary>
    public static Key Of(Act act) => Keys[act];

    /// <summary>The action's key's name as the keyboard shows it.</summary>
    public static string Name(Act act) => Name(Of(act));

    public static string Name(Key key) => OS.GetKeycodeString(key).Replace("Kp ", "Num ");

    /// <summary>Whether the action's key is held down.</summary>
    public static bool Held(Act act) => Input.IsKeyPressed(Of(act));

    /// <summary>Whether <paramref name="key"/> is the action's key, or its second key while no action has that.</summary>
    public static bool Is(InputEventKey key, Act act) =>
        key.Keycode == Of(act) || Entries[act].Also is var also && also != Key.None && key.Keycode == also && !Keys.ContainsValue(also);

    /// <summary>Whether the key keeps its own use and no action can have it.</summary>
    public static bool Reserved(Key key) => key is Key.None or Key.Unknown or Key.Escape or Key.Enter or Key.KpEnter
        or Key.Shift or Key.Ctrl or Key.Alt or Key.Meta or Key.Capslock or Key.Up or Key.Down or Key.Left or Key.Right
        or >= Key.Key0 and <= Key.Key9 or >= Key.F1 and <= Key.F4;

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
        var swapped = All.Where(other => other != act && keys[other] == key && Overlap(other, act)).ToList();
        keys[act] = key;
        foreach (var other in swapped)
            keys[other] = old;
        foreach (var other in swapped)
            if (All.Where(third => third != other && keys[third] == old && Overlap(third, other)).Select(third => (Act?)third).FirstOrDefault() is { } blocker)
            {
                clash = (other, blocker);
                return [];
            }
        _keys = keys;
        Save();
        return swapped;
    }

    /// <summary>Every action back on its first key.</summary>
    public static void Reset()
    {
        _keys = All.ToDictionary(act => act, act => Entries[act].Default);
        Save();
    }

    private static bool Overlap(Act a, Act b) => (Entries[a].Where & Entries[b].Where) != 0;

    private static string Setting(Act act) =>
        string.Concat(act.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static Dictionary<Act, Key> Keys
    {
        get
        {
            if (_keys is not null)
                return _keys;
            _keys = All.ToDictionary(act => act, act => Entries[act].Default);
            var config = new ConfigFile();
            if (config.Load(Settings) != Error.Ok)
                return _keys;
            var chosen = new Dictionary<Act, Key>(_keys);
            foreach (var act in All)
                if ((string)config.GetValue("keys", Setting(act), "") is { Length: > 0 } name && OS.FindKeycodeFromString(name) is var key && !Reserved(key))
                    chosen[act] = key;
            // Keys that clash (a file written by hand) leave every action on its first key.
            if (!All.Any(a => All.Any(b => a < b && chosen[a] == chosen[b] && Overlap(a, b))))
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
            config.SetValue("keys", Setting(act), OS.GetKeycodeString(_keys![act]));
        config.Save(Settings);
    }
}
