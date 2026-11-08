using Machines.Import.Model;

namespace Machines.Sim.Survival;

/// <summary>What sets a wave apart from the others. All of these are the remake's own.</summary>
public enum WiredSpecial
{
    None,

    /// <summary>Forced darkness: scanners halve and aim suffers, unless a machine has night vision.</summary>
    Night,

    /// <summary>Twice as many machines, the fastest of those the budget still buys that many of.</summary>
    Storm,

    /// <summary>Spy locators arrive first and lay mines on the way and round the pod.</summary>
    Spies,

    /// <summary>A commander with an orb of treachery tries to turn the squad.</summary>
    Treason,

    /// <summary>Gliders and nothing else.</summary>
    AirRaid,

    /// <summary>Ballistas stay at long range and shell the pod.</summary>
    Siege,

    /// <summary>Real ion strikes come down on the hero or the pod, each announced by a circle.</summary>
    IonStorm,
}

/// <summary>What a machine of the waves is there for, beyond fighting what it meets.</summary>
public enum WiredRole
{
    Fighter,

    /// <summary>It lays mines and does not fight.</summary>
    Spy,

    /// <summary>It goes for the squad with its orb of treachery.</summary>
    Turncoat,

    /// <summary>It shells the pod from as far as its weapons reach and leaves all else alone.</summary>
    Besieger,
}

/// <summary>
/// A boss of the Wired waves: <paramref name="Name"/> is shown over its bar (an English source string for the view's
/// translations), <paramref name="HitPoints"/> multiplies the type's. <paramref name="Guard"/> comes along beside the
/// wave's escort, <paramref name="Guards"/> of them, healing the boss if <paramref name="GuardsHeal"/>.
/// <paramref name="Damage"/> multiplies what its weapons do, for one whose weapon was made for a stage of the game further on.
/// </summary>
public sealed record WiredBoss(string Name, ResearchKey Machine, float HitPoints, int Count = 1, ResearchKey? Guard = null, int Guards = 0, bool GuardsHeal = false, float Damage = 1);

/// <summary>One machine a wave brings. <paramref name="Key"/> is the real machine; the rest says what makes this one special.</summary>
public sealed record WiredFoePlan(ResearchKey Key)
{
    /// <summary>The boss it is, if it is one.</summary>
    public WiredBoss? Boss { get; init; }

    public bool Elite { get; init; }

    /// <summary>It stays with the wave's boss and heals it.</summary>
    public bool Healer { get; init; }

    public WiredRole Role { get; init; }

    /// <summary>What multiplies its type's hit points and its weapons' damage.</summary>
    public float HitPoints { get; init; } = 1;

    public float Damage { get; init; } = 1;

    /// <summary>What multiplies its cost, and so its wreck and its kill value: the first waves' weaker machines are worth less.</summary>
    public float Worth { get; init; } = 1;
}

/// <summary>Machines that arrive together, <paramref name="After"/> so many seconds into the wave.</summary>
public sealed record WiredGroup(float After, IReadOnlyList<WiredFoePlan> Machines);

/// <summary>What a wave will bring, group by group, the earliest first. <paramref name="Boss"/> is null on a wave without one.</summary>
public sealed record WiredWavePlan(int Wave, float Budget, WiredBoss? Boss, IReadOnlyList<WiredGroup> Groups, WiredSpecial Special = WiredSpecial.None);

/// <summary>
/// What the waves of a Wired run are made of, the remake's own design. Everything here is pure: it takes the world's
/// random numbers and hands back plans, which <c>GameWorld.Wired</c> carries out.
/// </summary>
public static class WiredDirector
{
    /// <summary>The bosses, one every <see cref="WiredBalance.BossEvery"/> waves; then the list starts over, stronger.</summary>
    public static readonly IReadOnlyList<WiredBoss> Bosses =
    [
        new("The Crusher", new ResearchKey("AGGRESSOR", "BALLISTA", 3, "T_VORTEX"), 8, Damage: 0.2f),
        new("Ironfist", new ResearchKey("AGGRESSOR", "KNIGHT", 3, "LR_PLASMA_CANNON1_X2"), 8),
        new("The Traitor", new ResearchKey("ADMINISTRATOR", "COMMANDER", 4, "L_MULTI_LAUNCHER5_R_TREACHERY"), 7,
            Guard: new ResearchKey("ADMINISTRATOR", "OVERSEER", 2, "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED"), Guards: 4, GuardsHeal: true),
        new("Primus", new ResearchKey("AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7"), 3),
        new("The Twins", new ResearchKey("AGGRESSOR", "KNIGHT", 5, "LRT_PLASMA_CANNON2_X3"), 4, Count: 2),
        new("The Queen", new ResearchKey("ADMINISTRATOR", "BOSS", 2, "T_BEE_BOMB"), 8,
            Guard: new ResearchKey("ADMINISTRATOR", "BOSS", 1, "T_WASP_LIGHT_STING"), Guards: 6),
    ];

