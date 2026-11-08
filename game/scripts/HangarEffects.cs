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
    public static string Machine(ResearchKey key) =>
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

    /// <summary>What a good does, a line for the hangar's banner, for a chip at the strength it would have; null for what needs no words.</summary>
    public static string? Effect(WiredItem item, float? strength = null) => item.Kind switch
    {
        WiredKind.Chip => WiredChips.Find(item.Catalogue) is { } chip ? ChipEffect(chip, strength ?? chip.Value) : null,
        WiredKind.Module => ModuleEffect(item),
        WiredKind.Ability => AbilityEffect(item.Catalogue),
        _ => null,
    };

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
