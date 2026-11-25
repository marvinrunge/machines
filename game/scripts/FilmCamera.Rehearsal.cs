using Godot;

namespace Machines.Game;

// The space stage rehearsed for the warm-up: what a story's deck cutscenes show in space, put up once behind the loading
// screen so that Godot builds its pipelines there and not in the middle of the cutscene.
public abstract partial class FilmCamera
{
    // The models and music the rehearsal loaded, kept so the cutscene finds them loaded.
    private static readonly HashSet<Resource> Rehearsed = [];

    /// <summary>
    /// For the warm-up of a story's deck: a space stage (Showing, drawn under the loading screen) with one of each thing the
    /// deck's cutscenes (every <c>.cut</c> under data/planets) show in space: their props as their cues make them (glowing,
    /// trailing, in their bow shock), their blasts, fires, smoke, sparks, flashes and strikes on the planet, all bunched
    /// before its camera, the planet in view behind. Its first frames, drawn while the loading screen is up, built 43
    /// pipelines that held the cutscene up (the first blast on the hull 290 ms, the mothership's dive 90 ms). Null when no
    /// cutscene goes to space; the caller frees it.
    /// </summary>
    public static SpaceStage? RehearseSpace(Node parent)
    {
        var planets = System.IO.Path.Combine(Paths.Root, "data", "planets");
        if (!System.IO.Directory.Exists(planets))
            return null;
        var parts = new List<CutscenePart>();
        foreach (var path in System.IO.Directory.GetFiles(planets, "*.cut", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                if (CutsceneScript.Load(path).Part(true) is { HasSpace: true } part)
                    parts.Add(part);
            }
            catch (Exception error)
            {
                GD.PrintErr($"warm-up: cutscene {path}: {error.Message}");
            }
        }
        if (parts.Count == 0)
            return null;
        var stage = new SpaceStage(parts[0].Options) { Name = "WarmSpace" };
        parent.AddChild(stage);
        stage.Showing = true;
        // Everything bunched round the origin, the camera far enough back to see it all and the planet below beyond it.
        stage.Camera.Fov = 75;
        stage.Camera.Position = new Vector3(0, 3000, -6000);
        stage.Camera.LookAt(Vector3.Zero, Vector3.Up);
        var effects = stage.Effects;
        var spot = 0;
        Vector3 Next() => new((spot++ % 5 - 2) * 300, 0, 0);
        var props = new Dictionary<string, SceneProp>();
        foreach (var cue in parts.SelectMany(p => p.Cues).Where(c => c.Space))
        {
            switch (cue.Kind)
            {
                case "explosion":
                    effects.SpaceBlast(Next(), cue.Number(1));
                    break;
                case "impact":
                    effects.PlanetImpact(Next(), Vector3.Up, cue.Number(1));
                    break;
                case "sparks":
                    effects.SparkShower(Next(), null, (int)cue.Number("amount", 40));
                    break;
                case "fire" when Effects.HullFire(cue.Number(1), cue.Number(2)) is { } fire:
                    effects.AddChild(fire);
                    fire.Position = Next();
                    fire.Emitting = true;
                    break;
                case "smoke":
                    var smoke = Effects.SpaceSmoke(cue.Number(1), cue.Number("rise", 14));
                    effects.AddChild(smoke);
                    smoke.Position = Next();
                    smoke.Emitting = true;
                    break;
                case "flash":
                    var flash = FxShaders.NewFlash(new Color(1, 0.85f, 0.6f), 6, cue.Number(1), 6);
                    flash.SetInstanceShaderParameter("energy", 6f);
                    stage.Root.AddChild(flash);
                    flash.Position = Next();
                    stage.Root.AddChild(new OmniLight3D { LightEnergy = 12, OmniRange = cue.Number(1) * 2, ShadowEnabled = false, Position = flash.Position });
                    break;
                case "prop":
                    var path = cue.Args[1].Contains('/') ? $"res://assets/story/{cue.Args[1]}.glb" : $"res://assets/story/props/{cue.Args[1]}.glb";
                    if (!ResourceLoader.Exists(path) || GD.Load<PackedScene>(path) is not { } scene || scene.Instantiate() is not Node3D model)
                        break;
                    Rehearsed.Add(scene);
                    stage.Root.AddChild(model);
                    var at = Next();
                    var prop = new SceneProp(model, at + Vector3.Back * 400, at, cue, effects);
                    if (cue.Number("engines", 0) > 0)
                        Effects.Engines(model, cue.Args[1], cue.Number("engines", 0));
                    // Moved along a little, so that its trail has a way to draw.
                    for (var step = 0; step <= 4; step++)
                        prop.Update(null!, cue.At + Mathf.Max(cue.Number(4), 0.01f) * step / 4);
                    props[cue.Args[0]] = prop;
                    break;
                // The music loaded before (its first frame held the cutscene's first frame up).
                case "music" when ResourceLoader.Exists($"res://assets/{cue.Args[0]}.ogg"):
                    Rehearsed.Add(GD.Load<AudioStream>($"res://assets/{cue.Args[0]}.ogg"));
                    break;
                case "bowshock" when props.TryGetValue(cue.Args[0], out var shocked):
                    shocked.Bowshock(Mathf.Max(cue.Number(1), 1), 0, 0, cue.Number("size", 0));
                    shocked.Update(null!, float.MaxValue / 2);
                    break;
            }
        }
        return stage;
    }

