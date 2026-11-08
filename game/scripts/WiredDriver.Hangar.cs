using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

// The driver in the hangar and the menu: what the bot buys, wears, sells and takes it does with the hangar's cards, tabs
// and buttons, one command at a time and each landed before the next; the save and the load go through the menu.
public partial class WiredDriver
{
    /// <summary>Seconds between two deals, more than a double click takes, so that no click counts as the second half of one.</summary>
    private const double DealGap = 0.6;

    /// <summary>The hangar at the pod, for as long as it is open: the bot's next command, carried out on the screen, then its answer awaited.</summary>
    private async Task Shop(Game game, HangarPage page)
    {
        var world = game.World;
        var state = world.Wired!.Heroes[Game.Player];
        await Until(() => state.HangarOpen || !IsInstanceValid(page), 3);
        await Wait(1);
        await Moment("hangar", $"hangar-w{world.Wired.Wave}");
        Say($"hangar up: {game.RunReport}");
        Command? last = null;
        var again = 0;
        while (IsInstanceValid(page) && game.HangarShown == page)
        {
            if (!state.HangarOpen)
            {
                await Wait(0.2);
                continue;
            }
            _orders.Clear();
            _bot.Think();
            if (_orders.OfType<WiredCommand>().FirstOrDefault() is not { } order)
            {
                await Wait(0.5);
                continue;
            }
            again = Equals(order, last) ? again + 1 : 0;
            last = order;
            if (again >= 2)
            {
                // The driver's own way out: the same command three times over without an answer is not to be had here.
                Say($"hangar: {Describe(state, order)} came back three times unanswered, on to the wave");
                await CallTheWave(page);
                await Until(() => !IsInstanceValid(page) || game.HangarShown != page, 5);
                break;
            }
            var before = Fingerprint(world, state);
            var done = await Carry(page, state, order);
            Say($"hangar: {Describe(state, order)}{(done ? "" : ": not on the screen")}");
            if (!done)
                continue;
            // Landed once the run shows it; the banner speaks of a hovered card as well, so it only tells why not.
            var landed = await Until(() => !IsInstanceValid(page) || Fingerprint(world, state) != before ? this : null, 2) is not null;
            if (!landed && IsInstanceValid(page) && page.Banner.Length > 0)
                Say($"hangar says: '{page.Banner}'");
            if (order is WiredReadyCommand)
                await Until(() => !IsInstanceValid(page) || game.HangarShown != page, 5);
        }
        if (IsInstanceValid(game))
            Say($"hangar down: {game.RunReport}");
    }

    /// <summary>What a command touches of the run, to see it land.</summary>
    private static string Fingerprint(GameWorld world, WiredState state) =>
        $"{world.Races[Game.Player].Bmu:0}|{string.Join(",", state.Items.Select(i => $"{i.Id}:{i.Mk}"))}|{string.Join(",", state.Loadout.Worn.Select(i => i.Id))}|" +
        $"{string.Join(",", state.Loadout.Chips.Select(c => c?.Id))}|{state.ChipChoice.Count}|{string.Join(",", state.Offer)}|{state.Rerolled}|" +
        $"{world.Escorts(Game.Player).Count()}|{world.WiredPod(Game.Player)?.HitPoints}|{state.HangarOpen}";

    /// <summary>A command in a few words, with the names of what it touches.</summary>
    private static string Describe(WiredState state, Command order)
    {
        string Name(int? id) => state.Items.Find(i => i.Id == id)?.Catalogue ?? $"#{id}";
        return order switch
        {
            WiredBuyCommand buy => $"buy {buy.Item}",
            WiredSellCommand sell => $"sell {Name(sell.Item)}",
            WiredEquipCommand equip => $"equip {Name(equip.Item)} on {equip.Slot.Kind} {equip.Slot.Index}",
            WiredChassisCommand chassis => $"change to the chassis {Name(chassis.Item)}",
            WiredUpgradeCommand upgrade => $"upgrade {Name(upgrade.Item)}",
            WiredChipCommand chip => $"place the chip {Name(chip.Item)} in socket {chip.Socket + 1}",
            WiredChipChoiceCommand choice => $"take chip {choice.Choice + 1} of {state.ChipChoice.Count} ({state.ChipChoice.ElementAtOrDefault(choice.Choice)})",
            WiredRerollCommand => "reroll the offer",
            WiredSquadBuyCommand squad => $"recruit {squad.Machine}",
            WiredPodRepairCommand repair => $"mend the pod for {repair.Bmu} BMU",
            WiredReadyCommand => "ready",
            _ => order.ToString(),
        };
    }

