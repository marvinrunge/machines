using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The story's stealth (plan WP-D, GameWorld.StorySight): the Thresher's searchlight sees only in its cone, walls hide and
/// grates do not, the dark hides what neither a beam, a lit room nor its own lamp lights, unless it comes near enough to
/// touch; a hero's signature carries to the seekers' ears; suspicion grows into a hunt, a lost hunt into a search, and the
/// search back into the round; the Thresher cannot be hurt; the lamps and lights are the sim's; and all of it plays alike.
/// </summary>
public class StorySightTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    /// <summary>The prologue as a person starts it alone: the trio Red, the ship Blue, every Grunt the computer's.</summary>
    private static GameWorld Prologue(int seed = 3) =>
        MatchSetup.Create(Data, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Unscripted = true }, FogOfWar = false });

    private static Machine Locator(GameWorld world) => world.HeroOf(world.Story!.Locator)!;

    private static WiredState LocatorState(GameWorld world) => world.Wired!.Heroes[world.Story!.Locator];

    private static Machine Thresher(GameWorld world, Vector2 at, float facing = 0)
    {
        var thresher = world.AddMachine(Race.Blue, at, "AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");
        thresher.Facing = facing;
        return thresher;
    }

    private static Machine Reaper(GameWorld world, Vector2 at, float facing = 0)
    {
        var reaper = world.AddMachine(Race.Blue, at, "AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE");
        reaper.Facing = facing;
        return reaper;
    }

    /// <summary>Sets the Locator down still at a point of the deck, its lamp as asked, nothing can hurt it, and lets two ticks pass so its signature is that of standing there.</summary>
    private static Machine Place(GameWorld world, Vector2 at, bool lamp)
    {
        var hero = Locator(world);
        hero.Position = at;
        LocatorState(world).InvulnerableUntil = float.MaxValue;
        world.SetStoryLamp(hero.Id, lamp);
        world.Step();
        world.Step();
        return hero;
    }

    private static List<StorySightNews> Run(GameWorld world, float seconds, Action? eachTick = null)
    {
        var news = new List<StorySightNews>();
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            eachTick?.Invoke();
            world.Step();
            news.AddRange(world.Story!.Sight.News.Where(n => n.Tick == world.Tick - 1));
        }
        return news;
    }

    // The middle of the Thresher's lane on deck A (R4), which the deck leaves dark.
    private static readonly Vector2 Lane = new(192, 52);

    // ---- seeing ----------------------------------------------------------------------------------------

    [Fact]
    public void The_thresher_has_a_searchlight_and_cannot_be_hurt_in_the_prologue_and_a_reaper_neither()
    {
        var world = Prologue();
        var thresher = Thresher(world, Lane);
        var reaper = Reaper(world, Lane + new Vector2(0, 2));
        world.Step();

        Assert.Equal(new[] { thresher.Id, reaper.Id }, world.Story!.Sight.Seekers.Keys);
        Assert.True(world.StorySeekerOf(thresher.Id)!.Searchlight);
        Assert.True(world.StorySeekerOf(thresher.Id)!.Unkillable);
        Assert.False(world.StorySeekerOf(reaper.Id)!.Searchlight);
        Assert.False(world.StorySeekerOf(reaper.Id)!.Unkillable);
    }

    [Fact]
    public void A_searchlight_sees_only_inside_its_cone()
    {
        var world = Prologue();
        var thresher = Thresher(world, Lane, facing: 0);
        var hero = Place(world, Lane + new Vector2(30, 0), lamp: false);

        // Dark, but in its beam 30 m along the lane.
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(thresher, hero));
        // Turned away, it does not see even a lamp at that distance.
        thresher.Facing = MathF.PI / 2;
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, hero));
        world.SetStoryLamp(hero.Id, true);
        world.Step();
        thresher.Facing = MathF.PI / 2;
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, hero));
        // 20° off its heading is inside; 35° is not.
        world.SetStoryLamp(hero.Id, false);
        world.Step();
        thresher.Facing = 20 * MathF.PI / 180;
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(thresher, hero));
        thresher.Facing = 35 * MathF.PI / 180;
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, hero));
    }

    [Fact]
    public void A_wall_hides_a_lit_hero_and_a_grate_does_not()
    {
        var world = Prologue();
        // Every door open, so only walls and grates stand between.
        foreach (var door in world.Deck!.Doors)
            world.OpenDoor(door.Letter);
        // From the parallel corridor P1 into the escape duct V2: through a grate window at x 112-114, a wall at x 110.
        var hero = Place(world, new Vector2(243, 267), lamp: true);
        var throughGrate = Reaper(world, new Vector2(243, 284), facing: -MathF.PI / 2);
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(throughGrate, hero));
        hero.Position = new Vector2(235, 267);
        world.Step();
        world.Step();
        var throughWall = Reaper(world, new Vector2(235, 284), facing: -MathF.PI / 2);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(throughWall, hero));

        // Deck A's lane against the store room R5 behind its north wall, in the Thresher's beam were the wall not there.
        var behind = Place(world, new Vector2(148, 116), lamp: true);
        var thresher = Thresher(world, new Vector2(148, 52), facing: MathF.PI / 2);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, behind));
    }

    [Fact]
    public void The_dark_hides_a_still_hero_at_twenty_metres_and_light_shows_it_at_thirty_five()
    {
        var world = Prologue();
        var reaper = Reaper(world, Lane, facing: 0);
        var hero = Place(world, Lane + new Vector2(20, 0), lamp: false);
        Assert.Equal("off", world.StoryLightAt(hero.Position));
        Assert.Equal(0.05f, world.StorySignature(hero.Id), 4);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(reaper, hero));

        // Its own lamp shows it further off.
        hero = Place(world, Lane + new Vector2(35, 0), lamp: true);
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(reaper, hero));

        // So does a lit room, its lamp off.
        hero = Place(world, Lane + new Vector2(35, 0), lamp: false);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(reaper, hero));
        Assert.True(world.SetStoryLights("deckA", "on"));
        Assert.Equal("on", world.StoryLightAt(hero.Position));
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(reaper, hero));
        // A red emergency light only near by.
        Assert.True(world.SetStoryLights("deckA", "emergency"));
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(reaper, hero));
        hero = Place(world, Lane + new Vector2(12, 0), lamp: false);
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(reaper, hero));
        Assert.False(world.SetStoryLights("deckA", "bright"));
        Assert.False(world.SetStoryLights("nowhere", "on"));

        // A searchlight's beam lights 35 m along it, but not 45 (the Reaper out of its way, on deck B).
        reaper.Position = new Vector2(216, 184);
        world.SetStoryLights("deckA", "off");
        var thresher = Thresher(world, Lane, facing: 0);
        hero = Place(world, Lane + new Vector2(35, 0), lamp: false);
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(thresher, hero));
        hero = Place(world, Lane + new Vector2(45, 0), lamp: false);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, hero));
        // With its own lamp off it has no beam.
        world.SetStoryLamp(thresher.Id, false);
        hero = Place(world, Lane + new Vector2(30, 0), lamp: false);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, hero));
    }

    [Fact]
    public void Near_enough_to_touch_a_hero_is_seen_whatever_the_light_or_way()
    {
        var world = Prologue();
        // Looking the other way, its lamp off.
        var thresher = Thresher(world, Lane, facing: MathF.PI);
        world.SetStoryLamp(thresher.Id, false);
        var hero = Place(world, Lane + new Vector2(6, 0), lamp: false);
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(thresher, hero));
        hero = Place(world, Lane + new Vector2(10, 0), lamp: false);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, hero));
    }

    [Fact]
    public void A_commanders_night_eyes_see_in_the_dark()
    {
        var world = Prologue();
        var reaper = Reaper(world, Lane, facing: 0);
        var hero = Place(world, Lane + new Vector2(30, 0), lamp: false);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(reaper, hero));
        var commander = world.AddMachine(Race.Blue, Lane - new Vector2(10, 0), "ADMINISTRATOR", "COMMANDER", 5, "LR_PULSE_CANNON_X2");
        world.Issue(new FormSquadronCommand(Race.Blue, [reaper.Id, commander.Id], 1));
        world.Step();
        Assert.Same(commander, world.CommanderFor(reaper));
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(reaper, hero));
        // Further than 100 m from him it is as dark as ever.
        commander.Position = Lane + new Vector2(0, 110);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(reaper, hero));
    }

    // ---- the signature ---------------------------------------------------------------------------------

    [Fact]
    public void The_signature_adds_the_lamp_a_shot_the_pulse_and_loud_wheels()
    {
        var world = Prologue();
        var hero = Place(world, Lane, lamp: false);
        Assert.Equal(StorySight.Still, world.StorySignature(hero.Id), 4);
        world.SetStoryLamp(hero.Id, true);
        world.Step();
        Assert.Equal(StorySight.Still + StorySight.Lamp, world.StorySignature(hero.Id), 4);
        world.SetStoryLamp(hero.Id, false);
        LocatorState(world).LastShotAt = world.Time;
        world.Step();
        Assert.Equal(StorySight.Still + StorySight.Shot, world.StorySignature(hero.Id), 4);
        world.Step(); // the shot stays for three seconds
        Assert.Equal(StorySight.Still + StorySight.Shot, world.StorySignature(hero.Id), 4);
        LocatorState(world).LastShotAt = float.NegativeInfinity;
        LocatorState(world).LocateReadyAt = world.Time + WiredBalance.LocateCooldown;
        world.Step();
        Assert.Equal(StorySight.Still + StorySight.Pulse, world.StorySignature(hero.Id), 4);
        Run(world, StorySight.PulseFor + 0.1f);
        Assert.Equal(StorySight.Still, world.StorySignature(hero.Id), 4);

        // A Grunt at its walk's full speed (StoryDrive) is twice as loud as the Locator would be.
        var story = world.Story!;
        var grunt = world.HeroOf(story.Seats[1])!;
        Assert.Equal(Locomotion.Wheels, grunt.Stats.Locomotion);
        world.SetStoryLamp(grunt.Id, false);
        grunt.Position = Lane + new Vector2(0, 3);
        world.Step();
        for (var i = 0; i < 3; i++)
        {
            grunt.Position += new Vector2(grunt.Stats.Speed * StoryDrive.Walk * GameWorld.Dt, 0);
            world.Step();
        }
        Assert.Equal(StorySight.Still + StorySight.Moving * StorySight.Wheels, world.StorySignature(grunt.Id), 3);
        grunt.Position += new Vector2(grunt.Stats.Speed * StoryDrive.Walk * GameWorld.Dt / 2, 0);
        world.Step();
        Assert.Equal(StorySight.Still + StorySight.Moving * StorySight.Wheels / 2, world.StorySignature(grunt.Id), 3);
    }

    // ---- lamps --------------------------------------------------------------------------------------------

    [Fact]
    public void The_lamp_is_the_sims_and_the_lamp_command_switches_a_heros_own()
    {
        var world = Prologue();
        var story = world.Story!;
        var locator = Locator(world);
        var ike = world.HeroOf(story.Seats[1])!;
        // The Locator's shines; Ike hangs dark in his clamp; a machine elsewhere shines.
        Assert.True(world.StoryLampOn(locator.Id));
        Assert.False(world.StoryLampOn(ike.Id));
        var reaper = Reaper(world, Lane);
        Assert.True(world.StoryLampOn(reaper.Id));

        var before = world.Checksum();
        world.Issue(new StoryLampCommand(Race.Red, false) { Issuer = story.Locator });
        world.Step();
        Assert.Empty(world.Rejected);
        Assert.False(world.StoryLampOn(locator.Id));
        world.Issue(new StoryLampCommand(Race.Red) { Issuer = story.Locator });
        world.Step();
        Assert.True(world.StoryLampOn(locator.Id));
        // Ike's player switches Ike's.
        world.Issue(new StoryLampCommand(Race.Red, true) { Issuer = story.Seats[1] });
        world.Step();
        Assert.True(world.StoryLampOn(ike.Id));
        Assert.NotEqual(before, world.Checksum());

        // The ship has no lamp key; outside a story there is nothing to switch.
        world.Issue(new StoryLampCommand(Race.Blue, false) { Issuer = PlayerId.Of(Race.Blue) });
        world.Step();
        Assert.Equal("only the trio has lamps", world.Rejected.Single().Reason);
        var skirmish = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai });
        skirmish.Issue(new StoryLampCommand(Race.Red, false));
        skirmish.Step();
        Assert.Equal("no story", skirmish.Rejected.Single().Reason);
        Assert.True(skirmish.StoryLampOn(1));
    }

    [Fact]
    public void The_grunts_lamps_follow_the_locators_until_a_grunts_own_key_switches_it()
    {
        var world = Prologue();
        var story = world.Story!;
        var locator = Locator(world);
        var (ike, bob) = (world.HeroOf(story.Seats[1])!, world.HeroOf(story.Seats[2])!);
        // Out of their clamps, both shine with the Locator.
        ike.Position = new Vector2(60, 80);
        bob.Position = new Vector2(68, 80);
        Assert.True(world.StoryLampOn(ike.Id) && world.StoryLampOn(bob.Id));

        // The Locator dims its lamp to sneak: theirs go dark with it, and their signatures lose the lamp's share.
        world.Issue(new StoryLampCommand(Race.Red, false) { Issuer = story.Locator });
        world.Step();
        Assert.False(world.StoryLampOn(ike.Id));
        Assert.False(world.StoryLampOn(bob.Id));
        world.Step();
        Assert.True(world.StorySignature(ike.Id) < StorySight.Lamp, $"Ike's signature {world.StorySignature(ike.Id)}");

        // Bob's own key: his lamp is his from then on, the Locator's switch no longer moves it; Ike's still follows.
        world.Issue(new StoryLampCommand(Race.Red, true) { Issuer = story.Seats[2] });
        world.Step();
        Assert.True(world.StoryLampOn(bob.Id));
        Assert.False(world.StoryLampOn(ike.Id));
        world.Issue(new StoryLampCommand(Race.Red, true) { Issuer = story.Locator });
        world.Issue(new StoryLampCommand(Race.Red, false) { Issuer = story.Seats[2] });
        world.Step();
        Assert.True(world.StoryLampOn(ike.Id));
        Assert.False(world.StoryLampOn(bob.Id));
    }

    // ---- suspicion, hunt, search -------------------------------------------------------------------------

    [Fact]
    public void A_patrol_dwells_at_its_points_sweeping_its_beam()
    {
        var world = Prologue();
        var thresher = Thresher(world, world.Deck!.Marker("spawn_thresher").Position);
        var patrol = world.StoryPatrol(thresher, "patrol_thresher");
        Assert.Equal(6, patrol.Points.Count);
        Assert.Equal(new[] { 0, 3 }, patrol.Pauses);
        Place(world, new Vector2(54, 47), lamp: false);

        // It walks to its first point (32 m west) and stands there sweeping about south, then goes on.
        var eyes = world.StorySeekerOf(thresher.Id)!;
        var stood = new List<(float Time, float Facing, Vector2 At)>();
        for (var i = 0; i < 40 * GameWorld.TicksPerSecond; i++)
        {
            world.Step();
            if (world.Time < eyes.DwellUntil && eyes.DwelledAt == 0)
                stood.Add((world.Time, thresher.Facing, thresher.Position));
            else if (stood.Count > 0 && world.Time > eyes.DwellUntil + 2)
                break;
        }
        Assert.NotEmpty(stood);
        Assert.True(Vector2.Distance(stood[0].At, patrol.Points[0]) < 3, $"it dwelt at {stood[0].At}");
        Assert.InRange(stood[^1].Time - stood[0].Time, StorySight.DwellTime - 0.2f, StorySight.DwellTime);
        Assert.All(stood, s => Assert.True(Vector2.Distance(s.At, stood[0].At) < 0.05f));
        var south = -MathF.PI / 2;
        var off = stood.Select(s => MathF.IEEERemainder(s.Facing - south, 2 * MathF.PI) * 180 / MathF.PI).ToList();
        Assert.True(off.Max() > 10 && off.Min() < -10, $"it swept {off.Min():0}° to {off.Max():0}° about south");
        // Then it goes on to its next point.
        Assert.Equal(1, Assert.IsType<PatrolOrder>(thresher.Order).Next);
        Assert.True(Vector2.Distance(thresher.Position, stood[0].At) > 1);
    }

    [Fact]
    public void Suspicion_grows_into_a_hunt_a_lost_hunt_into_a_search_and_the_search_back_into_the_round()
    {
        var world = Prologue();
        var thresher = Thresher(world, world.Deck!.Marker("spawn_thresher").Position);
        world.StoryPatrol(thresher, "patrol_thresher");
        // Lit by its own lamp, ahead of the Thresher in the lane.
        var hero = Place(world, new Vector2(148, 50), lamp: true);
        var eyes = world.StorySeekerOf(thresher.Id)!;

        var news = Run(world, 20, () => { });
        var spotted = news.FindIndex(n => n.Kind == StorySightKind.Spotted);
        Assert.True(spotted >= 0, string.Join(", ", news.Select(n => n.Kind)));
        Assert.Contains(news.Take(spotted), n => n.Kind == StorySightKind.Suspicious && n.Hero == hero.Id);
        var hunt = Assert.IsType<AttackOrder>(thresher.Order);
        Assert.Equal(hero.Id, hunt.Target);
        Assert.IsType<PatrolOrder>(hunt.Back!.Order);
        Assert.Contains(StoryMarks.Spotted, world.Story!.Flags);
        Assert.Equal(1, world.StorySuspicionOf(hero.Id));
        // Spotted, the hero is the Thresher's to pick out.
        Assert.Same(hero, world.NearestEnemy(thresher, 200));

        // It slips away far behind the walls, its lamp off: the hunt goes on blind for a while, then is lost.
        var lastSeen = hero.Position;
        hero.Position = new Vector2(22, 47);
        world.SetStoryLamp(hero.Id, false);
        news = Run(world, StorySight.LoseAfter + 0.5f);
        Assert.Contains(news, n => n.Kind == StorySightKind.Lost && n.Seeker == thresher.Id);
        var search = Assert.IsType<SearchOrder>(thresher.Order);
        Assert.True(Vector2.Distance(search.Spot, lastSeen) < 3, $"searched at {search.Spot}, last seen at {lastSeen}");
        Assert.IsType<PatrolOrder>(search.Back!.Order);
        Assert.DoesNotContain(StoryMarks.Spotted, world.Story.Flags);
        Assert.Null(world.NearestEnemy(thresher, 200));

        // It looks about there, finds nothing and takes up its round again.
        news = Run(world, StorySight.SearchFor + 2);
        Assert.Contains(news, n => n.Kind == StorySightKind.Calm && n.Seeker == thresher.Id);
        Assert.IsType<PatrolOrder>(thresher.Order);
        Assert.Equal(0, eyes.Suspicion);
    }

    [Fact]
    public void A_heard_hero_draws_a_seeker_to_look_without_its_seeing_it()
    {
        var world = Prologue();
        var reaper = Reaper(world, Lane, facing: 0);
        // Dark, 20 m off, behind the Reaper: it does not see it, and still it hears nothing.
        var hero = Place(world, Lane - new Vector2(20, 0), lamp: false);
        Assert.Empty(Run(world, 2));
        Assert.Equal(0, world.StorySeekerOf(reaper.Id)!.Suspicion);
        Assert.Null(world.NearestEnemy(reaper, 200));

        // A shot carries 30 m: it grows suspicious, turns to it and goes to look, but never picks it out.
        var news = Run(world, 2.5f, () => LocatorState(world).LastShotAt = world.Time);
        Assert.Equal(StoryGlimpse.Heard, world.StoryPerceives(reaper, hero));
        Assert.Contains(news, n => n.Kind == StorySightKind.Suspicious && n.Hero == hero.Id);
        Assert.Contains(news, n => n.Kind == StorySightKind.Searching && n.Hero == hero.Id);
        var search = Assert.IsType<SearchOrder>(reaper.Order);
        Assert.True(Vector2.Distance(search.Spot, hero.Position) < 1);
        Assert.DoesNotContain(news, n => n.Kind == StorySightKind.Spotted);
    }

    [Fact]
    public void A_seeker_picks_out_a_hero_only_once_it_has_spotted_it()
    {
        var world = Prologue();
        var reaper = Reaper(world, Lane, facing: 0);
        var hero = Place(world, Lane + new Vector2(30, 0), lamp: true);
        // Seen, but not sure yet.
        Assert.Equal(StoryGlimpse.Seen, world.StoryPerceives(reaper, hero));
        Assert.Null(world.NearestEnemy(reaper, 200));
        var news = Run(world, 1);
        Assert.Contains(news, n => n.Kind == StorySightKind.Spotted);
        Assert.Equal(hero.Id, Assert.IsType<AttackOrder>(reaper.Order).Target);
        Assert.Same(hero, world.NearestEnemy(reaper, 200));
    }

    [Fact]
    public void Nothing_hurts_the_thresher_and_a_hit_gives_the_shooter_away()
    {
        var world = Prologue();
        var thresher = Thresher(world, Lane, facing: MathF.PI);
        var hero = Place(world, Lane + new Vector2(30, 0), lamp: false);
        var (hitPoints, armour) = (thresher.HitPoints, thresher.Armour);
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(thresher, hero));

        world.Damage(thresher, 500, Race.Red, hero, "BOLTER");

        Assert.Equal((hitPoints, armour), (thresher.HitPoints, thresher.Armour));
        Assert.False(thresher.IsDead);
        var eyes = world.StorySeekerOf(thresher.Id)!;
        Assert.Equal(1, eyes.Suspicion);
        Assert.Equal(hero.Id, eyes.Quarry);
        // A Reaper is hurt as ever.
        var reaper = Reaper(world, Lane + new Vector2(0, 3));
        world.Step();
        var before = reaper.HitPoints + reaper.Armour;
        world.Damage(reaper, 3, Race.Red, hero, "BOLTER");
        Assert.True(reaper.HitPoints + reaper.Armour < before);
        Assert.Equal(1, world.StorySeekerOf(reaper.Id)!.Suspicion);
    }

    [Fact]
    public void A_blind_machine_sees_nothing()
    {
        var world = Prologue();
        var reaper = Reaper(world, Lane, facing: 0);
        var hero = Place(world, Lane + new Vector2(4, 0), lamp: true);
        world.StoryWatch(reaper).Blind = true;
        Assert.Empty(Run(world, 3));
        Assert.Equal(StoryGlimpse.None, world.StoryPerceives(reaper, hero));
        Assert.IsType<IdleOrder>(reaper.Order);
    }

    // ---- the same for everyone -----------------------------------------------------------------------------

    [Fact]
    public void Two_runs_of_a_hunt_are_alike_to_the_bit()
    {
        static (ulong Sum, string News) Play()
        {
            var world = Prologue(seed: 11);
            var thresher = Thresher(world, world.Deck!.Marker("spawn_thresher").Position);
            world.StoryPatrol(thresher, "patrol_thresher");
            var reaper = Reaper(world, new Vector2(216, 172));
            world.StoryPatrol(reaper, "patrol_thresher");
            var hero = Place(world, new Vector2(136, 28), lamp: false);
            var news = new List<StorySightNews>();
            for (var i = 0; i < 60 * GameWorld.TicksPerSecond; i++)
            {
                // The Locator creeps along the south aisle and back, its lamp now and then on.
                hero.Position = new Vector2(136 + 100 * MathF.Abs(MathF.Sin(i / 400f)), 28);
                if (i % 200 == 0)
                    world.SetStoryLamp(hero.Id, i % 400 == 0);
                world.Step();
                news.AddRange(world.Story!.Sight.News.Where(n => n.Tick == world.Tick - 1));
            }
            return (world.Checksum(), string.Join(";", news));
        }

        var (first, firstNews) = Play();
        var (second, secondNews) = Play();
        Assert.Equal(first, second);
        Assert.Equal(firstNews, secondNews);
        Assert.NotEmpty(firstNews);
    }
}
