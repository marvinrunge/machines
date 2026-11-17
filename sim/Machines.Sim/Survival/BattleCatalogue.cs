using Machines.Import.Model;

namespace Machines.Sim.Survival;

/// <summary>
/// A map a battle can be fought on: its scenario and planet, its English name (and the original's string id for it), and
/// the two teams by scenario slot. The first race of each team is its anchor: that race's pod is the team's.
/// </summary>
public sealed record BattleMap(string Scenario, string Planet, string Name, int StringId, IReadOnlyList<Race> TeamA, IReadOnlyList<Race> TeamB);

/// <summary>Battle's own tables: the tier of each chassis, what each tier may wear, and the maps.</summary>
public static class BattleCatalogue
{
    /// <summary>The highest tier there is.</summary>
    public const int MostTier = 8;

    /// <summary>The tier of each chassis, by its catalogue id. A machine added to the catalogue gets a row here.</summary>
    public static readonly IReadOnlyDictionary<string, int> Tiers = new SortedDictionary<string, int>(StringComparer.Ordinal)
    {
        ["grunt1"] = 1,
        ["grunt2"] = 2,
        ["grunt3"] = 3,
        ["reaper2"] = 3,
        ["overseer2"] = 3,
        ["reaper3"] = 4,
        ["overseer3"] = 4,
        ["knight3"] = 5,
        ["ballista3"] = 5,
        ["boss1"] = 5,
        ["knight4"] = 6,
        ["commander4"] = 6,
        ["knight5"] = 7,
        ["commander5"] = 7,
        ["ballista4"] = 7,
        ["boss2"] = 7,
        ["gorilla5"] = 8,
        // Wired's own machines, by the rows of the tree they stand in.
        ["grunt4"] = 6,
        ["reaper4"] = 6,
        ["boss3"] = 6,
        ["grunt5"] = 8,
        ["reaper5"] = 8,
        ["widow6"] = 8,
        ["ballista5"] = 8,
        ["commander6"] = 8,
        // The constructors and spies, by their rows too.
        ["dozer1"] = 3,
        ["builder3"] = 4,
        ["behemoth4"] = 6,
        ["titan6"] = 8,
        ["spy3"] = 4,
        ["spy5"] = 6,
        ["phantom6"] = 8,
        // The locators: the crab a start beside Grunt 1, the glider the first flyer.
        ["geo1"] = 1,
        ["geo3"] = 2,
    };

    /// <summary>The tier of a chassis, or 0 for one without a row.</summary>
    public static int Tier(string chassis) => Tiers.GetValueOrDefault(chassis);

    /// <summary>The chassis of a tier, in the Wired catalogue's order.</summary>
    public static IReadOnlyList<WiredChassis> OfTier(int tier) => WiredCatalogue.Chassis.Where(c => Tier(c.Id) == tier).ToList();

    /// <summary>The highest Mk a module or weapon may have on a chassis of the tier: Mk II from tier IV, Mk III from VI.</summary>
    public static int MostMk(int tier) => tier >= 6 ? 3 : tier >= 4 ? 2 : 1;

    /// <summary>Abilities go on chassis from this tier.</summary>
    public const int AbilitiesFrom = 3;

    /// <summary>The abilities a battle does not offer: an orb of treachery cannot turn a hero.</summary>
    public static readonly IReadOnlyList<string> NotOffered = ["treachery"];

    /// <summary>The lowest tier of a bracket whose top is <paramref name="tier"/>.</summary>
    public static int BracketBottom(int tier) => Math.Max(1, tier - 1);

    /// <summary>Whether a chassis may play in the bracket whose top is <paramref name="tier"/>.</summary>
    public static bool InBracket(string chassis, int tier) => Tier(chassis) is var own && own >= BracketBottom(tier) && own <= tier && own > 0;

