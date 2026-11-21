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
        // Whose lamp shines and who carries a searchlight is the sim's (GameWorld.StorySight): every client shows the same.
        ActorView.LampAllowed = deck is null ? null : World.StoryLampOn;
        ActorView.Searchlight = deck is null ? null : id => World.StorySeekerOf(id)?.Searchlight == true;
        _deckLights.Clear();
        if (deck is null)
            return TerrainBuilder.Build(World.Planet, HeightField.Current!, map, _assets.PlanetTerrain(request.Planet));
        _deckView = DeckView.Build(deck);
        CutsceneTold -= DeckHears;
        CutsceneTold += DeckHears;
        return _deckView;
    }

    /// <summary>
    /// A cutscene's word for the deck: "lights &lt;on|off|emergency&gt; [group ...]" lights the named groups (LIGHTS), or else
    /// those of the rooms the camera and the Locator are in. Until the director keeps the lights (plan WP-C) this is how a
    /// cutscene (Erwischt) switches them.
    /// </summary>
    private void DeckHears(string words)
    {
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (_deckView is null || World.Deck is not { } deck || parts.Length < 2 || parts[0] != "lights")
            return;
        IEnumerable<string> groups = parts[2..];
        if (parts.Length == 2)
        {
            var places = new List<System.Numerics.Vector2>();
            if (GetViewport().GetCamera3D() is { } camera)
                places.Add(new(camera.GlobalPosition.X, camera.GlobalPosition.Z));
            if (World.Story is { Seats.Count: > 0 } story && World.HeroOf(story.Locator) is { } hero)
                places.Add(hero.Position);
            var zones = deck.Zones.Where(z => places.Any(z.Contains)).Select(z => z.Name).ToHashSet();
            groups = deck.Lights.Where(l => zones.Contains(l.Zone)).Select(l => l.Name);
        }
        foreach (var group in groups.ToList())
            _deckView.SetLights(group, parts[1]);
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

    // How the sim had each light group when the view last followed it.
    private readonly Dictionary<string, string> _deckLights = new(StringComparer.Ordinal);

    /// <summary>
    /// Each frame of a match on a deck: the deck's light groups follow the sim's (GameWorld.StoryLightState, which the director
    /// switches), a group only when the sim changed it, so what a cutscene's words lit in the view (DeckHears) stays until
    /// then. The machines' lamps follow the sim by themselves (ActorView.LampAllowed). And the stealth check, if asked.
    /// </summary>
    private void FollowDeck(double delta)
    {
        if (_deckView is null || World.Deck is not { } deck)
            return;
        foreach (var light in deck.Lights)
        {
            if (World.StoryLightState(light.Name) is not { } state)
                continue;
            if (!_deckLights.TryGetValue(light.Name, out var shown))
                _deckLights[light.Name] = shown = light.State;
            if (shown == state)
                continue;
            _deckLights[light.Name] = state;
            _deckView.SetLights(light.Name, state);
        }
        SightTestIfAsked(delta);
    }

    /// <summary>A hero's Lamp key in the story: switches its lamp, the sim's (StoryLampCommand), with the switch's click.</summary>
    private void SwitchStoryLamp()
    {
        if (World.Story is null || RefusesOrders() || World.HeroOf(Me) is null)
            return;
        Issue(new Machines.Sim.Story.StoryLampCommand(Player));
        _audio.PlayGui("gui/sounds/igclick.wav");
    }
}
