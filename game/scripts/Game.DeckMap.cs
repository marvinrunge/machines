using System.Linq;
using Godot;

namespace Machines.Game;

// A plan of a ship deck from the game itself, for working out a chapter's route (user's wish 2026-11-24, "die echte Karte"):
// MACHINES_DECK_MAP=file.png,x0,y0,x1,y1[,pixels per metre[,cut]] looks straight down on that part of the deck (metres, x east,
// y north; north at the top) once the chapter has stood a few seconds, saves the picture and quits. An orthographic camera
// hangs high above with the ceilings hidden; with a cut it hangs at that height instead, everything above it left out and the walls cut
// through, as in a builder's plan; a plain light from above and every lamp group on light it evenly. Each pixel stands
// for 1/ppm metres from (x0, y1) at the top left.
public partial class Game
{
    private SubViewport? _deckMap;
    private int _deckMapFrames;
    private double _deckMapWait;

    private void TakeDeckMapIfAsked(double delta)
    {
        if (_deckView is null || System.Environment.GetEnvironmentVariable("MACHINES_DECK_MAP")?.Split(',') is not { Length: >= 5 } asked)
            return;
        if (_deckMap is null)
        {
            if ((_deckMapWait += delta) < 6)
                return;
            float N(int i, float or) => asked.Length > i ? float.Parse(asked[i], System.Globalization.CultureInfo.InvariantCulture) : or;
            var (x0, y0, x1, y1) = (N(1, 0), N(2, 0), N(3, 0), N(4, 0));
            var ppm = N(5, 8);
            // Without a cut the ceilings are hidden and the camera hangs high above, so tall stacks show as they stand.
            var cut = N(6, 0);
            if (cut <= 0)
            {
                cut = 60;
                // The ceiling's batches carry no name of their own (a second child of one name is renamed): found by their mesh.
                var slab = Props.Get("kit_ceiling")?.Mesh;
                var ceilings = _deckView.FindChildren("*", "MultiMeshInstance3D", true, false).OfType<MultiMeshInstance3D>()
                    .Where(m => slab is not null && m.Multimesh?.Mesh == slab).ToList();
                GD.Print($"deck map: {ceilings.Count} ceiling batches hidden");
                foreach (var ceiling in ceilings)
                    ceiling.Visible = false;
            }
            _deckMap = new SubViewport
            {
                Size = new Vector2I(Mathf.RoundToInt((x1 - x0) * ppm), Mathf.RoundToInt((y1 - y0) * ppm)),
                World3D = GetWorld3D(),
                Msaa3D = Viewport.Msaa.Msaa4X,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            };
            var camera = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Size = y1 - y0,
                Near = 0.01f,
                Far = cut + 5,
                Current = true,
            };
            if (GetWorld3D().Environment?.Duplicate() is Godot.Environment air)
            {
                air.FogEnabled = false;
                air.VolumetricFogEnabled = false;
                air.AmbientLightSource = Godot.Environment.AmbientSource.Color;
                air.AmbientLightColor = new Color(0.55f, 0.55f, 0.6f);
                air.AmbientLightEnergy = 1;
                camera.Environment = air;
            }
            _deckMap.AddChild(camera);
            AddChild(_deckMap);
            // Deck x east is the world's X, deck y north the world's Z: looking down with +Z at the top puts north up.
            var middle = new Vector3((x0 + x1) / 2, 0, (y0 + y1) / 2);
            camera.LookAtFromPosition(middle + Vector3.Up * cut, middle, Vector3.Back);
            var sun = new DirectionalLight3D { LightEnergy = 0.8f, ShadowEnabled = false };
            AddChild(sun);
            sun.LookAtFromPosition(middle + new Vector3(0.3f, 1, 0.5f), middle, Vector3.Back);
            foreach (var group in _deckView.LightGroups.ToList())
                _deckView.SetLights(group, "on");
            GD.Print($"deck map: {_deckMap.Size.X}x{_deckMap.Size.Y} px, x {x0}..{x1}, y {y0}..{y1}, {ppm} px/m, cut at {cut} m");
            return;
        }
        if (++_deckMapFrames < 12)
            return;
        var file = asked[0];
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
        // The camera looks down with north at the top, which leaves east on the left: mirrored back.
        var picture = _deckMap.GetTexture().GetImage();
        picture.FlipX();
        picture.SavePng(file);
        GD.Print($"deck map: saved {file}");
        GetTree().Quit();
    }
}
