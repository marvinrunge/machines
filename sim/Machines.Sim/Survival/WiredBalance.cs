namespace Machines.Sim.Survival;

/// <summary>How hard a Wired run is.</summary>
public enum WiredDifficulty
{
    Normal,
    Hard,
    Hardcore,
}

/// <summary>
/// The numbers of the survival mode Wired, the remake's own addition: nothing here comes from the original game. They
/// are starting values, to be tuned with the bench; the catalogues hold the numbers of each item.
/// </summary>
public static class WiredBalance
{
    // ---- the hero ---------------------------------------------------------------------------------

    /// <summary>How much tougher the hero is than an enemy of the same type: hit points and armour on the chassis' data.</summary>
    public const float HeroHitPoints = 3, HeroArmour = 3;

    /// <summary>Items an inventory holds, not counting what the hero wears.</summary>
    public const int InventoryLimit = 24;

    public const int AbilitySlots = 2, ChipSockets = 6;

    /// <summary>The highest Mk of a weapon; a module's prices say how far it goes.</summary>
    public const int MostMk = 3;

    /// <summary>What each Mk above the first adds to a weapon's damage and takes off its reload.</summary>
    public const float DamagePerMk = 0.2f, ReloadPerMk = 0.08f;

    /// <summary>What the steps to Mk 2 and Mk 3 of a weapon cost, in percent of its price.</summary>
    public static readonly IReadOnlyList<int> WeaponUpgradePercent = [80, 130];

    /// <summary>The most load allowed, as a share of the capacity: above it the hangar refuses.</summary>
    public const float MostLoad = 1.5f;

    /// <summary>What overload costs in speed, acceleration and turning for all of the capacity it is over: 6 % for each 10 %.</summary>
    public const float OverloadCost = 0.6f;

    /// <summary>However many coolers it carries, a weapon's reload does not fall below this share.</summary>
    public const float LeastReload = 0.4f;

    /// <summary>The degrees a driven machine's rounds can stray for each point of inaccuracy: 3.2 on a Grunt 1, 2 m at 35 m.</summary>
    public const float StrayPerPoint = 0.08f;

    /// <summary>The dash's cooldown in seconds at Mk 1 to Mk 3 of the thrusters.</summary>
    public static readonly IReadOnlyList<float> DashCooldowns = [12, 9, 6];

    /// <summary>How long a dash lasts and how fast it throws the hero: about 16 m.</summary>
    public const float DashTime = 0.35f, DashSpeed = 45;

    // ---- abilities --------------------------------------------------------------------------------

    /// <summary>How long a machine turned by the hero fights for it before it goes back.</summary>
    public const float TreacheryTime = 30;

    /// <summary>How far from the aim point a machine still counts as aimed at, for the orb.</summary>
    public const float TreacheryAim = 12;

    /// <summary>The lightning of a discharge: how many arcs run out from the hero, and what each does where it ends.</summary>
    public const int DischargeArcs = 12, DischargeDamage = 40;

    /// <summary>What a supercharge gives back, as a share of each machine's hit points.</summary>
    public const float SuperchargeHeal = 0.4f;

    /// <summary>The mines a minefield lays, and the most of a hero's that may lie on the ground: an open hangar holds the wave back, not the cooldown.</summary>
    public const int MinesLaid = 5, MostMines = 15;

    /// <summary>How far ahead of the hero a minefield's middle lies, and how far apart its mines are.</summary>
    public const float MinesAhead = 12, MineGap = 7;

    // ---- chips ------------------------------------------------------------------------------------

    /// <summary>However short the reloads, fire counts as unbroken with no longer between rounds than this, or this share of the slowest reload.</summary>
    public const float FireGap = 1.5f, FireGapShare = 1.25f;

    /// <summary>How long after a hit reactive armour takes something off the next.</summary>
    public const float ReactiveTime = 4;

    /// <summary>How long after the last hit the hero counts as out of fire.</summary>
    public const float OutOfFireTime = 5;

    /// <summary>How often the hero calls on the squad to cover it, in seconds.</summary>
    public const float CoverEvery = 3;

    // ---- the economy ------------------------------------------------------------------------------

