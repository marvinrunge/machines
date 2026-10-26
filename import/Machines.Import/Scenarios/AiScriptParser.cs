using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;

namespace Machines.Import.Scenarios;

/// <summary>
/// Reads an AI race script (<c>.spl</c>). Follows <c>MachLogAIController::readRules</c> and
/// <c>createCorrectSquadronOperation</c>:
/// <code>
/// PRODUCTION LIST
/// NUMBER 2 1 RESOURCE_CARRIER 1
/// END PRODUCTION LIST
/// SQUAD
/// AI ATTACK DELAY 5 RESOURCE MINIMUM 3
/// NUMBER 2 0 AGGRESSOR 2 GRUNT L_BOLTER
/// END SQUAD
/// </code>
/// As in the original, only <c>NUMBER</c> lines count in the production list, and unknown words are skipped.
/// Numbers are read like C's <c>atoi</c>/<c>atof</c>; anything odd is reported in <see cref="AiScript.Warnings"/>.
/// </summary>
public static class AiScriptParser
{
    public static AiScript Load(string path)
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

    public static AiScript Parse(string text) => new Reader().Read(TextLines.Read(text).ToList());

    private sealed class Reader
    {
        private readonly List<ParmLine> _ignored = [];
        private readonly List<ScenarioWarning> _warnings = [];

        public AiScript Read(List<ParmLine> lines)
        {
            var i = 0;

            // The production list runs to the first END.
            List<ProductionOrder> production = [];
            for (; i < lines.Count && lines[i].Keyword != "END"; i++)
            {
                if (lines[i].Keyword == "NUMBER")
                    production.Add(ParseNumber(lines[i]));
                else
                    _ignored.Add(lines[i]);
            }
            if (i == lines.Count)
                throw new ParmFormatException("the production list is never closed with END", lines.Count == 0 ? 0 : lines[^1].LineNumber);
            i++;

            List<AiSquad> squads = [];
            for (; i < lines.Count; i++)
            {
                if (lines[i].Keyword != "SQUAD")
                {
                    _ignored.Add(lines[i]);
                    continue;
                }

                var header = lines[i];
                List<ProductionOrder> members = [];
                List<SquadOperation> operations = [];
                string? defcon = null;

                for (i++; ; i++)
                {
                    if (i == lines.Count)
                        throw new ParmFormatException("SQUAD is never closed with END", header.LineNumber);

                    var line = lines[i];
                    if (line.Keyword == "END")
                        break;

                    switch (line.Keyword)
                    {
                        case "AI": operations.Add(ParseOperation(line)); break;
                        case "NUMBER": members.Add(ParseNumber(line)); break;
                        case "DEFCON" when line.Args is ["LOW" or "NORMAL" or "HIGH"]: defcon = line.Args[0]; break;
                        default: _ignored.Add(line); break;
                    }
                }

                squads.Add(new AiSquad(squads.Count, members, operations, defcon, header.LineNumber));
            }

            return new AiScript(production, squads, _ignored, _warnings);
        }

        private ProductionOrder ParseNumber(ParmLine line)
        {
            // NUMBER <count> <priority> <type> <level> [<subtype>] [<weapon combo>]
            if (line.Args.Count is < 4 or > 6)
                throw new ParmFormatException("expected NUMBER count priority type level [subtype] [combo]", line.LineNumber);

            var key = new ResearchKey(
                line.Args[2],
                line.Args.Count > 4 ? line.Args[4] : null,
                Int(line, 3),
                line.Args.Count > 5 ? line.Args[5] : null);
            return new ProductionOrder(Int(line, 0), Int(line, 1), key, line.LineNumber);
        }

