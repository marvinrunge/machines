using Godot;
using Machines.Sim.Story;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Machines.Game;

// Package E's deck (plan stufe7 §4 E, design-escape §13 "View"): what the escape's hazards are, drawn from the sim's state
// (StoryHazards) every frame and changing nothing in it. The hold's containers and the rack on their rails, lerped between
// the ticks, with the amber lamp of a lane about to slide and its light strip running the way it will go; the black-amber
// stripes of the lanes; the gallery's five jets (warning: steam and a pulsing red glow; burning: a band of fire), their
// nozzles, J4's valve plate and hand wheel, the lane lever; the shutter X lowering in its frame, held at half, shut, rammed
// and gone; the footbridge as grating over a glowing shaft, sagging as it creaks and falling into the shaft; the Notgang's
// collapse as a heap of wreckage with the fire face burning at its edge. Built from the kit's props (crate_large,
// crate_small, debris_*, kit_door_leaf, kit_floor_grate) and plain shapes where the kit has nothing (the strips, the
// nozzles, the wheel, the lever, the shaft's walls).
public partial class DeckView
{
    /// <summary>The footbridge's room (R19): its floor is the view's own, so it can fall (EscapeFloor).</summary>
    private const string BridgeZone = "R19";

    /// <summary>How deep the shaft under the footbridge is drawn, metres.</summary>
    private const float ShaftDepth = 38;

    private static readonly Color Amber = new(1f, 0.62f, 0.12f), SteelDark = new(0.12f, 0.12f, 0.13f);

    /// <summary>The effects the escape's view throws sparks with (Game sets it).</summary>
    public Effects? Fx { get; set; }

    private Node3D? _escapeRoot;
    private double _escapeClock;

    private sealed class RailView
    {
        public required Node3D Body;
        public required OmniLight3D Lamp;
        public AudioStreamPlayer3D? Scrape;
        public double NextSparks;
    }

    private readonly Dictionary<string, RailView> _railViews = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ShaderMaterial> _laneStrips = [];

    private sealed class JetView
    {
        public required Node3D Band;
        public required Vector3 Nozzle;
        public Effects.JetLook Look;
        public double Since;
        public AudioStreamPlayer3D? Roar;
    }

    private readonly JetView?[] _jets = new JetView?[6];
    private Node3D? _wheel, _lever;

    private Node3D? _shutter;
    private float _shutterHeight, _shutterShown;
    private Vector3 _shutterAt;
    private readonly List<OmniLight3D> _shutterLamps = [];
    private AudioStreamPlayer3D? _shutterGrind;
    private bool _shutterGone;

    private Node3D? _bridge;
    private readonly List<MeshInstance3D> _bridgeSegments = [];
    private bool _bridgeDown;

    private Node3D? _heap;
    private readonly List<GpuParticles3D> _fireFace = [];
    private OmniLight3D? _fireGlow;

    private OmniLight3D? _lockGlow;

    private Node3D EscapeRoot()
    {
        if (_escapeRoot is null)
        {
            _escapeRoot = new Node3D { Name = "Escape" };
            AddChild(_escapeRoot);
        }
        return _escapeRoot;
    }

    // ---- the footbridge's floor (called from Floors) -----------------------------------------------------------

    /// <summary>
    /// A floor module of the footbridge (R19), put up as the view's own grating over the shaft instead of in the deck's
    /// batches, so that it can sag and fall; true when it was one (the deck's floor leaves it out).
    /// </summary>
    private bool EscapeFloor(DeckFloor floor)
    {
        const int m = DeckLayout.Module;
        var middle = new NVector2(floor.X + m / 2f, floor.Y + m / 2f);
        if (Layout.Deck.Zone(BridgeZone) is not { } zone || !zone.Contains(middle) || Props.Get("kit_floor_grate") is not { } model)
            return false;
        if (_bridge is null)
        {
            _bridge = new Node3D { Name = "Footbridge" };
            AddChild(_bridge);
            Shaft(zone);
        }
        var size = model.Size;
        var place = Props.Place(model, Props.World(new NVector3(middle, 0)) - new Vector3(0, size.Y, 0), 90, new Vector3(m / size.X, 1, m / size.Z));
        var segment = new MeshInstance3D
        {
            Name = $"Segment{_bridgeSegments.Count}",
            Mesh = model.Mesh,
            Transform = place,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = 1 | TerrainBuilder.GroundLayer,
        };
        segment.SetMeta("rest", place.Origin);
        _bridge.AddChild(segment);
        _bridgeSegments.Add(segment);
        return true;
    }

