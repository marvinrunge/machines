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
        if (GraphicsSettings.Remake)
            look.Enhance();
        look.AddChild(look._sun);
        look.AddChild(new WorldEnvironment { Environment = look._environment });
        look.Update();
        return look;
    }

    /// <summary>
    /// The remake's look over the original's light: filmic tone mapping (AgX) with a touch more saturation, glow
    /// around the brightest spots (muzzle flashes, explosions, the sun), ambient occlusion where things meet the
    /// ground, and soft shadows that reach as far as the camera looks.
    /// </summary>
    private void Enhance()
    {
        _environment.TonemapMode = Godot.Environment.ToneMapper.Agx;
        _environment.TonemapExposure = 1.0f;
        _environment.AdjustmentEnabled = true;
        _environment.AdjustmentSaturation = 1.1f;
        _environment.AdjustmentContrast = 1.12f;
        _environment.GlowEnabled = true;
        _environment.GlowIntensity = 0.6f;
        _environment.GlowBloom = 0.04f;
        _environment.GlowHdrThreshold = 1.1f;
        _environment.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Softlight;
        // Metres: the machines stand a few metres tall, the buildings ten and more.
        _environment.SsaoEnabled = true;
        _environment.SsaoRadius = 3f;
        _environment.SsaoIntensity = 1.6f;
        _environment.SsaoDetail = 0.6f;
        _sun.LightAngularDistance = 1.2f;
        _sun.ShadowBlur = 1.5f;
        _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        _sun.DirectionalShadowBlendSplits = true;
        _softShadows = true;

        // Smooth surfaces (the water) mirror what is around them.
        _environment.SsrEnabled = true;
        _environment.SsrMaxSteps = 48;

        // Stars come out at night, and the other satellites (the moon) show as discs.
        _sky.SkyCover = Stars.Value;
        _sky.SkyCoverModulate = new Color(1, 1, 1, 0);
        foreach (var satellite in _env.Satellites.Where(s => s != _sunSatellite && _env.Orbits.ContainsKey(s.Orbit)))
        {
            var disc = new DirectionalLight3D { SkyMode = DirectionalLight3D.SkyModeEnum.SkyOnly, LightAngularDistance = 1.6f, Name = satellite.Name };
            AddChild(disc);
            _discs.Add((satellite, disc));
        }
    }

    private readonly List<(Satellite Satellite, DirectionalLight3D Disc)> _discs = [];

    /// <summary>How dark it is, from 0 by day to 1 at night, by the sun's height; lamps follow it.</summary>
    public static float Night { get; private set; }

    /// <summary>A starfield on the sky (equirectangular), the same every time.</summary>
    private static readonly Lazy<Texture2D> Stars = new(() =>
    {
        const int width = 2048, height = 1024;
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var random = new Random(1999);
        for (var i = 0; i < 2600; i++)
        {
            // Spread evenly over the sphere, and only above the horizon.
            var x = random.Next(width);
            var y = (int)(Math.Acos(random.NextDouble()) / Math.PI * height);
            var brightness = (float)Math.Pow(random.NextDouble(), 3) * 0.8f + 0.2f;
            var tint = random.NextDouble() < 0.2 ? new Color(0.8f, 0.85f, 1f) : random.NextDouble() < 0.1 ? new Color(1f, 0.9f, 0.75f) : Colors.White;
            image.SetPixel(x, Math.Clamp(y, 0, height - 1), new Color(tint.R * brightness, tint.G * brightness, tint.B * brightness));
        }
        return ImageTexture.CreateFromImage(image);
    });

    private bool _softShadows;

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
        // Every satellite adds its ambient light by its own height in the sky: the sun by day, the moon by night.
        var ambient = Colors.Black;
        foreach (var satellite in _env.Satellites)
        {
            if (satellite.AmbientColourTable is null || !_env.Orbits.TryGetValue(satellite.Orbit, out var satelliteOrbit))
                continue;
            var light = Table(satellite.AmbientColourTable, Elevation(satelliteOrbit, _gameTime()), Colors.Black);
            ambient = new Color(ambient.R + light.R, ambient.G + light.G, ambient.B + light.B);
        }
        var floor = direct * MinimumAmbient;
        // The remake never lets a night go fully black: a little starlight stays.
        if (_softShadows)
            floor = new Color(Mathf.Max(floor.R, 0.05f), Mathf.Max(floor.G, 0.06f), Mathf.Max(floor.B, 0.1f));
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

        Night = Mathf.Clamp((4 - elevation) / 6, 0, 1);
        if (_discs.Count > 0)
        {
            _sky.SkyCoverModulate = new Color(1, 1, 1, Night);
            foreach (var (satellite, disc) in _discs)
            {
                var discOrbit = _env.Orbits[satellite.Orbit];
                var height = Elevation(discOrbit, _gameTime());
                var colour = Table(satellite.MeshColourTable, height, Colors.White);
                disc.RotationDegrees = new Vector3(-Mathf.Clamp(height, 1, 89), 180 - (discOrbit.SetHeading ?? 0), 0);
                disc.LightColor = new Color(colour.R, colour.G, colour.B);
                disc.LightEnergy = height > 0 ? colour.A * 1.5f : 0;
            }
        }
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

        // Shadows reach a little beyond what the camera looks at, so they stay sharp up close.
        if (_softShadows)
            _sun.DirectionalShadowMaxDistance = Mathf.Clamp(_viewDistance() * 3f, 120, 700);
    }
}
