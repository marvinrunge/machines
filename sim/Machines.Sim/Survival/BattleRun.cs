using Machines.Import.Model;

namespace Machines.Sim.Survival;

/// <summary>How a battle came to its end.</summary>
public enum BattleEnd
{
    /// <summary>A team had no hero left standing and no body to come.</summary>
    Wipe,
    /// <summary>A team took the other's pod.</summary>
    Capture,
    /// <summary>The time was up: a draw.</summary>
    Time,
}

/// <summary>The end of a battle: the team that won (0 for the first, 1 for the second) or none for a draw, why, and when.</summary>
public sealed record BattleResult(int? Winner, BattleEnd Reason, float EndedAt);

/// <summary>What a race did in a battle, for its rewards.</summary>
public sealed class BattleTally
{
    /// <summary>The chassis its hero fought on.</summary>
    public string Chassis { get; init; } = "";

    /// <summary>Hit points (armour included) it took from enemy heroes.</summary>
    public int Damage { get; set; }

    /// <summary>Enemy heroes it dealt the last hit to, and their chassis, in order.</summary>
    public int Kills => Killed.Count;

    public List<string> Killed { get; } = [];

    /// <summary>
    /// Capture points it earned on the enemy pod: its share as it stood at the end. A share lost to a hit, a step out of the
    /// circle or a fall takes its points with it, so that a pod's column never adds up to more than the pod and nobody
    /// earns by stepping in and out.
    /// </summary>
    public float Capture { get; set; }

    public int Deaths { get; set; }

    /// <summary>Whether its hero stood when the battle ended.</summary>
    public bool AliveAtEnd { get; set; }

    internal void MixInto(WiredHash hash)
    {
        hash.Mix(Chassis);
        hash.Mix(Damage);
        foreach (var chassis in Killed)
            hash.Mix(chassis);
        hash.Mix(BitConverter.SingleToInt32Bits(Capture));
        hash.Mix(Deaths);
        hash.Mix(AliveAtEnd ? 1 : 0);
    }
}

/// <summary>
/// A battle under way, the remake's own team mode: two teams, each with a pod, their capture of each other's pod, what each
/// race did and how it ended. The heroes themselves are <see cref="WiredState"/>s of the world's <see cref="WiredRun"/>.
/// </summary>
public sealed class BattleRun
{
    public BattleRun(BattleOptions options, IReadOnlyList<Race> teamA, IReadOnlyList<Race> teamB, float openingEnds)
    {
        Options = options;
        Teams = [teamA, teamB];
        OpeningEnds = openingEnds;
    }

    /// <summary>The options as the battle plays them, every hero resolved.</summary>
    public BattleOptions Options { get; }

    /// <summary>The two teams' races; the first of each is the anchor, whose pod is the team's.</summary>
    public IReadOnlyList<IReadOnlyList<Race>> Teams { get; }

    /// <summary>Each team's pod by id; null where the scenario has none.</summary>
    public int?[] Pods { get; } = new int?[2];

    /// <summary>When the opening ends and the fighting starts.</summary>
    public float OpeningEnds { get; }

    /// <summary>Seconds fought since the opening ended.</summary>
    public float Elapsed { get; internal set; }

    /// <summary>Seconds left on the clock; at 0 the battle is a draw.</summary>
    public float TimeLeft => MathF.Max(0, Options.TimeLimit - Elapsed);

    /// <summary>Each race's share of the capture of the enemy pod, while its hero stands in the circle.</summary>
    public SortedDictionary<Race, float> Shares { get; } = [];

    /// <summary>What each race did.</summary>
    public SortedDictionary<Race, BattleTally> Tally { get; } = [];

    /// <summary>The races the computer drives now: bots from the start, and players who left.</summary>
    public SortedSet<Race> Bots { get; } = [];

    /// <summary>How it ended; null while it lasts.</summary>
    public BattleResult? Result { get; internal set; }

    public bool Over => Result is not null;

    /// <summary>The team a race fights in: 0, 1, or -1 for none.</summary>
    public int TeamOf(Race race) => Teams[0].Contains(race) ? 0 : Teams[1].Contains(race) ? 1 : -1;

    /// <summary>A team's progress on taking the other team's pod: its heroes' shares, at most <see cref="BattleBalance.CaptureTarget"/>.</summary>
    public float Progress(int team)
    {
        var sum = Teams[team].Sum(race => Shares.GetValueOrDefault(race));
        // Shares are whole ticks' worth, but their float sum may fall a hair short of the target.
        return sum >= BattleBalance.CaptureTarget - Slack ? BattleBalance.CaptureTarget : sum;
    }

