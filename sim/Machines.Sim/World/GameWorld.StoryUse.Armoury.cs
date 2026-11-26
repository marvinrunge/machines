using System.Globalization;
using Machines.Import.Model;
using Machines.Sim.Combat;
using System.Numerics;
using Machines.Sim.Story;
using Machines.Sim.Survival;
using static Machines.Sim.Story.Prologue;

namespace Machines.Sim.World;

// Stage 7's Use targets of package W (section 6: mend, mount, strip), in the registry of GameWorld.Companions (plan stufe7 C9,
// design-waves §5.3–5.4). Only the Locator mends, mounts and strips, holding Use within reach and standing still:
//   mend  "mend:<seat>"   a standing Grunt below its hit points, 10 % of them (and armour) a second;
//   mount "mount:<seat>"  an unarmed Grunt while L-7 carries a Bolter, 2 s: the Bolter goes into its Right(L) socket;
//   strip "wreck:<id>"    a wave unit's wreck, 3 s: a Bolter wreck yields its Bolter while the batches last (one carried at a
//                         time), a marked wreck at the end its part for the stash (StoryRun.Salvage, cue "salvaged <id>").
// A hit on the Locator breaks the hold, as it does a revive.
public sealed partial class GameWorld
{
    /// <summary>The targets' prefixes: mending and mounting a Grunt by its seat's player.</summary>
    public const string MendTarget = "mend:", MountTarget = "mount:";

    /// <summary>Adds what a hero of <paramref name="seat"/> could use here of this section, with its kind and distance.</summary>
    private void StoryUseTargetsArmoury(StoryRun run, int seat, Machine hero, List<StoryUseOption> near)
    {
        if (seat != 0 || run.Section != 6)
            return;
        var waves = run.Waves;
        // A wreck L-7 stands at is stripped, not the Grunt that followed it there: a mend is offered only for a Grunt nearer
        // than the nearest wreck to strip (the priority, C9, would otherwise always take the Grunt).
        var strip = float.MaxValue;
        foreach (var (_, wreck) in waves.Wrecks)
        {
            var d = Vector2.Distance(wreck.Position, hero.Position);
            if (d <= StoryWaves.Reach + hero.Radius && StripYield(run, wreck) is not null)
                strip = MathF.Min(strip, d);
        }
        foreach (var grunt in run.Seats.Skip(1))
        {
            if (CompanionDown(grunt) || CompanionDormant(grunt) || HeroOf(grunt) is not { } machine)
                continue;
            var d = Vector2.Distance(machine.Position, hero.Position);
            if (d > StoryWaves.Reach + hero.Radius + machine.Radius)
                continue;
            var id = grunt.Value.ToString(CultureInfo.InvariantCulture);
            if ((machine.HitPoints < machine.MaxHitPoints || machine.Armour < machine.MaxArmour) && d - machine.Radius < strip)
                near.Add(new(MendTarget + id, StoryUseKind.Mend, d));
            if (waves.Carried is not null && machine.Weapons.Count == 0)
                near.Add(new(MountTarget + id, StoryUseKind.Mount, d));
        }
        foreach (var (id, wreck) in waves.Wrecks)
        {
            var d = Vector2.Distance(wreck.Position, hero.Position);
            if (d <= StoryWaves.Reach + hero.Radius && StripYield(run, wreck) is not null)
                near.Add(new(StoryMarks.WreckTarget + id.ToString(CultureInfo.InvariantCulture), StoryUseKind.Strip, d));
        }
    }

    /// <summary>What stripping a wreck yields now: its mark at the end, its Bolter while L-7 carries none and the batches last; null: nothing.</summary>
    public static string? StripYield(StoryRun run, StoryWreck wreck)
    {
        if (!run.Flags.Contains(StoryMarks.ArmourySealed) && wreck.Marked is { } marked)
            return marked;
        if (wreck.HasBolter && !wreck.Stripped && run.Waves.Carried is null && run.Flags.Contains(StoryMarks.ArmourySealed))
            return StoryWaves.Bolter;
        return null;
    }

    /// <summary>Uses a target of this section; <see cref="NotMine"/> for one it does not know, null when done, else why not.</summary>
    private string? StoryUseArmoury(StoryRun run, PlayerId player, int seat, Machine hero, string target)
    {
        if (!target.StartsWith(MendTarget, StringComparison.Ordinal) && !target.StartsWith(MountTarget, StringComparison.Ordinal)
            && !target.StartsWith(StoryMarks.WreckTarget, StringComparison.Ordinal))
            return NotMine;
        if (seat != 0)
            return "only the Locator does that";
        if (WhyNotArmouryHold(run, hero, target) is { } why)
            return why;
        var waves = run.Waves;
        if (!waves.Holds.TryGetValue(player.Value, out var hold) || hold.Target != target)
            waves.Holds[player.Value] = new StoryWaveHold(target) { HitPoints = hero.HitPoints };
        run.Crew.Holding[player.Value] = target;
        HoldStill(hero.Id);
        return null;
    }

