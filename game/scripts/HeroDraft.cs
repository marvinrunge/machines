using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// A hero being made in the hangar, before any run: the choices so far, as the <see cref="WiredOptions"/> they will become.
/// Each change makes the hero anew with <see cref="WiredState.Create"/>, so what the screen shows is what the run will
/// start with and nothing here works out prices or fits on its own. Nothing is lost by changing one's mind: what comes
/// off stays owned, and only giving a thing back returns its price. A weapon put onto a slot that carries the same weapon
/// joins the battery there, each copy bought at its full price, unless the put replaces. Free of Godot, so the tests can
/// compile it. Wired is the remake's own addition.
/// </summary>
public sealed class HeroDraft
{
    /// <summary>A draft has no race yet: this one stands in, the colours are <see cref="Colour"/>'s.</summary>
    private const Race Maker = Race.Red;

    private readonly GameData _data;
    private readonly WiredProfile _profile;

    // What was bought, one copy at a time and in the order bought, the free weapon first, each with the slot it goes into:
    // a battery's copies stand next to each other, so that they come to one entry of the options (Entries). In the same
    // order, what each came to in the state made from it.
    private List<WiredStartItem> _goods = [];
    private List<WiredItem> _made = [];
    private WiredState _state = null!;
    private int _bank;

    /// <summary>Takes up the last hero as far as what it chose is still open, or starts afresh.</summary>
    public HeroDraft(GameData data, WiredProfile profile)
    {
        (_data, _profile) = (data, profile);
        Arena = WiredProfile.Arenas[0].Scenario;
        var items = new List<WiredStartItem>();
        if (profile.LastHero is { } last)
        {
            Name = last.Name;
            Colour = last.Colour;
            Chassis = profile.CanStartOn(last.Chassis) ? last.Chassis : WiredBalance.StartChassis;
            Difficulty = profile.CanPlay(last.Difficulty) ? last.Difficulty : WiredDifficulty.Normal;
            Arena = profile.CanPlay(last.Arena) ? last.Arena : Arena;
            items = last.Items.ToList();
        }
        // The free weapon leads the list and says where it goes (see WiredOptions.Items).
        var free = items.FindIndex(i => i.Catalogue == WiredBalance.StartWeapon);
        var leader = free >= 0 ? items[free] : new WiredStartItem(WiredBalance.StartWeapon, FirstWeaponSlot());
        if (free >= 0)
            items.RemoveAt(free);
        items.Insert(0, leader);
        // Each entry as the copies it comes to: no more than its slot carries, as the run makes it.
        var chassis = WiredCatalogue.FindChassis(Chassis) ?? WiredCatalogue.Chassis[0];
        _goods = items.SelectMany(entry => Enumerable.Repeat(entry with { Count = 1 }, WiredCatalogue.Copies(chassis, entry.Slot, entry.Catalogue, entry.Count))).ToList();
        Settle();
    }

    public string Name { get; set; } = "";

    public Race Colour { get; set; } = Race.Red;

    public WiredDifficulty Difficulty { get; private set; } = WiredDifficulty.Normal;

    public string Arena { get; set; }

    public string Chassis { get; private set; } = WiredBalance.StartChassis;

    /// <summary>What the hero comes to with the choices so far.</summary>
    public WiredState State => _state;

    /// <summary>What is left of the start capital.</summary>
    public int Bank => _bank;

    /// <summary>All there was to spend.</summary>
    public int Capital => WiredBalance.StartCapital(Difficulty);

    /// <summary>The run these choices make.</summary>
    public WiredOptions Options => new()
    {
        Difficulty = Difficulty,
        HeroName = Name,
        Arena = Arena,
        Chassis = Chassis,
        Items = Entries(),
        ChipPool = _profile.ChipPool,
    };

    /// <summary>What the profile keeps of this hero.</summary>
    public WiredHeroSheet Sheet => new()
    {
        Name = Name,
        Colour = Colour,
        Chassis = Chassis,
        Difficulty = Difficulty,
        Arena = Arena,
        Items = Entries(),
    };

    /// <summary>What a catalogue entry costs at this difficulty.</summary>
    public int Price(string catalogue) => WiredBalance.Priced(WiredCatalogue.Price(catalogue), Difficulty);

    /// <summary>What can be bought at the start, of one kind. The waves bring the rest to the shop.</summary>
    public IEnumerable<string> ForSale(WiredKind kind) => WiredCatalogue.ForSale(1).Where(id => WiredCatalogue.KindOf(id) == kind);

