using Godot;

namespace Machines.Game;

// The story's sound in the match (StoryAudio): made only when a story is played, so every other match sounds as before.
public partial class Game
{
    private StoryAudio? _storyAudio;

    /// <summary>
    /// A story chapter has its own sound in place of the soundtrack: the menu's tune fades out, and <see cref="StoryAudio"/>
    /// plays the lines, the rooms, the sound effects and the music by threat. It reads the world (the Thresher, the section,
    /// the Locator's health) and changes nothing in it.
    /// </summary>
    private void SetUpStoryAudio()
    {
        if (World.Story is not { } story)
            return;
        Music.Silence();
        var locator = story.Locator;
        _storyAudio = new StoryAudio
        {
            Deck = World.Deck,
            ThresherDistance = listener => ThresherDistance(listener, World.Players.RaceOf(locator)),
            // The most suspicious seeker's suspicion of this player's hero (the sight's, GameWorld.StorySuspicionOf).
            Suspicion = () => World.HeroOf(Me) is { } mine ? World.StorySuspicionOf(mine.Id) : 0,
            Section = () => World.Story?.Section ?? 1,
            LocatorHealth = () => World.HeroOf(locator) is { MaxHitPoints: > 0 } hero ? (float)hero.HitPoints / hero.MaxHitPoints : 1,
            Shown = () => !Hidden,
            Over = () => World.Story?.Over ?? true,
        };
        AddChild(_storyAudio);
    }

    // How far the nearest hostile Thresher (Wired's Reaper 4) is from where the story is heard, on the ground; null without one.
    private float? ThresherDistance(Vector3 listener, Machines.Import.Model.Race race)
    {
        float? nearest = null;
        foreach (var machine in World.Machines)
        {
            if (machine.IsDead || machine.Race == race || !machine.IsType("AGGRESSOR", "ASSASSIN") || machine.Stats.HardwareLevel != 4)
                continue;
            var distance = new Vector2(machine.Position.X - listener.X, machine.Position.Y - listener.Z).Length();
            if (nearest is null || distance < nearest)
                nearest = distance;
        }
        return nearest;
    }
}
