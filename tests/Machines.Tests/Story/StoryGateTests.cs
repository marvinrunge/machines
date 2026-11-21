using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Story;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Story;

/// <summary>
/// The gate audit (plan WP-A2): the story borrows Wired's heroes as a battle does, and with them what a battle refuses of a
/// run (<see cref="GameWorld.Borrowed"/>): no hangar, no shop or equipping, no waves, no pod and no body coming back at it,
/// no towers, no squad, no wrecks or crates to take up, no alliance and no self-destruct. A battle keeps its own words for
/// all of it, and a Wired run is not touched.
/// </summary>
public class StoryGateTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    /// <summary>The prologue as a person starts it alone: the trio's race theirs, the ship the computer's.</summary>
    private static GameWorld Prologue(int seed = 3) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions(), FogOfWar = false });

    private static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    /// <returns>Why the story refused the command, given by the Locator, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command with { Issuer = world.Story!.Locator });
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    private static WiredState Locator(GameWorld world) => world.Wired!.Heroes[world.Story!.Locator];

    [Fact]
    public void Only_a_battle_and_the_story_borrow_the_heroes()
    {
        Assert.True(Prologue().Borrowed);
        Assert.True(Battles.New().Borrowed);
        var wired = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player }, 1,
            new MatchOptions { Wired = new WiredOptions(), FogOfWar = false });
        Assert.NotNull(wired.Wired);
        Assert.False(wired.Borrowed);
        Assert.False(MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }).Borrowed);
    }

    [Fact]
    public void The_hangar_is_refused_in_the_story()
    {
        var world = Prologue();
        var state = Locator(world);

        Assert.Equal("there is no hangar in the story", Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Equal("there is no hangar in the story", world.WhyNoHangar(state));
        Assert.False(state.HangarOpen);
        // A hangar left open by anything else is shut on the next tick: the story has none to stand in.
        state.HangarOpen = true;
        world.Step();
        Assert.False(state.HangarOpen);
    }

    [Fact]
    public void Equipping_buying_and_readying_are_refused_in_the_story()
    {
        var world = Prologue();
        var state = Locator(world);
        var bolter = state.AddLoot("BOLTER", 1);
        var worn = state.Loadout;

        Assert.Equal("there is no hangar in the story", Do(world, new WiredEquipCommand(Race.Red, bolter.Id, WiredSlot.Weapon(Mounting.Left))));
        Assert.Equal("there is no hangar in the story", Do(world, new WiredEquipCommand(Race.Red, bolter.Id, WiredSlot.Weapon(Mounting.Left), Add: true)));
        Assert.Equal("there is no hangar in the story", Do(world, new WiredBuyCommand(Race.Red, "BOLTER", null)));
        Assert.Equal("there is no hangar in the story", Do(world, new WiredReadyCommand(Race.Red)));
        Assert.Equal(worn, state.Loadout);
        Assert.False(state.Loadout.Holds(bolter.Id));
    }

    [Fact]
    public void No_wave_ever_starts_in_ten_minutes_of_a_story()
    {
        var world = Prologue();
        var countdown = world.Wired!.Countdown;

        var events = Run(world, 600);

        Assert.DoesNotContain(events, e => e.Kind is GameEventKind.WiredWaveStarted or GameEventKind.WiredSpecialWave);
        Assert.Equal(WiredPhase.Story, world.Wired.Phase);
        Assert.Equal(0, world.Wired.Wave);
        Assert.Equal(countdown, world.Wired.Countdown);
        Assert.Empty(world.Wired.Groups);
        Assert.Empty(world.WiredPickups);
        Assert.All(world.Wired.Heroes.Values, state => Assert.Empty(state.Offer));
    }

    [Fact]
    public void A_fallen_grunt_never_comes_back_at_a_pod()
    {
        var world = Prologue();
        var story = world.Story!;
        var seat = story.Seats[1];
        var state = world.Wired!.Heroes[seat];
        world.HeroOf(seat)!.HitPoints = 0;

        var events = Run(world, WiredBalance.RespawnTime * 10);

        Assert.Null(world.HeroOf(seat));
        Assert.Null(state.Hero);
        Assert.Null(state.RespawnAt);
        Assert.False(state.WaitsForWave);
        Assert.Null(world.WiredPod(seat));
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.WiredHeroRespawned);
        Assert.False(story.Over);
        // Even a body the run's rules would bring has no pod to come back at.
        state.RespawnAt = world.Time;
        events = Run(world, 1);
        Assert.Null(world.HeroOf(seat));
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.WiredHeroRespawned);
    }

    [Fact]
    public void The_story_has_no_towers_no_squad_and_no_sides_to_change()
    {
        var world = Prologue();
        var state = Locator(world);
        var hero = world.HeroOf(world.Story!.Locator)!;

        Assert.Equal("there is no hangar in the story", Do(world, new WiredTowerCommand(Race.Red, "turret1", hero.Position, 0)));
        Assert.Equal("no towers in the story", world.WhyNoScrap(state, 1));
        Assert.Equal("there is no squad in the story", world.WhyNoRally(state));
        Assert.Equal("sides are fixed in the story", Do(world, new AllianceCommand(Race.Red, Race.Blue, Ally: true)));
        Assert.Equal("nothing self-destructs in the story", Do(world, new SelfDestructCommand(Race.Red, [hero.Id])));
        Assert.True(world.AreEnemies(Race.Red, Race.Blue));
        Assert.Null(hero.SelfDestructAt);
    }

    [Fact]
    public void A_hero_of_the_story_takes_up_no_wreck()
    {
        var world = Prologue();
        var hero = world.HeroOf(world.Story!.Locator)!;
        var state = Locator(world);
        var reaper = world.AddMachine(Race.Blue, hero.Position + new Vector2(1, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Damage(reaper, 1_000_000, Race.Red);
        world.Step();
        var wreck = Assert.Single(world.Debris);
        var bank = state.Bank;

        hero.Position = wreck.Position;
        var events = Run(world, 1);

        Assert.Contains(wreck, world.Debris);
        Assert.DoesNotContain(events, e => e.Kind is GameEventKind.WiredWreckCollected or GameEventKind.WiredCrateCollected);
        Assert.Equal(bank, state.Bank);
        Assert.Equal(0, state.Stats.WrecksCollected);
    }

    [Fact]
    public void A_battle_keeps_its_own_words_for_what_it_refuses()
    {
        var world = Battles.Begun();
        var state = world.Wired!.Heroes[PlayerId.Of(Race.Red)];

        Assert.Equal("there is no hangar in a battle", Battles.Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Equal("there is no hangar in a battle", Battles.Do(world, new WiredEquipCommand(Race.Red, 1, WiredSlot.Weapon(Mounting.Left))));
        Assert.Equal("there is no hangar in a battle", world.WhyNoHangar(state));
        Assert.Equal("no towers in a battle", Battles.Do(world, new WiredTowerCommand(Race.Red, "turret1", Battles.Hero(world, Race.Red).Position, 0)));
        Assert.Equal("no towers in a battle", world.WhyNoScrap(state, 1));
        Assert.Equal("there is no squad in a battle", world.WhyNoRally(state));
        Assert.Equal("teams are fixed in a battle", Battles.Do(world, new AllianceCommand(Race.Red, Race.Blue, Ally: false)));
        Assert.Equal("nothing self-destructs in a battle", Battles.Do(world, new SelfDestructCommand(Race.Red, [Battles.Hero(world, Race.Red).Id])));
    }
}
