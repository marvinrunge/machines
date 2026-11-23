using System.Numerics;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>What a unit of the armoury's waves is, for its pace, its fight and what its wreck yields.</summary>
public enum StoryWaveKind
{
    /// <summary>Grunt 1 with a Bolter: drives to its post by its door and fights from there.</summary>
    Bolter,

    /// <summary>Grunt 1 with an electric charge: rushes the nearest of the trio.</summary>
    Zap,

    /// <summary>Grunt 2 with a Bolter (gate N only): tracked, slower, tougher.</summary>
    Tank,

    /// <summary>Grunt 2 with an electric charge (gate N only): a slow, tough rusher.</summary>
    TankZap,

    /// <summary>The heavy Grunt of batch 5, Old Iron (StoryHeavy).</summary>
    Heavy,
}

/// <summary>One unit of a batch: what it is, the door it comes by, where it waits behind it, and its delay from the batch's start (seconds).</summary>
public sealed record StoryWaveUnit(StoryWaveKind Kind, char Door, string Staging, float Delay);

/// <summary>A batch of the armoury: its number, the pause before it (seconds; the first is the lockdown's short one) and its units.</summary>
public sealed record StoryWavePlan(int Number, float PauseBefore, IReadOnlyList<StoryWaveUnit> Units);

/// <summary>A wave unit as the waves keep it: its batch, what it is, its door, and when it was last hurt and last moved.</summary>
public sealed class StoryWaveFoe(int wave, StoryWaveKind kind, char door, string staging)
{
    public int Wave { get; } = wave;
    public StoryWaveKind Kind { get; } = kind;
    public char Door { get; } = door;
    public string Staging { get; } = staging;
    /// <summary>Its hit points at the last look, and the game time it was last hurt (its spawn at first).</summary>
    public int HitPoints { get; set; }
    public float HurtAt { get; set; }
    /// <summary>Where it stood when last it moved 2 m, and since when (the anti-hang review).</summary>
    public Vector2 StillAt { get; set; }
    public float StillSince { get; set; }
}

/// <summary>A wreck of a wave unit (GameWorld.Debris registers it as it drops): what it was and what was taken from it.</summary>
public sealed class StoryWreck(StoryWaveKind kind, int wave, Vector2 position)
{
    public StoryWaveKind Kind { get; } = kind;
    public int Wave { get; } = wave;
    public Vector2 Position { get; } = position;
    /// <summary>Stripped of its Bolter (or of all it had): the view shows it bared.</summary>
    public bool Stripped { get; set; }
    /// <summary>The salvage it is marked for at the end (<see cref="StoryWaves.Grunt2Drive"/>, <see cref="StoryWaves.ElectricCharge"/>), null when none; cleared once stripped.</summary>
    public string? Marked { get; set; }

    /// <summary>Whether its weapon is a Bolter (a Bolter wreck yields one while the batches last).</summary>
    public bool HasBolter => Kind is StoryWaveKind.Bolter or StoryWaveKind.Tank;

    /// <summary>Whether it was an electric unit.</summary>
    public bool Electric => Kind is StoryWaveKind.Zap or StoryWaveKind.TankZap;
}

/// <summary>A held Use of the armoury's (mend, mount, strip): its target and how many ticks it has run.</summary>
public sealed class StoryWaveHold(string target)
{
    public string Target { get; } = target;
    public int Ticks { get; set; }
    /// <summary>The holder's hit points at the last tick: a hit breaks the hold, as it does a revive.</summary>
    public int HitPoints { get; set; } = -1;
}

/// <summary>Where the heavy Grunt's charge is (GameWorld.StoryCharge).</summary>
public enum StoryChargePhase
{
    /// <summary>Cruising and firing, the next charge waiting for its time.</summary>
    Cruise,
    /// <summary>Stopped, turned on its line, the engine roaring (1.5 s).</summary>
    Telegraph,
    /// <summary>Straight down the locked line, unable to turn.</summary>
    Run,
    /// <summary>Crashed into something: the radiator open, its armour off (3 s).</summary>
    Stunned,
}

