using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// For automated checks of the hero's third-person view (Game.Chase.cs): MACHINES_WIRED_SCENE=<folder>,chase pictures a row
// of chassis from behind, out in the field with a foe within the guns' arc, firing, with the view past the arc while the
// hull turns and once it has turned, beside the pod with the pod behind, and with a slope behind; and the first of them
// from its faceplate by its key, and the wheel's steps (which keep the choice in the options: run it with APPDATA pointing at a
// scratch folder). MACHINES_CHASE_CHASSIS=<catalogue>+... picks others than the row's.
public partial class Game
{
    private static readonly (string Chassis, (Mounting Mounting, string Weapon)[] Weapons)[] ChaseRow =
    [
        ("grunt1", [(Mounting.Right, "BOLTER")]),
        ("reaper3", [(Mounting.Left, "HEAVY_BOLTER1"), (Mounting.Right, "HEAVY_BOLTER1")]),
        ("knight5", [(Mounting.Left, "HEAVY_BOLTER1"), (Mounting.Right, "HEAVY_BOLTER1")]),
        ("gorilla5", [(Mounting.Left, "HEAVY_BOLTER1"), (Mounting.Right, "HEAVY_BOLTER1")]),
        ("boss2", [(Mounting.Top, "HEAVY_BOLTER1")]),
        ("ballista5", [(Mounting.Left, "HEAVY_BOLTER1"), (Mounting.Right, "HEAVY_BOLTER1")]),
        ("titan6", [(Mounting.Top, "HEAVY_BOLTER1")]),
    ];

