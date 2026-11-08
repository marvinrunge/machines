using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Sim.Survival;

/// <summary>The four families of chips; two linked ones of a family strengthen each other.</summary>
public enum ChipFamily
{
    Offensive,
    Defensive,
    Salvage,
    Command,
}

/// <summary>What chips add to a build: shares of the whole, and whole numbers. <see cref="HeroBuild.Resolve"/> works them in.</summary>
public sealed class ChipBuild
{
    public float Armour { get; set; }
    public float Damage { get; set; }
    public float Range { get; set; }

    /// <summary>The power each weapon draws on top of its own.</summary>
    public int PowerPerWeapon { get; set; }

    /// <summary>How near the hero must come to a wreck, in metres, if that is further than it would reach anyway.</summary>
    public float PickupReach { get; set; }
}

/// <summary>A hit as the chips see it: on what, by what, and the armour the target had before it.</summary>
public readonly record struct ChipHit(Actor Target, Actor? Shooter, string? Weapon, int ArmourBefore);

/// <summary>A chip at work on a hero. <paramref name="Strength"/> is its value with what linking adds.</summary>
public readonly record struct ChipUse(GameWorld World, WiredState State, Machine Hero, WiredChip Chip, float Strength)
{
    public float Now => World.Time;

    /// <summary>The chip's second number with what linking adds, for those where that is a size and not a condition.</summary>
    public float ScaledLimit => Chip.Value > 0 ? Chip.Limit * Strength / Chip.Value : Chip.Limit;
}

/// <summary>
/// A chip for the hero's circuit board. <paramref name="Value"/> is the size of its effect and <paramref name="Limit"/>
/// a second number where it has one (a reach, a threshold, a time). <paramref name="Base"/> chips are in every run's
/// pool, the others have to be unlocked. Its hooks say what it does; one with no hook is asked for by name, further down.
/// </summary>
public sealed record WiredChip(string Id, string Name, ChipFamily Family, float Value, float Limit = 0, bool Base = false)
{
    /// <summary>What it adds to a build; pure, as the hangar tries changes out with it.</summary>
    public Action<ChipBuild, WiredChip, float>? Build { get; init; }

    /// <summary>What multiplies a hit the hero's own machine deals to an enemy.</summary>
    public Func<ChipUse, ChipHit, float>? Dealt { get; init; }

    /// <summary>What multiplies a hit on the hero.</summary>
    public Func<ChipUse, ChipHit, float>? Taken { get; init; }

    /// <summary>What it does when a hit has landed on the hero, before anything else looks at it.</summary>
    public Action<ChipUse, ChipHit>? Struck { get; init; }

    /// <summary>What it does each tick of the hero's life.</summary>
    public Action<ChipUse>? Tick { get; init; }

    /// <summary>What it does when a machine of the waves falls to the hero or its squad.</summary>
    public Action<ChipUse, Machine>? Kill { get; init; }

    /// <summary>Whether it is at work right now, for the HUD.</summary>
    public Func<ChipUse, bool>? Lit { get; init; }
}

/// <summary>The chips and the board they sit on, as data; Wired's own, like everything in this folder.</summary>
public static class WiredChips
{
    /// <summary>What linking adds to each linked chip's value.</summary>
    public const float LinkBonus = 0.5f;

    /// <summary>How many chips a boss wave offers to choose from.</summary>
    public const int ChoiceSize = 3;

