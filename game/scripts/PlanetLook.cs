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
    /// The original's only shadows were plates painted under the models, and it could light with a black ambient; the
    /// sun here casts shadows, so the ambient never falls below this share of the sunlight.
    /// </summary>
    private const float MinimumAmbient = 0.5f;

    /// <summary>
    /// The remake's base light while the sun stands high: the ambient's floor as a share of the sunlight, and the sun. The
    /// planet's own ambient is kept whole: dark planets (Gracko, the moons, space) give little or none, and halving it, with
    /// a floor of a fifth, left their shade sides nearly black.
    /// </summary>
    private const float RemakeMinimumAmbient = 0.45f;
    private const float RemakeSun = 1.6f;

    /// <summary>The remake's contrast, in a world with light in its sky (see Update).</summary>
    private const float RemakeContrast = 1.12f;

    /// <summary>
    /// The volumetric fog's density by day, at most (it thickens towards dawn, dusk and night): a share of the light is
    /// lost over the planet's MAX VISIBILITY (<see cref="HazeReach"/> of e-foldings), but never less than the minimum.
    /// </summary>
    private const float HazeDensity = 0.004f, MinimumHaze = 0.0006f, HazeReach = 0.6f;

    /// <summary>Haze darker than this (its colour's luminance) only swallows light and is left out.</summary>
    private const float BlackHaze = 0.04f;

    /// <summary>The classic look's shadows are the original's: the plates under the models, not the sun's.</summary>
    public static bool PaintedShadows => !GraphicsSettings.Lighting && GraphicsSettings.Models == ModelLook.Original;

    private static readonly Lazy<ShaderMaterial> Plate = new(() => new ShaderMaterial
    {
        // A little before the ground along the line of sight, as the original's plates were marked coplanar with it.
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
                uniform float strength = 0.5;
                void vertex() {
                    vec4 seen = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
                    seen.xyz *= max(0.0, 1.0 - 0.5 / max(length(seen.xyz), 0.5));
                    POSITION = PROJECTION_MATRIX * seen;
                }
                void fragment() {
                    ALBEDO = vec3(0.0);
                    ALPHA = strength;
                }
                """,
        },
        RenderPriority = -1,
    });

    /// <summary>The material of the plates, as dark as the sun is stronger than the ambient light (W4dShadow::setLight).</summary>
    public static ShaderMaterial ShadowPlate => Plate.Value;

    /// <summary>The goggles are on (EnvPlanetEnvironment::nvgOn): see <see cref="FirstPersonScreen"/>.</summary>
    public bool NightVision { get; set; }

    // The planet's goggles, with the original's values where its .env does not say.
    private Color _nvgFilter = new(0, 1, 0), _nvgFog = new(0, 0.6f, 0.1f), _nvgSky = new(0, 0.9f, 0.3f), _nvgSkyline = new(0, 0.6f, 0.1f);
    private float _nvgFogMultiplier = 1.5f, _nvgLightMultiplier = 7;

    /// <summary>The colour the goggles let through.</summary>
    public Color NightVisionFilter => _nvgFilter;

    private void ReadGoggles()
    {
        if (_env.OtherBlocks.FirstOrDefault(b => b.Kind == "nvg") is not { } goggles)
            return;
        foreach (var (key, value) in goggles.Properties)
        {
            var colour = value.Tuple is { Count: >= 3 } c ? new Color(c[0], c[1], c[2]) : Colors.Black;
            switch (key)
            {
                case "filter colour": _nvgFilter = colour; break;
                case "fog colour": _nvgFog = colour; break;
                case "sky colour": _nvgSky = colour; break;
                case "skyline colour": _nvgSkyline = colour; break;
                case "fog multiplier": _nvgFogMultiplier = value.Number ?? _nvgFogMultiplier; break;
                case "light multiplier": _nvgLightMultiplier = value.Number ?? _nvgLightMultiplier; break;
            }
        }
    }

    public static PlanetLook Create(PlanetEnvironment env, string planet, Func<float> gameTime, Func<float> viewDistance,
        Func<string, ClassicAssets.SkySprite?> skySprite, (Mesh Mesh, Texture2D Texture, float Radius)? dome = null, (Mesh Mesh, float Radius)? skyline = null)
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

        look.ReadGoggles();
        var fogStill = env.Fog is { } fogBlock && env.ColourTables.TryGetValue(fogBlock.ColourTable, out var fogTable) && fogTable.Count > 0
            ? Lookup(fogTable, 60).Luminance : 0;
        Enclosed = Interior || GraphicsSettings.Lighting && (look._sunOrbit is { } fixedSun && fixedSun.MinElevation == fixedSun.MaxElevation
            || look._uniformSky is not null && fogStill < BlackHaze);
        // Indoors no sun shines: the deck's own lamps light it (DeckView).
        look._sun = new DirectionalLight3D { ShadowEnabled = !PaintedShadows && !Interior, Name = "Sun", Visible = !Interior };
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
        if (GraphicsSettings.Lighting && look._sunOrbit is { } sunOrbit
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
                RenderPriority = -9,
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

        // The hills on the horizon, before the sky, its satellites and stars, in the colour of the hour; the photographed
        // sky has its own horizon.
        if (skyline is { } hills && look._photo is null
            && env.OtherBlocks.FirstOrDefault(b => b.Kind == "skyline")?.Properties.FirstOrDefault(p => p.Key == "colour table").Value?.Text is { } skylineTable)
        {
            look._skylineTable = skylineTable;
            look._skyline = new ShaderMaterial { Shader = SkylineShader.Value, RenderPriority = -8 };
            look._skylineView = new MeshInstance3D
            {
                Mesh = hills.Mesh,
                MaterialOverride = look._skyline,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Name = "Skyline",
            };
            look._skylineRadius = hills.Radius;
            look.AddChild(look._skylineView);
        }

        _adapting = false;
        if (GraphicsSettings.Lighting)
            look.Enhance();
        ApplyBloom(look._environment);
        if (Interior)
            look.Indoors();
        look.AddChild(look._sun);
        var attributes = new CameraAttributesPractical();
        Adapt(attributes);
        look.AddChild(new WorldEnvironment { Environment = look._environment, CameraAttributes = attributes });
        look.Update();
        return look;
    }

    /// <summary>
    /// The glow round the brightest spots (muzzle flashes, explosions, glowing eyes, lamps, the sun) as the options
    /// set it (<see cref="GraphicsSettings.Bloom"/>): low as the remake always glowed, high wider and stronger. With
    /// the lens dirt on, the glow lights up the smudges of a dirty lens where it falls (<see cref="LensDirt"/>).
    /// </summary>
    public static void ApplyBloom(Godot.Environment environment)
    {
        var level = GraphicsSettings.Bloom;
        environment.GlowEnabled = level > 0;
        if (level == 0)
            return;
        var high = level == 2;
        environment.GlowIntensity = high ? 0.9f : 0.6f;
        environment.GlowBloom = high ? 0.1f : 0.04f;
        environment.GlowHdrThreshold = high ? 0.95f : 1.1f;
        environment.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Softlight;
        // High glows wider: two more of the larger blurs carry it (Godot's own are levels 3 and 5).
        if (high)
        {
            environment.SetGlowLevel(4, 1);
            environment.SetGlowLevel(6, 0.5f);
        }
        if (GraphicsSettings.LensDirt)
        {
            // The map dims the glow where the lens is clean (by about half) and keeps it whole on the
            // smudges; the glow is raised to match, so a clean spot glows as without the dirt.
            environment.GlowMap = LensDirt.Value;
            environment.GlowMapStrength = DirtStrength;
            environment.GlowIntensity /= 1 - DirtStrength * (1 - DirtClean);
        }
    }

    private const float DirtStrength = 0.8f;
    private const float DirtClean = 0.4f;

    /// <summary>
    /// A dirty lens, made once: a grey where the glass is clean (<see cref="DirtClean"/>) with soft round smudges and
    /// a few streaks of grime brighter on it, more of them towards the edges, a little warm. Seeded, so it is the
    /// same every time.
    /// </summary>
    private static readonly Lazy<Texture2D> LensDirt = new(() =>
    {
        const int width = 480, height = 270;
        var light = new float[width * height];
        var random = new Random(1999);
        void Smudge(float cx, float cy, float radius, float strength, float ring)
        {
            for (var y = (int)Math.Max(0, cy - radius); y < Math.Min(height, cy + radius + 1); y++)
                for (var x = (int)Math.Max(0, cx - radius); x < Math.Min(width, cx + radius + 1); x++)
                {
                    var d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / radius;
                    if (d >= 1)
                        continue;
                    // A soft disc, its rim a little brighter as a drop dried on glass leaves it.
                    var fall = 1 - d * d;
                    light[y * width + x] += strength * (fall * fall * (1 - ring) + ring * MathF.Exp(-MathF.Pow((d - 0.85f) / 0.08f, 2)));
                }
        }
        // More towards the edges: the middle of a lens is wiped more often.
        (float X, float Y) Spot()
        {
            var x = (float)random.NextDouble();
            var y = (float)random.NextDouble();
            if (random.NextDouble() < 0.6)
                (x, y) = (x < 0.5f ? x * x * 2 : 1 - (1 - x) * (1 - x) * 2, y < 0.5f ? y * y * 2 : 1 - (1 - y) * (1 - y) * 2);
            return (x * width, y * height);
        }
        for (var i = 0; i < 70; i++)
        {
            var (x, y) = Spot();
            Smudge(x, y, 4 + (float)random.NextDouble() * 18, 0.15f + (float)random.NextDouble() * 0.35f, (float)random.NextDouble() * 0.5f);
        }
        for (var i = 0; i < 14; i++)
        {
            var (x, y) = Spot();
            Smudge(x, y, 30 + (float)random.NextDouble() * 50, 0.1f + (float)random.NextDouble() * 0.15f, 0);
        }
        // Streaks wiped across the glass: a row of faint smudges along a slanting line.
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = Spot();
            var angle = (float)random.NextDouble() * MathF.PI;
            var length = 40 + (float)random.NextDouble() * 120;
            for (var t = 0f; t < length; t += 3)
                Smudge(x + MathF.Cos(angle) * t, y + MathF.Sin(angle) * t, 5, 0.06f, 0);
        }
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgb8);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var v = Math.Clamp(DirtClean + light[y * width + x] * (1 - DirtClean), 0, 1);
                image.SetPixel(x, y, new Color(v, v * 0.97f, v * 0.92f));
            }
        return ImageTexture.CreateFromImage(image);
    });

    /// <summary>
    /// The remake's look over the original's light: filmic tone mapping (AgX) with a touch more saturation, glow
    /// around the brightest spots (muzzle flashes, explosions, the sun; see <see cref="ApplyBloom"/>), ambient occlusion where things meet the
    /// ground, and soft shadows that reach as far as the camera looks.
    /// </summary>
    private void Enhance()
    {
        _environment.TonemapMode = Godot.Environment.ToneMapper.Agx;
        _environment.TonemapExposure = 1.0f;
        _environment.AdjustmentEnabled = true;
        _environment.AdjustmentSaturation = 1.1f;
        _environment.AdjustmentContrast = RemakeContrast;
        // Each kind of planet has its own grade: the shade and the light tinted apart (ColourGrade).
        _environment.AdjustmentColorCorrection = ColourGrade.For(_planet);
        _adapting = true;
        // Metres: the machines stand a few metres tall, the buildings ten and more. Seen from the strategy camera a
        // small radius barely shows, and occlusion that only darkens the ambient light is lost in the sun: it reaches
        // further, and takes a share of the direct light too.
        _environment.SsaoEnabled = true;
        _environment.SsaoRadius = 6f;
        _environment.SsaoIntensity = 2.6f;
        _environment.SsaoPower = 1.6f;
        _environment.SsaoDetail = 0.8f;
        _environment.SsaoLightAffect = 0.35f;
        _sun.LightAngularDistance = 1.2f;
        _sun.ShadowBlur = 1.5f;
        _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        _sun.DirectionalShadowBlendSplits = true;
        _softShadows = true;

        // Smooth surfaces (the water) mirror what is around them; on planets without water nothing would.
        _environment.SsrEnabled = !TerrainMaterials.Dry(_planet);
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

    /// <summary>
    /// Whether the match is played indoors, on a ship deck (the story's; Game.BuildGround sets it for each match before the
    /// look is made): no sun and no sky, a black background past the walls, little ambient light so the deck's lamps tell,
    /// the corridors fading into the dark, ambient occlusion sized to rooms rather than landscapes, and a thin haze the
    /// lamps and searchlights shine through. Smooth metal keeps its screen-space reflections, on purpose.
    /// </summary>
    public static bool Interior { get; set; }

    /// <summary>
    /// Indoors, the share of the ambient light the deck has now (1 at full; lower in the story's dark start, Game.ShowDeckStory).
    /// </summary>
    public static float Gloom { get; set; } = 1;

    // Indoors: the ambient light, the dark the far end of a corridor fades into (metres), occlusion and bounced light about
    // the size of a machine, and the haze.
    private static readonly Color InteriorAmbient = new(0.04f, 0.042f, 0.05f);
    private const float InteriorFogBegin = 25, InteriorFogEnd = 140;
    private const float InteriorOcclusion = 1.5f, InteriorBounce = 2.5f;
    private static readonly Color InteriorHaze = new(0.55f, 0.56f, 0.6f);
    private const float InteriorHazeDensity = 0.012f, InteriorHazeLength = 60;

    /// <summary>The look indoors (<see cref="Interior"/>), over what the planet's own light set.</summary>
    private void Indoors()
    {
        _environment.BackgroundMode = Godot.Environment.BGMode.Color;
        _environment.BackgroundColor = Colors.Black;
        _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        _environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;
        _environment.FogEnabled = true;
        _environment.FogMode = Godot.Environment.FogModeEnum.Depth;
        _environment.FogSkyAffect = 0;
        foreach (var (_, disc) in _discs)
            disc.Visible = false;
        if (_domeView is not null)
            _domeView.Visible = false;
        if (_skylineView is not null)
            _skylineView.Visible = false;
        if (!GraphicsSettings.Lighting)
            return;
        _environment.SsaoRadius = InteriorOcclusion;
        _environment.SsaoIntensity = 2.0f;
        _environment.SsilRadius = InteriorBounce;
        _environment.VolumetricFogEnabled = true;
        _environment.VolumetricFogAnisotropy = 0.5f;
        _environment.VolumetricFogLength = InteriorHazeLength;
        _environment.VolumetricFogDetailSpread = 2;
        _environment.VolumetricFogSkyAffect = 0;
        _environment.VolumetricFogTemporalReprojectionEnabled = true;
        _environment.VolumetricFogAlbedo = InteriorHaze;
        _environment.VolumetricFogDensity = InteriorHazeDensity;
        // Dark rooms are most of the picture: the remake's contrast would take them to black (see Update).
        _environment.AdjustmentContrast = 1;
    }

    /// <summary>Each frame indoors: no sun, the ambient low (bright through the goggles), the far dark black.</summary>
    private void UpdateIndoors()
    {
        _sun.LightEnergy = 0;
        foreach (var (_, _, quad, _) in _sprites)
            quad.Visible = false;
        if (!NightVision)
        {
            _environment.AmbientLightColor = InteriorAmbient;
            _environment.AmbientLightEnergy = Gloom;
        }
        _environment.FogLightColor = NightVision ? _nvgFog * 0.25f : Colors.Black;
        _environment.FogDensity = 1;
        _environment.FogDepthBegin = NightVision ? InteriorFogBegin * 2 : InteriorFogBegin;
        _environment.FogDepthEnd = NightVision ? InteriorFogEnd * 2 : InteriorFogEnd;
        if (_environment.VolumetricFogEnabled)
        {
            _environment.VolumetricFogDensity = InteriorHazeDensity;
            _environment.VolumetricFogAlbedo = NightVision ? _nvgFog : InteriorHaze;
            _environment.VolumetricFogSkyAffect = 0;
        }
        if (_softShadows)
            _environment.AdjustmentContrast = 1;
    }

    private readonly List<(Satellite Satellite, DirectionalLight3D Disc)> _discs = [];
    private PhotoSky? _photo;

    // The dynamic sky's colour tables by the dome height they apply from (EnvDynamicSky::findVertexColour).
    private List<(float Elevation, string Table)> _bands = [];
    private ShaderMaterial? _dome;
    private MeshInstance3D? _domeView;
    private float _domeRadius = 1;
    private const int MostBands = 8;

    private ShaderMaterial? _skyline;
    private MeshInstance3D? _skylineView;
    private string? _skylineTable;
    private float _skylineRadius = 1;

    private static readonly Lazy<Shader> SkylineShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;
            uniform vec3 colour = vec3(0.0);
            void fragment() {
                ALBEDO = colour;
                ALPHA = 1.0;
            }
            """,
    });

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

    /// <summary>How dark it is, from 0 by day to 1 at night, by the sun's height.</summary>
    public static float Night { get; private set; }

    /// <summary>
    /// How much the lamps are wanted, 0 to 1: at night, and always indoors (<see cref="Enclosed"/>), in the remake's
    /// light. The machines' headlights and the buildings' lamps follow it.
    /// </summary>
    public static float Dark { get; private set; }

    /// <summary>
    /// Whether the planet is a place indoors or a station in space: lit by a lamp fixed overhead (kidnap, sat, control,
    /// m_contrl) or under a black sky of one colour (guns, space, suicide, deepspac). The moons' black sky is a real one,
    /// with a real sun in it.
    /// </summary>
    public static bool Enclosed { get; private set; }

    /// <summary>The ambient light indoors, as a share of what it would be: the shade there is darker, so lamps tell.</summary>
    private const float EnclosedAmbient = 0.55f;

    /// <summary>The light overhead indoors, as a share of what it would be.</summary>
    private const float EnclosedLight = 0.6f;

    /// <summary>
    /// How far a wave of a Wired run has forced the night on, 0 to 1. The sun of many a planet never sets and its tables know
    /// no night, so the sun sinks here for the look of it, and its light and the sky go over to the night's colours below.
    /// </summary>
    public float ForcedNight { get; set; }

    private float _dim;

    /// <summary>
    /// How much a cutscene's dusk darkens the whole picture besides (0 none, at most 0.9): the exposure falls by that share,
    /// so the sky, the ground and the forced night's strong moonlight go dark together while what glows still stands out
    /// (the prologue's Abwurf on m_desert at dusk, abwurf.cut's <c>dusk … dim=</c>).
    /// </summary>
    public float Dim
    {
        get => _dim;
        set
        {
            var dim = Mathf.Clamp(value, 0, 0.9f);
            _environment.TonemapExposure *= (1 - dim) / (1 - _dim);
            _dim = dim;
        }
    }

    /// <summary><see cref="ForcedNight"/> as of the last frame, for what glows by itself and has to fade with the day.</summary>
    public static float ForcedDark { get; private set; }

    // How high the light of a forced night stands, in degrees, and how strong it is.
    private const float MoonHeight = 32, MoonEnergy = 0.5f, RemakeMoon = 1.6f;

    private static readonly Color Moonlight = new(0.3f, 0.36f, 0.55f), StarLight = new(0.075f, 0.09f, 0.15f);
    private static readonly Color NightHorizon = new(0.045f, 0.06f, 0.11f), NightTop = new(0.008f, 0.012f, 0.035f), NightFog = new(0.02f, 0.03f, 0.055f), NightHills = new(0.01f, 0.015f, 0.03f);

    /// <summary>A colour of the day as the forced night has it by now.</summary>
    private Color Forced(Color day, Color night) => ForcedNight > 0 ? day.Lerp(night with { A = day.A }, ForcedNight) : day;

    /// <summary>The sun's height above the horizon now, degrees (morning mist follows it; see Weather).</summary>
    public static float SunElevation { get; private set; } = 60;

    /// <summary>Towards the light that casts the shadows, as last set (the clouds' shadows fall along it; see CloudShadows).</summary>
    public static Vector3 TowardsSun { get; private set; } = new Vector3(0, 1, 0.6f).Normalized();

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

    /// <summary>The way the sun shines as last set (see Update).</summary>
    private Vector3? _sunSet;

    public override void _Process(double delta) => Update();

    /// <summary>Whether the eye adapts to the light (the remake's light, not in a film; see <see cref="Adapt"/>).</summary>
    private static bool _adapting;

    /// <summary>
    /// The eye adapts to what it looks at, within a stop either way of the light as set: a canyon's shade, the night or a
    /// dark station opens up a little over a few seconds, the glare of a burning base or a nuclear blast closes it down.
    /// A film keeps the exposure its shots were graded for. Camera attributes of their own (the depth of field, see
    /// FilmLook.Focus) take these over too, as they replace the environment's.
    /// </summary>
    public static void Adapt(CameraAttributesPractical attributes)
    {
        attributes.AutoExposureEnabled = _adapting && Cinematic.Asked is null;
        // Godot's sensitivity here bounds the scene's measured brightness: about 310 gives the exposure the light was set
        // for, lower opens up (220, half a stop), higher closes down (450, half a stop). The scale is set so a sunlit
        // desert by day stays where it was.
        // Indoors the eye does not open up: the dark there is meant, and the lamps are to tell.
        attributes.AutoExposureMinSensitivity = Enclosed ? 310 : 220;
        attributes.AutoExposureMaxSensitivity = 450;
        attributes.AutoExposureSpeed = 0.6f;
        attributes.AutoExposureScale = 0.5f;
    }

    public override void _Ready()
    {
        // A film draws itself larger and multisampled (Cinematic.Quality).
        if (Cinematic.Asked is null)
            Smooth(GetViewport(), _softShadows);
    }

    /// <summary>
    /// The remake's light is smoothed over time (FSR 2 at the window's own size): edges, the grass's cut-out blades
    /// (alpha to coverage, which without multisampling shimmered) and sparkling highlights stay still from frame to
    /// frame, and the screen effects' noise (occlusion, bounced light, reflections) settles. The original's look keeps
    /// the project's edge smoothing (SMAA) on a picture as sharp as the original's. MACHINES_TAA=0 leaves it out for a run.
    /// </summary>
    public static void Smooth(Viewport viewport, bool temporal)
    {
        temporal &= System.Environment.GetEnvironmentVariable("MACHINES_TAA") != "0";
        viewport.Scaling3DMode = temporal ? Viewport.Scaling3DModeEnum.Fsr2 : Viewport.Scaling3DModeEnum.Bilinear;
        viewport.Scaling3DScale = 1;
        viewport.ScreenSpaceAA = temporal ? Viewport.ScreenSpaceAAEnum.Disabled : Viewport.ScreenSpaceAAEnum.Smaa;
    }

    /// <summary>The first game time the sun stands highest (0 on a planet without one).</summary>
    public static float Noon(PlanetEnvironment env)
    {
        var sun = env.Satellites.FirstOrDefault(s => s.Name == "sun") ?? env.Satellites.FirstOrDefault();
        if (sun is null || !env.Orbits.TryGetValue(sun.Orbit, out var orbit) || orbit.Period == 0)
            return 0;
        var period = Mathf.Abs(orbit.Period);
        var highest = orbit.MaxElevation >= orbit.MinElevation ? period / 2 : 0;
        return Mathf.PosMod(highest - orbit.StartTime, period);
    }

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
        if (ForcedNight > 0)
            elevation = Mathf.Lerp(elevation, -14, ForcedNight);
        SunElevation = elevation;
        ForcedDark = ForcedNight;

        // The light shines from the sun's position towards the ground; it never comes from below the horizon.
        var towardsSun = _sunOrbit is { } sunOrbit ? Direction(sunOrbit, _gameTime()) : new Vector3(0, 1, 0.6f).Normalized();
        var level = new Vector3(towardsSun.X, 0, towardsSun.Z).Normalized();
        if (level == Vector3.Zero)
            level = Vector3.Back;
        var lit = Mathf.DegToRad(Mathf.Lerp(Mathf.Clamp(elevation, 1, 89), MoonHeight, ForcedNight));
        // Turned in small steps, not every frame: each turn makes every shadow edge snap anew to the shadow map, and a
        // sun creeping on frame by frame made them shimmer.
        var shining = -(level * Mathf.Cos(lit) + Vector3.Up * Mathf.Sin(lit));
        TowardsSun = -shining;
        if (_sunSet is not { } was || was.AngleTo(shining) > Mathf.DegToRad(0.15f))
        {
            _sunSet = shining;
            _sun.Basis = Basis.LookingAt(shining, Vector3.Up);
        }
        var direct = Forced(Table(_sunSatellite?.DirectionalColourTable, elevation, Colors.White), Moonlight);
        // The goggles turn every light into their own colour, as bright as its strongest part (EnvNVGLightTransform).
        _sun.LightColor = NightVision ? _nvgFilter * direct.V : direct;
        // The remake's sun is stronger and its ambient weaker (below): lit sides and shade stand apart.
        _sun.LightEnergy = elevation > 0 ? (_softShadows ? RemakeSun : 1.1f) : 0.15f;
        _sun.LightEnergy = Mathf.Lerp(_sun.LightEnergy, _softShadows ? RemakeMoon : MoonEnergy, ForcedNight);
        // A storm's thick air dims the sun (Weather).
        _sun.LightEnergy *= 1 - 0.35f * Weather.Haze;
        // Indoors the light overhead is dimmer too: the lamps of machines and buildings carry the place.
        if (Enclosed)
            _sun.LightEnergy *= EnclosedLight;
        // Every satellite adds its ambient light by its own height in the sky: the sun by day, the moon by night.
        var ambient = Colors.Black;
        foreach (var satellite in _env.Satellites)
        {
            if (satellite.AmbientColourTable is null || !_env.Orbits.TryGetValue(satellite.Orbit, out var satelliteOrbit))
                continue;
            var light = Table(satellite.AmbientColourTable, Elevation(satelliteOrbit, _gameTime()), Colors.Black);
            ambient = new Color(ambient.R + light.R, ambient.G + light.G, ambient.B + light.B);
        }
        ambient = Forced(ambient, StarLight);
        // The remake lowers the ambient's floor a little while the sun stands high, where shade then stands apart from
        // sunlight; as it sinks the scene lives on its ambient, which goes back to the original's. A sun fixed overhead is the stations'
        // lamp (kidnap, sat, control): no day to set the shade against, and their walls, which it never reaches, live on
        // the ambient too.
        var lamp = _sunOrbit is { } still && still.MinElevation == still.MaxElevation;
        var high = _softShadows && !lamp ? Mathf.Clamp(elevation / 35, 0, 1) : 0;
        var floor = direct * Mathf.Lerp(MinimumAmbient, RemakeMinimumAmbient, high);
        // The remake never lets a night go fully black: a little starlight stays.
        if (_softShadows)
            floor = new Color(Mathf.Max(floor.R, 0.04f), Mathf.Max(floor.G, 0.05f), Mathf.Max(floor.B, 0.085f));
        _environment.AmbientLightColor = new Color(Mathf.Max(ambient.R, floor.R), Mathf.Max(ambient.G, floor.G), Mathf.Max(ambient.B, floor.B));
        // Indoors the shade is darker, and the lamps of machines and buildings light it (Dark).
        if (Enclosed)
            _environment.AmbientLightColor *= EnclosedAmbient;
        if (PaintedShadows)
            ShadowPlate.SetShaderParameter("strength", Mathf.Clamp(direct.V - ambient.V, 0.2f, 0.75f));

        Night = Mathf.Clamp((4 - elevation) / 6, 0, 1);
        Dark = Enclosed ? 1 : Night;
        if (NightVision)
        {
            // The goggles multiply the ambient light; by day it is blinding, whatever the planet's (planet.cpp, accumulateAmbient).
            var strong = new Color(ambient.R, ambient.G, ambient.B) * _nvgLightMultiplier;
            _environment.AmbientLightColor = new Color(1.5f, 1.5f, 1.5f).Lerp(strong, Night);
            _environment.AmbientLightEnergy = 1;
        }

        var fogColour = Forced(_env.Fog is { } fog ? Table(fog.ColourTable, elevation, new Color(0.6f, 0.6f, 0.65f)) : new Color(0.6f, 0.6f, 0.65f), NightFog);
        // The table's alpha says how far the fog lets one see (EnvPlanetEnvironment::update).
        var clearAir = fogColour.A;
        // A storm fills the air with sand, snow or rain: the fog takes its colour (Weather).
        fogColour = fogColour.Lerp((Weather.HazeColour * Mathf.Clamp(direct.Luminance * 1.2f, 0.25f, 1)) with { A = 1 }, Weather.Haze);
        if (NightVision)
            fogColour = _nvgFog;
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
        (horizon, top) = (Forced(horizon, NightHorizon), Forced(top, NightTop));
        if (NightVision)
            horizon = top = _nvgSky;
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
                var c = NightVision ? _nvgSky : Forced(Table(_bands[i].Table, elevation, horizon), NightHorizon.Lerp(NightTop, i / (float)Mathf.Max(count - 1, 1)));
                colours[i] = new Vector3(c.R, c.G, c.B);
                heights[i] = _bands[i].Elevation;
            }
            _dome.SetShaderParameter("band_colour", colours);
            _dome.SetShaderParameter("band_elevation", heights);
            _dome.SetShaderParameter("bands", count);
            _sky.SkyHorizonColor = Colors.Black;
            _sky.SkyTopColor = Colors.Black;
        }
        if (_skyline is not null)
        {
            var hills = NightVision ? _nvgSkyline : Forced(Table(_skylineTable, elevation, Colors.Black), NightHills);
            _skyline.SetShaderParameter("colour", new Vector3(hills.R, hills.G, hills.B));
        }

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
            var horizonBand = Forced(_bands.Count > 0 ? BandAt(5, elevation, horizon) : horizon, NightHorizon);
            var topBand = Forced(_bands.Count > 0 ? BandAt(60, elevation, top) : top, NightTop);
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
            disc.LightEnergy = height > 0 ? colour.A * 1.5f * (1 - ForcedNight) : 0;
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
            // The goggles see farther, and the zenith view, which the strategy camera is, through a fog cut thinner by the
            // planet's multiplier so as not to hide the ground (MachCameras::reduceFog).
            var visibility = Mathf.Max(f.MaxVisibility * clearAir, 1) * (NightVision ? _nvgFogMultiplier : 1) * (view > 0 ? f.ZenithMultiplier : 1);
            // In a storm the fog closes in: at its height one sees only a hundred metres or so past what the camera looks at.
            _environment.FogDepthBegin = Mathf.Lerp(view + visibility * 0.25f, view * 0.3f, Weather.Haze);
            _environment.FogDepthEnd = Mathf.Lerp(view + visibility * 1.5f, view + 140, Weather.Haze);
            _environment.FogDensity = Mathf.Lerp(1, 0.95f, Weather.Haze);
            // The storm hides the sky too, so the clear sky does not show above the hills.
            _environment.FogSkyAffect = Mathf.Lerp(0.6f, 1, Weather.Haze);
            if (_environment.VolumetricFogEnabled)
            {
                // A low sun shines through more air: the haze thickens a little as it sinks, and shafts stand out. It is
                // as thick as the planet's air is clear (thinner where the original sees farther), and lit in the
                // sky's colour at the horizon where the original's fog is darker: many planets fade to black far off,
                // and haze that dark would swallow the day. Where both are black (the stations under a black sky:
                // kidnap, guns, midian ...) there is no haze: it would only swallow the light (half of it on kidnap),
                // and the depth fog already fades the distance to black.
                var low = Mathf.Clamp(1 - elevation / 30, 0, 1);
                var haze = new Color(Mathf.Max(fogColour.R, horizon.R), Mathf.Max(fogColour.G, horizon.G), Mathf.Max(fogColour.B, horizon.B));
                _environment.VolumetricFogAlbedo = haze;
                _environment.VolumetricFogDensity = haze.Luminance < BlackHaze ? 0
                    : Mathf.Clamp(HazeReach / Mathf.Max(f.MaxVisibility, 1), MinimumHaze, HazeDensity) * (1 + 0.5f * low) * (1 + 3 * Weather.Haze);
                // In a storm the haze hides the sky as it hides the hills, so no clear patch shows above them.
                _environment.VolumetricFogSkyAffect = Weather.Haze;
            }
        }
        if (_softShadows)
        {
            // The remake's contrast turns about mid grey and takes the darkest tones to black. In a dark world (black
            // sky and fog: the stations, space) the scene is mostly such tones and its walls went black (kidnap at
            // half the original's light); there it keeps the original's.
            var world = Mathf.Max(fogColour.Luminance, horizon.Luminance);
            _environment.AdjustmentContrast = Mathf.Lerp(1, RemakeContrast, Mathf.Clamp(world / (2 * BlackHaze), 0, 1));
        }
        UpdateShadowReach();
        if (Interior)
            UpdateIndoors();
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
        if (_skylineView is not null)
        {
            _skylineView.GlobalPosition = camera.GlobalPosition;
            _skylineView.Scale = Vector3.One * (camera.Far * 0.78f / _skylineRadius);
        }
        if (_sprites.Count == 0)
            return;
        var distance = camera.Far * 0.85f;
        foreach (var (satellite, sprite, quad, material) in _sprites)
        {
            var orbit = _env.Orbits[satellite.Orbit];
            var height = Elevation(orbit, _gameTime());
            var colour = Table(satellite.MeshColourTable, height, Colors.White);
            quad.Visible = height > -10 && colour.A > 0.01f && !(ForcedNight > 0.3f && satellite == _sunSatellite);
            quad.GlobalPosition = camera.GlobalPosition + Direction(orbit, _gameTime()) * distance;
            quad.Scale = Vector3.One * (2 * sprite.HalfSize / Mathf.Max(1, orbit.Radius) * distance);
            // The table colours the picture as the original's material plan did; black would hide it.
            material.AlbedoColor = colour.R + colour.G + colour.B < 0.05f ? new Color(1, 1, 1, colour.A) : colour;        }
    }

    private void UpdateShadowReach()
    {
        // Shadows reach a little beyond what the camera looks at, so they stay sharp up close. The reach moves only
        // when the zoom has moved a quarter away from it: changed every frame, it laid the shadow cascades out anew
        // each time and the shadows jumped while zooming.
        if (!_softShadows)
            return;
        var wanted = Mathf.Clamp(_viewDistance() * 3f, 120, 700);
        var reach = _sun.DirectionalShadowMaxDistance;
        if (wanted > reach * 1.25f || wanted < reach / 1.25f)
            _sun.DirectionalShadowMaxDistance = wanted;
    }
}