    /// <summary>
    /// For the warm-up of a map a story's cutscene plays on outside (its name begins with the chapter's: prolog-abwurf):
    /// each prop the outside parts of that chapter's cutscenes (data/planets/&lt;chapter&gt;/*.cut) move, put up at
    /// <paramref name="at"/> as their cues make them (trailing), and what their landing raises. The pod streaking in held
    /// the outside of Abwurf up for 460 ms (its model loaded, its pipelines built), its landing 90 ms.
    /// </summary>
    public static void RehearseOutside(Node3D stage, Vector3 at, string map)
    {
        var chapter = map.Split('-')[0];
        var folder = System.IO.Path.Combine(Paths.Root, "data", "planets", chapter);
        if (!map.Contains('-') || !System.IO.Directory.Exists(folder))
            return;
        var effects = new Effects { Name = "WarmCutsceneEffects" };
        stage.AddChild(effects);
        foreach (var path in System.IO.Directory.GetFiles(folder, "*.cut"))
        {
            CutscenePart? part;
            try
            {
                part = CutsceneScript.Load(path).Part(false);
            }
            catch (Exception error)
            {
                GD.PrintErr($"warm-up: cutscene {path}: {error.Message}");
                continue;
            }
            foreach (var cue in part?.Cues.Where(c => c is { Kind: "prop", Space: false }) ?? [])
            {
                var name = cue.Args[1];
                var file = name.Contains('/') ? $"res://assets/story/{name}.glb" : $"res://assets/story/props/{name}.glb";
                if (!ResourceLoader.Exists(file) || GD.Load<PackedScene>(file) is not { } scene || scene.Instantiate() is not Node3D model)
                    continue;
                Rehearsed.Add(scene);
                stage.AddChild(model);
                var prop = new SceneProp(model, at + new Vector3(0, 40, 30), at, cue, effects);
                // Along its way, short of landing (which would shake the camera): its trail drawn.
                for (var step = 0; step < 4; step++)
                    prop.Update(null!, cue.At + Mathf.Max(cue.Number(4), 0.01f) * step / 4);
                if (cue.Number("land", 0) is > 0 and var land)
                {
                    effects.Explosion(at, land);
                    effects.KickUp(at, new Vector2(land * 3, land * 3));
                    effects.DustBurst(at, land * 18, new Color(0.62f, 0.47f, 0.32f));
                    effects.Scorch(at, land * 2);
                }
            }
        }
    }
}
