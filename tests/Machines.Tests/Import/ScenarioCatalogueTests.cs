using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Planets;
using Machines.Import.Scenarios;

namespace Machines.Tests.Import;

/// <summary>Reads <c>sceninfo.dat</c> and checks every menu entry against the scenario and planet files.</summary>
public class ScenarioCatalogueTests
{
    private static readonly Lazy<ScenarioCatalogue> CatalogueData = new(() => ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat")));

    private static ScenarioCatalogue Catalogue => CatalogueData.Value;

    [Fact]
    public void Campaign_has_five_systems_and_twenty_five_missions()
    {
        Assert.Equal(["tutorial", "corinthian", "border", "CoreSystem", "MidianPrime"], Catalogue.Campaign.Select(s => s.Name));
        Assert.Equal(9, Catalogue.Campaign.Sum(s => s.Planets.Count));
        Assert.Equal(25, Catalogue.CampaignScenarios.Count());
        Assert.Equal(5, Catalogue.Campaign[0].Planets.Sum(p => p.Scenarios.Count));
        Assert.Equal(["dogbar"], Catalogue.Campaign.Single(s => s.Name == "border").After);

        // AFTER takes every remaining word: Talon Prime needs both gracko and resfac.
        var talon = Catalogue.Campaign.SelectMany(s => s.Planets).Single(p => p.Name == "talonPrime");
        Assert.Equal(["gracko", "resfac"], talon.After);
    }

    [Fact]
    public void Skirmish_and_multiplayer_lists()
    {
        Assert.Equal(62, Catalogue.Skirmish.Concat(Catalogue.Multiplayer).Sum(t => t.Scenarios.Count));

        var ice = Catalogue.Skirmish.First(t => t.Name == "ice");
        Assert.Equal(MapSize.Small, ice.Size);
        // The same scenario on a day and a night planet.
        Assert.Equal([("ice", "ice"), ("ice", "icedn"), ("micey", "micey"), ("micey", "miceydn")], ice.Scenarios.Select(s => (s.Name, s.Planet)));

        var siege = Catalogue.Skirmish.SelectMany(t => t.Scenarios).First(s => s.Name == "fortseig");
        Assert.Equal((2, 5), (siege.MaxPlayers, siege.Music));
    }

    [Fact]
    public void Campaign_media_and_carried_over_machines()
    {
        var commsat = Catalogue.CampaignScenarios.Single(s => s.Name == "commsat");
        Assert.Equal(Race.Red, commsat.SaveMachines);
        Assert.Equal("flics/gui/v&d/com1vic.bmp", commsat.Media["DEBRIEFING"]);

        var seek = Catalogue.CampaignScenarios.Single(s => s.Name == "1o2");
        Assert.Equal(("seekndes", "flics/1o2final2.smk"), (seek.Planet, seek.Media["WIN"]));
        Assert.Equal(["1o1demo"], seek.After);
    }

    [Fact]
    public void Every_entry_points_at_an_existing_scenario_and_planet()
    {
        var folder = RepoPaths.OriginalDataFolder();
        var names = Catalogue.AllScenarios.Select(s => s.Name).ToHashSet();

        Assert.All(Catalogue.AllScenarios, s => Assert.True(File.Exists(Path.Combine(folder, s.Name + ".scn")), s.Name));
        Assert.All(Catalogue.AllScenarios, s => Assert.True(Directory.Exists(Path.Combine(folder, "models", "planet", s.Planet)), s.Planet));

        // AFTER names any campaign entry: a scenario, a planet or a system.
        var known = names
            .Concat(Catalogue.Campaign.Select(s => s.Name))
            .Concat(Catalogue.Campaign.SelectMany(s => s.Planets).Select(p => p.Name))
            .ToHashSet();
        var afters = Catalogue.CampaignScenarios.SelectMany(s => s.After)
            .Concat(Catalogue.Campaign.SelectMany(s => s.After))
            .Concat(Catalogue.Campaign.SelectMany(s => s.Planets).SelectMany(p => p.After));
        Assert.All(afters, after => Assert.Contains(after, known));
    }

    [Fact]
    public void Everything_a_listed_scenario_places_lies_on_its_planet()
    {
        var folder = RepoPaths.OriginalDataFolder();
        var outside = new List<string>();

        foreach (var entry in Catalogue.AllScenarios.DistinctBy(s => (s.Name, s.Planet)))
        {
            var scenario = ScenarioParser.Load(Path.Combine(folder, entry.Name + ".scn"));
            var surface = SurfaceParser.Load(Path.Combine(folder, "models", "planet", entry.Planet, entry.Planet + ".psf"));
            bool OnPlanet(Vector3 p) => p.X >= 0 && p.Y >= 0 && p.X <= surface.Width && p.Y <= surface.Depth;

            outside.AddRange(scenario.MineralSites.Where(s => !OnPlanet(s.Position)).Select(s => $"{entry.Name}@{entry.Planet}:{s.LineNumber}"));
            foreach (var race in scenario.Races)
            {
                outside.AddRange(race.Machines.Where(m => !OnPlanet(m.Position)).Select(m => $"{entry.Name}@{entry.Planet}:{m.LineNumber}"));
                outside.AddRange(race.Constructions.Where(c => !OnPlanet(c.Position)).Select(c => $"{entry.Name}@{entry.Planet}:{c.LineNumber}"));
            }
        }

        // guns.scn puts a mineral site at y = 1075 on a 600 x 1000 m planet: that ore can never be reached.
        Assert.Equal(["guns@guns:7"], outside);
    }
}
