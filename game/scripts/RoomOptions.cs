using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The rules of a multiplayer room's settings that the room pages follow, free of Godot so the tests can compile them: a
/// room's options are changed from those it has, so that what the settings page does not show (a Wired run's or a battle's
/// own settings, the rules a recording keeps) comes through a change unharmed; and which hero a battle room takes, in the
/// relay's words. Battle is the remake's own addition.
/// </summary>
public static class RoomOptions
{
    /// <summary>
    /// A battle room made of <paramref name="kept"/>: the bracket topped by <paramref name="tier"/>, the lives and the victory
    /// chosen, everything else of an earlier battle (its wait for a body, its time) kept. A battle is fought without fog, in
    /// first person, with fixed teams and nothing of Wired.
    /// </summary>
    public static MatchOptions Battle(MatchOptions kept, int tier, int lives, BattleWin win) => kept with
    {
        Victory = VictoryCondition.Battle,
        FogOfWar = false,
        BroadcastAlliances = false,
        DisableFirstPerson = false,
        Wired = null,
        Battle = (kept.Battle ?? new BattleOptions()) with
        {
            Tier = Math.Clamp(tier, 1, BattleCatalogue.MostTier),
            Lives = Math.Clamp(lives, 1, 5),
            Win = Enum.IsDefined(win) ? win : BattleWin.Both,
        },
    };

    /// <summary>
    /// A normal room made of <paramref name="kept"/>: the settings the page shows from <paramref name="chosen"/>, everything
    /// else (a Wired run, a campaign's carry, the rules a recording keeps) as it was; it is no battle any more.
    /// </summary>
    public static MatchOptions Normal(MatchOptions kept, MatchOptions chosen) => kept with
    {
        Battle = null,
        StartingBmu = chosen.StartingBmu,
        Resources = chosen.Resources,
        TechLevel = chosen.TechLevel,
        Victory = chosen.Victory,
        TimeLimit = chosen.TimeLimit,
        AiLevel = chosen.AiLevel,
        FogOfWar = chosen.FogOfWar,
        BroadcastAlliances = chosen.BroadcastAlliances,
        DisableFirstPerson = chosen.DisableFirstPerson,
    };

    /// <returns>
    /// Why a hero may not play in a battle room whose bracket is topped by <paramref name="tier"/>, in the relay's words (so
    /// the page refuses it before the relay would), or null.
    /// </returns>
    public static string? WhyNotHere(BattleHero hero, int tier) =>
        !BattleCatalogue.InBracket(hero.Chassis, tier) ? "that hero is not in the room's bracket"
        : BattleBots.WhyNot(hero, tier) is not null ? "that hero does not fit together"
        : null;
}
