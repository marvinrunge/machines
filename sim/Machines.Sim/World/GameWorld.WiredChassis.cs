using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// What the HUD and the readouts show of the special of a hero's chassis (<see cref="GameWorld.SpecialView"/>), the
/// remake's own addition.
/// </summary>
/// <param name="Name">What it is called, in English.</param>
/// <param name="Phase">Where it stands, for one that goes through phases (an anchor); null otherwise.</param>
/// <param name="SecondsLeft">The seconds until it is ready again or its phase is over; 0 when there is no wait.</param>
/// <param name="Share">How far that wait or the work under way has come, from 0 to 1.</param>
/// <param name="Count">What it has of what it counts: mines carried, towers standing, enemies located.</param>
/// <param name="Most">The most of that.</param>
/// <param name="Ready">Whether its key would work now.</param>
/// <param name="Why">Why not, when it would not.</param>
public sealed record WiredSpecialView(ChassisSpecial Special, string Name, string? Phase, float SecondsLeft, float Share, int Count, int Most, bool Ready, string? Why);

/// <summary>
/// The specials of the chassis Wired's hero can wear, the remake's own addition: the special command, what the HUD reads
/// of it, and what the hangar asks of the chassis. Each special lives in a file of its own: the bodies' in
/// GameWorld.WiredBodies, the towers in GameWorld.WiredTowers, the mines in GameWorld.WiredMines, the Phantom's
/// shadow in GameWorld.WiredShadow and the geo locators' pulse in GameWorld.WiredLocate. Nothing here runs without
/// <see cref="Wired"/>.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The special of the chassis a hero wears.</summary>
    private ChassisSpecial WornSpecial(WiredState state) => state.Build(Data).Chassis.Special;

    /// <returns>
    /// null if the hero can use the special of its chassis now, <paramref name="on"/> saying what is wanted of one that
    /// switches (a Colossus' anchor), otherwise why not. The HUD asks the same.
    /// </returns>
    public string? WhyNoSpecial(WiredState state, bool on = true)
    {
        if (HeroOf(state.Race) is not { } hero)
            return "the hero is down";
        if (hero.Inside is not null)
            return "not from inside the pod";
        if (Battle is { } battle)
        {
            if (battle.InOpening(Time))
                return "the battle has not begun yet";
            if (battle.Over)
                return "the battle is over";
        }
        var special = WornSpecial(state);
        if ((special & ChassisSpecial.Anchor) != 0)
            return WhyNoAnchor(state, on);
        if ((special & ChassisSpecial.Field) != 0)
            return WhyNoRally(state);
        if ((special & ChassisSpecial.Locate) != 0)
            return WhyNoLocate(state);
        if ((special & ChassisSpecial.Mines) != 0)
            return WhyNoMine(state);
        return "this chassis has nothing to use";
    }

    /// <summary>
    /// The special command: a Colossus anchors or is released, a Marshal rallies its squad, a geo locator sends out its
    /// pulse, a spy lays a mine.
    /// </summary>
    private string? UseSpecial(WiredState state, bool on)
    {
        if (WhyNoSpecial(state, on) is { } why)
            return why;
        var special = WornSpecial(state);
        if ((special & ChassisSpecial.Anchor) != 0)
            return Anchor(state, on);
        if ((special & ChassisSpecial.Field) != 0)
            return Rally(state);
        if ((special & ChassisSpecial.Locate) != 0)
            return Locate(state);
        return (special & ChassisSpecial.Mines) != 0 ? LayMine(state) : "this chassis has nothing to use";
    }

    /// <summary>What the HUD shows of the special of a race's hero's chassis; null for a chassis without one, or outside a Wired match.</summary>
    public WiredSpecialView? SpecialView(Race race)
    {
        if (Wired?.Heroes.GetValueOrDefault(race) is not { } state)
            return null;
        var special = WornSpecial(state);
        if ((special & ChassisSpecial.Towers) != 0)
            return TowerView(state);
        if ((special & ChassisSpecial.Mines) != 0)
            return MineView(state);
        if ((special & ChassisSpecial.Locate) != 0)
            return LocateView(state);
        return special == ChassisSpecial.None ? null : BodyView(state);
    }

    /// <summary>The escorts a hero may have now: by the wave and the chips, and on top of that what a Marshal adds.</summary>
    public int SquadLimit(WiredState state) => WiredBalance.SquadLimit(Wired!.ShopWave, WiredChips.SquadExtra(state),
        (WornSpecial(state) & ChassisSpecial.Field) != 0 ? WiredBalance.FieldSquad : 0);

    /// <summary>Whether a machine is a hero that wears a chassis with the Phantom's shadow.</summary>
    private bool IsShadowChassis(Actor target) =>
        target is Machine && Wired is { } run && run.Heroes.Values.FirstOrDefault(s => s.Hero == target.Id) is { } state
        && (WornSpecial(state) & ChassisSpecial.Shadow) != 0;

    /// <summary>
    /// A hero gives itself away: the shadow of a chassis that has one starts over. Any other hero has no shadow to lose and
    /// keeps <see cref="WiredState.RevealedAt"/> at its start, so that its fingerprint stays as it was.
    /// </summary>
    private void Reveal(WiredState state)
    {
        if ((WornSpecial(state) & ChassisSpecial.Shadow) != 0)
            state.RevealedAt = Time;
    }

    /// <summary>Whether a hero of this build carries mines to lay: on a chassis of the spy line.</summary>
    private static bool MineCarrier(HeroBuild build) => (build.Chassis.Special & ChassisSpecial.Mines) != 0;
}
