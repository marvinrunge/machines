using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// For automated checks of a run in the match. What a look at the fifth wave or a boss would take hours of play for, a script
// sets up before the real wave comes: only MACHINES_WIRED_SCENE=<folder>,<scene> does any of this.
public partial class Game
{
    private bool _sceneScripted, _sceneHolds;

    // Which of the scenes joined by + this run plays: the next run, by the end screen's button, plays the next.
    private static int _sceneRun;

    /// <summary>
    /// Plays a scene of a run through and prints its figures, with pictures in the folder: hud, boss, twins, night, ion, pickup,
    /// down, intermission, refit, load, groundload, refused, triggers, squad, keys, specials, towers, leave, bay. The last seven press what a player presses,
    /// through Input. The hero wears full gear and, unless the scene is about its death, does not fall. losepod, losebody, plainend,
    /// unlock and unlock20 end the run and leave the game open for its end screen, plainsave saves it half way and quits; several
    /// joined by + go with the runs one after the other.
    /// The world starts different by it, so that its saves and recordings play back only with the same scene.
    /// </summary>
    private async void ScriptRun()
    {
        if (_sceneHolds && World.HeroOf(Player) is { } kept)
            kept.HitPoints = kept.MaxHitPoints;
        if (_sceneScripted || Watching || World.Wired is not { } run || System.Environment.GetEnvironmentVariable("MACHINES_WIRED_SCENE") is not { Length: > 0 } scene)
            return;
        _sceneScripted = true;
        var words = scene.Split(',');
        var folder = words[0];
        if (_scriptLoaded)
        {
            await SceneLoaded(folder);
            GetTree().Quit();
            return;
        }
        if (MatchRequest.Current.Saved is not null)
            GD.Print($"scene: a saved run goes on: {RunReport}");
        var scenes = words[1].Split('+');
        words[1] = scenes[Math.Min(_sceneRun++, scenes.Length - 1)];
        var ends = words[1] is "losepod" or "losebody" or "unlock" or "unlock20" or "plainend" or "plainsave";
        _sceneHolds = words[1] != "down" && !ends;
        await Seconds(1);
        GD.Print($"scene: {words[1]} begins: {RunReport}");
        await (words[1] switch
        {
            "hud" => SceneHud(folder, run),
            "boss" => SceneBoss(folder, run, 10, "run-boss"),
            "twins" => SceneBoss(folder, run, 25, "run-twins"),
            "night" => SceneNight(folder, run),
            "ion" => SceneIon(folder, run),
            "pickup" => ScenePickup(folder, run),
            "down" => SceneDown(folder, run),
            "intermission" => SceneIntermission(folder, run),
            "refit" => SceneRefit(folder, run),
            "load" => SceneLoad(folder, run),
            "groundload" => SceneLoad(folder, run, ground: true),
            "triggers" => SceneTriggers(folder, run),
            "squad" => SceneSquad(folder, run),
            "keys" => SceneKeys(folder, run),
            "specials" => SceneSpecials(folder, run),
            "towers" => SceneTowers(folder, run),
            "leave" => SceneLeave(folder, run),
            "bay" => SceneBay(folder, run),
            "refused" => SceneRefused(folder, run),
            "stray" => SceneStray(folder, run),
            "podhit" => ScenePodHit(folder, run),
            "losepod" => SceneLosePod(folder, run),
            "losebody" => SceneLoseBody(folder, run),
            "unlock" => SceneUnlock(folder, run, 10),
            "unlock20" => SceneUnlock(folder, run, 20),
            "plainend" => ScenePlain(folder, run),
            "plainsave" => ScenePlainSave(folder, run),
            _ => Task.CompletedTask,
        });
        if (!_scriptLoaded && !ends)
            GetTree().Quit();
    }