    private async Task SceneChase(string folder, WiredRun run)
    {
        var state = run.Heroes[Me];
        run.Countdown = 100_000;
        World.Races[Player].Bmu = 100000;
        var picked = System.Environment.GetEnvironmentVariable("MACHINES_CHASE_CHASSIS")?.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var row = picked is { Length: > 0 }
            ? picked.Select(c => ChaseRow.FirstOrDefault(r => r.Chassis == c) is { Chassis: not null } known ? known : (Chassis: c, Weapons: new[] { (Mounting.Top, "BOLTER") })).ToArray()
            : ChaseRow;
        _chase = true;
        foreach (var (chassis, weapons) in row)
        {
            await WearAllForScene(state, chassis, weapons);
            if (World.HeroOf(Me) is not { } hero || _driven is null)
            {
                GD.PrintErr($"scene: chase: no hero on {chassis}");
                continue;
            }
            var home = hero.Position;
            if (World.WiredPod(Me) is { } pod)
            {
                // Beside the pod, looking away from it: the pod stands behind the hero and the arm comes in before it.
                var away = hero.Position - pod.Position;
                away = away.LengthSquared() < 1 ? NVector2.UnitX : NVector2.Normalize(away);
                hero.Position = World.Nav.NearestFreePoint(pod.Position + away * (pod.Radius + hero.Radius + 3), crossesWater: false) ?? hero.Position;
                hero.Facing = MathF.Atan2(away.Y, away.X);
                (_aimYaw, _lookPitch) = (hero.Facing, 0);
                await Seconds(1.2);
                ChaseSnap(folder, $"chase-{chassis}-pod", hero);
                // Out on the field, beyond the pod's reach.
                hero.Position = World.Nav.NearestFreePoint(pod.Position + away * (WiredBalance.PodReach + 30), crossesWater: false) ?? hero.Position;
                hero.Facing = MathF.Atan2(away.Y, away.X);
                (_aimYaw, _lookPitch) = (hero.Facing, 0);
                await Seconds(0.8);
            }
            // A foe within the guns' arc (a turret's a little to the side), 35 m off or nearer within reach.
            var arc = World.GunArcOf(hero);
            var aside = arc > 0 ? 0.6f * arc : 20 * MathF.PI / 180;
            var metres = MathF.Min(35, 0.8f * hero.Weapons.Select(w => w.Spec.Range).DefaultIfEmpty(35).Max());
            _turretLogs = true;
            var foe = TurretFoe(run.Enemy, hero, hero.Facing + aside, metres);
            await Seconds(0.5);
            await ChaseSightOn(foe);
            await Seconds(1.2);
            ChaseSnap(folder, $"chase-{chassis}-aim", hero);
            ChaseSeen(hero, foe);
            Press(MouseButton.Left, true);
            await Seconds(0.7);
            ChaseSnap(folder, $"chase-{chassis}-fire", hero);
            Press(MouseButton.Left, false);
            if (arc > 0)
            {
                // From close behind and above, the view at nine tenths of the arc: the guns stand angled on their mounts.
                var (zoom, pitch, yaw) = (_chaseZoom, _lookPitch, _aimYaw);
                (_chaseZoom, _lookPitch, _aimYaw) = (0, ChaseCamera.MostDown - ChaseCamera.StartPitch, Mathf.Wrap(hero.Facing + 0.9f * arc, -MathF.PI, MathF.PI));
                await Seconds(1);
                ChaseSnap(folder, $"chase-{chassis}-swing", hero);
                (_chaseZoom, _lookPitch, _aimYaw) = (zoom, pitch, yaw);
                await Seconds(0.5);
            }
            if (chassis == row[0].Chassis)
            {
                // Its key to the faceplate and back, then the wheel: in a step, in to the faceplate, out to behind (nearest),
                // out a step. The choice is kept in the options, so this part changes the settings of the user:// it runs in.
                await Tap(KeyMap.Act.ChaseView);
                await Seconds(0.4);
                ChaseSnap(folder, $"chase-{chassis}-first", hero);
                var keyed = ChaseShown;
                await Tap(KeyMap.Act.ChaseView);
                var back = ChaseShown;
                var steps = new List<string>();
                foreach (var wheel in new[] { MouseButton.WheelUp, MouseButton.WheelUp, MouseButton.WheelDown, MouseButton.WheelDown })
                {
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = wheel, Pressed = true });
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = wheel, Pressed = false });
                    await Seconds(0.3);
                    steps.Add(ChaseShown ? $"behind x{ChaseCamera.Zooms[_chaseZoom]} arm {_chaseSpot?.Arm ?? 0:0.0}" : "faceplate");
                }
                GD.Print($"scene: chase: key {KeyMap.Name(KeyMap.Act.ChaseView)}: behind {keyed} then {back}; wheel: {string.Join(", ", steps)}; kept {GameOptions.ThirdPerson}");
                await Seconds(0.5);
            }
            // The view 60 degrees off, past any arc: the hull turns on the spot until the view is back within it.
            var facing = hero.Facing;
            _aimYaw = Mathf.Wrap(_aimYaw + 60 * MathF.PI / 180, -MathF.PI, MathF.PI);
            await Seconds(0.15);
            ChaseSnap(folder, $"chase-{chassis}-turn", hero);
            await Seconds(2.5);
            ChaseSnap(folder, $"chase-{chassis}-turned", hero);
            GD.Print($"scene: chase: {chassis} arc {arc * 180 / MathF.PI:0.0} degrees, hull turned {Mathf.Wrap(hero.Facing - facing, -MathF.PI, MathF.PI) * 180 / MathF.PI:0.0} of 60, " +
                     $"gun angled {World.GunAngleOf(hero) * 180 / MathF.PI:0.0}, on sight {World.OnSight(hero.Id)}");
            _turretLogs = false;
            foe.HitPoints = 0;
            // A slope behind the hero: the arm comes in before the hill.
            if (SlopeSpot(hero.Position, 400) is { } slope)
            {
                hero.Position = slope.At;
                hero.Facing = slope.Facing;
                (_aimYaw, _lookPitch) = (slope.Facing, 0);
                await Seconds(1.2);
                ChaseSnap(folder, $"chase-{chassis}-slope", hero);
            }
            hero.Position = home;
            await Seconds(0.5);
        }
    }

    /// <summary>Has the dot rest on a machine from where the view stands, behind the hero or at its faceplate.</summary>
    private async Task ChaseSightOn(Machine target)
    {
        for (var frame = 0; frame < 6 && _views.TryGetValue(target.Id, out var view); frame++)
        {
            (_aimYaw, _lookPitch) = LookTo(view.Centre);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>Logs whether the hero's own model stands between the camera and the foe the dot rests on, and how high the pivot stands over the hero's top.</summary>
    private void ChaseSeen(Machine hero, Machine foe)
    {
        if (_chaseSpot is not { } spot || !_views.TryGetValue(hero.Id, out var mine) || !_views.TryGetValue(foe.Id, out var theirs))
            return;
        var to = theirs.Centre - spot.Camera;
        var hidden = mine.Hit(spot.Camera, to.Normalized()) is { } at && at < to.Length();
        GD.Print($"scene: chase: hero {hero.Id} foe {(hidden ? "hidden behind the hero" : "seen")}, {Mathf.Sqrt(to.X * to.X + to.Z * to.Z):0.0} m off, " +
                 $"hero top {mine.Top - mine.GlobalPosition.Y:0.0} m, pivot {spot.Pivot.Y - mine.Top:0.0} m over it");
    }

    private void ChaseSnap(string folder, string name, Machine hero)
    {
        Snap(folder, name);
        GD.Print($"scene: chase: {name} arm {_chaseSpot?.Arm ?? 0:0.0} m, camera {(_chaseSpot is { } s ? s.Camera.ToString("0.0") : "faceplate")}, " +
                 $"hull {hero.Facing * 180 / MathF.PI:0.0}, view {_aimYaw * 180 / MathF.PI:0.0}, gun sight {_hud.Crosshair.Aim} on {_gun.On}, dot {_dotSight}");
    }

    /// <summary>A free place near <paramref name="near"/> with the ground rising steepest behind it, and the way facing from the rise.</summary>
    private (NVector2 At, float Facing)? SlopeSpot(NVector2 near, float reach)
    {
        if (HeightField.Current is not { } ground)
            return null;
        (NVector2 At, float Facing, float Rise)? best = null;
        for (var x = -reach; x <= reach; x += 20)
            for (var y = -reach; y <= reach; y += 20)
            {
                var at = near + new NVector2(x, y);
                if (!World.Nav.IsFree(at, crossesWater: false))
                    continue;
                for (var k = 0; k < 8; k++)
                {
                    var facing = k * MathF.PI / 4;
                    var behind = at - new NVector2(MathF.Cos(facing), MathF.Sin(facing)) * 10;
                    var rise = ground.At(behind.X, behind.Y) - ground.At(at.X, at.Y);
                    if (rise > (best?.Rise ?? 5))
                        best = (at, facing, rise);
                }
            }
        return best is { } found ? (found.At, found.Facing) : null;
    }
}