        private SquadOperation ParseOperation(ParmLine line)
        {
            var args = line.Args;
            if (args.Count == 0)
                throw new ParmFormatException("AI needs an operation", line.LineNumber);

            switch (args[0])
            {
                case "LOCATE":
                {
                    int sites = 200;
                    float range = 1_000_000;
                    for (var i = 1; i < args.Count; i++)
                    {
                        if (args[i] == "SITES" && i + 1 < args.Count) sites = Int(line, ++i);
                        else if (args[i] == "RANGE_POD" && i + 1 < args.Count) range = Float(line, ++i);
                        else Unknown(line, args[i]);
                    }
                    return new LocateOperation(sites, range, line.LineNumber);
                }

                case "CONSTRUCT":
                    for (var i = 1; i < args.Count; i++)
                        Unknown(line, args[i]);
                    return new ConstructOperation(line.LineNumber);

                case "ATTACK":
                    return ParseAttack(line);

                case "DROP_MINES":
                    if (args.Count < 4)
                        throw new ParmFormatException("expected AI DROP_MINES min max POD|START x y", line.LineNumber);
                    if (args[3] == "POD")
                        return new DropMinesOperation(Float(line, 1), Float(line, 2), null, line.LineNumber);
                    if (args.Count != 6)
                        throw new ParmFormatException("AI DROP_MINES needs a start x y when not POD", line.LineNumber);
                    return new DropMinesOperation(Float(line, 1), Float(line, 2), new Vector2(Float(line, 4), Float(line, 5)), line.LineNumber);

                case "PATROL":
                {
                    float delay = 0;
                    var start = 1;
                    for (var i = 0; i < args.Count - 1; i++)
                    {
                        if (args[i] == "DELAY")
                        {
                            delay = Float(line, i + 1);
                            start = i + 2;
                        }
                    }
                    if ((args.Count - start) % 2 != 0 || args.Count - start < 4)
                        throw new ParmFormatException("AI PATROL needs at least two x y pairs", line.LineNumber);
                    var path = new List<Vector2>();
                    for (var i = start; i < args.Count; i += 2)
                        path.Add(new Vector2(Float(line, i), Float(line, i + 1)));
                    return new PatrolOperation(delay, path, line.LineNumber);
                }

                default:
                    throw new ParmFormatException($"unknown AI operation '{args[0]}'", line.LineNumber);
            }
        }

        private AttackOperation ParseAttack(ParmLine line)
        {
            // AI ATTACK DELAY <minutes> [keywords...]
            var args = line.Args;
            if (args.Count < 3 || args[1] != "DELAY")
                throw new ParmFormatException("expected AI ATTACK DELAY minutes ...", line.LineNumber);

            var attack = new AttackOperation(line.LineNumber) { DelayMinutes = Float(line, 2) };
            for (var i = 3; i < args.Count; i++)
            {
                bool HasValue(int count) => i + count < args.Count;

                switch (args[i])
                {
                    case "NONE": break;
                    case "RESOURCE": attack = attack with { Target = AttackTarget.Resource }; break;
                    case "RESEARCH": attack = attack with { Target = AttackTarget.Research }; break;
                    case "NO_CLUSTER_CHECK": attack = attack with { NoClusterCheck = true }; break;
                    case "POINT" when HasValue(2): attack = attack with { StartPoint = new Vector2(Float(line, i + 1), Float(line, i + 2)) }; i += 2; break;
                    case "MINIMUM" when HasValue(1): attack = attack with { Minimum = Int(line, ++i) }; break;
                    case "ASSEMBLY" when HasValue(1): attack = attack with { AssemblyPoint = Int(line, ++i) }; break;
                    case "DO" when HasValue(1): attack = attack with { DoCount = Int(line, ++i) }; break;
                    case "OBJECT" when HasValue(1): attack = attack with { Target = AttackTarget.Object, ObjectType = NormaliseObjectType(args[++i]) }; break;
                    case "SUBTYPE" when HasValue(1) && attack.Target == AttackTarget.Object: attack = attack with { ObjectSubType = args[++i] }; break;

                    case "USE" when HasValue(1):
                        var weapon = args[++i];
                        if (weapon == "NUKE") attack = attack with { UseNuke = true };
                        else if (weapon == "ION_ORBITAL_CANNON") attack = attack with { UseIonCannon = true };
                        else Unknown(line, weapon);
                        break;

                    default:
                        Unknown(line, args[i]);
                        break;
                }
            }
            return attack;
        }

        private static string NormaliseObjectType(string type) => type switch
        {
            "ARTIFACT" => "ARTEFACT",
            "HWLAB" => "HARDWARE_LAB",
            "MISSILE" => "MISSILE_EMPLACEMENT",
            _ => type,
        };

        private void Unknown(ParmLine line, string word) =>
            _warnings.Add(new ScenarioWarning(line.LineNumber, $"AI {line.Args[0]}: '{word}' ignored"));

        private double Number(ParmLine line, int index, bool integer)
        {
            var token = line.Args[index];
            var (value, clean, readAs) = CNumbers.Parse(token, integer);
            if (!clean)
                _warnings.Add(new ScenarioWarning(line.LineNumber, $"{line.Keyword}: '{token}' read as {readAs}"));
            return value;
        }

        private int Int(ParmLine line, int index) => (int)Number(line, index, integer: true);

        private float Float(ParmLine line, int index) => (float)Number(line, index, integer: false);
    }
}
