using Machines.Sim.Combat;

namespace Machines.Game;

// What a Wired hero's model is put together from, as plain names and indices: nothing here touches Godot, so the
// tests can compile this file and run it over the real rigs. HeroSeat says where the weapons sit and HeroRig does the
// rest. Wired is the remake's own addition.

/// <summary>A part of a rig as far as putting rigs together needs it: what it hangs from and what it is called.</summary>
public readonly record struct RigPart(int Parent, string Name);

/// <summary>
/// Where a weapon's parts come from: a machine exported with it, and the mount it stands on there.
/// <paramref name="Mirrored"/> turns a weapon that leans to one side over for the other (see <see cref="HeroDonors.For"/>).
/// </summary>
public sealed record HeroDonor(string Model, Mounting Mount, bool Mirrored = false);

/// <summary>Which real rigs a Wired hero is made of: each chassis' own, and for each weapon the machines exported with it.</summary>
public static class HeroDonors
{
    /// <summary>The model of each chassis of the catalogue. The weapons it was exported with come off again.</summary>
    public static readonly IReadOnlyDictionary<string, string> Chassis = new Dictionary<string, string>
    {
        ["grunt1"] = "AGGRESSOR_1_GRUNT_R_BOLTER",
        ["grunt2"] = "AGGRESSOR_2_GRUNT_L_BOLTER",
        ["grunt3"] = "AGGRESSOR_3_GRUNT_LR_HEAVY_BOLTER1_X2",
        ["reaper2"] = "AGGRESSOR_2_ASSASSIN_L_PLASMA_RIFLE",
        ["reaper3"] = "AGGRESSOR_3_ASSASSIN_LR_PLASMA_RIFLE_X2",
        ["overseer2"] = "ADMINISTRATOR_2_OVERSEER_LR_PULSE_RIFLE_X2",
        ["overseer3"] = "ADMINISTRATOR_3_OVERSEER_LR_PULSE_RIFLE_X2",
        ["knight3"] = "AGGRESSOR_3_KNIGHT_LR_PLASMA_CANNON1_X2",
        ["knight4"] = "AGGRESSOR_4_KNIGHT_LR_PLASMA_CANNON2_X2",
        ["knight5"] = "AGGRESSOR_5_KNIGHT_LRT_PLASMA_CANNON2_X3",
        ["commander4"] = "ADMINISTRATOR_4_COMMANDER_LR_MULTI_LAUNCHER5_X2",
        ["commander5"] = "ADMINISTRATOR_5_COMMANDER_LR_PULSE_CANNON_X2",
        ["ballista3"] = "AGGRESSOR_3_BALLISTA_T_MULTI_LAUNCHER1",
        ["ballista4"] = "AGGRESSOR_4_BALLISTA_LR_MULTI_LAUNCHER2_X2",
        ["boss1"] = "ADMINISTRATOR_1_BOSS_T_WASP_LIGHT_STING",
        ["boss2"] = "ADMINISTRATOR_2_BOSS_T_BEE_BOMB",
        ["gorilla5"] = "AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7",
    };

    private const Mounting L = Mounting.Left, R = Mounting.Right, T = Mounting.Top;

