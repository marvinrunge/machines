using System.Text;
using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The story's machines against their hitboxes, as pictures: run res://scenes/Studio.tscn with
/// <c>MACHINES_STUDIO=&lt;out folder&gt;</c> and <c>MACHINES_STUDIO_HITBOX=1</c> (MACHINES_LOOK picks the models). Each
/// machine is drawn in its rest pose from above and from the side, at one scale per machine, on a 1 m grid, with the
/// shapes the simulation gives it: in red the circle it stands in (<see cref="Actor.Radius"/>, for driving into one
/// another and the size classes), in orange the box rounds hit (<c>GameWorld.BoxOf</c>, a square of the radius, as tall as
/// twice its aim point), and in green the story's body fitted to the model (<see cref="StoryBodies"/>). Writes
/// <c>&lt;out&gt;/hitbox-&lt;machine&gt;-&lt;look&gt;.png</c> and <c>&lt;out&gt;/hitbox-&lt;look&gt;.txt</c>, the model's
/// extents measured from its vertices (glow sprites left out).
/// </summary>
public partial class Studio
{
    private sealed record HitboxSubject(string File, string Label, string Type, string? SubType, int Level, Func<Rig?> Rig);

    private async Task HitboxSheets(string output)
    {
        // The story's look: Ike and Bob's empty gun cradle comes off (HeroRig.Unarmed).
        GraphicsSettings.Story = true;
        var data = Paths.Data;
        var look = GraphicsSettings.Models.ToString().ToLowerInvariant();
        string?[] None() => [null, null, null];
        var subjects = new List<HitboxSubject>
        {
            new("l7", "L-7 (Geo Locator 1, the hero geo1)", "GEO_LOCATOR", null, 1, () => HeroRig.Compose(_assets, Race.Red, "geo1", None())),
            new("l7-bolter", "L-7 with its Bolter", "GEO_LOCATOR", null, 1, () => HeroRig.Compose(_assets, Race.Red, "geo1", [null, null, "BOLTER"])),
            new("ike-bob", "Ike and Bob (Grunt 1, the hero grunt1, unarmed)", "AGGRESSOR", "GRUNT", 1, () => HeroRig.Compose(_assets, Race.Red, "grunt1", None())),
            new("ike-bob-bolter", "Ike and Bob with a Bolter", "AGGRESSOR", "GRUNT", 1, () => HeroRig.Compose(_assets, Race.Red, "grunt1", [null, "BOLTER", null])),
            new("grunt1", "Grunt 1 R_BOLTER (the workshop's Grunts)", "AGGRESSOR", "GRUNT", 1, () => _assets.RigFor("AGGRESSOR_1_GRUNT_R_BOLTER", Race.Red)),
            new("grunt2", "Grunt 2 L_BOLTER", "AGGRESSOR", "GRUNT", 2, () => _assets.RigFor("AGGRESSOR_2_GRUNT_L_BOLTER", Race.Red)),
            new("grunt3", "Grunt 3 LR_HEAVY_BOLTER1_X2", "AGGRESSOR", "GRUNT", 3, () => _assets.RigFor("AGGRESSOR_3_GRUNT_LR_HEAVY_BOLTER1_X2", Race.Red)),
            new("reaper2", "Reaper 2 L_PLASMA_RIFLE", "AGGRESSOR", "ASSASSIN", 2, () => _assets.RigFor("AGGRESSOR_2_ASSASSIN_L_PLASMA_RIFLE", Race.Red)),
            new("reaper3", "Reaper 3 LR_PLASMA_RIFLE_X2", "AGGRESSOR", "ASSASSIN", 3, () => _assets.RigFor("AGGRESSOR_3_ASSASSIN_LR_PLASMA_RIFLE_X2", Race.Red)),
            new("thresher", "Thresher (Reaper 4) LR_PLASMA_RIFLE_X2", "AGGRESSOR", "ASSASSIN", 4, () => _assets.RigFor("AGGRESSOR_4_ASSASSIN_LR_PLASMA_RIFLE_X2", Race.Red)),
            new("commander", "Commander 5 LR_PULSE_CANNON_X2", "ADMINISTRATOR", "COMMANDER", 5, () => _assets.RigFor("ADMINISTRATOR_5_COMMANDER_LR_PULSE_CANNON_X2", Race.Red)),
        };
        var report = new StringBuilder();
        report.AppendLine($"look {look}: model extents from the vertices (x forward, z across, y up), metres; sim radius, hit box, story body");
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        foreach (var subject in subjects)
        {
            if (subject.Rig() is not { } rig)
            {
                report.AppendLine($"{subject.File}: no rig");
                continue;
            }
            var view = RigView.Create(rig);
            _stage.AddChild(view);
            for (var f = 0; f < 2; f++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var points = SolidPoints(view);
            var stats = data.Parm.Machine(subject.SubType is null ? subject.Type : $"{subject.Type} {subject.SubType}").Level(subject.Level);
            var radius = Machine.RadiusOf(stats);
            var aim = MathF.Max(1.2f, radius * 0.6f);
            var body = StoryBodies.Of(subject.Type, subject.SubType, subject.Level);
            var (fore, aft) = (points.Max(p => p.X), points.Min(p => p.X));
            var (left, right) = (points.Max(p => p.Z), points.Min(p => p.Z));
            var (top, bottom) = (points.Max(p => p.Y), points.Min(p => p.Y));
            var reach = points.Max(p => new Vector2(p.X, p.Z).Length());
            var line = $"{subject.File,-15} fore {fore,5:0.00} aft {aft,6:0.00} across {right,6:0.00}..{left,5:0.00} (width {left - right,5:0.00}, length {fore - aft,5:0.00}) "
                + $"height {top,5:0.00} footprint reach {reach,5:0.00} | HighClearance {stats.HighClearance:0.00} LowClearance {stats.LowClearance:0.00} radius {radius:0.00} "
                + $"hit box {2 * radius:0.0} x {2 * radius:0.0} x {2 * aim:0.0}"
                + (body is { } b ? $" | story body fore {b.Fore:0.00} aft {b.Aft:0.00} half width {b.Half:0.00} height {b.Height:0.00}" : "");
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
            Rectangle(lines, -radius, radius, -radius, radius, high, orange, top: true);
            if (body is { } fitted)
                Rectangle(lines, -fitted.Aft, fitted.Fore, -fitted.Half, fitted.Half, high, green, top: true);
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
                + (body is { } c ? $" · green: story body {c.Fore + c.Aft:0.0} x {2 * c.Half:0.0} x {c.Height:0.0} m" : "");
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

    private static void Rectangle(List<(Vector3, Vector3, Color)> lines, float x0, float x1, float z0, float z1, float y, Color colour, bool top)
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
