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
        PlanetLook.Gloom = 1;
        _gloomKnown = false;
        // Occlusion culling only pays where walls hide most of the world; on open ground it would only cost.
        GetViewport().UseOcclusionCulling = deck is not null;
        // Whose lamp shines and who carries a searchlight is the sim's (GameWorld.StorySight): every client shows the same.
        // While the Locator boots its lamp is dark until the boot tests it, in the view alone (Game.StoryBoot).
        ActorView.LampAllowed = deck is null ? null : id => BootLamp(id) ?? World.StoryLampOn(id);
        ActorView.Searchlight = deck is null ? null : id => World.StorySeekerOf(id)?.Searchlight == true;
        // The trio's lamps came through the update damaged: dim, flickering now and then (the view's alone).
        ActorView.LampDamaged = deck is null ? null
            : id => World.Story is { } run && run.Seats.Any(seat => World.HeroOf(seat)?.Id == id);
        // Switched-off machines have dark eyes and no lamp, the trio's enemies red ones and a red lamp (the view's, Game.StoryEyes).
        ActorView.EyesOf = deck is null ? null : StoryEyes;
        _deckLights.Clear();
        if (deck is null)
        {
            Ticked -= DeckTicked;
            return TerrainBuilder.Build(World.Planet, HeightField.Current!, map, _assets.PlanetTerrain(request.Planet));
        }
        _deckView = DeckView.Build(deck);
        CutsceneTold -= DeckHears;
        CutsceneTold += DeckHears;
        StartDeckShow();
        return _deckView;
    }

    /// <summary>
    /// A cutscene's word for the deck: "lights &lt;on|off|emergency&gt; [group ...]" lights the named groups (LIGHTS), or else
    /// those of the rooms the camera and the Locator are in. Until the director keeps the lights (plan WP-C) this is how a
    /// cutscene switches them.
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
    /// story prop of its name, fitted to its box; kept by their place in the list as every artefact is. Only what the sim
    /// still has where the scenario put it: a checkpoint's world has the doors opened before it open and the core gone from
    /// its first tick (their removal came before the view could hear it), and a crate it set elsewhere (a barricade's) gets
    /// its model there from FollowCrates.
    /// </summary>
    private Node3D DeckArtefacts(ArtefactSection section)
    {
        var root = new Node3D { Name = "Artefacts" };
        var types = section.Types.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        for (var index = 0; index < section.Placements.Count; index++)
        {
            var placement = section.Placements[index];
            if (World.FindTarget(Machines.Sim.World.Artefact.IdBase + index) is not Machines.Sim.World.Artefact { IsDead: false } standing
                || System.Numerics.Vector2.DistanceSquared(standing.Position, new(placement.Position.X, placement.Position.Y)) > 0.01f)
                continue;
            var at = new Vector3(placement.Position.X, 0, placement.Position.Y);
            if (Props.Artefact(types.GetValueOrDefault(placement.TypeName), placement, at) is not { } model)
                continue;
            root.AddChild(model);
            _artefactModels[index] = model;
            if (placement.TypeName == "core")
                _deckView?.CoreLights(at, types.GetValueOrDefault("core")?.Height ?? 14);
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
        SlideDoors();
        ShowDeckStory(delta);
        SightTestIfAsked(delta);
    }

    // The deck doors' leaves sliding open: the sim's slide, the model and where it stood closed.
    private readonly List<(DoorSwing Swing, Node3D Model, Vector3 Closed)> _doorSlides = [];

    // The doors whose sound has played, by letter and the game time they began to open (a door sounds once, not per leaf).
    private readonly HashSet<(char, float)> _doorsSounded = [];

    /// <summary>When the stop's thud comes in door_open (game/assets/story/sfx), seconds into the sound.</summary>
    private const float DoorThudAt = 1.7f;

    /// <summary>
    /// A deck door's leaf the sim took off the map as the door began to open (GameWorld.OpenDoor): its model slides into the
    /// wall beside the doorway as the sim's leaf does (<see cref="DoorSwing"/>), and the door's sound plays once for its
    /// leaves, started or slowed so that the thud of its stop lands as the leaves come to rest (a blast door's deeper).
    /// </summary>
    private void SlideDoorLeaf(DoorSwing swing, Node3D model)
    {
        _doorSlides.Add((swing, model, model.Position));
        if (!_doorsSounded.Add((swing.Letter, swing.From)))
            return;
        var leaves = World.DoorSwings.Where(s => s.Letter == swing.Letter && s.From == swing.From).ToList();
        var middle = leaves.SelectMany(s => s.Boundary).Aggregate(System.Numerics.Vector2.Zero, (sum, p) => sum + p)
            / Math.Max(1, leaves.Sum(s => s.Boundary.Count));
        var seconds = swing.Until - swing.From;
        var (from, pitch) = seconds <= DoorThudAt ? (DoorThudAt - seconds, 1f) : (0f, DoorThudAt / seconds);
        StoryAudio.Play("door_open", new Vector3(middle.X, 2, middle.Y), from, pitch);
    }

    /// <summary>Each frame: the sliding leaves where the sim's slide has them now; one at rest in the wall is gone.</summary>
    private void SlideDoors()
    {
        if (_doorSlides.Count == 0)
            return;
        var now = World.Time + (float)_accumulator;
        for (var i = _doorSlides.Count - 1; i >= 0; i--)
        {
            var (swing, model, closed) = _doorSlides[i];
            var share = IsInstanceValid(model) ? swing.Share(now) : 1;
            if (share >= 1)
            {
                if (IsInstanceValid(model))
                    model.QueueFree();
                _doorSlides.RemoveAt(i);
                continue;
            }
            model.Position = closed + new Vector3(swing.Slide.X, 0, swing.Slide.Y) * share;
        }
    }

    /// <summary>A hero's Lamp key in the story: switches its lamp, the sim's (StoryLampCommand), with the switch's click.</summary>
    private void SwitchStoryLamp()
    {
        if (World.Story is null || RefusesOrders() || World.HeroOf(Me) is null)
            return;
        Issue(new Machines.Sim.Story.StoryLampCommand(Player));
        _audio.PlayGui("gui/sounds/igclick.wav");
    }

    /// <summary>How far the first-person view keeps from a deck's walls, metres.</summary>
    private const float WallClearance = 0.4f;

    /// <summary>
    /// The first-person view's place on a ship deck, clear of its walls and grates: where <paramref name="eye"/> or the ground
    /// round it within <see cref="WallClearance"/> is in one, the eye drawn back along the way to <paramref name="pivot"/> (the
    /// machine's middle) to the first place that is clear, at the same height.
    /// </summary>
    private static Vector3 ClearOfWalls(Machines.Import.Model.Deck deck, Vector3 eye, Vector3 pivot)
    {
        bool Clear(float x, float z)
        {
            foreach (var (dx, dz) in new[] { (0f, 0f), (WallClearance, 0f), (-WallClearance, 0f), (0f, WallClearance), (0f, -WallClearance) })
                if (deck.Cell((int)MathF.Floor(x + dx), (int)MathF.Floor(z + dz)) is Machines.Import.Model.DeckCell.Wall or Machines.Import.Model.DeckCell.High or Machines.Import.Model.DeckCell.Grate)
                    return false;
            return true;
        }
        if (Clear(eye.X, eye.Z))
            return eye;
        for (var step = 1; step <= 10; step++)
        {
            var t = step / 10f;
            var (x, z) = (Mathf.Lerp(eye.X, pivot.X, t), Mathf.Lerp(eye.Z, pivot.Z, t));
            if (Clear(x, z))
                return new Vector3(x, eye.Y, z);
        }
        return new Vector3(pivot.X, eye.Y, pivot.Z);
    }
}
