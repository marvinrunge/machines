using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// Why something cannot be had or done in the pod, as English words with {0}, {1} ... and the values for them, so that the
/// screens can say it in the player's language. A value that is a catalogue id stands for that thing's name, an array of
/// ids for those names joined by "or".
/// </summary>
public sealed record BattleWhy(string Words, params object[] Args)
{
    public override string ToString() => string.Format(Words, Args.Select(a => a is string[] ids ? string.Join(" or ", ids) : a).ToArray());
}

/// <summary>What a battle or a Wired run brings the account: XP on the chassis it was fought on, free XP and credits.</summary>
public sealed record BattleNote(string Chassis, int Xp, int FreeXp, int Credits, bool Won = false, bool Wired = false)
{
    /// <summary>What a player's battle brings, by <see cref="BattleRewards.Of(BattleRun, Machines.Sim.World.PlayerId)"/>; null for a player that had no hero in it.</summary>
    public static BattleNote? Of(BattleRun run, Machines.Sim.World.PlayerId player)
    {
        if (!run.Tally.TryGetValue(player, out var tally))
            return null;
        var reward = BattleRewards.Of(run, player);
        return new BattleNote(tally.Chassis, reward.Xp, reward.FreeXp, reward.Credits, reward.Won);
    }

    /// <summary>What a Wired run that reached <paramref name="waves"/> brings the chassis it ended on: Wired gives less.</summary>
    public static BattleNote OfWired(string chassis, int waves)
    {
        var (xp, credits) = BattleRewards.OfWired(waves);
        return new BattleNote(chassis, xp, 0, credits, Wired: true);
    }
}

/// <summary>The hero as the pod keeps it between battles, with the battle last chosen: what was chosen, not what it came to.</summary>
public sealed record BattleHeroSheet
{
    public string Name { get; init; } = "";
    public Race Colour { get; init; } = Race.Red;
    public string Chassis { get; init; } = WiredBalance.StartChassis;
    public IReadOnlyList<BattleItem> Items { get; init; } = [];

    /// <summary>The map last chosen, by its scenario.</summary>
    public string Map { get; init; } = "";

    public int Lives { get; init; } = 1;
    public BattleWin Win { get; init; } = BattleWin.Both;

    /// <summary>The hero as a battle takes it, playing <paramref name="race"/>.</summary>
    public BattleHero Hero(Race race) => new(race, BattleDraft.ShortName(Name), Chassis, Items.ToList());
}

/// <summary>
/// The battle part of <c>wired.json</c> (<see cref="WiredProfile.Battle"/>): XP per chassis, free XP, credits, what was
/// researched and bought, and the last hero. A typed section, so that writing Wired's runs keeps it.
/// </summary>
public sealed class BattleProfile
{
    /// <summary>XP earned on each chassis, by its id.</summary>
    public SortedDictionary<string, int> Xp { get; set; } = new(StringComparer.Ordinal);

    public int FreeXp { get; set; }

    public long Credits { get; set; }

