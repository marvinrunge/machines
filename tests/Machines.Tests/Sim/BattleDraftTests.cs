using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The hero put together in the pod: what the tree shows and why it is locked, what may go on which tier at which Mk, that
/// owned items go on any number of slots, and the battle against bots its choices make.
/// </summary>
public class BattleDraftTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly WiredSlot Right = WiredSlot.Weapon(Mounting.Right);

    private static readonly WiredSlot Left = WiredSlot.Weapon(Mounting.Left);

    private static (BattleDraft Draft, LocalBattleAccount Account) NewDraft(Action<BattleProfile>? setup = null)
    {
        var profile = new WiredProfile();
        setup?.Invoke(profile.Battle);
        var account = new LocalBattleAccount(profile);
        return (new BattleDraft(Data, account), account);
    }

    private static void Owns(BattleProfile book, string id, int mk = 1)
    {
        book.Owned[id] = mk;
        if (WiredCatalogue.FindChassis(id) is not null)
            book.Researched.Add(id);
    }

    private static string[] Worn(BattleDraft draft) => draft.Loadout.Worn.Skip(1).Select(i => $"{i.Catalogue}/{i.Mk}").ToArray();

    [Fact]
    public void A_new_hero_is_the_first_grunt_with_its_bolter_at_tier_one()
    {
        var (draft, _) = NewDraft();

        Assert.Equal(WiredBalance.StartChassis, draft.Chassis);
        Assert.Equal(1, draft.Tier);
        Assert.Equal(["BOLTER/1"], Worn(draft));
        var hero = draft.Hero(Race.Blue);
        Assert.Equal(Race.Blue, hero.Race);
        Assert.False(hero.Bot);
        Assert.Equal(new BattleItem("BOLTER", Right, 1), hero.Items.Single());
        Assert.Null(draft.WhyNotFight());
    }

    [Fact]
    public void The_tree_shows_each_chassis_its_tier_its_cost_and_why_it_is_locked()
    {
        var (draft, account) = NewDraft(book =>
        {
            book.Xp[WiredBalance.StartChassis] = 200;
            book.Credits = 1000;
        });

        var tree = draft.Tree().ToDictionary(n => n.Chassis);
        Assert.Equal(WiredCatalogue.Chassis.Count, tree.Count);
        Assert.Equal(BattleStage.Worn, tree["grunt1"].Stage);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 7, 8 }, new[] { "grunt1", "grunt2", "reaper2", "reaper3", "knight3", "knight4", "ballista4", "boss2", "gorilla5" }.Select(id => tree[id].Tier));
        // The constructors and spies too: the tier is the row of the shop's tree plus one.
        Assert.Equal(new[] { 3, 4, 6, 8, 4, 6, 8 }, new[] { "dozer1", "builder3", "behemoth4", "titan6", "spy3", "spy5", "phantom6" }.Select(id => tree[id].Tier));
        // The locators: the crab a start beside Grunt 1, the glider on the second row.
        Assert.Equal(new[] { 1, 2 }, new[] { "geo1", "geo3" }.Select(id => tree[id].Tier));
        Assert.All(WiredCatalogue.Chassis, c => Assert.InRange(tree[c.Id].Tier, 1, BattleCatalogue.MostTier));

        var grunt2 = tree["grunt2"];
        Assert.Equal(BattleStage.Short, grunt2.Stage);
        Assert.Equal(330, grunt2.Cost);
        Assert.Equal("Needs {0} XP on {1}", grunt2.Lock!.Words);

        var reaper2 = tree["reaper2"];
        Assert.Equal(BattleStage.Closed, reaper2.Stage);
        Assert.Equal("Research {0} first", reaper2.Lock!.Words);
        Assert.Equal(new[] { "grunt2" }, reaper2.Lock.Args.Single());

        account.Note(new BattleNote(WiredBalance.StartChassis, 130, 0, 0));
        Assert.Equal(BattleStage.Researchable, draft.Node("grunt2").Stage);
        Assert.Null(draft.Node("grunt2").Lock);

        Assert.Null(draft.Research("grunt2"));
        var dear = draft.Node("grunt2");
        Assert.Equal(BattleStage.TooDear, dear.Stage);
        Assert.Equal(1100, dear.Cost);
        Assert.Equal("Needs {0} Cr", dear.Lock!.Words);
        Assert.Equal(BattleStage.Short, draft.Node("reaper2").Stage);

        account.Note(new BattleNote(WiredBalance.StartChassis, 0, 0, 100));
        Assert.Equal(BattleStage.ForSale, draft.Node("grunt2").Stage);
        Assert.Null(draft.Buy("grunt2"));
        Assert.Equal(BattleStage.Owned, draft.Node("grunt2").Stage);
        Assert.Equal(0, account.Credits);
    }

    [Fact]
    public void Only_an_owned_chassis_can_be_worn_and_what_is_worn_moves_with_the_mk_its_tier_allows()
    {
        var (draft, _) = NewDraft(book =>
        {
            Owns(book, "grunt2");
            Owns(book, "reaper3");
            Owns(book, "AUTO_CANNON", 2);
            book.Researched.Add("reaper2");
        });

        Assert.Equal("Buy it first", draft.WhyNotWear("reaper2")!.Words);
        Assert.Equal("Research {0} first", draft.WhyNotWear("knight3")!.Words);

        Assert.Null(draft.Put("AUTO_CANNON", Right));
        Assert.Equal(["AUTO_CANNON/1"], Worn(draft));

        Assert.Null(draft.Wear("reaper3"));
        Assert.Equal(4, draft.Tier);
        Assert.Equal(["AUTO_CANNON/2"], Worn(draft));
        Assert.Equal(2, draft.Hero(Race.Red).Items.Single().Mk);

        Assert.Null(draft.Wear("grunt2"));
        Assert.Equal(["AUTO_CANNON/1"], Worn(draft));
        Assert.Equal(new BattleItem("AUTO_CANNON", Left, 1), draft.Items.Single());
    }

    [Fact]
    public void An_owned_item_goes_on_any_number_of_slots_and_what_is_not_owned_on_none()
    {
        var (draft, _) = NewDraft(book => Owns(book, "grunt3"));
        Assert.Null(draft.Wear("grunt3"));

        Assert.Null(draft.Put("BOLTER", Left));
        // The right slot carries the bolter already: without replacing it, that would be a second copy (a battery).
        Assert.Null(draft.Put("BOLTER", Right, replace: true));
        Assert.Equal(["BOLTER/1", "BOLTER/1"], Worn(draft));

        Assert.Equal("you do not own that", draft.Put("AUTO_CANNON", Left)!.Words);
        Assert.Null(draft.WhyNotPut("AUTO_CANNON", Left, buying: true));

        var made = draft.Loadout.Weapon(Mounting.Left)!.Id;
        draft.TakeOff(made);
        Assert.Equal(["BOLTER/1"], Worn(draft));
        Assert.Single(draft.Items);
    }

    [Fact]
    public void Moving_an_item_onto_a_taken_slot_takes_off_what_sat_there()
    {
        var (draft, _) = NewDraft(book =>
        {
            Owns(book, "grunt3");
            Owns(book, "AUTO_CANNON");
        });
        draft.Wear("grunt3");
        draft.Put("AUTO_CANNON", Left);
        var bolter = draft.Loadout.Weapon(Mounting.Right)!.Id;

        Assert.Null(draft.Move(bolter, Left));

        Assert.Null(draft.Loadout.Weapon(Mounting.Right));
        Assert.Equal("BOLTER", draft.Loadout.Weapon(Mounting.Left)!.Catalogue);
        Assert.Equal(["BOLTER/1"], Worn(draft));
    }

    [Fact]
    public void Abilities_go_on_from_tier_three_and_treachery_is_not_offered()
    {
        var (draft, _) = NewDraft(book =>
        {
            Owns(book, "discharge");
            Owns(book, "treachery");
            Owns(book, "grunt3");
        });

        var why = draft.WhyNotPut("discharge", WiredSlot.Ability(0));
        Assert.Equal("From tier {0}", why!.Words);
        Assert.Equal("III", why.Args.Single());
        Assert.DoesNotContain(draft.Offers(WiredKind.Ability), o => o.Item == "treachery");
        Assert.Equal("that is not offered in a battle", draft.WhyNotPut("treachery", WiredSlot.Ability(0))!.Words);

        draft.Wear("grunt3");
        Assert.Null(draft.Put("discharge", WiredSlot.Ability(0)));
        Assert.Equal("that is not offered in a battle", draft.Put("treachery", WiredSlot.Ability(1))!.Words);
        Assert.Contains("discharge/1", Worn(draft));
    }

    [Fact]
    public void A_higher_mk_is_sold_for_the_tier_that_takes_it()
    {
        var (draft, account) = NewDraft(book =>
        {
            book.Credits = 10_000;
            Owns(book, "reaper3");
        });

        var bolter = draft.Offers(WiredKind.Weapon).First(o => o.Item == "BOLTER");
        Assert.Equal(1, bolter.Mk);
        Assert.Null(bolter.Upgrade);
        Assert.Equal("Mk {0} from tier {1}", bolter.Note!.Words);
        var refused = draft.Buy("BOLTER", 2);
        Assert.Equal("Mk {0} from tier {1}", refused!.Words);
        Assert.Equal(new object[] { 2, "IV" }, refused.Args);
        Assert.Equal(1, account.Mk("BOLTER"));

        draft.Wear("reaper3");
        bolter = draft.Offers(WiredKind.Weapon).First(o => o.Item == "BOLTER");
        Assert.Equal(BattleRules.Price("BOLTER", 2), bolter.Upgrade);
        Assert.Null(draft.Buy("BOLTER", 2));
        Assert.Equal(2, draft.MkFor("BOLTER"));
        Assert.Contains("BOLTER/2", Worn(draft));
    }

    [Fact]
    public void The_shelves_list_what_is_owned_first_then_what_is_on_sale_then_what_this_tier_cannot_take()
    {
        var (draft, _) = NewDraft(book => Owns(book, "plates"));

        var modules = draft.Offers(WiredKind.Module).ToList();
        Assert.Equal("plates", modules[0].Item);
        Assert.Equal(1, modules[0].Mk);
        Assert.All(modules.Skip(1), o => Assert.Equal(0, o.Mk));
        Assert.Equal(BattleRules.Price("frame"), modules.First(o => o.Item == "frame").Price);

        var abilities = draft.Offers(WiredKind.Ability).ToList();
        Assert.All(abilities, o => Assert.Equal("From tier {0}", o.Lock!.Words));
        Assert.Equal(WiredCatalogue.Weapons.Count, draft.Offers(WiredKind.Weapon).Count());
    }

    [Fact]
    public void Too_much_on_a_small_chassis_is_refused()
    {
        var (draft, _) = NewDraft(book => Owns(book, "MULTI_LAUNCHER2"));

        Assert.Equal("the weapon is too big for this slot", draft.Put("MULTI_LAUNCHER2", Right)!.Words);
        Assert.Equal(["BOLTER/1"], Worn(draft));
    }

    [Fact]
    public void The_pod_takes_a_plate_on_every_module_slot_only_as_far_as_the_battle_takes_the_hero_as_it_is()
    {
        // The heaviest owned weapons and an owned plate on every module slot, as a player could drag them: whatever the pod
        // lets on is a hero the battle takes as it is (BattleBots.WhyNot), so no hero comes into a fight clamped to a bot's
        // loadout; what would go over the most load is refused up front.
        foreach (var chassis in BattleCatalogue.Tiers.Keys)
        {
            var (draft, _) = NewDraft(book =>
            {
                Owns(book, chassis);
                foreach (var weapon in WiredCatalogue.Weapons.Where(w => !w.GliderOnly))
                    Owns(book, weapon.Id, WiredBalance.MostMk);
                Owns(book, "plates", 3);
            });
            Assert.Null(draft.Wear(chassis));
            var heaviest = WiredCatalogue.Weapons.Where(w => !w.GliderOnly).OrderByDescending(w => w.Load).ThenBy(w => w.Id, StringComparer.Ordinal).ToList();
            foreach (var slot in WiredCatalogue.FindChassis(chassis)!.Slots.Where(s => s.Fixed is null).Select(s => WiredSlot.Weapon(s.Mounting)))
                heaviest.FirstOrDefault(w => draft.Put(w.Id, slot) is null);
            var refused = 0;
            for (var at = 0; at < WiredCatalogue.FindChassis(chassis)!.Modules; at++)
            {
                if (draft.Put("plates", new WiredSlot(WiredSlotKind.Module, at)) is { } why)
                {
                    Assert.Equal("too heavy for this chassis", why.Words);
                    refused++;
                }
                Assert.Null(draft.WhyNotFight());
                Assert.True(HeroBuild.Resolve(Data, draft.Loadout).LoadShare <= WiredBalance.MostLoad, chassis);
                var hero = draft.Hero(Race.Red);
                Assert.Same(hero, BattleBots.Resolved(hero, draft.Tier, 0, draft.Map));
            }
            Assert.True(draft.Loadout.Modules.Count(m => m?.Catalogue == "plates") + refused == WiredCatalogue.FindChassis(chassis)!.Modules, chassis);
        }
    }

    [Fact]
    public void The_last_hero_comes_back_as_far_as_it_is_still_owned()
    {
        var profile = new WiredProfile();
        Owns(profile.Battle, "grunt2");
        Owns(profile.Battle, "FLAME_THROWER2");
        profile.Battle.LastHero = new BattleHeroSheet
        {
            Name = "  A name that is much longer than any hero's  ",
            Colour = Race.Yellow,
            Chassis = "grunt2",
            Items = [new BattleItem("FLAME_THROWER2", Left), new BattleItem("AUTO_CANNON", Right)],
            Map = "micey",
            Lives = 3,
            Win = BattleWin.Wipe,
        };

        var draft = new BattleDraft(Data, new LocalBattleAccount(profile));

        Assert.Equal("grunt2", draft.Chassis);
        Assert.Equal(["FLAME_THROWER2/1"], Worn(draft));
        Assert.Equal(Race.Yellow, draft.Colour);
        Assert.Equal("micey", draft.Map);
        Assert.Equal(3, draft.Lives);
        Assert.Equal(BattleWin.Wipe, draft.Win);
        Assert.True(draft.Name.Length <= BattleHero.MostName);

        profile.Battle.Owned.Remove("grunt2");
        var fallen = new BattleDraft(Data, new LocalBattleAccount(profile));
        Assert.Equal(WiredBalance.StartChassis, fallen.Chassis);
        Assert.Equal(["BOLTER/1"], Worn(fallen));
    }

    [Fact]
    public void Keeping_the_draft_remembers_the_hero_and_the_battle()
    {
        var (draft, account) = NewDraft(book => Owns(book, "grunt2"));
        draft.Wear("grunt2");
        draft.Name = "Kai";
        draft.Colour = Race.Blue;
        draft.Map = "m_green";
        draft.Map = "no_such_map";
        draft.Lives = 9;
        draft.Win = BattleWin.Capture;

        Assert.True(draft.Keep());

        var sheet = account.LastHero!;
        Assert.Equal("Kai", sheet.Name);
        Assert.Equal("grunt2", sheet.Chassis);
        Assert.Equal("m_green", sheet.Map);
        Assert.Equal(5, sheet.Lives);
        Assert.Equal(BattleWin.Capture, sheet.Win);
        Assert.Equal(draft.Items, sheet.Items);
        Assert.Equal("Kai", sheet.Hero(Race.Green).Name);
    }

    [Fact]
    public void Against_bots_the_player_fights_with_three_bots_mirrored_in_the_bracket_of_the_worn_chassis()
    {
        var (draft, _) = NewDraft(book => Owns(book, "reaper3"));
        draft.Wear("reaper3");
        draft.Colour = Race.Green;
        draft.Map = "m_desert";
        draft.Lives = 2;
        draft.Win = BattleWin.Wipe;

        var options = draft.Options();

        var map = BattleCatalogue.Map("m_desert")!;
        Assert.Equal(4, options.Tier);
        Assert.Equal(2, options.Lives);
        Assert.Equal(BattleWin.Wipe, options.Win);
        Assert.Equal(map.TeamA, options.TeamA);
        Assert.Equal(map.TeamB, options.TeamB);
        Assert.Equal(new[] { Race.Red, Race.Green, Race.Blue, Race.Yellow }.Order(), options.Heroes.Select(h => h.Race));
        var mine = options.Heroes.Single(h => h.Race == Race.Green);
        Assert.False(mine.Bot);
        Assert.Equal("reaper3", mine.Chassis);
        Assert.Equal(draft.Items, mine.Items);
        Assert.All(options.Heroes.Where(h => h.Race != Race.Green), h => Assert.True(h.Bot));
        // Green is the first of team B: the first of team A mirrors its tier.
        Assert.Equal(4, BattleCatalogue.Tier(options.Heroes.Single(h => h.Race == Race.Red).Chassis));
        Assert.All(options.Heroes, h => Assert.Null(BattleBots.WhyNot(h, options.Tier)));
        Assert.Equal(Race.Green, BattleDraft.PlayerRace(map, draft.Colour));
    }

    [Fact]
    public void Watching_lets_bots_play_all_four_and_the_battle_sets_up()
    {
        var (draft, _) = NewDraft();
        draft.Map = "fortmult";

        var options = draft.Options(watch: true);

        Assert.Equal(4, options.Heroes.Count);
        Assert.All(options.Heroes, h => Assert.True(h.Bot));
        Assert.All(options.Heroes, h => Assert.Equal(1, BattleCatalogue.Tier(h.Chassis)));

        var world = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller>(), 3,
            new MatchOptions { Battle = BattleDraft.Compose(BattleCatalogue.Map("m_desert")!, 2, 1, BattleWin.Both, draft.Hero(Race.Red)) });
        Assert.NotNull(world.Battle);
        Assert.Equal(4, world.Wired!.Heroes.Count);
        Assert.Equal("BOLTER", world.Wired.Heroes[Race.Red].Loadout.Weapon(Mounting.Right)!.Catalogue);
    }

    [Fact]
    public void An_unnamed_hero_fights_under_the_name_given_and_a_named_one_keeps_its_own()
    {
        var (draft, _) = NewDraft();
        draft.Map = "m_desert";
        Assert.Equal("", draft.Hero(Race.Red).Name);

        Assert.Equal("Pilot", draft.Options(unnamed: "  Pilot ").Heroes.Single(h => !h.Bot).Name);
        Assert.Equal("", draft.Options().Heroes.Single(h => !h.Bot).Name);
        draft.Name = "Rivet";
        Assert.Equal("Rivet", draft.Options(unnamed: "Pilot").Heroes.Single(h => !h.Bot).Name);
    }

    [Fact]
    public void Only_a_chassis_that_leads_nowhere_is_the_top_of_its_line()
    {
        var (draft, _) = NewDraft();
        Assert.True(draft.Leads);
        Assert.Equal("grunt2", draft.Next());

        // Every chassis grunt1 leads to researched: none is next, but it is not the top of a line.
        var (researched, _) = NewDraft(book =>
        {
            foreach (var chassis in WiredCatalogue.Chassis.Where(c => c.Needs.Contains(WiredBalance.StartChassis)))
                book.Researched.Add(chassis.Id);
        });
        Assert.Null(researched.Next());
        Assert.True(researched.Leads);

        var last = WiredCatalogue.Chassis.First(c => !WiredCatalogue.Chassis.Any(o => o.Needs.Contains(c.Id))).Id;
        var (top, _) = NewDraft(book => Owns(book, last));
        Assert.Null(top.Wear(last));
        Assert.False(top.Leads);
        // The end screen asks the same of the chassis fought on.
        Assert.True(BattleDraft.LeadsOn(WiredBalance.StartChassis));
        Assert.False(BattleDraft.LeadsOn(last));
        Assert.Null(top.Next());
    }

    [Fact]
    public void A_new_account_meets_bots_of_the_first_tier_fitted_as_it_is()
    {
        var (draft, _) = NewDraft();
        draft.Map = "fortmult";
        var options = draft.Options();
        var own = options.Heroes.Single(h => !h.Bot);
        string Kit(BattleHero hero) => string.Join(",", hero.Items.Select(i => $"{i.Catalogue}/{i.Mk}"));
        Assert.Equal(WiredBalance.StartChassis, own.Chassis);
        // The first tier's line is Grunt 1 and the crab, which the bots take in turn.
        var line = BattleCatalogue.OfTier(1).Select(c => c.Id).ToList();
        Assert.Equal(["grunt1", "geo1"], line);
        var teams = new[] { options.TeamA.Order().ToList(), options.TeamB.Order().ToList() };
        var bots = options.Heroes.Where(h => h.Bot).ToList();
        Assert.Equal(3, bots.Count);
        Assert.All(bots, bot =>
        {
            // The first of a team on Grunt 1, the second on the crab.
            var k = teams.Select(team => team.IndexOf(bot.Race)).Single(i => i >= 0);
            Assert.Equal(BattleBots.Pick(draft.Map, 1, k), bot.Chassis);
            Assert.Equal(k % 2 == 0 ? "grunt1" : "geo1", bot.Chassis);
            // Each wears the start kit as the player does, the Bolter on its one slot: right on Grunt 1, top on the crab.
            Assert.Equal(Kit(own), Kit(bot));
            Assert.Equal(WiredSlot.Weapon(bot.Chassis == "geo1" ? Mounting.Top : Mounting.Right), bot.Items.Single(i => i.Catalogue == "BOLTER").Slot);
        });
        Assert.Contains(bots, bot => bot.Chassis == "geo1");
        // From the second tier on bots wear modules too.
        Assert.Contains(BattleBots.Loadout("grunt2", 2), i => WiredCatalogue.FindModule(i.Catalogue) is not null);
    }

    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(8, "VIII")]
    [InlineData(12, "12")]
    public void Tiers_are_written_in_roman(int tier, string written) => Assert.Equal(written, BattleDraft.Roman(tier));

    // ---- batteries -----------------------------------------------------------------------------------

    private static string[] Battery(BattleDraft draft, Mounting mounting) => draft.Loadout.Battery(mounting).Select(i => $"{i.Catalogue}/{i.Mk}").ToArray();

    /// <summary>A Reaper 3 with the bolter on its medium right slot and nothing else, owning what <paramref name="setup"/> adds.</summary>
    private static (BattleDraft Draft, LocalBattleAccount Account) OnTheReaper(Action<BattleProfile>? setup = null)
    {
        var (draft, account) = NewDraft(book =>
        {
            Owns(book, "reaper3");
            setup?.Invoke(book);
        });
        Assert.Null(draft.Wear("reaper3"));
        Assert.Equal(["BOLTER/1"], Battery(draft, Mounting.Right));
        return (draft, account);
    }

    [Fact]
    public void An_owned_weapon_put_onto_its_slot_joins_the_battery_for_nothing_up_to_what_the_slot_carries()
    {
        var (draft, account) = OnTheReaper(book => book.Credits = 500);

        Assert.Null(draft.Put("BOLTER", Right));

        Assert.Equal(["BOLTER/1", "BOLTER/1"], Battery(draft, Mounting.Right));
        Assert.Equal(new BattleItem("BOLTER", Right, 1, 2), draft.Items.Single());
        Assert.Equal(500, account.Credits);
        Assert.Null(draft.WhyNotFight());

        Assert.Equal("a medium slot carries two at most", draft.WhyNotPut("BOLTER", Right)!.Words);
        Assert.Equal("a medium slot carries two at most", draft.Put("BOLTER", Right)!.Words);
        Assert.Single(draft.Items);
    }

    [Fact]
    public void Replacing_a_battery_leaves_the_one_put_there_alone()
    {
        var (draft, _) = OnTheReaper(book => Owns(book, "AUTO_CANNON"));
        draft.Put("BOLTER", Right);

        Assert.Null(draft.Put("BOLTER", Right, replace: true));
        Assert.Equal(["BOLTER/1"], Battery(draft, Mounting.Right));

        draft.Put("BOLTER", Right);
        Assert.Null(draft.Put("AUTO_CANNON", Right));
        Assert.Equal(["AUTO_CANNON/1"], Battery(draft, Mounting.Right));
        Assert.Equal(new BattleItem("AUTO_CANNON", Right, 1), draft.Items.Single());
    }

    [Fact]
    public void A_weapon_that_forms_no_battery_is_refused_a_second_time_on_its_slot()
    {
        var (draft, _) = NewDraft(book =>
        {
            Owns(book, "boss2");
            Owns(book, "BEE_BOMB");
        });
        Assert.Null(draft.Wear("boss2"));
        var top = WiredSlot.Weapon(Mounting.Top);
        Assert.Null(draft.Put("BEE_BOMB", top));

        Assert.Equal("that weapon cannot form a battery", draft.Put("BEE_BOMB", top)!.Words);
        Assert.Null(draft.Put("BEE_BOMB", top, replace: true));
        Assert.Equal(["BEE_BOMB/1"], Battery(draft, Mounting.Top));
    }

    [Fact]
    public void Taking_off_takes_one_copy()
    {
        var (draft, _) = OnTheReaper();
        draft.Put("BOLTER", Right);

        draft.TakeOff(draft.Loadout.Weapon(Mounting.Right)!.Id);

        Assert.Equal(["BOLTER/1"], Battery(draft, Mounting.Right));
        Assert.Equal(1, draft.Items.Single().Count);
    }

    [Fact]
    public void A_copy_moves_off_its_battery_alone_and_joins_another_of_its_weapon()
    {
        var (draft, _) = OnTheReaper();
        draft.Put("BOLTER", Right);

        Assert.Null(draft.Move(draft.Loadout.Battery(Mounting.Right)[1].Id, Left));
        Assert.Equal(["BOLTER/1"], Battery(draft, Mounting.Left));
        Assert.Equal(["BOLTER/1"], Battery(draft, Mounting.Right));

        Assert.Null(draft.Move(draft.Loadout.Weapon(Mounting.Left)!.Id, Right));
        Assert.Equal(["BOLTER/1", "BOLTER/1"], Battery(draft, Mounting.Right));
        Assert.Empty(Battery(draft, Mounting.Left));
        Assert.Equal(new BattleItem("BOLTER", Right, 1, 2), draft.Items.Single());
    }

    [Fact]
    public void A_battery_moves_with_its_chassis_cut_to_what_its_new_slot_carries()
    {
        var (draft, _) = OnTheReaper(book => Owns(book, "grunt2"));
        draft.Put("BOLTER", Right);

        // grunt2 has a light left slot only.
        Assert.Null(draft.Wear("grunt2"));

        Assert.Equal(["BOLTER/1"], Battery(draft, Mounting.Left));
        Assert.Equal(new BattleItem("BOLTER", Left, 1), draft.Items.Single());
    }

    [Fact]
    public void The_users_harvester_takes_six_plasma_cannons_at_the_mk_owned_and_not_one_weapon_more()
    {
        var (draft, _) = NewDraft(book =>
        {
            Owns(book, "reaper5");
            Owns(book, "PLASMA_CANNON2", 2);
        });
        Assert.Null(draft.Wear("reaper5"));

        foreach (var side in new[] { Left, Right })
        {
            for (var copy = 0; copy < 3; copy++)
                Assert.Null(draft.Put("PLASMA_CANNON2", side));
        }

        // 6 × 8 load on a chassis that carries 32: exactly the most there may be.
        Assert.Equal(WiredBalance.MostLoad, HeroBuild.Resolve(Data, draft.Loadout).LoadShare);
        Assert.Equal("too heavy for this chassis", draft.Put("BOLTER", WiredSlot.Weapon(Mounting.Top))!.Words);
        Assert.Equal(Enumerable.Repeat("PLASMA_CANNON2/2", 3), Battery(draft, Mounting.Left));
        Assert.Equal(Enumerable.Repeat("PLASMA_CANNON2/2", 3), Battery(draft, Mounting.Right));
        Assert.Equal([new BattleItem("PLASMA_CANNON2", Left, 2, 3), new BattleItem("PLASMA_CANNON2", Right, 2, 3)], draft.Items.OrderBy(i => i.Slot!.Index));
        Assert.Null(draft.WhyNotFight());
    }

    [Fact]
    public void The_last_hero_comes_back_with_its_batteries_cut_to_what_the_slot_carries()
    {
        var profile = new WiredProfile();
        Owns(profile.Battle, "reaper3");
        profile.Battle.LastHero = new BattleHeroSheet
        {
            Chassis = "reaper3",
            Items = [new BattleItem("BOLTER", Right, 1, 9), new BattleItem("BOLTER", Left, 1, 0)],
        };

        var draft = new BattleDraft(Data, new LocalBattleAccount(profile));

        Assert.Equal(["BOLTER/1", "BOLTER/1"], Battery(draft, Mounting.Right));
        Assert.Equal(["BOLTER/1"], Battery(draft, Mounting.Left));
        Assert.Equal([new BattleItem("BOLTER", Right, 1, 2), new BattleItem("BOLTER", Left, 1)], draft.Items);
    }

    [Fact]
    public void Only_a_weapon_on_a_weapon_slot_forms_a_battery_a_module_or_ability_put_onto_its_own_slot_stands_there_alone()
    {
        var profile = new WiredProfile();
        foreach (var id in new[] { "knight4", "plates", "cooler", "discharge" })
            Owns(profile.Battle, id);
        var (first, second, ability) = (WiredSlot.Module(0), WiredSlot.Module(1), WiredSlot.Ability(0));
        // A sheet saved with a counted module comes back with it once.
        profile.Battle.LastHero = new BattleHeroSheet { Chassis = "knight4", Items = [new BattleItem("plates", first, 1, 2)] };
        var draft = new BattleDraft(Data, new LocalBattleAccount(profile));
        Assert.Equal([new BattleItem("plates", first, 1)], draft.Items);

        // Put again onto its own slot it stands there alone, as before batteries, and the hero still fights.
        Assert.Null(draft.Put("discharge", ability));
        Assert.Null(draft.Put("plates", first));
        Assert.Null(draft.Put("discharge", ability));
        Assert.Equal([new BattleItem("plates", first, 1), new BattleItem("discharge", ability, 1)], draft.Items.OrderBy(i => i.Slot!.Kind));
        Assert.Null(draft.WhyNotFight());

        // Each worn item is still its own entry: taking off the cooler takes off the cooler.
        Assert.Null(draft.Put("cooler", second));
        draft.TakeOff(draft.Loadout.At(second)!.Id);
        Assert.Null(draft.Loadout.At(second));
        Assert.Equal("plates", draft.Loadout.At(first)?.Catalogue);

        // A worn plate moved onto the slot of another plate takes its place.
        Assert.Null(draft.Put("plates", second));
        Assert.Null(draft.Move(draft.Loadout.At(second)!.Id, first));
        Assert.Equal("plates", draft.Loadout.At(first)?.Catalogue);
        Assert.Null(draft.Loadout.At(second));
        Assert.Single(draft.Items, i => i.Catalogue == "plates");
        Assert.All(draft.Items, i => Assert.Equal(1, i.Count));
        Assert.Null(draft.WhyNotFight());
    }
}