    /// <summary>Why the Locator cannot hold Use on an armoury target from where it stands; null when it can.</summary>
    private string? WhyNotArmouryHold(StoryRun run, Machine hero, string target)
    {
        var waves = run.Waves;
        if (target.StartsWith(StoryMarks.WreckTarget, StringComparison.Ordinal))
        {
            if (!int.TryParse(target[StoryMarks.WreckTarget.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !waves.Wrecks.TryGetValue(id, out var wreck))
                return "no wreck there";
            if (Vector2.Distance(wreck.Position, hero.Position) > StoryWaves.Reach + hero.Radius)
                return "too far away";
            return StripYield(run, wreck) is null ? "nothing to take from it" : null;
        }
        var mount = target.StartsWith(MountTarget, StringComparison.Ordinal);
        var digits = target[(mount ? MountTarget.Length : MendTarget.Length)..];
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var player) || run.Seats.IndexOf(new PlayerId(player)) < 1)
            return "nobody there";
        var seat = new PlayerId(player);
        if (CompanionDown(seat) || CompanionDormant(seat) || HeroOf(seat) is not { } grunt)
            return "the Grunt is down";
        if (Vector2.Distance(grunt.Position, hero.Position) > StoryWaves.Reach + hero.Radius + grunt.Radius)
            return "too far away";
        if (mount)
            return waves.Carried is null ? "no Bolter carried" : grunt.Weapons.Count > 0 ? "it is armed" : null;
        return grunt.HitPoints >= grunt.MaxHitPoints && grunt.Armour >= grunt.MaxArmour ? "it is whole" : null;
    }

    /// <summary>
    /// A tick of an armoury hold (UpdateHolds): null when the target is not the armoury's; false when the hold ends (out of
    /// reach, broken by a hit, done); true while it goes on. The holder stands still meanwhile.
    /// </summary>
    private bool? StoryHoldArmoury(StoryRun run, PlayerId holder, Machine hero, string what)
    {
        if (!what.StartsWith(MendTarget, StringComparison.Ordinal) && !what.StartsWith(MountTarget, StringComparison.Ordinal)
            && !what.StartsWith(StoryMarks.WreckTarget, StringComparison.Ordinal))
            return null;
        var waves = run.Waves;
        if (!waves.Holds.TryGetValue(holder.Value, out var hold) || hold.Target != what || WhyNotArmouryHold(run, hero, what) is not null)
        {
            waves.Holds.Remove(holder.Value);
            return false;
        }
        // A hit on the Locator starts the hold again (it holds on: the player lets go of nothing).
        if (hero.HitPoints < hold.HitPoints)
            hold.Ticks = 0;
        hold.HitPoints = hero.HitPoints;
        HoldStill(hero.Id);
        hold.Ticks++;
        if (what.StartsWith(MendTarget, StringComparison.Ordinal))
            return Mend(run, holder, what, hold);
        var seconds = what.StartsWith(MountTarget, StringComparison.Ordinal) ? StoryWaves.MountSeconds : StoryWaves.StripSeconds;
        if (hold.Ticks < (int)(seconds * TicksPerSecond))
            return true;
        waves.Holds.Remove(holder.Value);
        if (what.StartsWith(MountTarget, StringComparison.Ordinal))
            Mount(run, new PlayerId(int.Parse(what[MountTarget.Length..], CultureInfo.InvariantCulture)));
        else
            Strip(run, int.Parse(what[StoryMarks.WreckTarget.Length..], CultureInfo.InvariantCulture));
        return false;
    }

    /// <summary>Whether a hold of Use keeps the Locator where it stands (the armoury's holds as a revive does).</summary>
    private static bool ArmouryHoldsStill(string what) =>
        what.StartsWith(MendTarget, StringComparison.Ordinal) || what.StartsWith(MountTarget, StringComparison.Ordinal)
        || what.StartsWith(StoryMarks.WreckTarget, StringComparison.Ordinal);

    private bool Mend(StoryRun run, PlayerId holder, string what, StoryWaveHold hold)
    {
        var seat = new PlayerId(int.Parse(what[MendTarget.Length..], CultureInfo.InvariantCulture));
        if (HeroOf(seat) is not { } grunt)
            return false;
        // Nothing while a hit has just broken it; then 10 % a second, armour too.
        if (hold.Ticks <= 1)
            return true;
        AddHitPoints(grunt, grunt.MaxHitPoints * StoryWaves.MendRate * Dt);
        if (grunt.Armour < grunt.MaxArmour)
            grunt.ArmourRegen += grunt.MaxArmour * StoryWaves.MendRate * Dt;
        if (grunt.HitPoints < grunt.MaxHitPoints || grunt.Armour < grunt.MaxArmour)
            return true;
        run.Waves.Holds.Remove(holder.Value);
        run.Waves.Mends++;
        StoryEvents.Cue(this, $"mended {seat.Value}", grunt.Position, grunt.Id);
        if (run.Seats.IndexOf(seat) == 1 && Story is { } story && story.Crew.Of(seat).Said.Add(Lines.IkeMended))
            new Then.SayToSeat(Lines.IkeMended, 1).Do(this, run, run.Director);
        return false;
    }

