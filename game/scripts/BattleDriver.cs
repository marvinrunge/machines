using Godot;
using Machines.Import.Model;
using Machines.Sim.Ai;
using Machines.Sim.Survival;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// For automated checks of a whole battle in the real game, through the real input:
/// MACHINES_BATTLE_DRIVE=&lt;folder&gt;[,&lt;option&gt;...] goes from the main menu's Battle button to the pod, where it changes to
/// a chassis of the tier asked for, researches or buys one thing the account can pay for and fills the empty slots with what
/// it owns, all by clicks and drags; it sets the map, lives and victory on the foot bar, presses "Fight against bots" and
/// plays the player's hero in first person with held keys and buttons. What to do a <see cref="BattleBot"/> of the player's
/// race decides, its orders caught by its issue hook; the driver only carries them out with the keys and the mouse, as
/// <see cref="WiredDriver"/> does with the Wired bench's bot, and never touches the world. At the end screen it checks the
/// XP, free XP and credits shown against <see cref="BattleRewards.Of"/> and, back in the pod, the account against them.
/// It prints "drive: ..." lines, "drive: tick N checksum X ..." every 100 ticks, and writes its pictures to the folder. Over a
/// recording (MACHINES_REPLAY) it opens the recording from the menu and prints the same tick lines and nothing else, for
/// the checksums to be compared. Options:
/// <list type="bullet">
/// <item><c>map=&lt;scenario&gt;</c>: the battle map (fortmult, mgrack, m_desert, micey, m_green); else the pod's;</item>
/// <item><c>tier=&lt;n&gt;</c>: the chassis tier to fight on, worn, bought or researched in the pod if it can be; else the worn one's;</item>
/// <item><c>lives=&lt;1..5&gt;</c> and <c>win=both|wipe|capture</c>: else the pod's;</item>
/// <item><c>shots=&lt;moment&gt;+...</c>, <c>all</c> (the default) or <c>none</c>: pictures at menu, pod, deal, ready, opening,
/// fight, capture, down, watching, ended, end and after, and at t&lt;tick&gt;; a file "snap" in the folder asks for one at once;</item>
/// <item><c>end=pod|menu</c>: the end screen's button it presses (pod).</item>
/// </list>
/// Battle is the remake's own addition.
/// </summary>
public partial class BattleDriver(string folder, BattleDriver.Course course) : WiredDriver(folder, course.Pictures)
{
    /// <summary>What the switch asks for.</summary>
    public sealed record Course(string? Map, int? Tier, int? Lives, BattleWin? Win, string End, Plan Pictures)
    {
        public static Course Parse(IEnumerable<string> options)
        {
            var set = options.Select(o => o.Split('=', 2)).Where(w => w.Length == 2).ToDictionary(w => w[0].Trim().ToLowerInvariant(), w => w[1].Trim());
            string? Get(string key) => set.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
            int? Number(string key) => Get(key) is { } text && int.TryParse(text, out var value) ? value : null;
            var map = Get("map");
            if (map is not null && BattleCatalogue.Map(map) is null)
                throw new ArgumentException($"MACHINES_BATTLE_DRIVE: no battle map '{map}'; the maps are {string.Join(", ", BattleCatalogue.Maps.Select(m => m.Scenario))}");
            var win = Get("win") is { } won ? Enum.TryParse<BattleWin>(won, true, out var parsed) ? parsed : throw new ArgumentException($"MACHINES_BATTLE_DRIVE: no victory '{won}'") : (BattleWin?)null;
            var shots = (Get("shots") ?? "all").Split('+', StringSplitOptions.RemoveEmptyEntries).Where(s => s != "none").ToHashSet();
            var end = Get("end") ?? "pod";
            if (end is not ("pod" or "menu"))
                throw new ArgumentException($"MACHINES_BATTLE_DRIVE: end is pod or menu, not '{end}'");
            return new Course(map, Number("tier"), Number("lives") is { } lives ? Math.Clamp(lives, 1, 5) : null, win, end,
                new Plan("Driver", int.MaxValue, null, 0, shots, end, "squad"));
        }
    }

    private static bool _driving;

    /// <summary>Comes along if the switch says so, from the first screen on; it outlives every change of scene.</summary>
    public static new void Attend(SceneTree tree)
    {
        if (_driving || System.Environment.GetEnvironmentVariable("MACHINES_BATTLE_DRIVE") is not { Length: > 0 } drive)
            return;
        _driving = true;
        var words = drive.Split(',');
        tree.Root.CallDeferred(Node.MethodName.AddChild, new BattleDriver(words[0], Course.Parse(words.Skip(1))));
    }

