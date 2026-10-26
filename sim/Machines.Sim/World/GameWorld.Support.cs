using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// Healing, repair, capture, deconstruction, recycling, garrisons, self-destruct and treachery, with the
/// original's rates (machlog: charger, oprepair, opcaptur, opdecons, oprecycl, garrison, actor, trechorb).
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>MachGuiSelfDestructCommand: the charge goes off 5 seconds after the order.</summary>
    public const float SelfDestructDelay = 5f;

    // MachLogStatsImpl defaults (stats.ini does not change them).
    public const float FriendlyDeconstructMultiplier = 3f;
    public const float EnemyDeconstructMultiplier = 1f;
    public const float EnemyCaptureMultiplier = 2f;
    public const float RecycleRefund = 0.5f;

    /// <summary>A building is taken over once its hit points drop below this share (opcaptur.cpp).</summary>
    public const float CaptureRatio = 1f / 3f;

    /// <summary>Hit point fractions that did not yet add up to a whole point.</summary>
    private readonly Dictionary<int, float> _hitPointCarry = [];

    private static bool IsConstructor(Machine machine) => machine.Spec.Type == "CONSTRUCTOR";

    private string? OrderWork(Race race, IReadOnlyList<int> ids, int targetId, Func<Machine, bool> able, string noneAble,
        Func<Actor, string?> whyNotTarget, Func<int, Order> order)
    {
        var target = Find(targetId);
        if (target is null || target.IsDead)
            return "nothing there";
        if (whyNotTarget(target) is { } why)
            return why;
        var workers = Own<Machine>(race, ids).Where(able).Where(m => m.Id != targetId).ToList();
        if (workers.Count == 0)
            return noneAble;
        foreach (var worker in workers)
            Send(worker, order(target.Id), target is Construction building ? ClosestPointOutside(building, worker.Position) : target.Position);
        return null;
    }

    private string? Recycle(RecycleCommand recycle)
    {
        var machines = Own<Machine>(recycle.Race, recycle.Machines).ToList();
        if (machines.Count == 0)
            return "nothing selected";
        foreach (var machine in machines)
        {
            var smelter = recycle.Smelter is { } id
                ? Find<Construction>(id)
                : Constructions.Where(c => c.Race == recycle.Race && c.Complete && c.Spec.Type is "SMELTER" or "POD")
                    .MinBy(c => Vector2.DistanceSquared(c.Position, machine.Position));
            if (smelter is null || smelter.Race != recycle.Race || smelter.Spec.Type is not ("SMELTER" or "POD"))
                return "recycling needs one of your smelters or pods";
            Send(machine, new RecycleOrder(smelter.Id), ClosestPointOutside(smelter, machine.Position));
        }
        return null;
    }

    /// <summary>What recycling a machine pays back: half of what its factory charged (machineOriginalCost).</summary>
    public int RecycleValue(Machine machine)
    {
        var item = Data.ResearchTree.Find(new ResearchKey(machine.Spec.Type, machine.Spec.SubType, machine.Stats.HardwareLevel, machine.WeaponCombo));
        var price = item is null ? machine.Cost : ResearchTree.FactoryPrice(item, Data.Parm);
        return (int)(price * RecycleRefund);
    }

    // ---- per-tick behaviour ---------------------------------------------------------------------

    private void UpdateSupport()
    {
        foreach (var actor in Actors.Where(a => a.SelfDestructAt is { } at && at <= Time && !a.IsDead).ToList())
        {
            actor.SelfDestructAt = null;
            actor.LastHitBy = null;
            actor.HitPoints = 0;
        }

        // Garrisons repair everyone inside at their REPAIR_RATE hit points per minute.
        foreach (var garrison in Constructions.Where(c => c.IsType("GARRISON") && c.Garrisoned.Count > 0))
        {
            garrison.Garrisoned.RemoveAll(id => Find<Machine>(id) is null);
            foreach (var machine in garrison.Garrisoned.Select(Find<Machine>).OfType<Machine>())
                AddHitPoints(machine, (garrison.Stats.RepairRate ?? 0) / 60f * Dt);
        }
    }

    private void UpdateHeal(Machine healer, HealOrder heal)
    {
        var target = Find<Machine>(heal.Target);
        var charger = healer.Weapons.FirstOrDefault(w => w.IsHealer);
        if (target is null || target.IsDead || target.Race != healer.Race || target.Inside is not null || charger is null
            || target.HitPoints >= target.MaxHitPoints)
        {
            healer.Order = Order.Idle;
            return;
        }

        if (healer.DistanceTo(target) > charger.Spec.Range * 0.95f)
        {
            Approach(healer, target.Position);
            return;
        }
        healer.Path.Clear();
        healer.PathIndex = 0;
        Face(healer, target.Position);
        // MachLogSuperCharger: DAMAGE hit points per second while the beam holds.
        AddHitPoints(target, charger.Spec.Damage * Dt);
    }

    /// <summary>Idle healers look after damaged machines nearby, as the original's administrators did.</summary>
    private void SeekPatients(Machine healer)
    {
        if (Tick % 10 != healer.Id % 10)
            return;
        var patient = Machines
            .Where(m => m.Race == healer.Race && m.Id != healer.Id && m.Inside is null && m.HitPoints < m.MaxHitPoints && !m.IsDead)
            .Where(m => healer.DistanceTo(m) <= healer.Stats.ScannerRange)
            .MinBy(m => healer.DistanceTo(m));
        if (patient is not null)
            Send(healer, new HealOrder(patient.Id), patient.Position);
    }

    private bool AtBuilding(Machine machine, Construction building)
    {
        if (InReach(machine, building))
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            Face(machine, building.Position);
            return true;
        }
        Approach(machine, ClosestPointOutside(building, machine.Position));
        return false;
    }

    private void UpdateRepair(Machine constructor, RepairOrder repair)
    {
        var building = Find<Construction>(repair.Building);
        if (building is null || building.Race != constructor.Race || building.HitPoints >= building.MaxHitPoints)
        {
            constructor.Order = Order.Idle;
            return;
        }
        if (!AtBuilding(constructor, building))
            return;

        // oprepair.cpp: CONSTRUCTION_RATE BMUs per minute from the bank, turned into hit points in
        // proportion to the building's cost.
        var bmu = SpendBmu(constructor.Race, (constructor.Stats.ConstructionRate ?? 0) / 60f * Dt);
        AddHitPoints(building, HitPointValue(building, bmu));
    }

    private void UpdateCapture(Machine constructor, CaptureOrder capture)
    {
        var building = Find<Construction>(capture.Building);
        if (building is null || !AreEnemies(constructor.Race, building.Race))
        {
            constructor.Order = Order.Idle;
            return;
        }
        if (!AtBuilding(constructor, building))
            return;

        var bmu = (constructor.Stats.ConstructionRate ?? 0) * EnemyCaptureMultiplier / 60f * Dt;
        // Never wears the building down past what capturing needs (0.3 of its hit points).
        var floor = 0.3f * building.MaxHitPoints;
        var removed = MathF.Min(HitPointValue(building, bmu), MathF.Max(0, building.HitPoints - floor));
        AddHitPoints(building, -removed);
        if (building.HitPoints < building.MaxHitPoints * CaptureRatio)
        {
            TakeOver(building, constructor.Race);
            constructor.Order = Order.Idle;
        }
    }

    private void UpdateDeconstruct(Machine constructor, DeconstructOrder deconstruct)
    {
        var building = Find<Construction>(deconstruct.Building);
        if (building is null || building.IsDead)
        {
            constructor.Order = Order.Idle;
            return;
        }
        if (!AtBuilding(constructor, building))
            return;

        var own = building.Race == constructor.Race;
        var bmu = (constructor.Stats.ConstructionRate ?? 0) * (own ? FriendlyDeconstructMultiplier : EnemyDeconstructMultiplier) / 60f * Dt;
        var hitPoints = HitPointValue(building, bmu);
        // opdecons.cpp: whatever comes off is cashed in at the recycle refund.
        if (hitPoints > building.HitPoints)
        {
            bmu *= building.HitPoints / hitPoints;
            hitPoints = building.HitPoints;
        }
        if (!own)
            building.LastHitBy = constructor.Race;
        AddHitPoints(building, -hitPoints);
        AddBmu(constructor.Race, bmu * RecycleRefund);
        if (building.IsDead)
            constructor.Order = Order.Idle;
    }

    private void UpdateRecycle(Machine machine, RecycleOrder recycle)
    {
        var smelter = Find<Construction>(recycle.Smelter);
        if (smelter is null || smelter.Race != machine.Race || !smelter.Complete)
        {
            machine.Order = Order.Idle;
            return;
        }
        if (!AtBuilding(machine, smelter))
            return;

        AddBmu(machine.Race, RecycleValue(machine));
        Raise(new GameEvent(GameEventKind.Recycled, machine.Id, machine.Position, smelter.Id, machine.Spec.TypeName));
        RemoveQuietly(machine);
    }

    private void UpdateEnterGarrison(Machine machine, EnterGarrisonOrder enter)
    {
        var garrison = Find<Construction>(enter.Garrison);
        if (garrison is null || !garrison.Complete || garrison.Race != machine.Race)
        {
            machine.Order = Order.Idle;
            return;
        }
        if (!AtBuilding(machine, garrison))
            return;

        machine.Order = Order.Idle;
        if (garrison.Garrisoned.Count >= GarrisonStations(garrison))
            return;
        machine.Inside = garrison.Id;
        machine.Position = garrison.Position;
        garrison.Garrisoned.Add(machine.Id);
    }

    /// <summary>How many machines fit in a garrison: its <c>STATION ... PARK</c> places in parmdata.</summary>
    public static int GarrisonStations(Construction garrison) =>
        Math.Max(1, garrison.Stats.Geometry.Count(g => g.Keyword == "STATION" && g.Args[^1] == "PARK"));

    /// <summary>Brings a garrisoned machine out by the door; does nothing for anyone else.</summary>
    private void LeaveGarrison(Machine machine)
    {
        if (machine.Inside is not { } id || Find<Construction>(id) is not { } garrison || !garrison.IsType("GARRISON"))
            return;
        garrison.Garrisoned.Remove(machine.Id);
        machine.Inside = null;
        machine.Position = DoorOf(garrison, machine);
    }

    /// <summary>A free spot outside the building on its entrance side (the original doors face +x before rotation).</summary>
    private Vector2 DoorOf(Construction building, Machine machine)
    {
        var angle = building.Orientation * MathF.PI / 180;
        var outward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var far = building.Position + outward * (building.Radius + 20);
        for (var ring = 0; ring < 6; ring++)
        {
            var candidate = ClosestPointOutside(building, far) + outward * (ring * Navigation.NavGrid.CellSize);
            if (Nav.IsFree(candidate, CrossesWater(machine)))
                return candidate;
        }
        return ClosestPointOutside(building, far);
    }

    /// <summary>A building changes sides: production and research stop, and whoever was inside comes out.</summary>
    private void TakeOver(Construction building, Race race)
    {
        building.Race = race;
        building.ProductionQueue.Clear();
        building.ProductionSpent = 0;
        building.ResearchQueue.Clear();
        foreach (var machine in building.Technicians.Concat(building.Garrisoned).Select(Find<Machine>).OfType<Machine>().ToList())
        {
            machine.Inside = null;
            machine.Position = DoorOf(building, machine);
        }
        building.Technicians.Clear();
        building.Garrisoned.Clear();
        _turretTargets.Remove(building.Id);
        Raise(new GameEvent(GameEventKind.RaceChanged, building.Id, building.Position, Detail: race.ToString()));
    }

    /// <summary>MachLogTreacheryOrb: a machine hit by the orb defects to the shooter's race.</summary>
    private void Defect(Machine machine, Race race)
    {
        LeaveGarrison(machine);
        machine.Race = race;
        machine.Squad = null;
        machine.SelfDestructAt = null;
        machine.Order = Order.Idle;
        machine.Path.Clear();
        machine.PathIndex = 0;
        Raise(new GameEvent(GameEventKind.RaceChanged, machine.Id, machine.Position, Detail: race.ToString()));
    }

    /// <summary>Hit points for this many BMUs of the building (MachLogConstruction::hitPointValueOfBMUs).</summary>
    private static float HitPointValue(Construction building, float bmu) =>
        building.Stats.Cost <= 0 ? 0 : bmu / building.Stats.Cost * building.MaxHitPoints;

    /// <summary>Adds (or removes) hit points, keeping fractions until they make a whole point.</summary>
    private void AddHitPoints(Actor actor, float amount)
    {
        _hitPointCarry.TryGetValue(actor.Id, out var carry);
        carry += amount;
        var whole = (int)carry;
        _hitPointCarry[actor.Id] = carry - whole;
        actor.HitPoints = Math.Clamp(actor.HitPoints + whole, 0, actor.MaxHitPoints);
    }
}