    /// <summary>The machines exported with each weapon of the catalogue and the mounts they carry it on, the first asked first.</summary>
    private static readonly Dictionary<string, (string Model, Mounting[] Mounts)[]> Carriers = new()
    {
        ["BOLTER"] = [("AGGRESSOR_2_GRUNT_L_BOLTER", [L]), ("AGGRESSOR_1_GRUNT_R_BOLTER", [R])],
        ["AUTO_CANNON"] = [("AGGRESSOR_3_ASSASSIN_LR_AUTO_CANNON_X2", [L, R])],
        ["ELECTRIC_CHARGE"] = [("AGGRESSOR_2_GRUNT_L_ELECTRIC_CHARGE", [L]), ("AGGRESSOR_1_GRUNT_R_ELECTRIC_CHARGE", [R])],
        ["FLAME_THROWER2"] = [("AGGRESSOR_2_GRUNT_L_FLAME_THROWER2", [L])],
        ["PULSE_RIFLE"] = [("ADMINISTRATOR_2_OVERSEER_LR_PULSE_RIFLE_X2", [L, R])],
        ["PLASMA_RIFLE"] = [("AGGRESSOR_3_ASSASSIN_LR_PLASMA_RIFLE_X2", [L, R])],
        ["HEAVY_BOLTER1"] = [("AGGRESSOR_3_GRUNT_LR_HEAVY_BOLTER1_X2", [L, R])],
        ["PLASMA_CANNON1"] = [("AGGRESSOR_3_KNIGHT_LR_PLASMA_CANNON1_X2", [L, R])],
        ["MULTI_LAUNCHER3"] = [("AGGRESSOR_4_KNIGHT_LR_MULTI_LAUNCHER3_X2", [L, R])],
        ["MULTI_LAUNCHER5"] = [("ADMINISTRATOR_4_COMMANDER_LR_MULTI_LAUNCHER5_X2", [L, R])],
        ["HEAVY_BOLTER2"] = [("AGGRESSOR_3_KNIGHT_LR_HEAVY_BOLTER2_X2", [L, R])],
        ["PULSE_CANNON"] = [("ADMINISTRATOR_5_COMMANDER_LR_PULSE_CANNON_X2", [L, R])],
        ["MULTI_LAUNCHER4"] = [("AGGRESSOR_5_KNIGHT_LRT_MULTI_LAUNCHER4_X3", [L, R, T])],
        ["PLASMA_CANNON2"] = [("AGGRESSOR_5_KNIGHT_LRT_PLASMA_CANNON2_X3", [L, R, T])],
        ["MULTI_LAUNCHER1"] = [("AGGRESSOR_3_BALLISTA_T_MULTI_LAUNCHER1", [T])],
        ["MULTI_LAUNCHER7"] = [("AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7", [L, R])],
        ["MULTI_LAUNCHER2"] = [("AGGRESSOR_4_BALLISTA_LR_MULTI_LAUNCHER2_X2", [L, R])],
        ["WASP_LIGHT_STING"] = [("ADMINISTRATOR_1_BOSS_T_WASP_LIGHT_STING", [T])],
        ["WASP_METAL_STING"] = [("ADMINISTRATOR_1_BOSS_T_WASP_METAL_STING", [T])],
        ["BEE_BOMB"] = [("ADMINISTRATOR_2_BOSS_T_BEE_BOMB", [T])],
    };

    /// <summary>Weapons built off centre, away from their mount's middle: on the other side they are turned over.</summary>
    private static readonly HashSet<string> Leaning = ["FLAME_THROWER2"];

    public static IEnumerable<string> Weapons => Carriers.Keys;

    /// <summary>Whether a weapon's own shape decides how far it stands from the body: it reaches over it.</summary>
    public static bool Leans(string weapon) => Leaning.Contains(weapon);

    /// <summary>Whether a weapon comes from a machine of this very chassis, on this mount: it needs no seat, it stands as exported.</summary>
    public static bool AtHome(string chassis, HeroDonor donor, Mounting mounting) =>
        donor.Mount == mounting && Chassis.TryGetValue(chassis, out var model) && Machine(model) == Machine(donor.Model);

    // TYPE_LEVEL_SUBTYPE, the part of a model's name before its weapons.
    private static string Machine(string model) => string.Join('_', model.Split('_').Take(3));

    /// <summary>Whether a chassis' mounts lie inside its body: the Ballista 4 sinks its great launchers into its dome, anything smaller would vanish.</summary>
    public static bool Sinks(string chassis) => chassis == "ballista4";

    /// <summary>Whether a chassis' mount hangs under its body: the Bee's holds a bomb, anything else rides on its back, where it shows.</summary>
    public static bool Rides(string chassis) => chassis == "boss2";

    /// <summary>
    /// Where a chassis' shoulder lies in its body, if it has one on one side only: from how far off the centre line, and up to
    /// what height, above which the hull's rim begins. The Reaper 2's gold pod holds its gun on the left; only the HD model has
    /// one on the right too, the others a small cone at the antenna's foot.
    /// </summary>
    public static (float Start, float Top)? Shoulder(string chassis) => chassis == "reaper2" ? (0.7f, 1.25f) : null;

    /// <summary>Whether a weapon loses its HD model's round foot here: it was made for its own machine's roof, and the other looks have none.</summary>
    public static bool Footless(string chassis, HeroWorn worn) =>
        worn.Weapon is "MULTI_LAUNCHER1" or "MULTI_LAUNCHER2" && !AtHome(chassis, worn.Donor, worn.Side);

    /// <summary>
    /// The size a weapon is shown at: the Ballista 4's great launcher comes smaller on anything lesser than itself,
    /// and the flame thrower with its tank on the Ballista 3, whose roof is shorter than it.
    /// </summary>
    public static float Scale(string chassis, string weapon) => (chassis, weapon) switch
    {
        ("ballista3", "MULTI_LAUNCHER2") => 0.7f,
        ("knight4" or "knight5" or "commander5", "MULTI_LAUNCHER2") => 0.6f,
        ("commander4", "MULTI_LAUNCHER2") => 0.5f,
        ("ballista3", "FLAME_THROWER2") => 0.75f,
        _ => 1,
    };

