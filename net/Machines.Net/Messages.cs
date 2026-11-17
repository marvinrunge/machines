using System.Text.Json.Serialization;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Net;

/// <summary>
/// Everything sent between a client and the relay, as JSON frames (see <see cref="Framing"/>). The relay only
/// matches players up and forwards; every client runs the whole simulation (deterministic lockstep).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Refused), "refused")]
[JsonDerivedType(typeof(ListRooms), "listRooms")]
[JsonDerivedType(typeof(RoomList), "roomList")]
[JsonDerivedType(typeof(CreateRoom), "createRoom")]
[JsonDerivedType(typeof(JoinRoom), "joinRoom")]
[JsonDerivedType(typeof(LeaveRoom), "leaveRoom")]
[JsonDerivedType(typeof(SetPlayer), "setPlayer")]
[JsonDerivedType(typeof(SetSeat), "setSeat")]
[JsonDerivedType(typeof(TakeSeat), "takeSeat")]
[JsonDerivedType(typeof(ChangeRoom), "changeRoom")]
[JsonDerivedType(typeof(RoomState), "roomState")]
[JsonDerivedType(typeof(StartMatch), "startMatch")]
[JsonDerivedType(typeof(MatchStarted), "matchStarted")]
[JsonDerivedType(typeof(Turn), "turn")]
[JsonDerivedType(typeof(ChecksumReport), "checksum")]
[JsonDerivedType(typeof(Desync), "desync")]
[JsonDerivedType(typeof(PlayerLeft), "playerLeft")]
[JsonDerivedType(typeof(RejoinMatch), "rejoinMatch")]
[JsonDerivedType(typeof(MatchResumed), "matchResumed")]
[JsonDerivedType(typeof(PlayerReturned), "playerReturned")]
[JsonDerivedType(typeof(Chat), "chat")]
[JsonDerivedType(typeof(Ack), "ack")]
[JsonDerivedType(typeof(ResumeLink), "resumeLink")]
[JsonDerivedType(typeof(LinkResumed), "linkResumed")]
[JsonDerivedType(typeof(Goodbye), "goodbye")]
[JsonDerivedType(typeof(Bundle), "bundle")]
[JsonDerivedType(typeof(PlayerJoined), "playerJoined")]
[JsonDerivedType(typeof(PlayerLagging), "playerLagging")]
[JsonDerivedType(typeof(MatchLoaded), "matchLoaded")]
[JsonDerivedType(typeof(MatchDecided), "matchDecided")]
[JsonDerivedType(typeof(MatchEnded), "matchEnded")]
[JsonDerivedType(typeof(SeatKey), "seatKey")]
[JsonDerivedType(typeof(LogPart), "logPart")]
[JsonDerivedType(typeof(LeftMatch), "leftMatch")]
public abstract record NetMessage;

/// <summary>
/// First message of a client. <paramref name="Protocol"/> must match the relay's. With <paramref name="Resume"/> the client
/// asks to keep its seat over a dropped line (see <see cref="Connection"/>); a relay that cannot ignores it, and a client
/// that leaves it out is served as before.
/// </summary>
public sealed record Hello(string Name, int Protocol, bool Resume = false) : NetMessage;

/// <summary>
/// The relay takes a client in. <paramref name="Session"/> is the key a dropped line comes back with, given only to a
/// client that asked to resume; <paramref name="Relay"/> the relay's version, for the game to show.
/// </summary>
public sealed record Welcome(int ClientId, string? Session = null, string? Relay = null) : NetMessage;

/// <summary>A request the relay turned down, and why.</summary>
public sealed record Refused(string Reason) : NetMessage;

public sealed record ListRooms : NetMessage;

public sealed record RoomList(IReadOnlyList<RoomSummary> Rooms) : NetMessage;

