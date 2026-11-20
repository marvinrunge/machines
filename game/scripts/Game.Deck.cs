using Godot;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Game;

// A match on a ship deck (the story's): the deck's own view in place of a planet's ground, its doors and crates shown by the
// story's props, its light indoors (PlanetLook.Interior) and the dark rows of switched-off machines. Every other match keeps
// its planet; none of this does anything there.
public partial class Game
{
    private DeckView? _deckView;

    /// <summary>The deck's view, in a match on a deck (the story's); null on a planet.</summary>
    public DeckView? DeckView => _deckView;

    /// <summary>
    /// The ground of the match: on a deck its view (DeckView), lit indoors and with what is hidden behind its walls left
    /// undrawn; on a planet its terrain. The settings that go with it are set for each match, so none is left over from the
    /// one before.
    /// </summary>
    private Node3D BuildGround(MatchRequest request, Texture2D? map)
    {
        var deck = World.Deck;
        PlanetLook.Interior = deck is not null;
        // Occlusion culling only pays where walls hide most of the world; on open ground it would only cost.
        GetViewport().UseOcclusionCulling = deck is not null;
        ActorView.LampAllowed = deck is null ? null : StoryLamp;
        _clampSpots = deck is null ? [] : [.. deck.Markers.Where(m => m.Tags.Contains("clamp")).Select(m => m.Position)];
        if (deck is null)
            return TerrainBuilder.Build(World.Planet, HeightField.Current!, map, _assets.PlanetTerrain(request.Planet));
        _deckView = DeckView.Build(deck);
        return _deckView;
    }

    /// <summary>
    /// The scenario's artefacts on a deck (its doors, gates, vent covers, crates and the computer core), each shown by the
    /// story prop of its name, fitted to its box; kept by their place in the list as every artefact is.
    /// </summary>
    private Node3D DeckArtefacts(ArtefactSection section)
    {
        var root = new Node3D { Name = "Artefacts" };
        var types = section.Types.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        for (var index = 0; index < section.Placements.Count; index++)
        {
            var placement = section.Placements[index];
            var at = new Vector3(placement.Position.X, 0, placement.Position.Y);
            if (Props.Artefact(types.GetValueOrDefault(placement.TypeName), placement, at) is not { } model)
                continue;
            root.AddChild(model);
            _artefactModels[index] = model;
        }
        return root;
    }

    // Where the deck's clamps hold their machines.
    private System.Numerics.Vector2[] _clampSpots = [];

    /// <summary>
    /// Whether a machine's lamp may shine in a story: not while it hangs switched off in a clamp (the dark rows of the
    /// maintenance decks, Ike and Bob before they are woken), the Locator's always. Until the sim keeps each machine's lamp
    /// (plan WP-D, StoryLampCommand) this is the view's rule; the director's lamp state is to take its place here.
    /// </summary>
    private bool StoryLamp(int id)
    {
        if (World.Find(id) is not Machine machine)
            return true;
        if (World.Story is { Seats.Count: > 0 } story && World.HeroOf(story.Locator)?.Id == id)
            return true;
        var at = machine.Position;
        foreach (var spot in _clampSpots)
            if (System.Numerics.Vector2.DistanceSquared(spot, at) < 1.5f * 1.5f)
                return false;
        return true;
    }
}
