using Machines.Sim.World;

namespace Machines.Sim.Presentation;

/// <summary>
/// Which original sound goes with which weapon, explosion, machine and report, as machphys and machlog chose
/// them (bolter.cpp, pulsewep.cpp, plasmawe.cpp, mulaunch.cpp, machexp.cpp, objexplo.cpp, machine.cpp,
/// machvman.cpp). Names are the ids of <c>sounds/snddef64.dat</c> and <c>sounds/vemail/vemail.dat</c>.
/// </summary>
public static class SoundTable
{
    /// <summary>The voice-mail name of a machine type: <c>GRUNT</c>, <c>DOZER</c>, <c>GEO</c> ...</summary>
    public static string? VoiceOf(Machine machine) => VoiceOf(machine.Spec.Type, machine.Spec.SubType);

    public static string? VoiceOf(string type, string? subType) => type switch
    {
        "AGGRESSOR" or "ADMINISTRATOR" => subType,
        "TECHNICIAN" => "TECHNICIAN",
        "GEO_LOCATOR" => "GEO",
        "SPY_LOCATOR" => "SPY",
        "CONSTRUCTOR" => "DOZER",
        "RESOURCE_CARRIER" => "RESOURCE_CARRIER",
        "APC" => "APC",
        _ => null,
    };

    /// <summary>The firing sound of a weapon (machphys: bolter, pulsewep, plasmawe, mulaunch, ...).</summary>
    public static string? FiringSound(string weapon) => weapon switch
    {
        "BOLTER" => "SID_BOLTER",
        "HEAVY_BOLTER1" or "HEAVY_BOLTER2" => "SID_HEAVYBOLTER",
        "AUTO_CANNON" => "SID_AUTOCANNON",
        "PULSE_RIFLE" => "SID_PULSERIFLE",
        "PULSE_CANNON" => "SID_PULSECANNON",
        "PLASMA_RIFLE" => "SID_PLASMARIFLE",
        "PLASMA_CANNON1" or "PLASMA_CANNON2" => "SID_PLASMACANNON",
        "FLAME_THROWER1" or "FLAME_THROWER2" => "SID_FLAMETHROWER",
        "ELECTRIC_CHARGE" => "SID_ELECTRICCHARGE",
        "SUPERCHARGE_ADVANCED" or "SUPERCHARGE_SUPER" => "SID_SUPERHEAL",
        "MULTI_LAUNCHER1" => "SID_MLAUNCH1",
        "MULTI_LAUNCHER2" => "SID_MLAUNCH2",
        "MULTI_LAUNCHER3" => "SID_MLAUNCH3",
        "MULTI_LAUNCHER4" => "SID_MLAUNCH4",
        "MULTI_LAUNCHER5" => "SID_MLAUNCH5",
        "MULTI_LAUNCHER6" => "SID_MLAUNCH6",
        "MULTI_LAUNCHER7" => "SID_MLAUNCH7",
        "LARGE_MISSILE" => "SID_LARGEMLAUNCH",
        "ORB_OF_TREACHERY" => "SID_TREACHERY",
        "BEE_BOMB" => "SID_BEE_BOMB",
        "WASP_LIGHT_STING" => "SID_WASP_STING_LIGHT",
        "WASP_METAL_STING" => "SID_WASP_STING_METAL",
        "GORILLA_PUNCH" => "SID_PUNCH",
        "NUCLEAR_MISSILE" => "SID_NUKEMLAUNCH",
        _ => null,
    };

    /// <summary>The sound where a round lands, when the weapon has one; missiles use one of four explosions (1 to 4).</summary>
    public static string? HitSound(string weapon, int variant) => weapon switch
    {
        "BOLTER" or "HEAVY_BOLTER1" or "HEAVY_BOLTER2" or "AUTO_CANNON" => "SID_BOLTER_HIT",
        "PULSE_RIFLE" or "PULSE_CANNON" or "PLASMA_RIFLE" or "PLASMA_CANNON1" or "PLASMA_CANNON2" => "SID_PUCHIT",
        _ when weapon.StartsWith("MULTI_LAUNCHER", StringComparison.Ordinal) || weapon == "LARGE_MISSILE"
            => $"SID_XPLODE{Math.Clamp(variant, 1, 4)}_MISSILE",
        _ => null,
    };

