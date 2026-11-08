// The balance bench of the survival mode Wired: a bot plays whole runs by each buying strategy, over many seeds and all
// four arenas, and the waves it reaches, what it bought and what it earned are printed against the targets.
//   dotnet run -c Release --project tools/wired-bench [-- <seeds> [difficulty|all] [strategies, comma separated|all] [most waves]]
// The timing of a wave of 60 machines, in the manner of tools/sim-bench (the mean tick must stay under 16 ms):
//   dotnet run -c Release --project tools/wired-bench -- perf [arena]
using System.Collections.Concurrent;
using System.Diagnostics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.WiredBench;

const int ThinkEvery = 10;
string[] arenas = ["m_desert", "m_green", "m_moon", "m_dales"];

var repo = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(repo, "Machines.sln")))
    repo = Path.GetDirectoryName(repo) ?? throw new InvalidOperationException("run inside the repository");
var dataFolder = Path.Combine(repo, "data", "original");
var grounds = new ConcurrentDictionary<string, byte[]?>();

GameWorld NewRun(GameData data, string arena, WiredDifficulty difficulty, int seed)
{
    var world = MatchSetup.Create(data, arena, arena,
        new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
        new MatchOptions { Wired = new WiredOptions { Difficulty = difficulty, Arena = arena } });
    // With the original terrain exported, shots are checked against the ground as in a real run.
    var ground = grounds.GetOrAdd(arena, a =>
    {
        var heights = Path.Combine(repo, "classic", "planets", a, "heights.bin");
        return File.Exists(heights) ? File.ReadAllBytes(heights) : null;
    });
    if (ground is not null)
        world.Terrain = TerrainHeights.FromExport(ground);
    return world;
}

if (args.Length > 0 && args[0] == "perf")
{
    Perf(args.Length > 1 ? [args[1]] : arenas);
    return;
}

var seeds = args.Length > 0 ? int.Parse(args[0]) : 20;
var difficulties = args.Length > 1 && args[1] != "all" ? [Enum.Parse<WiredDifficulty>(args[1], ignoreCase: true)] : Enum.GetValues<WiredDifficulty>();
var strategies = args.Length > 2 && args[2] != "all"
    ? Strategy.All.Where(s => args[2].Split(',').Contains(s.Name)).ToList()
    : Strategy.All;
var mostWaves = args.Length > 3 ? int.Parse(args[3]) : 30;

var jobs = (from difficulty in difficulties from strategy in strategies from seed in Enumerable.Range(1, seeds) select (difficulty, strategy, seed)).ToList();
var results = new ConcurrentBag<Result>();
var clock = Stopwatch.StartNew();
using var loaded = new ThreadLocal<GameData>(() => GameData.Load(dataFolder));
// One core stays free for whatever else is running.
Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, job =>
{
    results.Add(Play(loaded.Value!, job.strategy, job.difficulty, job.seed));
    if (results.Count % 50 == 0)
        Console.Error.WriteLine($"{results.Count}/{jobs.Count} runs, {clock.Elapsed.TotalSeconds:0} s");
});

Console.WriteLine($"{jobs.Count} runs in {clock.Elapsed.TotalSeconds:0} s, {seeds} seeds, the four arenas in turn, up to wave {mostWaves}");
Console.WriteLine();
Console.WriteLine($"{"difficulty",-10} {"strategy",-14} {"mean",5} {"median",6} {"min",3} {"max",3} {">=5",5} {">=8",5} {">=20",5} {"deaths",6} {"pod lost",8}");
foreach (var difficulty in difficulties)
{
    foreach (var strategy in strategies)
    {
        var runs = results.Where(r => r.Difficulty == difficulty && r.Strategy == strategy.Name).ToList();
        var waves = runs.Select(r => r.Cleared).Order().ToList();
        Console.WriteLine($"{difficulty,-10} {strategy.Name,-14} {waves.Average(),5:0.0} {waves[waves.Count / 2],6} {waves[0],3} {waves[^1],3} "
            + $"{Share(runs, r => r.Cleared >= 5),5} {Share(runs, r => r.Cleared >= 8),5} {Share(runs, r => r.Cleared >= 20),5} "
            + $"{runs.Average(r => r.Deaths),6:0.0} {Share(runs, r => r.Ending == WiredEnding.PodLost),8}");
    }
}

Console.WriteLine();
Console.WriteLine("Waves cleared by arena, the buying strategies together:");
foreach (var difficulty in difficulties)
{
    foreach (var arena in arenas)
    {
        var runs = results.Where(r => r.Difficulty == difficulty && r.Arena == arena && Buys(r)).ToList();
        if (runs.Count > 0)
            Console.WriteLine($"  {difficulty,-10} {arena,-9} mean {runs.Average(r => r.Cleared),5:0.0}, wave 5 {Share(runs, r => r.Cleared >= 5),5}, wave 8 {Share(runs, r => r.Cleared >= 8),5}");
    }
}