    public static readonly IReadOnlyList<WiredChip> All =
    [
        // Below 30 % hit points the reloads run 40 % faster.
        new("overload", "Overload", ChipFamily.Offensive, 0.4f, 0.3f) { Tick = Overload, Lit = Hurt },
        // +15 % range.
        new("marksman", "Marksman", ChipFamily.Offensive, 0.15f, Base: true) { Build = (build, _, value) => build.Range += value },
        // Each second of unbroken fire +2 % damage, up to 30 %.
        new("sustained", "Sustained fire", ChipFamily.Offensive, 0.02f, 0.3f, Base: true) { Dealt = Sustained, Lit = Firing },
        // +30 % damage for 10 s after the armour is shot away.
        new("revenge", "Revenge", ChipFamily.Offensive, 0.3f, 10) { Dealt = Revenge, Struck = ArmourGone, Lit = Avenging },
        // Kills explode for 15 % of the victim's hit points within 8 m.
        new("shrapnel", "Shrapnel", ChipFamily.Offensive, 0.15f, 8) { Kill = Shrapnel },
        // +15 % damage, and each weapon draws one point of power more.
        new("hotbarrels", "Glowing barrels", ChipFamily.Offensive, 0.15f, 1, Base: true) { Build = HotBarrels },

        // Once a wave a killing hit leaves 1 hit point and 3 s of safety.
        new("lastditch", "Emergency repair", ChipFamily.Defensive, 3) { Struck = LastDitch, Lit = Invulnerable },
        // A hit takes 20 % off the next.
        new("reactive", "Reactive armour", ChipFamily.Defensive, 0.2f) { Taken = Reactive },
        // One hit point every 3 s out of fire.
        new("nano", "Nano healing", ChipFamily.Defensive, 1, 3, Base: true) { Tick = Nano, Lit = OutOfFire },
        // +20 % armour.
        new("tough", "Toughness", ChipFamily.Defensive, 0.2f, Base: true) { Build = (build, _, value) => build.Armour += value },
        // Armour counts 25 % more within 40 m of the pod.
        new("bastion", "Last bastion", ChipFamily.Defensive, 0.25f, 40) { Taken = Bastion, Lit = AtPod },
        // Blasts do 35 % less.
        new("damper", "Damper", ChipFamily.Defensive, 0.35f, Base: true) { Taken = Damper },

        // Wrecks come from 15 m away; the wreck's flight is the view's.
        new("magnet", "Scrap magnet", ChipFamily.Salvage, 15, Base: true) { Build = (build, _, value) => build.PickupReach = MathF.Max(build.PickupReach, value) },
        // Selling brings 25 points more of what was paid: 75 % instead of 50 %.
        new("recycler", "Recycler", ChipFamily.Salvage, 0.25f, Base: true),
        // The shop is 10 % cheaper.
        new("trader", "Trader", ChipFamily.Salvage, 0.1f, Base: true),
        // Elites leave crates half as often again.
        new("trove", "Treasure trove", ChipFamily.Salvage, 0.5f),
        // 10 BMUs more for a wave, and 10 more for every 5 waves behind it.
        new("vein", "Gold vein", ChipFamily.Salvage, 10, 5),
        // Half the bank stays through a death.
        new("savings", "Savings bank", ChipFamily.Salvage, 0.5f),

        // The squad aims 15 points better within 30 m of the hero.
        new("booster", "Radio booster", ChipFamily.Command, 15, 30, Base: true),
        // One escort more, as far as the largest squad.
        new("tactics", "Squad tactics", ChipFamily.Command, 1, Base: true),
        // The squad runs 20 % faster while it follows.
        new("rally", "Rally call", ChipFamily.Command, 0.2f, Base: true),
        // The squad mends 2 % of its hit points a second within 30 m of the hero.
        new("workshop", "Field workshop", ChipFamily.Command, 0.02f, 30) { Tick = Workshop },
        // Escorts come one hardware level better where there is one.
        new("veterans", "Veterans", ChipFamily.Command, 1),
        // The squad turns on what hits the hero.
        new("cover", "Covering fire", ChipFamily.Command, 1) { Struck = CoveringFire },
    ];

    /// <summary>The chips of a run that has unlocked nothing yet.</summary>
    public static readonly IReadOnlyList<string> BasePool = All.Where(c => c.Base).Select(c => c.Id).ToList();

    /// <summary>The board's traces between its sockets, counted from 0: a ring of six and the bridge between the first and the fourth.</summary>
    public static readonly IReadOnlyList<(int A, int B)> Traces = [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (5, 0), (0, 3)];