/// <summary>A room in the lobby's list. <paramref name="Mode"/> is "battle" for a battle room (null for a normal one), and
/// <paramref name="Tier"/> the top of its bracket (0 for a normal room). <paramref name="Joinable"/> counts the seats a bot has
/// played since the start of a running battle that a player may still join (P2 design §6.2): while the relay keeps the
/// battle's beat and nothing is decided.</summary>
public sealed record RoomSummary(int Id, string Name, string Scenario, string Planet, int Players, int MaxPlayers, bool Started,
    string? Mode = null, int Tier = 0, int Joinable = 0)
{
    /// <summary>The <see cref="Mode"/> of a battle room.</summary>
    public const string Battle = "battle";
}

/// <summary>
/// Opens a room on a map. <paramref name="Slots"/> are the map's start positions (its scenario races); players
/// take them in the order they join, and with <paramref name="FillWithAi"/> those still open at the start play the
/// original AI. <paramref name="SeatsPerSlot"/> is how many people command each start position's race together (one or
/// two); a battle room ignores it and seats its options' team size instead (<see cref="BattleOptions.TeamSize"/>).
/// </summary>
public sealed record CreateRoom(string Name, string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options, bool FillWithAi,
    int SeatsPerSlot = 1) : NetMessage;

/// <summary>
/// Joins a room before its match. A running battle takes a player late into a seat a bot has played since the start (P2
/// design §6.2): <paramref name="Seat"/> names the seat in the room's list, else the relay picks one on the team with fewer
/// people; <paramref name="Terrain"/> is the joiner's ground fingerprint (as <see cref="SetPlayer.Terrain"/>), which must be the
/// battle's.
/// </summary>
public sealed record JoinRoom(int RoomId, int? Seat = null, long? Terrain = null) : NetMessage;

public sealed record LeaveRoom : NetMessage;

/// <summary>
/// A player's colour and whether they are ready to start; <paramref name="Terrain"/> fingerprints the ground their game
/// copy's export gives the room's planet (none without one). The colour is that of the player's race, so it changes for
/// every teammate sitting at the same race, and is refused while another race shows it. In a battle room
/// <paramref name="Hero"/> is the hero the player brings from their pod (its race and player are filled in at the start
/// from the seat); nobody is ready there without one, and the relay refuses one that is not in the room's bracket or does
/// not fit together. Other rooms ignore it.
/// </summary>
public sealed record SetPlayer(Race Colour, bool Ready, long? Terrain = null, BattleHero? Hero = null) : NetMessage;

/// <summary>What a start position nobody sits at is for (the slot's drop-down in the room, MachGuiPlayerListItem).</summary>
public enum SeatUse
{
    /// <summary>Waiting for a player.</summary>
    Open,
    /// <summary>Nobody plays there.</summary>
    Closed,
    /// <summary>The AI plays there, whoever comes.</summary>
    Computer,
}

/// <summary>
/// The host's choice for a start position of the room: <paramref name="Seat"/> is its place in the room's list. A player
/// sitting there is taken out of the room; the host cannot take themself out.
/// </summary>
public sealed record SetSeat(int Seat, SeatUse Use) : NetMessage;

/// <summary>Before the match a player moves to a free open seat of the room (<paramref name="Seat"/>: its place in the room's list).</summary>
public sealed record TakeSeat(int Seat) : NetMessage;

/// <summary>
/// The host changes the room's map and settings (MachGuiCtxSettings). Everyone has to get ready again; players the new
/// map has no room for are taken out of the room, the host last.
/// </summary>
public sealed record ChangeRoom(string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options, bool FillWithAi,
    int SeatsPerSlot = 1) : NetMessage;

/// <summary>
/// A room as it stands. <paramref name="Seats"/> lists every seat, each with its race: in a normal room the start
/// positions in race order, <paramref name="SeatsPerSlot"/> seats each; in a battle room each race of the teams once, or
/// with a team size that many seats of each team's anchor.
/// </summary>
public sealed record RoomState(int Id, string Name, string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options,
    bool FillWithAi, int HostId, IReadOnlyList<LobbyPlayer> Players, IReadOnlyList<RoomSeat> Seats, int SeatsPerSlot = 1) : NetMessage;

