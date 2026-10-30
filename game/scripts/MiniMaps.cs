using Godot;
using Machines.Import.Scenarios;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The maps of the panel's continent map and of the menus. With the original ground they show the original's overview
/// image of the planet (map.BMP, <see cref="ClassicAssets.PlanetMap"/>); with the sharpened or painted ground, the map
/// as the look draws it, taken straight down without the machines and buildings, once for each scenario and for each
/// choice of the parts that show on it (the ground, the trees and rocks, the lighting), the first time it is played
/// (under the <see cref="LoadingScreen"/>), and kept in <c>classic/minimaps/&lt;ground&gt;-&lt;scenery&gt;-&lt;lighting&gt;</c>.
///
/// MACHINES_MINIMAPS=1 takes every scenario's that is missing, in the look of MACHINES_LOOK, and quits;
/// MACHINES_MINIMAPS=all takes them all again.
/// </summary>
public static partial class MiniMaps
{
    /// <summary>The picture's longer side, in pixels.</summary>
    public const int Size = 1024;

    private static readonly Dictionary<string, Texture2D?> Loaded = [];

    /// <summary>Whether the ground chosen has its own map: all but the original one.</summary>
    private static bool Drawn => GraphicsSettings.Terrain != TerrainLook.Original;

    /// <summary>Where the map of <paramref name="scenario"/> lies for the parts chosen that show on it.</summary>
    private static string PathOf(string scenario)
    {
        static string Part(bool remake) => remake ? "remake" : "original";
        var parts = GraphicsSettings.Parts;
        var folder = $"{parts.Terrain}-{Part(parts.Scenery)}-{Part(parts.Lighting)}".ToLowerInvariant();
        return System.IO.Path.Combine(Paths.Classic, "minimaps", folder, scenario.ToLowerInvariant() + ".png");
    }

    /// <summary>
    /// The map shown for <paramref name="scenario"/> with the parts chosen, or null where the original's is: with mipmaps
    /// for drawing at any size, or made <paramref name="width"/> pixels wide for drawing at that size without them.
    /// </summary>
    public static Texture2D? For(string scenario, int width = 0)
    {
        if (!Drawn)
            return null;
        var path = PathOf(scenario);
        var key = $"{path}|{width}";
        if (Loaded.TryGetValue(key, out var map))
            return map;
        if (File.Exists(path) && Image.LoadFromFile(path) is { } image)
        {
            if (width > 0 && width < image.GetWidth())
                image.Resize(width, Mathf.Max(1, Mathf.RoundToInt((float)image.GetHeight() * width / image.GetWidth())), Image.Interpolation.Lanczos);
            else
                image.GenerateMipmaps();
            map = ImageTexture.CreateFromImage(image);
        }
        return Loaded[key] = map;
    }

    /// <summary>Whether the parts chosen draw their own map of <paramref name="scenario"/> and has not taken it yet.</summary>
    public static bool Missing(string scenario) => Drawn && !File.Exists(PathOf(scenario));

