using System.Numerics;
using Machines.Sim.Story;

namespace Machines.Sim.World;

// Stage 7's Use targets of package E (section 7: the hand wheel, the lane lever, the chocks), in the registry of
// GameWorld.Companions (plan stufe7 C9). The valve needs no Use: standing on it is enough (GameWorld.StoryHazards).
public sealed partial class GameWorld
{
    /// <summary>The hold's chocks by their prop tags (deck PROPS chock_1..3) and the lane each stops.</summary>
    private static readonly (string Name, int Tag, int Lane)[] Chocks = [("chock_1", 337, 1), ("chock_2", 338, 2), ("chock_3", 339, 3)];

    /// <summary>How near a Grunt must be to a chock to push it into its lane, metres (it stands in a notch beside it).</summary>
    private const float ChockReach = 5;

    /// <summary>Adds what a hero of <paramref name="seat"/> could use here of this section, with its kind and distance.</summary>
    private void StoryUseTargetsEscape(StoryRun run, int seat, Machine hero, List<StoryUseOption> near)
    {
        if (!run.Flags.Contains(StoryMarks.Escape))
            return;
        if (seat > 0)
        {
            foreach (var (name, tag, _) in Chocks)
            {
                if (Artefacts.FirstOrDefault(a => a.Tag == tag && !a.IsDead) is { } chock
                    && Vector2.Distance(chock.Position, hero.Position) is var d && d <= ChockReach + hero.Radius)
                    near.Add(new(name, StoryUseKind.Device, d));
            }
            return;
        }
        foreach (var name in (string[])["wheel_j4", "lever_f"])
        {
            if (Deck?.Markers.FirstOrDefault(m => m.Name == name) is { } marker
                && Vector2.Distance(marker.Position, hero.Position) is var d && d <= UseReach + hero.Radius)
                near.Add(new(name, StoryUseKind.Device, d));
        }
    }

    /// <summary>Uses a target of this section; <see cref="NotMine"/> for one it does not know, null when done, else why not.</summary>
    private string? StoryUseEscape(StoryRun run, PlayerId player, int seat, Machine hero, string target)
    {
        if (target is "wheel_j4" or "lever_f")
        {
            if (seat != 0)
                return "only the Locator reaches that";
            if (Deck?.Markers.FirstOrDefault(m => m.Name == target) is not { } marker)
                return NotMine;
            if (Vector2.Distance(marker.Position, hero.Position) > UseReach + hero.Radius)
                return "too far away";
            // Held: the wheel turns in 2 s, the lever in 1 s (GameWorld.StoryHazards, UpdateEscapeDevices).
            run.Crew.Holding[player.Value] = target;
            HoldStill(hero.Id);
            return null;
        }
        foreach (var (name, tag, lane) in Chocks)
        {
            if (target != name)
                continue;
            if (seat == 0)
                return "only a Grunt pushes the chock";
            if (Artefacts.FirstOrDefault(a => a.Tag == tag && !a.IsDead) is not { } chock)
                return "that is done with";
            if (Vector2.Distance(chock.Position, hero.Position) > ChockReach + hero.Radius)
                return "too far away";
            PushChock(run, chock, lane);
            return null;
        }
        return NotMine;
    }

    /// <summary>A chock pushed into its lane: the lane's container stops at it for <see cref="StoryHazards.ChockCycles"/> rolls.</summary>
    internal void PushChock(StoryRun run, Artefact chock, int lane)
    {
        RemoveArtefact(chock.Id);
        run.Hazards.Chocked[lane] = StoryHazards.ChockCycles;
        foreach (var rail in run.Hazards.Rails.Where(r => r.Lane == lane && r.State != RailState.Rest))
            (rail.State, rail.Speed, rail.Way) = (RailState.Rest, 0, 0);
        StoryEvents.Cue(this, $"chock {lane}", chock.Position);
    }
}
