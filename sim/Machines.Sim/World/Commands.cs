using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>A player's or AI's instruction. Commands are queued and applied at the start of the next tick.</summary>
public abstract record Command(Race Race);

public sealed record MoveCommand(Race Race, IReadOnlyList<int> Machines, Vector2 Target) : Command(Race);

public sealed record AttackCommand(Race Race, IReadOnlyList<int> Machines, int Target) : Command(Race);

public sealed record AttackMoveCommand(Race Race, IReadOnlyList<int> Machines, Vector2 Target) : Command(Race);

public sealed record StopCommand(Race Race, IReadOnlyList<int> Machines) : Command(Race);

/// <summary>Place a construction site and send constructors to build it.</summary>
public sealed record PlaceConstructionCommand(
    Race Race,
    IReadOnlyList<int> Constructors,
    string Type,
    string? SubType,
    int HardwareLevel,
    Vector2 Position,
    int Orientation) : Command(Race);

/// <summary>Send constructors to help build (or finish) an existing site.</summary>
public sealed record ConstructCommand(Race Race, IReadOnlyList<int> Constructors, int Construction) : Command(Race);

public sealed record LocateCommand(Race Race, IReadOnlyList<int> Locators, Vector2 Target) : Command(Race);

public sealed record TransportCommand(Race Race, IReadOnlyList<int> Carriers, int Mine, int Smelter) : Command(Race);

public sealed record EnterLabCommand(Race Race, IReadOnlyList<int> Technicians, int Lab) : Command(Race);

public sealed record BuildMachineCommand(Race Race, int Factory, ResearchKey Machine) : Command(Race);

public sealed record ResearchCommand(Race Race, int Lab, ResearchKey Item) : Command(Race);

public sealed record PatrolCommand(Race Race, IReadOnlyList<int> Machines, IReadOnlyList<Vector2> Points) : Command(Race);

/// <summary>Fire a charged ion cannon (pod) or nuclear missile (silo) at any point of the map.</summary>
public sealed record StrikeCommand(Race Race, int Launcher, Vector2 Target) : Command(Race);

public sealed record HealCommand(Race Race, IReadOnlyList<int> Machines, int Target) : Command(Race);

public sealed record RepairCommand(Race Race, IReadOnlyList<int> Constructors, int Building) : Command(Race);

public sealed record CaptureCommand(Race Race, IReadOnlyList<int> Constructors, int Building) : Command(Race);

public sealed record DeconstructCommand(Race Race, IReadOnlyList<int> Constructors, int Building) : Command(Race);

/// <summary>Recycle machines at the given smelter or pod, or the nearest one when none is given.</summary>
public sealed record RecycleCommand(Race Race, IReadOnlyList<int> Machines, int? Smelter = null) : Command(Race);

public sealed record EnterGarrisonCommand(Race Race, IReadOnlyList<int> Machines, int Garrison) : Command(Race);

/// <summary>Send machines into one of the race's APCs; those that do not fit stay out.</summary>
public sealed record BoardCommand(Race Race, IReadOnlyList<int> Machines, int Apc) : Command(Race);

/// <summary>Send APCs to a point to let their passengers out there.</summary>
public sealed record DeployCommand(Race Race, IReadOnlyList<int> Apcs, Vector2 Target) : Command(Race);

/// <summary>Blow up machines or buildings after the original's 5 seconds; a stop command calls it off.</summary>
public sealed record SelfDestructCommand(Race Race, IReadOnlyList<int> Actors) : Command(Race);

/// <summary>Why a command was refused, for the client to show.</summary>
public sealed record CommandRejected(Command Command, string Reason);
