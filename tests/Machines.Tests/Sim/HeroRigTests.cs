using System.Numerics;
using System.Text.Json;
using Machines.Game;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Tests.Sim;

/// <summary>
/// The names and indices a Wired hero's model is put together by, and where its weapons sit on the real meshes of each
/// look (game/scripts/HeroParts.cs and HeroSeat.cs, compiled in here: they know nothing of Godot). How it looks is seen in
/// pictures, MACHINES_HERO_SHOT. Reading the exports runs alone: beside it the network tests, which go by the clock, miss
/// their time now and then.
/// </summary>
[Collection(nameof(HeroRigTests))]
public class HeroRigTests
{
    private static string Exports => Path.Combine(Path.GetDirectoryName(RepoPaths.OriginalDataFolder().TrimEnd('/', '\\'))!, "..", "classic");

    private static string Models => Path.Combine(Exports, "models", "red");

    private static bool Exported => Directory.Exists(Models);

    // Where each look finds a rig, the first asked first (ClassicAssets).
    private static readonly Dictionary<string, string[]> Looks = new()
    {
        ["Classic"] = ["models"],
        ["Overhauled"] = [Path.Combine("remake", "models"), "models"],
        ["Hd"] = [Path.Combine("meshy", "models"), Path.Combine("remake", "models"), "models"],
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), RigShape[]> Shapes = new();

    // A rig as seats are worked out from it: as HeroRig has it from the Godot meshes, the exporter's y and z swapped.
    private static RigShape[] Shape(string look, string model) => Shapes.GetOrAdd((look, model), _ =>
    {
        var file = Looks[look].Select(folder => Path.Combine(Exports, folder, "red", model + ".json")).First(File.Exists);
        using var json = JsonDocument.Parse(File.ReadAllBytes(file));
        return json.RootElement.GetProperty("rig").GetProperty("nodes").EnumerateArray().Select(n =>
        {
            var v = n.GetProperty("transform").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var rest = new Matrix4x4(v[0], v[2], v[1], 0, v[6], v[8], v[7], 0, v[3], v[5], v[4], 0, v[9], v[11], v[10], 1);
            var surfaces = n.TryGetProperty("parts", out var parts) ? parts.EnumerateArray().Select(p =>
            {
                var c = p.GetProperty("positions").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                return Enumerable.Range(0, c.Length / 3).Select(i => new Vector3(c[i * 3], c[i * 3 + 2], c[i * 3 + 1])).ToArray();
            }).ToArray() : [];
            return new RigShape(n.GetProperty("parent").GetInt32(), n.GetProperty("name").GetString() ?? "", rest, surfaces);
        }).ToArray();
    });

    private static HeroWorn Wearing(string look, string weapon, Mounting side)
    {
        var donor = HeroDonors.For(weapon, side)!;
        var rig = Shape(look, donor.Model);
        return new HeroWorn(side, weapon, donor, rig, RigGraft.MountOf(rig.Select(p => new RigPart(p.Parent, p.Name)).ToArray(), donor.Mount));
    }

