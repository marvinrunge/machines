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

/// <summary>Machines hold their ground: they shoot from where they stand and never move to (MachGuiStandGroundCommand).</summary>
public sealed record StandGroundCommand(Race Race, IReadOnlyList<int> Machines) : Command(Race);

/// <summary>Sets the machines' initiative (MachGuiDefconCommand).</summary>
public sealed record DefConCommand(Race Race, IReadOnlyList<int> Machines, DefCon DefCon) : Command(Race);

/// <summary>Spy locators lay land mines at the points, one at each, in turn (MachGuiDropLandMineCommand).</summary>
public sealed record DropLandMinesCommand(Race Race, IReadOnlyList<int> Spies, IReadOnlyList<Vector2> Points) : Command(Race);

/// <summary>Spy locators fetch new mines from the nearest own garrison (MachGuiRefillLandMineCommand).</summary>
public sealed record RefillLandMinesCommand(Race Race, IReadOnlyList<int> Spies) : Command(Race);

/// <summary>Spy locators put their camouflage on or take it off (MachGuiCamouflageCommand; network games).</summary>
public sealed record CamouflageCommand(Race Race, IReadOnlyList<int> Spies) : Command(Race);

/// <summary>
/// The machines become squadron <paramref name="Squad"/> (0 to 9), whose members it had are let go
/// (MachGuiSquadronBank::createSquad).
/// </summary>
public sealed record FormSquadronCommand(Race Race, IReadOnlyList<int> Machines, int Squad) : Command(Race);

/// <summary>Scavengers gather the given wrecks, one after another (MachGuiScavengeCommand).</summary>
public sealed record ScavengeCommand(Race Race, IReadOnlyList<int> Scavengers, IReadOnlyList<int> Debris) : Command(Race);

/// <summary>
/// First-person control of one machine, sent every tick while a player drives it: forward and turn input
/// (−1 to 1) and the enemy under the crosshair to fire at. <paramref name="Driving"/> false hands it back.
/// </summary>
/// <summary>
/// First-person driving: speed and turn, while the trigger is held the point the weapons fire at, which of the
/// machine's weapons are selected to fire (a bit for each, in mounting order; Tab in the original), and where its
/// upper body is turned, radians anticlockwise from the hull (for the other players to see; the simulation does
/// not read it).
/// </summary>
public sealed record DriveCommand(Race Race, int Machine, bool Driving, float Forward = 0, float Turn = 0, Vector2? Aim = null, int Weapons = -1, float Head = 0) : Command(Race);

/// <summary>Why a command was refused, for the client to show.</summary>
public sealed record CommandRejected(Command Command, string Reason);

/// <summary>Where a factory's new machines go once built (MachGuiAssemblyPointCommand); null clears it.</summary>
public sealed record AssemblyPointCommand(Race Race, IReadOnlyList<int> Factories, Vector2? Point) : Command(Race);

/// <summary>Takes an entry off a factory's queue; what was spent on it comes back (MachGuiProductionBank).</summary>
public sealed record CancelProductionCommand(Race Race, int Factory, int Index) : Command(Race);

/// <summary>Takes an entry off a lab's research queue; the research done on it is kept.</summary>
public sealed record CancelResearchCommand(Race Race, int Lab, int Index) : Command(Race);
