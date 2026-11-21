using Machines.Import.Model;

namespace Machines.Sim.Navigation;

/// <summary>
/// How a machine gets about, as far as the <see cref="NavGrid"/> asks: whether it crosses water (hover and glider), how
/// much room it needs (its size class) and whether it crawls through ducts. On the original planets' 4 m grid only the
/// water counts, as it always did; the size and the crawl count only on a ship deck's fine 1 m grid
/// (<see cref="NavGrid.Fine"/>, plan §1.1).
/// </summary>
/// <param name="Water">It crosses water cells (hover, glider).</param>
/// <param name="Size">
/// Its size class: 0 a point (no room asked, as on the original planets), 1 (A) up to 1.5 m of radius (the Locator, the
/// Grunt), 2 (B) up to 2.5 m (Reapers, the Commander), 3 (C) anything wider (the Thresher). A cell admits a class when its
/// middle is at least <see cref="Room"/> metres from the nearest blocked cell.
/// </param>
/// <param name="Crawl">It may go into ducts and over their lips (spider legs and the smallest access size: the Locator).</param>
public readonly record struct Mobility(bool Water, int Size, bool Crawl)
{
    /// <summary>A point on land: no water, no room asked, no ducts. What every query of the original planets is.</summary>
    public static readonly Mobility Land = new(false, 0, false);

    /// <summary>A point that crosses water.</summary>
    public static readonly Mobility Waterborne = new(true, 0, false);

    /// <summary>The largest size class.</summary>
    public const int Largest = 3;

    /// <summary>The room (metres from a cell's middle to the nearest blocked cell) each size class needs: 0, 1.5, 2.5, 3.5.</summary>
    public static float Room(int size) => size <= 0 ? 0 : size + 0.5f;

    /// <summary>The same, doubled and squared (the integers the grid compares): 0, 9, 25, 49.</summary>
    internal static int RoomSquared4(int size) => size <= 0 ? 0 : (2 * size + 1) * (2 * size + 1);

    /// <summary>The size class of a machine of a radius: the smallest whose room is at least the radius.</summary>
    public static int SizeOf(float radius) => radius <= 1.5f ? 1 : radius <= 2.5f ? 2 : Largest;

    /// <summary>A machine of these stats and radius: it crosses water if it hovers or glides, crawls if it walks on legs and fits the smallest door.</summary>
    public static Mobility Of(MachineLevel stats, float radius) => new(
        stats.Locomotion is Locomotion.Hover or Locomotion.Glider,
        SizeOf(radius),
        stats.Locomotion == Locomotion.Spider && stats.MinAccessSizeIndex == 0);

    /// <summary>Only the water, as the 4 m grid of the original planets knows it.</summary>
    public Mobility Coarse => Water ? Waterborne : Land;

    /// <summary>A small number for the grid's caches: 0 to 15.</summary>
    internal int Key => (Water ? 1 : 0) | Math.Clamp(Size, 0, Largest) << 1 | (Crawl ? 8 : 0);

    public override string ToString() => $"{(Water ? "water " : "")}size {Size}{(Crawl ? " crawl" : "")}";
}
