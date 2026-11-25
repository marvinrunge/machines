using Godot;
using Machines.Import.Model;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Machines.Game;

/// <summary>
/// A ship deck as the story shows it, in place of a planet's ground: floors, walls, ceilings, grates and the rooms' fittings
/// from the story's kit (Props), laid out by <see cref="DeckLayout"/> on the deck's 1 m grid; the repeated modules drawn as
/// multimeshes in 32 m squares, so the ones out of sight are left out, and boxes in the walls that hide what is behind them
/// (occlusion culling, which the story's scene turns on). Its lamps come in the deck's light groups (LIGHTS): ceiling lamps
/// while a group is on, red emergency lamps on the walls while it is on emergency, none while it is off; they cast no
/// shadows but in the tallest rooms and reach only so far, so they hardly shine through walls. The story is the remake's own.
/// </summary>
public partial class DeckView : Node3D
{
    /// <summary>The squares the repeated modules are batched in, metres.</summary>
    private const int Chunk = 32;

    /// <summary>How far above a ceiling the walls go on into it, so no seam shows where they meet.</summary>
    private const float WallOverlap = 0.5f;

    /// <summary>Lamps further than this from the camera fade out (metres), and the fade's length.</summary>
    private const float LampFade = 72, LampFadeLength = 24;

    private static readonly Color LampColour = new(1f, 0.88f, 0.72f), EmergencyColour = new(1f, 0.12f, 0.07f);
    private const float LampEnergy = 1.7f, EmergencyEnergy = 0.7f;

    public DeckLayout Layout { get; private set; } = null!;

    private readonly Dictionary<string, LightGroup> _groups = new(StringComparer.Ordinal);

    /// <summary>The deck's light groups, by name.</summary>
    public IEnumerable<string> LightGroups => _groups.Keys;

    /// <summary>How a light group is lit now: on, off or emergency.</summary>
    public string? LightState(string group) => _groups.GetValueOrDefault(group)?.State;

    private sealed class LightGroup
    {
        public string State = "off";
        public readonly List<Light3D> Ceiling = [], Wall = [];
        public StandardMaterial3D Fixture = null!, Glass = null!;
    }

    /// <summary>The view of <paramref name="deck"/>, laid out and put up.</summary>
    public static DeckView Build(Deck deck)
    {
        var view = new DeckView { Name = "Deck", Layout = new DeckLayout(deck) };
        view.PutUp();
        return view;
    }

    // Instances of each prop by the square they stand in, and whether they cast shadows and lie on the ground's layer.
    private readonly Dictionary<(string Prop, int Square, bool Shadow, bool Ground), List<Transform3D>> _batches = [];

    private void Add(string prop, Transform3D place, bool shadow = true, bool ground = false)
    {
        var square = (int)(place.Origin.X / Chunk) * 1000 + (int)(place.Origin.Z / Chunk);
        if (!_batches.TryGetValue((prop, square, shadow, ground), out var list))
            _batches[(prop, square, shadow, ground)] = list = [];
        list.Add(place);
    }

    private void Add(string prop, NVector3 at, float angle, Vector3 scale, bool shadow = true, bool ground = false)
    {
        if (Props.Get(prop) is { } model)
            Add(prop, Props.Place(model, Props.World(at), angle, scale), shadow, ground);
    }

