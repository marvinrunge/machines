using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

// What goods do, in words: the values show what they change on the hero, and these lines all the rest.
public static partial class HangarText
{
    /// <summary>The colour of a chip's family: Offensive red, Defensive blue, Salvage yellow, Command green.</summary>
    public static Color FamilyColour(ChipFamily family) => family switch
    {
        ChipFamily.Offensive => new Color(1f, 0.32f, 0.24f),
        ChipFamily.Defensive => new Color(0.32f, 0.58f, 1f),
        ChipFamily.Salvage => new Color(1f, 0.82f, 0.25f),
        _ => new Color(0.42f, 0.98f, 0.3f),
    };

    /// <summary>The colour of the family of a chip; null for what is no chip.</summary>
    public static Color? Family(WiredItem item) => item.Kind == WiredKind.Chip && WiredChips.Find(item.Catalogue) is { } chip ? FamilyColour(chip.Family) : null;

    /// <summary>A machine's name, with its level.</summary>
    public static string Machine(ResearchKey key) => WiredMachines.Find(key.TypeName, key.HardwareLevel) is { } own ? Lang.T(own.Name) :
        $"{Lang.T(Studio.Names.GetValueOrDefault(key.SubType ?? key.Type, key.SubType ?? key.Type))} {key.HardwareLevel}";

    /// <summary>What a machine carries, each kind of weapon once.</summary>
    public static IEnumerable<string> Weapons(ResearchKey key) => key.WeaponCombo is { } combo
        ? WeaponCombos.Split(combo).Select(w => w.Weapon).Distinct().Select(WeaponName)
        : [];

    /// <summary>A weapon's name, for those that do not read as one the way they are written in parmdata.</summary>
    public static string WeaponName(string weapon) => weapon switch
    {
        "SUPERCHARGE_ADVANCED" => $"{Lang.T("Supercharge")} 1",
        "SUPERCHARGE_SUPER" => $"{Lang.T("Supercharge")} 2",
        "ORB_OF_TREACHERY" => Lang.T("Treachery"),
        _ => Lang.T(Studio.Title(weapon)),
    };

