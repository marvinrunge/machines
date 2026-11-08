using Godot;
using Machines.Sim.Presentation;
using Machines.Sim.World;

namespace Machines.Game;

// How a damaged building looks (MachPhysEntityDamage), and how a machine comes and goes by teleport.
public partial class ActorView
{
    private GpuParticles3D? _firstSmoke, _secondSmoke;
    private readonly GpuParticles3D?[] _flames = new GpuParticles3D?[3];
    private Node3D? _splat;
    private int _darkness;

    /// <summary>The ground splat under a building at 60 % damage (fx/groundsplat), from the game's effects.</summary>
    public Mesh? SplatMesh { get; set; }

    // Darkening in steps of five per cent, each shared by all buildings.
    private static readonly Dictionary<int, StandardMaterial3D> Darkenings = [];

    private void ShowBuildingDamage(Construction building, float damage)
    {
        var (type, subType, level) = (building.Spec.Type, building.Spec.SubType, building.Stats.HardwareLevel);
        Darken(Mathf.Clamp((int)(damage / 5), 0, 19));
        var (first, second) = BuildingDamage.SmokeOf(type, subType);
        var size = _bounds.Size;
        var width = Mathf.Max(size.X, size.Z);
        // Thin, tall buildings such as a beacon count only so high, or the smoke would look out of place.
        var height = Mathf.Min(size.Y, 2 * width);
        var centre = _bounds.GetCenter();
        _firstSmoke = Smoke(_firstSmoke, damage >= BuildingDamage.FirstSmoke, first, height, width, new Vector3(centre.X, height * 0.4f, centre.Z));
        _secondSmoke = Smoke(_secondSmoke, damage >= BuildingDamage.SecondSmoke, second, height, width, new Vector3(centre.X, height * 0.65f, centre.Z));
        var flames = BuildingDamage.FlamesOf(type, subType, level);
        if (flames.Count == 3)
        {
            Flame(0, flames[0], damage >= BuildingDamage.FirstFlame);
            Flame(1, flames[1], damage >= BuildingDamage.SecondFlame);
            Flame(2, flames[2], damage >= BuildingDamage.ThirdFlame);
        }
        var splat = damage >= BuildingDamage.Splat;
        if (splat && _splat is null && SplatMesh is { } mesh)
        {
            var scale = SplatScale(mesh);
            _splat = new MeshInstance3D
            {
                Mesh = mesh,
                Name = "Splat",
                Scale = new Vector3(size.X * scale.X, 1, size.Z * scale.Y),
                Position = Vector3.Up * 0.1f,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = SplatMaterial(mesh),
            };
            AddChild(_splat);
        }
        if (_splat is not null)
            _splat.Visible = splat;
    }

    /// <summary>
    /// The dark patch under a burning building: the original's 32 pixels, keyed black, or in the remake's effects
    /// those pixels smoothed into scorched ground.
    /// </summary>
    public static Material? SplatMaterial(Mesh mesh)
    {
        if (SplatMaterials.TryGetValue(GraphicsSettings.Effects, out var made))
            return made;
        if (mesh.SurfaceGetMaterial(0) is not StandardMaterial3D original)
            return null;
        var material = (StandardMaterial3D)original.Duplicate();
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        material.AlbedoColor = Colors.Black;
        if (GraphicsSettings.Effects && original.AlbedoTexture?.GetImage() is { } image)
        {
            image.ClearMipmaps();
            image.Convert(Image.Format.Rgba8);
            image.Resize(256, 256, Image.Interpolation.Cubic);
            for (var y = 0; y < 256; y++)
                for (var x = 0; x < 256; x++)
                    image.SetPixel(x, y, new Color(1, 1, 1, image.GetPixel(x, y).A * 0.8f));
            image.GenerateMipmaps();
            material.AlbedoTexture = ImageTexture.CreateFromImage(image);
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            material.AlbedoColor = new Color(0.04f, 0.03f, 0.025f);
        }
        return SplatMaterials[GraphicsSettings.Effects] = material;
    }

    private static readonly Dictionary<bool, Material> SplatMaterials = [];

    private GpuParticles3D? Smoke(GpuParticles3D? plume, bool on, (float R, float G, float B) colour, float height, float width, Vector3 at)
    {
        if (plume is null)
        {
            if (!on)
                return null;
            plume = Effects.Plume(5, 4, height + 4, width * 0.5f, new Color(colour.R, colour.G, colour.B, 0.7f));
            plume.Position = at;
            AddChild(plume);
        }
        plume.Emitting = on;
        return plume;
    }

