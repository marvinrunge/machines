using System.Net;
using System.Text;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>Chat through the real relay: in a room to the others, in a match also to a single player.</summary>
public class ChatTests
{
    [Fact]
    public async Task A_chat_reaches_the_others_in_the_room_and_only_them()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await Client.Connect(relay, "Anna");
        await using var ben = await Client.Connect(relay, "Ben");
        await using var cleo = await Client.Connect(relay, "Cleo");
        await using var dan = await Client.Connect(relay, "Dan");
        var room = await anna.Open("Anna's room", slots: 2);
        await ben.Join(room);
        await cleo.Open("Cleo's room", slots: 2);
        // Dan stays in the list of rooms.

        anna.Connection.Send(new Chat("whoever", "  Hello there  "));

        var heard = await ben.Next<Chat>();
        Assert.Equal(new Chat("Anna", "Hello there"), heard);
        await anna.HeardNoChat();
        await cleo.HeardNoChat();
        await dan.HeardNoChat();
    }

    [Fact]
    public async Task In_a_match_a_chat_for_one_player_reaches_only_them()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await Client.Connect(relay, "Anna");
        await using var ben = await Client.Connect(relay, "Ben");
        await using var cleo = await Client.Connect(relay, "Cleo");
        var room = await anna.Open("three", slots: 3);
        await ben.Join(room);
        await cleo.Join(room);
        var state = await anna.Next<RoomState>(s => s.Players.Count == 3);
        foreach (var client in new[] { ben, cleo, anna })
        {
            client.Connection.Send(new SetPlayer(state.Players.Single(p => p.ClientId == client.Id).Colour, Ready: true));
            await anna.Next<RoomState>(s => s.Players.Single(p => p.ClientId == client.Id).Ready);
        }
        anna.Connection.Send(new StartMatch());
        var match = (await anna.Next<MatchStarted>()).Match;
        var bensRace = match.Players.Single(p => p.ClientId == ben.Id).Slot;

        anna.Connection.Send(new Chat("Anna", "Ally with me", To: bensRace));
        anna.Connection.Send(new Chat("Anna", "Good luck, all"));

        Assert.Equal(new Chat("Anna", "Ally with me", bensRace), await ben.Next<Chat>());
        Assert.Equal(new Chat("Anna", "Good luck, all"), await ben.Next<Chat>());
        Assert.Equal(new Chat("Anna", "Good luck, all"), await cleo.Next<Chat>());
        await anna.HeardNoChat();
        await cleo.HeardNoChat();
    }

    [Fact]
    public void A_chat_without_an_addressee_reads_as_one_for_everyone()
    {
        // As the clients before addressed chat sent it: the protocol stays the same.
        var old = NetJson.Decode(Encoding.UTF8.GetBytes("""{"$type":"chat","From":"Anna","Text":"Hello"}"""));

        Assert.Equal(new Chat("Anna", "Hello"), old);
        Assert.Equal(new Chat("Anna", "Hi", Race.Green), NetJson.Decode(NetJson.Encode(new Chat("Anna", "Hi", Race.Green))));
    }

    internal sealed class Client : IAsyncDisposable
    {
        public required Connection Connection { get; init; }
        public int Id { get; private set; }

        public static async Task<Client> Connect(RelayServer relay, string name)
        {
            var connection = await Connection.ConnectAsync("127.0.0.1", relay.Port, CancellationToken.None);
            connection.Send(new Hello(name, NetJson.Protocol));
            var client = new Client { Connection = connection };
            client.Id = (await client.Next<Welcome>()).ClientId;
            return client;
        }

        public async Task<int> Open(string name, int slots)
        {
            Connection.Send(new CreateRoom(name, "m_desert", "m_desert", Enum.GetValues<Race>().Take(slots).ToList(), new MatchOptions(), FillWithAi: false));
            return (await Next<RoomState>(s => s.Players.Any(p => p.ClientId == Id))).Id;
        }

        public async Task Join(int room)
        {
            Connection.Send(new JoinRoom(room));
            await Next<RoomState>(s => s.Players.Any(p => p.ClientId == Id));
        }

        /// <summary>
        /// Nothing said reached this client: the relay answers in the order it sends, so a chat it had sent here
        /// would arrive before the answer to a question asked after the others heard theirs.
        /// </summary>
        public async Task HeardNoChat()
        {
            Connection.Send(new ListRooms());
            var heard = await Next<NetMessage>(m => m is RoomList or Chat);
            Assert.IsType<RoomList>(heard);
        }

        public async Task<T> Next<T>(Func<T, bool>? wanted = null) where T : NetMessage
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var message = await Connection.Inbox.ReadAsync(timeout.Token);
                if (message is Refused refused)
                    throw new InvalidOperationException(refused.Reason);
                if (message is T typed && (wanted is null || wanted(typed)))
                    return typed;
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
