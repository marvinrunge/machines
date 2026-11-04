namespace Machines.Sim.Presentation;

/// <summary>A flame on a damaged building: where it stands in the building's own space (the original's axes, z up) and how big it burns.</summary>
public sealed record BuildingFlame(float X, float Y, float Z, float Width, float Height);

/// <summary>
/// What a damaged building shows, as machphys set it up (MachPhysEntityDamage, its flameParameters): it darkens from
/// the first hit; at 40 % damage it smokes in the first colour of its kind, at 50 % the first flame burns, at 60 % a
/// dark splat lies on the ground under it, at 65 % the third flame burns, at 75 % it smokes in the second colour too,
/// and at 85 % the second flame burns.
/// </summary>
public static class BuildingDamage
{
    public const float FirstSmoke = 40, FirstFlame = 50, Splat = 60, ThirdFlame = 65, SecondSmoke = 75, SecondFlame = 85;

    /// <summary>The colours of a building's two smokes, red, green, blue from 0 to 1 (damageSmoke1Type, damageSmoke2Type, smokpuff.cpp).</summary>
    public static ((float R, float G, float B) First, (float R, float G, float B) Second) SmokeOf(string type, string? subType) => (type, subType) switch
    {
        ("POD", _) => ((0.2f, 0, 0), (1, 0, 0)),
        ("BEACON", _) => ((0, 0, 1), (0.15f, 1, 0.93f)),
        ("FACTORY", "MILITARY") => ((0.46f, 0.31f, 0), (1, 0.59f, 0)),
        ("FACTORY", _) => ((0.39f, 0.39f, 1), (0.78f, 0.78f, 1)),
        ("GARRISON", _) => ((0, 1, 0), (0.5f, 0.89f, 0)),
        ("HARDWARE_LAB", "MILITARY") => ((0.27f, 0.77f, 1), (0.46f, 0.31f, 0)),
        ("HARDWARE_LAB", _) => ((0.82f, 0.87f, 1), (0.5f, 0.89f, 0)),
        ("MINE", _) => ((1, 0.59f, 0), (0.2f, 0.2f, 0.2f)),
        ("MISSILE_EMPLACEMENT", _) => ((1, 0.24f, 0), (0.78f, 0.78f, 0)),
        ("SMELTER", _) => ((0.46f, 0.31f, 0), (1, 0, 0)),
        _ => ((0.1f, 0.1f, 0.1f), (0.15f, 0.07f, 0.05f)),
    };