    private void Flame(int index, BuildingFlame flame, bool on)
    {
        if (flame.Width * flame.Height == 0)
            return;
        if (_flames[index] is null)
        {
            if (!on)
                return;
            var fire = Effects.DamageFire(flame.Width, flame.Height);
            // Original axes, z up, as the models (see ClassicAssets): y and z swap.
            fire.Position = new Vector3(flame.X, flame.Z, flame.Y);
            // In the remake the flames light what is around them.
            if (GraphicsSettings.Effects)
                fire.AddChild(new OmniLight3D { LightColor = new Color(1, 0.55f, 0.2f), LightEnergy = 2, OmniRange = flame.Height * 1.5f, Position = Vector3.Up * flame.Height * 0.4f });
            AddChild(fire);
            _flames[index] = fire;
        }
        _flames[index]!.Emitting = on;
        if (_flames[index]!.GetChildCount() > 0 && _flames[index]!.GetChild(0) is OmniLight3D light)
            light.Visible = on;
    }

    /// <summary>The vertices go darker from the first hit to the last (MachPhysEntityDamage::darkening); a veil over its meshes here.</summary>
    private void Darken(int step)
    {
        if (step == _darkness || _rigView is null && GetNodeOrNull("Body") is null)
            return;
        _darkness = step;
        var veil = Darkening(step);
        foreach (var mesh in Shape())
            mesh.MaterialOverlay = veil;
    }

