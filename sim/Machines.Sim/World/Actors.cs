using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.World;

/// <summary>Anything on the map that belongs to a race and can be damaged.</summary>
public abstract class Actor
{
    protected Actor(int id, Race race, Vector2 position)
    {
        Id = id;
        Race = race;
        Position = position;
    }

    public int Id { get; }

    /// <summary>The race it belongs to. Changing sides gives it to the new race's first player (<see cref="Owner"/>) unless it is then given to another.</summary>
    public Race Race
    {
        get => _race;
        set
        {
            if (value == _race)
                return;
            _race = value;
            OwnerOverride = null;
        }
    }

    private Race _race;

    /// <summary>The player it belongs to when that is not its race's first player; null for every actor of a race with one player.</summary>
    public PlayerId? OwnerOverride { get; private set; }

    /// <summary>
    /// The player it belongs to, within its race: machines obey their owner when the race has several players, buildings
    /// every player of the race.
    /// </summary>
    public PlayerId Owner => OwnerOverride ?? PlayerId.Of(Race);

    /// <summary>Gives it to a player of its race; the race's first player is kept as no override at all.</summary>
    public void GiveTo(PlayerId owner) => OwnerOverride = owner == PlayerId.Of(Race) ? null : owner;

    /// <summary>
    /// Another race whose colours, map dot and icon it wears: a scenario's DISPLAY AS block, passed on to what its
    /// factories and constructors make (MachActor::displayMapAndIconRace). Changing sides ends it.
    /// </summary>
    public Race? DisplayAs { get; set; }

    /// <summary>The race it looks like (see <see cref="DisplayAs"/>).</summary>
    public Race ShownRace => DisplayAs ?? Race;

    public Vector2 Position { get; set; }

    /// <summary>Height above the ground plane, as placed by the scenario. Only the client uses it.</summary>
    public float Height { get; set; }

    public int HitPoints { get; set; }
    public int MaxHitPoints { get; protected set; }
    public int Armour { get; set; }
    public int MaxArmour { get; protected set; }

    public bool IsDead => HitPoints <= 0;

    /// <summary>Destroyed by the ion beam itself: it goes up in a white glow, with no explosion.</summary>
    public bool Vaporised { get; set; }

    /// <summary>The vortex killed it: its model is drawn out to nothing, without an explosion (MachLogVortexBomb).</summary>
    public bool Swallowed { get; set; }

    /// <summary>When it last reported being hit.</summary>
    public float? LastHitMail { get; set; }

    /// <summary>Destroyed by its own charge.</summary>
    public bool SelfDestructed { get; set; }

    /// <summary>Distance from the centre at which the actor's body ends; used for ranges and spacing.</summary>
    public abstract float Radius { get; }

    /// <summary>BMU cost; what destroying it is worth to the score.</summary>
    public abstract int Cost { get; }

    /// <summary>The race that damaged it last, credited when it dies.</summary>
    public Race? LastHitBy { get; set; }

    /// <summary>
    /// The player who damaged it last, within <see cref="LastHitBy"/>'s race: it only picks who of that race is credited,
    /// whether anyone is stays <see cref="LastHitBy"/>'s to say. Not in the checksum.
    /// </summary>
    public PlayerId? LastHitByPlayer { get; set; }

    /// <summary>Game time at which a started self-destruct goes off.</summary>
    public float? SelfDestructAt { get; set; }

    /// <summary>How ready it is to fight (see <see cref="Machines.Sim.World.Alert"/>).</summary>
    public Alert Alert { get; } = new();

    public Durability Durability
    {
        get => new(HitPoints, Armour);
        set => (HitPoints, Armour) = (value.HitPoints, value.Armour);
    }

    /// <summary>Distance from this actor's centre to the other's edge (its footprint for buildings).</summary>
    public float DistanceTo(Actor other) => other is Construction building
        ? building.DistanceFrom(Position)
        : MathF.Max(0, Vector2.Distance(Position, other.Position) - other.Radius);
}

