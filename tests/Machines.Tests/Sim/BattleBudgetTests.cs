using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Machines.Sim;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Sim;

/// <summary>
/// What a battle of five, ten and fifteen a side costs the sim a tick (P2 design §7, slice S5), to back offering ten against
/// ten and accepting fifteen: opt-in, as it plays for a minute or two and its figures are only worth reading from a Release
/// build (<c>MACHINES_BATTLE_BUDGET=&lt;report file&gt; dotnet test -c Release --filter BattleBudgetTests</c>).
/// </summary>
public class BattleBudgetTests(ITestOutputHelper output)
{
    private static readonly string? Report = Environment.GetEnvironmentVariable("MACHINES_BATTLE_BUDGET") is { Length: > 0 } path ? path : null;

    private sealed class BattleBudgetAttribute : FactAttribute
    {
        public BattleBudgetAttribute()
        {
            if (Report is null)
                Skip = "set MACHINES_BATTLE_BUDGET=<report file> (and build Release) to measure what 5, 10 and 15 a side cost the sim a tick";
        }
    }

    /// <summary>The ticks a measured battle runs at most: five minutes, past the opening's first orders and into the fight.</summary>
    private const int Ticks = 5 * 60 * GameWorld.TicksPerSecond;

    /// <summary>
    /// Battles on the Desert at tier 3 with the ground of the export where it has been made (as the game plays them), played
    /// one after another on one thread: every hero a bot, then every hero a person's who sends a <see cref="DriveCommand"/>
    /// each tick, at five, ten and fifteen a side, with the lives a room proposes. The figures are the sim's step alone, the
    /// commands of the tick applied in it (what every client and a rebuild spend a tick; the people's own reckoning of where
    /// to drive is their clients' and is not counted), with the 50 ms of a tick as the budget.
    /// </summary>
    [BattleBudget]
    public void Five_ten_and_fifteen_a_side_cost_the_sim_this_much_a_tick()
    {
        var optimized = typeof(GameWorld).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;
        var ground = Ground();
        var lines = new List<string>
        {
            $"Battle budget on m_desert, tier 3, {(ground is null ? "no ground (the export is not made)" : "with the export's ground")}, sim built {(optimized ? "Release (optimized)" : "Debug (NOT optimized: build with -c Release)")}, " +
            $"{Environment.ProcessorCount} logical cores, .NET {Environment.Version}; at most {Ticks} ticks a battle, one thread, the 50 ms tick the budget:",
        };
        // The first battles warm the JIT and the data up; they are not counted.
        Measure(() => Battle(5, people: false, ground), 600);
        Measure(() => Battle(5, people: true, ground), 600);
        foreach (var people in new[] { false, true })
        {
            foreach (var size in new[] { 5, 10, 15 })
                lines.Add(Measure(() => Battle(size, people, ground), Ticks).Line(size, people));
        }
        foreach (var line in lines)
            output.WriteLine(line);
        File.WriteAllLines(Report!, lines);
    }

    private static TerrainHeights? Ground()
    {
        var repository = Path.GetFullPath(Path.Combine(RepoPaths.OriginalDataFolder(), "..", ".."));
        var heights = Path.Combine(repository, "classic", "planets", "m_desert", "heights.bin");
        return File.Exists(heights) ? TerrainHeights.FromExport(File.ReadAllBytes(heights)) : null;
    }

    /// <summary>A battle of a team size on the Desert at tier 3, every seat a bot's or a person's whose hero they drive.</summary>
    private static GameWorld Battle(int size, bool people, TerrainHeights? ground)
    {
        var entry = BattleCatalogue.Map("m_desert")!;
        var options = new BattleOptions { Tier = 3, TeamSize = size, TeamA = entry.TeamA, TeamB = entry.TeamB, Lives = BattleBalance.ProposedLives(size) };
        var rows = BattleSeats.Of(options).Select(seat => new PlayerSeat(seat.Player, seat.Race, people ? PlayerKind.Human : PlayerKind.Bot, $"Seat {seat.Player.Value}")).ToList();
        var world = MatchSetup.Create(Battles.Data, entry.Scenario, entry.Planet, new PlayerTable(rows), 7, new MatchOptions { Battle = options, FogOfWar = false });
        if (ground is not null)
            world.Terrain = ground;
        return world;
    }