    public static bool IsBossWave(int wave) => wave > 0 && wave % WiredBalance.BossEvery == 0;

    /// <summary>The boss of a wave, or null.</summary>
    public static WiredBoss? BossOf(int wave) => IsBossWave(wave) ? Bosses[(wave / WiredBalance.BossEvery - 1) % Bosses.Count] : null;

    /// <summary>What multiplies a boss's hit points for each time the list has come round before.</summary>
    public static float LapStrength(int wave)
    {
        var strength = 1f;
        for (var lap = (wave / WiredBalance.BossEvery - 1) / Bosses.Count; lap > 0; lap--)
            strength *= WiredBalance.BossLapHitPoints;
        return strength;
    }

    /// <summary>The machines special waves bring, whatever the tiers have opened by then.</summary>
    public static readonly ResearchKey Spy = new("SPY_LOCATOR", null, 3, null), BetterSpy = new("SPY_LOCATOR", null, 5, null),
        Turncoat = new("ADMINISTRATOR", "COMMANDER", 4, "L_MULTI_LAUNCHER5_R_TREACHERY"),
        Besieger = new("AGGRESSOR", "BALLISTA", 3, "T_MULTI_LAUNCHER1"), BigBesieger = new("AGGRESSOR", "BALLISTA", 4, "LR_MULTI_LAUNCHER2_X2");

    /// <summary>
    /// Whether a wave is a special one: the first at <see cref="WiredBalance.SpecialFrom"/>, then each third of the waves
    /// without a boss.
    /// </summary>
    public static bool IsSpecialWave(int wave) => wave >= WiredBalance.SpecialFrom && !IsBossWave(wave)
        && Enumerable.Range(WiredBalance.SpecialFrom, wave - WiredBalance.SpecialFrom).Count(w => !IsBossWave(w)) % WiredBalance.SpecialEvery == 0;

    /// <summary>A kind of special wave for a wave, at random among those open by then, other than the last one.</summary>
    public static WiredSpecial RollSpecial(int wave, WiredSpecial last, Random random)
    {
        var open = Enum.GetValues<WiredSpecial>().Where(s => s != WiredSpecial.None && s != last && WiredBalance.SpecialOpens(s) <= wave).ToList();
        return open[random.Next(open.Count)];
    }

    /// <summary>What a tier counts for against a wave's budget: its hardware's cost.</summary>
    private static int Cost(GameData data, WiredTier tier) =>
        data.Parm.Machine($"{tier.Type} {tier.SubType}").Level(tier.Level).Cost;

    private static int Cost(GameData data, ResearchKey key) => data.Parm.Machine(key.TypeName).Level(key.HardwareLevel).Cost;

    /// <summary>
    /// Machines for a budget, at random among the newest tiers open at the wave that it still covers, until it runs out or
    /// there are <paramref name="most"/> of them, of the given <paramref name="tiers"/> if there are any. Even the smallest
    /// budget buys one of the first tier.
    /// </summary>
    public static List<WiredFoePlan> Buy(GameData data, int wave, float budget, Random random, int most = WiredBalance.MostInWave, IReadOnlyList<WiredTier>? tiers = null)
    {
        var open = (tiers ?? WiredCatalogue.Tiers).Where(t => t.Wave <= wave).ToList();
        var bought = new List<WiredFoePlan>();
        while (bought.Count < most)
        {
            var left = budget;
            var within = open.Where(t => Cost(data, t) <= left).TakeLast(WiredBalance.TierSpread).ToList();
            if (within.Count == 0)
                break;
            var tier = within[random.Next(within.Count)];
            budget -= Cost(data, tier);
            bought.Add(Foe(tier, wave, random));
        }
        if (bought.Count == 0)
            bought.Add(Foe(open[0], wave, random));
        return bought;
    }

    /// <summary>A machine of a tier: any of its combos, and now and then an elite.</summary>
    private static WiredFoePlan Foe(WiredTier tier, int wave, Random random) =>
        Foe(new WiredFoePlan(new ResearchKey(tier.Type, tier.SubType, tier.Level, tier.Combos[random.Next(tier.Combos.Count)])), wave, random);