    /// <summary>How near the hero comes to a wreck to collect it, in metres, and what a glider's reach is in shares of that.</summary>
    public const float PickupReach = 3, GliderPickup = 1.5f;

    /// <summary>The share of what was paid that selling brings.</summary>
    public const int SellPercent = 50;

    /// <summary>The offers in the shop.</summary>
    public const int OfferSize = 8;

    /// <summary>What another offer costs, once per intermission.</summary>
    public const int RerollCost = 25;

    /// <summary>The pod's hit points one BMU mends.</summary>
    public const int PodHitPointsPerBmu = 2;

    /// <summary>How near the pod the hero must stand for the hangar, in metres from its walls.</summary>
    public const float PodReach = 40;

    /// <summary>The free BOLTER and the chassis every hero starts with.</summary>
    public const string StartWeapon = "BOLTER", StartChassis = "grunt1";

    /// <summary>All prices in the hangar, in percent of the catalogue's, at a difficulty.</summary>
    public static int PricePercent(WiredDifficulty difficulty) => difficulty switch
    {
        WiredDifficulty.Hard => 115,
        WiredDifficulty.Hardcore => 130,
        _ => 100,
    };

    /// <summary>A catalogue price at a difficulty, to the nearest BMU.</summary>
    public static int Priced(int price, WiredDifficulty difficulty) => (price * PricePercent(difficulty) + 50) / 100;

    /// <summary>The BMUs a hero has to spend at creation, before any start chassis' price.</summary>
    public static int StartCapital(WiredDifficulty difficulty) => difficulty switch
    {
        WiredDifficulty.Hard => 110,
        WiredDifficulty.Hardcore => 80,
        _ => 150,
    };

    /// <summary>The spare bodies a run starts with.</summary>
    public static int SpareBodies(WiredDifficulty difficulty) => difficulty switch
    {
        WiredDifficulty.Hard => 1,
        WiredDifficulty.Hardcore => 0,
        _ => 2,
    };

    /// <summary>What the hero and the machines round it get back between waves near the pod, as a share of their maximum each second.</summary>
    public const float FreeRepair = 0.1f;

    /// <summary>A boss always leaves a crate, an elite this many times in a hundred.</summary>
    public const int EliteLootPercent = 15;

    /// <summary>How often a crate holds a chip, as long as there is one left to find.</summary>
    public const int CrateChipPercent = 20;

    /// <summary>The waves from which a crate's weapon or module may come a Mk higher, each time this many times in a hundred.</summary>
    public static readonly IReadOnlyList<int> CrateMkFrom = [10, 20];

    public const int CrateMkPercent = 30;

    // ---- waves ------------------------------------------------------------------------------------

    /// <summary>Seconds before the first wave and between waves.</summary>
    public const float PrepareTime = 45, IntermissionTime = 30;

    /// <summary>What "Ready" pays for each second of an intermission saved.</summary>
    public const int ReadyBonus = 2;

    /// <summary>The budget of the first wave in machine cost, and what each wave multiplies it by.</summary>
    public const float FirstBudget = 40, BudgetGrowth = 1.17f;

    /// <summary>What a difficulty multiplies a wave's budget by.</summary>
    public static float BudgetShare(WiredDifficulty difficulty) => difficulty switch
    {
        WiredDifficulty.Hard => 1.3f,
        WiredDifficulty.Hardcore => 1.6f,
        _ => 1,
    };

    /// <summary>A wave's budget. Multiplied out step by step, as powers differ from one machine to the next.</summary>
    public static float Budget(int wave, WiredDifficulty difficulty)
    {
        var budget = FirstBudget;
        for (var i = 1; i < wave; i++)
            budget *= BudgetGrowth;
        return budget * BudgetShare(difficulty);
    }

    /// <summary>
    /// The first waves bring at least this many machines, each the weaker for it (WiredDirector.Crowded), up to this wave.
    /// The budget buys a single Grunt for each of the first four waves on Normal.
    /// </summary>
    public const int CrowdSize = 4, CrowdUntil = 4;

    /// <summary>In how many groups such a crowd arrives, and the seconds between its first and its last.</summary>
    public const int CrowdGroups = 2, CrowdSpread = 20;

