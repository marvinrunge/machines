using System.Text.Json;
using System.Text.Json.Serialization;
using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>A run's best in one arena at one difficulty.</summary>
public sealed record WiredRecord(int Waves = 0, long Score = 0, int Kills = 0, string Hero = "", string Chassis = "");

/// <summary>A hero as the hangar keeps it between runs: what was chosen, not what it came to.</summary>
public sealed record WiredHeroSheet
{
    public string Name { get; init; } = "";
    public Race Colour { get; init; } = Race.Red;
    public string Chassis { get; init; } = WiredBalance.StartChassis;
    public WiredDifficulty Difficulty { get; init; } = WiredDifficulty.Normal;
    public string Arena { get; init; } = "";
    public IReadOnlyList<WiredStartItem> Items { get; init; } = [];
}

/// <summary>What a run tells the profile when it ends.</summary>
public sealed record WiredRunResult(string Arena, WiredDifficulty Difficulty, string Hero, string Chassis, long Score, WiredStats Stats);

/// <summary>All runs together, for the milestones.</summary>
public sealed record WiredTotals
{
    public int Runs { get; init; }
    public int Waves { get; init; }
    public int Kills { get; init; }
    public int Bosses { get; init; }
    public int Wrecks { get; init; }
    public int Crates { get; init; }

    /// <summary>The most waves in a row without a death in any run.</summary>
    public int CleanWaves { get; init; }
}

/// <summary>What a milestone counts.</summary>
public enum WiredMeasure
{
    Waves,
    Kills,
    Bosses,
    Wrecks,
    Crates,
    CleanWaves,
}

/// <summary>A chassis to start on, open from <paramref name="Wave"/> waves cleared in one run, with a squad of <paramref name="Squad"/> as well if that is more than none.</summary>
public sealed record WiredStartChassis(string Chassis, int Wave, int Squad = 0);

/// <summary>An arena of Wired, open from <paramref name="Wave"/> waves cleared in one run. <paramref name="Name"/> is its name in the original's English, the key to de.po.</summary>
public sealed record WiredArena(string Scenario, int Wave, string Name);

/// <summary>A chip beyond the base pool: it joins the pool once <paramref name="Measure"/> comes to <paramref name="Amount"/>.</summary>
public sealed record WiredMilestone(string Chip, WiredMeasure Measure, int Amount);

/// <summary>
/// Something not open yet, by its id in <see cref="WiredProfile.Unlocks"/>: <paramref name="Words"/> say what it takes, with
/// <paramref name="Need"/> as {0} and <paramref name="Squad"/> as {1}, and <paramref name="Have"/> is how far the player has come.
/// </summary>
public sealed record WiredGoal(string Id, string Words, int Need, int Have, int Squad = 0)
{
    /// <summary>How much of the way is done, 0 to 1.</summary>
    public float Share => Need > 0 ? Math.Clamp((float)Have / Need, 0, 1) : 1;
}

/// <summary>
/// What stays of Wired between runs, in <c>user://wired.json</c>: what was unlocked, the records per arena and difficulty
/// and the last hero. Unlocks only widen the choice, they never make a hero stronger, and what a run may use goes into
/// <see cref="WiredOptions"/>, so a recording plays the same whatever this file holds later. Wired is the remake's own
/// addition. Nothing here but the file's place touches Godot, so the tests can compile it.
/// </summary>
public sealed class WiredProfile
{
    /// <summary>What Hard must reach for Hardcore to open: this many waves cleared in one run.</summary>
    public const int HardcoreWave = 15;

    /// <summary>The word in <see cref="Unlocks"/> for that.</summary>
    public const string Hardcore = "hardcore";

    /// <summary>What a chassis, an arena or Hardcore asks for, the wave as {0} and a squad as {1}.</summary>
    public const string Clear = "Clear wave {0}", ClearWithSquad = "Clear wave {0} with a squad of {1}", ClearOnHard = "Clear wave {0} on Hard";

    private const string Backup = ".bak", Broken = ".bad";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The chassis a hero can start on, the first always.</summary>
    public static readonly IReadOnlyList<WiredStartChassis> StartChassis =
    [
        new(WiredBalance.StartChassis, 0),
        new("reaper2", 10),
        new("overseer2", 10, 3),
        new("knight3", 15),
        new("ballista3", 15),
        new("commander4", 20),
        new("boss1", 20),
        new("gorilla5", 30),
    ];