/// <summary>A machine: a unit that moves, works and fights.</summary>
public sealed class Machine : Actor
{
    public Machine(int id, Race race, Vector2 position, MachineSpec spec, MachineLevel stats, string? weaponCombo)
        : base(id, race, position)
    {
        BuiltFor = race;
        Spec = spec;
        Stats = stats;
        WeaponCombo = weaponCombo;
        FittedBody = BodyOf(spec, stats);
        MaxHitPoints = HitPoints = stats.HitPoints;
        MaxArmour = Armour = stats.Armour;
    }

    public MachineSpec Spec { get; private set; }
    public MachineLevel Stats { get; private set; }
    public string? WeaponCombo { get; private set; }

    /// <summary>
    /// Its body fitted to its model (<see cref="MachineBodies"/>), which it stands and is hit in where the match's options
    /// say <see cref="MatchOptions.FittedBodies"/>; null for a flyer and one without a body, which keep their circle and box.
    /// </summary>
    public MachineBodies.Body? FittedBody { get; private set; }

    private static MachineBodies.Body? BodyOf(MachineSpec spec, MachineLevel stats) =>
        stats.Locomotion == Locomotion.Glider ? null : MachineBodies.Of(spec.Type, spec.SubType, stats.HardwareLevel);

    public List<WeaponMount> Weapons { get; } = [];

    /// <summary>How often it was refitted: a Wired hero's rig is rebuilt by the view when this changes.</summary>
    public int Refits { get; private set; }

    /// <summary>
    /// Makes it another machine, the only way a machine ever changes: a Wired hero in the hangar (the remake's own
    /// addition). Its hit points and armour keep their share of the new maximum, rounded up so that a refit never kills it.
    /// </summary>
    internal void Refit(MachineSpec spec, MachineLevel stats, string? weaponCombo, IEnumerable<WeaponMount> weapons)
    {
        var (hitPoints, armour) = (Share(HitPoints, MaxHitPoints, stats.HitPoints), Share(Armour, MaxArmour, stats.Armour));
        (Spec, Stats, WeaponCombo) = (spec, stats, weaponCombo);
        FittedBody = BodyOf(spec, stats);
        (MaxHitPoints, MaxArmour) = (stats.HitPoints, stats.Armour);
        (HitPoints, Armour) = (hitPoints, armour);
        Weapons.Clear();
        Weapons.AddRange(weapons);
        Refits++;
    }

    private static int Share(int value, int most, int newMost) =>
        most <= 0 ? newMost : (int)((value * (long)newMost + most - 1) / most);

    public ResearchKey Key => new(Spec.Type, Spec.SubType, Stats.HardwareLevel, WeaponCombo);

    /// <summary>Compass heading in radians, 0 along +X.</summary>
    public float Facing { get; set; }

    public Order Order { get; set; } = Order.Idle;
    public List<Vector2> Path { get; } = [];
    public int PathIndex { get; set; }

    /// <summary>Ore on board (transporters).</summary>
    public int Ore { get; set; }

    /// <summary>The squadron it belongs to, if any: an AI's from its script, a player's one of 0 to 9.</summary>
    public int? Squad { get; set; }

    /// <summary>
    /// Its initiative (MachLog::DefCon): how readily it picks fights of its own. Players' machines start at
    /// <see cref="DefCon.Normal"/>, the AI's at <see cref="DefCon.Low"/> (MachLogScenario).
    /// </summary>
    public DefCon DefCon { get; set; } = DefCon.Normal;

    /// <summary>When it was last given an order; for a while after one it does only that (MachLogMachine::virtualDefCon).</summary>
    public float OrderedAt { get; set; } = float.MinValue;

    /// <summary>For a spy locator: the land mines it still carries.</summary>
    public int Mines { get; set; }

    /// <summary>For a spy locator: hidden from the enemy's own choice of targets (camouflage, network games).</summary>
    public bool Camouflaged { get; set; }

    public bool IsSpy => Spec.Type == "SPY_LOCATOR";

    /// <summary>What a spy inside an enemy lab is downloading, or null (MachLogSpyLocator's pResItemCurrentlyStealing_).</summary>
    public ResearchKey? Stealing { get; set; }

