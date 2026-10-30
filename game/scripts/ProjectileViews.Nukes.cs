using Godot;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// Nuclear missiles in flight (<see cref="GameWorld.MissilesInFlight"/>) as the original flew them
/// (MachPhysMissile::beLaunched): straight up out of the silo for EXTRAS[3] metres (350), on at that height until a
/// quarter of the whole way short of the target, then down onto it, turning over from the climb to the dive as it
/// flies on. The original's flight took as long as that path; here the path is flown in the simulation's flight
/// time, so the bomb goes off as the missile lands. In the remake's effects it burns and trails smoke as the
/// multi-launchers' missiles do.
/// </summary>
public partial class ProjectileViews
{
    private readonly Dictionary<GameWorld.MissileFlight, Nuke> _nukes = [];

    private sealed class Nuke
    {
        public required Node3D View;
        public required Vector3 Start, Top, Drop, End;
        public float Shown;
    }

    private void ShowMissilesInFlight(float delta)
    {
        foreach (var flight in _world.MissilesInFlight)
        {
            if (!_nukes.TryGetValue(flight, out var nuke))
            {
                if (NewNuke(flight) is not { } made)
                    continue;
                _nukes[flight] = nuke = made;
            }
            // Smoothly on between ticks, and back onto the simulation's clock should it drift.
            var elapsed = _world.Time - flight.Launched;
            nuke.Shown = Mathf.Abs(nuke.Shown + delta - elapsed) < 0.25f ? nuke.Shown + delta : elapsed;
            PlaceNuke(nuke, Mathf.Clamp(nuke.Shown / Mathf.Max(0.01f, flight.Lands - flight.Launched), 0, 1));
        }

        foreach (var flight in _nukes.Keys.Where(f => !_world.MissilesInFlight.Contains(f)).ToList())
        {
            var view = _nukes[flight].View;
            _nukes.Remove(flight);
            if (view.GetNodeOrNull<GpuParticles3D>("Trail") is { } trail)
            {
                trail.Reparent(this);
                trail.Emitting = false;
                GetTree().CreateTimer(trail.Lifetime + 0.2).Timeout += trail.QueueFree;
            }
            view.QueueFree();
        }
    }

    private Nuke? NewNuke(GameWorld.MissileFlight flight)
    {
        if (_assets.EffectMesh("nmisea") is not { } model)
            return null;
        var view = new MeshInstance3D { Mesh = model, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        if (GraphicsSettings.Effects)
        {
            RoundLooks.Exhaust(view, model);
            view.AddChild(new OmniLight3D { LightColor = new Color(1, 0.6f, 0.3f), LightEnergy = 3, OmniRange = 30, ShadowEnabled = false });
        }
        AddChild(view);

        // It leaves the silo's muzzle (the missile stands in it, nose up) or, without one, just above the silo.
        var silo = _world.Find<Construction>(flight.Launcher);
        var mount = silo?.Weapons.FirstOrDefault(w => w.Spec.TypeName == "NUCLEAR_MISSILE");
        var start = mount is not null && _viewOf(flight.Launcher)?.Muzzle(mount.Mounting, mount.Spec, 0) is { } muzzle
            ? muzzle
            : ActorView.World(flight.From) + Vector3.Up * 3;
        var spec = mount?.Spec ?? _world.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == "NUCLEAR_MISSILE");
        var extras = spec is null ? [] : Blast.Extras(spec);
        var climb = extras.Count > 3 ? extras[3] : 350;
        var end = ActorView.World(flight.Target);
        var top = start + Vector3.Up * climb;
        var over = end with { Y = top.Y };
        var back = top - over;
        var drop = over + (back.Length() > 0.01f ? back.Normalized() * Mathf.Min(start.DistanceTo(end) / 4, back.Length()) : Vector3.Zero);
        return new Nuke { View = view, Start = start, Top = top, Drop = drop, End = end };
    }

    /// <summary>The missile <paramref name="done"/> of the way along its path, at an even speed, nose along its flight.</summary>
    private static void PlaceNuke(Nuke nuke, float done)
    {
        var (climb, level, dive) = (nuke.Start.DistanceTo(nuke.Top), nuke.Top.DistanceTo(nuke.Drop), nuke.Drop.DistanceTo(nuke.End));
        var gone = done * (climb + level + dive);
        var down = (nuke.End - nuke.Drop).Normalized();
        Vector3 at, forward;
        if (gone < climb)
            (at, forward) = (nuke.Start.Lerp(nuke.Top, gone / climb), Vector3.Up);
        else if (gone < climb + level)
        {
            var t = (gone - climb) / level;
            (at, forward) = (nuke.Top.Lerp(nuke.Drop, t), Vector3.Up.Slerp(down, t));
        }
        else
            (at, forward) = (nuke.Drop.Lerp(nuke.End, Mathf.Clamp((gone - climb - level) / Mathf.Max(dive, 0.01f), 0, 1)), down);
        // Rolled as the other rounds are (Place), with the target's bearing standing in while it points straight up.
        var heading = (nuke.End - nuke.Start) with { Y = 0 };
        var side = (Mathf.Abs(forward.Y) > 0.99f ? (heading.LengthSquared() > 0.01f ? heading.Normalized() : Vector3.Right) : forward)
            .Cross(Vector3.Up).Normalized();
        nuke.View.GlobalTransform = new Transform3D(new Basis(forward, side.Cross(forward), side), at);
    }
}
