namespace Machines.Sim.World;

/// <summary>
/// The machines' bodies as the game draws them, for what hits them and what a machine driven in first person runs into
/// (GameWorld.Bodies), where the original has a machine stand in its circle (<see cref="Actor.Radius"/>, its
/// HIGH_CLEARANCE, at least 1.5 m) and be hit in a square box of that radius, as tall as twice its aim point. Matches whose
/// options say <see cref="MatchOptions.FittedBodies"/> use these; recordings from before keep the circles.
/// <para>
/// A body is a box along the machine's facing: how far it reaches forward and back from the machine's middle, half its
/// width and its height, measured from the models' vertices in their rest pose (glow sprites left out) by
/// <c>MACHINES_STUDIO_HITBOX=numbers</c> (game/scripts/Studio.Hitbox.cs): for each type and level the largest over every
/// loadout its exports carry, the Wired heroes' bare chassis and all three looks (classic, overhauled and HD), rounded up to
/// 5 cm. Every client plays the same simulation whatever look it shows, so a body covers them all. A machine's own weapons
/// count, on its sides and on top. Flyers keep their box in the air, and a type and level without a body here keeps its circle.
/// </para>
/// <para>
/// Which doors, ducts and entrances a machine fits through (its size class, <see cref="Actor.Radius"/> against the door),
/// how far weapons reach (to the circle's edge), where machines are sent to stand (formations, free places, the spots
/// they attack, heal or build from) and how machines on orders keep out of one another (the original's motion sequencer)
/// still go by the circle. A body decides which rounds meet a machine, and where one driven in first person stops.
/// </para>
/// </summary>
public static class MachineBodies
{
    /// <summary>A machine's body in metres from its middle: forward and back along its facing, half its width, its height.</summary>
    public readonly record struct Body(float Fore, float Aft, float Half, float Height)
    {
        /// <summary>Half its length.</summary>
        public float HalfLength => (Fore + Aft) / 2;

        /// <summary>How far its middle lies ahead of the machine's.</summary>
        public float Ahead => (Fore - Aft) / 2;

        /// <summary>The farthest its corners reach from the machine's middle.</summary>
        public float Reach => MathF.Sqrt(MathF.Max(Fore, Aft) * MathF.Max(Fore, Aft) + Half * Half);
    }