/// <summary>
/// The armoury's waves (plan stufe7 §4 W, design-waves): five batches of the barracks' freshly updated Grunts through three
/// doors, the pauses between them with their bar, the press and anti-hang rules, the wrecks and what L-7 strips, mends and
/// mounts, and the heavy Grunt's charge. The waves run from the director's beat <c>armoury</c> (StoryDirector.Armoury, a
/// look every tick); everything here changes on ticks and goes into <see cref="Fingerprint"/>.
/// </summary>
public sealed class StoryWaves
{
    // ---- the numbers --------------------------------------------------------------------------------

    /// <summary>The most units of a batch alive at once: a spawn that is due waits.</summary>
    public const int MostAlive = 5;

    /// <summary>Every so many seconds the unit of the batch hurt longest ago presses in on the Locator.</summary>
    public const float PressEvery = 10;

    /// <summary>The anti-hang review: every so many seconds; a unit that has not moved <see cref="HangMetres"/> in <see cref="HangSeconds"/> is set back behind its door.</summary>
    public const float ReviewEvery = 3, HangMetres = 2, HangSeconds = 12;

    /// <summary>The pauses' bar runs from this share when it starts (the first, the lockdown's, from 82 %).</summary>
    public const float FirstBarFrom = 0.82f;

    /// <summary>The early call: the bar jumps to this many seconds once the trio stands ready (everyone at 90 %, no Bolter left to mount).</summary>
    public const float EarlyCall = 5, ReadyShare = 0.9f;

    /// <summary>L-7 mends itself while the armoury is sealed and no unit stands: this share of its hit points a second.</summary>
    public const float SelfRepair = 0.04f;

    /// <summary>Mending a standing Grunt: this share of its hit points (and armour) a second.</summary>
    public const float MendRate = 0.1f;

    /// <summary>How near L-7 must stand to a Grunt or a wreck for its holds (metres, beyond both radii for a Grunt).</summary>
    public const float Reach = 4;

    /// <summary>A strip takes so long; a mount so long.</summary>
    public const float StripSeconds = 3, MountSeconds = 2;

    /// <summary>The share of the Locator's hit points an electric unit's strike takes (four kill it; 0.35 before the driver's
    /// run, where two Grunt 2 tanks and a zap downed it in batches 3 and 4 again and again); the Grunts take the usual half harm.</summary>
    public const float ZapShare = 0.25f;

    /// <summary>Barricade: when the ship opens a door with this many crates or more in its slots, its units shoot at them this long (seconds; batches 1–2, then from 3 on).</summary>
    public const int BarricadeCrates = 2;
    public const float BarricadeEarly = 6, BarricadeLate = 4;

    /// <summary>Each person in a Grunt's seat adds one Bolter Grunt to batches 2–4 (door T, this late) and this share to the boss's hit points.</summary>
    public const float CoopDelay = 12, CoopBossShare = 0.15f;

    /// <summary>The pace (share of top speed) each kind keeps (C12, through Then.Pace by its role).</summary>
    public static float PaceOf(StoryWaveKind kind) => kind switch
    {
        StoryWaveKind.Zap => 0.75f,
        StoryWaveKind.TankZap => 0.6f,
        StoryWaveKind.Tank => 0.4f,
        StoryWaveKind.Heavy => StoryHeavy.Cruise,
        _ => 0.5f,
    };

    /// <summary>The salvage ids the end strips set (StoryRun.Salvage, StoryProgress.Salvaged).</summary>
    public const string Grunt2Drive = "chassis:grunt2", ElectricCharge = "weapon:ELECTRIC_CHARGE";

    /// <summary>The weapon L-7 carries from a Bolter wreck to an unarmed Grunt.</summary>
    public const string Bolter = "BOLTER";

    /// <summary>The roles the waves cast their units in, by kind; the escorts of the boss in their own.</summary>
    public static string RoleOf(StoryWaveKind kind) => kind switch
    {
        StoryWaveKind.Zap => "wave_zap",
        StoryWaveKind.Tank => "wave_tank",
        StoryWaveKind.TankZap => "wave_tankzap",
        StoryWaveKind.Heavy => StoryHeavy.BossRole,
        _ => "wave_bolter",
    };

