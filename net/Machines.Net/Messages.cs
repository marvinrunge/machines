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
/// <paramref name="Tier"/> the top of its bracket (0 for a normal room).</summary>
public sealed record RoomSummary(int Id, string Name, string Scenario, string Planet, int Players, int MaxPlayers, bool Started,
    string? Mode = null, int Tier = 0)
{
    /// <summary>The <see cref="Mode"/> of a battle room.</summary>
    public const string Battle = "battle";
}

/// <summary>
/// Opens a room on a map. <paramref name="Slots"/> are the map's start positions (its scenario races); players
/// take them in the order they join, and with <paramref name="FillWithAi"/> those still open at the start play the
/// original AI.
/// </summary>
public sealed record CreateRoom(string Name, string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options, bool FillWithAi) : NetMessage;

public sealed record JoinRoom(int RoomId) : NetMessage;

public sealed record LeaveRoom : NetMessage;

/// <summary>
/// A player's colour and whether they are ready to start; <paramref name="Terrain"/> fingerprints the ground their game
/// copy's export gives the room's planet (none without one). In a battle room <paramref name="Hero"/> is the hero the
/// player brings from their pod (its race is filled in at the start); nobody is ready there without one, and the relay
/// refuses one that is not in the room's bracket or does not fit together. Other rooms ignore it.
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

/// <summary>
/// The host changes the room's map and settings (MachGuiCtxSettings). Everyone has to get ready again; players the new
/// map has no room for are taken out of the room, the host last.
/// </summary>
public sealed record ChangeRoom(string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options, bool FillWithAi) : NetMessage;

public sealed record RoomState(int Id, string Name, string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options,
    bool FillWithAi, int HostId, IReadOnlyList<LobbyPlayer> Players, IReadOnlyList<RoomSeat> Seats) : NetMessage;

/// <summary>A player in a room; in a battle room with the hero they bring (null until they chose one).</summary>
public sealed record LobbyPlayer(int ClientId, string Name, Race Colour, bool Ready, long? Terrain = null, BattleHero? Hero = null);

/// <summary>A start position of the room: the player sitting there, or what it is for.</summary>
public sealed record RoomSeat(int? ClientId, SeatUse Use);

/// <summary>The host starts the match once everyone is ready.</summary>
public sealed record StartMatch : NetMessage;

public sealed record MatchStarted(MatchInfo Match) : NetMessage;

/// <summary>Everything a client needs to build the same world as everyone else.</summary>
/// <param name="InputDelay">Ticks between giving a command and every world carrying it out.</param>
/// <param name="Terrain">The ground every player has alike, by its fingerprint: shots are stopped by hills only
/// then, so that all worlds agree. Null when some player has none or another.</param>
public sealed record MatchInfo(string Scenario, string Planet, MatchOptions Options, int Seed, int InputDelay, IReadOnlyList<MatchPlayer> Players,
    long? Terrain = null);

/// <summary>Who plays which start position (<paramref name="Slot"/>) in which colour; AI slots have no client.</summary>
public sealed record MatchPlayer(int ClientId, string Name, Race Slot, Race Colour, bool Ai);

/// <summary>One player's commands for one tick; sent every tick, empty or not.</summary>
public sealed record Turn(long Tick, Race Race, IReadOnlyList<Command> Commands) : NetMessage;

/// <summary>A player's world checksum after a tick; the relay compares them.</summary>
public sealed record ChecksumReport(long Tick, Race Race, ulong Value) : NetMessage;

/// <summary>Players' worlds differed after this tick.</summary>
public sealed record Desync(long Tick) : NetMessage;

/// <summary>
/// A player left; their last turn was for <paramref name="FromTick"/> − 1, nobody waits for them after it, and
/// from that tick the AI plays their race.
/// </summary>
public sealed record PlayerLeft(Race Race, long FromTick) : NetMessage;

/// <summary>A player who dropped out asks to play their race again (the relay knows them by name).</summary>
public sealed record RejoinMatch(int RoomId) : NetMessage;

/// <summary>
/// The relay takes a returning player back: their race in <paramref name="Match"/>, and the tick from which they
/// play again. Every turn and every coming and going so far follows, in order, to rebuild the world from.
/// </summary>
public sealed record MatchResumed(MatchInfo Match, Race Slot, long FromTick) : NetMessage;

/// <summary>A player is back: from <paramref name="FromTick"/> on, the AI hands their race back and everyone waits for their turns again.</summary>
public sealed record PlayerReturned(Race Race, long FromTick) : NetMessage;

/// <summary>
/// A chat message: in a room to everyone in it; in a match to everyone, or with <paramref name="To"/> only to whoever
/// plays that race, as the original's F1–F3 sent one to a single opponent (MachGuiMessageBroker::sendInGameChatMessage).
/// The relay fills in <paramref name="From"/> and passes it on to the others; the sender shows their own.
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
