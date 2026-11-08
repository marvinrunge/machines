using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Sim.Survival;

/// <summary>Where a Wired run stands.</summary>
public enum WiredPhase
{
    /// <summary>The time before the first wave.</summary>
    Prepare,
    Wave,
    Intermission,
    Ended,
}

/// <summary>How a Wired run came to its end.</summary>
public enum WiredEnding
{
    PodLost,
    OutOfBodies,
}

/// <summary>A crate on the ground, from a boss or an elite of <paramref name="Wave"/>. What it holds is rolled when it is picked up, for the hero who does.</summary>
public sealed record WiredPickup(int Id, Vector2 Position, int Wave);

/// <summary>An ion strike announced: it comes down at <paramref name="Position"/> at the time <paramref name="At"/>. The warning circle is <paramref name="Radius"/> wide.</summary>
public sealed record WiredStrike(Vector2 Position, float Radius, float At);

/// <summary>A machine of the waves the hero has turned, until the time <paramref name="BackAt"/> it goes back.</summary>
public sealed record WiredTurned(WiredFoe Foe, float BackAt);

/// <summary>A machine of the waves on the map, with what the director keeps in mind about it.</summary>
public sealed class WiredFoe(Machine machine, WiredFoePlan plan, bool huntsHero, float since)
{
    public Machine Machine { get; } = machine;
    public WiredFoePlan Plan { get; } = plan;

    /// <summary>It goes for the hero and not the pod.</summary>
    public bool HuntsHero { get; } = huntsHero;

    public bool IsBoss => Plan.Boss is not null;

    /// <summary>A boss called in its reinforcements already.</summary>
    public bool CalledForHelp { get; set; }

    /// <summary>Set down near the pod after the last call: it is not fetched a second time for being late.</summary>
    public bool Fetched { get; set; }

    /// <summary>When it last moved, fired or was hit, as far as anyone looked.</summary>
    public float StirredAt { get; set; } = since;

    /// <summary>Where it stood and what it had left at the last look.</summary>
    public Vector2 LastPlace { get; set; } = machine.Position;

    public int LastDurability { get; set; } = machine.HitPoints + machine.Armour;
}

/// <summary>What a Wired run shares between its heroes: where it stands, the wave under way, and each race's hero.</summary>
public sealed class WiredRun(WiredOptions options)
{
    public WiredOptions Options { get; } = options;

    public WiredPhase Phase { get; set; } = WiredPhase.Prepare;

    /// <summary>The last wave begun; 0 before the first.</summary>
    public int Wave { get; set; }

    /// <summary>Seconds until the next wave, between waves.</summary>
    public float Countdown { get; set; } = WiredBalance.PrepareTime;

    /// <summary>Each hero, by race. There is one for now; co-op would add more.</summary>
    public SortedDictionary<Race, WiredState> Heroes { get; } = [];

    /// <summary>The race the waves belong to; nobody plays it.</summary>
    public Race Enemy { get; set; }

    /// <summary>Where groups may arrive: places a path to the pod was found from.</summary>
    public List<Vector2> SpawnPoints { get; } = [];

    /// <summary>When the intermission under way would end with the hangar left shut: Ready pays for the seconds up to then, and not for those an open hangar holds.</summary>
    public float IntermissionEnds { get; set; }

    /// <summary>The wave's boss, while one is under way that has one.</summary>
    public WiredBoss? Boss { get; set; }

    /// <summary>The enemy's machines on the map.</summary>
    public List<WiredFoe> Foes { get; } = [];

    /// <summary>Machines of the waves fighting for the hero for now, which still have to be dealt with.</summary>
    public List<WiredTurned> Turned { get; } = [];

    /// <summary>What sets the wave under way apart; <see cref="WiredSpecial.None"/> between waves.</summary>
    public WiredSpecial Special { get; set; }

    /// <summary>What sets the wave to come apart, known ahead for the HUD to announce it.</summary>
    public WiredSpecial NextSpecial { get; set; }