    /// <summary>All the roles of wave units, the boss's last.</summary>
    public static readonly IReadOnlyList<string> Roles = ["wave_bolter", "wave_zap", "wave_tank", "wave_tankzap", StoryHeavy.BossRole];

    /// <summary>The machine each kind is.</summary>
    public static StoryUnit UnitOf(StoryWaveKind kind) => kind switch
    {
        StoryWaveKind.Zap => Prologue.GruntZap,
        StoryWaveKind.Tank => Prologue.Grunt2,
        StoryWaveKind.TankZap => Prologue.Grunt2Zap,
        StoryWaveKind.Heavy => Prologue.HeavyGrunt,
        _ => Prologue.HostileGrunt,
    };

    /// <summary>The posts by a door its units drive to.</summary>
    public static IReadOnlyList<string> PostsOf(char door) => door switch
    {
        'R' => ["wpost_r_1", "wpost_r_2", "wpost_r_3"],
        'T' => ["wpost_t_1", "wpost_t_2", "wpost_t_3"],
        _ => ["wpost_n_1", "wpost_n_2", "wpost_n_3"],
    };

    /// <summary>The five batches (design-waves §4): 3 / 4 / 5 / 6 units and the boss; the boss's two escorts come at its half.</summary>
    public static readonly IReadOnlyList<StoryWavePlan> Plan =
    [
        new(1, 10, [new(StoryWaveKind.Bolter, 'R', "wave_r_1", 0), new(StoryWaveKind.Bolter, 'R', "wave_r_2", 4), new(StoryWaveKind.Bolter, 'R', "wave_r_3", 8)]),
        new(2, 35, [new(StoryWaveKind.Bolter, 'T', "wave_t_1", 0), new(StoryWaveKind.Bolter, 'T', "wave_t_2", 3), new(StoryWaveKind.Bolter, 'R', "wave_r_1", 6),
            new(StoryWaveKind.Zap, 'R', "wave_r_2", 9)]),
        new(3, 25, [new(StoryWaveKind.Tank, 'N', "wave_n_1", 0), new(StoryWaveKind.Tank, 'N', "wave_n_2", 9), new(StoryWaveKind.Zap, 'R', "wave_r_1", 12),
            new(StoryWaveKind.Bolter, 'T', "wave_t_1", 10), new(StoryWaveKind.Bolter, 'T', "wave_t_2", 13)]),
        new(4, 25, [new(StoryWaveKind.Tank, 'N', "wave_n_1", 0), new(StoryWaveKind.Bolter, 'R', "wave_r_1", 3), new(StoryWaveKind.Bolter, 'T', "wave_t_1", 5),
            new(StoryWaveKind.TankZap, 'N', "wave_n_2", 6), new(StoryWaveKind.Bolter, 'R', "wave_r_2", 7), new(StoryWaveKind.Zap, 'T', "wave_t_2", 10)]),
        new(5, 35, [new(StoryWaveKind.Heavy, 'N', "boss_n", 0)]),
    ];

    /// <summary>The boss's escorts at its half: a Bolter Grunt by R at once and one by T 2 s later.</summary>
    public static readonly IReadOnlyList<StoryWaveUnit> Escorts =
        [new(StoryWaveKind.Bolter, 'R', "wave_r_1", 0), new(StoryWaveKind.Bolter, 'T', "wave_t_1", 2)];

    /// <summary>The door each batch names on the HUD as the next ("NÄCHSTE CHARGE: TÜR OST"), the doors of its units in order.</summary>
    public static string DoorsOf(int wave) => wave >= 1 && wave <= Plan.Count
        ? new string(Plan[wave - 1].Units.Select(u => u.Door).Distinct().ToArray()) : "";

    // ---- what it keeps ------------------------------------------------------------------------------

    /// <summary>The batch fighting or next to come (0 before the lockdown); <see cref="Fighting"/> says which.</summary>
    public int Wave { get; set; }

