// The M3 performance gate: four players with 200 machines each must stay smooth. Builds a four-player match with
// the original AI, adds 200 armed machines per race around its base, sends them all to the middle of the map and
// times every simulation tick (20 a second, so 50 ms is the whole budget of a frame's worth of game time).
//   dotnet run -c Release --project tools/sim-bench [-- <scenario> <planet> <machines per race> <seconds>]
using System.Diagnostics;
using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

var scenario = args.Length > 0 ? args[0] : "m_green";
var planet = args.Length > 1 ? args[1] : scenario;
var perRace = args.Length > 2 ? int.Parse(args[2]) : 200;
var seconds = args.Length > 3 ? int.Parse(args[3]) : 120;

var repo = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(repo, "Machines.sln")))
    repo = Path.GetDirectoryName(repo) ?? throw new InvalidOperationException("run inside the repository");
var data = GameData.Load(Path.Combine(repo, "data", "original"));

var races = data.LoadScenario(scenario).Races.Select(r => r.Race).Take(4).ToList();
var world = MatchSetup.Create(data, scenario, planet, races.ToDictionary(r => r, _ => Controller.Ai), seed: 7);
// With the original terrain exported (tools/classic-export), shots are checked against the ground as in a real
// match; SIM_BENCH_TERRAIN=0 leaves it out.
var heights = Path.Combine(repo, "classic", "planets", planet, "heights.bin");
if (File.Exists(heights) && Environment.GetEnvironmentVariable("SIM_BENCH_TERRAIN") != "0")
{
    world.Terrain = Machines.Sim.Navigation.TerrainHeights.FromExport(File.ReadAllBytes(heights));
    Console.WriteLine("terrain: the original ground heights");
}

// A mix of the fighting machines, each with a weapon it can carry.
(string Type, string? SubType, int Level, string Combo)[] roster =
[
    ("AGGRESSOR", "GRUNT", 2, "L_BOLTER"),
    ("AGGRESSOR", "GRUNT", 2, "L_ELECTRIC_CHARGE"),
    ("AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE"),
    ("AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2"),
];
var centre = new Vector2(world.Planet.Surface.Width / 2, world.Planet.Surface.Depth / 2);
foreach (var race in races)
{
    var home = world.Actors.Where(a => a.Race == race).Select(a => a.Position).Aggregate(Vector2.Zero, (s, p) => s + p)
               / Math.Max(1, world.Actors.Count(a => a.Race == race));
    var placed = 0;
    for (var ring = 1; placed < perRace && ring < 80; ring++)
    {
        var spots = 6 * ring;
        for (var s = 0; s < spots && placed < perRace; s++)
        {
            var angle = MathF.Tau * s / spots;
            var spot = home + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (6 * ring + 20);
            if (spot.X < 5 || spot.Y < 5 || spot.X > world.Planet.Surface.Width - 5 || spot.Y > world.Planet.Surface.Depth - 5
                || !world.Nav.IsFree(spot, crossesWater: false))
                continue;
            var (type, subType, level, combo) = roster[placed % roster.Length];
            world.AddMachine(race, spot, type, subType, level, combo);
            placed++;
        }
    }
    var army = world.Machines.Where(m => m.Race == race).Select(m => m.Id).ToList();
    world.Issue(new AttackMoveCommand(race, army, centre));
    Console.WriteLine($"{race}: {army.Count} machines from {home} to {centre}");
}

world.Profile = [];
var times = new List<double>();
var clock = new Stopwatch();
for (var tick = 0; tick < seconds * 20; tick++)
{
    clock.Restart();
    world.Step();
    times.Add(clock.Elapsed.TotalMilliseconds);
    if ((tick + 1) % 200 == 0)
    {
        var recent = times.TakeLast(200).ToList();
        Console.WriteLine($"{(tick + 1) / 20,4} s  machines {world.Machines.Count(),4}  mean {recent.Average(),6:F2} ms  max {recent.Max(),6:F2} ms   "
                          + string.Join("  ", world.Profile.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value / 200:F1}")));
        Console.WriteLine($"        searches {world.Nav.Searches}  expanded {world.Nav.Expanded}  gave up {world.Nav.GaveUp}  calls {world.Nav.Calls}  sight steps {world.Nav.SightSteps}");
        world.Nav.Searches = world.Nav.Expanded = world.Nav.GaveUp = world.Nav.Calls = world.Nav.SightSteps = 0;
        world.Profile.Clear();
    }
}
var sorted = times.Order().ToList();
Console.WriteLine($"all ticks: mean {times.Average():F2} ms, p95 {sorted[(int)(sorted.Count * 0.95)]:F2} ms, p99 {sorted[(int)(sorted.Count * 0.99)]:F2} ms, max {sorted[^1]:F2} ms");
