using System.Text.Json;
using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// Reference pictures of the machines, for remaking them piece by piece: run res://scenes/Studio.tscn with
/// <c>MACHINES_STUDIO=&lt;out folder&gt;[,GRUNT,DOZER,...]</c> (the machines' types or subtypes as classic/models.txt
/// names them; none for all machines; BUILDINGS for all buildings, or their kinds: FACTORY_MILITARY, TURRET, MINE...).
/// Every machine is built as the game builds it (<see cref="ClassicAssets.RigFor"/>,
/// <see cref="RigView"/>), in red, in its rest pose, on white, seen from the front left and a little above.
/// Per hull (a type at one level), in &lt;out&gt;/&lt;type&gt;/:
/// <list type="bullet">
/// <item><c>-context.png</c>: the whole machine with its first loadout, as in the game;</item>
/// <item><c>-hull.png</c>: without its weapons and its faceplate (and <c>-hull-alpha.png</c>, see-through);</item>
/// <item><c>-parts.png</c> and <c>-parts.json</c>: the hull's parts in flat colours, and each part's pivot, size
/// and what moves it, so remade parts can take the old animations;</item>
/// <item><c>-races.png</c>, with <c>MACHINES_STUDIO_RACES=1</c>: the whole machine in each of the four races'
/// colours and markings.</item>
/// </list>
/// Per type: <c>-weapons.png</c>, every weapon its exports carry, at one scale so sizes compare, and
/// <c>-overview.png</c>, its levels side by side. For all: <c>&lt;out&gt;/faceplates.png</c>, every distinct eye box
/// once with its eyes dark, at one scale. The three pictures of a hull share one camera and line up pixel for pixel.
/// A building is photographed like a hull, its weapons kept on (an emplacement carries one set for good).
/// Smaller JPEGs of the pictures go to &lt;out&gt;/web, for the gallery page.
/// </summary>
public partial class Studio : Node
{
    private const int HullSize = 2048;
    private const int CellSize = 1024;
    private const int LabelHeight = 72;
    private const int OverviewSize = 768;
    private const int FaceCell = 512;
    private const int WebSize = 1400;

    // The camera: from the front, turned towards the machine's left, a little above; a narrow lens, so the
    // pictures come close to a drawing to scale.
    private static readonly float Azimuth = Mathf.DegToRad(35);
    private static readonly float Elevation = Mathf.DegToRad(25);
    private const float Fov = 20;

    /// <summary>The names the game shows for the types and subtypes of classic/models.txt.</summary>
    internal static readonly Dictionary<string, string> Names = new()
    {
        ["GRUNT"] = "Grunt", ["ASSASSIN"] = "Reaper", ["KNIGHT"] = "Knight", ["BALLISTA"] = "Ballista", ["NINJA"] = "Gorilla",
        ["BOSS"] = "Boss", ["OVERSEER"] = "Overseer", ["COMMANDER"] = "Commander", ["DOZER"] = "Dozer", ["BUILDER"] = "Builder",
        ["BEHEMOTH"] = "Behemoth", ["LAB_TECH"] = "Lab Tech", ["TECH_BOY"] = "Engineer", ["BRAIN_BOX"] = "Brainbox",
        ["RESOURCE_CARRIER"] = "Resource Carrier", ["GEO_LOCATOR"] = "Geo Locator", ["SPY_LOCATOR"] = "Spy Locator", ["APC"] = "APC",
        ["FACTORY_MILITARY"] = "Military Factory", ["FACTORY_CIVILIAN"] = "Civilian Factory", ["HARDWARE_LAB_MILITARY"] = "Military Lab",
        ["HARDWARE_LAB_CIVILIAN"] = "Civilian Lab", ["SMELTER"] = "Smelter", ["MINE"] = "Mine", ["GARRISON"] = "Garrison",
        ["BEACON"] = "Beacon", ["POD"] = "Pod", ["TURRET"] = "Turret", ["SENTRY"] = "Sentry", ["LAUNCHER"] = "Launcher", ["ICBM"] = "ICBM",
    };

    /// <summary>The weapon mounts, by their part's name: left, right and top.</summary>
    private static readonly Dictionary<string, char> Mounts = new() { ["l_gun"] = 'L', ["r_gun"] = 'R', ["gun"] = 'T', ["t_gun"] = 'T' };

