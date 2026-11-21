using System.Numerics;
using Machines.Game;
using Machines.Import.Model;
using Machines.Import.Planets;

namespace Machines.Tests.Story;

/// <summary>
/// The view of the prologue's deck as DeckView puts it up (plan WP-L), checked without Godot: the rooms' heights, a wall
/// on every face of the open cells and a ceiling over each (no stars), floors, the doors and grates, the lamps of each
/// light group kept from shining through walls, the dressing standing where the sim keeps machines out, and that every
/// prop it names is in game/assets/story/props.
/// </summary>
public class DeckLayoutTests
{
    private static readonly Lazy<DeckLayout> Loaded = new(() => new DeckLayout(DeckParser.Load(Repo("data", "planets", "prolog", "prolog.deck"))));

    private static DeckLayout Layout => Loaded.Value;
    private static Deck Deck => Layout.Deck;

    private static string Repo(params string[] parts) =>
        Path.GetFullPath(Path.Combine([RepoPaths.OriginalDataFolder(), "..", "..", .. parts]));

    private static IEnumerable<(int X, int Y)> Cells()
    {
        for (var y = 0; y < Deck.Depth; y++)
            for (var x = 0; x < Deck.Width; x++)
                yield return (x, y);
    }

    private static float HeightAt(string marker) =>
        Layout.Height((int)MathF.Floor(Deck.Marker(marker).Position.X), (int)MathF.Floor(Deck.Marker(marker).Position.Y));

    [Fact]
    public void Rooms_ducts_doors_and_grates_are_as_tall_as_the_deck_says()
    {
        Assert.Equal(7, HeightAt("spawn_l7"));
        Assert.Equal(9, HeightAt("spawn_thresher"));
        Assert.Equal(16, HeightAt("hack_core"));
        Assert.Equal(20, HeightAt("pod_3"));
        Assert.All(Cells().Where(c => Deck.Cell(c.X, c.Y) is DeckCell.Duct or DeckCell.Lip), c => Assert.Equal(DeckLayout.DuctHeight, Layout.Height(c.X, c.Y)));
        Assert.All(Cells().Where(c => Layout.IsOpen(c.X, c.Y)), c => Assert.True(Layout.Height(c.X, c.Y) > 0, $"cell {c} has no ceiling height"));
        // A small door, the wide blast door to the spine, the blast doors of the core's anteroom, and a grate into a duct.
        Assert.Equal(DeckLayout.DoorHeight, Opening('A').Height);
        Assert.Equal((8, DeckLayout.WideBlastHeight), (Opening('J').Width, Opening('J').Height));
        Assert.Equal((6, DeckLayout.BlastHeight), (Opening('I').Width, Opening('I').Height));
        Assert.All(Layout.Openings.Where(o => o.Kind == "grate"), o => Assert.True(o.Height <= 4, $"grate at {o.Rect} is {o.Height} m"));
    }

    private static DeckOpening Opening(char letter) => Layout.Openings.Single(o => o.Letter == letter);

    [Fact]
    public void Every_door_and_grate_wall_has_its_opening_across_its_way()
    {
        Assert.All(Deck.Doors, door => Assert.Contains(Layout.Openings, o => o.Letter == door.Letter));
        var grates = Layout.Openings.Where(o => o.Kind == "grate").ToList();
        Assert.Equal(Cells().Count(c => Deck.Cell(c.X, c.Y) == DeckCell.Grate), grates.Sum(g => g.Rect.Width * g.Rect.Depth));
        // The duct's grate windows face the corridors north and south of it: their panels run east-west.
        Assert.All(grates.Where(g => g.Rect.Y0 >= 113 && g.Rect.Y1 <= 122), g => Assert.True(g.AlongX));
        // Door A between bays 6 and 7 is passed east-west: its panel runs north-south.
        Assert.False(Opening('A').AlongX);
    }

    [Fact]
    public void Every_open_cell_has_one_ceiling_at_its_height_so_no_sky_shows()
    {
        var covered = new int[Deck.Width * Deck.Depth];
        foreach (var ceiling in Layout.Ceilings)
            for (var y = ceiling.Rect.Y0; y < ceiling.Rect.Y1; y++)
                for (var x = ceiling.Rect.X0; x < ceiling.Rect.X1; x++)
                {
                    Assert.True(Layout.IsOpen(x, y), $"ceiling over wall at {x},{y}");
                    Assert.Equal(Layout.Height(x, y), ceiling.Height);
                    covered[y * Deck.Width + x]++;
                }
        Assert.All(Cells().Where(c => Layout.IsOpen(c.X, c.Y)), c => Assert.Equal(1, covered[c.Y * Deck.Width + c.X]));
    }

