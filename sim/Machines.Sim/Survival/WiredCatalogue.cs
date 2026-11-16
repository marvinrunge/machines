using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.Survival;

/// <summary>What a weapon slot takes: its size or smaller.</summary>
public enum SlotSize
{
    Light,
    Medium,
    Heavy,
}

/// <summary>A chassis' weapon slot. <paramref name="Fixed"/> names a weapon that is part of the chassis and cannot come off.</summary>
public sealed record ChassisSlot(Mounting Mounting, SlotSize Size, string? Fixed = null);

/// <summary>
/// What a chassis can do besides carrying weapons, Wired's own. The body's (<see cref="WiredMachines.BodySpecials"/>:
/// plough, harvest, lurk, anchor) belong to the machine type and level and go for every machine of it in a Wired match;
/// the others are the hero's, from the chassis it wears.
/// </summary>
[Flags]
public enum ChassisSpecial
{
    None = 0,

    /// <summary>Old Iron: hits from the front cost less, and its dash rams.</summary>
    Plough = 1,

    /// <summary>Harvester: a kill near it gives armour back and brings in the wrecks there.</summary>
    Harvest = 2,

    /// <summary>Widow: standing still, its first shot flies further and hurts more.</summary>
    Lurk = 4,

    /// <summary>Colossus: it anchors its tracks for range, reload and armour.</summary>
    Anchor = 8,

    /// <summary>Hornet: a dash without thrusters.</summary>
    Afterburner = 16,

    /// <summary>Marshal: a bigger squad that aims and mends better near it, and calls it in.</summary>
    Field = 32,

    /// <summary>The constructors: they raise towers.</summary>
    Towers = 64,

    /// <summary>The spies: they lay mines.</summary>
    Mines = 128,

    /// <summary>Phantom: quiet, it cannot be locked on from afar.</summary>
    Shadow = 256,

    /// <summary>The geo locators: a pulse marks every enemy within the scanner's reach, and the marked take more from the hero's side.</summary>
    Locate = 512,
}

/// <summary>
/// A machine as the hero's body. The real game data supplies its hit points, armour, repair, speed, turn, accuracy,
/// scanner and locomotion (<see cref="MachineSpec.Level"/>); slots, load, power, price and the way there are Wired's own.
/// </summary>
/// <param name="SubType">None for a type without subtypes, the spies and the geo locators.</param>
/// <param name="Needs">Having owned any one of these opens it up; none for the first.</param>
/// <param name="FromWave">The first wave whose shop sells it in a run; chassis new to Wired wait.</param>
/// <param name="Dash">The cooldown of a dash built into the chassis, in seconds; thrusters may make it shorter.</param>
/// <param name="Special">What it can do besides carrying weapons.</param>
/// <param name="Towers">The most towers a constructor stands at once.</param>
public sealed record WiredChassis(string Id, string Type, string? SubType, int Level, IReadOnlyList<ChassisSlot> Slots, int Modules,
    int Load, int Power, int Price, IReadOnlyList<string> Needs, int FromWave = 0, float? Dash = null,
    ChassisSpecial Special = ChassisSpecial.None, int Towers = 0)
{
    public string TypeName => SubType is null ? Type : $"{Type} {SubType}";

    /// <summary>
    /// The Boss line: only its top slot takes the stings and bombs. It is not "a machine that flies": the geo locator's
    /// glider flies as well and takes none of them, and every flight rule goes by the machine's locomotion instead.
    /// </summary>
    public bool Glider => SubType == "BOSS";

    public ChassisSlot? Slot(Mounting mounting) => Slots.FirstOrDefault(s => s.Mounting == mounting);
}

/// <summary>
/// A real weapon (<see cref="WeaponSpec"/>, by its name in parmdata) as an item; size, load, power and price are Wired's own.
/// <paramref name="FromWave"/> is the first wave the shop offers it for. <paramref name="Single"/> keeps it from forming a
/// battery: it never comes more than once on a slot.
/// </summary>
public sealed record WiredWeapon(string Id, SlotSize Size, int Load, int Power, int Price, int FromWave, bool GliderOnly = false, bool Single = false);

