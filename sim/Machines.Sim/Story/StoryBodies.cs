using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// The story's own bodies for the few machines of its cast whose model it draws smaller than the general table
/// (<see cref="MachineBodies"/>) has them. Every machine stands and is hit in its fitted body in every mode
/// (GameWorld.Bodies); the general table takes for each type and level the largest of every loadout and all three looks,
/// the classic one too, and counts weapons standing on top. The story never shows the classic look, and its Locator and
/// Grunts carry their Bolters on top, so for these it keeps the bodies measured from the overhauled and HD models alone,
/// weapons counted only where they stick out of the hull sideways (the Commander's cannons), not on top. Every other
/// machine in a story has the general body.
/// <para>
/// The size classes the ship's doors and ducts let through (<see cref="Navigation.Mobility"/>) still go by the circle, so a
/// body changes nothing about which machine gets where: only how near machines come to one another and which rounds meet them.
/// </para>
/// </summary>
public static class StoryBodies
{
    // Measured 2026-11-22 (overhauled / HD): fore, aft, half width, height.
    private static readonly Dictionary<(string Type, string? SubType, int Level), MachineBodies.Body> Bodies = new()
    {
        // L-7 (hero geo1): 3.37 x 2.72 x 2.18 m; its Bolter on top reaches 2.9 m (the general table: 3.05 m, the HD model).
        [("GEO_LOCATOR", null, 1)] = new(1.75f, 1.65f, 1.40f, 2.20f),
        // Ike and Bob (hero grunt1) and the workshop's Grunts: 2.71 x 3.13 m, 1.75 m tall unarmed, 2.28 m with the Bolter
        // raised (the general table counts the Bolter: 1.25 m ahead, 2.30 m tall).
        [("AGGRESSOR", "GRUNT", 1)] = new(1.00f, 1.75f, 1.60f, 1.80f),
        // Grunt 2: 2.78 x 2.90 m (HD), 2.42 x 4.18 m (overhauled), 2.6 m tall.
        [("AGGRESSOR", "GRUNT", 2)] = new(1.25f, 1.60f, 2.15f, 2.60f),
        // Commander 5: 3.58 x 6.33 m (its cannons out at the sides), 9.15 m tall.
        [("ADMINISTRATOR", "COMMANDER", 5)] = new(1.80f, 1.80f, 3.20f, 9.15f),
    };

    /// <summary>The story's own body of a machine of this type and level, or null where it has the general one (<see cref="MachineBodies"/>).</summary>
    public static MachineBodies.Body? Own(string type, string? subType, int level) =>
        Bodies.TryGetValue((type, subType, level), out var body) ? body : null;

    /// <summary>The body of a machine of this type and level in a story: its own, or the general one.</summary>
    public static MachineBodies.Body? Of(string type, string? subType, int level) =>
        Own(type, subType, level) ?? MachineBodies.Of(type, subType, level);

    /// <summary>The story's own bodies, for the tests.</summary>
    public static IEnumerable<((string Type, string? SubType, int Level) Machine, MachineBodies.Body Body)> All => Bodies.Select(p => (p.Key, p.Value));
}
