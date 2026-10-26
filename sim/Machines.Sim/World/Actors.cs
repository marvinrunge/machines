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
    public Race Race { get; set; }
    public Vector2 Position { get; set; }

    /// <summary>Height above the ground plane, as placed by the scenario. Only the client uses it.</summary>
    public float Height { get; set; }

    public int HitPoints { get; set; }
    public int MaxHitPoints { get; protected set; }
    public int Armour { get; set; }
    public int MaxArmour { get; protected set; }

    public bool IsDead => HitPoints <= 0;

    /// <summary>Distance from the centre at which the actor's body ends; used for ranges and spacing.</summary>
    public abstract float Radius { get; }

    /// <summary>BMU cost; what destroying it is worth to the score.</summary>
    public abstract int Cost { get; }

    /// <summary>The race that damaged it last, credited when it dies.</summary>
    public Race? LastHitBy { get; set; }

    /// <summary>Game time at which a started self-destruct goes off.</summary>
    public float? SelfDestructAt { get; set; }

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
        Spec = spec;
        Stats = stats;
        WeaponCombo = weaponCombo;
        MaxHitPoints = HitPoints = stats.HitPoints;
        MaxArmour = Armour = stats.Armour;
    }

    public MachineSpec Spec { get; }
    public MachineLevel Stats { get; }
    public string? WeaponCombo { get; }
    public List<WeaponMount> Weapons { get; } = [];

    public ResearchKey Key => new(Spec.Type, Spec.SubType, Stats.HardwareLevel, WeaponCombo);

    /// <summary>Compass heading in radians, 0 along +X.</summary>
    public float Facing { get; set; }

    public Order Order { get; set; } = Order.Idle;
    public List<Vector2> Path { get; } = [];
    public int PathIndex { get; set; }

    /// <summary>Ore on board (transporters).</summary>
    public int Ore { get; set; }

    /// <summary>AI squadron number, if any.</summary>
    public int? Squad { get; set; }

    /// <summary>The building or APC a machine is inside (a lab, a garrison, an APC); it is off the map.</summary>
    public int? Inside { get; set; }

    /// <summary>The building whose door the machine is walking through (on the way in).</summary>
    public int? Doorway { get; set; }

    /// <summary>For an APC: the machines it carries.</summary>
    public List<int> Passengers { get; } = [];

    /// <summary>True while a player drives this machine in first person.</summary>
    public bool DrivenByPlayer { get; set; }

    /// <summary>Fractional armour regrown but not yet added.</summary>
    public float ArmourRegen { get; set; }

    /// <summary>Carries a weapon that attacks: damage or treachery. Healers and strike launchers do not count.</summary>
    public bool CanFight => Weapons.Any(w => w.DealsDamage || w.IsTreachery);

    public bool CanHeal => Weapons.Any(w => w.IsHealer);

    public override float Radius => MathF.Max(1.5f, Stats.HighClearance);
    public override int Cost => Stats.Cost;

    public bool IsType(string type, string? subType = null) => Spec.Type == type && (subType is null || Spec.SubType == subType);
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
        }
    }

    /// <summary>Where machines come out of the building: a factory's pad before its door.</summary>
    public Vector2? EntranceOutside { get; }

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

    /// <summary>The AI builds it again where it stood when it is destroyed (<c>REBUILD</c> in the scenario).</summary>
    public bool Rebuild { get; set; }

    // Mines
    public int? MineralSite { get; set; }
    public float Ore { get; set; }

    // Smelters (and pods): when a carrier last dropped ore off
    public long? OreDroppedTick { get; set; }

    // Factories
    public List<ResearchKey> ProductionQueue { get; } = [];

    /// <summary>Where new machines gather (MachLogFactory::specifiedDeployPoint), if the player set one.</summary>
    public Vector2? AssemblyPoint { get; set; }
    public float ProductionSpent { get; set; }

    // Labs
    public List<ResearchKey> ResearchQueue { get; } = [];
    public List<int> Technicians { get; } = [];

    // Garrisons: machines inside, being repaired
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

/// <summary>A mineral deposit. Sites are hidden from a race until one of its locators finds them.</summary>
public sealed class MineralSiteState(int id, MineralSite source)
{
    public int Id { get; } = id;
    public int Grade { get; } = source.Grade;
    public Vector2 Position { get; } = new(source.Position.X, source.Position.Y);
    public float Ore { get; set; } = source.Ore;
    public HashSet<Race> DiscoveredBy { get; } = [];
    public int? Mine { get; set; }
}