    /// <summary>A building's three flames, none of a size that does not burn; none for kinds it does not know.</summary>
    public static IReadOnlyList<BuildingFlame> FlamesOf(string type, string? subType, int level) => (type, subType, level) switch
    {
        ("POD", _, _) => [new(0, 0, 29, 10, 10), new(0, 0, 0, 40, 40), new(0, 0, 20, 20, 20)],
        ("GARRISON", _, _) => [new(10, 10, 8, 8, 30), new(-10, 0, 8, 8, 30), new(0, 0, 0, 40, 50)],
        ("SMELTER", _, 1) => [new(12, 0, 6, 15, 18), new(2, 0, 0, 25, 50), new(18.5f, 9.5f, 6, 3, 15)],
        ("SMELTER", _, 3) => [new(0, 0, 0, 36, 36), new(0, 0, 11, 12, 15), new(0, 0, 15, 6, 15)],
        ("MINE", _, 1) => [new(5.6f, -4.6f, 3, 2, 8), new(0, 0, 0, 12, 28), new(5.6f, 4.6f, 3, 2, 8)],
        ("MINE", _, 3) => [new(-1.65f, -10, 3, 5, 30), new(-1.65f, 0, 0, 10, 35), new(-1.65f, 10, 3, 7, 10)],
        ("MINE", _, 5) => [new(-3.5f, 0, 28, 8, 25), new(4.5f, -8, 33, 8, 20), new(0, 0, 0, 40, 50)],
        ("BEACON", _, 1) => [new(0, 0, 13, 2.5f, 10), new(0, 0, 0, 0, 0), new(0, 0, 0, 8, 25)],
        ("BEACON", _, 3) => [new(-1, 0, 22, 5, 8), new(7, 0, 24, 0.5f, 4), new(0, 0, 0, 10, 30)],
        ("FACTORY", "CIVILIAN", 1) => [new(-1.5f, 13.5f, 18, 5, 20), new(0, 0, 7, 20, 40), new(5, -4, 7, 8, 16)],
        ("FACTORY", "CIVILIAN", 3) => [new(0, 0, 9, 26, 26), new(-2.7f, 7, 22, 9, 20), new(10, 10, 8, 10, 20)],
        ("FACTORY", "CIVILIAN", 5) => [new(10, 10, 10, 14, 20), new(1.84f, 2.95f, 0, 50, 60), new(-3, 8, 25, 10, 40)],
        ("FACTORY", "MILITARY", 1) => [new(1, 6, 8, 5, 25), new(6.5f, 0, 3.8f, 13, 20), new(1, -5, 0, 15, 30)],
        ("FACTORY", "MILITARY", 3) => [new(5.5f, 0, 5, 14, 18), new(-3, 0, 0, 20, 45), new(-2, 0, 20, 3, 10)],
        ("FACTORY", "MILITARY", 4) => [new(6.8f, 0, 0, 10, 30), new(20, 0, 14, 14, 16), new(-4, 0, 0, 15, 50)],
        ("FACTORY", "MILITARY", 5) => [new(0, 17, 0, 20, 50), new(0, 0, 7, 30, 40), new(0, -17, 0, 20, 50)],
        ("HARDWARE_LAB", "CIVILIAN", 1) => [new(5, 0, 6, 8, 25), new(0, 0, 0, 20, 30), new(-6, 0, 8, 5, 10)],
        ("HARDWARE_LAB", "CIVILIAN", 3) => [new(12, 0, 7, 8, 16), new(0, 0, 0, 40, 60), new(0, 0, 12, 10, 20)],
        ("HARDWARE_LAB", "MILITARY", 1) => [new(4, 1, 7, 5, 10), new(-4, -1, 7, 8, 20), new(0, 0, 0, 20, 35)],
        ("HARDWARE_LAB", "MILITARY", 3) => [new(-3, -4, 6.6f, 8, 16), new(3, 4, 0, 20, 35), new(0, 0, 0, 16, 32)],
        ("MISSILE_EMPLACEMENT", "TURRET", 1) => [new(0, 0, 1.7f, 1, 2), new(1.8f, 0, 1.4f, 0.2f, 1), new(0, 0, 0, 2.5f, 3)],
        ("MISSILE_EMPLACEMENT", "TURRET", 2) => [new(0, 0, 2, 1.5f, 2), new(1.5f, 1.5f, 2, 0.6f, 3), new(1.5f, -1.5f, 2, 0.6f, 3)],
        ("MISSILE_EMPLACEMENT", "TURRET", 3) => [new(0, 1, 2.5f, 1.5f, 3), new(0, 0, 0, 3, 9), new(0, -1, 2.5f, 1.5f, 3)],
        ("MISSILE_EMPLACEMENT", "SENTRY", 3) => [new(0, -1.6f, 3, 1, 4), new(0, 0, 0, 2, 10), new(0, 1.6f, 3, 1, 4)],
        ("MISSILE_EMPLACEMENT", "SENTRY", 4) => [new(0, -1.7f, 0, 3, 10), new(0, 0, 0.5f, 5, 12), new(0, 1.7f, 0, 3, 15)],
        ("MISSILE_EMPLACEMENT", "LAUNCHER", _) => [new(0, 0, 5, 4, 8), new(0, 0, 0, 8, 18), new(3, 0, 0, 1, 5)],
        ("MISSILE_EMPLACEMENT", "ICBM", _) => [new(7, 0, 5, 10, 10), new(0, 0, 0, 20, 40), new(-15, 0, 0, 5, 10)],
        _ => [],
    };
}
