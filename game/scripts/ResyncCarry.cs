using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// What a player had in hand when the relay rebuilt their world from its log (P2 design §5.4: a resync after going out of step,
/// or a rejoin after a gap): kept over the reload of the match scene, for the rebuilt one to take back once it is warmed up. The
/// camera and its four kept places, the selection, the machine driven in first person, the chat's lines and the fog's
/// uncovered ground. Ids stay good, the rebuilt world being the same match.
/// </summary>
public static class ResyncCarry
{
    /// <summary>What was kept, and for which player: another player's scene (a late join) takes none of it.</summary>
    public sealed record Kept(PlayerId Player, CameraRig.View Camera, CameraRig.View?[] Views, IReadOnlyList<int> Selection, int? FirstPerson,
        IReadOnlyList<string> Chat, FogOfWar.Explored? Fog);

    private static Kept? _kept;

    /// <summary>Keeps what the scene about to be left had in hand.</summary>
    public static void Keep(Kept kept) => _kept = kept;

    /// <summary>What was kept for <paramref name="player"/>, once: null when nothing was, or it was another player's.</summary>
    public static Kept? Take(PlayerId player)
    {
        var kept = _kept;
        _kept = null;
        return kept?.Player == player ? kept : null;
    }
}
