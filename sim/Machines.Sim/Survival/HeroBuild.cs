using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Sim.Survival;

/// <summary>A weapon as a hero carries it: a copy of the real one with its Mk and the hero's reload, never the shared spec.</summary>
/// <param name="Item">The item behind it; null for a weapon that is part of the chassis.</param>
/// <param name="Copy">Where it stands in its mounting's battery: 0 for the first copy and every single weapon.</param>
public sealed record HeroWeapon(Mounting Mounting, WeaponSpec Spec, WiredItem? Item, float DamagePerSecond, int Copy = 0);

/// <summary>
/// What a <see cref="HeroLoadout"/> comes to: the machine it makes and how its two budgets stand. Worked out by
/// <see cref="Resolve"/> alone, for the hero in the match and for any change the hangar tries out, so that the
/// view holds none of these sums.
/// </summary>
public sealed record HeroBuild
{
    public required WiredChassis Chassis { get; init; }
    public required MachineSpec Spec { get; init; }

    /// <summary>The chassis' data with the hero's toughness, the modules and overload worked in; this hero's own copy.</summary>
    public required MachineLevel Stats { get; init; }

    /// <summary>The weapons on the machine: left, right, top, leaving out empty slots, a battery's copies next to each other in order.</summary>
    public required IReadOnlyList<HeroWeapon> Weapons { get; init; }

    /// <summary>The combo that names the weapons (<see cref="WeaponCombos.Split"/> reads it), or null for none.</summary>
    public required string? WeaponCombo { get; init; }

    public required int Load { get; init; }
    public required int LoadCapacity { get; init; }
    public required int PowerDraw { get; init; }
    public required int PowerSupply { get; init; }

    /// <summary>The sum of the weapons' damage per second.</summary>
    public required float DamagePerSecond { get; init; }

    /// <summary>The reach of its furthest weapon, 0 without any.</summary>
    public required float Range { get; init; }

    /// <summary>What gyros and overload make of its turning, as a share.</summary>
    public required float TurnShare { get; init; }

    /// <summary>What wrecks bring it, as a share of their BMUs.</summary>
    public required float WreckShare { get; init; }

    /// <summary>How near it must come to a wreck to collect it.</summary>
    public required float PickupReach { get; init; }

    /// <summary>The cooldown of its dash in seconds: of the thrusters or the chassis' own, the shorter; null without either.</summary>
    public required float? DashCooldown { get; init; }

    /// <summary>What a player should know about it, in plain words for the hangar.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Load as a share of the capacity: above 1 it is overloaded, above <see cref="WiredBalance.MostLoad"/> the hangar refuses.</summary>
    public float LoadShare => LoadCapacity <= 0 ? 0 : Load / (float)LoadCapacity;

    /// <summary>The share of its power needs that the reactor meets, at most 1.</summary>
    public float PowerShare => PowerDraw <= PowerSupply ? 1 : PowerSupply / (float)PowerDraw;

    /// <summary>
    /// How fast its hull turns when driven, degrees a second, under a first-person <paramref name="limit"/>. Every chassis
    /// turns faster than that limit by itself, so the limit moves with gyros and overload, or they would change nothing.
    /// </summary>
    public float DrivenTurnRate(float limit) => MathF.Min(Stats.RotationSpeed, limit * TurnShare);

    /// <summary>Whether its gun turns on an upper body (its level's BODY_TURN_LINK, as <see cref="Machine.CanTurnUpperBody"/>), not with the hull.</summary>
    public bool TurnsUpperBody => Stats.BodyTurnLink is { Length: > 0 } link && link != "NONE";

    /// <summary>
    /// How fast it aims with its hull, degrees a second, for a chassis without an upper body: a sighted driver's hull, and
    /// a hero bot's turn to face its target. Its ROTATION_SPEED, but no faster than a turret
    /// (<see cref="WiredBalance.HullAimCap"/>, moved by gyros and overload), times the chassis' <see cref="WiredChassis.GunTurn"/>.
    /// </summary>
    public float HullAimRate => MathF.Min(Stats.RotationSpeed, WiredBalance.HullAimCap * TurnShare) * Chassis.GunTurn;

    /// <summary>How fast its gun turns, degrees a second: its upper body's rate, or without one <see cref="HullAimRate"/>.</summary>
    public float GunTurnRate => TurnsUpperBody ? Stats.BodyTurnRate ?? 0 : HullAimRate;

    /// <summary>
    /// Resolves a loadout. Pure: it reads the game data and the catalogues and changes nothing. A weapon on a mounting
    /// the chassis does not have counts for nothing; <see cref="HeroLoadout.WhyNotFit"/> keeps those from happening.
    /// </summary>
    public static HeroBuild Resolve(GameData data, HeroLoadout loadout) => Resolve(data, loadout, loadout.ChassisEntry);

