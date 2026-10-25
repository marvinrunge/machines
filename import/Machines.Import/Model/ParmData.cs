namespace Machines.Import.Model;

/// <summary>Everything in <c>parmdata.dat</c>: the game's unit, building and weapon balance.</summary>
public sealed record ParmData(
    IReadOnlyList<EvasionStrategy> EvasionStrategies,
    IReadOnlyList<MachineSpec> Machines,
    IReadOnlyList<ConstructionSpec> Constructions,
    IReadOnlyList<WeaponSpec> Weapons,
    GeneralData General)
{
    /// <param name="typeName">The original type name, e.g. <c>"AGGRESSOR GRUNT"</c> or <c>"APC"</c>.</param>
    public MachineSpec Machine(string typeName) =>
        Machines.FirstOrDefault(m => m.TypeName == typeName)
        ?? throw new KeyNotFoundException($"no machine '{typeName}'");

    public ConstructionSpec Construction(string typeName) =>
        Constructions.FirstOrDefault(c => c.TypeName == typeName)
        ?? throw new KeyNotFoundException($"no construction '{typeName}'");

    public WeaponSpec Weapon(string typeName) =>
        Weapons.FirstOrDefault(w => w.TypeName == typeName)
        ?? throw new KeyNotFoundException($"no weapon '{typeName}'");

    public EvasionStrategy Evasion(string name) =>
        EvasionStrategies.FirstOrDefault(e => e.Name == name)
        ?? throw new KeyNotFoundException($"no evasion strategy '{name}'");
}

/// <summary>
/// Where a damaged machine runs to. A lower priority number is preferred;
/// -1 means never considered, -2 on garrisons means the machine never evades.
/// </summary>
public sealed record EvasionStrategy(
    string Name,
    int GarrisonsPriority,
    int AggressivesPriority,
    int TurretsPriority,
    int PodPriority);

/// <summary>The <c>GENERAL_DATA</c> block. Angles and rates are in degrees.</summary>
/// <param name="FirstPersonLookMinAngle">Furthest look up; negative angles look up.</param>
/// <param name="FirstPersonLookMaxAngle">Furthest look down.</param>
public sealed record GeneralData(
    float FirstPersonLookUpDownRate,
    float FirstPersonLookMinAngle,
    float FirstPersonLookMaxAngle,
    float FirstPersonMaxFastTurnRate,
    float FirstPersonMaxSlowTurnRate,
    float StartRotationDistance,
    float MaxMoveGradient,
    float MaxTerrainUnevenness,
    float VirtualDefconInterval,
    int PcPathFindingPriority);
