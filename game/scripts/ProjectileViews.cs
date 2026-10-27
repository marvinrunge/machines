using Godot;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The rounds in flight (<see cref="GameWorld.Projectiles"/>) as the original drew them: each weapon's own
/// projectile model (plasma and pulse bolts, missiles, stings) flying nose first at the height it was fired
/// from, and where one strikes, its weapon's splat. Without the exported effects a glowing streak stands in.
/// </summary>
public partial class ProjectileViews : Node3D
{
    // The projectile model each weapon fires (machphys: plasmbol, pulseblb, missile, stingl, stingm).
    private static readonly Dictionary<string, string> Models = new()
    {
        ["PLASMA_RIFLE"] = "plaboltr", ["PLASMA_CANNON1"] = "plaboltc", ["PLASMA_CANNON2"] = "plabolt2",
        ["PULSE_RIFLE"] = "pulboltr", ["PULSE_CANNON"] = "pulboltc",
        ["MULTI_LAUNCHER1"] = "mis1", ["MULTI_LAUNCHER2"] = "mis2", ["MULTI_LAUNCHER3"] = "mis3", ["MULTI_LAUNCHER4"] = "mis4",
        ["MULTI_LAUNCHER5"] = "mis5", ["MULTI_LAUNCHER6"] = "mis6", ["MULTI_LAUNCHER7"] = "mis7",
        ["LARGE_MISSILE"] = "lmiss", ["WASP_LIGHT_STING"] = "stinglight", ["WASP_METAL_STING"] = "stingmetal",
        ["VIRUS"] = "virusblt",
    };

    /// <summary>Rounds leave the gun about this high above the ground and keep to it.</summary>
    private const float Height = 1.6f;

    private readonly Dictionary<int, (Projectile Projectile, Node3D View, Vector3 Muzzle)> _shown = [];
    private Func<int, ActorView?> _viewOf = _ => null;

    /// <summary>Over this distance a round goes from its muzzle onto the line the simulation flies it along.</summary>
    private const float Converge = 20;
    private GameWorld _world = null!;
    private ClassicAssets _assets = null!;

    public static ProjectileViews Create(GameWorld world, ClassicAssets assets, Func<int, ActorView?> viewOf, Effects? effects = null) =>
        new() { Name = "Projectiles", _world = world, _assets = assets, _viewOf = viewOf, _effects = effects };

    private Effects? _effects;

    /// <summary>Missiles (the multi-launchers' and the large missile) trail vapour and burst in fireballs (MachPhysMissile).</summary>
    private static bool IsMissile(string weapon) => weapon.StartsWith("MULTI_LAUNCHER") || weapon == "LARGE_MISSILE";

    public override void _Process(double delta)
    {
        var live = new HashSet<int>();
        foreach (var projectile in _world.Projectiles)
        {
            live.Add(projectile.Id);
            if (!_shown.TryGetValue(projectile.Id, out var shown))
            {
                // It leaves the weapon's muzzle and comes onto its line of flight as it goes.
                var muzzle = _viewOf(projectile.Shooter)?.Muzzle(projectile.Mounting, projectile.Weapon, projectile.Barrel) is { } at
                    ? at - Ground(projectile.Start)
                    : Vector3.Zero;
                shown = (projectile, NewView(projectile), muzzle);
                _shown[projectile.Id] = shown;
                _line[projectile.Id] = projectile.Position;
                // Straight from the muzzle's height to the height of what it was fired at, whatever the ground
                // does in between, as the original's rounds flew.
                var from = Ground(projectile.Start).Y + muzzle.Y;
                var to = AimHeight(projectile);
                _climb[projectile.Id] = (from, (to - from) / Mathf.Max(1, System.Numerics.Vector2.Distance(projectile.Start, projectile.Aim)));
                AddChild(shown.View);
            }
            // Between ticks the round flies on at its speed, so it moves smoothly; the tick sets it right again.
            var ahead = _line[projectile.Id] + projectile.Direction * projectile.Weapon.ProjectileSpeed * (float)delta;
            _line[projectile.Id] = System.Numerics.Vector2.Distance(ahead, projectile.Position) < projectile.Weapon.ProjectileSpeed * 0.1f
                ? ahead
                : projectile.Position;
            var travelled = System.Numerics.Vector2.Distance(_line[projectile.Id], projectile.Start);
            var (height, slope) = _climb[projectile.Id];
            var sideways = shown.Muzzle with { Y = 0 };
            Place(shown.View, _line[projectile.Id], projectile, sideways * Mathf.Clamp(1 - travelled / Converge, 0, 1), height + slope * travelled, slope);
        }

        foreach (var id in _shown.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _line.Remove(id);
            _climb.Remove(id);
            var (projectile, view, _) = _shown[id];
            _shown.Remove(id);
            // Struck short of its range: the weapon's splat where it hit.
            if (projectile.Travelled < projectile.Weapon.Range - 0.5f)
                Splat(view.GlobalPosition, projectile.Weapon.TypeName);
            if (IsMissile(projectile.Weapon.TypeName))
            {
                // A missile bursts wherever its flight ends, and its trail drifts on and thins out behind it.
                if (GraphicsSettings.Remake)
                    _effects?.Impact(view.GlobalPosition);
                if (view.GetNodeOrNull<GpuParticles3D>("Trail") is { } trail)
                {
                    trail.Reparent(this);
                    trail.Emitting = false;
                    GetTree().CreateTimer(trail.Lifetime + 0.2).Timeout += trail.QueueFree;
                }
            }
            view.QueueFree();
        }
    }

