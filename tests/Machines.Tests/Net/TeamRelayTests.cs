using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// Teams of people through the real relay, TCP and WebSocket clients in the same room (P1 design T8, slice S4b): ten people
/// fight five against five, and four command a skirmish two to a race. Seats fill every race's first before any race's
/// second; every checksum agrees; a leaver's machines are kept by the teammate, their hero driven by a bot, the race played
/// by the computer once its last person is gone; who comes back by name plays on as before; and every recording plays back
/// to the same sums.
/// </summary>
public class TeamRelayTests
{
    private static PlayerId P(int value) => new(value);

    [Fact]
    public async Task Ten_people_fight_five_against_five_leave_come_back_by_name_and_agree_on_every_checksum()
    {
        await using var relay = await RelayClients.Start();
        // Two of them are called Kim; every second client talks over a WebSocket, the others over TCP.
        string[] names = ["Anna", "Ben", "Cleo", "Dan", "Kim", "Finn", "Gus", "Kim", "Ivy", "Jo"];
        var clients = new List<RelayClient>();
        for (var i = 0; i < names.Length; i++)
            clients.Add(await relay.Connect(names[i], web: i % 2 == 1));
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 3, Lives = 3, TeamSize = 5 }, FogOfWar = false };
        var room = await RelayClients.Open(clients[0],
            new CreateRoom("Ten", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green, Race.Yellow], options, FillWithAi: false), [.. clients.Skip(1)]);

        // The map's two anchors, five seats each, filled a team's first seat before either team's second, and so on.
        Assert.Equal([Race.Red, Race.Green], room.Slots);
        Assert.Equal([.. Enumerable.Repeat(Race.Red, 5), .. Enumerable.Repeat(Race.Green, 5)], room.Seats.Select(s => s.Race));
        Assert.Equal([0, 2, 4, 6, 8, 1, 3, 5, 7, 9], room.Seats.Select(s => clients.FindIndex(c => c.Id == s.ClientId)));
        Assert.Equal(["Anna", "Ben", "Cleo", "Dan", "Kim", "Finn", "Gus", "Kim 2", "Ivy", "Jo"], clients.Select(c => room.Players.Single(p => p.ClientId == c.Id).Name));
        Assert.Single(room.Players.Where(p => clients.FindIndex(c => c.Id == p.ClientId) % 2 == 0).Select(p => p.Colour).Distinct());
        Assert.Single(room.Players.Where(p => clients.FindIndex(c => c.Id == p.ClientId) % 2 == 1).Select(p => p.Colour).Distinct());

        var match = await RelayClients.Begin(clients[0], clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 3, line: clients.IndexOf(c) % 3));
        // Red's seats are players 0, 4 to 7, Green's 2, 8 to 11, in seat order; everyone a person.
        Assert.Equal([0, 4, 5, 6, 7, 2, 8, 9, 10, 11], room.Seats.Select(s => clients.Single(c => c.Id == s.ClientId).Me.Value));
        Assert.All(match.Players, p => Assert.False(p.Ai));
        Assert.Equal(10, match.Options.Battle!.Heroes.Count);
        foreach (var client in clients)
        {
            Assert.Equal(client.Race, client.World.Players.RaceOf(client.Me));
            Assert.Equal(client.Me, client.World.HeroOf(client.Me)!.Owner);
            Assert.Empty(client.World.HeroBots);
            Assert.Equal(250, client.World.Battle!.CaptureTarget);
            client.Script = new HeroScript(client.Me, 100 + clients.IndexOf(client));
        }

        await RelayClients.PlayTo(400, clients);

        // Kim at Red's third seat (over TCP) and Ben at Green's first (over a WebSocket) quit: a bot drives each one's hero.
        var (kim, ben) = (clients[4], clients[1]);
        Assert.Equal((P(5), P(2)), (kim.Me, ben.Me));
        await kim.Leave();
        await ben.Leave();
        await RelayClients.PlayTo(700, clients);
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.Equal([P(2), P(5)], client.World.Players.AwayPlayers);
            Assert.Equal([P(2), P(5)], client.World.HeroBots.Keys);
            Assert.Equal([P(2), P(5)], client.World.Battle!.Bots);
            Assert.Equal(Controller.Player, client.World.Races[Race.Red].Controller);
            Assert.Equal(Controller.Player, client.World.Races[Race.Green].Controller);
        }

        // Kim comes back, over a WebSocket now, by name alone: the Kim who left, not the one still playing.
        var back = await relay.Rejoin("Kim", web: true, room.Id);
        Assert.Equal(P(5), back.Me);
        Assert.Equal(Race.Red, back.Race);
        back.Script = new HeroScript(back.Me, 200);
        clients.Add(back);
        await RelayClients.PlayTo(1300, clients);
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.Equal([P(2)], client.World.Players.AwayPlayers);
            Assert.Equal([P(2)], client.World.HeroBots.Keys);
            Assert.Equal([P(2)], client.World.Battle!.Bots);
        }
        // She is back from a tick the relay picked once her rebuilt world was on time, after she asked; her hero is hers
        // again: the turns she sent since carry her orders.
        var returned = clients[0].Session.Presence.OfType<PlayerReturned>().Single(r => r.Player == P(5)).FromTick;
        Assert.True(returned > 700, $"back from tick {returned}");
        Assert.Contains(clients[0].Session.Played, t => t.Player == P(5) && t.Tick >= returned && t.Commands.Count > 0);

        Assert.True(RelayClients.AssertAgree(clients) >= 1300 / 20);
        Assert.Contains(clients[0].Session.Presence, m => m is PlayerReturned { Player.Value: 5 });
        foreach (var client in new[] { clients[0], back })
        {
            var played = RelayClients.AssertPlaysBack(client);
            Assert.Equal(client.World.Battle!.Fingerprint(), played.Battle!.Fingerprint());
            Assert.Equal([P(2)], played.HeroBots.Keys);
        }
    }

    [Fact]
    public async Task Four_commanders_two_a_race_keep_each_others_machines_hand_over_to_the_computer_come_back_and_agree()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var cleo = await relay.Connect("Cleo", web: false);
        var dan = await relay.Connect("Dan", web: true);
        var room = await RelayClients.Open(anna,
            new CreateRoom("Together", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2), ben, cleo, dan);

        // Every race once before any race twice: Anna and Cleo command Red, Ben and Dan Blue.
        Assert.Equal([Race.Red, Race.Red, Race.Blue, Race.Blue], room.Seats.Select(s => s.Race));
        Assert.Equal([anna.Id, cleo.Id, ben.Id, dan.Id], room.Seats.Select(s => s.ClientId!.Value));
        List<RelayClient> clients = [anna, ben, cleo, dan];
        var match = await RelayClients.Begin(anna, clients);
        Assert.Equal([(anna.Id, 0), (cleo.Id, 4), (ben.Id, 1), (dan.Id, 5)], match.Players.Select(p => (p.ClientId, p.Player!.Value.Value)));
        foreach (var client in clients)
        {
            Assert.Equal([P(0), P(1), P(4), P(5)], client.Session.Humans);
            Assert.True(client.World.Players.Shared(Race.Red) && client.World.Players.Shared(Race.Blue));
            // Blue's commanders give each other machines now and then; Red's keep theirs, whose owners the test follows.
            client.Script = new CommanderScript(client.Me, 300 + client.Me.Value, gives: client.Race == Race.Blue);
        }
        // Each race's starting machines are dealt between its two commanders.
        foreach (var (race, first, second) in new[] { (Race.Red, P(0), P(4)), (Race.Blue, P(1), P(5)) })
        {
            var machines = anna.World.Machines.Where(m => m.Race == race).ToList();
            Assert.Contains(machines, m => m.Owner == first);
            Assert.Contains(machines, m => m.Owner == second);
        }

        await RelayClients.PlayTo(300, clients);

        // Dan gives Ben a machine of his.
        var gift = dan.World.Machines.First(m => m.Owner == P(5) && !m.IsDead && m.Inside is null).Id;
        dan.Session.Queue(new GiveCommand(Race.Blue, [gift], To: P(1)));
        await RelayClients.PlayTo(320, clients);
        Assert.All(clients, c => Assert.Equal(P(1), c.World.Find<Machine>(gift)!.Owner));

        // Cleo quits: Anna keeps her machines, and Red stays the people's.
        await cleo.Leave();
        await RelayClients.PlayTo(500, clients);
        var kept = Follower(anna.World, P(4));
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.True(client.World.Players.Away(P(4)));
            Assert.Equal(P(0), client.World.Players.KeeperOf(P(4)));
            Assert.Equal(Controller.Player, client.World.Races[Race.Red].Controller);
            Assert.DoesNotContain(client.World.AiControllers, ai => ai.Race == Race.Red);
            Assert.True(client.World.MayOrder(P(0), client.World.Find<Machine>(kept)!));
        }
        // An order of Anna's for one of them is carried out in every world.
        var (leader, orders) = await Follow(anna, kept, clients);
        Assert.All(orders, order => Assert.Equal(new FollowOrder(leader), order));

        // Cleo comes back by name alone and commands her machines again; Anna's orders for them are passed over.
        var cleoBack = await relay.Rejoin("Cleo", web: false, room.Id);
        Assert.Equal(P(4), cleoBack.Me);
        cleoBack.Script = new CommanderScript(cleoBack.Me, 404, gives: false);
        clients.Add(cleoBack);
        await RelayClients.PlayTo(700, clients);
        // Another of hers than the one Anna had follow her machine: nobody else gives follow orders.
        var hers = Follower(anna.World, P(4), except: kept);
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.False(client.World.Players.Away(P(4)));
            Assert.Null(client.World.Players.KeeperOf(P(4)));
            Assert.False(client.World.MayOrder(P(0), client.World.Find<Machine>(hers)!));
            Assert.True(client.World.MayOrder(P(4), client.World.Find<Machine>(hers)!));
        }
        (leader, orders) = await Follow(anna, hers, clients);
        Assert.All(orders, order => Assert.NotEqual(new FollowOrder(leader), order));
        (leader, orders) = await Follow(cleoBack, hers, clients);
        Assert.All(orders, order => Assert.Equal(new FollowOrder(leader), order));

        // Blue's commanders quit one after the other: Ben keeps Dan's machines, then the computer plays Blue.
        await dan.Leave();
        await RelayClients.PlayTo(850, clients);
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.Equal(P(1), client.World.Players.KeeperOf(P(5)));
            Assert.Equal(Controller.Player, client.World.Races[Race.Blue].Controller);
        }
        await ben.Leave();
        await RelayClients.PlayTo(1000, clients);
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.Null(client.World.Players.KeeperOf(P(5)));
            Assert.Equal(Controller.Ai, client.World.Races[Race.Blue].Controller);
            Assert.Contains(client.World.AiControllers, ai => ai.Race == Race.Blue);
        }

        // Ben comes back naming the player he was, as the game does: Blue is the people's again, Ben keeps Dan's machines.
        var benBack = await relay.Rejoin("Ben", web: true, room.Id, P(1));
        Assert.Equal(P(1), benBack.Me);
        benBack.Script = new CommanderScript(benBack.Me, 401, gives: false);
        clients.Add(benBack);
        await RelayClients.PlayTo(1300, clients);
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.Equal(Controller.Player, client.World.Races[Race.Blue].Controller);
            Assert.DoesNotContain(client.World.AiControllers, ai => ai.Race == Race.Blue);
            Assert.Equal(P(1), client.World.Players.KeeperOf(P(5)));
            Assert.Equal([P(5)], client.World.Players.AwayPlayers);
        }

        Assert.True(RelayClients.AssertAgree(clients) >= 1300 / 20);
        // Every world heard of the comings and goings alike, in the order of their ticks.
        Assert.Equal([(P(4), true), (P(4), false), (P(5), true), (P(1), true), (P(1), false)],
            anna.Session.Presence.Select(m => m is PlayerLeft left ? (left.Player, true) : (((PlayerReturned)m).Player, false)));
        var ticks = anna.Session.Presence.Select(m => m is PlayerLeft left ? left.FromTick : ((PlayerReturned)m).FromTick).ToList();
        Assert.Equal(ticks.Order(), ticks);
        Assert.All(clients.Where(c => !c.Gone), c => Assert.Equal(anna.Session.Presence, c.Session.Presence));
        foreach (var client in new[] { anna, cleoBack, benBack })
            RelayClients.AssertPlaysBack(client);
    }

    /// <summary>
    /// A teammate whose world goes its own way (P2 design §5.2): three of the four worlds agree, so only hers is out of step
    /// and rebuilt from the relay's log; meanwhile her machines are held (they keep their orders, nobody else's AI takes
    /// them, her teammate plays on), and once her world is on time she is back and agrees with everyone again.
    /// </summary>
    [Fact]
    public async Task A_second_commander_whose_world_goes_its_own_way_alone_is_rebuilt_and_agrees_again()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: true);
        var ben = await relay.Connect("Ben", web: false);
        var cleo = await relay.Connect("Cleo", web: true);
        var dan = await relay.Connect("Dan", web: false);
        var room = await RelayClients.Open(anna,
            new CreateRoom("Together", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2), ben, cleo, dan);
        List<RelayClient> clients = [anna, ben, cleo, dan];
        await RelayClients.Begin(anna, clients);
        Assert.Equal(P(4), cleo.Me);
        var redAi = false;
        foreach (var client in clients)
        {
            client.Script = new CommanderScript(client.Me, 700 + client.Me.Value, gives: false);
            client.AfterStep = world => redAi |= world.AiControllers.Any(ai => ai.Race == Race.Red);
        }
        await RelayClients.PlayTo(100, clients);
        Assert.Equal(5, RelayClients.AssertAgree(clients));

        // Something only Cleo's world has, a cheat or a bug, after tick 110: her teammate's world and the others' do not.
        cleo.Corrupt(110);
        await RelayClients.PlayTo(500, clients);

        // Her report of 120 was out of step with the three others': her world, and hers alone, was rebuilt from 120 on.
        var resumed = Assert.Single(cleo.Rebuilt);
        Assert.Equal((P(4), ResumeReason.Resync, 1, (string?)null), (resumed.Player, resumed.Reason, resumed.Resyncs, resumed.Key));
        Assert.True(resumed.FromTick > 120, $"rebuilt from {resumed.FromTick}");
        Assert.All(clients.Where(c => c != cleo), c => Assert.Equal(0, c.Rebuilds));
        Assert.NotEqual(cleo.History.Single(h => h is { World: 0, Tick: 120 }).Value, relay.Relay.Agreed(room.Id, 120));
        Assert.Equal(cleo.History.Single(h => h is { World: 1, Tick: 120 }).Value, relay.Relay.Agreed(room.Id, 120));
        // Her report reached the relay before the third of the others' (agreed by 3 of 4 then) or after it (checked against the
        // value agreed already), as the four connections happen to deliver them.
        var agreed = relay.Relay.Agreed(room.Id, 120)!.Value;
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: Cleo (player 4) out of step at tick 120: ", StringComparison.Ordinal)
                                          && (l.EndsWith($" against {agreed:X16}, agreed by 3 of 4 playing", StringComparison.Ordinal)
                                              || l.EndsWith($" against the agreed {agreed:X16}", StringComparison.Ordinal)));
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: resync 1/3 of Cleo (player 4) from tick {resumed.FromTick}: log ", StringComparison.Ordinal));

        // Every world held her machines from the rebuild until she was back, and Red was never the computer's.
        foreach (var client in clients)
        {
            Assert.Equal([(true, resumed.FromTick)], client.Session.Presence.OfType<PlayerLagging>().Where(l => l.Lagging).Select(l => (l.Lagging, l.FromTick)));
            Assert.Single(client.Session.Presence.OfType<PlayerLagging>(), l => !l.Lagging && l.Player == P(4));
            Assert.Empty(client.Session.Presence.OfType<PlayerLeft>());
            Assert.False(client.Session.Lagging(P(4)));
        }
        Assert.False(redAi, "the computer took Red while Cleo's world was rebuilt");

        Assert.True(RelayClients.AssertRecovered(relay.Relay, room.Id, clients) >= 4 * 500 / 20);
        Assert.True(RelayClients.AssertAgree(clients, resyncs: true) >= 500 / 20);
        foreach (var client in new[] { anna, cleo })
            RelayClients.AssertPlaysBack(client);
    }

    /// <summary>
    /// Comings and goings close together, as the relay makes them when people quit and come back while the match stands still:
    /// in a co-op skirmish Cleo quits; then Anna and Dan quit in one tick, the relay's next bundle, while Cleo and Dan ask to
    /// come back by name, and are back once their rebuilt worlds are on time. Every world, those rebuilt from the relay's log
    /// too, keeps the rules at every tick by the comings and goings the bundles brought: an away commander's machines are
    /// kept by the lowest numbered commander of the race who is there, and the race is the computer's only while nobody of it is.
    /// </summary>
    [Fact]
    public async Task Through_the_relay_commanders_quit_in_one_tick_come_back_and_every_world_keeps_the_keeper_rules()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var cleo = await relay.Connect("Cleo", web: false);
        var dan = await relay.Connect("Dan", web: true);
        var room = await RelayClients.Open(anna,
            new CreateRoom("Together", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2), ben, cleo, dan);
        List<RelayClient> clients = [anna, ben, cleo, dan];
        await RelayClients.Begin(anna, clients);
        var seen = new List<(string Who, long Tick, Controller Red, bool RedAi, PlayerId? KeepsAnna, PlayerId? KeepsCleo, bool DanAway, PlayerId? KeepsDan)>();
        void Watch(RelayClient client)
        {
            client.Script = new CommanderScript(client.Me, 500 + client.Me.Value + clients.Count, gives: false);
            client.AfterStep = world => seen.Add((client.Name, world.Tick, world.Races[Race.Red].Controller, world.AiControllers.Any(ai => ai.Race == Race.Red),
                world.Players.KeeperOf(P(0)), world.Players.KeeperOf(P(4)), world.Players.Away(P(5)), world.Players.KeeperOf(P(5))));
        }
        clients.ForEach(Watch);

        await RelayClients.PlayTo(200, clients);
        await cleo.Leave();
        await RelayClients.PlayTo(300, clients);
        // Everyone stands at 300 with their turns sent up to 304: once the relay has closed 304, whoever quits now is gone
        // from 305, its next bundle, which waits for everyone still there.
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.NextTick) == 305, TimeSpan.FromSeconds(15), "bundle 304 closed");
        await anna.Leave();
        var cleoBack = await relay.Rejoin("Cleo", web: false, room.Id);
        await dan.Leave();
        var danBack = await relay.Rejoin("Dan", web: true, room.Id);
        Assert.Equal((P(4), P(5)), (cleoBack.Me, danBack.Me));
        foreach (var back in new[] { cleoBack, danBack })
        {
            Watch(back);
            clients.Add(back);
        }
        await RelayClients.PlayTo(500, clients);

        // Anna and Dan left in one tick; Cleo and Dan came back after it.
        var log = ben.Session.Presence;
        var changes = log.Select(m => m switch
        {
            PlayerLeft left => (left.Player, Left: true, left.FromTick),
            PlayerReturned back => (back.Player, Left: false, back.FromTick),
            _ => throw new InvalidOperationException($"{m} in the log"),
        }).ToList();
        Assert.Equal([(P(4), true), (P(0), true), (P(5), true)], changes.Where(c => c.Left).Select(c => (c.Player, c.Left)));
        Assert.Equal([305L, 305L], changes.Where(c => c.Left).Skip(1).Select(c => c.FromTick));
        Assert.Equal([P(4), P(5)], changes.Where(c => !c.Left).Select(c => c.Player).OrderBy(p => p.Value));
        Assert.All(changes.Where(c => !c.Left), c => Assert.True(c.FromTick > 305, $"{c.Player} back from {c.FromTick}"));

        // In every world, also as the two who came back caught up, at every tick: the lowest numbered commander of Red who is
        // there keeps the machines of the one who is not, Red is the computer's only while neither is there, and Ben keeps
        // Dan's while Dan is away.
        Assert.Equal(2, seen.Count(s => s.Who == "Cleo" && s.Tick == 1));
        Assert.Equal(2, seen.Count(s => s.Who == "Dan" && s.Tick == 1));
        Assert.Contains(seen, s => s.Who == "Ben" && s.Tick == 500);
        Assert.Contains(seen, s => s.RedAi);
        foreach (var s in seen)
        {
            bool Away(PlayerId player) => RelayClients.AwayAt(log, player, s.Tick);
            var there = new[] { P(0), P(4) }.Where(p => !Away(p)).ToList();
            PlayerId? keeper = there.Count > 0 ? there.MinBy(p => p.Value) : null;
            Assert.True((s.Red, s.RedAi) == (keeper is null ? (Controller.Ai, true) : (Controller.Player, false)), $"{s.Who} at {s.Tick}: Red {s.Red}");
            Assert.Equal(Away(P(0)) ? keeper : null, s.KeepsAnna);
            Assert.Equal(Away(P(4)) ? keeper : null, s.KeepsCleo);
            Assert.Equal(Away(P(5)), s.DanAway);
            Assert.Equal(Away(P(5)) ? P(1) : null, s.KeepsDan);
        }
        Assert.All(clients.Where(c => !c.Gone), c => Assert.Equal(ben.Session.Presence, c.Session.Presence));
        Assert.True(RelayClients.AssertAgree(clients) >= 500 / 20);
        foreach (var client in new[] { ben, cleoBack, danBack })
            Assert.Equal(client.World.Players.Keepers, RelayClients.AssertPlaysBack(client).Players.Keepers);
    }

    /// <summary>
    /// The same in five against five: Cleo quits; then Anna and Ben, Green's first, quit in one tick while Cleo and Ben ask to
    /// come back. In every world at every tick a bot drives the hero of each person who is away by the comings and goings the
    /// bundles brought, and a team is the computer's only while nobody of it is there.
    /// </summary>
    [Fact]
    public async Task Through_the_relay_heroes_change_hands_in_the_ticks_their_people_quit_and_come_back()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: true);
        var ben = await relay.Connect("Ben", web: false);
        var cleo = await relay.Connect("Cleo", web: true);
        var dan = await relay.Connect("Dan", web: false);
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 1, Lives = 3, TeamSize = 5 }, FogOfWar = false };
        var room = await RelayClients.Open(anna, new CreateRoom("Ten", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false), ben, cleo, dan);
        List<RelayClient> clients = [anna, ben, cleo, dan];
        await RelayClients.Begin(anna, clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 1, line: c.Id % 2));
        Assert.Equal([P(0), P(2), P(4), P(8)], clients.Select(c => c.Me));
        var seen = new List<(string Who, long Tick, PlayerId[] Bots, Controller Red, Controller Green)>();
        void Watch(RelayClient client)
        {
            client.Script = new HeroScript(client.Me, 600 + client.Me.Value + clients.Count);
            client.AfterStep = world => seen.Add((client.Name, world.Tick, [.. world.HeroBots.Keys.Where(p => p.Value is 0 or 2 or 4)],
                world.Races[Race.Red].Controller, world.Races[Race.Green].Controller));
        }
        clients.ForEach(Watch);

        await RelayClients.PlayTo(300, clients);
        await cleo.Leave();
        await RelayClients.PlayTo(400, clients);
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.NextTick) == 405, TimeSpan.FromSeconds(15), "bundle 404 closed");
        await anna.Leave();
        var cleoBack = await relay.Rejoin("Cleo", web: true, room.Id);
        await ben.Leave();
        var benBack = await relay.Rejoin("Ben", web: false, room.Id);
        Assert.Equal((P(4), P(2)), (cleoBack.Me, benBack.Me));
        foreach (var back in new[] { cleoBack, benBack })
        {
            Watch(back);
            clients.Add(back);
        }
        await RelayClients.PlayTo(700, clients);

        var log = dan.Session.Presence;
        Assert.Equal([(P(4), true), (P(0), true), (P(2), true), (P(2), false), (P(4), false)],
            log.Select(m => m is PlayerLeft left ? (left.Player, true) : (((PlayerReturned)m).Player, false)).OrderBy(c => !c.Item2).ThenBy(c => c.Item2 ? 0 : c.Player.Value));
        Assert.Equal([405L, 405L], log.OfType<PlayerLeft>().Skip(1).Select(l => l.FromTick));
        Assert.All(log.OfType<PlayerReturned>(), r => Assert.True(r.FromTick > 405, $"{r.Player} back from {r.FromTick}"));
        Assert.Contains(seen, s => s.Red == Controller.Ai);
        foreach (var s in seen)
        {
            bool Away(PlayerId player) => RelayClients.AwayAt(log, player, s.Tick);
            PlayerId[] bots = [.. new[] { P(0), P(2), P(4) }.Where(Away)];
            Assert.True(bots.SequenceEqual(s.Bots), $"{s.Who} at {s.Tick}: bots of [{string.Join(", ", s.Bots)}]");
            var red = new[] { P(0), P(4) }.All(Away) ? Controller.Ai : Controller.Player;
            var green = new[] { P(2), P(8) }.All(Away) ? Controller.Ai : Controller.Player;
            Assert.True((red, green) == (s.Red, s.Green), $"{s.Who} at {s.Tick}: Red {s.Red}, Green {s.Green}");
        }
        Assert.Equal(2, seen.Count(s => s.Who == "Cleo" && s.Tick == 1));
        Assert.Equal(2, seen.Count(s => s.Who == "Ben" && s.Tick == 1));
        Assert.Contains(seen, s => s.Who == "Dan" && s.Tick == 700);
        Assert.All(clients.Where(c => !c.Gone), c => Assert.Equal(dan.Session.Presence, c.Session.Presence));
        Assert.True(RelayClients.AssertAgree(clients) >= 700 / 20);
        foreach (var client in new[] { dan, cleoBack, benBack })
        {
            var played = RelayClients.AssertPlaysBack(client);
            Assert.Equal(client.World.HeroBots.Keys, played.HeroBots.Keys);
            Assert.Equal(client.World.Battle!.Fingerprint(), played.Battle!.Fingerprint());
        }
    }

    /// <summary>
    /// A tick no value of which more than half of the players reported is settled once everyone still playing has reported
    /// it, also when the last who had not quits instead (P2 design §5.2): two worlds that differ there, with nobody left to
    /// tell which went its own way, are both rebuilt from the relay's log and agree again; worlds that agree are agreed on,
    /// and the one who quit, back, catches up past it in peace.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tick_only_a_leaver_had_still_to_report_is_compared_when_they_leave(bool differ)
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: false);
        var ben = await relay.Connect("Ben", web: true);
        var cleo = await relay.Connect("Cleo", web: false);
        var room = await RelayClients.Open(anna,
            new CreateRoom("Together", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2), ben, cleo);
        List<RelayClient> clients = [anna, ben, cleo];
        await RelayClients.Begin(anna, clients);
        Assert.Equal((P(0), P(1), P(4)), (anna.Me, ben.Me, cleo.Me));
        await RelayClients.PlayTo(99, clients);

        // With differ, Ben's world goes its own way just before tick 100; Anna and he report it, Cleo, a tick behind, does not.
        if (differ)
            ben.World.Races[Race.Blue].Bmu += 1;
        await RelayClients.PlayTo(103, [anna, ben]);
        Assert.Equal(differ, anna.Sums[100] != ben.Sums[100]);
        // The relay has taken both reports for 100: alike, they are two of three, more than half, and agreed at once; different,
        // neither value is, and they wait for Cleo's.
        await RelayClients.Until(() => differ ? relay.Relay.Pending(room.Id, 100)?.Count == 2 : relay.Relay.Agreed(room.Id, 100) is not null,
            TimeSpan.FromSeconds(15), "both reports of tick 100 at the relay");
        Assert.Equal(differ, relay.Relay.Agreed(room.Id, 100) is null);

        // Cleo quits without having reported tick 100: everyone still there has.
        await cleo.Leave();
        await RelayClients.PlayTo(140, clients);
        if (differ)
        {
            // Neither of the two is more than half: both worlds are rebuilt from the relay's log, and agree from then on.
            await RelayClients.PlayTo(300, clients);
            foreach (var client in new[] { anna, ben })
                Assert.Equal((ResumeReason.Resync, 1), (Assert.Single(client.Rebuilt).Reason, client.Rebuilt[0].Resyncs));
            Assert.Contains(relay.Lines, l => l.Contains("(player 1) out of step at tick 100: ", StringComparison.Ordinal)
                                              && l.EndsWith(", no value agreed by more than half of 2 playing", StringComparison.Ordinal));
            Assert.Equal(anna.Sums[100], relay.Relay.Agreed(room.Id, 100));
            Assert.Equal(15, RelayClients.AssertAgree(clients, resyncs: true));
            return;
        }
        // Agreed on: Cleo comes back, catches up past it, and everyone agrees on every checksum.
        var back = await relay.Rejoin("Cleo", web: false, room.Id);
        clients.Add(back);
        await RelayClients.PlayTo(200, clients);
        Assert.Equal(10, RelayClients.AssertAgree(clients));
    }

    /// <summary>A machine of a player's that a follow order can be given to: a constructor if they have one, which no fight needs.</summary>
    private static int Follower(GameWorld world, PlayerId owner, int? except = null) =>
        world.Machines.Where(m => m.Owner == owner && !m.IsDead && m.Inside is null && m.Id != except)
            .OrderBy(m => m.Spec.Type == "CONSTRUCTOR" ? 0 : 1).ThenBy(m => m.Id).First().Id;

    /// <summary>
    /// A commander orders <paramref name="machine"/> to follow a machine of their own (the lowest numbered), their script
    /// resting meanwhile so that nothing else of theirs orders it in the same turn; returns that leader, and the machine's
    /// order in every world just after the tick the order was carried out in.
    /// </summary>
    private static async Task<(int Leader, List<Order?> Orders)> Follow(RelayClient commander, int machine, List<RelayClient> clients)
    {
        var playing = clients.Where(c => !c.Gone).ToList();
        var leader = commander.World.Machines.Where(m => m.Owner == commander.Me && !m.IsDead && m.Id != machine).Min(m => m.Id);
        var due = commander.Session.NextLocalTurn;
        var seen = new List<Order?>();
        foreach (var client in playing)
        {
            client.AfterStep = world =>
            {
                if (world.Tick == due + 1)
                    seen.Add(world.Find<Machine>(machine)?.Order);
            };
        }
        var script = commander.Script;
        commander.Script = null;
        commander.Session.Queue(new FollowCommand(commander.Race, [machine], leader));
        await RelayClients.PlayTo(due + 2, playing);
        commander.Script = script;
        foreach (var client in playing)
            client.AfterStep = null;
        Assert.Equal(playing.Count, seen.Count);
        return (leader, seen);
    }
}
