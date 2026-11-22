using System.Globalization;
using System.Text;
using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// Every machine against its hitboxes: run res://scenes/Studio.tscn with <c>MACHINES_STUDIO=&lt;out folder&gt;</c> and
/// <c>MACHINES_STUDIO_HITBOX=numbers</c> (it runs under <c>--headless</c>) or <c>=1</c> (pictures too, which need the GPU).
/// The subjects are every machine of classic/models.txt and classic/wired/models.txt with the loadouts its exports carry,
/// and every Wired hero's chassis bare (<see cref="HeroRig.Compose(ClassicAssets, Race, string, IReadOnlyList{string?})"/>).
/// <para>
/// <c>numbers</c> measures each subject in all three looks (classic, overhauled and, as MACHINES_HD_MODELS=1 or the editor
/// offer them, HD), from the vertices of its model in its rest pose, glow sprites left out, and writes
/// <c>&lt;out&gt;/bodies.tsv</c> (a line per subject and look: forward, back, left, right and top of the model, metres) and
/// <c>&lt;out&gt;/bodies.cs</c>, the table of <see cref="MachineBodies"/>: for each type and level the largest of every
/// loadout and every look, rounded up to 5 cm. A look without the model is left out; a type and level no look has a
/// model of is logged and keeps its circle.
/// </para>
/// <para>
/// <c>1</c> draws each subject in the current look (MACHINES_LOOK) from above and from the side, on a 1 m grid, with the
/// shapes the simulation gives it: in red the circle it stands in (<see cref="Actor.Radius"/>), in orange the box rounds
/// hit without a body (<c>GameWorld.BoxOf</c>, a square of the radius, as tall as twice its aim point), and in green its
/// body (<see cref="MachineBodies"/>): <c>&lt;out&gt;/hitbox-&lt;subject&gt;-&lt;look&gt;.png</c>.
/// </para>
/// </summary>
public partial class Studio
{
    private sealed record HitboxSubject(string File, string Label, string Type, string? SubType, int Level, Func<ClassicAssets, Rig?> Rig);

    /// <summary>What a subject measures in one look: forward and back of its middle, its left and right, its top, metres.</summary>
    private readonly record struct Extent(float Fore, float Aft, float Left, float Right, float Top);