/// <summary>What a module does for each Mk: shares of the chassis' values, and whole points.</summary>
public sealed record ModuleEffect
{
    public float Armour { get; init; }
    public float HitPoints { get; init; }
    public float Repair { get; init; }
    public float Speed { get; init; }
    public float Turn { get; init; }
    public float Scanner { get; init; }
    public float Reload { get; init; }
    public float Wrecks { get; init; }
    public int Capacity { get; init; }
    public int Power { get; init; }
    public int Accuracy { get; init; }
    public bool NightVision { get; init; }
    public bool Dash { get; init; }
}

/// <summary>A module; <paramref name="Prices"/> holds the price of Mk 1 and of each step up, so its length is its highest Mk.</summary>
public sealed record WiredModule(string Id, string Name, int Load, int Power, IReadOnlyList<int> Prices, int FromWave, ModuleEffect Effect);

/// <summary>
/// An ability: <paramref name="Based"/> names the real weapon behind it, where there is one. <paramref name="Cooldown"/> is
/// in seconds, or <paramref name="CooldownWaves"/> in waves; <paramref name="Reach"/> is its range or radius in metres.
/// </summary>
public sealed record WiredAbility(string Id, string Name, string? Based, float Cooldown, int CooldownWaves, float Reach, int Power, int Price, int FromWave);

/// <summary>A tower a constructor hero raises: a MISSILE_EMPLACEMENT of the original's, by its subtype and level.</summary>
public sealed record WiredTower(string Id, string SubType, int Level)
{
    /// <summary>The construction every tower is.</summary>
    public const string Type = "MISSILE_EMPLACEMENT";
}

/// <summary>A type of machine the waves bring in from <paramref name="Wave"/> on, and the hangar sells as an escort from <see cref="EscortFrom"/>.</summary>
public sealed record WiredTier(int Wave, string Type, string SubType, int Level, IReadOnlyList<string> Combos)
{
    /// <summary>The first wave whose hangar sells it as an escort: the wave the waves bring it from, unless given.</summary>
    public int EscortFrom { get; init; } = Wave;

    public string TypeName => $"{Type} {SubType}";

    public bool Offers(ResearchKey key) =>
        key.Type == Type && key.SubType == SubType && key.HardwareLevel == Level && key.WeaponCombo is { } combo && Combos.Contains(combo);
}

/// <summary>Everything a Wired hero can be made of, as data. The order of each list is the shop's and must stay fixed.</summary>
public static class WiredCatalogue
{
    private const SlotSize L = SlotSize.Light, M = SlotSize.Medium, H = SlotSize.Heavy;

    private static ChassisSlot Left(SlotSize size) => new(Mounting.Left, size);
    private static ChassisSlot Right(SlotSize size) => new(Mounting.Right, size);
    private static ChassisSlot Top(SlotSize size) => new(Mounting.Top, size);