    /// <summary>
    /// A person at the wheel of their hero, as the game's first person sends a drive every tick while driven: towards the
    /// nearest enemy hero along a way looked up now and then while it is far, straight at it once it is near, aiming at it,
    /// firing and now and then looking about.
    /// </summary>
    private sealed class Pilot(PlayerId player, int seed)
    {
        /// <summary>Within this many metres the person drives straight at the enemy and fires.</summary>
        private const float Near = 220;

        private readonly Random _random = new(seed);
        private List<Vector2> _way = [];
        private int _plannedAt = -1000;

        public DriveCommand Drive(GameWorld world, Machine hero)
        {
            var race = hero.Race;
            Machine? enemy = null;
            var nearest = float.MaxValue;
            foreach (var other in world.Machines)
            {
                if (other.IsDead || !world.AreEnemies(race, other.Race))
                    continue;
                var distance = Vector2.DistanceSquared(other.Position, hero.Position);
                if (distance < nearest)
                    (enemy, nearest) = (other, distance);
            }
            var target = enemy?.Position ?? world.BattlePod(1 - world.Battle!.TeamOf(player))?.Position ?? hero.Position;
            var close = enemy is not null && nearest < Near * Near;
            if (!close && (world.Tick - _plannedAt >= 100 || _way.Count == 0))
            {
                _way = world.Nav.FindPath(hero.Position, target, crossesWater: false);
                _plannedAt = world.Tick;
            }
            while (_way.Count > 0 && Vector2.Distance(_way[0], hero.Position) < 12)
                _way.RemoveAt(0);
            var goal = close || _way.Count == 0 ? target : _way[0];
            var way = goal - hero.Position;
            var off = MathF.IEEERemainder(DetMath.Atan2(way.Y, way.X) - hero.Facing, 2 * MathF.PI);
            var turn = off > 0.15f ? 1 : off < -0.15f ? -1 : 0;
            var aim = target - hero.Position;
            return new DriveCommand(race, hero.Id, true, MathF.Abs(off) > 1.2f ? 0.3f : 1, turn, target, Weapons: close ? 1 + _random.Next(3) : 0,
                Head: (float)(_random.NextDouble() - 0.5), Buttons: close && _random.Next(2) == 0, Yaw: DetMath.Atan2(aim.Y, aim.X)) { Issuer = player };
        }
    }

    private sealed record Figures(int Ticks, double Mean, double Median, double P99, double Worst, long WorstTick, string WorstParts, double Seconds, long Allocated, long Heap, string End, string Fight)
    {
        public string Line(int size, bool people) =>
            $"{size} vs {size}, {(people ? "every hero driven by a person each tick" : "every hero a bot")}: {Ticks} ticks, mean {Mean:F3} ms, median {Median:F3} ms, " +
            $"p99 {P99:F3} ms, worst {Worst:F1} ms (tick {WorstTick}: {WorstParts}), {Ticks / Seconds:F0} ticks/s of the step alone, mean {Mean / 50 * 100:F2} % and p99 {P99 / 50 * 100:F1} % of the tick, " +
            $"{Allocated / Math.Max(Ticks, 1) / 1024.0:F1} KiB allocated a tick, heap {Heap / (1024.0 * 1024):F0} MiB after; {End}; {Fight}";
    }

