using System.Globalization;
using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// A campaign scenario's <c>CONDITIONS</c> and <c>ACTIONS</c>, run as the original's SimConditionsManager ran them
/// (sim/conditim.cpp, sim/action.cpp). Every action waits on a named condition and, once it is met, acts and is gone.
/// After a look that finds it unmet, the condition says when it is worth looking again (recommendedCallBackTimeGap):
/// a timer when it is due, places and unit counts after 5 seconds, deaths, shots and BMUs after 2. A
/// <c>DISABLED</c> action waits for an <c>ENABLE_ACTION</c>, which also restarts its timer (MachLogTimerCondition::doBecomeEnabled).
/// </summary>
public sealed class MissionScript
{
    /// <summary>The voice mail every reinforcement sends unless it says NO_VOICEMAIL.</summary>
    public const string ReinforcementsVoiceMail = "VID_POD_REINFORCEMENTS_ONLINE";

    /// <summary>The condition and action types the original knows; no campaign mission uses the rest of them.</summary>
    public static readonly IReadOnlySet<string> ConditionTypes = new HashSet<string>
    {
        "TIMER", "REACHED_LOCATION", "LOW_UNIT_COUNT", "HIGH_UNIT_COUNT", "ALL_UNITS_DEAD", "ANY", "ALL", "NOT",
        "SCENARIO_FLAG", "FIRED_AT", "BMU_COUNT", "RESEARCH_COMPLETE",
    };

    public static readonly IReadOnlySet<string> ActionTypes = new HashSet<string>
    {
        "WON", "LOST", "VOICE_MAIL", "TASK_STATE", "ENABLE_ACTION", "REINFORCEMENTS", "ACTIVATE_SQUADRONS",
        "CHANGE_RACE", "EXPLOSION", "SET_SCENARIO_FLAG",
    };

    private readonly List<ScriptAction> _actions = [];
    private float _earliestCheck;

    private MissionScript() { }

    /// <summary>The tasks the script has set, by number from 1: complete or still open (MachLogTaskStateAction).</summary>
    public SortedDictionary<int, bool> Tasks { get; } = [];

    /// <summary>Flags set by SET_SCENARIO_FLAG, in the order they were set; a won mission hands them on.</summary>
    public List<string> FlagsSet { get; } = [];

    /// <summary>Races the script declared winners (WON), in that order.</summary>
    public List<Race> Won { get; } = [];

    /// <summary>Races the script declared losers (LOST).</summary>
    public HashSet<Race> Lost { get; } = [];

    /// <summary>AI squads told to attack now (ACTIVATE_SQUADRONS): their attack's DELAY is over.</summary>
    public HashSet<(Race Race, int Squad)> ActivatedSquadrons { get; } = [];

    /// <summary>The scenario lines of the actions that have fired, in order: what a mission check reports.</summary>
    public List<int> Fired { get; } = [];

    /// <summary>Actions still waiting.</summary>
    public int ActionsLeft => _actions.Count;

    /// <summary>
    /// Builds the script of a scenario as it loads (MachLogActions::load). <paramref name="carry"/> answers
    /// SCENARIO_FLAG and FROM_SCENARIO from the missions won before.
    /// </summary>
    public static MissionScript Compile(Scenario scenario, CampaignCarry? carry)
    {
        carry ??= new CampaignCarry();
        var script = new MissionScript();
        var conditions = new Dictionary<string, Condition>();
        foreach (var line in scenario.Conditions)
            conditions[line.Name] = CompileCondition(line, conditions, carry);

        foreach (var line in scenario.Actions)
        {
            if (!conditions.TryGetValue(line.Condition, out var condition) || CompileAction(line, carry) is not { } act)
                continue;
            script._actions.Add(new ScriptAction(line.Condition, condition, line.Enabled, act, line.LineNumber));
        }
        return script;
    }

    /// <summary>Called every tick; looks only at the actions whose time to look has come.</summary>
    internal void Update(GameWorld world)
    {
        var now = world.Time;
        if (now < _earliestCheck)
            return;
        _earliestCheck = now + 3600;
        for (var i = 0; i < _actions.Count;)
        {
            var action = _actions[i];
            if (action.Enabled)
            {
                if (action.NextCheck < now && CheckAndAct(action, world))
                {
                    _actions.RemoveAt(i);
                    continue;
                }
                _earliestCheck = MathF.Min(_earliestCheck, action.NextCheck);
            }
            i++;
        }
    }

