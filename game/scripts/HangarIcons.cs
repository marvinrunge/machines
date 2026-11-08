using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The pictures on the hangar's cards. Weapons, chassis and escorts are photographed from their own models, one after the
/// other while the screen is up, what is on show first and the rest of what the page may come to as soon as the page opens,
/// and kept while the game runs; until its picture is ready, and for all that has no model (modules, abilities, chips), a
/// card shows a line drawing (<see cref="HangarGlyphs"/>). Wired is the remake's own addition.
/// </summary>
public partial class HangarIcons : Node
{
    private const int Pixels = 144;
    private const float Fov = 16;

    /// <summary>What sets a machine's picture apart from a chassis of the same name.</summary>
    private const string MachineKey = "machine:";

    private static readonly Dictionary<string, Texture2D> Pictures = [];
    private static ClassicAssets? _kept;
    private static HangarIcons? _current;

    private readonly Queue<string> _waiting = new(), _later = new();
    private readonly HashSet<string> _asked = [], _shown = [];
    private readonly ClassicAssets _assets;
    private readonly Func<Race> _race;
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private RigView? _view;
    private string? _key;
    private int _frames;

    public HangarIcons(ClassicAssets assets, Func<Race> race)
    {
        (_assets, _race) = (assets, race);
        if (_kept != assets)
            Pictures.Clear();
        _kept = assets;
    }

    public override void _EnterTree() => _current = this;

    public override void _ExitTree()
    {
        if (_current == this)
            _current = null;
    }

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

    /// <summary>A card's picture: its photograph, or its line drawing for now.</summary>
    public static void Draw(CanvasItem item, WiredItem what, Rect2 box, Color tint)
    {
        item.DrawRect(box, new Color(MenuTheme.Well, tint.A));
        if (_current?.Picture(what) is { } picture)
            item.DrawTextureRect(picture, box, false, tint);
        else
            DrawGlyph(item, what, box, tint);
        item.DrawRect(box, new Color(MenuTheme.Edge, tint.A * 0.8f), false, 1);
    }

    /// <summary>Whether what is drawn for <paramref name="what"/> is still its line drawing and a photograph is on its way.</summary>
    public static bool Awaited(WiredItem what) => what.Kind is WiredKind.Weapon or WiredKind.Chassis && _current?.Picture(what) is null;

    /// <summary>A machine's picture, for the squad's cards.</summary>
    public static void Draw(CanvasItem item, ResearchKey machine, Rect2 box, Color tint)
    {
        item.DrawRect(box, new Color(MenuTheme.Well, tint.A));
        if (_current?.Picture($"{MachineKey}{_current._race()}/{Model(machine)}") is { } picture)
            item.DrawTextureRect(picture, box, false, tint);
        else
            DrawLines(item, HangarGlyphs.All.GetValueOrDefault(machine.SubType ?? ""), MenuTheme.Glow, box, tint);
        item.DrawRect(box, new Color(MenuTheme.Edge, tint.A * 0.8f), false, 1);
    }

    /// <summary>The pictures the page will come to, behind what is on show: all this is the time that takes the shelves longest.</summary>
    public static void Ahead(IEnumerable<WiredItem> items, IEnumerable<ResearchKey> machines)
    {
        if (_current is not { } icons)
            return;
        foreach (var item in items.Where(i => i.Kind is WiredKind.Weapon or WiredKind.Chassis))
            icons.Later(icons.Key(item));
        foreach (var machine in machines)
            icons.Later($"{MachineKey}{icons._race()}/{Model(machine)}");
    }

    private void Later(string key)
    {
        if (!Pictures.ContainsKey(key) && _asked.Add(key))
            _later.Enqueue(key);
    }

    /// <summary>A machine's model by the name <see cref="ActorView.ModelOf"/> gives it.</summary>
    private static string Model(ResearchKey key) => string.Join('_', new[] { key.Type, key.HardwareLevel.ToString(), key.SubType, key.WeaponCombo }.OfType<string>());

    private static void DrawGlyph(CanvasItem item, WiredItem what, Rect2 box, Color tint)
    {
        DrawLines(item, HangarGlyphs.Of(what.Kind, what.Catalogue), HangarText.Family(what) ?? MenuTheme.Glow, box, tint);
    }

