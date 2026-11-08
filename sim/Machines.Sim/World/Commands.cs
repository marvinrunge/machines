using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>A player's or AI's instruction. Commands are queued and applied at the start of the next tick.</summary>
public abstract record Command(Race Race);

/// <summary>Machines drive to a point, through <paramref name="Via"/> first when given (points picked with Shift, MachGuiMoveCommand).</summary>
public sealed record MoveCommand(Race Race, IReadOnlyList<int> Machines, Vector2 Target, IReadOnlyList<Vector2>? Via = null) : Command(Race);

/// <summary>Machines go after another and stay with it for as long as it lives (MachLogFollowOperation).</summary>
public sealed record FollowCommand(Race Race, IReadOnlyList<int> Machines, int Target) : Command(Race);

/// <summary>
/// What was picked with Shift held: each machine carries out one step after the other. Any other order to a machine
/// takes the rest of its steps away. A site among the steps stands at once, only the work on it waits its turn
/// (MachGuiConstructCommand::placeConstruction).
/// </summary>
public sealed record SeriesCommand(Race Race, IReadOnlyList<int> Machines, IReadOnlyList<Command> Steps) : Command(Race);

/// <summary>Machines attack a target, as do missile emplacements among the ids (MachGuiAttackCommand).</summary>
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

/// <summary>
/// Carriers fetch from the supplier, a mine or another race's smelter or pod, and from <paramref name="More"/> when
/// given (picked with Shift), and bring it to a smelter or pod; with <paramref name="Smelter"/> 0 they find their own
/// (MachGuiPickUpCommand). <paramref name="Adding"/> adds to the suppliers a carrier has.
/// </summary>
public sealed record TransportCommand(Race Race, IReadOnlyList<int> Carriers, int Mine, int Smelter, IReadOnlyList<int>? More = null, bool Adding = false) : Command(Race);

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

/// <summary>
/// An order to a whole squadron through its commander (MachGuiCommand::doAdminApply): with an administrator in it,
/// the commander shares the work out among the members; without one, the order goes to them as it is.
/// </summary>
public sealed record SquadronCommand(Race Race, int Squad, Command Order) : Command(Race);

/// <summary>Scavengers gather the given wrecks, one after another (MachGuiScavengeCommand).</summary>
public sealed record ScavengeCommand(Race Race, IReadOnlyList<int> Scavengers, IReadOnlyList<int> Debris) : Command(Race);

/// <summary>
/// First-person driving, sent every tick while a player drives a machine: forward and turn input (−1 to 1, turn
/// positive to the left), while the trigger is held the point the weapons fire at, which of the machine's weapons
/// are selected to fire (a bit for each, in mounting order; Tab in the original), where its upper body is turned
/// (radians anticlockwise from the hull, for the other players to see; the simulation does not read it), sideways
/// input (−1 to 1, positive to the left; only machines on legs, hover or glider move sideways, the remake's
/// addition), whether it turns at the slow rate (Ctrl) and how high the point aimed at lies: where the line of sight
/// through the crosshair meets a model, the ground or its end, as the original fired (MachPhys1stPersonDriver::aimData);
/// null aims at the middle of whatever stands at the point, as before. <paramref name="Buttons"/> says the bits of
/// <paramref name="Weapons"/> are triggers held, a button for each weapon, and not a selection: the others go on
/// reloading (the remake's addition). <paramref name="Driving"/> false hands it back.
/// </summary>
public sealed record DriveCommand(Race Race, int Machine, bool Driving, float Forward = 0, float Turn = 0, Vector2? Aim = null, int Weapons = -1,
    float Head = 0, float Strafe = 0, bool Slow = false, float? AimHeight = null, bool Buttons = false) : Command(Race);

/// <summary>Why a command was refused, for the client to show.</summary>
public sealed record CommandRejected(Command Command, string Reason);

/// <summary>Where a factory's new machines go once built (MachGuiAssemblyPointCommand); null clears it.</summary>
public sealed record AssemblyPointCommand(Race Race, IReadOnlyList<int> Factories, Vector2? Point) : Command(Race);

/// <summary>Takes an entry off a factory's queue; what was spent on it comes back (MachGuiProductionBank).</summary>
public sealed record CancelProductionCommand(Race Race, int Factory, int Index) : Command(Race);

