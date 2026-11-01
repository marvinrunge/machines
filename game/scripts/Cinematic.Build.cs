using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// A base at work on the green planet (MACHINES_CINEMATIC=build, data/maps/trailer-green.scn).
public partial class Cinematic
{
    private List<int> _constructors = [], _carriers = [], _technicians = [];
    private int _locator;

    /// <summary>
    /// Footage of the red base by the lake: over the water to the shore; the constructors raising a factory with their
    /// torches; beside a carrier on its way from the mine to the smelter; riding in one; the locator setting out and
    /// the technicians going into their lab; and round the base from high up, the lake beyond. The mine has no ore yet
    /// in the first minute, so the carriers only drive its round.
    /// </summary>
    private Shot[] Build()
    {
        _constructors = Of(Race.Red, "CONSTRUCTOR");
        _carriers = Of(Race.Red, "RESOURCE_CARRIER");
        _technicians = Of(Race.Red, "TECHNICIAN");
        _locator = Of(Race.Red, "GEO_LOCATOR").FirstOrDefault(-1);
        var lab = Building(Race.Red, "HARDWARE_LAB", 486, 1120);
        var site = new NVector2(596, 1100);
        var mine = new NVector2(372, 1320);
        var smelter = new NVector2(462, 1352);
        At(0.2f, () => _world.Issue(new PlaceConstructionCommand(Race.Red, _constructors, "FACTORY", "CIVILIAN", 3, site, -270)));
        for (var round = 0; round < 4; round++)
        {
            var there = round % 2 == 0 ? smelter : mine;
            At(0.2f + 14 * round, () => _world.Issue(new MoveCommand(Race.Red, _carriers, there)));
        }
        At(36, () =>
        {
            _world.Issue(new LocateCommand(Race.Red, [_locator], new NVector2(470, 1060)));
            _world.Issue(new EnterLabCommand(Race.Red, _technicians, lab));
        });
        var building = Ground(site.X, site.Y);
        return
        [
        new("dawn", 9, u =>
        {
            // Low over the lake to its southern shore and the base.
            var e = Ease(u);
            var eye = Ground(Mathf.Lerp(545, 525, e), Mathf.Lerp(780, 1010, u), Mathf.Lerp(16, 6, e));
            return new Frame(eye, Ground(Mathf.Lerp(505, 515, e), 1190, Mathf.Lerp(-4, 4, e)), 40, Blur: 1);
        }),
        new("torch", 9, u =>
        {
            // Round the factory going up, over the constructors at work round it.
            return new Frame(Around(building, 78, 2.0f + 0.45f * u, 14), building + Vector3.Up * 4, 40, Blur: 1.0f);
        }),
        new("haul", 9, u =>
        {
            // Beside a carrier on its way.
            var picked = Pick("carrier", () => _carriers.Cast<int?>().FirstOrDefault());
            var carrier = Follow("carrier", picked is { } id ? Position(id) : null, 4);
            return new Frame(Lift(carrier + new Vector3(-8, 0, 24), 3), carrier + new Vector3(4, 2, 0), 38, Blur: 1.6f);
        }),
        new("ride", 9, u => Cockpit(Pick("rider", () => _carriers.Cast<int?>().LastOrDefault()) ?? -1, -0.08f)),
        new("crew", 9, u =>
        {
            // By the lab as the locator sets out past it and the technicians go in.
            var labAt = Position(lab) ?? Ground(486, 1120);
            var crew = Follow("crew", Centre(_technicians.Append(_locator)), 2);
            var eye = Lift(labAt + new Vector3(30 - 6 * u, 0, 26), 2.5f);
            return new Frame(eye, crew.Lerp(labAt, 0.4f) + Vector3.Up * 3, 40, eye.DistanceTo(crew), 1.4f);
        }),
        new("lake", 9, u =>
        {
            // Round the base from high up, the lake beyond.
            var centre = Ground(500, 1220);
            return new Frame(Around(centre, 230, 1.2f + 0.45f * u, 110), centre + new Vector3(0, 0, -60), 42, Blur: 0.4f);
        }),
        ];
    }
}