Console.WriteLine();
Console.WriteLine("The wave runs ended at, in percent of the buying strategies' runs:");
foreach (var difficulty in difficulties)
{
    var runs = results.Where(r => r.Difficulty == difficulty && Buys(r)).ToList();
    if (runs.Count > 0)
        Console.WriteLine($"  {difficulty,-10} {string.Join(" ", runs.GroupBy(r => r.Cleared + 1).OrderBy(g => g.Key).Select(g => $"w{g.Key}:{g.Count() * 100 / runs.Count}"))}");
}

Console.WriteLine();
Console.WriteLine("The same without buying, with what it found on the way:");
foreach (var difficulty in difficulties)
{
    var runs = results.Where(r => r.Difficulty == difficulty && r.Strategy == "none").ToList();
    if (runs.Count > 0)
        Console.WriteLine($"  {difficulty,-10} {string.Join(" ", runs.GroupBy(r => r.Cleared + 1).OrderBy(g => g.Key).Select(g => $"w{g.Key}:{g.Count() * 100 / runs.Count}"))}"
            + $"  pod lost {Share(runs, r => r.Ending == WiredEnding.PodLost)}, by arena {string.Join(" ", arenas.Select(a => $"{a}:{runs.Where(r => r.Arena == a).Select(r => r.Cleared).DefaultIfEmpty(0).Average():0.0}"))}");
}

Console.WriteLine();
Console.WriteLine("What felled the hero, in percent of its deaths, and at which waves:");
foreach (var difficulty in difficulties)
{
    var deaths = results.Where(r => r.Difficulty == difficulty && Buys(r)).SelectMany(r => r.Falls).ToList();
    if (deaths.Count == 0)
        continue;
    Console.WriteLine($"  {difficulty,-10} by weapon: {string.Join(", ", deaths.GroupBy(d => d.Weapon).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count() * 100 / deaths.Count}"))}");
    Console.WriteLine($"  {"",-10} by wave:   {string.Join(" ", deaths.GroupBy(d => d.Wave).OrderBy(g => g.Key).Select(g => $"w{g.Key}:{g.Count() * 100 / deaths.Count}"))}");
}

Console.WriteLine();
Console.WriteLine("Strategies against the targets (the buying ones together, by difficulty):");
foreach (var difficulty in difficulties)
{
    var runs = results.Where(r => r.Difficulty == difficulty && Buys(r)).ToList();
    if (runs.Count > 0)
        Console.WriteLine($"  {difficulty,-10} wave 5 cleared {Share(runs, r => r.Cleared >= 5)}, fell before clearing wave 20 {Share(runs, r => r.Cleared < 20)}");
    foreach (var (name, what) in new[] { ("none", "without buying"), ("bare", "without buying or wearing what it found") })
    {
        var idle = results.Where(r => r.Difficulty == difficulty && r.Strategy == name).ToList();
        if (idle.Count > 0)
            Console.WriteLine($"  {difficulty,-10} {what}: fell before clearing wave 8 {Share(idle, r => r.Cleared < 8)}");
    }
}

Console.WriteLine();
Console.WriteLine("BMUs earned by the end of a wave, mean of the runs that got there:");
Console.WriteLine($"{"difficulty",-10} {string.Join(" ", Enumerable.Range(1, 10).Select(w => $"{"w" + w,6}"))}");
foreach (var difficulty in difficulties)
{
    var runs = results.Where(r => r.Difficulty == difficulty && Buys(r)).ToList();
    var earned = Enumerable.Range(1, 10).Select(w => runs.Where(r => r.Earned.Count >= w).Select(r => r.Earned[w - 1]).DefaultIfEmpty(0).Average());
    Console.WriteLine($"{difficulty,-10} {string.Join(" ", earned.Select(e => $"{e,6:0}"))}");
}

Console.WriteLine();
Console.WriteLine("Purchases per run, by strategy, Normal first:");
foreach (var strategy in strategies)
{
    var runs = results.Where(r => r.Strategy == strategy.Name).OrderBy(r => r.Difficulty).ToList();
    var bought = runs.SelectMany(r => r.Purchases).GroupBy(p => p.Key).Select(g => (Name: g.Key, Count: g.Sum(p => p.Value) / (float)runs.Count))
        .OrderByDescending(p => p.Count).ThenBy(p => p.Name, StringComparer.Ordinal).Take(12);
    Console.WriteLine($"  {strategy.Name,-14} {string.Join(", ", bought.Select(p => $"{p.Name} {p.Count:0.0}"))}");
}

