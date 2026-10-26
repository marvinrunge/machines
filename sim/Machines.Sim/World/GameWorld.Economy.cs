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

    private void UpdateMine(Construction mine)
    {
        if (mine.MineralSite is not { } siteId)
            return;
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
        var spawn = FreePointNear(factory);
        var machine = AddMachine(factory.Race, spawn, key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo);
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
                var p = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
                if (Nav.IsFree(p, crossesWater: false) && !Machines.Any(m => Vector2.DistanceSquared(m.Position, p) < 9))
                    return p;
            }
        }
        return centre;
    }
}
