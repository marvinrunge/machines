namespace Machines.Import.Model;

public enum ResearchKind
{
    Machine,
    Construction,
}

public enum LabType
{
    Military,
    Civilian,
}

/// <summary>
/// How the original identifies a research item: type, optional sub-type, hardware level and
/// optional weapon combo, e.g. <c>AGGRESSOR KNIGHT 4 LR_PLASMA_CANNON2_X2</c>.
/// </summary>
public sealed record ResearchKey(string Type, string? SubType, int HardwareLevel, string? WeaponCombo)
{
    /// <summary>The name used by <c>parmdata.dat</c>, e.g. <c>"AGGRESSOR KNIGHT"</c>.</summary>
    public string TypeName => SubType is null ? Type : $"{Type} {SubType}";

    public override string ToString() =>
        WeaponCombo is null ? $"{TypeName} {HardwareLevel}" : $"{TypeName} {HardwareLevel} {WeaponCombo}";
}

/// <summary>A <c>FACTORY_COST</c> line: change the factory price relative to the parmdata cost, or replace it.</summary>
public sealed record FactoryCostAdjustment(bool Relative, int Amount);

/// <summary>One entry of <c>global.rst</c>.</summary>
public sealed record ResearchItem
{
    public required ResearchKind Kind { get; init; }
    public required ResearchKey Key { get; init; }
    public required int SoftwareLevel { get; init; }
    /// <summary>Lowest technician level needed: 1 Technician, 2 Engineer, 3 Brainbox; 0 needs none.</summary>
    public required int TechLevel { get; init; }
    /// <summary>Research units to complete the item.</summary>
    public required int ResearchCost { get; init; }
    /// <summary>BMUs spent on the research, drawn in step with progress.</summary>
    public required int BuildingCost { get; init; }
    public required LabType Lab { get; init; }
    public ResearchKey? Parent { get; init; }
    public FactoryCostAdjustment? FactoryCost { get; init; }

    /// <summary>
    /// True for items the original creates in code rather than reading from the file:
    /// every level-4 aggressor sub-type also gets a large-missile (<c>LR_LARGE_MISSILE_X2</c>) variant.
    /// No scenario refers to these items.
    /// </summary>
    public bool Implicit { get; init; }

    /// <summary>Line of the item in the file (for implicit items, the line of the item that created it).</summary>
    public required int LineNumber { get; init; }
}

/// <summary>The global research tree from <c>global.rst</c>.</summary>
public sealed record ResearchTree(IReadOnlyList<ResearchItem> Items)
{
    public ResearchItem? Find(ResearchKey key) => Items.FirstOrDefault(i => i.Key == key);

    public ResearchItem Get(ResearchKey key) =>
        Find(key) ?? throw new KeyNotFoundException($"no research item {key}");

    public IEnumerable<ResearchItem> Children(ResearchItem parent) => Items.Where(i => i.Parent == parent.Key);

    /// <summary>
    /// The BMU price a factory charges for one machine of this item: the parmdata cost of the machine,
    /// changed by <c>FACTORY_COST</c>. As in the original, only adjustments above zero are applied,
    /// so negative <c>RELATIVE</c> values in the file have no effect. Constructions have no factory price.
    /// </summary>
    public static int FactoryPrice(ResearchItem item, ParmData parm)
    {
        if (item.Kind != ResearchKind.Machine)
            return 0;

        var price = parm.Machine(item.Key.TypeName).Level(item.Key.HardwareLevel).Cost;

        if (item.FactoryCost is { Amount: > 0 } adjustment)
            price = adjustment.Relative ? price + adjustment.Amount : adjustment.Amount;

        return price;
    }
}

/// <summary>One line of <c>global.cst</c>.</summary>
public sealed record ConstructionOrderEntry(string Type, string? SubType, int HardwareLevel)
{
    public bool Matches(ConstructionRef reference) =>
        Type == reference.Type && SubType == reference.SubType && HardwareLevel == reference.HardwareLevel;

    public override string ToString() => SubType is null ? $"{Type} {HardwareLevel}" : $"{Type} {SubType} {HardwareLevel}";
}

/// <summary>
/// The construction order from <c>global.cst</c>. A constructor can raise every building up to and
/// including its most advanced entry, once the race has built (not just researched) that entry.
/// </summary>
public sealed record ConstructionOrder(IReadOnlyList<ConstructionOrderEntry> Entries)
{
    public int IndexOf(ConstructionRef reference)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Matches(reference))
                return i;
        }
        throw new KeyNotFoundException($"{reference} is not in the construction order");
    }

    public IEnumerable<ConstructionOrderEntry> UpTo(ConstructionRef mostAdvanced) => Entries.Take(IndexOf(mostAdvanced) + 1);
}
