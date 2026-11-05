using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Presentation;

namespace Machines.Sim.World;

/// <summary>
/// What a race is told of its machines and buildings being hit, as the original culled it
/// (MachLogMachine::sufficientTimePassedSinceLastHit, MachLogConstruction::dealWithHitVoiceMail): one in a bad way
/// reports every few seconds, any other only if nothing has reported near it for a while.
/// </summary>
public sealed partial class GameWorld
{
    private const float MachineHitMailGap = 10, BuildingHitMailGap = 15;
    private const float NearDeathMailGap = 4, CriticalMailGap = 5;
    private const float NearDeathRatio = 0.35f, CriticalRatio = 0.1f;

    /// <summary>Only this far from the last one to report may another report at once.</summary>
    private const float HitMailDistance = 200;

    // The race's last hit report, for its machines and its buildings: when the next may be given, and where it was.
    private readonly Dictionary<(Race, bool Building), (float Next, Vector2 At)> _hitMails = [];

    private void ReportHit(Actor target)
    {
        var ratio = target.HitPoints / (float)Math.Max(1, target.MaxHitPoints);
        switch (target)
        {
            case Machine machine when SoundTable.VoiceOf(machine) is { } voice && TimeToReportHit(machine, ratio, NearDeathRatio, NearDeathMailGap, MachineHitMailGap):
                SendVoiceMail(machine.Race, $"VID_{voice}_{(ratio <= NearDeathRatio ? "NEAR_DEATH" : "DAMAGED")}", machine.Id);
                break;
            case Construction building when TimeToReportHit(building, ratio, CriticalRatio, CriticalMailGap, BuildingHitMailGap):
                var manned = building.Technicians.Count + building.Garrisoned.Count > 0;
                SendVoiceMail(building.Race, SoundTable.BuildingHitReport(building.Spec.Type, building.Spec.SubType, ratio < CriticalRatio, manned));
                break;
        }
    }

    private bool TimeToReportHit(Actor target, float ratio, float badRatio, float badGap, float gap)
    {
        var since = target.LastHitMail is { } last ? Time - last : float.MaxValue;
        if (ratio < badRatio)
        {
            if (since <= badGap)
                return false;
        }
        else
        {
            var key = (target.Race, target is Construction);
            var (next, at) = _hitMails.GetValueOrDefault(key);
            if (Vector2.DistanceSquared(target.Position, at) <= HitMailDistance * HitMailDistance && Time < next || since <= gap)
                return false;
            _hitMails[key] = (Time + gap - 0.5f, target.Position);
        }
        target.LastHitMail = Time;
        return true;
    }

    /// <summary>A constructor's job is done and it reports so, and that it waits for another.</summary>
    private void ReportJobDone(Machine constructor, string report)
    {
        SendVoiceMail(constructor.Race, report, constructor.Id);
        ReportAwaitingJob(constructor);
    }

    /// <summary>A constructor with no more sites in its series waits for another job, and says so (MachLogConstructOperation::doUpdate).</summary>
    private void ReportAwaitingJob(Machine constructor)
    {
        if (!_series.ContainsKey(constructor.Id))
            SendVoiceMail(constructor.Race, "VID_DOZER_AWAITING_NEW_JOB", constructor.Id);
    }

    /// <summary>
    /// A lab starting on an empty queue says how it goes: a long piece of research, an ordinary one, or that nobody is
    /// in there and nobody on the way (MachLogHardwareLab::addResearchItem).
    /// </summary>
    private void AnnounceResearch(Construction lab, ResearchItem item)
    {
        var rate = lab.Technicians.Select(Find<Machine>).OfType<Machine>().Sum(t => t.Stats.ResearchRate ?? 0);
        if (rate > 0)
            SendVoiceMail(lab.Race, ResearchCost(lab.Race, item) / rate > 4 ? "VID_TECHNICIAN_RESEARCHING_TOUGH_ONE" : "VID_TECHNICIAN_RESEARCHING");
        else if (!Machines.Any(m => m.Race == lab.Race && m.Order is EnterLabOrder entering && entering.Lab == lab.Id))
            SendVoiceMail(lab.Race, "VID_POD_NO_TECHNICIANS");
    }

    /// <summary>The route's end has gone or changed sides (MachLogResourceCarrier::giveTransportRouteFailureVoicemail).</summary>
    private void ReportRouteLost(Machine carrier) => SendVoiceMail(carrier.Race, "VID_RESOURCE_CARRIER_ROUTE_FAILURE", carrier.Id);

    /// <summary>The route's smelter is lost and another took its place (MachLogResourceCarrier::giveAlternativeDestinationVoicemail).</summary>
    private void ReportNewDestination(Machine carrier) => SendVoiceMail(carrier.Race, "VID_RESOURCE_CARRIER_ALTERNATIVE_DESTINATION", carrier.Id);

    /// <summary>
    /// A constructor better than any before it opens the list of buildings further, for good, and its race hears of
    /// it unless all it opened is locked (MachLogConstructionTree::updateAvailableConstructions).
    /// </summary>
    private void ReportNewConstructions()
    {
        foreach (var state in Races.Values)
        {
            var best = BestConstructor(state.Race);
            if (best <= state.ConstructionsReached)
                continue;
            var opened = Data.ConstructionOrder.Entries.Skip(state.ConstructionsReached + 1).Take(best - state.ConstructionsReached);
            if (opened.Any(e => !Options.RestrictConstruction || !state.Locked.Contains(e)))
                SendVoiceMail(state.Race, "VID_POD_NEW_CONSTRUCTIONS");
            state.ConstructionsReached = best;
        }
    }

    /// <summary>A mine's deposit ran out: told once per deposit, and whether the mine found another (MachLogMineralSite, MachLogMine).</summary>
    private void ReportDryMine(Construction mine, int site, bool moved)
    {
        if (Races[mine.Race].DrySitesTold.Add(site))
            SendVoiceMail(mine.Race, "VID_POD_MINERALS_EXHAUSTED");
        SendVoiceMail(mine.Race, moved ? "VID_POD_MINE_SWITCHED_SOURCE" : "VID_POD_MINE_INOPERABLE");
    }
}