    /// <summary>The arenas, the first two always.</summary>
    public static readonly IReadOnlyList<WiredArena> Arenas =
    [
        new("m_desert", 0, "Canyon Attack"),
        new("m_green", 0, "Oasis"),
        new("m_moon", 10, "Lunar Desert"),
        new("m_dales", 20, "Island Skirmish"),
    ];

    /// <summary>What opens each chip outside the base pool. The chips' order is <see cref="WiredChips.All"/>'s.</summary>
    public static readonly IReadOnlyList<WiredMilestone> Milestones =
    [
        new("overload", WiredMeasure.Bosses, 1),
        new("revenge", WiredMeasure.Kills, 100),
        new("shrapnel", WiredMeasure.Kills, 300),
        new("lastditch", WiredMeasure.CleanWaves, 5),
        new("reactive", WiredMeasure.CleanWaves, 10),
        new("bastion", WiredMeasure.Waves, 25),
        new("trove", WiredMeasure.Crates, 5),
        new("vein", WiredMeasure.Wrecks, 100),
        new("savings", WiredMeasure.Wrecks, 300),
        new("workshop", WiredMeasure.Bosses, 3),
        new("veterans", WiredMeasure.Bosses, 5),
        new("cover", WiredMeasure.CleanWaves, 15),
    ];

    /// <summary>What was unlocked: chassis, chips and arenas by their ids, and <see cref="Hardcore"/>.</summary>
    public SortedSet<string> Unlocks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What was opened and has not come up in the hangar yet, as ids of <see cref="Unlocks"/>: the menu and the hangar show it as new.</summary>
    public SortedSet<string> Fresh { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The records, by <see cref="RecordKey"/>.</summary>
    public SortedDictionary<string, WiredRecord> Records { get; set; } = new(StringComparer.Ordinal);

    public WiredTotals Totals { get; set; } = new();

    public WiredHeroSheet? LastHero { get; set; }

    /// <summary>The file is there but could not be read just now: what this holds is not all of it, so it is not written over.</summary>
    [JsonIgnore]
    public bool Held { get; private set; }

#if GODOT
    private static WiredLedger? _runs;

    public static string FilePath => Godot.ProjectSettings.GlobalizePath("user://wired.json");

    /// <summary>The runs on their way into the file.</summary>
    public static WiredLedger Runs => _runs ??= new WiredLedger(FilePath, Godot.GD.PushWarning);

    /// <summary>What was saved, with any run that had to wait: this is the last chance it gets before the file is written over.</summary>
    public static WiredProfile Load()
    {
        Runs.Write();
        return Load(FilePath, Godot.GD.PushWarning);
    }

    /// <summary>Writes it to its place. A disk that will not have it costs this save and no more: the game goes on.</summary>
    public void Save()
    {
        try
        {
            Save(FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Godot.GD.PushWarning($"wired profile not saved: {exception.Message}");
        }
    }
#endif

    /// <summary>
    /// What was saved. A file that is broken is kept beside it as <c>.bad</c> and the last good one, <c>.bak</c>, stands in; one
    /// that cannot be opened just now is left alone and the profile comes back <see cref="Held"/>. Only with neither is it new.
    /// </summary>
    public static WiredProfile Load(string path, Action<string>? warn = null)
    {
        if (!File.Exists(path))
            return Read(path + Backup, warn) ?? new();
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"wired profile not read: {exception.Message}");
            var held = Read(path + Backup, warn) ?? new();
            held.Held = true;
            return held;
        }
        try
        {
            return Parse(text);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            warn?.Invoke($"wired profile broken, kept as {path}{Broken}: {exception.Message}");
            KeepBroken(path, warn);
            return Read(path + Backup, warn) ?? new();
        }
    }