    /// <summary>
    /// The same on <paramref name="chassis"/>, a row of the catalogue for the loadout's chassis as it may be tuned: so a
    /// change to a knob can be tried out before it goes into the catalogue.
    /// </summary>
    public static HeroBuild Resolve(GameData data, HeroLoadout loadout, WiredChassis chassis)
    {
        var spec = data.Parm.Machine(chassis.TypeName);
        // A hero wears a few of the original's levels tuned (the crab); every other level as it is, the same reference.
        var level = WiredMachines.AsHero(chassis.TypeName, spec.Level(chassis.Level));

        float armour = 0, hitPoints = 0, repair = 0, speed = 0, turn = 0, scanner = 0, reload = 0, wrecks = 0;
        int capacity = chassis.Load, supply = chassis.Power, accuracy = 0, load = 0, draw = 0;
        var nightVision = level.NightVision;
        float? dash = null;
        foreach (var item in loadout.Modules.OfType<WiredItem>())
        {
            if (WiredCatalogue.FindModule(item.Catalogue) is not { } module)
                continue;
            var (effect, mk) = (module.Effect, item.Mk);
            load += module.Load;
            draw += module.Power;
            armour += effect.Armour * mk;
            hitPoints += effect.HitPoints * mk;
            repair += effect.Repair * mk;
            speed += effect.Speed * mk;
            turn += effect.Turn * mk;
            scanner += effect.Scanner * mk;
            reload += effect.Reload * mk;
            wrecks += effect.Wrecks * mk;
            capacity += effect.Capacity * mk;
            supply += effect.Power * mk;
            accuracy += effect.Accuracy * mk;
            nightVision |= effect.NightVision;
            if (effect.Dash)
            {
                var cooldown = WiredBalance.DashCooldowns[Math.Clamp(mk - 1, 0, WiredBalance.DashCooldowns.Count - 1)];
                dash = dash is { } other ? MathF.Min(other, cooldown) : cooldown;
            }
        }
        // A dash built into the chassis: thrusters can only make it come round sooner.
        if (chassis.Dash is { } builtIn)
            dash = dash is { } thrusters ? MathF.Min(thrusters, builtIn) : builtIn;
        foreach (var item in loadout.Abilities.OfType<WiredItem>())
            draw += WiredCatalogue.FindAbility(item.Catalogue)?.Power ?? 0;
        var chips = WiredChips.Building(loadout);

        // Load and power of the guns come first, as the reloads depend on the sum of everything. Each copy of a battery
        // weighs and draws as a single weapon does.
        var mounted = new List<(Mounting Mounting, WeaponSpec Spec, WiredItem? Item, int Copy)>();
        foreach (var mounting in Enum.GetValues<Mounting>())
        {
            if (chassis.Slot(mounting) is not { } slot)
                continue;
            if (slot.Fixed is { } part)
            {
                draw += chips.PowerPerWeapon;
                mounted.Add((mounting, data.Parm.Weapon(part), null, 0));
                continue;
            }
            var battery = loadout.Battery(mounting);
            for (var copy = 0; copy < battery.Count; copy++)
            {
                if (WiredCatalogue.FindWeapon(battery[copy].Catalogue) is not { } weapon)
                    continue;
                load += weapon.Load;
                draw += weapon.Power + chips.PowerPerWeapon;
                mounted.Add((mounting, data.Parm.Weapon(weapon.Id), battery[copy], copy));
            }
        }
        var extraBarrels = mounted.Count(w => w.Copy > 0);

        var powerShare = draw <= supply ? 1 : supply / (float)draw;
        var coolers = MathF.Max(WiredBalance.LeastReload, 1 - reload);
        var weapons = mounted.Select(w => Carried(w.Mounting, w.Spec, w.Item, coolers / powerShare, chips, w.Copy)).ToList();

        var overload = capacity <= 0 ? 0 : MathF.Max(0, load / (float)capacity - 1);
        var burden = MathF.Max(0, 1 - overload * WiredBalance.OverloadCost);
        var (move, rotate) = ((1 + speed) * burden, (1 + turn) * burden);
        var stats = level with
        {
            HitPoints = (int)MathF.Round(level.HitPoints * WiredBalance.HeroHitPoints * (1 + hitPoints)),
            Armour = (int)MathF.Round(level.Armour * WiredBalance.HeroArmour * (1 + armour + chips.Armour)),
            RepairRate = level.RepairRate * (1 + repair),
            Speed = level.Speed * move,
            TaskSpeed = level.TaskSpeed * move,
            Acceleration = level.Acceleration * move,
            RotationSpeed = level.RotationSpeed * rotate,
            RotationAcceleration = level.RotationAcceleration * rotate,
            BodyTurnRate = level.BodyTurnRate * rotate * chassis.GunTurn,
            // Constructors and spies have no accuracy in the data, which the original's machines read as perfect aim.
            Accuracy = BatteryAccuracy(Math.Min(100, (level.Accuracy ?? WiredBalance.NoAccuracy) + accuracy), extraBarrels, WiredBalance.BatteryAimLoss),
            ScannerRange = (int)MathF.Round(level.ScannerRange * (1 + scanner)),
            NightVision = nightVision,
            NumberOfWeapons = weapons.Count,
        };

        var warnings = new List<string>();
        if (load > capacity * WiredBalance.MostLoad)
            warnings.Add("Too heavy for this chassis");
        else if (load > capacity)
            warnings.Add("Overloaded: slower");
        if (draw > supply)
            warnings.Add("Not enough power: slower reloads");
        if (weapons.Count == 0)
            warnings.Add("No weapon");
        else if (chassis.Slots.Any(s => s.Fixed is null && loadout.Weapon(s.Mounting) is null))
            warnings.Add("Empty weapon slot");

        return new HeroBuild
        {
            Chassis = chassis,
            Spec = spec,
            Stats = stats,
            Weapons = weapons,
            WeaponCombo = Combo(weapons),
            Load = load,
            LoadCapacity = capacity,
            PowerDraw = draw,
            PowerSupply = supply,
            DamagePerSecond = weapons.Sum(w => w.DamagePerSecond),
            Range = weapons.Select(w => w.Spec.Range).DefaultIfEmpty(0).Max(),
            TurnShare = rotate,
            WreckShare = 1 + wrecks,
            // Whatever flies reaches further down for a wreck: the Boss line and the geo locator's glider alike.
            PickupReach = MathF.Max(WiredBalance.PickupReach, chips.PickupReach) * (level.Locomotion == Locomotion.Glider ? WiredBalance.GliderPickup : 1),
            DashCooldown = dash,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The hero's accuracy with its batteries: <paramref name="loss"/> points off for each barrel beyond the first of every
    /// battery, never below 0; with none to take off, the accuracy as it is.
    /// </summary>
    public static int BatteryAccuracy(int accuracy, int extraBarrels, int loss) =>
        extraBarrels * loss <= 0 ? accuracy : Math.Max(0, accuracy - extraBarrels * loss);

    /// <summary>A weapon with its Mk (+20 % damage and -8 % reload each), the hero's reload and what the chips add worked in.</summary>
    private static HeroWeapon Carried(Mounting mounting, WeaponSpec real, WiredItem? item, float reloadShare, ChipBuild chips, int copy)
    {
        var steps = (item?.Mk ?? 1) - 1;
        var spec = real with
        {
            Damage = real.Damage * (1 + WiredBalance.DamagePerMk * steps) * (1 + chips.Damage),
            Range = real.Range * (1 + chips.Range),
            ReloadTime = real.ReloadTime * (1 - WiredBalance.ReloadPerMk * steps) * reloadShare,
        };
        return new HeroWeapon(mounting, spec, item, DamageRate(spec), copy);
    }

    /// <summary>
    /// A weapon's damage over time as the driven hero fires it: a burst, a VARIABLE one whole, as often as the world lets it. That
    /// is when the reload is over and the burst's last round out, which goes one gap short of its duration, so that a single
    /// round waits for the reload alone; and the world counts in ticks, so a reload between two lasts to the next.
    /// </summary>
    private static float DamageRate(WeaponSpec weapon)
    {
        var rounds = Math.Max(1, weapon.BurstRounds);
        var cycle = MathF.Max(weapon.ReloadTime, weapon.BurstDuration * (rounds - 1) / rounds);
        var ticks = MathF.Ceiling(cycle * GameWorld.TicksPerSecond - TickSlack);
        return ticks <= 0 ? 0 : weapon.Damage * rounds * GameWorld.TicksPerSecond / ticks;
    }

    /// <summary>What a product of floats may be over a whole number of ticks and still be that number.</summary>
    private const float TickSlack = 0.001f;

    /// <summary>
    /// Names the weapons as a combo does, each on its mounting: <c>L_BOLTER_R_PLASMA_RIFLE</c>. A battery names its mounting
    /// for every copy, <c>L_BOLTER_L_BOLTER_R_BOLTER</c>, which <see cref="WeaponCombos.Split"/> reads back copy for copy.
    /// </summary>
    private static string? Combo(IReadOnlyList<HeroWeapon> weapons) => weapons.Count == 0
        ? null
        : string.Join('_', weapons.Select(w => $"{w.Mounting.ToString()[0]}_{w.Spec.TypeName}"));
}
