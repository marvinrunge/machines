using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Package W's door pointers (design-waves §8.1, "an edge arrow to the next wave's door or doors, the same widget as the squad
// pointer of section 3"): while the armoury is sealed, each door the next batch comes by is marked where it lies, or pointed
// to by an arrow at the edge of the view when it is not in sight, amber in the pause (beating once the bar is past 70 %), red
// for a door that units of the fighting batch are still to come through. The way out after the room unseals is the
// objective's (StoryHud.DrawWay).
public partial class StoryHud
{
    /// <summary>The bar's share from which the next batch's doors beat.</summary>
    private const float DoorsBeatFrom = 0.7f;

    private void DrawBatchDoors(StoryReadout now, GameWorld world, StoryRun run)
    {
        var waves = run.Waves;
        if (now.Ending is not null || !run.Flags.Contains(StoryMarks.ArmourySealed) || waves.Wave < 1 || waves.Wave > StoryWaves.Plan.Count
            || world.Deck is not { } deck || GetViewport().GetCamera3D() is not { } camera || world.HeroOf(Game.Me) is not { } hero)
            return;
        var (doors, colour, strong) = waves.Fighting
            ? (waves.Due.Select(u => u.Door).Distinct(), HudInk.Danger, false)
            : (StoryWaves.DoorsOf(waves.Wave).AsEnumerable(), HudInk.Warning, waves.Bar(world.Time) >= DoorsBeatFrom);
        foreach (var door in doors)
        {
            if (DeckDoorSpots.Middle(deck, door) is not { } middle)
                continue;
            var metres = Mathf.RoundToInt(NVector2.Distance(middle, hero.Position));
            Pointer(camera, ActorView.World(middle) + Vector3.Up * 4, colour, $"{DoorName(door)}  {metres} m", strong);
        }
    }

    /// <summary>A door of the armoury as the HUD names it: R the west door, T the east door, N the freight gate.</summary>
    private static string DoorName(char door) => Lang.T(door switch { 'R' => "WEST DOOR", 'T' => "EAST DOOR", _ => "FREIGHT GATE" });
}