    /// <summary>A batch is in the room (its spawns begun and not all down).</summary>
    public bool Fighting { get; set; }

    /// <summary>The batches cleared so far.</summary>
    public int Cleared { get; set; }

    /// <summary>The game time the batch began, and how many of its units have been set down (escorts and co-op ones included).</summary>
    public float WaveStart { get; set; }
    public int Spawned { get; set; }

    /// <summary>The units still to come of the batch (escorts and co-op extras added as they are due), by delay.</summary>
    public List<StoryWaveUnit> Due { get; } = [];

    /// <summary>The pause before the next batch: when its bar started, and when the batch comes (game time); the share the bar started at.</summary>
    public float PauseStart { get; set; }
    public float PauseEnds { get; set; }
    public float BarFrom { get; set; }

    /// <summary>The early call was made in this pause.</summary>
    public bool Called { get; set; }

    /// <summary>The wave units in the room, by machine id.</summary>
    public SortedDictionary<int, StoryWaveFoe> Foes { get; } = [];

    /// <summary>The doors the batch opened (closed again at its end) and those it found barricaded, with the time the shooting at the crates ends.</summary>
    public SortedSet<char> Opened { get; } = [];
    public SortedDictionary<char, float> Barricades { get; } = [];

    /// <summary>When the next press and the next review come.</summary>
    public float NextPress { get; set; }
    public float NextReview { get; set; }

    /// <summary>The wrecks of wave units, by debris id.</summary>
    public SortedDictionary<int, StoryWreck> Wrecks { get; } = [];

    /// <summary>What L-7 carries from a wreck (a Bolter), null when nothing.</summary>
    public string? Carried { get; set; }

    /// <summary>The armoury's holds of Use, by the holder's player.</summary>
    public SortedDictionary<int, StoryWaveHold> Holds { get; } = [];

    /// <summary>The Grunts (by seat's player) that had a Bolter mounted.</summary>
    public SortedSet<int> Mounted { get; } = [];

    /// <summary>Counters for the driver and the tests: units pressed, set back, Bolters stripped, Grunts mended (finished).</summary>
    public int Presses { get; set; }
    public int Fetched { get; set; }
    public int Stripped { get; set; }
    public int Mends { get; set; }

    // The heavy Grunt's charge.

    /// <summary>The boss's machine id (0: none yet), its charge's phase and since when, the next charge's time.</summary>
    public int Boss { get; set; }
    public StoryChargePhase Phase { get; set; }
    public float PhaseSince { get; set; }
    public float NextCharge { get; set; }

    /// <summary>The locked line: where the run began, which way, at whom (machine id).</summary>
    public Vector2 RunFrom { get; set; }
    public Vector2 RunWay { get; set; }
    public int RunTarget { get; set; }

    /// <summary>The heroes the current run has hit already (each once).</summary>
    public SortedSet<int> RunHits { get; } = [];

    /// <summary>The boss's armour before a stun took it off (restored as the stun ends).</summary>
    public int StunArmour { get; set; }

    /// <summary>A Grunt's ram added its second to this stun already.</summary>
    public bool StunRammed { get; set; }

    /// <summary>Charges begun, heroes hit, crashes.</summary>
    public int Charges { get; set; }
    public int ChargeHits { get; set; }
    public int Crashes { get; set; }

    /// <summary>While the charge's own hit is dealt (GameWorld.StoryCharge), its share for <see cref="Harm"/>.</summary>
    internal float? ChargeShare { get; set; }

    /// <summary>Whether a machine is a unit of the waves (the boss and the escorts included).</summary>
    public bool IsFoe(int id) => Foes.ContainsKey(id);

    /// <summary>The batch's units still standing in the world.</summary>
    public int Alive(GameWorld world) => Foes.Keys.Count(id => world.Find<Machine>(id) is { IsDead: false });