/// <summary>
/// A player in a room, under the name the room shows (a name another player of the room already has gets a number); in a
/// battle room with the hero they bring (null until they chose one).
/// </summary>
public sealed record LobbyPlayer(int ClientId, string Name, Race Colour, bool Ready, long? Terrain = null, BattleHero? Hero = null);

/// <summary>A seat of the room: the race it plays, and the player sitting there or what it is for.</summary>
public sealed record RoomSeat(int? ClientId, SeatUse Use, Race Race);

/// <summary>The host starts the match once everyone is ready.</summary>
public sealed record StartMatch : NetMessage;

public sealed record MatchStarted(MatchInfo Match) : NetMessage;

/// <summary>Everything a client needs to build the same world as everyone else.</summary>
/// <param name="InputDelay">Ticks between giving a command and every world carrying it out.</param>
/// <param name="Terrain">The ground every player has alike, by its fingerprint: shots are stopped by hills only
/// then, so that all worlds agree. Null when some player has none or another.</param>
public sealed record MatchInfo(string Scenario, string Planet, MatchOptions Options, int Seed, int InputDelay, IReadOnlyList<MatchPlayer> Players,
    long? Terrain = null)
{
    /// <summary>
    /// The players of the match, a row for each of <see cref="Players"/> in its order: each its own
    /// <see cref="MatchPlayer.Player"/>, and where a row names none, the number <see cref="PlayerTable.Number"/> gives it
    /// over the rows' slots in their order; a person for a row that is no AI's, a bot for the computer's rows in a battle
    /// (<see cref="MatchOptions.IsBattle"/>), else the AI playing the whole race. A world is built from exactly this table
    /// (<see cref="Replay.BuildWorld"/>, the game's match), in its rows' order.
    /// </summary>
    public PlayerTable Table()
    {
        var ids = PlayerTable.Number([.. Players.Select(p => p.Slot)]);
        var battle = Options.IsBattle;
        return new PlayerTable(Players.Select((p, i) =>
            new PlayerSeat(p.Player ?? ids[i], p.Slot, !p.Ai ? PlayerKind.Human : battle ? PlayerKind.Bot : PlayerKind.Ai, p.Name)));
    }
}

/// <summary>
/// Who plays which start position (<paramref name="Slot"/>, their race) in which colour: the race's, alike in every row of
/// it; the computer's rows have no client. <paramref name="Player"/> is the player the row is (the relay and the game always
/// name it; null numbers the row by <see cref="PlayerTable.Number"/> from the rows' order), and <paramref name="Uid"/> the
/// player's account once there are accounts (none yet).
/// </summary>
public sealed record MatchPlayer(int ClientId, string Name, Race Slot, Race Colour, bool Ai, PlayerId? Player = null, string? Uid = null);

/// <summary>
/// One player's commands for one tick. Every world carries out only those of the player's own race, each as the player's
/// (<see cref="Command.Issuer"/>, which no turn carries). <paramref name="Tick"/> is the tick asked for; inside a
/// <see cref="Bundle"/>, the tick it runs on. In turn mode (offline, recordings) one is sent every tick, empty or not; online
/// (bundle mode) at most one a slot of the client's clock (P2 design §4.2), stamped from its world, which also tells the relay
/// how far that world is: <paramref name="Seen"/> the newest bundle it had run (−1 before any), <paramref name="Held"/> how
/// many bundles it held then, for the relay's turnaround (§2.7). Inside a bundle those two are left out, and
/// <paramref name="Asked"/> is the newest stamp the relay merged into it (−1 elsewhere), so a client knows which of its
/// turns landed there (§4.4). The three are left out of the JSON at those values, so a turn of a recording reads as before.
/// </summary>
public sealed record Turn(long Tick, PlayerId Player, IReadOnlyList<Command> Commands, long Seen = -1, int Held = 0, long Asked = -1) : NetMessage;