    // Measured 2026-11-22, classic / overhauled / HD: length x width x height of the largest model of each look, then the body: fore, aft, half width, height.
    private static readonly Dictionary<(string Type, string? SubType, int Level), Body> Bodies = new()
    {
        // BOSS 1: flies, keeps its box in the air.
        // BOSS 2: flies, keeps its box in the air.
        // BOSS 3: flies, keeps its box in the air.
        // COMMANDER 4 (radius 1.63, 4 models): classic 3.29 x 4.87 x 6.39, overhauled 3.29 x 4.89 x 6.39, HD 3.19 x 4.40 x 6.24 m.
        [("ADMINISTRATOR", "COMMANDER", 4)] = new(1.95f, 1.45f, 2.55f, 6.40f),
        // COMMANDER 5 (radius 2.00, 3 models): classic 3.74 x 6.34 x 9.15, overhauled 3.74 x 6.32 x 9.15, HD 3.43 x 6.33 x 8.97 m.
        [("ADMINISTRATOR", "COMMANDER", 5)] = new(1.95f, 1.85f, 3.20f, 9.20f),
        // COMMANDER 6 (radius 2.50, 2 models): classic 4.47 x 9.80 x 11.44, overhauled 4.47 x 9.80 x 11.44, HD 4.47 x 9.80 x 11.44 m.
        [("ADMINISTRATOR", "COMMANDER", 6)] = new(2.25f, 2.30f, 4.95f, 11.45f),
        // OVERSEER 2 (radius 2.06, 3 models): classic 4.01 x 5.44 x 4.86, overhauled 4.01 x 5.44 x 4.86, HD 3.87 x 4.70 x 4.92 m.
        [("ADMINISTRATOR", "OVERSEER", 2)] = new(2.65f, 1.40f, 2.75f, 4.95f),
        // OVERSEER 3 (radius 2.40, 3 models): classic 5.22 x 5.94 x 5.89, overhauled 5.09 x 5.84 x 5.89, HD 5.01 x 5.69 x 5.81 m.
        [("ADMINISTRATOR", "OVERSEER", 3)] = new(2.65f, 2.65f, 3.05f, 5.90f),
        // ASSASSIN 2 (radius 2.40, 3 models): classic 5.14 x 4.06 x 6.00, overhauled 5.14 x 4.05 x 6.00, HD 5.14 x 4.14 x 5.80 m.
        [("AGGRESSOR", "ASSASSIN", 2)] = new(2.65f, 2.55f, 2.20f, 6.00f),
        // ASSASSIN 3 (radius 2.40, 3 models): classic 5.14 x 4.20 x 4.76, overhauled 5.14 x 4.20 x 4.76, HD 5.14 x 4.33 x 4.70 m.
        [("AGGRESSOR", "ASSASSIN", 3)] = new(2.65f, 2.55f, 2.20f, 4.80f),
        // ASSASSIN 4 (radius 2.88, 2 models): classic 8.02 x 6.29 x 6.53, overhauled 8.02 x 6.29 x 6.53, HD 8.02 x 6.29 x 6.53 m.
        [("AGGRESSOR", "ASSASSIN", 4)] = new(4.05f, 4.00f, 3.20f, 6.55f),
        // ASSASSIN 5 (radius 3.48, 2 models): classic 8.69 x 6.56 x 8.14, overhauled 8.69 x 6.56 x 8.14, HD 8.69 x 6.56 x 8.14 m.
        [("AGGRESSOR", "ASSASSIN", 5)] = new(4.40f, 4.30f, 3.30f, 8.15f),
        // ASSASSIN 6 (radius 2.88, 2 models): classic 9.78 x 9.68 x 6.79, overhauled 9.78 x 9.68 x 6.79, HD 9.78 x 9.68 x 6.79 m.
        [("AGGRESSOR", "ASSASSIN", 6)] = new(4.35f, 5.45f, 4.90f, 6.80f),
        // ASSASSIN 9: no such machine in the data, left out.
        // BALLISTA 3 (radius 1.50, 3 models): classic 4.50 x 2.99 x 4.58, overhauled 4.24 x 2.99 x 4.58, HD 4.24 x 2.99 x 4.58 m.
        [("AGGRESSOR", "BALLISTA", 3)] = new(2.70f, 2.35f, 1.50f, 4.60f),
        // BALLISTA 4 (radius 3.60, 2 models): classic 10.01 x 9.65 x 10.04, overhauled 10.01 x 9.50 x 9.97, HD 9.90 x 9.65 x 10.04 m.
        [("AGGRESSOR", "BALLISTA", 4)] = new(6.40f, 3.70f, 4.85f, 10.05f),
        // BALLISTA 5 (radius 4.50, 2 models): classic 13.75 x 21.96 x 13.60, overhauled 13.75 x 21.96 x 13.60, HD 13.75 x 21.96 x 13.60 m.
        [("AGGRESSOR", "BALLISTA", 5)] = new(8.40f, 5.40f, 11.00f, 13.65f),
        // GRUNT 1 (radius 1.50, 3 models): classic 3.02 x 3.13 x 2.28, overhauled 2.96 x 3.13 x 2.28, HD 2.73 x 3.13 x 2.24 m.
        [("AGGRESSOR", "GRUNT", 1)] = new(1.25f, 1.80f, 1.60f, 2.30f),
        // GRUNT 2 (radius 1.60, 4 models): classic 3.40 x 4.18 x 2.61, overhauled 3.40 x 4.18 x 2.61, HD 3.02 x 3.27 x 2.55 m.
        [("AGGRESSOR", "GRUNT", 2)] = new(1.60f, 1.85f, 2.15f, 2.65f),
        // GRUNT 3 (radius 2.06, 2 models): classic 3.60 x 5.59 x 3.29, overhauled 3.60 x 5.59 x 3.28, HD 3.37 x 5.15 x 3.28 m.
        [("AGGRESSOR", "GRUNT", 3)] = new(1.70f, 1.95f, 2.80f, 3.30f),
        // GRUNT 4 (radius 2.37, 2 models): classic 4.39 x 6.43 x 4.20, overhauled 4.39 x 6.43 x 4.20, HD 4.39 x 6.43 x 4.20 m.
        [("AGGRESSOR", "GRUNT", 4)] = new(1.95f, 2.50f, 3.25f, 4.25f),
        // GRUNT 5 (radius 3.09, 2 models): classic 6.98 x 8.34 x 5.30, overhauled 6.98 x 8.34 x 5.30, HD 6.98 x 8.34 x 5.30 m.
        [("AGGRESSOR", "GRUNT", 5)] = new(4.20f, 2.80f, 4.25f, 5.35f),
        // KNIGHT 3 (radius 2.00, 3 models): classic 4.11 x 5.54 x 5.26, overhauled 4.08 x 5.54 x 5.26, HD 4.15 x 5.25 x 5.26 m.
        [("AGGRESSOR", "KNIGHT", 3)] = new(2.50f, 1.70f, 2.80f, 5.30f),
        // KNIGHT 4 (radius 2.06, 3 models): classic 3.73 x 6.94 x 8.07, overhauled 3.67 x 6.94 x 8.07, HD 3.67 x 6.82 x 7.83 m.
        [("AGGRESSOR", "KNIGHT", 4)] = new(1.75f, 2.05f, 3.50f, 8.10f),
        // KNIGHT 5 (radius 2.20, 3 models): classic 4.33 x 6.87 x 6.43, overhauled 4.29 x 6.78 x 6.43, HD 4.28 x 6.31 x 6.30 m.
        [("AGGRESSOR", "KNIGHT", 5)] = new(2.10f, 2.25f, 3.45f, 6.45f),
        // NINJA 5 (radius 6.00, 2 models): classic 8.27 x 17.61 x 14.09, overhauled 8.16 x 17.39 x 14.09, HD 7.58 x 17.43 x 14.13 m.
        [("AGGRESSOR", "NINJA", 5)] = new(3.95f, 4.45f, 8.85f, 14.15f),
        // APC 2 (radius 6.13, 1 model): classic 15.63 x 10.79 x 5.57, overhauled 15.61 x 10.79 x 5.57, HD 13.36 x 9.72 x 5.21 m.
        [("APC", null, 2)] = new(7.85f, 7.85f, 5.45f, 5.60f),
        // APC 4: flies, keeps its box in the air.
        // APC 5: flies, keeps its box in the air.
        // BEHEMOTH 4 (radius 7.20, 3 models): classic 19.59 x 12.27 x 8.16, overhauled 19.59 x 12.27 x 8.16, HD 19.59 x 12.27 x 8.16 m.
        [("CONSTRUCTOR", "BEHEMOTH", 4)] = new(10.50f, 9.15f, 6.20f, 8.20f),
        // BEHEMOTH 5 (radius 8.80, 1 model): classic 24.17 x 15.97 x 10.42, overhauled 24.17 x 15.97 x 10.33, HD 20.55 x 15.30 x 10.33 m.
        [("CONSTRUCTOR", "BEHEMOTH", 5)] = new(13.15f, 11.10f, 8.00f, 10.45f),
        // BEHEMOTH 6 (radius 8.80, 2 models): classic 24.17 x 15.97 x 13.51, overhauled 24.17 x 15.97 x 13.51, HD 24.17 x 15.97 x 13.51 m.
        [("CONSTRUCTOR", "BEHEMOTH", 6)] = new(13.15f, 11.10f, 8.00f, 13.55f),
        // BUILDER 3 (radius 2.53, 3 models): classic 7.92 x 5.47 x 5.99, overhauled 7.92 x 5.47 x 5.99, HD 7.92 x 5.47 x 5.99 m.
        [("CONSTRUCTOR", "BUILDER", 3)] = new(4.45f, 3.50f, 2.80f, 6.00f),
        // DOZER 1 (radius 2.53, 3 models): classic 7.52 x 6.38 x 4.13, overhauled 7.52 x 6.38 x 4.19, HD 7.52 x 6.38 x 4.13 m.
        [("CONSTRUCTOR", "DOZER", 1)] = new(4.45f, 3.10f, 3.20f, 4.20f),
        // GEO_LOCATOR 1 (radius 1.50, 3 models): classic 3.37 x 2.72 x 2.18, overhauled 3.42 x 2.72 x 2.18, HD 3.42 x 2.72 x 3.01 m.
        [("GEO_LOCATOR", null, 1)] = new(1.85f, 1.65f, 1.40f, 3.05f),
        // GEO_LOCATOR 2 (radius 3.20, 1 model): classic 4.75 x 8.25 x 4.87, overhauled 4.75 x 8.25 x 4.87, HD 4.93 x 7.19 x 4.63 m.
        [("GEO_LOCATOR", null, 2)] = new(2.45f, 2.50f, 4.15f, 4.90f),
        // GEO_LOCATOR 3: flies, keeps its box in the air.
        // RESOURCE_CARRIER 1 (radius 1.66, 1 model): classic 4.13 x 3.77 x 2.40, overhauled 4.11 x 3.77 x 2.40, HD 4.00 x 3.77 x 2.30 m.
        [("RESOURCE_CARRIER", null, 1)] = new(2.20f, 2.00f, 1.90f, 2.40f),
        // RESOURCE_CARRIER 2 (radius 3.26, 1 model): classic 6.89 x 7.50 x 4.18, overhauled 6.87 x 7.50 x 4.18, HD 7.30 x 5.78 x 4.85 m.
        [("RESOURCE_CARRIER", null, 2)] = new(3.65f, 3.70f, 3.80f, 4.90f),
        // RESOURCE_CARRIER 3 (radius 3.26, 1 model): classic 5.98 x 7.25 x 4.54, overhauled 5.86 x 7.18 x 4.54, HD 5.72 x 6.88 x 4.48 m.
        [("RESOURCE_CARRIER", null, 3)] = new(3.00f, 3.00f, 3.65f, 4.55f),
        // RESOURCE_CARRIER 4 (radius 7.90, 1 model): classic 12.01 x 17.19 x 6.60, overhauled 11.91 x 17.19 x 6.60, HD 11.59 x 16.97 x 6.42 m.
        [("RESOURCE_CARRIER", null, 4)] = new(6.05f, 6.05f, 8.75f, 6.65f),
        // RESOURCE_CARRIER 5 (radius 6.06, 1 model): classic 14.72 x 12.76 x 8.78, overhauled 14.67 x 12.76 x 8.78, HD 13.50 x 10.51 x 10.13 m.
        [("RESOURCE_CARRIER", null, 5)] = new(7.40f, 7.40f, 6.45f, 10.15f),
        // SPY_LOCATOR 3 (radius 1.50, 3 models): classic 1.47 x 3.30 x 4.06, overhauled 1.47 x 3.30 x 4.06, HD 1.47 x 3.30 x 4.06 m.
        [("SPY_LOCATOR", null, 3)] = new(0.65f, 0.95f, 1.80f, 4.10f),
        // SPY_LOCATOR 5 (radius 1.50, 3 models): classic 1.19 x 3.03 x 3.56, overhauled 1.19 x 3.03 x 3.56, HD 1.19 x 3.03 x 3.65 m.
        [("SPY_LOCATOR", null, 5)] = new(0.50f, 0.75f, 1.65f, 3.65f),
        // SPY_LOCATOR 6 (radius 2.40, 2 models): classic 5.04 x 9.45 x 10.39, overhauled 5.04 x 9.45 x 10.39, HD 5.04 x 9.45 x 10.39 m.
        [("SPY_LOCATOR", null, 6)] = new(2.30f, 2.80f, 5.00f, 10.40f),
        // BRAIN_BOX 5 (radius 1.50, 1 model): classic 4.34 x 3.77 x 4.33, overhauled 4.33 x 3.77 x 4.33, HD 3.93 x 3.79 x 4.34 m.
        [("TECHNICIAN", "BRAIN_BOX", 5)] = new(3.20f, 1.20f, 1.95f, 4.35f),
        // LAB_TECH 2 (radius 1.50, 1 model): classic 3.45 x 2.36 x 3.31, overhauled 3.40 x 2.36 x 3.29, HD 3.38 x 2.36 x 3.62 m.
        [("TECHNICIAN", "LAB_TECH", 2)] = new(2.35f, 1.15f, 1.20f, 3.65f),
        // TECH_BOY 3 (radius 1.50, 1 model): classic 3.67 x 2.85 x 3.18, overhauled 3.67 x 2.85 x 3.18, HD 3.29 x 2.43 x 3.65 m.
        [("TECHNICIAN", "TECH_BOY", 3)] = new(2.65f, 1.05f, 1.45f, 3.70f),
    };

    /// <summary>The body of a machine of this type and level, or null where none was measured (a flyer's, or one no look has a model of).</summary>
    public static Body? Of(string type, string? subType, int level) =>
        Bodies.TryGetValue((type, subType, level), out var body) ? body : null;

    /// <summary>The bodies measured, for the tests.</summary>
    public static IEnumerable<((string Type, string? SubType, int Level) Machine, Body Body)> All => Bodies.Select(p => (p.Key, p.Value));
}
