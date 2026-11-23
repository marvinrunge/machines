using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>Which of its parts the big Grunt plays: the armoury's fifth batch, or one of the escape's pack.</summary>
public enum StoryHeavyPart
{
    /// <summary>The armoury's boss (package W): few hit points, as many as the trio's Bolters can take in a fight of a minute or so.</summary>
    Boss,

    /// <summary>One of the escape's two pack heavies (package E): Old Iron's own toughness, which the ship's hazards take by shares.</summary>
    Pack,
}

/// <summary>
/// The big Grunt of stage 7 (plan stufe7 C16 and package G): the one machine the armoury's boss and the escape's two pack
/// heavies share, Old Iron (<see cref="Prologue.HeavyGrunt"/>, Wired's Grunt 5, drawn 1.5 times a Grunt 3 with a dozer blade)
/// with only the twin heavy Bolters. Its tune for the story: hit points and armour by the part it plays, no armour of its own
/// coming back (<see cref="MachineLevel.RepairRate"/> 0, so neither the boss's bar nor the hazards' planned shares are undone),
/// its mass for the boss bar and Bob's line, the shares of its top speed its packages drive it at, its size class on the deck,
/// and the roles it is cast in. Its body keeps Old Iron's plough (hits from the front cost <see cref="Survival.WiredBalance.PloughShare"/>),
/// but not the dash and ram Wired's Old Irons throw at what they attack by themselves (GameWorld.WiredBodies.RamAhead): the
/// story's big Grunt runs at the trio only by its package's own charge. Nothing here is the boss's charge (package W) or the pack's leash (package E); they use this. The story is the remake's own.
/// </summary>
public static class StoryHeavy
{
    /// <summary>Its mass in tonnes, as the boss bar shows it and Bob says it (P_BOB_BIG): 3.2 t.</summary>
    public const float MassTonnes = 3.2f;

    /// <summary>The boss's hit points with no Bolter on the trio's Grunts, what each Bolter one of them carries as batch 5 begins adds, and the most (design-waves).</summary>
    public const int BossHitPoints = 100, BossPerBolter = 20, BossMostHitPoints = 140;

    /// <summary>The boss's armour: what soaks the first hits (all but one point each) until it is gone.</summary>
    public const int BossArmour = 30;

    /// <summary>A pack heavy's hit points and armour: Old Iron's own (WiredMachines), the escape's hazards take shares of them.</summary>
    public const int PackHitPoints = 380, PackArmour = 200;

    /// <summary>
    /// The shares of its top speed (Old Iron's 9.5 m/s) it drives at (for <c>Pace(role, share)</c>): the boss cruises at
    /// 0.45 (4.3 m/s, just under the trio's walk) and charges at its whole speed.
    /// </summary>
    public const float Cruise = 0.45f, Charge = 1f;

    /// <summary>Its size class on the prologue's deck (<see cref="Mobility.SizeOf"/> of its radius, 3.09 m): the largest, 13 m gaps.</summary>
    public const int SizeClass = Mobility.Largest;

    /// <summary>The roles it is cast in: the armoury's boss, the escape's first heavy (the hazards' planned victim) and second (the door breaker).</summary>
    public const string BossRole = "boss", PackRole1 = "heavy1", PackRole2 = "heavy2";

    /// <summary>Whether a machine is one of the big Grunts: Old Iron, of a story.</summary>
    public static bool Is(Machine machine) =>
        machine.Spec.Type == Prologue.HeavyGrunt.Type && machine.Spec.SubType == Prologue.HeavyGrunt.SubType
        && machine.Stats.HardwareLevel == Prologue.HeavyGrunt.Level;

    /// <summary>Its hit points in a part: the boss's grow by <see cref="BossPerBolter"/> for each Bolter on the trio's Grunts, up to <see cref="BossMostHitPoints"/>.</summary>
    public static int HitPointsOf(StoryHeavyPart part, int bolters = 0) => part == StoryHeavyPart.Boss
        ? Math.Min(BossMostHitPoints, BossHitPoints + BossPerBolter * Math.Max(0, bolters))
        : PackHitPoints;

