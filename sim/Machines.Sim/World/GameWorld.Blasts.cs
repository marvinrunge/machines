using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    private readonly List<(float At, WeaponSpec Weapon, Vector2 Target, Race By, Vector2 Direction)> _incoming = [];

    /// <summary>Area weapons going off now.</summary>
    public List<Blast> Blasts { get; } = [];

    /// <summary>Blasts still to come (nuclear missiles in flight, punches on their way down): where and when.</summary>
    public IEnumerable<(float At, Vector2 Target)> Incoming => _incoming.Select(i => (i.At, i.Target));

    /// <summary>The research item that gives every pod of a race its ion cannon.</summary>
    public static readonly ResearchKey IonCannonResearch = new("POD", null, 1, "T_ION_ORBITAL_CANNON");

    public Blast StartBlast(WeaponSpec weapon, Vector2 position, Race? by, Vector2 direction = default)
    {
        var blast = new Blast(weapon.TypeName, position, by, Time, Blast.WavesFor(weapon),
            direction == default ? default : Vector2.Normalize(direction));
        Blasts.Add(blast);
        Raise(new GameEvent(GameEventKind.Blast, 0, position, Detail: weapon.TypeName));
        return blast;
    }

    /// <summary>A blast that goes off later, at the time <paramref name="at"/>: a missile landing, a punch coming down.</summary>
    internal void ScheduleBlast(float at, WeaponSpec weapon, Vector2 position, Race by, Vector2 direction = default) =>
        _incoming.Add((at, weapon, position, by, direction));

    /// <summary>
    /// Fires an ion cannon or nuclear missile. The ion beam comes down at once; the missile flies at its
    /// SPEED and its bomb goes off on arrival.
    /// </summary>
    internal void LaunchStrike(Construction launcher, WeaponMount weapon, Vector2 target)
    {
        weapon.Reload = weapon.Spec.ReloadTime;
        Raise(new GameEvent(GameEventKind.Fired, launcher.Id, launcher.Position, Detail: weapon.Spec.TypeName));
        if (weapon.Spec.TypeName == "NUCLEAR_MISSILE")
        {
            var flight = Vector2.Distance(launcher.Position, target) / MathF.Max(1, weapon.Spec.ProjectileSpeed);
            ScheduleBlast(Time + flight, weapon.Spec, target, launcher.Race);
        }
        else
        {
            StartBlast(weapon.Spec, target, launcher.Race);
        }
    }

    /// <summary>
    /// A pod mounts its ion cannon once its race has researched it, or when the scenario grants it; the
    /// cannon then charges for its RELOAD_TIME before the first shot (MachLogPod::activateIonCannon).
    /// </summary>
    private void ArmPods()
    {
        foreach (var pod in Constructions.Where(c => c.IsType("POD") && c.Complete && c.Weapons.Count == 0))
        {
            if (!pod.IonCannonGranted && !Races[pod.Race].Researched.Contains(IonCannonResearch))
                continue;
            var spec = Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == "ION_ORBITAL_CANNON");
            if (spec is null)
                continue;
            pod.WeaponCombo = IonCannonResearch.WeaponCombo;
            pod.Weapons.Add(new WeaponMount(Mounting.Top, spec) { Reload = spec.ReloadTime });
        }
    }

    private void UpdateBlasts()
    {
        for (var i = _incoming.Count - 1; i >= 0; i--)
        {
            if (_incoming[i].At > Time)
                continue;
            StartBlast(_incoming[i].Weapon, _incoming[i].Target, _incoming[i].By, _incoming[i].Direction);
            _incoming.RemoveAt(i);
        }

        foreach (var blast in Blasts)
        {
            var age = Time - blast.StartTime;
            for (var w = 0; w < blast.Waves.Count; w++)
            {
                var wave = blast.Waves[w];
                if (age < wave.Start)
                    continue;
                var radius = wave.RadiusAt(age);
                foreach (var actor in Actors.Where(a => !a.IsDead && !blast.Hit[w].Contains(a.Id)).ToList())
                {
                    if (actor is Machine { Inside: not null })
                        continue;
                    var offset = actor.Position - blast.Position;
                    var distance = MathF.Max(0, offset.Length() - actor.Radius);
                    if (distance > radius)
                        continue;
                    // A cone (the punch) spares whoever stands at its point: the puncher.
                    if (wave.ConeHalfAngle is { } cone && (offset.LengthSquared() < 1e-4f
                        || DetMath.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(offset), blast.Direction), -1, 1)) > cone))
                        continue;
                    blast.Hit[w].Add(actor.Id);
                    Damage(actor, wave.DamageAt(distance), blast.By);
                    Raise(new GameEvent(GameEventKind.Hit, actor.Id, actor.Position, Detail: blast.Weapon));
                }
            }
        }
        Blasts.RemoveAll(b => Time - b.StartTime > b.Duration);
    }
}