    private void PutUp()
    {
        Floors();
        Walls();
        Ceilings();
        Openings();
        foreach (var piece in Layout.Pieces)
        {
            if (piece.Prop == "plate")
                AddChild(SwitchPlate(piece));
            else
                Add(piece.Prop, piece.Position, piece.Angle, new Vector3(piece.Scale.X, piece.Scale.Y, piece.Scale.Z));
        }
        foreach (var frame in Layout.Frames)
            if (Props.Frame(frame) is { } fitted)
                Add(fitted.Name, Props.Place(fitted.Model, Props.World(new NVector3(frame.Centre, 0)), frame.Angle, Vector3.One));
        Lamps();
        StoryFittings();
        CoreGlow();
        foreach (var (key, places) in _batches)
            AddChild(Batch(key.Prop, places, key.Shadow, key.Ground));
        _batches.Clear();
        AddChild(Occluder());
        Colliders();
        foreach (var light in Layout.Deck.Lights)
            SetLights(light.Name, light.State);
        // For pictures: MACHINES_DECK_LIGHTS=<group>=<state>,... sets groups as the director would ("*" is every group).
        if (System.Environment.GetEnvironmentVariable("MACHINES_DECK_LIGHTS") is { Length: > 0 } asked)
            foreach (var pair in asked.Split(','))
                if (pair.Split('=') is [var group, var state])
                    foreach (var name in group == "*" ? _groups.Keys.ToList() : [group])
                        SetLights(name, state);
        // For pictures: MACHINES_DECK_ALARM=1 sounds the alarm, as the director does at the core's 90 %.
        if (System.Environment.GetEnvironmentVariable("MACHINES_DECK_ALARM") == "1")
            SetAlarm(true);
    }

    // ---- modules --------------------------------------------------------------------------------

    private void Floors()
    {
        foreach (var floor in Layout.Floors)
        {
            // The footbridge's floor is the escape's own, so that it can fall (DeckView.Escape).
            if (EscapeFloor(floor))
                continue;
            var at = new NVector3(floor.X + DeckLayout.Module / 2f, floor.Y + DeckLayout.Module / 2f, 0);
            var prop = floor.Grate ? "kit_floor_grate" : "kit_floor";
            if (Props.Get(prop) is not { } model)
                continue;
            // The top at 0, where the machines stand; under a grate a plain floor lower down.
            var size = model.Size;
            var scale = new Vector3(DeckLayout.Module / size.X, 1, DeckLayout.Module / size.Z);
            Add(prop, Props.Place(model, Props.World(at) - new Vector3(0, size.Y, 0), 90, scale), shadow: false, ground: true);
            if (floor.Grate && Props.Get("kit_floor") is { } under)
                Add("kit_floor", Props.Place(under, Props.World(at) - new Vector3(0, under.Size.Y + GratePit, 0), 90,
                    new Vector3(DeckLayout.Module / under.Size.X, 1, DeckLayout.Module / under.Size.Z)), shadow: false);
        }
        GrateSkirts();
    }

    /// <summary>How deep the pit under a grate is: the plain floor lies this far below the grate's top.</summary>
    private const float GratePit = 1.2f;

