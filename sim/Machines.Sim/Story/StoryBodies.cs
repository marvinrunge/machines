namespace Machines.Sim.Story;

/// <summary>
/// The machines' bodies as the story draws them (the overhauled and HD models; the story never shows the classic look),
/// for what runs into them and what hits them while a story is played (GameWorld.StoryBodies). Everywhere else a machine
/// stands in its circle (<see cref="World.Actor.Radius"/>, the original's HIGH_CLEARANCE, at least 1.5 m) and is hit in a
/// square box of that radius, as tall as twice its aim point: that is the original's own, and skirmish, campaign, Wired
/// and battles keep it.
/// <para>
/// A body is a box along the machine's facing: how far it reaches forward and back from the machine's middle, half its
/// width and its height, measured from the models' vertices in their rest pose (glow sprites left out) by
/// <c>MACHINES_STUDIO_HITBOX=1</c> (game/scripts/Studio.Hitbox.cs), the larger of the two looks, rounded up to 5 cm. A
/// Grunt 1 sits behind its middle (0.99 m forward, 1.73 m back) and stands 1.8 m tall, where its circle reached 1.5 m
/// all round and its box 2.4 m up; a Reaper's head stands up to 6 m, over a box of 2.9 m. Weapons count where they stick
/// out of the hull sideways (the Reapers' and the Commander's guns), not where they stand on top (the Locator's Bolter).
/// </para>
/// <para>
/// The size classes the ship's doors and ducts let through (<see cref="Navigation.Mobility"/>) still go by the circle, so a
/// body changes nothing about which machine gets where: only how near machines come to one another and which rounds meet them.
/// </para>
/// </summary>
public static class StoryBodies
{
    /// <summary>A machine's body in metres from its middle: forward and back along its facing, half its width, its height.</summary>
    public readonly record struct Body(float Fore, float Aft, float Half, float Height)
    {
        /// <summary>Half its length, and how far its middle lies ahead of the machine's.</summary>
        public float HalfLength => (Fore + Aft) / 2;

        public float Ahead => (Fore - Aft) / 2;

        /// <summary>The farthest its corners reach from the machine's middle.</summary>
        public float Reach => MathF.Sqrt(MathF.Max(Fore, Aft) * MathF.Max(Fore, Aft) + Half * Half);
    }

    // Measured 2026-11-22 (overhauled / HD): fore, aft, half width, height.
    private static readonly Dictionary<(string Type, string? SubType, int Level), Body> Bodies = new()
    {
        // L-7 (hero geo1): 3.37 x 2.72 x 2.18 m; its Bolter on top reaches 2.9 m.
        [("GEO_LOCATOR", null, 1)] = new(1.75f, 1.65f, 1.40f, 2.20f),
        // Ike and Bob (hero grunt1) and the workshop's Grunts: 2.71 x 3.13 m, 1.75 m tall unarmed, 2.28 m with the Bolter raised.
        [("AGGRESSOR", "GRUNT", 1)] = new(1.00f, 1.75f, 1.60f, 1.80f),
        // Grunt 2: 2.78 x 2.90 m (HD), 2.42 x 4.18 m (overhauled), 2.6 m tall.
        [("AGGRESSOR", "GRUNT", 2)] = new(1.25f, 1.60f, 2.15f, 2.60f),
        // Grunt 3: 3.60 x 5.59 m, 3.3 m tall.
        [("AGGRESSOR", "GRUNT", 3)] = new(1.70f, 1.95f, 2.80f, 3.30f),
        // Reaper 2: 5.14 x 4.14 m, its head 6.0 m up; Reaper 3 the same legs, 4.8 m.
        [("AGGRESSOR", "ASSASSIN", 2)] = new(2.65f, 2.55f, 2.20f, 6.00f),
        [("AGGRESSOR", "ASSASSIN", 3)] = new(2.65f, 2.55f, 2.20f, 4.80f),
        // The Thresher (Reaper 4): 8.02 x 6.29 x 6.53 m.
        [("AGGRESSOR", "ASSASSIN", 4)] = new(4.05f, 4.00f, 3.20f, 6.55f),
        // Commander 5: 3.58 x 6.33 m (its cannons out at the sides), 9.15 m tall.
        [("ADMINISTRATOR", "COMMANDER", 5)] = new(1.80f, 1.80f, 3.20f, 9.15f),
    };

    /// <summary>The body of a machine of this type and level as the story draws it, or null where none was measured.</summary>
    public static Body? Of(string type, string? subType, int level) =>
        Bodies.TryGetValue((type, subType, level), out var body) ? body : null;

    /// <summary>The bodies measured, for the tests.</summary>
    public static IEnumerable<((string Type, string? SubType, int Level) Machine, Body Body)> All => Bodies.Select(p => (p.Key, p.Value));
}