    /// <returns>
    /// null if an item bought now could go into the slot, or stay in the inventory without one; otherwise why not. Onto a
    /// slot that carries the same weapon it would join the battery there, unless it is to <paramref name="replace"/> it.
    /// </returns>
    public string? WhyNotBuy(string catalogue, WiredSlot? slot, bool replace = false)
    {
        var fit = slot is null ? null : WhyNotHold(Probe(catalogue), slot, replace);
        return fit ?? (Price(catalogue) > _bank ? "not enough BMUs" : null);
    }

    /// <returns>
    /// null if the slot can hold the item, otherwise why not. Where the item <see cref="Joins"/> the battery on the slot it is
    /// asked as a further copy (the slot's size, a weapon that forms none); with <paramref name="replace"/> what sits there
    /// comes off first.
    /// </returns>
    public string? WhyNotHold(WiredItem item, WiredSlot slot, bool replace = false)
    {
        var loadout = _state.Loadout;
        if (!replace && Joins(item, slot))
            return loadout.WhyNotAdd(item, slot) ?? _state.WhyNotWear(_data, loadout.Adding(slot, item));
        return loadout.WhyNotFit(item, slot) ?? _state.WhyNotWear(_data, loadout.With(slot, item));
    }

    /// <summary>
    /// Whether an item put into a slot joins the battery there rather than replacing it: a weapon onto a weapon slot that
    /// carries the same weapon, the item itself not counted.
    /// </summary>
    public bool Joins(WiredItem item, WiredSlot slot) =>
        item.Kind == WiredKind.Weapon && slot.Kind == WiredSlotKind.Weapon && _state.Loadout.Without(item.Id).At(slot)?.Catalogue == item.Catalogue;

    /// <summary>
    /// Buys something and puts it into a slot, or with none into the inventory. Onto a slot that carries the same weapon it
    /// joins the battery as its last copy, paid for as any; otherwise, or with <paramref name="replace"/>, what sat there
    /// comes off, a whole battery.
    /// </summary>
    /// <returns>null if done, otherwise why not.</returns>
    public string? Buy(string catalogue, WiredSlot? slot, bool replace = false)
    {
        if (WhyNotBuy(catalogue, slot, replace) is { } why)
            return why;
        var good = new WiredStartItem(catalogue, slot);
        if (slot is not null && !replace && Joins(Probe(catalogue), slot))
            _goods.Insert(OnSlot(slot)[^1] + 1, good);
        else
        {
            Empty(slot);
            _goods.Add(good);
        }
        Settle();
        return null;
    }

    /// <summary>
    /// Puts something owned into a slot, from the inventory or another slot. Onto a slot that carries the same weapon it
    /// joins the battery; otherwise, or with <paramref name="replace"/>, what sat there comes off, a whole battery. Of a
    /// battery elsewhere one copy moves.
    /// </summary>
    /// <returns>null if done, otherwise why not.</returns>
    public string? Put(int item, WiredSlot slot, bool replace = false)
    {
        var at = IndexOf(item);
        if (at < 0)
            return "you do not own that";
        if (WhyNotHold(_made[at], slot, replace) is { } why)
            return why;
        if (!replace && Joins(_made[at], slot))
            Join(at, slot);
        else
        {
            // The copies of one weapon are all alike: the last of a battery leaves it, so that the others stay together.
            var moving = Run(at)[^1];
            Empty(slot);
            _goods[moving] = _goods[moving] with { Slot = slot };
        }
        Settle();
        return null;
    }

    /// <summary>Takes something off the hero, of a battery one copy; it stays owned.</summary>
    public void TakeOff(int item)
    {
        if (IndexOf(item) is >= 0 and var at && Run(at)[^1] is var last)
            _goods[last] = _goods[last] with { Slot = null };
        Settle();
    }

    /// <summary>Gives something back for all that was paid. The free weapon was not paid for and stays.</summary>
    /// <returns>Whether it went back.</returns>
    public bool GiveBack(int item)
    {
        var at = IndexOf(item);
        if (at <= 0)
            return false;
        _goods.RemoveAt(at);
        Settle();
        return true;
    }