    /// <summary>
    /// Where the weapon for <paramref name="mounting"/> comes from: the same side where a machine carries it there, else
    /// the other. The original hung one model on either side, so that changes nothing but in the HD look, whose left and
    /// right weapons can differ. Null for a weapon no export carries.
    /// </summary>
    public static HeroDonor? For(string weapon, Mounting mounting)
    {
        if (!Carriers.TryGetValue(weapon, out var carriers))
            return null;
        foreach (var (model, mounts) in carriers)
            if (mounts.Contains(mounting))
                return new HeroDonor(model, mounting);
        var (first, sides) = carriers[0];
        return new HeroDonor(first, sides[0], Leaning.Contains(weapon) && mounting != Mounting.Top && mounting != sides[0]);
    }
}

/// <summary>What a hero's guns do in the view.</summary>
public static class HeroGuns
{
    // How long after a burst a round may still be on its way to the view, in seconds.
    private const float Slack = 0.25f;

    /// <summary>
    /// Those of a hero's twin guns a round can have left. Its driver fires them one by one and a round does not say which
    /// it came from, but only a gun in its burst has any left: the other stays cold. None in a burst, they all count.
    /// </summary>
    public static List<WeaponMount> Firing(IReadOnlyList<WeaponMount> twins)
    {
        var firing = twins.Where(w => w.RoundsLeft > 0 || w.Reload > 0 && w.Spec.ReloadTime - w.Reload <= w.Spec.BurstDuration + Slack).ToList();
        return firing.Count > 0 ? firing : twins.ToList();
    }
}

/// <summary>
/// A chassis' rig and the weapons' rigs put together, as indices: which part of which rig each new part is, what it
/// hangs from, and where every old index went. A mount is <c>l_gun</c>, <c>r_gun</c> and <c>gun</c> or <c>t_gun</c>, and
/// a weapon everything on its mount: the mount's mesh and the parts below it.
/// </summary>
public sealed class RigGraft
{
    /// <summary>
    /// A part of the new rig. <paramref name="Chassis"/> is its index in the chassis' rig, or -1; for a mount the chassis
    /// lacks, <paramref name="Opposite"/> is the mount on the other side to mirror. <paramref name="Stripped"/> marks a
    /// mount that lost what it carried, its mesh too. <paramref name="Weapon"/> and <paramref name="Donor"/> name the
    /// weapon's rig and the part of it: for a mount, the donor's mount, whose mesh it takes.
    /// </summary>
    public sealed record Node(int Parent, string Name, int Chassis, bool Stripped = false, Mounting? Weapon = null, int Donor = -1, int Opposite = -1);

    /// <summary>A weapon's rig and its mount in it.</summary>
    public sealed record Source(IReadOnlyList<RigPart> Parts, int Mount);

    public IReadOnlyList<Node> Nodes => _nodes;

    private readonly List<Node> _nodes = [];
    private readonly int[] _chassis;
    private readonly Dictionary<Mounting, int[]> _donors = [];

    // Parts of a chassis that hang from a mount without being the weapon: the Grunt 3's shoulders turn with its guns.
    private static bool OfTheChassis(string name) => name.EndsWith("_shoulder", StringComparison.Ordinal);

    /// <summary>The mount of a side in a rig, or -1. What hangs from another mount is no mount (the autocannon's "gun").</summary>
    public static int MountOf(IReadOnlyList<RigPart> parts, Mounting mounting)
    {
        for (var i = 0; i < parts.Count; i++)
            if (IsMount(parts[i].Name, mounting) && !UnderMount(parts, i))
                return i;
        return -1;
    }

    private static bool IsMount(string name, Mounting mounting) => mounting switch
    {
        Mounting.Left => name == "l_gun",
        Mounting.Right => name == "r_gun",
        _ => name is "gun" or "t_gun",
    };

    private static bool IsMount(string name) => name is "l_gun" or "r_gun" or "gun" or "t_gun";

    private static bool UnderMount(IReadOnlyList<RigPart> parts, int part)
    {
        for (var p = parts[part].Parent; p >= 0 && p < parts.Count; p = parts[p].Parent == p ? -1 : parts[p].Parent)
            if (IsMount(parts[p].Name))
                return true;
        return false;
    }