    public static readonly IReadOnlyList<WiredChassis> Chassis =
    [
        new("grunt1", "AGGRESSOR", "GRUNT", 1, [Right(L)], 1, 6, 6, 0, []),
        new("grunt2", "AGGRESSOR", "GRUNT", 2, [Left(L)], 1, 8, 7, 110, ["grunt1"]),
        new("grunt3", "AGGRESSOR", "GRUNT", 3, [Left(M), Right(M)], 2, 12, 9, 240, ["grunt2"]),
        new("reaper2", "AGGRESSOR", "ASSASSIN", 2, [Left(M), Right(L)], 2, 12, 10, 300, ["grunt2"]),
        new("reaper3", "AGGRESSOR", "ASSASSIN", 3, [Left(M), Right(M)], 2, 15, 12, 480, ["reaper2", "grunt3"]),
        new("overseer2", "ADMINISTRATOR", "OVERSEER", 2, [Left(L), Right(L)], 3, 10, 14, 320, ["grunt2"]),
        new("overseer3", "ADMINISTRATOR", "OVERSEER", 3, [Left(M), Right(L)], 3, 12, 16, 480, ["overseer2"]),
        new("knight3", "AGGRESSOR", "KNIGHT", 3, [Left(M), Right(M)], 3, 20, 16, 750, ["reaper3", "grunt3"]),
        new("knight4", "AGGRESSOR", "KNIGHT", 4, [Left(H), Right(H)], 3, 26, 20, 1200, ["knight3"]),
        new("knight5", "AGGRESSOR", "KNIGHT", 5, [Left(H), Right(H), Top(M)], 4, 34, 26, 1900, ["knight4"]),
        new("commander4", "ADMINISTRATOR", "COMMANDER", 4, [Left(H), Right(M)], 4, 22, 28, 1300, ["overseer3", "knight3"]),
        new("commander5", "ADMINISTRATOR", "COMMANDER", 5, [Left(H), Right(H)], 5, 26, 34, 1900, ["commander4"]),
        new("ballista3", "AGGRESSOR", "BALLISTA", 3, [Top(H)], 2, 24, 16, 800, ["grunt3", "reaper3"]),
        new("ballista4", "AGGRESSOR", "BALLISTA", 4, [Left(H), Right(H)], 3, 36, 22, 1500, ["ballista3"]),
        new("boss1", "ADMINISTRATOR", "BOSS", 1, [Top(L)], 2, 8, 14, 700, ["overseer3", "geo3"]),
        new("boss2", "ADMINISTRATOR", "BOSS", 2, [Top(H)], 3, 14, 20, 1400, ["boss1"]),
        new("gorilla5", "AGGRESSOR", "NINJA", 5, [Left(H), Right(H), new ChassisSlot(Mounting.Top, H, "GORILLA_PUNCH")], 4, 44, 30, 3200, ["knight5", "ballista4"]),
        // Wired's own machines (WiredMachines), sidegrades at the ends of the lines: Sarge, Old Iron, Thresher, Harvester,
        // Widow, Colossus, Hornet and Marshal. Their values are the concept's, a start for balancing.
        new("grunt4", "AGGRESSOR", "GRUNT", 4, [Left(M), Right(M), Top(L)], 3, 20, 13, 850, ["grunt3"], 12),
        new("grunt5", "AGGRESSOR", "GRUNT", 5, [Left(H), Right(M), Top(M)], 4, 34, 20, 2400, ["grunt4"], 20, Dash: 10, Special: ChassisSpecial.Plough),
        new("reaper4", "AGGRESSOR", "ASSASSIN", 4, [Left(H), Right(M)], 3, 24, 16, 1250, ["reaper3"], 12),
        new("reaper5", "AGGRESSOR", "ASSASSIN", 5, [Left(H), Right(H), Top(L)], 3, 32, 22, 3000, ["reaper4"], 20, Special: ChassisSpecial.Harvest),
        new("widow6", "AGGRESSOR", "ASSASSIN", 6, [Left(H), Right(M)], 3, 18, 24, 2700, ["reaper3"], 20, Special: ChassisSpecial.Lurk),
        new("ballista5", "AGGRESSOR", "BALLISTA", 5, [Left(H), Right(H), Top(H)], 3, 52, 30, 3600, ["ballista4"], 20, Special: ChassisSpecial.Anchor),
        new("boss3", "ADMINISTRATOR", "BOSS", 3, [Left(L), Right(L), Top(M)], 2, 14, 20, 1700, ["boss1"], 14, Dash: 8, Special: ChassisSpecial.Afterburner),
        new("commander6", "ADMINISTRATOR", "COMMANDER", 6, [Left(H), Right(H), Top(M)], 6, 32, 44, 3300, ["commander5"], 20, Special: ChassisSpecial.Field),
        // The constructor line, which raises towers, and the spy line, which lays mines: the original's machines new to Wired
        // as chassis, with Wired's own Titan (Behemoth 6) and Phantom (Spy Locator 6) at their ends.
        new("dozer1", "CONSTRUCTOR", "DOZER", 1, [Top(L)], 2, 8, 8, 420, ["grunt2"], 6, Special: ChassisSpecial.Towers, Towers: 2),
        new("builder3", "CONSTRUCTOR", "BUILDER", 3, [Top(L)], 2, 12, 10, 700, ["dozer1"], 9, Special: ChassisSpecial.Towers, Towers: 4),
        new("behemoth4", "CONSTRUCTOR", "BEHEMOTH", 4, [Top(M)], 3, 18, 12, 1100, ["builder3"], 12, Special: ChassisSpecial.Towers, Towers: 6),
        new("titan6", "CONSTRUCTOR", "BEHEMOTH", 6, [Top(H)], 4, 28, 18, 2600, ["behemoth4"], 20, Special: ChassisSpecial.Towers, Towers: 8),
        new("spy3", "SPY_LOCATOR", null, 3, [Top(L)], 3, 8, 12, 420, ["overseer2"], 6, Special: ChassisSpecial.Mines),
        new("spy5", "SPY_LOCATOR", null, 5, [Left(L), Right(L)], 3, 10, 16, 1000, ["spy3"], 12, Special: ChassisSpecial.Mines),
        new("phantom6", "SPY_LOCATOR", null, 6, [Left(M), Right(M)], 4, 16, 26, 2600, ["spy5"], 20, Special: ChassisSpecial.Mines | ChassisSpecial.Shadow),
        // The locator line, scouts that locate: the original's Geo Locator 1, the crab, a second start beside Grunt 1 with
        // its frame (as a hero tuned, WiredMachines.HeroTunes), and its Geo Locator 3, the first chassis that flies and the
        // cheaper way to Boss 1. Only ever appended: Chassis[0] is the run's own and the hangar numbers chassis by place.
        new("geo1", "GEO_LOCATOR", null, 1, [Top(L)], 1, 6, 6, 0, [], Special: ChassisSpecial.Locate),
        new("geo3", "GEO_LOCATOR", null, 3, [Top(L)], 2, 7, 9, 280, ["geo1"], 5, Special: ChassisSpecial.Locate),
    ];

