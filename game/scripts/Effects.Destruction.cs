using Godot;

namespace Machines.Game;

// Machines and buildings destroyed.
public partial class Effects
{
    /// <summary>
    /// Something was destroyed at <paramref name="at"/> (its foot). In the remake's effects it goes up in an
    /// <see cref="Explosion"/> the size of its view; in the classic look its view blows up the original's way as it
    /// goes (<see cref="ActorView.BlowUp"/>), and something without a view only flashes.
    /// </summary>
    public void Destroyed(Vector3 at, ActorView? view)
    {
        if (GraphicsSettings.Effects)
            Explosion(at, view?.Size ?? 4);
        else if (view is null)
            Glow(at + Vector3.Up * 2, new Color(1, 0.55f, 0.15f) * 3, 8, 0.3f);
    }

    /// <summary>
    /// What follows a building's <see cref="Explosion"/> in the remake, after the original (MachPhysObjectExplosion):
    /// a burst wave out to twice the size it swells to, a fireball for every 9 m of it after 0.3 to 0.6 s, and dark
    /// smoke rising from the ruin for half a minute. <paramref name="box"/> is its model's box in its own space.
    /// </summary>
    public void BuildingDown(Transform3D place, Aabb box)
    {
        var (sizeX, sizeZ) = (box.Size.X, box.Size.Z);
        var dimension = Mathf.Max(Mathf.Max(sizeX, sizeZ), box.Size.Y);
        var foot = place * new Vector3(box.GetCenter().X, 0, box.GetCenter().Z);
        ShockRing(foot + Vector3.Up * 0.5f, Mathf.Max(sizeX, sizeZ) * 1.8f * 2, new Color(1, 0.6f, 0.3f), 1);

        for (var i = 1; i <= (int)(dimension / 9); i++)
        {
            var at = place * new Vector3(box.GetCenter().X + (GD.Randf() - 0.5f) * sizeX, GD.Randf() * box.End.Y,
                box.GetCenter().Z + (GD.Randf() - 0.5f) * sizeZ);
            var later = CreateTween();
            later.TweenInterval(0.3f + GD.Randf() * 0.3f);
            later.TweenCallback(Callable.From(() => Fireballs(at, Mathf.Clamp(dimension / 12, 1, 2.5f), 4)));
        }

        const float rise = 10, burning = 30;
        var smoke = Plume(2 + (int)(dimension / 15), rise, dimension * 1.6f, Mathf.Min(sizeX, sizeZ) * 0.7f,
            new Color(0.12f, 0.11f, 0.1f, 0.75f));
        smoke.Emitting = true;
        AddChild(smoke);
        smoke.GlobalPosition = foot;
        var end = smoke.CreateTween();
        end.TweenInterval(burning);
        end.TweenCallback(Callable.From(() => smoke.Emitting = false));
        end.TweenInterval(rise);
        end.TweenCallback(Callable.From(smoke.QueueFree));
    }
}
