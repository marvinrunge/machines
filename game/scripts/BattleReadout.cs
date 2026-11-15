using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>How a battle ended for one race: won with its team, lost, or a draw.</summary>
public enum BattleOutcome
{
    Won,
    Lost,
    Draw,
}

/// <summary>
/// A hero of a battle on the HUD's team plate: its name, chassis and tier, what is left of it, the lives it began with and has
/// left (the body it stands in or is waiting for counted), the seconds until it is back while it is down, and whether it is out
/// for good.
/// </summary>
public sealed record BattleHeroReadout(Race Race, string Name, string Chassis, int Tier, bool Bot, bool You, float Health, float Armour,
    int Lives, int LivesLeft, bool Standing, float? BackIn, bool Out, int Kills);

/// <summary>
/// A team of a battle: its heroes in race order, and how far it has taken the other team's pod, 0 to 100, with whether one
/// of its heroes stands in that circle now.
/// </summary>
public sealed record BattleTeamReadout(int Team, bool Ours, IReadOnlyList<BattleHeroReadout> Heroes, float Progress, bool Capturing, bool PodStands)
{
    /// <summary>Whether the team has a hero standing or one to come.</summary>
    public bool InTheFight => Heroes.Any(h => !h.Out);
}

/// <summary>
/// What the HUD of a battle reads off the world at one moment, for one race, as plain figures: the clock and the opening, both
/// teams, the capture of both pods, the race's own hero (its lives, the wait for its next body, whether it is out), its
/// abilities and thrusters, and how the battle ended. Free of Godot so that a test can hold each figure against the
/// simulation. Battle is the remake's own addition.
/// </summary>
public sealed record BattleReadout
{
    /// <summary>Seconds left on the clock, whole and rounded up; the draw comes at 0.</summary>
    public required int Seconds { get; init; }

    /// <summary>The whole clock, for the share of it left.</summary>
    public required int TimeLimit { get; init; }

    /// <summary>Seconds left of the opening, whole and rounded up, while it lasts; null once the fight is on.</summary>
    public required int? Opening { get; init; }

    /// <summary>Whether a pod can be taken in this battle: not when only wiping out counts.</summary>
    public required bool Capture { get; init; }

    /// <summary>The race's team first, the other second.</summary>
    public required BattleTeamReadout Ours { get; init; }

    public required BattleTeamReadout Theirs { get; init; }

    /// <summary>How the battle ended for the race; null while it lasts.</summary>
    public required BattleOutcome? Outcome { get; init; }

    public required BattleEnd? Reason { get; init; }

    /// <summary>The race's own hero, as its team's plate has it.</summary>
    public required BattleHeroReadout You { get; init; }

    /// <summary>The seconds until the next body stands at the pod, while the hero is down and one is coming.</summary>
    public float? DownSeconds => You.BackIn;

    /// <summary>The hero fell for the last time: the race fights no more and looks on.</summary>
    public bool Out => You.Out;

    /// <summary>The team-mates standing, in race order, that a race out of the fight can look on through.</summary>
    public required IReadOnlyList<Race> Watchable { get; init; }

    public required IReadOnlyList<AbilityReadout> Abilities { get; init; }

    public required DashReadout? Dash { get; init; }

    /// <summary>The hero's distance from its own pod and from the enemy's, in metres; null without either.</summary>
    public required float? PodDistance { get; init; }

    public required float? EnemyPodDistance { get; init; }

    /// <summary>The figures for a race of a battle; null in any other match or for a race in neither team.</summary>
    public static BattleReadout? Of(GameWorld world, Race race)
    {
        if (world.Battle is not { } run || world.Wired is not { } wired)
            return null;
        var own = run.TeamOf(race);
        if (own < 0)
            return null;
        var now = world.Time;
        var teams = new[] { Team(world, run, 0, own, race), Team(world, run, 1, own, race) };
        var you = teams[own].Heroes.First(h => h.Race == race);
        var state = wired.Heroes.GetValueOrDefault(race);
        var hero = world.HeroOf(race);
        var (pod, enemyPod) = (world.BattlePod(own), world.BattlePod(1 - own));
        var outcome = run.Result is null ? (BattleOutcome?)null : run.Won(race) ? BattleOutcome.Won : run.Lost(race) ? BattleOutcome.Lost : BattleOutcome.Draw;
        return new BattleReadout
        {
            Seconds = (int)MathF.Ceiling(run.TimeLeft - 1e-4f),
            TimeLimit = run.Options.TimeLimit,
            Opening = run.InOpening(now) && !run.Over ? (int)MathF.Ceiling(run.OpeningLeft(now) - 1e-4f) : null,
            Capture = run.Options.Win != BattleWin.Wipe,
            Ours = teams[own],
            Theirs = teams[1 - own],
            Outcome = outcome,
            Reason = run.Result?.Reason,
            You = you,
            Watchable = teams[own].Heroes.Where(h => h.Standing && h.Race != race).Select(h => h.Race).ToList(),
            Abilities = state is null ? [] : SlotsOf(state, now),
            Dash = state is not null && state.Build(world.Data).DashCooldown is { } cooldown
                ? new DashReadout(MathF.Max(0, state.DashReadyAt - now), 1 - Share(state.DashReadyAt - now, cooldown), now < state.DashUntil)
                : null,
            PodDistance = hero is not null && pod is not null ? hero.DistanceTo(pod) : null,
            EnemyPodDistance = hero is not null && enemyPod is not null ? hero.DistanceTo(enemyPod) : null,
        };
    }

