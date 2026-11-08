using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// A hero being made in the hangar, before any run: the choices so far, as the <see cref="WiredOptions"/> they will become.
/// Each change makes the hero anew with <see cref="WiredState.Create"/>, so what the screen shows is what the run will
/// start with and nothing here works out prices or fits on its own. Nothing is lost by changing one's mind: what comes
/// off stays owned, and only giving a thing back returns its price. Free of Godot, so the tests can compile it. Wired is
/// the remake's own addition.
/// </summary>
public sealed class HeroDraft
{
    /// <summary>A draft has no race yet: this one stands in, the colours are <see cref="Colour"/>'s.</summary>
    private const Race Maker = Race.Red;

    private readonly GameData _data;
    private readonly WiredProfile _profile;

    // What was bought, the free weapon first, and in the same order what each came to in the state made from it.
    private List<WiredStartItem> _items = [];
    private List<WiredItem> _made = [];
    private WiredState _state = null!;
    private int _bank;

    /// <summary>Takes up the last hero as far as what it chose is still open, or starts afresh.</summary>
    public HeroDraft(GameData data, WiredProfile profile)
    {
        (_data, _profile) = (data, profile);
        Arena = WiredProfile.Arenas[0].Scenario;
        if (profile.LastHero is { } last)
        {
            Name = last.Name;
            Colour = last.Colour;
            Chassis = profile.CanStartOn(last.Chassis) ? last.Chassis : WiredBalance.StartChassis;
            Difficulty = profile.CanPlay(last.Difficulty) ? last.Difficulty : WiredDifficulty.Normal;
            Arena = profile.CanPlay(last.Arena) ? last.Arena : Arena;
            _items = last.Items.ToList();
        }
        // The free weapon leads the list and says where it goes (see WiredOptions.Items).
        var free = _items.FindIndex(i => i.Catalogue == WiredBalance.StartWeapon);
        var leader = free >= 0 ? _items[free] : new WiredStartItem(WiredBalance.StartWeapon, FirstWeaponSlot());
        if (free >= 0)
            _items.RemoveAt(free);
        _items.Insert(0, leader);
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
        Items = _items.ToList(),
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
        Items = _items.ToList(),
    };

    /// <summary>What a catalogue entry costs at this difficulty.</summary>
    public int Price(string catalogue) => WiredBalance.Priced(WiredCatalogue.Price(catalogue), Difficulty);

    /// <summary>What can be bought at the start, of one kind. The waves bring the rest to the shop.</summary>
    public IEnumerable<string> ForSale(WiredKind kind) => WiredCatalogue.ForSale(1).Where(id => WiredCatalogue.KindOf(id) == kind);

    /// <returns>null if an item bought now could go into the slot, or stay in the inventory without one; otherwise why not.</returns>
    public string? WhyNotBuy(string catalogue, WiredSlot? slot)
    {
        var fit = slot is null ? null : WhyNotHold(new WiredItem(-1, WiredCatalogue.KindOf(catalogue)!.Value, catalogue), slot);
        return fit ?? (Price(catalogue) > _bank ? "not enough BMUs" : null);
    }

    /// <returns>null if the slot can hold the item, otherwise why not.</returns>
    public string? WhyNotHold(WiredItem item, WiredSlot slot) =>
        _state.Loadout.WhyNotFit(item, slot) ?? _state.WhyNotWear(_data, _state.Loadout.With(slot, item));

    /// <summary>Buys something and puts it into a slot, or with none into the inventory. What sat there comes off.</summary>
    /// <returns>null if done, otherwise why not.</returns>
    public string? Buy(string catalogue, WiredSlot? slot)
    {
        if (WhyNotBuy(catalogue, slot) is { } why)
            return why;
        Empty(slot);
        _items.Add(new WiredStartItem(catalogue, slot));
        Settle();
        return null;
    }

    /// <summary>Puts something owned into a slot, from the inventory or another slot. What sat there comes off.</summary>
    /// <returns>null if done, otherwise why not.</returns>
    public string? Put(int item, WiredSlot slot)
    {
        var at = IndexOf(item);
        if (at < 0)
            return "you do not own that";
        if (WhyNotHold(_made[at], slot) is { } why)
            return why;
        Empty(slot);
        _items[at] = _items[at] with { Slot = slot };
        Settle();
        return null;
    }

    /// <summary>Takes something off the hero; it stays owned.</summary>
    public void TakeOff(int item)
    {
        if (IndexOf(item) is >= 0 and var at)
            _items[at] = _items[at] with { Slot = null };
        Settle();
    }

    /// <summary>Gives something back for all that was paid. The free weapon was not paid for and stays.</summary>
    /// <returns>Whether it went back.</returns>
    public bool GiveBack(int item)
    {
        var at = IndexOf(item);
        if (at <= 0)
            return false;
        _items.RemoveAt(at);
        Settle();
        return true;
    }

    /// <summary>Starts on another chassis: what is worn moves as it would in the hangar (<see cref="HeroLoadout.OnChassis"/>), and what does not fit stays owned.</summary>
    public void Wear(string chassis)
    {
        var moved = _state.Loadout.OnChassis(new WiredItem(-1, WiredKind.Chassis, chassis));
        _items = _items.Select((entry, at) => entry with { Slot = moved.SlotOf(_made[at].Id) }).ToList();
        Chassis = chassis;
        Settle();
    }

    /// <summary>Another difficulty means another capital and other prices: what can no longer be paid goes back, the last bought first.</summary>
    /// <returns>What went back, by catalogue id, for the screen to say so.</returns>
    public IReadOnlyList<string> Choose(WiredDifficulty difficulty)
    {
        var gone = _items.Select(i => i.Catalogue).ToList();
        Difficulty = difficulty;
        Settle();
        foreach (var item in _items)
            gone.Remove(item.Catalogue);
        return gone;
    }

    private int IndexOf(int item) => _made.FindIndex(i => i.Id == item);

    /// <summary>What sits in a slot comes off.</summary>
    private void Empty(WiredSlot? slot)
    {
        if (slot is not null && _state.Loadout.At(slot) is { } sitting && IndexOf(sitting.Id) is >= 0 and var at)
            _items[at] = _items[at] with { Slot = null };
    }

    /// <summary>The weapon slot a new hero's free weapon goes to.</summary>
    private WiredSlot? FirstWeaponSlot() => WiredCatalogue.FindChassis(Chassis)?.Slots.Where(s => s.Fixed is null)
        .Select(s => WiredSlot.Weapon(s.Mounting)).FirstOrDefault();

    /// <summary>Makes the hero anew. Whatever the capital no longer covers or the hero no longer wears, the list says so afterwards.</summary>
    private void Settle()
    {
        _state = WiredState.Create(_data, Maker, Options, out _bank);
        var goods = new Queue<WiredItem>(_state.Items.Where(i => i.Kind != WiredKind.Chassis));
        var (items, made) = (new List<WiredStartItem>(), new List<WiredItem>());
        foreach (var entry in _items)
        {
            if (!goods.TryPeek(out var good) || good.Catalogue != entry.Catalogue)
                continue;
            goods.Dequeue();
            items.Add(entry with { Slot = _state.Loadout.SlotOf(good.Id) });
            made.Add(good);
        }
        (_items, _made) = (items, made);
    }
}