    /// <summary>The chassis that need none before them, in catalogue order: those a hero may start on with nothing owned.</summary>
    public static readonly IReadOnlyList<WiredChassis> Roots = [.. Chassis.Where(c => c.Needs.Count == 0)];

    /// <summary>The towers a constructor hero may raise, smallest first; which of them a chassis builds goes by its machine's data.</summary>
    public static readonly IReadOnlyList<WiredTower> Towers =
    [
        new("turret1", "TURRET", 1),
        new("turret2", "TURRET", 2),
        new("turret3", "TURRET", 3),
        new("sentry3", "SENTRY", 3),
        new("sentry4", "SENTRY", 4),
        new("launcher4", "LAUNCHER", 4),
    ];

    public static readonly IReadOnlyList<WiredWeapon> Weapons =
    [
        new("BOLTER", L, 2, 1, 40, 1),
        new("AUTO_CANNON", L, 2, 1, 100, 1),
        new("ELECTRIC_CHARGE", L, 2, 2, 60, 1),
        new("FLAME_THROWER2", L, 3, 2, 80, 1),
        new("PULSE_RIFLE", L, 2, 3, 130, 2),
        new("PLASMA_RIFLE", L, 3, 2, 100, 2),
        new("HEAVY_BOLTER1", M, 4, 2, 170, 3),
        new("PLASMA_CANNON1", M, 5, 4, 280, 5),
        new("MULTI_LAUNCHER3", M, 5, 3, 300, 5),
        new("MULTI_LAUNCHER5", M, 6, 4, 420, 6),
        new("HEAVY_BOLTER2", M, 6, 3, 360, 6),
        new("PULSE_CANNON", M, 5, 5, 400, 7),
        new("MULTI_LAUNCHER4", M, 6, 4, 460, 8),
        new("PLASMA_CANNON2", H, 8, 6, 700, 10),
        new("MULTI_LAUNCHER1", H, 9, 5, 750, 10),
        new("MULTI_LAUNCHER7", H, 10, 6, 1000, 12),
        new("MULTI_LAUNCHER2", H, 12, 7, 1200, 14),
        // The glider's own body weapons have barrels enough and no room on its top to stack, and three of the bomb's
        // blasts, which go off at the target without straying and hit friend and foe, would tame nothing: no batteries.
        new("WASP_LIGHT_STING", L, 2, 2, 200, 6, GliderOnly: true, Single: true),
        new("WASP_METAL_STING", M, 4, 3, 450, 9, GliderOnly: true, Single: true),
        new("BEE_BOMB", H, 7, 5, 800, 12, GliderOnly: true, Single: true),
    ];