    /// <summary>How strong such a crowd is together against what the budget bought: as one Grunt in the first wave, rising with each.</summary>
    public static float CrowdStrength(int wave) => 1 + 0.1f * (wave - 1);

    /// <summary>The most machines a wave's budget buys.</summary>
    public const int MostInWave = 60;

    /// <summary>The most machines that arrive together, and the groups a wave is split into if it has the machines for it.</summary>
    public const int GroupSize = 4, FewestGroups = 3;

    /// <summary>The seconds between a wave's first group and its last, at least and at most.</summary>
    public const int LeastSpread = 60, MostSpread = 90;

    /// <summary>How many of the newest tiers a wave draws on.</summary>
    public const int TierSpread = 4;

    /// <summary>In how many cases out of a hundred a machine goes for the hero rather than the pod.</summary>
    public const int HunterPercent = 30;

    /// <summary>How often the idle get new orders and the director looks for machines that hang, in seconds.</summary>
    public const float OrdersEvery = 5;

    /// <summary>
    /// How long after the last group a machine that has neither moved nor been in fire may stand, in seconds, and how
    /// long after that even one that keeps busy is left to get there by itself.
    /// </summary>
    public const float StandingTime = 45, LastCall = 150;

    /// <summary>How far from the pod a machine that hung is set down.</summary>
    public const float PullDistance = 100;

    /// <summary>What a wave pays when it is cleared.</summary>
    public static int WaveBonus(int wave) => 20 + 8 * wave;

    // ---- special waves ----------------------------------------------------------------------------

    /// <summary>The first wave that can be special, and how often one is: every so many waves that have no boss.</summary>
    public const int SpecialFrom = 4, SpecialEvery = 3;

    /// <summary>The first wave a kind of special wave can come at: from then on the machines it needs are within the wave's means.</summary>
    public static int SpecialOpens(WiredSpecial special) => special switch
    {
        WiredSpecial.Spies => 6,
        WiredSpecial.Treason => 8,
        WiredSpecial.AirRaid => 9,
        WiredSpecial.Siege => 11,
        WiredSpecial.IonStorm => 12,
        _ => SpecialFrom,
    };

    /// <summary>How much darkness a night wave forces, from 0 to 1.</summary>
    public const float NightDarkness = 1;

    /// <summary>How many times as many machines a storm brings, and of how many kinds.</summary>
    public const int StormShare = 2, StormKinds = 2;

    /// <summary>The spies of a wave: the fewest, one more for each so many waves, and at most.</summary>
    public const int FewestSpies = 2, WavesPerSpy = 6, MostSpies = 5;

    /// <summary>The wave from which spies come at the higher level.</summary>
    public const int BetterSpiesFrom = 20;

    /// <summary>The seconds the spies have before the wave's fighters arrive.</summary>
    public const float SpyLead = 20;

    /// <summary>The ring of mines round the pod, in metres from its middle.</summary>
    public const float MineRingNear = 35, MineRingFar = 55;

    /// <summary>How far apart a spy lays the mines on the way, outside the ring.</summary>
    public const float MineRoadGap = 30;

    /// <summary>How many of a spy's mines in a hundred it lays on the way to the pod.</summary>
    public const int MinesOnTheWayPercent = 40;

    /// <summary>The fewest machines of an air raid and of a siege, whatever the budget.</summary>
    public const int FewestRaiders = 2, FewestBesiegers = 2;

    /// <summary>The wave from which a siege brings the bigger Ballistas.</summary>
    public const int BigSiegeFrom = 26;

    /// <summary>How often an ion storm strikes, how long before each the warning stands, and how wide it is.</summary>
    public const float IonEvery = 25, IonWarning = 5, IonRadius = 40;

    /// <summary>In how many cases out of a hundred a strike goes for the hero rather than the pod.</summary>
    public const int IonOnHeroPercent = 60;

    // ---- where they come from ---------------------------------------------------------------------

    /// <summary>The least distance of a spawn point from the pod, and from another spawn point.</summary>
    public const float SpawnDistance = 250, SpawnGap = 60;

