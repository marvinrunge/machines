using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// A ship deck's doors (plan WP-B): a closed door is an active artefact on its slot (tools/deck/deck.mjs writes them into
/// the scenario, tagged 100 + 10 × the door's place in the deck's DOORS, the second leaf one more). Opening one takes it off
/// the map whole: its cells are free again, and the room machines need round them is measured anew.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The scenario tag of a deck door's first leaf (the second's is one more), or null if the deck has no such door.</summary>
    public static int? DoorTag(Deck deck, char letter)
    {
        for (var i = 0; i < deck.Doors.Count; i++)
        {
            if (deck.Doors[i].Letter == letter)
                return 100 + 10 * i;
        }
        return null;
    }

    /// <summary>The artefacts standing in a deck door's slot (both leaves of a double door), in id order; none when it is open.</summary>
    public IReadOnlyList<Artefact> DoorLeaves(char letter) =>
        Deck is { } deck && DoorTag(deck, letter) is { } tag
            ? _artefacts.Values.Where(a => a.Tag == tag || a.Tag == tag + 1).OrderBy(a => a.Id).ToList()
            : [];

    /// <summary>Whether a deck door stands closed: an artefact still stands in its slot.</summary>
    public bool DoorClosed(char letter) => DoorLeaves(letter).Count > 0;

    /// <summary>Opens a deck door: its leaves are taken off the map (<see cref="RemoveArtefact"/>). False when it was open already.</summary>
    public bool OpenDoor(char letter)
    {
        var leaves = DoorLeaves(letter);
        foreach (var leaf in leaves)
            RemoveArtefact(leaf.Id);
        return leaves.Count > 0;
    }

    /// <summary>
    /// Takes an artefact off the map whole (a door that opens, a crate carried away): no wreckage, no blast, no linked damage.
    /// Its ground is free again and nothing on a line meets it any more. False when there is no such artefact.
    /// </summary>
    public bool RemoveArtefact(int id)
    {
        if (!_artefacts.Remove(id, out var artefact))
            return false;
        if (artefact.Boundary.Count >= 3)
            Nav.SetArea(artefact.Boundary, present: false);
        // What stands on a line is filed by count; one gone and another set down in the same tick would leave it stale.
        _artefactsFiled = -1;
        Raise(new GameEvent(GameEventKind.ArtefactRemoved, artefact.Id, artefact.Position, artefact.Tag));
        return true;
    }
}