    public static readonly IReadOnlyList<WiredModule> Modules =
    [
        new("plates", "Armour plates", 3, 0, [90, 180, 320], 1, new ModuleEffect { Armour = 0.25f, HitPoints = 0.1f }),
        new("frame", "Reinforced frame", 2, 0, [110, 220, 380], 1, new ModuleEffect { HitPoints = 0.25f, Capacity = 3 }),
        new("repair", "Repair system", 1, 2, [90, 180, 320], 1, new ModuleEffect { Repair = 0.6f }),
        new("drive", "Drive", 2, 2, [80, 170, 300], 1, new ModuleEffect { Speed = 0.12f }),
        new("gyro", "Gyro", 1, 1, [60, 130, 230], 1, new ModuleEffect { Turn = 0.2f }),
        new("thrusters", "Thrusters", 2, 2, [180, 300, 450], 3, new ModuleEffect { Dash = true }),
        new("targeting", "Targeting computer", 1, 2, [100, 200, 350], 2, new ModuleEffect { Accuracy = 10 }),
        new("scanner", "Scanner", 1, 1, [70, 150, 260], 1, new ModuleEffect { Scanner = 0.2f }),
        new("nightvision", "Night vision", 1, 1, [200], 3, new ModuleEffect { NightVision = true }),
        new("reactor", "Reactor", 3, 0, [120, 240, 420], 1, new ModuleEffect { Power = 5 }),
        new("cooler", "Cooler", 2, 1, [130, 260, 450], 4, new ModuleEffect { Reload = 0.06f }),
        new("scrap", "Scrap collector", 1, 1, [150, 300, 500], 2, new ModuleEffect { Wrecks = 0.5f }),
    ];

    public static readonly IReadOnlyList<WiredAbility> Abilities =
    [
        new("vortex", "Vortex", "VORTEX", 40, 0, 90, 5, 900, 8),
        new("treachery", "Treachery", "ORB_OF_TREACHERY", 45, 0, 90, 4, 700, 8),
        new("discharge", "Discharge", "ELECTRIC_CHARGE", 20, 0, 12, 2, 250, 3),
        new("supercharge", "Supercharge", "SUPERCHARGE_SUPER", 30, 0, 35, 3, 350, 4),
        new("minefield", "Minefield", null, 35, 0, 0, 2, 300, 4),
        new("ionstrike", "Ion call", "ION_ORBITAL_CANNON", 0, 5, 0, 6, 1200, 12),
    ];

