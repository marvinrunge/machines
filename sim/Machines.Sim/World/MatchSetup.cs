using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>Builds a <see cref="GameWorld"/> from an original scenario.</summary>
public static class MatchSetup
{
    /// <param name="players">The races taking part and who controls each; races not listed are left out.</param>
    /// <remarks>One player per race: the table of <see cref="PlayerTable.OnePerRace"/>, in the dictionary's order.</remarks>
    public static GameWorld Create(GameData data, string scenarioName, string planetName, IReadOnlyDictionary<Race, Controller> players,
        int seed = 1, MatchOptions? options = null) =>
        Create(data, scenarioName, planetName, PlayerTable.OnePerRace(players, (options ?? new MatchOptions()).IsBattle), seed, options);

    /// <param name="players">
    /// The players taking part, as the match records them; races without a row are left out, and the races are created in
    /// the order of their first row. In a battle the races are the teams', in race order: a seat no row names gets a bot,
    /// as the sim composes a hero no one brought, and rows of races outside the teams are left out.
    /// </param>
    public static GameWorld Create(GameData data, string scenarioName, string planetName, PlayerTable players,
        int seed = 1, MatchOptions? options = null)
    {
        options ??= new MatchOptions();
        if (options is { IsBattle: true, Battle: null })
            options = options with { Battle = new BattleOptions() };
        if (options is { IsStory: true, Story: null })
            options = options with { Story = new StoryOptions() };
        IReadOnlyList<Race> races = players.Races;
        // A story keeps of the scenario only the ground and its artefacts (doors, crates, the core): its seats are the trio's
        // three, a bot's where nobody sits, and the ship is the computer's. No pods, no machines: the director sets them down.
        if (options.Story is { } chapter)
        {
            options = options with { Victory = VictoryCondition.Story, Wired = null, Battle = null };
            players = StorySeats.Seated(players, chapter);
            races = players.Races;
        }
        // A battle keeps of the scenario the ground, its artefacts and one pod per team; it is no Wired run.
        else if (options.Battle is { } fight)
        {
            var (teamA, teamB) = BattleSeats.Teams(fight, BattleCatalogue.Map(scenarioName));
            options = options with
            {
                Battle = fight with { TeamA = teamA, TeamB = teamB },
                Victory = VictoryCondition.Battle,
                Wired = null,
            };
            if (options.Battle!.TeamA.Count == 0 || options.Battle.TeamB.Count == 0 || options.Battle.TeamA.Intersect(options.Battle.TeamB).Any())
                throw new ArgumentException($"a battle on {scenarioName} needs two teams apart");
            races = [.. options.Battle.TeamA.Concat(options.Battle.TeamB).Distinct().Order()];
            players = Seated(players, options.Battle, races, scenarioName);
        }
        // A Wired run keeps of the scenario only the ground, its artefacts and each player's pod.
        else if (options.Wired is not null || options.Victory == VictoryCondition.Wired)
            options = options with { Wired = options.Wired ?? new WiredOptions(), Victory = VictoryCondition.Wired };
        if (options.Battle is null && options.Story is null && players.Rows.FirstOrDefault(r => r.Kind == PlayerKind.Bot) is { } bot)
            throw new ArgumentException($"player {bot.Id} is a bot, which only a battle or the story has");
        var wired = options.Wired is not null;
        var battle = options.Battle;
        var story = options.Story;
        var bare = wired || battle is not null || story is not null;
        var scenario = data.LoadScenario(scenarioName);
        var world = new GameWorld(data, data.LoadPlanet(planetName), seed) { Options = options, Players = players.Fresh() };

        // Active artefacts (trees, rocks, wrecks, doors the scenario names ACTIVE) stand in the way: their boundary,
        // turned as they stand, is blocked to machines and building (MachLogArtefact::addObstaclePolygon), and they
        // can be destroyed. Scenery (DUMB) blocks nothing. ONDESTROY links name artefacts by their ID clause.
        if (scenario.Artefacts is { } artefacts)
        {
            var tagged = new Dictionary<int, Artefact>();
            for (var i = 0; i < artefacts.Placements.Count; i++)
            {
                var placement = artefacts.Placements[i];
                var subType = artefacts.Types.ToList().FindIndex(t => t.Name == placement.TypeName);
                if (subType < 0 || artefacts.Types[subType] is not { Active: true } type)
                    continue;
                var (sin, cos) = (DetMath.Sin(placement.Orientation * MathF.PI / 180), DetMath.Cos(placement.Orientation * MathF.PI / 180));
                var at = new Vector2(placement.Position.X, placement.Position.Y);
                var boundary = type.Boundary.Select(p => at + new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos)).ToList();
                var artefact = world.AddArtefact(i, at, type, subType, boundary);
                if (placement.Id is { } tag)
                {
                    tagged[tag] = artefact;
                    // (WP-B) A deck's doors are opened by their tag (GameWorld.OpenDoor).
                    artefact.Tag = tag;
                }
            }
            foreach (var link in artefacts.OnDestroy)
            {
                if (tagged.TryGetValue(link.Id, out var dying) && tagged.TryGetValue(link.Damage, out var damaged))
                    dying.OnDestroy.Add((damaged, link.HitPoints, link.Armour));
            }
        }

