using System.Globalization;
using System.Text.RegularExpressions;
using Machines.Net;

/// <summary>
/// The P2 acceptance's comparer (design §9, S6): the logs of games and net bots of one match set against each other and
/// against the relay's log. Every log's "checksum at tick N: x" lines must agree with every other's wherever both have the
/// tick (a world rebuilt after going out of step counts with its newest value), and its "pace: tick T head H utc ms lag l
/// away a" lines are set against the relay's clock tick at that moment, <c>(utc − anchor) / 50 ms</c>, from the relay's
/// "room r: bundle 0 was due at ms UTC" line, skipping the stretches a world catches up after it was built (from "net: loaded
/// at tick …, N bundles in hand" with N above 0 or "net: rebuilt from tick" to "net: caught up at tick", or to the first pace
/// line with no more bundles in hand than the pacer's buffer): how often it was within 10 ticks of the clock, the most it stood
/// behind, the slowest 200 ticks, and how many lines say lagging or away. A world that fell behind while it played and caught
/// up again ("net: catching up from tick" of a net bot) is not skipped: that is what keeping speed is about.
/// The logs named with --at-speed must keep speed: within 10 ticks in 95 % of their lines, never more than 40 behind, no 200
/// ticks slower than 190 ticks in 10 s, never lagging. Also lists the relay's events (marks, handovers, returns, joins, out of
/// step, rebuilds, delay changes, the summary). Exit code 0 when all holds.
/// </summary>
internal static class Compare
{
    private static readonly Regex Checksum = new(@"checksum at tick (\d+): ([0-9a-fA-F]{16})");
    private static readonly Regex Pace = new(@"pace: tick (\d+) head (-?\d+) utc (\d+) lag (\d) away (\d)");
    private static readonly Regex CatchUpFrom = new(@"net: loaded at tick \d+, ([1-9]\d*) bundles in hand|net: rebuilt from tick");
    private static readonly Regex CaughtUp = new(@"net: caught up at tick");
    private static readonly Regex Events = new(
        @"bundle 0 was due|the beat starts|marked at|handed over|back at tick|on time again|out of step|resync|rejoin of|late join of|delay \d+ \S+ \d+ at tick|fell behind|decided|the beat stops|ticks, \d+ turns moved|taken out");

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("net-bots compare <relay log> [--room id] [--from tick] [--at-speed a.log,b.log] <log>...");
            return 2;
        }
        var relayLog = args[0];
        var (room, from, atSpeed, logs) = ((int?)null, 300L, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new List<string>());
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--room":
                    room = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--from":
                    from = long.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--at-speed":
                    atSpeed.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName)!);
                    break;
                default:
                    logs.Add(args[i]);
                    break;
            }
        }

        var relayLines = File.ReadAllLines(relayLog);
        var anchorPattern = new Regex($@"room {(room is { } id ? id.ToString(CultureInfo.InvariantCulture) : @"\d+")}: bundle 0 was due at (\d+) ms UTC");
        if (relayLines.Select(l => anchorPattern.Match(l)).FirstOrDefault(m => m.Success) is not { } anchorLine)
        {
            Console.Error.WriteLine($"no anchor line in {relayLog}");
            return 2;
        }
        var anchor = long.Parse(anchorLine.Groups[1].Value, CultureInfo.InvariantCulture);
        Console.WriteLine($"relay: {relayLog}, bundle 0 due at {anchor} ms UTC; its events:");
        foreach (var line in relayLines.Where(l => Events.IsMatch(l)))
            Console.WriteLine($"  {line}");

        var ok = true;
        var sums = new List<(string Name, SortedDictionary<long, string> Sums)>();
        Console.WriteLine();
        Console.WriteLine($"pace against the relay's clock from its tick {from} on (catch-ups after a load or a rebuild skipped):");
        foreach (var log in logs)
        {
            var name = Path.GetFileName(log);
            var (mine, samples, lagging, away, twice) = Read(log, anchor, from);
            sums.Add((name, mine));
            var line = $"  {name}: {mine.Count} checksums{(twice > 0 ? $" ({twice} ticks again from a rebuilt world)" : "")}";
            if (samples.Count > 0)
            {
                var within = samples.Count(s => s.Behind <= 10) / (double)samples.Count;
                var most = samples.Max(s => s.Behind);
                // The slowest 200 ticks: between pace lines two apart (one every 100 ticks), as ticks in ten seconds.
                var slowest = samples.Zip(samples.Skip(2), (a, b) => (Ticks: b.Tick - a.Tick, Ms: b.Utc - a.Utc))
                    .Where(p => p.Ticks == 200 && p.Ms > 0).Select(p => p.Ticks * 10_000.0 / p.Ms).DefaultIfEmpty(double.NaN).Min();
                line += $", {samples.Count} pace lines: {within:P1} within 10 ticks of the clock, at most {most} behind, slowest {slowest:F0} ticks in 10 s, " +
                        $"{lagging} lagging, {away} away";
                if (atSpeed.Contains(name))
                {
                    var fast = within >= 0.95 && most <= 40 && !(slowest < 190) && lagging == 0;
                    line += fast ? " - at speed" : " - NOT AT SPEED";
                    ok &= fast;
                }
            }
            else if (atSpeed.Contains(name))
            {
                line += " - no pace lines";
                ok = false;
            }
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("checksums:");
        if (sums.Count > 0)
        {
            var (firstName, first) = sums[0];
            foreach (var (name, mine) in sums.Skip(1))
            {
                var common = mine.Keys.Where(first.ContainsKey).ToList();
                var differ = common.Where(t => first[t] != mine[t]).ToList();
                Console.WriteLine($"  {name} against {firstName}: {common.Count} ticks in common, {differ.Count} differ" +
                                  (differ.Count > 0 ? $" (first at tick {differ[0]}: {mine[differ[0]]} against {first[differ[0]]})" : ""));
                ok &= differ.Count == 0;
            }
            // And every pair, beyond the first log's ticks.
            var all = sums.SelectMany(s => s.Sums.Select(kv => (s.Name, Tick: kv.Key, Value: kv.Value))).GroupBy(s => s.Tick).ToList();
            var split = all.Where(g => g.Select(s => s.Value).Distinct().Count() > 1).Select(g => g.Key).ToList();
            var shared = sums.Select(s => s.Sums.Keys.DefaultIfEmpty(-1).Max()).Min();
            Console.WriteLine($"  {all.Count} ticks reported in all, {split.Count} with values that differ{(split.Count > 0 ? $" (first {split[0]})" : "")}; " +
                              $"the last tick every log has: {shared}{(shared >= 0 && sums.All(s => s.Sums.ContainsKey(shared)) ? $", {sums.Select(s => s.Sums[shared]).Distinct().Count()} value(s) there" : "")}");
            ok &= split.Count == 0;
        }
        Console.WriteLine(ok ? "all holds" : "SOMETHING FAILED");
        return ok ? 0 : 1;
    }

    private static (SortedDictionary<long, string> Sums, List<(long Tick, long Utc, long Behind)> Samples, int Lagging, int Away, int Twice) Read(string log, long anchor, long from)
    {
        var sums = new SortedDictionary<long, string>();
        var samples = new List<(long Tick, long Utc, long Behind)>();
        var (lagging, away, twice, catchingUp) = (0, 0, 0, false);
        foreach (var line in File.ReadLines(log))
        {
            if (Checksum.Match(line) is { Success: true } sum)
            {
                var tick = long.Parse(sum.Groups[1].Value, CultureInfo.InvariantCulture);
                if (sums.ContainsKey(tick))
                    twice++;
                sums[tick] = sum.Groups[2].Value.ToLowerInvariant();
            }
            else if (CatchUpFrom.IsMatch(line))
                catchingUp = true;
            else if (CaughtUp.IsMatch(line))
                catchingUp = false;
            else if (Pace.Match(line) is { Success: true } pace)
            {
                var tick = long.Parse(pace.Groups[1].Value, CultureInfo.InvariantCulture);
                // A world built with fewer bundles in hand than a catch-up takes works them off without saying it caught up:
                // once a pace line shows no more in hand than the pacer's buffer, the world plays at the beat's pace again.
                if (catchingUp && long.Parse(pace.Groups[2].Value, CultureInfo.InvariantCulture) - tick + 1 <= BundlePacer.Buffer + 1)
                    catchingUp = false;
                if (catchingUp)
                    continue;
                var utc = long.Parse(pace.Groups[3].Value, CultureInfo.InvariantCulture);
                var clock = (utc - anchor) / 50;
                if (clock < from)
                    continue;
                samples.Add((tick, utc, clock - tick));
                lagging += pace.Groups[4].Value == "1" ? 1 : 0;
                away += pace.Groups[5].Value == "1" ? 1 : 0;
            }
        }
        return (sums, samples, lagging, away, twice);
    }
}
