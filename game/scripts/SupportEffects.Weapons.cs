using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Game;

/// <summary>
/// The classic look's bolters and flamethrowers. A bolter draws no shot at all: its flash stands at the muzzle and
/// sparks at what it hits (MachPhysBolter); a flamethrower throws balls of fire (MachPhysFlameBall).
/// </summary>
public partial class SupportEffects
{
    private const float FlashTime = 0.1f, SparkSize = 2.5f, SparkTime = 0.3f, FlameBallSize = 0.8f, FlameBallTime = 1.4f, LitUpTime = 0.1f;
    private const int FlameBallPictures = 17;

    /// <summary>
    /// The flash at a bolter's muzzle for a tenth of a second (MachPhysBolter::fire), the bolter's smaller, the heavy
    /// one's longer, and the patch of light on the ground under it for as long as its light is on (<paramref name="lit"/>).
    /// </summary>
    public void BolterFlash(Vector3 at, Vector3 along, string weapon, float lit)
    {
        var (name, scale) = weapon switch
        {
            "BOLTER" => ("boltflash", Vector3.One * 0.8f),
            "AUTO_CANNON" => ("autoflash", Vector3.One),
            "HEAVY_BOLTER1" or "HEAVY_BOLTER2" => ("heavyflash", new Vector3(1.5f, 1, 1)),
            _ => ((string?)null, Vector3.One),
        };
        if (name is null || _assets.EffectMesh(name) is not { } mesh || along.LengthSquared() < 0.0001f)
            return;
        var up = Mathf.Abs(along.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        var side = along.Cross(up).Normalized();
        var flash = new MeshInstance3D { Mesh = mesh, Name = "BolterFlash", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(flash);
        flash.GlobalTransform = new Transform3D(new Basis(along * scale.X, side.Cross(along) * scale.Y, side * scale.Z), at);
        _timed.Add(new Timed { Node = flash, Duration = FlashTime, Update = _ => { } });
        if (_assets.EffectMesh("groundlight") is { } glow && HeightField.Current is { } ground)
        {
            var patch = new MeshInstance3D { Mesh = glow, Name = "GroundLight", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(patch);
            patch.GlobalPosition = new Vector3(at.X, ground.At(at.X, at.Z) + ActorView.GroundLightHeight, at.Z);
            _timed.Add(new Timed { Node = patch, Duration = lit, Update = _ => { } });
        }
    }

    // What is lit up now and the white that lies over it. A light of the original's that falls on nothing else
    // (W4dUniformLight, scope LOCAL), which an overlay is here.
    private readonly HashSet<Node> _litUp = [];
    private static readonly Lazy<StandardMaterial3D> LitUp = new(() => new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        AlbedoColor = new Color(0.6f, 0.6f, 0.6f),
    });

    /// <summary>A pulse bolt lights up what it hits for a tenth of a second (lightingUpVictim in pulsewep.cpp).</summary>
    public void LightUp(Node victim, IEnumerable<MeshInstance3D> shape)
    {
        if (!_litUp.Add(victim))
            return;
        var meshes = shape.ToArray();
        var kept = meshes.Select(m => m.MaterialOverlay).ToArray();
        foreach (var mesh in meshes)
            mesh.MaterialOverlay = LitUp.Value;
        GetTree().CreateTimer(LitUpTime).Timeout += () =>
        {
            _litUp.Remove(victim);
            for (var i = 0; i < meshes.Length; i++)
                if (IsInstanceValid(meshes[i]) && meshes[i].MaterialOverlay == LitUp.Value)
                    meshes[i].MaterialOverlay = kept[i];
        };
    }

    /// <summary>Sparks on what a bolt hit, where the line from the shooter meets it (MachPhysBolter::impactSparks).</summary>
    public void BoltSparks(ActorView victim, Vector3 from) => BoltSparks(victim.Centre, victim.Bounds.Size, from);

    /// <summary>Sparks on something of this <paramref name="size"/> around <paramref name="centre"/>.</summary>
    public void BoltSparks(Vector3 centre, Vector3 size, Vector3 from)
    {
        var pictures = Pictures(3, i => $"models/texture4/bhit{i}_bt.bmp");
        if (pictures.Length == 0)
            return;
        var way = from - centre;
        var towards = way.LengthSquared() < 0.01f ? Vector3.Up : way.Normalized();
        var reach = new Vector3(size.X / 2 / Mathf.Max(Mathf.Abs(towards.X), 0.01f), size.Y / 2 / Mathf.Max(Mathf.Abs(towards.Y), 0.01f), size.Z / 2 / Mathf.Max(Mathf.Abs(towards.Z), 0.01f));
        var (spark, material) = Sprite("BoltSparks", SparkSize, pictures[0]);
        AddChild(spark);
        spark.GlobalPosition = centre + towards * (reach[(int)reach.MinAxisIndex()] + 0.5f);
        _timed.Add(new Timed
        {
            Node = spark,
            Duration = SparkTime,
            Update = age => material.AlbedoTexture = pictures[Mathf.Min(pictures.Length - 1, (int)(age / SparkTime * pictures.Length))],
        });
    }

    /// <summary>
    /// A burst of flame: per ball the EXTRAS give when it leaves, how long it hangs at the muzzle, how much further than
    /// the weapon's range it goes, how far up and how many times its size it ends. It burns through its pictures on
    /// the way (MachPhysFlameThrower::createFlameBall, MachPhysFlameBall::move), and goes out where it meets the mark.
    /// </summary>
    public void FlameBalls(Vector3 from, Vector3 to, WeaponSpec weapon)
    {
        var pictures = Pictures(FlameBallPictures, i => $"models/texture4/ex{i:D2}_bt.bmp");
        var extras = Blast.Extras(weapon);
        var along = (to - from).Normalized();
        if (pictures.Length == 0 || extras.Count < 5 || along.LengthSquared() < 0.5f)
            return;
        var first = Enumerable.Range(0, extras.Count / 5).Min(i => extras[i * 5]);
        for (var i = 0; i + 4 < extras.Count; i += 5)
        {
            var (start, delay, further, rise, scale) = (extras[i] - first, extras[i + 1], extras[i + 2], extras[i + 3], extras[i + 4]);
            var end = from + along * (weapon.Range + further) + Vector3.Up * rise;
            var meets = Mathf.Min(1, (from.DistanceTo(to) + 1) / from.DistanceTo(end));
            var (ball, material) = Sprite("FlameBall", FlameBallSize, pictures[0]);
            AddChild(ball);
            ball.GlobalPosition = from;
            ball.Visible = false;
            _timed.Add(new Timed
            {
                Node = ball,
                Duration = start + FlameBallTime,
                Update = age =>
                {
                    var burnt = age - start;
                    var flown = Mathf.Clamp((burnt - delay) / (FlameBallTime - delay), 0, 1);
                    ball.Visible = burnt >= 0 && flown < meets;
                    if (!ball.Visible)
                        return;
                    material.AlbedoTexture = pictures[Mathf.Min(pictures.Length - 1, (int)(burnt / FlameBallTime * pictures.Length))];
                    ball.GlobalPosition = from.Lerp(end, flown);
                    ball.Scale = Vector3.One * Mathf.Lerp(1, scale, flown);
                },
            });
        }
    }

    /// <summary>The sixteen pictures of fire (MachPhysEffects::flameTextures).</summary>
    public Texture2D[] FirePictures => _firePictures ??= Pictures(16, i => $"models/texture4/fire/fl{i:D2}_bt.bmp");
    private Texture2D[]? _firePictures;

    private Texture2D[] Pictures(int count, Func<int, string> name) =>
        Enumerable.Range(0, count).Select(i => _assets.Texture(name(i))).OfType<Texture2D>().ToArray();

    /// <summary>A square facing the camera that shines by itself, whose black is see-through.</summary>
    private static (MeshInstance3D Sprite, StandardMaterial3D Material) Sprite(string name, float size, Texture2D picture)
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
            AlbedoTexture = picture,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var sprite = new MeshInstance3D
        {
            Name = name,
            Mesh = new QuadMesh { Size = Vector2.One * size },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        return (sprite, material);
    }
}
