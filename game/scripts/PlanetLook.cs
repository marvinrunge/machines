using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// Sun, sky and fog from the planet's <c>.env</c> file, following envirnmt's rules: the sun's orbit rises
/// linearly from its minimum to its maximum elevation over half a period and sinks back (EnvOrbit), and
/// colour tables controlled by the sun are looked up by its elevation, interpolating between keys. In the remake
/// look, planets with a day sky show a photographed one tinted to it (<see cref="PhotoSky"/>).
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

    /// <summary>The remake's darker base light: the ambient's floor, its share of the planet's own, and the sun.</summary>
    private const float RemakeMinimumAmbient = 0.2f;
    private const float RemakeAmbient = 0.5f;
    private const float RemakeSun = 1.6f;

    /// <summary>
    /// The volumetric fog's density by day, at most (it thickens towards dawn, dusk and night): a share of the light is
    /// lost over the planet's MAX VISIBILITY (<see cref="HazeReach"/> of e-foldings), but never less than the minimum.
    /// </summary>
    private const float HazeDensity = 0.004f, MinimumHaze = 0.0006f, HazeReach = 0.6f;

    public static PlanetLook Create(PlanetEnvironment env, string planet, Func<float> gameTime, Func<float> viewDistance,
        Func<string, ClassicAssets.SkySprite?> skySprite, (Mesh Mesh, Texture2D Texture, float Radius)? dome = null)
    {
        var look = new PlanetLook { Name = "PlanetLook", _env = env, _planet = planet, _gameTime = gameTime, _viewDistance = viewDistance };
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
        // A static sky has one table for all of its dome.
        if (bands is not { Count: > 0 } && env.OtherBlocks.FirstOrDefault(b => b.Kind == "static sky")?.Properties
                .FirstOrDefault(p => p.Key == "colour table").Value?.Text is { } staticTable)
            bands = [(0f, staticTable)];
        look._bands = bands ?? [];
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
        // The remake shows a photographed sky where the original's is a day sky (PhotoSky), in place of its dome; it
        // also lights the scene a little: reflections, and part of the ambient light.
        if (GraphicsSettings.Remake && look._sunOrbit is { } sunOrbit
            && PhotoSky.For(planet, Mathf.Min(sunOrbit.MinElevation, sunOrbit.MaxElevation), Mathf.Max(sunOrbit.MinElevation, sunOrbit.MaxElevation), dome?.Texture) is { } photo)
        {
            look._photo = photo;
            look._environment.Sky = new Sky { SkyMaterial = photo.Material, ProcessMode = Sky.ProcessModeEnum.Incremental };
            look._environment.AmbientLightSource = Godot.Environment.AmbientSource.Bg;
            look._environment.AmbientLightSkyContribution = PhotoSky.AmbientShare;
            look._environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Bg;
            dome = null;
        }
        // The satellites' own pictures (the sun, moons and planets), as the original drew them in its sky.
        foreach (var satellite in env.Satellites.Where(s => env.Orbits.ContainsKey(s.Orbit)))
        {
            if (skySprite(satellite.Name) is not { } sprite)
                continue;
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = sprite.Texture,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
                DisableFog = true,
                DisableReceiveShadows = true,
            };
            var quad = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = Vector2.One },
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Name = satellite.Name,
            };
            look.AddChild(quad);
            look._sprites.Add((satellite, sprite, quad, material));
            if (satellite == look._sunSatellite)
                look._sun.SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly;
        }

        // The original's sky dome: its own texture, tinted vertex by vertex from the bands' colours.
        if (dome is { } sky && look._bands.Count > 0)
        {
            look._dome = new ShaderMaterial { Shader = DomeShader.Value, RenderPriority = -10 };
            look._dome.SetShaderParameter("sky_texture", sky.Texture);
            look._domeView = new MeshInstance3D
            {
                Mesh = sky.Mesh,
                MaterialOverride = look._dome,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Name = "SkyDome",
            };
            look._domeRadius = sky.Radius;
            look.AddChild(look._domeView);
        }

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

        // Light bounces between nearby surfaces: sunlit sand warms the shade beside it, a red hull tints the ground.
        _environment.SsilEnabled = true;
        _environment.SsilRadius = 6;
        _environment.SsilIntensity = 1.3f;
        _environment.SsilSharpness = 0.9f;

        // Air the light shines through, in the planet's fog colour: shafts where cliffs and buildings shade the
        // sun, haloes round lamps and fire at night (see Update). Airless planets have none.
        if (_env.Fog is not null)
        {
            _environment.VolumetricFogEnabled = true;
            _environment.VolumetricFogAnisotropy = 0.6f;
            _environment.VolumetricFogLength = 320;
            _environment.VolumetricFogDetailSpread = 2;
            _environment.VolumetricFogSkyAffect = 0;
            _environment.VolumetricFogTemporalReprojectionEnabled = true;
        }

        // Stars come out at night (a painted night sky when there is one for the planet), and the other
        // satellites without a picture of their own show as discs.
        _sky.SkyCover = NightSky(_planet) ?? Stars.Value;
        _sky.SkyCoverModulate = new Color(1, 1, 1, 0);
        _photo?.SetStars(_sky.SkyCover);
        foreach (var satellite in _env.Satellites.Where(s => s != _sunSatellite && _env.Orbits.ContainsKey(s.Orbit) && _sprites.All(p => p.Satellite != s)))
        {
            var disc = new DirectionalLight3D { SkyMode = DirectionalLight3D.SkyModeEnum.SkyOnly, LightAngularDistance = 1.6f, Name = satellite.Name };
            AddChild(disc);
            _discs.Add((satellite, disc));
        }
    }

    private readonly List<(Satellite Satellite, DirectionalLight3D Disc)> _discs = [];
    private PhotoSky? _photo;

    // The dynamic sky's colour tables by the dome height they apply from (EnvDynamicSky::findVertexColour).
    private List<(float Elevation, string Table)> _bands = [];
    private ShaderMaterial? _dome;
    private MeshInstance3D? _domeView;
    private float _domeRadius = 1;
    private const int MostBands = 8;

    /// <summary>
    /// The sky dome: each vertex takes the colour of the bands for its height above the horizon, as the
    /// original coloured them, and the texture is lit by it. It is added over a black background, so the
    /// stars behind it show through as the sky darkens.
    /// </summary>
    private static readonly Lazy<Shader> DomeShader = new(() => new Shader
    {
        Code = $$"""
            shader_type spatial;
            render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled;
            uniform sampler2D sky_texture : source_color, repeat_enable, filter_linear_mipmap;
            uniform vec3 band_colour[{{MostBands}}];
            uniform float band_elevation[{{MostBands}}];
            uniform int bands = 1;
            varying vec3 tint;
            void vertex() {
                float elevation = degrees(atan(VERTEX.y, length(VERTEX.xz)));
                vec3 colour = band_colour[bands - 1];
                if (elevation <= band_elevation[0]) {
                    colour = band_colour[0];
                } else {
                    for (int i = 1; i < bands; i++) {
                        if (elevation <= band_elevation[i]) {
                            float f = (elevation - band_elevation[i - 1]) / max(band_elevation[i] - band_elevation[i - 1], 0.001);
                            colour = mix(band_colour[i - 1], band_colour[i], f);
                            break;
                        }
                    }
                }
                tint = colour;
            }
            void fragment() {
                ALBEDO = texture(sky_texture, UV).rgb * tint;
            }
            """,
    });
    private readonly List<(Satellite Satellite, ClassicAssets.SkySprite Sprite, MeshInstance3D Quad, StandardMaterial3D Material)> _sprites = [];
    private string _planet = "";

    /// <summary>
    /// Which painted night sky a planet takes (<c>game/assets/sky/&lt;theme&gt;.png</c>, or one named after the
    /// planet itself): planets that share a landscape share a sky.
    /// </summary>
    private static readonly Dictionary<string, string> SkyThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m_desert"] = "desert", ["desspy"] = "desert", ["daynight"] = "desert", ["desert"] = "desert",
        ["m_green"] = "green", ["mgreendn"] = "green", ["m_dales"] = "green", ["mdalesdn"] = "green", ["finger"] = "green",
        ["hilly"] = "green", ["talonth8"] = "green",
        ["ice"] = "ice", ["icedn"] = "ice", ["micey"] = "ice", ["miceydn"] = "ice", ["resfac"] = "ice",
        ["m_moon"] = "lunar", ["mmoony"] = "lunar", ["1o1"] = "lunar", ["1o2"] = "lunar", ["1o3"] = "lunar", ["CommSat"] = "lunar",
        ["dogbar"] = "lunar", ["multidog"] = "lunar", ["seekndes"] = "lunar", ["tut"] = "lunar",
        ["gracko"] = "volcanic", ["mgrack"] = "volcanic",
        ["fortmult"] = "fortress", ["fortmdn"] = "fortress", ["fortsdn"] = "fortress", ["fortseig"] = "fortress", ["seige"] = "fortress",
        ["5o1"] = "fortress",
        ["home"] = "industrial", ["joust"] = "industrial", ["midian"] = "industrial", ["control"] = "industrial",
        ["m_contrl"] = "industrial", ["kidnap"] = "industrial", ["sat"] = "industrial", ["roller"] = "industrial",
        ["space"] = "space", ["suicide"] = "space", ["guns"] = "space", ["deepspac"] = "space", ["2o1"] = "space",
    };

    /// <summary>A painted night sky (equirectangular) for the planet, its theme or every planet, if one is there.</summary>
    private static Texture2D? NightSky(string planet)
    {
        var names = new[] { planet, SkyThemes.GetValueOrDefault(planet, ""), "stars" }.Where(n => n.Length > 0);
        foreach (var name in names)
            foreach (var extension in new[] { "png", "jpg", "webp" })
                if (ResourceLoader.Exists($"res://assets/sky/{name}.{extension}"))
                    return GD.Load<Texture2D>($"res://assets/sky/{name}.{extension}");
        return null;
    }

    /// <summary>
    /// Where a satellite stands at a game time (EnvOrbit): its height swings between the orbit's minimum and
    /// maximum elevation and back over a period, while it goes once round the sky from its set heading.
    /// </summary>
    public static Vector3 Direction(Orbit orbit, float time)
    {
        var elevation = Mathf.DegToRad(Elevation(orbit, time));
        var heading = Mathf.DegToRad((orbit.SetHeading ?? 0) + 360 * (orbit.StartTime + time) / orbit.Period);
        // The original's x and y are the ground plane (Godot's x and z), z up.
        return new Vector3(Mathf.Cos(elevation) * Mathf.Cos(heading), Mathf.Sin(elevation), Mathf.Cos(elevation) * Mathf.Sin(heading));
    }

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
        // A negative period runs the orbit the other way round; the height swings the same.
        var period = Mathf.Abs(orbit.Period);
        var t = (orbit.StartTime + time) % period;
        var half = period / 2;
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

    /// <summary>The dome's colour at a height above the horizon (degrees), between its bands as the dome shader blends them.</summary>
    private Color BandAt(float height, float elevation, Color fallback)
    {
        if (height <= _bands[0].Elevation)
            return Table(_bands[0].Table, elevation, fallback);
        for (var i = 1; i < _bands.Count; i++)
            if (height <= _bands[i].Elevation)
                return Table(_bands[i - 1].Table, elevation, fallback).Lerp(Table(_bands[i].Table, elevation, fallback),
                    (height - _bands[i - 1].Elevation) / Mathf.Max(_bands[i].Elevation - _bands[i - 1].Elevation, 0.001f));
        return Table(_bands[^1].Table, elevation, fallback);
    }

    private void Update()
    {
        var elevation = _sunOrbit is { } orbit ? Elevation(orbit, _gameTime()) : 60;

        // The light shines from the sun's position towards the ground; it never comes from below the horizon.
        var towardsSun = _sunOrbit is { } sunOrbit ? Direction(sunOrbit, _gameTime()) : new Vector3(0, 1, 0.6f).Normalized();
        var level = new Vector3(towardsSun.X, 0, towardsSun.Z).Normalized();
        if (level == Vector3.Zero)
            level = Vector3.Back;
        var lit = Mathf.DegToRad(Mathf.Clamp(elevation, 1, 89));
        _sun.Basis = Basis.LookingAt(-(level * Mathf.Cos(lit) + Vector3.Up * Mathf.Sin(lit)), Vector3.Up);
        var direct = Table(_sunSatellite?.DirectionalColourTable, elevation, Colors.White);
        _sun.LightColor = direct;
        // The remake's sun is stronger and its ambient weaker (below): lit sides and shade stand apart.
        _sun.LightEnergy = elevation > 0 ? (_softShadows ? RemakeSun : 1.1f) : 0.15f;
        // Every satellite adds its ambient light by its own height in the sky: the sun by day, the moon by night.
        var ambient = Colors.Black;
        foreach (var satellite in _env.Satellites)
        {
            if (satellite.AmbientColourTable is null || !_env.Orbits.TryGetValue(satellite.Orbit, out var satelliteOrbit))
                continue;
            var light = Table(satellite.AmbientColourTable, Elevation(satelliteOrbit, _gameTime()), Colors.Black);
            ambient = new Color(ambient.R + light.R, ambient.G + light.G, ambient.B + light.B);
        }
        // The remake darkens the ambient while the sun stands high, where shade then stands apart from sunlight; as it
        // sinks the scene lives on its ambient, which goes back to the original's.
        var high = _softShadows ? Mathf.Clamp(elevation / 35, 0, 1) : 0;
        var floor = direct * Mathf.Lerp(MinimumAmbient, RemakeMinimumAmbient, high);
        // The remake never lets a night go fully black: a little starlight stays.
        if (_softShadows)
            floor = new Color(Mathf.Max(floor.R, 0.04f), Mathf.Max(floor.G, 0.05f), Mathf.Max(floor.B, 0.085f));
        _environment.AmbientLightColor = new Color(Mathf.Max(ambient.R, floor.R), Mathf.Max(ambient.G, floor.G), Mathf.Max(ambient.B, floor.B));
        // Bounced light (SSIL) brings back some of what the darker ambient takes away, where it really bounces.
        _environment.AmbientLightEnergy = Mathf.Lerp(1, RemakeAmbient, high);

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
        if (_dome is not null)
        {
            // The dome brings the sky's colours; behind it the sky stays black for the stars.
            var count = Math.Min(MostBands, _bands.Count);
            var colours = new Vector3[MostBands];
            var heights = new float[MostBands];
            for (var i = 0; i < count; i++)
            {
                var c = Table(_bands[i].Table, elevation, horizon);
                colours[i] = new Vector3(c.R, c.G, c.B);
                heights[i] = _bands[i].Elevation;
            }
            _dome.SetShaderParameter("band_colour", colours);
            _dome.SetShaderParameter("band_elevation", heights);
            _dome.SetShaderParameter("bands", count);
            _sky.SkyHorizonColor = Colors.Black;
            _sky.SkyTopColor = Colors.Black;
        }

        Night = Mathf.Clamp((4 - elevation) / 6, 0, 1);
        if (_sky.SkyCover is not null)
        {
            // Stars by night, and all day where the sky stays dark (moons and space have hardly any air).
            var airless = Mathf.Clamp((0.18f - top.Luminance) / 0.12f, 0, 1);
            _sky.SkyCoverModulate = new Color(1, 1, 1, Mathf.Max(Night, airless));
        }
        if (_photo is not null)
        {
            // The photographed sky takes the colours the original's dome bands have now, a little above the horizon (the
            // lowest band often lies under it, black) and overhead.
            var horizonBand = _bands.Count > 0 ? BandAt(5, elevation, horizon) : horizon;
            var topBand = _bands.Count > 0 ? BandAt(60, elevation, top) : top;
            _photo.Update(elevation, horizonBand, topBand, towardsSun, direct, _sun.SkyMode != DirectionalLight3D.SkyModeEnum.LightOnly, Night, _gameTime());
            _photo.SetGround(fogColour);
        }
        foreach (var (satellite, disc) in _discs)
        {
            var discOrbit = _env.Orbits[satellite.Orbit];
            var height = Elevation(discOrbit, _gameTime());
            var colour = Table(satellite.MeshColourTable, height, Colors.White);
            // A satellite straight overhead or underfoot (control's stand at 90.5° and -90°) looks along the up
            // vector, which cannot then say which way is up: north does instead.
            var towards = -Direction(discOrbit, _gameTime());
            disc.Basis = Basis.LookingAt(towards, Mathf.Abs(towards.Y) > 0.999f ? Vector3.Forward : Vector3.Up);
            disc.LightColor = new Color(colour.R, colour.G, colour.B);
            disc.LightEnergy = height > 0 ? colour.A * 1.5f : 0;
        }
        PlaceSprites();
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
            if (_environment.VolumetricFogEnabled)
            {
                // A low sun shines through more air: the haze thickens a little as it sinks, and shafts stand out. It is
                // as thick as the planet's air is clear (thinner where the original sees farther), and lit in the
                // sky's colour at the horizon where the original's fog is darker: many planets fade to black far off,
                // and haze that dark would swallow the day.
                var low = Mathf.Clamp(1 - elevation / 30, 0, 1);
                var haze = new Color(Mathf.Max(fogColour.R, horizon.R), Mathf.Max(fogColour.G, horizon.G), Mathf.Max(fogColour.B, horizon.B));
                _environment.VolumetricFogAlbedo = haze;
                _environment.VolumetricFogDensity = Mathf.Clamp(HazeReach / Mathf.Max(f.MaxVisibility, 1), MinimumHaze, HazeDensity) * (1 + 0.5f * low);
            }
        }
        UpdateShadowReach();
    }

    /// <summary>
    /// The satellites' pictures stand far out in their direction from the camera, sized as the original's square
    /// at its orbit radius, in the colour (and fade) its mesh colour table gives for its height.
    /// </summary>
    private void PlaceSprites()
    {
        if (GetViewport()?.GetCamera3D() is not { } camera)
            return;
        // The dome goes round the camera, inside its far plane.
        if (_domeView is not null)
        {
            _domeView.GlobalPosition = camera.GlobalPosition;
            _domeView.Scale = Vector3.One * (camera.Far * 0.8f / _domeRadius);
        }
        if (_sprites.Count == 0)
            return;
        var distance = camera.Far * 0.85f;
        foreach (var (satellite, sprite, quad, material) in _sprites)
        {
            var orbit = _env.Orbits[satellite.Orbit];
            var height = Elevation(orbit, _gameTime());
            var colour = Table(satellite.MeshColourTable, height, Colors.White);
            quad.Visible = height > -10 && colour.A > 0.01f;
            quad.GlobalPosition = camera.GlobalPosition + Direction(orbit, _gameTime()) * distance;
            quad.Scale = Vector3.One * (2 * sprite.HalfSize / Mathf.Max(1, orbit.Radius) * distance);
            // The table colours the picture as the original's material plan did; black would hide it.
            material.AlbedoColor = colour.R + colour.G + colour.B < 0.05f ? new Color(1, 1, 1, colour.A) : colour;        }
    }

    private void UpdateShadowReach()
    {
        // Shadows reach a little beyond what the camera looks at, so they stay sharp up close.
        if (_softShadows)
            _sun.DirectionalShadowMaxDistance = Mathf.Clamp(_viewDistance() * 3f, 120, 700);
    }
}
