using Godot;
using Machines.Sim.World;
using NVector3 = System.Numerics.Vector3;

namespace Machines.Game;

// The hero's third-person view (Wired and battles, the Modern layout): the camera stands behind and above the hero on the
// line of the view's heading through its pivot (ChaseCamera), so the mouse turns the view, the gun follows at its own rate
// and the keys drive the hull, exactly as from the faceplate; only the picture is taken from further back. Its key (F by
// default) or the mouse wheel switches between the two, the choice kept in the options; inside a building, or with the
// Classic layout, it is first person. The commands are the same in both views.
public partial class Game
{
    // Whether the driver wants the view from behind, which wheel step it stands at, and the arm and pivot height as they
    // are drawn (the arm lengthens slowly and shortens at once, the pivot leaves out a walker's bobbing).
    private bool _chase = GameOptions.ThirdPerson;
    private int _chaseZoom = 1;
    private float _chaseArm = -1, _chasePivotY;
    private int _chaseFor = -1;

    // Where the camera stood this frame and its pivot, for the aim and the marks; null while the view is first person.
    private (Vector3 Camera, Vector3 Pivot, float Arm)? _chaseSpot;

    // The ears stay with the hero, so what is heard is what first person hears.
    private AudioListener3D? _chaseEar;

    /// <summary>Whether the hero is seen from behind this frame: the driver wants it, the view is sighted, and the hero is out in the open.</summary>
    private bool ChaseShown => _chase && Sighted && _driven is { Inside: null } machine && _views.TryGetValue(machine.Id, out var view) && view.Floor is null;

    /// <summary>The hero's key: from the faceplate to behind and back; with the Classic layout there is no view from behind.</summary>
    private void ToggleChase()
    {
        if (!Sighted)
        {
            _hud.Message(Lang.T("Third person needs the Modern layout (Options, Classic F.P. Keys off)"));
            return;
        }
        SetChase(!_chase);
    }

    private void SetChase(bool chase)
    {
        if (_chase == chase)
            return;
        _chase = chase;
        GameOptions.ThirdPerson = chase;
        _audio.PlayGui("gui/sounds/weapchng.wav");
    }