    private static BattleTeamReadout Team(GameWorld world, BattleRun run, int team, int own, Race viewer)
    {
        var heroes = run.Teams[team].Order().Select(race => Hero(world, run, race, viewer)).ToList();
        var capturing = run.Teams[team].Any(race => run.Shares.ContainsKey(race));
        return new BattleTeamReadout(team, team == own, heroes, run.Progress(team), capturing, world.BattlePod(team) is not null);
    }

    private static BattleHeroReadout Hero(GameWorld world, BattleRun run, Race race, Race viewer)
    {
        var sheet = run.Options.Heroes.FirstOrDefault(h => h.Race == race);
        var state = world.Wired!.Heroes.GetValueOrDefault(race);
        var hero = world.HeroOf(race);
        var chassis = run.Tally.GetValueOrDefault(race)?.Chassis ?? sheet?.Chassis ?? "";
        var coming = state?.RespawnAt is not null;
        var left = (hero is not null || coming ? 1 : 0) + (state?.SpareBodies ?? 0);
        return new BattleHeroReadout(race, HeroName(run, race), chassis, BattleCatalogue.Tier(chassis), run.Bots.Contains(race), race == viewer,
            hero is null ? 0 : Share(hero.HitPoints, hero.MaxHitPoints), hero is null ? 0 : Share(hero.Armour, hero.MaxArmour),
            run.Options.Lives, left, hero is not null, coming ? MathF.Max(0, state!.RespawnAt!.Value - world.Time) : null,
            hero is null && !coming, run.Tally.GetValueOrDefault(race)?.Kills ?? 0);
    }

    /// <summary>The filled ability slots; the ion call counts its cooldown in seconds in a battle, as there are no waves.</summary>
    private static List<AbilityReadout> SlotsOf(WiredState state, float now)
    {
        var slots = new List<AbilityReadout>();
        for (var slot = 0; slot < state.Loadout.Abilities.Count; slot++)
        {
            if (state.Loadout.Abilities[slot] is not { } item || WiredCatalogue.FindAbility(item.Catalogue) is not { } ability)
                continue;
            var seconds = state.AbilitySecondsLeft(slot, now);
            var cooldown = ability.CooldownWaves > 0 ? BattleBalance.IonCooldown : ability.Cooldown;
            slots.Add(new AbilityReadout(slot, item.Catalogue, seconds, 0, 1 - Share(seconds, cooldown)));
        }
        return slots;
    }

    private static float Share(float part, float whole) => whole <= 0 ? 0 : Math.Clamp(part / whole, 0, 1);

    /// <summary>A hero's name as the screens write it: its race's when it has none (an empty name is none).</summary>
    public static string HeroName(BattleRun run, Race race) =>
        run.Options.Heroes.FirstOrDefault(h => h.Race == race)?.Name is { Length: > 0 } name ? name : race.ToString();
}

/// <summary>
/// Which capture events the HUD calls out with the banner and its sound: every tenth taken, and a capture that begins after
/// the pod had been left alone for <see cref="Quiet"/> seconds, but not each restart from nothing when a capturer's share is
/// lost to a hit or a step out of the circle and comes again a tick later (it did seven times in fourteen seconds).
/// </summary>
public sealed class BattleCaptureCalls
{
    /// <summary>Seconds a team's capture must have stood at nothing for a new start to be called out again.</summary>
    public const float Quiet = 10;

    // When each team's capture was last seen above nothing, in the world's seconds.
    private readonly float?[] _active = new float?[2];

    /// <returns>Whether to call out a capture event of <paramref name="team"/> with its detail (the progress, "0" for a start) at <paramref name="now"/>.</returns>
    public bool Call(int team, string? detail, float now) =>
        team is 0 or 1 && (detail != "0" || _active[team] is not { } last || now - last >= Quiet);

    /// <summary>Notes each team's progress at <paramref name="now"/>; called after the events of that moment are called out.</summary>
    public void Seen(BattleRun run, float now)
    {
        for (var team = 0; team < 2; team++)
        {
            if (run.Progress(team) > 0)
                _active[team] = now;
        }
    }
}
