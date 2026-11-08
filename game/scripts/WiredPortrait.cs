using Godot;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The picture of a run's hero for the radar and the control panel. The original's bitmaps go by machine and weapon, and a
/// hero's combination of the two is made up: there is none, so it is photographed as it stands, anew whenever it wears
/// something else. Wired is the remake's own addition.
/// </summary>
public partial class WiredPortrait(ClassicAssets assets) : Node
{
    private const int Pixels = 128;
    private const float Fov = 18;

    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private RigView? _view;
    private string? _worn;
    private int _frames;

    /// <summary>The hero as it was last photographed; null until there is a picture.</summary>
    public Texture2D? Picture { get; private set; }

    public override void _Ready()
    {
        _viewport = new SubViewport
        {
            OwnWorld3D = true,
            TransparentBg = true,
            Msaa3D = Viewport.Msaa.Msaa4X,
            Size = new Vector2I(Pixels, Pixels),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_viewport);
        _camera = new Camera3D { Fov = Fov, Near = 0.05f, Far = 800, Current = true };
        _viewport.AddChild(_camera);
        _viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.78f, 0.72f),
                AmbientLightEnergy = 0.8f,
            },
        });
        foreach (var (azimuth, energy) in new[] { (55f, 1.5f), (-70f, 0.6f) })
        {
            var from = new Vector3(Mathf.Cos(Mathf.DegToRad(azimuth)), 0.8f, Mathf.Sin(Mathf.DegToRad(azimuth))).Normalized();
            _viewport.AddChild(new DirectionalLight3D { Transform = new Transform3D(Basis.LookingAt(-from, Vector3.Up), from * 50), LightEnergy = energy });
        }
    }

    /// <summary>Has the hero photographed if it wears something else than in the picture.</summary>
    public void Follow(Race race, HeroLoadout loadout)
    {
        var worn = $"{race}/{loadout.Chassis.Catalogue}/{string.Join('/', loadout.Weapons.Select(w => w?.Catalogue))}";
        if (_view is not null)
        {
            // One frame for the transforms, one to draw.
            if (++_frames < 3)
                return;
            var image = _viewport.GetTexture().GetImage();
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            image.Convert(Image.Format.Rgba8);
            Picture = ImageTexture.CreateFromImage(image);
            _view.QueueFree();
            _view = null;
            return;
        }
        if (worn != _worn)
        {
            _worn = worn;
            Stage(race, loadout);
        }
    }

    private void Stage(Race race, HeroLoadout loadout)
    {
        if (HeroRig.Compose(assets, race, loadout) is not { } rig)
            return;
        var view = RigView.Create(rig);
        _viewport.AddChild(view);
        view.Animate(0.016f, 0, 0, 0, 0);
        var points = HeroStudio.Points(view, rig);
        if (points.Count == 0)
        {
            view.QueueFree();
            return;
        }
        var box = new Aabb(points[0], Vector3.Zero);
        foreach (var point in points)
            box = box.Expand(point);
        var towards = new Vector3(0.78f, 0.34f, 0.5f).Normalized();
        var radius = Math.Max(box.Size.Length() / 2, 0.2f);
        _camera.Position = box.GetCenter() + towards * (radius / Mathf.Sin(Mathf.DegToRad(Fov / 2)) * 0.9f);
        _camera.LookAt(box.GetCenter(), Vector3.Up);
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        (_view, _frames) = (view, 0);
    }
}
