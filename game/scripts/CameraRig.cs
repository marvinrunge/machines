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

    public float Distance
    {
        get => _distance;
        set => _distance = value;
    }

    public override void _Ready()
    {
        Camera = new Camera3D { Far = 6000, Fov = 50 };
        AddChild(Camera);
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

        var input = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) input.Y -= 1;
        if (Input.IsKeyPressed(Key.S) && !Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Down)) input.Y += 1;
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
    }
}
