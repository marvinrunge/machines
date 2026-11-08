using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// What a Wired hero can do besides shooting, the remake's own addition: the abilities in its two slots and the dash of its
/// thrusters. They go through the same blasts, orbs, sprays and mines as the machines that carry those weapons do, so
/// that they look, sound and hurt alike, friends too. A machine turned by an ability goes back after a while (GameWorld.WiredWaves counts it).
/// </summary>
public sealed partial class GameWorld
{
    /// <returns>null if the ability in a slot can be used at a point now, otherwise why not. The HUD asks the same.</returns>
    public string? WhyNoAbility(WiredState state, int slot, Vector2 aim)
    {
        if (slot < 0 || slot >= state.Loadout.Abilities.Count)
            return "no such ability slot";
        if (state.Loadout.Abilities[slot] is not { } item || WiredCatalogue.FindAbility(item.Catalogue) is not { } ability)
            return "no ability in this slot";
        if (HeroOf(state.Race) is not { } hero)
            return "the hero is down";
        if (hero.Inside is not null)
            return "not from inside the pod";
        if (state.AbilitySecondsLeft(slot, Time) > 0 || state.AbilityWavesLeft(slot, Wired!.Wave) > 0)
            return "that is not ready yet";
        if (ability.Id is "vortex" or "treachery" && Vector2.Distance(hero.Position, aim) > ability.Reach)
            return "that is out of reach";
        if (ability.Id == "treachery" && Aimed(state, aim) is null)
            return "nothing there to turn";
        if (ability.Id == "minefield" && LandMines.Count(m => m.Race == state.Race) + WiredBalance.MinesLaid > WiredBalance.MostMines)
            return "too many mines on the ground";
        return null;
    }

    /// <summary>The enemy machine a treachery aimed at a point goes for: the nearest to the point, unless that is a boss.</summary>
    private Machine? Aimed(WiredState state, Vector2 aim)
    {
        var target = Machines.Where(m => !m.IsDead && m.Inside is null && AreEnemies(state.Race, m.Race)
                && Vector2.Distance(m.Position, aim) - m.Radius <= WiredBalance.TreacheryAim)
            .OrderBy(m => Vector2.DistanceSquared(m.Position, aim)).ThenBy(m => m.Id).FirstOrDefault();
        return target is null || IsWiredBoss(target) ? null : target;
    }

    private string? UseAbility(WiredState state, int slot, Vector2 aim)
    {
        if (WhyNoAbility(state, slot, aim) is { } why)
            return why;
        var hero = HeroOf(state.Race)!;
        var item = state.Loadout.Abilities[slot]!;
        var ability = WiredCatalogue.FindAbility(item.Catalogue)!;
        switch (ability.Id)
        {
            case "vortex":
                FireAbilityBlast(hero, ability, VortexPoint(hero, Data.Parm.Weapon(ability.Based!), aim));
                break;
            case "ionstrike":
                FireAbilityBlast(hero, ability, aim);
                break;
            case "treachery":
                ThrowOrb(hero, ability, Aimed(state, aim)!);
                break;
            case "discharge":
                Discharge(hero, ability);
                break;
            case "supercharge":
                Supercharge(state, hero, ability);
                break;
            case "minefield":
                LayMinefield(hero, aim);
                break;
        }
        if (ability.CooldownWaves > 0)
            state.AbilityReadyWave[item.Id] = Wired!.Wave + ability.CooldownWaves;
        else
            state.AbilityReadyAt[item.Id] = Time + ability.Cooldown;
        Raise(new GameEvent(GameEventKind.WiredAbilityUsed, hero.Id, aim, slot, ability.Id));
        return null;
    }

    /// <summary>The real blast of the weapon behind an ability. Unlike the Eradicator's vortex this leaves the hero free to move.</summary>
    private void FireAbilityBlast(Machine hero, WiredAbility ability, Vector2 point)
    {
        var weapon = Data.Parm.Weapon(ability.Based!);
        Raise(new GameEvent(GameEventKind.Fired, hero.Id, hero.Position, null, weapon.TypeName));
        StartBlast(weapon, point, hero.Race);
    }

    /// <summary>A real orb from a mount that exists for this one round, aimed where the target will be. It can miss, as any orb can.</summary>
    private void ThrowOrb(Machine hero, WiredAbility ability, Machine target)
    {
        var orb = new WeaponMount(Mounting.Top, Data.Parm.Weapon(ability.Based!));
        var mark = Predicted(target, Vector2.Distance(hero.Position, target.Position) / MathF.Max(1, orb.Spec.ProjectileSpeed));
        Raise(new GameEvent(GameEventKind.Fired, hero.Id, hero.Position, target.Id, orb.Spec.TypeName));
        Launch(hero, orb, new Vector2(mark.X, mark.Y), mark.Z);
    }

