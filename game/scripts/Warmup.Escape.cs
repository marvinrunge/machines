using Godot;

namespace Machines.Game;

// Package E's warm-up (plan stufe7 §3, §4 E): what the escape shows first is drawn once behind the loading screen where the
// camera looks, so its pipelines are on the card before they come into view: the heavy Grunts of the pack (HeavyGruntRig.Warm)
// and the escape's own pieces of DeckView.Escape (a container, the lanes' strips, a burning jet with its steam, the shutter,
// a bridge segment over the shaft's glow, the fire face).
public partial class Warmup
{
    /// <summary>This package's things to warm up, a step each (Warmup.Steps runs them in a story).</summary>
    private System.Collections.Generic.IEnumerable<bool> WarmEscape()
    {
        if (_world.Story is not { } run || run.Section != 7)
            yield break;
        if (HeavyGruntRig.Warm(_assets, Game.Look(run.Options.Ship)) is { } heavy)
        {
            heavy.Name = "WarmHeavyGruntEscape";
            heavy.Position = _focus;
            AddChild(heavy);
        }
        yield return false;
        var pieces = new Node3D { Name = "WarmEscape", Position = _focus };
        AddChild(pieces);
        var i = 0;
        foreach (var piece in DeckView.EscapePieces())
        {
            // In a row before the camera's focus, a few metres apart.
            piece.Position = new Vector3((i++ - 4) * 3, 0, 2);
            pieces.AddChild(piece);
        }
        yield return false;
    }
}
