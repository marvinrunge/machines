namespace Machines.Sim.Survival;

/// <summary>What the hero did with one of its weapons in a run.</summary>
public sealed class WiredWeaponUse
{
    public int Rounds { get; set; }
    public int Kills { get; set; }
    public long Damage { get; set; }
}

/// <summary>
/// What a race has done in a Wired run, for the end screen, the records and the score. Kills and damage dealt count the
/// hero's and its squad's together; the weapons are the hero's own.
/// </summary>
public sealed class WiredStats
{
    public int WavesCleared { get; set; }

    /// <summary>Enemy machines destroyed, and what they cost.</summary>
    public int Kills { get; set; }

    public int KillValue { get; set; }

    public int BossesKilled { get; set; }

    /// <summary>Armour and hit points taken from enemy machines.</summary>
    public long DamageDealt { get; set; }

    /// <summary>Armour and hit points the hero lost.</summary>
    public long DamageTaken { get; set; }

    /// <summary>All the BMUs wrecks and bonuses brought, whatever became of them.</summary>
    public int BmusEarned { get; set; }

    public int WrecksCollected { get; set; }

    public int CratesCollected { get; set; }

    /// <summary>How often the hero fell.</summary>
    public int Deaths { get; set; }

    /// <summary>The waves cleared since the hero last fell.</summary>
    public int CleanStreak { get; set; }

    /// <summary>The longest such row of the run.</summary>
    public int CleanWaves { get; set; }

    /// <summary>The most escorts standing at once.</summary>
    public int LargestSquad { get; set; }

    /// <summary>The use of each weapon, by its name in parmdata.</summary>
    public SortedDictionary<string, WiredWeaponUse> Weapons { get; } = new(StringComparer.Ordinal);

    public WiredWeaponUse Weapon(string name)
    {
        if (!Weapons.TryGetValue(name, out var use))
            Weapons[name] = use = new WiredWeaponUse();
        return use;
    }

    internal void MixInto(WiredHash hash)
    {
        hash.Mix(WavesCleared);
        hash.Mix(Kills);
        hash.Mix(KillValue);
        hash.Mix(BossesKilled);
        hash.Mix(DamageDealt);
        hash.Mix(DamageTaken);
        hash.Mix(BmusEarned);
        hash.Mix(WrecksCollected);
        hash.Mix(CratesCollected);
        hash.Mix(Deaths);
        hash.Mix(CleanStreak);
        hash.Mix(CleanWaves);
        hash.Mix(LargestSquad);
        foreach (var (name, use) in Weapons)
        {
            hash.Mix(name);
            hash.Mix(use.Rounds);
            hash.Mix(use.Kills);
            hash.Mix(use.Damage);
        }
    }
}
