using Godot;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// The story's companions on the client (plan WP-F): the Locator's orders to the Grunts, the Use key held for a revive, a
// switch, a weapon or the pod's hatch, a Grunt's person ramming, and the crates the Grunts carry shown where the world set
// them down. The world decides everything (GameWorld.Companions); here it is only asked and shown.
public partial class Game
{
    /// <summary>The Grunts' order as the story's command, with the point the Locator's sight rests on (the distraction's).</summary>
    partial void SendGruntOrder(KeyMap.Act act, System.Numerics.Vector2 point)
    {
        var order = act switch
        {
            KeyMap.Act.GruntWait => CompanionOrder.Wait,
            KeyMap.Act.GruntDistract => CompanionOrder.Distract,
            _ => CompanionOrder.Follow,
        };
        Issue(new StoryOrderCommand(Player, order, point));
    }

    // Whether this player holds Use down on something the world took up.
    private bool _storyUseHeld;

    /// <summary>
    /// The Use key in a story, beside the hack's (HackKey): pressed where nothing is to be hacked but something to be used
    /// (a downed Grunt, a switch, a rack, the hatch), it holds Use down in the world until the key comes up. True when the
    /// event was the key's.
    /// </summary>
    private bool StoryUse(InputEvent @event)
    {
        if (World.Story is not { } story || Watching || MatchOver)
            return false;
        if (KeyMap.IsUp(@event, KeyMap.Act.Use))
        {
            if (!_storyUseHeld)
                return false;
            _storyUseHeld = false;
            Issue(new StoryUseCommand(Player, Held: false));
            return true;
        }
        if (!KeyMap.Is(@event, KeyMap.Act.Use) || HackShown || World.HeroOf(Me) is not { } hero || HackInReach(story, hero)
            || World.StoryUseTarget(Me) is not { } target)
            return false;
        _storyUseHeld = true;
        Issue(new StoryUseCommand(Player, target));
        return true;
    }

    /// <summary>Whether a target still to be hacked is in reach: then the key is the hack's (<see cref="TryOpenHack"/>).</summary>
    private bool HackInReach(StoryRun story, Machine hero) =>
        story.Hacks.Targets(World.Deck).Any(t => !story.Hacks.Done.Contains(t.Target) && !story.Hacks.Locked.Contains(t.Target)
            && System.Numerics.Vector2.Distance(t.Position, hero.Position) <= GameWorld.HackReach)
        && World.Story?.Seats.IndexOf(Me) == 0;

    /// <summary>A Grunt's person rams with the dash's key (a Grunt has no thrusters); the Locator dashes as a hero does.</summary>
    private void StoryDash()
    {
        if (World.Story is { } story && story.Seats.IndexOf(Me) > 0)
            Issue(new StoryRamCommand(Player));
        else
            Dash();
    }

    // The crates set down again whose model is made, by their place in the scenario and where they stand.
    private readonly HashSet<(int Placement, System.Numerics.Vector2 At)> _cratesShown = [];

    /// <summary>
    /// Each frame of a story: a crate the Grunts carried off is taken off the view with the world's ArtefactRemoved; where the
    /// world sets it down again (a barricade's slot, back on the pile) it gets its model anew, as the deck's artefacts have.
    /// </summary>
    private void FollowCrates()
    {
        CompanionTestIfAsked();
        if (World.Story is null || GetNodeOrNull<Node3D>("Artefacts") is not { } root)
            return;
        foreach (var artefact in World.Artefacts)
        {
            if (artefact.IsDead || _artefactModels.ContainsKey(artefact.Placement) || !_cratesShown.Add((artefact.Placement, artefact.Position)))
                continue;
            var placement = new ArtefactPlacement(artefact.Type.Name, new System.Numerics.Vector3(artefact.Position, 0), 0, artefact.Tag, 0);
            if (Props.Artefact(artefact.Type, placement, new Vector3(artefact.Position.X, 0, artefact.Position.Y)) is not { } model)
                continue;
            root.AddChild(model);
            _artefactModels[artefact.Placement] = model;
        }
    }
}
