using System.Text.RegularExpressions;
using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Planets;
using Machines.Sim.Navigation;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The deck format's stage 7 parts (plan §4 D): the high stack 'h', the SLIDES rails, variants, the props' ids below the
/// doors' (1000 + 10 a door) and a scenario with every artefact id once.
/// </summary>
public class DeckParserTests
{
    private static string Repo(params string[] parts) =>
        Path.Combine([Path.GetDirectoryName(Path.GetDirectoryName(RepoPaths.OriginalDataFolder()))!, .. parts]);

    private const string Grid = "DECK t 6 4\nGRID\n######\n#.hh.#\n#.hh.#\n######\nENDGRID\n";

    [Fact]
    public void A_high_stack_is_a_wall_eight_metres_tall_to_the_sim()
    {
        var deck = DeckParser.Parse(Grid);
        Assert.Equal(DeckCell.High, deck.Cell(2, 1));
        Assert.True(Deck.Known('h'));
        var nav = new NavGrid(deck);
        var walker = new Mobility(false, 0, false);
        Assert.True(nav.IsFree(1, 1, walker));
        Assert.False(nav.IsFree(2, 1, walker));
        Assert.False(nav.IsFree(3, 2, new Mobility(false, 0, true)));
        var heights = TerrainHeights.FromDeck(deck);
        Assert.Equal(TerrainHeights.DeckWallHeight, heights.At(new System.Numerics.Vector2(3, 2)));
        Assert.Equal(0, heights.At(new System.Numerics.Vector2(1.5f, 1.5f)));
    }

    [Fact]
    public void Slides_carry_a_prop_and_a_slide_without_one_is_refused()
    {
        var props = "PROPS\nbox crate_large 1.5 1.5 0 7\nENDPROPS\n";
        var deck = DeckParser.Parse(Grid + props + "SLIDES\nrail box 1.5 1 1.5 3 lane_1\nENDSLIDES\n");
        var slide = Assert.Single(deck.Slides);
        Assert.Equal(("rail", "box", new System.Numerics.Vector2(1.5f, 3)), (slide.Name, slide.Prop, slide.To));
        Assert.Equal(["lane_1"], slide.Tags);
        Assert.NotEqual(DeckParser.Parse(Grid + props).Fingerprint, deck.Fingerprint);
        Assert.Throws<ParmFormatException>(() => DeckParser.Parse(Grid + props + "SLIDES\nrail crate 1 1 1 3\nENDSLIDES\n"));
    }

    [Fact]
    public void A_variant_fills_its_zones_only_when_asked_for()
    {
        var text = Grid + "ZONES\nshort 1 1 2 3 variant=short cells=h\nENDZONES\n";
        Assert.Equal(DeckCell.Floor, DeckParser.Parse(text).Cell(1, 1));
        var shortWay = DeckParser.Parse(text, "short");
        Assert.Equal(DeckCell.High, shortWay.Cell(1, 1));
        Assert.Equal(DeckCell.Floor, shortWay.Cell(4, 1));
        Assert.Equal("short", shortWay.Variant);
        Assert.Throws<ParmFormatException>(() => DeckParser.Parse(text, "long"));
    }

    [Fact]
    public void Props_keep_their_ids_once_and_below_the_doors()
    {
        Assert.Throws<ParmFormatException>(() => DeckParser.Parse(Grid + "PROPS\na crate_large 1 1 0 7\nb crate_large 4 1 0 7\nENDPROPS\n"));
        Assert.Throws<ParmFormatException>(() => DeckParser.Parse(Grid + $"PROPS\na crate_large 1 1 0 {DeckParser.DoorTagBase}\nENDPROPS\n"));
    }

    /// <summary>The artefact ids of a scenario's AT lines given more than once.</summary>
    private static List<int> DuplicateIds(string scenario) =>
        [.. Regex.Matches(scenario, @"^AT .* ID (\d+)", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value))
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key)];

    [Fact]
    public void The_prologue_scenario_gives_every_artefact_its_own_id_and_the_doors_theirs_from_1000()
    {
        Assert.Equal([1010], DuplicateIds("AT 1 1 0 0 ID 1010\t// door B\nAT 2 2 0 0 ID 1010\t// prop\nAT 3 3 0 0 ID 311\n"));
        var scenario = File.ReadAllText(Repo("data", "maps", "prolog.scn"));
        Assert.Empty(DuplicateIds(scenario));
        var deck = DeckParser.Load(Repo("data", "planets", "prolog", "prolog.deck"));
        Assert.All(deck.Props, p => Assert.InRange(p.Id, 1, DeckParser.DoorTagBase - 1));
        // Every closed door's leaves carry GameWorld.DoorTag (and one more for the second leaf): the .scn and the sim agree.
        foreach (Match line in Regex.Matches(scenario, @"^AT .* ID (\d+)\t// door (\w) ", RegexOptions.Multiline))
        {
            var tag = GameWorld.DoorTag(deck, line.Groups[2].Value[0])!.Value;
            Assert.InRange(int.Parse(line.Groups[1].Value), tag, tag + 1);
        }
        Assert.Equal(DeckParser.DoorTagBase + 10 * 20, GameWorld.DoorTag(deck, 'N'));
    }
}
