using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Import.Planets;
using Machines.Import.Scenarios;

namespace Machines.Tests.Import;

/// <summary>Reads all 47 shipped planets (<c>.psf</c>, <c>.csp</c>, <c>.env</c>) and checks them against the scenarios.</summary>
public class PlanetTests
{
    private static string PlanetFolder => Path.Combine(RepoPaths.OriginalDataFolder(), "models", "planet");

    private static readonly Lazy<IReadOnlyDictionary<string, Planet>> AllPlanets = new(() =>
        Directory.GetDirectories(PlanetFolder).Select(Planet.Load).ToDictionary(p => p.Name));

    private static IReadOnlyDictionary<string, Planet> Planets => AllPlanets.Value;

    [Fact]
    public void Every_shipped_planet_parses()
    {
        var errors = new List<string>();
        foreach (var folder in Directory.GetDirectories(PlanetFolder))
        {
            try { Planet.Load(folder); }
            catch (ParmFormatException e) { errors.Add(e.Message); }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        Assert.Equal(47, Planets.Count);
        Assert.Equal(15330, Planets.Values.Sum(p => p.Surface.Tiles.Count));
    }

    [Fact]
    public void Desert_surface_matches_the_file()
    {
        var surface = Planets["desert"].Surface;

        Assert.Equal((10, 10, 80f, 800f), (surface.TilesX, surface.TilesY, surface.TileSize, surface.Width));
        Assert.Equal((4, 4), (surface.TileCellsX, surface.TileCellsY));
        Assert.Equal((new Vector2(-40, -40), new Vector2(40, 40)), (surface.TileMin, surface.TileMax));
        Assert.Equal("desert.bg", surface.BackgroundFile);
        Assert.Equal(new TerrainTile(0, 0, 1, -0.065f, @"lod\ariz06.lod"), surface.Tile(0, 0));
        Assert.Equal(new TerrainTile(5, 0, 0, -0.065f, @"lod\ariz04_1.lod"), surface.Tile(5, 0));
    }

    [Fact]
    public void Desert_config_space_matches_the_file()
    {
        var space = Planets["desert"].ConfigSpace;

        // DOMAIN 0 60 460 180 740
        Assert.Equal([new(60, 460), new(180, 460), new(180, 740), new(60, 740)], space.Domains[0].Boundary);
        Assert.Equal(new Portal(0, 1, new(180, 600), new(180, 460), space.Portals[0].LineNumber), space.Portals[0]);

        var first = space.Obstacles[0];
        Assert.Equal(50f, first.Height);
        Assert.Equal([new(60, 800), new(70, 725), new(180, 720), new(195, 800)], first.Polygon);
    }

    [Fact]
    public void Every_portal_joins_existing_domains_and_some_obstacles_are_water_or_low()
    {
        foreach (var planet in Planets.Values)
            Assert.All(planet.ConfigSpace.Portals, p => Assert.True(p.DomainA < planet.ConfigSpace.Domains.Count && p.DomainB < planet.ConfigSpace.Domains.Count));

        Assert.Contains(Planets.Values.SelectMany(p => p.ConfigSpace.Obstacles), o => o.Water);
        Assert.Contains(Planets.Values.SelectMany(p => p.ConfigSpace.Obstacles), o => o.Low);
    }

    [Fact]
    public void Desert_environment_matches_the_file()
    {
        var environment = Planets["desert"].Environment;

        Assert.Equal(new Orbit(-2.5f, 27.5f, 80000, 1000, 1800, 70), environment.Orbits["sun orbit"]);
        Assert.Equal(["sun", "zmoon"], environment.Satellites.Select(s => s.Name));
        Assert.Equal(new Satellite("sun", "sun orbit", "models/planet/desert/sky/sun.x", "sun colours", "sun directional", "sun ambient"), environment.Satellites[0]);
        Assert.Equal(new Fog(250, "fog colours", "sun"), environment.Fog);

        Assert.Equal(new ColourKey(3.75f, new Vector4(1, 0.1f, 0, 1)), environment.ColourTables["sun colours"][0]);
        Assert.Equal(new ColourKey(8, new Vector4(1, 1, 1, 0.2f)), environment.ColourTables["moon colours"][0]);

        var sky = environment.OtherBlocks.Single(b => b.Kind == "dynamic sky");
        Assert.Equal(["mesh file", "controller", "colour table @ 0", "colour table @ 20"], sky.Properties.Select(p => p.Key));
    }

    [Fact]
    public void Night_vision_and_stars_are_kept_as_blocks()
    {
        var environment = Planets["5o1"].Environment;

        var nvg = environment.OtherBlocks.Single(b => b.Kind == "nvg");
        Assert.Equal([0f, 1f, 0.2f], nvg.Properties.Single(p => p.Key == "filter colour").Value.Tuple);
        Assert.Equal(12, Planets.Values.Count(p => p.Environment.OtherBlocks.Any(b => b.Kind == "stars")));
    }

    [Fact]
    public void Broken_references_are_rejected()
    {
        const string text = """
            orbit: "o" { min elevation: 0 max elevation: 10 radius: 1 start time: 0 period: 60 }
            satellite: "sun" { orbit: "o" mesh colour table: "missing" }
            """;

        var error = Assert.Throws<ParmFormatException>(() => EnvironmentParser.Parse(text));
        Assert.Contains("\"missing\"", error.Message);
    }
}
