using Machines.Sim.Combat;

namespace Machines.Game;

/// <summary>
/// One trigger for each weapon in first person (the options' Mouse button per weapon, always in Wired), the remake's own:
/// the left trigger fires what is mounted left, the right one what is mounted right, the top one what is on top, held
/// ones together. It goes by the weapons' mountings, not by their places in the list, so a machine with only a right gun
/// fires it with the right trigger. A machine with guns on one mounting only, on whatever mounting and however many other
/// places stand empty, fires them with the left trigger as well, so that nobody is left without one: a battery's copies
/// stand on one mounting and count as one gun here.
/// </summary>
public static class WeaponTriggers
{
    /// <summary>The triggers of a gun on <paramref name="mounting"/>, on a machine with guns on <paramref name="places"/> mountings (<see cref="Places"/>).</summary>
    public static IReadOnlyList<Mounting> Of(Mounting mounting, int places) =>
        places == 1 && mounting != Mounting.Left ? [Mounting.Left, mounting] : [mounting];

    /// <summary>How many mountings carry a gun: the copies of a battery count once.</summary>
    public static int Places(IReadOnlyList<WeaponMount> weapons)
    {
        var mountings = 0;
        foreach (var weapon in weapons)
            mountings |= 1 << (int)weapon.Mounting;
        return System.Numerics.BitOperations.PopCount((uint)mountings);
    }

    /// <summary>The weapons that fire for the triggers held: a bit for each by its place in the list, as a DriveCommand takes them, so every copy of a battery.</summary>
    public static int Mask(IReadOnlyList<WeaponMount> weapons, Func<Mounting, bool> held)
    {
        var (mask, places) = (0, Places(weapons));
        for (var i = 0; i < weapons.Count; i++)
        {
            if (Of(weapons[i].Mounting, places).Any(held))
                mask |= 1 << i;
        }
        return mask;
    }
}

/// <summary>
/// What the driver's hands are doing with the triggers, the remake's own like the triggers themselves. The world looks at
/// them 20 times a second, and a burst takes the aim on two ticks at least: the first starts it and spends the reload, the
/// next lets the round go. So a quick click could fall between two ticks, or spend a reload on a round that never left.
/// A press is therefore owed to its gun until that has opened fire, for <see cref="Patience"/> seconds at most: a click in
/// the last moment of a reload still counts, one in the middle of it does not. And only a press seen here counts: a button
/// that came down before the driver got in, or that stayed marked as down when the window lost the focus, fires nothing.
/// </summary>
public sealed class TriggerHands
{
    /// <summary>How long a press waits for its gun to open fire, in seconds.</summary>
    public const double Patience = 0.3;

    private readonly HashSet<Mounting> _down = [];
    private readonly Dictionary<Mounting, double> _owed = [];
    private readonly Dictionary<Mounting, double> _pressed = [];

    public void Press(Mounting trigger, double now)
    {
        _down.Add(trigger);
        _owed[trigger] = now + Patience;
        _pressed[trigger] = now;
    }

    /// <summary>When a trigger last went down, in the clock's seconds; never for one not pressed since the hands let go.</summary>
    public double PressedAt(Mounting trigger) => _pressed.GetValueOrDefault(trigger, double.NegativeInfinity);

    public void Release(Mounting trigger) => _down.Remove(trigger);

    /// <summary>The hands are off the triggers: the driver got out, or the window lost the focus.</summary>
    public void LetGo()
    {
        _down.Clear();
        _owed.Clear();
        _pressed.Clear();
    }

    public bool Down(Mounting trigger) => _down.Contains(trigger);

    /// <summary>The weapons that are to fire: those of the triggers <paramref name="held"/> and of the presses still owed.</summary>
    public int Mask(IReadOnlyList<WeaponMount> weapons, Func<Mounting, bool> held, double now)
    {
        var places = WeaponTriggers.Places(weapons);
        foreach (var (trigger, until) in _owed.ToList())
        {
            if (now > until || weapons.Any(w => w.RoundsLeft > 0 && WeaponTriggers.Of(w.Mounting, places).Contains(trigger)))
                _owed.Remove(trigger);
        }
        return WeaponTriggers.Mask(weapons, trigger => held(trigger) || _owed.ContainsKey(trigger));
    }
}