    private static string Number(float value) => Math.Round(value, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', Lang.Code == "de" ? ',' : '.');

    private static string Percent(float share) => Number(share * 100);

    /// <summary>
    /// What a good does, a line for the hangar's banner, for a chip at the strength it would have, for a chassis as a
    /// <paramref name="battle"/> has it or a run; null for what needs no words.
    /// </summary>
    public static string? Effect(WiredItem item, float? strength = null, bool battle = false) => item.Kind switch
    {
        WiredKind.Chip => WiredChips.Find(item.Catalogue) is { } chip ? ChipEffect(chip, strength ?? chip.Value) : null,
        WiredKind.Module => ModuleEffect(item),
        WiredKind.Ability => AbilityEffect(item.Catalogue),
        WiredKind.Chassis => ChassisEffect(item.Catalogue, battle),
        _ => null,
    };

    /// <summary>
    /// What a chassis has of its own besides its slots, a line for the hangar's banner; null for a chassis with nothing of its
    /// own. In a <paramref name="battle"/> without what only a run has: wrecks, a squad to call, towers. The values are those
    /// the world plays by, and the key the one the player has given the special.
    /// </summary>
    public static string? ChassisEffect(string id, bool battle = false)
    {
        if (WiredCatalogue.FindChassis(id) is not { } chassis)
            return null;
        var key = KeyMap.Name(KeyMap.Act.Special);
        var level = Paths.Data.Parm.Machine(chassis.TypeName).Level(chassis.Level);
        var special = chassis.Special;
        string? line = null;
        if (id == "grunt4")
            line = Lang.T("Third slot: a light weapon on top, the cheapest way to three slots.");
        else if (id == "reaper4")
            line = Lang.T("First heavy slot of the line: any heavy weapon fits on the left.");
        else if (special.HasFlag(ChassisSpecial.Plough))
            line = Lang.T("Ram plough: hits from the front cost {0} % less. A built-in dash every {1} s knocks over whatever it meets and deals it {2} damage.",
                Percent(1 - WiredBalance.PloughShare), Number(chassis.Dash ?? 0), WiredBalance.RamDamage);
        else if (special.HasFlag(ChassisSpecial.Harvest))
            line = battle
                ? Lang.T("Harvest: every kill within {0} m gives back {1} % armour.", Number(WiredBalance.HarvestReach), Percent(WiredBalance.HarvestArmour))
                : Lang.T("Harvest: every kill within {0} m gives back {1} % armour and pulls in the wrecks there.", Number(WiredBalance.HarvestReach), Percent(WiredBalance.HarvestArmour));
        else if (special.HasFlag(ChassisSpecial.Lurk))
            line = Lang.T("Lurk: after {0} s standing still the first shot flies {1} % further and deals {2} % more damage.",
                Number(WiredBalance.LurkStill), Percent(WiredBalance.LurkRange - 1), Percent(WiredBalance.LurkDamage - 1));
        else if (special.HasFlag(ChassisSpecial.Anchor))
            line = Lang.T("Anchor (key {0}): after {1} s the tracks lock and it stands: +{2} % range, −{3} % reload, +{4} % armour. Releasing takes {1} s.",
                key, Number(WiredBalance.AnchorTime), Percent(WiredBalance.AnchorRange - 1), Percent(1 - WiredBalance.AnchorReload), Percent(WiredBalance.AnchorArmour - 1));
        else if (special.HasFlag(ChassisSpecial.Afterburner))
            line = Lang.T("Afterburner: a dash every {0} s without thrusters; the module slot stays free.", Number(chassis.Dash ?? 0));
        else if (special.HasFlag(ChassisSpecial.Field))
            line = battle
                ? Lang.T("Field marshal: team-mates within {0} m get +{1} accuracy and {2} % more repair.", Number(WiredBalance.FieldReach), Number(WiredBalance.FieldAim), Percent(WiredBalance.FieldRepair))
                : Lang.T("Field marshal: squad +{0}. Within {1} m the squad gets +{2} accuracy and {3} % more repair. Rally (key {4}): the squad teleports to you, every {5} s.",
                    WiredBalance.FieldSquad, Number(WiredBalance.FieldReach), Number(WiredBalance.FieldAim), Percent(WiredBalance.FieldRepair), key, Number(WiredBalance.RallyCooldown));
        else if (special.HasFlag(ChassisSpecial.Towers))
            line = battle
                ? Lang.T("Raises no towers in a battle.")
                : Lang.T("Towers (key {0}): up to {1} at once – {2} – at the original's prices.", key, chassis.Towers, TowerList(chassis, level));
        else if (special.HasFlag(ChassisSpecial.Shadow))
            line = Lang.T("Shadow: after {0} s without firing, enemies beyond {1} m lose their lock. Mines (key {2}): carries {3}.",
                Number(WiredBalance.ShadowQuiet), Number(WiredBalance.ShadowReach), key, level.MineCapacity ?? 0);
        else if (special.HasFlag(ChassisSpecial.Mines))
            line = Lang.T("Mines (key {0}): carries {1}.", key, level.MineCapacity ?? 0);
        else if (special.HasFlag(ChassisSpecial.Locate))
        {
            line = Lang.T("Locate (key {0}): every enemy within the scanner's reach is located for {1} s and takes {2} % more damage; every {3} s.",
                key, Number(WiredBalance.LocateTime), Percent(WiredBalance.LocateShare - 1), Number(WiredBalance.LocateCooldown));
            // The Boss line flies too but says nothing of it: the locator's glider is the first flyer on the way.
            if (level.Locomotion == Locomotion.Glider)
                line += " " + Lang.T("Flies: weapons of {0} m or less cannot reach it, and it crosses water.", 20);
        }
        return line;
    }

    /// <summary>
    /// What a chassis' special is called, for the shop's comparison; null for none. Sarge and the Thresher have only their
    /// slots, which the comparison names anyway.
    /// </summary>
    public static string? SpecialName(WiredChassis chassis)
    {
        var names = new (ChassisSpecial Special, string Name)[]
        {
            (ChassisSpecial.Plough, "Ram plough"), (ChassisSpecial.Harvest, "Harvest"), (ChassisSpecial.Lurk, "Lurk"),
            (ChassisSpecial.Anchor, "Anchor"), (ChassisSpecial.Afterburner, "Afterburner"), (ChassisSpecial.Field, "Field marshal"),
            (ChassisSpecial.Towers, "Towers"), (ChassisSpecial.Shadow, "Shadow"), (ChassisSpecial.Mines, "Mines"), (ChassisSpecial.Locate, "Locate"),
        }.Where(n => chassis.Special.HasFlag(n.Special)).Select(n => Lang.T(n.Name)).ToList();
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>
    /// The towers a constructor raises, by kind with their levels: "Turret 1–3, Sentry 3–4, Launcher 4". The same rule as the
    /// world's (<c>GameWorld.TowersFor</c>): the catalogue's towers up to its machine's most advanced construction.
    /// </summary>
    private static string TowerList(WiredChassis chassis, MachineLevel level)
    {
        if (level.MostAdvancedConstruction is not { } most)
            return "";
        var order = Paths.Data.ConstructionOrder.UpTo(most).Where(e => e.Type == WiredTower.Type).ToList();
        var towers = WiredCatalogue.Towers.Where(t => order.Any(e => e.SubType == t.SubType && e.HardwareLevel == t.Level)).ToList();
        return string.Join(", ", towers.GroupBy(t => t.SubType).Select(kind =>
        {
            var (low, high) = (kind.Min(t => t.Level), kind.Max(t => t.Level));
            var name = Lang.T(Studio.Names.GetValueOrDefault(kind.Key, kind.Key));
            return low == high ? $"{name} {low}" : $"{name} {low}–{high}";
        }));
    }

    /// <summary>A chassis that dashes by itself, or sees in the dark by itself, has little or nothing from the module that does that.</summary>
    private static (string Note, string Line)? ModuleGift(WiredItem item, WiredChassis chassis)
    {
        if (WiredCatalogue.FindModule(item.Catalogue) is not { Effect: var effect })
            return null;
        if (effect.Dash && chassis.Dash is { } dash)
            return ("Adds little", Lang.T("Thrusters add little on this chassis: it dashes every {0} s by itself.", Number(dash)));
        if (effect.NightVision && WiredMachines.AsHero(chassis.TypeName, Paths.Data.Parm.Machine(chassis.TypeName).Level(chassis.Level)).NightVision)
            return ("Adds nothing", Lang.T("This chassis sees in the dark by itself: the module adds nothing."));
        return null;
    }

    /// <summary>A few words on a module's card where the chassis has its gift already: "Adds little"; null where the module brings what it says.</summary>
    public static string? ModuleNote(WiredItem item, WiredChassis chassis) => ModuleGift(item, chassis) is { } gift ? Lang.T(gift.Note) : null;

    /// <summary>The banner's sentence on a module the chassis makes all but useless; null where the module brings what it says.</summary>
    public static string? ModuleHint(WiredItem item, WiredChassis chassis) => ModuleGift(item, chassis)?.Line;

    private static string? ChipEffect(WiredChip chip, float value)
    {
        var limit = chip.Value > 0 ? chip.Limit * value / chip.Value : chip.Limit;
        return chip.Id switch
        {
            "overload" => Lang.T("Below {0} % hit points the reloads run {1} % faster.", Percent(chip.Limit), Percent(value)),
            "marksman" => Lang.T("{0} % more range.", Percent(value)),
            "sustained" => Lang.T("Each second of unbroken fire adds {0} % damage, up to {1} %.", Percent(value), Percent(limit)),
            "revenge" => Lang.T("{0} % more damage for {1} s once the armour is shot away.", Percent(value), Number(chip.Limit)),
            "shrapnel" => Lang.T("Kills explode for {0} % of the victim's hit points within {1} m.", Percent(value), Number(chip.Limit)),
            "hotbarrels" => Lang.T("{0} % more damage; each weapon draws {1} power more.", Percent(value), Number(chip.Limit)),
            "lastditch" => Lang.T("Once a wave a killing hit leaves 1 hit point and {0} s of safety.", Number(value)),
            "reactive" => Lang.T("A hit takes {0} % off the next.", Percent(MathF.Min(0.5f, value))),
            "nano" => Lang.T(value == 1 ? "{0} hit point every {1} s out of fire." : "{0} hit points every {1} s out of fire.", Number(value), Number(chip.Limit)),
            "tough" => Lang.T("{0} % more armour.", Percent(value)),
            "bastion" => Lang.T("Armour counts {0} % more within {1} m of the pod.", Percent(value), Number(chip.Limit)),
            "damper" => Lang.T("Blasts do {0} % less.", Percent(MathF.Min(0.9f, value))),
            "magnet" => Lang.T("Wrecks come from {0} m away.", Number(value)),
            "recycler" => Lang.T("Selling brings {0} % of what was paid.", WiredChips.SellPercent(value)),
            "trader" => Lang.T("The shop is {0} % cheaper.", Percent(value)),
            "trove" => Lang.T("Elites leave crates {0} % more often.", Percent(value)),
            "vein" => Lang.T("{0} BMUs more for a wave, and {0} more for every {1} waves behind it.", Number(value), Number(chip.Limit)),
            "savings" => Lang.T("{0} % of the bank stays through a death.", Percent(MathF.Min(1, value))),
            "booster" => Lang.T("The squad aims {0} points better within {1} m of the hero.", Number(value), Number(chip.Limit)),
            "tactics" => Lang.T(MathF.Ceiling(value) == 1 ? "{0} escort more, as far as the largest squad." : "{0} escorts more, as far as the largest squad.", Number(MathF.Ceiling(value))),
            "rally" => Lang.T("The squad runs {0} % faster while it follows.", Percent(value)),
            "workshop" => Lang.T("The squad mends {0} % of its hit points a second within {1} m of the hero.", Percent(value), Number(chip.Limit)),
            "veterans" => Lang.T(MathF.Ceiling(value) == 1 ? "Escorts come {0} hardware level better where there are any." : "Escorts come {0} hardware levels better where there are any.", Number(MathF.Ceiling(value))),
            "cover" => Lang.T("The squad turns on what hits the hero."),
            _ => null,
        };
    }

    /// <summary>What the values do not show of a module: all but those shares and points of the chassis' own that stand there anyway.</summary>
    private static string? ModuleEffect(WiredItem item)
    {
        if (WiredCatalogue.FindModule(item.Catalogue) is not { Effect: var effect })
            return null;
        if (effect.Dash)
            return Lang.T("A dash, every {0} s.", WiredBalance.DashCooldowns[Math.Clamp(item.Mk - 1, 0, WiredBalance.DashCooldowns.Count - 1)]);
        if (effect.Wrecks > 0)
            return Lang.T("Wrecks bring {0} % more.", Percent(effect.Wrecks * item.Mk));
        if (effect.NightVision)
            return Lang.T("Sees in the dark: night waves take nothing from its aim.");
        if (effect.Reload > 0)
            return Lang.T("Reloads {0} % faster.", Percent(effect.Reload * item.Mk));
        return null;
    }

    private static string? AbilityEffect(string ability)
    {
        if (WiredCatalogue.FindAbility(ability) is not { } entry)
            return null;
        var wait = entry.CooldownWaves > 0 ? Lang.T("every {0} waves", entry.CooldownWaves) : Lang.T("every {0} s", entry.Cooldown);
        var what = ability switch
        {
            "vortex" => Lang.T("Opens a vortex where it is aimed"),
            "treachery" => Lang.T("Throws an orb that turns the machine it hits"),
            "discharge" => Lang.T("Lightning strikes what stands within {0} m", entry.Reach),
            "supercharge" => Lang.T("The hero and the escorts near it get hit points back"),
            "minefield" => Lang.T("Lays a row of mines ahead of the hero"),
            "ionstrike" => Lang.T("Calls the ion cannon down on where it is aimed"),
            _ => null,
        };
        return what is null ? null : $"{what}, {wait}.";
    }
}