    /// <summary>
    /// The sides of the pits under the grates, where a grate meets plain floor or a wall: a steel skirt from the pit's
    /// floor up under the grate's edge, so no open edge shows down into the void beneath the plain floor's slabs.
    /// </summary>
    private void GrateSkirts()
    {
        if (Props.Get("kit_wall_low") is not { } model)
            return;
        var grates = Layout.Floors.Where(f => f.Grate).Select(f => (f.X, f.Y)).ToHashSet();
        const int m = DeckLayout.Module;
        foreach (var (x, y) in grates)
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if (grates.Contains((x + dx * m, y + dy * m)))
                    continue;
                // On the block's edge towards the neighbour, its front facing back into the pit.
                var edge = new NVector2(x + m / 2f + dx * m / 2f, y + m / 2f + dy * m / 2f) - new NVector2(dx, dy) * 0.08f;
                var scale = new Vector3(m / model.Size.X, (GratePit + 0.1f) / model.Size.Y, 0.12f / model.Size.Z);
                Add("kit_wall_low", new NVector3(edge, -GratePit - 0.05f), DeckLayout.Degrees(new NVector2(-dx, -dy)), scale, shadow: false);
            }
    }

    private void Walls()
    {
        foreach (var face in Layout.Faces)
        {
            var span = face.Top + WallOverlap - face.Bottom;
            var low = span <= 5;
            var prop = low ? "kit_wall_low" : "kit_wall";
            if (Props.Get(prop) is not { } model)
                continue;
            // Tall walls take the vented module and the plain one (the same size) in turn, as a chequer, so a long wall
            // does not repeat one panel.
            var plain = !low && Props.Get("kit_wall_plain") is not null;
            var (width, tall, deep) = (model.Size.X, model.Size.Y, model.Size.Z);
            var rows = Math.Max(1, (int)MathF.Round(span / tall));
            var count = Math.Max(1, (int)MathF.Round(face.Length / DeckLayout.Module));
            var along = NVector2.Normalize(face.To - face.From);
            var scale = new Vector3(face.Length / count / width, span / rows / tall, 1);
            var angle = DeckLayout.Degrees(face.Normal);
            for (var i = 0; i < count; i++)
            {
                // Each module's front on the face, its body in the wall behind it.
                var at = face.From + along * ((i + 0.5f) * face.Length / count) - face.Normal * (deep / 2);
                for (var row = 0; row < rows; row++)
                    Add(plain && (i + row) % 2 == 1 ? "kit_wall_plain" : prop, new NVector3(at, face.Bottom + row * span / rows), angle, scale);
            }
        }
        Corners();
    }

    /// <summary>Faces shorter than this (metres) take no corner column: the reveals of doorways and ducts.</summary>
    private const float CornerFace = 1.5f;

    /// <summary>
    /// A steel column on every outer corner of the walls (kit_wall_corner, kit_wall_low_corner on walls up to 5 m), over
    /// the seam where two runs of wall modules meet at a right angle: where one face ends at a point and runs away from it
    /// against the other's normal, and the other alike. Not at the reveals of doorways and ducts (faces under
    /// <see cref="CornerFace"/>), not round the door frames, which cover their corners, and not where a pillar stands.
    /// The column's origin is the corner, its +z along the first face's normal and its +x along the second's.
    /// </summary>
    private void Corners()
    {
        static NVector2 Cw(NVector2 v) => new(v.Y, -v.X);
        var ends = Layout.Faces.Where(f => f.Length >= CornerFace)
            .SelectMany(f => new[] { (At: f.From, Into: NVector2.Normalize(f.To - f.From), Face: f), (At: f.To, Into: NVector2.Normalize(f.From - f.To), Face: f) })
            .GroupBy(e => (MathF.Round(e.At.X), MathF.Round(e.At.Y)));
        var pillars = Layout.Pieces.Where(p => p.Prop == "kit_pillar").Select(p => new NVector2(p.Position.X, p.Position.Y)).ToList();
        foreach (var corner in ends)
        {
            var at = corner.First().At;
            if (Layout.Frames.Any(f => NVector2.Distance(f.Centre, at) < f.Half + 2.5f) || pillars.Any(p => NVector2.Distance(p, at) < 1.2f))
                continue;
            foreach (var a in corner)
                foreach (var b in corner)
                {
                    // b's normal is a's turned clockwise (the column's +x), and each face runs away against the other's normal.
                    if (NVector2.Distance(b.Face.Normal, Cw(a.Face.Normal)) > 0.01f
                        || NVector2.Distance(a.Into, -b.Face.Normal) > 0.01f || NVector2.Distance(b.Into, -a.Face.Normal) > 0.01f)
                        continue;
                    var bottom = Math.Max(a.Face.Bottom, b.Face.Bottom);
                    var top = Math.Min(a.Face.Top, b.Face.Top);
                    if (top - bottom < 0.5f)
                        continue;
                    var span = top + WallOverlap - bottom;
                    var prop = span <= 5 ? "kit_wall_low_corner" : "kit_wall_corner";
                    if (Props.Get(prop) is { } model)
                        Add(prop, new NVector3(at, bottom), DeckLayout.Degrees(a.Face.Normal), new Vector3(1, span / model.Size.Y, 1));
                }
        }
    }

    private void Ceilings()
    {
        if (Props.Get("kit_ceiling") is not { } model)
            return;
        var size = model.Size;
        foreach (var ceiling in Layout.Ceilings)
        {
            var rect = ceiling.Rect;
            var nx = Math.Max(1, (int)MathF.Round(rect.Width / (float)DeckLayout.Module));
            var ny = Math.Max(1, (int)MathF.Round(rect.Depth / (float)DeckLayout.Module));
            // Low ceilings (ducts, doorways) are thinner, so their beams do not hang into a machine's way.
            var thick = DeckLayout.CeilingSlab(ceiling.Height);
            var scale = new Vector3(rect.Width / (float)nx / size.X, thick, rect.Depth / (float)ny / size.Z);
            for (var j = 0; j < ny; j++)
                for (var i = 0; i < nx; i++)
                {
                    var at = new NVector3(rect.X0 + (i + 0.5f) * rect.Width / nx, rect.Y0 + (j + 0.5f) * rect.Depth / ny, ceiling.Height - thick * size.Y);
                    // A ceiling throws no shadow: the lamps hang under it, and it would only darken the deck.
                    Add("kit_ceiling", Props.Place(model, Props.World(at), 90, scale), shadow: false);
                }
        }
    }

    /// <summary>The grate walls (the doors are the scenario's artefacts, shown by their props).</summary>
    private void Openings()
    {
        foreach (var opening in Layout.Openings.Where(o => o.Kind == "grate"))
        {
            if (Props.Get("kit_grate_wall") is not { } model)
                continue;
            var scale = new Vector3(opening.Width / model.Size.X, opening.Height / model.Size.Y, 1);
            Add("kit_grate_wall", new NVector3(opening.Rect.Centre, 0), opening.AlongX ? 90 : 0, scale);
        }
    }

    private static Node3D Batch(string prop, List<Transform3D> places, bool shadow, bool ground)
    {
        var model = Props.Get(prop)!;
        var multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = model.Mesh, InstanceCount = places.Count };
        for (var i = 0; i < places.Count; i++)
            multimesh.SetInstanceTransform(i, places[i]);
        return new MultiMeshInstance3D
        {
            Name = prop,
            Multimesh = multimesh,
            CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = ground ? 1 | TerrainBuilder.GroundLayer : 1,
        };
    }

    /// <summary>A switch's floor plate: a low steel plate with a yellow rim that glows faintly.</summary>
    private static Node3D SwitchPlate(DeckPiece piece)
    {
        var plate = new MeshInstance3D
        {
            Name = "SwitchPlate",
            Mesh = new BoxMesh { Size = new Vector3(piece.Scale.X, piece.Scale.Y, piece.Scale.Z) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.17f, 0.15f), Roughness = 0.6f, Metallic = 0.3f },
            Position = Props.World(piece.Position) + new Vector3(0, piece.Scale.Y / 2, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        plate.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(piece.Scale.X + 0.2f, piece.Scale.Y * 0.6f, piece.Scale.Z + 0.2f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.85f, 0.62f, 0.05f), EmissionEnabled = true, Emission = new Color(1f, 0.65f, 0.1f), EmissionEnergyMultiplier = 0.6f,
            },
            Position = new Vector3(0, -piece.Scale.Y * 0.2f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return plate;
    }

    // ---- light ------------------------------------------------------------------------------------

    private void Lamps()
    {
        var lamps = new Node3D { Name = "Lamps" };
        AddChild(lamps);
        var fixtures = new Dictionary<(string Group, bool Wall), List<Transform3D>>();
        foreach (var lamp in Layout.Lamps)
        {
            if (!_groups.TryGetValue(lamp.Group, out var group))
            {
                _groups[lamp.Group] = group = new LightGroup
                {
                    Fixture = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.24f, 0.22f), Emission = LampColour, EmissionEnergyMultiplier = 3 },
                    Glass = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.03f, 0.02f), Emission = EmergencyColour, EmissionEnergyMultiplier = 4 },
                };
            }
            var at = Props.World(lamp.Position);
            if (lamp.Kind == DeckLampKind.Ceiling)
            {
                // Straight down, as wide as it may open without falling on floor behind a wall (DeckLayout.ClearReach).
                var light = new SpotLight3D
                {
                    LightColor = LampColour,
                    LightEnergy = LampEnergy,
                    SpotRange = lamp.Range,
                    SpotAngle = lamp.Spread,
                    SpotAngleAttenuation = 0.6f,
                    SpotAttenuation = 0.5f,
                    ShadowEnabled = lamp.Shadowed,
                    Transform = new Transform3D(Basis.LookingAt(Vector3.Down, Vector3.Back), at - new Vector3(0, 0.1f, 0)),
                    DistanceFadeEnabled = !lamp.Shadowed,
                    DistanceFadeBegin = LampFade,
                    DistanceFadeLength = LampFadeLength,
                    LightVolumetricFogEnergy = 0.6f,
                };
                lamps.AddChild(light);
                group.Ceiling.Add(light);
                Fixture(fixtures, (lamp.Group, false), new Transform3D(Basis.Identity, at));
                // A lamp hung below its ceiling hangs from a cable.
                var ceiling = Layout.Height((int)MathF.Floor(lamp.Position.X), (int)MathF.Floor(lamp.Position.Y));
                if (ceiling - lamp.Position.Z > 0.6f)
                    lamps.AddChild(new MeshInstance3D
                    {
                        Mesh = new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.03f, Height = ceiling - lamp.Position.Z, RadialSegments = 4 },
                        MaterialOverride = CableMaterial.Value,
                        Position = at + new Vector3(0, (ceiling - lamp.Position.Z) / 2, 0),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    });
            }
            else
            {
                var outward = new Vector3(Mathf.Cos(Mathf.DegToRad(lamp.Angle)), 0, Mathf.Sin(Mathf.DegToRad(lamp.Angle)));
                // Into its room and down, never back through its wall.
                var light = new SpotLight3D
                {
                    LightColor = EmergencyColour,
                    LightEnergy = EmergencyEnergy,
                    SpotRange = lamp.Range,
                    SpotAngle = lamp.Spread,
                    SpotAngleAttenuation = 0.8f,
                    SpotAttenuation = 1.0f,
                    Transform = new Transform3D(Basis.LookingAt((outward * 0.8f + Vector3.Down * 0.6f).Normalized(), Vector3.Up), at + outward * 0.3f),
                    DistanceFadeEnabled = true,
                    DistanceFadeBegin = LampFade,
                    DistanceFadeLength = LampFadeLength,
                    LightVolumetricFogEnergy = 0.8f,
                };
                lamps.AddChild(light);
                group.Wall.Add(light);
                // The lamp's housing on the wall, and its glass, lit with its group.
                Add("lamp_emergency", lamp.Position - new NVector3(0, 0, 0.3f), lamp.Angle, Vector3.One, shadow: false);
                Fixture(fixtures, (lamp.Group, true), new Transform3D(Basis.Identity, at + outward * 0.14f));
            }
        }
        foreach (var ((name, wall), places) in fixtures)
        {
            var group = _groups[name];
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = wall ? new SphereMesh { Radius = 0.11f, Height = 0.16f, RadialSegments = 8, Rings = 4 } : new BoxMesh { Size = new Vector3(1.4f, 0.1f, 0.45f) },
                InstanceCount = places.Count,
            };
            for (var i = 0; i < places.Count; i++)
                multimesh.SetInstanceTransform(i, places[i]);
            lamps.AddChild(new MultiMeshInstance3D
            {
                Name = wall ? $"{name}Glass" : $"{name}Fixtures",
                Multimesh = multimesh,
                MaterialOverride = wall ? group.Glass : group.Fixture,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    private static void Fixture(Dictionary<(string, bool), List<Transform3D>> fixtures, (string, bool) key, Transform3D place)
    {
        if (!fixtures.TryGetValue(key, out var list))
            fixtures[key] = list = [];
        list.Add(place);
    }

    private static readonly Lazy<StandardMaterial3D> CableMaterial = new(() => new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Roughness = 0.8f });

    /// <summary>
    /// Lights a group (LIGHTS) as the story asks: "on" its ceiling lamps, "emergency" its red wall lamps, "off" none. Unknown
    /// groups and states are left alone.
    /// </summary>
    public void SetLights(string name, string state)
    {
        if (!_groups.TryGetValue(name, out var group) || state is not ("on" or "off" or "emergency"))
            return;
        group.State = state;
        foreach (var light in group.Ceiling)
            light.Visible = state == "on";
        foreach (var light in group.Wall)
            light.Visible = state == "emergency";
        group.Fixture.EmissionEnabled = state == "on";
        group.Glass.EmissionEnabled = state == "emergency";
    }

    private float _gloom = 1;

    /// <summary>
    /// The dark start's emergency lamps (the story's sections 1 and 2): their light and their glass at this share of full.
    /// </summary>
    public void SetGloom(float share)
    {
        if (Mathf.Abs(share - _gloom) < 0.005f)
            return;
        _gloom = share;
        foreach (var group in _groups.Values)
        {
            foreach (var light in group.Wall)
                light.LightEnergy = EmergencyEnergy * share;
            group.Glass.EmissionEnergyMultiplier = 4 * Mathf.Max(share, 0.5f);
        }
    }

    // ---- culling and particles ---------------------------------------------------------------------

    /// <summary>
    /// The walls as boxes that hide what is behind them, a little inside the walls so the wall modules on their faces are
    /// never hidden by their own wall. Everything inside a wall's column is never seen, so the boxes stand full height.
    /// </summary>
    private OccluderInstance3D Occluder()
    {
        const float inset = 0.3f, low = -0.5f, high = 24;
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var rect in Layout.Occluders)
        {
            if (rect.Width < 1 || rect.Depth < 1)
                continue;
            var (x0, x1) = (rect.X0 + inset, rect.X1 - inset);
            var (z0, z1) = (rect.Y0 + inset, rect.Y1 - inset);
            var first = vertices.Count;
            foreach (var y in new[] { low, high })
            {
                vertices.Add(new Vector3(x0, y, z0));
                vertices.Add(new Vector3(x1, y, z0));
                vertices.Add(new Vector3(x1, y, z1));
                vertices.Add(new Vector3(x0, y, z1));
            }
            // Four sides and the top; nothing is seen from below the floor.
            int[] quads = [0, 1, 5, 4, 1, 2, 6, 5, 2, 3, 7, 6, 3, 0, 4, 7, 4, 5, 6, 7];
            for (var q = 0; q < quads.Length; q += 4)
                indices.AddRange([first + quads[q], first + quads[q + 1], first + quads[q + 2], first + quads[q], first + quads[q + 2], first + quads[q + 3]]);
        }
        var occluder = new ArrayOccluder3D();
        occluder.SetArrays([.. vertices], [.. indices]);
        return new OccluderInstance3D { Name = "Occluder", Occluder = occluder };
    }

    /// <summary>
    /// What sparks and debris land on and bounce off indoors (the effects' camera-following height field would take the
    /// ceilings for ground): the deck's floor, and each room's ceiling.
    /// </summary>
    private void Colliders()
    {
        var deck = Layout.Deck;
        AddChild(new GpuParticlesCollisionBox3D { Name = "FloorCollision", Size = new Vector3(deck.Width, 2, deck.Depth), Position = new Vector3(deck.Width / 2f, -1, deck.Depth / 2f) });
        foreach (var room in Layout.Rooms)
        {
            var height = DeckLayout.ZoneHeight(room) ?? DeckLayout.DefaultHeight;
            var size = room.Max - room.Min;
            var centre = room.Centre();
            AddChild(new GpuParticlesCollisionBox3D { Name = $"{room.Name}Ceiling", Size = new Vector3(size.X, 2, size.Y), Position = new Vector3(centre.X, height + 1, centre.Y) });
        }
    }
}