    /// <summary>The veil of a step of darkening, none at the first.</summary>
    public static Material? Darkening(int step)
    {
        if (!Darkenings.TryGetValue(step, out var veil) && step > 0)
            Darkenings[step] = veil = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0, 0, 0, step / 20f),
            };
        return step > 0 ? veil : null;
    }

    // ---- a machine burning ----------------------------------------------------------------------

    // The two flames on the highest part of a machine, each with the offset of its pictures; they burn a minute from
    // the last change to its health.
    private readonly List<(MeshInstance3D Flame, StandardMaterial3D Material, int Offset)> _topFlames = [];
    private float _flamesLeft, _flamesFrame, _burnedAt = -1;
    private static int _nextFlame;

    /// <summary>The sixteen pictures of fire, from the game's effects.</summary>
    public Texture2D[] FirePictures { get; set; } = [];

    // From 90 % damage each hit lights it for a minute, with its sound (MachPhysDoublesidedFlame::startFlame).
    private void BurnOn(float damage, float hitPoints)
    {
        if (damage < 90)
        {
            _burnedAt = -1;
            _flamesLeft = 0;
            foreach (var (flame, _, _) in _topFlames)
                flame.Visible = false;
            return;
        }
        if (hitPoints == _burnedAt)
            return;
        _burnedAt = hitPoints;
        Crackle(60);
        if (GraphicsSettings.Effects)
            return;
        if (_topFlames.Count == 0)
            LightTopFlames();
        _flamesLeft = 60;
        foreach (var (flame, _, _) in _topFlames)
            flame.Visible = true;
    }

    // Two crossed flames, as wide as the part each way and twice as high, stand on the part that reaches highest in
    // its own space, a faceplate apart (MachPhysMachineDamage::update).
    private void LightTopFlames()
    {
        if (FirePictures.Length == 0 || _rigView?.TopPart() is not { } top)
            return;
        var height = 2 * top.Box.Size.Y;
        foreach (var (width, turn) in new[] { (top.Box.Size.Z, Mathf.Pi / 2), (top.Box.Size.X, 0f) })
        {
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoTexture = FirePictures[0],
            };
            var flame = new MeshInstance3D
            {
                Name = "TopFlame",
                Mesh = new QuadMesh { Size = new Vector2(width, height) },
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = Vector3.Up * height * 0.48f,
                Rotation = Vector3.Up * turn,
            };
            top.Part.AddChild(flame);
            _topFlames.Add((flame, material, _nextFlame));
            _nextFlame = (_nextFlame + 5) % 16;
        }
    }

    // The flames show a picture a frame, as the original's, and go out after their minute.
    private void UpdateFlames(double delta)
    {
        if (_flamesLeft <= 0)
            return;
        _flamesLeft -= (float)delta;
        _flamesFrame += (float)delta * 30;
        foreach (var (flame, material, offset) in _topFlames)
        {
            flame.Visible = _flamesLeft > 0;
            material.AlbedoTexture = FirePictures[((int)_flamesFrame + offset) % FirePictures.Length];
        }
    }

    /// <summary>
    /// The classic look's smoke on a machine: black puffs as wide as it, doubling in a second as they rise a little from
    /// near its top, one every five seconds from 65 % damage, three together every six, rising farther, from 90 %
    /// (MachPhysMachineDamage::update).
    /// </summary>
    private void ShowClassicDamage(float damage)
    {
        var wisp = Mathf.Max(_bounds.Size.X, _bounds.Size.Z);
        var height = _bounds.Size.Y;
        _damageSmoke = Emitter(_damageSmoke, damage is >= 65 and < 90, () => Effects.Puffs(1, 5, height * 0.1f, wisp));
        _thickSmoke = Emitter(_thickSmoke, damage >= 90, () => Effects.Puffs(3, 6, height * 0.6f, wisp));
    }

    // Every mesh of the model itself, not of what is shown on it.
    public IEnumerable<MeshInstance3D> Shape()
    {
        var model = (Node?)_rigView ?? GetNodeOrNull("Body");
        return model is null ? [] : model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Append(model as MeshInstance3D).OfType<MeshInstance3D>()
            .Where(mesh => _topFlames.All(burning => burning.Item1 != mesh));
    }

    // ---- teleporting ----------------------------------------------------------------------------

    private StandardMaterial3D? _beam;
    private float _beamTime;

    /// <summary>
    /// The machine comes into the world, new from a factory or out of an APC (MachPhysMachine::doFadeInAnimation): its
    /// shape glows blue out of nothing for 0.75 s, then white, which fades away and comes back for 1.25 s.
    /// </summary>
    public void TeleportIn()
    {
        _beam = Blowup.BeamMaterial();
        _beamTime = 0;
        foreach (var mesh in Shape())
            mesh.MaterialOverride = _beam;
    }

    private void UpdateTeleport(double delta)
    {
        if (_beam is null)
            return;
        _beamTime += (float)delta;
        if (Blowup.BeamIn(_beam, _beamTime))
            return;
        _beam = null;
        foreach (var mesh in Shape())
            mesh.MaterialOverride = null;
    }

    /// <summary>The machine goes into an APC: see <see cref="Blowup.TeleportOut"/>.</summary>
    public void TeleportOut()
    {
        if (_model is not Node3D model || GetParent() is not { } parent)
            return;
        var shown = Visible;
        Visible = true;
        Blowup.TeleportOut(this, model, parent);
        Visible = shown;
    }

    // ---- scanning -------------------------------------------------------------------------------

    private MeshInstance3D? _scan;
    private float _scanTime, _scanSize;

    /// <summary>
    /// A geo locator searching (MachPhysGeoLocator::isLocating): the radial disc over its hips, as wide as it can scan,
    /// swelling from half to full size twice a second and turning, with its sound. Null stops it.
    /// </summary>
    public void Scan(Mesh? disc, float range)
    {
        if (disc is null)
        {
            _scan?.QueueFree();
            if (_scan is not null)
                Hum = null;
            _scan = null;
            if (_rigView is not null)
                _rigView.Locating = false;
            return;
        }
        if (_scan is not null || _rigView is null)
            return;
        var hip = (_rigView.PartTransform("hip") ?? _rigView.PartTransform("hips"))?.Origin;
        _scanTime = 0;
        _scanSize = range;
        _scan = new MeshInstance3D
        {
            Mesh = disc,
            Name = "Scan",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = (hip is { } at ? ToLocal(at) : _bounds.GetCenter()) + Vector3.Up * _bounds.Size.Y * 0.2f,
        };
        AddChild(_scan);
        _rigView.Locating = true;
        Hum = "SID_GEOLOCATE";
    }

    private void UpdateScan(double delta)
    {
        if (_scan is null)
            return;
        _scanTime += (float)delta;
        // Half to full size each half second; half a turn and back each second.
        var size = _scanSize * (0.5f + 0.5f * (_scanTime % 0.5f / 0.5f));
        var turn = _scanTime % 1;
        _scan.Scale = Vector3.One * size;
        _scan.Rotation = Vector3.Up * Mathf.Pi * (turn < 0.5f ? turn * 2 : 2 - turn * 2);
    }
}