    private static Vector3 Direction(Projectile projectile) => new(projectile.Direction.X, 0, projectile.Direction.Y);

    private static Vector3 Ground(System.Numerics.Vector2 at) => ActorView.World(at) + Vector3.Up * Height;

    /// <summary>
    /// How high the point a round was fired at lies: the middle of the actor there (or the one it was aimed to
    /// miss, which the simulation aims at most 4.5 m beside it), so it flies up to a flyer; else above the ground.
    /// </summary>
    private float AimHeight(Projectile projectile)
    {
        var target = _world.Actors
            .Where(a => a.Id != projectile.Shooter && !a.IsDead
                && System.Numerics.Vector2.Distance(a.Position, projectile.Aim) <= a.Radius + 5)
            .OrderBy(a => System.Numerics.Vector2.Distance(a.Position, projectile.Aim) - a.Radius)
            .FirstOrDefault();
        return target is not null && _viewOf(target.Id) is { } view ? view.Centre.Y : Ground(projectile.Aim).Y;
    }

    private static void Place(Node3D view, System.Numerics.Vector2 at, Projectile projectile, Vector3 offset, float height, float slope)
    {
        // Nose along the line of flight, climbing or falling with it; the models point along their x axis and
        // the basis keeps Godot's handedness.
        var forward = (Direction(projectile) + Vector3.Up * slope).Normalized();
        var side = forward.Cross(Vector3.Up).Normalized();
        var position = ActorView.World(at) + offset;
        view.GlobalTransform = new Transform3D(new Basis(forward, side.Cross(forward), side), position with { Y = height });
    }

    // Where each round is on its line of flight, moved on smoothly between ticks, and the height it starts at
    // and climbs (or falls) by per metre.
    private readonly Dictionary<int, System.Numerics.Vector2> _line = [];
    private readonly Dictionary<int, (float Height, float Slope)> _climb = [];

    /// <summary>Energy rounds in flight that light their way, at most this many at once.</summary>
    private const int MostLitRounds = 8;
    private int _litRounds;

    private Node3D NewView(Projectile projectile)
    {
        var view = Shape(projectile);
        var name = projectile.Weapon.TypeName;
        // Missiles trail vapour from their tail; the models point along their x axis.
        if (IsMissile(name))
        {
            var trail = Effects.VapourTrail();
            view.AddChild(trail);
            trail.Position = new Vector3(-0.8f, 0, 0);
        }
        // In the remake plasma and pulse rounds light the ground and machines they pass.
        if (GraphicsSettings.Remake && _litRounds < MostLitRounds && (name.Contains("PLASMA") || name.Contains("PULSE")))
        {
            _litRounds++;
            view.AddChild(new OmniLight3D
            {
                LightColor = name.Contains("PLASMA") ? new Color(0.35f, 1f, 0.45f) : new Color(0.45f, 0.7f, 1f),
                LightEnergy = 2f,
                OmniRange = 9,
                ShadowEnabled = false,
            });
            view.TreeExiting += () => _litRounds--;
        }
        return view;
    }

    private Node3D Shape(Projectile projectile)
    {
        if (projectile.Weapon.TypeName == "ORB_OF_TREACHERY" && Orb(projectile.Race) is { } orb)
            return orb;
        if (Models.TryGetValue(projectile.Weapon.TypeName, out var model) && _assets.EffectMesh(model) is { } mesh)
            return new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };

        // A streak in the weapon's colour.
        var colour = projectile.Weapon.TypeName.Contains("PLASMA") ? new Color(0.3f, 1, 0.4f)
            : projectile.Weapon.TypeName.Contains("PULSE") ? new Color(0.4f, 0.7f, 1)
            : new Color(1, 0.8f, 0.4f);
        return new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.6f, 0.2f, 0.2f) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = colour,
                EmissionEnabled = true,
                Emission = colour,
                EmissionEnergyMultiplier = 3,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>
    /// The orb of treachery (MachPhysTreacheryOrb): a ring of the shooter's colour (MachPhysRadialDisc's ring_bt)
    /// that opens from nothing to 4 m across over 0.7 s, fading in, and flies on at half strength. It always faces
    /// the camera; the original's disc faced along its flight, edge-on from above.
    /// </summary>
    private Node3D? Orb(Machines.Import.Model.Race race)
    {
        if (_assets.Texture("models/texture4/ring_bt.bmp") is not { } ring)
            return null;
        var colour = ClassicAssets.RaceColour(race);
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
            AlbedoTexture = ring,
            AlbedoColor = colour with { A = 0 },
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var orb = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = Vector3.One * 0.01f,
        };
        var open = CreateTween().BindNode(orb).SetParallel();
        open.TweenProperty(orb, "scale", Vector3.One * 2, 0.7f);
        open.TweenProperty(material, "albedo_color", colour with { A = 1 }, 0.7f);
        open.Chain().TweenProperty(material, "albedo_color", colour with { A = 0.5f }, 0.05f);
        return orb;
    }

    /// <summary>The weapon's impact splat (plasplat, pulsplat), growing and fading in a third of a second.</summary>
    private void Splat(Vector3 at, string weapon)
    {
        var name = weapon.Contains("PLASMA") ? "splat" : weapon.Contains("PULSE") ? "pulsesplat" : null;
        if (name is null || _assets.EffectMesh(name) is not { } mesh)
            return;
        var splat = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(splat);
        splat.GlobalPosition = at;
        var grow = CreateTween();
        grow.TweenProperty(splat, "scale", Vector3.One * 2.5f, 0.33f).From(Vector3.One * 0.5f);
        grow.TweenCallback(Callable.From(splat.QueueFree));
    }
}
