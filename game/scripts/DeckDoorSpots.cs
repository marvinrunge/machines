using System.Runtime.CompilerServices;
using Machines.Import.Model;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// Where a deck's door or gate is, open or shut: the middle of the grid's cells of its letter (a door's leaves are gone
/// while it stands open, and gate N's for good once the heavy Grunt has burst it). Found once a deck and kept.
/// </summary>
public static class DeckDoorSpots
{
    private static readonly ConditionalWeakTable<Deck, Dictionary<char, NVector2?>> Found = new();

    /// <summary>The middle of door <paramref name="letter"/> on <paramref name="deck"/>, metres; null when the deck has no such door.</summary>
    public static NVector2? Middle(Deck deck, char letter)
    {
        var doors = Found.GetValue(deck, _ => []);
        lock (doors)
        {
            if (doors.TryGetValue(letter, out var known))
                return known;
            var sum = NVector2.Zero;
            var count = 0;
            for (var y = 0; y < deck.Depth; y++)
                for (var x = 0; x < deck.Width; x++)
                    if (deck.At(x, y) == letter)
                    {
                        sum += new NVector2(x + 0.5f, y + 0.5f);
                        count++;
                    }
            return doors[letter] = count > 0 ? sum / count : null;
        }
    }
}
