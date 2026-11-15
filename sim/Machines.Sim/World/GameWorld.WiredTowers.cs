using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The towers a constructor hero raises, the remake's own addition: the original's missile emplacements, placed, paid for,
/// built and mended by the hero itself, and pulled down for part of their price. Not built yet: no tower can be placed.
/// </summary>
public sealed partial class GameWorld
{
    private const string TowersNotBuilt = "no towers yet";

    /// <returns>null if the hero can place a tower of the catalogue there now, otherwise why not.</returns>
    public string? WhyNoTower(WiredState state, string tower, Vector2 position, int orientation) => TowersNotBuilt;

    private string? PlaceTower(WiredState state, WiredTowerCommand command) => TowersNotBuilt;

    /// <returns>null if the hero can pull down a tower of its race now, otherwise why not.</returns>
    public string? WhyNoScrap(WiredState state, int tower) => TowersNotBuilt;

    private string? ScrapTower(WiredState state, int tower) => TowersNotBuilt;

    /// <summary>A constructor hero builds on the nearest site of its race in reach, or mends a tower there.</summary>
    private void UpdateTowerWork(WiredState state, Machine hero)
    {
    }

    /// <summary>The towers a chassis builds, smallest first.</summary>
    public IReadOnlyList<WiredTower> TowersFor(WiredChassis chassis) => [];

    /// <summary>The towers of a race standing, finished or not, by id.</summary>
    public IReadOnlyList<Construction> TowersOf(Race race) => [];

    /// <summary>The most towers the hero's chassis lets stand at once.</summary>
    public int MostTowers(WiredState state) => 0;

    /// <summary>What the HUD shows of the towers.</summary>
    private WiredSpecialView? TowerView(WiredState state) => null;
}