    /// <summary>How far in from the map's edge spawn points lie, and how far apart they are tried.</summary>
    public const float EdgeInset = 40, EdgeSpacing = 200;

    /// <summary>
    /// The ring of spawn points nearer the pod, so that the first waves need not walk from the map's edge (more than a
    /// kilometre on every arena, two minutes before the first shot): its radius, how many points are tried on it, and how
    /// much longer than the straight line the way from one may be.
    /// </summary>
    public const float RingRadius = 320, RingDetour = 1.8f;

    public const int RingPoints = 12;

    /// <summary>
    /// How far from the pod a wave's groups arrive: near at first, from further with each wave, from everywhere by wave 8.
    /// Growing by 80 m a wave the bench's runs ended a wave or two sooner; by 200 m they end about where they did.
    /// </summary>
    public static float SpawnReach(int wave) => 400 + 200 * Math.Max(0, wave - 1);

    /// <summary>The fewest places a group chooses from, the nearest ones when the reach holds fewer.</summary>
    public const int FewestSpawnChoices = 3;

    // ---- elites and bosses ------------------------------------------------------------------------

    /// <summary>From this wave on this many in a hundred of a wave's machines are elites.</summary>
    public const int EliteFrom = 6, ElitePercent = 10;

    /// <summary>What makes an elite: its hit points and its weapons' damage multiplied by these.</summary>
    public const float EliteHitPoints = 2, EliteDamage = 1.25f;

    /// <summary>After this wave elites grow by this share with each wave.</summary>
    public const int EliteGrowthAfter = 30;

    public const float EliteGrowth = 0.04f;

    /// <summary>What a wave's elites have on top of the above.</summary>
    public static float EliteStrength(int wave) => 1 + EliteGrowth * Math.Max(0, wave - EliteGrowthAfter);

    /// <summary>Every so many waves a boss comes.</summary>
    public const int BossEvery = 5;

    /// <summary>What multiplies a boss's hit points each time the list comes round.</summary>
    public const float BossLapHitPoints = 1.6f;

    /// <summary>What a boss below half calls in: this share of the wave's budget, at least the given BMUs, in so many machines.</summary>
    public const float ReinforcementShare = 0.25f, LeastReinforcements = 80;

    public const int MostReinforcements = 6;

    // ---- death and score --------------------------------------------------------------------------

    /// <summary>Seconds from the hero's death to its new body standing at the pod.</summary>
    public const float RespawnTime = 6;

    /// <summary>What a wave cleared and a boss destroyed add to the score.</summary>
    public const int ScorePerWave = 150, ScorePerBoss = 500;

    /// <summary>The score in percent of its sum, at a difficulty.</summary>
    public static int ScorePercent(WiredDifficulty difficulty) => difficulty switch
    {
        WiredDifficulty.Hard => 150,
        WiredDifficulty.Hardcore => 200,
        _ => 100,
    };

    /// <summary>The score of a run so far: what was destroyed, waves, bosses and half of all the BMUs earned.</summary>
    public static long Score(WiredStats stats, WiredDifficulty difficulty) =>
        (stats.KillValue + (long)ScorePerWave * stats.WavesCleared + (long)ScorePerBoss * stats.BossesKilled + stats.BmusEarned / 2) * ScorePercent(difficulty) / 100;

    // ---- the squad --------------------------------------------------------------------------------

    /// <summary>The squadron bought escorts join.</summary>
    public const int EscortSquad = 1;

    /// <summary>What an escort costs, in percent of its hardware cost.</summary>
    public const int EscortPercent = 120;

    /// <summary>The escorts allowed at first, and at most.</summary>
    public const int SmallestSquad = 3, LargestSquad = 6;

    /// <summary>The waves that allow one escort more.</summary>
    public static readonly IReadOnlyList<int> SquadGrowsAt = [5, 10, 15];

    /// <summary>The escorts allowed when <paramref name="wave"/> is the wave coming or under way, with what chips add: never more than the largest squad.</summary>
    public static int SquadLimit(int wave, int extra = 0) => Math.Min(LargestSquad, SmallestSquad + SquadGrowsAt.Count(w => wave >= w) + extra);
}
