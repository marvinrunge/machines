using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// The pod's ion cannon and a nuclear missile on the moon (MACHINES_CINEMATIC=strike, data/maps/trailer-moon.scn).
public partial class Cinematic
{
    private List<int> _army = [];
    private int _pod, _silo;
    private Vector3? _aim;

    /// <summary>
    /// Footage of a blue army marching on the red base and what the base sends against it: the pod's ion beam at
    /// 10 s, and at 20.5 s the silo's nuclear missile, which comes down about seven seconds later.
    /// </summary>
    private Shot[] Strike(int pass)
    {
        // The moon's night wants more light.
        _exposure = 2.4f;
        _army = _world.Machines.Where(m => m.Race == Race.Blue).Select(m => m.Id).ToList();
        _pod = Building(Race.Red, "POD", 1784, 772);
        _silo = Building(Race.Red, "MISSILE_EMPLACEMENT", 1879, 817, "ICBM");
        At(0.5f, () => _world.Issue(new MoveCommand(Race.Blue, _army, new NVector2(1560, 840))));
        At(10, () => Fire(_pod));
        At(20.5f, () => Fire(_silo));
        var silo = Ground(1879, 817);
        if (pass == 2)
            return
            [
            new("front", 9, u =>
            {
                // Low along the army's front rank as it comes on.
                var army = Follow("army", Centre(_army), 2);
                var eye = Lift(army + new Vector3(30, 0, 44 - 12 * u), 1.6f);
                return new Frame(eye, army + new Vector3(10, 3, 0), 36, eye.DistanceTo(army), 1.4f);
            }),
            new("beam", 9, u =>
            {
                // Under the beam, looking up it from the edge of the army.
                var army = Hold("army", Centre(_army));
                var eye = Lift(army + new Vector3(50, 0, 45), 3);
                return new Frame(eye, army + Vector3.Up * Mathf.Lerp(14, 8, u), 50, Blur: 0.8f);
            }),
            new("base", 8, u =>
            {
                // Over the turrets at the base, the missile climbing out behind it.
                var eye = Ground(1690, 880, 6) + new Vector3(0, 0, -10 * u);
                return new Frame(eye, Ground(1850, 800, 30 + 40 * Mathf.SmoothStep(0.3f, 0.7f, u)), 44, Blur: 0.5f);
            }),
            new("wave", 10, u =>
            {
                // Low on the plain, the blast rolling out towards the camera.
                var aim = _aim ?? Hold("army", Centre(_army));
                var eye = Lift(aim + new Vector3(120, 0, 170), 2);
                return new Frame(eye, aim + Vector3.Up * 25, 46, Blur: 0.4f);
            }),
            new("crater", 9, u =>
            {
                var aim = _aim ?? Hold("army", Centre(_army));
                return new Frame(Around(aim, 140, 0.5f + 0.4f * u, 45), aim, 42, Blur: 0.4f);
            }),
            ];
        return
        [
        new("march", 9, u =>
        {
            // Low ahead of the army and off its flank as it comes on (the fast ones run ahead of its middle).
            var army = Follow("army", Centre(_army), 2);
            var eye = Lift(army + new Vector3(80 - 12 * u, 0, 50), 1.6f);
            return new Frame(eye, army + Vector3.Up * 3, 32, eye.DistanceTo(army), 1.2f);
        }),
        new("ion", 9, u =>
        {
            // From the north side, wide, the beam coming down into the army.
            var army = Hold("army", Centre(_army));
            var eye = Lift(army + new Vector3(-20 + 10 * u, 0, -120), 25);
            return new Frame(eye, army + Vector3.Up * 18, 44, Blur: 0.6f);
        }),
        new("silo", 8, u =>
        {
            // Low north of the silo (the lab stands to its south-west), looking up as the missile climbs out of it.
            var eye = Lift(silo + new Vector3(14, 0, -48), 2);
            var climb = Mathf.SmoothStep(0.3f, 0.6f, u);
            return new Frame(eye, silo + new Vector3(-6 * climb, 6 + 150 * climb, -4 * climb), Mathf.Lerp(44, 58, climb), Blur: 0.8f);
        }),
        new("nuke", 10, u =>
        {
            // From the north-west on the plain, the missile coming down on the army.
            var aim = _aim ?? Hold("army", Centre(_army));
            var eye = Lift(aim + new Vector3(-170, 0, -110), 22);
            return new Frame(eye, aim + Vector3.Up * Mathf.Lerp(30, 45, u), 42, Blur: 0.3f);
        }),
        new("after", 9, u =>
        {
            var aim = _aim ?? Hold("army", Centre(_army));
            var e = Ease(u);
            var eye = aim + new Vector3(-60, 12, 70).Lerp(new Vector3(-120, 90, 190), e);
            return new Frame(eye, aim + Vector3.Up * 6, 44, Blur: Mathf.Lerp(1, 0.4f, e));
        }),
        ];
    }

    // The pod's ion cannon or the silo's missile at the middle of the army, charged at once.
    private void Fire(int launcher)
    {
        if (_world.FindTarget(launcher) is not Construction building || SimCentre(_army) is not { } army)
            return;
        foreach (var weapon in building.Weapons)
            weapon.Reload = 0;
        if (launcher == _silo)
            _aim = Ground(army.X, army.Y);
        _world.Issue(new StrikeCommand(Race.Red, launcher, army));
    }
}