    private bool CheckAndAct(ScriptAction action, GameWorld world)
    {
        if (action.Condition.Met(world))
        {
            Fired.Add(action.LineNumber);
            action.Act(this, world);
            return true;
        }
        action.NextCheck = world.Time + action.Condition.Gap(world);
        return false;
    }

    /// <summary>
    /// ENABLE_ACTION: every action waiting on this condition is switched on, and its condition told so: a timer starts
    /// counting again from now (SimConditionsManager::enableAction).
    /// </summary>
    private void Enable(string conditionName, GameWorld world)
    {
        foreach (var action in _actions.Where(a => a.ConditionName == conditionName))
        {
            action.Enabled = true;
            action.Condition.BecomeEnabled(world);
            _earliestCheck = 0;
        }
    }

    /// <summary>A fingerprint of the script's state for the lockstep checksum.</summary>
    internal long Fingerprint()
    {
        long hash = _actions.Count;
        foreach (var action in _actions)
            hash = hash * 31 + (action.Enabled ? 1 : 0);
        foreach (var (task, complete) in Tasks)
            hash = hash * 31 + task * 2 + (complete ? 1 : 0);
        return hash * 31 + FlagsSet.Count;
    }

    // ---- conditions -----------------------------------------------------------------------------------------------

    private sealed class ScriptAction(string conditionName, Condition condition, bool enabled, Action<MissionScript, GameWorld> act, int lineNumber)
    {
        public string ConditionName { get; } = conditionName;
        public Condition Condition { get; } = condition;
        public bool Enabled { get; set; } = enabled;
        public Action<MissionScript, GameWorld> Act { get; } = act;
        public int LineNumber { get; } = lineNumber;
        public float NextCheck { get; set; }
    }

    private abstract class Condition
    {
        private long _evaluatedTick;
        private bool _result;

        /// <summary>Looked at once a tick at most: the actions on it see the same answer (SimCondition::hasConditionBeenMet).</summary>
        public bool Met(GameWorld world)
        {
            if (_evaluatedTick != world.Tick)
            {
                _result = Evaluate(world);
                _evaluatedTick = world.Tick;
            }
            return _result;
        }

        protected abstract bool Evaluate(GameWorld world);
        public abstract float Gap(GameWorld world);
        public virtual void BecomeEnabled(GameWorld world) { }
    }

    /// <summary>TIMER name seconds (MachLogTimerCondition).</summary>
    private sealed class Timer(float seconds) : Condition
    {
        private float _fireAt = -1;
        private float FireAt => _fireAt < 0 ? seconds : _fireAt;
        protected override bool Evaluate(GameWorld world) => world.Time >= FireAt;
        public override float Gap(GameWorld world) => FireAt - world.Time;
        public override void BecomeEnabled(GameWorld world) => _fireAt = seconds + world.Time;
    }

    /// <summary>A condition met by others (ANY, ALL), every one of them looked at as in the original.</summary>
    private sealed class AnyAll(IReadOnlyList<Condition> parts, bool all) : Condition
    {
        protected override bool Evaluate(GameWorld world)
        {
            var met = 0;
            foreach (var part in parts)
                met += part.Met(world) ? 1 : 0;
            return all ? met == parts.Count : met > 0;
        }

        public override float Gap(GameWorld world) => 2.1f;
    }

    private sealed class Not(Condition part) : Condition
    {
        protected override bool Evaluate(GameWorld world) => !part.Met(world);
        public override float Gap(GameWorld world) => 1.54f;
    }

    /// <summary>A condition that only reads the world (deaths, shots, BMUs, research, flags).</summary>
    private sealed class Check(Func<GameWorld, bool> evaluate, float gap) : Condition
    {
        protected override bool Evaluate(GameWorld world) => evaluate(world);
        public override float Gap(GameWorld world) => gap;
    }

    private enum Kind { Any, Machine, Construction, Type, Artefact }

    /// <summary>
    /// REACHED_LOCATION, LOW_UNIT_COUNT and HIGH_UNIT_COUNT, which the original all ran through
    /// MachLogReachedLocationCondition: the race's machines and buildings that match, within the range if there is
    /// one, counted against the number. Artefacts are counted by their type's place in the ARTEFACTS section, of any race.
    /// </summary>
    private sealed class Count : Condition
    {
        public required Race Race { get; init; }
        public required int Number { get; init; }
        public required bool AtLeast { get; init; }
        public (Vector2 Point, float Range)? Area { get; init; }
        public Kind Kind { get; init; }
        public string? Type { get; init; }
        public int? Level { get; init; }
        public string? SubType { get; init; }
        public string? WeaponCombo { get; init; }
        public int ArtefactSubType { get; init; }