    /// <summary>The building or APC a machine is inside (a lab, a garrison, an APC); it is off the map.</summary>
    public int? Inside { get; set; }

    /// <summary>The building whose door the machine is walking through (on the way in).</summary>
    public int? Doorway { get; set; }

    /// <summary>For an APC: the machines it carries.</summary>
    public List<int> Passengers { get; } = [];

    /// <summary>True while a player drives this machine in first person.</summary>
    public bool DrivenByPlayer { get; set; }

    /// <summary>The size class it finds its way and moves by, when not its radius's (<see cref="Navigation.Mobility.SizeOf"/>):
    /// set once as a story casts it (the prologue's big Grunts squeeze through the deck's gates as class 2); null for every other.</summary>
    public int? NavSize { get; set; }

    /// <summary>The player driving it while <see cref="DrivenByPlayer"/>; null otherwise. Not in the checksum, as that is not.</summary>
    public PlayerId? Driver { get; set; }

    /// <summary>
    /// The command it is carrying out, by number: machines sent by one command wait for one another rather than step
    /// aside (MachLogMachineMotionSequencer::objectHasSameCommandId).
    /// </summary>
    public int CommandId { get; set; }

    /// <summary>Whether it moves sideways when driven in first person: on legs, hover or glider (the remake's addition).</summary>
    public bool CanStrafe => Stats.Locomotion is Locomotion.Spider or Locomotion.Hover or Locomotion.Glider;

    /// <summary>Fractional armour regrown but not yet added.</summary>
    public float ArmourRegen { get; set; }

    /// <summary>Carries a weapon that attacks: damage or treachery. Healers and strike launchers do not count.</summary>
    public bool CanFight => Weapons.Any(w => w.DealsDamage || w.IsTreachery);

    public bool CanHeal => Weapons.Any(w => w.IsHealer);

    /// <summary>
    /// Turns its upper body on its legs or tracks, to aim and to look about, rather than its whole hull: a
    /// <c>BODY_TURN_LINK</c> in parmdata (MachPhysMachine::canTurnUpperBody).
    /// </summary>
    public bool CanTurnUpperBody => Stats.BodyTurnLink is { Length: > 0 } link && link != "NONE";

    /// <summary>The level-3 resource carrier, which gathers wreckage instead of carrying ore (MachLogResourceCarrier::isScavenger).</summary>
    public bool IsScavenger => Spec.Type == "RESOURCE_CARRIER" && Stats.HardwareLevel == 3;

    /// <summary>The race it was made for, whatever treachery has done since (MachLogMachine::logOriginalRace).</summary>
    public Race BuiltFor { get; }

    /// <summary>The squadron it left when it changed sides, for when it comes back.</summary>
    public int? OldSquad { get; set; }

    public override float Radius => RadiusOf(Stats);

    /// <summary>How wide a machine with these stats is, before it is made.</summary>
    public static float RadiusOf(MachineLevel stats) => MathF.Max(1.5f, stats.HighClearance);
    public override int Cost => Stats.Cost;

    public bool IsType(string type, string? subType = null) => Spec.Type == type && (subType is null || Spec.SubType == subType);
}

/// <summary>How ready an actor is for trouble (MachLogCanAttack's alertness, MachActor's times for its calls for help).</summary>
public sealed class Alert
{
    /// <summary>Its alertness: a look comes to something this many times in a hundred, and the higher it is the sooner the next look. It is born totally alert.</summary>
    public int Level { get; set; } = 100;

    /// <summary>When its alertness last fell.</summary>
    public float DiminishedAt { get; set; }

    /// <summary>When it next looks about.</summary>
    public float NextLook { get; set; }

    /// <summary>Before this time it looks for no new target (MachLogCanAttack::suppressAttackingUrges).</summary>
    public float NoChecksBefore { get; set; }

    /// <summary>Before this time a hit by another than its target does not make it think again.</summary>
    public float NextHitByTest { get; set; }

    /// <summary>Before this time it does not call for help again.</summary>
    public float NextSos { get; set; }

    /// <summary>The same against a player driving the shooter.</summary>
    public float NextFirstPersonSos { get; set; }

