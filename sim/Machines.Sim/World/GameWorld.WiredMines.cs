using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The mines a spy hero lays, the remake's own addition: one at a time under the hero, from what its chassis carries,
/// refilled with every life, every wave and every change of chassis. Not built yet: none can be laid.
/// </summary>
public sealed partial class GameWorld
{
    private const string MinesNotBuilt = "not built yet";

    /// <returns>null if the hero can lay a mine now, otherwise why not.</returns>
    public string? WhyNoMine(WiredState state) => MinesNotBuilt;

    private string? LayMine(WiredState state) => MinesNotBuilt;

    /// <summary>Fills the hero up with as many mines as its chassis carries.</summary>
    private void RefillMines(WiredState state)
    {
    }

    /// <summary>What the HUD shows of the mines, and of a Phantom's shadow.</summary>
    private WiredSpecialView? MineView(WiredState state) => null;
}
