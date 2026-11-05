using Godot;

namespace Machines.Game;

/// <summary>
/// RTS camera: WASD or arrow keys (or the screen edge) pan, Q/E rotate, the mouse wheel zooms.
/// Starts in the original's zenith-style view (41 degrees down, from the scenario's CAMERA_POS).
/// With the options' Classic Keys it takes the original's keys (PhysZenithFlyControl, PhysGroundFlyControl) and the
/// heights of zenith.dat, and with the Classic Mouse a drag of the right button moves it (<see cref="Drag"/>).
/// </summary>
public partial class CameraRig : Node3D
{
    private const float PanSpeed = 220f;
    private const float EdgeMargin = 6f;

    private float _yaw;

    /// <summary>Which way the camera looks around its focus, radians (Q and E turn it).</summary>
    public float Yaw { get => _yaw; set => _yaw = value; }
    private float _distance = 160f;
    private Vector2 _bounds = new(1600, 1600);

    public Camera3D Camera { get; private set; } = null!;

    /// <summary>False while first-person mode owns the view.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Pan when the mouse touches the window edge; off for automated screenshots.</summary>
    public bool EdgePan { get; set; } = true;

    /// <summary>Degrees down from the horizon; the original zenith camera uses 41.</summary>
    public float Pitch { get; set; } = 41f;

    /// <summary>
    /// Whether the rig is the ground camera, low over the ground looking ahead. Ctrl, Shift and the up or down arrow
    /// tilt its view while Shift is held; let go of Shift and it levels again (PhysGroundFlyControl).
    /// </summary>
    public bool Ground { get; set; }

    private const float TiltSpeed = 45f, MaxTilt = 70f;

    /// <summary>
    /// How near and far the zenith camera may be with the Classic Keys: MIN_HEIGHT 25 and MAX_HEIGHT 85 of zenith.dat,
    /// over the ground at its ELEVATION of 41 degrees.
    /// </summary>
    private const float ZenithNearest = 38, ZenithFurthest = 130;

    private const float ClimbSpeed = 15f, MaxRise = 40f;

    // How far the ground camera has climbed over the ground.
    private float _rise;

    /// <summary>Which way the right button is being dragged, along one axis (MachCameras::scroll), or nothing.</summary>
    public Vector2I Drag { get; set; }

    /// <summary>True while it holds still: the ground camera while a box is drawn.</summary>
    public bool Frozen { get; set; }

    /// <summary>A place of the camera, as Ctrl and F5 to F8 keep it (MachCameras::saveCamera).</summary>
    public readonly record struct View(Vector3 Position, float Yaw, float Pitch, float Distance, bool Ground);

    /// <summary>The camera's place now.</summary>
    public View Snapshot => new(Position, _yaw, Pitch, _distance, Ground);

    /// <summary>Where the keys last moved the camera to.</summary>
    public View? LastView { get; private set; }

    /// <summary>Goes to a place kept.</summary>
    public void Show(View view)
    {
        (Position, _yaw, Pitch, _distance) = (view.Position, view.Yaw, view.Pitch, view.Distance);
        Place();
    }

    /// <summary>
    /// The ground camera goes behind the focus, which faces <paramref name="facing"/> (radians anticlockwise in the
    /// original's axes), <paramref name="back"/> metres off.
    /// </summary>
    public void LookAlong(float facing, float back)
    {
        _yaw = Mathf.Atan2(-Mathf.Cos(facing), -Mathf.Sin(facing));
        _distance = back / Mathf.Cos(Mathf.DegToRad(Pitch));
        Place();
    }

    // The ground camera's tilt, degrees up.
    private float _tilt;

    public float Distance
    {
        get => _distance;
        set => _distance = value;
    }

    public override void _Ready()
    {
        Camera = new Camera3D { Far = 6000, Fov = 50 };
        AddChild(Camera);
        GameOptions.ShowGamma();
        Place();
    }

    public void Setup(Vector3 focus, Vector2 planetSize)
    {
        Position = focus;
        _bounds = planetSize;
        Place();
    }

