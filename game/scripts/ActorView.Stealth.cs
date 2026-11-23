using Godot;
using Machines.Sim.World;

namespace Machines.Game;

// Package S's view of the Thresher (design-stealth §9.1, §9.4, §13 View), view-only: his walk slowed to his patrol's quarter
// pace and warped so each foot lingers and comes down hard; each plant (RigView.Plants, found in his walk) a stomp on his bus,
// a drop of the hips and a roll to the planted side, a jolt of the beam, dust at the foot (Game.StoryStealth shakes the view
// and sifts the ceiling near the listener). His beam's states: calm wide, focused to a rod and brighter when he is wary or
// searching, with small saccades while he doubts; three black-outs as he draws, and two thin aiming lasers from his guns to
// his quarry while he draws and fires.
public partial class ActorView
{
    /// <summary>Section 2's Thresher this frame (Game.StoryStealth sets it each frame; null: none under the rule).</summary>
    internal static StalkerLook? Stalker { get; set; }

    /// <summary>Where his quarry stands, for the lasers (Game.StoryStealth; null: none).</summary>
    internal static Vector3? StalkerQuarry { get; set; }

    /// <summary>One of his feet came down at a point, with its weight (1 a front foot, 0.6 a back one): Game.StoryStealth plays and shakes it.</summary>
    internal static Action<Vector3, float>? StalkerPlanted { get; set; }

    /// <summary>His walk on his round as a share of its own pace at the slowest, and how hard the warp brings his feet down (design-stealth §13).</summary>
    private const float StalkerSlowest = 0.2f, StalkerWarp = 0.6f;

    /// <summary>A plant drops his hips this far (metres) and rolls him this much to the planted side, his beam this much down.</summary>
    private const float HipDrop = 0.25f, HipRoll = 1.5f, BeamJolt = 1.5f;

    /// <summary>His beam's cone as a rod (focused, degrees either way) and calm; how much brighter the rod is.</summary>
    private const float RodAngle = 12, WideAngle = 25, RodBoost = 2;

    private bool _stalker;
    private float? _lastWalk;
    private float _drop, _dropRoll, _jolt, _focus, _stalkerClock, _drawAt = -10;
    private StalkerState _stalkerState;
    private MeshInstance3D[]? _lasers;

    partial void StorySyncStealth(Actor actor)
    {
        var now = Stalker is { } look && look.Id == actor.Id;
        if (now == _stalker)
            return;
        _stalker = now;
        _lastWalk = null;
        if (_rigView is not null)
        {
            _rigView.SlowestWalk = now ? StalkerSlowest : 0.5f;
            _rigView.WalkWarp = now ? StalkerWarp : 0;
            if (!now)
                _rigView.Transform = Transform3D.Identity;
        }
        if (!now && _lasers is not null)
            foreach (var laser in _lasers)
                laser.Visible = false;
    }