    /// <summary>One of the bot's commands done with the hangar's cards and buttons; false if what it needs is not on the screen.</summary>
    private async Task<bool> Carry(HangarPage page, WiredState state, Command order)
    {
        for (; Now - _lastDeal < DealGap; await Frames(1))
        {
        }
        try
        {
            switch (order)
            {
                case WiredBuyCommand buy when WiredCatalogue.FindChassis(buy.Item) is not null:
                    return await Deal(page, HangarShelf.Tree, c => c.Good.Item.Catalogue == buy.Item, strip: false);
                case WiredBuyCommand buy:
                    return await Deal(page, HangarShelf.Offer, c => c.Good.Deal == HangarDeal.Buy && c.Good.Item.Catalogue == buy.Item, strip: true);
                case WiredUpgradeCommand upgrade:
                    return await Deal(page, HangarShelf.Upgrades, c => c.Good.Item.Id == upgrade.Item, strip: true);
                case WiredSellCommand sell:
                    return await Deal(page, ShelfOf(state, sell.Item), c => c.Good.Item.Id == sell.Item && c.Good.Deal == HangarDeal.Sell, strip: true);
                case WiredChassisCommand chassis:
                    var owned = state.Items.Find(i => i.Id == chassis.Item)?.Catalogue;
                    return await Deal(page, HangarShelf.Tree, c => c.Good.Item.Catalogue == owned, strip: false);
                case WiredEquipCommand equip:
                    return await Put(page, ShelfOf(state, equip.Item), equip.Item, equip.Slot);
                case WiredChipCommand { Item: { } chip } socket:
                    return await Put(page, HangarShelf.Chips, chip, WiredSlot.Chip(socket.Socket));
                case WiredChipChoiceCommand choice:
                    await Open(page, HangarShelf.Chips);
                    if (choice.Choice >= page.ChipChoices.Count)
                        return false;
                    await Move(Middle(page.ChipChoices[choice.Choice]));
                    await Wait(0.4);
                    await Moment("chip");
                    await Click(Middle(page.ChipChoices[choice.Choice]));
                    return true;
                case WiredRerollCommand:
                    await Open(page, HangarShelf.Offer);
                    if (page.Reroll is not { } reroll)
                        return false;
                    await Click(Middle(reroll));
                    return true;
                case WiredSquadBuyCommand squad:
                    await Open(page, HangarShelf.Squad);
                    if (page.Squad.FirstOrDefault(s => s.Machine == squad.Machine) is not { } card)
                        return false;
                    await Reveal(card);
                    await Click(Middle(card));
                    return true;
                case WiredPodRepairCommand repair:
                    return await Mend(page, repair.Bmu);
                case WiredReadyCommand:
                    return await CallTheWave(page);
            }
            return false;
        }
        finally
        {
            _lastDeal = Now;
        }
    }

    private static HangarShelf ShelfOf(WiredState state, int item) => state.Items.Find(i => i.Id == item)?.Kind switch
    {
        WiredKind.Weapon => HangarShelf.Weapons,
        WiredKind.Module => HangarShelf.Modules,
        WiredKind.Ability => HangarShelf.Abilities,
        WiredKind.Chip => HangarShelf.Chips,
        _ => HangarShelf.Chassis,
    };

    /// <summary>A shelf comes up by its tabs, the shop's or the stock's first.</summary>
    private async Task Open(HangarPage page, HangarShelf shelf)
    {
        if (page.ShelfShown == shelf)
            return;
        if (page.GroupTab(shelf >= HangarShelf.Tree) is { } group)
            await Click(Middle(group));
        await Click(Middle(page.TabOf(shelf)));
        await Wait(0.4);
    }

    /// <summary>A card scrolled into its shelf's view with the wheel, if it is below or above.</summary>
    private async Task Reveal(Control card)
    {
        Node? at = card.GetParent();
        while (at is not null and not ScrollContainer)
            at = at.GetParent();
        if (at is not ScrollContainer scroll)
            return;
        for (var turn = 0; turn < 40; turn++)
        {
            var (view, rect) = (scroll.GetGlobalRect(), card.GetGlobalRect());
            if (view.Encloses(rect))
                return;
            await Move(view.GetCenter());
            var wheel = rect.Position.Y < view.Position.Y ? MouseButton.WheelUp : MouseButton.WheelDown;
            Button(wheel, true);
            Button(wheel, false);
            await Frames(2);
        }
    }

    private static Vector2 Strip(ItemCard card) => card.GetGlobalTransform() * new Vector2(card.Size.X / 2, card.Size.Y - ItemCard.Strip / 2 - 6);

    /// <summary>A deal on a card: its strip, or the card itself where the card is the deal (the chassis tree).</summary>
    private async Task<bool> Deal(HangarPage page, HangarShelf shelf, Func<ItemCard, bool> which, bool strip)
    {
        await Open(page, shelf);
        if (page.Cards.FirstOrDefault(which) is not { } card)
            return false;
        await Reveal(card);
        await Move(Middle(card));
        await Wait(0.3);
        await Click(strip ? Strip(card) : Middle(card));
        return true;
    }

    /// <summary>A card carried onto a slot of the hero or a socket of the chip board.</summary>
    private async Task<bool> Put(HangarPage page, HangarShelf shelf, int item, WiredSlot slot)
    {
        await Open(page, shelf);
        if (page.Cards.FirstOrDefault(c => c.Good.Item.Id == item) is not { } card || page.Slots.FirstOrDefault(s => s.Slot == slot && s.IsVisibleInTree()) is not { } place)
            return false;
        await Reveal(card);
        await Drag(Middle(card), Middle(place));
        return true;
    }

