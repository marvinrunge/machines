using Godot;

namespace Machines.Game;

/// <summary>
/// Weather in the remake look, purely for the eye (the simulation knows nothing of it), as the player chose on the
/// planet page: on the desert sand drifting over the ground, dust devils and sandstorms; on the ice snowfall, snow
/// drifts and blizzards; on the green planets, hills and forts rain showers that wet the ground, and morning mist; on
/// Gracko and the red moons ash and embers; on the airless grey moons shooting stars. Light weather comes and goes
/// almost all the time, a storm every ten to fifteen minutes. When is drawn from the match's seed, so every player
/// (and a replay) sees the same weather. The wind blows the grass and the drifts; its sound and the rain's are made
/// here, from noise. Storms roll billows of sand, snow or ash over the ground. The particles' pictures are Kenney's
/// (game/assets/particles, CC0).
/// </summary>
public partial class Weather : Node3D
{
    private enum Kind { Desert, Green, Ice, Ash, Moon }

    private static readonly Dictionary<string, Kind> Planets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m_desert"] = Kind.Desert, ["desspy"] = Kind.Desert, ["daynight"] = Kind.Desert, ["desert"] = Kind.Desert,
        ["m_green"] = Kind.Green, ["mgreendn"] = Kind.Green, ["m_dales"] = Kind.Green, ["mdalesdn"] = Kind.Green, ["finger"] = Kind.Green,
        ["hilly"] = Kind.Green, ["talonth8"] = Kind.Green, ["fortmdn"] = Kind.Green, ["fortmult"] = Kind.Green, ["fortsdn"] = Kind.Green,
        ["fortseig"] = Kind.Green, ["seige"] = Kind.Green, ["5o1"] = Kind.Green,
        ["ice"] = Kind.Ice, ["icedn"] = Kind.Ice, ["micey"] = Kind.Ice, ["miceydn"] = Kind.Ice, ["resfac"] = Kind.Ice,
        ["gracko"] = Kind.Ash, ["mgrack"] = Kind.Ash, ["dogbar"] = Kind.Ash, ["multidog"] = Kind.Ash, ["commsat"] = Kind.Ash,
        ["m_moon"] = Kind.Moon, ["mmoony"] = Kind.Moon, ["1o1"] = Kind.Moon, ["1o2"] = Kind.Moon, ["1o3"] = Kind.Moon, ["seekndes"] = Kind.Moon,
    };

    /// <summary>The colour of the dust machines kick up on each kind of planet.</summary>
    private static readonly Dictionary<Kind, Color> Dust = new()
    {
        [Kind.Desert] = new(0.72f, 0.62f, 0.48f), [Kind.Green] = new(0.55f, 0.48f, 0.38f), [Kind.Ice] = new(0.85f, 0.88f, 0.93f),
        [Kind.Ash] = new(0.45f, 0.28f, 0.2f), [Kind.Moon] = new(0.5f, 0.52f, 0.49f),
    };

    /// <summary>The wind, metres a second over the ground (x east, y south), for the grass and what blows.</summary>
    public static Vector2 Wind { get; private set; } = new(1.5f, 0.8f);

    /// <summary>How thick a storm's air is (0 to 1), and its colour; the sky and light follow it (PlanetLook).</summary>
    public static float Haze { get; private set; }

    public static Color HazeColour { get; private set; } = Colors.White;

    private Kind _kind;
    private Func<float> _time = null!;
    private Func<Vector3> _focus = null!;
    private Func<float> _distance = null!;
    private (float Start, float Length)[] _storms = [];
    private float[] _phases = [];
    private float _windAngle, _wet, _nextMeteor, _nextDevil;
    private Random _random = null!;

    private GpuParticles3D? _fall, _fallNear, _splash, _embers, _drift, _clouds;
    private Color _cloudColour = Colors.White;
    private ShaderMaterial? _driftMaterial;
    private FogVolume? _mist;
    private FogMaterial? _mistMaterial;
    private readonly List<(MeshInstance3D Mesh, ShaderMaterial Material, Vector3 At, float Born, float Life)> _devils = [];
    private AudioStreamPlayer? _windSound, _rainSound;

    /// <summary>For automated checks: MACHINES_WEATHER=&lt;0..1&gt; holds a storm that strong.</summary>
    private float? _forced;

    /// <summary>The weather of a planet, or null for planets without (stations, industrial worlds).</summary>
    public static Weather? Create(string planet, int seed, Func<float> time, Func<Vector3> focus, Func<float> distance)
    {
        EnsureGlobals();
        if (!GraphicsSettings.Remake || !Planets.TryGetValue(planet, out var kind))
            return null;
        Effects.DustTint = Dust[kind];
        var weather = new Weather { Name = "Weather", _kind = kind, _time = time, _focus = focus, _distance = distance };
        weather._random = new Random(seed ^ 0x5eed);
        weather.Plan();
        if (float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_WEATHER"), System.Globalization.CultureInfo.InvariantCulture, out var forced))
            weather._forced = Mathf.Clamp(forced, 0, 1);
        weather.Build();
        return weather;
    }

    /// <summary>
    /// The shader values the weather sets for every material that reads them: the wind (grass) and how wet the
    /// ground is (TerrainMaterials). Added once, before any shader that reads them is made.
    /// </summary>
    public static void EnsureGlobals()
    {
        if (_globals)
        {
            // A new match starts calm and dry.
            RenderingServer.GlobalShaderParameterSet("weather_wetness", 0f);
            return;
        }
        _globals = true;
        RenderingServer.GlobalShaderParameterAdd("weather_wind", RenderingServer.GlobalShaderParameterType.Vec2, Wind);
        RenderingServer.GlobalShaderParameterAdd("weather_wetness", RenderingServer.GlobalShaderParameterType.Float, 0f);
    }

    private static bool _globals;

    // ---- when ----------------------------------------------------------------------------------

    /// <summary>
    /// The match's storms (the first after three to seven minutes, then one every ten to fifteen, each one and a half
    /// to three minutes long) and the phases of the slow swell of light weather, all from the seed.
    /// </summary>
    private void Plan()
    {
        var storms = new List<(float, float)>();
        var t = 180 + (float)_random.NextDouble() * 240;
        while (t < 4 * 3600)
        {
            var length = 90 + (float)_random.NextDouble() * 90;
            storms.Add((t, length));
            t += length + 600 + (float)_random.NextDouble() * 300;
        }
        _storms = [.. storms];
        _phases = [.. Enumerable.Range(0, 4).Select(_ => (float)_random.NextDouble() * Mathf.Tau)];
        _windAngle = (float)_random.NextDouble() * Mathf.Tau;
        _nextMeteor = 5;
        _nextDevil = 20;
    }

    /// <summary>How strong the storm is at a time: rising and falling over 25 seconds at its ends.</summary>
    private float StormAt(float t)
    {
        if (_forced is { } forced)
            return forced;
        foreach (var (start, length) in _storms)
        {
            if (t < start - 30)
                break;
            if (t <= start + length + 30)
                return Mathf.SmoothStep(0, 1, (t - start) / 25) * Mathf.SmoothStep(0, 1, (start + length - t) / 25);
        }
        return 0;
    }

    /// <summary>The light weather's swell, 0 to 1, over periods of a few minutes.</summary>
    private float LightAt(float t) =>
        Mathf.Clamp(0.5f + 0.3f * Mathf.Sin(t / 170 + _phases[0]) + 0.2f * Mathf.Sin(t / 61 + _phases[1]) + 0.1f * Mathf.Sin(t / 23 + _phases[2]), 0, 1);

    // ---- what ----------------------------------------------------------------------------------

    private void Build()
    {
        switch (_kind)
        {
            case Kind.Green:
                (_fall, _fallNear, _splash) = Rain();
                _mistMaterial = new FogMaterial { Albedo = new Color(0.85f, 0.88f, 0.92f), HeightFalloff = 0.12f, EdgeFade = 0.3f, Density = 0 };
                _mist = new FogVolume { Size = new Vector3(600, 30, 600), Shape = RenderingServer.FogVolumeShape.Box, Material = _mistMaterial, Name = "Mist" };
                AddChild(_mist);
                break;
            case Kind.Ice:
                (_fall, _fallNear) = (Flakes(6000, "circle_05", new Color(0.95f, 0.97f, 1f, 0.9f), 1.6f, 0.24f), Flakes(1200, "circle_05", new Color(0.95f, 0.97f, 1f, 0.9f), 1.6f, 0.18f));
                _drift = Drift(new Color(0.95f, 0.97f, 1f, 0.7f));
                _clouds = Clouds(_cloudColour = new Color(0.9f, 0.93f, 0.97f));
                break;
            case Kind.Desert:
                // Lighter than the sand, so the wisps show against it.
                _drift = Drift(new Color(0.97f, 0.88f, 0.72f, 0.8f));
                (_fall, _fallNear) = (Flakes(5000, "dirt_03", new Color(0.85f, 0.66f, 0.44f, 0.8f), 0.4f, 0.09f, sideways: true),
                    Flakes(1500, "dirt_03", new Color(0.85f, 0.66f, 0.44f, 0.7f), 0.4f, 0.07f, sideways: true));
                _clouds = Clouds(_cloudColour = new Color(0.82f, 0.62f, 0.4f));
                break;
            case Kind.Ash:
                (_fall, _fallNear) = (Flakes(3000, "dirt_02", new Color(0.62f, 0.6f, 0.58f, 0.95f), 0.7f, 0.25f), Flakes(500, "dirt_02", new Color(0.62f, 0.6f, 0.58f, 0.95f), 0.7f, 0.18f));
                _embers = Embers();
                _clouds = Clouds(_cloudColour = new Color(0.5f, 0.4f, 0.35f));
                break;
        }
        foreach (var node in new Node?[] { _fall, _fallNear, _splash, _embers, _drift, _clouds })
            if (node is not null)
                AddChild(node);
        if (_fall is not null)
        {
            // The ground under the view stops what falls (splashing rain): a height field of what is drawn, following
            // the camera.
            AddChild(new GpuParticlesCollisionHeightField3D
            {
                Size = new Vector3(420, 900, 420), Resolution = GpuParticlesCollisionHeightField3D.ResolutionEnum.Resolution1024,
                UpdateMode = GpuParticlesCollisionHeightField3D.UpdateModeEnum.WhenMoved, FollowCameraEnabled = true, Name = "Ground",
            });
        }
        if (_kind != Kind.Moon)
        {
            // The airless moons are silent.
            _windSound = Loop(WindSound.Value, "Wind");
            if (_kind == Kind.Green)
                _rainSound = Loop(RainSound.Value, "Rain");
        }
    }

    public override void _Process(double delta)
    {
        var t = _time();
        var storm = StormAt(t);
        var light = _forced is not null ? 1 : LightAt(t);
        var dt = (float)delta;
        var focus = _focus();
        var distance = _distance();
        var camera = GetViewport().GetCamera3D();

        // The wind swings slowly round and freshens with the weather.
        var angle = _windAngle + 0.6f * Mathf.Sin(t / 500 + _phases[3]);
        var speed = _kind == Kind.Moon ? 0 : 1.5f + 4 * light + 13 * storm;
        Wind = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;
        RenderingServer.GlobalShaderParameterSet("weather_wind", Wind);

        // What falls, and how much of it: rain only in showers; snow now and then and in blizzards; sand only blown by
        // a storm; ash always a little.
        var fall = _kind switch
        {
            Kind.Green => storm,
            Kind.Ice => Mathf.Max(0, light - 0.45f) * 1.4f + storm,
            Kind.Desert => storm,
            Kind.Ash => 0.25f + 0.3f * light + 0.6f * storm,
            _ => 0,
        };
        var reach = Mathf.Clamp(distance * 0.75f, 40, 220);
        // Seen from high up, what falls is drawn larger, or it would vanish under a pixel.
        var seen = Mathf.Clamp(distance / 45, 1, 5);
        Place(_fall, focus + new Vector3(0, _kind == Kind.Desert ? 8 : 35, 0), reach, Mathf.Clamp(fall, 0, 1) * Mathf.Clamp(reach * reach / (220 * 220), 0.25f, 1));
        if (camera is not null)
            Place(_fallNear, camera.GlobalPosition - camera.GlobalBasis.Z * 18 + Vector3.Up * 12, 25, Mathf.Clamp(fall, 0, 1));
        Blow(_fall, fall, seen);
        Blow(_fallNear, fall, Mathf.Sqrt(seen));

        if (_drift is not null && _driftMaterial is not null)
        {
            // Over the ground in the wind: always a little on the desert, more as it freshens.
            var drift = _kind == Kind.Desert ? 0.25f + 0.45f * light + 0.6f * storm : 0.15f + 0.4f * light + 0.7f * storm;
            _drift.GlobalPosition = focus;
            _drift.AmountRatio = Mathf.Clamp(drift, 0.05f, 1);
            _driftMaterial.SetShaderParameter("wind", Wind);
            _driftMaterial.SetShaderParameter("area", Mathf.Clamp(distance * 0.8f, 40, 260));
            _driftMaterial.SetShaderParameter("strength", Mathf.Clamp(drift, 0, 1));
            _driftMaterial.SetShaderParameter("size", Mathf.Sqrt(seen));
        }
        if (_embers is not null)
            _embers.GlobalPosition = focus + Vector3.Up * 2;
        if (_clouds?.ProcessMaterial is ParticleProcessMaterial clouds)
        {
            // In a storm, billows of sand, snow or ash roll over the ground with the wind.
            var billows = _kind == Kind.Ash ? 0.3f * light + storm : storm;
            _clouds.GlobalPosition = focus + Vector3.Up * 4;
            _clouds.AmountRatio = Mathf.Clamp(billows, 0, 1);
            _clouds.Emitting = billows > 0.03f;
            clouds.EmissionBoxExtents = new Vector3(Mathf.Clamp(distance, 60, 260), 4, Mathf.Clamp(distance, 60, 260));
            clouds.Direction = new Vector3(Wind.X, 0.05f, Wind.Y).Normalized();
            clouds.InitialVelocityMin = Wind.Length() * 0.6f;
            clouds.InitialVelocityMax = Wind.Length() * 1.0f;
            // As bright as the day, dark at night.
            if (_clouds.DrawPass1 is QuadMesh { Material: StandardMaterial3D cloudLook })
                cloudLook.AlbedoColor = _cloudColour * Mathf.Lerp(1, 0.18f, PlanetLook.Night);
        }

        // Rain wets the ground over a minute; it dries over three.
        var target = _kind == Kind.Green && storm > 0.15f ? 1 : 0;
        _wet = Mathf.MoveToward(_wet, target, dt / (target > _wet ? 50 : 180));
        RenderingServer.GlobalShaderParameterSet("weather_wetness", _wet);

        // Storms thicken the air and dim the sun (PlanetLook reads this).
        Haze = _kind switch { Kind.Desert => storm, Kind.Ice => storm * 0.7f, Kind.Green => storm * 0.45f, Kind.Ash => storm * 0.5f, _ => 0 };
        HazeColour = _kind switch
        {
            Kind.Desert => new Color(0.78f, 0.58f, 0.36f),
            Kind.Ice => new Color(0.86f, 0.89f, 0.94f),
            Kind.Green => new Color(0.45f, 0.48f, 0.52f),
            _ => new Color(0.35f, 0.25f, 0.2f),
        };

        if (_mist is not null && _mistMaterial is not null)
        {
            // Mist in the hollows when the sun is low on the planets that have mornings, and in damp spells after rain.
            var low = PlanetLook.SunElevation is var e and > -2 and < 14 ? 1 - Mathf.Abs(e - 6) / 8 : 0;
            var mist = Mathf.Clamp(Mathf.Max(low, _wet * 0.6f), 0, 1);
            _mist.GlobalPosition = new Vector3(focus.X, HeightField.Current?.At(focus.X, focus.Z) ?? focus.Y, focus.Z);
            _mistMaterial.Density = 0.025f * mist;
        }

        if (_kind == Kind.Desert)
            DustDevils(t, light, storm, focus, distance);
        if (_kind == Kind.Moon && camera is not null)
            ShootingStars(t, storm, camera);

        if (_windSound is not null)
            _windSound.VolumeDb = Mathf.LinearToDb(0.04f + 0.08f * light + 0.5f * Haze + 0.15f * storm);
        if (_rainSound is not null)
            _rainSound.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, 0.6f * Mathf.Clamp(fall, 0, 1)));
    }

    /// <summary>Puts an emitter's box over a point, as wide as the view, and sets how much of it falls.</summary>
    private static void Place(GpuParticles3D? particles, Vector3 at, float reach, float amount)
    {
        if (particles is null)
            return;
        particles.GlobalPosition = at;
        particles.AmountRatio = Mathf.Clamp(amount, 0, 1);
        particles.Emitting = amount > 0.01f;
        if (particles.ProcessMaterial is ParticleProcessMaterial process)
            process.EmissionBoxExtents = new Vector3(reach, process.EmissionBoxExtents.Y, reach);
    }

    /// <summary>What falls drifts with the wind, drawn <paramref name="scale"/> times its size.</summary>
    private void Blow(GpuParticles3D? particles, float strength, float scale)
    {
        if (particles?.ProcessMaterial is not ParticleProcessMaterial process)
            return;
        var fallSpeed = _kind == Kind.Green ? 26f : _kind == Kind.Desert ? 1 : 1.6f;
        process.Direction = new Vector3(Wind.X, -fallSpeed, Wind.Y).Normalized();
        var speed = new Vector2(Wind.Length(), fallSpeed).Length();
        process.InitialVelocityMin = speed * 0.85f;
        process.InitialVelocityMax = speed * 1.15f;
        process.ScaleMin = 0.7f * scale;
        process.ScaleMax = 1.3f * scale;
    }

    // ---- the particles --------------------------------------------------------------------------

    /// <summary>Rain: streaks falling 26 m/s, splashing where they meet the ground; and a few close to the camera.</summary>
    private static (GpuParticles3D Far, GpuParticles3D Near, GpuParticles3D Splash) Rain()
    {
        var splash = new GpuParticles3D
        {
            Name = "Splash", Amount = 4000, Lifetime = 0.35f, Emitting = false, LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(-Vector3.One * 400, Vector3.One * 800),
            DrawPass1 = Effects.Billboard(Effects.Particle("circle_02"), additive: false),
            ProcessMaterial = new ParticleProcessMaterial
            {
                Direction = Vector3.Up, Spread = 60, InitialVelocityMin = 0.5f, InitialVelocityMax = 1.5f, Gravity = new Vector3(0, -6, 0),
                ScaleMin = 0.15f, ScaleMax = 0.3f, ScaleCurve = Effects.Grow(0.4f),
                ColorRamp = Effects.Ramp(new Color(0.85f, 0.9f, 1, 0.5f), new Color(0.85f, 0.9f, 1, 0.3f), new Color(0.85f, 0.9f, 1, 0)),
            },
        };
        GpuParticles3D Streaks(string name, int amount, float reach, bool splashes)
        {
            var process = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(reach, 2, reach),
                Direction = Vector3.Down, Spread = 2, InitialVelocityMin = 24, InitialVelocityMax = 29, Gravity = Vector3.Zero,
                ScaleMin = 0.8f, ScaleMax = 1.2f,
                CollisionMode = ParticleProcessMaterial.CollisionModeEnum.HideOnContact,
            };
            if (splashes)
                process.SubEmitterMode = ParticleProcessMaterial.SubEmitterModeEnum.AtCollision;
            var material = new StandardMaterial3D
            {
                AlbedoTexture = Effects.Particle("trace_06"), AlbedoColor = new Color(0.8f, 0.85f, 0.95f, 0.55f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, VertexColorUseAsAlbedo = true, DisableReceiveShadows = true,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Disabled,
            };
            return new GpuParticles3D
            {
                Name = name, Amount = amount, Lifetime = 2.2f, Emitting = false, LocalCoords = false, CollisionBaseSize = 0.02f,
                TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(-Vector3.One * 400, Vector3.One * 800),
                DrawPass1 = new QuadMesh { Size = new Vector2(0.25f, 1.2f), Material = material },
                ProcessMaterial = process,
            };
        }
        var far = Streaks("Rain", 9000, 120, splashes: true);
        far.SubEmitter = "../Splash";
        return (far, Streaks("RainNear", 1500, 25, splashes: false), splash);
    }

    /// <summary>Snow, ash or blown sand: flakes or grit drifting down (or along, for sand) and tumbling.</summary>
    private static GpuParticles3D Flakes(int amount, string picture, Color colour, float fall, float size, bool sideways = false) => new()
    {
        Name = "Flakes", Amount = amount, Lifetime = sideways ? 4 : 9, Emitting = false, LocalCoords = false, CollisionBaseSize = 0.05f,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(-Vector3.One * 400, Vector3.One * 800),
        DrawPass1 = new QuadMesh
        {
            Size = Vector2.One * size * (sideways ? 3 : 1),
            Material = new StandardMaterial3D
            {
                AlbedoTexture = Effects.Particle(picture), AlbedoColor = colour, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, VertexColorUseAsAlbedo = true,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
            },
        },
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(120, sideways ? 6 : 3, 120),
            Direction = Vector3.Down, Spread = 10, InitialVelocityMin = fall * 0.8f, InitialVelocityMax = fall * 1.2f, Gravity = Vector3.Zero,
            TurbulenceEnabled = true, TurbulenceNoiseStrength = sideways ? 0.6f : 1.2f, TurbulenceNoiseScale = 6, TurbulenceInfluenceMin = 0.05f, TurbulenceInfluenceMax = 0.15f,
            ScaleMin = 0.6f, ScaleMax = 1.4f, AngleMin = -180, AngleMax = 180,
            ColorRamp = Effects.Ramp(Colors.White with { A = 0 }, Colors.White, Colors.White with { A = 0 }),
            CollisionMode = ParticleProcessMaterial.CollisionModeEnum.HideOnContact,
        },
    };

    /// <summary>
    /// A storm's billows: big clouds of sand, snow or ash (Kenney's smoke pictures, turned every way) rolling low over
    /// the ground with the wind, lit by the sun.
    /// </summary>
    private static GpuParticles3D Clouds(Color colour)
    {
        var material = new StandardMaterial3D
        {
            AlbedoTexture = Effects.Particle("smoke_07"), AlbedoColor = colour, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true, VertexColorUseAsAlbedo = true,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled, DisableReceiveShadows = true,
            // Lit by the day as a whole, not by the sun from one side: a billow is mostly the light scattered in it.
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            // Fade where they cut into the ground, rather than showing a hard edge.
            ProximityFadeEnabled = true, ProximityFadeDistance = 6,
        };
        return new GpuParticles3D
        {
            Name = "StormClouds", Amount = 90, Lifetime = 14, Preprocess = 14, LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(-Vector3.One * 400, Vector3.One * 800),
            DrawPass1 = new QuadMesh { Size = Vector2.One, Material = material },
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(150, 4, 150),
                Direction = Vector3.Right, Spread = 15, InitialVelocityMin = 4, InitialVelocityMax = 8, Gravity = Vector3.Zero,
                ScaleMin = 18, ScaleMax = 40, ScaleCurve = Effects.Grow(0.6f),
                AngleMin = -180, AngleMax = 180, AngularVelocityMin = -6, AngularVelocityMax = 6,
                ColorRamp = Effects.Ramp(Colors.White with { A = 0 }, Colors.White with { A = 0.65f }, Colors.White with { A = 0 }),
            },
        };
    }

    /// <summary>Embers rising over Gracko and the red moons, glowing as they go.</summary>
    private static GpuParticles3D Embers() => new()
    {
        Name = "Embers", Amount = 160, Lifetime = 6, LocalCoords = false,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(-Vector3.One * 400, Vector3.One * 800),
        DrawPass1 = Effects.Billboard(Effects.Particle("star_01"), additive: true),
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(110, 1, 110),
            Direction = Vector3.Up, Spread = 25, InitialVelocityMin = 0.6f, InitialVelocityMax = 1.6f, Gravity = new Vector3(0, 0.3f, 0),
            TurbulenceEnabled = true, TurbulenceNoiseStrength = 1.5f, TurbulenceNoiseScale = 3,
            ScaleMin = 0.25f, ScaleMax = 0.45f, AngleMin = -180, AngleMax = 180,
            ColorRamp = Effects.Ramp(new Color(4, 1.4f, 0.3f, 0), new Color(3.5f, 1.1f, 0.2f, 1), new Color(1.5f, 0.3f, 0.05f, 0)),
        },
    };

    /// <summary>
    /// Sand or snow drifting over the ground: wisps that run with the wind, each laid on the ground's height (from
    /// the height field) where it is, over the area round the view.
    /// </summary>
    private GpuParticles3D Drift(Color colour)
    {
        var field = HeightField.Current;
        var heights = new float[(field?.VerticesX ?? 1) * (field?.VerticesY ?? 1)];
        if (field is not null)
            for (var y = 0; y < field.VerticesY; y++)
                for (var x = 0; x < field.VerticesX; x++)
                    heights[y * field.VerticesX + x] = field.Vertex(x, y);
        var bytes = new byte[heights.Length * 4];
        Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
        var heightMap = ImageTexture.CreateFromImage(Image.CreateFromData(field?.VerticesX ?? 1, field?.VerticesY ?? 1, false, Image.Format.Rf, bytes));
        _driftMaterial = new ShaderMaterial { Shader = DriftShader.Value };
        _driftMaterial.SetShaderParameter("height_map", heightMap);
        _driftMaterial.SetShaderParameter("spacing", field?.Spacing ?? 4);
        _driftMaterial.SetShaderParameter("vertices", new Vector2(field?.VerticesX ?? 1, field?.VerticesY ?? 1));
        _driftMaterial.SetShaderParameter("tint", colour);
        return new GpuParticles3D
        {
            Name = "Drift", Amount = 700, Lifetime = 3.5f, LocalCoords = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityAabb = new Aabb(-Vector3.One * 400, Vector3.One * 800),
            ProcessMaterial = _driftMaterial,
            DrawPass1 = new PlaneMesh
            {
                Size = Vector2.One,
                Material = new StandardMaterial3D
                {
                    AlbedoTexture = Wisp.Value, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled, DisableReceiveShadows = true,
                },
            },
        };
    }

    private static readonly Lazy<Shader> DriftShader = new(() => new Shader
    {
        Code = """
            shader_type particles;
            uniform sampler2D height_map : filter_linear;
            uniform float spacing = 4.0;
            uniform vec2 vertices = vec2(401.0);
            uniform vec2 wind = vec2(3.0, 1.0);
            uniform float area = 80.0;
            uniform float strength = 0.5;
            uniform float size = 1.0;
            uniform vec4 tint = vec4(1.0);

            float rnd(inout uint s) {
                s = s * 747796405u + 2891336453u;
                uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
                return float((w >> 22u) ^ w) / 4294967295.0;
            }
            float ground(vec2 p) { return texture(height_map, (p / spacing + 0.5) / vertices).r; }

            void start() {
                uint s = NUMBER * 1664525u + RANDOM_SEED * 22695477u + 1u;
                vec2 p = EMISSION_TRANSFORM[3].xz + (vec2(rnd(s), rnd(s)) - 0.5) * 2.0 * area;
                CUSTOM = vec4(rnd(s), 0.0, 0.6 + rnd(s) * 0.9, 0.0);
                TRANSFORM = mat4(1.0);
                TRANSFORM[3].xyz = vec3(p.x, ground(p), p.y);
                VELOCITY = vec3(wind.x, 0.0, wind.y) * (0.8 + 0.5 * rnd(s));
            }

            void process() {
                CUSTOM.y += DELTA / LIFETIME;
                vec3 pos = TRANSFORM[3].xyz + VELOCITY * DELTA;
                pos.y = ground(pos.xz) + 0.18 + 0.06 * sin(TIME * 2.7 + CUSTOM.x * 30.0);
                vec2 d = normalize(wind + vec2(1e-4, 0.0));
                float along = (3.0 + length(wind) * 0.35) * CUSTOM.z * size;
                float across = 0.9 * CUSTOM.z * size;
                TRANSFORM[0] = vec4(d.x * along, 0.0, d.y * along, 0.0);
                TRANSFORM[1] = vec4(0.0, 1.0, 0.0, 0.0);
                TRANSFORM[2] = vec4(-d.y * across, 0.0, d.x * across, 0.0);
                TRANSFORM[3].xyz = pos;
                float fade = smoothstep(0.0, 0.25, CUSTOM.y) * (1.0 - smoothstep(0.65, 1.0, CUSTOM.y));
                // Only over gentle ground: a flat wisp over a slope would hang in the air.
                vec2 slope = vec2(ground(pos.xz + vec2(2.0, 0.0)) - ground(pos.xz - vec2(2.0, 0.0)),
                    ground(pos.xz + vec2(0.0, 2.0)) - ground(pos.xz - vec2(0.0, 2.0))) / 4.0;
                fade *= 1.0 - smoothstep(0.12, 0.3, length(slope));
                COLOR = vec4(tint.rgb, tint.a * fade * strength);
            }
            """,
    });

    /// <summary>Now and then a dust devil wanders over the desert with the wind, more of them when it blows.</summary>
    private void DustDevils(float t, float light, float storm, Vector3 focus, float distance)
    {
        var now = Time.GetTicksMsec() / 1000f;
        for (var i = _devils.Count - 1; i >= 0; i--)
        {
            var (mesh, material, at, born, life) = _devils[i];
            var age = (now - born) / life;
            if (age >= 1)
            {
                mesh.QueueFree();
                _devils.RemoveAt(i);
                continue;
            }
            var here = at + new Vector3(Wind.X, 0, Wind.Y) * 0.6f * (now - born);
            mesh.GlobalPosition = new Vector3(here.X, HeightField.Current?.At(here.X, here.Z) ?? 0, here.Z);
            material.SetShaderParameter("strength", Mathf.SmoothStep(0, 1, age * 6) * Mathf.SmoothStep(0, 1, (1 - age) * 5));
        }
        if (t < _nextDevil || _devils.Count >= 2 || storm > 0.5f)
            return;
        _nextDevil = t + 40 + (float)_random.NextDouble() * (light > 0.5f ? 50 : 120);
        var r = Mathf.Clamp(distance * 0.5f, 30, 160);
        var start = focus + new Vector3(((float)_random.NextDouble() - 0.5f) * 2 * r, 0, ((float)_random.NextDouble() - 0.5f) * 2 * r);
        var shader = new ShaderMaterial { Shader = DevilShader.Value };
        shader.SetShaderParameter("noise", Swirl.Value);
        shader.SetShaderParameter("tint", Effects.DustTint);
        var size = 0.7f + (float)_random.NextDouble() * 0.6f;
        var devil = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 3.2f * size, BottomRadius = 0.5f * size, Height = 22 * size, CapTop = false, CapBottom = false, RadialSegments = 24, Rings = 6 },
            MaterialOverride = shader, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "DustDevil",
        };
        devil.Position = Vector3.Up * 11 * size;
        var holder = new MeshInstance3D { Name = "DustDevilBase" };
        AddChild(holder);
        holder.AddChild(devil);
        _devils.Add((holder, shader, start, Time.GetTicksMsec() / 1000f, 20 + (float)_random.NextDouble() * 15));
    }

    private static readonly Lazy<Shader> DevilShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
            uniform sampler2D noise : filter_linear_mipmap, repeat_enable;
            uniform vec3 tint = vec3(0.72, 0.62, 0.48);
            uniform float strength = 0.0;
            void fragment() {
                // The dust spins up the funnel: the picture turns faster at the bottom and climbs.
                vec2 uv = vec2(UV.x * 2.0 + TIME * (0.9 + 0.6 * (1.0 - UV.y)), UV.y * 1.5 + TIME * 0.35);
                float n = texture(noise, uv).r * texture(noise, uv * vec2(1.7, 0.6) + 0.37).r;
                float ends = smoothstep(0.0, 0.25, UV.y) * smoothstep(1.0, 0.55, UV.y);
                ALBEDO = tint;
                ALPHA = clamp(n * 1.8 - 0.25, 0.0, 1.0) * ends * strength * 0.75;
            }
            """,
    });

    /// <summary>On the airless moons a shooting star now and then; in a shower one every second or two.</summary>
    private void ShootingStars(float t, float storm, Camera3D camera)
    {
        if (t < _nextMeteor)
            return;
        _nextMeteor = t + (storm > 0.3f ? 0.6f + (float)_random.NextDouble() * 1.5f : 12 + (float)_random.NextDouble() * 30);
        var heading = (float)_random.NextDouble() * Mathf.Tau;
        var height = Mathf.DegToRad(20 + (float)_random.NextDouble() * 45);
        var direction = new Vector3(Mathf.Cos(height) * Mathf.Cos(heading), Mathf.Sin(height), Mathf.Cos(height) * Mathf.Sin(heading));
        var distance = camera.Far * 0.7f;
        var across = direction.Cross(Vector3.Up).Normalized().Rotated(direction, ((float)_random.NextDouble() - 0.5f) * 1.2f);
        var length = distance * 0.05f;
        var streak = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(length, length * 0.012f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = Trail.Value, AlbedoColor = new Color(2.5f, 2.4f, 2.1f, 1), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, DisableFog = true,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = "ShootingStar",
        };
        AddChild(streak);
        var from = camera.GlobalPosition + direction * distance;
        streak.GlobalPosition = from;
        // Facing the camera, its long side along its flight.
        streak.GlobalBasis = new Basis(across, (-direction).Cross(across).Normalized(), -direction);
        var tween = streak.CreateTween();
        var flight = 0.6f + (float)_random.NextDouble() * 0.6f;
        tween.TweenProperty(streak, "global_position", from + across * length * 3, flight);
        tween.Parallel().TweenProperty(streak.MaterialOverride, "albedo_color:a", 0f, flight).SetEase(Tween.EaseType.In);
        tween.TweenCallback(Callable.From(streak.QueueFree));
    }

    // ---- sounds ---------------------------------------------------------------------------------

    private AudioStreamPlayer Loop(AudioStreamWav stream, string name)
    {
        var player = new AudioStreamPlayer { Stream = stream, Name = name, VolumeDb = -60, Autoplay = true };
        AddChild(player);
        return player;
    }

    private const int Rate = 22050;

    /// <summary>Wind: brown noise rising and falling in gusts, looped over nine seconds.</summary>
    private static readonly Lazy<AudioStreamWav> WindSound = new(() => Synth(9, (random, i, state) =>
    {
        state[0] = state[0] * 0.997f + ((float)random.NextDouble() - 0.5f) * 0.06f;
        state[1] += (((float)random.NextDouble() - 0.5f) * 0.0004f - state[1] * 0.00002f);
        var gust = 0.55f + 0.45f * Mathf.Sin(i / (float)Rate * 0.9f) * Mathf.Sin(i / (float)Rate * 0.37f + 1);
        // A gentle low-pass for the howl.
        state[2] += (state[0] - state[2]) * 0.08f;
        return state[2] * 3.2f * gust;
    }));

    /// <summary>Rain: hissing noise and the patter of drops, looped over seven seconds.</summary>
    private static readonly Lazy<AudioStreamWav> RainSound = new(() => Synth(7, (random, i, state) =>
    {
        var white = (float)random.NextDouble() * 2 - 1;
        // High-passed hiss: the noise less its slow part.
        state[0] += (white - state[0]) * 0.05f;
        var hiss = (white - state[0]) * 0.18f;
        // Drops: now and then a short ping that dies away.
        if (random.NextDouble() < 90.0 / Rate)
            (state[1], state[2]) = (0.2f + (float)random.NextDouble() * 0.35f, 2000 + (float)random.NextDouble() * 3000);
        state[1] *= 0.995f;
        var drop = state[1] * Mathf.Sin(i * Mathf.Tau * state[2] / Rate);
        return hiss + drop * 0.4f;
    }));

    /// <summary>Some seconds of sound from a sample function, the end blended into the start so it loops without a seam.</summary>
    private static AudioStreamWav Synth(float seconds, Func<Random, int, float[], float> sample)
    {
        var random = new Random(7);
        var state = new float[4];
        var fade = Rate;
        var total = (int)(seconds * Rate) + fade;
        var raw = new float[total];
        for (var i = 0; i < total; i++)
            raw[i] = sample(random, i, state);
        var length = total - fade;
        var data = new byte[length * 2];
        for (var i = 0; i < length; i++)
        {
            var v = raw[i];
            if (i < fade)
                v = Mathf.Lerp(raw[length + i], raw[i], i / (float)fade);
            var s = (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue);
            (data[i * 2], data[i * 2 + 1]) = ((byte)(s & 0xff), (byte)((s >> 8) & 0xff));
        }
        return new AudioStreamWav
        {
            Data = data, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = length,
        };
    }

    // ---- pictures -------------------------------------------------------------------------------

    /// <summary>A shooting star's trail: bright at its head, fading behind.</summary>
    private static readonly Lazy<Texture2D> Trail = new(() => Effects.Generate(64, (x, y) =>
        new Color(1, 1, 1, Mathf.Clamp((x + 1) / 2, 0, 1) * Mathf.Clamp(1 - Mathf.Abs(y) * 1.2f, 0, 1))));

    /// <summary>A wisp of blown sand or snow: a long streak with ragged density along it.</summary>
    private static readonly Lazy<Texture2D> Wisp = new(() =>
    {
        var noise = new FastNoiseLite { Frequency = 0.08f, Seed = 11 };
        return Effects.Generate(64, (x, y) =>
        {
            var along = Mathf.Clamp(1 - Mathf.Abs(x), 0, 1);
            var across = Mathf.Clamp(1 - Mathf.Abs(y) * 1.3f, 0, 1);
            var ragged = 0.5f + 0.5f * noise.GetNoise2D(x * 40, y * 6);
            return new Color(1, 1, 1, Mathf.Pow(along, 0.7f) * across * across * ragged);
        });
    });

    /// <summary>Swirling noise for the dust devils, repeating.</summary>
    private static readonly Lazy<Texture2D> Swirl = new(() => new NoiseTexture2D
    {
        Width = 128, Height = 128, Seamless = true, GenerateMipmaps = true,
        Noise = new FastNoiseLite { Frequency = 0.05f, FractalOctaves = 3, Seed = 4 },
    });
}