    /// <summary>A file that may well not be there or not be whole.</summary>
    private static WiredProfile? Read(string path, Action<string>? warn)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            warn?.Invoke($"wired profile not read: {exception.Message}");
            return null;
        }
    }

    private static WiredProfile Parse(string text) => (JsonSerializer.Deserialize<WiredProfile>(text, Json) ?? throw new JsonException("the file holds null")).Mended();

    private static void KeepBroken(string path, Action<string>? warn)
    {
        try
        {
            File.Copy(path, path + Broken, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"wired profile not kept: {exception.Message}");
        }
    }

    /// <summary>A file written by hand may leave out or empty what the screens count on: that goes back to what a new profile has.</summary>
    private WiredProfile Mended()
    {
        Unlocks = new SortedSet<string>((Unlocks ?? []).OfType<string>(), StringComparer.Ordinal);
        Fresh = new SortedSet<string>((Fresh ?? []).OfType<string>(), StringComparer.Ordinal);
        var records = new SortedDictionary<string, WiredRecord>(StringComparer.Ordinal);
        foreach (var (key, record) in Records ?? [])
        {
            if (record is not null)
                records[key] = record with { Hero = record.Hero ?? "", Chassis = record.Chassis ?? "" };
        }
        Records = records;
        Totals ??= new();
        if (LastHero is { } hero)
            LastHero = hero with
            {
                // An unnamed hero was once kept under the word for one in the language of the day; it is unnamed again.
                Name = hero.Name is null or "Hero" or "Held" ? "" : hero.Name,
                Colour = Enum.IsDefined(hero.Colour) ? hero.Colour : Race.Red,
                Chassis = hero.Chassis ?? WiredBalance.StartChassis,
                Difficulty = Enum.IsDefined(hero.Difficulty) ? hero.Difficulty : WiredDifficulty.Normal,
                Arena = hero.Arena ?? "",
                Items = (hero.Items ?? []).Where(i => i?.Catalogue is not null && WiredCatalogue.KindOf(i.Catalogue) is not null).ToList(),
            };
        return this;
    }

    /// <summary>
    /// Writes it, by way of a second file that is on the disk before it takes the first's place: a crash half way through must
    /// not cost the player all of it. What stood there stays as <c>.bak</c>. A <see cref="Held"/> profile writes nothing.
    /// </summary>
    public void Save(string path)
    {
        if (Held)
            return;
        var written = path + ".new";
        using (var file = new FileStream(written, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(file, this, Json);
            file.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
            File.Replace(written, path, path + Backup);
        else
            File.Move(written, path);
    }

    public static string RecordKey(string arena, WiredDifficulty difficulty) => $"{arena}/{difficulty}";

    public WiredRecord Record(string arena, WiredDifficulty difficulty) => Records.GetValueOrDefault(RecordKey(arena, difficulty)) ?? new WiredRecord();

    /// <summary>Whether a chassis can be started on.</summary>
    public bool CanStartOn(string chassis) => StartChassis.Any(s => s.Chassis == chassis && (s.Wave == 0 || Unlocks.Contains(chassis)));

    /// <summary>Whether an arena can be chosen.</summary>
    public bool CanPlay(string arena) => Arenas.Any(a => a.Scenario == arena && (a.Wave == 0 || Unlocks.Contains(arena)));

    /// <summary>Whether a difficulty can be chosen.</summary>
    public bool CanPlay(WiredDifficulty difficulty) => difficulty != WiredDifficulty.Hardcore || Unlocks.Contains(Hardcore);

    /// <summary>The chips a run may offer: the base pool and what was unlocked, in the catalogue's order.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ChipPool => WiredChips.All.Where(c => c.Base || Unlocks.Contains(c.Id)).Select(c => c.Id).ToList();

    /// <summary>
    /// A run has ended: it goes into the records and the totals, and what it opens is opened. Returns what is new, as ids
    /// of <see cref="Unlocks"/>, for the end screen to name.
    /// </summary>
    public IReadOnlyList<string> Note(WiredRunResult run)
    {
        var (stats, key) = (run.Stats, RecordKey(run.Arena, run.Difficulty));
        var best = Records.GetValueOrDefault(key) ?? new WiredRecord();
        Records[key] = new WiredRecord(
            Math.Max(best.Waves, stats.WavesCleared),
            Math.Max(best.Score, run.Score),
            Math.Max(best.Kills, stats.Kills),
            run.Score >= best.Score ? run.Hero : best.Hero,
            run.Score >= best.Score ? run.Chassis : best.Chassis);
        Totals = Totals with
        {
            Runs = Totals.Runs + 1,
            Waves = Totals.Waves + stats.WavesCleared,
            Kills = Totals.Kills + stats.Kills,
            Bosses = Totals.Bosses + stats.BossesKilled,
            Wrecks = Totals.Wrecks + stats.WrecksCollected,
            Crates = Totals.Crates + stats.CratesCollected,
            CleanWaves = Math.Max(Totals.CleanWaves, stats.CleanWaves),
        };

        var opened = new List<string>();
        void Open(string id)
        {
            if (!Unlocks.Add(id))
                return;
            opened.Add(id);
            Fresh.Add(id);
        }
        foreach (var start in StartChassis.Where(s => s.Wave > 0 && stats.WavesCleared >= s.Wave && stats.LargestSquad >= s.Squad))
            Open(start.Chassis);
        foreach (var arena in Arenas.Where(a => a.Wave > 0 && stats.WavesCleared >= a.Wave))
            Open(arena.Scenario);
        if (run.Difficulty == WiredDifficulty.Hard && stats.WavesCleared >= HardcoreWave)
            Open(Hardcore);
        foreach (var milestone in Milestones.Where(m => Count(m.Measure) >= m.Amount))
            Open(milestone.Chip);
        return opened;
    }

    /// <summary>The hangar is up: what was new is handed over, and from now on is not.</summary>
    public IReadOnlySet<string> TakeFresh()
    {
        var fresh = new SortedSet<string>(Fresh, StringComparer.Ordinal);
        Fresh.Clear();
        return fresh;
    }

    /// <summary>The most waves cleared in one run, on any arena, at one difficulty or at any.</summary>
    public int BestWave(WiredDifficulty? difficulty = null) =>
        Records.Where(r => difficulty is null || r.Key.EndsWith($"/{difficulty}", StringComparison.Ordinal)).Select(r => r.Value.Waves).DefaultIfEmpty(0).Max();

    /// <summary>All that is still closed, what is nearest to opening first.</summary>
    public IReadOnlyList<WiredGoal> Goals()
    {
        var best = BestWave();
        var goals = new List<WiredGoal>();
        foreach (var start in StartChassis.Where(s => s.Wave > 0 && !Unlocks.Contains(s.Chassis)))
            goals.Add(new WiredGoal(start.Chassis, start.Squad > 0 ? ClearWithSquad : Clear, start.Wave, Math.Min(best, start.Wave - 1), start.Squad));
        foreach (var arena in Arenas.Where(a => a.Wave > 0 && !Unlocks.Contains(a.Scenario)))
            goals.Add(new WiredGoal(arena.Scenario, Clear, arena.Wave, Math.Min(best, arena.Wave - 1)));
        if (!Unlocks.Contains(Hardcore))
            goals.Add(new WiredGoal(Hardcore, ClearOnHard, HardcoreWave, Math.Min(BestWave(WiredDifficulty.Hard), HardcoreWave - 1)));
        foreach (var milestone in Milestones.Where(m => !Unlocks.Contains(m.Chip)))
            goals.Add(new WiredGoal(milestone.Chip, Words(milestone.Measure, milestone.Amount), milestone.Amount, Math.Min(Count(milestone.Measure), milestone.Amount - 1)));
        return goals.OrderByDescending(g => g.Share).ThenBy(g => g.Need - g.Have).ToList();
    }

    /// <summary>What a milestone asks for, its amount as {0}.</summary>
    public static string Words(WiredMeasure measure, int amount) => measure switch
    {
        WiredMeasure.Waves => "{0} waves in all",
        WiredMeasure.Kills => "{0} kills in all",
        WiredMeasure.Bosses => amount == 1 ? "{0} boss killed" : "{0} bosses killed",
        WiredMeasure.Wrecks => "{0} wrecks collected",
        WiredMeasure.Crates => "{0} crates collected",
        _ => "{0} waves without a death",
    };

    /// <summary>How far a measure has come, over all runs.</summary>
    public int Count(WiredMeasure measure) => measure switch
    {
        WiredMeasure.Waves => Totals.Waves,
        WiredMeasure.Kills => Totals.Kills,
        WiredMeasure.Bosses => Totals.Bosses,
        WiredMeasure.Wrecks => Totals.Wrecks,
        WiredMeasure.Crates => Totals.Crates,
        _ => Totals.CleanWaves,
    };
}

/// <summary>
/// The runs that have ended and are not in the file yet. A run's end is the one moment the profile has to be written, and something
/// else may hold the file just then, so a run stays here until it is in, read afresh each time, and nothing says it is before it is.
/// </summary>
public sealed class WiredLedger(string path, Action<string>? warn = null)
{
    private readonly List<WiredRunResult> _waiting = [];

    /// <summary>A run has yet to go into the file.</summary>
    public bool Waiting => _waiting.Count > 0;

    /// <summary>A run has ended. Returns what it and any before it opened, or null while the file cannot be written.</summary>
    public IReadOnlyList<string>? Tell(WiredRunResult run)
    {
        _waiting.Add(run);
        return Write();
    }

    /// <summary>All that waits goes in together or not at all. Returns what that opened, or null while the file cannot be written.</summary>
    public IReadOnlyList<string>? Write()
    {
        if (!Waiting)
            return [];
        var profile = WiredProfile.Load(path, warn);
        if (profile.Held)
            return null;
        var opened = _waiting.SelectMany(profile.Note).ToList();
        try
        {
            profile.Save(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"wired profile not saved: {exception.Message}");
            return null;
        }
        _waiting.Clear();
        return opened;
    }
}
