using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The story, the remake's own: a chapter played in first person by the Locator and the two Grunts it wakes, on a ship deck
/// (<see cref="Deck"/>). The heroes are Wired's (a <see cref="WiredRun"/> in phase <see cref="WiredPhase.Story"/>, with no
/// waves, shop, hangar or pod), as a battle borrows them; what is the story's own is here. Nothing here runs without
/// <see cref="Story"/>.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The story's chapter under way, in a story match; null in every other match.</summary>
    public StoryRun? Story { get; private set; }

    /// <summary>The deck the story is played on: the planet's.</summary>
    public Deck? Deck => Planet.Deck;

    /// <summary>
    /// Starts the chapter: each of the three seats gets its hero, unarmed, at its marker on the deck (the Locator in its
    /// clamp, Ike and Bob in theirs), looking the way the marker does. A Grunt the computer plays follows the Locator for
    /// now. Every checkpoint starts where the chapter does until the director sets the world up for it.
    /// </summary>
    internal void StartStory(StoryOptions options)
    {
        var deck = Planet.Deck ?? throw new ArgumentException($"the story is played on a deck, which planet {Planet.Name} has none of");
        Wired = new WiredRun(new WiredOptions()) { Phase = WiredPhase.Story, Enemy = options.Ship };
        var run = Story = new StoryRun(options);
        var seats = StorySeats.Of(Players, options.Race);
        for (var i = 0; i < seats.Count; i++)
        {
            var player = seats[i];
            var state = WiredState.ForStory(options.Race, StorySeats.Chassis[i], player);
            Wired.Heroes[player] = state;
            run.Seats.Add(player);
            var marker = deck.Marker(StorySeats.Spawns[i]);
            var hero = AddHero(options.Race, marker.Position, HeroBuild.Resolve(Data, state.Loadout), player);
            hero.Facing = marker.Angle * MathF.PI / 180;
            state.Hero = hero.Id;
        }
        // The computer plays every Grunt's seat no person sits at (co-op, GameWorld.StoryCoop).
        SettleStorySeats(run);
    }

    /// <summary>A tick of the story. The director (plan WP-C) will tick here; the skeleton only keeps the run.</summary>
    private void UpdateStory()
    {
        if (Story is not { Over: false })
            return;
        UpdateHacks();
    }

    /// <summary>
    /// A hero of the story fell. The Locator's fall ends the chapter (it starts again from its checkpoint); a Grunt's is
    /// only told, as the companions' own downed state (plan WP-F) will keep them from falling at all.
    /// </summary>
    private void StoryHeroDown(StoryRun run, WiredState state, int id)
    {
        var fell = _events.Find(e => e.Kind == GameEventKind.Destroyed && e.Actor == id);
        state.Hero = null;
        state.Stats.Deaths++;
        Raise(new GameEvent(GameEventKind.WiredHeroDied, id, fell?.Position ?? Vector2.Zero, (int)state.Race, "0", Player: state.Player));
        if (state.Player == run.Locator && !run.Over)
            run.Ending = StoryEnding.LocatorLost;
    }

    /// <summary>
    /// The chapter ends when its run says so: the trio's race is out, the borrowed run ended, and the story tells why. Nobody
    /// wins by fighting; a story match never ends by annihilation (the ship has nothing on the map at first).
    /// </summary>
    private void CheckStoryEnd()
    {
        if (Story is not { Over: true } run || Wired is not { Phase: not WiredPhase.Ended } heroes)
            return;
        heroes.Phase = WiredPhase.Ended;
        if (!Races[run.Options.Race].Defeated)
        {
            Races[run.Options.Race].Defeated = true;
            Raise(new GameEvent(GameEventKind.RaceDefeated, 0, Vector2.Zero, Detail: run.Options.Race.ToString()));
        }
        Raise(new GameEvent(GameEventKind.StoryFailed, 0, Vector2.Zero, (int)run.Options.Race, run.Ending.ToString(), Player: run.Locator));
    }

    /// <summary>The story's state for the checksum: the run and the deck it is played on.</summary>
    private long StoryFingerprint() => Story!.Fingerprint() * 31 + (Deck?.Fingerprint ?? 0);

    /// <summary>A story command applies itself (<see cref="StoryCommand.ApplyTo"/>); outside a story there is none to apply.</summary>
    private string? ApplyStory(StoryCommand command) => Story is null ? "no story" : command.ApplyTo(this);
}
