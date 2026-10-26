using System.Text.Json.Serialization;
using Machines.Import.Model;
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
[JsonDerivedType(typeof(RoomState), "roomState")]
[JsonDerivedType(typeof(StartMatch), "startMatch")]
[JsonDerivedType(typeof(MatchStarted), "matchStarted")]
[JsonDerivedType(typeof(Turn), "turn")]
[JsonDerivedType(typeof(ChecksumReport), "checksum")]
[JsonDerivedType(typeof(Desync), "desync")]
[JsonDerivedType(typeof(PlayerLeft), "playerLeft")]
[JsonDerivedType(typeof(Chat), "chat")]
public abstract record NetMessage;

/// <summary>First message of a client. <paramref name="Protocol"/> must match the relay's.</summary>
public sealed record Hello(string Name, int Protocol) : NetMessage;

public sealed record Welcome(int ClientId) : NetMessage;

/// <summary>A request the relay turned down, and why.</summary>
public sealed record Refused(string Reason) : NetMessage;

public sealed record ListRooms : NetMessage;

public sealed record RoomList(IReadOnlyList<RoomSummary> Rooms) : NetMessage;

public sealed record RoomSummary(int Id, string Name, string Scenario, string Planet, int Players, int MaxPlayers, bool Started);

/// <summary>
/// Opens a room on a map. <paramref name="Slots"/> are the map's start positions (its scenario races); players
/// take them in the order they join, and with <paramref name="FillWithAi"/> the rest play the original AI.
/// </summary>
public sealed record CreateRoom(string Name, string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options, bool FillWithAi) : NetMessage;

public sealed record JoinRoom(int RoomId) : NetMessage;

public sealed record LeaveRoom : NetMessage;

/// <summary>A player's colour and whether they are ready to start.</summary>
public sealed record SetPlayer(Race Colour, bool Ready) : NetMessage;

public sealed record RoomState(int Id, string Name, string Scenario, string Planet, IReadOnlyList<Race> Slots, MatchOptions Options,
    bool FillWithAi, int HostId, IReadOnlyList<LobbyPlayer> Players) : NetMessage;

public sealed record LobbyPlayer(int ClientId, string Name, Race Colour, bool Ready);

/// <summary>The host starts the match once everyone is ready.</summary>
public sealed record StartMatch : NetMessage;

public sealed record MatchStarted(MatchInfo Match) : NetMessage;

/// <summary>Everything a client needs to build the same world as everyone else.</summary>
/// <param name="InputDelay">Ticks between giving a command and every world carrying it out.</param>
public sealed record MatchInfo(string Scenario, string Planet, MatchOptions Options, int Seed, int InputDelay, IReadOnlyList<MatchPlayer> Players);

/// <summary>Who plays which start position (<paramref name="Slot"/>) in which colour; AI slots have no client.</summary>
public sealed record MatchPlayer(int ClientId, string Name, Race Slot, Race Colour, bool Ai);

/// <summary>One player's commands for one tick; sent every tick, empty or not.</summary>
public sealed record Turn(long Tick, Race Race, IReadOnlyList<Command> Commands) : NetMessage;

/// <summary>A player's world checksum after a tick; the relay compares them.</summary>
public sealed record ChecksumReport(long Tick, Race Race, ulong Value) : NetMessage;

/// <summary>Players' worlds differed after this tick.</summary>
public sealed record Desync(long Tick) : NetMessage;

/// <summary>A player left; their last turn was for <paramref name="FromTick"/> − 1, nobody waits for them after it.</summary>
public sealed record PlayerLeft(Race Race, long FromTick) : NetMessage;

public sealed record Chat(string From, string Text) : NetMessage;
