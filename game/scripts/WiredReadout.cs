using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>What an escort is about, for its card on the squad strip.</summary>
public enum EscortDoing
{
    Following,
    Attacking,
    Holding,
    Moving,
    Idle,
}

/// <summary>A boss on the map: its health and armour, each 0 to 1.</summary>
public readonly record struct BossReadout(int Id, string Name, float Health, float Armour);

/// <summary>A filled ability slot: the seconds or waves still to wait, and how far the cooldown has come, 0 to 1.</summary>
public readonly record struct AbilityReadout(int Slot, string Catalogue, float SecondsLeft, int WavesLeft, float Share)
{
    public bool Ready => SecondsLeft <= 0 && WavesLeft <= 0;
}

/// <summary>The thrusters: the seconds still to wait, how far the cooldown has come, and whether they are firing now.</summary>
public readonly record struct DashReadout(float SecondsLeft, float Share, bool Firing)
{
    public bool Ready => SecondsLeft <= 0;
}

/// <summary>An escort on the squad strip.</summary>
public readonly record struct EscortReadout(int Id, string Model, int Level, float Health, EscortDoing Doing);

/// <summary>A chip on the board, lit while it is at work.</summary>
public readonly record struct ChipReadout(string Id, ChipFamily Family, bool Lit);

/// <summary>
/// What the HUD of a Wired run reads off the world at one moment, as plain figures, free of Godot so that a test can hold each
/// of them against the simulation. Wired is the remake's own addition.
/// </summary>
public sealed record WiredReadout
{
    /// <summary>The wave the HUD speaks of: the one under way, or else the one coming.</summary>
    public required int Wave { get; init; }

    public required bool BetweenWaves { get; init; }

    /// <summary>Seconds on the clock between waves, whole and rounded up; 0 once the wave is on its way.</summary>
    public required int Seconds { get; init; }

    /// <summary>The whole wait those seconds count down from, for the share of it left.</summary>
    public required int WaitLength { get; init; }

    /// <summary>The kind of the wave under way or, between waves, of the one coming.</summary>
    public required WiredSpecial Special { get; init; }

    /// <summary>The name of the boss of that wave, as the catalogue has it.</summary>
    public required string? BossName { get; init; }

    public required int EnemiesLeft { get; init; }

    /// <summary>The most enemies the wave under way has had at once, for the share of them left.</summary>
    public required int MostEnemies { get; init; }

    public required int Bank { get; init; }

    public required long Score { get; init; }

    /// <summary>What is left of the pod, 0 to 1; null once it is gone.</summary>
    public required float? Pod { get; init; }

    public required IReadOnlyList<BossReadout> Bosses { get; init; }

    public required int SpareBodies { get; init; }

    /// <summary>The spare bodies the run began with.</summary>
    public required int BodiesAtStart { get; init; }

    /// <summary>The seconds until the next body stands at the pod, while the hero is down and one is coming.</summary>
    public required float? DownSeconds { get; init; }

    /// <summary>The filled ability slots; the slots are <see cref="WiredBalance.AbilitySlots"/> however many are filled.</summary>
    public required IReadOnlyList<AbilityReadout> Abilities { get; init; }

    /// <summary>The thrusters, for a hero that wears them.</summary>
    public required DashReadout? Dash { get; init; }

    public required IReadOnlyList<EscortReadout> Squad { get; init; }

    public required int SquadLimit { get; init; }

    public required IReadOnlyList<ChipReadout> Chips { get; init; }

    /// <summary>Whether the hangar would open now.</summary>
    public required bool HangarHere { get; init; }

    /// <summary>The hero's distance from the pod in metres; null without either.</summary>
    public required float? PodDistance { get; init; }

    /// <summary>A boss left chips to choose from, in the hangar.</summary>
    public required bool ChipChoice { get; init; }

    /// <summary>The seconds until the next ion strike, and whether the hero stands in that circle, out of the pod where it can be hit.</summary>
    public required float? StrikeIn { get; init; }

    public required bool InStrike { get; init; }

