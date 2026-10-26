using System.Globalization;
using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Scenarios;

/// <summary>
/// Reads a <c>.scn</c> file. Follows <c>MachLogScenario::load</c>, <c>MachLogArtefacts::parseArtefactsSection</c>
/// and <c>MachLogActions::parseActionsSection</c>. Type names are normalised to the <c>parmdata.dat</c>
/// spelling (<c>HWLAB</c> → <c>HARDWARE_LAB</c>, <c>MISSILE</c> → <c>MISSILE_EMPLACEMENT</c>,
/// <c>LAB_MILITARY</c> → <c>MILITARY</c>).
/// </summary>
public static class ScenarioParser
{
    public static Scenario Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (ParmFormatException e)
        {
            throw new ParmFormatException($"{Path.GetFileName(path)}: {e.Message}", e.LineNumber);
        }
    }

    public static Scenario Parse(string text) => new Reader([.. TextLines.Read(text)]).Read();

    private sealed class Reader(List<ParmLine> lines)
    {
        private int _next;

        private readonly Dictionary<Race, DefaultController> _defaultPlayers = new();
        private readonly List<MineralSite> _sites = [];
        private readonly List<RaceSetup> _races = [];
        private readonly List<ScenarioCondition> _conditions = [];
        private readonly List<ScenarioAction> _actions = [];
        private readonly List<ConstructionRestriction> _restrictions = [];
        private readonly List<ParmLine> _ignored = [];
        private readonly List<ScenarioWarning> _warnings = [];
        private ResearchFiles? _research;
        private ArtefactSection? _artefacts;

        public Scenario Read()
        {
            while (TryTake(out var line))
            {
                switch (line.Keyword)
                {
                    case "RACES_DEFAULT": ReadRacesDefault(); break;
                    case "MINERAL": ReadMineralSites(line); break;
                    case "RACE": _races.Add(ReadRace(line)); break;
                    case "RESEARCH_ITEMS": ReadResearchItems(line); break;
                    case "ARTEFACTS": ReadArtefacts(line); break;
                    case "CONDITIONS": ReadConditionsAndActions(line); break;
                    case "RESTRICT_CONSTRUCTION": _restrictions.Add(ReadRestriction(line)); break;
                    default: _ignored.Add(line); break;
                }
            }

            foreach (var race in Enum.GetValues<Race>())
                _defaultPlayers.TryAdd(race, DefaultController.None);

            var duplicate = _races.GroupBy(r => r.Race).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new ParmFormatException($"race {duplicate.Key} is defined twice", duplicate.Last().LineNumber);

            CheckConditionReferences();

            return new Scenario
            {
                DefaultPlayers = _defaultPlayers,
                MineralSites = _sites,
                Races = _races,
                Research = _research,
                Artefacts = _artefacts,
                Conditions = _conditions,
                Actions = _actions,
                ConstructionRestrictions = _restrictions,
                IgnoredLines = _ignored,
                Warnings = _warnings,
            };
        }

        // ---- sections -------------------------------------------------------------------------

        private void ReadRacesDefault()
        {
            foreach (var line in Until("ENDRACES_DEFAULT"))
            {
                var race = ParseRace(line.Keyword, line);
                var controller = line.Args.Count > 0 ? line.Args[0] : "none";
                _defaultPlayers[race] = controller switch
                {
                    "PC" or "PC_LOCAL" => DefaultController.Player,
                    "AI" or "AI_LOCAL" => DefaultController.Ai,
                    _ => DefaultController.None,
                };
            }
        }

        private void ReadMineralSites(ParmLine header)
        {
            if (header.Args is not ["SITES"])
                throw new ParmFormatException("expected MINERAL SITES", header.LineNumber);
            if (_sites.Count > 0)
                throw new ParmFormatException("MINERAL SITES appears twice", header.LineNumber);

            foreach (var line in Until("ENDSITES"))
            {
                Expect(line, "SITE");
                ArgCount(line, 5, 6);
                var grade = Int(line, 0);
                var ore = Int(line, 1);
                if (grade is < 1 or > 4)
                    throw new ParmFormatException($"mineral grade {grade} is not between 1 and 4", line.LineNumber);
                if (ore <= 0)
                    throw new ParmFormatException("a mineral site needs ore", line.LineNumber);

                _sites.Add(new MineralSite(grade, ore, Vec3(line, 2), line.Args.Count == 6 ? ParseResourceLevel(line.Args[5], line) : ResourceLevel.Default, line.LineNumber));
            }
        }

        private RaceSetup ReadRace(ParmLine header)
        {
            ArgCount(header, 1, 2);
            var race = ParseRace(header.Args[0], header);
            var script = header.Args.Count > 1 && header.Args[1] != "none" ? header.Args[1] : null;

            CameraStart? camera = null;
            int? bmu = null, ore = null;
            var scoreDisplayed = true;
            string? virtualBeacon = null;
            List<Race> allies = [];
            List<Vector2> aggressorPoints = [], administratorPoints = [];
            List<ScenarioMachine> machines = [];
            List<ScenarioConstruction> constructions = [];
            Race? displayAs = null;
            var section = "";

            foreach (var line in Until("ENDRACE"))
            {
                switch (line.Keyword)
                {
                    case "MACHINES":
                    case "CONSTRUCTIONS":
                        ArgCount(line, 0, 0);
                        section = line.Keyword;
                        break;

                    case "CAMERA_POS":
                        ArgCount(line, 7, 7);
                        if (line.Args[6] is not ("ZENITH" or "GROUND" or "FREE_MOVING" or "THIRD_PERSON"))
                            throw new ParmFormatException($"unknown camera type '{line.Args[6]}'", line.LineNumber);
                        camera = new CameraStart(Vec3(line, 0), Float(line, 3), Float(line, 4), Float(line, 5), line.Args[6]);
                        break;

                    case "NBMU": ArgCount(line, 1, 1); bmu = Int(line, 0); break;
                    case "NORE": ArgCount(line, 1, 1); ore = Int(line, 0); break;
                    case "SCORE": ArgCount(line, 1, 1); scoreDisplayed = line.Args[0] != "OFF"; break;
                    case "ALLY": ArgCount(line, 1, 1); allies.Add(ParseRace(line.Args[0], line)); break;

                    case "VIRTUAL_BEACON":
                        ArgCount(line, 1, 1);
                        if (line.Args[0] is not ("NO_BEACON" or "LEVEL_1_BEACON" or "LEVEL_3_BEACON"))
                            throw new ParmFormatException($"unknown beacon type '{line.Args[0]}'", line.LineNumber);
                        virtualBeacon = line.Args[0] == "NO_BEACON" ? null : line.Args[0];
                        break;

                    case "DISPLAY":
                        ArgCount(line, 2, 2);
                        displayAs = ParseRace(line.Args[1], line);
                        break;

                    case "ENDDISPLAY":
                        displayAs = null;
                        break;

                    case "ASSEMBLY":
                        ArgCount(line, 3, 3);
                        var point = new Vector2(Float(line, 1), Float(line, 2));
                        if (line.Args[0] == "AGGRESSOR")
                            aggressorPoints.Add(point);
                        else if (line.Args[0] == "ADMINISTRATOR")
                            administratorPoints.Add(point);
                        else
                            throw new ParmFormatException($"assembly point type must be AGGRESSOR or ADMINISTRATOR, found '{line.Args[0]}'", line.LineNumber);
                        break;

                    case "ALTERNATIVE_SITE" when section == "CONSTRUCTIONS":
                        ArgCount(line, 4, 4);
                        if (constructions.Count == 0 || !constructions[^1].NotBuilt)
                            throw new ParmFormatException("ALTERNATIVE_SITE must follow a NOT_BUILT construction", line.LineNumber);
                        var last = constructions[^1];
                        constructions[^1] = last with
                        {
                            AlternativeSites = [.. last.AlternativeSites, (Vec3(line, 0), Orientation(line, 3))],
                        };
                        break;

                    default:
                        if (section == "MACHINES")
                            machines.Add(ParseMachine(line, displayAs));
                        else if (section == "CONSTRUCTIONS")
                            constructions.Add(ParseConstruction(line, displayAs));
                        else
                            throw new ParmFormatException($"unexpected '{line.Keyword}' in RACE {race}", line.LineNumber);
                        break;
                }
            }

            return new RaceSetup
            {
                Race = race,
                AiScript = script,
                Camera = camera,
                Bmu = bmu,
                Ore = ore,
                ScoreDisplayed = scoreDisplayed,
                Allies = allies,
                VirtualBeacon = virtualBeacon,
                AggressorAssemblyPoints = aggressorPoints,
                AdministratorAssemblyPoints = administratorPoints,
                Machines = machines,
                Constructions = constructions,
                LineNumber = header.LineNumber,
            };
        }

        private void ReadResearchItems(ParmLine header)
        {
            ArgCount(header, 1, 4);
            var paths = header.Args;
            _research = new ResearchFiles(
                Default: paths[0],
                Low: paths.Count > 1 ? paths[1] : paths[0],
                Medium: paths.Count > 2 ? paths[2] : paths[0],
                High: paths.Count > 3 ? paths[3] : paths[0]);

            foreach (var line in Until("ENDRESEARCH_ITEMS"))
                throw new ParmFormatException($"unexpected '{line.Keyword}' before ENDRESEARCH_ITEMS", line.LineNumber);
        }

        private void ReadArtefacts(ParmLine header)
        {
            string? models = null;
            List<ArtefactType> types = [];
            List<ArtefactPlacement> placements = [];
            List<ArtefactOnDestroy> onDestroy = [];

            foreach (var line in Until("ENDARTEFACTS"))
            {
                switch (line.Keyword)
                {
                    case "MODELS":
                        ArgCount(line, 1, 1);
                        models = line.Args[0];
                        break;

                    case "DUMB":
                        ArgCount(line, 1, 1);
                        types.Add(new ArtefactType(line.Args[0], false, null, null, null, null, null, []));
                        break;

                    case "ACTIVE":
                        // ACTIVE name COST n HP n ARMOUR n IDS n HEIGHT h BOUNDARY x y x y ...
                        if (line.Args.Count < 18 || line.Args[1] != "COST" || line.Args[3] != "HP" || line.Args[5] != "ARMOUR"
                            || line.Args[7] != "IDS" || line.Args[9] != "HEIGHT" || line.Args[11] != "BOUNDARY")
                            throw new ParmFormatException("expected ACTIVE name COST n HP n ARMOUR n IDS n HEIGHT h BOUNDARY x y ...", line.LineNumber);
                        var coordinates = line.Args.Count - 12;
                        if (coordinates % 2 != 0)
                            throw new ParmFormatException("BOUNDARY has an odd number of coordinates", line.LineNumber);
                        types.Add(new ArtefactType(
                            line.Args[0], true, Int(line, 2), Int(line, 4), Int(line, 6), Int(line, 8), Float(line, 10),
                            Enumerable.Range(0, coordinates / 2).Select(i => new Vector2(Float(line, 12 + 2 * i), Float(line, 13 + 2 * i))).ToList()));
                        break;

                    case "AT":
                        if (types.Count == 0)
                            throw new ParmFormatException("AT before any artefact type", line.LineNumber);
                        if (line.Args.Count == 6 && line.Args[4] != "ID")
                            throw new ParmFormatException("expected AT x y z angle [ID n]", line.LineNumber);
                        ArgCount(line, 4, 6);
                        placements.Add(new ArtefactPlacement(types[^1].Name, Vec3(line, 0), Float(line, 3), line.Args.Count == 6 ? Int(line, 5) : null, line.LineNumber));
                        break;

                    case "ONDESTROY":
                        // ONDESTROY id DAMAGE n HP n ARMOUR n
                        if (line.Args.Count != 7 || line.Args[1] != "DAMAGE" || line.Args[3] != "HP" || line.Args[5] != "ARMOUR")
                            throw new ParmFormatException("expected ONDESTROY id DAMAGE n HP n ARMOUR n", line.LineNumber);
                        onDestroy.Add(new ArtefactOnDestroy(Int(line, 0), Int(line, 2), Int(line, 4), Int(line, 6), line.LineNumber));
                        break;

                    default:
                        throw new ParmFormatException($"unexpected '{line.Keyword}' in ARTEFACTS", line.LineNumber);
                }
            }

            var ids = placements.Where(p => p.Id is not null).Select(p => p.Id!.Value).ToHashSet();
            var orphan = onDestroy.FirstOrDefault(d => !ids.Contains(d.Id));
            if (orphan is not null)
                throw new ParmFormatException($"ONDESTROY refers to artefact {orphan.Id}, which has no AT ... ID line", orphan.LineNumber);

            _artefacts = new ArtefactSection(models, types, placements, onDestroy);
        }

        private void ReadConditionsAndActions(ParmLine header)
        {
            foreach (var line in Until("ENDCONDITIONS"))
            {
                if (line.Args.Count < 1)
                    throw new ParmFormatException($"condition {line.Keyword} needs a name", line.LineNumber);
                _conditions.Add(new ScenarioCondition(line.Keyword, line.Args[0], line.Args.Skip(1).ToList(), line.LineNumber));
            }

            var actionsHeader = Take("ACTIONS after ENDCONDITIONS");
            Expect(actionsHeader, "ACTIONS");

            foreach (var line in Until("ENDACTIONS"))
            {
                // <type> ON <condition> ...; DISABLED may appear anywhere on the line.
                // The original never checks the word ON: the condition is always the third word
                // (SET_SCENARIO_FLAG lines say NAME instead).
                if (line.Args.Count < 2)
                    throw new ParmFormatException($"expected {line.Keyword} ON <condition> ...", line.LineNumber);
                if (line.Args[0] != "ON")
                    _warnings.Add(new ScenarioWarning(line.LineNumber, $"{line.Keyword}: '{line.Args[0]}' where ON is expected"));

                var args = line.Args.Skip(2).Where(a => a != "DISABLED").ToList();
                List<ParmLine> body = [];
                if (line.Keyword == "REINFORCEMENTS")
                {
                    foreach (var inner in Until("END"))
                    {
                        if (inner.Keyword is not ("MACHINE" or "POINT" or "ASSIGN" or "FROM_SCENARIO"))
                            throw new ParmFormatException($"unexpected '{inner.Keyword}' in REINFORCEMENTS", inner.LineNumber);
                        body.Add(inner);
                    }
                }

                _actions.Add(new ScenarioAction(line.Keyword, line.Args[1], !line.Args.Contains("DISABLED"), args, body, line.LineNumber));
            }
        }

        private ConstructionRestriction ReadRestriction(ParmLine line)
        {
            // RESTRICT_CONSTRUCTION RACE <race> <type> [<subtype>] <level>
            ArgCount(line, 4, 5);
            if (line.Args[0] != "RACE")
                throw new ParmFormatException("expected RESTRICT_CONSTRUCTION RACE ...", line.LineNumber);

            var target = line.Args[1] switch
            {
                "RED" => RaceTarget.Red,
                "BLUE" => RaceTarget.Blue,
                "GREEN" => RaceTarget.Green,
                "YELLOW" => RaceTarget.Yellow,
                "AI" => RaceTarget.Ai,
                "PC" => RaceTarget.Pc,
                _ => RaceTarget.All,
            };
            var type = NormaliseType(line.Args[2]);
            return line.Args.Count == 5
                ? new ConstructionRestriction(target, type, NormaliseSubType(line.Args[3]), Int(line, 4), line.LineNumber)
                : new ConstructionRestriction(target, type, null, Int(line, 3), line.LineNumber);
        }

        // ---- race block lines -----------------------------------------------------------------

        private ScenarioMachine ParseMachine(ParmLine line, Race? displayAs)
        {
            // <type> <hw> <sw> <x> <y> <z> [<subtype> [<weapon combo>]] [SQUAD <n>]
            ArgCount(line, 5, 9);
            var type = line.Keyword;
            var (needsSubType, needsCombo) = type switch
            {
                "AGGRESSOR" or "ADMINISTRATOR" => (true, true),
                "TECHNICIAN" or "CONSTRUCTOR" => (true, false),
                "GEO_LOCATOR" or "SPY_LOCATOR" or "RESOURCE_CARRIER" or "APC" => (false, false),
                _ => throw new ParmFormatException($"unknown machine type '{type}'", line.LineNumber),
            };
            if (needsSubType && line.Args.Count < 6 || needsCombo && line.Args.Count < 7)
                throw new ParmFormatException($"{type} needs a sub-type{(needsCombo ? " and a weapon combo" : "")}", line.LineNumber);

            // Only the exact word SQUAD counts, as in the original (one file spells it SQAUD).
            int? squad = null;
            for (var i = 5; i < line.Args.Count - 1; i++)
            {
                if (line.Args[i] == "SQUAD")
                    squad = Int(line, i + 1);
            }

            return new ScenarioMachine
            {
                Type = type,
                SubType = needsSubType ? line.Args[5] : null,
                HardwareLevel = Int(line, 0),
                SoftwareLevel = Int(line, 1),
                Position = Vec3(line, 2),
                WeaponCombo = needsCombo ? line.Args[6] : null,
                Squad = squad,
                DisplayAs = displayAs,
                LineNumber = line.LineNumber,
            };
        }

        private ScenarioConstruction ParseConstruction(ParmLine line, Race? displayAs)
        {
            // <type> <level> <x> <y> <z> <orientation> [<subtype>] [<weapon combo>] [REBUILD] [NOT_BUILT] [ACTIVATE_ION_CANNON]
            ArgCount(line, 5, 9);
            var type = NormaliseType(line.Keyword);
            var hasSubType = type switch
            {
                "HARDWARE_LAB" or "FACTORY" or "MISSILE_EMPLACEMENT" => true,
                "BEACON" or "GARRISON" or "MINE" or "SMELTER" or "POD" => false,
                _ => throw new ParmFormatException($"unknown construction type '{line.Keyword}'", line.LineNumber),
            };

            var rest = line.Args.Skip(5).ToList();
            string? subType = null;
            if (hasSubType)
            {
                if (rest.Count == 0)
                    throw new ParmFormatException($"{type} needs a sub-type", line.LineNumber);
                subType = NormaliseSubType(rest[0]);
                rest.RemoveAt(0);
            }

            var flags = new HashSet<string> { "REBUILD", "NOT_BUILT", "ACTIVATE_ION_CANNON" };
            string? weaponCombo = null;
            if (rest.Count > 0 && !flags.Contains(rest[0]) && IsWeaponCombo(rest[0]))
            {
                weaponCombo = rest[0];
                rest.RemoveAt(0);
            }

            return new ScenarioConstruction
            {
                Type = type,
                SubType = subType,
                HardwareLevel = Int(line, 0),
                Position = Vec3(line, 1),
                Orientation = Orientation(line, 4),
                WeaponCombo = weaponCombo,
                Rebuild = rest.Contains("REBUILD"),
                NotBuilt = rest.Contains("NOT_BUILT"),
                ActivateIonCannon = rest.Contains("ACTIVATE_ION_CANNON"),
                DisplayAs = displayAs,
                AlternativeSites = [],
                UnknownWords = rest.Where(w => !flags.Contains(w)).ToList(),
                LineNumber = line.LineNumber,
            };
        }

        // ---- reference checks -----------------------------------------------------------------

        private void CheckConditionReferences()
        {
            var defined = new HashSet<string>();
            foreach (var condition in _conditions)
            {
                // ANY/ALL/NOT look their parts up while parsing, so the parts must come first.
                foreach (var part in condition.ReferencedConditions)
                {
                    if (!defined.Contains(part))
                        throw new ParmFormatException($"{condition.Type} {condition.Name} uses '{part}', which is not defined before it", condition.LineNumber);
                }
                if (!defined.Add(condition.Name))
                    throw new ParmFormatException($"condition '{condition.Name}' is defined twice", condition.LineNumber);
            }

            foreach (var action in _actions)
            {
                if (!defined.Contains(action.Condition))
                    throw new ParmFormatException($"{action.Type} uses undefined condition '{action.Condition}'", action.LineNumber);
                if (action.Type == "ENABLE_ACTION" && (action.Args.Count == 0 || !defined.Contains(action.Args[0])))
                    throw new ParmFormatException("ENABLE_ACTION must name a defined condition whose actions it enables", action.LineNumber);
            }
        }

        // ---- helpers --------------------------------------------------------------------------

        private bool TryTake(out ParmLine line)
        {
            if (_next < lines.Count)
            {
                line = lines[_next++];
                return true;
            }
            line = null!;
            return false;
        }

        private ParmLine Take(string expected) =>
            TryTake(out var line) ? line : throw new ParmFormatException($"file ends, expected {expected}", lines.Count == 0 ? 0 : lines[^1].LineNumber);

        /// <summary>Lines up to (and consuming) the closing keyword.</summary>
        private IEnumerable<ParmLine> Until(string end)
        {
            while (true)
            {
                var line = Take(end);
                if (line.Keyword == end)
                    yield break;
                yield return line;
            }
        }

        private static void Expect(ParmLine line, string keyword)
        {
            if (line.Keyword != keyword)
                throw new ParmFormatException($"expected {keyword}, found '{line.Keyword}'", line.LineNumber);
        }

        private static void ArgCount(ParmLine line, int min, int max)
        {
            if (line.Args.Count < min || line.Args.Count > max)
                throw new ParmFormatException(
                    min == max ? $"{line.Keyword} needs {min} values, found {line.Args.Count}" : $"{line.Keyword} needs {min} to {max} values, found {line.Args.Count}",
                    line.LineNumber);
        }

        /// <summary>Reads a number as the original did (<see cref="CNumbers"/>); anything not clean becomes a warning.</summary>
        private double CNumber(ParmLine line, int index, bool integer)
        {
            var token = line.Args[index];
            var (value, clean, readAs) = CNumbers.Parse(token, integer);
            if (!clean)
                _warnings.Add(new ScenarioWarning(line.LineNumber, $"{line.Keyword}: '{token}' read as {readAs}"));
            return value;
        }

        private int Int(ParmLine line, int index) => (int)CNumber(line, index, integer: true);

        private float Float(ParmLine line, int index) => (float)CNumber(line, index, integer: false);

        private Vector3 Vec3(ParmLine line, int index) => new(Float(line, index), Float(line, index + 1), Float(line, index + 2));

        private int Orientation(ParmLine line, int index)
        {
            var value = Float(line, index);
            if (Math.Abs(value) is not (0f or 90f or 180f or 270f))
                throw new ParmFormatException($"building orientation must be 0, 90, 180 or 270, found {value.ToString(CultureInfo.InvariantCulture)}", line.LineNumber);
            return (int)value;
        }

        private static Race ParseRace(string word, ParmLine line) => word switch
        {
            "RED" => Race.Red,
            "BLUE" => Race.Blue,
            "GREEN" => Race.Green,
            "YELLOW" => Race.Yellow,
            _ => throw new ParmFormatException($"unknown race '{word}'", line.LineNumber),
        };

        private static ResourceLevel ParseResourceLevel(string word, ParmLine line) => word switch
        {
            "RES_DEFAULT" or "DEFAULT" => ResourceLevel.Default,
            "RES_LOW" or "LOW" => ResourceLevel.Low,
            "RES_MEDIUM" or "MEDIUM" => ResourceLevel.Medium,
            "RES_HIGH" or "HIGH" => ResourceLevel.High,
            _ => throw new ParmFormatException($"unknown resource level '{word}'", line.LineNumber),
        };

        private static string NormaliseType(string type) => type switch
        {
            "HWLAB" => "HARDWARE_LAB",
            "MISSILE" => "MISSILE_EMPLACEMENT",
            _ => type,
        };

        private static string NormaliseSubType(string subType) => subType switch
        {
            "LAB_MILITARY" => "MILITARY",
            "LAB_CIVILIAN" => "CIVILIAN",
            _ => subType,
        };

        private static bool IsWeaponCombo(string word) =>
            word.StartsWith("L_", StringComparison.Ordinal) || word.StartsWith("R_", StringComparison.Ordinal)
            || word.StartsWith("T_", StringComparison.Ordinal) || word.StartsWith("LR_", StringComparison.Ordinal)
            || word.StartsWith("LRT_", StringComparison.Ordinal);
    }
}
