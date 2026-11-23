using Godot;
using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// The look of stage 7's big Grunt (<see cref="Prologue.HeavyGrunt"/>, Sim's StoryHeavy): Old Iron, Wired's Grunt 5, drawn 1.5
/// times a Grunt 3 with its dozer blade and the Grunt 3's twin heavy Bolters. tools/wired-models built Old Iron from the
/// Grunt 3's export with those very guns (<c>classic/wired/models/&lt;colour&gt;/AGGRESSOR_5_GRUNT_LR_HEAVY_BOLTER1_X2</c>),
/// so its loadout is the model as made: the rig <see cref="ClassicAssets.RigFor"/> gives for its id, the same one every
/// machine's view takes by its name (ActorView.ModelOf), kept once per look. Nothing has to be put together as
/// <see cref="HeroRig"/> does for a hero; were the guns ever to change, <see cref="Compose"/> is the one place to do it.
/// The boss's states (revving, stunned) are the armoury's (package W). The story is the remake's own.
/// </summary>
public static class HeavyGruntRig
{
    /// <summary>Its model's id: type, level, subtype and weapons, as the view names a machine's model.</summary>
    public static readonly string ModelId =
        $"{Prologue.HeavyGrunt.Type}_{Prologue.HeavyGrunt.Level}_{Prologue.HeavyGrunt.SubType}_{Prologue.HeavyGrunt.Weapons}";

    /// <summary>Its rig in the colours of <paramref name="look"/>, or null where Wired's models have not been built.</summary>
    public static Rig? Compose(ClassicAssets assets, Race look) => assets.RigFor(ModelId, look);

    /// <summary>
    /// Loads its rig behind the loading screen and gives a model of it to put up out of sight for a frame, so its meshes and
    /// materials are on the card before it bursts in (a warm-up step of the armoury's and the escape's, Warmup.Armoury.cs and
    /// Warmup.Escape.cs). Null without the model.
    /// </summary>
    public static Node3D? Warm(ClassicAssets assets, Race look)
    {
        if (Compose(assets, look) is not { } rig)
        {
            GD.PushWarning($"warm-up: no model for the big Grunt ({ModelId}); build Wired's models (tools/wired-models)");
            return null;
        }
        return RigView.Create(rig);
    }
}
