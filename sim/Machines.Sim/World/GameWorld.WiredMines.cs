using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The mines a spy hero lays, the remake's own addition: one at a time under the hero, from what its chassis carries,
/// refilled with every life, every wave and every change of chassis (in a battle with every life only). They are the
/// original's land mines once on the ground (GameWorld.UpdateLandMines): 80 points of damage to the first enemy on the
/// ground that comes near, gliders passed over. A race has no more than <see cref="WiredBalance.MostMines"/> on the
/// ground, the minefield ability's counted with the hero's own (<see cref="RoomForMines"/>).
/// </summary>
public sealed partial class GameWorld
{
    /// <returns>
    /// null if the hero can lay a mine now, otherwise why not: it is down, inside the pod, the battle has not begun or is
    /// over, the chassis carries none, none are left, the last one went down less than <see cref="WiredBalance.MineLayGap"/>
    /// ago, or the race has all the mines on the ground it may have. The HUD asks the same.
    /// </returns>
    public string? WhyNoMine(WiredState state)
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
        if (!MineCarrier(state.Build(Data)))
            return "this chassis has nothing to use";
        if (hero.Mines <= 0)
            return "no mines left";
        if (Time - state.MineLaidAt < WiredBalance.MineLayGap)
            return "that is not ready yet";
        return RoomForMines(state.Race, 1) ? null : $"{WiredBalance.MostMines} mines lie already";
    }

    /// <summary>
    /// Whether a race may put <paramref name="more"/> mines more on the ground: all of its mines count, those its hero laid
    /// one by one and those of its minefields alike, up to <see cref="WiredBalance.MostMines"/>.
    /// </summary>
    private bool RoomForMines(Race race, int more)
    {
        var lying = 0;
        foreach (var mine in LandMines)
        {
            if (mine.Race == race)
                lying++;
        }
        return lying + more <= WiredBalance.MostMines;
    }

    /// <summary>
    /// The special's key on a spy's chassis: a mine goes down where the hero stands, on the ground the pod's door stands
    /// on. Laying it does not give the hero away.
    /// </summary>
    private string? LayMine(WiredState state)
    {
        if (WhyNoMine(state) is { } why)
            return why;
        var hero = HeroOf(state.Race)!;
        var mine = new LandMineState(_nextMineId++, hero.Race, OnFreeGround(hero.Position));
        LandMines.Add(mine);
        hero.Mines--;
        state.MineLaidAt = Time;
        Raise(new GameEvent(GameEventKind.MineLaid, hero.Id, mine.Position, mine.Id));
        return null;
    }

    /// <summary>
    /// Fills the hero up with as many mines as its chassis carries, and empties one whose chassis carries none. A battle
    /// gives them with each life only, as the hero is set down (GameWorld.AddHero).
    /// </summary>
    private void RefillMines(WiredState state)
    {
        if (Battle is not null || HeroOf(state.Race) is not { } hero)
            return;
        var build = state.Build(Data);
        hero.Mines = MineCarrier(build) ? build.Stats.MineCapacity ?? 0 : 0;
    }

    /// <summary>
    /// What the HUD shows of the mines: those carried of the most the chassis carries, and the wait for the next. A
    /// Phantom's shows its shadow instead of that wait, which is short: hidden, or seen with the seconds until it is hidden
    /// again (<see cref="ShadowPhase"/>).
    /// </summary>
    private WiredSpecialView? MineView(WiredState state)
    {
        var build = state.Build(Data);
        var special = build.Chassis.Special;
        var (count, most) = (HeroOf(state.Race)?.Mines ?? 0, build.Stats.MineCapacity ?? 0);
        var why = WhyNoSpecial(state);
        if ((special & ChassisSpecial.Shadow) != 0)
        {
            var (phase, toHidden) = ShadowPhase(state);
            return new WiredSpecialView(special, "Mines", phase, toHidden, 1 - toHidden / WiredBalance.ShadowQuiet, count, most, why is null, why);
        }
        var wait = Math.Clamp(state.MineLaidAt + WiredBalance.MineLayGap - Time, 0, WiredBalance.MineLayGap);
        return new WiredSpecialView(special, "Mines", null, wait, 1 - wait / WiredBalance.MineLayGap, count, most, why is null, why);
    }
}
