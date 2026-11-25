using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Sim.World;

// Stage 7's Use targets of package S (section 2: valves and noise sources to bang), in the registry of GameWorld.Companions
// (plan stufe7 C9): a Grunt within reach of a noise source of the deck (a marker tagged noise with loud= or far=) bangs it,
// while the story's stealth is on (design-stealth §3.5).
public sealed partial class GameWorld
{
    /// <summary>How near a Grunt's middle must be to a valve or noise source to bang it, metres beyond its own radius.</summary>
    public const float BangReach = 4;

    /// <summary>The noise sources a Grunt can bang (a valve's far end and a director's lure place are not).</summary>
    private static bool Bangable(DeckMarker marker) =>
        marker.Tags.Contains("noise") && marker.Tags.Any(t => t.StartsWith("far=", StringComparison.Ordinal) || t.StartsWith("loud=", StringComparison.Ordinal));

    /// <summary>The noise sources of the deck a Grunt may bang now (stealth on, the source not spent), for its seat's Use and the HUD.</summary>
    public IEnumerable<DeckMarker> StoryNoiseSources()
    {
        if (Story is not { } run || !run.Flags.Contains(StoryMarks.Stealth) || Deck is not { } deck)
            yield break;
        foreach (var marker in deck.Markers)
        {
            if (Bangable(marker) && !run.Stealth.Spent.Contains(marker.Name) && marker.Name != "lure_f")
                yield return marker;
        }
    }

    /// <summary>Adds what a hero of <paramref name="seat"/> could use here of this section, with its kind and distance.</summary>
    private void StoryUseTargetsStealth(StoryRun run, int seat, Machine hero, List<StoryUseOption> near)
    {
        if (seat < 1)
            return;
        foreach (var marker in StoryNoiseSources())
        {
            var distance = Vector2.Distance(marker.Position, hero.Position);
            if (distance <= BangReach + hero.Radius)
                near.Add(new(marker.Name, StoryUseKind.Device, distance));
        }
    }

    /// <summary>Uses a target of this section; <see cref="NotMine"/> for one it does not know, null when done, else why not.</summary>
    private string? StoryUseStealth(StoryRun run, PlayerId player, int seat, Machine hero, string target)
    {
        if (StoryNoiseSources().FirstOrDefault(m => m.Name == target) is not { } marker)
            return NotMine;
        if (seat < 1)
            return "only a Grunt bangs on it";
        if (Vector2.Distance(marker.Position, hero.Position) > BangReach + hero.Radius)
            return "too far away";
        return StoryBang(target, hero.Id);
    }
}