        // Unmarked sites appear in every setting, marked ones only in theirs; ids are list positions.
        foreach (var site in scenario.MineralSites.Where(s => s.Level == ResourceLevel.Default || s.Level == options.Resources))
            world.Sites.Add(new MineralSiteState(world.Sites.Count, site));

        foreach (var race in races)
        {
            var controller = players.HasHuman(race) ? Controller.Player : Controller.Ai;
            var setup = scenario.Race(race) ?? throw new ArgumentException($"{scenarioName} has no {race} race");
            var state = new RaceState(race, controller)
            {
                Bmu = MatchOptions.BmuFor(options.StartingBmu) ?? setup.Bmu ?? 0,
                VirtualBeacon = setup.VirtualBeacon switch { "LEVEL_1_BEACON" => 1, "LEVEL_3_BEACON" => 3, _ => 0 },
                ScoreDisplayed = setup.ScoreDisplayed,
            };
            if (battle is not null)
                state.Allies.UnionWith((battle.TeamA.Contains(race) ? battle.TeamA : battle.TeamB).Where(mate => mate != race));
            else if (!wired)
                state.Allies.UnionWith(setup.Allies);
            world.Races[race] = state;
            world.BuildingPlans[race] = [];

            foreach (var building in story is not null ? Enumerable.Empty<ScenarioConstruction>() : battle is not null ? BattlePod(setup, race, battle) : wired ? WiredBuildings(setup, players.HasHuman(race)) : setup.Constructions)
            {
                if (building.NotBuilt)
                {
                    world.BuildingPlans[race].Add(building);
                    continue;
                }
                var placed = world.AddConstruction(race, new Vector2(building.Position.X, building.Position.Y), building.Type, building.SubType,
                    building.HardwareLevel, building.Orientation, complete: true, building.WeaponCombo);
                placed.Height = building.Position.Z;
                placed.DisplayAs = building.DisplayAs;
                placed.IonCannonGranted = building.ActivateIonCannon && !bare;
                placed.Rebuild = building.Rebuild;
            }

            // With several commanders the race's starting machines are dealt among them in turn; otherwise all are the race's.
            var humans = players.PlayersOf(race).Where(id => players[id].Kind == PlayerKind.Human).ToList();
            var dealt = 0;
            foreach (var unit in bare ? [] : setup.Machines)
            {
                PlayerId? owner = humans.Count > 1 ? humans[dealt++ % humans.Count] : null;
                var machine = world.AddMachine(race, new Vector2(unit.Position.X, unit.Position.Y), unit.Type, unit.SubType, unit.HardwareLevel, unit.WeaponCombo, owner);
                machine.Squad = unit.Squad;
                machine.Height = unit.Position.Z;
                machine.DisplayAs = unit.DisplayAs;
            }

            if (setup.AiScript is { } script && !bare)
                world.StandIns[race] = () => new Ai.AiController(race, data.LoadAiScript(script), setup);
            if (controller == Controller.Ai && world.StandIns.TryGetValue(race, out var standIn))
                world.AiControllers.Add(standIn());
        }

        if (scenario.Research is { } research && !bare)
        {
            var file = options.TechLevel switch
            {
                TechLevel.Low => research.Low,
                TechLevel.Medium => research.Medium,
                TechLevel.High => research.High,
                _ => research.Default,
            };
            ApplyResearch(world, data.LoadResearch(file));
        }

        // RESTRICT_CONSTRUCTION locks just the one entry for the races it names (MachLogScenario::parseRestrictConstruction);
        // GameWorld.Buildable leaves it out while the option holds.
        foreach (var restriction in scenario.ConstructionRestrictions)
        {
            foreach (var state in world.Races.Values.Where(s => Targets(restriction.Race, s)))
                state.Locked.Add(new ConstructionOrderEntry(restriction.Type, restriction.SubType, restriction.HardwareLevel));
        }

        // As in the original, a pod victory needs pods: without any, the match falls back to annihilation.
        if (options.Victory == VictoryCondition.Pod && !world.Constructions.Any(c => c.IsType("POD")))
            world.Options = options with { Victory = VictoryCondition.Annihilation };
        foreach (var state in world.Races.Values)
        {
            state.StartedWithPod = world.Constructions.Any(c => c.Race == state.Race && c.IsType("POD"));
            state.ConstructionsReached = world.BestConstructor(state.Race);
        }