    /// <summary>The kind of the last special wave: none comes twice running.</summary>
    public WiredSpecial LastSpecial { get; set; }

    /// <summary>The ion strikes announced and still to come down, the next first.</summary>
    public List<WiredStrike> Strikes { get; } = [];

    /// <summary>When an ion storm next announces a strike.</summary>
    public float NextStrike { get; set; }

    /// <summary>The groups of the wave still to come, the next first.</summary>
    public List<WiredGroup> Groups { get; } = [];

    /// <summary>When the wave under way began, when a group last arrived, and when the director next looks over the machines.</summary>
    public float WaveStarted { get; set; }

    public float LastGroupAt { get; set; }

    public float NextReview { get; set; }

    /// <summary>How the run ended; null while it lasts.</summary>
    public WiredEnding? Ending { get; set; }

    /// <summary>
    /// The most machines the wave under way has had to deal with at once, for the share of them the HUD shows left. It
    /// follows from the rest, so the fingerprint leaves it out.
    /// </summary>
    public int MostEnemies { get; set; }

    /// <summary>The machines of the wave still to deal with, on the map or to come.</summary>
    public int EnemiesLeft => Foes.Count + Turned.Count + Groups.Sum(g => g.Machines.Count);

    /// <summary>The bosses on the map, for their bars.</summary>
    public IEnumerable<WiredFoe> Bosses => Foes.Where(f => f.IsBoss);

    public bool BetweenWaves => Phase is WiredPhase.Prepare or WiredPhase.Intermission;

    /// <summary>Whether the wave is still waited for. Ready ends that at once, though the wave itself comes only at the end of the tick.</summary>
    public bool Waiting => BetweenWaves && Countdown > 0;

    /// <summary>The wave the shop and the squad go by: the one under way, or else the one coming.</summary>
    public int ShopWave => Phase == WiredPhase.Wave ? Wave : Wave + 1;

    /// <summary>A fingerprint of the run for the world's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        hash.Mix((int)Phase);
        hash.Mix(Wave);
        hash.Mix(BitConverter.SingleToInt32Bits(Countdown));
        hash.Mix(BitConverter.SingleToInt32Bits(IntermissionEnds));
        hash.Mix((int)Enemy);
        foreach (var foe in Foes)
        {
            hash.Mix(foe.Machine.Id);
            hash.Mix(foe.HuntsHero ? 1 : 0);
            hash.Mix(foe.CalledForHelp ? 1 : 0);
            hash.Mix(foe.Fetched ? 1 : 0);
            hash.Mix(BitConverter.SingleToInt32Bits(foe.StirredAt));
            hash.Mix(BitConverter.SingleToInt32Bits(foe.LastPlace.X));
            hash.Mix(BitConverter.SingleToInt32Bits(foe.LastPlace.Y));
            hash.Mix(foe.LastDurability);
        }
        foreach (var group in Groups)
        {
            hash.Mix(BitConverter.SingleToInt32Bits(group.After));
            hash.Mix(group.Machines.Count);
        }
        hash.Mix((int)Special);
        hash.Mix((int)NextSpecial);
        hash.Mix((int)LastSpecial);
        hash.Mix(BitConverter.SingleToInt32Bits(NextStrike));
        hash.Mix(Boss?.Name);
        hash.Mix(BitConverter.SingleToInt32Bits(WaveStarted));
        hash.Mix(BitConverter.SingleToInt32Bits(LastGroupAt));
        hash.Mix(BitConverter.SingleToInt32Bits(NextReview));
        hash.Mix(Ending is { } ending ? (int)ending : -1);
        hash.Mix(SpawnPoints.Count);
        foreach (var turned in Turned)
        {
            hash.Mix(turned.Foe.Machine.Id);
            hash.Mix(BitConverter.SingleToInt32Bits(turned.BackAt));
        }
        foreach (var strike in Strikes)
        {
            hash.Mix(BitConverter.SingleToInt32Bits(strike.Position.X));
            hash.Mix(BitConverter.SingleToInt32Bits(strike.Position.Y));
            hash.Mix(BitConverter.SingleToInt32Bits(strike.At));
        }
        foreach (var hero in Heroes.Values)
            hero.MixInto(hash);
        return hash.Value;
    }
}

