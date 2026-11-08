using Godot;
using Machines.Sim.Presentation;

namespace Machines.Game;

/// <summary>
/// How a damaged artefact looks (MachPhysArtefact::damageLevel, MachPhysEntityDamage for what is no building): it
/// darkens from the first hit and smokes in the dark colours from 40 % and 75 % damage, a splat lies under it from 60 %,
/// and it burns from 65 %: one flame in the middle, or, if it is much longer than wide, one at each end, the second from
/// 85 %. A child of the artefact's model; the box is that model's.
/// </summary>
public partial class ArtefactDamage : Node3D
{
    private Node3D _model = null!;
    private Aabb _box;
    private Mesh? _splatMesh;
    private float _flameHeight;

    private GpuParticles3D? _firstSmoke, _secondSmoke, _firstFlame, _secondFlame;
    private Node3D? _splat;
    private int _darkness;

    public static ArtefactDamage Create(Node3D model, Aabb box, Mesh? splat)
    {
        var damage = new ArtefactDamage
        {
            Name = "Damage", _model = model, _box = box, _splatMesh = splat,
            _flameHeight = box.Size.Y * (1.2f + 0.6f * Random.Shared.NextSingle()),
        };
        model.AddChild(damage);
        return damage;
    }

    public void Show(float damage)
    {
        var size = _box.Size;
        Darken(Mathf.Clamp((int)(damage / 5), 0, 19));
        var (first, second) = BuildingDamage.SmokeOf("", null);
        var width = Mathf.Max(size.X, size.Z);
        var height = Mathf.Min(size.Y, 2 * width);
        var centre = _box.GetCenter();
        _firstSmoke = Smoke(_firstSmoke, damage >= BuildingDamage.FirstSmoke, first, height, width, new Vector3(centre.X, height * 0.4f, centre.Z));
        _secondSmoke = Smoke(_secondSmoke, damage >= BuildingDamage.SecondSmoke, second, height, width, new Vector3(centre.X, height * 0.65f, centre.Z));

        var ground = _box.Position.Y;
        if (size.X > 1.8f * size.Z || size.Z > 1.8f * size.X)
        {
            var along = size.X > size.Z ? new Vector3(size.X * 0.25f, 0, 0) : new Vector3(0, 0, size.Z * 0.25f);
            var narrow = Mathf.Min(size.X, size.Z);
            var foot = new Vector3(centre.X, ground, centre.Z);
            _firstFlame = Flame(_firstFlame, damage >= BuildingDamage.SecondFlame, foot + along, narrow);
            _secondFlame = Flame(_secondFlame, damage >= BuildingDamage.ThirdFlame, foot - along, narrow);
        }
        else
            _secondFlame = Flame(_secondFlame, damage >= BuildingDamage.ThirdFlame, new Vector3(centre.X, ground, centre.Z), Mathf.Min(size.X, size.Z));

        var splat = damage >= BuildingDamage.Splat;
        if (splat && _splat is null && _splatMesh is { } mesh)
        {
            var scale = ActorView.SplatScale(mesh);
            _splat = new MeshInstance3D
            {
                Mesh = mesh,
                Name = "Splat",
                Scale = new Vector3(size.X * scale.X, 1, size.Z * scale.Y),
                Position = new Vector3(centre.X, ground + 0.1f, centre.Z),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = ActorView.SplatMaterial(mesh),
            };
            AddChild(_splat);
        }
    }

    private GpuParticles3D? Smoke(GpuParticles3D? plume, bool on, (float R, float G, float B) colour, float height, float width, Vector3 at)
    {
        if (plume is null && on)
        {
            plume = Effects.Plume(5, 4, height + 4, width * 0.5f, new Color(colour.R, colour.G, colour.B, 0.7f));
            plume.Position = at;
            AddChild(plume);
        }
        if (plume is not null)
            plume.Emitting = on;
        return plume;
    }

    private GpuParticles3D? Flame(GpuParticles3D? flame, bool on, Vector3 at, float width)
    {
        if (flame is null && on)
        {
            flame = Effects.DamageFire(width, _flameHeight);
            flame.Position = at;
            AddChild(flame);
        }
        if (flame is not null)
            flame.Emitting = on;
        return flame;
    }

    private void Darken(int step)
    {
        if (step == _darkness)
            return;
        _darkness = step;
        var veil = ActorView.Darkening(step);
        foreach (var mesh in Meshes(_model))
            mesh.MaterialOverlay = veil;
    }

    /// <summary>Every mesh of an artefact's model itself, not of its damage.</summary>
    public static IEnumerable<MeshInstance3D> Meshes(Node3D model) =>
        model.FindChildren("*", nameof(MeshInstance3D), true, false).Append(model).OfType<MeshInstance3D>().Where(mesh => mesh.GetParent() is not ArtefactDamage);
}
