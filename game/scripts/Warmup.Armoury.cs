using Machines.Import.Model;

namespace Machines.Game;

// Package W's warm-up (plan stufe7 §3): the heavy Grunt that bursts gate N in batch 5 (Old Iron) is put up once behind the
// loading screen where the camera looks (HeavyGruntRig.Warm), so its meshes and materials are on the card before it rolls in.
public partial class Warmup
{
    /// <summary>This package's things to warm up, a step each (Warmup.Steps runs them in a story).</summary>
    private System.Collections.Generic.IEnumerable<bool> WarmArmoury()
    {
        if (_world.Story is not { } run || run.Section > 6)
            yield break;
        if (HeavyGruntRig.Warm(_assets, Game.Look(run.Options.Ship)) is { } heavy)
        {
            heavy.Name = "WarmHeavyGrunt";
            heavy.Position = _focus;
            AddChild(heavy);
        }
        yield return false;
    }
}