/// <summary>FNV-1a over whole numbers and strings; <see cref="string.GetHashCode()"/> differs from one run of the game to the next.</summary>
internal sealed class WiredHash
{
    public long Value { get; private set; } = unchecked((long)14695981039346656037UL);

    public void Mix(long value)
    {
        for (var i = 0; i < 8; i++)
            Value = unchecked((Value ^ (byte)(value >> (8 * i))) * 1099511628211L);
    }

    public void Mix(string? text)
    {
        Mix(text?.Length ?? -1);
        foreach (var c in text ?? "")
            Mix(c);
    }
}

/// <summary>
/// One race's hero in a Wired run: all it owns, what it wears, its spare bodies, its shop and what it has done. Everything
/// here changes through commands or the world's ticks, so that a recording brings it back.
/// </summary>
public sealed class WiredState
{
    private int _nextItem = 1;
    private (HeroLoadout Loadout, HeroBuild Build)? _built;
    private (HeroLoadout Loadout, List<(WiredChip Chip, float Strength)> Chips)? _chips;

    private WiredState(Race race, WiredDifficulty difficulty, string chassis)
    {
        Race = race;
        Difficulty = difficulty;
        Loadout = HeroLoadout.Bare(Add(WiredKind.Chassis, chassis, 0));
        SpareBodies = WiredBalance.SpareBodies(difficulty);
    }

    public Race Race { get; }
    public WiredDifficulty Difficulty { get; }

    /// <summary>The machine that is the hero, while it stands.</summary>
    public int? Hero { get; set; }

    /// <summary>The pod it defends.</summary>
    public int? Pod { get; set; }

    /// <summary>All it owns, worn or not.</summary>
    public List<WiredItem> Items { get; } = [];

    public HeroLoadout Loadout { get; set; }

    public int SpareBodies { get; set; }

    /// <summary>When a fallen hero's new body stands at the pod; null otherwise.</summary>
    public float? RespawnAt { get; set; }

    /// <summary>What it has done in this run.</summary>
    public WiredStats Stats { get; } = new();

    /// <summary>Its score so far.</summary>
    public long Score => WiredBalance.Score(Stats, Difficulty);

    /// <summary>The shop's offers by catalogue id; null where one was bought.</summary>
    public List<string?> Offer { get; } = [];

    /// <summary>Whether this intermission's reroll is used up.</summary>
    public bool Rerolled { get; set; }

    /// <summary>The chips offered after a boss, until one is chosen.</summary>
    public List<string> ChipChoice { get; } = [];

    public bool HangarOpen { get; set; }

    /// <summary>How often the hangar has been opened: it tells the screen one opening from the next.</summary>
    public int HangarOpens { get; set; }

    /// <summary>The chassis it has ever owned: selling one does not close the way on from it.</summary>
    public HashSet<string> Reached { get; } = [];

    /// <summary>The machines it bought for its squad, by id, whichever squadron the player has put them in. Lost ones stay listed; <see cref="GameWorld.Escorts"/> gives those still standing.</summary>
    public SortedSet<int> EscortIds { get; } = [];

    /// <summary>When an ability, by its item, can be used again; one not listed is ready. It stays with the item when that is taken off.</summary>
    public SortedDictionary<int, float> AbilityReadyAt { get; } = [];

    /// <summary>The wave from which an ability that counts in waves can be used again.</summary>
    public SortedDictionary<int, int> AbilityReadyWave { get; } = [];

    /// <summary>When the thrusters can be fired again, until when a dash lasts, and which way it goes.</summary>
    public float DashReadyAt { get; set; }

    public float DashUntil { get; set; }