    public void Raise(int minimum) => Level = Math.Max(Level, minimum);
}

/// <summary>
/// How readily a machine fights of its own accord (MachLog::DefCon; the panel's "initiative"): <see cref="Low"/>
/// hunts whatever its scanner finds, <see cref="Normal"/> takes on what it can shoot from where it is or what
/// threatens it, <see cref="High"/> keeps to its orders.
/// </summary>
public enum DefCon
{
    Low,
    Normal,
    High,
}

/// <summary>A land mine a spy locator laid (MachLogLandMine): it goes off under the first enemy to come near.</summary>
/// <param name="owner">The player who laid it; the race's first player when not given.</param>
public sealed class LandMineState(int id, Race race, Vector2 position, PlayerId? owner = null)
{
    public int Id { get; } = id;
    public Race Race { get; } = race;
    public Vector2 Position { get; } = position;

    /// <summary>The player who laid it, within its race.</summary>
    public PlayerId Owner { get; } = owner ?? PlayerId.Of(race);
}

/// <summary>A building: placed by constructors, or present at the start.</summary>
public sealed class Construction : Actor
{
    public Construction(int id, Race race, Vector2 position, ConstructionSpec spec, ConstructionLevel stats, int orientation)
        : base(id, race, position)
    {
        Spec = spec;
        Stats = stats;
        Orientation = orientation;
        MaxHitPoints = stats.HitPoints;
        MaxArmour = Armour = stats.Armour;
        HitPoints = 1;
        var (min, max) = LocalFootprint(stats, orientation);
        Footprint = (min + position, max + position);
        // ENTRANCE: the point outside the door first (MachPhysConstructionData::addEntrance).
        if (stats.Geometry.FirstOrDefault(g => g.Keyword == "ENTRANCE") is { Args.Count: >= 2 } entrance)
        {
            float F(int i) => float.Parse(entrance.Args[i], System.Globalization.CultureInfo.InvariantCulture);
            EntranceOutside = Rotate(new Vector2(F(0), F(1)), orientation) + position;
            if (entrance.Args.Count >= 4)
                EntranceInside = Rotate(new Vector2(F(2), F(3)), orientation) + position;
            if (entrance.Args.Count >= 8)
                (Door, DoorWidth) = (Rotate(new Vector2(F(4), F(5)), orientation) + position, entrance.Args[7] == "LARGE_DOOR" ? 11.9f : 6.9f);
        }
        if (stats.Geometry.FirstOrDefault(g => g.Keyword == "INTERIOR_BOUNDARY") is { Args.Count: 4 } interior)
            Interior = Box(interior.Args, position, orientation);
        InteriorObstacles = stats.Geometry.Where(g => g.Keyword == "INTERIOR_OBSTACLE" && g.Args.Count == 8)
            .Select(g => Box([g.Args[0], g.Args[1], g.Args[4], g.Args[5]], position, orientation)).ToList();
    }

    /// <summary>The point of the door on the way through the entrance.</summary>
    public Vector2? Door { get; }

    /// <summary>The door's width: 6.9 m for a small door, 11.9 m for a large one (MachPhysConstructionData::addEntrance).</summary>
    public float DoorWidth { get; }

    /// <summary>The floor inside the building (<c>INTERIOR_BOUNDARY</c>), turned and placed with it; null for a building without one.</summary>
    public (Vector2 Min, Vector2 Max)? Interior { get; }

    /// <summary>What stands on that floor (<c>INTERIOR_OBSTACLE</c>).</summary>
    public IReadOnlyList<(Vector2 Min, Vector2 Max)> InteriorObstacles { get; }

    /// <summary>A building machines can go into: a door and a floor behind it (MachPhysConstruction::hasInterior).</summary>
    public bool HasInterior => Interior is not null && Door is not null && EntranceOutside is not null && EntranceInside is not null;

    /// <summary>The places inside for machines to stand: research bays in a lab, parking bays elsewhere.</summary>
    public IReadOnlyList<Vector2> Bays => _bays ??= Pads("STATION");

    private IReadOnlyList<Vector2>? _bays;