/// <summary>
/// One tick of a running match as the relay closed it (P2 design §2.4): every turn it placed on <paramref name="Tick"/> that
/// held commands, one a player at most, in player order; the comings and goings from this tick on (<paramref name="Presence"/>:
/// only <see cref="PlayerLeft"/>, <see cref="PlayerJoined"/>, <see cref="PlayerReturned"/> and <see cref="PlayerLagging"/>,
/// each from this tick, in player order and for one player in that order); and the input delay in force
/// (<paramref name="Delay"/>, 0 for none), which clients stamp their turns with and the world never sees. A player without a
/// turn here has nothing new for the tick. The relay sends one for every tick, in order, empty ones too.
/// </summary>
public sealed record Bundle(long Tick, IReadOnlyList<Turn> Turns, IReadOnlyList<NetMessage>? Presence = null, int Delay = 0) : NetMessage
{
    /// <summary>Whether the bundle holds no turn and no coming or going (a sealed log segment leaves such bundles out).</summary>
    [JsonIgnore]
    public bool Empty => Turns.Count == 0 && (Presence is null || Presence.Count == 0);
}

/// <summary>A player's world checksum after a tick; the relay compares them.</summary>
public sealed record ChecksumReport(long Tick, PlayerId Player, ulong Value) : NetMessage;

/// <summary>Players' worlds differed after this tick.</summary>
public sealed record Desync(long Tick) : NetMessage;

/// <summary>
/// A player left; their last turn was for <paramref name="FromTick"/> − 1, nobody waits for them after it, and
/// from that tick on the computer stands in for them (<see cref="GameWorld.ChangePresence"/>): the AI plays their race,
/// a teammate keeps their machines, or a bot drives their hero.
/// </summary>
public sealed record PlayerLeft(PlayerId Player, long FromTick) : NetMessage;

/// <summary>
/// A player who dropped out asks to play again (the relay knows them by name). <paramref name="Player"/> is the player they
/// were, when the game still knows it: the relay then takes that row and no other of the same name. With
/// <paramref name="Key"/>, the seat key the relay gave (<see cref="SeatKey"/>), it takes the seat that key is for, the player
/// given, also from a connection that still holds it (P2 design §6.1).
/// </summary>
public sealed record RejoinMatch(int RoomId, PlayerId? Player = null, string? Key = null) : NetMessage;

/// <summary>Why a player's world is rebuilt from the relay's log (P2 design §5.3).</summary>
public enum ResumeReason
{
    /// <summary>They came back to the match.</summary>
    Rejoin,
    /// <summary>Their world went its own way.</summary>
    Resync,
    /// <summary>They join a running battle in a bot's seat.</summary>
    LateJoin,
}

/// <summary>
/// The relay takes a player into a running match to rebuild their world: the player they are in <paramref name="Match"/>,
/// and <paramref name="FromTick"/>, the first bundle that comes live. Then follow <paramref name="Parts"/>
/// <see cref="LogPart"/>s of the sealed log, then the bundles since as plain bundles, then <see cref="MatchEnded"/> when the end
/// is agreed already, then the live ones (P2 design §5.3).
/// <paramref name="Key"/> is the seat's key when it is a new one, <paramref name="Resyncs"/> how often the player was resynced.
/// </summary>
public sealed record MatchResumed(MatchInfo Match, PlayerId Player, long FromTick, ResumeReason Reason = ResumeReason.Rejoin, int Parts = 0,
    string? Key = null, int Resyncs = 0) : NetMessage;

/// <summary>A player is back: from <paramref name="FromTick"/> on, they play again what the computer played for them and everyone waits for their turns again.</summary>
public sealed record PlayerReturned(PlayerId Player, long FromTick) : NetMessage;

/// <summary>
/// A late joiner takes over a seat a bot has played since the start (P2 design §6.2): from <paramref name="FromTick"/> on the
/// world's row of <paramref name="Player"/> is a person's, named <paramref name="Name"/>, who drives the hero as it stands.
/// </summary>
public sealed record PlayerJoined(PlayerId Player, long FromTick, string Name) : NetMessage;