        protected override bool Evaluate(GameWorld world)
        {
            int count;
            if (Kind == Kind.Artefact)
                count = world.Artefacts.Count(a => a.SubType == ArtefactSubType);
            else
            {
                count = 0;
                foreach (var actor in world.Actors)
                {
                    if (Matches(actor) && (Area is not { } area || Vector2.DistanceSquared(area.Point, actor.Position) < area.Range * area.Range))
                        count++;
                }
            }
            return AtLeast ? count >= Number : count < Number;
        }

        private bool Matches(Actor actor)
        {
            if (actor.Race != Race)
                return false;
            switch (Kind)
            {
                case Kind.Any: return true;
                case Kind.Machine: return actor is Machine;
                case Kind.Construction: return actor is Construction { Complete: true };
            }
            var (type, subType, level, combo, complete) = actor switch
            {
                Machine m => (m.Spec.Type, m.Spec.SubType, m.Stats.HardwareLevel, m.WeaponCombo, true),
                Construction c => (c.Spec.Type, c.Spec.SubType, c.Stats.HardwareLevel, c.WeaponCombo, c.Complete),
                _ => ("", null, 0, null, false),
            };
            return type == Type
                && (SubType is null || subType == SubType)
                && (Level is null || level == Level && complete)
                && (WeaponCombo is null || combo == WeaponCombo);
        }

        public override float Gap(GameWorld world) => 5;
    }