    /// <summary>Flat colours for the part maps, far apart from each other.</summary>
    private static readonly Color[] Palette =
    [
        new("#e6194b"), new("#3cb44b"), new("#4363d8"), new("#f58231"), new("#911eb4"), new("#42d4f4"), new("#f032e6"),
        new("#bfef45"), new("#fabed4"), new("#469990"), new("#dcbeff"), new("#9a6324"), new("#fffac8"), new("#800000"),
        new("#aaffc3"), new("#808000"), new("#ffd8b1"), new("#000075"), new("#a9a9a9"), new("#ffe119"), new("#000000"),
    ];

    private sealed record Export(string Id, string Kind, int Level, string? Loadout, bool Building = false);

    /// <summary>A machine on the stage: its rig, its parts' meshes, and which parts are weapons or the faceplate.</summary>
    private sealed class Unit
    {
        public required Export Export { get; init; }
        public required Rig Rig { get; init; }
        public required RigView View { get; init; }
        public required MeshInstance3D?[] Meshes { get; init; }
        /// <summary>The mount (L, R or T) whose weapon a part belongs to, for every part of a weapon.</summary>
        public required Dictionary<int, char> Weapon { get; init; }
        public required HashSet<int> Face { get; init; }
        public bool Hull(int node) => !Weapon.ContainsKey(node) && !Face.Contains(node);
    }

    private ClassicAssets _assets = null!;
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private WorldEnvironment _world = null!;
    private Godot.Environment _look = null!;
    private Godot.Environment _flat = null!;
    private Node3D _stage = null!;
    private string _output = "";
    private readonly List<(string Key, string Label, Image Picture, float Distance)> _faces = [];
    private readonly List<string> _report = [];