    /// <summary>The chassis researched, which may then be bought.</summary>
    public SortedSet<string> Researched { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What was bought, chassis and items, each at the highest Mk owned (a chassis at 1).</summary>
    public SortedDictionary<string, int> Owned { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Chassis that can be researched since the pod was last up, for the menu's tag and the tree's gold plates.</summary>
    public SortedSet<string> Fresh { get; set; } = new(StringComparer.Ordinal);

    public BattleHeroSheet? LastHero { get; set; }

    public int Battles { get; set; }

    public int Wins { get; set; }

    /// <summary>A file written by hand, or by none at all, gets what every account starts with and loses what no longer is.</summary>
    public BattleProfile Mended()
    {
        Xp = new SortedDictionary<string, int>((Xp ?? []).Where(x => x.Key is not null && x.Value > 0).ToDictionary(x => x.Key, x => x.Value), StringComparer.Ordinal);
        FreeXp = Math.Max(0, FreeXp);
        Credits = Math.Max(0, Credits);
        Researched = new SortedSet<string>((Researched ?? []).Where(id => WiredCatalogue.FindChassis(id ?? "") is not null), StringComparer.Ordinal);
        Owned = new SortedDictionary<string, int>((Owned ?? [])
            .Where(o => o.Key is not null && WiredCatalogue.KindOf(o.Key) is WiredKind.Chassis or WiredKind.Weapon or WiredKind.Module or WiredKind.Ability)
            .ToDictionary(o => o.Key, o => Math.Clamp(o.Value, 1, BattleRules.MostMk(o.Key))), StringComparer.Ordinal);
        Fresh = new SortedSet<string>((Fresh ?? []).OfType<string>(), StringComparer.Ordinal);
        Battles = Math.Max(0, Battles);
        Wins = Math.Clamp(Wins, 0, Battles);
        // Every account starts on the first grunt with its bolter, and owns every other start of the tree (the crab) too.
        foreach (var root in WiredCatalogue.Roots)
        {
            Researched.Add(root.Id);
            Owned[root.Id] = 1;
        }
        Owned[WiredBalance.StartWeapon] = Math.Max(1, Owned.GetValueOrDefault(WiredBalance.StartWeapon));
        if (LastHero is { } hero)
        {
            var chassis = hero.Chassis ?? WiredBalance.StartChassis;
            LastHero = hero with
            {
                Name = BattleDraft.ShortName(hero.Name ?? ""),
                Colour = Enum.IsDefined(hero.Colour) ? hero.Colour : Race.Red,
                Chassis = chassis,
                // A battery's count from 1 to what its slot carries; an entry from before batteries has one.
                Items = (hero.Items ?? []).Where(i => i?.Catalogue is not null)
                    .Select(i => i with { Count = WiredProfile.Copies(chassis, i.Slot, i.Catalogue, i.Count) }).ToList(),
                Map = hero.Map ?? "",
                Lives = Math.Clamp(hero.Lives, 1, 5),
                Win = Enum.IsDefined(hero.Win) ? hero.Win : BattleWin.Both,
            };
        }
        return this;
    }

    public BattleProfile Copy() => new()
    {
        Xp = new(Xp, StringComparer.Ordinal),
        FreeXp = FreeXp,
        Credits = Credits,
        Researched = new(Researched, StringComparer.Ordinal),
        Owned = new(Owned, StringComparer.Ordinal),
        Fresh = new(Fresh, StringComparer.Ordinal),
        LastHero = LastHero,
        Battles = Battles,
        Wins = Wins,
    };
}

/// <summary>
/// Battle's prices and the way through the tree: what research costs in XP and who pays it, and what the pod sells for
/// credits. Pure, so that the pod, the tests and, in phase 3, a server agree.
/// </summary>
public static class BattleRules
{
    /// <summary>Research costs this many times the Wired price, in XP.</summary>
    public const int ResearchFactor = 3;

    /// <summary>What a chassis costs to research, in XP.</summary>
    public static int ResearchCost(string chassis) => ResearchFactor * (WiredCatalogue.FindChassis(chassis)?.Price ?? 0);

    /// <summary>What a chassis costs to buy, in credits.</summary>
    public static int ChassisPrice(string chassis) => BattleBalance.PodPriceFactor * (WiredCatalogue.FindChassis(chassis)?.Price ?? 0);

    /// <summary>The highest Mk an item has: weapons three, modules as many as their prices, abilities and chassis one.</summary>
    public static int MostMk(string item) =>
        WiredCatalogue.FindWeapon(item) is not null ? WiredBalance.MostMk
        : WiredCatalogue.FindModule(item) is { } module ? module.Prices.Count
        : 1;

    /// <summary>
    /// What an item costs at a Mk, in credits: Mk I ten times the Wired price, each Mk above ten times Wired's price for
    /// that step up; a chassis its <see cref="ChassisPrice"/>. Null for a Mk the item does not have.
    /// </summary>
    public static int? Price(string item, int mk = 1)
    {
        if (mk < 1 || mk > MostMk(item))
            return null;
        if (WiredCatalogue.FindChassis(item) is not null)
            return ChassisPrice(item);
        if (WiredCatalogue.FindModule(item) is { } module)
            return BattleBalance.PodPriceFactor * module.Prices[mk - 1];
        if (WiredCatalogue.FindWeapon(item) is { } weapon)
            return BattleBalance.PodPriceFactor * (mk == 1 ? weapon.Price : (weapon.Price * WiredBalance.WeaponUpgradePercent[mk - 2] + 50) / 100);
        return WiredCatalogue.FindAbility(item) is { } ability ? BattleBalance.PodPriceFactor * ability.Price : null;
    }

    /// <summary>The lowest tier of chassis an item may go on at a Mk: Mk II from tier IV, Mk III from VI, abilities from III.</summary>
    public static int TierFor(string item, int mk = 1) =>
        WiredCatalogue.FindAbility(item) is not null ? BattleCatalogue.AbilitiesFrom
        : mk >= 3 ? 6 : mk == 2 ? 4 : 1;

    /// <summary>
    /// Who pays a chassis' research: the XP of its parents (<see cref="WiredChassis.Needs"/>), the richest first, then free
    /// XP; what each gives, and the shortfall that is left.
    /// </summary>
    public static (IReadOnlyList<(string Chassis, int Xp)> Parents, int FreeXp, int Short) Payment(IBattleAccount account, string chassis)
    {
        var left = ResearchCost(chassis);
        var parents = new List<(string, int)>();
        var needs = WiredCatalogue.FindChassis(chassis)?.Needs ?? [];
        foreach (var parent in needs.OrderByDescending(account.Xp).ThenBy(n => n, StringComparer.Ordinal))
        {
            var take = Math.Min(left, account.Xp(parent));
            if (take <= 0)
                continue;
            parents.Add((parent, take));
            left -= take;
        }
        var free = Math.Min(left, account.FreeXp);
        return (parents, free, left - free);
    }

    /// <summary>The XP there is towards a chassis' research: its parents' and the free XP.</summary>
    public static int XpTowards(IBattleAccount account, string chassis) =>
        (WiredCatalogue.FindChassis(chassis)?.Needs ?? []).Sum(account.Xp) + account.FreeXp;

    /// <returns>null if the chassis can be researched now, otherwise why not.</returns>
    public static BattleWhy? WhyNotResearch(IBattleAccount account, string chassis)
    {
        if (WiredCatalogue.FindChassis(chassis) is not { } entry)
            return new BattleWhy("no such chassis");
        if (account.Researched(chassis))
            return new BattleWhy("that is researched already");
        if (entry.Needs.Count > 0 && !entry.Needs.Any(account.Researched))
            return new BattleWhy("Research {0} first", (object)entry.Needs.ToArray());
        if (Payment(account, chassis).Short > 0)
            return new BattleWhy("Needs {0} XP on {1}", ResearchCost(chassis), entry.Needs.ToArray());
        return null;
    }

    /// <returns>null if the item can be bought at the Mk now, otherwise why not.</returns>
    public static BattleWhy? WhyNotBuy(IBattleAccount account, string item, int mk)
    {
        if (WiredCatalogue.KindOf(item) is not (WiredKind.Chassis or WiredKind.Weapon or WiredKind.Module or WiredKind.Ability))
            return new BattleWhy("no such item");
        if (BattleCatalogue.NotOffered.Contains(item))
            return new BattleWhy("that is not offered in a battle");
        if (Price(item, mk) is not { } price)
            return new BattleWhy("that has no higher Mk");
        if (account.Mk(item) >= mk)
            return new BattleWhy("you own that already");
        if (WiredCatalogue.FindChassis(item) is not null && !account.Researched(item))
            return new BattleWhy("Research {0} first", (object)new[] { item });
        if (mk > 1 && account.Mk(item) < mk - 1)
            return new BattleWhy("Buy Mk {0} first", mk - 1);
        return account.Credits < price ? new BattleWhy("Needs {0} Cr", price) : null;
    }
}

/// <summary>
/// The player's account for battles: XP on each chassis, free XP, credits, what was researched and bought. Phase 1 keeps it
/// in the local profile (<see cref="LocalBattleAccount"/>) and trusts it, as battles are fought in friends' rooms; phase 3
/// moves it behind a server, which will check research and purchases and write what battles bring itself.
/// </summary>
public interface IBattleAccount
{
    /// <summary>XP earned on a chassis.</summary>
    int Xp(string chassis);

    /// <summary>XP that pays any research.</summary>
    int FreeXp { get; }

    long Credits { get; }

    bool Researched(string chassis);

    /// <summary>The highest Mk of an item (or a chassis, at 1) that is owned; 0 for none.</summary>
    int Mk(string item);

    bool Owned(string item, int mk = 1) => Mk(item) >= mk;

    /// <summary>Researches a chassis, paid by its parents' XP and free XP (<see cref="BattleRules.Payment"/>).</summary>
    /// <returns>null if done, otherwise why not.</returns>
    BattleWhy? Research(string chassis);

    /// <summary>Buys an item at a Mk, or a chassis, for credits. Owned, it may go on any owned chassis that may take it, any number of times.</summary>
    /// <returns>null if done, otherwise why not.</returns>
    BattleWhy? Buy(string item, int mk = 1);

    /// <summary>What a battle or a Wired run brought. Returns the chassis that can now be researched and could not before.</summary>
    IReadOnlyList<string> Note(BattleNote note);

    BattleHeroSheet? LastHero { get; set; }

    /// <summary>Chassis that came within reach since the pod was last up.</summary>
    IReadOnlySet<string> Fresh { get; }

    /// <summary>The pod is up: what was new is handed over, and from now on is not.</summary>
    IReadOnlySet<string> TakeFresh();

    int Battles { get; }

    int Wins { get; }

    /// <summary>Keeps what changed. Returns whether it is kept; research and purchases keep themselves.</summary>
    bool Save();
}

/// <summary>
/// The account in the local profile, <c>wired.json</c>'s battle section. Research and purchases are written at once, and taken
/// back when the file cannot be written, so that the pod never shows what is not kept. Phase 1 only: phase 3 puts a server's
/// account behind <see cref="IBattleAccount"/> in its place.
/// </summary>
public sealed class LocalBattleAccount(WiredProfile profile, string? path = null, Action<string>? warn = null) : IBattleAccount
{
    private BattleProfile Book => profile.Battle;

    public WiredProfile Profile => profile;

    public int Xp(string chassis) => Book.Xp.GetValueOrDefault(chassis);

    public int FreeXp => Book.FreeXp;

    public long Credits => Book.Credits;

    public bool Researched(string chassis) => Book.Researched.Contains(chassis);

    public int Mk(string item) => Book.Owned.GetValueOrDefault(item);

    public bool Owned(string item, int mk = 1) => Mk(item) >= mk;

    public int Battles => Book.Battles;

    public int Wins => Book.Wins;

    public BattleHeroSheet? LastHero
    {
        get => Book.LastHero;
        set => Book.LastHero = value;
    }

    public IReadOnlySet<string> Fresh => Book.Fresh;

    public IReadOnlySet<string> TakeFresh()
    {
        var fresh = new SortedSet<string>(Book.Fresh, StringComparer.Ordinal);
        Book.Fresh.Clear();
        return fresh;
    }

    public BattleWhy? Research(string chassis)
    {
        if ((Unwritable ?? BattleRules.WhyNotResearch(this, chassis)) is { } why)
            return why;
        return Kept(book =>
        {
            var (parents, free, _) = BattleRules.Payment(this, chassis);
            foreach (var (parent, xp) in parents)
                book.Xp[parent] = Xp(parent) - xp;
            book.FreeXp -= free;
            foreach (var spent in book.Xp.Where(x => x.Value <= 0).Select(x => x.Key).ToList())
                book.Xp.Remove(spent);
            book.Researched.Add(chassis);
            book.Fresh.Remove(chassis);
        });
    }

    public BattleWhy? Buy(string item, int mk = 1)
    {
        if ((Unwritable ?? BattleRules.WhyNotBuy(this, item, mk)) is { } why)
            return why;
        return Kept(book =>
        {
            book.Credits -= BattleRules.Price(item, mk)!.Value;
            book.Owned[item] = mk;
        });
    }

    public IReadOnlyList<string> Note(BattleNote note)
    {
        var before = Within();
        var book = Book;
        if (WiredCatalogue.FindChassis(note.Chassis) is not null && note.Xp > 0)
            book.Xp[note.Chassis] = Xp(note.Chassis) + note.Xp;
        book.FreeXp += Math.Max(0, note.FreeXp);
        book.Credits += Math.Max(0, note.Credits);
        if (!note.Wired)
        {
            book.Battles++;
            if (note.Won)
                book.Wins++;
        }
        var opened = Within().Except(before).ToList();
        foreach (var chassis in opened)
            book.Fresh.Add(chassis);
        return opened;
    }

    /// <summary>The chassis that can be researched now.</summary>
    private HashSet<string> Within() => WiredCatalogue.Chassis.Select(c => c.Id).Where(id => BattleRules.WhyNotResearch(this, id) is null).ToHashSet();

    public bool Save()
    {
        if (profile.Held)
            return false;
        if (path is null)
            return true;
        try
        {
            profile.Save(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"battle account not saved: {exception.Message}");
            return false;
        }
    }

    /// <summary>Why nothing can be kept just now: the file was there but could not be read, so what this holds is not all of it.</summary>
    private BattleWhy? Unwritable => profile.Held ? new BattleWhy("the profile cannot be written just now") : null;

    /// <summary>A change that is written at once, or taken back.</summary>
    private BattleWhy? Kept(Action<BattleProfile> change)
    {
        if (Unwritable is { } held)
            return held;
        var before = Book.Copy();
        change(Book);
        if (Save())
            return null;
        profile.Battle = before;
        return new BattleWhy("the profile cannot be written just now");
    }
}

/// <summary>
/// The battles that have ended and are not in the file yet, and Wired runs' XP likewise: as with <see cref="WiredLedger"/>
/// something else may hold the file just then, so each waits here until it is in, read afresh each time.
/// </summary>
public sealed class BattleLedger(string path, Action<string>? warn = null)
{
    private readonly List<BattleNote> _waiting = [];

    public bool Waiting => _waiting.Count > 0;

    /// <summary>A battle has ended. Returns the chassis it and any before it brought within reach, or null while the file cannot be written.</summary>
    public IReadOnlyList<string>? Tell(BattleNote note)
    {
        _waiting.Add(note);
        return Write();
    }

    /// <summary>All that waits goes in together or not at all.</summary>
    public IReadOnlyList<string>? Write()
    {
        if (!Waiting)
            return [];
        var profile = WiredProfile.Load(path, warn);
        if (profile.Held)
            return null;
        var account = new LocalBattleAccount(profile);
        var opened = _waiting.SelectMany(account.Note).Distinct().ToList();
        try
        {
            profile.Save(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"battle account not saved: {exception.Message}");
            return null;
        }
        _waiting.Clear();
        return opened;
    }
}

#if GODOT
/// <summary>The player's battle account in the game: the local one for now (see <see cref="IBattleAccount"/>).</summary>
public static class BattleAccounts
{
    private static BattleLedger? _ledger;

    /// <summary>Battles and Wired runs on their way into the file.</summary>
    public static BattleLedger Ledger => _ledger ??= new BattleLedger(WiredProfile.FilePath, Godot.GD.PushWarning);

    /// <summary>The account as it stands, with whatever had to wait written first.</summary>
    public static LocalBattleAccount Load()
    {
        if (Ledger.Waiting)
            Ledger.Write();
        return new LocalBattleAccount(WiredProfile.Load(), WiredProfile.FilePath, Godot.GD.PushWarning);
    }
}
#endif