    // The discharge's lightning, made at its first use: it is told apart from an electric charger's by being this one.
    private WeaponSpec? _discharge;

    /// <summary>What a round's hits count for in a run's statistics: its weapon, or none for the discharge, which is an ability.</summary>
    private string? WeaponOf(WeaponSpec weapon) => ReferenceEquals(weapon, _discharge) ? null : weapon.TypeName;

    /// <summary>
    /// Arcs of the electric charger's lightning run out from the hero, as far as the ability reaches, and strike what they
    /// meet. They look for the enemies in reach first, as one could slip between arcs that keep to their places, and the
    /// others spread out all round.
    /// </summary>
    private void Discharge(Machine hero, WiredAbility ability)
    {
        var charge = _discharge ??= Data.Parm.Weapon(ability.Based!) with { Range = ability.Reach, Damage = WiredBalance.DischargeDamage };
        Raise(new GameEvent(GameEventKind.Fired, hero.Id, hero.Position, null, charge.TypeName));
        var inReach = FiledNear(hero.Position, ability.Reach + _spatialReach).OfType<Machine>()
            .Where(m => !m.IsDead && m.Inside is null && AreEnemies(hero.Race, m.Race) && m.DistanceTo(hero) <= ability.Reach)
            .OrderBy(m => Vector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).Take(WiredBalance.DischargeArcs).ToList();
        foreach (var enemy in inReach)
            Spray(hero, charge, AimPoint(enemy));
        var middle = AimPoint(hero);
        for (var arc = inReach.Count; arc < WiredBalance.DischargeArcs; arc++)
        {
            var angle = MathF.Tau * arc / WiredBalance.DischargeArcs;
            Spray(hero, charge, middle + new Vector3(DetMath.Cos(angle), DetMath.Sin(angle), 0) * ability.Reach);
        }
    }

    /// <summary>The hero and the escorts near it get a share of their hit points back at once.</summary>
    private void Supercharge(WiredState state, Machine hero, WiredAbility ability)
    {
        Raise(new GameEvent(GameEventKind.Fired, hero.Id, hero.Position, null, ability.Based));
        foreach (var machine in Escorts(state.Race).Where(e => e.Inside is null && e.DistanceTo(hero) <= ability.Reach).Append(hero))
            AddHitPoints(machine, machine.MaxHitPoints * WiredBalance.SuperchargeHeal);
    }

    /// <summary>A row of real mines across the way to the aim point, in front of the hero. A mine that would lie on blocked ground moves to the nearest free one.</summary>
    private void LayMinefield(Machine hero, Vector2 aim)
    {
        var way = aim - hero.Position;
        var ahead = way.LengthSquared() > 1e-4f ? Vector2.Normalize(way) : new Vector2(DetMath.Cos(hero.Facing), DetMath.Sin(hero.Facing));
        var across = new Vector2(-ahead.Y, ahead.X);
        for (var i = 0; i < WiredBalance.MinesLaid; i++)
        {
            var place = hero.Position + ahead * WiredBalance.MinesAhead + across * ((i - (WiredBalance.MinesLaid - 1) / 2f) * WiredBalance.MineGap);
            var mine = new LandMineState(_nextMineId++, hero.Race, OnFreeGround(place));
            LandMines.Add(mine);
            Raise(new GameEvent(GameEventKind.MineLaid, hero.Id, mine.Position, mine.Id));
        }
    }

    // ---- the dash ---------------------------------------------------------------------------------

    private string? Dash(WiredState state, Vector2 direction)
    {
        if (HeroOf(state.Race) is not { } hero)
            return "the hero is down";
        if (hero.Inside is not null)
            return "not from inside the pod";
        if (state.Build(Data).DashCooldown is not { } cooldown)
            return "the hero has no thrusters";
        if (Time < state.DashReadyAt)
            return "that is not ready yet";
        state.DashWay = direction.LengthSquared() > 1e-4f ? Vector2.Normalize(direction) : new Vector2(DetMath.Cos(hero.Facing), DetMath.Sin(hero.Facing));
        state.DashReadyAt = Time + cooldown;
        state.DashUntil = Time + WiredBalance.DashTime;
        // Driven, it simply goes on from where it lands; on orders its run would pull it back onto the way it was on.
        if (!hero.DrivenByPlayer)
            StartAfresh(hero);
        Raise(new GameEvent(GameEventKind.WiredDashed, hero.Id, hero.Position));
        return null;
    }

    /// <summary>While a dash lasts the thrusters throw the hero along, sliding along what is in the way.</summary>
    private void UpdateDash(WiredState state, Machine hero)
    {
        if (Time < state.DashUntil)
            SlideOn(hero, state.DashWay * (WiredBalance.DashSpeed * Dt));
    }
}