    /// <summary>What the waves bring and the hangar sells, by the wave each opens at. The combos are real ones, with rigs of their own.</summary>
    public static readonly IReadOnlyList<WiredTier> Tiers =
    [
        new(1, "AGGRESSOR", "GRUNT", 1, ["R_BOLTER", "R_ELECTRIC_CHARGE"]),
        new(3, "AGGRESSOR", "GRUNT", 2, ["L_BOLTER", "L_ELECTRIC_CHARGE", "L_FLAME_THROWER2"]),
        new(5, "AGGRESSOR", "ASSASSIN", 2, ["L_AUTO_CANNON", "L_PLASMA_RIFLE"]),
        new(7, "AGGRESSOR", "GRUNT", 3, ["LR_HEAVY_BOLTER1_X2"]),
        new(9, "ADMINISTRATOR", "OVERSEER", 2, ["LR_PULSE_RIFLE_X2", "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED"]),
        new(9, "ADMINISTRATOR", "BOSS", 1, ["T_WASP_LIGHT_STING", "T_WASP_METAL_STING"]),
        new(11, "AGGRESSOR", "ASSASSIN", 3, ["LR_AUTO_CANNON_X2", "LR_PLASMA_RIFLE_X2"]),
        new(13, "AGGRESSOR", "KNIGHT", 3, ["LR_HEAVY_BOLTER2_X2", "LR_PLASMA_CANNON1_X2"]),
        new(16, "AGGRESSOR", "BALLISTA", 3, ["T_MULTI_LAUNCHER1", "T_VORTEX"]),
        new(16, "ADMINISTRATOR", "BOSS", 2, ["T_BEE_BOMB"]),
        new(19, "AGGRESSOR", "KNIGHT", 4, ["LR_MULTI_LAUNCHER3_X2", "LR_PLASMA_CANNON2_X2"]),
        new(23, "ADMINISTRATOR", "COMMANDER", 4, ["LR_MULTI_LAUNCHER5_X2", "L_MULTI_LAUNCHER5_R_SUPERCHARGE_SUPER", "L_MULTI_LAUNCHER5_R_TREACHERY"]),
        new(26, "AGGRESSOR", "BALLISTA", 4, ["LR_MULTI_LAUNCHER2_X2"]),
        new(30, "AGGRESSOR", "KNIGHT", 5, ["LRT_MULTI_LAUNCHER4_X3", "LRT_PLASMA_CANNON2_X3"]),
        new(36, "AGGRESSOR", "NINJA", 5, ["L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7"]),
        // Wired's own machines come only after the Gorilla, so that the runs the balance aims at (to wave 20) never meet
        // them; the hangar sells them as escorts from wave 20. No export has them with these weapons: the view puts them
        // together as it does a hero (HeroRig).
        new(38, "AGGRESSOR", "GRUNT", 4, ["LR_HEAVY_BOLTER2_X2_T_AUTO_CANNON"]) { EscortFrom = WiredBalance.OwnEscortsFrom },
        new(38, "AGGRESSOR", "ASSASSIN", 4, ["L_PLASMA_CANNON2_R_PLASMA_CANNON1"]) { EscortFrom = WiredBalance.OwnEscortsFrom },
        new(42, "ADMINISTRATOR", "BOSS", 3, ["LR_PULSE_RIFLE_X2_T_WASP_METAL_STING"]) { EscortFrom = WiredBalance.OwnEscortsFrom },
        new(42, "AGGRESSOR", "ASSASSIN", 6, ["L_MULTI_LAUNCHER1_R_PULSE_CANNON"]) { EscortFrom = WiredBalance.OwnEscortsFrom },
        new(46, "AGGRESSOR", "GRUNT", 5, ["L_PLASMA_CANNON2_R_HEAVY_BOLTER2_T_MULTI_LAUNCHER3"]) { EscortFrom = WiredBalance.OwnEscortsFrom },
        new(46, "AGGRESSOR", "ASSASSIN", 5, ["LR_PLASMA_CANNON2_X2_T_PULSE_RIFLE"]) { EscortFrom = WiredBalance.OwnEscortsFrom },
        new(50, "AGGRESSOR", "BALLISTA", 5, ["LRT_MULTI_LAUNCHER2_X3"]) { EscortFrom = WiredBalance.OwnEscortsFrom },
    ];

    /// <summary>
    /// The tiers the hangar sells as escorts at a wave, the newest first: by the wave each was first sold from, and among
    /// those of one wave the later in the list first.
    /// </summary>
    public static IEnumerable<WiredTier> EscortTiers(int wave) => Tiers.Select((tier, at) => (Tier: tier, At: at))
        .Where(t => t.Tier.EscortFrom <= wave).OrderByDescending(t => t.Tier.EscortFrom).ThenByDescending(t => t.At).Select(t => t.Tier);

    public static WiredChassis? FindChassis(string id) => Chassis.FirstOrDefault(c => c.Id == id);
    public static WiredWeapon? FindWeapon(string id) => Weapons.FirstOrDefault(w => w.Id == id);
    public static WiredModule? FindModule(string id) => Modules.FirstOrDefault(m => m.Id == id);
    public static WiredAbility? FindAbility(string id) => Abilities.FirstOrDefault(a => a.Id == id);
    public static WiredTower? FindTower(string id) => Towers.FirstOrDefault(t => t.Id == id);

