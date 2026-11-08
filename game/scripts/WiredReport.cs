using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>What a line of the score counts.</summary>
public enum WiredScoreKind
{
    Machines,
    Waves,
    Bosses,
    Bmus,
    Difficulty,
}

/// <summary>A line of how a score came about: what it counts, how many of it, and the points that came to.</summary>
public sealed record WiredScoreLine(WiredScoreKind Kind, long Amount, long Points);

/// <summary>What the end screen says of a run, from its statistics. Free of Godot, so the tests can compile it. Wired is the remake's own addition.</summary>
public sealed class WiredReport(WiredStats stats, WiredDifficulty difficulty)
{
    public WiredStats Stats => stats;

    public long Score { get; } = WiredBalance.Score(stats, difficulty);

    /// <summary>The score's parts in the order of its sum, the last being what the difficulty adds. Together they come to <see cref="Score"/>.</summary>
    public IReadOnlyList<WiredScoreLine> Lines
    {
        get
        {
            var lines = new List<WiredScoreLine>
            {
                new(WiredScoreKind.Machines, stats.Kills, stats.KillValue),
                new(WiredScoreKind.Waves, stats.WavesCleared, (long)stats.WavesCleared * WiredBalance.ScorePerWave),
                new(WiredScoreKind.Bosses, stats.BossesKilled, (long)stats.BossesKilled * WiredBalance.ScorePerBoss),
                new(WiredScoreKind.Bmus, stats.BmusEarned, stats.BmusEarned / 2),
            };
            if (WiredBalance.ScorePercent(difficulty) != 100)
                lines.Add(new WiredScoreLine(WiredScoreKind.Difficulty, WiredBalance.ScorePercent(difficulty), Score - lines.Sum(l => l.Points)));
            return lines;
        }
    }

    /// <summary>The hero's weapons as they were used, what did the most damage first; those that never fired do not count.</summary>
    public IReadOnlyList<KeyValuePair<string, WiredWeaponUse>> Weapons =>
        stats.Weapons.Where(w => w.Value.Rounds > 0 || w.Value.Damage > 0).OrderByDescending(w => w.Value.Damage).ThenBy(w => w.Key, StringComparer.Ordinal).ToList();

    /// <summary>A figure as the player's language writes it, thousands set apart.</summary>
    public static string Figure(long figure, bool german) =>
        figure.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', german ? '.' : ',');
}

/// <summary>
/// A run against what stood before it at the same arena and difficulty. Only a best that was there to beat makes a new
/// record: the first run there sets one too, but that is called what it is.
/// </summary>
public sealed record WiredRecordNews(WiredRecord Before, long Score, int Waves, int Kills)
{
    /// <summary>Nothing stood here before, and this run did come to something.</summary>
    public bool First => Before is { Score: 0, Waves: 0, Kills: 0 } && Score > 0;

    public bool NewScore => Score > Before.Score;

    public bool NewWaves => Waves > Before.Waves;

    public bool NewKills => Kills > Before.Kills;

    /// <summary>The best score beaten, which has to have been there.</summary>
    public bool Beaten => NewScore && !First;

    /// <summary>How far short of the best score this run fell.</summary>
    public long Short => Math.Max(0, Before.Score - Score);
}
