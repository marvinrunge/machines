using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    /// <summary>Seconds wreckage burns (MachLogDebris's BURN_TIME); with a scavenger about it lasts longer.</summary>
    public const float DebrisBurnTime = 60;

    /// <summary>How far a scavenger looks for wreckage to gather (MachLogResourceCarrier::update, DEFCON normal), and at low DefCon.</summary>
    public const float ScavengingRange = 80, LowDefConScavengingRange = 140;

    private int _nextDebrisId = 1;

    /// <summary>Wreckage on the ground, oldest first.</summary>
    public List<DebrisState> Debris { get; } = [];

    public DebrisState? FindDebris(int id) => Debris.Find(d => d.Id == id);

    /// <summary>
    /// What a destroyed actor leaves (MachLogMobile::dropDebris, MachLogConstruction::dropDebris): a machine half
    /// its cost, unless it flew; a building a fifth of its cost by how far it was built; an artefact its whole cost
    /// (MachLogArtefact::dropDebris).
    /// </summary>
    private void DropDebris(Actor actor)
    {
        int quantity;
        (Vector2 Min, Vector2 Max) boundary;
        switch (actor)
        {
            case Artefact artefact:
                quantity = artefact.Cost;
                boundary = (artefact.Position - new Vector2(artefact.Radius), artefact.Position + new Vector2(artefact.Radius));
                break;
            case Machine { Stats.Locomotion: not Locomotion.Glider } machine:
                quantity = machine.Stats.Cost / 2;
                boundary = (machine.Position - new Vector2(machine.Radius), machine.Position + new Vector2(machine.Radius));
                break;
            case Construction building:
                var percentComplete = (int)(100 * building.Built / Math.Max(1, building.Stats.Cost));
                quantity = (int)(building.Stats.Cost * percentComplete / 100f / 5);
                boundary = building.Footprint;
                break;
            default:
                return;
        }
        if (quantity <= 0)
            return;
        // It burns out after a minute, or, while any race has a scavenger, two seconds more per BMU
        // (MachLogDebris::setLifeTime).
        var lifetime = Machines.Any(m => m.IsScavenger) ? DebrisBurnTime + quantity * 2 : DebrisBurnTime;
        Debris.Add(new DebrisState(_nextDebrisId++, actor.Race, actor.Position, quantity, boundary, Time, Time + lifetime));
    }

    /// <summary>Wreckage that has burnt out is gone.</summary>
    private void BurnOutDebris() => Debris.RemoveAll(d => d.ExpiresAt <= Time);

    /// <summary>
    /// A scavenger drives onto each wreck in turn, within two metres of its middle, and gathers it: its BMUs go
    /// straight to the race's bank, what does not fit is lost (MachLogScavengeOperation::doUpdate).
    /// </summary>
    private void UpdateScavenge(Machine scavenger, ScavengeOrder scavenge)
    {
        var wreck = scavenge.Debris.Select(FindDebris).FirstOrDefault(d => d is not null);
        if (wreck is null)
        {
            if (scavenge.Back is { } back)
                TakeUpAgain(scavenger, back);
            else
                scavenger.Order = Order.Idle;
            return;
        }
        if (Vector2.DistanceSquared(scavenger.Position, wreck.Position) > 4)
        {
            Approach(scavenger, wreck.Position);
            return;
        }
        AddBmu(scavenger.Race, wreck.Quantity);
        Debris.Remove(wreck);
        scavenger.Path.Clear();
        scavenger.PathIndex = 0;
        Raise(new GameEvent(GameEventKind.Scavenged, scavenger.Id, wreck.Position, wreck.Id, wreck.Quantity.ToString()));
    }

    /// <summary>
    /// A scavenger looks about every second and a half, whatever it is doing, unless its virtual DefCon is high or it is
    /// evading, standing its ground or gathering already: the nearest wreck within 80 m, 140 m at low DefCon, that it can
    /// stand on draws it off, and it takes up what it was doing afterwards, an idle one going back to where it stood
    /// (MachLogResourceCarrier::update, MachLogStrategy::changeToScavengeMode, MachLogAutoScavengeOperation).
    /// </summary>
    private void SeekDebris(Machine scavenger)
    {
        if (Debris.Count == 0 || Tick % 30 != scavenger.Id % 30 || scavenger.Inside is not null || scavenger.DrivenByPlayer
            || scavenger.Order is EvadeOrder or StandGroundOrder or ScavengeOrder or EnterGarrisonOrder || VirtualDefCon(scavenger) == DefCon.High)
            return;
        var range = scavenger.DefCon == DefCon.Low ? LowDefConScavengingRange : ScavengingRange;
        var water = CrossesWater(scavenger);
        var wreck = Debris
            .Where(d => Vector2.DistanceSquared(d.Position, scavenger.Position) < range * range && Nav.IsFree(d.Position, water))
            .MinBy(d => Vector2.DistanceSquared(d.Position, scavenger.Position));
        if (wreck is null)
            return;
        var back = scavenger.Order is IdleOrder
            ? new Interrupted(new MoveOrder(scavenger.Position), scavenger.Position, false)
            : new Interrupted(scavenger.Order, scavenger.PathIndex < scavenger.Path.Count ? scavenger.Path[^1] : null, false);
        var orderedAt = scavenger.OrderedAt;
        Send(scavenger, new ScavengeOrder([wreck.Id], back), wreck.Position);
        scavenger.OrderedAt = orderedAt;
    }
}
