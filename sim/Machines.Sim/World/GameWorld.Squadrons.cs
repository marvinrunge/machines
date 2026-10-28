using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- squadrons led by an administrator (MachLogSquadron, the admin operations) --------------

    /// <summary>A squadron's attack its commander keeps going: the target, and where it stood when last chased.</summary>
    private sealed record SquadAttack(int Commander, int Target, Vector2 LastSeen);

    private readonly Dictionary<(Race Race, int Squad), SquadAttack> _squadAttacks = [];

    /// <summary>
    /// A squadron's commander: its administrator of the highest level, the first of them if several
    /// (MachLogSquadron::addToControl); null when it has none.
    /// </summary>
    public Machine? CommanderOf(Race race, int squad) => Machines
        .Where(m => m.Race == race && m.Squad == squad && !m.IsDead && m.Spec.Type == "ADMINISTRATOR")
        .OrderByDescending(m => m.Stats.HardwareLevel).ThenBy(m => m.Id)
        .FirstOrDefault();

    /// <summary>
    /// The admin version of an order (MachLogAdminMoveTo, AdminAttack, AdminHeal, AdminSuperConstruct and
    /// AdminLocate operations): the whole squadron moves together; its fighters take on the target in turn and
    /// chase it; its technicians heal; its constructors build, repair, capture or take apart while its fighters
    /// escort them; its locators search while the fighters go along. Anything else goes to the members as it is.
    /// </summary>
    private string? ApplySquadron(SquadronCommand squadron)
    {
        var members = Machines.Where(m => m.Race == squadron.Race && m.Squad == squadron.Squad && !m.IsDead
            && (m.Inside is not { } building || Find<Construction>(building)?.Garrisoned.Contains(m.Id) == true)).ToList();
        if (members.Count == 0)
            return "that squadron has no machines";
        var ids = members.Select(m => m.Id).ToList();
        _squadAttacks.Remove((squadron.Race, squadron.Squad));
        if (CommanderOf(squadron.Race, squadron.Squad) is not { } commander)
            return Apply(Retarget(squadron.Order, ids));

        // Machines standing their ground are left out of a squadron's orders (MachLogAdminAttackOperation).
        var free = members.Where(m => m.Order is not StandGroundOrder).ToList();
        List<int> Of(Func<Machine, bool> which) => free.Where(which).Select(m => m.Id).ToList();
        List<int> Fighters() => Of(m => m.Spec.Type is "AGGRESSOR" or "ADMINISTRATOR" && m.CanFight);

        switch (squadron.Order)
        {
            case MoveCommand move:
                return Apply(move with { Machines = free.Select(m => m.Id).ToList() });

            case AttackCommand attack:
            {
                if (Find(attack.Target) is not { } target || !AreEnemies(squadron.Race, target.Race))
                    return "no enemy there";
                _squadAttacks[(squadron.Race, squadron.Squad)] = new SquadAttack(commander.Id, target.Id, target.Position);
                DispatchSquadAttack(squadron.Race, squadron.Squad, chaseAll: true);
                return null;
            }

            case HealCommand heal:
                Apply(heal with { Machines = Of(m => m.CanHeal && m.Id != heal.Target) });
                return null;

            case ConstructCommand or RepairCommand or CaptureCommand or DeconstructCommand:
            {
                var constructors = Of(m => m.Spec.Type == "CONSTRUCTOR");
                var (job, building) = squadron.Order switch
                {
                    ConstructCommand c => ((Command)(c with { Constructors = constructors }), c.Construction),
                    RepairCommand r => (r with { Constructors = constructors }, r.Building),
                    CaptureCommand c => (c with { Constructors = constructors }, c.Building),
                    DeconstructCommand d => (d with { Constructors = constructors }, d.Building),
                    _ => throw new InvalidOperationException(),
                };
                var why = Apply(job);
                if (why is null && Find(building) is { } site)
                    Apply(new MoveCommand(squadron.Race, Fighters(), site.Position));
                return why;
            }

            case LocateCommand locate:
            {
                var why = Apply(locate with { Locators = Of(m => m.Spec.Type == "GEO_LOCATOR") });
                if (why is null)
                    Apply(new MoveCommand(squadron.Race, Fighters(), locate.Target));
                return why;
            }

            default:
                return Apply(Retarget(squadron.Order, ids));
        }
    }

    /// <summary>The same order for other machines: those of the squadron, in place of the ones it named.</summary>
    private static Command Retarget(Command order, IReadOnlyList<int> ids) => order switch
    {
        MoveCommand c => c with { Machines = ids },
        AttackCommand c => c with { Machines = ids },
        AttackMoveCommand c => c with { Machines = ids },
        StopCommand c => c with { Machines = ids },
        ConstructCommand c => c with { Constructors = ids },
        LocateCommand c => c with { Locators = ids },
        TransportCommand c => c with { Carriers = ids },
        PatrolCommand c => c with { Machines = ids },
        HealCommand c => c with { Machines = ids },
        RepairCommand c => c with { Constructors = ids },
        CaptureCommand c => c with { Constructors = ids },
        DeconstructCommand c => c with { Constructors = ids },
        RecycleCommand c => c with { Machines = ids },
        StandGroundCommand c => c with { Machines = ids },
        DefConCommand c => c with { Machines = ids },
        DropLandMinesCommand c => c with { Spies = ids },
        RefillLandMinesCommand c => c with { Spies = ids },
        CamouflageCommand c => c with { Spies = ids },
        ScavengeCommand c => c with { Scavengers = ids },
        SelfDestructCommand c => c with { Actors = ids },
        _ => order,
    };

    /// <summary>Every five seconds a squadron's attack goes on (MachLogAdminAttackOperation::doUpdate).</summary>
    private void UpdateSquadAttacks()
    {
        if (_squadAttacks.Count == 0 || Tick % 100 != 0)
            return;
        foreach (var (race, squad) in _squadAttacks.Keys.ToList())
            DispatchSquadAttack(race, squad, chaseAll: false);
    }

    /// <summary>
    /// Each fighter of the squadron without a target of its own attacks if it is within 100 m of the target or has
    /// it in range; the others drive after it, again whenever it has moved off by nearly half their distance from
    /// it. The attack ends with the target or the commander.
    /// </summary>
    private void DispatchSquadAttack(Race race, int squad, bool chaseAll)
    {
        var key = (race, squad);
        var attack = _squadAttacks[key];
        if (Find<Machine>(attack.Commander) is not { IsDead: false } commander || commander.Squad != squad
            || Find(attack.Target) is not { IsDead: false } target || !AreEnemies(race, target.Race))
        {
            _squadAttacks.Remove(key);
            return;
        }
        var chasers = new List<Machine>();
        foreach (var machine in Machines.Where(m => m.Race == race && m.Squad == squad && !m.IsDead && m.CanFight && m.Id != target.Id
            && m.Order is not StandGroundOrder and not EvadeOrder && m.Inside is null).ToList())
        {
            if (machine.Order is AttackOrder)
                continue;
            var distance = Vector2.Distance(machine.Position, target.Position);
            if (distance < 100 || distance <= machine.Weapons.Where(w => w.DealsDamage).Select(w => w.Spec.Range).DefaultIfEmpty(0).Max())
                Send(machine, new AttackOrder(target.Id), target.Position);
            else if (chaseAll || Vector2.Distance(attack.LastSeen, target.Position) / distance > 0.45f)
                chasers.Add(machine);
        }
        if (chasers.Count > 0)
        {
            foreach (var (machine, destination) in GroupMove.Destinations(chasers, target.Position, Nav))
                Send(machine, new MoveOrder(destination), destination, target.Position);
            _squadAttacks[key] = attack with { LastSeen = target.Position };
        }
    }
}
