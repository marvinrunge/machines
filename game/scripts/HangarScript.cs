using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// For automated checks of the hangar, in the real game with real input events: MACHINES_HANGAR_SCRIPT=&lt;folder&gt;,&lt;what&gt;
/// plays the mouse and writes its pictures to the folder, printing what became of each step as "hangar: ...".
/// <list type="bullet">
/// <item><c>look</c>: a picture of the screen as it comes up, in the look of MACHINES_LOOK, and quits;</item>
/// <item><c>path</c>: at creation, the chassis path with a click on a closed chassis, and quits;</item>
/// <item><c>full</c>: drags onto a slot, onto a wrong slot, takes off, changes chassis, fires and starts the run;</item>
/// <item><c>start</c>: starts the run with the remembered hero;</item>
/// <item><c>match</c>: over a run (<see cref="Game.ScriptHangar"/>), the same drag through the Wired commands;</item>
/// <item><c>shop</c>: over a run, all of the shop from the pod to the wave, with the checksum after each step, the hero driven in;</item>
/// <item><c>bay</c>: the same with the hero sent into a bay and no one driving it;</item>
/// <item><c>view</c>: over a run, every shelf for what sticks out, and a picture named by the look.</item>
/// </list>
/// </summary>
public partial class HangarScript(HangarPage page, string folder, string what) : Node
{
    private Vector2 _mouse = new(4, 4);
    private bool _down;

    private static void Say(string text) => GD.Print($"hangar: {text}");

    private string Worn(HeroLoadout? loadout = null)
    {
        loadout ??= page.Desk.Loadout;
        return $"{loadout.Chassis.Catalogue} [{string.Join(", ", loadout.Worn.Skip(1).Select(i => i.Catalogue))}], bank {page.Desk.Bank}";
    }

    public override async void _Ready()
    {
        await Wait(what == "look" ? 3.5 : 2.5);
        Overflow();
        switch (what)
        {
            case "look":
                await Picture($"creation-{GraphicsSettings.Name(GraphicsSettings.Look).Split(' ')[0].ToLowerInvariant()}");
                GetTree().Quit();
                break;
            case "path":
                await Open(HangarShelf.Tree);
                await Wait(1);
                await Move(Card("knight3"));
                await Click(MouseButton.Left);
                await Wait(0.3);
                Say($"clicked the closed knight3: says '{page.Banner}'");
                await Move(Card("reaper2"));
                await Wait(1);
                Say($"pointing at reaper2: says '{page.Banner}'");
                Overflow();
                await Picture("creation-path");
                GetTree().Quit();
                break;
            case "full":
                await Full();
                break;
            case "start":
                await Start();
                break;
            case "match":
                await Match();
                break;
            case "shop" or "bay":
                await Shop();
                break;
            case "view":
                await View();
                break;
        }
    }

    private Game Run => (Game)GetTree().CurrentScene;

    private void Step(string name) => Say($"step {name}: {Run.RunReport}");

    /// <summary>The commands of a click come back from the world within a few ticks.</summary>
    private async Task Landed() => await Wait(0.6);