    private static (RigShape[] Body, HeroSolid Solid) Chassis(string look, string chassis)
    {
        var body = Shape(look, HeroDonors.Chassis[chassis]);
        return (body, HeroSeat.Solid(chassis, body));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RigPart[]> Rigs = new();

    // Read once: the exports hold every vertex.
    private static RigPart[] Rig(string model) => Rigs.GetOrAdd(model, _ =>
    {
        using var file = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Models, model + ".json")));
        return file.RootElement.GetProperty("rig").GetProperty("nodes").EnumerateArray()
            .Select(n => new RigPart(n.GetProperty("parent").GetInt32(), n.GetProperty("name").GetString() ?? "")).ToArray();
    });

    private static RigPart[] Parts(params (int Parent, string Name)[] parts) => parts.Select(p => new RigPart(p.Parent, p.Name)).ToArray();

    private static readonly RigPart[] Walker = Parts((-1, "root"), (0, "hip"), (1, "l_gun"), (2, "bolt"), (1, "head"), (2, "recoil"));

    private static readonly RigPart[] Gun = Parts((-1, "root"), (0, "hip"), (1, "r_gun"), (2, "barrel"), (3, "tip"));

    private static void Sound(RigGraft graft)
    {
        for (var i = 0; i < graft.Nodes.Count; i++)
        {
            var seen = new HashSet<int>();
            for (var p = i; p >= 0; p = graft.Nodes[p].Parent)
            {
                Assert.True(p < graft.Nodes.Count, $"{graft.Nodes[i].Name} hangs from nothing");
                Assert.True(seen.Add(p), $"{graft.Nodes[i].Name} hangs from itself");
            }
        }
    }

    [Fact]
    public void A_mount_loses_what_it_carried_and_the_indices_close_up()
    {
        var graft = new RigGraft(Walker, new Dictionary<Mounting, RigGraft.Source?> { [Mounting.Left] = null });

        Assert.Equal(["root", "hip", "l_gun", "head"], graft.Nodes.Select(n => n.Name));
        Assert.True(graft.Nodes[2].Stripped);
        Assert.Equal([0, 1, 2, -1, 3, -1], Enumerable.Range(0, Walker.Length).Select(graft.Chassis));
        Assert.Equal(1, graft.Nodes[3].Parent);
        Assert.Equal(-1, graft.Chassis(-1));
        Sound(graft);
    }

    [Fact]
    public void A_weapon_comes_with_its_parts_and_their_indices()
    {
        var graft = new RigGraft(Walker, new Dictionary<Mounting, RigGraft.Source?> { [Mounting.Left] = new(Gun, 2) });

        Assert.Equal(["root", "hip", "l_gun", "head", "barrel", "tip"], graft.Nodes.Select(n => n.Name));
        var mount = graft.Nodes[2];
        Assert.Equal((Mounting.Left, 2), (mount.Weapon!.Value, mount.Donor));
        Assert.Equal(2, graft.Weapon(Mounting.Left, 2));
        Assert.Equal(4, graft.Weapon(Mounting.Left, 3));
        Assert.Equal(5, graft.Weapon(Mounting.Left, 4));
        Assert.Equal(-1, graft.Weapon(Mounting.Left, 1));
        Assert.Equal(-1, graft.Weapon(Mounting.Right, 3));
        Assert.Equal(2, graft.Nodes[4].Parent);
        Assert.Equal(4, graft.Nodes[5].Parent);
        Sound(graft);
    }

    [Fact]
    public void The_chassis_keeps_its_shoulders_on_a_mount()
    {
        var grunt = Parts((-1, "root"), (0, "body"), (1, "r_gun"), (2, "r_shoulder"), (2, "bolt"));
        var graft = new RigGraft(grunt, new Dictionary<Mounting, RigGraft.Source?> { [Mounting.Right] = null });

        Assert.Equal(["root", "body", "r_gun", "r_shoulder"], graft.Nodes.Select(n => n.Name));
        Assert.Equal(2, graft.Nodes[3].Parent);
    }

    [Fact]
    public void What_hung_from_a_shoulder_left_behind_hangs_from_the_mount()
    {
        var knight = Parts((-1, "root"), (0, "head"), (1, "r_shoulder"), (2, "r_gun"), (3, "cannon"));
        var graft = new RigGraft(Walker, new Dictionary<Mounting, RigGraft.Source?> { [Mounting.Left] = new(knight, 3) });

        Assert.Equal(graft.Mount(Mounting.Left), graft.Nodes[graft.Weapon(Mounting.Left, 4)].Parent);
    }

    [Fact]
    public void A_mount_the_chassis_lacks_mirrors_the_other_on_the_same_parent()
    {
        var graft = new RigGraft(Walker, new Dictionary<Mounting, RigGraft.Source?> { [Mounting.Left] = null, [Mounting.Right] = new(Gun, 2) });

        var mount = graft.Nodes[graft.Mount(Mounting.Right)];
        Assert.Equal(("r_gun", -1, 2, 1), (mount.Name, mount.Chassis, mount.Opposite, mount.Parent));
        Assert.Equal(graft.Mount(Mounting.Right), graft.Weapon(Mounting.Right, 2));
        Sound(graft);
    }

    [Fact]
    public void A_mount_inside_a_weapon_is_no_mount_of_the_hero()
    {
        var cannon = Parts((-1, "root"), (0, "hip"), (1, "l_gun"), (2, "gun"));
        Assert.Equal(-1, RigGraft.MountOf(cannon, Mounting.Top));

        var graft = new RigGraft(Walker, new Dictionary<Mounting, RigGraft.Source?> { [Mounting.Left] = new(cannon, 2) });

        Assert.Equal("barrels", graft.Nodes[graft.Weapon(Mounting.Left, 3)].Name);
        Assert.Equal(-1, graft.Mount(Mounting.Top));
    }

    [Fact]
    public void A_weapon_comes_from_its_own_side_and_else_from_the_other()
    {
        Assert.Equal(new HeroDonor("AGGRESSOR_1_GRUNT_R_BOLTER", Mounting.Right), HeroDonors.For("BOLTER", Mounting.Right));
        Assert.Equal(new HeroDonor("AGGRESSOR_2_GRUNT_L_BOLTER", Mounting.Left), HeroDonors.For("BOLTER", Mounting.Left));
        Assert.Equal(new HeroDonor("AGGRESSOR_3_BALLISTA_T_MULTI_LAUNCHER1", Mounting.Top), HeroDonors.For("MULTI_LAUNCHER1", Mounting.Left));
        Assert.Null(HeroDonors.For("NO_SUCH_GUN", Mounting.Left));
    }

    [Fact]
    public void A_weapon_that_leans_is_turned_over_on_the_other_side_only()
    {
        Assert.False(HeroDonors.For("FLAME_THROWER2", Mounting.Left)!.Mirrored);
        Assert.True(HeroDonors.For("FLAME_THROWER2", Mounting.Right)!.Mirrored);
        Assert.False(HeroDonors.For("FLAME_THROWER2", Mounting.Top)!.Mirrored);
        Assert.False(HeroDonors.For("BOLTER", Mounting.Top)!.Mirrored);
    }

    [Fact]
    public void Every_part_of_the_catalogue_has_its_donor()
    {
        Assert.Equal(WiredCatalogue.Chassis.Select(c => c.Id).Order(), HeroDonors.Chassis.Keys.Order());
        Assert.Equal(WiredCatalogue.Weapons.Select(w => w.Id).Order(), HeroDonors.Weapons.Order());
    }

    [Fact]
    public void A_chassis_own_weapon_stands_as_exported_and_no_other_does()
    {
        Assert.True(HeroDonors.AtHome("reaper3", HeroDonors.For("AUTO_CANNON", Mounting.Left)!, Mounting.Left));
        Assert.True(HeroDonors.AtHome("grunt1", HeroDonors.For("BOLTER", Mounting.Right)!, Mounting.Right));
        Assert.False(HeroDonors.AtHome("grunt2", HeroDonors.For("BOLTER", Mounting.Right)!, Mounting.Left));
        Assert.False(HeroDonors.AtHome("reaper2", HeroDonors.For("AUTO_CANNON", Mounting.Left)!, Mounting.Left));
    }

    [Fact]
    public void The_real_rigs_have_the_mounts_the_tables_name()
    {
        if (!Exported)
            return;
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var parts = Rig(HeroDonors.Chassis[chassis.Id]);
            foreach (var slot in chassis.Slots)
            {
                var opposite = slot.Mounting switch { Mounting.Left => Mounting.Right, Mounting.Right => Mounting.Left, _ => slot.Mounting };
                Assert.True(RigGraft.MountOf(parts, slot.Mounting) >= 0 || RigGraft.MountOf(parts, opposite) >= 0, $"{chassis.Id} has no mount for {slot.Mounting}");
            }
        }
        foreach (var weapon in WiredCatalogue.Weapons)
            foreach (var mounting in Enum.GetValues<Mounting>())
            {
                var donor = HeroDonors.For(weapon.Id, mounting)!;
                Assert.True(RigGraft.MountOf(Rig(donor.Model), donor.Mount) >= 0, $"{donor.Model} has no {donor.Mount} mount");
            }
    }

    [Fact]
    public void Every_weapon_goes_on_every_slot_it_fits()
    {
        if (!Exported)
            return;
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var parts = Rig(HeroDonors.Chassis[chassis.Id]);
            foreach (var weapon in WiredCatalogue.Weapons)
            {
                var slots = new Dictionary<Mounting, RigGraft.Source?>();
                foreach (var slot in chassis.Slots.Where(s => s.Fixed is null))
                {
                    var donor = HeroDonors.For(weapon.Id, slot.Mounting)!;
                    var rig = Rig(donor.Model);
                    slots[slot.Mounting] = WiredCatalogue.WhyNotFit(chassis, slot.Mounting, weapon) is null ? new(rig, RigGraft.MountOf(rig, donor.Mount)) : null;
                }
                var graft = new RigGraft(parts, slots);
                Sound(graft);
                foreach (var (side, source) in slots.Where(s => s.Value is not null))
                {
                    var mount = graft.Mount(side);
                    Assert.True(mount >= 0, $"{chassis.Id} lost its {side} mount for {weapon.Id}");
                    foreach (var part in RigGraft.Carried(source!.Parts, source.Mount))
                        Assert.True(graft.Weapon(side, part) >= 0, $"{weapon.Id} lost {source.Parts[part].Name} on {chassis.Id}");
                }
                // What stays of the chassis is still what it was, under its old name.
                for (var i = 0; i < parts.Length; i++)
                    if (graft.Chassis(i) is >= 0 and var kept)
                        Assert.Equal(parts[i].Name, graft.Nodes[kept].Name);
                Assert.All(chassis.Slots.Where(s => s.Fixed is not null), fixedSlot => Assert.False(graft.Nodes[graft.Mount(fixedSlot.Mounting)].Stripped));
            }
        }
    }

    private static readonly MountBox Bolter = new(-0.7f, 1, -0.5f, 0.7f, -0.25f, 0.25f);

    [Fact]
    public void A_weapon_like_the_chassis_own_does_not_move()
    {
        Assert.Equal((0f, 0f, 0f), HeroSeat.Between(Bolter, Bolter, Mounting.Left));
        Assert.Equal((0f, 0f, 0f), HeroSeat.Between(Bolter, Bolter, Mounting.Right));
        Assert.Equal((0f, 0f, 0f), HeroSeat.Between(Bolter, Bolter, Mounting.Top));
    }

    [Fact]
    public void A_wider_weapon_keeps_the_inner_edge_and_grows_outward()
    {
        var wide = Bolter with { MinZ = -1, MaxZ = 1 };

        Assert.Equal(-0.75f, HeroSeat.Between(Bolter, wide, Mounting.Left).Z, 3);
        Assert.Equal(0.75f, HeroSeat.Between(Bolter, wide, Mounting.Right).Z, 3);
        Assert.Equal(0, HeroSeat.Between(Bolter, wide, Mounting.Top).Z, 3);
        Assert.Equal(0, HeroSeat.Between(Bolter, wide, Mounting.Left, sideways: false).Z, 3);
    }

    [Fact]
    public void Barrels_without_a_breech_come_back_to_the_mount()
    {
        var barrels = Bolter with { MinX = 0.8f, MaxX = 2.2f };

        Assert.Equal(-1.2f, HeroSeat.Between(Bolter, barrels, Mounting.Left).X, 3);
    }

    [Fact]
    public void A_short_gun_comes_out_of_a_long_weapons_room_no_more_than_it_must()
    {
        var cannon = Bolter with { MinX = 0.4f, MaxX = 2.5f };

        Assert.Equal(1.1f, HeroSeat.Between(cannon, Bolter, Mounting.Left).X, 3);
    }

    [Fact]
    public void A_launcher_on_its_stand_comes_down_on_a_mount_for_guns_and_a_gun_goes_up_on_a_mount_for_launchers()
    {
        var launcher = Bolter with { MinY = 0, MaxY = 2.5f };

        Assert.Equal(-0.5f, HeroSeat.Between(Bolter, launcher, Mounting.Left).Y, 3);
        Assert.Equal(0.5f, HeroSeat.Between(launcher, Bolter, Mounting.Top).Y, 3);
    }

    [Fact]
    public void On_a_mount_in_the_body_a_weapon_stands_on_the_skin_in_the_middle()
    {
        var launcher = Bolter with { MinY = 0, MaxY = 2.5f, MinZ = -2, MaxZ = 2 };

        var (_, up, side) = HeroSeat.Between(launcher, Bolter, Mounting.Left, surface: 1.2f);

        Assert.Equal(1.7f, up, 3);
        Assert.Equal(0, side, 3);
    }

    [Fact]
    public void Only_what_would_dwarf_its_chassis_comes_smaller()
    {
        Assert.Equal(0.7f, HeroDonors.Scale("ballista3", "MULTI_LAUNCHER2"));
        Assert.Equal(0.5f, HeroDonors.Scale("commander4", "MULTI_LAUNCHER2"));
        Assert.Equal(0.6f, HeroDonors.Scale("commander5", "MULTI_LAUNCHER2"));
        Assert.Equal(0.6f, HeroDonors.Scale("knight4", "MULTI_LAUNCHER2"));
        Assert.Equal(0.6f, HeroDonors.Scale("knight5", "MULTI_LAUNCHER2"));
        Assert.Equal(1, HeroDonors.Scale("ballista4", "MULTI_LAUNCHER2"));
        Assert.Equal(0.75f, HeroDonors.Scale("ballista3", "FLAME_THROWER2"));
        Assert.Equal(1, HeroDonors.Scale("knight5", "FLAME_THROWER2"));
        Assert.All(HeroDonors.Chassis.Keys, chassis => Assert.All(HeroDonors.Weapons.Where(w => w is not "MULTI_LAUNCHER2" and not "FLAME_THROWER2"), weapon => Assert.Equal(1, HeroDonors.Scale(chassis, weapon))));
        Assert.Equal(new MountBox(-1, 1, -2, 2, -3, 3), new MountBox(-2, 2, -4, 4, -6, 6) * 0.5f);
    }

    // The corners of a box, three by three: the skin of a body, without what is left out.
    private static Vector3[] Box(Vector3 low, Vector3 high, bool open = false)
    {
        Vector3 At(int corner) => new((corner & 1) == 0 ? low.X : high.X, (corner & 2) == 0 ? low.Y : high.Y, (corner & 4) == 0 ? low.Z : high.Z);
        int[][] sides = [[0, 1, 3, 2], [4, 5, 7, 6], [2, 3, 7, 6], [0, 4, 6, 2], [1, 5, 7, 3], [0, 1, 5, 4]];
        return sides.Take(open ? 5 : 6).SelectMany(s => new[] { At(s[0]), At(s[1]), At(s[2]), At(s[0]), At(s[2]), At(s[3]) }).ToArray();
    }

    // Four metres long, two high, three wide, standing on the ground.
    private static readonly HeroSolid Block = new([Box(new Vector3(-2, 0, -1.5f), new Vector3(2, 2, 1.5f))]);

    // A gun's points along x from a place, each row 10 cm from the last; the front 15 % is its muzzle.
    private static HeroPoints Barrel(Vector3 from, float length = 2)
    {
        var all = Enumerable.Range(0, (int)(length * 10)).SelectMany(i => new[] { -0.1f, 0, 0.1f }.Select(z => from + new Vector3(i * 0.1f, 0, z))).ToList();
        return new HeroPoints(all, all.Where(p => p.X >= from.X + length * 0.85f).ToList());
    }

    private static readonly (float Held, float Over) Held = (0.3f, 0);

    [Fact]
    public void A_solid_is_what_its_skin_closes_and_knows_how_far_off_a_point_lies()
    {
        Assert.True(Block.Holds(new Vector3(0, 1, 0)));
        Assert.True(Block.Holds(new Vector3(1.9f, 1.9f, 1.4f)));
        Assert.False(Block.Holds(new Vector3(0, 3, 0)));
        Assert.False(Block.Holds(new Vector3(3, 1, 0)));
        Assert.Equal(1, Block.Gap(new Vector3(0, 3, 0)), 0.15f);
        Assert.Equal(float.PositiveInfinity, Block.Gap(new Vector3(0, 30, 0)));
        Assert.True(Block.Covers(new Vector3(0, 7, 0)));
        Assert.False(Block.Covers(new Vector3(3, 1, 0)));
        Assert.Equal(1.5f, Block.Skin(new Vector3(0, 0.5f, 0), Vector3.UnitY)!.Value, 0.15f);
        Assert.Null(Block.Skin(new Vector3(3, 0.5f, 0), Vector3.UnitY));
    }

    [Fact]
    public void A_body_open_where_nobody_looks_is_solid_all_the_same()
    {
        var open = new HeroSolid([Box(new Vector3(-1), new Vector3(1), open: true)]);

        Assert.True(open.Holds(Vector3.Zero));
    }

    [Fact]
    public void A_gun_that_stands_well_stays()
    {
        var bedded = Barrel(new Vector3(-1, 1, 1.7f));

        Assert.Equal(Vector3.Zero, HeroSeat.Fit(Block, [], bedded, Held));
    }

    [Fact]
    public void A_buried_gun_comes_out_no_more_than_it_must()
    {
        var buried = Barrel(new Vector3(-1, 1, 1));

        var move = HeroSeat.Fit(Block, [], buried, Held);
        var stand = HeroSeat.Stand(Block, [], buried, move);

        Assert.True(stand.Buried <= HeroSeat.MostBuried, $"{stand}");
        Assert.True(stand.Held >= Held.Held, $"{stand}");
        Assert.True(move.Length() < 1.2f, $"{move}");
    }

    [Fact]
    public void A_gun_in_the_air_comes_to_the_body()
    {
        var off = Barrel(new Vector3(-1, 1, 2.5f));

        var move = HeroSeat.Fit(Block, [], off, Held);

        Assert.True(HeroSeat.Stand(Block, [], off, move).Held >= Held.Held);
        Assert.True(move.Z < 0, $"{move}");
    }

    [Fact]
    public void A_muzzle_does_not_stay_in_the_body()
    {
        var blocked = Barrel(new Vector3(-3.6f, 1, 0));

        var stand = HeroSeat.Stand(Block, [], blocked, HeroSeat.Fit(Block, [], blocked, Held));

        Assert.True(HeroSeat.Stand(Block, [], blocked, Vector3.Zero).Blocked > HeroSeat.MostBlocked);
        Assert.True(stand.Blocked <= HeroSeat.MostBlocked, $"{stand}");
    }

    [Fact]
    public void A_gun_keeps_out_of_the_one_seated_before_it()
    {
        var gun = Barrel(new Vector3(-1, 1, 1.7f));
        HeroSolid[] seated = [new([Box(new Vector3(-1.2f, 0.7f, 1.5f), new Vector3(1.2f, 1.3f, 2.1f))])];

        var stand = HeroSeat.Stand(Block, seated, gun, HeroSeat.Fit(Block, seated, gun, Held));

        Assert.True(HeroSeat.Stand(Block, seated, gun, Vector3.Zero).Crowded > HeroSeat.MostCrowded);
        Assert.True(stand.Crowded <= HeroSeat.MostCrowded, $"{stand}");
        Assert.True(stand.Held >= Held.Held, $"{stand}");
    }

    [Fact]
    public void A_weapon_on_top_comes_back_over_the_body_and_keeps_to_the_centre_line()
    {
        var hanging = Barrel(new Vector3(1, 2.05f, 0), 4);

        var move = HeroSeat.Fit(Block, [], hanging, (Held.Held, HeroSeat.LeastOver));

        Assert.True(HeroSeat.Stand(Block, [], hanging, Vector3.Zero).Over < HeroSeat.LeastOver);
        Assert.True(HeroSeat.Stand(Block, [], hanging, move).Over >= HeroSeat.LeastOver);
        Assert.Equal(0, move.Z);
    }

    [Fact]
    public void A_weapon_on_top_keeps_to_the_centre_line_though_a_step_aside_were_shorter()
    {
        // A wing on one side: a short step onto it would do as well, but it would stand askew.
        var winged = new HeroSolid([Box(new Vector3(-2, 0, -1.5f), new Vector3(2, 2, 1.5f)), Box(new Vector3(2, 0, 0.25f), new Vector3(5, 2, 1.5f))]);
        var hanging = Barrel(new Vector3(1, 2.05f, 0), 4);

        Assert.True(HeroSeat.Stand(winged, [], hanging, new Vector3(0, 0, 0.4f)).Over >= HeroSeat.LeastOver);

        var move = HeroSeat.Fit(winged, [], hanging, (Held.Held, HeroSeat.LeastOver));

        Assert.Equal(0, move.Z);
        Assert.True(HeroSeat.Stand(winged, [], hanging, move).Over >= HeroSeat.LeastOver);
    }

    [Fact]
    public void Twins_move_alike_on_either_side()
    {
        var left = Barrel(new Vector3(-1, 1, -1));

        var (one, other) = (HeroSeat.Fit(Block, [], left, Held, twins: true), HeroSeat.Fit(Block, [], left.Across, Held, twins: true, right: true));

        Assert.NotEqual(Vector3.Zero, one);
        Assert.Equal(one with { Z = -one.Z }, other);
    }

    [Fact]
    public void Out_of_a_wall_on_the_centre_line_the_left_gun_and_the_right_go_opposite_ways()
    {
        var wall = new HeroSolid([Box(new Vector3(-3, 0, -0.35f), new Vector3(3, 5, 0.35f))]);
        var gun = Barrel(new Vector3(-1, 2.5f, 0));

        var (one, other) = (HeroSeat.Fit(wall, [], gun, Held), HeroSeat.Fit(wall, [], gun, Held, right: true));

        Assert.NotEqual(0, one.Z);
        Assert.Equal(one with { Z = -one.Z }, other);
    }

    [Fact]
    public void A_twin_stands_where_the_body_lets_its_pair_stand_too()
    {
        // A lump on one side only: without its pair the gun on the other side would stay where it is.
        var lump = new HeroSolid([Box(new Vector3(-2, 0, -1.5f), new Vector3(2, 2, 1.5f)), Box(new Vector3(-2, 0.5f, -2.2f), new Vector3(2, 1.5f, 0))]);
        var gun = Barrel(new Vector3(-1, 1, 1.75f));

        Assert.Equal(Vector3.Zero, HeroSeat.Fit(lump, [], gun, Held));
        var move = HeroSeat.Fit(lump, [], gun, Held, twins: true);

        Assert.NotEqual(Vector3.Zero, move);
        Assert.True(HeroSeat.Stand(lump, [], gun.Across, move with { Z = -move.Z }).Buried <= HeroSeat.MostBuried);
    }

    [Fact]
    public void A_twin_keeps_to_its_half_of_the_machine()
    {
        // Reaching over the top from the left: alone it could stay, but its pair would go through it.
        var gun = new HeroPoints(Enumerable.Range(0, 12).Select(i => new Vector3(0, 2.1f, -1 + i * 0.15f)).ToList(), []);

        Assert.Equal(Vector3.Zero, HeroSeat.Fit(Block, [], gun, Held));
        var move = HeroSeat.Fit(Block, [], gun, Held, twins: true);

        Assert.True(gun.All.Count(p => p.Z + move.Z > 0) <= HeroSeat.MostCrowded * gun.All.Count, $"{move}");
    }

    [Fact]
    public void In_every_look_every_weapon_stands_well_on_every_slot_it_fits()
    {
        if (!Exported)
            return;
        var wrong = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(Looks.Keys.SelectMany(look => WiredCatalogue.Chassis.Select(chassis => (look, chassis))), each =>
        {
            var (look, chassis) = each;
            var (body, solid) = Chassis(look, chassis.Id);
            foreach (var slot in chassis.Slots.Where(s => s.Fixed is null))
                foreach (var weapon in WiredCatalogue.Weapons.Where(w => WiredCatalogue.WhyNotFit(chassis, slot.Mounting, w) is null))
                {
                    var worn = Wearing(look, weapon.Id, slot.Mounting);
                    if (HeroDonors.AtHome(chassis.Id, worn.Donor, slot.Mounting))
                        continue;
                    var stand = HeroSeat.Measure(chassis.Id, body, solid, worn, HeroSeat.Seats(chassis.Id, body, solid, [worn])[slot.Mounting]);
                    if (!Well(stand, HeroSeat.Wanted(chassis.Id, worn)))
                        wrong.Add($"{look} {chassis.Id} {slot.Mounting} {weapon.Id}: {stand}");
                    if (slot.Mounting != Mounting.Left || chassis.Slot(Mounting.Right) is null || WiredCatalogue.WhyNotFit(chassis, Mounting.Right, weapon) is not null)
                        continue;

                    // A pair: the right one follows the left, so it is looked at here as well.
                    var pair = Wearing(look, weapon.Id, Mounting.Right);
                    if (HeroDonors.AtHome(chassis.Id, pair.Donor, Mounting.Right))
                        continue;
                    var seats = HeroSeat.Seats(chassis.Id, body, solid, [worn, pair]);
                    var right = HeroSeat.Measure(chassis.Id, body, solid, pair, seats[Mounting.Right], [HeroSeat.Solid(chassis.Id, body, worn, seats[Mounting.Left])]);
                    if (!Well(right, HeroSeat.Wanted(chassis.Id, pair)) || right.Crowded > HeroSeat.MostCrowded + 0.03f)
                        wrong.Add($"{look} {chassis.Id} pair {weapon.Id}: {right}");
                }
        });
        Assert.True(wrong.IsEmpty, string.Join('\n', wrong.Order()));
    }

    // A seat lies on a grid and is measured by sample, so a hair over will do.
    private static bool Well(HeroStand stand, (float Held, float Over) wanted)
    {
        const float hair = 0.01f;
        return stand.Buried <= HeroSeat.MostBuried + hair && stand.Held >= wanted.Held - hair && stand.Blocked <= HeroSeat.MostBlocked + hair && stand.Over >= wanted.Over - hair;
    }

    [Theory]
    [InlineData("overseer2", "BOLTER")]
    [InlineData("overseer3", "ELECTRIC_CHARGE")]
    [InlineData("commander5", "HEAVY_BOLTER1")]
    [InlineData("reaper3", "BOLTER")]
    [InlineData("knight5", "PLASMA_RIFLE")]
    [InlineData("grunt3", "AUTO_CANNON")]
    [InlineData("grunt3", "FLAME_THROWER2")]
    [InlineData("overseer2", "FLAME_THROWER2")]
    [InlineData("reaper2", "FLAME_THROWER2")]
    [InlineData("reaper2", "BOLTER")]
    [InlineData("reaper2", "AUTO_CANNON")]
    [InlineData("reaper2", "ELECTRIC_CHARGE")]
    [InlineData("reaper2", "PLASMA_RIFLE")]
    [InlineData("reaper2", "PULSE_RIFLE")]
    public void A_pair_of_guns_stands_alike(string chassis, string weapon)
    {
        if (!Exported)
            return;
        foreach (var look in Looks.Keys)
        {
            var (left, right) = Pair(look, chassis, weapon);

            Assert.True(Vector3.Distance(left, right with { Z = -right.Z }) <= 0.45f, $"{look}: {left} and {right}");
        }
    }

    private static (Vector3 Left, Vector3 Right) Pair(string look, string chassis, string weapon)
    {
        var (body, solid) = Chassis(look, chassis);
        var worn = new[] { Wearing(look, weapon, Mounting.Left), Wearing(look, weapon, Mounting.Right) };
        var seats = HeroSeat.Seats(chassis, body, solid, worn);
        return (Vector3.Transform(HeroSeat.Middle(chassis, worn[0]), seats[Mounting.Left] * HeroSeat.MountPlace(body, Mounting.Left)),
            Vector3.Transform(HeroSeat.Middle(chassis, worn[1]), seats[Mounting.Right] * HeroSeat.MountPlace(body, Mounting.Right)));
    }

    [Fact]
    public void The_reapers_flame_throwers_stand_in_line_though_it_has_no_right_mount_of_its_own()
    {
        if (!Exported)
            return;
        foreach (var look in Looks.Keys)
        {
            var (left, right) = Pair(look, "reaper2", "FLAME_THROWER2");
            var apart = Vector3.Abs(left - right with { Z = -right.Z });

            Assert.True(apart.X <= 0.2f && apart.Y <= 0.2f && apart.Z <= 0.2f, $"{look}: {left} and {right}");
        }
    }

    // The share of a weapon's surface the body holds, by a look at the points the seat rule looks at.
    private static float HeldBy(string look, string chassis, Mounting side, string weapon)
    {
        var (body, solid) = Chassis(look, chassis);
        var worn = Wearing(look, weapon, side);
        return HeroSeat.Measure(chassis, body, solid, worn, HeroSeat.Seats(chassis, body, solid, [worn])[side]).Held;
    }

    [Theory]
    [InlineData("AUTO_CANNON")]
    [InlineData("BOLTER")]
    [InlineData("ELECTRIC_CHARGE")]
    [InlineData("PLASMA_RIFLE")]
    [InlineData("PULSE_RIFLE")]
    [InlineData("FLAME_THROWER2")]
    public void On_the_reaper_a_gun_is_held_on_the_right_as_on_the_left(string weapon)
    {
        if (!Exported)
            return;
        // The HD model has pods of its own, and they differ a little.
        foreach (var look in new[] { "Classic", "Overhauled" })
        {
            var (left, right) = (HeldBy(look, "reaper2", Mounting.Left, weapon), HeldBy(look, "reaper2", Mounting.Right, weapon));

            Assert.True(right >= left * 0.8f, $"{look}: {right} on the right, {left} on the left");
        }
    }

    [Fact]
    public void The_barrel_of_the_reapers_right_flame_thrower_lies_at_its_shoulder()
    {
        if (!Exported)
            return;
        foreach (var look in Looks.Keys)
        {
            var (body, solid) = Chassis(look, "reaper2");
            var worn = Wearing(look, "FLAME_THROWER2", Mounting.Right);
            var place = HeroSeat.Seats("reaper2", body, solid, [worn])[Mounting.Right] * HeroSeat.MountPlace(body, Mounting.Right);
            var points = HeroSolid.Spread(worn.Rig[worn.Mount].Surfaces, HeroSolid.Cell).Select(p => Vector3.Transform(p, place)).ToList();

            // The barrel is what stands furthest out: the tank lies round the hip, and held alone it would leave the barrel in the air.
            var outer = points.Max(p => p.Z);
            var barrel = points.Where(p => p.Z > outer - 0.45f).ToList();
            var held = barrel.Count(p => solid.Gap(p) <= HeroSeat.Touching) / (float)barrel.Count;

            Assert.True(held >= 0.2f, $"{look}: {held}");
        }
    }

    [Theory]
    [InlineData("Classic", true)]
    [InlineData("Overhauled", true)]
    [InlineData("Hd", false)]
    public void The_reaper_has_a_shoulder_on_either_side_in_every_look(string look, bool copied)
    {
        if (!Exported)
            return;
        var (body, solid) = Chassis(look, "reaper2");
        var right = HeroSeat.MountPlace(body, Mounting.Right).Translation;

        Assert.Equal(copied, HeroSeat.Shoulder("reaper2", body) is not null);
        Assert.True(solid.Holds(right), $"{look}: nothing round {right}");
        Assert.True(solid.Holds(right with { Z = -right.Z }), $"{look}: nothing on the left");
    }

    [Fact]
    public void Only_the_reapers_shoulder_goes_across_and_not_the_hull_it_stands_against()
    {
        if (!Exported)
            return;
        foreach (var chassis in HeroDonors.Chassis.Keys.Where(c => c != "reaper2"))
            Assert.Null(HeroSeat.Shoulder(chassis, Shape("Classic", HeroDonors.Chassis[chassis])));
        foreach (var look in new[] { "Classic", "Overhauled" })
        {
            var body = Shape(look, HeroDonors.Chassis["reaper2"]);
            var shoulder = HeroSeat.Shoulder("reaper2", body)!;
            var taken = shoulder.Triangles.SelectMany((list, s) => list.SelectMany(t => body[shoulder.Part].Surfaces[s].Skip(t * 3).Take(3))).ToList();
            var lies = HeroDonors.Shoulder("reaper2")!.Value;

            Assert.NotEmpty(taken);
            Assert.All(taken, c => Assert.True(c.Z <= -lies.Start && c.Y < lies.Top, $"{look}: {c}"));
        }
    }

    [Fact]
    public void A_seat_turned_over_keeps_every_corner_where_it_was_and_turns_no_part_inside_out()
    {
        if (!Exported)
            return;
        var turned = 0;
        foreach (var look in Looks.Keys)
            foreach (var chassis in WiredCatalogue.Chassis)
            {
                if (chassis.Slot(Mounting.Right) is null || WiredCatalogue.WhyNotFit(chassis, Mounting.Right, WiredCatalogue.FindWeapon("FLAME_THROWER2")!) is not null)
                    continue;
                var (body, solid) = Chassis(look, chassis.Id);
                var worn = Wearing(look, "FLAME_THROWER2", Mounting.Right);
                var mount = HeroSeat.MountPlace(body, Mounting.Right);
                var seat = HeroSeat.Seats(chassis.Id, body, solid, [worn])[Mounting.Right];
                var upright = HeroSeat.Upright(worn, seat);

                Assert.True(worn.Donor.Mirrored);
                Assert.True((seat * mount).GetDeterminant() < 0, "what the seat rule measures is turned over");
                Assert.True((upright * mount).GetDeterminant() > 0, $"{look} {chassis.Id}");
                foreach (var corner in worn.Rig[worn.Mount].Surfaces.SelectMany(s => s).Take(50))
                {
                    var (there, here) = (Vector3.Transform(corner, seat * mount), Vector3.Transform(corner with { Z = -corner.Z }, upright * mount));
                    Assert.True(Vector3.Distance(there, here) < 0.001f, $"{look} {chassis.Id}: {there} and {here}");
                }
                turned++;
            }
        Assert.True(turned > 30, $"{turned}");
    }

    [Fact]
    public void A_seat_that_is_not_turned_over_stays_as_it_is()
    {
        var worn = new HeroWorn(Mounting.Right, "BOLTER", HeroDonors.For("BOLTER", Mounting.Right)!, [], 0);
        var seat = Matrix4x4.CreateTranslation(1, 2, 3);

        Assert.Equal(seat, HeroSeat.Upright(worn, seat));
    }

    [Fact]
    public void What_is_turned_over_has_one_part_and_no_motion_a_turned_mesh_would_miss()
    {
        if (!Exported)
            return;
        foreach (var weapon in HeroDonors.Weapons.Where(HeroDonors.Leans))
        {
            var donor = HeroDonors.For(weapon, Mounting.Right)!;
            using var file = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Models, donor.Model + ".json")));
            var rig = file.RootElement.GetProperty("rig");
            var parts = Rig(donor.Model);
            var mount = RigGraft.MountOf(parts, donor.Mount);
            int[] Moved(string name) => rig.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(m => m.GetProperty("node").GetInt32()).ToArray() : [];
            var moving = rig.TryGetProperty("animations", out var cycles) ? cycles.EnumerateObject().SelectMany(c => c.Value.GetProperty("tracks").EnumerateArray()).Select(t => t.GetProperty("node").GetInt32()) : [];

            Assert.Equal([mount], RigGraft.Carried(parts, mount));
            Assert.DoesNotContain(mount, Moved("idle").Concat(Moved("weapons")).Concat(moving));
        }
    }

    [Fact]
    public void A_turned_mesh_winds_its_triangles_the_other_way()
    {
        Assert.Equal([0, 2, 1, 3, 5, 4], HeroSeat.Rewound([0, 1, 2, 3, 4, 5]));
        Assert.Equal([3, 5, 4], HeroSeat.Rewound([0, 1, 2, 3, 4, 5], [1]));
    }

    [Fact]
    public void A_turned_mesh_keeps_its_texture_running_the_same_way()
    {
        float[] tangents = [1, 2, 3, 1, 4, 5, 6, -1];

        Assert.Equal([1, 2, -3, -1, 4, 5, -6, 1], HeroSeat.TurnedOver(tangents));
        Assert.Equal(1, tangents[3]);
    }

    [Fact]
    public void Three_flame_throwers_keep_their_tanks_apart()
    {
        if (!Exported)
            return;
        foreach (var look in Looks.Keys)
        {
            var (body, solid) = Chassis(look, "knight5");
            var worn = new[] { Mounting.Left, Mounting.Right, Mounting.Top }.Select(side => Wearing(look, "FLAME_THROWER2", side)).ToList();
            var seats = HeroSeat.Seats("knight5", body, solid, worn);

            var others = worn.Take(2).Select(w => HeroSeat.Solid("knight5", body, w, seats[w.Side])).ToList();
            var stand = HeroSeat.Measure("knight5", body, solid, worn[2], seats[Mounting.Top], others);

            Assert.True(stand.Crowded <= HeroSeat.MostCrowded + 0.01f, $"{look}: {stand}");
        }
    }

    [Fact]
    public void A_weapon_on_the_bee_rides_level_on_its_back_and_its_own_bomb_stays_under_it()
    {
        if (!Exported)
            return;
        foreach (var look in Looks.Keys)
        {
            var (body, solid) = Chassis(look, "boss2");
            var mount = HeroSeat.MountPlace(body, Mounting.Top);

            var bolter = Wearing(look, "HEAVY_BOLTER1", Mounting.Top);
            var place = HeroSeat.Seats("boss2", body, solid, [bolter])[Mounting.Top] * mount;

            Assert.True(place.Translation.Y > mount.Translation.Y + 1, $"{look}: {place.Translation} over {mount.Translation}");
            Assert.Equal(1, place.M22, 0.01f);
            Assert.Equal(0, place.Translation.Z, 0.15f);

            var bomb = Wearing(look, "BEE_BOMB", Mounting.Top);
            Assert.True(HeroSeat.Seats("boss2", body, solid, [bomb])[Mounting.Top].IsIdentity);
        }
    }

    [Theory]
    [InlineData("MULTI_LAUNCHER1", "ballista3", Mounting.Top)]
    [InlineData("MULTI_LAUNCHER2", "ballista4", Mounting.Left)]
    public void A_launcher_loses_its_foot_in_the_hd_look_on_any_machine_but_its_own(string weapon, string home, Mounting side)
    {
        if (!Exported)
            return;
        var worn = Wearing("Hd", weapon, side);
        var feet = worn.Rig.SelectMany(p => p.Surfaces).Count(s => HeroSeat.IsFoot(s, worn.Rig[worn.Mount]));

        Assert.True(feet >= 2, "the mount's own, and the one repeated below it");
        Assert.False(HeroDonors.Footless(home, worn));
        Assert.True(HeroDonors.Footless("knight4", worn));
        Assert.False(HeroDonors.Footless("knight4", Wearing("Hd", "BOLTER", side)));
    }

    [Fact]
    public void A_surface_is_a_foot_only_where_it_repeats_its_mount()
    {
        Vector3[] foot = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        var mount = new RigShape(-1, "l_gun", Matrix4x4.Identity, [foot]);

        Assert.True(HeroSeat.IsFoot(foot.ToArray(), mount));
        Assert.False(HeroSeat.IsFoot([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], mount));
        Assert.False(HeroSeat.IsFoot([], mount));
    }

    [Fact]
    public void The_great_launcher_stands_no_higher_than_what_carries_it()
    {
        if (!Exported)
            return;
        foreach (var look in Looks.Keys)
            foreach (var chassis in new[] { "commander4", "commander5", "knight4", "knight5", "ballista3" })
            {
                var (body, _) = Chassis(look, chassis);
                var worn = Wearing(look, "MULTI_LAUNCHER2", Mounting.Left);
                var launcher = RigGraft.Carried(worn.Rig.Select(p => new RigPart(p.Parent, p.Name)).ToArray(), worn.Mount).SelectMany(p => worn.Rig[p].Surfaces).SelectMany(s => s).ToList();
                var machine = Enumerable.Range(0, body.Length).Where(p => body[p].Surfaces.Length > 0).Select(p => World(body, p)).ToList();

                var high = MountBox.Of(launcher)!.Value is var box ? (box.MaxY - box.MinY) * HeroDonors.Scale(chassis, "MULTI_LAUNCHER2") : 0;

                Assert.True(high < machine.Max(m => m.Translation.Y) + 1, $"{look} {chassis}: {high}");
            }
    }

    // ---- on the hangar's stage ---------------------------------------------------------------------

    // The stage with the shelves, and with the tree open, in a window of 1920 by 1080 and of 1600 by 900; in a run and before one.
    private static readonly (float Width, float Height)[] Stages = [(978, 810), (978, 870), (708, 810), (822, 672), (634, 672), (634, 730)];

    // A hero as it stands on the turntable: the axis through the middle of its box, its feet on the disc, a glider above it.
    private static List<Vector3> OnTheDisc(RigShape[] rig)
    {
        var points = Enumerable.Range(1, rig.Length - 1).SelectMany(p => rig[p].Surfaces.SelectMany(s => s).Select(v => Vector3.Transform(v, World(rig, p)))).ToList();
        var (low, high) = (new Vector3(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)), new Vector3(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
        var floor = low.Y > 3 ? low.Y - 1.2f : low.Y;
        return points.Select(p => new Vector3(p.X - (low.X + high.X) / 2, p.Y - floor, p.Z - (low.Z + high.Z) / 2)).ToList();
    }

    [Theory]
    [InlineData(978, 810)]
    [InlineData(708, 810)]
    [InlineData(822, 672)]
    [InlineData(634, 672)]
    [InlineData(502, 540)]
    [InlineData(394, 540)]
    public void The_stage_has_a_place_for_everything_and_nothing_on_top_of_anything(float width, float height)
    {
        foreach (var modules in Enumerable.Range(0, 6))
            foreach (var top in new[] { false, true })
            {
                var layout = HangarLayout.For(width, height, top, modules);
                var boxes = layout.Boxes.ToList();

                Assert.Equal(2 + (top ? 1 : 0) + 2 + modules + 3, boxes.Count);
                Assert.All(boxes, box => Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= width && box.Bottom <= height && box.Width > 0, $"{box}"));
                for (var a = 0; a < boxes.Count; a++)
                    for (var b = a + 1; b < boxes.Count; b++)
                        Assert.False(boxes[a].Meets(boxes[b]), $"{width}x{height}, {modules} modules, top {top}: {boxes[a]} and {boxes[b]}");
            }
    }

    [Theory]
    [InlineData(708, 810)]
    [InlineData(634, 672)]
    public void Five_modules_stay_wide_enough_to_read_beside_the_tree(float width, float height)
    {
        var layout = HangarLayout.For(width, height, top: true, modules: 5);

        Assert.All(Enumerable.Range(0, 5), i => Assert.True(layout.Cards[WiredSlot.Module(i)].Width >= 100, $"{layout.Cards[WiredSlot.Module(i)]}"));
    }

    [Theory]
    [InlineData("Classic")]
    [InlineData("Overhauled")]
    [InlineData("Hd")]
    public void A_hero_stands_clear_of_the_cards_however_it_is_turned_and_is_not_made_small_for_it(string look)
    {
        if (!Exported)
            return;
        const float fov = 26, elevation = 0.3f;
        var (sin, cos) = MathF.SinCos(elevation);
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var points = OnTheDisc(Shape(look, HeroDonors.Chassis[chassis.Id]));
            var bands = HangarFrame.Bands(points);
            var some = points.Where((_, i) => i % Math.Max(1, points.Count / 300) == 0).ToList();
            foreach (var (width, height) in Stages)
            {
                var layout = HangarLayout.For(width, height, chassis.Slots.Any(s => s.Mounting == Mounting.Top), chassis.Modules);
                var shot = HangarFrame.Fit(bands, width, height, layout.Boxes, fov, elevation);
                var scale = height / 2 / MathF.Tan(fov * MathF.PI / 360);
                var (eyeX, eyeY) = (cos * shot.Distance, shot.Height + sin * shot.Distance);
                var (top, foot) = (float.MaxValue, float.MinValue);
                for (var turn = 0f; turn < MathF.Tau; turn += MathF.PI / 8)
                    foreach (var point in some)
                    {
                        var (x, z) = (point.X * MathF.Cos(turn) - point.Z * MathF.Sin(turn), point.X * MathF.Sin(turn) + point.Z * MathF.Cos(turn));
                        var (awayX, awayY) = (x - eyeX, point.Y - eyeY);
                        var depth = -cos * awayX - sin * awayY;
                        var screenX = width / 2 + shot.Across * height - z / depth * scale;
                        var screenY = height / 2 + shot.Up * height - (-sin * awayX + cos * awayY) / depth * scale;
                        (top, foot) = (Math.Min(top, screenY), Math.Max(foot, screenY));
                        Assert.True(screenX >= 0 && screenY >= 0 && screenX <= width && screenY <= height, $"{look} {chassis.Id} off a stage of {width}x{height}");
                        Assert.False(layout.Boxes.Any(box => box.Meets(new HangarBox(screenX, screenY, 0.1f, 0.1f))), $"{look} {chassis.Id} on a stage of {width}x{height} under a card at {screenX:0},{screenY:0}");
                    }
                Assert.True(foot - top >= height * 0.28f, $"{look} {chassis.Id} on {width}x{height} is only {foot - top:0} high");
            }
        }
    }

    [Fact]
    public void A_hero_stands_halfway_along_a_gap_that_is_taller_than_it_is_wide()
    {
        var bands = HangarFrame.Bands([new Vector3(1.5f, 0, 0), new Vector3(1.5f, 2, 0)]);
        // A gap as narrow as can be between two walls, far taller than the hero.
        HangarBox[] walls = [new(0, 0, 330, 800), new(370, 0, 330, 800)];

        var shot = HangarFrame.Fit(bands, 700, 800, walls, 26, 0.3f);

        Assert.InRange(shot.Up, -0.05f, 0.05f);
        Assert.InRange(shot.Across, -0.05f, 0.05f);
    }

    private static int UpperBody(string look, string model)
    {
        var file = Looks[look].Select(folder => Path.Combine(Exports, folder, "red", model + ".json")).First(File.Exists);
        using var json = JsonDocument.Parse(File.ReadAllBytes(file));
        return json.RootElement.GetProperty("rig").TryGetProperty("upperBody", out var upper) ? upper.GetInt32() : -1;
    }

    [Theory]
    [InlineData("Classic")]
    [InlineData("Overhauled")]
    [InlineData("Hd")]
    public void The_traces_of_modules_and_abilities_end_on_the_bodys_skin(string look)
    {
        if (!Exported)
            return;
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var model = HeroDonors.Chassis[chassis.Id];
            var rig = Shape(look, model);
            var spots = HeroHull.Spots(rig, UpperBody(look, model));

            Assert.Equal(HeroHull.ModuleSpots + 2, spots.Length);
            Assert.All(spots, spot =>
            {
                var name = rig[spot.Part].Name;
                Assert.False(name.StartsWith("r_") || name.StartsWith("l_") || name.EndsWith(".cdf"), $"{look} {chassis.Id} on {name}");
                // Where it lies there is skin: on one of that part's triangles, as seen from ahead.
                var on = rig[spot.Part].Surfaces.SelectMany(s => s.Chunk(3)).Where(t => t.Length == 3).Any(t => OnTriangle(spot.At, t[0], t[1], t[2]));
                Assert.True(on, $"{look} {chassis.Id}: a spot in the air off {name}");
            });
            var places = spots.Select(s => Vector3.Transform(s.At, World(rig, s.Part))).ToList();
            Assert.True(places.Max(p => p.Z) - places.Min(p => p.Z) > 0.2f, $"{look} {chassis.Id}: all on one spot");
        }
    }

    private static bool OnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        var (ab, ac, ap) = (b - a, c - a, point - a);
        var normal = Vector3.Cross(ab, ac);
        if (normal.LengthSquared() < 1e-10f || MathF.Abs(Vector3.Dot(Vector3.Normalize(normal), ap)) > 0.01f)
            return false;
        var (u, v) = (Vector3.Dot(Vector3.Cross(ap, ac), normal) / normal.LengthSquared(), Vector3.Dot(Vector3.Cross(ab, ap), normal) / normal.LengthSquared());
        return u >= -0.01f && v >= -0.01f && u + v <= 1.01f;
    }

    [Theory]
    [InlineData("grunt1", "hip")]
    [InlineData("reaper2", "body")]
    [InlineData("knight5", "head")]
    [InlineData("gorilla5", "head")]
    [InlineData("boss1", "body")]
    [InlineData("boss2", "body")]
    [InlineData("ballista3", "hip")]
    [InlineData("ballista4", "hip")]
    public void The_body_is_found_among_the_limbs(string chassis, string part)
    {
        if (!Exported)
            return;
        var model = HeroDonors.Chassis[chassis];
        var rig = Shape("Classic", model);

        Assert.Equal(part, rig[HeroHull.Body(rig, UpperBody("Classic", model))].Name);
    }

    [Fact]
    public void Every_chip_has_a_drawing_of_its_own()
    {
        var drawings = WiredChips.All.Select(c => HangarGlyphs.Of(WiredKind.Chip, c.Id)).ToList();

        Assert.All(drawings, Assert.NotNull);
        Assert.Equal(drawings.Count, drawings.Distinct().Count());
        Assert.Equal(drawings.Count, drawings.Select(d => string.Join('/', d!.SelectMany(l => l))).Distinct().Count());
    }

    [Fact]
    public void Every_chassis_and_escort_has_a_drawing_until_its_photograph_is_there()
    {
        Assert.All(WiredCatalogue.Chassis, c => Assert.NotNull(HangarGlyphs.Of(WiredKind.Chassis, c.Id)));
        Assert.All(WiredCatalogue.Tiers, t => Assert.True(HangarGlyphs.All.ContainsKey(t.SubType ?? ""), t.SubType));
        Assert.All(HangarGlyphs.All.Values.SelectMany(g => g).SelectMany(l => l), v => Assert.InRange(v, 0, HangarGlyphs.Grid));
    }

    private static Matrix4x4 World(RigShape[] rig, int part)
    {
        var place = Matrix4x4.Identity;
        for (var p = part; p >= 0; p = rig[p].Parent == p ? -1 : rig[p].Parent)
            place *= rig[p].Rest;
        return place;
    }
}

[CollectionDefinition(nameof(HeroRigTests), DisableParallelization = true)]
public class HeroRigCollection;