    /// <summary>The machines of the exports' lists and the Wired heroes' chassis, bare.</summary>
    private static List<HitboxSubject> HitboxSubjects()
    {
        var subjects = new List<HitboxSubject>();
        var lists = new[] { Path.Combine(Paths.Classic, "models.txt"), Path.Combine(Paths.Classic, "wired", "models.txt") };
        foreach (var line in lists.Where(File.Exists).SelectMany(File.ReadLines).Where(l => l.StartsWith("MACHINE ")))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var withSub = words[1] is "AGGRESSOR" or "ADMINISTRATOR" or "CONSTRUCTOR" or "TECHNICIAN";
            var id = string.Join('_', words.Skip(1));
            subjects.Add(new HitboxSubject(id.ToLowerInvariant(), id, words[1], withSub ? words[3] : null, int.Parse(words[2], CultureInfo.InvariantCulture),
                assets => assets.RigFor(id, Race.Red)));
        }
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var id = chassis.Id;
            subjects.Add(new HitboxSubject($"hero-{id}", $"hero {id}, bare", chassis.Type, chassis.SubType, chassis.Level,
                assets => HeroRig.Compose(assets, Race.Red, id, [null, null, null])));
        }
        return subjects;
    }

    /// <summary>A type and level's stats in the data with Wired's levels added, or null.</summary>
    private static MachineLevel? StatsOf(ParmData parm, string type, string? subType, int level)
    {
        var name = subType is null ? type : $"{type} {subType}";
        return parm.Machines.FirstOrDefault(m => m.TypeName == name)?.Levels.FirstOrDefault(l => l.HardwareLevel == level);
    }

    private async Task HitboxNumbers(string output)
    {
        var parm = WiredMachines.Extend(Paths.Data.Parm);
        var subjects = HitboxSubjects();
        var looks = new List<ModelLook> { ModelLook.Original, ModelLook.Overhauled };
        if (GraphicsSettings.HdModels)
            looks.Add(ModelLook.Hd);
        var tsv = new StringBuilder("look\tsubject\ttype\tsubtype\tlevel\tfore\taft\tleft\tright\ttop\tfile\n");
        var measured = new Dictionary<(string Type, string? SubType, int Level), List<(string Subject, ModelLook Look, Extent Extent)>>();
        foreach (var look in looks)
        {
            var assets = new ClassicAssets(Paths.Root, look);
            foreach (var subject in subjects)
            {
                // A look without its own file shows another look's (ModelPath), which only measures that one again.
                GD.Print($"hitbox: {look} {subject.File}");
                if (subject.Rig(assets) is not { } rig)
                {
                    GD.Print($"hitbox: {subject.Label} has no model in {look}");
                    continue;
                }
                var view = RigView.Create(rig);
                _stage.AddChild(view);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var points = SolidPoints(view);
                view.QueueFree();
                if (points.Count == 0)
                {
                    GD.Print($"hitbox: {subject.Label} shows nothing in {look}");
                    continue;
                }
                var extent = new Extent(points.Max(p => p.X), -points.Min(p => p.X), points.Max(p => p.Z), -points.Min(p => p.Z), points.Max(p => p.Y));
                var file = SourceOf(assets, subject);
                tsv.Append(CultureInfo.InvariantCulture, $"{look}\t{subject.File}\t{subject.Type}\t{subject.SubType}\t{subject.Level}\t{extent.Fore:0.000}\t{extent.Aft:0.000}\t{extent.Left:0.000}\t{extent.Right:0.000}\t{extent.Top:0.000}\t{file}\n");
                var key = (subject.Type, subject.SubType, subject.Level);
                if (!measured.TryGetValue(key, out var list))
                    measured[key] = list = [];
                list.Add((subject.File, look, extent));
            }
        }
        File.WriteAllText(Path.Combine(output, "bodies.tsv"), tsv.ToString());
        File.WriteAllText(Path.Combine(output, "bodies.cs"), BodyTable(parm, measured));
        GD.Print($"hitbox: {measured.Count} types and levels measured into {output}");
    }

    /// <summary>The file a subject's machine comes from in a look, to tell a look's own model from a fallback; the chassis' for a hero.</summary>
    private static string SourceOf(ClassicAssets assets, HitboxSubject subject)
    {
        var id = subject.File.StartsWith("hero-", StringComparison.Ordinal) ? HeroDonors.Chassis.GetValueOrDefault(subject.File[5..]) : subject.Label;
        return id is null ? "" : assets.ModelFile(id, Race.Red)?.Replace('\\', '/') is { } path ? Path.GetRelativePath(Paths.Root, path).Replace('\\', '/') : "";
    }

    private static float UpTo5Cm(float metres) => MathF.Ceiling(metres * 20 - 1e-3f) / 20;

    /// <summary>The C# table: per type and level the largest of its subjects in every look, rounded up to 5 cm; gliders and the unknown left out.</summary>
    private static string BodyTable(ParmData parm, Dictionary<(string Type, string? SubType, int Level), List<(string Subject, ModelLook Look, Extent Extent)>> measured)
    {
        var text = new StringBuilder();
        foreach (var ((type, subType, level), list) in measured.OrderBy(p => p.Key.Type, StringComparer.Ordinal).ThenBy(p => p.Key.SubType, StringComparer.Ordinal).ThenBy(p => p.Key.Level))
        {
            var name = $"{(subType ?? type)} {level}";
            if (StatsOf(parm, type, subType, level) is not { } stats)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"        // {name}: no such machine in the data, left out.");
                continue;
            }
            if (stats.Locomotion == Locomotion.Glider)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"        // {name}: flies, keeps its box in the air.");
                continue;
            }
            var fore = UpTo5Cm(list.Max(m => m.Extent.Fore));
            var aft = UpTo5Cm(list.Max(m => m.Extent.Aft));
            var half = UpTo5Cm(list.Max(m => MathF.Max(m.Extent.Left, m.Extent.Right)));
            var height = UpTo5Cm(list.Max(m => m.Extent.Top));
            string Look(ModelLook look)
            {
                var of = list.Where(m => m.Look == look).ToList();
                if (of.Count == 0)
                    return $"{look} none";
                var length = of.Max(m => m.Extent.Fore + m.Extent.Aft);
                var width = of.Max(m => m.Extent.Left + m.Extent.Right);
                var top = of.Max(m => m.Extent.Top);
                return string.Create(CultureInfo.InvariantCulture, $"{LookName(look)} {length:0.00} x {width:0.00} x {top:0.00}");
            }
            var radius = Machine.RadiusOf(stats);
            var models = list.Select(m => m.Subject).Distinct().Count();
            text.AppendLine(CultureInfo.InvariantCulture, $"        // {name} (radius {radius:0.00}, {models} model{(models == 1 ? "" : "s")}): {Look(ModelLook.Original)}, {Look(ModelLook.Overhauled)}, {Look(ModelLook.Hd)} m.");
            var sub = subType is null ? "null" : $"\"{subType}\"";
            text.AppendLine(CultureInfo.InvariantCulture, $"        [(\"{type}\", {sub}, {level})] = new({fore:0.00}f, {aft:0.00}f, {half:0.00}f, {height:0.00}f),");
        }
        return text.ToString();
    }

    private static string LookName(ModelLook look) => look switch
    {
        ModelLook.Original => "classic",
        ModelLook.Overhauled => "overhauled",
        _ => "HD",
    };

    private async Task HitboxSheets(string output)
    {
        var data = Paths.Data;
        var parm = WiredMachines.Extend(data.Parm);
        var look = GraphicsSettings.Models.ToString().ToLowerInvariant();
        var report = new StringBuilder();
        report.AppendLine($"look {look}: model extents from the vertices (x forward, z across, y up), metres; sim radius, hit box, body");
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        foreach (var subject in HitboxSubjects())
        {
            if (subject.Rig(_assets) is not { } rig || StatsOf(parm, subject.Type, subject.SubType, subject.Level) is not { } stats)
            {
                report.AppendLine($"{subject.File}: no rig");
                continue;
            }
            var view = RigView.Create(rig);
            _stage.AddChild(view);
            for (var f = 0; f < 2; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var points = SolidPoints(view);
            var radius = Machine.RadiusOf(stats);
            var aim = MathF.Max(1.2f, radius * 0.6f);
            var body = MachineBodies.Of(subject.Type, subject.SubType, subject.Level);
            var (fore, aft) = (points.Max(p => p.X), points.Min(p => p.X));
            var (left, right) = (points.Max(p => p.Z), points.Min(p => p.Z));
            var top = points.Max(p => p.Y);
            var line = $"{subject.File,-40} fore {fore,5:0.00} aft {aft,6:0.00} across {right,6:0.00}..{left,5:0.00} (width {left - right,5:0.00}, length {fore - aft,5:0.00}) "
                + $"height {top,5:0.00} | radius {radius:0.00} hit box {2 * radius:0.0} x {2 * radius:0.0} x {2 * aim:0.0}"
                + (body is { } b ? $" | body fore {b.Fore:0.00} aft {b.Aft:0.00} half width {b.Half:0.00} height {b.Height:0.00}" : "");
            report.AppendLine(line);
            GD.Print(line);

            var lines = new List<(Vector3 A, Vector3 B, Color Colour)>();
            var gridLines = new List<(Vector3 A, Vector3 B, Color Colour)>();
            var red = new Color(0.85f, 0.1f, 0.1f);
            var orange = new Color(1f, 0.55f, 0f);
            var green = new Color(0.05f, 0.7f, 0.15f);
            var grid = new Color(0.55f, 0.6f, 0.7f, 0.6f);
            // Half the picture's width: the model, the circle and the shapes all inside, with a margin.
            var span = new[] { fore, -aft, left, -right, radius, body?.Fore ?? 0, body?.Aft ?? 0, body?.Half ?? 0 }.Max() * 1.15f + 0.5f;
            var tall = MathF.Max(top, 2 * aim) * 1.1f + 0.5f;

            // From above: the grid on the ground, the circle, the hit box, the body.
            for (var g = -MathF.Floor(span); g <= span; g++)
            {
                gridLines.Add((new Vector3(g, 0, -span), new Vector3(g, 0, span), grid));
                gridLines.Add((new Vector3(-span, 0, g), new Vector3(span, 0, g), grid));
            }
            const int Segments = 96;
            var high = top + 0.05f;
            for (var s = 0; s < Segments; s++)
            {
                var (a0, a1) = (s * Mathf.Tau / Segments, (s + 1) * Mathf.Tau / Segments);
                lines.Add((new Vector3(Mathf.Cos(a0) * radius, high, Mathf.Sin(a0) * radius), new Vector3(Mathf.Cos(a1) * radius, high, Mathf.Sin(a1) * radius), red));
            }
            Rectangle(lines, -radius, radius, -radius, radius, high, orange);
            if (body is { } fitted)
                Rectangle(lines, -fitted.Aft, fitted.Fore, -fitted.Half, fitted.Half, high, green);
            var lineMesh = LineMesh(lines, onTop: true);
            var gridMesh = LineMesh(gridLines, onTop: false);
            _stage.AddChild(lineMesh);
            _stage.AddChild(gridMesh);
            _camera.Size = 2 * span;
            _camera.Transform = new Transform3D(Basis.LookingAt(Vector3.Down, Vector3.Forward), new Vector3(0, top + 20, 0));
            var above = (await Shoot(CellSize)).OnWhite();
            lineMesh.QueueFree();
            gridMesh.QueueFree();

            // From the side: the ground, the hit box's and the body's height.
            lines.Clear();
            gridLines.Clear();
            for (var g = -MathF.Floor(span); g <= span; g++)
                gridLines.Add((new Vector3(g, 0, -span), new Vector3(g, tall, -span), grid));
            for (var h = 0; h <= tall; h++)
                gridLines.Add((new Vector3(-span, h, -span), new Vector3(span, h, -span), grid));
            var near = span + 0.1f;
            Side(lines, -radius, radius, 0, 2 * aim, near, orange);
            // The circle seen from the side: its width, at the aim point's height (where the original aimed).
            lines.Add((new Vector3(-radius, aim, near), new Vector3(radius, aim, near), red));
            lines.Add((new Vector3(-radius, aim - 0.2f, near), new Vector3(-radius, aim + 0.2f, near), red));
            lines.Add((new Vector3(radius, aim - 0.2f, near), new Vector3(radius, aim + 0.2f, near), red));
            if (body is { } sideBody)
                Side(lines, -sideBody.Aft, sideBody.Fore, 0, sideBody.Height, near, green);
            lineMesh = LineMesh(lines, onTop: true);
            gridMesh = LineMesh(gridLines, onTop: false);
            _stage.AddChild(lineMesh);
            _stage.AddChild(gridMesh);
            _camera.Size = MathF.Max(2 * span, tall);
            _camera.Transform = new Transform3D(Basis.LookingAt(Vector3.Forward, Vector3.Up), new Vector3(0, MathF.Max(2 * span, tall) / 2 - 0.5f, span + 30));
            var beside = (await Shoot(CellSize)).OnWhite();
            lineMesh.QueueFree();
            gridMesh.QueueFree();
            view.QueueFree();

            var caption = $"{subject.Label} · {look}: model {fore - aft:0.0} m long, {left - right:0.0} m wide, {top:0.0} m tall";
            var shapes = $"red: radius {radius:0.00} m · orange: hit box {2 * radius:0.0} x {2 * radius:0.0} x {2 * aim:0.0} m"
                + (body is { } c ? $" · green: body {c.Fore + c.Aft:0.0} x {2 * c.Half:0.0} x {c.Height:0.0} m" : "");
            var sheet = await Compose([(caption + " · from above (front to the right)", above), (shapes + " · from the side", beside)], CellSize, 110, 26);
            sheet.SavePng(Path.Combine(output, $"hitbox-{subject.File}-{look}.png"));
        }
        File.WriteAllText(Path.Combine(output, $"hitbox-{look}.txt"), report.ToString());
    }

    /// <summary>The model's vertices in the world, the glow sprites' left out (their corners sit at their middles).</summary>
    private static List<Vector3> SolidPoints(Node node)
    {
        var points = new List<Vector3>();
        foreach (var child in node.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D { Mesh: ArrayMesh mesh } instance || !instance.IsVisibleInTree())
                continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.SurfaceGetArrays(s);
                if (mesh.SurfaceGetMaterial(s) is ShaderMaterial && arrays[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.PackedVector2Array)
                    continue;
                points.AddRange(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Select(v => instance.GlobalTransform * v));
            }
        }
        return points;
    }

    private static void Rectangle(List<(Vector3, Vector3, Color)> lines, float x0, float x1, float z0, float z1, float y, Color colour)
    {
        Vector3[] c = [new(x0, y, z0), new(x1, y, z0), new(x1, y, z1), new(x0, y, z1)];
        for (var i = 0; i < 4; i++)
        {
            lines.Add((c[i], c[(i + 1) % 4], colour));
            // A second line beside it, so the shape shows thick.
            var nudge = (c[(i + 2) % 4] - c[i]).Normalized() * 0.03f;
            lines.Add((c[i] + nudge, c[(i + 1) % 4] + nudge, colour));
        }
    }

    private static void Side(List<(Vector3, Vector3, Color)> lines, float x0, float x1, float y0, float y1, float z, Color colour)
    {
        Vector3[] c = [new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z)];
        for (var i = 0; i < 4; i++)
        {
            lines.Add((c[i], c[(i + 1) % 4], colour));
            var nudge = (c[(i + 2) % 4] - c[i]).Normalized() * 0.03f;
            lines.Add((c[i] + nudge, c[(i + 1) % 4] + nudge, colour));
        }
    }

    /// <summary>Lines, unlit, in their own colours; <paramref name="onTop"/> drawn over everything.</summary>
    private static MeshInstance3D LineMesh(List<(Vector3 A, Vector3 B, Color Colour)> lines, bool onTop)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var (a, b, colour) in lines)
        {
            mesh.SurfaceSetColor(colour);
            mesh.SurfaceAddVertex(a);
            mesh.SurfaceSetColor(colour);
            mesh.SurfaceAddVertex(b);
        }
        mesh.SurfaceEnd();
        return new MeshInstance3D
        {
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                NoDepthTest = onTop,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                RenderPriority = 10,
            },
        };
    }
}
