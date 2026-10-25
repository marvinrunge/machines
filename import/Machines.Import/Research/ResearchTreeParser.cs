using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Research;

/// <summary>
/// Reads <c>global.rst</c>. Follows <c>MachLogResearchTree::readAllItems</c>, including the
/// large-missile item it adds for every level-4 aggressor.
/// </summary>
public static class ResearchTreeParser
{
    public static ResearchTree Load(string path) => Parse(File.ReadAllText(path));

    public static ResearchTree Parse(string text)
    {
        var items = new List<ResearchItem>();
        using var lines = TextLines.Read(text).GetEnumerator();

        while (lines.MoveNext())
        {
            var header = lines.Current;
            var kind = header.Keyword switch
            {
                "MACHINE" => ResearchKind.Machine,
                "CONSTRUCTION" => ResearchKind.Construction,
                _ => throw new ParmFormatException($"expected MACHINE or CONSTRUCTION, found '{header.Keyword}'", header.LineNumber),
            };
            if (header.Args.Count is < 1 or > 2)
                throw new ParmFormatException($"{header.Keyword} needs a type and an optional sub-type", header.LineNumber);

            var item = ParseItem(kind, header, lines);
            items.Add(item);

            // The original adds this for every level-4 aggressor entry. Knight and Ballista have
            // several, but lookups take the first match, so later copies were unreachable; we keep one.
            // No scenario refers to these items (the combo belongs to the Leviathan launcher).
            var largeMissile = item.Key with { WeaponCombo = "LR_LARGE_MISSILE_X2" };
            if (kind == ResearchKind.Machine && item.Key is { Type: "AGGRESSOR", HardwareLevel: 4 }
                && items.All(i => i.Key != largeMissile))
            {
                items.Add(item with
                {
                    Key = largeMissile,
                    TechLevel = 1,
                    Parent = null,
                    FactoryCost = new FactoryCostAdjustment(Relative: false, Amount: 100),
                    Implicit = true,
                });
            }
        }

        var duplicates = items.GroupBy(i => i.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ParmFormatException($"research item {duplicates[0]} appears twice", items.First(i => i.Key == duplicates[0]).LineNumber);

        var tree = new ResearchTree(items);
        foreach (var item in items.Where(i => i.Parent is not null))
        {
            if (tree.Find(item.Parent!) is null)
                throw new ParmFormatException($"{item.Key}: parent {item.Parent} is not in the tree", item.LineNumber);
        }
        return tree;
    }

    private static ResearchItem ParseItem(ResearchKind kind, ParmLine header, IEnumerator<ParmLine> lines)
    {
        int? hardwareLevel = null, softwareLevel = null, techLevel = null, researchCost = null, buildingCost = null;
        var lab = LabType.Military;
        string? weaponCombo = null;
        ResearchKey? parent = null;
        FactoryCostAdjustment? factoryCost = null;

        while (true)
        {
            if (!lines.MoveNext())
                throw new ParmFormatException($"{header.Keyword} {string.Join(' ', header.Args)} is never closed with END", header.LineNumber);

            var line = lines.Current;
            switch (line.Keyword)
            {
                case "END":
                    var key = new ResearchKey(
                        header.Args[0],
                        header.Args.Count > 1 ? header.Args[1] : null,
                        Required(hardwareLevel, "HWLEVEL", header),
                        weaponCombo);
                    return new ResearchItem
                    {
                        Kind = kind,
                        Key = key,
                        SoftwareLevel = Required(softwareLevel, "SWLEVEL", header),
                        TechLevel = Required(techLevel, "TECHLEVEL", header),
                        ResearchCost = Required(researchCost, "RESEARCH_COST", header),
                        BuildingCost = Required(buildingCost, "BUILDING_COST", header),
                        Lab = lab,
                        Parent = parent,
                        FactoryCost = factoryCost,
                        LineNumber = header.LineNumber,
                    };

                case "HWLEVEL": hardwareLevel = SingleInt(line); break;
                case "SWLEVEL": softwareLevel = SingleInt(line); break;
                case "TECHLEVEL": techLevel = SingleInt(line); break;
                case "RESEARCH_COST": researchCost = SingleInt(line); break;
                case "BUILDING_COST": buildingCost = SingleInt(line); break;
                case "MILITARY": lab = LabType.Military; break;
                case "CIVILIAN": lab = LabType.Civilian; break;

                case "WCOMBO":
                    RequireArgs(line, 1);
                    weaponCombo = line.Args[0];
                    break;

                case "FACTORY_COST":
                    RequireArgs(line, 2);
                    if (line.Args[0] is not ("RELATIVE" or "ABSOLUTE"))
                        throw new ParmFormatException($"FACTORY_COST must be RELATIVE or ABSOLUTE, found '{line.Args[0]}'", line.LineNumber);
                    factoryCost = new FactoryCostAdjustment(line.Args[0] == "RELATIVE", ParmDataParser.ParseInt(line.Args[1], line));
                    break;

                case "PARENT":
                    // PARENT <type> <hwlevel> [<subtype> [<weapon combo>]]
                    if (line.Args.Count is < 2 or > 4)
                        throw new ParmFormatException("PARENT needs a type, a level, and an optional sub-type and weapon combo", line.LineNumber);
                    parent = new ResearchKey(
                        line.Args[0],
                        line.Args.Count > 2 ? line.Args[2] : null,
                        ParmDataParser.ParseInt(line.Args[1], line),
                        line.Args.Count > 3 ? line.Args[3] : null);
                    break;

                default:
                    throw new ParmFormatException($"unknown research keyword '{line.Keyword}'", line.LineNumber);
            }
        }
    }

    private static int SingleInt(ParmLine line)
    {
        RequireArgs(line, 1);
        return ParmDataParser.ParseInt(line.Args[0], line);
    }

    private static void RequireArgs(ParmLine line, int count)
    {
        if (line.Args.Count != count)
            throw new ParmFormatException($"{line.Keyword} needs {count} values, found {line.Args.Count}", line.LineNumber);
    }

    private static int Required(int? value, string keyword, ParmLine header) =>
        value ?? throw new ParmFormatException($"{header.Keyword} {string.Join(' ', header.Args)} has no {keyword}", header.LineNumber);
}