/// <summary>
/// A player's turns come late (<paramref name="Lagging"/>), or on time again, from <paramref name="FromTick"/> (P2 design
/// §2.6): while lagging, every world lets go of the wheel of what they drive at that tick; their next drive takes it again.
/// </summary>
public sealed record PlayerLagging(PlayerId Player, long FromTick, bool Lagging) : NetMessage;

/// <summary>The client's world is built and warmed up (P2 design §2.8): the relay starts the beat once every player has said so.</summary>
public sealed record MatchLoaded : NetMessage;

/// <summary>The client's world decided the match for everyone at <paramref name="Tick"/>: a winner, a battle over, a run ended (P2 design §2.8).</summary>
public sealed record MatchDecided(long Tick) : NetMessage;

/// <summary>More than half of the playing persons' worlds decided the match at <paramref name="Tick"/>: it is over (P2 design §2.8).</summary>
public sealed record MatchEnded(long Tick) : NetMessage;

/// <summary>The key with which this player takes their seat of the match back from any connection (P2 design §6.1).</summary>
public sealed record SeatKey(int RoomId, PlayerId Player, string Key) : NetMessage;

/// <summary>
/// One piece of a sealed segment of the relay's log (the bundles from <paramref name="From"/> to <paramref name="To"/>,
/// <see cref="BundleSegments"/>): piece <paramref name="Part"/> of <paramref name="Parts"/>, base64 in the JSON.
/// </summary>
public sealed record LogPart(long From, long To, int Part, int Parts, byte[] Data) : NetMessage;

/// <summary>The relay took this client out of the running match, and why (P2 design §2.6, §5.5, §6.1); it is back in the lobby.</summary>
public sealed record LeftMatch(string Reason) : NetMessage;

/// <summary>
/// A chat message: in a room to everyone in it; in a match to everyone, or with <paramref name="To"/> only to every player
/// of that race (the own race: the team; another: that side), as the original's F1–F3 sent one to a single opponent
/// (MachGuiMessageBroker::sendInGameChatMessage). The relay fills in <paramref name="From"/> and passes it on to the
/// others; the sender shows their own.
/// </summary>
public sealed record Chat(string From, string Text, Race? To = null) : NetMessage
{
    /// <summary>The longest text the relay passes on: the original's room chat (MAX_CHATMESSAGE_LEN less a name).</summary>
    public const int MaxText = 486;

    /// <summary>The longest text typed in a match (MAX_INGAMECHATMESSAGE_LEN less a name).</summary>
    public const int MaxMatchText = 175;
}

/// <summary>
/// What a <see cref="Connection"/> says to the one at the other end to carry a session over changing sockets. These are
/// neither counted nor kept, and never reach an inbox: the game and the relay only see the messages in between.
/// </summary>
public abstract record LinkMessage : NetMessage;

/// <summary>
/// The sender has received the first <paramref name="Received"/> messages of the other side, which no longer has to keep
/// them. Sent now and then, and when nothing else is said for a while, to show the line still stands.
/// </summary>
public sealed record Ack(long Received) : LinkMessage;

/// <summary>
/// The first message on a new socket of a client whose line dropped: the session it was given in <see cref="Welcome"/>,
/// and how many of the relay's messages it has received. The relay answers <see cref="LinkResumed"/>, or
/// <see cref="Refused"/> when it no longer knows the session.
/// </summary>
public sealed record ResumeLink(string Session, long Received) : LinkMessage;

/// <summary>The relay took the new socket for the session; it has received the first <paramref name="Received"/> of the client's messages.</summary>
public sealed record LinkResumed(long Received) : LinkMessage;

/// <summary>The sender closes the connection on purpose: the other side need not wait for it to come back.</summary>
public sealed record Goodbye : LinkMessage;
