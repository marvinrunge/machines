using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

// What a Wired hero means to the views. In any other match World.Wired is null and none of this does anything.
public partial class Game
{
    /// <summary>What a hero of a Wired run wears; null for every other actor.</summary>
    private HeroLoadout? HeroOf(Actor actor) =>
        World.Wired is { } run && run.Heroes.TryGetValue(actor.Race, out var state) && state.Hero == actor.Id ? state.Loadout : null;

    /// <summary>A hero the hangar refitted changes its model where it stands, in sparks and the sound of work.</summary>
    private void ShowRefit(ActorView view, Machine machine)
    {
        if (!view.Refitted(machine) || HeroOf(machine) is not { } worn)
            return;
        view.Refit(machine, worn, _assets, _audio);
        // Tab's choice is a place in the list of weapons, and the list has changed: as on getting in, all of them fire.
        if (_driven?.Id == machine.Id)
            _weaponSelect = int.MaxValue;
        _support.BoltSparks(view.Centre, view.Bounds.Size / 2, view.Centre + Vector3.Up * view.Bounds.Size.Y);
        _audio.Play("SID_CONSTRUCTING", view.GlobalPosition);
    }

    private bool _refitScripted;

    /// <summary>
    /// For automated checks of a Wired run (MACHINES_WIRED=1 with MACHINES_MAP): MACHINES_SCREENSHOT_REFIT=&lt;chassis&gt;,&lt;left&gt;,&lt;right&gt;,&lt;top&gt;
    /// selects the hero, keeps the camera on it and refits it after two seconds through the hangar's commands, - for an
    /// empty slot, <c>WEAPON*3</c> for a battery of three (the further copies added to the first), then prints what its view
    /// shows, every gun's muzzle. A fifth word drives it afterwards (first), printing the gun and muzzle of every round it
    /// fires, or sets it on fire before (burning).
    /// </summary>
    private async void ScriptRefit()
    {
        if (_refitScripted || World.Wired is not { } run || System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_REFIT") is not { Length: > 0 } script
            || World.HeroOf(Player) is not { } hero)
            return;
        _refitScripted = true;
        var words = script.Split(',');
        var state = run.Heroes[Player];
        Select([hero.Id]);
        _rig.Position = _views[hero.Id].Position;

        async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        async Task Do(Command command)
        {
            if (words.Length > 4 && words[4] == "burning" && World.HeroOf(Player) is { } burning)
                burning.HitPoints = burning.MaxHitPoints / 20;
            // The world forgets its refusals with the next tick, so they are looked for after each.
            var reasons = new List<string>();
            void Refused() => reasons.AddRange(World.Rejected.Where(r => Equals(r.Command, command)).Select(r => r.Reason));
            Ticked += Refused;
            Issue(command);
            await Wait(0.3);
            Ticked -= Refused;
            foreach (var reason in reasons)
                GD.PrintErr($"hero: {command.GetType().Name} refused: {reason}");
        }

        await Wait(2);
        World.Races[Player].Bmu = 1000000;
        // Through the screen, as a player opens it: a hangar opened without one is closed again by FollowHangar.
        OpenHangar();
        await Wait(0.3);
        // Whatever it wears now goes first, so that a smaller chassis can take the hero.
        foreach (var worn in state.Loadout.Weapons.OfType<WiredItem>().Concat(state.Loadout.Copies.SelectMany(c => c)).ToList())
            await Do(new WiredUnequipCommand(Player, worn.Id));
        await Do(new WiredChassisCommand(Player, state.Add(WiredKind.Chassis, words[0], 0).Id));
        foreach (var (word, mounting) in words.Skip(1).Take(3).Zip(Enum.GetValues<Mounting>()))
        {
            if (word == "-")
                continue;
            var star = word.IndexOf('*');
            var (weapon, count) = star < 0 ? (word, 1) : (word[..star], int.TryParse(word[(star + 1)..], out var copies) ? copies : 1);
            for (var copy = 0; copy < count; copy++)
                await Do(new WiredEquipCommand(Player, state.Add(WiredKind.Weapon, weapon, 0).Id, WiredSlot.Weapon(mounting), Add: copy > 0));
        }
        CloseHangar();
        await Wait(0.3);
        if (World.HeroOf(Player) is not { } refitted || !_views.TryGetValue(refitted.Id, out var view))
            return;
        _rig.Position = view.Position;
        GD.Print($"hero: {refitted.Spec.Type} {refitted.Spec.SubType} refits {refitted.Refits}, selected {_selection.Contains(refitted.Id)}, bounds {view.Bounds}, eye {view.Eye - view.GlobalPosition}");
        for (var gun = 0; gun < refitted.Weapons.Count; gun++)
        {
            var weapon = refitted.Weapons[gun];
            GD.Print($"hero: {weapon.Mounting} copy {weapon.Copy} {weapon.Spec.TypeName} muzzle {MuzzleOf(refitted.Id, weapon.Spec, gun) - view.GlobalPosition}");
        }
        if (words.Length > 4 && words[4] == "first")
        {
            EnterFirstPerson();
            // Which gun every round leaves, and from where: a battery fires copy after copy, each from its own barrel.
            Ticked += () =>
            {
                foreach (var shot in World.Events.Where(e => IsInstanceValid(view) && e.Kind == GameEventKind.Fired && e.Actor == refitted.Id && e.Gun >= 0 && e.Gun < refitted.Weapons.Count))
                {
                    var gun = refitted.Weapons[shot.Gun];
                    GD.Print($"hero: tick {World.Tick} gun {shot.Gun} {gun.Mounting} copy {gun.Copy} {gun.Spec.TypeName} muzzle {view.Muzzle(gun.Mounting, gun.Spec, 0, gun.Copy) - view.GlobalPosition}");
                }
            };
        }
    }
}