    public override async void _Ready()
    {
        var spec = (System.Environment.GetEnvironmentVariable("MACHINES_STUDIO") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (spec.Length == 0)
        {
            GD.PrintErr("Studio: set MACHINES_STUDIO=<out folder>[,TYPE ...]");
            GetTree().Quit(1);
            return;
        }
        var output = _output = Path.GetFullPath(spec[0]);
        var wanted = spec.Skip(1).Select(s => s.ToUpperInvariant()).ToHashSet();
        _assets = new ClassicAssets(Paths.Root);
        BuildStage();

        var exports = ReadExports().Where(e => wanted.Count == 0 ? !e.Building : wanted.Contains(e.Kind) || (e.Building && wanted.Contains("BUILDINGS"))).ToList();
        foreach (var kind in exports.Select(e => e.Kind).Distinct())
            await Photograph(kind, exports.Where(e => e.Kind == kind).ToList(), output);
        await FaceSheet(Path.Combine(output, "faceplates.png"));
        _report.Add($"{_faces.Count} distinct faceplates");

        foreach (var line in _report)
            GD.Print(line);
        GetTree().Quit();
    }

    /// <summary>
    /// The machines and buildings exported (classic/models.txt): MACHINE type level [subtype] [loadout], CONSTRUCTION
    /// type level [subtype] [weapons]. Factories and labs are military or civilian, emplacements turrets, sentries,
    /// launchers or ICBMs.
    /// </summary>
    private static IEnumerable<Export> ReadExports()
    {
        var path = Path.Combine(Paths.Classic, "models.txt");
        foreach (var line in File.ReadLines(path).Where(l => l.StartsWith("CONSTRUCTION ")))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kind = words[1] switch
            {
                "FACTORY" or "HARDWARE_LAB" => $"{words[1]}_{words[3]}",
                "MISSILE_EMPLACEMENT" => words[3],
                _ => words[1],
            };
            yield return new Export(string.Join('_', words.Skip(1)), kind, int.Parse(words[2]), null, true);
        }
        foreach (var line in File.ReadLines(path).Where(l => l.StartsWith("MACHINE ")))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var withSub = words[1] is "AGGRESSOR" or "ADMINISTRATOR" or "CONSTRUCTOR" or "TECHNICIAN";
            var kind = withSub ? words[3] : words[1];
            var loadout = words.Length > (withSub ? 4 : 3) ? words[^1] : null;
            yield return new Export(string.Join('_', words.Skip(1)), kind, int.Parse(words[2]), loadout);
        }
    }

    /// <summary>
    /// The weapons a loadout names, by mount: "LR_HEAVY_BOLTER1_X2" carries HEAVY_BOLTER1 left and right,
    /// "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED" a pulse rifle left and the supercharge right.
    /// </summary>
    private static Dictionary<char, string> WeaponsOf(string? loadout)
    {
        var mounts = new Dictionary<char, string>();
        string? sides = null;
        var name = new List<string>();
        void Close()
        {
            if (sides is not null && name.Count > 0)
                foreach (var side in sides)
                    mounts[side] = string.Join('_', name);
            name.Clear();
        }
        foreach (var word in (loadout ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is "L" or "R" or "T" or "LR" or "LRT")
            {
                Close();
                sides = word;
            }
            else if (word is not ("X2" or "X3"))
                name.Add(word);
        }
        Close();
        return mounts;
    }

    private void BuildStage()
    {
        _viewport = new SubViewport
        {
            Size = new Vector2I(HullSize, HullSize),
            TransparentBg = true,
            OwnWorld3D = true,
            Msaa3D = Viewport.Msaa.Msaa8X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_viewport);
        _stage = new Node3D { Name = "Stage" };
        _viewport.AddChild(_stage);
        _camera = new Camera3D { Fov = Fov, Near = 0.05f, Far = 1000, Current = true };
        _viewport.AddChild(_camera);

        // A bright, neutral studio: a pale sky lights and mirrors from all round, behind a see-through background.
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.78f, 0.82f, 0.88f),
                SkyHorizonColor = new Color(0.93f, 0.93f, 0.93f),
                GroundHorizonColor = new Color(0.85f, 0.85f, 0.85f),
                GroundBottomColor = new Color(0.45f, 0.45f, 0.45f),
            },
        };
        // The game's remake look (PlanetLook.Enhance): AgX, a touch more saturation and contrast, glow on the
        // brightest spots, ambient occlusion where parts meet, at a machine's scale.
        _look = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.8f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = 1.0f,
            AdjustmentEnabled = true,
            AdjustmentSaturation = 1.1f,
            AdjustmentContrast = 1.12f,
            GlowEnabled = true,
            GlowIntensity = 0.6f,
            GlowBloom = 0.04f,
            GlowHdrThreshold = 1.1f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Softlight,
            SsaoEnabled = true,
            SsaoRadius = 1.2f,
            SsaoIntensity = 1.6f,
            SsaoPower = 1.4f,
            SsaoDetail = 0.8f,
            SsaoLightAffect = 0.2f,
        };
        // The part maps: flat colours exactly as given, nothing on top.
        _flat = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.ClearColor,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _world = new WorldEnvironment { Environment = _look };
        _viewport.AddChild(_world);

        // A key light from the front left and high up, casting the shadows; a weaker fill from the right.
        _viewport.AddChild(Light(Mathf.DegToRad(60), Mathf.DegToRad(50), 1.6f, true));
        _viewport.AddChild(Light(Mathf.DegToRad(-70), Mathf.DegToRad(20), 0.45f, false));
    }

    private static DirectionalLight3D Light(float azimuth, float elevation, float energy, bool shadow)
    {
        var from = Towards(azimuth, elevation);
        return new DirectionalLight3D
        {
            Transform = new Transform3D(Basis.LookingAt(-from, Vector3.Up), from * 50),
            LightEnergy = energy,
            ShadowEnabled = shadow,
            ShadowBlur = 1.5f,
            LightAngularDistance = 1.5f,
            DirectionalShadowMaxDistance = 80,
        };
    }

    /// <summary>A direction from the machine: azimuth from its front (+x) towards its left (+z), elevation above.</summary>
    private static Vector3 Towards(float azimuth, float elevation) =>
        new(Mathf.Cos(elevation) * Mathf.Cos(azimuth), Mathf.Sin(elevation), Mathf.Cos(elevation) * Mathf.Sin(azimuth));

    private Unit? Build(Export export, Race race = Race.Red)
    {
        if (_assets.RigFor(export.Id, race) is not { } rig)
        {
            _report.Add($"{export.Id}: no rig");
            return null;
        }
        var view = RigView.Create(rig);
        view.Visible = false;
        _stage.AddChild(view);
        var meshes = new MeshInstance3D?[rig.Nodes.Length];
        for (var i = 0; i < meshes.Length; i++)
            meshes[i] = (view.FindChild($"Part{i}", true, false) as Node3D)?.GetNodeOrNull<MeshInstance3D>("Mesh");

        // A weapon is everything on its mount: the mount's own part and the parts below it (recoil, barrels, the
        // weapon's model). The highest mount counts, so the Reaper's cannon ("gun") under its left mount is left.
        var weapon = new Dictionary<int, char>();
        var face = new HashSet<int>();
        for (var i = 0; i < meshes.Length; i++)
        {
            char? side = null;
            for (var n = i; n >= 0 && n < rig.Nodes.Length; n = rig.Nodes[n].Parent == n ? -1 : rig.Nodes[n].Parent)
            {
                if (!export.Building && Mounts.TryGetValue(rig.Nodes[n].Name, out var mount))
                    side = mount;
                if (n == rig.Faceplate)
                    face.Add(i);
            }
            if (side is { } s)
                weapon[i] = s;
        }
        foreach (var mesh in meshes.OfType<MeshInstance3D>())
            SeeThroughGlow(mesh);
        return new Unit { Export = export, Rig = rig, View = view, Meshes = meshes, Weapon = weapon, Face = face };
    }

    private readonly Dictionary<Shader, Shader?> _seeThrough = [];

    /// <summary>
    /// Glowing sprites add their light, which on the see-through stage writes their black as solid black: drawn
    /// mixed instead, as clear as they are dark, they lie on the white as a glow.
    /// </summary>
    private void SeeThroughGlow(MeshInstance3D mesh)
    {
        const string added = "ALBEDO = c.rgb * c.a * colour * 2.0;";
        for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
        {
            if (mesh.Mesh.SurfaceGetMaterial(s) is not ShaderMaterial { Shader: { } shader } glow || !shader.Code.Contains("blend_add"))
                continue;
            if (!_seeThrough.TryGetValue(shader, out var mixed))
                _seeThrough[shader] = mixed = shader.Code.Contains(added)
                    ? new Shader
                    {
                        Code = shader.Code.Replace("blend_add", "blend_mix").Replace(added,
                            added + " ALPHA = clamp(max(ALBEDO.r, max(ALBEDO.g, ALBEDO.b)), 0.0, 1.0); ALBEDO = clamp(ALBEDO / max(ALPHA, 0.001), 0.0, 1.0);"),
                    }
                    : null;
            var material = new ShaderMaterial { Shader = mixed };
            if (mixed is not null)
                foreach (var name in new[] { "sprite", "colour" })
                    material.SetShaderParameter(name, glow.GetShaderParameter(name));
            mesh.SetSurfaceOverrideMaterial(s, mixed is null
                ? new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0, 0, 0, 0) }
                : material);
        }
    }

    private async Task Photograph(string kind, List<Export> exports, string output)
    {
        var name = Names.GetValueOrDefault(kind, kind);
        var folder = Path.Combine(output, Slug(name));
        Directory.CreateDirectory(folder);
        var units = exports.Select(e => Build(e)).OfType<Unit>().ToList();

        var overview = new List<(string Label, Image Picture)>();
        foreach (var level in units.GroupBy(u => u.Export.Level))
        {
            var unit = level.First();
            var stem = Path.Combine(folder, $"{Slug(name)}-{level.Key}");
            var whole = await PhotographHull(unit, stem);
            if (System.Environment.GetEnvironmentVariable("MACHINES_STUDIO_RACES") == "1")
                await PhotographRaces(unit, whole, stem, $"{name} {level.Key}");
            whole.Resize(OverviewSize, OverviewSize, Image.Interpolation.Lanczos);
            overview.Add(($"{name} {level.Key}", whole));
            await PhotographFace(unit, $"{name} {level.Key}");
            Compare(level.ToList());
            var parts = unit.Meshes.Select((m, i) => (m, i)).Where(p => p.m is not null).ToList();
            _report.Add($"{name} {level.Key}: {parts.Count(p => unit.Hull(p.i))} hull parts, "
                + $"{parts.Count(p => unit.Weapon.ContainsKey(p.i))} weapon parts, {parts.Count(p => unit.Face.Contains(p.i))} faceplate parts"
                + (unit.Face.Count == 0 ? " (no faceplate)" : ""));
        }

        // Every weapon once: from the left mount where it sits on several, which the camera sees from outside.
        var weapons = new List<(string Label, Unit Unit, char Side)>();
        var levels = new Dictionary<string, SortedSet<int>>();
        foreach (var unit in units)
        {
            foreach (var (side, weapon) in WeaponsOf(unit.Export.Loadout).OrderBy(w => "LTR".IndexOf(w.Key)))
            {
                if (levels.TryGetValue(weapon, out var seen))
                {
                    seen.Add(unit.Export.Level);
                    continue;
                }
                if (Points(unit, i => unit.Weapon.GetValueOrDefault(i) == side) is not { Count: > 3 } points
                    || points.Max(p => p.DistanceTo(points[0])) < 0.05f)
                {
                    _report.Add($"{name}: {Title(weapon)} on {unit.Export.Id} has no parts of its own");
                    continue;
                }
                levels[weapon] = [unit.Export.Level];
                weapons.Add((weapon, unit, side));
            }
        }
        weapons = weapons.Select(w => w with { Label = $"{Title(w.Label)} · {name} {string.Join(", ", levels[w.Label])}" }).ToList();
        if (weapons.Count > 0)
            Save(await Compose(await WeaponPictures(weapons), CellSize, LabelHeight, 38), Path.Combine(folder, $"{Slug(name)}-weapons.png"));
        Save(await Compose(overview, OverviewSize, 56, 30), Path.Combine(folder, $"{Slug(name)}-overview.png"));

        foreach (var unit in units)
            unit.View.QueueFree();
    }

    /// <summary>
    /// The whole machine as each race wears it, from the red one's camera: the races differ in more than their
    /// colour (logos.dat swaps whole textures).
    /// </summary>
    private async Task PhotographRaces(Unit red, Image redPicture, string stem, string label)
    {
        var frame = Frame(Points(red, _ => true));
        var first = (Image)redPicture.Duplicate();
        first.Resize(CellSize, CellSize, Image.Interpolation.Lanczos);
        var cells = new List<(string, Image)> { ($"{label} · {Race.Red}", first) };
        foreach (var race in new[] { Race.Blue, Race.Green, Race.Yellow })
        {
            if (Build(red.Export, race) is not { } unit)
                continue;
            unit.View.Visible = true;
            Show(unit, _ => true);
            Aim(frame.Centre, frame.Distance);
            cells.Add(($"{label} · {race}", (await Shoot(CellSize)).OnWhite()));
            unit.View.QueueFree();
        }
        Save(await Compose(cells, CellSize, LabelHeight, 38), stem + "-races.png");
    }

    /// <summary>Every weapon on its own, all at the scale the largest needs, so their sizes compare.</summary>
    private async Task<List<(string Label, Image Picture)>> WeaponPictures(List<(string Label, Unit Unit, char Side)> weapons)
    {
        var frames = weapons.Select(w => Frame(Points(w.Unit, i => w.Unit.Weapon.GetValueOrDefault(i) == w.Side))).ToList();
        var distance = frames.Max(f => f.Distance);
        var pictures = new List<(string, Image)>();
        foreach (var ((label, unit, side), frame) in weapons.Zip(frames))
        {
            unit.View.Visible = true;
            Show(unit, i => unit.Weapon.GetValueOrDefault(i) == side);
            Aim(frame.Centre, distance);
            pictures.Add((label, (await Shoot(CellSize)).OnWhite()));
            unit.View.Visible = false;
        }
        return pictures;
    }

    /// <summary>
    /// The machine's eye box alone, its eyes dark, for the one sheet of all of them (see <see cref="FaceSheet"/>);
    /// a box like one already taken only adds the machine's name to it.
    /// </summary>
    private async Task PhotographFace(Unit unit, string label)
    {
        if (unit.Face.Count == 0)
            return;
        // Turned so the box stands as it does on most machines (upright, its window to the front): bosses wear theirs
        // tilted.
        if (unit.View.FindChild($"Part{unit.Rig.Faceplate}", true, false) is Node3D box)
            unit.View.Basis = box.GlobalBasis.Orthonormalized().Inverse();
        var frame = Frame(Points(unit, unit.Face.Contains));
        var vertices = unit.Face.Select(i => unit.Meshes[i]?.Mesh).OfType<ArrayMesh>()
            .Sum(m => Enumerable.Range(0, m.GetSurfaceCount()).Sum(m.SurfaceGetArrayLen));
        var key = $"{vertices}|{frame.Distance:F2}";
        var index = _faces.FindIndex(f => f.Key == key);
        if (index >= 0)
        {
            _faces[index] = _faces[index] with { Label = _faces[index].Label + ", " + label };
        }
        else
        {
            unit.View.Visible = true;
            Show(unit, unit.Face.Contains);
            DarkenEyes(unit);
            Aim(frame.Centre, frame.Distance);
            _faces.Add((key, label, (await Shoot(CellSize)).OnWhite(), frame.Distance));
            unit.View.Visible = false;
        }
        unit.View.Basis = Basis.Identity;
    }

    /// <summary>
    /// Every distinct eye box on one sheet, each as large as the others relative to it: one generation makes them
    /// all, and none comes out finer than the hulls and weapons it sits on.
    /// </summary>
    private async Task FaceSheet(string path)
    {
        if (_faces.Count == 0)
            return;
        var largest = _faces.Max(f => f.Distance);
        // The cells carry letters; which machines wear each box goes to faceplates.json beside the sheet.
        var letters = _faces.Select((_, k) => ((char)('A' + k)).ToString()).ToList();
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(
            _faces.Zip(letters).Select(p => new { letter = p.Second, machines = p.First.Label.Split(", ") }),
            new JsonSerializerOptions { WriteIndented = true }));
        var cells = _faces.Zip(letters).Select(p =>
        {
            var (f, letter) = p;
            // Taken filling its picture; shrunk by its size against the largest's, on a white cell.
            var side = Math.Max(8, (int)(FaceCell * f.Distance / largest));
            var picture = (Image)f.Picture.Duplicate();
            picture.Resize(side, side, Image.Interpolation.Lanczos);
            var cell = Image.CreateEmpty(FaceCell, FaceCell, false, Image.Format.Rgba8);
            cell.Fill(Colors.White);
            cell.BlitRect(picture, new Rect2I(0, 0, side, side), new Vector2I((FaceCell - side) / 2, (FaceCell - side) / 2));
            return (letter, cell);
        }).ToList();
        Save(await Compose(cells, FaceCell, 56, 30), path);
    }

    /// <summary>The whole machine, its hull alone, and the hull's part map, from one camera; the whole one returned.</summary>
    private async Task<Image> PhotographHull(Unit unit, string stem)
    {
        unit.View.Visible = true;
        Show(unit, _ => true);
        var (centre, distance) = Frame(Points(unit, _ => true));
        Aim(centre, distance);
        var whole = (await Shoot(HullSize)).OnWhite();
        Save(whole, stem + "-context.png");

        Show(unit, unit.Hull);
        var hull = await Shoot(HullSize);
        hull.SavePng(stem + "-hull-alpha.png");
        Save(hull.OnWhite(), stem + "-hull.png");

        // The part map: each hull part in its own flat colour, unlit.
        var colours = new Dictionary<int, Color>();
        foreach (var (mesh, i) in unit.Meshes.Select((m, i) => (m, i)))
        {
            if (mesh is null || !mesh.Visible)
                continue;
            var colour = Palette[colours.Count % Palette.Length];
            colours[i] = colour;
            mesh.MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = colour, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        }
        _world.Environment = _flat;
        Save((await Shoot(HullSize)).OnWhite(), stem + "-parts.png");
        _world.Environment = _look;
        foreach (var mesh in unit.Meshes.OfType<MeshInstance3D>())
            mesh.MaterialOverride = null;
        File.WriteAllText(stem + "-parts.json", PartsJson(unit, colours, Path.GetFileName(stem)));
        unit.View.Visible = false;
        return whole;
    }

    /// <summary>Pictures side by side in a square-ish grid on white, each labelled underneath.</summary>
    private async Task<Image> Compose(List<(string Label, Image Picture)> cells, int cell, int labelHeight, int fontSize)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(cells.Count));
        var rows = (cells.Count + columns - 1) / columns;
        var sheet = new SubViewport
        {
            Size = new Vector2I(columns * cell, rows * (cell + labelHeight)),
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(sheet);
        sheet.AddChild(new ColorRect { Color = Colors.White, Size = sheet.Size });
        for (var k = 0; k < cells.Count; k++)
        {
            var at = new Vector2(k % columns * cell, k / columns * (cell + labelHeight));
            sheet.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(cells[k].Picture), Position = at, Size = new Vector2(cell, cell) });
            var label = new Label
            {
                Text = cells[k].Label,
                Position = at + new Vector2(8, cell),
                Size = new Vector2(cell - 16, labelHeight),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            label.AddThemeFontSizeOverride("font_size", fontSize);
            label.AddThemeColorOverride("font_color", new Color(0.2f, 0.2f, 0.2f));
            sheet.AddChild(label);
        }
        sheet.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        for (var f = 0; f < 3; f++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = sheet.GetTexture().GetImage();
        sheet.QueueFree();
        return image;
    }

    /// <summary>
    /// Saves a picture, and a smaller JPEG of it under &lt;out&gt;/web for the gallery page (at most WebSize wide).
    /// </summary>
    private void Save(Image image, string path)
    {
        image.SavePng(path);
        var web = Path.Combine(_output, "web", Path.GetRelativePath(_output, Path.ChangeExtension(path, ".jpg")));
        Directory.CreateDirectory(Path.GetDirectoryName(web)!);
        var small = (Image)image.Duplicate();
        if (small.GetWidth() > WebSize)
            small.Resize(WebSize, small.GetHeight() * WebSize / small.GetWidth(), Image.Interpolation.Lanczos);
        small.SaveJpg(web, 0.88f);
    }

    /// <summary>The faceplate's eyes switched off: glowing sprites hidden, glowing plates black.</summary>
    private static void DarkenEyes(Unit unit)
    {
        var hidden = new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0, 0, 0, 0) };
        var black = new StandardMaterial3D { AlbedoColor = new Color(0.02f, 0.02f, 0.02f), Roughness = 0.45f };
        foreach (var mesh in unit.Face.Select(i => unit.Meshes[i]).OfType<MeshInstance3D>())
        {
            for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            {
                var material = mesh.Mesh.SurfaceGetMaterial(s);
                if (material is ShaderMaterial { Shader: { } shader } && shader.Code.Contains("blend_add"))
                    mesh.SetSurfaceOverrideMaterial(s, hidden);
                else if (material is StandardMaterial3D { EmissionEnabled: true })
                    mesh.SetSurfaceOverrideMaterial(s, black);
            }
        }
    }

    private static void Show(Unit unit, Func<int, bool> shown)
    {
        for (var i = 0; i < unit.Meshes.Length; i++)
            if (unit.Meshes[i] is { } mesh)
                mesh.Visible = shown(i);
    }

    /// <summary>
    /// The corners of the parts shown, in the world. Their meshes' boxes would not do: the boxes of parts with
    /// sprites are made larger, so the sprites spread out by the shader are never culled.
    /// </summary>
    private static List<Vector3> Points(Unit unit, Func<int, bool> shown)
    {
        var view = Basis.LookingAt(-Towards(Azimuth, Elevation), Vector3.Up);
        var points = new List<Vector3>();
        for (var i = 0; i < unit.Meshes.Length; i++)
        {
            if (unit.Meshes[i] is not { Mesh: ArrayMesh mesh } instance || !shown(i))
                continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.SurfaceGetArrays(s);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                // Sprites and spinners keep their corners at their centre or on their axis, with the way out in UV2:
                // spread them as their shaders do for this camera (see ClassicAssets.AddSprites, AddSpinners).
                if (mesh.SurfaceGetMaterial(s) is not ShaderMaterial || arrays[(int)Mesh.ArrayType.TexUV2].VariantType != Variant.Type.PackedVector2Array)
                {
                    points.AddRange(vertices.Select(v => instance.GlobalTransform * v));
                    continue;
                }
                var spread = arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
                var axes = arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.PackedVector3Array
                    ? arrays[(int)Mesh.ArrayType.Normal].AsVector3Array() : [];
                for (var v = 0; v < vertices.Length; v++)
                {
                    var at = instance.GlobalTransform * vertices[v];
                    if (axes.Length == vertices.Length)
                    {
                        var axis = instance.GlobalBasis * axes[v];
                        var across = view.Z.Cross(axis).Normalized();
                        points.Add(at + (axis.Normalized() * spread[v].X + across * spread[v].Y) * axis.Length());
                    }
                    else
                        points.Add(at + view.X * spread[v].X + view.Y * spread[v].Y);
                }
            }
        }
        return points;
    }

    /// <summary>
    /// Where the camera looks and how far back it stands so the points fill the square picture with a small
    /// margin: the points measured across and up the view.
    /// </summary>
    private static (Vector3 Centre, float Distance) Frame(List<Vector3> corners)
    {
        var basis = Basis.LookingAt(-Towards(Azimuth, Elevation), Vector3.Up);
        float Across(Vector3 p) => p.Dot(basis.X);
        float Up(Vector3 p) => p.Dot(basis.Y);
        float Near(Vector3 p) => p.Dot(basis.Z);
        var (left, right) = (corners.Min(Across), corners.Max(Across));
        var (low, high) = (corners.Min(Up), corners.Max(Up));
        var middle = corners.Aggregate(Vector3.Zero, (s, p) => s + p) / corners.Count;
        var centre = basis.X * (left + right) / 2 + basis.Y * (low + high) / 2 + basis.Z * Near(middle);
        var half = Mathf.Max(right - left, high - low) / 2 * 1.08f;
        var distance = half / Mathf.Tan(Mathf.DegToRad(Fov / 2)) + (corners.Max(Near) - Near(middle));
        return (centre, distance);
    }

    private void Aim(Vector3 centre, float distance)
    {
        var towards = Towards(Azimuth, Elevation);
        _camera.Transform = new Transform3D(Basis.LookingAt(-towards, Vector3.Up), centre + towards * distance);
    }

    private async Task<Image> Shoot(int size)
    {
        _viewport.Size = new Vector2I(size, size);
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        // A few frames, so the ambient occlusion and the glow have settled.
        for (var f = 0; f < 4; f++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = _viewport.GetTexture().GetImage();
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    /// <summary>Warns where exports of one hull differ outside their weapons: the split would be wrong there.</summary>
    private void Compare(List<Unit> level)
    {
        static string Hull(Unit u) => string.Join(' ', u.Rig.Nodes.Select((n, i) => (n, i)).Where(p => u.Hull(p.i))
            .Select(p => $"{p.n.Name}:{(u.Meshes[p.i]?.Mesh is ArrayMesh m ? Enumerable.Range(0, m.GetSurfaceCount()).Sum(m.SurfaceGetArrayLen) : 0)}"));
        foreach (var other in level.Skip(1))
            if (Hull(other) != Hull(level[0]))
                _report.Add($"WARNING {other.Export.Id}: its hull differs from {level[0].Export.Id}'s; check the weapon split");
    }

    /// <summary>
    /// The hull's parts for remaking them: per part its rig name, parent, the piece it belongs to, pivot and size
    /// (metres, in the original's axes: x forward, y left, z up), its colour in the part map, and what moves it.
    /// </summary>
    private static string PartsJson(Unit unit, Dictionary<int, Color> colours, string stem)
    {
        static float[] Original(Vector3 v) => [MathF.Round(v.X, 3), MathF.Round(v.Z, 3), MathF.Round(v.Y, 3)];
        var rig = unit.Rig;
        var parts = rig.Nodes.Select((node, i) =>
        {
            var moves = new List<string>();
            moves.AddRange(rig.Animations.Where(a => a.Value.Tracks.Any(t => t.Node == i)).Select(a => a.Key));
            if (rig.Wheels is { } wheels && (wheels.Left.Contains(i) || wheels.Right.Contains(i)))
                moves.Add("wheel");
            if (rig.Tracks is { } tracks && (tracks.Left == i || tracks.Right == i))
                moves.Add("tread");
            if (rig.Idle.Any(t => t.Track.Node == i))
                moves.Add("idle");
            if (rig.Working.Any(t => t.Track.Node == i))
                moves.Add("working");
            if (rig.Weapons.Any(w => w.Node == i))
                moves.Add("recoil");
            if (rig.UpperBody == i)
                moves.Add("turns to aim");
            var pivot = unit.View.FindChild($"Part{i}", true, false) is Node3D part ? part.GlobalPosition : Vector3.Zero;
            var points = Points(unit, n => n == i);
            var size = points.Count == 0 ? Vector3.Zero
                : points.Aggregate(new Aabb(points[0], Vector3.Zero), (box, p) => box.Expand(p)).Size;
            return new
            {
                node = i,
                name = node.Name,
                parent = node.Parent,
                piece = unit.Face.Contains(i) ? "faceplate" : unit.Weapon.TryGetValue(i, out var side) ? $"weapon {side}" : "hull",
                pivot = Original(pivot),
                size = Original(size),
                colour = colours.TryGetValue(i, out var c) ? "#" + c.ToHtml(false) : null,
                moves,
            };
        });
        return JsonSerializer.Serialize(new
        {
            id = unit.Export.Id,
            images = new { context = stem + "-context.png", hull = stem + "-hull.png", parts = stem + "-parts.png" },
            axes = "metres; x forward, y left, z up (the original's)",
            parts,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>A weapon's name for people: HEAVY_BOLTER1 becomes "Heavy bolter 1".</summary>
    internal static string Title(string weapon)
    {
        var words = weapon.ToLowerInvariant().Split('_');
        var text = string.Join(' ', words.Select(w => w.Length > 1 && char.IsDigit(w[^1]) && !char.IsDigit(w[0]) ? w.TrimEnd("0123456789".ToCharArray()) + " " + w[w.TrimEnd("0123456789".ToCharArray()).Length..] : w));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

internal static class StudioImages
{
    /// <summary>The picture laid onto white (its colours not premultiplied, as Godot's viewports give them).</summary>
    public static Image OnWhite(this Image image)
    {
        var data = image.GetData();
        for (var p = 0; p < data.Length; p += 4)
        {
            var alpha = data[p + 3] / 255f;
            for (var c = 0; c < 3; c++)
                data[p + c] = (byte)Math.Round(data[p + c] * alpha + 255 * (1 - alpha));
            data[p + 3] = 255;
        }
        return Image.CreateFromData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
    }
}