    /// <summary>What a catalogue id is, or null for none.</summary>
    public static WiredKind? KindOf(string id) =>
        FindChassis(id) is not null ? WiredKind.Chassis
        : FindWeapon(id) is not null ? WiredKind.Weapon
        : FindModule(id) is not null ? WiredKind.Module
        : FindAbility(id) is not null ? WiredKind.Ability
        : WiredChips.Find(id) is not null ? WiredKind.Chip
        : null;

    /// <summary>What a catalogue id costs before the difficulty; chips cannot be bought and cost nothing.</summary>
    public static int Price(string id) =>
        FindChassis(id)?.Price ?? FindWeapon(id)?.Price ?? FindModule(id)?.Prices[0] ?? FindAbility(id)?.Price ?? 0;

    /// <summary>What the shop may offer for a wave: weapons, modules and abilities whose time has come, in catalogue order.</summary>
    public static List<string> ForSale(int wave) =>
    [
        .. Weapons.Where(w => w.FromWave <= wave).Select(w => w.Id),
        .. Modules.Where(m => m.FromWave <= wave).Select(m => m.Id),
        .. Abilities.Where(a => a.FromWave <= wave).Select(a => a.Id),
    ];

    /// <returns>null if the weapon fits the chassis at that mounting, otherwise why not.</returns>
    public static string? WhyNotFit(WiredChassis chassis, Mounting mounting, WiredWeapon weapon)
    {
        if (chassis.Slot(mounting) is not { } slot)
            return "this chassis has no such slot";
        if (slot.Fixed is not null)
            return "that weapon is part of the chassis";
        // Every chassis outside the Boss line hears the same, the geo locator's glider too, which flies and still has no
        // room for the stings and the bomb.
        if (weapon.GliderOnly && !(chassis.Glider && mounting == Mounting.Top))
            return "that fits only the top slot of the Boss line";
        return weapon.Size > slot.Size ? "the weapon is too big for this slot" : null;
    }

    /// <summary>The copies of one weapon a mounting of the chassis carries as a battery, by the slot's size; 0 for a missing or fixed slot.</summary>
    public static int MostCopies(WiredChassis chassis, Mounting mounting) =>
        chassis.Slot(mounting) is { Fixed: null } slot ? WiredBalance.MostCopies[(int)slot.Size] : 0;

    /// <returns>
    /// null if <paramref name="count"/> copies of the weapon may stand as a battery on that mounting of the chassis, otherwise
    /// why not: what <see cref="WhyNotFit"/> says, then a weapon that forms none, then the slot's size. One copy is a
    /// single weapon, as before batteries.
    /// </returns>
    public static string? WhyNotCopies(WiredChassis chassis, Mounting mounting, WiredWeapon weapon, int count)
    {
        if (WhyNotFit(chassis, mounting, weapon) is { } why)
            return why;
        if (count <= 1)
            return null;
        if (weapon.Single)
            return "that weapon cannot form a battery";
        return count > MostCopies(chassis, mounting) ? Full(chassis.Slot(mounting)!.Size) : null;
    }

    /// <summary>What a slot of a size says to one copy more than it carries.</summary>
    public static string Full(SlotSize size) => size switch
    {
        SlotSize.Light => "a light slot carries one weapon",
        SlotSize.Medium => "a medium slot carries two at most",
        _ => "a heavy slot carries three at most",
    };

    /// <summary>
    /// A number of copies of an item made to stand on a slot of the chassis: from 1 to as many as <see cref="WhyNotCopies"/>
    /// lets the slot carry, and 1 for anything that is no weapon on a weapon slot, or no weapon that fits it. A battery saved
    /// or sent with a bad count still makes a hero.
    /// </summary>
    public static int Copies(WiredChassis chassis, WiredSlot? slot, string catalogue, int count)
    {
        if (slot is not { Kind: WiredSlotKind.Weapon, Index: >= 0 and <= (int)Mounting.Top } at || FindWeapon(catalogue) is not { } weapon)
            return 1;
        count = Math.Min(count, WiredBalance.MostCopies[^1]);
        while (count > 1 && WhyNotCopies(chassis, (Mounting)at.Index, weapon, count) is not null)
            count--;
        return Math.Max(1, count);
    }
}