    /// <summary>What a tick in the circle adds to a share or a tally.</summary>
    public static float Gain => BattleBalance.CaptureRate / World.GameWorld.TicksPerSecond;

    /// <summary>
    /// <paramref name="points"/> and a tick's gain, counted as a whole number of ticks' worth so that adding a twentieth of a
    /// point does not drift: 20 seconds in the circle are 20 points, not 19.99998.
    /// </summary>
    public static float Gained(float points) => (MathF.Round(points / Gain) + 1) * Gain;

    /// <summary>How far short of a whole a sum of points may fall and still count as it.</summary>
    internal const float Slack = 1e-3f;

    /// <summary>Seconds left of the opening at <paramref name="now"/>.</summary>
    public float OpeningLeft(float now) => MathF.Max(0, OpeningEnds - now);

    /// <summary>Whether the heroes are still held at <paramref name="now"/>.</summary>
    public bool InOpening(float now) => now < OpeningEnds;

    /// <summary>Whether a race won or lost the battle; both false while it lasts and on a draw.</summary>
    public bool Won(Race race) => Result is { Winner: { } team } && TeamOf(race) == team;

    public bool Lost(Race race) => Result is { Winner: { } team } && TeamOf(race) is var own && own >= 0 && own != team;

    /// <summary>A fingerprint of the battle for the world's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        hash.Mix(BitConverter.SingleToInt32Bits(Elapsed));
        foreach (var pod in Pods)
            hash.Mix(pod ?? -1);
        foreach (var (race, share) in Shares)
        {
            hash.Mix((int)race);
            hash.Mix(BitConverter.SingleToInt32Bits(share));
        }
        foreach (var (race, tally) in Tally)
        {
            hash.Mix((int)race);
            tally.MixInto(hash);
        }
        foreach (var race in Bots)
            hash.Mix((int)race);
        hash.Mix(Result?.Winner ?? -1);
        hash.Mix(Result is { } result ? (int)result.Reason : -1);
        hash.Mix(Result is { } ended ? BitConverter.SingleToInt32Bits(ended.EndedAt) : -1);
        return hash.Value;
    }
}

/// <summary>What a battle brings a race: XP on the chassis it fought on, free XP to the account, and credits with the salvage in them.</summary>
public sealed record BattleReward(int Xp, int FreeXp, int Credits, int Salvage, bool Won);

/// <summary>What a battle or a Wired run is worth to the player's account. Pure, so that every client, and later a server, agree.</summary>
public static class BattleRewards
{
    /// <summary>
    /// The reward of a race: 100 for taking part, a point for every <see cref="BattleBalance.DamagePerXp"/> hit points dealt
    /// to enemy heroes, 40 a kill and one for each capture point, half as much again on a win. Credits are six for each XP
    /// and the salvage of the enemy heroes it killed.
    /// </summary>
    public static BattleReward Of(BattleRun run, Race race)
    {
        var tally = run.Tally.GetValueOrDefault(race) ?? new BattleTally();
        var won = run.Won(race);
        var xp = BattleBalance.ParticipationXp + tally.Damage / BattleBalance.DamagePerXp + BattleBalance.KillXp * tally.Kills
            + BattleBalance.CapturePointXp * MathF.Floor(tally.Capture + BattleRun.Slack);
        var whole = (int)MathF.Round(xp * (won ? BattleBalance.WinFactor : 1));
        var salvage = tally.Killed.Sum(Salvage);
        return new BattleReward(whole, (int)MathF.Round(whole * BattleBalance.FreeXpShare), BattleBalance.CreditsPerXp * whole + salvage, salvage, won);
    }

    /// <summary>What killing a hero on the chassis brings: a share of what the pod sells that chassis for.</summary>
    public static int Salvage(string chassis) =>
        (int)MathF.Round(BattleBalance.SalvageShare * BattleBalance.PodPriceFactor * (WiredCatalogue.FindChassis(chassis)?.Price ?? 0));

    /// <summary>What a Wired run that reached <paramref name="waves"/> gives its final chassis: XP and credits, less than a battle.</summary>
    public static (int Xp, int Credits) OfWired(int waves)
    {
        var xp = BattleBalance.WiredXpPerWave * Math.Max(0, waves);
        return (xp, BattleBalance.WiredCreditsPerXp * xp);
    }
}
