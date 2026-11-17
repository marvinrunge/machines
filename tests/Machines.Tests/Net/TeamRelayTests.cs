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
        Assert.True(back.FirstTurn > 400, $"back from tick {back.FirstTurn}");
        back.Script = new HeroScript(back.Me, 200);
        clients.Add(back);
        await RelayClients.PlayTo(1300, clients);
        foreach (var client in clients.Where(c => !c.Gone))
        {
            Assert.Equal([P(2)], client.World.Players.AwayPlayers);
            Assert.Equal([P(2)], client.World.HeroBots.Keys);
            Assert.Equal([P(2)], client.World.Battle!.Bots);
        }
        // Her hero is hers again: the turns she sent since carry her orders.
        Assert.Contains(clients[0].Session.Played, t => t.Player == P(5) && t.Tick >= back.FirstTurn && t.Commands.Count > 0);

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
        // Every world heard of the comings and goings alike, each return from the tick the returning player played again.
        Assert.Equal([(P(4), true), (P(4), false), (P(5), true), (P(1), true), (P(1), false)],
            anna.Session.Presence.Select(m => m is PlayerLeft left ? (left.Player, true) : (((PlayerReturned)m).Player, false)));
        Assert.Equal([cleoBack.FirstTurn, benBack.FirstTurn], anna.Session.Presence.OfType<PlayerReturned>().Select(r => r.FromTick));
        Assert.All(clients.Where(c => !c.Gone), c => Assert.Equal(anna.Session.Presence, c.Session.Presence));
        foreach (var client in new[] { anna, cleoBack, benBack })
            RelayClients.AssertPlaysBack(client);
    }

    [Fact]
    public async Task A_second_commander_whose_world_goes_its_own_way_is_a_desync_for_everyone()
    {
        await using var relay = await RelayClients.Start();
        var anna = await relay.Connect("Anna", web: true);
        var ben = await relay.Connect("Ben", web: false);
        var cleo = await relay.Connect("Cleo", web: true);
        var dan = await relay.Connect("Dan", web: false);
        await RelayClients.Open(anna,
            new CreateRoom("Together", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2), ben, cleo, dan);
        List<RelayClient> clients = [anna, ben, cleo, dan];
        await RelayClients.Begin(anna, clients);
        Assert.Equal(P(4), cleo.Me);
        await RelayClients.PlayTo(100, clients);
        Assert.Equal(5, RelayClients.AssertAgree(clients));

        // Something only Cleo's world has, a cheat or a bug: her teammate's world and the others' do not.
        cleo.World.Races[Race.Red].Bmu += 1;
        await RelayClients.PlayTo(200, clients);

        Assert.All(clients, c => Assert.Equal(120, c.Session.DesyncTick));
        Assert.Equal(anna.Sums[100], cleo.Sums[100]);
        Assert.NotEqual(anna.Sums[120], cleo.Sums[120]);
    }

    /// <summary>
    /// A tick is compared once everyone still playing has reported it, also when the last who had not quits instead: worlds
    /// that differ there are a desync at that tick, worlds that agree are agreed on, and the one who quit, back, catches up
    /// past it in peace.
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
        // The relay has taken both reports for 100 once it forwarded the turns they sent after them (for 107) to Cleo.
        var forwarded = new HashSet<PlayerId>();
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (forwarded.Count < 2)
            {
                if (await cleo.Connection.Inbox.ReadAsync(wait.Token) is Turn { Tick: 107 } turn)
                    forwarded.Add(turn.Player);
            }
        }

        // Cleo quits without having reported tick 100: everyone still there has.
        await cleo.Leave();
        await RelayClients.PlayTo(140, clients);
        if (differ)
        {
            Assert.Equal(100, anna.Session.DesyncTick);
            Assert.Equal(100, ben.Session.DesyncTick);
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