    private async Task Open(HangarShelf shelf)
    {
        if (page.GroupTab(shelf >= HangarShelf.Tree) is { } group)
        {
            await Move(group.GetGlobalRect().GetCenter());
            await Click(MouseButton.Left);
        }
        await Move(page.TabOf(shelf).GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Wait(0.3);
    }

    /// <summary>Where a card's deal is clicked.</summary>
    private static Vector2 Strip(ItemCard card) => card.GetGlobalTransform() * new Vector2(card.Size.X / 2, card.Size.Y - ItemCard.Strip / 2 - 6);

    private ChipButton Named(string text) => page.FindChildren("*", "", true, false).OfType<ChipButton>().First(b => b.Text.Contains(text));

    private void Notes()
    {
        foreach (var slot in page.Slots.Where(s => s.Visible && s.Note is not null))
            Say($"  {slot.Title}: {slot.Item?.Catalogue ?? "-"}{(slot.Absent ? " (no slot)" : "")} '{slot.Note}'");
    }

    private async Task Shop()
    {
        Step("the hangar stands open at the pod");
        var desk = page.Desk;

        // The chips a boss left wait over the hero until one is taken.
        await Open(HangarShelf.Chips);
        await Picture("run-chipchoice");
        var chosen = page.ChipChoices[0];
        var left = page.ChipChoices.Count;
        await Move(chosen.GetGlobalRect().GetCenter());
        await Wait(0.4);
        Say($"pointing at the chip offered: says '{page.Banner}'");
        await Click(MouseButton.Left);
        await Landed();
        Step($"took {chosen.Good.Item.Catalogue} of the {left} offered, {page.ChipChoices.Count} left");

        // What is out of reach says so, in the sim's words and the player's language.
        await Open(HangarShelf.Tree);
        await Wait(1);
        await Move(Card("knight3"));
        await Click(MouseButton.Left);
        await Wait(0.3);
        Say($"clicked the closed knight3: says '{page.Banner}'");

        // A weapon from the offer, with the question it raises.
        await Open(HangarShelf.Offer);
        var offer = page.Cards.Where(c => c.Good.Item.Kind == WiredKind.Weapon && desk.BestSlot(c.Good) is not null).OrderBy(c => c.Good.Item.Catalogue == "BOLTER").First();
        var weapon = offer.Good.Item.Catalogue;
        await Move(offer.GetGlobalRect().GetCenter());
        await Wait(0.5);
        Say($"pointing at {weapon}: {offer.Good.Price} BMU, dps {page.Hero.DamagePerSecond:0.0} -> {page.Trying?.DamagePerSecond:0.0}");
        await Move(Strip(offer));
        await Click(MouseButton.Left);
        await Landed();
        Step($"bought {weapon}");

        // On the hero, by dragging.
        await Open(HangarShelf.Weapons);
        await Press(Card(weapon, owned: true));
        await Move(Slot(WiredSlot.Weapon(Mounting.Right)), 12);
        await Wait(0.3);
        await Release();
        await Wait(0.15);
        await Picture("run-spark");
        await Landed();
        Step($"equipped {weapon}");

        // The way to the Reaper, weapon on and a slot left empty, the hero still in the pod.
        await Open(HangarShelf.Tree);
        await Wait(0.4);
        await Move(Card("grunt2"));
        await Click(MouseButton.Left);
        await Landed();
        Step("bought grunt2");
        await Move(Card("reaper2"));
        await Wait(0.5);
        Say($"pointing at the open reaper2: says '{page.Banner}', hit points {page.Hero.Stats.HitPoints} -> {page.Trying?.Stats.HitPoints}");
        await Click(MouseButton.Left);
        await Landed();
        Step("bought reaper2");
        await Move(page.Screen.GetGlobalRect().GetCenter());
        await Wait(0.3);
        await Move(Card("reaper2"));
        await Wait(1.2);
        Say($"pointing at the owned reaper2: says '{page.Banner}'");
        Notes();
        await Picture("hangar-tree");
        await Click(MouseButton.Left);
        await Wait(1);
        Step("changed to reaper2: the hero is out of the door");
        await Picture("run-reaper");

        // A Mk up.
        await Open(HangarShelf.Upgrades);
        var mk = page.Cards.First(c => c.Good.Item.Catalogue == weapon);
        await Move(mk.GetGlobalRect().GetCenter());
        await Wait(0.5);
        Say($"pointing at Mk {mk.Good.Item.Mk + 1}: {mk.Good.Amount} BMU, dps {page.Hero.DamagePerSecond:0.0} -> {page.Trying?.DamagePerSecond:0.0}");
        Notes();
        await Move(Strip(mk));
        await Click(MouseButton.Left);
        await Landed();
        Step($"{weapon} a Mk up");

        // A reroll, and a second one that the world refuses because the first has not come back yet.
        await Open(HangarShelf.Offer);
        await Move(page.Reroll!.GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Click(MouseButton.Left);
        await Landed();
        Say($"rerolled twice at once: says '{page.Banner}'");
        Step("rerolled");
        var spare = page.Cards.FirstOrDefault(c => c.Good.Item.Kind == WiredKind.Weapon);
        if (spare is not null)
        {
            await Move(spare.GetGlobalRect().GetCenter());
            await Wait(0.5);
        }
        await Picture("hangar-offer");
        if (spare is not null)
        {
            await Move(Strip(spare));
            await Click(MouseButton.Left);
            await Landed();
            Step($"bought {spare.Good.Item.Catalogue} to sell it");
        }

        await Open(HangarShelf.Squad);
        await Wait(0.4);
        var escort = page.Squad.First(s => s.Why is null);
        await Move(escort.GetGlobalRect().GetCenter());
        await Wait(0.3);
        await Click(MouseButton.Left);
        await Landed();
        Step($"bought an escort, {escort.Machine}");
        await Picture("run-squad");

        await Open(HangarShelf.Pod);
        await Move(Named("25").GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Landed();
        Step("mended the pod for 25 BMU");
        await Picture("run-pod");
        await Move(Named(Lang.T("Repair it all").ToUpperInvariant()).GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Landed();
        Step("mended the rest of the pod");

        await Open(HangarShelf.Chassis);
        var grunt = page.Cards.First(c => c.Good.Item.Catalogue == "grunt2");
        await Move(Strip(grunt));
        await Click(MouseButton.Left);
        await Landed();
        Step("sold grunt2 by its strip");
        await Open(HangarShelf.Weapons);
        if (page.Cards.FirstOrDefault(c => c.Good.Deal == HangarDeal.Sell) is { } sold)
        {
            await Move(sold.GetGlobalRect().GetCenter());
            await Click(MouseButton.Right);
            await Landed();
            Step($"sold {sold.Good.Item.Catalogue} by a right click");
        }

        // Two chips of a family on the bridge.
        await Open(HangarShelf.Chips);
        var (first, second) = (Card("marksman"), WiredSlot.Chip(0));
        await Press(first);
        await Move(Slot(second), 12);
        await Wait(0.3);
        await Release();
        await Landed();
        Step("marksman in socket 1");
        await Press(Card("hotbarrels"));
        await Move(Slot(WiredSlot.Chip(3)), 12);
        await Wait(0.6);
        Say($"holding hotbarrels over socket 4: says '{page.Banner}', range {page.Hero.Range:0.0} -> {page.Trying?.Range:0.0}, dps {page.Hero.DamagePerSecond:0.0} -> {page.Trying?.DamagePerSecond:0.0}");
        await Release();
        await Landed();
        Step("hotbarrels in socket 4: linked over the bridge");
        Say($"linked: {WiredChips.Linked(desk.Loadout.Chips, 0)} and {WiredChips.Linked(desk.Loadout.Chips, 3)}, strengths {WiredChips.Strength(desk.Loadout.Chips, 0):0.000} and {WiredChips.Strength(desk.Loadout.Chips, 3):0.000}");
        await Move(page.Slots.First(s => s.Slot == WiredSlot.Chip(3)).GetGlobalRect().GetCenter());
        await Wait(1.2);
        await Picture("hangar-chips");

        // Off: what comes after this the game does, as the screen goes with the hangar.
        Overflow();
        await Move(Named(Lang.T("Ready").ToUpperInvariant()).GetGlobalRect().GetCenter());
        await Wait(0.3);
        Run.WatchTheWave(folder);
        await Click(MouseButton.Left);
    }

    /// <summary>Every shelf for what sticks out, then the one that says most, named by the look and the language.</summary>
    private async Task View()
    {
        foreach (var shelf in Enum.GetValues<HangarShelf>())
        {
            await Open(shelf);
            await Wait(0.3);
            Say($"shelf {shelf}: {page.Cards.Count} cards");
            Overflow();
            await Picture($"shelf-{shelf.ToString().ToLowerInvariant()}");
        }
        await Open(HangarShelf.Tree);
        await Wait(1);
        await Move(Card("reaper2"));
        await Wait(2);
        var look = GraphicsSettings.Name(GraphicsSettings.Look).Split(' ')[0].ToLowerInvariant();
        await Picture($"hangar-match-{look}{(Lang.Code == "de" ? "" : "-" + Lang.Code)}");
        ((Game)GetTree().CurrentScene).EndHangarScript(folder);
    }

    private async Task Full()
    {
        Say($"start: {Worn()}");
        await Picture("step-1-start");
        var right = Slot(WiredSlot.Weapon(Mounting.Right));

        // Hovering shows deltas and the model without changing anything.
        await Move(Card("AUTO_CANNON"));
        await Wait(0.5);
        Say($"hover AUTO_CANNON: dps {page.Hero.DamagePerSecond:0.0} -> {page.Trying?.DamagePerSecond:0.0}, on the model {RigWeapons()}, still {Worn()}");

        // A drag onto the slot that takes it.
        await Press(Card("FLAME_THROWER2"));
        await Move(right, 12);
        await Wait(0.3);
        Say($"holding over the right slot: fits {SlotOf(right)?.Fits}");
        await Release();
        await Wait(0.12);
        await Picture("hangar-spark");
        await Wait(0.4);
        Say($"dragged FLAME_THROWER2 onto the right slot: {Worn()}");
        await Wait(0.8);
        await Picture("step-2-equipped");

        // Test fire, with its picture in the middle of it.
        await Move(page.Screen.GetGlobalRect().GetCenter());
        await Move(right);
        await Wait(0.2);
        Say($"pointing at the worn weapon: says '{page.Banner}', model keeps it: {page.Trying is null}");
        await Click(MouseButton.Left);
        await Wait(0.45);
        await Picture("hangar-fire");
        await Wait(2);
        var firing = page.Screen.Firing;
        var testFire = page.FindChildren("*", "", true, false).OfType<ChipButton>().First(b => b.Text == Lang.T("Test fire").ToUpperInvariant());
        await Move(testFire.GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Wait(0.1);
        Say($"test fire button: firing {firing} -> {page.Screen.Firing}");

        // Turning and zooming the turntable.
        var (yaw, zoom) = (page.Screen.Yaw, page.Screen.Zoom);
        var spot = page.Screen.GetGlobalRect().GetCenter() + new Vector2(0, -40);
        await Press(spot);
        await Move(spot + new Vector2(120, 30), 6);
        await Release();
        await Click(MouseButton.WheelUp);
        await Click(MouseButton.WheelUp);
        await Wait(0.2);
        Say($"dragged on the screen: yaw {yaw:0.00} -> {page.Screen.Yaw:0.00}; wheel: zoom {zoom:0.00} -> {page.Screen.Zoom:0.00}");
        page.Screen.Zoom = zoom;

        // A wrong slot: everything that does not fit is red while dragging.
        await Press(Card("ELECTRIC_CHARGE"));
        var module = Slot(WiredSlot.Module(0));
        await Move(module, 12);
        await Wait(0.5);
        Say($"holding over the module slot: fits {SlotOf(module)?.Fits}, says '{page.Banner}'; red slots: {string.Join(", ", page.Slots.Where(s => s.Fits == false).Select(s => s.Title))}");
        await Picture("hangar-drag");
        await Release();
        await Wait(0.3);
        Say($"dropped on the wrong slot: says '{page.Banner}', {Worn()}");

        // Right click takes off, and it stays owned.
        await Wait(0.5);
        await Move(right);
        await Click(MouseButton.Right);
        await Wait(0.5);
        Say($"right click on the right slot: {Worn()}, spares {string.Join(", ", page.Cards.Where(c => c.Good.Price is null).Select(c => c.Good.Item.Catalogue))}");

        // Click to pick, click to put.
        await Move(Card("BOLTER", owned: true));
        await Click(MouseButton.Left);
        await Wait(0.2);
        await Move(right);
        await Wait(0.3);
        await Click(MouseButton.Left);
        await Wait(0.5);
        Say($"picked BOLTER and clicked the slot: {Worn()}");

        // The chassis: the preview says what stays on, what falls off and what stays empty.
        await Move(page.TabOf(WiredKind.Chassis).GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Wait(0.3);
        await Move(Card("knight3"));
        await Click(MouseButton.Left);
        await Wait(0.3);
        Say($"clicked the locked knight3: says '{page.Banner}'");
        await Wait(0.5);
        await Move(Card("reaper2"));
        await Wait(1);
        Say($"hover reaper2: says '{page.Banner}', armour {page.Hero.Stats.Armour} -> {page.Trying?.Stats.Armour}, model {RigWeapons()}, turn limit shown");
        foreach (var slot in page.Slots.Where(s => s.Visible))
            Say($"  {slot.Title}: {slot.Item?.Catalogue ?? "-"}{(slot.Absent ? " (no slot)" : "")} '{slot.Note}'");
        await Picture("hangar-hover-chassis");
        await Press(Card("reaper2"));
        await Move(page.Plate.GetGlobalRect().GetCenter(), 12);
        await Wait(0.3);
        await Release();
        await Wait(0.6);
        Say($"dragged reaper2 onto the plate: {Worn()}");
        await Picture("step-3-chassis");

        // The other way: down to the Grunt, with the spare on the Reaper's second mount.
        await Move(page.TabOf(WiredKind.Weapon).GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Wait(0.3);
        await Press(Card("FLAME_THROWER2", owned: true));
        await Move(Slot(WiredSlot.Weapon(Mounting.Left)), 12);
        await Release();
        await Wait(0.6);
        await Move(page.TabOf(WiredKind.Chassis).GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Wait(0.3);
        await Move(Card("grunt1"));
        await Wait(1);
        Say($"hover grunt1 from {Worn()}: says '{page.Banner}'");
        foreach (var slot in page.Slots.Where(s => s.Visible && s.Note is not null))
            Say($"  {slot.Title}: {slot.Item?.Catalogue ?? "-"}{(slot.Absent ? " (no slot)" : "")} '{slot.Note}'");
        await Picture("hangar-hover-down");

        // The hero's colours and name.
        await Move(page.GetGlobalRect().GetCenter());
        var lamp = page.FindChildren("*", "", true, false).OfType<ColourLamp>().ElementAt(1);
        await Move(lamp.GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        var field = page.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First();
        await Move(field.GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Frames();
        foreach (var letter in "Kai")
        {
            GetViewport().PushInput(new InputEventKey { Pressed = true, Unicode = letter, Keycode = (Key)char.ToUpperInvariant(letter) });
            await Frames();
            GetViewport().PushInput(new InputEventKey { Pressed = false, Keycode = (Key)char.ToUpperInvariant(letter) });
            await Frames();
        }
        Say($"typed '{field.Text}', colour {page.Desk.Look}");
        await Wait(0.4);
        await Picture("step-4-blue");

        await Start();
    }

    /// <summary>The script has started its run: the way back to the hangar, after the run's end, is not for it to walk again.</summary>
    public static bool Started { get; private set; }

    /// <summary>Presses the button that starts the run.</summary>
    private async Task Start()
    {
        Started = true;
        var start = page.FindChildren("*", "", true, false).OfType<ChipButton>()
            .First(b => b.Text == Lang.T("Start the mission").ToUpperInvariant());
        await Move(start.GetGlobalRect().GetCenter());
        await Wait(0.2);
        Say($"pressing start, options {page.Desk is CreationDesk}");
        await Click(MouseButton.Left);
    }

    private async Task Match()
    {
        Say($"over the run: {Worn()}");
        var spare = page.Cards.FirstOrDefault(c => c.Good.Item.Kind == WiredKind.Weapon);
        await Move(page.TabOf(WiredKind.Weapon).GetGlobalRect().GetCenter());
        await Click(MouseButton.Left);
        await Wait(0.3);
        // What is worn comes off and goes on again by the commands.
        var worn = page.Slots.First(s => s.Slot.Kind == WiredSlotKind.Weapon && s.Item is not null);
        await Move(worn.GetGlobalRect().GetCenter());
        await Click(MouseButton.Right);
        await Wait(1);
        Say($"right click on {worn.Title}: {Worn()}, spares {string.Join(", ", page.Cards.Select(c => c.Good.Item.Catalogue))}");
        spare = page.Cards.FirstOrDefault();
        if (spare is not null && page.Desk.BestSlot(spare.Good) is { } slot)
        {
            await Press(spare.GetGlobalRect().GetCenter());
            await Move(Slot(slot), 12);
            await Wait(0.3);
            await Release();
            await Wait(1);
            Say($"dragged {spare.Good.Item.Catalogue} onto {slot}: {Worn()}");
        }
        await Picture("hangar-match");
        ((Game)GetTree().CurrentScene).EndHangarScript(folder);
    }

    /// <summary>What sticks out of the window.</summary>
    private void Overflow()
    {
        var window = GetViewport().GetVisibleRect();
        var out_ = page.FindChildren("*", nameof(Control), true, false).OfType<Control>()
            .Where(c => c.IsVisibleInTree() && !Scrolled(c) && !window.Grow(1).Encloses(c.GetGlobalRect()))
            .Select(c => $"{c.GetType().Name} {c.GetGlobalRect()}").ToList();
        Say($"window {window.Size}: {out_.Count} controls outside{(out_.Count > 0 ? ": " + string.Join("; ", out_.Take(8)) : "")}");
        var inside = page.Inventory.GetGlobalRect();
        Say($"cards outside the inventory's width: {page.Cards.Count(c => c.GetGlobalRect().End.X > inside.End.X)}");
    }

    private static bool Scrolled(Node node)
    {
        for (var at = node.GetParent(); at is not null; at = at.GetParent())
            if (at is ScrollContainer)
                return true;
        return false;
    }

    private string RigWeapons() => page.Screen.View?.Rig.Nodes.Length is { } parts ? $"{parts} parts" : "none";

    private SlotCard? SlotOf(Vector2 at) => page.Slots.FirstOrDefault(s => s.GetGlobalRect().HasPoint(at));

    private Vector2 Slot(WiredSlot slot) => page.Slots.First(s => s.Slot == slot).GetGlobalRect().GetCenter();

    private Vector2 Card(string catalogue, bool owned = false)
    {
        var card = page.Cards.First(c => c.Good.Item.Catalogue == catalogue && (!owned || c.Good.Price is null));
        return card.GetGlobalRect().GetCenter();
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task Frames(int count = 2)
    {
        for (var i = 0; i < count; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Move(Vector2 to, int steps = 1)
    {
        var from = _mouse;
        for (var i = 1; i <= steps; i++)
        {
            var at = from.Lerp(to, (float)i / steps);
            GetViewport().PushInput(new InputEventMouseMotion
            {
                Position = at,
                GlobalPosition = at,
                Relative = at - _mouse,
                ButtonMask = _down ? MouseButtonMask.Left : 0,
            });
            _mouse = at;
            await Frames();
        }
    }

    private void Button(MouseButton button, bool pressed)
    {
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = _mouse,
            GlobalPosition = _mouse,
            ButtonIndex = button,
            Pressed = pressed,
            ButtonMask = pressed ? (MouseButtonMask)(1 << ((int)button - 1)) : 0,
        });
    }

    private async Task Click(MouseButton button)
    {
        Button(button, true);
        await Frames();
        Button(button, false);
        await Frames();
    }

    private async Task Press(Vector2 at)
    {
        await Move(at);
        await Frames();
        Button(MouseButton.Left, true);
        _down = true;
        await Frames();
    }

    private async Task Release()
    {
        Button(MouseButton.Left, false);
        _down = false;
        await Frames();
    }

    private async Task Picture(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var path = System.IO.Path.Combine(folder, name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Say($"picture {path}");
    }
}