    private static (Vector2, Vector2) Box(IReadOnlyList<string> corners, Vector2 position, int orientation)
    {
        float F(int i) => float.Parse(corners[i], System.Globalization.CultureInfo.InvariantCulture);
        var (a, b) = (Rotate(new Vector2(F(0), F(1)), orientation) + position, Rotate(new Vector2(F(2), F(3)), orientation) + position);
        return (Vector2.Min(a, b), Vector2.Max(a, b));
    }

    /// <summary>Where machines come out of the building: a factory's pad before its door.</summary>
    public Vector2? EntranceOutside { get; }

    /// <summary>The pads carriers drive onto to load ore: a mine's <c>PICKUP</c> points, turned and placed with it.</summary>
    public IReadOnlyList<Vector2> PickUpPads => _pickUpPads ??= Pads("PICKUP");

    /// <summary>The pads carriers drive onto to drop ore: a smelter's or pod's <c>PUTDOWN</c> points.</summary>
    public IReadOnlyList<Vector2> PutDownPads => _putDownPads ??= Pads("PUTDOWN");

    private IReadOnlyList<Vector2>? _pickUpPads, _putDownPads;

    private List<Vector2> Pads(string keyword) => Stats.Geometry.Where(g => g.Keyword == keyword && g.Args.Count >= 2)
        .Select(g => Rotate(new Vector2(float.Parse(g.Args[0], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(g.Args[1], System.Globalization.CultureInfo.InvariantCulture)), Orientation) + Position)
        .ToList();

    /// <summary>The point just inside the door, where machines going in end up.</summary>
    public Vector2? EntranceInside { get; }

    public ConstructionSpec Spec { get; }
    public ConstructionLevel Stats { get; }
    public int Orientation { get; }

    /// <summary>The building's ground boundary in world space (axis-aligned, after rotation).</summary>
    public (Vector2 Min, Vector2 Max) Footprint { get; }

    /// <summary>BMUs put in by constructors so far.</summary>
    public float Built { get; private set; }

    public bool Complete => Built >= Stats.Cost;

    public List<WeaponMount> Weapons { get; } = [];

    /// <summary>Weapon combo of a turret or emplacement, if any.</summary>
    public string? WeaponCombo { get; set; }

    /// <summary>A pod given its ion cannon by the scenario (<c>ACTIVATE_ION_CANNON</c>) rather than by research.</summary>
    public bool IonCannonGranted { get; set; }

    /// <summary>A silo's dome counts as open (MachLogMissileEmplacement::domeOpen); the AI launches only then.</summary>
    public bool DomeOpen { get; internal set; }

    /// <summary>When a silo's dome, opening or about to close, changes over (MachLogMissileEmplacement's inTransition_).</summary>
    public float? DomeChange { get; internal set; }

    /// <summary>A silo stands open, from the moment its Opening plan starts until its Closing plan does.</summary>
    public bool DomeRaised => DomeOpen || DomeChange is not null;

    /// <summary>Where a silo's head turned to for a launch, until it turns back to close.</summary>
    public Vector2? StrikeAt { get; internal set; }

    /// <summary>When a silo's head has turned and its missile leaves for <see cref="StrikeAt"/>.</summary>
    public float? LaunchAt { get; internal set; }

    /// <summary>The AI builds it again where it stood when it is destroyed (<c>REBUILD</c> in the scenario).</summary>
    public bool Rebuild { get; set; }

    // Mines
    public int? MineralSite { get; set; }
    public float Ore { get; set; }

    // Smelters (and pods): when a carrier last dropped ore off
    public long? OreDroppedTick { get; set; }

    // Factories
    public List<ResearchKey> ProductionQueue { get; } = [];

    /// <summary>
    /// Who ordered each machine on <see cref="ProductionQueue"/>, kept only for a race of several players and only through
    /// <see cref="Enqueue"/> and <see cref="Dequeue"/>. Whenever it is not in step with the queue (a race of one player, or
    /// the queue changed another way), every machine on it is the race's first player's (<see cref="ProductionOwner"/>) until
    /// the queue is empty again: owners out of step are let go at the next change, so that they never fall back into step by
    /// chance and name the wrong machines.
    /// </summary>
    public List<PlayerId> ProductionOwners { get; } = [];

    /// <summary>Puts a machine at the end of the queue, for <paramref name="owner"/> when the race keeps owners (null when it does not).</summary>
    public void Enqueue(ResearchKey machine, PlayerId? owner)
    {
        // Owners out of step say nothing any more; an empty queue starts in step again.
        if (ProductionOwners.Count != ProductionQueue.Count)
            ProductionOwners.Clear();
        var inStep = ProductionOwners.Count == ProductionQueue.Count;
        ProductionQueue.Add(machine);
        if (owner is { } who && inStep)
            ProductionOwners.Add(who);
    }

    /// <summary>Takes the entry at <paramref name="index"/> off the queue, and its owner while the owners are in step (else all of them).</summary>
    public void Dequeue(int index)
    {
        var inStep = ProductionOwners.Count == ProductionQueue.Count;
        ProductionQueue.RemoveAt(index);
        if (inStep)
            ProductionOwners.RemoveAt(index);
        else
            ProductionOwners.Clear();
    }

    /// <summary>Whose the machine at <paramref name="index"/> of the queue is: its owner while the owners are in step, else the race's first player.</summary>
    public PlayerId ProductionOwner(int index) =>
        ProductionOwners.Count == ProductionQueue.Count && index >= 0 && index < ProductionOwners.Count ? ProductionOwners[index] : PlayerId.Of(Race);

    /// <summary>Where new machines gather (MachLogFactory::specifiedDeployPoint), if the player set one.</summary>
    public Vector2? AssemblyPoint { get; set; }
    public float ProductionSpent { get; set; }

    // Labs
    public List<ResearchKey> ResearchQueue { get; } = [];
    public List<int> Technicians { get; } = [];

    // Garrisons: machines inside, being repaired; labs: enemy spies inside, downloading research
    public List<int> Garrisoned { get; } = [];

    public string TypeName => Spec.TypeName;

    public bool IsType(string type, string? subType = null) => Spec.Type == type && (subType is null || Spec.SubType == subType);

    public override float Radius
    {
        get
        {
            var size = Footprint.Max - Footprint.Min;
            return MathF.Max(size.X, size.Y) / 2;
        }
    }

    public override int Cost => Stats.Cost;

    /// <summary>Adds BMUs to the build; hit points grow with progress, as in the original.</summary>
    public void AddConstruction(float bmu)
    {
        var wasComplete = Complete;
        Built = MathF.Min(Stats.Cost, Built + bmu);
        var fraction = Stats.Cost == 0 ? 1 : Built / Stats.Cost;
        if (!wasComplete)
            HitPoints = Math.Max(HitPoints, Math.Max(1, (int)(MaxHitPoints * fraction)));
    }

    public void MakeComplete()
    {
        Built = Stats.Cost;
        HitPoints = MaxHitPoints;
    }

    /// <summary>Distance from a point to the edge of the building (0 inside).</summary>
    public float DistanceFrom(Vector2 point)
    {
        var dx = MathF.Max(0, MathF.Max(Footprint.Min.X - point.X, point.X - Footprint.Max.X));
        var dy = MathF.Max(0, MathF.Max(Footprint.Min.Y - point.Y, point.Y - Footprint.Max.Y));
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static (Vector2, Vector2) LocalFootprint(ConstructionLevel stats, int orientation)
    {
        var boundary = stats.Geometry.FirstOrDefault(g => g.Keyword == "BOUNDARY");
        Vector2 min = new(-10, -10), max = new(10, 10);
        if (boundary is { Args.Count: 4 })
        {
            float F(int i) => float.Parse(boundary.Args[i], System.Globalization.CultureInfo.InvariantCulture);
            min = new(F(0), F(1));
            max = new(F(2), F(3));
        }

        // Rotate the local box by the building's quarter turns.
        var corners = new[] { min, max, new Vector2(min.X, max.Y), new Vector2(max.X, min.Y) }
            .Select(c => Rotate(c, orientation))
            .ToList();
        return (new(corners.Min(c => c.X), corners.Min(c => c.Y)), new(corners.Max(c => c.X), corners.Max(c => c.Y)));
    }

    private static Vector2 Rotate(Vector2 v, int degrees) => (((degrees % 360) + 360) % 360) switch
    {
        90 => new(-v.Y, v.X),
        180 => new(-v.X, -v.Y),
        270 => new(v.Y, -v.X),
        _ => v,
    };
}

/// <summary>
/// An active artefact (MachLogArtefact): a tree, rock, wreck, door or shield the scenario sets on the planet as ACTIVE.
/// It stands in the way, can be shot at and destroyed, and leaves wreckage worth its cost. It belongs to no race, so
/// it is kept apart from the races' actors and its <see cref="Actor.Race"/> means nothing; its id is
/// <see cref="IdBase"/> plus its place in the scenario's list, clear of the actors' ids.
/// </summary>
public sealed class Artefact : Actor
{
    public const int IdBase = 1 << 24;

    public Artefact(int placement, Vector2 position, ArtefactType type, int subType, IReadOnlyList<Vector2> boundary)
        : base(IdBase + placement, Race.Red, position)
    {
        Type = type;
        SubType = subType;
        Placement = placement;
        Boundary = boundary;
        MaxHitPoints = HitPoints = Math.Max(1, type.HitPoints ?? 1);
        MaxArmour = Armour = type.Armour ?? 0;
        Radius = boundary.Count > 0 ? MathF.Max(1, boundary.Max(p => Vector2.Distance(p, position))) : 1;
    }

    public ArtefactType Type { get; }

    /// <summary>Its type's place among the section's DUMB and ACTIVE types, counted from 0 as the original did.</summary>
    public int SubType { get; }

    /// <summary>Its line in the scenario's list of placements (what the view drew it from).</summary>
    public int Placement { get; }

    /// <summary>The ground it blocks, turned as it stands (empty for one without a boundary).</summary>
    public IReadOnlyList<Vector2> Boundary { get; }

    /// <summary>The scenario's ID clause for it (what ONDESTROY links and a deck's doors are named by), or null.</summary>
    public int? Tag { get; set; }

    public override float Radius { get; }

    public override int Cost => Type.Cost ?? 0;

    /// <summary>
    /// What its destruction does to others (ONDESTROY): their hit points and armour become the given values, or, where
    /// the scenario gave them negative, drop by that much (MachLogArtefact::damageLinkedArtefact).
    /// </summary>
    public List<(Artefact Other, int HitPoints, int Armour)> OnDestroy { get; } = [];
}

/// <summary>
/// The burning wreckage a destroyed machine or building leaves (MachLogDebris): BMUs that a scavenger can pick up
/// for its race until it burns out. It belongs to no one's forces and cannot be hit.
/// </summary>
public sealed class DebrisState(int id, Race race, Vector2 position, int quantity, (Vector2 Min, Vector2 Max) boundary, float createdAt, float expiresAt)
{
    public int Id { get; } = id;
    /// <summary>The race of what was destroyed (anyone's scavengers may take it).</summary>
    public Race Race { get; } = race;
    public Vector2 Position { get; } = position;
    public int Quantity { get; } = quantity;
    /// <summary>The ground the wreck covers: the building's footprint or the machine's box.</summary>
    public (Vector2 Min, Vector2 Max) Boundary { get; } = boundary;
    public float CreatedAt { get; } = createdAt;
    public float ExpiresAt { get; } = expiresAt;
}

/// <summary>
/// A mineral deposit. Sites are hidden until a locator finds them; once found, by whichever race, every race's
/// mines may dig there (MachLogMineralSite::hasBeenDiscovered).
/// </summary>
public sealed class MineralSiteState(int id, MineralSite source)
{
    public int Id { get; } = id;
    public int Grade { get; } = source.Grade;
    public Vector2 Position { get; } = new(source.Position.X, source.Position.Y);
    public float Ore { get; set; } = source.Ore;
    public HashSet<Race> DiscoveredBy { get; } = [];
}