    /// <summary>The sound of an area weapon going off.</summary>
    public static string? BlastSound(string weapon) => weapon switch
    {
        "VORTEX" => "SID_VORTEX",
        "ION_ORBITAL_CANNON" => "SID_IONCANNON",
        "NUCLEAR_MISSILE" => "SID_NUKE",
        "BEE_BOMB" => "SID_BEE_DETONATE",
        _ => null,
    };

    /// <summary>A destroyed machine: one of six fireballs, 1 to 6 (machexp.cpp); a building by its size (objexplo.cpp).</summary>
    public static string ExplosionSound(bool building, float size, int variant) => building
        ? size > 40 ? "SID_XPLODE_CONSTR_LARGE" : "SID_XPLODE_CONSTR_SMALL"
        : $"SID_XPLODE{Math.Clamp(variant, 1, 6)}_MACHINE";

    /// <summary>MachPhysMachine::getLocomotionSounds: (movement, idle) by type, subtype and hardware level.</summary>
    public static (string? Move, string? Idle) LocomotionSounds(string type, string? subType, int level)
    {
        const string wheelS = "WHEEL_{0}_SMALL", wheelL = "WHEEL_{0}_LARGE", trackS = "TRACK_{0}_SMALL", trackL = "TRACK_{0}_LARGE";
        const string spiderS = "SPIDER_{0}_SMALL", spiderL = "SPIDER_{0}_LARGE", spiderXL = "SPIDER_{0}_VERY_LARGE";
        const string hoverS = "HOVER_{0}_SMALL", hoverL = "HOVER_{0}_LARGE";
        static (string?, string?) Pair(string family) =>
            ("SID_" + string.Format(family, "MOVEMENT"), "SID_" + string.Format(family, "IDLE"));
        static (string?, string?) Fly(string idle) => ("SID_FLY_BLAST", idle);

        return (type, subType, level) switch
        {
            ("AGGRESSOR", "GRUNT", 1) => Pair(wheelS),
            ("AGGRESSOR", "GRUNT", _) => Pair(trackS),
            ("AGGRESSOR", "ASSASSIN", _) => Pair(spiderL),
            ("AGGRESSOR", "KNIGHT", 3) => Pair(trackL),
            ("AGGRESSOR", "KNIGHT", _) => Pair(hoverL),
            ("AGGRESSOR", "BALLISTA", 3) => Pair(wheelL),
            ("AGGRESSOR", "BALLISTA", _) => Pair(trackL),
            ("AGGRESSOR", "NINJA", _) => ("SID_NINJA_MOVEMENT", "SID_NINJA_IDLE"),
            ("CONSTRUCTOR", "BEHEMOTH", 4) => Pair(wheelL),
            ("CONSTRUCTOR", "BEHEMOTH", _) => Pair(trackL),
            ("CONSTRUCTOR", _, _) => Pair(trackS),
            ("ADMINISTRATOR", "BOSS", 1) => Fly("SID_WASP_FLY"),
            ("ADMINISTRATOR", "BOSS", _) => Fly("SID_BEE_FLY"),
            ("ADMINISTRATOR", "OVERSEER", 2) => Pair(trackL),
            ("ADMINISTRATOR", "OVERSEER", _) => Pair(spiderXL),
            ("ADMINISTRATOR", "COMMANDER", _) => Pair(hoverL),
            ("TECHNICIAN", "LAB_TECH", _) => Pair(wheelS),
            ("TECHNICIAN", "TECH_BOY", _) => Pair(trackS),
            ("TECHNICIAN", _, _) => Pair(hoverS),
            ("RESOURCE_CARRIER", _, 1) => Pair(wheelS),
            ("RESOURCE_CARRIER", _, 2) => Pair(trackS),
            ("RESOURCE_CARRIER", _, 3) => Pair(spiderXL),
            ("RESOURCE_CARRIER", _, 4) => Pair(hoverL),
            ("RESOURCE_CARRIER", _, _) => Pair(trackL),
            ("APC", _, 2) => Pair(trackL),
            ("APC", _, 4) => Fly("SID_APC_FLY_LARGE"),
            ("APC", _, _) => Fly("SID_APC_FLY_SMALL"),
            ("GEO_LOCATOR", _, 1) => Pair(spiderS),
            ("GEO_LOCATOR", _, 2) => Pair(wheelS),
            ("GEO_LOCATOR", _, _) => Fly("SID_GEO_FLY"),
            ("SPY_LOCATOR", _, _) => Pair(hoverS),
            _ => (null, null),
        };
    }
}