        // A campaign mission runs its script, which alone decides it; so does Default, if the scenario has one.
        if (options.Victory is VictoryCondition.Scripted or VictoryCondition.Default)
        {
            var script = MissionScript.Compile(scenario, options.Campaign);
            if (options.Victory == VictoryCondition.Default && script.ActionsLeft == 0)
                world.Options = options with { Victory = VictoryCondition.Annihilation };
            else
                world.Script = script;
        }

        if (story is not null)
            world.StartStory(story);
        else if (battle is not null)
            world.StartBattle(battle, scenarioName);
        else if (options.Wired is { } run)
        {
            // Where the others would have started, the waves may arrive.
            var starts = Enum.GetValues<Race>().Where(r => !players.HasHuman(r))
                .Select(scenario.Race).OfType<RaceSetup>()
                .SelectMany(setup => setup.Constructions.Where(b => b.Type == "POD").Select(b => b.Position).Concat(setup.Machines.Select(m => m.Position)).Take(1))
                .Select(p => new Vector2(p.X, p.Y));
            world.StartWired(run, starts);
        }
        return world;
    }

    /// <summary>
    /// The players of a battle: the given rows of the teams' races, and a bot for every seat (<see cref="BattleSeats.Of"/>)
    /// no row names, named after the hero the battle composes for it. A row of a team's race that is none of its seats would
    /// be a player without a hero, and is refused.
    /// </summary>
    private static PlayerTable Seated(PlayerTable given, BattleOptions battle, IReadOnlyList<Race> races, string scenario)
    {
        var seats = BattleSeats.Of(battle);
        var rows = given.Rows.Where(r => races.Contains(r.Race)).ToList();
        if (rows.FirstOrDefault(r => !seats.Any(seat => seat.Player == r.Id && seat.Race == r.Race)) is { } stray)
            throw new ArgumentException($"player {stray.Id} of {stray.Race} has no seat in this battle");
        var missing = seats.Where(seat => rows.All(r => r.Id != seat.Player)).ToList();
        if (missing.Count == 0)
            return rows.Count == given.Rows.Count ? given : new PlayerTable(rows);
        var heroes = BattleBots.Compose(battle, battle.Heroes, scenario).Heroes;
        foreach (var seat in missing)
        {
            var name = heroes.FirstOrDefault(h => (h.Player ?? PlayerId.Of(h.Race)) == seat.Player)?.Name ?? BattleBots.TeamName(seat.Team, seat.Place);
            rows.Add(new PlayerSeat(seat.Player, seat.Race, PlayerKind.Bot, name));
        }
        return new PlayerTable(rows);
    }

    /// <summary>What of a race's buildings stands in a battle: the first pod of each team's anchor, the first race of the team.</summary>
    private static IEnumerable<ScenarioConstruction> BattlePod(RaceSetup setup, Race race, BattleOptions battle) =>
        battle.TeamA[0] == race || battle.TeamB[0] == race ? setup.Constructions.Where(b => b.Type == "POD" && !b.NotBuilt).Take(1) : [];

    /// <summary>What of a race's buildings stands in a Wired run: the first pod of a race people play, and nothing of the others.</summary>
    private static IEnumerable<ScenarioConstruction> WiredBuildings(RaceSetup setup, bool played) =>
        played ? setup.Constructions.Where(b => b.Type == "POD" && !b.NotBuilt).Take(1) : [];

    private static void ApplyResearch(GameWorld world, ScenarioResearch research)
    {
        foreach (var entry in research.Entries)
        {
            if (world.Data.ResearchTree.Find(entry.Key) is null)
                continue; // known data defects (see data/README.md)

            foreach (var state in world.Races.Values.Where(s => Targets(entry.Race, s)))
            {
                state.Available.Add(entry.Key);
                if (entry.Researched)
                    state.Researched.Add(entry.Key);
                if (entry.ResearchCostOverride is { } cost)
                    state.ResearchCostOverrides[entry.Key] = cost;
            }
        }
    }

    /// <summary>
    /// Whether a scenario line for <paramref name="target"/> applies to the race: a named race, the AI's races, the
    /// players' races, or (without a race) all of them.
    /// </summary>
    private static bool Targets(RaceTarget target, RaceState state) => target switch
    {
        RaceTarget.Red => state.Race == Race.Red,
        RaceTarget.Blue => state.Race == Race.Blue,
        RaceTarget.Green => state.Race == Race.Green,
        RaceTarget.Yellow => state.Race == Race.Yellow,
        RaceTarget.Ai => state.Controller == Controller.Ai,
        RaceTarget.Pc => state.Controller == Controller.Player,
        _ => true,
    };
}