    /// <summary>SCENARIO_FLAG and the rest, by their original token positions (Args[k] is the line's word k + 2).</summary>
    private static Condition CompileCondition(ScenarioCondition line, Dictionary<string, Condition> earlier, CampaignCarry carry)
    {
        var args = line.Args;
        string Arg(int token) => token - 2 < args.Count ? args[token - 2] : "";
        int Int(int token) => int.TryParse(Arg(token), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        float Float(int token) => float.TryParse(Arg(token), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
        string? Optional(int token) => token - 2 < args.Count ? args[token - 2] : null;
        Condition Named(string name) => earlier.TryGetValue(name, out var c) ? c : Never;

        switch (line.Type)
        {
            case "TIMER":
                return new Timer(Float(2));
            case "ANY" or "ALL":
                return new AnyAll(args.Select(Named).ToList(), line.Type == "ALL");
            case "NOT":
                return new Not(Named(Arg(2)));
            case "SCENARIO_FLAG":
            {
                var set = Lookup(carry.Flags, Arg(3)) is { } flags && flags.Contains(line.Name);
                return new Check(_ => set, 3600);
            }
            case "ALL_UNITS_DEAD":
            {
                var race = ParseRace(Arg(3));
                return new Check(w => !w.Actors.Any(a => a.Race == race), 2);
            }
            case "FIRED_AT":
            {
                var (by, at) = (ParseRace(Arg(3)), ParseRace(Arg(6)));
                return new Check(w => w.HasFiredAt(by, at), 2);
            }
            case "BMU_COUNT":
            {
                var race = ParseRace(Arg(3));
                var atLeast = Arg(4) == "GREATER_THAN_EQUAL";
                var number = Int(5);
                return new Check(w => w.Races.TryGetValue(race, out var s) && (atLeast ? (int)s.Bmu >= number : (int)s.Bmu <= number), 2);
            }
            case "RESEARCH_COMPLETE":
            {
                // The original took the sixth word for a sub-type whatever it said (POD 1 WCOMBO T_ION_ORBITAL_CANNON).
                var race = ParseRace(Arg(3));
                var (type, level) = (Arg(4), Int(5));
                var subType = Optional(6) is { } sub and not "WCOMBO" ? sub : null;
                var combo = Optional(7);
                return new Check(w => w.Races.TryGetValue(race, out var s) && s.Researched.Any(k =>
                    k.Type == type && k.HardwareLevel == level && (subType is null || k.SubType == subType) && (combo is null || k.WeaponCombo == combo)), 1);
            }
            case "REACHED_LOCATION":
                return CountCondition(ParseRace(Arg(3)), Int(4), atLeast: true, (new Vector2(Float(6), Float(7)), Float(9)), Arg(10), Optional(11), Optional(12), Optional(13));
            case "LOW_UNIT_COUNT" or "HIGH_UNIT_COUNT":
                return CountCondition(ParseRace(Arg(3)), Int(4), line.Type == "HIGH_UNIT_COUNT", null, Arg(5), Optional(6), Optional(7), Optional(8));
            default:
                return Never;
        }
    }

    private static readonly Condition Never = new Check(_ => false, 3600);

    private static Count CountCondition(Race race, int number, bool atLeast, (Vector2, float)? area, string type, string? level, string? subType, string? combo)
    {
        var kind = type switch
        {
            "ANY" => Kind.Any,
            "MACHINE" => Kind.Machine,
            "CONSTRUCTION" => Kind.Construction,
            "ARTEFACT" => Kind.Artefact,
            _ => Kind.Type,
        };
        int? Level(string? word) => int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new Count
        {
            Race = race,
            Number = number,
            AtLeast = atLeast,
            Area = area,
            Kind = kind,
            Type = type,
            Level = kind == Kind.Type ? Level(level) : null,
            SubType = kind == Kind.Type ? subType : null,
            WeaponCombo = kind == Kind.Type ? combo : null,
            ArtefactSubType = kind == Kind.Artefact ? Level(level) ?? 0 : 0,
        };
    }

    // ---- actions --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The action a line stands for. Its words are looked up by keyword as the original did (RACE, AT, RANGE ...);
    /// SET_SCENARIO_FLAG's flag is the word after NAME, which is also its condition's name.
    /// </summary>
    private static Action<MissionScript, GameWorld>? CompileAction(ScenarioAction line, CampaignCarry carry)
    {
        var args = line.Args;
        string? After(string keyword, int offset = 1)
        {
            var i = IndexOf(args, keyword);
            return i >= 0 && i + offset < args.Count ? args[i + offset] : null;
        }
        float Float(string? word) => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
        Vector2 At() => new(Float(After("AT")), Float(After("AT", 2)));
        Race Race(string keyword = "RACE") => ParseRace(After(keyword) ?? "");

        switch (line.Type)
        {
            case "WON":
            {
                var race = Race();
                return (script, _) =>
                {
                    if (!script.Won.Contains(race))
                        script.Won.Add(race);
                };
            }
            case "LOST":
            {
                var race = Race();
                return (script, _) => script.Lost.Add(race);
            }
            case "VOICE_MAIL":
            {
                var (race, vid) = (Race(), After("VID") ?? "");
                return (_, world) => world.SendVoiceMail(race, vid);
            }
            case "TASK_STATE":
            {
                var number = (int)Float(After("NUMBER"));
                var complete = After("NUMBER", 2) == "COMPLETE";
                return (script, world) =>
                {
                    script.Tasks[number] = complete;
                    world.TaskChanged(number, complete);
                };
            }
            case "ENABLE_ACTION":
            {
                var target = args.Count > 0 ? args[0] : "";
                return (script, world) => script.Enable(target, world);
            }
            case "SET_SCENARIO_FLAG":
            {
                var flag = line.Condition;
                return (script, _) =>
                {
                    if (!script.FlagsSet.Contains(flag))
                        script.FlagsSet.Add(flag);
                };
            }
            case "ACTIVATE_SQUADRONS":
            {
                var race = Race();
                var start = IndexOf(args, "SQUADRONS");
                var squads = start < 0 ? [] : args.Skip(start + 1).Select(w => (int)Float(w)).ToList();
                return (script, _) =>
                {
                    foreach (var squad in squads)
                        script.ActivatedSquadrons.Add((race, squad - 1)); // by squadron id, which counts from 1 (MachLogRaces::race)
                };
            }
            case "CHANGE_RACE":
            {
                var (from, to, at, range) = (Race("ORIGINAL_RACE"), Race("TARGET_RACE"), At(), Float(After("RANGE")));
                return (_, world) => world.ChangeRace(from, to, at, range);
            }
            case "EXPLOSION":
            {
                var (at, range, damage, size) = (At(), Float(After("RANGE")), (int)Float(After("DAMAGE")), Float(After("SIZE")));
                return (_, world) => world.ScriptExplosion(at, range, damage, size);
            }
            case "REINFORCEMENTS":
                return CompileReinforcements(line, carry);
            default:
                return null;
        }
    }

    /// <summary>
    /// REINFORCEMENTS RACE x [NO_VOICEMAIL], then MACHINE type level [sub-type [weapons]] lines, a POINT, an ASSIGN to
    /// a squad, and FROM_SCENARIO lines that bring in survivors of a won mission (MachLogReinforcementsAction).
    /// </summary>
    private static Action<MissionScript, GameWorld> CompileReinforcements(ScenarioAction line, CampaignCarry carry)
    {
        var race = ParseRace(line.Args.SkipWhile(a => a != "RACE").Skip(1).FirstOrDefault() ?? "");
        var voiceMail = !line.Args.Contains("NO_VOICEMAIL");
        var units = new List<(string Type, string? SubType, int Level, string? Combo)>();
        var point = Vector2.Zero;
        int? squad = null;
        foreach (var body in line.Body)
        {
            var words = body.Args;
            int Int(int i) => i < words.Count && int.TryParse(words[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            float Float(int i) => i < words.Count && float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
            switch (body.Keyword)
            {
                case "MACHINE" when words.Count >= 2:
                    units.Add((words[0], words.Count >= 3 ? words[2] : null, Int(1), words.Count >= 4 ? words[3] : null));
                    break;
                case "POINT":
                    point = new Vector2(Float(0), Float(1));
                    break;
                case "ASSIGN":
                    squad = Int(0) - 1; // by squadron id, which counts from 1
                    break;
                case "FROM_SCENARIO" when words.Count >= 1:
                    units.AddRange(FromScenario(race, words, carry));
                    break;
            }
        }
        return (_, world) => world.Reinforce(race, units, point, squad, voiceMail);
    }

    /// <summary>
    /// FROM_SCENARIO scenario TYPE count ...: the best of that mission's survivors of each type, by sub-type and
    /// level (MachLogReinforcementsAction::parseFromScenario, which sorts them greatest first).
    /// </summary>
    private static IEnumerable<(string, string?, int, string?)> FromScenario(Race race, IReadOnlyList<string> words, CampaignCarry carry)
    {
        var survivors = (Lookup(carry.Survivors, words[0]) ?? [])
            .Where(m => m.Race == race)
            .OrderBy(m => m.Type, StringComparer.Ordinal)
            .ThenByDescending(m => SubTypeRank(m.Type, m.SubType))
            .ThenByDescending(m => m.HardwareLevel)
            .ToList();
        for (var i = 1; i + 1 < words.Count; i += 2)
        {
            var count = int.TryParse(words[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            foreach (var machine in survivors.Where(m => m.Type == words[i]).Take(count))
                yield return (machine.Type, machine.SubType, machine.HardwareLevel, machine.WeaponCombo);
        }
    }

    /// <summary>Where a sub-type stands in the original's enums (machphys.hpp), which the survivors are sorted by.</summary>
    private static int SubTypeRank(string type, string? subType)
    {
        string[] order = type switch
        {
            "AGGRESSOR" => ["GRUNT", "ASSASSIN", "KNIGHT", "BALLISTA", "NINJA"],
            "ADMINISTRATOR" => ["BOSS", "OVERSEER", "COMMANDER"],
            "CONSTRUCTOR" => ["DOZER", "BUILDER", "BEHEMOTH"],
            "TECHNICIAN" => ["LAB_TECH", "TECH_BOY", "BRAIN_BOX"],
            _ => [],
        };
        return Array.IndexOf(order, subType);
    }

    /// <summary>Who plays a campaign mission (RACES_DEFAULT): its PC race the player, the others the AI.</summary>
    public static Dictionary<Race, Controller> CampaignPlayers(Scenario scenario) => scenario.DefaultPlayers
        .Where(p => p.Value != DefaultController.None && scenario.Race(p.Key) is not null)
        .OrderBy(p => p.Key)
        .ToDictionary(p => p.Key, p => p.Value == DefaultController.Player ? Controller.Player : Controller.Ai);

    /// <summary>The machines of a race still standing: what a won mission that saves its machines hands on.</summary>
    public static List<SurvivingMachine> Survivors(GameWorld world, Race race) => world.Machines
        .Where(m => m.Race == race && !m.IsDead)
        .OrderBy(m => m.Id)
        .Select(m => new SurvivingMachine(race, m.Spec.Type, m.Spec.SubType, m.Stats.HardwareLevel, m.WeaponCombo))
        .ToList();

    private static T? Lookup<T>(Dictionary<string, T> byScenario, string scenario) where T : class =>
        byScenario.FirstOrDefault(p => string.Equals(p.Key, scenario, StringComparison.OrdinalIgnoreCase)).Value;

    private static int IndexOf(IReadOnlyList<string> words, string keyword)
    {
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] == keyword)
                return i;
        }
        return -1;
    }

    private static Race ParseRace(string word) => Enum.TryParse<Race>(word, ignoreCase: true, out var race) ? race : Race.Red;
}
