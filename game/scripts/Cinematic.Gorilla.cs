using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Gorillas on the desert (MACHINES_CINEMATIC=gorilla, data/maps/trailer-desert.scn).
public partial class Cinematic
{
    private List<int> _gorillas = [];
    private int _hero, _wrecker, _rival, _brute;

    /// <summary>
    /// Footage of five red gorillas storming the blue base and three blue ones coming out of its gate to meet them.
    /// The first two of each side go for each other, fist to fist; the other red ones break through the gate. All
    /// stand their ground (DefCon high): under fire they would turn away.
    /// </summary>
    private Shot[] Gorilla(int pass)
    {
        _gorillas = Of(Race.Red, "AGGRESSOR", "NINJA");
        _hero = NearestOf(_gorillas, 1110, 240);
        var others = _gorillas.Where(id => id != _hero).ToList();
        _wrecker = others[0];
        var defenders = Of(Race.Blue, "AGGRESSOR", "NINJA");
        _rival = NearestOf(defenders, 1268, 238);
        _brute = NearestOf(defenders, 1268, 262);
        var third = defenders.Where(id => id != _rival && id != _brute).ToList();
        At(0.5f, () =>
        {
            _world.Issue(new DefConCommand(Race.Red, _gorillas, DefCon.High));
            _world.Issue(new DefConCommand(Race.Blue, defenders, DefCon.High));
            _world.Issue(new AttackCommand(Race.Red, [_hero], _rival));
            _world.Issue(new AttackCommand(Race.Red, [_wrecker], _brute));
            _world.Issue(new AttackCommand(Race.Blue, [_rival], _hero));
            _world.Issue(new AttackCommand(Race.Blue, [_brute], _wrecker));
            _world.Issue(new AttackMoveCommand(Race.Blue, third, new NVector2(1150, 250)));
            _world.Issue(new AttackMoveCommand(Race.Red, others.Skip(1).ToList(), new NVector2(1330, 250)));
        });
        // Where they meet (the battle is the same in every pass): the red ones punch from about x 1175, the blue ones
        // from 1220. The first fists come down at 9.45 s; the first two red ones fall at 14.55 and 18.85 s, the
        // last at 36.9 s; the third blue one punches on from 1228,251 (27.4, 31.55 and 35.7 s).
        var brawl = Ground(1197, 243);
        if (pass == 2)
            return
            [
            new("over", 7, u =>
            {
                // High behind the line, the base ahead of it.
                var line = Follow("line", Centre(_gorillas), 2);
                var eye = Lift(line + new Vector3(-55 + 12 * u, 0, 20), 32);
                return new Frame(eye, line + new Vector3(70, 0, 0), 44, Blur: 0.5f);
            }),
            // Inside the first as it punches its rival twice, cut before it falls.
            new("inside", 7.4f, u => Cockpit(_hero)),
            new("fists", 9, u =>
            {
                // Low ahead of a red one still standing, backing away as it punches.
                var red = Pick("red", () => Standing(_gorillas, 1175, 240));
                var gorilla = Follow("gorilla", red is { } id ? Position(id) : null, 3);
                var eye = Lift(gorilla + new Vector3(32 - 6 * u, 0, -14), 1.6f);
                return new Frame(eye, gorilla + Vector3.Up * 5, 36, eye.DistanceTo(gorilla), 1.6f);
            }),
            new("melee", 9, u => new Frame(Around(brawl, 40, 1.2f + 0.4f * u, 4), brawl + Vector3.Up * 5, 42, Blur: 1.2f)),
            // Inside the last red one, to its last punch.
            new("last", 4.4f, u => Cockpit(Pick("last", () => Standing(_gorillas, 1180, 244)) ?? -1)),
            ];
        return
        [
        new("stride", 7, u =>
        {
            // Low beside the southernmost of the line as it strides east, keeping pace.
            var south = Pick("south", () => _gorillas.OrderByDescending(id => Position(id)?.Z ?? 0).Cast<int?>().FirstOrDefault());
            var near = Follow("near", south is { } id ? Position(id) : null, 3);
            var line = Follow("line", Centre(_gorillas), 2);
            var eye = Lift(near + new Vector3(-6 + 12 * u, 0, 34), 1.3f);
            return new Frame(eye, near.Lerp(line, 0.5f) + new Vector3(6, 5, 0), 36, eye.DistanceTo(near), 1.5f);
        }),
        new("field", 9, u =>
        {
            // Behind the red ones and off their flank, the blue ones coming out of the gate at them.
            var eye = Ground(1132 + 6 * u, 268, 3);
            return new Frame(eye, brawl + new Vector3(10, 4, 0), 40, eye.DistanceTo(brawl), 1.0f);
        }),
        new("wide", 9, u => new Frame(Around(brawl, 58, 1.9f + 0.35f * u, 9), brawl + Vector3.Up * 4, 40, Blur: 0.9f)),
        // Inside the third blue one as it punches the last red ones.
        new("blue", 8, u => Cockpit(third.FirstOrDefault(-1))),
        new("ruins", 9, u =>
        {
            // Up over the base, looking down on it.
            var e = Ease(u);
            var eye = Ground(1240, 340).Lerp(Ground(1190, 420), e) + Vector3.Up * Mathf.Lerp(8, 85, e);
            return new Frame(eye, Ground(1320, 240, 2), Mathf.Lerp(46, 50, e), Blur: Mathf.Lerp(1.2f, 0.4f, e));
        }),
        ];
    }

    // The machine of these still standing nearest a point.
    private int? Standing(IEnumerable<int> ids, float x, float y) =>
        ids.Where(id => _world.FindTarget(id) is not null).OrderBy(id => Position(id) is { } at ? at.DistanceTo(Ground(x, y)) : float.MaxValue)
            .Cast<int?>().FirstOrDefault();
}