    /// <summary>The Bolter L-7 carries goes into a Grunt's socket (an emergency mount: seals cut, no feed; the pod's landing tears it off).</summary>
    private void Mount(StoryRun run, PlayerId seat)
    {
        if (run.Waves.Carried is null || !MountBolter(seat))
            return;
        run.Waves.Carried = null;
        run.Waves.Mounted.Add(seat.Value);
        var index = run.Seats.IndexOf(seat);
        StoryEvents.Cue(this, $"mounted {seat.Value}", HeroOf(seat)?.Position ?? default, HeroOf(seat)?.Id ?? 0);
        new Then.SayToSeat(index == 1 ? Lines.IkeArmed : Lines.BobMounted, index).Do(this, run, run.Director);
    }

    /// <summary>A Bolter put into the first free socket of a Grunt's that takes it; false when it has none (or is no hero).</summary>
    internal bool MountBolter(PlayerId seat)
    {
        if (Wired?.Heroes.GetValueOrDefault(seat) is not { } state || HeroOf(seat) is not { } grunt || grunt.Weapons.Count > 0)
            return false;
        var item = state.Add(WiredKind.Weapon, StoryWaves.Bolter, 0);
        var slot = Enum.GetValues<Mounting>().Select(WiredSlot.Weapon)
            .FirstOrDefault(s => state.Loadout.At(s) is null && state.Loadout.WhyNotFit(item, s) is null);
        if (slot is null)
            return false;
        Wear(state, state.Loadout.With(slot, item));
        return true;
    }

    /// <summary>A wreck stripped: its Bolter carried, or its marked part into the stash (StoryRun.Salvage; the view saves it at once).</summary>
    private void Strip(StoryRun run, int id)
    {
        var waves = run.Waves;
        if (!waves.Wrecks.TryGetValue(id, out var wreck) || StripYield(run, wreck) is not { } yield)
            return;
        if (yield == StoryWaves.Bolter)
        {
            wreck.Stripped = true;
            waves.Carried = StoryWaves.Bolter;
            waves.Stripped++;
            run.Flags.Add(CarriedOnce);
            StoryEvents.Cue(this, $"stripped {id} bolter", wreck.Position);
        }
        else
        {
            wreck.Marked = null;
            wreck.Stripped = true;
            run.Salvage.Add(yield);
            StoryEvents.Cue(this, $"salvaged {yield}", wreck.Position);
        }
        // The first strip of all: what L-7 thinks of whom it strips.
        if (run.Flags.Add("armoury:stripped"))
            new Then.Say(Lines.L7Strip, StoryAudience.Inner, 0).Do(this, run, run.Director);
    }

    /// <summary>Registers the wreck of a wave unit as it drops (GameWorld.Debris): what it was, for what stripping it yields.</summary>
    private void StoryWreckDropped(Machine machine, int debris, Vector2 at)
    {
        if (Story is not { } run || !run.Waves.Foes.TryGetValue(machine.Id, out var foe))
            return;
        run.Waves.Wrecks[debris] = new StoryWreck(foe.Kind, foe.Wave, at);
        StoryEvents.Cue(this, $"wreck {debris} {foe.Kind}", at, machine.Id);
    }

    /// <summary>A wreck laid down for a checkpoint's set-up (the batches before it), as the run played through would have left it.</summary>
    internal void StoryWreckAt(StoryWaveKind kind, int wave, Vector2 at, bool stripped)
    {
        var id = _nextDebrisId++;
        var radius = kind is StoryWaveKind.Tank or StoryWaveKind.TankZap ? 1.6f : 1.5f;
        Debris.Add(new DebrisState(id, Story?.Options.Ship ?? Race.Red, at, 1, (at - new Vector2(radius), at + new Vector2(radius)), Time, float.PositiveInfinity));
        if (Story is { } run)
            run.Waves.Wrecks[id] = new StoryWreck(kind, wave, at) { Stripped = stripped };
    }

    /// <summary>A Grunt of the computer's goes for a foe it sees (CompanionBot.FireIfArmed): its order once, not again every look.</summary>
    internal void CompanionAttack(Machine grunt, Actor foe)
    {
        if (grunt.Order is AttackOrder attack && attack.Target == foe.Id)
            return;
        Send(grunt, new AttackOrder(foe.Id), foe.Position);
    }

    /// <summary>Whether the Locator holds an armoury Use on this Grunt (mend or mount): the Grunt holds still for it.</summary>
    public bool BeingWorkedOn(StoryRun run, PlayerId grunt) =>
        run.Crew.Holding.TryGetValue(run.Locator.Value, out var what)
        && (what == MendTarget + grunt.Value.ToString(CultureInfo.InvariantCulture) || what == MountTarget + grunt.Value.ToString(CultureInfo.InvariantCulture));
}
