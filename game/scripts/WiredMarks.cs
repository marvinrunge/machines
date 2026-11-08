using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// What a Wired run marks in the world: wrecks glint in gold, faster as they are about to go, crates hover in a shaft of light,
/// and the circle of a coming ion strike lies on the ground, with a ring closing in on the moment it comes down. A hero's
/// reach over wrecks shows as sparks flying to it. It only shows what the world holds. Wired is the remake's own addition.
/// </summary>
public partial class WiredMarks : Node3D
{
    private const int MostGlints = 256;

    // The rings and sectors of a circle's ground mesh, and how far above the ground it lies.
    private const int Rings = 26, Sectors = 96;
    private const float Lift = 0.6f;

    /// <summary>Seconds a spark takes to the hero.</summary>
    private const float Flight = 0.35f;

    private static readonly Color Wreck = new(1f, 0.74f, 0.25f), Crate = new(0.35f, 1f, 0.62f);

    private static readonly Lazy<Shader> GlintShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            varying vec4 tint;
            void vertex() {
                // Faces the camera, and keeps a size on screen however far away it is.
                vec4 centre = MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0);
                float size = clamp(length(centre.xyz) * 0.12, 2.6, 36.0) * INSTANCE_CUSTOM.x;
                POSITION = PROJECTION_MATRIX * (centre + vec4(VERTEX.xy * size, 0.0, 0.0));
                tint = COLOR;
            }
            void fragment() {
                vec2 p = UV * 2.0 - 1.0;
                float core = exp(-dot(p, p) * 26.0);
                float star = exp(-abs(p.x) * 12.0 - abs(p.y) * 2.4) + exp(-abs(p.y) * 12.0 - abs(p.x) * 2.4);
                float halo = exp(-dot(p, p) * 6.0) * 0.45;
                float edge = 1.0 - smoothstep(0.7, 1.0, length(p));
                // Laid over the ground, not added to it: on bright sand a glow would vanish.
                ALBEDO = mix(tint.rgb, vec3(1.0), clamp(core * 1.4, 0.0, 1.0));
                ALPHA = clamp(core * 1.5 + star + halo, 0.0, 1.0) * edge * tint.a;
            }
            """,
    });

    private static readonly Lazy<Shader> ShaftShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            uniform vec3 colour : source_color = vec3(0.35, 1.0, 0.62);
            void fragment() {
                float across = 1.0 - abs(dot(NORMAL, VIEW));
                ALBEDO = colour * pow(UV.y, 2.5) * (1.0 - across * across) * 0.55;
            }
            """,
    });

    private static readonly Lazy<Shader> CircleShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            uniform float left = 1.0;
            void fragment() {
                float r = UV.x;
                float rim = smoothstep(0.962, 0.975, r) * (1.0 - smoothstep(0.994, 1.0, r));
                float closing = 1.0 - smoothstep(0.004, 0.016, abs(r - left));
                float dashes = step(0.5, fract(UV.y * 40.0 + TIME * 0.5));
                float band = smoothstep(0.885, 0.895, r) * (1.0 - smoothstep(0.945, 0.955, r)) * dashes;
                float fill = (0.1 + 0.26 * (1.0 - left)) * (0.8 + 0.2 * sin(TIME * (6.0 + 10.0 * (1.0 - left)))) * (1.0 - step(1.0, r));
                ALBEDO = mix(vec3(1.0, 0.16, 0.06), vec3(1.0, 0.9, 0.7), closing * 0.8);
                ALPHA = max(max(rim, closing), max(band * 0.9, fill));
            }
            """,
    });

    private readonly record struct Flying(Vector3 From, double Since);

    private readonly Game _game;
    private readonly Dictionary<int, (Node3D Node, MeshInstance3D Box)> _crates = [];
    private readonly Dictionary<WiredStrike, (MeshInstance3D Mesh, ShaderMaterial Material)> _circles = [];
    private readonly List<Flying> _flying = [];
    private MultiMesh _glints = null!;
    private StandardMaterial3D? _crateSkin;
    private double _time;

    public WiredMarks(Game game)
    {
        _game = game;
        Name = "WiredMarks";
    }

    public override void _Ready()
    {
        _glints = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One * 2, Material = new ShaderMaterial { Shader = GlintShader.Value, RenderPriority = 10 } },
            InstanceCount = MostGlints,
            VisibleInstanceCount = 0,
        };
        AddChild(new MultiMeshInstance3D
        {
            Name = "Glints",
            Multimesh = _glints,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-100000, -100000, -100000), new Vector3(200000, 200000, 200000)),
        });
    }

    /// <summary>A wreck picked up from afar: a spark flies from it to the hero.</summary>
    public void Fly(Vector3 from) => _flying.Add(new Flying(from, _time));

    /// <summary>Called every frame, with the hero's place if it has one.</summary>
    public void Show(double delta, Vector3? hero)
    {
        _time += delta;
        var world = _game.World;
        if (world.Wired is not { } run)
            return;
        ShowGlints(world, hero);
        ShowCrates(world);
        ShowCircles(world, run);
    }

    private void ShowGlints(GameWorld world, Vector3? hero)
    {
        var count = 0;
        foreach (var wreck in world.Debris)
        {
            if (count == MostGlints)
                break;
            // A wreck about to go glints in a hurry.
            var going = wreck.ExpiresAt - world.Time < 10;
            var twinkle = 0.72f + 0.28f * MathF.Sin((float)_time * (going ? 16 : 3.5f) + wreck.Id * 1.7f);
            Glint(count++, ActorView.World(wreck.Position) + Vector3.Up * 2.4f, Wreck, twinkle, 1 + 0.25f * twinkle);
        }
        foreach (var crate in world.WiredPickups)
        {
            if (count == MostGlints)
                break;
            Glint(count++, ActorView.World(crate.Position) + Vector3.Up * 3f, Crate, 0.85f + 0.15f * MathF.Sin((float)_time * 5), 1.5f);
        }
        _flying.RemoveAll(f => _time - f.Since > Flight);
        if (hero is { } to)
        {
            foreach (var spark in _flying)
            {
                if (count == MostGlints)
                    break;
                var share = (float)((_time - spark.Since) / Flight);
                Glint(count++, spark.From.Lerp(to, share * share) + Vector3.Up * (2 * MathF.Sin(share * Mathf.Pi)), Wreck, 1, 1.3f - share * 0.5f);
            }
        }
        _glints.VisibleInstanceCount = count;
    }

    private void Glint(int index, Vector3 place, Color colour, float strength, float size)
    {
        _glints.SetInstanceTransform(index, new Transform3D(Basis.Identity, place));
        _glints.SetInstanceColor(index, new Color(colour, strength));
        _glints.SetInstanceCustomData(index, new Color(size, 0, 0, 0));
    }

    private void ShowCrates(GameWorld world)
    {
        foreach (var crate in world.WiredPickups)
        {
            if (!_crates.ContainsKey(crate.Id))
                _crates[crate.Id] = MakeCrate(crate);
            var (node, box) = _crates[crate.Id];
            box.Position = Vector3.Up * (1.5f + 0.2f * MathF.Sin((float)_time * 2 + crate.Id));
            box.Rotation = new Vector3(0, (float)_time * 0.8f + crate.Id, 0);
            node.Visible = true;
        }
        foreach (var id in _crates.Keys.Where(id => world.WiredPickups.All(p => p.Id != id)).ToList())
        {
            _crates[id].Node.QueueFree();
            _crates.Remove(id);
        }
    }

    private (Node3D Node, MeshInstance3D Box) MakeCrate(WiredPickup crate)
    {
        var node = new Node3D { Name = $"Crate{crate.Id}", Position = ActorView.World(crate.Position) };
        var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.8f, 1.3f, 1.8f) }, MaterialOverride = _crateSkin ??= CrateSkin() };
        node.AddChild(box);
        node.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 1.1f, Height = 26, CapTop = false, CapBottom = false, RadialSegments = 12, Rings = 1 },
            Position = Vector3.Up * 13,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new ShaderMaterial { Shader = ShaftShader.Value },
        });
        AddChild(node);
        return (node, box);
    }

    /// <summary>Dark plates with a lit frame and cross, drawn here, so that a crate needs no export.</summary>
    private static StandardMaterial3D CrateSkin()
    {
        const int Size = 64;
        var (plate, light) = (Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8), Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8));
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var frame = x < 5 || y < 5 || x >= Size - 5 || y >= Size - 5;
                var cross = Math.Abs(x - y) < 3 || Math.Abs(x + y - Size) < 3;
                plate.SetPixel(x, y, frame ? new Color(0.3f, 0.34f, 0.32f) : new Color(0.1f, 0.13f, 0.12f));
                light.SetPixel(x, y, frame || cross ? Crate : Colors.Black);
            }
        }
        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(plate),
            Metallic = 0.4f,
            Roughness = 0.5f,
            EmissionEnabled = true,
            EmissionTexture = ImageTexture.CreateFromImage(light),
            EmissionEnergyMultiplier = 1.6f,
            Uv1Triplanar = false,
        };
    }

    private void ShowCircles(GameWorld world, WiredRun run)
    {
        foreach (var strike in run.Strikes)
        {
            if (!_circles.ContainsKey(strike))
                _circles[strike] = MakeCircle(strike);
            _circles[strike].Material.SetShaderParameter("left", Mathf.Clamp((strike.At - world.Time) / WiredBalance.IonWarning, 0, 1));
        }
        foreach (var strike in _circles.Keys.Where(s => !run.Strikes.Contains(s)).ToList())
        {
            _circles[strike].Mesh.QueueFree();
            _circles.Remove(strike);
        }
    }

    /// <summary>A disc lying on the ground as it is, its u the share of the radius and its v the way round.</summary>
    private (MeshInstance3D Mesh, ShaderMaterial Material) MakeCircle(WiredStrike strike)
    {
        var (points, marks, indices) = (new List<Vector3>(), new List<Vector2>(), new List<int>());
        for (var ring = 0; ring <= Rings; ring++)
        {
            var share = ring / (float)Rings;
            for (var sector = 0; sector <= Sectors; sector++)
            {
                var angle = Mathf.Tau * sector / Sectors;
                var way = new System.Numerics.Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (strike.Radius * share);
                points.Add(ActorView.World(strike.Position + way) + Vector3.Up * Lift);
                marks.Add(new Vector2(share, sector / (float)Sectors));
            }
        }
        for (var ring = 0; ring < Rings; ring++)
        {
            for (var sector = 0; sector < Sectors; sector++)
            {
                var (a, b) = (ring * (Sectors + 1) + sector, (ring + 1) * (Sectors + 1) + sector);
                indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = marks.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var material = new ShaderMaterial { Shader = CircleShader.Value };
        var view = new MeshInstance3D { Name = "IonCircle", Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(view);
        return (view, material);
    }
}