    public static WiredChip? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Whether the chip in a socket is linked: a trace joins it to a chip of its family.</summary>
    public static bool Linked(IReadOnlyList<WiredItem?> sockets, int socket)
    {
        if (sockets[socket] is not { } chip || Find(chip.Catalogue) is not { } own)
            return false;
        foreach (var (a, b) in Traces)
        {
            if ((a == socket || b == socket) && sockets[a == socket ? b : a] is { } other && Find(other.Catalogue)?.Family == own.Family)
                return true;
        }
        return false;
    }

    /// <summary>The value of the chip in a socket, with what linking adds.</summary>
    public static float Strength(IReadOnlyList<WiredItem?> sockets, int socket) =>
        sockets[socket] is { } chip && Find(chip.Catalogue) is { } own ? own.Value * (Linked(sockets, socket) ? 1 + LinkBonus : 1) : 0;

    // ---- running the hooks --------------------------------------------------------------------------

    /// <summary>What the chips in a loadout's sockets add to its build.</summary>
    public static ChipBuild Building(HeroLoadout loadout)
    {
        var build = new ChipBuild();
        for (var socket = 0; socket < loadout.Chips.Count; socket++)
        {
            if (loadout.Chips[socket] is { } item && Find(item.Catalogue) is { Build: { } hook } chip)
                hook(build, chip, Strength(loadout.Chips, socket));
        }
        return build;
    }

    /// <summary>What a hit the hero deals is multiplied by.</summary>
    public static float Dealing(GameWorld world, WiredState state, Machine hero, ChipHit hit)
    {
        var share = 1f;
        foreach (var (chip, strength) in state.Chips)
            share *= chip.Dealt?.Invoke(new ChipUse(world, state, hero, chip, strength), hit) ?? 1;
        return share;
    }

    /// <summary>What a hit on the hero is multiplied by.</summary>
    public static float Taking(GameWorld world, WiredState state, Machine hero, ChipHit hit)
    {
        var share = 1f;
        foreach (var (chip, strength) in state.Chips)
            share *= chip.Taken?.Invoke(new ChipUse(world, state, hero, chip, strength), hit) ?? 1;
        return share;
    }

    /// <summary>A hit has landed on the hero.</summary>
    public static void Struck(GameWorld world, WiredState state, Machine hero, ChipHit hit)
    {
        foreach (var (chip, strength) in state.Chips)
            chip.Struck?.Invoke(new ChipUse(world, state, hero, chip, strength), hit);
    }

    /// <summary>A tick of the hero's life.</summary>
    public static void Ticking(GameWorld world, WiredState state, Machine hero)
    {
        foreach (var (chip, strength) in state.Chips)
            chip.Tick?.Invoke(new ChipUse(world, state, hero, chip, strength));
    }

    /// <summary>A machine of the waves fell to the hero's race.</summary>
    public static void Killed(GameWorld world, WiredState state, Machine hero, Machine victim)
    {
        foreach (var (chip, strength) in state.Chips)
            chip.Kill?.Invoke(new ChipUse(world, state, hero, chip, strength), victim);
    }

    /// <summary>The ids of the chips at work right now, for the HUD.</summary>
    public static IEnumerable<string> Lit(GameWorld world, WiredState state)
    {
        if (world.HeroOf(state.Race) is not { } hero)
            yield break;
        foreach (var (chip, strength) in state.Chips)
        {
            if (chip.Lit?.Invoke(new ChipUse(world, state, hero, chip, strength)) == true)
                yield return chip.Id;
        }
    }

    // ---- what is asked for by name ------------------------------------------------------------------

    /// <summary>What Trader takes off the shop's prices.</summary>
    public static float Discount(WiredState state) => state.ChipValue("trader");

    /// <summary>The percentage of what was paid that selling brings. Linked it comes to 87, never to what was paid.</summary>
    public static int SellPercent(WiredState state) => SellPercent(state.ChipValue("recycler"));

    /// <summary>The same for a Recycler of that value.</summary>
    public static int SellPercent(float recycler) => WiredBalance.SellPercent + (int)(recycler * 100);

    /// <summary>What multiplies an elite's chance of leaving a crate.</summary>
    public static float LootShare(WiredState state) => 1 + state.ChipValue("trove");