Console.WriteLine();
var allBought = results.SelectMany(r => r.Purchases).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
Console.WriteLine("Chassis bought in any strategy: " + string.Join(", ", WiredCatalogue.Chassis.Skip(1).Select(c => $"{c.Id} {allBought.GetValueOrDefault(c.Id)}")));
Console.WriteLine("Weapons bought in any strategy: " + string.Join(", ", WiredCatalogue.Weapons.Select(w => $"{w.Id} {allBought.GetValueOrDefault(w.Id)}")));
var total = WiredCatalogue.Weapons.Sum(w => allBought.GetValueOrDefault(w.Id));
if (total > 0)
{
    var top = WiredCatalogue.Weapons.MaxBy(w => allBought.GetValueOrDefault(w.Id))!;
    Console.WriteLine($"The weapon bought most is {top.Id}, {allBought.GetValueOrDefault(top.Id) * 100 / total} % of all weapons bought");
}
return;

bool Buys(Result run) => Strategy.All.First(s => s.Name == run.Strategy).Buys;

string Share(List<Result> runs, Func<Result, bool> holds) => $"{runs.Count(holds) * 100 / Math.Max(1, runs.Count)}%";

Result Play(GameData data, Strategy strategy, WiredDifficulty difficulty, int seed)
{
    var arena = arenas[seed % arenas.Length];
    var world = NewRun(data, arena, difficulty, seed);
    var run = world.Wired!;
    var state = run.Heroes[Race.Red];
    var bot = new Bot(world, data, strategy);
    var earned = new List<int>();
    var falls = new List<(int Wave, string Weapon)>();
    var lastWeapon = "?";
    // Two hours of game time: a run that goes on longer hangs, and the bench should say so and not wait for it.
    for (var tick = 0; tick < 20 * 7200 && run.Phase != WiredPhase.Ended && state.Stats.WavesCleared < mostWaves; tick++)
    {
        if (tick % ThinkEvery == 0)
            bot.Think();
        world.Step();
        foreach (var happened in world.Events)
        {
            if (happened.Kind == GameEventKind.Hit && happened.Actor == state.Hero && happened.Detail is { } weapon)
                lastWeapon = weapon;
            if (happened.Kind == GameEventKind.WiredHeroDied)
                falls.Add((run.Wave, lastWeapon));
        }
        while (earned.Count < state.Stats.WavesCleared)
            earned.Add(state.Stats.BmusEarned);
    }
    return new Result(strategy.Name, difficulty, seed, arena, state.Stats.WavesCleared, run.Ending, state.Stats.Deaths, earned, bot.Purchases, falls);
}

void Perf(IReadOnlyList<string> places)
{
    const int Wave = 59, Seconds = 480, AtThePod = 20;
    const float Near = 150;
    var data = GameData.Load(dataFolder);
    foreach (var arena in places)
    {
        var world = NewRun(data, arena, WiredDifficulty.Hardcore, seed: 7);
        var run = world.Wired!;
        var state = run.Heroes[Race.Red];
        var pod = world.WiredPod(Race.Red)!;
        run.Wave = Wave - 1;
        run.Countdown = 0;
        var (ticks, fighting) = (new List<double>(), new List<double>());
        var most = 0;
        for (var tick = 0; tick < 20 * Seconds; tick++)
        {
            // Pod and hero hold out, so that the machines stay on the map; the timing is of the machines.
            pod.HitPoints = pod.MaxHitPoints;
            state.SpareBodies = 5;
            var clock = Stopwatch.StartNew();
            world.Step();
            var spent = clock.Elapsed.TotalMilliseconds;
            if (run.Foes.Count >= 40)
                ticks.Add(spent);
            // The march costs little; what counts is the fight round the pod.
            if (run.Foes.Count(f => f.Machine.DistanceTo(pod) <= Near) >= AtThePod)
                fighting.Add(spent);
            most = Math.Max(most, run.Foes.Count);
        }
        Console.WriteLine($"{arena}: {most} machines at most; 40 or more on the map: {Timing(ticks)}; {AtThePod} or more at the pod: {Timing(fighting)}");
    }

    static string Timing(List<double> ticks)
    {
        if (ticks.Count == 0)
            return "never";
        ticks.Sort();
        return $"{ticks.Count} ticks, mean {ticks.Average():0.00} ms, p99 {ticks[ticks.Count * 99 / 100]:0.00} ms, worst {ticks[^1]:0.00} ms";
    }
}

/// <summary>What a run came to.</summary>
internal sealed record Result(string Strategy, WiredDifficulty Difficulty, int Seed, string Arena, int Cleared, WiredEnding? Ending, int Deaths,
    IReadOnlyList<int> Earned, IReadOnlyDictionary<string, int> Purchases, IReadOnlyList<(int Wave, string Weapon)> Falls);
