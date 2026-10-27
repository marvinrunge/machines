using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    private void UpdateEconomy()
    {
        foreach (var building in Constructions.Where(c => c.Complete).ToList())
        {
            switch (building.Spec.Type)
            {
                case "POD":
                    // The pod mines a trickle on its own: EXTRACTION_RATE per minute straight into the bank.
                    AddBmu(building.Race, (building.Stats.ExtractionRate ?? 0) / 60f * Dt);
                    break;

                case "MINE":
                    UpdateMine(building);
                    break;

                case "FACTORY":
                    UpdateFactory(building);
                    break;

                case "HARDWARE_LAB":
                    UpdateLab(building);
                    break;
            }
        }

        RegrowArmour();
    }

    /// <summary>
    /// Whether a building is at work, so its working animation plays, as the original switched it on
    /// (MachLog*::update): factories while they produce, labs while they research, mines while a site fills
    /// them, smelters for ten seconds after ore arrives; beacons, garrisons, pods and missile emplacements once
    /// they stand.
    /// </summary>
    public bool IsWorking(Construction building) => building.Complete && building.Spec.Type switch
    {
        "FACTORY" => building.ProductionQueue.Count > 0,
        "HARDWARE_LAB" or "SOFTWARE_LAB" => building.ResearchQueue.Count > 0,
        "MINE" => building.MineralSite is not null && building.Ore < (building.Stats.Capacity ?? 0),
        "SMELTER" => building.OreDroppedTick is { } dropped && Tick - dropped < 10 * TicksPerSecond,
        _ => true,
    };

    private void UpdateMine(Construction mine)
    {
        // A mine whose site ran dry moves on to another in reach, and one without a site takes one found later
        // (MachLogMine::mineralSiteIsExhausted, newMineralSiteAvailable); it looks once a second.
        if (mine.MineralSite is not { } siteId || Sites[siteId].Ore <= 0)
        {
            if (Tick % TicksPerSecond != mine.Id % TicksPerSecond || !AssignMineralSite(mine))
                return;
            siteId = mine.MineralSite!.Value;
        }
        var site = Sites[siteId];
        var room = (mine.Stats.Capacity ?? 0) - mine.Ore;
        var extracted = MathF.Min(MathF.Min((mine.Stats.ExtractionRate ?? 0) / 60f * Dt, room), site.Ore);
        if (extracted <= 0)
            return;
        mine.Ore += extracted;
        site.Ore -= extracted;
    }

    private void UpdateFactory(Construction factory)
    {
        if (factory.ProductionQueue.Count == 0)
            return;

        var key = factory.ProductionQueue[0];
        var item = Data.ResearchTree.Find(key);
        if (item is null)
        {
            factory.ProductionQueue.RemoveAt(0);
            return;
        }

        // BUILD_RATES BMUs per minute, paid as the machine is built; the price is the research tree's factory price.
        var price = ResearchTree.FactoryPrice(item, Data.Parm);
        var wanted = MathF.Min((factory.Stats.BuildRate ?? 75) / 60f * Dt, price - factory.ProductionSpent);
        factory.ProductionSpent += SpendBmu(factory.Race, wanted);
        if (factory.ProductionSpent < price)
            return;

        factory.ProductionQueue.RemoveAt(0);
        factory.ProductionSpent = 0;
        // The new machine appears on the pad before the door (MachLogFactory::createNewMachine), unless something
        // stands there, and then walks off it so the next one has room (moveToDeployPoint).
        var pad = factory.EntranceOutside is { } outside && PadClear(outside) ? outside : (Vector2?)null;
        var spawn = pad ?? FreePointNear(factory);
        var machine = AddMachine(factory.Race, spawn, key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo);
        // To the assembly point if one is set, else a little way out from the door.
        if (factory.AssemblyPoint is { } assembly)
        {
            var spot = FreeSpotNear(assembly, MathF.Max(1, machine.Stats.LowClearance));
            Send(machine, new MoveOrder(spot), spot);
        }
        else if (pad is { } at)
        {
            var away = at - factory.Position;
            var deploy = at + (away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX) * 25;
            Send(machine, new MoveOrder(deploy), deploy);
        }
        Raise(new GameEvent(GameEventKind.MachineBuilt, machine.Id, spawn, factory.Id, key.ToString()));
    }

    private void UpdateLab(Construction lab)
    {
        // Technicians who died inside are gone.
        lab.Technicians.RemoveAll(id => Find<Machine>(id) is null or { IsDead: true });
        while (lab.ResearchQueue.Count > 0 && Races[lab.Race].Researched.Contains(lab.ResearchQueue[0]))
            lab.ResearchQueue.RemoveAt(0);
        if (lab.ResearchQueue.Count == 0 || lab.Technicians.Count == 0)
            return;

        var state = Races[lab.Race];
        var key = lab.ResearchQueue[0];
        var item = Data.ResearchTree.Get(key);
        var cost = ResearchCost(lab.Race, item);

        // RESEARCH_RATE research units per minute, summed over the technicians inside.
        var units = lab.Technicians.Select(Find<Machine>).OfType<Machine>().Sum(t => t.Stats.ResearchRate ?? 0) / 60f * Dt;
        state.ResearchProgress.TryGetValue(key, out var progress);
        units = MathF.Min(units, MathF.Max(0, cost - progress));

        // BUILDING_COST is drawn in step with progress. The original let research run on an empty bank
        // (a stray semicolon in hwlab.cpp); the remake pauses it instead.
        var bmu = cost == 0 ? 0 : item.BuildingCost * units / cost;
        if (bmu > state.Bmu)
            return;
        state.Bmu -= bmu;
        state.Score.ResearchUnits += units;
        progress += units;
        state.ResearchProgress[key] = progress;

        if (progress >= cost)
        {
            state.Researched.Add(key);
            lab.ResearchQueue.RemoveAt(0);
            Raise(new GameEvent(GameEventKind.ResearchComplete, lab.Id, lab.Position, Detail: key.ToString()));
        }
    }

    private void RegrowArmour()
    {
        // Armour grows back by REPAIR_RATE per second up to its maximum; hit points do not.
        foreach (var machine in Machines)
        {
            if (machine.Armour >= machine.MaxArmour)
                continue;
            machine.ArmourRegen += machine.Stats.RepairRate * Dt;
            var whole = (int)machine.ArmourRegen;
            if (whole > 0)
            {
                machine.Armour = Math.Min(machine.MaxArmour, machine.Armour + whole);
                machine.ArmourRegen -= whole;
            }
        }
    }

    /// <summary>A free spot just outside a building for a newly built machine.</summary>
    /// <summary>
    /// The nearest place to <paramref name="point"/> that no machine stands on or is heading for
    /// (MachLogSpacialManipulation::getNearestFreeSpacePoint), searched ring by ring.
    /// </summary>
    private Vector2 FreeSpotNear(Vector2 point, float clearance)
    {
        var taken = Machines.Where(m => m.Inside is null)
            .SelectMany(m => m.Path.Count > 0 ? new[] { m.Position, m.Path[^1] } : [m.Position])
            .ToList();
        bool Free(Vector2 p) => Nav.IsFree(p, crossesWater: false) && taken.All(t => Vector2.DistanceSquared(t, p) >= 4 * clearance * clearance);
        if (Free(point))
            return point;
        for (var ring = 1; ring <= 10; ring++)
        {
            var distance = ring * 2.5f * clearance;
            var steps = 6 * ring;
            for (var step = 0; step < steps; step++)
            {
                var angle = step * MathF.Tau / steps;
                var candidate = point + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
                if (Free(candidate))
                    return candidate;
            }
        }
        return point;
    }

    /// <summary>A factory pad nothing stands on (the original tested it with a short motion chunk, 5 m clear).</summary>
    private bool PadClear(Vector2 pad) =>
        Nav.IsFree(pad, crossesWater: false) && !Machines.Any(m => m.Inside is null && Vector2.DistanceSquared(m.Position, pad) < 5 * 5);

    private Vector2 FreePointNear(Construction building)
    {
        var (min, max) = building.Footprint;
        var centre = (min + max) / 2;
        for (var ring = 1; ring < 12; ring++)
        {
            var distance = (max - min).Length() / 2 + ring * 4;
            for (var step = 0; step < 16; step++)
            {
                var angle = MathF.PI / 2 + step * MathF.Tau / 16;
                var p = centre + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * distance;
                if (Nav.IsFree(p, crossesWater: false) && !Machines.Any(m => Vector2.DistanceSquared(m.Position, p) < 9))
                    return p;
            }
        }
        return centre;
    }
}