    // The account as it stood when the fight began: the chassis fought on, its XP, free XP and credits.
    private (string Chassis, int Xp, int FreeXp, long Credits)? _before;

    // The match being driven, the bot that decides, and the pictures taken once.
    private Game? _game;
    private BattleBot _brain = null!;
    private bool _stand;
    private readonly HashSet<string> _taken = [];
    private double _sightedSince = -1, _downSince = -1, _watchSince = -1, _overSince = -1;
    private int _downs;

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        System.IO.Directory.CreateDirectory(Folder);
        Say($"battle plan: map {course.Map ?? "the pod's"}, tier {course.Tier?.ToString() ?? "the worn chassis'"}, lives {course.Lives?.ToString() ?? "the pod's"}, " +
            $"victory {course.Win?.ToString() ?? "the pod's"}, pictures [{string.Join(", ", course.Pictures.Shots)}], ends by {course.End}, folder {Folder}, " +
            $"replays to {Game.ReplayFolder}");
        try
        {
            if (MatchRequest.Current.Replay is not null)
                await Recording();
            else
            {
                await ToPod();
                await Pod();
                if (await Fight())
                    await End();
            }
        }
        catch (Exception exception)
        {
            Say($"failed: {exception}");
        }
        Say("over");
        GetTree().Quit();
    }

    // ---- the way in -------------------------------------------------------------------------------

    /// <summary>The main menu's Battle button.</summary>
    private async Task ToPod()
    {
        await Until(() => Find<MainMenuPage>(), 300);
        await Until(() => !MenuFilms.Playing, 30);
        // The page may be put up again in its first moments: the one there after them is the one to press.
        await Wait(1.5);
        var menu = await Until(() => Find<MainMenuPage>() is { } page && IsInstanceValid(page) ? page : null, 30) ?? throw new InvalidOperationException("no main menu came");
        await Moment("menu");
        var word = Lang.T("Battle").ToUpperInvariant();
        var battle = Buttons(menu).First(b => b.Text == word || b.Text.StartsWith(word + " ", StringComparison.Ordinal));
        Say("main menu: pressing Battle");
        await Click(Middle(battle));
    }

    /// <summary>
    /// The pod: the tier asked for, one deal the account can pay for, what is owned into the empty slots, the battle on the
    /// foot bar, and the way in.
    /// </summary>
    private async Task Pod()
    {
        var page = await Until(() => Find<HangarPage>() is { Desk: BattleDesk } found ? found : null, 60) ?? throw new InvalidOperationException("no pod came");
        await Wait(2);
        var desk = (BattleDesk)page.Desk;
        Say($"pod: {Hero(desk)}; account {Account(desk.Account, desk.Draft.Chassis)}");
        await Moment("pod");

        var dealt = false;
        if (course.Tier is { } tier && desk.Draft.Tier != tier)
            dealt = await ToTier(page, desk, tier);
        if (!dealt)
            dealt = await OneDeal(page, desk);
        if (dealt)
            await Moment("deal");
        await Lighten(page, desk);
        await Fill(page, desk);

        // The battle on the foot bar: map, lives and victory, stepped on by clicks as a player does.
        var choices = page.FindChildren("*", "", true, false).OfType<ChipChoice>().ToList();
        var maps = BattleCatalogue.Maps.Select(BattlePod.MapName).ToList();
        if (course.Map is { } scenario && choices.FirstOrDefault(c => c.Items.SequenceEqual(maps)) is { } map)
            await Step(map, BattlePod.MapName(BattleCatalogue.Map(scenario)!));
        if (course.Lives is { } lives && choices.FirstOrDefault(c => c.Items.FirstOrDefault() == BattlePod.LivesName(1)) is { } livesChoice)
            await Step(livesChoice, BattlePod.LivesName(lives));
        if (course.Win is { } win && choices.FirstOrDefault(c => c.Items.FirstOrDefault() == BattlePod.VictoryName(BattleWin.Both)) is { } victory)
            await Step(victory, BattlePod.VictoryName(win));
        await Wait(0.8);
        var draft = desk.Draft;
        Say($"pod ready: {Hero(desk)}; battle {draft.Map}, tier {BattleDraft.Roman(draft.Tier)}, {draft.Lives} lives, {draft.Win}; account {Account(desk.Account, draft.Chassis)}");
        await Moment("ready");
        if (draft.WhyNotFight() is { } why)
            Say($"pod: the hero may not fight: {BattleDesk.Say(why)}");
        _before = (draft.Chassis, desk.Account.Xp(draft.Chassis), desk.Account.FreeXp, desk.Account.Credits);
        var fight = Buttons(page).First(b => b.Text == Lang.T("Fight against bots").ToUpperInvariant());
        Say("pod: pressing Fight against bots");
        await Click(Middle(fight));
    }

    /// <summary>
    /// A chassis of the tier asked for: an owned one is worn by a click on the Path; one for sale that the credits pay for is
    /// bought and then worn; one that can be researched is researched (it is then for sale, but that would be a second
    /// deal). True when a deal was made.
    /// </summary>
    private async Task<bool> ToTier(HangarPage page, BattleDesk desk, int tier)
    {
        var nodes = desk.Draft.Tree().Where(n => n.Tier == tier).ToList();
        if (nodes.FirstOrDefault(n => n.Stage == BattleStage.Owned) is { } owned)
        {
            await WearOnPath(page, desk, owned.Chassis);
            return false;
        }
        if (nodes.Where(n => n.Stage == BattleStage.ForSale).OrderBy(n => n.Cost).FirstOrDefault() is { } sale)
        {
            var bought = await Carry(page, desk, $"buy the chassis {sale.Chassis} for {sale.Cost} Cr", HangarShelf.Tree, c => c.Good.Item.Catalogue == sale.Chassis, strip: false);
            await WearOnPath(page, desk, sale.Chassis);
            return bought;
        }
        if (nodes.Where(n => n.Stage == BattleStage.Researchable).OrderBy(n => n.Cost).FirstOrDefault() is { } research)
            return await Carry(page, desk, $"research {research.Chassis} for {research.Cost} XP", HangarShelf.Tree, c => c.Good.Item.Catalogue == research.Chassis, strip: false);
        Say($"pod: no chassis of tier {BattleDraft.Roman(tier)} can be had: {string.Join(", ", nodes.Select(n => $"{n.Chassis} {n.Stage}"))}; it fights on {desk.Draft.Chassis}");
        return false;
    }

    /// <summary>An owned chassis worn by a click on its card on the Path.</summary>
    private async Task WearOnPath(HangarPage page, BattleDesk desk, string chassis)
    {
        if (desk.Draft.Chassis == chassis || !desk.Account.Owned(chassis))
            return;
        await Gap();
        await Deal(page, HangarShelf.Tree, c => c.Good.Item.Catalogue == chassis, strip: false);
        await Until(() => desk.Draft.Chassis == chassis, 2);
        Say($"pod: {(desk.Draft.Chassis == chassis ? "changed to" : "could not change to")} {chassis}{Refusal(page)}");
    }

    /// <summary>
    /// One thing the account can pay for: research first (the cheapest), then a chassis for sale, then the cheapest weapon,
    /// module or ability for sale. True when it was made.
    /// </summary>
    private async Task<bool> OneDeal(HangarPage page, BattleDesk desk)
    {
        var draft = desk.Draft;
        var nodes = draft.Tree().ToList();
        if (nodes.Where(n => n.Stage == BattleStage.Researchable).OrderBy(n => n.Cost).ThenBy(n => n.Chassis, StringComparer.Ordinal).FirstOrDefault() is { } research)
            return await Carry(page, desk, $"research {research.Chassis} for {research.Cost} XP", HangarShelf.Tree, c => c.Good.Item.Catalogue == research.Chassis, strip: false);
        if (nodes.Where(n => n.Stage == BattleStage.ForSale).OrderBy(n => n.Cost).ThenBy(n => n.Chassis, StringComparer.Ordinal).FirstOrDefault() is { } sale)
            return await Carry(page, desk, $"buy the chassis {sale.Chassis} for {sale.Cost} Cr", HangarShelf.Tree, c => c.Good.Item.Catalogue == sale.Chassis, strip: false);
        var item = new[] { WiredKind.Weapon, WiredKind.Module, WiredKind.Ability }
            .SelectMany(kind => draft.Offers(kind))
            .Where(o => o.Mk == 0 && o.Lock is null && o.Price is { } price && price <= desk.Account.Credits)
            .OrderBy(o => o.Price).ThenBy(o => o.Item, StringComparer.Ordinal).FirstOrDefault();
        if (item is not null)
            return await Carry(page, desk, $"buy {item.Item} for {item.Price} Cr", ShelfOf(item.Kind), c => c.Good.Deal == HangarDeal.Buy && c.Good.Item.Catalogue == item.Item, strip: true);
        Say($"pod: nothing to research or buy with {Account(desk.Account, draft.Chassis)}");
        return false;
    }

    private static HangarShelf ShelfOf(WiredKind kind) => kind switch
    {
        WiredKind.Weapon => HangarShelf.Weapons,
        WiredKind.Module => HangarShelf.Modules,
        WiredKind.Ability => HangarShelf.Abilities,
        _ => HangarShelf.Tree,
    };

    /// <summary>A deal by a click on a card, and whether the account shows it landed.</summary>
    private async Task<bool> Carry(HangarPage page, BattleDesk desk, string what, HangarShelf shelf, Func<ItemCard, bool> which, bool strip)
    {
        await Gap();
        var before = Account(desk.Account, desk.Draft.Chassis);
        if (!await Deal(page, shelf, which, strip))
        {
            Say($"pod: {what}: not on the screen");
            return false;
        }
        var landed = await Until(() => Account(desk.Account, desk.Draft.Chassis) != before ? this : null, 2) is not null;
        Say($"pod: {what}: {(landed ? "done" : "not done")}{Refusal(page)}; account {Account(desk.Account, desk.Draft.Chassis)}");
        return landed;
    }

    /// <summary>
    /// What the account owns carried onto the hero's empty slots, the most costly that fits first, shelf by shelf.
    /// </summary>
    private async Task Fill(HangarPage page, BattleDesk desk)
    {
        foreach (var kind in new[] { WiredKind.Weapon, WiredKind.Module, WiredKind.Ability })
        {
            var shelf = ShelfOf(kind);
            if (!desk.Shelves.Contains(shelf))
                continue;
            await Open(page, shelf);
            for (var tries = 0; tries < 6; tries++)
            {
                var empty = page.Slots.Where(s => s.Slot.Takes == kind && s.IsVisibleInTree() && desk.Loadout.At(s.Slot) is null).Select(s => s.Slot).ToList();
                var worn = desk.Loadout.Worn.Select(i => i.Catalogue).ToHashSet();
                // As a player would: nothing that overloads the hero or leaves it short of power, no module or ability
                // twice (an owned one may go on any number of slots, but a second plate is what overloaded the hero), and
                // something not worn yet before a second weapon of the same.
                bool Fits(WiredSlot slot, HangarGood good) =>
                    desk.WhyNot(good, slot) is null && !(kind != WiredKind.Weapon && worn.Contains(good.Item.Catalogue))
                    && HeroBuild.Resolve(desk.Data, desk.Loadout.With(slot, good.Item)) is { LoadShare: <= 1, PowerShare: >= 1 };
                var pick = empty.SelectMany(slot => page.Cards
                        .Where(c => c.Good is { Price: null, Locked: null } && c.Good.Item.Kind == kind && Fits(slot, c.Good))
                        .Select(c => (Slot: slot, Card: c)))
                    .OrderBy(p => worn.Contains(p.Card.Good.Item.Catalogue)).ThenByDescending(p => Worth(p.Card.Good.Item.Catalogue))
                    .ThenBy(p => p.Card.Good.Item.Catalogue, StringComparer.Ordinal).ThenBy(p => p.Slot.Index)
                    .FirstOrDefault();
                if (pick.Card is null)
                    break;
                await Gap();
                var id = pick.Card.Good.Item.Catalogue;
                await Put(page, shelf, pick.Card.Good.Item.Id, pick.Slot);
                var put = await Until(() => desk.Loadout.At(pick.Slot) is not null ? this : null, 2) is not null;
                Say($"pod: {id} onto {pick.Slot.Kind} {pick.Slot.Index}: {(put ? "on" : "not on")}{Refusal(page)}");
                if (!put)
                    break;
            }
        }
    }

    /// <summary>
    /// What a player would take off before a fight, by a right click on its slot: a second of a module or ability (the hero
    /// comes back to the pod in what it last wore, and a deal may have put a weapon on), and while the hero is overloaded
    /// the module worth least.
    /// </summary>
    private async Task Lighten(HangarPage page, BattleDesk desk)
    {
        for (var tries = 0; tries < 8; tries++)
        {
            var loadout = desk.Loadout;
            var worn = loadout.Modules.Select((item, at) => (Item: item, Slot: new WiredSlot(WiredSlotKind.Module, at)))
                .Concat(loadout.Abilities.Select((item, at) => (Item: item, Slot: new WiredSlot(WiredSlotKind.Ability, at))))
                .Where(w => w.Item is not null).ToList();
            var twice = worn.Where((w, at) => worn.Take(at).Any(e => e.Item!.Catalogue == w.Item!.Catalogue)).Select(w => (Slot: w.Slot, Why: "a second")).FirstOrDefault();
            var heavy = HeroBuild.Resolve(desk.Data, loadout).LoadShare > 1
                ? worn.Where(w => w.Slot.Kind == WiredSlotKind.Module).OrderBy(w => Worth(w.Item!.Catalogue)).ThenByDescending(w => w.Slot.Index)
                    .Select(w => (Slot: w.Slot, Why: "overloaded")).FirstOrDefault()
                : default;
            if ((twice.Slot ?? heavy.Slot) is not { } off)
                return;
            var why = twice.Slot is not null ? twice.Why : heavy.Why;
            var id = loadout.At(off)!.Catalogue;
            await Open(page, off.Kind == WiredSlotKind.Module ? HangarShelf.Modules : HangarShelf.Abilities);
            if (page.Slots.FirstOrDefault(s => s.Slot == off && s.IsVisibleInTree()) is not { } place)
                return;
            await Gap();
            await Click(Middle(place), MouseButton.Right);
            var taken = await Until(() => desk.Loadout.At(off) is null ? this : null, 2) is not null;
            Say($"pod: {id} off {off.Kind} {off.Index} ({why}): {(taken ? "off" : "still on")}{Refusal(page)}");
            if (!taken)
                return;
        }
    }

    /// <summary>What an item is worth in Wired's prices, to pick the best owned one.</summary>
    private static int Worth(string id) =>
        WiredCatalogue.Weapons.FirstOrDefault(w => w.Id == id)?.Price
        ?? WiredCatalogue.Modules.FirstOrDefault(m => m.Id == id)?.Prices[0]
        ?? WiredCatalogue.Abilities.FirstOrDefault(a => a.Id == id)?.Price ?? 0;

    /// <summary>Between two deals more than a double click takes, so that no click counts as the second half of one.</summary>
    private async Task Gap() => await Wait(DealGap + 0.4);

    private static string Refusal(HangarPage page) => page.Banner is { Length: > 0 } banner ? $" (the pod says '{banner}')" : "";

    private static string Hero(BattleDesk desk) =>
        $"hero '{desk.Draft.Name}' on {desk.Draft.Chassis} (tier {BattleDraft.Roman(desk.Draft.Tier)}) [{string.Join(", ", desk.Loadout.Worn.Skip(1).Select(i => $"{i.Catalogue} Mk{i.Mk}"))}]";

    private static string Account(IBattleAccount account, string chassis) =>
        $"{account.Credits} Cr, {account.FreeXp} free XP, {account.Xp(chassis)} XP on {chassis}, researched [{string.Join(", ", WiredCatalogue.Chassis.Where(c => account.Researched(c.Id)).Select(c => c.Id))}], " +
        $"owned [{string.Join(", ", WiredCatalogue.Chassis.Where(c => account.Owned(c.Id)).Select(c => c.Id))}]";

    // ---- the battle -------------------------------------------------------------------------------

    /// <summary>The battle, frame by frame, until its end screen is up (true) or the match is gone (false).</summary>
    private async Task<bool> Fight()
    {
        if (await Until(() => Scene is Game found && found.IsNodeReady() && found.World.Battle is not null ? found : null, 180) is not { } game)
        {
            Say("no battle came");
            return false;
        }
        _game = game;
        var world = game.World;
        var run = world.Battle!;
        LetGo();
        _brain = new BattleBot(world, Game.Player, _orders.Add);
        (_thinkAt, _target, _place, _dash, _ability, _stand) = (0, null, null, null, null, false);
        _way = [];
        game.Ticked += () => Ticked(game);
        Say($"battle on {MatchRequest.Current.Scenario}: player {Game.Player}, tier {run.Options.Tier}, {run.Options.Lives} lives, {run.Options.Win}, " +
            $"teams [{string.Join(", ", run.Teams[0])}] against [{string.Join(", ", run.Teams[1])}], heroes " +
            string.Join("; ", run.Options.Heroes.Select(h => $"{h.Race} {h.Name} {h.Chassis}{(h.Bot ? " (bot)" : "")} [{string.Join(", ", h.Items.Select(i => i.Catalogue))}]")));
        await Until(() => !IsInstanceValid(game) || !game.Warming && !game.Hidden && world.Tick > 0, 180);
        while (true)
        {
            await Frames(1);
            if (!IsInstanceValid(game) || Scene != game)
            {
                LetGo();
                return false;
            }
            await Pictures();
            if (BattleEndScreen.Current is not null)
            {
                LetGo();
                return true;
            }
            await Moments(game, run);
            if (run.Over || game.MatchOver || game.MenuShown is not null)
            {
                LetGo();
                continue;
            }
            if (game.HeroDown || world.HeroOf(Game.Player) is null)
            {
                LetGo();
                continue;
            }
            if (game.Driven is null)
            {
                LetGo();
                await GetIn(game);
                continue;
            }
            if (run.InOpening(world.Time))
            {
                Idle(game);
                continue;
            }
            await Pilot(game);
        }
    }

    /// <summary>The pictures of the battle's moments, each once (a fall each time).</summary>
    private async Task Moments(Game game, BattleRun run)
    {
        var world = game.World;
        var now = Time.GetTicksMsec() / 1000.0;
        if (run.OpeningLeft(world.Time) is > 0 and <= 6)
            await Once("opening");
        if (!run.Over && (run.Progress(0) >= 15 || run.Progress(1) >= 15))
            await Once("capture");
        // In the middle of the fight: the sight on an enemy with the triggers held for a second, or five minutes in.
        var sighted = game.Driven is not null && game.CrosshairSight == Crosshair.Sight.Enemy && (_target is not null || _stand);
        _sightedSince = sighted ? _sightedSince < 0 ? now : _sightedSince : -1;
        if (!run.Over && (_sightedSince >= 0 && now - _sightedSince > 1 || run.Elapsed > 300 && game.Driven is not null))
            await Once("fight");
        _downSince = game.HeroDown && world.HeroOf(Game.Player) is null && !run.Over ? _downSince < 0 ? now : _downSince : -1;
        if (_downSince >= 0 && now - _downSince > 1.5 && !_taken.Contains($"down-{_downs}"))
        {
            _taken.Add($"down-{_downs}");
            await Moment("down", $"down-{_downs}");
        }
        _watchSince = game.WatchedName is not null && !run.Over && world.HeroOf(Game.Player) is null ? _watchSince < 0 ? now : _watchSince : -1;
        if (_watchSince >= 0 && now - _watchSince > 2.5)
            await Once("watching");
        _overSince = run.Over ? _overSince < 0 ? now : _overSince : -1;
        if (_overSince >= 0 && now - _overSince > 1.2)
            await Once("ended");
    }

    private async Task Once(string moment)
    {
        if (_taken.Add(moment))
            await Moment(moment);
    }

    /// <summary>After every tick: the world's state now and then, and what happened in the battle.</summary>
    private void Ticked(Game game)
    {
        Report(game);
        var world = game.World;
        if (course.Pictures.Shots.Contains($"t{world.Tick}"))
            Queue($"t{world.Tick}", $"t{world.Tick}");
        foreach (var e in world.Events)
        {
            switch (e.Kind)
            {
                case GameEventKind.BattleHeroKilled:
                    Say($"tick {world.Tick}: {e.Detail}'s hero fell{(e.Other is { } by ? $" to {(Race)by}" : "")}");
                    break;
                case GameEventKind.WiredHeroDied when e.Other == (int)Game.Player:
                    _downs++;
                    Say($"tick {world.Tick}: the player's hero fell, {e.Detail} spare bodies left");
                    break;
                case GameEventKind.WiredHeroRespawned when e.Other == (int)Game.Player:
                    Say($"tick {world.Tick}: the player's hero stands at the pod again, {e.Detail} spare bodies left");
                    break;
                case GameEventKind.BattleCaptureProgress:
                    Say($"tick {world.Tick}: team {e.Other} takes the pod: {e.Detail}");
                    break;
                case GameEventKind.BattleCaptureReset when e.Other == (int)Game.Player:
                    Say($"tick {world.Tick}: the player's share of the capture is lost ({e.Detail})");
                    break;
                case GameEventKind.WiredAbilityUsed when world.Find<Machine>(e.Actor)?.Race == Game.Player:
                    Say($"tick {world.Tick}: ability {e.Detail} went off");
                    break;
                case GameEventKind.WiredDashed when world.Find<Machine>(e.Actor)?.Race == Game.Player:
                    Say($"tick {world.Tick}: the thrusters fired");
                    break;
                case GameEventKind.BattleEnded:
                    Say(EndLine(world, e));
                    break;
            }
        }
    }

    /// <summary>The battle's end with the world's checksum at its tick, the same over a recording.</summary>
    private static string EndLine(GameWorld world, GameEvent ended) =>
        $"tick {world.Tick}: the battle is over: {ended.Detail}, winner {(ended.Other is { } team ? $"team {team}" : "none")}, checksum {world.Checksum():x16}";

    /// <summary>
    /// The world's state every 100 ticks, read from the world alone so that a recording prints the same: tick, checksum,
    /// clock, both teams' capture and every hero. Once the battle is over the match goes on for a few seconds that the
    /// recording, which ends with the battle, does not hold: those ticks are left out (the end's tick is told by BattleEnded).
    /// </summary>
    private static void Report(Game game)
    {
        var world = game.World;
        if (world.Tick % 100 != 0 || world.Battle is not { Over: false } run)
            return;
        string Hero(Race race)
        {
            var state = world.Wired?.Heroes.GetValueOrDefault(race);
            var hero = world.HeroOf(race);
            var tally = run.Tally.GetValueOrDefault(race);
            return $"{race} {(hero is null ? "down" : $"{hero.HitPoints}/{hero.MaxHitPoints}")} bodies {state?.SpareBodies} dmg {tally?.Damage} kills {tally?.Kills}";
        }
        Say($"tick {world.Tick} checksum {world.Checksum():x16} clock {run.TimeLeft:0} capture {run.Progress(0):0}/{run.Progress(1):0} " +
            $"[{string.Join("; ", run.Teams[0].Select(Hero))}] vs [{string.Join("; ", run.Teams[1].Select(Hero))}]");
    }

    // ---- the hands --------------------------------------------------------------------------------

    /// <summary>One frame of driving: what the bot asked for last, by the keys and the mouse.</summary>
    private async Task Pilot(Game game)
    {
        var world = game.World;
        var hero = game.Driven!;
        if (world.Tick >= _thinkAt)
            Think(world);
        if (_dash is { } dash)
        {
            _dash = null;
            await Dash(game, dash.Way, dash.Why);
            return;
        }
        if (_ability is { } ability)
        {
            _ability = null;
            await UseAbility(game, hero, ability.Slot, ability.Aim);
            return;
        }
        if (_target is { } id && world.Find<Machine>(id) is { IsDead: false } target)
            Attack(game, hero, target);
        else if (_place is { } place)
        {
            Fire(game, hero, 0, false);
            GoTo(game, hero, place, 2);
        }
        else if (_stand && StandFiring(game, hero))
            return;
        else if (_stand && PodBeside(world, hero) is { } pod)
            LookOut(game, hero, pod);
        else
            Idle(game);
    }

    /// <summary>A pod whose circle the hero stands in, its own or the enemy's, or null.</summary>
    private static Construction? PodBeside(GameWorld world, Machine hero) =>
        new[] { world.BattlePod(0), world.BattlePod(1) }.OfType<Construction>()
            .FirstOrDefault(pod => NVector2.Distance(pod.Position, hero.Position) <= BattleBalance.CaptureRadius + 4);

    /// <summary>
    /// Standing its ground by a pod with no foe in reach: the hull and the view turned away from the pod, the way its
    /// enemies come from, and not at its wall.
    /// </summary>
    private void LookOut(Game game, Machine hero, Construction pod)
    {
        Fire(game, hero, 0, false);
        Stop(hero);
        var heading = Heading(pod.Position, hero.Position);
        TurnHull(hero, heading);
        Look(game, heading, -0.05f);
    }

    /// <summary>The bot's look at the world, its commands caught and kept as what the driver is to do.</summary>
    private void Think(GameWorld world)
    {
        _thinkAt = world.Tick + BattleBot.ThinkEvery;
        _orders.Clear();
        _brain.Think();
        (_target, _place, _stand) = (null, null, false);
        foreach (var order in _orders)
        {
            switch (order)
            {
                case AttackCommand attack:
                    _target = attack.Target;
                    break;
                case MoveCommand move:
                    _place = move.Target;
                    break;
                case StandGroundCommand:
                    _stand = true;
                    break;
                case WiredDashCommand dash:
                    _dash = (dash.Direction, "the bot's");
                    break;
                case WiredAbilityCommand ability:
                    _ability = (ability.Slot, ability.Aim);
                    break;
                case DefConCommand:
                    // A hero in the hands answers to them, not to its DefCon.
                    break;
                default:
                    Say($"the bot asked in the field for {order}");
                    break;
            }
        }
    }

    /// <summary>Standing its ground: the sight on the nearest enemy hero in reach and the triggers held; false with none in reach.</summary>
    private bool StandFiring(Game game, Machine hero)
    {
        var world = game.World;
        var reach = game.GunsOf(hero).Where(g => g.Weapon is not null).Select(g => g.Weapon!.Spec.Range).DefaultIfEmpty(0).Max();
        var foe = world.Battle!.Teams.SelectMany(t => t).Where(r => world.AreEnemies(Game.Player, r)).Select(world.HeroOf).OfType<Machine>()
            .Where(m => m.Inside is null && NVector2.Distance(m.Position, hero.Position) <= reach)
            .OrderBy(m => NVector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).FirstOrDefault();
        if (foe is null)
            return false;
        Stop(hero);
        TurnHull(hero, Heading(hero.Position, foe.Position));
        if (game.CentreOf(foe.Id) is { } aim)
            LookAt(game, aim);
        Fire(game, hero, NVector2.Distance(hero.Position, foe.Position), game.CrosshairSight == Crosshair.Sight.Enemy);
        return true;
    }

    // ---- the end ----------------------------------------------------------------------------------

    /// <summary>The end screen: its figures checked against the rewards, its button, and the account in the pod after it.</summary>
    private async Task End()
    {
        var page = BattleEndScreen.Current!;
        for (var waited = 0.0; !page.Done && waited < 40; waited += 0.1)
            await Wait(0.1);
        await Wait(1.5);
        await Moment("end");
        Say($"end screen: {page.Figures}");
        var run = _game!.World.Battle!;
        var reward = BattleRewards.Of(run, Game.Player);
        var shown = page.Shown;
        var same = shown.Xp == reward.Xp && shown.FreeXp == reward.FreeXp && shown.Credits == reward.Credits;
        Say($"end check: shown {shown.Xp} XP, {shown.FreeXp} free XP, {shown.Credits} Cr; BattleRewards.Of gives {reward.Xp} XP, {reward.FreeXp} free XP, " +
            $"{reward.Credits} Cr (salvage {reward.Salvage}, won {reward.Won}): {(same ? "equal" : "DIFFERENT")}");
        if (Replays().FirstOrDefault() is { } replay)
            Say($"replay {replay}");
        var word = course.End == "pod" ? "Pod" : "Menu";
        if (page.ButtonOf(Lang.T(word).ToUpperInvariant()) is not { } button)
        {
            Say($"no button {word}");
            return;
        }
        await Click(Middle(button));
        Say($"pressed {word}");
        var after = await Until(() => (Node?)Find<MainMenuPage>() ?? (Find<HangarPage>() is { Desk: BattleDesk } pod ? pod : null), 60);
        await Wait(2.5);
        await Moment("after");
        var account = BattleAccounts.Load();
        Say($"after the battle: {after?.GetType().Name ?? "nothing"}; account {Account(account, _before?.Chassis ?? WiredBalance.StartChassis)}");
        if (_before is { } before)
        {
            var (xp, free, credits) = (account.Xp(before.Chassis) - before.Xp, account.FreeXp - before.FreeXp, account.Credits - before.Credits);
            var counted = xp == reward.Xp && free == reward.FreeXp && credits == reward.Credits;
            Say($"account check: +{xp} XP on {before.Chassis}, +{free} free XP, +{credits} Cr against the reward: {(counted ? "equal" : "DIFFERENT")}");
        }
    }

    // ---- a recording ------------------------------------------------------------------------------

    /// <summary>A recording opened from the menu; only the world's state is printed, at the same ticks as when it was played.</summary>
    private async Task Recording()
    {
        var path = System.Environment.GetEnvironmentVariable("MACHINES_REPLAY")!;
        await Until(() => Scene is Game || Find<MainMenuPage>() is not null ? this : null, 300);
        if (Scene is not Game)
        {
            await Wait(1);
            if (FilesPage.OpenReplay(GetTree(), path) is { } error)
                throw new InvalidOperationException($"the recording does not open: {error}");
        }
        var game = await Until(() => Scene is Game found && found.IsNodeReady() && found.World.Battle is not null ? found : null, 180)
            ?? throw new InvalidOperationException("no battle came from the recording");
        var ticks = MatchRequest.Current.Replay!.Ticks;
        Say($"watching a recording of {ticks} ticks: {path}");
        game.Ticked += () =>
        {
            Report(game);
            foreach (var e in game.World.Events.Where(e => e.Kind == GameEventKind.BattleEnded))
                Say(EndLine(game.World, e));
        };
        await Until(() => !IsInstanceValid(game) || game.World.Tick >= ticks, 7200);
        await Wait(1);
        if (IsInstanceValid(game))
            Say($"recording over: tick {game.World.Tick} checksum {game.World.Checksum():x16}");
    }
}