    private static void DrawLines(CanvasItem item, float[][]? lines, Color family, Rect2 box, Color tint)
    {
        var colour = new Color(family, tint.A);
        if (lines is null)
        {
            var inner = box.Grow(-box.Size.X * 0.3f);
            item.DrawRect(inner, new Color(colour, tint.A * 0.4f), false, 2);
            return;
        }
        var scale = box.Size / HangarGlyphs.Grid * 0.74f;
        var origin = box.Position + box.Size * 0.13f;
        foreach (var line in lines)
        {
            var points = new Vector2[line.Length / 2];
            for (var i = 0; i < points.Length; i++)
                points[i] = origin + new Vector2(line[i * 2], line[i * 2 + 1]) * scale;
            item.DrawPolyline(points, new Color(colour, tint.A * 0.25f), 5, true);
            item.DrawPolyline(points, colour, 1.8f, true);
        }
    }

    private string Key(WiredItem what) => what.Kind == WiredKind.Weapon ? what.Catalogue : $"{_race()}/{what.Catalogue}";

    private Texture2D? Picture(WiredItem what) => what.Kind is WiredKind.Weapon or WiredKind.Chassis ? Picture(Key(what)) : null;

    private Texture2D? Picture(string key)
    {
        if (Pictures.TryGetValue(key, out var picture))
            return picture;
        // What is on show goes first, even if it was asked for ahead of time.
        if (_shown.Add(key))
        {
            _asked.Add(key);
            _waiting.Enqueue(key);
        }
        return null;
    }

    public override void _Process(double delta)
    {
        if (_key is null)
        {
            // One that came up twice has its picture by now.
            while (_waiting.TryDequeue(out var next) || _later.TryDequeue(out next))
            {
                if (Pictures.ContainsKey(next))
                    continue;
                Stage(next);
                break;
            }
            return;
        }
        // One frame for the transforms, one to draw.
        if (++_frames < 3)
            return;
        var image = _viewport.GetTexture().GetImage();
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        image.Convert(Image.Format.Rgba8);
        Pictures[_key] = ImageTexture.CreateFromImage(image);
        _view?.QueueFree();
        _view = null;
        _key = null;
    }

    private void Stage(string key)
    {
        var id = key[(key.IndexOf('/') + 1)..];
        var race = _race();
        var weapon = WiredCatalogue.FindWeapon(id) is not null;
        var donor = weapon ? HeroDonors.For(id, Mounting.Left) ?? HeroDonors.For(id, Mounting.Right) ?? HeroDonors.For(id, Mounting.Top) : null;
        var rig = key.StartsWith(MachineKey) ? _assets.RigFor(id, race)
            : weapon ? donor is null ? null : _assets.RigFor(donor.Model, race) : HeroRig.Compose(_assets, race, id, []);
        if (rig is null)
            return;
        var view = RigView.Create(rig);
        _viewport.AddChild(view);
        view.Animate(0.016f, 0, 0, 0, 0);
        var parts = rig.Nodes.Select(n => new RigPart(n.Parent, n.Name)).ToArray();
        var mount = donor is null ? -1 : RigGraft.MountOf(parts, donor.Mount);
        var (shown, first, box) = (false, true, new Aabb());
        for (var i = 1; i < rig.Nodes.Length; i++)
        {
            if (view.FindChild($"Part{i}", true, false) is not Node3D part || part.GetNodeOrNull<MeshInstance3D>("Mesh") is not { } mesh)
                continue;
            var under = mount < 0;
            for (var at = i; at >= 0 && !under; at = rig.Nodes[at].Parent)
                under = at == mount;
            mesh.Visible = under;
            if (!under || mesh.Mesh is not ArrayMesh)
                continue;
            var aabb = mesh.GlobalTransform * mesh.GetAabb();
            box = first ? aabb : box.Merge(aabb);
            first = false;
            shown = true;
        }
        if (!shown)
        {
            view.QueueFree();
            return;
        }
        var towards = new Vector3(0.78f, 0.42f, 0.46f).Normalized();
        var radius = Math.Max(box.Size.Length() / 2, 0.2f);
        _camera.Position = box.GetCenter() + towards * (radius / Mathf.Sin(Mathf.DegToRad(Fov / 2)) * 0.98f);
        _camera.LookAt(box.GetCenter(), Vector3.Up);
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        (_view, _key, _frames) = (view, key, 0);
    }
}