    public Vector2 DashWay { get; set; }

    /// <summary>When the hero was last hit.</summary>
    public float LastHitAt { get; set; } = float.NegativeInfinity;

    /// <summary>When its last round left, and when its present fire began.</summary>
    public float LastShotAt { get; set; } = float.NegativeInfinity;

    public float FiringSince { get; set; }

    /// <summary>Until when Revenge adds to its damage, and until when nothing can hurt it.</summary>
    public float RevengeUntil { get; set; }

    public float InvulnerableUntil { get; set; }

    /// <summary>The wave Emergency repair last saved it in, as it does once a wave.</summary>
    public int LastDitchWave { get; set; } = -1;

    /// <summary>When it last called on the squad to cover it.</summary>
    public float CoverCalledAt { get; set; } = float.NegativeInfinity;

    /// <summary>What it owns and does not wear.</summary>
    public IEnumerable<WiredItem> Inventory => Items.Where(i => !Loadout.Holds(i.Id));

    public WiredItem? Item(int id) => Items.Find(i => i.Id == id);

    /// <summary>What the loadout comes to, worked out once for each loadout.</summary>
    public HeroBuild Build(GameData data)
    {
        if (_built is not { } built || !ReferenceEquals(built.Loadout, Loadout))
            _built = built = (Loadout, HeroBuild.Resolve(data, Loadout));
        return built.Build;
    }

    /// <summary>The chips in the sockets with their strength, linking included, worked out once for each loadout.</summary>
    public IReadOnlyList<(WiredChip Chip, float Strength)> Chips
    {
        get
        {
            if (_chips is { } known && ReferenceEquals(known.Loadout, Loadout))
                return known.Chips;
            var chips = new List<(WiredChip, float)>();
            for (var socket = 0; socket < Loadout.Chips.Count; socket++)
            {
                if (Loadout.Chips[socket] is { } item && WiredChips.Find(item.Catalogue) is { } chip)
                    chips.Add((chip, WiredChips.Strength(Loadout.Chips, socket)));
            }
            _chips = (Loadout, chips);
            return chips;
        }
    }

    /// <summary>What the chips of a kind in the sockets add up to, with what linking adds.</summary>
    public float ChipValue(string chip)
    {
        var value = 0f;
        foreach (var (worn, strength) in Chips)
        {
            if (worn.Id == chip)
                value += strength;
        }
        return value;
    }

    /// <summary>A catalogue price as this hero pays it.</summary>
    public int Priced(int price)
    {
        var priced = WiredBalance.Priced(price, Difficulty);
        return WiredChips.Discount(this) is > 0 and var off ? (int)MathF.Round(priced * (1 - off)) : priced;
    }

    /// <summary>What selling an item brings.</summary>
    public int SellValue(WiredItem item) => item.Paid * WiredChips.SellPercent(this) / 100;

    /// <summary>The seconds until the ability in a slot can be used again; 0 for one that is ready, or an empty slot.</summary>
    public float AbilitySecondsLeft(int slot, float now) =>
        Loadout.Abilities[slot] is { } item && AbilityReadyAt.TryGetValue(item.Id, out var ready) ? MathF.Max(0, ready - now) : 0;

    /// <summary>The waves until the ability in a slot can be used again, for one that counts in waves; 0 for one that is ready.</summary>
    public int AbilityWavesLeft(int slot, int wave) =>
        Loadout.Abilities[slot] is { } item && AbilityReadyWave.TryGetValue(item.Id, out var ready) ? Math.Max(0, ready - wave) : 0;

    /// <summary>What the next Mk of an item costs, or null when it has none.</summary>
    public int? UpgradePrice(WiredItem item)
    {
        if (WiredCatalogue.FindModule(item.Catalogue) is { } module)
            return item.Mk < module.Prices.Count ? Priced(module.Prices[item.Mk]) : null;
        if (WiredCatalogue.FindWeapon(item.Catalogue) is { } weapon && item.Mk < WiredBalance.MostMk)
            return Priced((weapon.Price * WiredBalance.WeaponUpgradePercent[item.Mk - 1] + 50) / 100);
        return null;
    }