    /// <summary>
    /// The mouse wheel: out steps the arm longer, from first person into third; in steps it shorter, and past the shortest
    /// into first person, as in a tank game. True if it was taken.
    /// </summary>
    private bool ChaseWheel(InputEventMouseButton wheel)
    {
        if (!Sighted || _towersUp || wheel.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown))
            return false;
        if (wheel.ButtonIndex == MouseButton.WheelDown)
        {
            if (!_chase)
            {
                _chaseZoom = 0;
                SetChase(true);
            }
            else
                _chaseZoom = Math.Min(_chaseZoom + 1, ChaseCamera.Zooms.Length - 1);
        }
        else if (_chase)
        {
            if (_chaseZoom > 0)
                _chaseZoom--;
            else
                SetChase(false);
        }
        return true;
    }

    /// <summary>The pitch the chase camera looks at for the driver's look up and down: a level look is a little downwards from behind.</summary>
    private float ChasePitch => ChaseCamera.Pitch(_lookPitch + ChaseCamera.StartPitch);

    /// <summary>
    /// Puts the camera behind the hero, looking where the driver looks: the arm by the hero's size and the options' distance,
    /// shortened by buildings (as the battle watch camera's, <see cref="Arm"/>) and hills, the camera kept over the ground.
    /// Near enough to stand in the model, the model is left out of the picture.
    /// </summary>
    private void PlaceChaseCamera(Machine machine, ActorView view, double delta)
    {
        if (_chaseFor != machine.Id)
            (_chaseArm, _chaseFor) = (-1, machine.Id);
        var radius = machine.Radius;
        var wanted = ChaseCamera.Arm(radius, GameOptions.ChaseDistance * ChaseCamera.Zooms[_chaseZoom]);
        var pitch = ChasePitch;
        var centre = view.Centre;
        var pivotY = centre.Y + ChaseCamera.Lift(wanted);
        _chasePivotY = _chaseArm < 0 ? pivotY : Mathf.Lerp(_chasePivotY, pivotY, 1 - Mathf.Exp(-8 * (float)delta));
        var pivot = new Vector3(view.GlobalPosition.X, _chasePivotY, view.GlobalPosition.Z);
        var ahead = ChaseCamera.Ahead(_aimYaw, pitch);
        // The buildings are tested on the ground plan, where the arm is shorter by the pitch.
        var flat = Mathf.Max(0.2f, Mathf.Cos(pitch));
        var walls = Arm(pivot, new Vector3(-ahead.X, 0, -ahead.Z).Normalized(), wanted * flat) / flat;
        var ground = HeightField.Current;
        var spot = ChaseCamera.Place(Of(pivot), _aimYaw, pitch, wanted, ChaseCamera.ShortestArm(radius), walls,
            ground is null ? null : (x, z) => ground.At(x, z), ground is null ? null : at => GroundNear(ground, at.X, at.Z));
        // Shorter at once (a wall or hill came between), longer slowly.
        _chaseArm = _chaseArm < 0 || spot.Arm < _chaseArm ? spot.Arm : Mathf.Lerp(_chaseArm, spot.Arm, 1 - Mathf.Exp(-4 * (float)delta));
        var placed = _chaseArm >= spot.Arm - 0.01f ? spot
            : ChaseCamera.Place(Of(pivot), _aimYaw, pitch, _chaseArm, ChaseCamera.ShortestArm(radius), _chaseArm,
                ground is null ? null : (x, z) => ground.At(x, z), ground is null ? null : at => GroundNear(ground, at.X, at.Z));
        var camera = new Vector3(placed.Camera.X, placed.Camera.Y, placed.Camera.Z);
        _firstPersonCamera.GlobalPosition = camera;
        _firstPersonCamera.GlobalRotation = new Vector3(pitch, -_aimYaw - Mathf.Pi / 2, 0);
        _firstPersonCamera.Basis *= CameraRig.ShakeAt(camera);
        var inside = ChaseCamera.InsideHero(Of(camera), Of(centre), radius);
        _firstPersonCamera.CullMask = inside ? ~ActorView.DrivenLayer & 0xFFFFF : 0xFFFFF;
        _chaseSpot = (camera, pivot, placed.Arm);
        // The cabin's hum is a sound from inside; from behind the engine is heard instead.
        view.LeaveCabinSound();
        // The ears at the faceplate, facing where the view looks.
        if (_chaseEar is null)
        {
            _chaseEar = new AudioListener3D { Name = "ChaseEar" };
            AddChild(_chaseEar);
        }
        _chaseEar.GlobalTransform = new Transform3D(_firstPersonCamera.GlobalBasis, view.Eye);
        if (!_chaseEar.IsCurrent())
            _chaseEar.MakeCurrent();
    }

    /// <summary>Back to the faceplate's picture: the hero out of it, the ears back with the camera.</summary>
    private void DropChaseCamera()
    {
        _chaseSpot = null;
        _firstPersonCamera.CullMask = ~ActorView.DrivenLayer & 0xFFFFF;
        if (_watched is null)
            GroundBlast.Onlooker = false;
        if (_chaseEar is not null && _chaseEar.IsCurrent())
            _chaseEar.ClearCurrent();
    }

    private static NVector3 Of(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// The highest ground under the camera and a metre round it, where its picture would cut into the ground (the battle
    /// watch camera's three metres would lift it high above a hero at the foot of a slope, the hero below the picture).
    /// </summary>
    private static float GroundNear(HeightField ground, float x, float z)
    {
        var most = ground.At(x, z);
        for (var k = 0; k < 4; k++)
            most = Mathf.Max(most, ground.At(x + Mathf.Cos(k * Mathf.Pi / 2), z + Mathf.Sin(k * Mathf.Pi / 2)));
        return most;
    }

    /// <summary>
    /// How far along a line of sight from the camera the aim begins: from behind, past the hero's pivot, so that the ground
    /// or a machine between the camera and the hero is never aimed at; from the faceplate at once.
    /// </summary>
    private float AimSkip(Vector3 from, Vector3 direction) =>
        _chaseSpot is { } spot ? ChaseCamera.AimFrom(Of(from), Of(direction), Of(spot.Pivot)) : 0;
}