    /// <summary>The shaft under the footbridge: dark walls down from its edges, and its glow far below (seen through the grating).</summary>
    private void Shaft(Machines.Import.Model.DeckZone zone)
    {
        var shaft = new Node3D { Name = "Shaft" };
        _bridge!.AddChild(shaft);
        var (min, max) = (zone.Min, zone.Max);
        var material = new StandardMaterial3D { AlbedoColor = SteelDark, Roughness = 0.7f, Metallic = 0.4f };
        void Wall(Vector3 centre, Vector3 size) => shaft.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material,
            Position = centre,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        var (width, depth, mid) = (max.X - min.X, max.Y - min.Y, (min + max) / 2);
        Wall(new Vector3(mid.X, -ShaftDepth / 2, min.Y - 0.25f), new Vector3(width + 1, ShaftDepth, 0.5f));
        Wall(new Vector3(mid.X, -ShaftDepth / 2, max.Y + 0.25f), new Vector3(width + 1, ShaftDepth, 0.5f));
        Wall(new Vector3(min.X - 0.25f, -ShaftDepth / 2, mid.Y), new Vector3(0.5f, ShaftDepth, depth));
        Wall(new Vector3(max.X + 0.25f, -ShaftDepth / 2, mid.Y), new Vector3(0.5f, ShaftDepth, depth));
        var glow = Effects.ShaftGlow(new Vector2(width, depth), ShaftDepth - 1);
        glow.Position = new Vector3(mid.X, 0, mid.Y);
        shaft.AddChild(glow);
    }

    // ---- each frame --------------------------------------------------------------------------------------------

    /// <summary>
    /// The escape's hazards as the sim has them at <paramref name="tick"/>, <paramref name="share"/> of the way on to the next
    /// tick: the rails, the lanes' strips, the jets, the shutter, the bridge, the collapse; <paramref name="craneLock"/> where
    /// the crane's amber lock still hangs (null once shot), <paramref name="gasMain"/> where the gas main beside Y is whole.
    /// </summary>
    public void ShowEscape(StoryHazards hazards, long tick, float share, double delta, NVector2? craneLock, NVector2? gasMain = null)
    {
        _escapeClock += delta;
        ShowRails(hazards, share);
        ShowJets(hazards, tick);
        ShowShutter(hazards, delta);
        ShowBridge(hazards, tick);
        ShowCollapse(hazards);
        ShowLock(craneLock);
        ShowGas(hazards, tick, gasMain);
    }

    // ---- the hold: containers, the rack, the lanes ---------------------------------------------------------------

    private void ShowRails(StoryHazards hazards, float share)
    {
        if (hazards.Rails.Count == 0)
            return;
        if (_laneStrips.Count == 0)
            Lanes();
        foreach (var (lane, strip) in _laneStrips)
        {
            // A lane's strip runs the way its container will go from its telegraph on, and while it slides.
            var moving = hazards.Rails.FirstOrDefault(r => r.Lane == lane && r.State != RailState.Rest && r.Way != 0);
            strip.SetShaderParameter("active", moving is null ? 0f : 1f);
            strip.SetShaderParameter("way", (float)(moving?.Way ?? 0));
            strip.SetShaderParameter("clock", (float)_escapeClock);
        }
        foreach (var rail in hazards.Rails)
        {
            if (!_railViews.TryGetValue(rail.Name, out var view))
                _railViews[rail.Name] = view = RailBody(rail);
            var y = (rail.Centre - rail.Speed * (1 - share)) / 1000f;
            var x = (rail.X0 + rail.X1) / 2000f;
            view.Body.Position = new Vector3(x, 0, y);
            var t = (float)_escapeClock;
            view.Lamp.Visible = rail.State != RailState.Rest;
            view.Lamp.LightEnergy = rail.State == RailState.Telegraph ? (Mathf.Sin(t * 25) > 0 ? 4 : 0.3f) : 2.5f;
            var sliding = rail.State == RailState.Slide && Math.Abs(rail.Speed) > 20;
            if (sliding && view.Scrape is null)
                view.Scrape = StoryAudio.Current?.Sounds.LoopOn(view.Body, rail.IsRack ? "rack_plough" : "container_scrape", rail.IsRack ? 2 : 0);
            else if (!sliding && view.Scrape is not null)
            {
                StorySounds.StopLoop(view.Scrape);
                view.Scrape = null;
            }
            if (sliding && _escapeClock >= view.NextSparks && Fx is { } fx)
            {
                // The bottom edge at its front throws sparks back along the rail.
                view.NextSparks = _escapeClock + 0.07;
                var front = y + rail.Way * rail.HalfLength / 1000f;
                var side = (rail.X1 - rail.X0) / 2000f * (GD.Randf() < 0.5f ? -1 : 1);
                fx.ScrapeSparks(new Vector3(x + side, 0.2f, front), new Vector3(0, 0, rail.Way), Math.Abs(rail.Speed) > 250 ? 12 : 6);
            }
        }
    }

