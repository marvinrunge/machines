using System.Globalization;
using Machines.Import.Model;

namespace Machines.Import.Parm;

/// <summary>Turns the text of <c>parmdata.dat</c> into typed <see cref="ParmData"/>.</summary>
public static class ParmDataParser
{
    public static ParmData Load(string path) => Parse(File.ReadAllText(path));

    public static ParmData Parse(string text)
    {
        var document = ParmReader.Read(text);

        return new ParmData(
            EvasionStrategies: Records(document, "EVASION_STRATEGIES").Select(ParseEvasion).ToList(),
            Machines: Records(document, "MACHINES").Select(ParseMachine).ToList(),
            Constructions: Records(document, "CONSTRUCTIONS").Select(ParseConstruction).ToList(),
            Weapons: Records(document, "WEAPONS").Select(ParseWeapon).ToList(),
            General: ParseGeneral(RequireSection(document, "GENERAL_DATA")));
    }

    private static ParmSection RequireSection(ParmDocument document, string name) =>
        document.Section(name) ?? throw new ParmFormatException($"missing section '{name}'", 0);

    private static IReadOnlyList<ParmRecord> Records(ParmDocument document, string section) =>
        RequireSection(document, section).Records;

    private static EvasionStrategy ParseEvasion(ParmRecord record)
    {
        var values = new SingleValues(record.Lines, record);
        return new EvasionStrategy(
            Name: values.Text("NAME"),
            GarrisonsPriority: values.Int("GARRISONS_PRIORITY"),
            AggressivesPriority: values.Int("AGGRESSIVES_PRIORITY"),
            TurretsPriority: values.Int("TURRETS_PRIORITY"),
            PodPriority: values.Int("POD_PRIORITY"));
    }

    private static MachineSpec ParseMachine(ParmRecord record)
    {
        var levels = HardwareLevels.For(record.TypeName);
        var software = new HashSet<string> { "SW_COSTS", "SW_RESEARCH_COSTS" };
        var columns = new LevelColumns(record, levels.Count, record.Lines.Where(l => !software.Contains(l.Keyword)));

        var machineLevels = levels.Select((hardwareLevel, i) =>
        {
            var conType = columns.OptionalText("MOST_ADVANCED_CONSTYPE", i);
            return new MachineLevel
            {
                HardwareLevel = hardwareLevel,
                Cost = columns.Int("HW_COSTS", i),
                ResearchCost = columns.Int("HW_RESEARCH_COSTS", i),
                HitPoints = columns.Int("HP", i),
                Armour = columns.Int("ARMOUR", i),
                RepairRate = columns.Float("REPAIR_RATE", i),
                Speed = columns.Float("SPEED", i),
                TaskSpeed = columns.Float("TASK_SPEED", i),
                Acceleration = columns.Float("ACCELERATION", i),
                RotationSpeed = columns.Float("ROTATION_SPEED", i),
                RotationAcceleration = columns.Float("ROTATION_ACCELERATION", i),
                Locomotion = ParseLocomotion(columns.Text("LOCOMOTION", i), columns.LineOf("LOCOMOTION")),
                LocomotionParameter = columns.OptionalFloat("LOCOMOTION_PARAMETER", i),
                HighClearance = columns.Float("HIGH_CLEARANCE", i),
                LowClearance = columns.Float("LOW_CLEARANCE", i),
                Volume = columns.Int("VOLUME", i),
                MinAccessSizeIndex = columns.Int("MIN_ACCESS_SIZE_INDEX", i),
                ScannerRange = columns.Int("SCANNER_RANGE", i),
                Evasion = columns.Text("EPP", i),
                NumberOfWeapons = columns.OptionalInt("NUMBER_WEAPONS", i) ?? 0,
                Accuracy = columns.OptionalInt("ACCURACY", i),
                BodyTurnLink = columns.OptionalText("BODY_TURN_LINK", i),
                BodyTurnRate = columns.OptionalFloat("BODY_TURN_RATE", i),
                NightVision = columns.OptionalText("NVG_CAPABILITY", i) == "YES",
                Capacity = columns.OptionalInt("CAPACITY", i),
                MaxSubordinates = columns.OptionalInt("MAX_SUBORDINATES", i),
                ResearchRate = columns.OptionalInt("RESEARCH_RATE", i),
                DownloadRate = columns.OptionalInt("DOWNLOAD_RATE", i),
                ConstructionRate = columns.OptionalInt("RATE_CONSTRUCTION", i),
                MineralGrade = columns.OptionalInt("MINERAL_GRADE", i),
                MostAdvancedConstruction = conType is null
                    ? null
                    : new ConstructionRef(
                        conType,
                        columns.Text("MOST_ADVANCED_CONSSUBTYPE", i),
                        ParseLevelToken(columns.Text("MOST_ADVANCED_CONSLEVEL", i), columns.LineOf("MOST_ADVANCED_CONSLEVEL"))),
                Values = columns.ValuesAt(i),
            };
        }).ToList();

        var single = new SingleValues(record.Lines.Where(l => software.Contains(l.Keyword)), record);
        return new MachineSpec(
            Type: record.TypeTokens[0],
            SubType: record.TypeTokens.Count > 1 ? record.TypeTokens[1] : null,
            Levels: machineLevels,
            SoftwareCosts: single.IntList("SW_COSTS"),
            SoftwareResearchCosts: single.IntList("SW_RESEARCH_COSTS"));
    }