    private void Snap(string folder, string name)
    {
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, name + ".png"));
        GD.Print($"scene: {name}: {RunReport}");
        if (WiredReadout.Of(World, Player) is { } now)
            GD.Print($"scene: {name} shows wave {now.Wave}, between {now.BetweenWaves}, seconds {now.Seconds}, {now.Special}, boss {now.BossName}, enemies {now.EnemiesLeft}, bank {now.Bank}, " +
                     $"score {now.Score}, pod {now.Pod:0.000}, bosses [{string.Join(", ", now.Bosses.Select(b => $"{b.Name} {b.Health:0.000}/{b.Armour:0.000}"))}], bodies {now.SpareBodies}/{now.BodiesAtStart}, " +
                     $"down {now.DownSeconds:0.0}, abilities [{string.Join(", ", now.Abilities.Select(a => $"{a.Catalogue} {a.SecondsLeft:0.0}s {a.WavesLeft}w"))}], dash {now.Dash?.SecondsLeft:0.0}, " +
                     $"squad {now.Squad.Count}/{now.SquadLimit} [{string.Join(", ", now.Squad.Select(s => $"{s.Model}{s.Level} {s.Health:0.00} {s.Doing}"))}], " +
                     $"chips [{string.Join(", ", now.Chips.Select(c => $"{c.Id}{(c.Lit ? "*" : "")}"))}], hangar {now.HangarHere}, strike {now.StrikeIn:0.0} in {now.InStrike}");
    }

    private async Task Do(Command command)
    {
        Issue(command);
        await Seconds(0.25);
        foreach (var refused in World.Rejected)
            GD.PrintErr($"scene: {command.GetType().Name} refused: {refused.Reason}");
    }

    /// <summary>Full gear through the hangar's commands, and a squad; the next wave is the given one, in a few seconds.</summary>
    private async Task Gear(WiredRun run, int wave, WiredSpecial special, bool squad = true)
    {
        var state = run.Heroes[Player];
        World.Races[Player].Bmu = 100000;
        run.Wave = wave - 1;
        OpenHangar();
        await Seconds(0.3);
        foreach (var worn in state.Loadout.Weapons.OfType<WiredItem>().ToList())
            await Do(new WiredUnequipCommand(Player, worn.Id));
        await Do(new WiredChassisCommand(Player, state.Add(WiredKind.Chassis, "knight5", 0).Id));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, "PLASMA_CANNON2", 0).Id, WiredSlot.Weapon(Mounting.Left)));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, "MULTI_LAUNCHER1", 0).Id, WiredSlot.Weapon(Mounting.Right)));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, "HEAVY_BOLTER1", 0).Id, WiredSlot.Weapon(Mounting.Top)));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Module, "thrusters", 0).Id, WiredSlot.Module(0)));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Module, "nightvision", 0).Id, WiredSlot.Module(1)));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Ability, "discharge", 0).Id, WiredSlot.Ability(0)));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Ability, "supercharge", 0).Id, WiredSlot.Ability(1)));
        var chips = new[] { "nano", "sustained", "marksman", "overload" };
        for (var socket = 0; socket < chips.Length; socket++)
            await Do(new WiredChipCommand(Player, state.Add(WiredKind.Chip, chips[socket], 0).Id, socket));
        if (squad)
        {
            foreach (var offer in WiredCatalogue.Tiers.Where(t => t.Wave <= run.ShopWave).SelectMany(t => t.Combos.Select(c => new ResearchKey(t.Type, t.SubType, t.Level, c))).Take(3).ToList())
                await Do(new WiredSquadBuyCommand(Player, offer));
        }
        CloseHangar();
        await Seconds(0.3);
        run.NextSpecial = special;
        run.Countdown = 3;
    }

    /// <summary>Waits for the first foes, sets the hero down near them and has it look at them.</summary>
    private async Task MeetFoes(WiredRun run, float metres = 45, Func<WiredFoe, bool>? which = null)
    {
        for (var second = 0; second < 120 && !run.Foes.Any(which ?? (_ => true)); second++)
            await Seconds(0.5);
        if (run.Foes.FirstOrDefault(which ?? (_ => true)) is not { } foe || World.HeroOf(Player) is not { } hero)
            return;
        var way = hero.Position - foe.Machine.Position;
        way = way.LengthSquared() < 1 ? NVector2.UnitX : NVector2.Normalize(way);
        hero.Position = World.Nav.NearestFreePoint(foe.Machine.Position + way * metres, crossesWater: false) ?? hero.Position;
        Face(foe.Machine.Position);
    }

    private void Face(NVector2 place)
    {
        if (World.HeroOf(Player) is not { } hero)
            return;
        var way = place - hero.Position;
        _aimYaw = MathF.Atan2(way.Y, way.X);
    }

    private async Task SceneHud(string folder, WiredRun run)
    {
        await Gear(run, 6, WiredSpecial.None);
        await MeetFoes(run);
        await Seconds(1.5);
        Issue(new WiredAbilityCommand(Player, 0, World.HeroOf(Player)!.Position));
        Issue(new WiredDashCommand(Player));
        World.Races[Player].Bmu += 137;
        await Seconds(1.5);
        if (run.Foes.FirstOrDefault() is { } foe)
            Face(foe.Machine.Position);
        await Seconds(0.8);
        Snap(folder, "run-hud");
    }

    private async Task SceneBoss(string folder, WiredRun run, int wave, string picture)
    {
        await Gear(run, wave, WiredSpecial.None);
        await MeetFoes(run, 55, f => f.IsBoss);
        await Seconds(1.2);
        Snap(folder, picture + "-called");
        foreach (var boss in run.Bosses.ToList())
            boss.Machine.HitPoints = boss.Machine.MaxHitPoints * 62 / 100;
        await Seconds(0.1);
        foreach (var boss in run.Bosses.ToList())
            boss.Machine.HitPoints = boss.Machine.MaxHitPoints * 45 / 100;
        await Seconds(1.2);
        if (run.Bosses.FirstOrDefault() is { } first)
            Face(first.Machine.Position);
        await Seconds(0.6);
        Snap(folder, picture);
    }

    private async Task SceneNight(string folder, WiredRun run)
    {
        await Gear(run, 7, WiredSpecial.Night);
        await Seconds(4);
        Snap(folder, "run-night-call");
        await MeetFoes(run, 40);
        await Seconds(7);
        if (run.Foes.FirstOrDefault() is { } foe)
            Face(foe.Machine.Position);
        await Seconds(0.8);
        Snap(folder, "run-night-dark");
        _screen.ToggleGoggles();
        await Seconds(1);
        Snap(folder, "run-night");
        _screen.ToggleGoggles();
        run.Groups.Clear();
        foreach (var end in run.Foes.ToList())
            end.Machine.HitPoints = 0;
        for (var second = 0; second < 60 && !run.BetweenWaves; second++)
            await Seconds(0.5);
        await Seconds(3);
        Snap(folder, "run-night-dawn");
        await Seconds(5);
        Snap(folder, "run-night-after");
    }

    private async Task SceneIon(string folder, WiredRun run)
    {
        await Gear(run, 7, WiredSpecial.IonStorm);
        for (var second = 0; second < 80 && run.Strikes.Count == 0; second++)
            await Seconds(0.5);
        if (run.Strikes.Count == 0)
            return;
        var strike = run.Strikes[0];
        var hero = World.HeroOf(Player)!;
        // Inside the circle, looking across it.
        hero.Position = World.Nav.NearestFreePoint(strike.Position + new NVector2(14, 22), crossesWater: false) ?? hero.Position;
        Face(strike.Position);
        _lookPitch = -0.22f;
        await Seconds(1.6);
        Snap(folder, "run-ion");
        var away = hero.Position + NVector2.Normalize(hero.Position - strike.Position) * 90;
        hero.Position = World.Nav.NearestFreePoint(away, crossesWater: false) ?? hero.Position;
        Face(strike.Position);
        _lookPitch = -0.1f;
        await Seconds(0.7);
        Snap(folder, "run-ion-outside");
        for (var i = 0; i < 40 && run.Strikes.Contains(strike); i++)
            await Seconds(0.1);
        await Seconds(1.2);
        Snap(folder, "run-ion-strike");
    }

    private async Task ScenePickup(string folder, WiredRun run)
    {
        await Gear(run, 6, WiredSpecial.None);
        await MeetFoes(run, 30);
        foreach (var foe in run.Foes.Take(4).ToList())
            foe.Machine.HitPoints = 0;
        for (var i = 0; i < 40 && World.Debris.Count == 0; i++)
            await Seconds(0.25);
        await Seconds(1);
        if (World.Debris.FirstOrDefault() is { } near)
        {
            var hero = World.HeroOf(Player)!;
            var way = hero.Position - near.Position;
            hero.Position = near.Position + (way.LengthSquared() < 1 ? NVector2.UnitX : NVector2.Normalize(way)) * 24;
            Face(near.Position);
            await Seconds(1.5);
            Snap(folder, "run-glints");
            // Set down a little aside, in sight: a crate as a wave would leave it.
            var aside = World.Nav.NearestFreePoint(hero.Position + new NVector2(MathF.Cos(_aimYaw + 0.35f), MathF.Sin(_aimYaw + 0.35f)) * 22, crossesWater: false) ?? hero.Position;
            World.WiredPickups.Add(new WiredPickup(9000, aside, run.Wave));
            await Seconds(0.8);
            Snap(folder, "run-crate");
            hero.Position = near.Position + NVector2.UnitX * (hero.Radius + 1.5f);
            Face(near.Position + NVector2.UnitX * 30);
            await Seconds(0.45);
            Snap(folder, "run-pickup");
            hero.Position = aside;
            await Seconds(0.6);
            Snap(folder, "run-crate-taken");
        }
    }

    private async Task SceneDown(string folder, WiredRun run)
    {
        await Gear(run, 6, WiredSpecial.None, squad: false);
        await MeetFoes(run);
        await Seconds(1);
        var old = World.HeroOf(Player)!;
        old.HitPoints = old.MaxHitPoints * 2 / 10;
        await Seconds(0.8);
        Snap(folder, "run-hurt");
        old.HitPoints = 0;
        await Seconds(2.5);
        Snap(folder, "run-down");
        GD.Print($"scene: down, input refused: selection {_selection.Count}, hero down {_heroDown}");
        for (var i = 0; i < 100 && World.HeroOf(Player) is null; i++)
            await Seconds(0.1);
        await Seconds(1.2);
        Snap(folder, "run-back");
        GD.Print($"scene: back: driven {_driven?.Id}, hero {World.HeroOf(Player)?.Id}, driven by player {World.HeroOf(Player)?.DrivenByPlayer}, weapons kept {World.PlayerWeapons.GetValueOrDefault(World.HeroOf(Player)?.Id ?? 0)}");
    }

    private async Task SceneIntermission(string folder, WiredRun run)
    {
        await Gear(run, 5, WiredSpecial.None);
        run.Countdown = 2;
        for (var i = 0; i < 40 && run.BetweenWaves; i++)
            await Seconds(0.25);
        await MeetFoes(run, 50, f => f.IsBoss);
        await Seconds(1);
        run.Groups.Clear();
        foreach (var foe in run.Foes.ToList())
            foe.Machine.HitPoints = 0;
        for (var i = 0; i < 80 && !run.BetweenWaves; i++)
            await Seconds(0.25);
        await Seconds(0.6);
        Snap(folder, "run-cleared");
        await Seconds(3.4);
        Snap(folder, "run-far");
        var (hero, pod) = (World.HeroOf(Player)!, World.WiredPod(Player)!);
        var door = pod.EntranceOutside ?? pod.Position;
        hero.Position = door + NVector2.Normalize(door - pod.Position) * 12;
        Face(pod.Position);
        await Seconds(1);
        Snap(folder, "run-intermission");
    }

    private async Task SceneRefit(string folder, WiredRun run)
    {
        var state = run.Heroes[Player];
        var hero = World.HeroOf(Player)!;
        // From inside the pod, where a bigger chassis no longer fits.
        hero.Position = World.WiredPod(Player)!.EntranceInside ?? hero.Position;
        await Seconds(1);
        GD.Print($"scene: refit from inside {hero.Inside?.ToString() ?? "no"}");
        OpenHangar();
        await Seconds(0.3);
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, "AUTO_CANNON", 0).Id, WiredSlot.Weapon(Mounting.Right)));
        GD.Print($"scene: refit 1 weapons {World.PlayerWeapons.GetValueOrDefault(hero.Id)} driven {hero.DrivenByPlayer} refits {hero.Refits}");
        await Seconds(1);
        GD.Print($"scene: refit 1 later weapons {World.PlayerWeapons.GetValueOrDefault(hero.Id)} driven {hero.DrivenByPlayer} refits {hero.Refits}, shown {_hud.Radar.Driven?.Id}");
        foreach (var worn in state.Loadout.Weapons.OfType<WiredItem>().ToList())
            await Do(new WiredUnequipCommand(Player, worn.Id));
        await Do(new WiredChassisCommand(Player, state.Add(WiredKind.Chassis, "knight5", 0).Id));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, "PLASMA_CANNON2", 0).Id, WiredSlot.Weapon(Mounting.Left)));
        CloseHangar();
        await Seconds(0.3);
        await Seconds(1.5);
        GD.Print($"scene: chassis weapons {World.PlayerWeapons.GetValueOrDefault(hero.Id)} driven {hero.DrivenByPlayer} refits {hero.Refits}, inside {hero.Inside?.ToString() ?? "no"}, first person {_driven?.Id}");
        Snap(folder, "run-refit");
    }

    private async Task SceneLoad(string folder, WiredRun run, bool ground = false)
    {
        await Seconds(2);
        if (ground)
        {
            LeaveFirstPerson();
            await Seconds(1);
        }
        Snap(folder, "run-before-save");
        var name = System.Environment.GetEnvironmentVariable("MACHINES_HANGAR_SAVE") ?? "scene";
        GD.Print($"scene: saved '{name}' ({SaveGame(name) ?? "fine"})");
        _scriptLoaded = true;
        LeaveFirstPerson();
        GD.Print($"scene: loading: {LoadGame(System.IO.Path.Combine(SaveFolder, name + SaveExtension)) ?? "fine"}");
    }

    private async Task SceneLoaded(string folder)
    {
        await Seconds(2);
        GD.Print($"scene: loaded: driven {_driven?.Id}, hero {World.HeroOf(Player)?.Id}, driven by player {World.HeroOf(Player)?.DrivenByPlayer}, mouse {Input.MouseMode}");
        Snap(folder, "run-loaded");
    }

    /// <summary>Fires at a far point and tells where each round went against where it was aimed.</summary>
    private async Task SceneStray(string folder, WiredRun run)
    {
        await Gear(run, 6, WiredSpecial.None, squad: false);
        await MeetFoes(run, 60);
        var hero = World.HeroOf(Player)!;
        GD.Print($"scene: stray: spread {World.AimSpread(hero):0.00} degrees");
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = GetViewport().GetVisibleRect().Size / 2 });
        for (var frame = 0; frame < 120; frame++)
        {
            foreach (var shot in World.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id))
                GD.Print($"scene: stray: {shot.Detail} sight {World.PlayerAims.GetValueOrDefault(hero.Id)} mark {shot.Mark}");
            await Seconds(0.05);
            if (frame == 60)
                Snap(folder, "run-stray");
        }
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
    }

    /// <summary>Foes set down at the pod: the warning is to come while the hero is away.</summary>
    private async Task ScenePodHit(string folder, WiredRun run)
    {
        await Gear(run, 6, WiredSpecial.None, squad: false);
        await MeetFoes(run, 40);
        var pod = World.WiredPod(Player)!;
        foreach (var foe in run.Foes.ToList())
            foe.Machine.Position = World.Nav.NearestFreePoint(pod.Position + new NVector2(30, 0), crossesWater: false) ?? foe.Machine.Position;
        for (var i = 0; i < 200 && pod.HitPoints == pod.MaxHitPoints; i++)
            await Seconds(0.1);
        await Seconds(2.5);
        Snap(folder, "run-pod-hit");
    }

    /// <summary>The hero in full gear meets the sixth wave and fires with all it has until something has fallen to it.</summary>
    private async Task FirstBlood(WiredRun run)
    {
        await Gear(run, 6, WiredSpecial.None, squad: false);
        await MeetFoes(run, 16);
        var stats = run.Heroes[Player].Stats;
        Press(MouseButton.Left, true);
        Press(MouseButton.Right, true);
        Press(MouseButton.Middle, true);
        for (var second = 0.0; second < 16 && stats.Kills == 0 && World.HeroOf(Player) is not null; second += 0.25)
        {
            if (run.Foes.FirstOrDefault() is { } foe)
                Face(foe.Machine.Position);
            await Seconds(0.25);
        }
        Press(MouseButton.Left, false);
        Press(MouseButton.Right, false);
        Press(MouseButton.Middle, false);
        var near = run.Foes.Select(f => World.HeroOf(Player)?.DistanceTo(f.Machine) ?? 0).DefaultIfEmpty(0).Min();
        GD.Print($"scene: the fight has gone on, kills {stats.Kills}, rounds {stats.Weapons.Values.Sum(w => w.Rounds)}, damage {stats.DamageDealt}, nearest foe {near:0.0} m, facing {World.HeroOf(Player)?.Facing:0.00}, aim {_aimYaw:0.00}: {RunReport}");
    }

    /// <summary>The player's own orders only, so that the recording plays back: the wave is called, and each of the hero's bodies goes by self-destruct until none is left.</summary>
    private async Task ScenePlain(string folder, WiredRun run)
    {
        if (!run.Heroes[Player].Stats.Weapons.Any() && run.Phase == WiredPhase.Prepare)
            Issue(new WiredReadyCommand(Player));
        while (run.Phase != WiredPhase.Ended)
        {
            if (World.HeroOf(Player) is { } hero)
            {
                Issue(new SelfDestructCommand(Player, [hero.Id]));
                while (World.HeroOf(Player) == hero && run.Phase != WiredPhase.Ended)
                    await Seconds(0.25);
            }
            await Seconds(1);
        }
        Snap(folder, "run-plain");
    }

    /// <summary>The same up to the second body, which is left standing in a game saved for another process to go on with.</summary>
    private async Task ScenePlainSave(string folder, WiredRun run)
    {
        Issue(new WiredReadyCommand(Player));
        var hero = World.HeroOf(Player)!;
        Issue(new SelfDestructCommand(Player, [hero.Id]));
        while (World.HeroOf(Player) == hero)
            await Seconds(0.25);
        while (World.HeroOf(Player) is null)
            await Seconds(0.25);
        await Seconds(2);
        Snap(folder, "run-saved");
        GD.Print($"scene: saved: {SaveGame("wired-end") ?? "fine"}: {RunReport}");
        GetTree().Quit();
    }

    /// <summary>What is left of the wave is set on a pod left with little, and their fire ends the run. If those about it hunt the hero and leave it alone, its last hit points are taken away.</summary>
    private async Task SceneLosePod(string folder, WiredRun run)
    {
        await FirstBlood(run);
        var pod = World.WiredPod(Player)!;
        foreach (var foe in run.Foes.ToList())
            foe.Machine.Position = World.Nav.NearestFreePoint(pod.Position + new NVector2(30, 0), crossesWater: false) ?? foe.Machine.Position;
        pod.HitPoints = Math.Min(pod.HitPoints, 25);
        for (var second = 0; second < 80 && run.Phase != WiredPhase.Ended; second++)
            await Seconds(0.5);
        GD.Print($"scene: after the foes' fire: pod {pod.HitPoints}, {run.Phase}");
        if (run.Phase != WiredPhase.Ended)
            pod.HitPoints = 0;
        for (var second = 0; second < 20 && run.Phase != WiredPhase.Ended; second++)
            await Seconds(0.5);
        Snap(folder, "run-pod-lost");
    }

    /// <summary>The hero has no spare body left, and its own charge goes off: self-destruct as the player orders it.</summary>
    private async Task SceneLoseBody(string folder, WiredRun run)
    {
        await FirstBlood(run);
        await ByOwnHand(folder, run);
    }

    private async Task ByOwnHand(string folder, WiredRun run)
    {
        run.Heroes[Player].SpareBodies = 0;
        if (World.HeroOf(Player) is { } hero)
            Issue(new SelfDestructCommand(Player, [hero.Id]));
        for (var second = 0; second < 80 && run.Phase != WiredPhase.Ended; second++)
            await Seconds(0.25);
        Snap(folder, "run-out-of-bodies");
    }

    /// <summary>
    /// The statistics are told what nine waves of play would have brought, and the tenth is cleared for real: the wave comes,
    /// its machines fall and the sim counts it. Then the hero goes by its own hand, so that the run's end has the waves to
    /// open things with.
    /// </summary>
    private async Task SceneUnlock(string folder, WiredRun run, int wave)
    {
        var state = run.Heroes[Player];
        await Gear(run, wave, WiredSpecial.None);
        var stats = state.Stats;
        stats.WavesCleared = wave - 1;
        (stats.Kills, stats.KillValue, stats.BmusEarned, stats.DamageDealt, stats.DamageTaken, stats.WrecksCollected, stats.CratesCollected) = (64, 2860, 3120, 18450, 2375, 21, 6);
        foreach (var (weapon, rounds, kills, damage) in new[] { ("PLASMA_CANNON2", 318, 27, 9120L), ("MULTI_LAUNCHER1", 244, 22, 6410L), ("HEAVY_BOLTER1", 702, 15, 2920L) })
            (stats.Weapon(weapon).Rounds, stats.Weapon(weapon).Kills, stats.Weapon(weapon).Damage) = (rounds, kills, damage);
        for (var second = 0; second < 120 && !(run.Wave == wave && run.Phase == WiredPhase.Wave); second++)
            await Seconds(0.25);
        for (var second = 0; second < 300 && run.Phase == WiredPhase.Wave; second++)
        {
            foreach (var foe in run.Foes.ToList())
                foe.Machine.HitPoints = 0;
            await Seconds(0.5);
        }
        GD.Print($"scene: wave {wave} cleared, cleared {stats.WavesCleared}, bosses {stats.BossesKilled}, squad {stats.LargestSquad}: {RunReport}");
        await ByOwnHand(folder, run);
    }

    private static void Press(MouseButton button, bool down) => Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = down });

    private async Task Tap(KeyMap.Act act) => await Tap(KeyMap.Of(act));

    private async Task Tap(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Seconds(0.1);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        await Seconds(0.15);
    }

    /// <summary>Holds the buttons long enough for every gun to fire and come round again; what fires is logged by <see cref="LogShots"/>.</summary>
    private async Task Hold(params MouseButton[] buttons)
    {
        GD.Print($"scene: holding {string.Join(" and ", buttons)}");
        foreach (var button in buttons)
            Press(button, true);
        await Seconds(2.4);
        foreach (var button in buttons)
            Press(button, false);
        await Seconds(0.6);
    }

    private void TellGuns()
    {
        var hero = World.HeroOf(Player)!;
        GD.Print($"scene: guns {string.Join(", ", hero.Weapons.Select(w => $"{w.Mounting} {w.Spec.TypeName}"))}, places {string.Join(", ", GunPlaces(hero).Select(p => $"{p.Mounting} on {string.Join("+", p.Triggers)}"))}");
    }

    /// <summary>Every way of holding the triggers, on a lone gun, on a hero with only a right gun and on all three.</summary>
    private async Task SceneTriggers(string folder, WiredRun run)
    {
        const MouseButton left = MouseButton.Left, right = MouseButton.Right, middle = MouseButton.Middle;
        Ticked += LogShots;
        var state = run.Heroes[Player];
        run.Countdown = 300;
        TellGuns();
        await Hold(left);
        await Hold(right);
        await Hold(middle);
        World.Races[Player].Bmu = 100000;
        OpenHangar();
        await Seconds(0.3);
        foreach (var worn in state.Loadout.Weapons.OfType<WiredItem>().ToList())
            await Do(new WiredUnequipCommand(Player, worn.Id));
        await Do(new WiredChassisCommand(Player, state.Add(WiredKind.Chassis, "knight5", 0).Id));
        await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, "MULTI_LAUNCHER1", 0).Id, WiredSlot.Weapon(Mounting.Right)));
        CloseHangar();
        await Seconds(1);
        TellGuns();
        await Hold(left);
        await Hold(right);
        Press(right, true);
        await Seconds(0.8);
        Snap(folder, "run-right-only");
        Press(right, false);
        await Seconds(0.6);
        await Hold(middle);
        await Gear(run, 6, WiredSpecial.None, squad: false);
        await Seconds(1);
        TellGuns();
        await Hold(left);
        await Hold(right);
        await Hold(middle);
        await Hold(left, right);
        foreach (var button in new[] { left, right, middle })
            Press(button, true);
        await Seconds(1.2);
        Snap(folder, $"run-weapons-{System.Environment.GetEnvironmentVariable("MACHINES_LOOK")?.ToLowerInvariant()}");
        foreach (var button in new[] { left, right, middle })
            Press(button, false);
        await Seconds(0.6);
        // A trigger put on a key fires by it.
        if (KeyMap.Button(KeyMap.Act.FireTop) is null)
        {
            var key = KeyMap.Of(KeyMap.Act.FireTop);
            GD.Print($"scene: holding {key}");
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
            await Seconds(2.4);
            Snap(folder, "run-rebound");
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
            await Seconds(0.4);
        }
    }

    /// <summary>Has the sight rest on a machine.</summary>
    private void Sight(Machine target)
    {
        Face(target.Position);
        var (eye, at) = (_views[_driven!.Id].Eye, _views[target.Id].Centre);
        _lookPitch = 0;
        LookBy(MathF.Atan2(at.Y - eye.Y, new Vector2(at.X - eye.X, at.Z - eye.Z).Length()));
    }

    private void TellSquad(string order) =>
        GD.Print($"scene: after {order}: [{string.Join(", ", World.Escorts(Player).Select(e => e.Order.GetType().Name))}]");

    /// <summary>The four orders by their keys, in a fight.</summary>
    private async Task SceneSquad(string folder, WiredRun run)
    {
        await Tap(KeyMap.Act.SquadFollow);
        await Seconds(0.4);
        Snap(folder, "run-no-squad");
        await Gear(run, 6, WiredSpecial.None);
        // The voice is one line at a time, and a fight has others: the three are given in the quiet, with time for an answer.
        run.Countdown = 60;
        await Seconds(5);
        await Tap(KeyMap.Act.SquadHold);
        await Seconds(4);
        TellSquad("hold");
        await Tap(KeyMap.Act.SquadBack);
        await Seconds(4);
        TellSquad("back");
        await Tap(KeyMap.Act.SquadFollow);
        await Seconds(4);
        TellSquad("follow");
        run.Countdown = 1;
        await MeetFoes(run, 50);
        await Seconds(1);
        _lookPitch = 0;
        LookBy(1);
        await Seconds(0.3);
        await Tap(KeyMap.Act.SquadAttack);
        await Seconds(0.5);
        Snap(folder, "run-squad-no-target");
        TellSquad("attack at the sky");
        await Seconds(2.6);
        if (run.Foes.FirstOrDefault(foe => !foe.Machine.IsDead) is not { } target)
            return;
        Sight(target.Machine);
        await Seconds(0.4);
        await Tap(KeyMap.Act.SquadAttack);
        await Seconds(0.45);
        TellSquad("attack");
        Snap(folder, "run-squad");
        // The squad has a long way to go from the pod: the target falls to something else.
        await Seconds(3);
        target.Machine.HitPoints = 0;
        await Seconds(1.5);
        TellSquad("the target's end");
    }

    /// <summary>
    /// The special of every chassis that has one, in first person: the hero changes to it at the pod, taps the special's key
    /// (the dash's for Old Iron and the Hornet) and is pictured with its block in the foot; a crab, with a Bolter, pulses at
    /// three foes standing before it, and a Harvester kills one beside it.
    /// </summary>
    private async Task SceneSpecials(string folder, WiredRun run)
    {
        var state = run.Heroes[Player];
        run.Countdown = 100_000;
        World.Races[Player].Bmu = 100000;
        Task Wear(string chassis, string? weapon = null, Mounting mounting = Mounting.Top) => WearForScene(state, chassis, weapon, mounting);
        Machine Place(float ahead, float aside, Race race, ResearchKey key) => PlaceForScene(ahead, aside, race, key);
        // A spy of the waves: it shoots at nobody.
        Machine Foe(float ahead, float aside) => Place(ahead, aside, Race.Blue, WiredDirector.Spy);

        await Wear("ballista5");
        await Tap(KeyMap.Act.Special);
        await Seconds(0.5);
        Snap(folder, "special-colossus-locking");
        await Seconds(2.2);
        Snap(folder, "special-colossus-anchored");
        await Tap(KeyMap.Act.Special);
        await Seconds(2.5);

        // The Marshal calls a Sarge, a level of Wired's own, which the squad strip names.
        await Wear("commander6");
        state.EscortIds.Add(Place(8, 4, Player, new ResearchKey("AGGRESSOR", "GRUNT", 4, null)).Id);
        await Seconds(0.5);
        await Tap(KeyMap.Act.Special);
        await Seconds(0.5);
        Snap(folder, "special-marshal");

        await Wear("spy3");
        await Tap(KeyMap.Act.Special);
        await Seconds(0.5);
        Snap(folder, "special-spy");

        // A shot gives the Phantom away: it is seen until it has been quiet for long enough.
        await Wear("phantom6", "BOLTER", Mounting.Left);
        await Tap(KeyMap.Act.Special);
        Press(MouseButton.Left, true);
        await Seconds(0.3);
        Press(MouseButton.Left, false);
        await Seconds(0.5);
        Snap(folder, "special-phantom-seen");
        await Seconds(3.5);
        Snap(folder, "special-phantom-hidden");

        await Wear("dozer1");
        await Tap(KeyMap.Act.Special);
        await Seconds(0.5);
        Snap(folder, "special-dozer");

        await Wear("widow6");
        await Seconds(0.6);
        Snap(folder, "special-widow-filling");
        await Seconds(1.5);
        Snap(folder, "special-widow-lurking");
        // The lurk works by itself, and its key says so.
        await Tap(KeyMap.Act.Special);
        await Seconds(0.4);
        Snap(folder, "special-widow-key");

        await Wear("reaper5");
        var harvester = World.HeroOf(Player)!;
        harvester.Durability = harvester.Durability with { Armour = harvester.MaxArmour / 2 };
        World.Damage(Foe(8, 0), 1_000_000, Player, harvester);
        // The harvest happened between ticks: its event is shown before the next tick clears it.
        ShowRunEvents();
        await Seconds(0.3);
        Snap(folder, "special-harvester");

        await Wear("grunt5");
        await Tap(KeyMap.Act.Dash);
        await Seconds(0.5);
        Snap(folder, "special-old-iron");
        // The ram is the thrusters' key's: the special's key names it.
        await Tap(KeyMap.Act.Special);
        await Seconds(0.4);
        Snap(folder, "special-old-iron-key");

        await Wear("boss3");
        await Tap(KeyMap.Act.Dash);
        await Seconds(0.5);
        Snap(folder, "special-hornet");

        await Wear("geo1", "BOLTER");
        var foes = new[] { Foe(22, 0), Foe(45, -12), Foe(70, 14) };
        await Seconds(0.5);
        // The edge of the view sweeps with the player's own pulse, for a third of a second once the world has sent it out.
        var crab = World.HeroOf(Player)!.Id;
        var special = KeyMap.Of(KeyMap.Act.Special);
        Input.ParseInputEvent(new InputEventKey { Keycode = special, PhysicalKeycode = special, Pressed = true });
        for (var wait = 0; wait < 50 && !_pulsedAt.ContainsKey(crab); wait++)
            await Seconds(0.02);
        await Seconds(0.1);
        Snap(folder, "special-crab-sweep");
        Input.ParseInputEvent(new InputEventKey { Keycode = special, PhysicalKeycode = special, Pressed = false });
        await Seconds(0.5);
        GD.Print($"scene: located [{string.Join(", ", foes.Select(f => $"{f.Id} {World.LocatedFor(f.Id):0.0} s"))}], aimed {_hud.Crosshair.Located}");
        Snap(folder, "special-crab-located");
        // A quarter of a second off the blink's half second: the other half of it.
        await Seconds(1.25);
        Snap(folder, "special-crab-blink");
    }

    /// <summary>The hero changes to a chassis at the pod through the hangar's commands, wearing at most one weapon, and is driven.</summary>
    private async Task WearForScene(WiredState state, string chassis, string? weapon = null, Mounting mounting = Mounting.Top)
    {
        OpenHangar();
        await Seconds(0.3);
        foreach (var worn in state.Loadout.Weapons.OfType<WiredItem>().Concat(state.Loadout.Copies.SelectMany(c => c)).ToList())
            await Do(new WiredUnequipCommand(Player, worn.Id));
        await Do(new WiredChassisCommand(Player, state.Add(WiredKind.Chassis, chassis, 0).Id));
        if (weapon is not null)
            await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, weapon, 0).Id, WiredSlot.Weapon(mounting)));
        CloseHangar();
        await Seconds(0.6);
        if (_driven is null && World.HeroOf(Player) is { } hero)
        {
            Select([hero.Id]);
            EnterFirstPerson();
        }
        await Seconds(1);
        GD.Print($"scene: {chassis}: special {World.SpecialView(Player)}");
    }

    /// <summary>A machine where the hero looks: as far ahead as given (or a little further, on free ground) and as far to the side.</summary>
    private Machine PlaceForScene(float ahead, float aside, Race race, ResearchKey key)
    {
        var hero = World.HeroOf(Player)!;
        var (forward, side) = (new NVector2(MathF.Cos(hero.Facing), MathF.Sin(hero.Facing)), new NVector2(-MathF.Sin(hero.Facing), MathF.Cos(hero.Facing)));
        var at = Enumerable.Range(0, 24).Select(k => hero.Position + forward * (ahead + k) + side * aside).FirstOrDefault(p => World.Nav.IsFree(p, crossesWater: false), hero.Position + forward * ahead);
        return World.AddMachine(race, at, key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo);
    }

    /// <summary>
    /// A crab's pulse ring and a Marshal's escort called in from afar, pictured as they go out; then a Titan's towers in
    /// first person, by the keys and the wheel: taken up away from the pod, the ghost where the sight meets the ground, the
    /// strip at each choice, a Launcher 4 placed and built until it is ready, pulled down again, and a place refused with a
    /// machine standing on it.
    /// </summary>
    private async Task SceneTowers(string folder, WiredRun run)
    {
        var state = run.Heroes[Player];
        run.Countdown = 100_000;
        World.Races[Player].Bmu = 100000;

        // The crab's pulse runs out over the ground, seen from high above, and its ring plays for a second and a half.
        await WearForScene(state, "geo1", "BOLTER");
        var spy = PlaceForScene(30, 0, Race.Blue, WiredDirector.Spy);
        if (World.HeroOf(Player) is { } far && _views.TryGetValue(far.Id, out var farView))
        {
            LeaveFirstPerson();
            (_rig.Position, _rig.Pitch, _rig.Distance) = (farView.Position, 58, 300);
            await Seconds(0.6);
            Issue(new WiredSpecialCommand(Player));
            await Seconds(0.4);
            Snap(folder, "towers-crab-wave");
            // Until the pulse is ready again the crab goes after the spy: it comes back to where it stood, facing as it did,
            // so that the Titan later sets out the same way, away from the pod, and a spy stands before it again.
            var (home, facing) = (far.Position, far.Facing);
            await Seconds(20);
            (far.Position, far.Facing) = (home, facing);
            if (spy.IsDead || World.Find<Machine>(spy.Id) is null)
                PlaceForScene(30, 0, Race.Blue, WiredDirector.Spy);
            else
                spy.Position = home + new System.Numerics.Vector2(MathF.Cos(facing), MathF.Sin(facing)) * 30;
            await Seconds(0.5);
            Select([far.Id]);
            EnterFirstPerson();
            await Seconds(1);
        }
        await Tap(KeyMap.Act.Special);
        await Seconds(0.3);
        LeaveFirstPerson();
        if (World.HeroOf(Player) is { } crab && _views.TryGetValue(crab.Id, out var crabView))
        {
            (_rig.Position, _rig.Pitch, _rig.Distance) = (crabView.Position, 38, 70);
            await Seconds(0.3);
            GD.Print($"scene: crab pulsing {Pulsing(crab)}");
            Snap(folder, "towers-crab-pulse");
            await Seconds(1.2);
            GD.Print($"scene: crab pulsing {Pulsing(crab)} after 1.8 s");
        }

        // The Marshal calls in an escort from far off: it fades out there and glows in beside the hero.
        await WearForScene(state, "commander6");
        var escort = PlaceForScene(60, 10, Player, new ResearchKey("AGGRESSOR", "GRUNT", 4, null));
        state.EscortIds.Add(escort.Id);
        LeaveFirstPerson();
        await Seconds(0.6);
        if (World.HeroOf(Player) is { } marshal && _views.TryGetValue(marshal.Id, out var marshalView))
            (_rig.Position, _rig.Pitch, _rig.Distance) = ((marshalView.Position + ActorView.World(escort.Position)) / 2, 45, 120);
        await Seconds(0.3);
        Issue(new WiredSpecialCommand(Player));
        await Seconds(0.3);
        GD.Print($"scene: escort {escort.Id} at {escort.Position}");
        Snap(folder, "towers-rally");
        await Seconds(1);

        await WearForScene(state, "titan6");
        // Away from the pod's door, where towers may stand.
        var forward = KeyMap.Of(KeyMap.Act.Forward);
        Input.ParseInputEvent(new InputEventKey { Keycode = forward, PhysicalKeycode = forward, Pressed = true });
        await Seconds(6);
        Input.ParseInputEvent(new InputEventKey { Keycode = forward, PhysicalKeycode = forward, Pressed = false });
        await Seconds(1);
        _lookPitch = Mathf.DegToRad(-14);
        void Report(string name)
        {
            var shown = Towers is { } t ? $"{t.Choices[t.Chosen].Tower?.Id ?? "scrap"} why {t.Why ?? "-"} target {t.Target} refund {t.Refund}" : "down";
            var standing = string.Join(", ", World.TowersOf(Player).Select(c => $"{c.Id} {c.Spec.SubType} {c.Stats.HardwareLevel} {(c.Complete ? "complete" : $"{c.Built:0}/{c.Stats.Cost}")}"));
            GD.Print($"scene: {name}: towers {shown}, stand {World.TowersOf(Player).Count} [{standing}], bank {World.Races[Player].Bmu}, firing {_driven is { } d && HeldWeapons(d) != 0}");
        }
        async Task Wheel(MouseButton button)
        {
            Press(button, true);
            await Seconds(0.05);
            Press(button, false);
            await Seconds(0.3);
        }
        await Tap(KeyMap.Act.Special);
        await Seconds(0.5);
        Report("towers-turret1");
        Snap(folder, "towers-turret1");
        for (var step = 0; step < 5; step++)
            await Wheel(MouseButton.WheelDown);
        Report("towers-launcher4");
        Snap(folder, "towers-launcher4");
        // The trigger places it, and fires nothing.
        Press(MouseButton.Left, true);
        await Seconds(0.3);
        Report("towers-pressed");
        Press(MouseButton.Left, false);
        await Seconds(1.5);
        Report("towers-placed");
        Snap(folder, "towers-placed");
        for (var second = 0; second < 30 && !World.TowersOf(Player).All(c => c.Complete); second++)
            await Seconds(1);
        await Seconds(0.4);
        Report("towers-ready");
        Snap(folder, "towers-ready");
        await Wheel(MouseButton.WheelDown);
        await Seconds(0.3);
        Report("towers-scrap");
        Snap(folder, "towers-scrap");
        Press(MouseButton.Left, true);
        await Seconds(0.2);
        Press(MouseButton.Left, false);
        await Seconds(0.6);
        Report("towers-scrapped");
        Snap(folder, "towers-scrapped");
        // Round to the first again, and a machine of the player's where it would stand.
        await Wheel(MouseButton.WheelDown);
        if (World.HeroOf(Player) is { } titan && Towers is not null)
        {
            var spot = _towerSpot;
            World.AddMachine(Player, spot, "AGGRESSOR", "GRUNT", 1, "L_BOLTER");
            // While the machine glows in; a refused ghost blinks, and it is pictured a few frames into a time it shows.
            await Seconds(2);
            for (var wait = 0; wait < 20 && Time.GetTicksMsec() / 250 % 2 == 0; wait++)
                await Seconds(0.03);
            await Seconds(0.08);
            Report("towers-refused");
            Snap(folder, "towers-refused");
            GD.Print($"scene: titan at {titan.Position}, spot {spot}");
        }
        await Tap(Key.Escape);
        await Seconds(0.4);
        Report("towers-away");
        Snap(folder, "towers-away");
    }

    /// <summary>The abilities, the dash, the special and the hangar by their keys, with what each says where it cannot be done.</summary>
    private async Task SceneKeys(string folder, WiredRun run)
    {
        var (hero, pod) = (World.HeroOf(Player)!, World.WiredPod(Player)!);
        await Tap(KeyMap.Act.Ability1);
        await Seconds(0.4);
        Snap(folder, "run-key-no-ability");
        await Seconds(2);
        await Tap(KeyMap.Act.Dash);
        await Seconds(0.4);
        Snap(folder, "run-key-no-thrusters");
        // The first chassis has no special of its own: its key says so.
        await Tap(KeyMap.Act.Special);
        await Seconds(0.4);
        Snap(folder, "run-key-no-special");
        hero.Position = pod.EntranceInside ?? hero.Position;
        await Seconds(1);
        GD.Print($"scene: hero inside {hero.Inside?.ToString() ?? "no"}");
        await Tap(KeyMap.Act.Dash);
        await Seconds(0.4);
        Snap(folder, "run-key-inside");
        hero.Position = pod.EntranceOutside ?? hero.Position;
        await Seconds(1);
        await Gear(run, 6, WiredSpecial.None, squad: false);
        await Seconds(2);
        hero = World.HeroOf(Player)!;
        await Tap(KeyMap.Act.Ability1);
        await Seconds(0.4);
        await Tap(KeyMap.Act.Ability1);
        await Seconds(0.4);
        Snap(folder, "run-key-cooling");
        await Tap(KeyMap.Act.Ability2);
        await Seconds(0.4);
        var state = run.Heroes[Player];
        GD.Print($"scene: abilities used: {state.AbilitySecondsLeft(0, World.Time):0.0} s, {state.AbilitySecondsLeft(1, World.Time):0.0} s left");
        async Task DashWith(Key? held)
        {
            _aimYaw = 0;
            await Seconds(1.5);
            var from = hero.Position;
            if (held is { } key)
                Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
            await Tap(KeyMap.Act.Dash);
            await Seconds(0.6);
            if (held is { } was)
                Input.ParseInputEvent(new InputEventKey { Keycode = was, PhysicalKeycode = was, Pressed = false });
            GD.Print($"scene: dash with {held?.ToString() ?? "nothing"} held, facing {hero.Facing:0.00}, view {_aimYaw:0.00}: moved {hero.Position - from}");
            await Seconds(14);
        }
        await DashWith(null);
        await DashWith(KeyMap.Of(KeyMap.Act.Left));
        await DashWith(KeyMap.Of(KeyMap.Act.Back));
        for (var second = 0; second < 40 && run.BetweenWaves; second++)
            await Seconds(0.5);
        await Tap(KeyMap.Act.Hangar);
        await Seconds(0.4);
        Snap(folder, "run-key-hangar");
        _sceneHolds = false;
        hero.HitPoints = 0;
        await Seconds(1);
        await Tap(KeyMap.Act.Ability1);
        await Seconds(0.4);
        Snap(folder, "run-key-down");
        await Tap(KeyMap.Act.FirstPerson);
        await Seconds(0.4);
    }

    private void Click(MouseButton button, Vector2 at)
    {
        _UnhandledInput(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = at });
        _UnhandledInput(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = at });
    }

    /// <summary>Out of first person in a fight: the hero fights on its own, the mouse commands the squad, and first person takes the hero back.</summary>
    private async Task SceneLeave(string folder, WiredRun run)
    {
        await Gear(run, 6, WiredSpecial.None);
        await MeetFoes(run, 40);
        var hero = World.HeroOf(Player)!;
        var shots = 0;
        void Count() => shots += World.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id);
        // The foes last, for the hero to have something to fight on its own.
        void Last()
        {
            foreach (var foe in run.Foes)
                (foe.Machine.HitPoints, foe.Machine.Armour) = (foe.Machine.MaxHitPoints, foe.Machine.MaxArmour);
        }
        Ticked += Count;
        Ticked += Last;
        await Seconds(0.5);
        await Tap(Key.Escape);
        await Seconds(0.5);
        GD.Print($"scene: left: driven {_driven?.Id}, by player {hero.DrivenByPlayer}, order {hero.Order.GetType().Name}, mouse {Input.MouseMode}, selected {_selection.Count}, fog {World.Options.FogOfWar}");
        shots = 0;
        Snap(folder, "run-left");
        await Seconds(8);
        Ticked -= Last;
        GD.Print($"scene: alone for 8 s: {shots} shots, order {hero.Order.GetType().Name}, hit points {hero.HitPoints}");
        var escort = World.Escorts(Player).First();
        var camera = GetViewport().GetCamera3D();
        LookAtActor(escort);
        await Seconds(0.5);
        Click(MouseButton.Left, camera.UnprojectPosition(_views[escort.Id].Centre));
        await Seconds(0.3);
        GD.Print($"scene: clicked escort {escort.Id}: selected [{string.Join(", ", _selection)}]");
        Click(MouseButton.Right, GetViewport().GetVisibleRect().Size * new Vector2(0.5f, 0.4f));
        await Seconds(0.6);
        TellSquad("the mouse's order");
        Snap(folder, "run-left-order");
        Select([World.WiredPod(Player)!.Id]);
        await Seconds(0.5);
        Snap(folder, "run-left-pod");
        Select([escort.Id]);
        await Seconds(0.3);
        await Tap(KeyMap.Act.FirstPerson);
        await Seconds(1.5);
        GD.Print($"scene: back in: driven {_driven?.Id}, hero {hero.Id}, by player {hero.DrivenByPlayer}, mouse {Input.MouseMode}, selected [{string.Join(", ", _selection)}]");
        Ticked += LogShots;
        await Hold(MouseButton.Left);
        Snap(folder, "run-back-in");
    }

    /// <summary>The hero in a bay of the pod: the hangar opens there, and first person takes it out by the door, where it opens as well.</summary>
    private async Task SceneBay(string folder, WiredRun run)
    {
        var (hero, pod) = (World.HeroOf(Player)!, World.WiredPod(Player)!);
        void Tell(string when) => GD.Print($"scene: {when}: inside {hero.Inside?.ToString() ?? "no"}, driven {_driven?.Id}, by player {hero.DrivenByPlayer}, hangar {HangarOpen}, {NVector2.Distance(hero.Position, pod.Position):0.0} m from the pod");
        Tell("start");
        await Tap(KeyMap.Act.FirstPerson);
        await Seconds(0.5);
        Issue(new EnterGarrisonCommand(Player, [hero.Id], pod.Id));
        for (var second = 0; second < 60 && hero.Inside is null; second++)
            await Seconds(0.5);
        Tell("in a bay");
        await Tap(KeyMap.Act.Hangar);
        await Seconds(0.6);
        Tell("key in the bay");
        CloseHangar();
        await Seconds(0.5);
        await Tap(KeyMap.Act.FirstPerson);
        await Seconds(1.5);
        Tell("first person from the bay");
        Snap(folder, "run-bay-out");
        await Tap(KeyMap.Act.Hangar);
        await Seconds(0.6);
        Tell("key at the door");
        CloseHangar();
        await Seconds(0.8);
        Tell("after the hangar");
    }

    private async Task SceneRefused(string folder, WiredRun run)
    {
        // The first chassis is small enough for the pod's door: driven over the threshold it counts as inside.
        var (hero, pod) = (World.HeroOf(Player)!, World.WiredPod(Player)!);
        hero.Position = pod.EntranceInside ?? hero.Position;
        await Seconds(1);
        GD.Print($"scene: hero inside {hero.Inside?.ToString() ?? "no"}");
        Issue(new WiredDashCommand(Player));
        await Seconds(0.5);
        Snap(folder, "run-refused-pod");
        hero.Position = pod.EntranceOutside ?? hero.Position;
        await Seconds(1);
        await Gear(run, 6, WiredSpecial.None, squad: false);
        hero = World.HeroOf(Player)!;
        Issue(new WiredAbilityCommand(Player, 0, hero.Position));
        await Seconds(0.3);
        Issue(new WiredAbilityCommand(Player, 0, hero.Position));
        await Seconds(0.4);
        Snap(folder, "run-refused-cooling");
        LeaveFirstPerson();
        _sceneHolds = false;
        Issue(new WiredAbilityCommand(Player, 1, hero.Position));
        await Seconds(0.3);
        hero.HitPoints = 0;
        await Seconds(1);
        Issue(new WiredDashCommand(Player));
        await Seconds(0.4);
        Snap(folder, "run-refused-down");
    }
}