    /// <summary>
    /// Starts on another chassis: what is worn moves as it would in the hangar (<see cref="HeroLoadout.OnChassis"/>), a
    /// battery cut to what its new slot carries, and what does not fit stays owned.
    /// </summary>
    public void Wear(string chassis)
    {
        var moved = _state.Loadout.OnChassis(new WiredItem(-1, WiredKind.Chassis, chassis));
        _goods = _goods.Select((good, at) => good with { Slot = moved.SlotOf(_made[at].Id) }).ToList();
        Chassis = chassis;
        Settle();
    }

    /// <summary>Another difficulty means another capital and other prices: what can no longer be paid goes back, the last bought first.</summary>
    /// <returns>What went back, by catalogue id and a copy at a time, for the screen to say so.</returns>
    public IReadOnlyList<string> Choose(WiredDifficulty difficulty)
    {
        var gone = _goods.Select(i => i.Catalogue).ToList();
        Difficulty = difficulty;
        Settle();
        foreach (var good in _goods)
            gone.Remove(good.Catalogue);
        return gone;
    }

    private int IndexOf(int item) => _made.FindIndex(i => i.Id == item);

    private static WiredItem Probe(string catalogue) => new(-1, WiredCatalogue.KindOf(catalogue)!.Value, catalogue);

    /// <summary>The goods in a slot, in order: a battery's copies, which stand next to each other.</summary>
    private List<int> OnSlot(WiredSlot slot) => Enumerable.Range(0, _goods.Count).Where(at => _goods[at].Slot == slot).ToList();

    /// <summary>The goods that stand with one in its slot, itself included; just itself in the inventory.</summary>
    private List<int> Run(int at) => _goods[at].Slot is { } slot ? OnSlot(slot) : [at];

    /// <summary>
    /// A good joins the battery on a slot: of a battery elsewhere its last copy, all of a kind. The copies come together
    /// where the earlier of the two stood, so that the free weapon stays first.
    /// </summary>
    private void Join(int at, WiredSlot slot)
    {
        var moving = Run(at)[^1];
        if (_goods[moving].Slot == slot)
            return;
        var battery = OnSlot(slot);
        List<int> joined = moving < battery[0] ? [moving, .. battery] : [.. battery, moving];
        var rest = Enumerable.Range(0, _goods.Count).Where(i => !joined.Contains(i)).ToList();
        var goods = rest.Select(i => _goods[i]).ToList();
        goods.InsertRange(rest.Count(i => i < joined.Min()), joined.Select(i => _goods[i] with { Slot = slot }));
        _goods = goods;
    }

    /// <summary>What sits in a slot comes off, a battery's every copy.</summary>
    private void Empty(WiredSlot? slot)
    {
        if (slot is null)
            return;
        foreach (var at in OnSlot(slot))
            _goods[at] = _goods[at] with { Slot = null };
    }

    /// <summary>The goods as the options take them: a battery's copies, next to each other on one weapon slot, as one entry with their count.</summary>
    private List<WiredStartItem> Entries()
    {
        var entries = new List<WiredStartItem>();
        foreach (var good in _goods)
        {
            if (good.Slot is { Kind: WiredSlotKind.Weapon } && entries.Count > 0 && entries[^1] is var last && last.Slot == good.Slot && last.Catalogue == good.Catalogue)
                entries[^1] = last with { Count = last.Count + 1 };
            else
                entries.Add(good with { Count = 1 });
        }
        return entries;
    }

    /// <summary>The weapon slot a new hero's free weapon goes to.</summary>
    private WiredSlot? FirstWeaponSlot() => WiredCatalogue.FindChassis(Chassis)?.Slots.Where(s => s.Fixed is null)
        .Select(s => WiredSlot.Weapon(s.Mounting)).FirstOrDefault();

    /// <summary>
    /// Makes the hero anew. Whatever the capital no longer covers or the hero no longer wears, the list says so afterwards: a
    /// copy left in the inventory stands there on its own.
    /// </summary>
    private void Settle()
    {
        _state = WiredState.Create(_data, Maker, Options, out _bank);
        var made = new Queue<WiredItem>(_state.Items.Where(i => i.Kind != WiredKind.Chassis));
        var (goods, items) = (new List<WiredStartItem>(), new List<WiredItem>());
        foreach (var good in _goods)
        {
            if (!made.TryPeek(out var item) || item.Catalogue != good.Catalogue)
                continue;
            made.Dequeue();
            goods.Add(good with { Slot = _state.Loadout.SlotOf(item.Id) });
            items.Add(item);
        }
        (_goods, _made) = (goods, items);
    }
}