    private static ConstructionSpec ParseConstruction(ParmRecord record)
    {
        var levels = HardwareLevels.For(record.TypeName);

        // Geometry lines carry their level as the first argument: BOUNDARY LEVEL_3 -15.2 -18.6 15.3 18.8
        bool IsGeometry(ParmLine line) => line.Args.Count > 0 && line.Args[0].StartsWith("LEVEL_", StringComparison.Ordinal);

        var geometry = record.Lines.Where(IsGeometry)
            .Select(l => (Level: ParseLevelToken(l.Args[0], l), Line: l with { Args = l.Args.Skip(1).ToList() }))
            .ToList();
        foreach (var (level, line) in geometry)
        {
            if (!levels.Contains(level))
                throw new ParmFormatException($"{record.TypeName} has no hardware level {level}", line.LineNumber);
        }

        var columns = new LevelColumns(record, levels.Count, record.Lines.Where(l => !IsGeometry(l)));

        var constructionLevels = levels.Select((hardwareLevel, i) => new ConstructionLevel
        {
            HardwareLevel = hardwareLevel,
            Cost = columns.Int("HW_COSTS", i),
            ResearchCost = columns.Int("HW_RESEARCH_COSTS", i),
            HitPoints = columns.Int("HP", i),
            Armour = columns.Int("ARMOUR", i),
            AccessSizeIndex = columns.OptionalInt("ACCESS_SIZE_INDEX", i),
            Capacity = columns.OptionalInt("CAPACITY", i),
            BuildRate = columns.OptionalInt("BUILD_RATES", i),
            ExtractionRate = columns.OptionalInt("EXTRACTION_RATE", i),
            NumberOfWeapons = columns.OptionalInt("NUMBER_WEAPONS", i) ?? 0,
            MaxRotation = columns.OptionalInt("MAX_ROTATION", i),
            Accuracy = columns.OptionalInt("ACCURACY", i),
            RepairRate = columns.OptionalFloat("REPAIR_RATE", i),
            MineDistance = columns.OptionalInt("MINE_DISTANCE", i),
            MineralDistance = columns.OptionalInt("MINERAL_DISTANCE", i),
            Geometry = geometry.Where(g => g.Level == hardwareLevel).Select(g => g.Line).ToList(),
            Values = columns.ValuesAt(i),
        }).ToList();

        return new ConstructionSpec(
            Type: record.TypeTokens[0],
            SubType: record.TypeTokens.Count > 1 ? record.TypeTokens[1] : null,
            Levels: constructionLevels);
    }

    private static WeaponSpec ParseWeapon(ParmRecord record)
    {
        var gameplayKeys = new HashSet<string> { "DAMAGE", "RANGE", "SPEED", "BURST", "RELOAD_TIME" };
        var values = new SingleValues(record.Lines.Where(l => gameplayKeys.Contains(l.Keyword)), record);

        var burst = values.Line("BURST");
        if (burst.Args.Count is < 2 or > 3)
            throw new ParmFormatException("BURST needs a round count, a duration and an optional FIXED or VARIABLE", burst.LineNumber);
        var rounds = ParseInt(burst.Args[0], burst);
        var timing = burst.Args.Count == 3 ? burst.Args[2] : null;
        if (timing is not (null or "FIXED" or "VARIABLE"))
            throw new ParmFormatException($"unknown burst timing '{timing}'", burst.LineNumber);
        if (rounds > 1 && timing is null)
            throw new ParmFormatException("a burst of more than one round needs FIXED or VARIABLE", burst.LineNumber);

        return new WeaponSpec
        {
            TypeName = record.TypeName,
            Damage = values.Float("DAMAGE"),
            Range = values.Float("RANGE"),
            ProjectileSpeed = values.Float("SPEED"),
            BurstRounds = rounds,
            BurstDuration = ParseFloat(burst.Args[1], burst),
            BurstTiming = timing,
            ReloadTime = values.Float("RELOAD_TIME"),
            Presentation = record.Lines.Where(l => !gameplayKeys.Contains(l.Keyword)).ToList(),
        };
    }

