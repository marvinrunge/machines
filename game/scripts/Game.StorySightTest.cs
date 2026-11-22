using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// A check of the story's stealth by eye (plan WP-D): MACHINES_STORY_SIGHT_TEST=1 (with MACHINES_STORY=prolog) puts the Thresher
// on its round of deck A (R4), its searchlight sweeping at the dwell points, and the Locator hidden in a dark alcove of the
// south clamp row, looking out at the lane, its lamp off and nothing able to hurt it. Every second the console says how
// suspicious the Thresher is, of whom, what it does and the Locator's signature; what its suspicion comes to (suspicious,
// spotted, lost, searching, calm) as it happens. MACHINES_STORY_SIGHT_TEST=x,y[,angle] hides the Locator elsewhere (metres
// of the deck, degrees: 0 east, 90 north); MACHINES_STORY_SIGHT_TEST=x,y,angle,lit leaves its lamp on, so that the Thresher
// finds it and fires at it from where it is (its aim by the distance, StorySight.WildChance): the console says where each
// round strikes, how far from the Locator. Offline only; with MACHINES_SCREENSHOT it makes the picture for the merge.
public partial class Game
{
    private bool _sightTestSet;
    private int? _sightThresher;
    private double _sightLogAt;
    private int _sightNewsTick = -1, _sightShotTick = -1;

    private void SightTestIfAsked(double delta)
    {
        if (_sightTestSet && _sightThresher is null
            || System.Environment.GetEnvironmentVariable("MACHINES_STORY_SIGHT_TEST") is not { Length: > 0 } asked || asked == "0"
            || World.Story is not { } story || World.Deck is not { } deck)
            return;
        if (!_sightTestSet)
        {
            _sightTestSet = true;
            if (Online)
            {
                Godot.GD.PrintErr("sight test: offline only");
                return;
            }
            if (World.HeroOf(story.Locator) is not { } locator)
                return;
            float Number(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            var at = asked.Split(',') is { Length: >= 2 } spot ? new System.Numerics.Vector2(Number(spot[0]), Number(spot[1])) : new System.Numerics.Vector2(67.6f, 16.6f);
            var facing = Godot.Mathf.DegToRad(asked.Split(',') is { Length: >= 3 } angle ? Number(angle[2]) : 60);
            var thresher = World.AddMachine(story.Options.Ship, deck.Marker("spawn_thresher").Position, "AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2");
            World.StoryPatrol(thresher, "patrol_thresher");
            _sightThresher = thresher.Id;
            locator.Position = at;
            locator.Facing = facing;
            World.SetStoryLamp(locator.Id, asked.Split(',') is { Length: >= 4 } lit && lit[3] == "lit");
            if (World.Wired?.Heroes.GetValueOrDefault(story.Locator) is { } state)
                state.InvulnerableUntil = float.MaxValue;
            _aimYaw = facing;
            SyncViews();
            Godot.GD.Print($"sight test: Thresher {thresher.Id} on its round from {thresher.Position}, the Locator hidden at {at} looking {Godot.Mathf.RadToDeg(facing):0}°");
            return;
        }

        var sight = story.Sight;
        if (World.Tick != _sightShotTick && World.HeroOf(story.Locator) is { } target && World.Find(_sightThresher!.Value) is { } shooter)
        {
            _sightShotTick = World.Tick;
            foreach (var e in World.Events.Where(e => e.Kind == GameEventKind.Impact && e.Actor == shooter.Id || e.Kind == GameEventKind.Hit && e.Actor == target.Id && e.Other == shooter.Id))
                Godot.GD.Print($"sight: t={World.Time:0.0} {(e.Kind == GameEventKind.Hit ? "hit" : "struck")} at ({e.Position.X:0.0},{e.Position.Y:0.0}),"
                    + $" {System.Numerics.Vector2.Distance(e.Position, target.Position):0.0} m from the Locator, the Thresher {System.Numerics.Vector2.Distance(shooter.Position, target.Position):0.0} m away");
        }
        foreach (var news in sight.News.Where(n => n.Tick > _sightNewsTick))
            Godot.GD.Print($"sight: t={World.Time:0.0} {news.Kind} seeker {news.Seeker} of {news.Hero} at ({news.Where.X:0},{news.Where.Y:0}) suspicion {news.Suspicion:0.00}");
        if (sight.News.Count > 0)
            _sightNewsTick = sight.News.Max(n => n.Tick);
        _sightLogAt += delta;
        if (_sightLogAt < 1)
            return;
        _sightLogAt = 0;
        if (World.Find(_sightThresher!.Value) is not Machine seeker || World.StorySeekerOf(seeker.Id) is not { } eyes)
            return;
        var hero = World.HeroOf(story.Locator);
        var glimpse = hero is null ? StoryGlimpse.None : World.StoryPerceives(seeker, hero);
        Godot.GD.Print($"sight: t={World.Time:0} thresher at ({seeker.Position.X:0},{seeker.Position.Y:0}) heading {Godot.Mathf.RadToDeg(World.GunHeading(seeker)):0}° {seeker.Order.GetType().Name}"
            + $" suspicion {eyes.Suspicion:0.00} of {eyes.Quarry} glimpse {glimpse} signature {(hero is null ? 0 : World.StorySignature(hero.Id)):0.00}"
            + (World.Time < eyes.DwellUntil ? " dwelling" : ""));
    }
}