    /// <summary>A container (or the rack) of a rail, its bottom middle at the node's place.</summary>
    private RailView RailBody(StoryRail rail)
    {
        var width = (rail.X1 - rail.X0) / 1000f;
        var length = 2 * rail.HalfLength / 1000f;
        var height = rail.IsRack ? 5f : 4f;
        var body = new Node3D { Name = rail.Name };
        EscapeRoot().AddChild(body);
        if (rail.IsRack)
        {
            // A transport frame with heavy crates lashed on it: a steel base and the kit's large crates in two layers.
            body.AddChild(Box(new Vector3(width, 0.6f, length), new Vector3(0, 0.3f, 0), SteelDark));
            foreach (var (cx, cz, layer) in new[] { (-1, -1, 0), (1, -1, 0), (-1, 1, 0), (1, 1, 0), (-1, 0, 1), (1, 0, 1) })
                Crate(body, new Vector3(cx * width / 4, 0.6f + layer * 2.2f, cz * length / 4), new Vector3(width / 2 - 0.4f, 2.2f, length / 2 - 0.6f));
        }
        else
            Crate(body, Vector3.Zero, new Vector3(width, height, length));
        var lamp = new OmniLight3D
        {
            Name = "Lamp",
            LightColor = Amber,
            OmniRange = 16,
            LightEnergy = 0,
            Position = new Vector3(0, height + 1.5f, 0),
            Visible = false,
            ShadowEnabled = false,
            DistanceFadeEnabled = true,
            DistanceFadeBegin = 80,
            DistanceFadeLength = 20,
        };
        body.AddChild(lamp);
        return new RailView { Body = body, Lamp = lamp };
    }

    /// <summary>The kit's large crate stretched to <paramref name="size"/>, its bottom middle at <paramref name="at"/>; a plain box without the kit.</summary>
    private static void Crate(Node3D under, Vector3 at, Vector3 size)
    {
        if (Props.Get("crate_large") is not { } model)
        {
            under.AddChild(Box(size, at + Vector3.Up * size.Y / 2, new Color(0.35f, 0.3f, 0.22f)));
            return;
        }
        var scale = new Vector3(size.X / Mathf.Max(model.Size.X, 0.01f), size.Y / Mathf.Max(model.Size.Y, 0.01f), size.Z / Mathf.Max(model.Size.Z, 0.01f));
        under.AddChild(new MeshInstance3D { Mesh = model.Mesh, Transform = Props.Place(model, at, 90, scale) });
    }