    partial void StoryFrameStealth(double delta)
    {
        if (!_stalker || _rigView is not { } rig || delta <= 0)
            return;
        var dt = (float)delta;
        _stalkerClock += dt;
        if (Stalker is { } look && look.State != _stalkerState)
        {
            if (look.State == StalkerState.Draw)
                _drawAt = _stalkerClock;
            _stalkerState = look.State;
        }
        // His plants: each foot's point of the cycle passed since the last frame.
        if (rig.WalkPhase is { } phase && Visible)
        {
            if (_lastWalk is { } last && last != phase)
            {
                var wrapped = phase < last;
                if (!wrapped || last - phase > 0.5f)
                {
                    foreach (var plant in rig.Plants)
                    {
                        var crossed = wrapped ? plant.Phase > last || plant.Phase <= phase : plant.Phase > last && plant.Phase <= phase;
                        if (crossed)
                            Planted(rig, plant);
                    }
                }
            }
            _lastWalk = phase;
        }
        else
            _lastWalk = null;
        // The drop springs back up in about a third of a second, the jolt sooner.
        _drop = Mathf.MoveToward(_drop, 0, dt * HipDrop * 3);
        _dropRoll = Mathf.MoveToward(_dropRoll, 0, dt * HipRoll * 3);
        _jolt = Mathf.MoveToward(_jolt, 0, dt * BeamJolt * 5);
        rig.Transform = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(_dropRoll)), Vector3.Down * _drop);
        _focus = Mathf.MoveToward(_focus, _stalkerState is StalkerState.Wary or StalkerState.Draw or StalkerState.Fire or StalkerState.Lost ? 1 : 0, dt * 3);
        Lasers();
    }

    private void Planted(RigView rig, RigPlant plant)
    {
        var foot = rig.PartPosition(plant.Node);
        foot = World(new System.Numerics.Vector2(foot.X, foot.Z));
        var weight = plant.Front ? 1 : 0.6f;
        _drop = HipDrop * weight;
        // To the side the foot stands on (the model's +z is its right).
        var side = (GlobalTransform.AffineInverse() * rig.PartPosition(plant.Node)).Z;
        _dropRoll = HipRoll * weight * Mathf.Sign(side == 0 ? 1 : side);
        _jolt = BeamJolt * weight;
        StalkerPlanted?.Invoke(foot, weight);
    }

    /// <summary>After the lamp is put at his eyes (ActorView.Beam): the plant's jolt, the rod, the saccades and the black-outs.</summary>
    partial void StoryBeamStealth(SpotLight3D beam)
    {
        if (!_stalker)
            return;
        var t = _stalkerClock;
        var yaw = _stalkerState == StalkerState.Wary ? Mathf.DegToRad(3) * Mathf.Sign(Mathf.Sin(t * Mathf.Tau * 4 + Mathf.Sin(t * 7))) * Mathf.Abs(Mathf.Sin(t * 9.3f)) : 0;
        var down = Mathf.DegToRad(_jolt);
        if (yaw != 0 || down != 0)
            beam.GlobalBasis = beam.GlobalBasis * new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, -down);
        beam.SpotAngle = Mathf.Lerp(WideAngle, RodAngle, _focus);
        beam.LightEnergy *= Mathf.Lerp(1, RodBoost, _focus);
        // Three black-outs in the 0.3 s after he begins to draw.
        var since = t - _drawAt;
        if (since is >= 0 and < 0.3f && (int)(since / 0.05f) % 2 == 0)
            beam.LightEnergy = 0;
    }

    // Two thin lasers from his guns (either side of his eye, a little below) to his quarry while he draws and fires.
    private void Lasers()
    {
        var on = _stalkerState is StalkerState.Draw or StalkerState.Fire && StalkerQuarry is not null && Visible && _rigView is not null;
        if (!on)
        {
            if (_lasers is not null)
                foreach (var laser in _lasers)
                    laser.Visible = false;
            return;
        }
        _lasers ??= [MakeLaser(), MakeLaser()];
        var eye = Eye;
        var right = GlobalBasis.Z.Normalized();
        var target = StalkerQuarry!.Value + Vector3.Up * 1.2f;
        for (var i = 0; i < 2; i++)
        {
            var from = eye + right * (i == 0 ? 1.6f : -1.6f) + Vector3.Down * 1.5f;
            var length = from.DistanceTo(target);
            var laser = _lasers[i];
            laser.Visible = length > 0.5f;
            if (!laser.Visible)
                continue;
            var along = (target - from) / length;
            var up = Mathf.Abs(along.Y) > 0.99f ? Vector3.Right : Vector3.Up;
            laser.GlobalTransform = new Transform3D(Basis.LookingAt(along, up).Scaled(new Vector3(1, 1, length)), from.Lerp(target, 0.5f));
        }
    }

    private MeshInstance3D MakeLaser()
    {
        var laser = new MeshInstance3D
        {
            Name = "StalkerLaser",
            Mesh = new BoxMesh { Size = new Vector3(0.035f, 0.035f, 1) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = LaserMaterial,
            TopLevel = true,
        };
        AddChild(laser);
        return laser;
    }

    /// <summary>His aiming lasers' look: thin, unshaded, glowing red (Warmup.Stealth compiles it behind the loading screen).</summary>
    internal static readonly StandardMaterial3D LaserMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(1, 0.1f, 0.08f, 0.8f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        EmissionEnabled = true,
        Emission = new Color(1, 0.1f, 0.05f),
        EmissionEnergyMultiplier = 3,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
}
