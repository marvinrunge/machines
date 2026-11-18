using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Xunit.Abstractions;

namespace Machines.Tests.Net;

/// <summary>
/// The acceptance of P2 (design §9, S6): the relay runs as its own process, as on Cloud Run, on its real timings (a bundle
/// every 50 ms, the mark after 3 s late, the handover after 30 s), and people's clients play a battle on its beat, each on
/// a thread of its own with both of the pacer's rules, each person's hero driven by a sim battle bot through their turns.
/// Ten of them play five against five while one computer cannot keep up for 45 s and another's world goes its own way, and
/// everyone else keeps speed with the relay's clock, the slow one is marked, handed over and back, the odd one is rebuilt and
/// back, every world agrees and every recording plays back; twenty play ten against ten and keep speed, which backs offering
/// that size in RoomOptions.TeamSizes (it passes in Release since turns say how many bundles are in hand from a fresh read;
/// in Debug the opening's path searches of twenty worlds in one process, 25,000 to 30,000 cells each around tick 200, can
/// still hold them back long enough to be marked). Opt-in (<c>MACHINES_P2_ACCEPT=1</c>, optionally <c>MACHINES_P2_ACCEPT_REPORT=&lt;file&gt;</c> for the figures),
/// as each plays for minutes on the real clock; run alone, nothing else on the computer.
/// </summary>
[Collection(nameof(BeatRelayTests))]
public class BeatAcceptanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private sealed class AcceptanceAttribute : FactAttribute
    {
        public AcceptanceAttribute()
        {
            if (Environment.GetEnvironmentVariable("MACHINES_P2_ACCEPT") != "1")
                Skip = "set MACHINES_P2_ACCEPT=1 to play P2's acceptance battles on a relay of its own (minutes each)";
        }
    }

    private static readonly string[] Names =
    [
        "Anna", "Ben", "Cleo", "Dora", "Slow", "Emil", "Fred", "Odd", "Gina", "Hugo",
        "Ida", "Jon", "Kim", "Lou", "Mia", "Ned", "Ola", "Pit", "Quin", "Rosa",
    ];

    /// <summary>The relay's clock tick from which the people's worlds must keep speed: the start's load and warm-up are over.</summary>
    private const long AtSpeedFrom = 300;

    [Acceptance]
    public async Task Ten_people_keep_the_beat_through_a_throttled_client_and_a_desync()
    {
        var run = await Play(teamSize: 5, people: 10, ticks: 4000, (Slow: "Slow", Odd: "Odd"));
        var (relay, clients, room, end) = (run.Relay, run.Clients, run.Room, run.End);
        var slow = clients.Single(c => c.Name == "Slow");
        var odd = clients.Single(c => c.Name == "Odd");
        var others = clients.Where(c => c != slow && c != odd).ToList();
        var anna = clients[0];
        var arrivals = anna.Arrivals.ToList();
        long Due(long tick) => run.Anchor + tick * 50;
        long HeardAt<T>(PlayerId player, long after, Func<T, bool>? also = null) where T : NetMessage =>
            arrivals.FirstOrDefault(a => a.Change is T change && Who(change) == player && a.Utc >= after && (also is null || also(change))).Utc;

        // (a) Everyone else kept speed with the relay's clock after tick 300 and was never marked.
        foreach (var client in others)
            Hold($"(a) {client.Name}", () => Say($"(a) {client.Name}: {RelayClients.AssertAtSpeed(client, AtSpeedFrom, end)}"));
        var marked = clients.SelectMany(c => c.Heard).OfType<PlayerLagging>().Where(l => l.Lagging).Select(l => l.Player).Distinct().ToList();
        Hold("(a) never marked", () => Assert.All(others, c => Assert.DoesNotContain(c.Me, marked)));

        // (b) Slow: its first late bundle once throttled, where no turn of its asking for that bundle or later had gone out by
        // the bundle's due time; the mark 2.5–4 s after it, the handover 29–31 s after it, back within 5 s of the throttle's end.
        Hold("(b)", () =>
        {
            Assert.True(slow.ThrottledAt is not null && slow.ThrottleOverAt is not null, "Slow was never throttled, or its throttle never ended");
            var stamps = slow.Stamps.OrderBy(s => s.Utc).ToList();
            var firstLate = FirstLate(stamps, run.Anchor, (slow.ThrottledAt!.Value - run.Anchor) / 50);
            var markedAt = HeardAt<PlayerLagging>(slow.Me, Due(firstLate), l => l.Lagging);
            var leftAt = HeardAt<PlayerLeft>(slow.Me, Due(firstLate));
            var backAt = HeardAt<PlayerReturned>(slow.Me, leftAt);
            Assert.True(markedAt > 0 && leftAt > 0 && backAt > 0, $"Slow marked at {markedAt}, handed over at {leftAt}, back at {backAt} (Unix ms)");
            var (toMark, toHandover, back) = (markedAt - Due(firstLate), leftAt - Due(firstLate), backAt - slow.ThrottleOverAt!.Value);
            Say($"(b) Slow throttled at clock tick {(slow.ThrottledAt.Value - run.Anchor) / 50} for {(slow.ThrottleOverAt.Value - slow.ThrottledAt.Value) / 1000.0:F1} s; " +
                $"first late bundle {firstLate}; marked {toMark / 1000.0:F2} s after it, handed over {toHandover / 1000.0:F2} s after it, back {back / 1000.0:F2} s after the throttle");
            Assert.InRange(toMark, 2_500, 4_000);
            Assert.InRange(toHandover, 29_000, 31_000);
            Assert.InRange(back, 0, 5_000);
        });

        // (c) Odd: found out of step at the first checksum tick after its world was spoiled, against a value more than half of
        // the ten reported; rebuilt once, back within 15 s of the rebuild, and every report of its new world the others'.
        Hold("(c)", () =>
        {
            var spoiled = Assert.Single(odd.Corruptions);
            var found = relay.Lines.Select(l => OutOfStep.Match(l.Line)).Where(m => m.Success && m.Groups["name"].Value == "Odd").ToList();
            Assert.NotEmpty(found);
            var at = long.Parse(found[0].Groups["tick"].Value, CultureInfo.InvariantCulture);
            Assert.Equal(spoiled.Tick - spoiled.Tick % 20 + 20, at);
            var agreedValue = found[0].Groups["agreed"].Value;
            Assert.NotEqual("", agreedValue);
            var agreeing = others.Concat([slow]).Count(c => c.History.Any(h => h.Tick == at && h.Value.ToString("X16", CultureInfo.InvariantCulture) == agreedValue));
            Assert.True(agreeing * 2 > 10, $"{agreeing} of the ten reported the value Odd was set against at tick {at}: {found[0].Value}");
            Assert.Equal(1, odd.Resyncs);
            Assert.Single(relay.Lines, l => l.Line.Contains($"resync 1/3 of Odd (player {odd.Me})", StringComparison.Ordinal));
            Assert.DoesNotContain(relay.Lines, l => l.Line.Contains("resync 2/3", StringComparison.Ordinal));
            var rebuiltAt = odd.RebuiltAt.Single();
            var oddBack = HeardAt<PlayerReturned>(odd.Me, rebuiltAt);
            Assert.True(oddBack > 0, "Odd never came back after its rebuild");
            Say($"(c) Odd spoiled after tick {spoiled.Tick}, found out of step at tick {at} ({found[0].Groups["values"].Value}, {agreeing} of 10 reported the agreed value); " +
                $"rebuilt from tick {odd.Resumed!.FromTick} ({(rebuiltAt - Due(at)) / 1000.0:F2} s after the tick was due), back {(oddBack - rebuiltAt) / 1000.0:F2} s after the rebuild came");
            Assert.InRange(oddBack - rebuiltAt, 0, 15_000);
            var consensus = Consensus(others);
            var later = odd.History.Where(h => h.World == 1).ToList();
            Assert.True(later.Count(h => h.Tick > at) >= 20, $"Odd's rebuilt world reported only {later.Count(h => h.Tick > at)} checksums after tick {at}");
            Assert.All(later, h => Assert.True(!consensus.TryGetValue(h.Tick, out var value) || value == h.Value,
                $"Odd's rebuilt world reported {h.Value:X16} at tick {h.Tick}, the others {consensus.GetValueOrDefault(h.Tick):X16}"));
            Assert.All(odd.History.Where(h => h.World == 0 && h.Tick <= spoiled.Tick), h => Assert.Equal(consensus[h.Tick], h.Value));
        });

        AssertAgreedAndPlayedBack(run, outOfStep: ["Odd"]);
        AllHeld();
    }

    [Acceptance]
    public async Task Twenty_people_keep_the_beat_ten_against_ten()
    {
        var run = await Play(teamSize: 10, people: 20, ticks: 3000);
        foreach (var client in run.Clients)
            Hold($"(a) {client.Name}", () => Say($"(a) {client.Name}: {RelayClients.AssertAtSpeed(client, AtSpeedFrom, run.End)}"));
        var marked = run.Clients.SelectMany(c => c.Heard).OfType<PlayerLagging>().Where(l => l.Lagging).Select(l => l.Player).Distinct().ToList();
        Hold("(a) never marked", () => Assert.Empty(marked));
        AssertAgreedAndPlayedBack(run, outOfStep: []);
        AllHeld();
    }

    // The criteria that did not hold. Each is checked whatever came before it, so that one failing run says all that failed
    // (a run of the ten against ten that failed (e) never got to (f) before).
    private readonly List<string> _failed = [];

    /// <summary>Checks one criterion of the acceptance: what does not hold is said and noted, and the next is checked all the same.</summary>
    private void Hold(string criterion, Action check)
    {
        try
        {
            check();
        }
        catch (Exception failure)
        {
            _failed.Add($"{criterion}: {failure.Message}");
            Say($"{criterion} DOES NOT HOLD: {failure.Message.ReplaceLineEndings(" ")}");
        }
    }

    /// <summary>The test fails with every criterion that did not hold.</summary>
    private void AllHeld() =>
        Assert.True(_failed.Count == 0, $"{_failed.Count} of the criteria did not hold:{Environment.NewLine}{string.Join(Environment.NewLine, _failed)}");

    /// <summary>What a run left to look at: the relay, the clients, the room, the tick the relay's clock stood at when bundle 0 was due (Unix ms), and the tick played to.</summary>
    private sealed record Run(RelayProcess Relay, List<RelayClient> Clients, int Room, long Anchor, long End);

    private static readonly Regex OutOfStep = new(
        @"room \d+: (?<name>.+?) \(player \d+\) out of step at tick (?<tick>\d+): (?<values>[0-9A-F]{16}(?: against (?:the agreed )?(?<agreed>[0-9A-F]{16}))?.*)$");

    private static readonly Regex SummaryLine = new(
        @"(?<ticks>\d+) ticks, (?<moved>\d+) turns moved, late bundles by player: (?<late>.*?); delay \(bundles\): (?<delays>.*?); (?<lateCloses>\d+) closed over 20 ms late, (?<nobody>\d+) counted nobody");

    /// <summary>
    /// A battle of <paramref name="teamSize"/> a side on m_desert at tier I with three lives, every seat a person's, played
    /// on the relay's beat to <paramref name="ticks"/> or its end, the people over TCP and WebSocket in turn; with
    /// <paramref name="trouble"/>, the one of that name throttled to 8 ticks a second for 45 s from the relay's tick 600, and
    /// the other's world spoiled after its tick 2400.
    /// </summary>
    private async Task<Run> Play(int teamSize, int people, long ticks, (string Slow, string Odd)? trouble = null)
    {
        using var timer = new FineTimer();
        var relay = await RelayProcess.Start();
        _relay = relay;
        var clients = new List<RelayClient>();
        _clients = clients;
        for (var i = 0; i < people; i++)
            clients.Add(await RelayClient.Connect(i % 2 == 0 ? $"127.0.0.1:{relay.Port}" : $"ws://127.0.0.1:{relay.WebPort}", Names[i], RelayClients.Data));
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 1, Lives = 3, TeamSize = teamSize }, FogOfWar = false };
        var room = await RelayClients.Open(clients[0], new CreateRoom("Acceptance", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false),
            [.. clients.Skip(1)]);
        Assert.Equal(people, room.Seats.Count);
        foreach (var client in clients)
            client.HoldLoad = true;
        var match = await RelayClients.Begin(clients[0], clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 1, line: c.Id % 2));
        Assert.Equal(teamSize, match.Options.Battle!.TeamSize);
        Assert.DoesNotContain(match.Players, p => p.Ai);
        foreach (var client in clients)
        {
            client.Script = new BattleBotScript(client.Me);
            client.SaysDecided = true;
            if (trouble is { } named && client.Name == named.Slow)
                client.Throttle(600, TimeSpan.FromSeconds(45), 8);
            if (trouble is { } spoilt && client.Name == spoilt.Odd)
                client.Corrupt(2400);
        }
        // The first person's world says where its slow ticks went.
        clients[0].World.Profile = [];
        var clock = relay.Clock(room.Id);
        RelayClients.Run(clients, clock);
        // Their worlds are built, and so is every world rebuilt later.
        foreach (var client in clients)
        {
            client.HoldLoad = false;
            client.Load();
        }
        var anchor = await relay.Anchor(room.Id, TimeSpan.FromSeconds(60));
        Say($"{people} people, {teamSize} a side on m_desert, tier I, 3 lives; {relay.Lines.FirstOrDefault(l => l.Line.Contains("the beat starts", StringComparison.Ordinal)).Line}");

        // To the tick asked for, or to where the beat stopped once the match was decided.
        var watch = Stopwatch.StartNew();
        var patience = TimeSpan.FromSeconds(ticks / 20.0 + 180);
        long Target() => relay.Lines.Select(l => BeatStops.Match(l.Line)).FirstOrDefault(m => m.Success) is { } stop
            ? Math.Min(ticks, long.Parse(stop.Groups[1].Value, CultureInfo.InvariantCulture))
            : ticks;
        // The most the garbage collector held every thread within a tenth of a second, and when.
        var (paused, mostPaused) = (GC.GetTotalPauseDuration(), (Length: TimeSpan.Zero, At: (long?)null));
        while (clients.Where(c => c.InMatch).Any(c => c.Tick < Target()))
        {
            if (watch.Elapsed > patience)
                Assert.Fail($"the battle did not reach tick {Target()} in {patience.TotalSeconds:0} s: " +
                            string.Join("; ", clients.Select(c => $"{c.Name} at {c.Tick}, bundles to {c.Session.NewestBundle}")) + Tail(relay));
            Assert.All(clients, c => Assert.True(c.Failure is null, $"{c.Name}'s thread failed: {c.Failure}"));
            await Task.Delay(100);
            var pausedNow = GC.GetTotalPauseDuration();
            if (pausedNow - paused > mostPaused.Length)
                mostPaused = (pausedNow - paused, clock());
            paused = pausedNow;
        }
        var end = Target();
        RelayClients.Halt(clients);
        // Before the pace is measured: the worst any world stood behind the relay's clock, and the garbage collector's pauses.
        var early = clients.SelectMany(c => c.Samples.Where(s => s.Clock < AtSpeedFrom).Select(s => (c.Name, s))).MaxBy(x => x.s.Clock - x.s.Tick);
        var gap = clients.SelectMany(c => c.Samples.Zip(c.Samples.Skip(1), (a, b) => (c.Name, Ms: b.Ms - a.Ms, b.Clock))).MaxBy(g => g.Ms);
        Say($"the longest a client's thread went between two looks at the clock: {gap.Ms:F0} ms ({gap.Name}, up to clock tick {gap.Clock}); " +
            $"the slowest tick {clients.Max(c => c.SlowestTick.Ms):F0} ms ({clients.MaxBy(c => c.SlowestTick.Ms)!.Name} at tick {clients.MaxBy(c => c.SlowestTick.Ms)!.SlowestTick.Tick}), " +
            $"the slowest look of a bot {clients.Max(c => c.SlowestThink.Ms):F0} ms ({clients.MaxBy(c => c.SlowestThink.Ms)!.Name} at tick {clients.MaxBy(c => c.SlowestThink.Ms)!.SlowestThink.Tick})");
        foreach (var slow in clients[0].SlowTicks)
            Say($"{clients[0].Name}'s slow {slow}");
        if (Environment.GetEnvironmentVariable("MACHINES_P2_ACCEPT_REPORT") is { Length: > 0 } kept)
            new Replay(clients[0].Match, clients[0].Session.Played, clients[0].World.Tick, clients[0].Session.Presence).Save($"{kept}.{people}.mreplay");
        var gc = GC.GetGCMemoryInfo(GCKind.Any);
        Say($"before tick {AtSpeedFrom}: most behind {early.s.Clock - early.s.Tick} ({early.Name} at clock tick {early.s.Clock}); garbage collections 0/1/2: " +
            $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms in all, " +
            $"the last one {string.Join("+", gc.PauseDurations.ToArray().Where(p => p > TimeSpan.Zero).Select(p => $"{p.TotalMilliseconds:F0} ms"))} (gen {gc.Generation}, {gc.HeapSizeBytes >> 20} MiB heap), " +
            $"at most {mostPaused.Length.TotalMilliseconds:F0} ms in a tenth of a second (at clock tick {mostPaused.At})");
        var seconds = (RelayClient.Utc - anchor) / 1000.0;
        // What the relay sent the people: its bundles and log parts, as the frames carry them.
        var bytes = clients.Sum(c => c.Received);
        Say($"played to tick {end} in {seconds:F0} s; the relay sent {bytes / 1024.0 / seconds:F1} KiB/s to the room, {bytes / 1024.0 / seconds / people:F2} KiB/s a person (bundle and log JSON)");
        foreach (var client in clients)
            Assert.True(client.Failure is null, $"{client.Name}'s thread failed: {client.Failure}");

        // Everyone leaves; the relay drops the room and says its summary.
        await Task.WhenAll(clients.Select(c => c.Leave()));
        var summary = await relay.Wait(SummaryLine, TimeSpan.FromSeconds(30));
        Say($"relay summary: {summary.Value}");
        // (The relay's arrow in "delay 4 → 3" comes through a Windows console's code page as some other character.)
        foreach (var line in relay.Lines.Where(l => Regex.IsMatch(l.Line, @"delay \d+ \S+ \d+ at tick|marked at|handed over|back at|resync|out of step|fell behind|joining|rebuilding")))
            Say($"relay: {line.Line}");
        return new Run(relay, clients, room.Id, anchor, end);
    }

    /// <summary>
    /// (d), (e) and (f): no tick was settled without a majority and nobody but <paramref name="outOfStep"/> was found out of
    /// step; every world agrees at every tick reported and at the last tick all reported; the delay stayed at 3–4 and at least
    /// 98 % of bundles closed no later than 20 ms after their due time; every recording plays back in turn mode.
    /// </summary>
    private void AssertAgreedAndPlayedBack(Run run, string[] outOfStep)
    {
        var (relay, clients) = (run.Relay, run.Clients);
        Hold("(d)", () =>
        {
            Assert.DoesNotContain(relay.Lines, l => l.Line.Contains("no value agreed by more than half", StringComparison.Ordinal));
            var found = relay.Lines.Select(l => OutOfStep.Match(l.Line)).Where(m => m.Success).Select(m => m.Groups["name"].Value).Distinct().ToList();
            Assert.All(found, name => Assert.Contains(name, outOfStep));
            var compared = RelayClients.AssertSumsAgree(clients, resyncs: outOfStep.Length > 0);
            var shared = clients.Select(c => c.Sums.Keys.Max()).Min();
            Assert.Single(clients.Select(c => c.Sums[shared]).Distinct());
            Say($"(d) {compared} checksum ticks agree; at tick {shared}, the last all reported, every world has {clients[0].Sums[shared]:X16}");
        });

        Hold("(e)", () =>
        {
            var summary = SummaryLine.Match(relay.Lines.Last(l => SummaryLine.IsMatch(l.Line)).Line);
            var delays = summary.Groups["delays"].Value.Split(", ").Select(d => d.Split(": ")).ToDictionary(d => int.Parse(d[0], CultureInfo.InvariantCulture), d => long.Parse(d[1], CultureInfo.InvariantCulture));
            var bundles = long.Parse(summary.Groups["ticks"].Value, CultureInfo.InvariantCulture);
            var lateCloses = long.Parse(summary.Groups["lateCloses"].Value, CultureInfo.InvariantCulture);
            Say($"(e) delays {summary.Groups["delays"].Value} over {bundles} bundles; {lateCloses} closed over 20 ms late ({lateCloses / (double)bundles:P2}); {summary.Groups["moved"].Value} turns moved; {summary.Groups["nobody"].Value} counted nobody");
            Assert.All(delays.Keys, delay => Assert.InRange(delay, 3, 4));
            Assert.True(lateCloses <= bundles * 0.02, $"{lateCloses} of {bundles} bundles closed more than 20 ms after their due time");
        });

        Hold("(f)", () =>
        {
            foreach (var client in clients)
                RelayClients.AssertPlaysBack(client);
            Say($"(f) all {clients.Count} recordings play back in turn mode to their own sums");
        });
    }

    /// <summary>
    /// The first bundle from <paramref name="from"/> on that the client was late for: by its due time no turn of the client's
    /// asking for that bundle or a later one had gone out (on a loopback line it is in a fraction of a millisecond).
    /// </summary>
    private static long FirstLate(List<(long Stamp, long Utc)> stamps, long anchor, long from)
    {
        var newest = -1L;
        var i = 0;
        for (var tick = from; ; tick++)
        {
            var due = anchor + tick * 50;
            while (i < stamps.Count && stamps[i].Utc <= due)
                newest = Math.Max(newest, stamps[i++].Stamp);
            if (newest < tick)
                return tick;
            if (i >= stamps.Count && tick > from + 100_000)
                throw new InvalidOperationException("never late");
        }
    }

    /// <summary>The value most of the clients reported at each tick.</summary>
    private static Dictionary<long, ulong> Consensus(IEnumerable<RelayClient> clients) =>
        clients.SelectMany(c => c.Sums).GroupBy(s => s.Key)
            .ToDictionary(g => g.Key, g => g.GroupBy(s => s.Value).OrderByDescending(v => v.Count()).First().Key);

    private static PlayerId Who(NetMessage change) => change switch
    {
        PlayerLeft left => left.Player,
        PlayerReturned back => back.Player,
        PlayerLagging lagging => lagging.Player,
        PlayerJoined joined => joined.Player,
        _ => throw new ArgumentException($"{change} is no coming or going"),
    };

    private static readonly Regex BeatStops = new(@"the beat stops at tick (\d+)");

    // What a test started, to be stopped whatever happens.
    private RelayProcess? _relay;
    private List<RelayClient>? _clients;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var client in _clients ?? [])
            await client.DisposeAsync();
        if (_relay is not null)
        {
            await _relay.DisposeAsync();
            // With a report: the relay's whole log beside it, one file a run.
            if (Environment.GetEnvironmentVariable("MACHINES_P2_ACCEPT_REPORT") is { Length: > 0 } report)
                File.WriteAllLines($"{report}.relay-{DateTime.Now:HHmmss}.log",
                    _relay.Lines.Select(l => $"{DateTimeOffset.FromUnixTimeMilliseconds(l.Utc).ToLocalTime():HH:mm:ss.fff} {l.Line}"));
        }
    }

    private void Say(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("MACHINES_P2_ACCEPT_REPORT") is { Length: > 0 } report)
            File.AppendAllText(report, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }

    private static string Tail(RelayProcess relay) =>
        $"; the relay's last lines:{Environment.NewLine}{string.Join(Environment.NewLine, relay.Lines.TakeLast(30).Select(l => l.Line))}";

    /// <summary>
    /// The relay as its own process (the built Machines.Relay.dll beside the tests, <c>--port 0 --http-port 0</c>, its own
    /// timings), so that it shares neither the garbage collector nor the thread pool with the clients: a stall of both would
    /// hide every stall of one against the other. Its standard output is read for the ports, the anchors and the events.
    /// </summary>
    internal sealed class RelayProcess : IAsyncDisposable
    {
        private readonly Process _process;

        private RelayProcess(Process process) => _process = process;

        /// <summary>Every line the relay wrote, with when it came (Unix ms, UTC).</summary>
        public ConcurrentQueue<(long Utc, string Line)> Lines { get; } = new();

        public int Port { get; private set; }
        public int WebPort { get; private set; }

        public static async Task<RelayProcess> Start()
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Machines.Relay.dll"), "--port", "0", "--http-port", "0" })
                start.ArgumentList.Add(argument);
            start.Environment.Remove("PORT");
            var relay = new RelayProcess(Process.Start(start) ?? throw new InvalidOperationException("the relay did not start"));
            relay._process.OutputDataReceived += (_, e) => relay.Take(e.Data);
            relay._process.ErrorDataReceived += (_, e) => relay.Take(e.Data);
            relay._process.BeginOutputReadLine();
            relay._process.BeginErrorReadLine();
            var listening = await relay.Wait(new Regex(@"listening on TCP port (\d+), HTTP port (\d+)"), TimeSpan.FromSeconds(30));
            relay.Port = int.Parse(listening.Groups[1].Value, CultureInfo.InvariantCulture);
            relay.WebPort = int.Parse(listening.Groups[2].Value, CultureInfo.InvariantCulture);
            return relay;
        }

        private void Take(string? line)
        {
            if (line is not null)
                Lines.Enqueue((RelayClient.Utc, line));
        }

        /// <summary>The first line the pattern matches, once there is one.</summary>
        public async Task<Match> Wait(Regex pattern, TimeSpan within)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (Lines.Select(l => pattern.Match(l.Line)).FirstOrDefault(m => m.Success) is { } found)
                    return found;
                if (watch.Elapsed > within || _process.HasExited)
                    Assert.Fail($"no relay line like {pattern} within {within.TotalSeconds:0} s{(_process.HasExited ? " (the relay ended)" : "")}{Tail(this)}");
                await Task.Delay(20);
            }
        }

        /// <summary>When bundle 0 of the room's match was due (Unix ms, UTC), from the relay's line, once the beat started.</summary>
        public async Task<long> Anchor(int room, TimeSpan within) =>
            long.Parse((await Wait(new Regex($@"room {room}: bundle 0 was due at (\d+) ms UTC"), within)).Groups[1].Value, CultureInfo.InvariantCulture);

        /// <summary>The relay's clock tick of the room's match, <c>(now − anchor) / 50 ms</c>; none before the beat starts.</summary>
        public Func<long?> Clock(int room)
        {
            var anchor = -1L;
            var pattern = new Regex($@"room {room}: bundle 0 was due at (\d+) ms UTC");
            return () =>
            {
                if (Interlocked.Read(ref anchor) < 0 && Lines.Select(l => pattern.Match(l.Line)).FirstOrDefault(m => m.Success) is { } found)
                    Interlocked.Exchange(ref anchor, long.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture));
                return Interlocked.Read(ref anchor) is var due and >= 0 ? (RelayClient.Utc - due) / 50 : null;
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            _process.Dispose();
        }
    }
}
