namespace Machines.Game;

/// <summary>
/// How the keys read from the settings settle where a run's actions clash among themselves (<see cref="KeyMap"/>). An
/// action the settings do not name stands on its first key, and a first key may be one the player gave another of a run's
/// actions before that action had it: then the player's choice stands, and the action on its first key moves to the first
/// spare key no action has, until the player says otherwise. Only two keys the player named that clash, or no spare key
/// left, leave every run's action on its first key. Godot-free, so that it can be tested.
/// </summary>
public static class KeyClash
{
    /// <summary>
    /// Settles the clashes among <paramref name="run"/> in <paramref name="keys"/>: the actions of <paramref name="named"/>
    /// keep their keys, and each other action that clashes with one of them moves to the first of <paramref name="spare"/>
    /// that no action has and is added to <paramref name="gaveWay"/>. Two actions clash where they have the same key and
    /// <paramref name="crosses"/> says they work in the same place.
    /// </summary>
    /// <returns>Whether no clash is left among <paramref name="run"/>.</returns>
    public static bool Settle<TAct, TKey>(IDictionary<TAct, TKey> keys, IReadOnlyList<TAct> run, IReadOnlySet<TAct> named,
        Func<TAct, TAct, bool> crosses, IReadOnlyList<TKey> spare, ICollection<TAct> gaveWay) where TAct : notnull
    {
        var same = EqualityComparer<TKey>.Default;
        bool Clash(TAct a, TAct b) => !EqualityComparer<TAct>.Default.Equals(a, b) && same.Equals(keys[a], keys[b]) && crosses(a, b);
        foreach (var act in run.Where(act => !named.Contains(act)))
        {
            if (!run.Any(other => named.Contains(other) && Clash(act, other)))
                continue;
            var free = spare.Where(key => !keys.Values.Contains(key, same)).ToList();
            if (free.Count == 0)
                return false;
            keys[act] = free[0];
            gaveWay.Add(act);
        }
        return !run.Any(a => run.Any(b => Clash(a, b)));
    }
}
