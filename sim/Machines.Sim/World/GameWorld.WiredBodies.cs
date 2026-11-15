using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The specials of the bodies of Wired's own machines, the remake's own addition: Old Iron's plough and ram, the
/// Harvester's harvest, the Widow's lurk and the Colossus' anchor, for every machine of those levels in a Wired match;
/// and the Marshal's field and rally, the hero's. Not built yet: each answers as if there were none.
/// </summary>
public sealed partial class GameWorld
{
    private const string BodiesNotBuilt = "not built yet";

    /// <summary>A tick of the bodies, before the heroes': what stands still, what anchors, what rams.</summary>
    private void UpdateBodies(WiredRun run)
    {
    }

    /// <summary>The share of a hit a body takes from <paramref name="dealer"/>: exactly 1 for any body without a special of it.</summary>
    private float BodyShare(Actor target, Actor? dealer) => 1;

    /// <summary>A hit has landed: what a body's special makes of it, <paramref name="before"/> being the target's hit points before.</summary>
    private void AfterHit(Actor target, int before, Actor? dealer)
    {
    }

    /// <summary>Whether a machine's tracks are locked, so that it goes nowhere.</summary>
    private bool Rooted(Machine machine) => false;

    /// <summary>What a rooted machine without a driver does instead of its order: it turns to face what it fights.</summary>
    private void HoldRooted(Machine machine)
    {
    }

    /// <summary>The accuracy a Marshal hero near a machine gives it.</summary>
    private float FieldAim(Machine machine) => 0;

    /// <summary>The share of its repair a Marshal hero near a machine adds to it.</summary>
    private float FieldRepair(Machine machine) => 0;

    /// <returns>null if the hero's Colossus can anchor (<paramref name="on"/>) or be released now, otherwise why not.</returns>
    public string? WhyNoAnchor(WiredState state, bool on) => BodiesNotBuilt;

    private string? Anchor(WiredState state, bool on) => BodiesNotBuilt;

    /// <returns>null if the hero's Marshal can call its squad in now, otherwise why not.</returns>
    public string? WhyNoRally(WiredState state) => BodiesNotBuilt;

    private string? Rally(WiredState state) => BodiesNotBuilt;

    /// <summary>What the HUD shows of the special of the hero's body.</summary>
    private WiredSpecialView? BodyView(WiredState state) => null;

    /// <summary>Where a machine's anchor stands.</summary>
    public AnchorPhase AnchorPhaseOf(Machine machine) => AnchorPhase.Free;
}
