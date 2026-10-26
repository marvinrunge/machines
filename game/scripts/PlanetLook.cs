using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// Sun, sky and fog from the planet's <c>.env</c> file, following envirnmt's rules: the sun's orbit rises
/// linearly from its minimum to its maximum elevation over half a period and sinks back (EnvOrbit), and
/// colour tables controlled by the sun are looked up by its elevation, interpolating between keys.
/// </summary>
public partial class PlanetLook : Node
{
    private PlanetEnvironment _env = null!;
    private Func<float> _gameTime = null!;
    private Func<float> _viewDistance = null!;
    private DirectionalLight3D _sun = null!;
    private Godot.Environment _environment = null!;
    private ProceduralSkyMaterial _sky = null!;
    private Satellite? _sunSatellite;
    private Orbit? _sunOrbit;
    private (string Horizon, string Top)? _skyTables;
    private string? _uniformSky;

    /// <summary>
    /// The original drew no shadows and could light with a black ambient; the remake casts shadows, so it
    /// never lets the ambient fall below this share of the sunlight.
    /// </summary>
    private const float MinimumAmbient = 0.5f;

    public static PlanetLook Create(PlanetEnvironment env, Func<float> gameTime, Func<float> viewDistance)
    {
        var look = new PlanetLook { Name = "PlanetLook", _env = env, _gameTime = gameTime, _viewDistance = viewDistance };
        look._sunSatellite = env.Satellites.FirstOrDefault(s => s.Name == "sun") ?? env.Satellites.FirstOrDefault();
        look._sunOrbit = look._sunSatellite is { } sat && env.Orbits.TryGetValue(sat.Orbit, out var orbit) ? orbit : null;

        var dynamicSky = env.OtherBlocks.FirstOrDefault(b => b.Kind == "dynamic sky");
        var bands = dynamicSky?.Properties
            .Where(p => p.Key.StartsWith("colour table @", StringComparison.Ordinal) && p.Value.Text is not null)
            .Select(p => (Elevation: float.TryParse(p.Key[(p.Key.IndexOf('@') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : 0, Table: p.Value.Text!))
            .OrderBy(b => b.Elevation)
            .ToList();
        if (bands is { Count: > 0 })
            look._skyTables = (bands[0].Table, bands[^1].Table);
        look._uniformSky = env.OtherBlocks.FirstOrDefault(b => b.Kind == "uniform sky")?.Properties
            .FirstOrDefault(p => p.Key == "colour table").Value?.Text;

        look._sun = new DirectionalLight3D { ShadowEnabled = true, Name = "Sun" };
        look._sky = new ProceduralSkyMaterial { SunAngleMax = 8, GroundCurve = 0.02f };
        look._environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = look._sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            FogEnabled = env.Fog is not null,
            FogMode = Godot.Environment.FogModeEnum.Depth,
            FogDepthCurve = 1.5f,
            FogSkyAffect = 0.6f,
        };
        look.AddChild(look._sun);
        look.AddChild(new WorldEnvironment { Environment = look._environment });
        look.Update();
        return look;
    }

    public override void _Process(double delta) => Update();

    /// <summary>The sun's elevation in degrees at a game time (EnvOrbit's rise-and-set plan).</summary>
    public static float Elevation(Orbit orbit, float time)
    {
        var t = (orbit.StartTime + time) % orbit.Period;
        var half = orbit.Period / 2;
        var phase = t < half ? t / half : 2 - t / half;
        return orbit.MinElevation + (orbit.MaxElevation - orbit.MinElevation) * phase;
    }

    /// <summary>A colour table at a key, interpolated between its entries and held beyond the ends.</summary>
    public static Color Lookup(IReadOnlyList<ColourKey> table, float key)
    {
        if (table.Count == 0)
            return Colors.White;
        var sorted = table.OrderBy(k => k.Key).ToList();
        if (key <= sorted[0].Key)
            return ToColor(sorted[0]);
        for (var i = 1; i < sorted.Count; i++)
        {
            if (key <= sorted[i].Key)
            {
                var f = (key - sorted[i - 1].Key) / (sorted[i].Key - sorted[i - 1].Key);
                return ToColor(sorted[i - 1]).Lerp(ToColor(sorted[i]), f);
            }
        }
        return ToColor(sorted[^1]);
    }

    private static Color ToColor(ColourKey key) => new(key.Colour.X, key.Colour.Y, key.Colour.Z, key.Colour.W);

    private Color Table(string? name, float key, Color fallback) =>
        name is not null && _env.ColourTables.TryGetValue(name, out var table) ? Lookup(table, key) : fallback;

    private void Update()
    {
        var elevation = _sunOrbit is { } orbit ? Elevation(orbit, _gameTime()) : 60;
        var heading = _sunOrbit?.SetHeading ?? 0;

        // The light shines from the sun's position towards the ground.
        _sun.RotationDegrees = new Vector3(-Mathf.Clamp(elevation, 1, 89), 180 - heading, 0);
        var direct = Table(_sunSatellite?.DirectionalColourTable, elevation, Colors.White);
        _sun.LightColor = direct;
        _sun.LightEnergy = elevation > 0 ? 1.1f : 0.15f;
        var ambient = Table(_sunSatellite?.AmbientColourTable, elevation, Colors.Black);
        var floor = direct * MinimumAmbient;
        _environment.AmbientLightColor = new Color(Mathf.Max(ambient.R, floor.R), Mathf.Max(ambient.G, floor.G), Mathf.Max(ambient.B, floor.B));
        _environment.AmbientLightEnergy = 1;

        var fogColour = _env.Fog is { } fog ? Table(fog.ColourTable, elevation, new Color(0.6f, 0.6f, 0.65f)) : new Color(0.6f, 0.6f, 0.65f);
        Color horizon, top;
        if (_skyTables is { } sky)
        {
            horizon = Table(sky.Horizon, elevation, fogColour);
            top = Table(sky.Top, elevation, horizon);
        }
        else
        {
            horizon = top = Table(_uniformSky, elevation, fogColour);
        }
        _sky.SkyHorizonColor = horizon;
        _sky.SkyTopColor = top;
        _sky.GroundHorizonColor = fogColour;
        _sky.GroundBottomColor = fogColour.Darkened(0.3f);

        if (_env.Fog is { } f)
        {
            // MAX VISIBILITY is measured from the viewer in the original's close-up camera. The RTS camera
            // here sits further back, so the fog starts beyond what it looks at.
            var view = _viewDistance();
            _environment.FogLightColor = new Color(fogColour.R, fogColour.G, fogColour.B);
            _environment.FogDepthBegin = view + f.MaxVisibility * 0.25f;
            _environment.FogDepthEnd = view + f.MaxVisibility * 1.5f;
            _environment.FogDensity = Mathf.Clamp(fogColour.A, 0.5f, 1f);
        }
    }
}