    /// <summary>
    /// MACHINES_MINIMAPS: starts every scenario of the skirmish page in turn, with the first two races, so the loading
    /// screen takes the maps that are missing (<paramref name="again"/>: all of them), then quits.
    /// </summary>
    public static void TakeAll(SceneTree tree, bool again)
    {
        var scenarios = new List<(string Name, string Planet)>();
        var catalogue = System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat");
        if (File.Exists(catalogue) && ScenarioCatalogueParser.Load(catalogue) is { } found)
            scenarios.AddRange(found.Skirmish.Concat(found.Multiplayer).SelectMany(t => t.Scenarios).Select(s => (s.Name, s.Planet)));
        var own = System.IO.Path.Combine(Paths.Maps, "maps.txt");
        if (File.Exists(own))
            scenarios.AddRange(File.ReadLines(own).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//"))
                .Select(l => l.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries)).Select(w => (w[0], w[1])));
        var left = new Queue<(string, string)>(scenarios.DistinctBy(s => s.Name.ToLowerInvariant()));
        if (again)
            foreach (var (name, _) in left)
                File.Delete(PathOf(name));
        GD.Print($"minimaps: {left.Count(s => Missing(s.Item1))} of {left.Count} scenarios to take for {GraphicsSettings.Parts}");
        tree.Root.CallDeferred(Node.MethodName.AddChild, new Round(left));
    }

    private sealed partial class Round(Queue<(string Name, string Planet)> left) : Node
    {
        public override void _Process(double delta)
        {
            if (LoadingScreen.Busy)
                return;
            while (left.TryDequeue(out var next))
            {
                var slots = Paths.Data.LoadScenario(next.Name).Races.Select(r => r.Race).OrderBy(r => r).ToList();
                if (!Missing(next.Name) || slots.Count == 0)
                    continue;
                GD.Print($"minimaps: {next.Name} on {next.Planet}");
                MatchRequest.Current = new MatchRequest(next.Name, next.Planet,
                    StartPositions.Assign(slots, 2, slots[0], random: false, new Random(1)), new MatchOptions());
                LoadingScreen.Show(GetTree(), next.Name.ToUpperInvariant(), "TAKING THE MAP");
                return;
            }
            GetTree().Quit();
        }
    }

    /// <summary>Takes the map of the match <paramref name="game"/> has begun, then does <paramref name="done"/>.</summary>
    public static void Take(Game game, string scenario, Action done) =>
        game.AddChild(new Camera(PathOf(scenario), done));

    /// <summary>
    /// A picture of the match's world from straight above, the planet filling it: an orthographic camera of its own
    /// over the middle of the map, a little above its highest ground. The machines, buildings, effects and weather
    /// are hidden while it is taken (the loading screen covers the match meanwhile), each frame after the match has
    /// shown them, and shown again after. So is the shadow the sun casts: the cliffs, all square to the map, would lay
    /// black wedges across it; the slopes stay lit and shaded as the sun stands, as the original's map shows them.
    /// </summary>
    private sealed partial class Camera(string path, Action done) : SubViewport
    {
        // Frames for the screen-space light and the anti-aliasing to settle.
        private const int SettleFrames = 12;

        private readonly Dictionary<Node3D, bool> _hidden = [];
        private readonly Dictionary<Light3D, bool> _shadows = [];
        private int _frames;

        public override void _Ready()
        {
            var game = GetParent<Game>();
            var surface = game.World.Planet.Surface;
            var (width, depth) = (surface.Width, surface.Depth);
            Size = width >= depth
                ? new Vector2I(MiniMaps.Size, Mathf.RoundToInt(MiniMaps.Size * depth / width))
                : new Vector2I(Mathf.RoundToInt(MiniMaps.Size * width / depth), MiniMaps.Size);
            World3D = game.GetWorld3D();
            Msaa3D = Msaa.Msaa4X;
            RenderTargetUpdateMode = UpdateMode.Always;
            ProcessPriority = int.MaxValue;

            var top = 0f;
            if (HeightField.Current is { } field)
                for (var x = 0; x < field.VerticesX; x++)
                    for (var y = 0; y < field.VerticesY; y++)
                        top = Mathf.Max(top, field.Vertex(x, y));
            // Looking down with the far edge of the planet (y = 0) at the top, as the original's map image has it.
            var camera = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Size = depth,
                Near = 1,
                Far = top + 200,
                Current = true,
            };
            // The planet's air without its fog: looking straight down, the fog would count the distance across the
            // map and grey the edges (Godot's fog measures from the camera's point, even for an orthographic one).
            if (game.GetWorld3D().Environment?.Duplicate() is Godot.Environment air)
            {
                air.FogEnabled = false;
                air.VolumetricFogEnabled = false;
                camera.Environment = air;
            }
            AddChild(camera);
            camera.LookAtFromPosition(new Vector3(width / 2, top + 20, depth / 2), new Vector3(width / 2, 0, depth / 2), Vector3.Forward);
        }

        public override void _Process(double delta)
        {
            Hide(GetParent());
            if (++_frames < SettleFrames)
                return;
            GetTexture().GetImage().SavePng(MakeFolder(path));
            foreach (var key in Loaded.Keys.Where(k => k.StartsWith(path + "|", StringComparison.Ordinal)).ToList())
                Loaded.Remove(key);
            foreach (var (node, visible) in _hidden)
                if (IsInstanceValid(node))
                    node.Visible = visible;
            foreach (var (light, shadow) in _shadows)
                if (IsInstanceValid(light))
                    light.ShadowEnabled = shadow;
            QueueFree();
            done();
        }

        /// <summary>Hides what moves over the map: what the scenario starts with, effects and weather.</summary>
        private void Hide(Node game)
        {
            foreach (var child in game.GetChildren())
                if (child is ActorView or Debris or Effects or ProjectileViews or SupportEffects or Weather or GroundBlast && child is Node3D node)
                {
                    _hidden.TryAdd(node, node.Visible);
                    node.Visible = false;
                }
            if (_shadows.Count == 0)
                foreach (var sun in game.FindChildren("*", "", true, false).OfType<DirectionalLight3D>())
                    _shadows[sun] = sun.ShadowEnabled;
            foreach (var sun in _shadows.Keys)
                sun.ShadowEnabled = false;
        }

        private static string MakeFolder(string file)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            return file;
        }
    }
}
