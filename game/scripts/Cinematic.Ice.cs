using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// A strike group on the ice planet (MACHINES_CINEMATIC=ice, data/maps/trailer-ice.scn).
public partial class Cinematic
{
    private List<int> _strikers = [], _defenders = [], _knights = [];
    private int _ballista, _boss, _commander, _overseer, _leader;
    private int? _turned;

    /// <summary>
    /// Footage of the red strike group falling on the blue army before its base. The knights go in at once and stand
    /// their ground (DefCon high); the ballista, the boss, the commander and the overseer hang back and are sent in
    /// later: the ballista at 12 s, the boss at 20 s, the commander and the overseer's healing at 30 s. The blue army
    /// comes on to meet them, so their weapons fire by themselves from 7 s on; the takes are timed to what they hit.
    /// </summary>
    private Shot[] Ice(int pass)
    {
        _strikers = _world.Machines.Where(m => m.Race == Race.Red).Select(m => m.Id).ToList();
        _defenders = _world.Machines.Where(m => m.Race == Race.Blue).Select(m => m.Id).ToList();
        _knights = Of(Race.Red, "AGGRESSOR", "KNIGHT");
        _leader = NearestOf(_knights, 290, 556);
        _ballista = Of(Race.Red, "AGGRESSOR", "BALLISTA").FirstOrDefault(-1);
        _boss = Of(Race.Red, "ADMINISTRATOR", "BOSS").FirstOrDefault(-1);
        _commander = Of(Race.Red, "ADMINISTRATOR", "COMMANDER").FirstOrDefault(-1);
        _overseer = Of(Race.Red, "ADMINISTRATOR", "OVERSEER").FirstOrDefault(-1);
        At(0.5f, () =>
        {
            _world.Issue(new DefConCommand(Race.Red, _knights, DefCon.High));
            _world.Issue(new AttackMoveCommand(Race.Red, _knights, new NVector2(290, 790)));
            _world.Issue(new MoveCommand(Race.Red, [_ballista, _commander, _overseer], new NVector2(290, 630)));
            _world.Issue(new MoveCommand(Race.Red, [_boss], new NVector2(290, 690)));
        });
        At(12, () => _world.Issue(new AttackCommand(Race.Red, [_ballista], NearestOf(_defenders, 287, 800))));
        At(20, () => _world.Issue(new AttackCommand(Race.Red, [_boss], NearestOf(_defenders, 285, 790))));
        At(30, () =>
        {
            _turned = NearestOf(Of(Race.Blue, "AGGRESSOR", "KNIGHT"), 300, 760);
            _world.Issue(new AttackCommand(Race.Red, [_commander], _turned.Value));
            _world.Issue(new HealCommand(Race.Red, [_overseer], _leader));
        });
        // Where the weapons come down (the battle is the same in every pass): at 7.2 s the commander's orb turns a
        // blue machine at the first of these, which the vortex (8.15 s) and the first bees (9.2 s) then fall on; the
        // second bees at 18 s; the second vortex at 30.45 s.
        var first = Ground(300, 700);
        var bees = Ground(268, 732);
        var vortex = Ground(291, 738);
        if (pass == 2)
            return
            [
            new("march", 4.5f, u =>
            {
                // High before the group as it comes down through the snow.
                var group = Follow("group", Centre(_knights), 2);
                return new Frame(Lift(group + new Vector3(-10, 0, 70 - 10 * u), 22), group + Vector3.Up * 3, 42, Blur: 0.6f);
            }),
            new("turned", 8.5f, u =>
            {
                // On the blue machine the orb turns red, the vortex and the bees after it.
                return new Frame(Around(first, 46, 0.2f + 0.3f * u, 6), first + Vector3.Up * 6, 44, Blur: 0.9f);
            }),
            new("cockpit", 5, u => Cockpit(_leader, -0.06f)),
            new("boss", 9, u =>
            {
                // Behind the boss as it flies in over the snow.
                var boss = Follow("boss", Position(_boss), 3);
                var ranks = Follow("ranks", Centre(_defenders), 2);
                var eye = Lift(boss - Flat(ranks - boss) * 16 + new Vector3(5, 0, 0), 5);
                return new Frame(eye, ranks + Vector3.Up * 2, 40, eye.DistanceTo(boss), 1.3f);
            }),
            new("line", 9, u =>
            {
                // Low off the outer knight of the line, the second vortex ahead.
                var east = Pick("east", () => _knights.Where(id => Position(id) is not null).OrderByDescending(id => Position(id)!.Value.X).Cast<int?>().FirstOrDefault());
                var knight = Follow("knight", east is { } id ? Position(id) : null, 3);
                var eye = Lift(knight + new Vector3(18 - 4 * u, 0, -8), 1.5f);
                return new Frame(eye, knight.Lerp(vortex, 0.5f) + Vector3.Up * 4, 38, eye.DistanceTo(knight), 1.4f);
            }),
            new("snowfall", 9, u =>
            {
                // Slowly over the wrecks in the falling snow.
                var battle = Hold("battle", Centre(_defenders) ?? Centre(_strikers));
                return new Frame(Lift(battle + new Vector3(40 - 12 * u, 0, -26), 7), battle + Vector3.Up * 2, 40, Blur: 1.2f);
            }),
            ];
        return
        [
        new("legs", 6.5f, u =>
        {
            // Low off the outer knight of the group as it strides down through the snow.
            var west = Pick("west", () => _knights.OrderBy(id => Position(id)?.X ?? 0).Cast<int?>().FirstOrDefault());
            var knight = Follow("knight", west is { } id ? Position(id) : null, 3);
            var eye = Lift(knight + new Vector3(-18, 0, 10 - 6 * u), 1.3f);
            return new Frame(eye, knight + new Vector3(0, 3, -2), 38, eye.DistanceTo(knight), 1.6f);
        }),
        new("vortex", 9, u =>
        {
            // From the east on the blue ranks, where the vortex comes down.
            return new Frame(Ground(372 - 8 * u, 676, 10), first + Vector3.Up * 4, 40, Blur: 0.8f);
        }),
        new("bees", 9, u => new Frame(Ground(208 + 8 * u, 700, 8), bees + Vector3.Up * 3, 40, Blur: 0.8f)),
        new("heal", 4.5f, u =>
        {
            // The overseer's beam on the leading knight.
            var leader = Follow("leader", Position(_leader), 3);
            return new Frame(Around(leader, 20, 0.6f + 0.3f * u, 4), leader + Vector3.Up * 3, 40, Blur: 1.4f);
        }),
        new("ballista", 8, u =>
        {
            // Behind the ballista as its second vortex comes down.
            var ballista = Follow("ballista", Position(_ballista), 3);
            var eye = Lift(ballista - Flat(vortex - ballista) * 14 + new Vector3(-6, 0, 0), 4);
            return new Frame(eye, vortex + Vector3.Up * 4, 40, eye.DistanceTo(ballista), 1.2f);
        }),
        new("blizzard", 9, u =>
        {
            var battle = Hold("battle", Centre(_strikers.Concat(_defenders)));
            var e = Ease(u);
            var eye = battle + new Vector3(-40, 8, -50).Lerp(new Vector3(-90, 70, -160), e);
            return new Frame(eye, battle + Vector3.Up * 2, 44, Blur: Mathf.Lerp(1, 0.4f, e));
        }),
        ];
    }
}
