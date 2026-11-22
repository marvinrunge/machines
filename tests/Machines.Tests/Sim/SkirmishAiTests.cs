using System.Numerics;
using System.Text.Json.Nodes;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The computers' skirmish rules (<see cref="MatchOptions.SkirmishAi"/>): a machine joins the squad that lacks its kind,
/// a geo locator drives to the nearest deposit not yet found, a computer without a mine saves for one, and a hard one
/// builds the best of a machine it has researched. Campaign missions and recordings from before keep the AI as it was.
/// </summary>
public class SkirmishAiTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld NewMatch(string scenario, bool tuned, AiLevel level = AiLevel.Normal) =>
        MatchSetup.Create(Data, scenario, scenario,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed: 7,
            new MatchOptions { SkirmishAi = tuned, AiLevel = level });

    private static void Run(GameWorld world, float seconds, Func<bool>? until = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (until?.Invoke() == true)
                return;
        }
    }

    [Fact]
    public void A_new_skirmish_plays_the_skirmish_rules_and_a_campaign_mission_does_not()
    {
        Assert.True(new MatchOptions().TunedAi);
        Assert.False(new MatchOptions { Victory = VictoryCondition.Scripted }.TunedAi);
        Assert.False(new MatchOptions { SkirmishAi = false }.TunedAi);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Machines_join_the_squads_that_ordered_them(bool tuned)
    {
        // m_desert's blue script has squads of several kinds (1 Grunt 1, 2 Grunt 2 and an Assassin 2, say): before, the
        // other members filled a machine's place, so it never joined and its squad ordered it again and again.
        var world = NewMatch("m_desert", tuned);
        world.Races[Race.Blue].Bmu = 5000;
        Run(world, 1200);

        var idle = world.Machines.Count(m => m.Race == Race.Blue && !m.IsDead && m.Squad is null && m.Spec.Type is "AGGRESSOR");
        if (tuned)
            Assert.True(idle <= 2, $"{idle} of Blue's fighters in no squad");
        else
            Assert.True(idle > 5, $"only {idle} of Blue's fighters in no squad: the old rule no longer shows");
    }

    [Fact]
    public void A_geo_locator_drives_to_the_nearest_deposit_it_has_not_found()
    {
        var world = NewMatch("m_moon", tuned: true);
        Machine? locator = null;
        Run(world, 120, () => (locator = world.Machines.FirstOrDefault(m => m.Race == Race.Blue && m.Spec.Type == "GEO_LOCATOR" && m.Order is LocateOrder)) is not null);
        Assert.NotNull(locator);

        var target = ((LocateOrder)locator.Order).Target;
        var nearest = world.Sites.Where(s => !s.DiscoveredBy.Contains(Race.Blue) && s.Ore > 0)
            .MinBy(s => Vector2.DistanceSquared(s.Position, locator.Position))!;
        Assert.Equal(nearest.Position, target);

        // And it finds it: the race has a deposit within the first minutes, as the original's AI did.
        Run(world, 300, () => world.Sites.Any(s => s.DiscoveredBy.Contains(Race.Blue)));
        Assert.Contains(world.Sites, s => s.DiscoveredBy.Contains(Race.Blue));
    }

    [Fact]
    public void A_computer_without_a_mine_builds_no_fighters_until_it_has_saved_for_one()
    {
        var world = NewMatch("m_desert", tuned: true);
        Run(world, 1);
        world.Races[Race.Blue].Bmu = 50;
        Run(world, 3);

        Assert.True(world.Races[Race.Blue].Hold);
        var queued = world.Constructions.Where(c => c.Race == Race.Blue && c.IsType("FACTORY")).SelectMany(f => f.ProductionQueue.Skip(1)).ToList();
        Assert.All(queued, k => Assert.Contains(k.Type, (string[])["GEO_LOCATOR", "CONSTRUCTOR", "RESOURCE_CARRIER"]));

        // A player's race never holds, whatever its bank.
        world.Races[Race.Red].Bmu = 0;
        Run(world, 2);
        Assert.False(world.Races[Race.Red].Hold);
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Normal)]
    public void A_hard_computer_builds_the_best_fighter_it_has_researched_for_a_place(AiLevel level)
    {
        // Blue knows every Grunt its level-1 military factory builds; its script's first squads ask for Grunt 1s.
        var world = NewMatch("m_desert", tuned: true, level);
        var blue = world.Races[Race.Blue];
        foreach (var key in Data.FactoryOffer("MILITARY", 1).Where(k => k.SubType == "GRUNT"))
            blue.Researched.Add(key);
        blue.Bmu = 5000;
        var built = new List<ResearchKey>();
        for (var i = 0; i < 400 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            foreach (var e in world.Events.Where(e => e.Kind == GameEventKind.MachineBuilt))
            {
                if (world.Find<Machine>(e.Actor) is { Race: Race.Blue, Spec.SubType: "GRUNT" } grunt)
                    built.Add(new ResearchKey(grunt.Spec.Type, grunt.Spec.SubType, grunt.Stats.HardwareLevel, grunt.WeaponCombo));
            }
        }

        Assert.NotEmpty(built);
        if (level == AiLevel.Hard)
            Assert.DoesNotContain(built, k => k.HardwareLevel == 1);
        else
            Assert.Contains(built, k => k.HardwareLevel == 1);
    }

    [Fact]
    public void A_recording_from_before_the_skirmish_rules_plays_back_with_the_ai_as_it_was()
    {
        // G2, played with the computers as they were; its file as one from before reads, without the field.
        var golden = GoldenCase.FourWay;
        var match = new GoldenMatch(golden, Data);
        var sum = match.PlayTo(golden.Ticks[^1])[0];
        var path = Path.Combine(Path.GetTempPath(), $"skirmish-ai-{Guid.NewGuid():N}{Replay.Extension}");
        try
        {
            match.Recording.Save(path);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.True(json["Match"]!["Options"]!.AsObject().Remove(nameof(MatchOptions.SkirmishAi)));
            File.WriteAllText(path, json.ToJsonString());
            var old = Replay.Load(path);
            Assert.False(old.Match.Options.SkirmishAi);

            var playback = old.Playback(Replay.BuildWorld(Data, old.Match));
            while (playback.World.Tick < golden.Ticks[^1])
                Assert.True(playback.TryStep(), $"the playback stuck at tick {playback.World.Tick}");
            Assert.Equal(sum, playback.World.Checksum());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