    /// <summary>What mending the pod costs, to the BMU above.</summary>
    public int PodRepairPrice(int hitPoints) =>
        (hitPoints * PodRepairPercent + WiredBalance.PodHitPointsPerBmu * 100 - 1) / (WiredBalance.PodHitPointsPerBmu * 100);

    /// <summary>The pod's hit points so many BMUs mend.</summary>
    public int PodRepairFor(int bmu) => bmu * WiredBalance.PodHitPointsPerBmu * 100 / PodRepairPercent;

    /// <summary>What the pod's repair costs in percent of its list price: it is part of the shop, Trader's discount and all.</summary>
    private int PodRepairPercent => Math.Max(1, (int)MathF.Round(WiredBalance.PricePercent(Difficulty) * (1 - WiredChips.Discount(this))));

    /// <summary>
    /// Whether the hero may change to a loadout: it must not weigh more than the most allowed, nor leave more in the
    /// inventory than fits. Neither holds up a change that makes things better.
    /// </summary>
    /// <returns>null if it may, otherwise why not.</returns>
    public string? WhyNotWear(GameData data, HeroLoadout loadout)
    {
        var (now, then) = (HeroBuild.Resolve(data, Loadout), HeroBuild.Resolve(data, loadout));
        if (then.LoadShare > WiredBalance.MostLoad && then.LoadShare > now.LoadShare)
            return "too heavy for this chassis";
        var (before, after) = (Inventory.Count(), Items.Count(i => !loadout.Holds(i.Id)));
        if (after > WiredBalance.InventoryLimit && after > before)
            return "no room in the inventory";
        return null;
    }

    /// <summary>Adds a new item to what it owns.</summary>
    public WiredItem Add(WiredKind kind, string catalogue, int paid, int mk = 1)
    {
        var item = new WiredItem(_nextItem++, kind, catalogue, mk, paid);
        Items.Add(item);
        if (kind == WiredKind.Chassis)
            Reached.Add(catalogue);
        return item;
    }

    /// <summary>
    /// Adds what a crate held. Found goods count as paid for at what the shop would charge for that Mk, so that what the
    /// hero has no use for can be sold; a chip has no price and brings nothing.
    /// </summary>
    public WiredItem AddLoot(string catalogue, int mk)
    {
        var item = Add(WiredCatalogue.KindOf(catalogue)!.Value, catalogue, Priced(WiredCatalogue.Price(catalogue)));
        while (item.Mk < mk && UpgradePrice(item) is { } price)
            Replace(item = item with { Mk = item.Mk + 1, Paid = item.Paid + price });
        return item;
    }

    /// <summary>Puts a new state of an owned item in the old one's place, in the loadout too.</summary>
    public void Replace(WiredItem item)
    {
        Items[Items.FindIndex(i => i.Id == item.Id)] = item;
        Loadout = Loadout.Replacing(item);
    }

