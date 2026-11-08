using Godot;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// The mouse in the world (MachWorldViewWindow, MachInGameScreen::doHandleRightClickEvent). A left click is carried out
/// when the button comes up, unless the mouse has moved 10 pixels and drawn a box. The right button gives the order in
/// the remake's scheme; in the original's (the options' Classic Mouse) a short click puts things back and a drag moves
/// the camera.
/// </summary>
public partial class Game
{
    /// <summary>The pixels the mouse moves with a button held before it draws a box or moves the camera.</summary>
    private const float DragStart = 10;

    /// <summary>The seconds within which the right button coming up counts as a click, in the world and on the map.</summary>
    public const double RightClickTime = 0.5, RightClickTimeOnMap = 0.1;

    // In the zenith camera the box's first corner stays on its point of the ground while the view scrolls.
    private NVector2? _dragGround;
    private bool _banding;
    private readonly HashSet<int> _banded = [];

    // Where and when the right button went down, in the original's scheme.
    private (Vector2 At, double Time)? _rightPress;

    private ClassicCursor _cursor = null!;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_heroDown)
        {
            DownInput(@event);
            return;
        }
        if (_driven is not null)
        {
            FirstPersonInput(@event);
            return;
        }

        // Once the match is over only its two keys work.
        if (MatchOver && @event is not InputEventKey)
            return;

        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                OnKey(key);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                LeftButton(left);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } right:
                RightButton(right);
                break;
            case InputEventMouseMotion motion when _rightPress is { } press:
                _rig.Drag = DragDirection(motion.Position - press.At);
                break;
        }
    }

    private void LeftButton(InputEventMouseButton left)
    {
        if (left.Pressed)
        {
            _dragStart = left.Position;
            _dragGround = ZenithCamera ? GroundAt(left.Position) : null;
            return;
        }
        if (_dragStart is null)
            return;
        if (_banding)
            SelectBand(left.CtrlPressed || left.ShiftPressed && !GameOptions.ClassicMouse);
        else
            Pick(PointedAt(left.Position), left.CtrlPressed, left.ShiftPressed, left.AltPressed);
        EndBand();
    }

    private void RightButton(InputEventMouseButton right)
    {
        if (GameOptions.ClassicMouse)
        {
            if (right.Pressed)
            {
                _rightPress = (right.Position, Time.GetTicksMsec() / 1000.0);
                return;
            }
            _rig.Drag = Vector2I.Zero;
            if (_rightPress is { } press && Time.GetTicksMsec() / 1000.0 - press.Time < RightClickTime)
                PutBack();
            _rightPress = null;
            return;
        }
        if (!right.Pressed)
            return;
        if (Holding)
            DropHand();
        else
            OrderAt(right.Position);
    }

    /// <summary>The command in hand or the building to place is given up.</summary>
    public void DropHand()
    {
        _inHand = null;
        CancelPlacement();
    }

    /// <summary>The way a drag of the right button goes once it has come 10 pixels: along whichever axis it has gone further.</summary>
    private static Vector2I DragDirection(Vector2 moved)
    {
        if (moved.Length() <= DragStart)
            return Vector2I.Zero;
        return Math.Abs(moved.X) >= Math.Abs(moved.Y) ? new Vector2I(Math.Sign(moved.X), 0) : new Vector2I(0, Math.Sign(moved.Y));
    }

    /// <summary>
    /// A short right click in the original's scheme (doHandleRightClickEvent): out of the building menu back to the
    /// commands, otherwise nothing selected any more and no command in hand.
    /// </summary>
    public void PutBack()
    {
        var building = _placing is not null || _hud.Panel.BuildMenuOpen;
        _inHand = null;
        CancelPlacement();
        if (building)
            _hud.Panel.CloseBuildMenu();
        else
            Select([]);
    }

    // ---- the box --------------------------------------------------------------------------------

    /// <summary>Draws the box while the left button is held, what it holds lit up (MachWorldViewWindow::update).</summary>
    private void UpdateBand()
    {
        if (_dragStart is not { } start)
            return;
        var camera = GetViewport().GetCamera3D();
        if (_dragGround is { } ground && ActorView.World(ground) is var anchor && !camera.IsPositionBehind(anchor))
            start = camera.UnprojectPosition(anchor);
        var mouse = PointedForScreenshot ?? GetViewport().GetMousePosition();
        if (!_banding && !Holding && start.DistanceTo(mouse) > DragStart)
        {
            _banding = true;
            // The ground camera holds still while the box is drawn.
            _rig.Frozen = !ZenithCamera;
        }
        if (!_banding)
            return;
        var rect = new Rect2(start, mouse - start).Abs();
        _hud.DragRect = rect;
        var inside = InBand(rect).Select(a => a.Id).ToHashSet();
        foreach (var id in _banded.Except(inside))
            if (_views.TryGetValue(id, out var left))
                left.Highlighted = false;
        foreach (var id in inside)
            if (_views.TryGetValue(id, out var view))
                view.Highlighted = !view.Selected;
        _banded.Clear();
        _banded.UnionWith(inside);
    }

    /// <summary>The box goes, whether it selected something or the camera changed under it.</summary>
    private void EndBand()
    {
        foreach (var id in _banded)
            if (_views.TryGetValue(id, out var view))
                view.Highlighted = false;
        _banded.Clear();
        _dragStart = null;
        _dragGround = null;
        _banding = false;
        _hud.DragRect = null;
        _rig.Frozen = false;
    }

    /// <summary>The player's machines and buildings in the box, in the order they are selected: first what can fight (MachWorldViewWindow::selectActors).</summary>
    private IEnumerable<Actor> InBand(Rect2 rect)
    {
        var camera = GetViewport().GetCamera3D();
        bool Shown(Actor actor)
        {
            var at = _views.TryGetValue(actor.Id, out var view) ? view.Centre : ActorView.World(actor.Position);
            return !camera.IsPositionBehind(at) && rect.HasPoint(camera.UnprojectPosition(at));
        }
        return World.Actors.Where(a => a.Race == Player && a is Machine { Inside: null } or Construction && !a.IsDead && Shown(a))
            .OrderBy(a => a switch
            {
                Machine { CanFight: true } => 0,
                Machine => 1,
                Construction { Weapons.Count: > 0 } => 2,
                _ => 3,
            });
    }

    private void SelectBand(bool keep)
    {
        var rect = _hud.DragRect ?? default;
        var picked = InBand(rect).Select(a => a.Id);
        Select(keep ? _selection.Union(picked).ToList() : picked.ToList());
    }

    // ---- the cursor -----------------------------------------------------------------------------

    /// <summary>
    /// The cursor for what lies under the mouse: the arrow over the panel, edge arrows while scrolling, what the command
    /// in hand or the building to place says, otherwise the intelligent cursor. On the map they are the small ones.
    /// </summary>
    private string CursorKind()
    {
        var mouse = GetViewport().GetMousePosition();
        var size = GetViewport().GetVisibleRect().Size;
        var onMap = _hud.Panel.PointedOnMap is not null;
        _cursor.Small = onMap;
        if (_driven is not null || GetViewport().GuiGetHoveredControl() is not null && !onMap)
            return "arrow";
        const float edge = 6;
        var vertical = mouse.Y <= edge ? "n" : mouse.Y >= size.Y - edge ? "s" : "";
        var horizontal = mouse.X <= edge ? "w" : mouse.X >= size.X - edge ? "e" : "";
        if (vertical.Length + horizontal.Length > 0 && _rig.EdgePan && !onMap)
            return "scrl" + vertical + horizontal;
        if (_inHand is { } hand)
            return hand.Cursor(_pointed);
        if (_placing is not null)
            return PlacingCursor();
        if (_hud.Panel.BuildMenuOpen)
            return "const";
        return CursorOf(AimAt(_pointed, Input.IsKeyPressed(Key.Ctrl), Input.IsKeyPressed(Key.Alt)));
    }

    // ---- automated checks -----------------------------------------------------------------------

    private Queue<string[]>? _scriptedInput;

    /// <summary>
    /// For automated checks: MACHINES_SCREENSHOT_INPUT=2:L:own;3:R:400,300;4:M:400,300;5:L:ground;6:K:M;7:Lp:own;8:Lr:own;9:Kp:J;10:Kr:J
    /// plays clicks (L, R, M for the middle button, with p and r for press and release alone) on a screen point, the player's
    /// first machine, the first enemy or the ground before the panel, and keys (K, with p and r likewise), at the seconds
    /// given, without touching the real mouse, and logs what stands selected and ordered after each. In first person, where
    /// what counts is what is held, they go through Input; and every shot of the driven machine is logged by the triggers it answered.
    /// </summary>
    private void PlayScriptedInput()
    {
        if (_scriptedInput is null)
        {
            _scriptedInput = new((System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_INPUT") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(step => step.Split(':')));
            if (_scriptedInput.Count > 0)
                Ticked += LogShots;
        }
        while (_scriptedInput.TryPeek(out var step) && _runTime >= double.Parse(step[0], System.Globalization.CultureInfo.InvariantCulture))
        {
            _scriptedInput.Dequeue();
            void Play(InputEvent @event)
            {
                if (_driven is null)
                    _UnhandledInput(@event);
                else
                    Input.ParseInputEvent(@event);
            }
            if (step[1][0] == 'K')
            {
                var key = Enum.Parse<Key>(step[2], ignoreCase: true);
                if (step[1].Length == 1 || step[1][1] == 'p')
                    Play(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
                if (step[1].Length == 2 && step[1][1] == 'r' || step[1].Length == 1 && _driven is not null)
                    Play(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
            }
            else
            {
                var at = ScriptedPoint(step[2]);
                var button = step[1][0] switch { 'L' => MouseButton.Left, 'M' => MouseButton.Middle, _ => MouseButton.Right };
                if (step[1].Length == 1 || step[1][1] == 'p')
                    Play(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = at });
                if (step[1].Length == 1 || step[1][1] == 'r')
                    Play(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = at });
            }
            var lead = _selection.Select(id => World.Find<Machine>(id)).FirstOrDefault(m => m is not null);
            GD.Print($"input {string.Join(":", step)}: selected {_selection.Count}, order {lead?.Order.GetType().Name}, hand {_inHand is not null}, placing {_placing is not null}, driving {_driven is not null}");
        }
    }

    private void LogShots()
    {
        foreach (var shot in World.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == _driven?.Id))
            GD.Print($"input: {Time.GetTicksMsec() / 1000.0:0.00} s fired {shot.Detail}, weapons mask {World.PlayerWeapons.GetValueOrDefault(shot.Actor)}, reloading [{string.Join(", ", _driven!.Weapons.Where(w => w.Reload > 0).Select(w => w.Mounting))}]");
    }

    private Vector2 ScriptedPoint(string point)
    {
        var camera = GetViewport().GetCamera3D();
        Vector2? Of(Actor? actor) => actor is not null && _views.TryGetValue(actor.Id, out var view) ? camera.UnprojectPosition(view.Centre) : null;
        return point switch
        {
            "own" => Of(World.Machines.FirstOrDefault(m => m.Race == Player)) ?? default,
            "enemy" => Of(World.Machines.FirstOrDefault(m => World.AreEnemies(Player, m.Race))) ?? default,
            "ground" => GetViewport().GetVisibleRect().Size * new Vector2(0.6f, 0.5f),
            _ when point.Split(',') is [var x, var y] => new Vector2(float.Parse(x), float.Parse(y)),
            _ => default,
        };
    }
}