    [Fact]
    public void Every_face_of_an_open_cell_towards_a_wall_or_a_lower_cell_is_walled_up_to_its_ceiling()
    {
        // Unit faces by (line, along, normal): the span each covers.
        var spans = new Dictionary<(float, float, float, float, float), List<(float Bottom, float Top)>>();
        foreach (var face in Layout.Faces)
        {
            Assert.True(face.Top > face.Bottom);
            var along = Vector2.Normalize(face.To - face.From);
            for (var t = 0; t < face.Length; t++)
            {
                var at = face.From + along * (t + 0.5f);
                var key = (at.X, at.Y, face.Normal.X, face.Normal.Y, 0f);
                if (!spans.TryGetValue(key, out var list))
                    spans[key] = list = [];
                list.Add((face.Bottom, face.Top));
            }
        }
        (int, int)[] directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        foreach (var (x, y) in Cells().Where(c => Layout.IsOpen(c.X, c.Y)))
            foreach (var (dx, dy) in directions)
            {
                var (nx, ny) = (x + dx, y + dy);
                var edge = new Vector2(x + 0.5f + dx * 0.5f, y + 0.5f + dy * 0.5f);
                var key = (edge.X, edge.Y, (float)-dx, (float)-dy, 0f);
                var top = Layout.Height(x, y);
                float? bottom = !Layout.IsOpen(nx, ny) ? 0 : Layout.Height(nx, ny) < top ? Layout.Height(nx, ny) : null;
                if (bottom is null)
                    Assert.False(spans.ContainsKey(key), $"a wall stands in the way from {x},{y} to {nx},{ny}");
                else
                    Assert.Equal([(bottom.Value, top)], spans.GetValueOrDefault(key) ?? []);
            }
    }

    [Fact]
    public void The_floor_lies_under_every_open_cell_and_grates_only_where_the_catwalk_runs()
    {
        var blocks = Layout.Floors.ToDictionary(f => (f.X, f.Y));
        foreach (var (x, y) in Cells().Where(c => Layout.IsOpen(c.X, c.Y)))
            Assert.True(blocks.ContainsKey((x / DeckLayout.Module * DeckLayout.Module, y / DeckLayout.Module * DeckLayout.Module)), $"no floor under {x},{y}");
        Assert.Contains(Layout.Floors, f => f.Grate);
        Assert.All(Layout.Floors.Where(f => f.Grate), f => Assert.Contains(Cells(), c => c.X >= f.X && c.X < f.X + 4 && c.Y >= f.Y && c.Y < f.Y + 4 && Deck.Cell(c.X, c.Y) == DeckCell.Catwalk));
    }

    [Fact]
    public void The_occluders_are_the_walls_and_only_the_walls()
    {
        var covered = new int[Deck.Width * Deck.Depth];
        foreach (var rect in Layout.Occluders)
            for (var y = rect.Y0; y < rect.Y1; y++)
                for (var x = rect.X0; x < rect.X1; x++)
                    covered[y * Deck.Width + x]++;
        Assert.All(Cells(), c => Assert.Equal(Layout.IsOpen(c.X, c.Y) ? 0 : 1, covered[c.Y * Deck.Width + c.X]));
    }

    [Fact]
    public void Each_light_group_has_lamps_in_its_zone_and_their_cones_fall_on_no_floor_behind_a_wall()
    {
        foreach (var light in Deck.Lights)
        {
            var zone = Deck.Zone(light.Zone)!;
            var lamps = Layout.Lamps.Where(l => l.Group == light.Name).ToList();
            Assert.Contains(lamps, l => l.Kind == DeckLampKind.Ceiling);
            foreach (var lamp in lamps)
            {
                var at = new Vector2(lamp.Position.X, lamp.Position.Y);
                Assert.True(zone.Contains(at), $"{light.Name}: lamp at {at} outside {zone.Name}");
                Assert.True(Layout.IsOpen((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y)));
                Assert.True(lamp.Position.Z < Layout.Height((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y)), $"{light.Name}: lamp above its ceiling");
                if (lamp.Kind == DeckLampKind.Ceiling && !lamp.Shadowed && lamp.Spread > DeckLayout.MinSpread)
                {
                    var floor = lamp.Position.Z * MathF.Tan(lamp.Spread * MathF.PI / 180);
                    Assert.True(Layout.ClearReach(at, floor + 0.5f) >= floor - 0.01f, $"{light.Name}: the lamp at {at} lights floor behind a wall");
                }
            }
        }
        // The tall rooms (the computer core, the pod bay) have one shadowed lamp each; it is the only kind there is.
        Assert.Equal(["core", "podbay"], Layout.Lamps.Where(l => l.Shadowed).Select(l => l.Group).Order());
        // Groups that start on emergency have red lamps on their walls.
        Assert.Contains(Layout.Lamps, l => l.Group == "bay7_emergency" && l.Kind == DeckLampKind.Wall);
    }

