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
/// A hero of a battle on the HUD's team plate: whose it is and the race (colour) it fights for, its number in a team of
/// several heroes of one race (none, 0, where every race has one hero, as in today's layout), its name, the name of the person
/// who plays it (null for the computer's), chassis and tier, what is left of it, the lives it began with and has left (the
/// body it stands in or is waiting for counted), the seconds until it is back while it is down, whether it is out for good,
/// whether a bot drives it and whether its person has left the match.
/// </summary>
public sealed record BattleHeroReadout(PlayerId Player, Race Race, int Number, string Name, string? PlayerName, string Chassis, int Tier, bool Bot,
    bool You, float Health, float Armour, int Lives, int LivesLeft, bool Standing, float? BackIn, bool Out, int Kills, bool Away = false)
{
    /// <summary>The hero as the marks over it and the team list name it: "3 · Torque" with a number, else its name alone.</summary>
    public string Label => BattleReadout.Label(Number, Name);
}

/// <summary>
/// A team of a battle: its heroes in their places, and how far it has taken the other team's pod in points of the
/// <paramref name="Target"/> that takes it (<see cref="BattleRun.CaptureTarget"/>), with whether one of its heroes stands in
/// that circle now.
/// </summary>
public sealed record BattleTeamReadout(int Team, bool Ours, IReadOnlyList<BattleHeroReadout> Heroes, float Progress, bool Capturing, bool PodStands, float Target)
{
    /// <summary>Whether the team has a hero standing or one to come.</summary>
    public bool InTheFight => Heroes.Any(h => !h.Out);

    /// <summary>The share of the other team's pod taken, 0 to 1.</summary>
    public float Taken => Target <= 0 ? 0 : Math.Clamp(Progress / Target, 0, 1);

    /// <summary>The share taken in whole per cent, as the capture bar writes it: 250 points of 250 are 100, 37 of 100 are 37.</summary>
    public int Percent => (int)MathF.Floor(Progress * 100 / MathF.Max(Target, 1) + 1e-3f);
}

