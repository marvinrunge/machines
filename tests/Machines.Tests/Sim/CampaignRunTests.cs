using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Machines.Import.Audio;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// Every campaign mission played for ten minutes: the AI on the computer's sides, and on the player's a simple
/// driver, who sends every machine to the script's REACHED_LOCATION points one after the other, a minute each, and
/// then at the nearest enemy. Nothing may throw, and every voice mail must be one the game has. Long (some minutes):
/// it runs only when MACHINES_MISSION_RUNS names a file, which gets a line per mission: how it ended, the script lines
/// that fired, the mails and the tasks.
/// </summary>
public class CampaignRunTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));
    private static readonly string? Report = Environment.GetEnvironmentVariable("MACHINES_MISSION_RUNS") is { Length: > 0 } path ? path : null;

    private const float Minutes = 10;

    private sealed class MissionRunAttribute : TheoryAttribute
    {
        public MissionRunAttribute()
        {
            if (Report is null)
                Skip = "set MACHINES_MISSION_RUNS=<report file> to play every mission for ten minutes";
        }
    }

    [MissionRun]
    [MemberData(nameof(MissionScriptTests.CampaignScenarios), MemberType = typeof(MissionScriptTests))]
    public void A_mission_plays_ten_minutes_without_fault(string name)
    {
        var data = DataLoader.Value;
        var entry = ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat")).CampaignScenarios.First(s => s.Name == name);
        var scenario = data.LoadScenario(name);
        var players = MissionScript.CampaignPlayers(scenario);
        var player = players.First(p => p.Value == Controller.Player).Key;
        var world = MatchSetup.Create(data, name, entry.Planet, players, seed: 7, new MatchOptions { Victory = VictoryCondition.Scripted });
        var vids = VoiceMailParser.Load(RepoPaths.OriginalData(Path.Combine("sounds", "vemail", "vemail.dat"))).Select(v => v.Id).ToHashSet();
        var points = scenario.Conditions
            .Where(c => c.Type == "REACHED_LOCATION" && c.Args.Count > 1 && c.Args[1] == player.ToString().ToUpperInvariant())
            .Select(c => c.Args.ToList().IndexOf("AT") is var at and >= 0
                ? new Vector2(float.Parse(c.Args[at + 1], CultureInfo.InvariantCulture), float.Parse(c.Args[at + 2], CultureInfo.InvariantCulture))
                : (Vector2?)null)
            .OfType<Vector2>().Distinct().ToList();

        var mails = new List<string>();
        var tasks = new List<string>();
        var clock = Stopwatch.StartNew();
        // Before the run, so that a mission that never comes back shows in the report.
        lock (DataLoader)
            File.AppendAllText(Report!, $"{name,-8} starts\n");
        var ticks = (int)(Minutes * 60 * GameWorld.TicksPerSecond);
        var decidedAt = -1f;
        for (var tick = 0; tick < ticks && decidedAt < 0; tick++)
        {
            if (tick % (15 * GameWorld.TicksPerSecond) == 0)
                Drive(world, player, points, tick / GameWorld.TicksPerSecond);
            // Where the run is, beside the report, for a mission that hangs.
            if (tick % (5 * GameWorld.TicksPerSecond) == 0)
                File.WriteAllText(Report + ".at", $"{name} {world.Time:0}s tick {world.Tick}\n");
            world.Step();
            foreach (var e in world.Events)
            {
                if (e.Kind == GameEventKind.VoiceMail)
                {
                    Assert.Contains(e.Detail!, vids);
                    if (e.Other == (int)player)
                        mails.Add(e.Detail!);
                }
                else if (e.Kind == GameEventKind.TaskChanged)
                {
                    tasks.Add($"{e.Other}:{e.Detail}");
                }
            }
            if (world.Winner is not null || world.Races[player].Defeated)
                decidedAt = world.Time;
        }

        var script = world.Script!;
        var outcome = world.Winner == player ? "WON" : world.Races[player].Defeated ? "LOST" : world.Winner is { } other ? $"{other} won" : "open";
        var standing = string.Join(" ", world.Races.Keys.Select(r => $"{r}:{world.Machines.Count(m => m.Race == r && !m.IsDead)}m/{world.Constructions.Count(c => c.Race == r && !c.IsDead)}c"));
        lock (DataLoader)
            File.AppendAllText(Report!,
                $"{name,-8} {outcome,-9} at {(decidedAt < 0 ? world.Time : decidedAt),5:0}s  fired {script.Fired.Count}/{script.Fired.Count + script.ActionsLeft} [{string.Join(",", script.Fired)}]" +
                $"  tasks [{string.Join(" ", tasks)}]  mails {mails.Count} [{string.Join(" ", mails.Distinct())}]  flags [{string.Join(",", script.FlagsSet)}]  {standing}" +
                $"  ({clock.Elapsed.TotalSeconds:0}s)\n");
    }

    /// <summary>The player's side: a minute at each of the script's points in turn, then at the nearest enemy.</summary>
    private static void Drive(GameWorld world, Race race, List<Vector2> points, int second)
    {
        var own = world.Machines.Where(m => m.Race == race && !m.IsDead).ToList();
        if (own.Count == 0)
            return;
        var ids = own.Select(m => m.Id).ToList();
        var stage = second / 60;
        if (stage < points.Count)
        {
            world.Issue(new MoveCommand(race, ids, points[stage]));
            return;
        }
        var centre = own.Aggregate(Vector2.Zero, (sum, m) => sum + m.Position) / own.Count;
        var enemy = world.Machines.Where(m => !m.IsDead && world.AreEnemies(race, m.Race)).Select(m => (m.Id, m.Position))
            .Concat(world.Constructions.Where(c => !c.IsDead && world.AreEnemies(race, c.Race)).Select(c => (c.Id, c.Position)))
            .OrderBy(a => Vector2.Distance(a.Position, centre))
            .Select(a => (int?)a.Id).FirstOrDefault();
        if (enemy is { } target)
            world.Issue(new AttackCommand(race, ids, target));
    }
}