/// <summary>Takes an entry off a lab's research queue; the research done on it is kept.</summary>
public sealed record CancelResearchCommand(Race Race, int Lab, int Index) : Command(Race);

/// <summary>
/// The race takes <paramref name="Target"/> for an ally, or with <paramref name="Ally"/> false no longer
/// (Ctrl and F1 to F3 in a network game, MachPromptText::doHandleKeyEvent). Only the race's own disposition
/// changes: the other's towards it stays as it was (MachLogRaces::dispositionToRace).
/// </summary>
public sealed record AllianceCommand(Race Race, Race Target, bool Ally) : Command(Race);

/// <summary>
/// What a player does with the hero of a Wired run, the remake's own survival mode: all of it goes through lockstep like
/// any other order. Except where it says otherwise, only with the hangar open, that is at the pod between waves.
/// </summary>
public abstract record WiredCommand(Race Race) : Command(Race);

/// <summary>Opens the hangar, which stops the countdown to the next wave, or closes it. The hero must be at the pod between waves.</summary>
public sealed record WiredHangarCommand(Race Race, bool Open) : WiredCommand(Race);

/// <summary>
/// Buys a chassis of the tree or one of the shop's offers, by its catalogue id, into the inventory, or with a
/// <paramref name="Slot"/> straight onto the hero: all at once or not at all, so that nothing bought is left lying when the hangar closes.
/// </summary>
public sealed record WiredBuyCommand(Race Race, string Item, WiredSlot? Slot = null) : WiredCommand(Race);

/// <summary>Sells an item out of the inventory for half of what went into it.</summary>
public sealed record WiredSellCommand(Race Race, int Item) : WiredCommand(Race);

/// <summary>Puts an owned weapon, module or ability into a slot; what sat there goes back into the inventory.</summary>
public sealed record WiredEquipCommand(Race Race, int Item, WiredSlot Slot) : WiredCommand(Race);

/// <summary>Takes a worn item off, back into the inventory.</summary>
public sealed record WiredUnequipCommand(Race Race, int Item) : WiredCommand(Race);

/// <summary>Changes to an owned chassis. Weapons and modules stay on where they still fit; the rest and the old chassis go into the inventory.</summary>
public sealed record WiredChassisCommand(Race Race, int Item) : WiredCommand(Race);

/// <summary>Raises a weapon or module by one Mk, worn or not.</summary>
public sealed record WiredUpgradeCommand(Race Race, int Item) : WiredCommand(Race);

/// <summary>Pays for a new shop offer, once per intermission.</summary>
public sealed record WiredRerollCommand(Race Race) : WiredCommand(Race);

/// <summary>Mends the pod for up to <paramref name="Bmu"/> BMUs; 0 for as much as it needs and the bank allows.</summary>
public sealed record WiredPodRepairCommand(Race Race, int Bmu = 0) : WiredCommand(Race);

/// <summary>Ends the wait for the next wave at once, with a bonus for the time saved. Needs no open hangar.</summary>
public sealed record WiredReadyCommand(Race Race) : WiredCommand(Race);

/// <summary>Places an owned chip in a socket of the board; with no <paramref name="Item"/> it pulls what is there.</summary>
public sealed record WiredChipCommand(Race Race, int? Item, int Socket) : WiredCommand(Race);

/// <summary>Takes one of the chips a boss left to choose from, by its place, with the hangar open into <paramref name="Socket"/> if one is named. Needs no open hangar.</summary>
public sealed record WiredChipChoiceCommand(Race Race, int Choice, int? Socket = null) : WiredCommand(Race);

/// <summary>Buys an escort, which comes out at the pod and follows the hero.</summary>
public sealed record WiredSquadBuyCommand(Race Race, ResearchKey Machine) : WiredCommand(Race);

/// <summary>Uses the ability in a slot at the point under the crosshair. Needs no open hangar.</summary>
public sealed record WiredAbilityCommand(Race Race, int Slot, Vector2 Aim) : WiredCommand(Race);

/// <summary>Fires the hero's thrusters that way; with no direction, straight ahead. Needs no open hangar.</summary>
public sealed record WiredDashCommand(Race Race, Vector2 Direction = default) : WiredCommand(Race);