    /// <summary>
    /// Plays a battle for at most <paramref name="most"/> ticks or to its end and times each step; then plays it again to
    /// its worst tick (the same, as the sim and the pilots are deterministic) to say which parts of that step took longest.
    /// </summary>
    private static Figures Measure(Func<GameWorld> make, int most)
    {
        var world = make();
        var pilots = Pilots(world);
        var times = new List<double>(most);
        var (worst, worstTick, worstCollections) = (0.0, 0L, "");
        var starts = world.Wired!.Heroes.Keys.Where(p => world.HeroOf(p) is not null).ToDictionary(p => p, p => world.HeroOf(p)!.Position);
        var travelled = new Dictionary<PlayerId, float>();
        GC.Collect();
        long allocated = 0;
        while (times.Count < most && !world.Battle!.Over)
        {
            Drive(world, pilots);
            var tick = world.Tick;
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var collections = Collections();
            var start = Stopwatch.GetTimestamp();
            world.Step();
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
            times.Add(ms);
            if (ms > worst)
            {
                var after = Collections();
                var collected = Enumerable.Range(0, 3).Where(generation => after[generation] > collections[generation]).Select(generation => $"gen{generation}").ToList();
                (worst, worstTick, worstCollections) = (ms, tick, collected.Count == 0 ? "no collection" : "a collection of " + string.Join("+", collected));
            }
            foreach (var (player, from) in starts)
            {
                if (world.HeroOf(player) is { } standing)
                    travelled[player] = MathF.Max(travelled.GetValueOrDefault(player), Vector2.Distance(standing.Position, from));
            }
        }
        var heap = GC.GetTotalMemory(forceFullCollection: true);
        var sorted = times.Order().ToList();
        double At(double share) => sorted[Math.Min(sorted.Count - 1, (int)(share * sorted.Count))];
        var run = world.Battle!;
        var end = run.Result is { } result ? $"ended at tick {world.Tick} ({result.Reason})" : $"still on at tick {world.Tick}";
        // How much fighting the figures stand for.
        var fight = $"hero damage {run.Tally.Values.Sum(t => t.Damage)}, kills {run.Tally.Values.Sum(t => t.Kills)}, " +
            $"heroes standing {world.Wired.Heroes.Keys.Count(p => world.HeroOf(p) is not null)} of {world.Wired.Heroes.Count}, " +
            $"heroes at most {travelled.Values.Sum() / Math.Max(travelled.Count, 1):F0} m from their start on average";
        return new Figures(times.Count, times.Average(), At(0.5), At(0.99), worst, worstTick, $"{worstCollections} in it; played again {Parts(make, worstTick)}", times.Sum() / 1000, allocated, heap, end, fight);
    }

    private static int[] Collections() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private static Dictionary<PlayerId, Pilot> Pilots(GameWorld world) =>
        world.Players.Rows.Where(r => r.Kind == PlayerKind.Human).ToDictionary(r => r.Id, r => new Pilot(r.Id, 11 + r.Id.Value));

    private static void Drive(GameWorld world, Dictionary<PlayerId, Pilot> pilots)
    {
        foreach (var (player, pilot) in pilots)
        {
            if (world.HeroOf(player) is { } hero)
            {
                hero.DrivenByPlayer = true;
                world.Issue(pilot.Drive(world, hero));
            }
        }
    }

    /// <summary>
    /// The parts of the step at <paramref name="tick"/> that took longest, played to it again with the world's profile on;
    /// what the profile does not cover (above all applying the tick's commands, a move order finding its path) is the rest.
    /// </summary>
    private static string Parts(Func<GameWorld> make, long tick)
    {
        var world = make();
        var pilots = Pilots(world);
        while (world.Tick < tick)
        {
            Drive(world, pilots);
            world.Step();
        }
        Drive(world, pilots);
        world.Profile = [];
        var start = Stopwatch.GetTimestamp();
        world.Step();
        var total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var parts = world.Profile.Where(part => !part.Key.StartsWith(' ')).ToList();
        parts.Add(new("commands and the rest", total - parts.Sum(part => part.Value)));
        return $"{total:F1} ms, " + string.Join(", ", parts.OrderByDescending(part => part.Value).Take(3).Select(part => $"{part.Key} {part.Value:F1} ms"));
    }
}