    private static MeshInstance3D Box(Vector3 size, Vector3 at, Color colour, float glow = 0) => new()
    {
        Mesh = new BoxMesh { Size = size },
        Position = at,
        MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = colour,
            Roughness = 0.6f,
            Metallic = 0.4f,
            EmissionEnabled = glow > 0,
            Emission = colour,
            EmissionEnergyMultiplier = glow,
        },
    };

    /// <summary>The lanes (zones lane_1..4): black-amber stripes along both edges and the light strip down the middle.</summary>
    private void Lanes()
    {
        var root = EscapeRoot();
        var stripes = new ShaderMaterial { Shader = StripeShader.Value };
        foreach (var zone in Layout.Deck.Zones.Where(z => z.Name.StartsWith("lane_", StringComparison.Ordinal)))
        {
            if (!int.TryParse(zone.Name["lane_".Length..], out var lane))
                continue;
            var (min, max) = (zone.Min, zone.Max);
            var length = max.Y - min.Y;
            var middle = (min + max) / 2;
            foreach (var edge in new[] { min.X + 0.45f, max.X - 0.45f })
                root.AddChild(new MeshInstance3D
                {
                    Name = $"Stripes{lane}",
                    Mesh = new PlaneMesh { Size = new Vector2(0.9f, length) },
                    MaterialOverride = stripes,
                    Position = new Vector3(edge, 0.025f, middle.Y),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            var strip = new ShaderMaterial { Shader = StripShader.Value };
            strip.SetShaderParameter("length", length);
            root.AddChild(new MeshInstance3D
            {
                Name = $"Strip{lane}",
                Mesh = new PlaneMesh { Size = new Vector2(0.5f, length) },
                MaterialOverride = strip,
                Position = new Vector3(middle.X, 0.03f, middle.Y),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            _laneStrips[lane] = strip;
        }
    }

    /// <summary>The lanes' edges: diagonal black and amber stripes, faintly lit.</summary>
    private static readonly Lazy<Shader> StripeShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode cull_disabled, shadows_disabled;
            void fragment() {
                float s = step(0.5, fract((UV.x * 0.9 + UV.y * 76.0) * 0.5));
                vec3 amber = vec3(0.95, 0.6, 0.08);
                ALBEDO = mix(vec3(0.03), amber, s);
                EMISSION = amber * s * 0.35;
                ROUGHNESS = 0.8;
            }
            """,
    });

    /// <summary>A lane's light strip: dim amber dashes, which run the way its container will slide while <c>active</c> (design-escape §3 C2).</summary>
    private static readonly Lazy<Shader> StripShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, shadows_disabled;
            uniform float active = 0.0;
            uniform float way = 0.0;
            uniform float clock = 0.0;
            uniform float length = 76.0;
            void fragment() {
                // A dash every 3 m, running 12 m/s the container's way (north is +z, where the plane's UV.y falls).
                float along = UV.y * length / 3.0 + clock * 4.0 * way;
                float dash = smoothstep(0.35, 0.5, fract(along)) * (1.0 - smoothstep(0.75, 0.9, fract(along)));
                float lit = mix(0.12, 0.25 + 2.6 * dash, active);
                ALBEDO = vec3(1.0, 0.6, 0.1) * lit;
            }
            """,
    });

    // ---- the gallery: jets, valve, hand wheel; the lane lever -------------------------------------------------

    private void ShowJets(StoryHazards hazards, long tick)
    {
        if (hazards.JetsFrom < 0 && hazards.J1From < 0 && _jets[1] is null)
            return;
        for (var j = 1; j <= 5; j++)
        {
            var view = _jets[j] ??= JetOf(j);
            if (view is null)
                continue;
            var look = hazards.Phase(j, tick) switch
            {
                StoryHazards.JetPhase.Fire => Effects.JetLook.Fire,
                StoryHazards.JetPhase.Flicker => Effects.JetLook.Warn,
                _ => Effects.JetLook.Off,
            };
            if (look != view.Look)
            {
                view.Look = look;
                view.Since = _escapeClock;
                var at = view.Band.GlobalPosition + view.Nozzle;
                switch (look)
                {
                    case Effects.JetLook.Warn:
                        StoryAudio.Play("jet_hiss_rise", at);
                        Fx?.SparkShower(at + Vector3.Up * 0.5f, Vector3.Up, 25);
                        break;
                    case Effects.JetLook.Fire:
                        StoryAudio.Play("jet_ignite", at);
                        view.Roar ??= StoryAudio.Current?.Sounds.LoopOn(view.Band, "jet_roar", -2);
                        break;
                }
                if (look != Effects.JetLook.Fire && view.Roar is not null)
                {
                    StorySounds.StopLoop(view.Roar, 0.3f);
                    view.Roar = null;
                }
            }
            var warned = look == Effects.JetLook.Warn ? Mathf.Clamp((float)((_escapeClock - view.Since) / (j == 1 ? 2.5 : 1)), 0, 1) : 0;
            Effects.ShowJet(view.Band, look, _escapeClock, warned);
        }
    }

    /// <summary>A jet's view from its zone (jet_j1..5, the band) and its marker (jet_1..5, the nozzle); null when the deck has neither.</summary>
    private JetView? JetOf(int jet)
    {
        var deck = Layout.Deck;
        if (deck.Zone($"jet_j{jet}") is not { } zone || deck.Markers.FirstOrDefault(m => m.Name == $"jet_{jet}") is not { } marker)
            return null;
        var middle = (zone.Min + zone.Max) / 2;
        var nozzle = new Vector3(marker.Position.X - middle.X, 2.5f, marker.Position.Y - middle.Y);
        var band = Effects.JetBand(zone.Max.X - zone.Min.X, zone.Max.Y - zone.Min.Y, nozzle);
        band.Name = $"Jet{jet}";
        band.Position = new Vector3(middle.X, 0, middle.Y);
        EscapeRoot().AddChild(band);
        // The nozzle: a steel spout out of the wall, the way the marker faces.
        var spout = new MeshInstance3D
        {
            Name = "Nozzle",
            Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.55f, Height = 1.4f, RadialSegments = 10 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.08f, 0.06f), Metallic = 0.6f, Roughness = 0.5f },
            Position = nozzle,
            Rotation = new Vector3(0, -Mathf.DegToRad(marker.Angle), Mathf.Pi / 2),
        };
        band.AddChild(spout);
        if (jet == 4)
            Valve();
        return new JetView { Band = band, Nozzle = nozzle };
    }

    /// <summary>J4's valve plate in its niche, the hand wheel in its crawl, and the hold's lane lever (markers valve_j4, wheel_j4, lever_f).</summary>
    private void Valve()
    {
        var deck = Layout.Deck;
        var root = EscapeRoot();
        if (deck.Markers.FirstOrDefault(m => m.Name == "valve_j4") is { } valve)
        {
            var plate = Box(new Vector3(3, 0.08f, 3), Props.World(new NVector3(valve.Position, 0.04f)), new Color(0.18f, 0.17f, 0.15f));
            plate.AddChild(Box(new Vector3(3.2f, 0.05f, 3.2f), new Vector3(0, -0.02f, 0), Amber, 0.6f));
            root.AddChild(plate);
        }
        if (deck.Markers.FirstOrDefault(m => m.Name == "wheel_j4") is { } wheel)
        {
            _wheel = new Node3D { Name = "HandWheel", Position = Props.World(new NVector3(wheel.Position, 1.4f)) };
            _wheel.AddChild(new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = 0.45f, OuterRadius = 0.6f, Rings = 16, RingSegments = 6 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.1f, 0.06f), Metallic = 0.5f, Roughness = 0.5f },
                Rotation = new Vector3(0, 0, Mathf.Pi / 2),
            });
            root.AddChild(_wheel);
        }
        if (deck.Markers.FirstOrDefault(m => m.Name == "lever_f") is { } lever)
        {
            _lever = new Node3D { Name = "LaneLever", Position = Props.World(new NVector3(lever.Position, 0)) };
            _lever.AddChild(Box(new Vector3(0.8f, 0.6f, 0.8f), new Vector3(0, 0.3f, 0), SteelDark));
            _lever.AddChild(Box(new Vector3(0.15f, 1.3f, 0.15f), new Vector3(0, 1.1f, 0), Amber, 0.5f));
            root.AddChild(_lever);
        }
    }

    /// <summary>The hand wheel turned (cue "wheel j4") or the lane lever pulled (cue "lever lanes"): the view turns it.</summary>
    public void EscapeDevice(string what)
    {
        var (device, turn) = what == "wheel" ? (_wheel, new Vector3(Mathf.Tau * 1.5f, 0, 0)) : (_lever, new Vector3(0, 0, -0.9f));
        if (device is null)
            return;
        device.CreateTween().TweenProperty(device, "rotation", device.Rotation + turn, what == "wheel" ? 1.2 : 0.4).SetTrans(Tween.TransitionType.Sine);
    }

    // ---- the shutter X -----------------------------------------------------------------------------------------

    private void ShowShutter(StoryHazards hazards, double delta)
    {
        if (hazards.Shutter == ShutterState.Open && _shutter is null)
            return;
        if (_shutter is null && !Shutter())
            return;
        var shutter = _shutter!;
        var target = hazards.Shutter switch
        {
            ShutterState.Closing => (float)hazards.ShutterDown / StoryHazards.ShutterLower,
            ShutterState.Shut => 1f,
            ShutterState.Broken => 1f,
            _ => 0f,
        };
        // The sim lowers it a step a tick: the view follows smoothly, as fast as it slams.
        _shutterShown = Mathf.MoveToward(_shutterShown, target, (float)delta * 3);
        var lowering = hazards.Shutter == ShutterState.Closing && _shutterShown < target - 0.001f;
        shutter.Visible = _shutterShown > 0.005f && !(hazards.Shutter == ShutterState.Broken && _shutterGone);
        if (hazards.Shutter == ShutterState.Broken && !_shutterGone)
        {
            // Broken before its burst was seen (a checkpoint's world): simply gone.
            _shutterGone = true;
            shutter.Visible = false;
        }
        shutter.Position = new Vector3(shutter.Position.X, _shutterAt.Y + _shutterHeight * (1 - _shutterShown), shutter.Position.Z);
        if (lowering && _shutterGrind is null)
            _shutterGrind = StoryAudio.Current?.Sounds.LoopOn(shutter, "shutter_grind", -3);
        else if (!lowering && _shutterGrind is not null)
        {
            StorySounds.StopLoop(_shutterGrind);
            _shutterGrind = null;
        }
        // Yellow strobes while it closes by itself (design-escape §3 C3), on both sides of its frame.
        var strobe = hazards.Shutter == ShutterState.Closing && Mathf.Sin((float)_escapeClock * 14) > 0.2f;
        foreach (var lamp in _shutterLamps)
            lamp.Visible = strobe;
    }

    /// <summary>The shutter's leaf in X's frame (the kit's door leaf stretched to the opening), and its strobes; false without X.</summary>
    private bool Shutter()
    {
        if (Layout.Openings.FirstOrDefault(o => o.Letter == 'X') is not { } opening || Props.Get("kit_door_leaf") is not { } model)
            return false;
        var centre = opening.Rect.Centre;
        _shutterHeight = Mathf.Max(4, opening.Height);
        var root = EscapeRoot();
        _shutter = new Node3D { Name = "ShutterX" };
        root.AddChild(_shutter);
        // Across the opening, facing along the way through (east-west for X's slot in a wall that runs north-south).
        var facing = opening.AlongX ? 90 : 0;
        var scale = new Vector3(opening.Width / Mathf.Max(model.Size.X, 0.01f), _shutterHeight / Mathf.Max(model.Size.Y, 0.01f), 1);
        _shutter.AddChild(new MeshInstance3D { Name = "Leaf", Mesh = model.Mesh, Transform = Props.Place(model, Vector3.Zero, facing, scale) });
        _shutterAt = Props.World(new NVector3(centre, 0));
        _shutter.Position = _shutterAt + Vector3.Up * _shutterHeight;
        _shutter.Visible = false;
        var across = opening.AlongX ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
        var along = opening.AlongX ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);
        foreach (var side in new[] { -1f, 1f })
            foreach (var end in new[] { -1f, 1f })
            {
                var lamp = new OmniLight3D
                {
                    Name = "Strobe",
                    LightColor = new Color(1, 0.8f, 0.15f),
                    LightEnergy = 4,
                    OmniRange = 12,
                    Position = _shutterAt + across * side * 4 + along * end * (opening.Width / 2f + 1) + Vector3.Up * Mathf.Min(_shutterHeight, 8),
                    Visible = false,
                    ShadowEnabled = false,
                };
                root.AddChild(lamp);
                _shutterLamps.Add(lamp);
            }
        return true;
    }

    /// <summary>The shutter rammed (cue "shutter ram n"): it bulges towards the gallery and springs back.</summary>
    public void RamShutter()
    {
        if (_shutter is null)
            return;
        var rest = _shutter.Position;
        var tween = _shutter.CreateTween();
        tween.TweenProperty(_shutter, "position", rest + new Vector3(-0.5f, 0, 0), 0.07);
        tween.TweenProperty(_shutter, "position", rest, 0.35).SetTrans(Tween.TransitionType.Elastic).SetEase(Tween.EaseType.Out);
    }

    /// <summary>The shutter burst (cue "shutter burst"): its leaf flies into the gallery, tumbling; where it was is empty.</summary>
    public Vector3? BurstShutter()
    {
        if (_shutter is null || _shutterGone)
            return null;
        _shutterGone = true;
        _shutter.Visible = false;
        var at = _shutterAt + Vector3.Up * 3;
        Throw("kit_door_leaf", at, new Vector3(-9, 4, (float)GD.RandRange(-2.0, 2.0)), new Vector3(1.5f, 0.4f, 2.5f), 1.4f);
        return at;
    }

    // ---- the footbridge ----------------------------------------------------------------------------------------

    private void ShowBridge(StoryHazards hazards, long tick)
    {
        if (_bridge is null || _bridgeDown)
            return;
        if (hazards.BridgeFallen)
        {
            // Fallen without its cue seen (a checkpoint's world): gone at once.
            DropBridge(animate: false);
            return;
        }
        // Creaking: the segments sag and shiver more and more until it goes (design-escape §3 C5).
        var creak = hazards.BridgeFallAt < 0 ? 0 : Mathf.Clamp((tick - hazards.BridgeFallAt) / (float)StoryHazards.BridgeCreak, 0, 1);
        foreach (var segment in _bridgeSegments)
        {
            var rest = segment.GetMeta("rest").AsVector3();
            var shiver = creak <= 0 ? 0 : 0.06f * creak * Mathf.Sin((float)_escapeClock * 31 + rest.X);
            segment.Position = rest + Vector3.Down * (0.6f * creak * creak + shiver);
        }
    }

    /// <summary>The bridge falls into the shaft (cue "bridge fall"): its segments go one after another, tumbling down into the glow.</summary>
    public void DropBridge(bool animate)
    {
        if (_bridgeDown || _bridge is null)
            return;
        _bridgeDown = true;
        var random = new RandomNumberGenerator { Seed = 7 };
        var west = _bridgeSegments.Count == 0 ? 0 : _bridgeSegments.Min(s => s.GetMeta("rest").AsVector3().X);
        foreach (var segment in _bridgeSegments)
        {
            if (!animate)
            {
                segment.Visible = false;
                continue;
            }
            var rest = segment.GetMeta("rest").AsVector3();
            // From the west, where the Thresher stands at its head, to the east.
            var delay = (rest.X - west) / 80 * 0.9;
            var spin = new Vector3(random.RandfRange(-1.2f, 1.2f), random.RandfRange(-0.6f, 0.6f), random.RandfRange(-1.2f, 1.2f));
            var tween = segment.CreateTween().SetParallel();
            tween.TweenProperty(segment, "position", rest + new Vector3(random.RandfRange(-1, 1), -ShaftDepth, random.RandfRange(-2, 2)), 2.4)
                .SetDelay(delay).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            tween.TweenProperty(segment, "rotation", segment.Rotation + spin, 2.4).SetDelay(delay);
            tween.Chain().TweenCallback(Callable.From(() => segment.Visible = false));
        }
    }

    // ---- the Notgang's collapse and its fire face ------------------------------------------------------------------

    private void ShowCollapse(StoryHazards hazards)
    {
        if (!hazards.Collapsed && _heap is null)
            return;
        if (_heap is null)
            Heap();
        _heap!.Visible = hazards.Collapsed;
        foreach (var fire in _fireFace)
            if (fire.Emitting != hazards.FireFace)
                fire.Emitting = hazards.FireFace;
        if (_fireGlow is not null)
        {
            _fireGlow.Visible = hazards.FireFace;
            _fireGlow.LightEnergy = 3 + 0.8f * Mathf.Sin((float)_escapeClock * 9) + 0.4f * Mathf.Sin((float)_escapeClock * 23);
        }
    }

    /// <summary>The heap of wreckage that fills the Notgang south of the breach (zone collapse_notgang) and the fire face at its edge (fire_notgang).</summary>
    private void Heap()
    {
        var root = EscapeRoot();
        _heap = new Node3D { Name = "Collapse" };
        root.AddChild(_heap);
        var deck = Layout.Deck;
        if (deck.Zone("collapse_notgang") is { } zone)
        {
            var random = new RandomNumberGenerator { Seed = 1530 };
            (string Prop, float Scale)[] pieces = [("debris_panel", 1.6f), ("debris_beam", 1.3f), ("crate_large", 1f), ("debris_panel", 1.2f), ("debris_beam", 1.6f)];
            for (var i = 0; i < 26; i++)
            {
                var (prop, scale) = pieces[i % pieces.Length];
                if (Props.Get(prop) is not { } model)
                    continue;
                var x = random.RandfRange(zone.Min.X + 1, zone.Max.X - 1);
                var y = random.RandfRange(zone.Min.Y + 1, zone.Max.Y - 1);
                // Higher towards the hull's side (south), where the ceiling came down.
                var up = random.RandfRange(0, 6) * (1 - (y - zone.Min.Y) / (zone.Max.Y - zone.Min.Y) * 0.6f);
                var turn = new Basis(Quaternion.FromEuler(new Vector3(random.RandfRange(-0.7f, 0.7f), random.RandfRange(0, Mathf.Tau), random.RandfRange(-0.7f, 0.7f))));
                var place = new Transform3D(turn * Basis.FromScale(Vector3.One * scale), Props.World(new NVector3(x, y, up))) * model.Offset;
                _heap.AddChild(new MeshInstance3D { Mesh = model.Mesh, Transform = place });
            }
        }
        if (deck.Zone("fire_notgang") is { } face)
        {
            var mid = (face.Min + face.Max) / 2;
            var width = face.Max.X - face.Min.X;
            for (var i = 0; i < 4; i++)
            {
                var fire = Effects.DamageFire(width / 4 + 0.5f, 7);
                fire.Position = new Vector3(face.Min.X + (i + 0.5f) * width / 4, 0.3f, mid.Y);
                root.AddChild(fire);
                _fireFace.Add(fire);
            }
            _fireGlow = new OmniLight3D
            {
                Name = "FireFace",
                LightColor = new Color(1, 0.5f, 0.15f),
                OmniRange = 22,
                Position = new Vector3(mid.X, 4, mid.Y + 3),
                Visible = false,
                ShadowEnabled = false,
                LightVolumetricFogEnergy = 1,
            };
            root.AddChild(_fireGlow);
        }
    }

    /// <summary>The collapse blasted open again (cue "reopen notgang"): pieces of the heap fly north into the Notgang.</summary>
    public void BlastHeap(Vector3 at)
    {
        var random = new RandomNumberGenerator { Seed = 1531 };
        for (var i = 0; i < 6; i++)
            Throw(i % 2 == 0 ? "debris_panel" : "debris_beam", at + new Vector3(random.RandfRange(-5, 5), 2, random.RandfRange(-4, 4)),
                new Vector3(random.RandfRange(-3, 3), random.RandfRange(4, 7), random.RandfRange(6, 11)),
                new Vector3(random.RandfRange(-4, 4), random.RandfRange(-2, 2), random.RandfRange(-4, 4)), 1.1f);
    }

    // ---- the gas main beside Y --------------------------------------------------------------------------------

    private readonly List<GpuParticles3D> _gasFire = [];
    private OmniLight3D? _gasGlow, _gasLeak;

    /// <summary>
    /// The gas main while whole: an orange leak light flickering on its pipe, so it reads as something to shoot. Burning
    /// (shot through, GameWorld.UpdateGasMain): flames across Y's apron (zone gas_fire) and their glow, until it burns out.
    /// </summary>
    private void ShowGas(StoryHazards hazards, long tick, NVector2? gasMain)
    {
        if (gasMain is { } pipe && hazards.GasFireUntil < 0)
        {
            _gasLeak ??= NewGasLeak();
            _gasLeak.Visible = true;
            _gasLeak.Position = Props.World(new NVector3(pipe, 1.4f));
            _gasLeak.LightEnergy = 1.2f + 0.5f * Mathf.Sin((float)_escapeClock * 11) + 0.3f * Mathf.Sin((float)_escapeClock * 29);
        }
        else if (_gasLeak is not null)
            _gasLeak.Visible = false;
        var burning = hazards.GasFireUntil >= 0 && tick < hazards.GasFireUntil;
        if (!burning && _gasGlow is null)
            return;
        if (_gasGlow is null)
            GasFire();
        foreach (var fire in _gasFire)
            if (fire.Emitting != burning)
                fire.Emitting = burning;
        _gasGlow!.Visible = burning;
        _gasGlow.LightEnergy = 4 + 1.2f * Mathf.Sin((float)_escapeClock * 8) + 0.6f * Mathf.Sin((float)_escapeClock * 21);
    }

    private OmniLight3D NewGasLeak()
    {
        var leak = new OmniLight3D { Name = "GasLeak", LightColor = new Color(1, 0.55f, 0.2f), OmniRange = 7, ShadowEnabled = false };
        EscapeRoot().AddChild(leak);
        return leak;
    }

    private void GasFire()
    {
        var root = EscapeRoot();
        var zone = Layout.Deck.Zone("gas_fire");
        var (min, max) = zone is { } z ? (z.Min, z.Max) : (new NVector2(504, 100), new NVector2(540, 120));
        var width = max.X - min.X;
        for (var row = 0; row < 2; row++)
            for (var i = 0; i < 5; i++)
            {
                var fire = Effects.DamageFire(width / 5 + 0.5f, 6);
                fire.Position = new Vector3(min.X + (i + 0.5f) * width / 5, 0.3f, min.Y + (row + 0.5f) * (max.Y - min.Y) / 2);
                root.AddChild(fire);
                _gasFire.Add(fire);
            }
        _gasGlow = new OmniLight3D
        {
            Name = "GasFire",
            LightColor = new Color(1, 0.5f, 0.15f),
            OmniRange = 26,
            Position = new Vector3((min.X + max.X) / 2, 4, (min.Y + max.Y) / 2),
            ShadowEnabled = false,
            LightVolumetricFogEnergy = 1,
        };
        root.AddChild(_gasGlow);
    }

    // ---- the crane's amber lock ----------------------------------------------------------------------------------

    private void ShowLock(NVector2? craneLock)
    {
        if (craneLock is not { } at)
        {
            if (_lockGlow is not null)
                _lockGlow.Visible = false;
            return;
        }
        _lockGlow ??= NewLockGlow();
        _lockGlow.Visible = true;
        _lockGlow.Position = Props.World(new NVector3(at, 1.6f));
        _lockGlow.LightEnergy = 1.6f + 0.6f * Mathf.Sin((float)_escapeClock * 3);
    }

    private OmniLight3D NewLockGlow()
    {
        var glow = new OmniLight3D { Name = "AmberLock", LightColor = Amber, OmniRange = 6, ShadowEnabled = false };
        glow.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.25f, Height = 0.5f, RadialSegments = 10, Rings = 5 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Amber, EmissionEnabled = true, Emission = Amber, EmissionEnergyMultiplier = 4 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        EscapeRoot().AddChild(glow);
        return glow;
    }

    // ---- the warm-up -----------------------------------------------------------------------------------------

    /// <summary>
    /// One of each of the escape's own pieces for the warm-up to draw behind the loading screen (Warmup.Escape): a container,
    /// a lane's strips, a burning jet with its steam, the shutter, a bridge segment over the shaft's glow, the fire face.
    /// </summary>
    public static IEnumerable<Node3D> EscapePieces()
    {
        var container = new Node3D { Name = "WarmContainer" };
        Crate(container, Vector3.Zero, new Vector3(8, 4, 12));
        container.AddChild(Box(new Vector3(0.15f, 1.3f, 0.15f), new Vector3(0, 5, 0), Amber, 0.5f));
        yield return container;
        yield return new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(0.5f, 8) }, MaterialOverride = new ShaderMaterial { Shader = StripShader.Value } };
        yield return new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(0.9f, 8) }, MaterialOverride = new ShaderMaterial { Shader = StripeShader.Value } };
        var jet = Effects.JetBand(8, 4, new Vector3(4, 2.5f, 0));
        Effects.ShowJet(jet, Effects.JetLook.Fire, 0, 0);
        foreach (var steam in jet.GetChildren().OfType<GpuParticles3D>().Where(p => p.Name == "Steam"))
            steam.Emitting = true;
        yield return jet;
        if (Props.Get("kit_door_leaf") is { } leaf)
            yield return new MeshInstance3D { Mesh = leaf.Mesh };
        if (Props.Get("kit_floor_grate") is { } grate)
            yield return new MeshInstance3D { Mesh = grate.Mesh };
        yield return Effects.ShaftGlow(new Vector2(8, 8), 6);
        var fire = Effects.DamageFire(4, 7);
        fire.Emitting = true;
        yield return fire;
    }
}
