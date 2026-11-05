using System.Globalization;
using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    private readonly HashSet<(Race By, Race At)> _firedAt = [];

    /// <summary>A campaign mission's script; null in skirmish and network games.</summary>
    public MissionScript? Script { get; set; }

    /// <summary>Whether a race has ever shot at another (MachLogRaces::firedAt, set as a weapon fires).</summary>
    public bool HasFiredAt(Race by, Race at) => _firedAt.Contains((by, at));

    private void NoteFiredAt(Race by, Actor target)
    {
        if (target is not Artefact && by != target.Race)
            _firedAt.Add((by, target.Race));
    }

    private void UpdateScript() => Script?.Update(this);

    /// <param name="speaker">The machine that reports, whose death cuts it off (MachLogVoiceMailManager::postDeathMail); 0 for none.</param>
    internal void SendVoiceMail(Race race, string vid, int speaker = 0) =>
        Raise(new GameEvent(GameEventKind.VoiceMail, speaker, Vector2.Zero, (int)race, vid));

    internal void TaskChanged(int number, bool complete) =>
        Raise(new GameEvent(GameEventKind.TaskChanged, 0, Vector2.Zero, number, complete ? "COMPLETE" : "OPEN"));

    /// <summary>
    /// CHANGE_RACE: every machine and building of one race within the range goes over to another, all at once
    /// (MachLogChangeRaceAction). Machines leave what they were doing; buildings what they were making.
    /// </summary>
    internal void ChangeRace(Race from, Race to, Vector2 at, float range)
    {
        if (!Races.ContainsKey(to))
            return;
        var changing = Actors.Where(a => a.Race == from && Vector2.DistanceSquared(at, a.Position) < range * range).OrderBy(a => a.Id).ToList();
        foreach (var actor in changing)
        {
            switch (actor)
            {
                case Construction building:
                    TakeOver(building, to);
                    break;
                case Machine machine when machine.Race == from:
                    Defect(machine, to);
                    break;
            }
        }
    }

    /// <summary>
    /// EXPLOSION: everything whose body is closer than the range takes the damage, machines less the further their
    /// middle is from it, down to 1 at the range (MachLogLinearProjectile::genericCheckForDamage, LINEAR_DAMAGE).
    /// </summary>
    internal void ScriptExplosion(Vector2 at, float range, int damage, float size)
    {
        Raise(new GameEvent(GameEventKind.Explosion, 0, at, Detail: size.ToString(CultureInfo.InvariantCulture)));
        foreach (var actor in Actors.Concat(Artefacts).Where(a => !a.IsDead && a is not Machine { Inside: not null }).OrderBy(a => a.Id).ToList())
        {
            var reach = actor is Construction building
                ? building.DistanceFrom(at)
                : MathF.Max(0, Vector2.Distance(at, actor.Position) - actor.Radius);
            if (reach >= range)
                continue;
            var hit = damage;
            if (actor is Machine && reach > 0)
            {
                var distance = Vector2.Distance(at, actor.Position);
                if (distance > 1 && range > 1)
                    hit = Math.Max(1, (int)(damage - distance * (damage / range)));
            }
            Damage(actor, hit, null);
            Raise(new GameEvent(GameEventKind.Hit, actor.Id, actor.Position, Detail: "EXPLOSION"));
        }
    }

    /// <summary>
    /// REINFORCEMENTS: the machines appear at once, each on the nearest free ground to the point
    /// (MachLogReinforcementsAction::doAction).
    /// </summary>
    internal void Reinforce(Race race, IReadOnlyList<(string Type, string? SubType, int Level, string? Combo)> units, Vector2 point, int? squad, bool voiceMail)
    {
        if (!Races.ContainsKey(race))
            return;
        foreach (var (type, subType, level, combo) in units)
        {
            var spec = Data.Parm.Machine(subType is null ? type : $"{type} {subType}");
            var clearance = MathF.Max(1, spec.Level(level).LowClearance);
            var machine = AddMachine(race, ReinforcementSpot(point, clearance), type, subType, level, combo);
            if (squad is not null)
                machine.Squad = squad;
        }
        if (voiceMail)
            SendVoiceMail(race, MissionScript.ReinforcementsVoiceMail);
    }

    private Vector2 ReinforcementSpot(Vector2 point, float clearance)
    {
        var taken = Machines.Where(m => m.Inside is null).Select(m => m.Position).ToList();
        bool Free(Vector2 p) => Nav.IsFree(p, crossesWater: false) && taken.All(t => Vector2.DistanceSquared(t, p) >= 4 * clearance * clearance);
        if (Free(point))
            return point;
        // The original widened its search 20 m at a time; going out ring by ring finds the same nearest spot.
        var step = 2 * clearance;
        for (var ring = 1; ring * step < 2000; ring++)
        {
            var steps = 6 * ring;
            for (var i = 0; i < steps; i++)
            {
                var angle = i * MathF.Tau / steps;
                var candidate = point + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * (ring * step);
                if (Free(candidate))
                    return candidate;
            }
        }
        return point;
    }
}
