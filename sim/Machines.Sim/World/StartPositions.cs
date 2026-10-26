using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// Who plays which start position of a skirmish map, and in which colour. A start position is one of the
/// scenario's races; the colour is only how it looks.
/// </summary>
/// <param name="Players">Start position (scenario race) and who controls it.</param>
/// <param name="Colours">The colour each start position is shown in.</param>
public sealed record StartPositions(Race PlayerSlot, IReadOnlyDictionary<Race, Controller> Players, IReadOnlyDictionary<Race, Race> Colours)
{
    /// <summary>
    /// Follows MachGuiStartupScreens: with fixed starts the player takes the position of their colour and the AI
    /// races the first free ones; with random starts every position is drawn at random. The player keeps their
    /// colour and the AI races take the unused colours in order.
    /// </summary>
    /// <param name="slots">The scenario's races, the possible start positions.</param>
    public static StartPositions Assign(IReadOnlyList<Race> slots, int players, Race colour, bool random, Random rng)
    {
        if (slots.Count == 0)
            throw new ArgumentException("the map has no start positions", nameof(slots));
        players = Math.Clamp(players, 1, slots.Count);

        var order = slots.OrderBy(r => r).ToList();
        Race playerSlot;
        List<Race> aiSlots;
        if (random)
        {
            var shuffled = order.OrderBy(_ => rng.Next()).ToList();
            playerSlot = shuffled[0];
            aiSlots = shuffled.Skip(1).Take(players - 1).ToList();
        }
        else
        {
            playerSlot = order.Contains(colour) ? colour : order[0];
            aiSlots = order.Where(r => r != playerSlot).Take(players - 1).ToList();
        }

        var controllers = new Dictionary<Race, Controller> { [playerSlot] = Controller.Player };
        var colours = new Dictionary<Race, Race> { [playerSlot] = colour };
        var unused = Enum.GetValues<Race>().Where(c => c != colour).ToList();
        foreach (var slot in aiSlots.OrderBy(r => r))
        {
            controllers[slot] = Controller.Ai;
            // Keep a position's own colour when it is free, so fixed starts look as the map was made.
            var look = unused.Contains(slot) ? slot : unused[0];
            colours[slot] = look;
            unused.Remove(look);
        }
        return new StartPositions(playerSlot, controllers, colours);
    }
}