    /// <summary>The pod mended for about what the bot means to spend: all of it if that is no more, else by the hundred and the 25.</summary>
    private async Task<bool> Mend(HangarPage page, int bmu)
    {
        await Open(page, HangarShelf.Pod);
        if (page.FindChildren("*", "", true, false).OfType<PodPanel>().FirstOrDefault() is not { } panel || ((MatchDesk)page.Desk).Pod is not { } pod)
            return false;
        var buttons = Buttons(panel).ToList();
        if (buttons.Count < 3)
            return false;
        if (bmu == 0 || pod.Price <= bmu)
        {
            await Click(Middle(buttons[2]));
            return true;
        }
        foreach (var (amount, button) in new[] { (100, buttons[1]), (25, buttons[0]) })
        {
            for (; bmu >= amount; bmu -= amount)
            {
                await Click(Middle(button));
                await Wait(DealGap);
            }
        }
        return true;
    }

    /// <summary>The button that calls the wave.</summary>
    private async Task<bool> CallTheWave(HangarPage page)
    {
        if (Buttons(page).FirstOrDefault(b => b.Text == Lang.T("Ready").ToUpperInvariant()) is not { } ready)
            return false;
        await Click(Middle(ready));
        return true;
    }

    // ---- the menu ---------------------------------------------------------------------------------

    /// <summary>The in-game menu's eight buttons once it is up, by its key.</summary>
    private async Task<List<ChipButton>?> OpenMenu(Game game)
    {
        LetGo();
        await Tap(KeyMap.Of(KeyMap.Act.Menu));
        return await MenuButtons(game);
    }

    private async Task<List<ChipButton>?> MenuButtons(Game game)
    {
        var grid = await Until(() => game.MenuShown?.FindChildren("*", nameof(GridContainer), true, false).OfType<GridContainer>()
            .FirstOrDefault(g => g.IsVisibleInTree() && g.GetChildCount() == 8), 20);
        await Wait(0.6);
        return grid?.GetChildren().OfType<ChipButton>().ToList();
    }

    /// <summary>The run saved through the menu under the driver's name, and on with it.</summary>
    private async Task Save(Game game)
    {
        _saved = true;
        _saveName = $"driver {plan.SaveAfter}";
        if (await OpenMenu(game) is not { Count: 8 } buttons)
        {
            Say("save: the menu did not come up");
            return;
        }
        await Moment("save", "save-menu");
        // In the original's order: Continue, Options, Briefing, Save Game down the left, then Restart, Load Game, Hot Keys, Exit Game.
        await Click(Middle(buttons[3]));
        var field = await Until(() => game.MenuShown?.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().FirstOrDefault(l => l.IsVisibleInTree()), 15);
        if (field is null)
        {
            Say("save: no page to save on");
            return;
        }
        await Wait(0.5);
        await Click(Middle(field));
        await Press(Godot.Key.A, ctrl: true);
        foreach (var letter in _saveName)
            await Letter(letter);
        await Moment("save", "save-page");
        var (tick, checksum) = (game.World.Tick, game.World.Checksum());
        var save = Buttons(game.MenuShown!).First(b => b.Text == Lang.T("Save").ToUpperInvariant());
        await Click(Middle(save));
        var back = await MenuButtons(game);
        var file = System.IO.Path.Combine(Game.SaveFolder, _saveName + Game.SaveExtension);
        Say($"saved '{_saveName}' at tick {tick}, checksum {checksum:x16}: {(System.IO.File.Exists(file) ? file : "no file")}");
        _savedAt = game.World.Time;
        if (back is { Count: 8 })
            await Click(Middle(back[0]));
        await Until(() => game.MenuShown is null, 10);
        Say($"back from the menu: {game.RunReport}");
    }

    /// <summary>The save loaded again through the menu; the match goes and the loaded one comes.</summary>
    private async Task Load(Game game)
    {
        _loaded = true;
        if (await OpenMenu(game) is not { Count: 8 } buttons)
        {
            Say("load: the menu did not come up");
            return;
        }
        Say($"loading at tick {game.World.Tick}, checksum {game.World.Checksum():x16}, {game.World.Time - _savedAt:0} s after the save");
        await Click(Middle(buttons[5]));
        var list = await Until(() => game.MenuShown?.FindChildren("*", nameof(ItemList), true, false).OfType<ItemList>().FirstOrDefault(l => l.IsVisibleInTree() && l.ItemCount > 0), 15);
        var index = list is null ? -1 : Enumerable.Range(0, list.ItemCount).FirstOrDefault(i => list.GetItemText(i).StartsWith(_saveName!), -1);
        if (list is null || index < 0)
        {
            Say($"load: '{_saveName}' is not on the list");
            return;
        }
        await Wait(0.5);
        var picked = list.GetItemText(index);
        await Click(list.GetGlobalTransform() * list.GetItemRect(index).GetCenter());
        await Moment("load", "load-page");
        var load = Buttons(game.MenuShown!).First(b => b.Text == Lang.T("Load").ToUpperInvariant());
        // The menu and the match go with the click.
        await Click(Middle(load));
        Say($"pressed load on '{picked}'");
    }
}