    /// <summary>A planned machine, which may turn out an elite.</summary>
    private static WiredFoePlan Foe(WiredFoePlan plan, int wave, Random random)
    {
        if (wave < WiredBalance.EliteFrom || random.Next(100) >= WiredBalance.ElitePercent)
            return plan;
        var strength = WiredBalance.EliteStrength(wave);
        return plan with { Elite = true, HitPoints = WiredBalance.EliteHitPoints * strength, Damage = WiredBalance.EliteDamage * strength };
    }

    /// <summary>A boss with what comes along with it.</summary>
    private static IEnumerable<WiredFoePlan> Party(WiredBoss boss, int wave)
    {
        var hitPoints = boss.HitPoints * LapStrength(wave);
        for (var i = 0; i < boss.Count; i++)
            yield return new WiredFoePlan(boss.Machine) { Boss = boss, HitPoints = hitPoints, Damage = boss.Damage };
        for (var i = 0; i < boss.Guards; i++)
            yield return new WiredFoePlan(boss.Guard!) { Healer = boss.GuardsHeal };
    }

    /// <summary>
    /// A storm: twice the machines a wave of this budget would bring. So that the budget goes round, it takes the fastest of
    /// those it can pay that many of, leaving out what flies; if it can pay none that often, the cheapest.
    /// </summary>
    private static List<WiredFoePlan> Storm(GameData data, int wave, float budget, Random random)
    {
        var wanted = Math.Min(WiredBalance.MostInWave, Buy(data, wave, budget, random).Count * WiredBalance.StormShare);
        var ground = WiredCatalogue.Tiers.Where(t => t.Wave <= wave && t.SubType != "BOSS").ToList();
        var within = ground.Where(t => Cost(data, t) * wanted <= budget).ToList();
        if (within.Count == 0)
            within = ground.OrderBy(t => Cost(data, t)).Take(WiredBalance.StormKinds).ToList();
        var fastest = within.OrderByDescending(t => data.Parm.Machine($"{t.Type} {t.SubType}").Level(t.Level).Speed).ThenBy(t => Cost(data, t))
            .Take(WiredBalance.StormKinds).ToList();
        return Enumerable.Range(0, wanted).Select(_ => Foe(fastest[random.Next(fastest.Count)], wave, random)).ToList();
    }

    /// <summary>An air raid: the budget in gliders, and at least a pair of them.</summary>
    private static List<WiredFoePlan> AirRaid(GameData data, int wave, float budget, Random random)
    {
        var gliders = WiredCatalogue.Tiers.Where(t => t.SubType == "BOSS").ToList();
        var raid = Buy(data, wave, budget, random, tiers: gliders);
        while (raid.Count < WiredBalance.FewestRaiders)
            raid.Add(Foe(gliders[0], wave, random));
        return raid;
    }

    /// <summary>A siege: the budget in Ballistas, and at least a pair of them, for all that they cost more than an early budget.</summary>
    private static List<WiredFoePlan> Siege(GameData data, int wave, float budget, Random random)
    {
        var key = wave >= WiredBalance.BigSiegeFrom ? BigBesieger : Besieger;
        var count = Math.Clamp((int)(budget / Cost(data, key)), WiredBalance.FewestBesiegers, WiredBalance.MostInWave);
        return Enumerable.Range(0, count).Select(_ => Foe(new WiredFoePlan(key) { Role = WiredRole.Besieger }, wave, random)).ToList();
    }

    /// <summary>The spies of a wave, who go first.</summary>
    private static List<WiredFoePlan> Spies(int wave)
    {
        var count = Math.Min(WiredBalance.MostSpies, WiredBalance.FewestSpies + (wave - WiredBalance.SpecialOpens(WiredSpecial.Spies)) / WiredBalance.WavesPerSpy);
        var key = wave >= WiredBalance.BetterSpiesFrom ? BetterSpy : Spy;
        return Enumerable.Range(0, count).Select(_ => new WiredFoePlan(key) { Role = WiredRole.Spy }).ToList();
    }