    /// <summary>The parts hanging from <paramref name="root"/>, however deep, in rig order.</summary>
    private static List<int> Below(IReadOnlyList<RigPart> parts, int root)
    {
        bool Under(int part)
        {
            for (var p = parts[part].Parent; p >= 0 && p < parts.Count; p = parts[p].Parent == p ? -1 : parts[p].Parent)
                if (p == root)
                    return true;
            return false;
        }
        return Enumerable.Range(0, parts.Count).Where(Under).ToList();
    }

    /// <summary>What a mount carries as a weapon: itself and what hangs from it, without the chassis' own parts there.</summary>
    public static IEnumerable<int> Carried(IReadOnlyList<RigPart> parts, int mount) =>
        Below(parts, mount).Where(p => !OfTheChassis(parts[p].Name)).Prepend(mount);

    /// <param name="chassis">The chassis' rig as exported, with the weapons it came with.</param>
    /// <param name="slots">The mounts to strip, each with what goes on it, or null for nothing.</param>
    public RigGraft(IReadOnlyList<RigPart> chassis, IReadOnlyDictionary<Mounting, Source?> slots)
    {
        // What comes off: everything below a mount but the chassis' own parts there.
        var gone = new HashSet<int>();
        var mounts = slots.Keys.ToDictionary(m => m, m => MountOf(chassis, m));
        foreach (var mount in mounts.Values.Where(m => m >= 0))
            gone.UnionWith(Below(chassis, mount).Where(p => !OfTheChassis(chassis[p].Name)));

        _chassis = new int[chassis.Count];
        for (var i = 0; i < chassis.Count; i++)
            _chassis[i] = gone.Contains(i) ? -1 : i - gone.Count(g => g < i);

        for (var i = 0; i < chassis.Count; i++)
        {
            if (gone.Contains(i))
                continue;
            var parent = chassis[i].Parent is >= 0 and var p && p < chassis.Count ? _chassis[p] : chassis[i].Parent;
            var side = mounts.Where(m => m.Value == i).Select(m => (Mounting?)m.Key).FirstOrDefault();
            var source = side is { } mounted ? slots[mounted] : null;
            _nodes.Add(new Node(parent, chassis[i].Name, i, side is not null, source is null ? null : side, source?.Mount ?? -1));
        }

        // A mount the chassis lacks stands where the opposite one would: on the same parent, mirrored.
        foreach (var (side, mount) in mounts.Where(m => m.Value < 0).OrderBy(m => m.Key))
        {
            var opposite = side switch { Mounting.Left => Mounting.Right, Mounting.Right => Mounting.Left, _ => side };
            var other = MountOf(chassis, opposite);
            if (other < 0)
                continue;
            mounts[side] = _nodes.Count;
            _nodes.Add(new Node(_chassis[chassis[other].Parent], side == Mounting.Left ? "l_gun" : "r_gun", -1, true, slots[side] is null ? null : side,
                slots[side]?.Mount ?? -1, other));
        }

        foreach (var (side, source) in slots.OrderBy(s => s.Key))
        {
            if (source is null)
                continue;
            var map = new int[source.Parts.Count];
            Array.Fill(map, -1);
            _donors[side] = map;
            var mount = _nodes.FindIndex(n => n.Weapon == side && n.Donor == source.Mount);
            if (mount < 0)
                continue;
            map[source.Mount] = mount;
            var parts = Below(source.Parts, source.Mount).Where(p => !OfTheChassis(source.Parts[p].Name)).ToList();
            foreach (var part in parts)
                map[part] = _nodes.Count + parts.IndexOf(part);
            foreach (var part in parts)
            {
                // What hung from a shoulder that stayed behind hangs from the mount.
                var parent = source.Parts[part].Parent;
                var name = source.Parts[part].Name;
                _nodes.Add(new Node(map[parent] >= 0 ? map[parent] : mount, IsMount(name) ? "barrels" : name, -1, false, side, part));
            }
        }
    }

    /// <summary>Where a part of the chassis' rig went; -1 for one that came off, and for -1.</summary>
    public int Chassis(int part) => part >= 0 && part < _chassis.Length ? _chassis[part] : -1;

    /// <summary>Where a part of the weapon's rig on <paramref name="mounting"/> went; -1 for what was left behind.</summary>
    public int Weapon(Mounting mounting, int part) =>
        _donors.TryGetValue(mounting, out var map) && part >= 0 && part < map.Length ? map[part] : -1;

    /// <summary>The new mount of a side, or -1.</summary>
    public int Mount(Mounting mounting) => _nodes.FindIndex(n => IsMount(n.Name, mounting) && !UnderMount(n));

    private bool UnderMount(Node node)
    {
        for (var p = node.Parent; p >= 0; p = _nodes[p].Parent)
            if (IsMount(_nodes[p].Name))
                return true;
        return false;
    }
}