    /// <summary>The bar of the pause: 0 to 1 at <paramref name="now"/>; 1 while a batch fights.</summary>
    public float Bar(float now) => Fighting || PauseEnds <= PauseStart ? 1
        : Math.Clamp(BarFrom + (1 - BarFrom) * (now - PauseStart) / (PauseEnds - PauseStart), 0, 1);

    /// <summary>A fingerprint of what the waves keep, for the run's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        static int F(float value) => BitConverter.SingleToInt32Bits(value);
        hash.Mix(Wave);
        hash.Mix(Fighting ? 1 : 0);
        hash.Mix(Cleared);
        hash.Mix(F(WaveStart));
        hash.Mix(Spawned);
        foreach (var unit in Due)
        {
            hash.Mix((int)unit.Kind);
            hash.Mix(unit.Door);
            hash.Mix(unit.Staging);
            hash.Mix(F(unit.Delay));
        }
        hash.Mix(F(PauseStart));
        hash.Mix(F(PauseEnds));
        hash.Mix(F(BarFrom));
        hash.Mix(Called ? 1 : 0);
        foreach (var (id, foe) in Foes)
        {
            hash.Mix(id);
            hash.Mix(foe.Wave);
            hash.Mix((int)foe.Kind);
            hash.Mix(foe.HitPoints);
            hash.Mix(F(foe.HurtAt));
            hash.Mix(F(foe.StillAt.X));
            hash.Mix(F(foe.StillAt.Y));
            hash.Mix(F(foe.StillSince));
        }
        foreach (var door in Opened)
            hash.Mix(door | 0x100);
        foreach (var (door, until) in Barricades)
        {
            hash.Mix(door | 0x200);
            hash.Mix(F(until));
        }
        hash.Mix(F(NextPress));
        hash.Mix(F(NextReview));
        foreach (var (id, wreck) in Wrecks)
        {
            hash.Mix(id);
            hash.Mix((int)wreck.Kind);
            hash.Mix(wreck.Wave);
            hash.Mix(wreck.Stripped ? 1 : 0);
            hash.Mix(wreck.Marked);
        }
        hash.Mix(Carried);
        foreach (var (player, hold) in Holds)
        {
            hash.Mix(player);
            hash.Mix(hold.Target);
            hash.Mix(hold.Ticks);
            hash.Mix(hold.HitPoints);
        }
        foreach (var player in Mounted)
            hash.Mix(player);
        hash.Mix(Presses);
        hash.Mix(Fetched);
        hash.Mix(Stripped);
        hash.Mix(Mends);
        hash.Mix(Boss);
        hash.Mix((int)Phase);
        hash.Mix(F(PhaseSince));
        hash.Mix(F(NextCharge));
        hash.Mix(F(RunFrom.X));
        hash.Mix(F(RunFrom.Y));
        hash.Mix(F(RunWay.X));
        hash.Mix(F(RunWay.Y));
        hash.Mix(RunTarget);
        foreach (var id in RunHits)
            hash.Mix(id);
        hash.Mix(StunArmour);
        hash.Mix(StunRammed ? 1 : 0);
        hash.Mix(Charges);
        hash.Mix(ChargeHits);
        hash.Mix(Crashes);
        return hash.Value;
    }

    /// <summary>
    /// The share of a hit of <paramref name="shooter"/> on <paramref name="target"/> this part takes over (GameWorld.StoryHarm);
    /// null: not its hit. The boss's charge sets its own share while it is dealt; an electric unit's strike takes
    /// <see cref="ZapShare"/> of the Locator's hit points (four kill it), and the Grunts take it at the usual half harm.
    /// </summary>
    public float? Harm(Actor target, Actor? shooter)
    {
        if (ChargeShare is { } charge && shooter?.Id == Boss)
            return charge;
        if (shooter is null || !Foes.TryGetValue(shooter.Id, out var foe) || foe.Kind is not (StoryWaveKind.Zap or StoryWaveKind.TankZap))
            return null;
        return target.Id == Locator ? ZapShare : null;
    }

    /// <summary>The Locator's machine id, kept for <see cref="Harm"/> (set at each look of the waves).</summary>
    internal int Locator { get; set; }
}
