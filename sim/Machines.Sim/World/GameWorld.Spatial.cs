using System.Numerics;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // Who is near whom: every living actor filed under the 32 m cell of its position, once a tick, so a search for
    // the nearest enemy looks only at the cells within its range instead of at every actor on the planet.
    private const float SpatialCell = 32;

    // Actors move a little during the tick after they are filed; searches look this much further.
    private const float SpatialSlack = 8;

    private List<Actor>[] _spatial = [];
    private int _spatialX, _spatialY;

    // The furthest an actor's edge lies from its filed position: the largest machine radius or building half-diagonal.
    private float _spatialReach;

    private void IndexActors()
    {
        if (_spatial.Length == 0)
        {
            _spatialX = (int)MathF.Ceiling(Planet.Surface.Width / SpatialCell) + 1;
            _spatialY = (int)MathF.Ceiling(Planet.Surface.Depth / SpatialCell) + 1;
            _spatial = new List<Actor>[_spatialX * _spatialY];
            for (var i = 0; i < _spatial.Length; i++)
                _spatial[i] = [];
        }
        foreach (var cell in _spatial)
            cell.Clear();
        _spatialReach = 0;
        foreach (var actor in _actors.Values)
        {
            if (actor.IsDead)
                continue;
            var (x, y) = SpatialCellOf(actor.Position);
            _spatial[y * _spatialX + x].Add(actor);
            var reach = actor is Construction building
                ? Vector2.Distance(building.Footprint.Min, building.Footprint.Max) / 2 + Vector2.Distance(building.Position, (building.Footprint.Min + building.Footprint.Max) / 2)
                : actor.Radius;
            _spatialReach = MathF.Max(_spatialReach, reach);
        }
    }

    private (int X, int Y) SpatialCellOf(Vector2 p) =>
        (Math.Clamp((int)(p.X / SpatialCell), 0, _spatialX - 1), Math.Clamp((int)(p.Y / SpatialCell), 0, _spatialY - 1));

    /// <summary>The closest enemy within range, preferring armed machines; ties go to the lower id.</summary>
    public Actor? NearestEnemy(Actor from, float range)
    {
        if (_spatial.Length == 0)
            IndexActors();
        var reach = range + _spatialReach + SpatialSlack;
        var (x0, y0) = SpatialCellOf(from.Position - new Vector2(reach));
        var (x1, y1) = SpatialCellOf(from.Position + new Vector2(reach));

        Actor? best = null;
        var bestRank = (Unarmed: 2, Distance: float.MaxValue, Id: int.MaxValue);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                foreach (var actor in _spatial[y * _spatialX + x])
                {
                    if (actor.IsDead || !AreEnemies(from.Race, actor.Race) || actor is Machine { Inside: not null })
                        continue;
                    var distance = from.DistanceTo(actor);
                    if (distance > range)
                        continue;
                    var rank = (Unarmed: actor is Machine { CanFight: true } ? 0 : 1, Distance: distance, actor.Id);
                    if (rank.CompareTo(bestRank) < 0)
                    {
                        best = actor;
                        bestRank = rank;
                    }
                }
            }
        }
        return best;
    }
}
