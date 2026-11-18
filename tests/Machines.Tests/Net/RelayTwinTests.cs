using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// T4 and T5 of the P2 design (§8.2): a relay on time plays as today's lockstep, and the recordings of bundle matches play back
/// in turn mode. Clients play through the real relay on <see cref="BeatTimings.Scripted"/> (the delay pinned at 4, a bundle
/// closing once every turn for it is in), each person's turn made by a script from their world as it stands at its tick, and a
/// session in turn mode over the same match, fed by twins of the same scripts as <see cref="GoldenMatch"/> feeds its own, has the
/// clients' checksum every 20 ticks. Players who quit and come back, by key or by name, from a sealed log or from its tail,
/// rebuild worlds that agree with every checksum the relay agreed on, and every recording plays back.
/// </summary>
public class RelayTwinTests
{
    /// <summary>
    /// Scripted, with a minute between due times and ten for the load: no bundle comes due while a test runs, so none closes
    /// before every turn for it is in, and no turn is moved.
    /// </summary>
    private static readonly BeatTimings Twin = BeatTimings.Scripted with { Interval = TimeSpan.FromMinutes(1), LoadTimeout = TimeSpan.FromMinutes(10) };

    private static PlayerId P(int value) => new(value);

    [Fact]
    public async Task Four_commanders_two_a_race_through_a_relay_on_time_have_every_checksum_of_todays_lockstep()
    {
        await using var relay = await RelayClients.Start(Twin);
        var (room, clients, match) = await Commanders(relay);
        RelayScript Script(PlayerId player, Race race) => new CommanderScript(player, 600 + player.Value, gives: race == Race.Blue);
        foreach (var client in clients)
            client.Script = Script(client.Me, client.Race);

        await RelayClients.PlayTo(1000, clients);

        Assert.Equal(0, relay.Relay.WithBeat(room.Id, b => b.Moved));
        Assert.Equal(1000 / 20, AssertTwin(match, clients, Script, 1000));
        Assert.True(clients[0].Session.Played.Count(t => t.Commands.Count > 0) > 50);
    }

