using System.Diagnostics.CodeAnalysis;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Survival;

/// <summary>
/// Something per player, in id order (every loop over it goes by <see cref="PlayerId"/>). The members that take a
/// <see cref="Race"/> are race forms, "the race's first player", kept for tests and tools: the sim and the net may not call
/// them (<see cref="PlayerId.RaceForm"/>), so their own code always names the player.
/// </summary>
public sealed class PlayerMap<T> : SortedDictionary<PlayerId, T>
{
    /// <summary>Race form: the race's first player's.</summary>
    [Obsolete("Race form: name the player.", DiagnosticId = PlayerId.RaceForm)]
    public T this[Race race]
    {
        get => this[PlayerId.Of(race)];
        set => this[PlayerId.Of(race)] = value;
    }

    /// <summary>Race form: the race's first player's, or the default.</summary>
    [Obsolete("Race form: name the player.", DiagnosticId = PlayerId.RaceForm)]
    public T? GetValueOrDefault(Race race) => GetValueOrDefault(PlayerId.Of(race));

    /// <summary>The player's, or the default.</summary>
    public T? GetValueOrDefault(PlayerId player) => TryGetValue(player, out var value) ? value : default;

    /// <summary>Race form: whether the race's first player has one.</summary>
    [Obsolete("Race form: name the player.", DiagnosticId = PlayerId.RaceForm)]
    public bool ContainsKey(Race race) => ContainsKey(PlayerId.Of(race));

    /// <summary>Race form: the race's first player's.</summary>
    [Obsolete("Race form: name the player.", DiagnosticId = PlayerId.RaceForm)]
    public bool TryGetValue(Race race, [MaybeNullWhen(false)] out T value) => TryGetValue(PlayerId.Of(race), out value);
}

/// <summary>A set of players, in id order. <see cref="Contains(Race)"/> is a race form (see <see cref="PlayerMap{T}"/>).</summary>
public sealed class PlayerSet : SortedSet<PlayerId>
{
    /// <summary>Race form: whether the race's first player is in the set.</summary>
    [Obsolete("Race form: name the player.", DiagnosticId = PlayerId.RaceForm)]
    public bool Contains(Race race) => Contains(PlayerId.Of(race));
}