    /// <summary>The maps of phase 1.</summary>
    public static readonly IReadOnlyList<BattleMap> Maps =
    [
        new("fortmult", "fortmult", "Central Dominance", 8318, [Race.Red, Race.Yellow], [Race.Blue, Race.Green]),
        new("mgrack", "mgrack", "The Red Planet", 8353, [Race.Red, Race.Blue], [Race.Green, Race.Yellow]),
        new("m_desert", "m_desert", "Canyon Attack", 8346, [Race.Red, Race.Blue], [Race.Green, Race.Yellow]),
        new("micey", "micey", "Ice Moon Mountain", 8348, [Race.Red, Race.Blue], [Race.Green, Race.Yellow]),
        new("m_green", "m_green", "Oasis", 8350, [Race.Red, Race.Yellow], [Race.Blue, Race.Green]),
    ];

    /// <summary>The map of a scenario, or null.</summary>
    public static BattleMap? Map(string scenario) => Maps.FirstOrDefault(m => m.Scenario == scenario);
}

/// <summary>Battle's numbers.</summary>
public static class BattleBalance
{
    /// <summary>Seconds at the start in which no hero can move, fire, dash or use an ability.</summary>
    public const float Opening = 10;

    /// <summary>The circle round a team's pod that the other team takes it from, in metres from the pod's middle.</summary>
    public const float CaptureRadius = 30;

    /// <summary>Points a second each enemy hero in the circle adds to its share, and the points that take the pod.</summary>
    public const float CaptureRate = 1, CaptureTarget = 100;

    /// <summary>
    /// The points that take a pod when the larger team has <paramref name="heroes"/> heroes: <see cref="CaptureTarget"/> up to
    /// two (every battle of today's layout), from three on 50 a hero, so that a whole team takes a pod in about as long as two
    /// heroes do today. Pods cannot be shot in a battle, so this is a pod's strength.
    /// </summary>
    public static float CaptureTargetFor(int heroes) => heroes <= 2 ? CaptureTarget : 50 * heroes;

    /// <summary>The most heroes a team may have in a room until the relay's beat comes (online plan P2); the sim takes up to <see cref="BattleSeats.MostPerTeam"/>.</summary>
    public const int MostTeamSize = 5;

    /// <summary>The lives a room proposes for a battle whose larger team has <paramref name="heroes"/> heroes; the room may choose 1 to 5.</summary>
    public static int ProposedLives(int heroes) => heroes <= 2 ? 1 : heroes < 10 ? 2 : 3;

    /// <summary>The cooldown of the ion call in a battle, in seconds, as there are no waves to count.</summary>
    public const float IonCooldown = 90;

    /// <summary>Seconds a hero back at the pod cannot be hurt.</summary>
    public const float RespawnShield = 3;

    /// <summary>Up to this tier a bot wears what a new account starts with: the start weapon and nothing else.</summary>
    public const int StartKitTier = 1;

    // ---- rewards ----------------------------------------------------------------------------------

    /// <summary>XP for taking part.</summary>
    public const int ParticipationXp = 100;

    /// <summary>
    /// Hit points dealt to enemy heroes for each XP: one and a half times the heroes' toughness, as heroes have three times
    /// the hit points of machines. Measured on bot battles so that an average battle gives about 250 XP (battle-log.md).
    /// </summary>
    public const float DamagePerXp = 1.5f * WiredBalance.HeroHitPoints;

    /// <summary>XP for each enemy hero the race dealt the last hit to, and for each capture point.</summary>
    public const int KillXp = 40, CapturePointXp = 1;

    /// <summary>What a win multiplies the XP by.</summary>
    public const float WinFactor = 1.5f;

    /// <summary>The share of the XP that also goes to the account as free XP.</summary>
    public const float FreeXpShare = 0.05f;

    /// <summary>Credits for each XP.</summary>
    public const int CreditsPerXp = 6;

    /// <summary>What the pod sells for, as a multiple of the Wired price, and the share of that a killed enemy hero's chassis brings as salvage.</summary>
    public const int PodPriceFactor = 10;

    public const float SalvageShare = 0.1f;

    /// <summary>XP a Wired run gives its final chassis for each wave reached, and credits for each of that XP: Wired gives less.</summary>
    public const int WiredXpPerWave = 10, WiredCreditsPerXp = 3;
}
