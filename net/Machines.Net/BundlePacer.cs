namespace Machines.Net;

/// <summary>
/// How many of the bundles in hand one 50 ms slot of the client's clock runs (online, bundle mode; P2 design §4.3), and how
/// long a frame may spend catching up without drawing. The game and the headless test clients run by the same rules.
/// </summary>
/// <remarks>
/// A slot runs one bundle while one to <see cref="Buffer"/> + 1 are in hand, two at four, three from five on: a client a few
/// ticks behind works its backlog off at three times the speed, a net 40 ticks a second. From <see cref="CatchUpLead"/> in
/// hand the slots give way to catching up, a frame of ticks without drawing: 30 ms of them, 100 ms from
/// <see cref="LongCatchUp"/> on, so a rebuild of thousands of ticks is not three times as long as its work.
/// </remarks>
public static class BundlePacer
{
    /// <summary>Bundles kept in hand against the line's jitter before the client runs faster.</summary>
    public const int Buffer = 2;

    /// <summary>The most bundles one slot runs.</summary>
    public const int MostPerSlot = 3;

    /// <summary>From this many bundles in hand the game catches up without drawing.</summary>
    public const int CatchUpLead = 60;

    /// <summary>From this many bundles in hand a catch-up frame may take longer.</summary>
    public const int LongCatchUp = 600;

    /// <summary>The bundles one slot runs with <paramref name="ready"/> in hand: none, one, two or three.</summary>
    public static int TicksForSlot(int ready) => ready <= 0 ? 0 : ready <= Buffer + 1 ? 1 : Math.Min(MostPerSlot, ready - Buffer);

    /// <summary>How long one frame may run ticks without drawing: none below <see cref="CatchUpLead"/>, 30 ms from it, 100 ms from <see cref="LongCatchUp"/>.</summary>
    public static TimeSpan CatchUpBudget(int ready) =>
        ready >= LongCatchUp ? TimeSpan.FromMilliseconds(100) : ready >= CatchUpLead ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero;
}
