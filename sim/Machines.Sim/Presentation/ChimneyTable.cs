namespace Machines.Sim.Presentation;

/// <summary>The colour of a smoke plume: the original's puff materials (smokpuff.cpp).</summary>
public enum ChimneySmoke
{
    /// <summary>PUFF_5: civilian factories, glowing white (0.86, 0.87, 0.88).</summary>
    Civilian,
    /// <summary>PUFF_6: military factories, glowing dark red (66/255, 0, 0).</summary>
    Military,
    /// <summary>PUFF_2: smelters, plain dark smoke.</summary>
    Smelter,
}

/// <summary>A chimney: where its smoke starts in the building's own space (the original's axes, z up) and how many wisps rise.</summary>
public sealed record Chimney(float X, float Y, float Z, int Wisps, ChimneySmoke Smoke);

/// <summary>
/// What a building's chimneys do while it works, as machphys set them up: factories (MachPhysFactory::doWorking
/// and dealWithSmoke) make their <c>chimhot1.bmp</c> surfaces pulse and send up a plume from each chimney every
/// 40 seconds; smelters (MachPhysSmelter::doWorking) one denser plume every 50. A plume's wisps (MachPhysSmokePlume)
/// set off at random in its first half, rise to <see cref="Height"/> metres over the second, swelling and shrinking.
/// The remake keeps the chimneys, heights, colours and rise, but lets the smoke flow on while the building works:
/// the original's two or three puffs every 40 seconds barely show.
/// </summary>
public static class ChimneyTable
{
    /// <summary>How high every plume rises, metres.</summary>
    public const float Height = 40;

    /// <summary>A wisp's average size, metres (each one 0.9 to 1.3 times it).</summary>
    public const float WispSize = 12.5f;

    /// <summary>The texture whose surfaces pulse while a factory works, and how: from half to full brightness every 0.2 s.</summary>
    public const string GlowTexture = "chimhot1.bmp";
    public const float GlowLow = 0.5f, GlowPeriod = 0.2f;

    /// <summary>A building's chimneys, or none.</summary>
    public static IReadOnlyList<Chimney> For(string type, string? subType, int level) => (type, subType, level) switch
    {
        ("FACTORY", "MILITARY", 1) => [new(2.2f, -6.4f, 12, 2, ChimneySmoke.Military), new(2.2f, 6.4f, 12, 2, ChimneySmoke.Military)],
        ("FACTORY", "MILITARY", 3) => [new(-5.3f, 0, 24.1f, 3, ChimneySmoke.Military)],
        // Level 4 is "TBD" in the original: no smoke.
        ("FACTORY", "MILITARY", 5) => [new(0.7f, -19.7f, 38.3f, 3, ChimneySmoke.Military), new(0.7f, 20.5f, 38.3f, 3, ChimneySmoke.Military)],
        ("FACTORY", "CIVILIAN", 1) =>
        [
            new(-6.2f, -5.6f, 22.8f, 2, ChimneySmoke.Civilian), new(-1.0f, -5.6f, 22.8f, 2, ChimneySmoke.Civilian),
            new(-1.8f, 13.6f, 24.1f, 3, ChimneySmoke.Civilian),
        ],
        ("FACTORY", "CIVILIAN", 3) =>
        [
            new(-2.5f, 7, 27.5f, 2, ChimneySmoke.Civilian), new(-10, -11.6f, 29, 3, ChimneySmoke.Civilian),
            new(11.8f, 14.8f, 29, 2, ChimneySmoke.Civilian),
        ],
        ("FACTORY", "CIVILIAN", 5) =>
        [
            new(-2.8f, 7.7f, 34.6f, 2, ChimneySmoke.Civilian), new(-4.5f, -14.5f, 28.1f, 2, ChimneySmoke.Civilian),
            new(4.3f, -14.5f, 28.1f, 2, ChimneySmoke.Civilian), new(13.2f, -14.5f, 28.1f, 2, ChimneySmoke.Civilian),
        ],
        ("SMELTER", _, 1) => [new(1.7f, 0.9f, 19, 5, ChimneySmoke.Smelter)],
        ("SMELTER", _, 3) => [new(0, 0, 16, 5, ChimneySmoke.Smelter)],
        _ => [],
    };

    /// <summary>
    /// How long a wisp takes to rise: half its plume's duration (40 s for factories, which start one every 40 s;
    /// 45 s for smelters, every 50 s).
    /// </summary>
    public static float Rise(string type) => type == "SMELTER" ? 22.5f : 20;
}