    /// <summary>Its armour in a part.</summary>
    public static int ArmourOf(StoryHeavyPart part) => part == StoryHeavyPart.Boss ? BossArmour : PackArmour;

    /// <summary>Old Iron's level as the story tunes it for a part; everything not named stays Old Iron's.</summary>
    public static MachineLevel Tuned(MachineLevel level, StoryHeavyPart part, int bolters = 0) => level with
    {
        HitPoints = HitPointsOf(part, bolters),
        Armour = ArmourOf(part),
        RepairRate = 0,
    };

    /// <summary>
    /// Tunes a big Grunt for a part: its own copy of its stats (what other machines of its type share stays as it is), hit
    /// points and armour whole at the new maximum if it was whole, else keeping their share (<see cref="Machine.Refit"/>);
    /// and its body's own dash is never ready (<see cref="WiredBody.DashReadyAt"/>), so it does not ram by itself.
    /// </summary>
    public static void Fit(GameWorld world, Machine machine, StoryHeavyPart part, int bolters = 0)
    {
        if (!Is(machine))
            throw new ArgumentException($"machine {machine.Id} is no big Grunt", nameof(machine));
        machine.Refit(machine.Spec, Tuned(machine.Stats, part, bolters), machine.WeaponCombo, machine.Weapons.ToList());
        if (world.Wired is { } run)
        {
            if (!run.Bodies.TryGetValue(machine.Id, out var body))
                run.Bodies[machine.Id] = body = new WiredBody { StillAt = machine.Position, StillSince = world.Time };
            body.DashReadyAt = float.MaxValue;
        }
    }

    /// <summary>Whether a big Grunt's body may dash by itself (Old Iron's ram): never once it is fitted, until its package frees it.</summary>
    public static bool RamsByItself(GameWorld world, Machine machine) =>
        world.Wired?.Bodies.GetValueOrDefault(machine.Id) is not { DashReadyAt: float.MaxValue };

    /// <summary>
    /// A big Grunt of the ship set down at the nearest free ground to <paramref name="at"/> and tuned for a part, standing
    /// ready as the hunters do (<see cref="Prologue.Hunters"/>). Not cast in any role: for a beat use <see cref="Then.SpawnHeavy"/>.
    /// </summary>
    public static Machine Spawn(GameWorld world, Vector2 at, StoryHeavyPart part, int bolters = 0)
    {
        var run = world.Story ?? throw new InvalidOperationException("a big Grunt belongs to a story");
        var machine = world.SpawnForStory(run.Options.Ship, at, Prologue.HeavyGrunt);
        machine.DefCon = Prologue.Hunters;
        Fit(world, machine, part, bolters);
        return machine;
    }
}

public static partial class Then
{
    /// <summary>
    /// Big Grunts of a role set down, one at each place, as <see cref="Spawn"/> sets down any machine of the cast, each tuned
    /// for its part (<see cref="StoryHeavy.Fit"/>): the armoury's boss with the trio's <paramref name="Bolters"/> counted in.
    /// </summary>
    public sealed record SpawnHeavy(string Role, StoryHeavyPart Part, IReadOnlyList<string> Places, DefCon DefCon = Prologue.Hunters, int Bolters = 0, Race? Looks = null)
        : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            var before = director.Cast.TryGetValue(Role, out var ids) ? ids.Count : 0;
            new Spawn(Role, Prologue.HeavyGrunt, Places, DefCon, Looks: Looks).Do(world, run, director);
            foreach (var id in director.Cast[Role].Skip(before))
            {
                if (world.Find<Machine>(id) is { } machine)
                    StoryHeavy.Fit(world, machine, Part, Bolters);
            }
        }
    }
}