    /// <summary>What Gold vein adds to a wave's bonus: its value for the wave and again for each 5 waves behind it.</summary>
    public static int WaveExtra(WiredState state, int wave)
    {
        var value = state.ChipValue("vein");
        return value > 0 ? (int)(value * (1 + wave / (int)Find("vein")!.Limit)) : 0;
    }

    /// <summary>The share of the bank that a death leaves.</summary>
    public static float Kept(WiredState state) => MathF.Min(1, state.ChipValue("savings"));

    /// <summary>How many escorts more the squad may have, up to the largest squad. Linked it is two: there are no halves of an escort.</summary>
    public static int SquadExtra(WiredState state) => (int)MathF.Ceiling(state.ChipValue("tactics"));

    /// <summary>
    /// The machine bought for a choice from the squad list: with Veterans the next hardware level of the type, or two when
    /// linked, where the tiers have one, keeping the weapons if that level has a combo of them.
    /// </summary>
    public static ResearchKey Veteran(WiredState state, ResearchKey key)
    {
        for (var steps = (int)MathF.Ceiling(state.ChipValue("veterans")); steps > 0; steps--)
        {
            var (at, combo) = (key, key.WeaponCombo);
            if (WiredCatalogue.Tiers.FirstOrDefault(t => t.Type == at.Type && t.SubType == at.SubType && t.Level == at.HardwareLevel + 1) is not { } better)
                break;
            key = at with { HardwareLevel = better.Level, WeaponCombo = better.Combos.FirstOrDefault(c => SameWeapons(c, combo)) ?? better.Combos[0] };
        }
        return key;
    }

    private static bool SameWeapons(string combo, string? other) => other is not null
        && WeaponCombos.Split(combo).Select(w => w.Weapon).Order().SequenceEqual(WeaponCombos.Split(other).Select(w => w.Weapon).Order());

    /// <summary>What an escort near its hero gains on an aim taken afresh, with Radio booster.</summary>
    public static float SquadAim(GameWorld world, Machine machine)
    {
        if (!IsEscort(world, machine, out var state) || world.HeroOf(state.Race) is not { } hero)
            return 0;
        return Vector2.DistanceSquared(machine.Position, hero.Position) <= Square(Find("booster")!.Limit) ? state.ChipValue("booster") : 0;
    }

    /// <summary>What multiplies a machine's top speed: an escort that follows its hero runs faster with Rally call.</summary>
    public static float Pace(GameWorld world, Machine machine) =>
        machine.Order is FollowOrder && IsEscort(world, machine, out var state) ? 1 + state.ChipValue("rally") : 1;

    private static bool IsEscort(GameWorld world, Machine machine, out WiredState state)
    {
        state = world.Wired!.Heroes.GetValueOrDefault(machine.Race)!;
        return state is not null && state.EscortIds.Contains(machine.Id);
    }

    private static float Square(float value) => value * value;

    // ---- the hooks ----------------------------------------------------------------------------------

    private static bool Hurt(ChipUse use) => use.Hero.HitPoints < use.Hero.MaxHitPoints * use.Chip.Limit;

    private static void Overload(ChipUse use)
    {
        if (!Hurt(use))
            return;
        foreach (var weapon in use.Hero.Weapons.Where(w => w.Reload > 0))
            weapon.Reload -= GameWorld.Dt * use.Strength;
    }

    /// <summary>The longest time between two rounds of unbroken fire: a little more than the slowest of its weapons takes to reload.</summary>
    public static float FireGap(Machine hero) =>
        MathF.Max(WiredBalance.FireGap, hero.Weapons.Select(w => w.Spec.ReloadTime).DefaultIfEmpty(0).Max() * WiredBalance.FireGapShare);

    private static bool Firing(ChipUse use) => use.Now - use.State.LastShotAt <= FireGap(use.Hero);

    private static float Sustained(ChipUse use, ChipHit hit) =>
        Firing(use) ? 1 + MathF.Min(use.ScaledLimit, use.Strength * (use.Now - use.State.FiringSince)) : 1;

    private static bool Avenging(ChipUse use) => use.Now < use.State.RevengeUntil;

