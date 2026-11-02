using System.Numerics;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The scenarios' RESTRICT_CONSTRUCTION lines, applied as MachLogScenario::parseRestrictConstruction applied them.</summary>
public class ConstructionRestrictionTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));
    private static readonly Lazy<ScenarioCatalogue> Catalogue = new(() => ScenarioCatalogueParser.Load(RepoPaths.OriginalData("sceninfo.dat")));

    private static readonly ConstructionOrderEntry MilitaryFactory5 = new("FACTORY", "MILITARY", 5);
    private static readonly ConstructionOrderEntry Sentry4 = new("MISSILE_EMPLACEMENT", "SENTRY", 4);

    /// <summary>Dogbar with a Behemoth for Red and Blue, whose constructors reach everything in <c>global.cst</c>.</summary>
    private static GameWorld Dogbar(bool restrict)
    {
        var data = DataLoader.Value;
        var entry = Catalogue.Value.CampaignScenarios.First(s => s.Name == "dogbar");
        var world = MatchSetup.Create(data, "dogbar", entry.Planet, MissionScript.CampaignPlayers(data.LoadScenario("dogbar")), seed: 3,
            new MatchOptions { Victory = VictoryCondition.Scripted, RestrictConstruction = restrict });
        foreach (var race in new[] { Race.Red, Race.Blue })
        {
            // Beside one of Red's Dozers; Blue has no constructor of its own here.
            var near = world.Machines.First(m => m.Race == race && (m.IsType("CONSTRUCTOR") || race != Race.Red));
            world.AddMachine(race, near.Position + new Vector2(30, 0), "CONSTRUCTOR", "BEHEMOTH", 5, null);
            world.Races[race].Bmu = 10000;
        }
        return world;
    }

    [Fact]
    public void Dogbar_keeps_the_level_5_military_factory_and_level_4_sentry_from_red()
    {
        var world = Dogbar(restrict: true);
        Assert.True(world.Races[Race.Red].Locked.SetEquals([MilitaryFactory5, Sentry4]));
        var red = world.Buildable(Race.Red);
        Assert.DoesNotContain(MilitaryFactory5, red);
        Assert.DoesNotContain(Sentry4, red);
        // Only the named entries: those before and after them in global.cst stay open.
        Assert.Contains(new ConstructionOrderEntry("FACTORY", "MILITARY", 3), red);
        Assert.Contains(new ConstructionOrderEntry("MISSILE_EMPLACEMENT", "SENTRY", 3), red);
        Assert.Contains(new ConstructionOrderEntry("FACTORY", "MILITARY", 4), red);
        Assert.Contains(new ConstructionOrderEntry("MISSILE_EMPLACEMENT", "ICBM", 5), red);
        // The lines name Red: Blue, the AI, builds them as before.
        Assert.Empty(world.Races[Race.Blue].Locked);
        Assert.Contains(MilitaryFactory5, world.Buildable(Race.Blue));
        Assert.Contains(Sentry4, world.Buildable(Race.Blue));
    }

    [Fact]
    public void Red_cannot_place_a_locked_building_in_dogbar_but_could_without_the_restrictions()
    {
        var open = Dogbar(restrict: false);
        Assert.Contains(MilitaryFactory5, open.Buildable(Race.Red));
        var behemoth = open.Machines.Single(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR", "BEHEMOTH"));
        var spot = FreeSpot(open, behemoth.Position, MilitaryFactory5);

        var restricted = Dogbar(restrict: true);
        Assert.Equal("your constructors cannot build that yet",
            restricted.WhyCannotPlace(Race.Red, "FACTORY", "MILITARY", 5, spot, 0));
        Assert.Null(restricted.WhyCannotPlace(Race.Red, "FACTORY", "MILITARY", 4, spot, 0));

        // A place order for it is turned down too, so a player cannot get round the menu.
        var dozers = restricted.Machines.Where(m => m.Race == Race.Red && m.IsType("CONSTRUCTOR")).Select(m => m.Id).ToList();
        var before = restricted.Constructions.Count();
        restricted.Issue(new PlaceConstructionCommand(Race.Red, dozers, "FACTORY", "MILITARY", 5, spot, 0));
        restricted.Step();
        Assert.Contains(restricted.Rejected, r => r.Command is PlaceConstructionCommand { Race: Race.Red }
            && r.Reason == "your constructors cannot build that yet");
        Assert.Equal(before, restricted.Constructions.Count());
    }

    /// <summary>A spot near <paramref name="near"/> where the building may go.</summary>
    private static Vector2 FreeSpot(GameWorld world, Vector2 near, ConstructionOrderEntry building)
    {
        for (var ring = 1; ring < 20; ring++)
        {
            for (var step = 0; step < ring * 8; step++)
            {
                var angle = step * MathF.Tau / (ring * 8);
                var spot = near + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ring * 15;
                if (world.WhyCannotPlace(Race.Red, building.Type, building.SubType, building.HardwareLevel, spot, 0) is null)
                    return spot;
            }
        }
        throw new InvalidOperationException($"no room for {building} near {near}");
    }
}