    [Fact]
    public async Task Ten_heroes_five_against_five_through_a_relay_on_time_have_every_checksum_of_todays_lockstep()
    {
        await using var relay = await RelayClients.Start(Twin);
        string[] names = ["Anna", "Ben", "Cleo", "Dan", "Eve", "Finn", "Gus", "Hal", "Ivy", "Jo"];
        var clients = new List<RelayClient>();
        for (var i = 0; i < names.Length; i++)
            clients.Add(await relay.Connect(names[i], web: i % 2 == 1));
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 3, Lives = 3, TeamSize = 5 }, FogOfWar = false };
        var room = await RelayClients.Open(clients[0],
            new CreateRoom("Twin", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green, Race.Yellow], options, FillWithAi: false), [.. clients.Skip(1)]);
        var match = await RelayClients.Begin(clients[0], clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 3, line: clients.IndexOf(c) % 3));
        RelayScript Script(PlayerId player, Race race) => new HeroScript(player, 700 + player.Value);
        foreach (var client in clients)
            client.Script = Script(client.Me, client.Race);
        Assert.Equal(10, clients[0].Session.Humans.Count);

        await RelayClients.PlayTo(800, clients);

        Assert.Equal(0, relay.Relay.WithBeat(room.Id, b => b.Moved));
        Assert.Equal(800 / 20, AssertTwin(match, clients, Script, 800));
        Assert.Contains(clients[0].Session.Played, t => t.Commands.Any(c => c is DriveCommand));
    }

    [Fact]
    public async Task Recordings_of_a_match_left_and_rejoined_play_back_and_worlds_rebuilt_from_a_sealed_log_or_its_tail_agree_with_the_relay()
    {
        await using var relay = await RelayClients.Start(BeatTimings.Scripted with { SegmentTicks = 100 });
        var (room, clients, _) = await Commanders(relay);
        var (anna, ben, cleo, dan) = (clients[0], clients[1], clients[2], clients[3]);
        foreach (var client in clients)
            client.Script = new CommanderScript(client.Me, 800 + client.Me.Value, gives: client.Race == Race.Blue);
        await RelayClients.PlayTo(150, clients);

        // Dan quits, and comes back with his key once his leave lies in a sealed segment of the log.
        await dan.Leave();
        await RelayClients.PlayTo(250, clients);
        await RelayClients.Until(() => relay.Relay.LogShape(room.Id).Segments.Count == 2, TimeSpan.FromSeconds(10), "two segments sealed");
        var danLeft = anna.Session.Presence.OfType<PlayerLeft>().Single(l => l.Player == dan.Me).FromTick;
        Assert.True(danLeft < relay.Relay.LogShape(room.Id).TailFrom);
        var dan2 = await relay.RejoinWithKey(dan, web: false);
        dan2.Script = new CommanderScript(dan2.Me, 900, gives: true);
        clients.Add(dan2);
        Assert.Equal((dan.Me, dan.Key, 2), (dan2.Me, dan2.Key, dan2.Resumed!.Parts));
        await RelayClients.PlayTo(430, clients);

        // Cleo quits, and comes back by name while her leave is in the tail.
        await cleo.Leave();
        await RelayClients.PlayTo(460, clients);
        var cleo2 = await relay.Rejoin("Cleo", web: true, room.Id);
        cleo2.Script = new CommanderScript(cleo2.Me, 901);
        clients.Add(cleo2);
        var cleoLeft = anna.Session.Presence.OfType<PlayerLeft>().Single(l => l.Player == cleo.Me).FromTick;
        Assert.True(cleoLeft >= relay.Relay.LogShape(room.Id).TailFrom, $"Cleo left at {cleoLeft}");
        await RelayClients.PlayTo(700, clients);
        await RelayClients.Until(() => relay.Relay.Agreed(room.Id, 700) is not null, TimeSpan.FromSeconds(10), "tick 700 agreed");

        // Both left, and both came back once their worlds were on time.
        Assert.Equal([(dan.Me, "left"), (dan.Me, "back"), (cleo.Me, "left"), (cleo.Me, "back")],
            anna.Session.Presence.Select(m => m switch
            {
                PlayerLeft left => (left.Player, "left"),
                PlayerReturned back => (back.Player, "back"),
                _ => throw new InvalidOperationException($"{m} in the log"),
            }));
        Assert.All(clients.Where(c => !c.Gone), c => Assert.Equal(anna.Session.Presence, c.Session.Presence));

        // The rebuilt worlds have every checksum the relay agreed on, from tick 20 on.
        foreach (var rebuilt in new[] { dan2, cleo2 })
        {
            Assert.Equal(Enumerable.Range(1, 700 / 20).Select(i => i * 20L), rebuilt.Sums.Keys);
            foreach (var (tick, sum) in rebuilt.Sums)
                Assert.Equal(relay.Relay.Agreed(room.Id, tick), sum);
        }
        Assert.True(RelayClients.AssertAgree(clients) >= 700 / 20);
        foreach (var client in new[] { anna, ben, dan2, cleo2 })
            RelayClients.AssertPlaysBack(client);
    }

    /// <summary>Anna and Cleo command Red, Ben and Dan Blue (players 0, 4, 1 and 5), on m_desert; begun, every world built.</summary>
    private static async Task<(RoomState Room, List<RelayClient> Clients, MatchInfo Match)> Commanders(RelayClients relay)
    {
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var cleo = await relay.Connect("Cleo", web: false);
        var dan = await relay.Connect("Dan", web: true);
        var room = await RelayClients.Open(anna,
            new CreateRoom("Twin", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2), ben, cleo, dan);
        List<RelayClient> clients = [anna, ben, cleo, dan];
        var match = await RelayClients.Begin(anna, clients);
        Assert.Equal([P(0), P(1), P(4), P(5)], [anna.Me, ben.Me, cleo.Me, dan.Me]);
        return (room, clients, match);
    }

    /// <summary>
    /// Today's lockstep over the same match: a session in turn mode with no local player, every human's turn for the tick
    /// <see cref="MatchInfo.InputDelay"/> ahead made at each tick by a twin of their client's script, in player order, through
    /// the wire, as <see cref="GoldenMatch"/> makes them; at every <see cref="LockstepSession.ChecksumInterval"/> its checksum
    /// is the one each client reported. Returns how many ticks were compared.
    /// </summary>
    private static int AssertTwin(MatchInfo match, IReadOnlyList<RelayClient> clients, Func<PlayerId, Race, RelayScript> script, long to)
    {
        var session = new LockstepSession(Replay.BuildWorld(RelayClients.Data, match), local: null, match.InputDelay);
        Assert.Equal(SessionMode.Turns, session.Mode);
        var humans = session.Humans.ToList();
        var scripts = humans.ToDictionary(p => p, p => script(p, session.World.Players.RaceOf(p)));
        var compared = 0;
        while (session.World.Tick < to)
        {
            var due = session.World.Tick + match.InputDelay;
            foreach (var player in humans)
                session.Receive(NetJson.Decode(NetJson.Encode(new Turn(due, player, scripts[player].Next(session.World)))));
            Assert.True(session.TryStep(), $"today's lockstep stuck at tick {session.World.Tick}");
            var tick = session.World.Tick;
            if (tick % session.ChecksumInterval != 0)
                continue;
            var sum = session.World.Checksum();
            foreach (var client in clients)
            {
                Assert.True(client.Sums.TryGetValue(tick, out var theirs) && theirs == sum,
                    $"at tick {tick} {client.Name} reported {(client.Sums.TryGetValue(tick, out var reported) ? reported.ToString("X16") : "nothing")}, today's lockstep has {sum:X16}");
            }
            compared++;
        }
        return compared;
    }
}
