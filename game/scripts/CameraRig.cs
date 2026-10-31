using Godot;

namespace Machines.Game;

/// <summary>
/// RTS camera: WASD or arrow keys (or the screen edge) pan, Q/E rotate, the mouse wheel zooms.
/// Starts in the original's zenith-style view (41 degrees down, from the scenario's CAMERA_POS).
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
        if (MatchChat.Typing)
        {
            Place();
            return;
        }

        // With Ctrl held the ground camera's arrows tilt rather than move. The up arrow tilts it down and the down
        // arrow up, as a pilot's stick does, unless the options reverse them.
        var tilting = Ground && Input.IsKeyPressed(Key.Ctrl);
        if (!Ground || !Input.IsKeyPressed(Key.Shift))
            _tilt = 0;
        else if (tilting && Input.IsKeyPressed(Key.Up) != Input.IsKeyPressed(Key.Down))
            _tilt = Mathf.Clamp(_tilt + (Input.IsKeyPressed(Key.Down) != GameOptions.ReverseKeys ? 1 : -1) * TiltSpeed * (float)delta, -MaxTilt, MaxTilt);

        var input = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up) && !tilting) input.Y -= 1;
        if (Input.IsKeyPressed(Key.S) && !Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Down) && !tilting) input.Y += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) input.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) input.X += 1;

        var mouse = GetViewport().GetMousePosition();
        var size = GetViewport().GetVisibleRect().Size;
        if (EdgePan && DisplayServer.WindowIsFocused())
        {
            if (mouse.X <= EdgeMargin) input.X -= 1;
            if (mouse.X >= size.X - EdgeMargin) input.X += 1;
            if (mouse.Y <= EdgeMargin) input.Y -= 1;
            if (mouse.Y >= size.Y - EdgeMargin) input.Y += 1;
        }

        if (Input.IsKeyPressed(Key.Q)) _yaw += 1.5f * (float)delta;
        if (Input.IsKeyPressed(Key.E)) _yaw -= 1.5f * (float)delta;

        if (input != Vector2.Zero)
        {
            var speed = PanSpeed * (_distance / 160f) * (float)delta;
            var forward = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
            var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
            Position += (right * input.X - forward * input.Y) * speed;
            Position = new Vector3(Mathf.Clamp(Position.X, 0, _bounds.X), 0, Mathf.Clamp(Position.Z, 0, _bounds.Y));
        }
        // The focus follows the ground, so the view keeps its distance on plateaus.
        Position = new Vector3(Position.X, HeightField.Current?.At(Position.X, Position.Z) ?? 0, Position.Z);
        Place();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active || @event is not InputEventMouseButton { Pressed: true } wheel)
            return;
        if (wheel.ButtonIndex == MouseButton.WheelUp)
            _distance = Mathf.Max(40, _distance * 0.9f);
        else if (wheel.ButtonIndex == MouseButton.WheelDown)
            _distance = Mathf.Min(700, _distance * 1.1f);
    }

    private void Place()
    {
        if (Camera is null)
            return;
        var pitch = Mathf.DegToRad(Pitch);
        var offset = new Vector3(Mathf.Sin(_yaw), 0, Mathf.Cos(_yaw)) * Mathf.Cos(pitch) * _distance + Vector3.Up * Mathf.Sin(pitch) * _distance;
        Camera.GlobalPosition = GlobalPosition + offset;
        Camera.LookAt(GlobalPosition, Vector3.Up);
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
        (_shakeStart, _shakeTime, _epicentre) = (Time.GetTicksMsec() / 1000f, duration, epicentre);
    }

    /// <summary>
    /// The turn the shaking gives a camera at <paramref name="camera"/> now; none when it is over. It weakens with
    /// the camera's distance d from the epicentre as (1 - d/200)², and is gone 200 m away
    /// (W4dSceneManager::shakeCurrentCamera).
    /// </summary>
    public static Basis ShakeAt(Vector3 camera)
    {
        var t = (Time.GetTicksMsec() / 1000f - _shakeStart) / Mathf.Max(_shakeTime, 0.01f);
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
