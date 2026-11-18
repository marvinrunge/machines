using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// Battles of the bigger teams through the real relay (P2 design §7, slice S5): the relay and the sim take up to fifteen a
/// side, which the menus do not offer and only MACHINES_MP's team=15 (later matchmaking) asks for; a battle of fifteen against
/// fifteen starts with every seat a hero, its players numbered up to 31, plays past its opening on the beat with every world
/// agreeing, takes a late joiner into its last seat, and every recording plays back.
/// </summary>
public class BigTeamRelayTests
{
    private static PlayerId P(int value) => new(value);

    [Fact]
    public async Task Fifteen_against_fifteen_starts_with_thirty_heroes_plays_on_the_beat_and_takes_a_late_joiner_into_its_last_seat()
    {
        await using var relay = await RelayClients.Start();
        var clients = new List<RelayClient>();
        foreach (var (name, i) in new[] { "Anna", "Ben", "Cleo" }.Select((n, i) => (n, i)))
            clients.Add(await relay.Connect(name, web: i % 2 == 0));
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 1, Lives = 3, TeamSize = BattleBalance.MostTeamSize }, FogOfWar = false };
        var room = await RelayClients.Open(clients[0], new CreateRoom("Fifteen", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false), [.. clients.Skip(1)]);
        Assert.Equal(30, room.Seats.Count);
        var match = await RelayClients.Begin(clients[0], clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 1, line: c.Id % 2));
        foreach (var client in clients)
            client.Script = new HeroScript(client.Me, 900 + client.Me.Value);

        // Thirty heroes: Red's seats are players 0 and 4 to 17, Green's 2 and 18 to 31; Anna and Cleo play Red, Ben Green.
        Assert.Equal(15, match.Options.Battle!.TeamSize);
        Assert.Equal([P(0), P(2), P(4)], clients.Select(c => c.Me));
        var anna = clients[0];
        Assert.Equal(30, anna.World.Players.Rows.Count);
        Assert.Equal([.. new[] { 0, 2 }.Concat(Enumerable.Range(4, 28)).Select(P)], anna.World.Players.Rows.Select(r => r.Id).Order());
        Assert.Equal([15, 15], anna.World.Battle!.Members.Select(team => team.Count));
        Assert.Equal(27, anna.World.HeroBots.Count);
        Assert.Equal(BattleBalance.CaptureTargetFor(15), anna.World.Battle.CaptureTarget);
        Assert.Equal(30, relay.Relay.WithBeat(room.Id, beat => beat.Seats.Count));

        // Past the opening, where every bot gives its first order, on the beat.
        await RelayClients.PlayTo(320, clients);
        Assert.False(anna.World.Battle.InOpening(anna.World.Time));
        Assert.Equal(30, anna.World.Wired!.Heroes.Keys.Count(p => anna.World.HeroOf(p) is not null));

        // Zoe joins late into the room's last seat, Green's fifteenth place: player 31, the highest a battle has.
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id, seat: 29);
        Assert.Equal((P(31), Race.Green, ResumeReason.LateJoin), (zoe.Me, zoe.Race, zoe.Resumed!.Reason));
        zoe.Script = new HeroScript(zoe.Me, 931);
        clients.Add(zoe);
        await RelayClients.PlayTo(900, clients);

        var joined = Assert.Single(anna.Session.Presence.OfType<PlayerJoined>());
        Assert.Equal((P(31), "Zoe"), (joined.Player, joined.Name));
        foreach (var client in clients)
        {
            Assert.Equal(anna.Session.Presence, client.Session.Presence);
            Assert.Equal(new PlayerSeat(P(31), Race.Green, PlayerKind.Human, "Zoe"), client.World.Players[P(31)]);
            Assert.Equal(26, client.World.HeroBots.Count);
            Assert.DoesNotContain(P(31), client.World.HeroBots.Keys);
        }
        Assert.Contains(anna.Session.Played, t => t.Player == P(31) && t.Tick >= joined.FromTick && t.Commands.Count > 0);

        Assert.True(RelayClients.AssertAgree(clients) >= 900 / 20);
        foreach (var client in clients)
            Assert.Equal([P(31)], RelayClients.AssertPlaysBack(client).Players.Joined);
    }
}