    /// <summary>
    /// A new hero by the options, and in <paramref name="bank"/> what is left of its capital. The hangar at hero creation
    /// calls this as well, to see what the choices so far come to. Pure: bad entries in the options, an unknown id or
    /// what the capital does not cover, are passed over, so that any options make a hero.
    /// </summary>
    public static WiredState Create(GameData data, Race race, WiredOptions options, out int bank)
    {
        var chassis = WiredCatalogue.FindChassis(options.Chassis) ?? WiredCatalogue.Chassis[0];
        var first = WiredCatalogue.Chassis[0];
        var price = chassis == first ? 0 : WiredBalance.Priced(chassis.Price, options.Difficulty);
        bank = WiredBalance.StartCapital(options.Difficulty) + price;

        var state = new WiredState(race, options.Difficulty, first.Id);
        if (chassis != first)
        {
            // Paid out of capital raised for it, so it cost the hero nothing and brings nothing when sold.
            bank -= price;
            state.Loadout = state.Loadout.OnChassis(state.Add(WiredKind.Chassis, chassis.Id, 0));
        }

        var bolter = state.Add(WiredKind.Weapon, WiredBalance.StartWeapon, 0);
        var free = options.Items.ToList().FindIndex(i => i.Catalogue == WiredBalance.StartWeapon);
        var place = free >= 0 ? options.Items[free].Slot : state.FreeSlotFor(bolter);
        if (place is not null && state.Loadout.WhyNotFit(bolter, place) is null)
            state.Loadout = state.Loadout.With(place, bolter);

        foreach (var entry in options.Items.Where((_, at) => at != free))
        {
            var kind = WiredCatalogue.KindOf(entry.Catalogue);
            if (kind is not (WiredKind.Weapon or WiredKind.Module or WiredKind.Ability))
                continue;
            var cost = WiredBalance.Priced(WiredCatalogue.Price(entry.Catalogue), options.Difficulty);
            if (cost > bank || state.Inventory.Count() >= WiredBalance.InventoryLimit)
                continue;
            bank -= cost;
            var item = state.Add(kind.Value, entry.Catalogue, cost);
            if (entry.Slot is { } slot && state.Loadout.WhyNotFit(item, slot) is null)
            {
                var worn = state.Loadout.With(slot, item);
                if (state.WhyNotWear(data, worn) is null)
                    state.Loadout = worn;
            }
        }
        return state;
    }

    /// <summary>The first empty weapon slot an item fits, or null.</summary>
    private WiredSlot? FreeSlotFor(WiredItem item) => Enum.GetValues<Mounting>()
        .Select(WiredSlot.Weapon)
        .FirstOrDefault(slot => Loadout.At(slot) is null && Loadout.WhyNotFit(item, slot) is null);

    internal void MixInto(WiredHash hash)
    {
        hash.Mix((int)Race);
        hash.Mix(Hero ?? -1);
        hash.Mix(Pod ?? -1);
        hash.Mix(SpareBodies);
        hash.Mix(RespawnAt is { } back ? BitConverter.SingleToInt32Bits(back) : -1);
        Stats.MixInto(hash);
        hash.Mix(HangarOpen ? 1 : 0);
        hash.Mix(HangarOpens);
        hash.Mix(Rerolled ? 1 : 0);
        foreach (var (item, ready) in AbilityReadyAt)
        {
            hash.Mix(item);
            hash.Mix(BitConverter.SingleToInt32Bits(ready));
        }
        foreach (var (item, ready) in AbilityReadyWave)
        {
            hash.Mix(item);
            hash.Mix(ready);
        }
        foreach (var time in (ReadOnlySpan<float>)[DashReadyAt, DashUntil, DashWay.X, DashWay.Y, LastHitAt, LastShotAt, FiringSince, RevengeUntil, InvulnerableUntil, CoverCalledAt])
            hash.Mix(BitConverter.SingleToInt32Bits(time));
        hash.Mix(LastDitchWave);
        foreach (var escort in EscortIds)
            hash.Mix(escort);
        foreach (var item in Items)
        {
            hash.Mix(item.Id);
            hash.Mix(item.Catalogue);
            hash.Mix(item.Mk);
            hash.Mix(item.Paid);
        }
        // By place, so that it matters which slot or socket an item sits in; the empty ones count too.
        hash.Mix(Loadout.Chassis.Id);
        foreach (var places in new[] { Loadout.Weapons, Loadout.Modules, Loadout.Abilities, Loadout.Chips })
        {
            hash.Mix(places.Count);
            foreach (var item in places)
                hash.Mix(item?.Id ?? -1);
        }
        foreach (var offer in Offer)
            hash.Mix(offer);
        foreach (var chip in ChipChoice)
            hash.Mix(chip);
        foreach (var reached in Reached.Order(StringComparer.Ordinal))
            hash.Mix(reached);
    }
}
