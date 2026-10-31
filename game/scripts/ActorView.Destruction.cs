using Godot;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Game;

// How a view goes when its actor is destroyed or turns traitor.
public partial class ActorView
{
    /// <summary>
    /// The actor was destroyed and its view goes, under <paramref name="effects"/>: a building shrinks and swells
    /// (see <see cref="Blowup.Swell"/>) and goes up with its burst wave, fireballs and smoke; a machine flies apart
    /// into its parts, in the remake's effects falling and bouncing (see <see cref="Shatter"/>), in the classic
    /// look as the original blew them away (see <see cref="Blowup.Machine"/>).
    /// </summary>
    public void BlowUp(Effects effects, ClassicAssets assets)
    {
        if (IsBuilding)
        {
            if (GraphicsSettings.Effects)
                effects.BuildingDown(GlobalTransform, _bounds);
            else
                Blowup.Building(this, assets, effects);
            if (_model is Node3D model)
                Blowup.Swell(this, model, effects, glow: GraphicsSettings.Effects);
        }
        else if (GraphicsSettings.Effects)
            Shatter(effects);
        else
            Blowup.Machine(this, _model as Node3D, assets, effects);
    }

    /// <summary>
    /// The machine has just turned from <paramref name="from"/> to its actor's side: for two seconds it flickers
    /// between the two sides' colours, five times (MachPhysTreacheryWeapon::traitorAnimation).
    /// </summary>
    public void Defect(Race from, Actor actor, ClassicAssets assets)
    {
        var (id, size) = ModelOf(actor);
        var look = Game.Look(from);
        // Every mesh with the one it had on the old side: the rig's parts by their number, or the single body.
        var swaps = new List<(MeshInstance3D Mesh, Mesh Now, Mesh Before)>();
        if (_rigView is not null && assets.RigFor(id, look) is { } old)
        {
            foreach (var mesh in _rigView.FindChildren("Mesh", nameof(MeshInstance3D), recursive: true, owned: false).OfType<MeshInstance3D>())
            {
                if (mesh.Mesh is { } now && mesh.GetParent()?.Name.ToString() is { } part && part.StartsWith("Part")
                    && int.TryParse(part[4..], out var i) && i < old.Nodes.Length && old.Nodes[i].Mesh is { } before)
                    swaps.Add((mesh, now, before));
            }
        }
        else if (GetNodeOrNull<MeshInstance3D>("Body") is { Mesh: { } body } single)
            swaps.Add((single, body, assets.MeshFor(id, look, size)));
        if (swaps.Count == 0)
            return;

        // A sequence of the old and the new colours, 0.4 s for both, played five times; it ends in the new.
        const float flicker = 0.4f;
        var tween = CreateTween();
        for (var k = 0; k < 10; k++)
        {
            var before = k % 2 == 0;
            tween.TweenCallback(Callable.From(() =>
            {
                foreach (var (mesh, now, was) in swaps)
                    mesh.Mesh = before ? was : now;
            }));
            tween.TweenInterval(flicker / 2);
        }
        tween.TweenCallback(Callable.From(() =>
        {
            foreach (var (mesh, now, _) in swaps)
                mesh.Mesh = now;
        }));
    }
}