/// <summary>
/// What the HUD of a battle reads off the world at one moment, for one player, as plain figures: the clock and the opening,
/// both teams, the capture of both pods, the player's own hero (its lives, the wait for its next body, whether it is out), its
/// abilities, thrusters and the special of its chassis, and how the battle ended. Free of Godot so that a test can hold each
/// figure against the simulation. Battle is the remake's own addition.
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

    /// <summary>The player's team first, the other second.</summary>
    public required BattleTeamReadout Ours { get; init; }

    public required BattleTeamReadout Theirs { get; init; }

    /// <summary>The points that take a pod in this battle (<see cref="BattleRun.CaptureTarget"/>): 100 in today's layout, more for bigger teams.</summary>
    public required float CaptureTarget { get; init; }

    /// <summary>How the battle ended for the player's team; null while it lasts.</summary>
    public required BattleOutcome? Outcome { get; init; }

    public required BattleEnd? Reason { get; init; }

    /// <summary>The player's own hero, as its team's plate has it.</summary>
    public required BattleHeroReadout You { get; init; }

    /// <summary>The seconds until the next body stands at the pod, while the hero is down and one is coming.</summary>
    public float? DownSeconds => You.BackIn;

    /// <summary>The hero fell for the last time: the player fights no more and looks on.</summary>
    public bool Out => You.Out;

    /// <summary>The team-mates standing, in their places, that a player out of the fight can look on through.</summary>
    public required IReadOnlyList<PlayerId> Watchable { get; init; }

    public required IReadOnlyList<AbilityReadout> Abilities { get; init; }

    public required DashReadout? Dash { get; init; }

    /// <summary>What the special of the hero's chassis shows, as the world has it; null for a chassis without one.</summary>
    public required WiredSpecialView? SpecialView { get; init; }

    /// <summary>The hero's distance from its own pod and from the enemy's, in metres; null without either.</summary>
    public required float? PodDistance { get; init; }

    public required float? EnemyPodDistance { get; init; }

    /// <summary>The figures for a player of a battle; null in any other match or for a player in neither team.</summary>
    public static BattleReadout? Of(GameWorld world, PlayerId player)
    {
        if (world.Battle is not { } run || world.Wired is not { } wired)
            return null;
        var own = run.TeamOf(player);
        if (own < 0)
            return null;
        var now = world.Time;
        var teams = new[] { Team(world, run, 0, own, player), Team(world, run, 1, own, player) };
        var you = teams[own].Heroes.First(h => h.Player == player);
        var state = wired.Heroes.GetValueOrDefault(player);
        var hero = world.HeroOf(player);
        var (pod, enemyPod) = (world.BattlePod(own), world.BattlePod(1 - own));
        var outcome = run.Result is null ? (BattleOutcome?)null : run.Won(player) ? BattleOutcome.Won : run.Lost(player) ? BattleOutcome.Lost : BattleOutcome.Draw;
        return new BattleReadout
        {
            Seconds = (int)MathF.Ceiling(run.TimeLeft - 1e-4f),
            TimeLimit = run.Options.TimeLimit,
            Opening = run.InOpening(now) && !run.Over ? (int)MathF.Ceiling(run.OpeningLeft(now) - 1e-4f) : null,
            Capture = run.Options.Win != BattleWin.Wipe,
            Ours = teams[own],
            Theirs = teams[1 - own],
            CaptureTarget = run.CaptureTarget,
            Outcome = outcome,
            Reason = run.Result?.Reason,
            You = you,
            Watchable = teams[own].Heroes.Where(h => h.Standing && h.Player != player).Select(h => h.Player).ToList(),
            Abilities = state is null ? [] : SlotsOf(state, now),
            Dash = state is not null && state.Build(world.Data).DashCooldown is { } cooldown
                ? new DashReadout(MathF.Max(0, state.DashReadyAt - now), 1 - Share(state.DashReadyAt - now, cooldown), now < state.DashUntil)
                : null,
            SpecialView = state is null ? null : world.SpecialView(state),
            PodDistance = hero is not null && pod is not null ? hero.DistanceTo(pod) : null,
            EnemyPodDistance = hero is not null && enemyPod is not null ? hero.DistanceTo(enemyPod) : null,
        };
    }

    /// <summary>A team as its players stand in it (<see cref="BattleRun.Members"/>): in today's layout its races in race order.</summary>
    private static BattleTeamReadout Team(GameWorld world, BattleRun run, int team, int own, PlayerId viewer)
    {
        var heroes = run.Members[team].Select(player => Hero(world, run, player, viewer)).ToList();
        var capturing = run.Members[team].Any(run.Shares.ContainsKey);
        return new BattleTeamReadout(team, team == own, heroes, run.Progress(team), capturing, world.BattlePod(team) is not null, run.CaptureTarget);
    }

    private static BattleHeroReadout Hero(GameWorld world, BattleRun run, PlayerId player, PlayerId viewer)
    {
        var sheet = SheetOf(run, player);
        var race = sheet?.Race ?? world.Players.RaceOf(player);
        var state = world.Wired!.Heroes.GetValueOrDefault(player);
        var hero = world.HeroOf(player);
        var tally = run.Tally.GetValueOrDefault(player);
        var chassis = tally?.Chassis ?? sheet?.Chassis ?? "";
        var coming = state?.RespawnAt is not null;
        var left = (hero is not null || coming ? 1 : 0) + (state?.SpareBodies ?? 0);
        var row = world.Players.Contains(player) ? world.Players[player] : null;
        return new BattleHeroReadout(player, race, NumberOf(world, player), HeroName(run, player), row is { Kind: PlayerKind.Human } ? row.Name : null,
            chassis, BattleCatalogue.Tier(chassis), run.Bots.Contains(player), player == viewer,
            hero is null ? 0 : Share(hero.HitPoints, hero.MaxHitPoints), hero is null ? 0 : Share(hero.Armour, hero.MaxArmour),
            run.Options.Lives, left, hero is not null, coming ? MathF.Max(0, state!.RespawnAt!.Value - world.Time) : null,
            hero is null && !coming, tally?.Kills ?? 0, world.Players.Away(player));
    }

    /// <summary>
    /// The number a player's hero carries where its race has several (a team of 3 to 5 heroes of one colour): its place in the
    /// race, from 1; 0 where the race has one player, so today's layout shows none.
    /// </summary>
    public static int NumberOf(GameWorld world, PlayerId player) =>
        world.Players.Contains(player) && world.Players.Shared(world.Players.RaceOf(player)) ? world.Players.PlaceInRace(player) + 1 : 0;

    /// <summary>A hero as the marks and lists name it: "3 · Torque" with a number, the name alone without.</summary>
    public static string Label(int number, string name) => number > 0 ? $"{number} · {name}" : name;

    /// <summary>The sheet a player's hero was made from: the one naming the player, or in today's layout the one of its race.</summary>
    public static BattleHero? SheetOf(BattleRun run, PlayerId player) =>
        run.Options.Heroes.FirstOrDefault(h => (h.Player ?? PlayerId.Of(h.Race)) == player);

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
    public static string HeroName(BattleRun run, PlayerId player) => SheetOf(run, player) is { } sheet
        ? sheet.Name is { Length: > 0 } name ? name : sheet.Race.ToString()
        : player.Value is >= 0 and < 4 ? ((Race)player.Value).ToString() : player.ToString();
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