    /// <summary>
    /// A wave by its budget: machines in groups of a few, the first at once and the last after 60 to 90 s. On a boss wave
    /// the budget pays for the boss's escort; the boss itself comes on top, with the first group. A special wave spends
    /// the budget in its own way: spies are paid out of it and arrive ahead of the others, a storm, a raid or a siege has
    /// machines of its own, a turncoat comes on top like a boss.
    /// </summary>
    public static WiredWavePlan Plan(GameData data, int wave, WiredDifficulty difficulty, Random random, WiredSpecial special = WiredSpecial.None)
    {
        var budget = WiredBalance.Budget(wave, difficulty);
        var spies = special == WiredSpecial.Spies ? Spies(wave) : [];
        var machines = special switch
        {
            WiredSpecial.Storm => Storm(data, wave, budget, random),
            WiredSpecial.AirRaid => AirRaid(data, wave, budget, random),
            WiredSpecial.Siege => Siege(data, wave, budget, random),
            _ => Crowded(wave, Buy(data, wave, MathF.Max(0, budget - spies.Sum(s => Cost(data, s.Key))), random)),
        };
        var count = Math.Min(machines.Count, Math.Max(WiredBalance.FewestGroups, (machines.Count + WiredBalance.GroupSize - 1) / WiredBalance.GroupSize));
        var spread = WiredBalance.LeastSpread + random.Next(WiredBalance.MostSpread - WiredBalance.LeastSpread + 1);
        // A crowd of weak machines comes close together: one by one, each fell at once and the hero waited for the next.
        if (machines.Any(m => m.Worth < 1))
            (count, spread) = (Math.Min(machines.Count, WiredBalance.CrowdGroups), WiredBalance.CrowdSpread);
        var boss = BossOf(wave);
        var groups = new List<WiredGroup>();
        var lead = spies.Count > 0 ? WiredBalance.SpyLead : 0;
        if (spies.Count > 0)
            groups.Add(new WiredGroup(0, spies));
        for (var i = 0; i < count; i++)
        {
            var group = machines.Take((i + 1) * machines.Count / count).Skip(i * machines.Count / count);
            if (i == 0 && boss is not null)
                group = Party(boss, wave).Concat(group);
            if (i == 0 && special == WiredSpecial.Treason)
                group = group.Prepend(new WiredFoePlan(Turncoat) { Role = WiredRole.Turncoat });
            groups.Add(new WiredGroup(lead + (count > 1 ? spread * i / (float)(count - 1) : 0), group.ToList()));
        }
        return new WiredWavePlan(wave, budget, boss, groups, special);
    }

    /// <summary>
    /// The first waves as more machines, each the weaker: what the budget bought comes as <see cref="WiredBalance.CrowdSize"/>
    /// machines, together <see cref="WiredBalance.CrowdStrength"/> times as strong (none stronger than bought) and worth
    /// what the bought ones were. The owner found the start of a run empty, one Grunt to a wave.
    /// </summary>
    private static List<WiredFoePlan> Crowded(int wave, List<WiredFoePlan> machines)
    {
        if (wave > WiredBalance.CrowdUntil || machines.Count == 0 || machines.Count >= WiredBalance.CrowdSize)
            return machines;
        var count = WiredBalance.CrowdSize;
        var share = MathF.Min(1, WiredBalance.CrowdStrength(wave) * machines.Count / count);
        var worth = machines.Count / (float)count;
        return Enumerable.Range(0, count).Select(i => machines[i % machines.Count])
            .Select(plan => plan with { HitPoints = plan.HitPoints * share, Damage = plan.Damage * share, Worth = plan.Worth * worth }).ToList();
    }

    /// <summary>What a boss below half calls in: a small group from a part of the wave's budget.</summary>
    public static List<WiredFoePlan> Reinforcements(GameData data, int wave, WiredDifficulty difficulty, Random random) =>
        Buy(data, wave, MathF.Max(WiredBalance.LeastReinforcements, WiredBalance.Budget(wave, difficulty) * WiredBalance.ReinforcementShare),
            random, WiredBalance.MostReinforcements);

    /// <summary>
    /// What a crate of a wave holds: a chip from <paramref name="chips"/> now and then, else a weapon, module or ability from
    /// the dearer half of what the shop could offer of that kind by then, which rises with the waves, and from waves 10 and
    /// 20 sometimes at a higher Mk.
    /// </summary>
    public static (string Catalogue, int Mk) Crate(int wave, IReadOnlyList<string> chips, Random random)
    {
        if (chips.Count > 0 && random.Next(100) < WiredBalance.CrateChipPercent)
            return (chips[random.Next(chips.Count)], 1);
        var offers = WiredCatalogue.ForSale(wave);
        var kinds = offers.Select(WiredCatalogue.KindOf).Distinct().ToList();
        var kind = kinds[random.Next(kinds.Count)];
        var stock = offers.Where(id => WiredCatalogue.KindOf(id) == kind).OrderByDescending(WiredCatalogue.Price).ToList();
        var catalogue = stock[random.Next(Math.Max(1, stock.Count / 2))];
        var mk = 1;
        foreach (var from in WiredBalance.CrateMkFrom)
        {
            if (wave >= from && random.Next(100) < WiredBalance.CrateMkPercent)
                mk++;
        }
        return (catalogue, mk);
    }
}