    public override void _Process(double delta)
    {
        if (!Active)
            return;
        // Keys typed into a chat message are not the camera's; it holds still meanwhile.
        if (MatchChat.Typing || Frozen)
        {
            Place();
            return;
        }

        var classic = GameOptions.ClassicKeys;
        var (ctrl, shift) = (Input.IsKeyPressed(Key.Ctrl), Input.IsKeyPressed(Key.Shift));
        var (up, down) = (Input.IsKeyPressed(Key.Up), Input.IsKeyPressed(Key.Down));

        // With Ctrl held the ground camera's arrows tilt rather than move, with the Classic Keys with Shift alone. The
        // up arrow tilts it down and the down arrow up, as a pilot's stick does, unless the options reverse them.
        var tilting = Ground && (classic ? shift : ctrl);
        if (!Ground || !shift)
            _tilt = 0;
        else if (tilting && up != down)
            _tilt = Mathf.Clamp(_tilt + (down != GameOptions.ReverseKeys ? 1 : -1) * TiltSpeed * (float)delta, -MaxTilt, MaxTilt);

        var input = Vector2.Zero;
        var (turn, climb) = (0f, 0f);
        if (classic)
        {
            var sideways = (Input.IsKeyPressed(Key.Right) ? 1 : 0) - (Input.IsKeyPressed(Key.Left) ? 1 : 0);
            if (!tilting)
                input.Y = (down ? 1 : 0) - (up ? 1 : 0);
            // The zenith camera's arrows slide it, and turn it with Shift; the ground camera's turn it, and Delete
            // and End slide it.
            if (Ground || shift)
                turn = -sideways;
            else
                input.X = sideways;
            if (Ground)
                input.X += (Input.IsKeyPressed(Key.End) ? 1 : 0) - (Input.IsKeyPressed(Key.Delete) ? 1 : 0);
            climb = (Input.IsKeyPressed(Key.Pageup) || Input.IsKeyPressed(Key.KpAdd) ? 1 : 0) - (Input.IsKeyPressed(Key.Pagedown) || Input.IsKeyPressed(Key.KpSubtract) ? 1 : 0);
        }
        else
        {
            // The moves' keys as the player chose them (KeyMap), and the arrows; Ctrl and S stops rather than moves.
            if (KeyMap.Held(KeyMap.Act.Forward) || up && !tilting) input.Y -= 1;
            if (KeyMap.Held(KeyMap.Act.Back) && !ctrl || down && !tilting) input.Y += 1;
            if (KeyMap.Held(KeyMap.Act.Left) || Input.IsKeyPressed(Key.Left)) input.X -= 1;
            if (KeyMap.Held(KeyMap.Act.Right) || Input.IsKeyPressed(Key.Right)) input.X += 1;
            if (KeyMap.Held(KeyMap.Act.TurnLeft)) turn += 1;
            if (KeyMap.Held(KeyMap.Act.TurnRight)) turn -= 1;
        }
        var keyed = input != Vector2.Zero || turn != 0 || climb != 0;
        // Ctrl moves the camera at a quarter of the speed.
        var gear = classic && ctrl ? 0.25f : 1;

        // The right button dragged sideways turns the zenith camera and slides the ground camera, up and down brings
        // the zenith camera down and up and raises and lowers the ground camera.
        if (Ground)
        {
            input.X += Drag.X;
            climb -= Drag.Y;
        }
        else
        {
            turn -= Drag.X;
            climb += Drag.Y;
        }

        var mouse = GetViewport().GetMousePosition();
        var size = GetViewport().GetVisibleRect().Size;
        if (EdgePan && DisplayServer.WindowIsFocused())
        {
            if (mouse.X <= EdgeMargin) input.X -= 1;
            if (mouse.X >= size.X - EdgeMargin) input.X += 1;
            if (mouse.Y <= EdgeMargin) input.Y -= 1;
            if (mouse.Y >= size.Y - EdgeMargin) input.Y += 1;
        }

        _yaw += turn * 1.5f * gear * (float)delta;
        if (Ground)
            _rise = Mathf.Clamp(_rise + climb * ClimbSpeed * gear * (float)delta, 0, MaxRise);
        else
            _distance *= 1 + climb * gear * (float)delta;
        if (classic && !Ground)
            _distance = Mathf.Clamp(_distance, ZenithNearest, ZenithFurthest);

        if (input != Vector2.Zero)
        {
            var speed = PanSpeed * (_distance / 160f) * gear * (float)delta;
            var forward = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
            var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
            Position += (right * input.X - forward * input.Y) * speed;
            Position = new Vector3(Mathf.Clamp(Position.X, 0, _bounds.X), 0, Mathf.Clamp(Position.Z, 0, _bounds.Y));
        }
        // The focus follows the ground, so the view keeps its distance on plateaus.
        Position = new Vector3(Position.X, HeightField.Current?.At(Position.X, Position.Z) ?? 0, Position.Z);
        Place();
        if (keyed)
            LastView = Snapshot;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active || @event is not InputEventMouseButton { Pressed: true } wheel)
            return;
        if (wheel.ButtonIndex == MouseButton.WheelUp)
            _distance = Mathf.Max(GameOptions.ClassicKeys && !Ground ? ZenithNearest : 40, _distance * 0.9f);
        else if (wheel.ButtonIndex == MouseButton.WheelDown)
            _distance = Mathf.Min(GameOptions.ClassicKeys && !Ground ? ZenithFurthest : 700, _distance * 1.1f);
    }

    private void Place()
    {
        if (Camera is null)
            return;
        var pitch = Mathf.DegToRad(Pitch);
        var offset = new Vector3(Mathf.Sin(_yaw), 0, Mathf.Cos(_yaw)) * Mathf.Cos(pitch) * _distance + Vector3.Up * Mathf.Sin(pitch) * _distance;
        var focus = GlobalPosition + (Ground ? Vector3.Up * _rise : Vector3.Zero);
        Camera.GlobalPosition = focus + offset;
        Camera.LookAt(focus, Vector3.Up);
        Camera.RotateObjectLocal(Vector3.Right, Mathf.DegToRad(_tilt));
        Camera.Basis *= ShakeAt(Camera.GlobalPosition);
    }

    // ---- the earth shaking (W4dCameraShake) -----------------------------------------------------

    private static Quaternion[] _shake = [];
    private static float _shakeStart, _shakeTime;
    private static Vector3 _epicentre;

    /// <summary>
    /// The ground shakes the view (W4dCameraShake), as after a nuke, a punch or a big building blowing up: a run of
    /// random turns of up to <paramref name="degrees"/> either way, each smaller than the last, over
    /// <paramref name="duration"/> seconds: the first degree 15° in 45 turns, the second 10° in 37, the third
    /// 5.5° in 32. It weakens with the camera's distance from <paramref name="epicentre"/> (see <see cref="ShakeAt"/>).
    /// </summary>
    public static void Shake(Vector3 epicentre, float degrees, float duration)
    {
        var turns = degrees >= 15 ? 45 : degrees >= 10 ? 37 : 32;
        var random = new Random();
        _shake = new Quaternion[turns + 2];
        _shake[0] = _shake[^1] = Quaternion.Identity;
        for (var i = 0; i < turns; i++)
        {
            var angle = Mathf.DegToRad(degrees * (1 - i / (float)turns));
            float Turn() => angle * (float)(random.NextDouble() * 2 - 1);
            _shake[i + 1] = Quaternion.FromEuler(new Vector3(Turn(), Turn(), Turn()));
        }
        (_shakeStart, _shakeTime, _epicentre) = ((float)FrameClock.Now, duration, epicentre);
    }

    /// <summary>
    /// The turn the shaking gives a camera at <paramref name="camera"/> now; none when it is over. It weakens with
    /// the camera's distance d from the epicentre as (1 - d/200)², and is gone 200 m away
    /// (W4dSceneManager::shakeCurrentCamera).
    /// </summary>
    public static Basis ShakeAt(Vector3 camera)
    {
        var t = ((float)FrameClock.Now - _shakeStart) / Mathf.Max(_shakeTime, 0.01f);
        if (_shake.Length == 0 || t is < 0 or >= 1)
            return Basis.Identity;
        var near = 1 - Mathf.Min(camera.DistanceTo(_epicentre) / 200, 1);
        var strength = near * near;
        var position = t * (_shake.Length - 1);
        var i = Math.Min((int)position, _shake.Length - 2);
        var turn = _shake[i].Slerp(_shake[i + 1], position - i);
        return new Basis(Quaternion.Identity.Slerp(turn, strength));
    }
}
