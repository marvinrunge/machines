using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// Where a sighted driver's gun will point once the commands still on their way are carried out, worked out in the
/// simulation's own steps (the hull's turn of <c>GameWorld.DriveAsTheOriginal</c>, then the upper body's
/// <see cref="GameWorld.GunStep"/>), so that the gun sight shows where a round fired now will go; and which held
/// triggers a command carries while "Fire waits for the gun" is on. Wired's sighted driver only; pure, so the tests run it
/// beside a world.
/// </summary>
public static class GunPrediction
{
    /// <summary>Seconds a trigger counts as freshly pressed: such a press fires wherever the gun points.</summary>
    public const float FreshPress = 0.25f;

    /// <summary>
    /// A command on its way: the tick the world carries it out on, the hull turn it makes then (radians anticlockwise, as
    /// <see cref="GameWorld.HullTurnOf"/> works it out) and the heading the driver looks along.
    /// </summary>
    public readonly record struct Due(long Tick, float Turn, float Yaw);

    /// <summary>
    /// The gun as it will stand: the hull's facing, the upper body's turn from it, the two together (the gun's heading), how
    /// far that lags behind where the driver looks (±π) and whether it is on the sight (within <see cref="WiredBalance.GunPlay"/>).
    /// </summary>
    public readonly record struct Gun(float Facing, float Head, float Heading, float Lag, bool On);

    /// <summary>A command joins those on their way; one due at the same tick as the last gives way to it, as the world keeps the last of a turn.</summary>
    public static void Add(List<Due> due, Due entry)
    {
        if (due.Count > 0 && due[^1].Tick == entry.Tick)
            due[^1] = entry;
        else
            due.Add(entry);
    }

    /// <summary>The commands the world has carried out by <paramref name="tick"/> leave.</summary>
    public static void Drop(List<Due> due, long tick) => due.RemoveAll(entry => entry.Tick < tick);

    /// <summary>The hull turns still to come from <paramref name="tick"/> on, but for the one due at <paramref name="except"/>, which a new command replaces.</summary>
    public static float Turning(IReadOnlyList<Due> due, long tick, long except)
    {
        // Summed in double, as Enumerable.Sum sums floats.
        var sum = 0d;
        foreach (var entry in due)
        {
            if (entry.Tick >= tick && entry.Tick != except)
                sum += entry.Turn;
        }
        return (float)sum;
    }

    /// <summary>
    /// Replays the commands due from <paramref name="tick"/> (the world's next) on the gun as the world stands now, in due
    /// order, and <paramref name="next"/>, the command not yet sent, after them; a tick without a command of its own keeps
    /// the last one's turn and yaw, as the world does. A tick in a battle's opening (<paramref name="held"/>) turns no hull,
    /// a busy machine turns none at all, and a machine inside a building turns no upper body and is never on the sight.
    /// </summary>
    /// <param name="facing">The hull's facing now, radians anticlockwise.</param>
    /// <param name="head">The upper body's turn from the hull now (<see cref="GameWorld.HeadOf"/>).</param>
    /// <param name="headStep">One tick of the upper body's turn (<see cref="GameWorld.HeadStep"/>); 0 for a machine without one.</param>
    /// <param name="carried">What the world holds from its last command, for ticks before the first due: its turn and yaw (NaN for none).</param>
    public static Gun Predict(float facing, float head, float headStep, bool busy, bool inside, long tick, IReadOnlyList<Due> due,
        Func<long, bool> held, Due? next = null, (float Turn, float Yaw)? carried = null)
    {
        var (turn, yaw) = carried ?? (0f, float.NaN);
        var last = next?.Tick ?? (due.Count > 0 ? due[^1].Tick : tick - 1);
        var index = 0;
        for (var t = tick; t <= last; t++)
        {
            while (index < due.Count && due[index].Tick < t)
                index++;
            Due? entry = next is { } coming && coming.Tick == t ? coming : index < due.Count && due[index].Tick == t ? due[index] : null;
            if (entry is { } command)
                (turn, yaw) = (held(t) ? 0 : command.Turn, command.Yaw);
            if (!busy)
                facing += turn;
            if (!inside && headStep > 0 && float.IsFinite(yaw))
                head = GameWorld.GunStep(head, MathF.IEEERemainder(yaw - facing, 2 * MathF.PI), headStep).Head;
        }
        var heading = facing + head;
        var lag = float.IsFinite(yaw) ? MathF.IEEERemainder(heading - yaw, 2 * MathF.PI) : 0;
        return new Gun(facing, head, heading, lag, !inside && float.IsFinite(yaw) && MathF.Abs(lag) <= WiredBalance.GunPlay);
    }

    /// <summary>
    /// Whether a held trigger's weapon goes into the next command. With "Fire waits for the gun" off, always. With it on: a
    /// weapon that is set down rather than fired along a barrel (the bee's bomb, the vortex) always; the Gorilla's punch
    /// while the hull is within <see cref="WiredBalance.PunchPlay"/> of where the driver looks (<paramref name="hullLag"/>);
    /// every other weapon while the gun is on the sight, the gun sight rests on an enemy, or its trigger went down less than
    /// <see cref="FreshPress"/> seconds ago (<paramref name="sincePress"/>), so a click fires at once wherever the gun points.
    /// </summary>
    public static bool Gate(bool waits, string weapon, bool gunOn, bool enemy, double sincePress, float hullLag)
    {
        if (!waits || weapon is "VORTEX" or "BEE_BOMB")
            return true;
        if (weapon == "GORILLA_PUNCH")
            return MathF.Abs(hullLag) <= WiredBalance.PunchPlay;
        return gunOn || enemy || sincePress < FreshPress;
    }
}