    private static GeneralData ParseGeneral(ParmSection section)
    {
        var record = new ParmRecord([section.Name], section.Lines, section.LineNumber);
        var values = new SingleValues(section.Lines, record);
        var look = values.Line("1ST_PERSON_LOOK_UP_DOWN_ANGLES");
        var turn = values.Line("1ST_PERSON_MAX_TURN_RATES");
        RequireArgs(look, 2);
        RequireArgs(turn, 2);

        return new GeneralData(
            FirstPersonLookUpDownRate: values.Float("1ST_PERSON_LOOK_UP_DOWN_RATE"),
            FirstPersonLookMinAngle: ParseFloat(look.Args[0], look),
            FirstPersonLookMaxAngle: ParseFloat(look.Args[1], look),
            FirstPersonMaxFastTurnRate: ParseFloat(turn.Args[0], turn),
            FirstPersonMaxSlowTurnRate: ParseFloat(turn.Args[1], turn),
            StartRotationDistance: values.Float("START_ROTATION_DISTANCE"),
            MaxMoveGradient: values.Float("MAX_MOVE_GRADIENT"),
            MaxTerrainUnevenness: values.Float("MAX_TERRAIN_UNEVENNESS"),
            VirtualDefconInterval: values.Float("VIRTUAL_DEFCON_INTERVAL"),
            PcPathFindingPriority: values.Int("PC_PATH_FINDING_PRIORITY"));
    }

    private static Locomotion ParseLocomotion(string token, ParmLine line) => token switch
    {
        "WHEELS" => Locomotion.Wheels,
        "TRACKS" => Locomotion.Tracks,
        "SPIDER" => Locomotion.Spider,
        "HOVER" => Locomotion.Hover,
        "GLIDER" => Locomotion.Glider,
        _ => throw new ParmFormatException($"unknown locomotion '{token}'", line.LineNumber),
    };

    private static int ParseLevelToken(string token, ParmLine line) =>
        token.StartsWith("LEVEL_", StringComparison.Ordinal)
        && int.TryParse(token.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            ? level
            : throw new ParmFormatException($"expected LEVEL_n, found '{token}'", line.LineNumber);

    private static void RequireArgs(ParmLine line, int count)
    {
        if (line.Args.Count != count)
            throw new ParmFormatException($"{line.Keyword} needs {count} values, found {line.Args.Count}", line.LineNumber);
    }

    internal static int ParseInt(string token, ParmLine line) =>
        int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ParmFormatException($"{line.Keyword}: '{token}' is not a whole number", line.LineNumber);

    internal static float ParseFloat(string token, ParmLine line) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ParmFormatException($"{line.Keyword}: '{token}' is not a number", line.LineNumber);

    /// <summary>Keywords whose values form one column per hardware level.</summary>
    private sealed class LevelColumns
    {
        private readonly Dictionary<string, ParmLine> _lines = new();
        private readonly ParmRecord _record;

        public LevelColumns(ParmRecord record, int levelCount, IEnumerable<ParmLine> lines)
        {
            _record = record;
            foreach (var line in lines)
            {
                if (line.Args.Count != levelCount)
                    throw new ParmFormatException(
                        $"{record.TypeName}: {line.Keyword} has {line.Args.Count} values but the type has {levelCount} hardware levels",
                        line.LineNumber);
                if (!_lines.TryAdd(line.Keyword, line))
                    throw new ParmFormatException($"{record.TypeName}: {line.Keyword} appears twice", line.LineNumber);
            }
        }

        public ParmLine LineOf(string key) =>
            _lines.TryGetValue(key, out var line)
                ? line
                : throw new ParmFormatException($"{_record.TypeName}: missing {key}", _record.LineNumber);

        public string Text(string key, int level) => LineOf(key).Args[level];
        public int Int(string key, int level) => ParseInt(Text(key, level), LineOf(key));
        public float Float(string key, int level) => ParseFloat(Text(key, level), LineOf(key));

        public string? OptionalText(string key, int level) => _lines.TryGetValue(key, out var line) ? line.Args[level] : null;
        public int? OptionalInt(string key, int level) => _lines.TryGetValue(key, out var line) ? ParseInt(line.Args[level], line) : null;
        public float? OptionalFloat(string key, int level) => _lines.TryGetValue(key, out var line) ? ParseFloat(line.Args[level], line) : null;

        public IReadOnlyDictionary<string, string> ValuesAt(int level) =>
            _lines.ToDictionary(kv => kv.Key, kv => kv.Value.Args[level]);
    }

    /// <summary>Keywords that appear once and are not split by level.</summary>
    private sealed class SingleValues
    {
        private readonly Dictionary<string, ParmLine> _lines = new();
        private readonly ParmRecord _record;

        public SingleValues(IEnumerable<ParmLine> lines, ParmRecord record)
        {
            _record = record;
            foreach (var line in lines)
            {
                if (!_lines.TryAdd(line.Keyword, line))
                    throw new ParmFormatException($"{record.TypeName}: {line.Keyword} appears twice", line.LineNumber);
            }
        }

        public ParmLine Line(string key) =>
            _lines.TryGetValue(key, out var line)
                ? line
                : throw new ParmFormatException($"{_record.TypeName}: missing {key}", _record.LineNumber);

        private string One(string key)
        {
            var line = Line(key);
            RequireArgs(line, 1);
            return line.Args[0];
        }

        public string Text(string key) => One(key);
        public int Int(string key) => ParseInt(One(key), Line(key));
        public float Float(string key) => ParseFloat(One(key), Line(key));
        public IReadOnlyList<int> IntList(string key) => Line(key).Args.Select(a => ParseInt(a, Line(key))).ToList();
    }
}
