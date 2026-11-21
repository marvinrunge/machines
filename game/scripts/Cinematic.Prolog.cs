namespace Machines.Game;

// The story's cutscenes as films (MACHINES_CINEMATIC=prolog-<name>, tools/cinematic/films.txt): the same script a match
// plays live (data/planets/prolog/<name>.cut, see CutsceneScript), filmed by this camera to be rendered (shoot.sh).
public partial class Cinematic
{
    /// <summary>The part of the cutscene's script for this map: on a story deck its deck part, elsewhere its outside part.</summary>
    private Shot[] Scripted(string name)
    {
        var path = ScriptPath(name) ?? throw new InvalidOperationException($"There is no cutscene '{name}' (data/planets/*/{name}.cut).");
        var script = CutsceneScript.Load(path);
        var part = script.Part(_world.Deck is not null)
            ?? throw new InvalidOperationException($"The cutscene '{name}' has no part to be played {(_world.Deck is not null ? "on a deck" : "outside")}.");
        // Its ship burns through the sky kilometres away.
        _camera.Far = 9000;
        return Takes(part);
    }
}
