using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- night (W4dEnvironment, MachLogMachine::perceivedDarkness) -----------------------------------
    //
    // How dark it is follows from the planet's satellites at the game's own time, the same on every machine. Darkness
    // cuts a machine's scanner by up to half and spoils every aim it takes afresh; what has night vision sees as by
    // day, and a commander lends his to his squad, the more the nearer he is.

    /// <summary>The illumination at or below which it counts as night, and at or above which as day, unless a planet says (W4dEnvironment).</summary>
    private const float NightThreshold = 0.25f, DayThreshold = 0.35f;

    /// <summary>How dark it is, from 0 by day to 1 at night; it is worked out once a second.</summary>
    public float Darkness { get; set; }

    private void UpdateDarkness()
    {
        if (Tick % TicksPerSecond == 0)
            Darkness = DarknessAt(Planet.Environment, Time);
    }

    /// <summary>W4dEnvironment::darkness at a game time. A world without any satellites (only a test has one) knows no night.</summary>
    public static float DarknessAt(PlanetEnvironment environment, float time)
    {
        if (environment.Satellites.Count == 0)
            return 0;
        var (night, day) = (NightThreshold, DayThreshold);
        if (environment.OtherBlocks.FirstOrDefault(b => b.Kind == "day phase") is { } phase)
        {
            float? Threshold(string name) => phase.Properties.FirstOrDefault(p => p.Key == name).Value?.Number;
            (night, day) = (Threshold("night threshold") ?? night, Threshold("day threshold") ?? day);
        }
        var light = Illumination(environment, time);
        return light >= day ? 0 : light <= night ? 1 : 1 - (light - night) / (day - night);
    }

    /// <summary>W4dEnvironment::averageIllumination: the satellites' ambient and directional light, red, green and blue together, over three.</summary>
    private static float Illumination(PlanetEnvironment environment, float time)
    {
        var sum = 0f;
        foreach (var satellite in environment.Satellites)
        {
            if (!environment.Orbits.TryGetValue(satellite.Orbit, out var orbit))
                continue;
            var elevation = Elevation(orbit, time);
            foreach (var name in new[] { satellite.AmbientColourTable, satellite.DirectionalColourTable })
            {
                if (name is not null && environment.ColourTables.TryGetValue(name, out var table) && table.Count > 0)
                {
                    var colour = ColourAt(table, elevation);
                    sum += colour.X + colour.Y + colour.Z;
                }
            }
        }
        return sum / 3;
    }

    /// <summary>A satellite's elevation in degrees at a game time (EnvOrbit): up to its highest over the first half of the period, and down again.</summary>
    public static float Elevation(Orbit orbit, float time)
    {
        var period = MathF.Abs(orbit.Period);
        if (period == 0)
            return orbit.MinElevation;
        var t = (orbit.StartTime + time) % period;
        var half = period / 2;
        var phase = t < half ? t / half : 2 - t / half;
        return orbit.MinElevation + (orbit.MaxElevation - orbit.MinElevation) * phase;
    }

    /// <summary>EnvElevationColourTable::colour: between the keys in a straight line, beyond them as at the last one.</summary>
    private static Vector4 ColourAt(IReadOnlyList<ColourKey> table, float elevation)
    {
        var (below, above) = ((ColourKey?)null, (ColourKey?)null);
        foreach (var key in table)
        {
            if (key.Key <= elevation && (below is null || key.Key > below.Key))
                below = key;
            if (key.Key >= elevation && (above is null || key.Key < above.Key))
                above = key;
        }
        if (below is null || above is null || below.Key == above.Key)
            return (below ?? above!).Colour;
        return Vector4.Lerp(below.Colour, above.Colour, (elevation - below.Key) / (above.Key - below.Key));
    }

    /// <summary>How dark it is to a machine (MachLogMachine::perceivedDarkness).</summary>
    public float PerceivedDarkness(Machine machine)
    {
        if (machine.Stats.NightVision || Darkness <= 0)
            return 0;
        return machine.Squad is { } squad && CommanderOf(machine.Race, squad) is { } commander
            ? MathF.Min(Darkness, Vector2.Distance(machine.Position, commander.Position) / 200)
            : Darkness;
    }

    /// <summary>How far a machine's scanner reaches as things stand (MachLogMachine::effectiveScannerRange, lightLevelEfficiency).</summary>
    public float ScannerReach(Machine machine) => machine.Stats.ScannerRange * (2 - PerceivedDarkness(machine)) / 2;
}