    [Fact]
    public void The_set_pieces_stand_at_their_markers_and_where_machines_cannot_drive_through_them()
    {
        var pieces = Layout.Pieces;
        foreach (var clamp in Deck.Markers.Where(m => m.Tags.Contains("clamp")))
            Assert.Contains(pieces, p => p.Prop == "clamp" && new Vector2(p.Position.X, p.Position.Y) == clamp.Position);
        Assert.Equal(3, pieces.Count(p => p.Prop == "pod_cradle"));
        Assert.Single(pieces, p => p.Prop == "repair_station");
        Assert.Single(pieces, p => p.Prop == "weapon_rack");
        Assert.Single(pieces, p => p.Prop == "bay_crane");
        Assert.Contains(pieces, p => p.Prop == "core_console");
        Assert.Equal(2, pieces.Count(p => p.Prop == "clamp_terminal"));
        Assert.Equal(3, pieces.Count(p => p.Prop == "plate"));
        // What stands on the floor and is not driven into (clamps, cradles, the repair station, plates) or built into the
        // wall (pillars) stands on high cover, which the sim blocks.
        var driveIn = new HashSet<string> { "clamp", "pod_cradle", "repair_station", "plate", "kit_pillar", "kit_door_frame" };
        foreach (var piece in pieces.Where(p => p.Position.Z == 0 && !driveIn.Contains(p.Prop)))
        {
            var cell = Deck.Cell((int)MathF.Floor(piece.Position.X), (int)MathF.Floor(piece.Position.Y));
            Assert.True(cell == DeckCell.Cover || piece.Prop == "junction_box", $"{piece.Prop} at {piece.Position} stands on {cell}");
        }
        // Fittings on the walls hang over the machines' heads.
        Assert.All(pieces.Where(p => p.Prop is "pa_speaker" or "pipe_run" or "cable_hang"), p => Assert.True(p.Position.Z >= 2.5f, $"{p.Prop} at {p.Position} hangs too low"));
        // Every piece of cover holds something.
        Assert.All(Layout.Covers, cover => Assert.Contains(pieces, p => cover.Contains((int)MathF.Floor(p.Position.X), (int)MathF.Floor(p.Position.Y))));
    }

    [Fact]
    public void Every_blast_door_door_and_gate_has_a_frame_that_leaves_its_way_open()
    {
        foreach (var opening in Layout.Openings.Where(o => o.Kind is "blast" or "door" or "gate"))
        {
            var centre = opening.Rect.Centre;
            var frame = Assert.Single(Layout.Pieces, p => p.Prop == "kit_door_frame" && new Vector2(p.Position.X, p.Position.Y) == centre);
            // Its opening is the slot's, and its lintel over the slot unless the room beside is too low for its top.
            Assert.Equal(opening.Width, frame.Scale.X * DeckLayout.FrameOpening, 3);
            Assert.True(frame.Scale.Y * DeckLayout.FrameLintel >= opening.Height - 0.01f || frame.Scale.Y * 8 < opening.Height / DeckLayout.FrameLintel * 8,
                $"the frame of door {opening.Letter} hangs into its way");
        }
    }

    [Fact]
    public void Every_room_a_machine_stands_up_in_has_an_alarm_beacon_on_a_wall_over_the_machines()
    {
        foreach (var room in Layout.Rooms.Where(r => (DeckLayout.ZoneHeight(r) ?? DeckLayout.DefaultHeight) >= 5))
            Assert.Contains(Layout.Beacons, b => room.Contains(new Vector2(b.Position.X, b.Position.Y)));
        Assert.All(Layout.Beacons, b => Assert.True(b.Position.Z >= 3.5f, $"beacon at {b.Position} hangs too low"));
    }

    [Fact]
    public void Every_prop_the_view_takes_is_in_the_story_props()
    {
        var folder = Repo("game", "assets", "story", "props");
        var named = Layout.Pieces.Select(p => p.Prop).Where(p => p != "plate")
            .Concat(["kit_floor", "kit_floor_grate", "kit_wall", "kit_wall_low", "kit_ceiling", "kit_grate_wall", "lamp_emergency"])
            .Distinct();
        Assert.All(named, prop => Assert.True(File.Exists(Path.Combine(folder, prop + ".glb")), $"{prop}.glb is missing"));
    }

    [Fact]
    public void The_layout_is_the_same_every_time()
    {
        var again = new DeckLayout(DeckParser.Load(Repo("data", "planets", "prolog", "prolog.deck")));
        Assert.Equal(Layout.Faces, again.Faces);
        Assert.Equal(Layout.Ceilings, again.Ceilings);
        Assert.Equal(Layout.Lamps, again.Lamps);
        Assert.Equal(Layout.Pieces, again.Pieces);
    }
}