    private static float Revenge(ChipUse use, ChipHit hit) => Avenging(use) ? 1 + use.Strength : 1;

    private static void ArmourGone(ChipUse use, ChipHit hit)
    {
        if (hit.ArmourBefore > 0 && use.Hero.Armour <= 0)
            use.State.RevengeUntil = use.Now + use.Chip.Limit;
    }

    private static void Shrapnel(ChipUse use, Machine victim) =>
        use.World.Shrapnel(victim, (int)(victim.MaxHitPoints * use.Strength), use.Chip.Limit, use.State.Race);

    private static void HotBarrels(ChipBuild build, WiredChip chip, float value)
    {
        build.Damage += value;
        build.PowerPerWeapon += (int)chip.Limit;
    }

    private static bool Invulnerable(ChipUse use) => use.Now < use.State.InvulnerableUntil;

    private static void LastDitch(ChipUse use, ChipHit hit)
    {
        if (!use.Hero.IsDead || use.State.LastDitchWave == use.World.Wired!.Wave)
            return;
        use.Hero.HitPoints = 1;
        use.State.LastDitchWave = use.World.Wired.Wave;
        use.State.InvulnerableUntil = use.Now + use.Strength;
        use.World.Raise(new GameEvent(GameEventKind.WiredHeroSaved, use.Hero.Id, use.Hero.Position, Detail: use.Chip.Id));
    }

    /// <summary>The last hit counts for this long; the one after it takes the less. At most half can go.</summary>
    private static float Reactive(ChipUse use, ChipHit hit) =>
        use.Now - use.State.LastHitAt <= WiredBalance.ReactiveTime ? 1 - MathF.Min(0.5f, use.Strength) : 1;

    private static bool OutOfFire(ChipUse use) =>
        use.Now - use.State.LastHitAt >= WiredBalance.OutOfFireTime && use.Hero.HitPoints < use.Hero.MaxHitPoints;

    private static void Nano(ChipUse use)
    {
        if (OutOfFire(use))
            use.World.AddHitPoints(use.Hero, use.Strength / use.Chip.Limit * GameWorld.Dt);
    }

    private static bool AtPod(ChipUse use) =>
        use.World.WiredPod(use.State.Race) is { } pod && use.Hero.DistanceTo(pod) <= use.Chip.Limit;

    /// <summary>Armour is a pool hits drain and not a share of each, so a quarter more of it is a fifth less drawn from it.</summary>
    private static float Bastion(ChipUse use, ChipHit hit) =>
        hit.ArmourBefore > 0 && AtPod(use) ? 1 / (1 + use.Strength) : 1;

    private static float Damper(ChipUse use, ChipHit hit) =>
        hit.Weapon is { } weapon && (Blast.IsArea(weapon) || weapon == GameWorld.LandMineBlast) ? 1 - MathF.Min(0.9f, use.Strength) : 1;

    /// <summary>Looked at once a second, as it goes through all the machines.</summary>
    private static void Workshop(ChipUse use)
    {
        if (use.World.Tick % GameWorld.TicksPerSecond != 0)
            return;
        foreach (var escort in use.World.Escorts(use.State.Race))
        {
            if (Vector2.DistanceSquared(escort.Position, use.Hero.Position) <= Square(use.Chip.Limit))
                use.World.AddHitPoints(escort, escort.MaxHitPoints * use.Strength);
        }
    }

    /// <summary>Only what can be told: a blast has no shooter. Linked the call goes out more often.</summary>
    private static void CoveringFire(ChipUse use, ChipHit hit)
    {
        if (hit.Shooter is not { IsDead: false } shooter || !use.World.AreEnemies(use.State.Race, shooter.Race)
            || use.Now < use.State.CoverCalledAt + WiredBalance.CoverEvery / use.Strength)
            return;
        use.State.CoverCalledAt = use.Now;
        foreach (var escort in use.World.Escorts(use.State.Race).Where(e => e.CanFight && e.Order is FollowOrder or IdleOrder or ReturnOrder))
            use.World.Counterattack(escort, shooter);
    }
}