    /// <summary>The figures for a race's hero; null without a run.</summary>
    public static WiredReadout? Of(GameWorld world, Race race)
    {
        if (world.Wired is not { } run || !run.Heroes.TryGetValue(race, out var state))
            return null;
        var (hero, pod, now) = (world.HeroOf(race), world.WiredPod(race), world.Time);
        var escorts = world.Escorts(race).OrderBy(m => m.Id).Select(m =>
            new EscortReadout(m.Id, m.Key.SubType ?? m.Key.Type, m.Key.HardwareLevel, Share(m.HitPoints, m.MaxHitPoints), Doing(m.Order))).ToList();
        var strike = run.Strikes.Count > 0 ? run.Strikes[0] : null;
        // An ended run has no next wave: it stands at the one it ended on.
        var wave = run.Phase == WiredPhase.Ended ? run.Wave : run.ShopWave;
        return new WiredReadout
        {
            Wave = wave,
            BetweenWaves = run.BetweenWaves,
            Seconds = run.Waiting ? (int)MathF.Ceiling(run.Countdown) : 0,
            WaitLength = (int)MathF.Ceiling(run.Phase == WiredPhase.Prepare ? WiredBalance.PrepareTime : WiredBalance.IntermissionTime),
            Special = run.BetweenWaves ? run.NextSpecial : run.Special,
            BossName = WiredDirector.BossOf(wave)?.Name,
            EnemiesLeft = run.EnemiesLeft,
            MostEnemies = Math.Max(run.MostEnemies, run.EnemiesLeft),
            Bank = (int)world.Races[race].Bmu,
            Score = WiredBalance.Score(state.Stats, run.Options.Difficulty),
            Pod = pod is null ? null : Share(pod.HitPoints, pod.MaxHitPoints),
            Bosses = run.Bosses.Where(b => !b.Machine.IsDead).Select(b => new BossReadout(b.Machine.Id, b.Plan.Boss?.Name ?? "",
                Share(b.Machine.HitPoints, b.Machine.MaxHitPoints), Share(b.Machine.Armour, b.Machine.MaxArmour))).ToList(),
            SpareBodies = state.SpareBodies,
            BodiesAtStart = WiredBalance.SpareBodies(run.Options.Difficulty),
            DownSeconds = state.RespawnAt is { } back ? MathF.Max(0, back - now) : null,
            Abilities = SlotsOf(state, run.Wave, now),
            Dash = state.Build(world.Data).DashCooldown is { } cooldown
                ? new DashReadout(MathF.Max(0, state.DashReadyAt - now), 1 - Share(state.DashReadyAt - now, cooldown), now < state.DashUntil)
                : null,
            Squad = escorts,
            SquadLimit = WiredBalance.SquadLimit(run.ShopWave, WiredChips.SquadExtra(state)),
            Chips = BoardOf(world, state),
            HangarHere = world.WhyNoHangar(state) is null,
            PodDistance = hero is not null && pod is not null ? hero.DistanceTo(pod) : null,
            ChipChoice = state.ChipChoice.Count > 0,
            StrikeIn = strike is null ? null : MathF.Max(0, strike.At - now),
            InStrike = hero is { Inside: null } && run.Strikes.Any(s => Vector2.Distance(hero.Position, s.Position) <= s.Radius),
        };
    }

    private static List<AbilityReadout> SlotsOf(WiredState state, int wave, float now)
    {
        var slots = new List<AbilityReadout>();
        for (var slot = 0; slot < state.Loadout.Abilities.Count; slot++)
        {
            if (state.Loadout.Abilities[slot] is not { } item || WiredCatalogue.FindAbility(item.Catalogue) is not { } ability)
                continue;
            var (seconds, waves) = (state.AbilitySecondsLeft(slot, now), state.AbilityWavesLeft(slot, wave));
            var share = ability.CooldownWaves > 0 ? 1 - Share(waves, ability.CooldownWaves) : 1 - Share(seconds, ability.Cooldown);
            slots.Add(new AbilityReadout(slot, item.Catalogue, seconds, waves, share));
        }
        return slots;
    }

    private static List<ChipReadout> BoardOf(GameWorld world, WiredState state)
    {
        var lit = WiredChips.Lit(world, state).ToHashSet();
        return state.Chips.Select(c => new ChipReadout(c.Chip.Id, c.Chip.Family, lit.Contains(c.Chip.Id))).ToList();
    }

    private static float Share(float part, float whole) => whole <= 0 ? 0 : Math.Clamp(part / whole, 0, 1);

    /// <summary>What an order comes to on the strip.</summary>
    private static EscortDoing Doing(Order order) => order switch
    {
        FollowOrder => EscortDoing.Following,
        AttackOrder or AttackMoveOrder => EscortDoing.Attacking,
        StandGroundOrder => EscortDoing.Holding,
        MoveOrder or ReturnOrder => EscortDoing.Moving,
        _ => EscortDoing.Idle,
    };
}
