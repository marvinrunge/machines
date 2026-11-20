using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

// MACHINES_HANGAR_SCRIPT=<folder>,film: the hangar for the trailer (tools/cinematic/shoot-hangar.sh). Over a run, a Reaper
// is fitted by hand, the mouse drawn as it moves: two weapons dragged onto its arms, the turntable turned, the Knight
// dragged onto the plate, heavier guns onto it and a test fire. Each beat prints its time ("hangar: film ... at s") so that
// the footage can be cut by it.
public partial class HangarScript
{
    private FilmPointer? _pointer;
    // The movie writer's footage runs a frame per drawn frame: the beats say which.
    private void Beat(string what) => Say($"film {what} at frame {Engine.GetFramesDrawn()}");

    private async Task Film()
    {
        var game = Run;
        var world = game.World;
        var state = world.Wired!.Heroes[Game.Me];
        async Task Do(Command command)
        {
            game.Issue(command);
            await Wait(0.3);
        }

        // The hero and the goods it is fitted with, made through the hangar's commands from items the script hands it.
        world.Races[Game.Player].Bmu = 25000;
        foreach (var worn in state.Loadout.Weapons.OfType<WiredItem>().ToList())
            await Do(new WiredUnequipCommand(Game.Player, worn.Id));
        foreach (var weapon in new[] { "HEAVY_BOLTER2", "PULSE_CANNON", "PLASMA_CANNON2", "MULTI_LAUNCHER5" })
            state.Add(WiredKind.Weapon, weapon, 0);
        state.Add(WiredKind.Chassis, "knight5", 0);
        await Do(new WiredChassisCommand(Game.Player, state.Add(WiredKind.Chassis, "reaper3", 0).Id));
        await Open(HangarShelf.Weapons);
        page.Screen.Yaw = 0.5f;

        _pointer = new FilmPointer();
        var layer = new CanvasLayer { Layer = 120, Name = "FilmPointer" };
        layer.AddChild(_pointer);
        AddChild(layer);
        _mouse = page.Screen.GetGlobalRect().GetCenter() + new Vector2(260, 220);
        await Glide(_mouse, 0.1);
        await Wait(1.5);
        Say($"film set: {Worn()}");
        Beat("begins");

        // Onto the Reaper's arms.
        await DragOn(Card("HEAVY_BOLTER2", owned: true), Slot(WiredSlot.Weapon(Mounting.Left)));
        Beat($"HEAVY_BOLTER2 on: {Worn()}");
        await DragOn(Card("PULSE_CANNON", owned: true), Slot(WiredSlot.Weapon(Mounting.Right)));
        Beat($"PULSE_CANNON on: {Worn()}");
        await Wait(0.6);

        // A turn of the turntable.
        var spot = page.Screen.GetGlobalRect().GetCenter() + new Vector2(-120, -30);
        await Glide(spot, 0.6);
        Button(MouseButton.Left, true);
        _down = true;
        await Glide(spot + new Vector2(330, 10), 1.4);
        Button(MouseButton.Left, false);
        _down = false;
        Beat("turned");
        await Wait(0.5);

        // The Knight onto the plate: what stays on is shown as it is held over it.
        await Glide(page.TabOf(HangarShelf.Chassis).GetGlobalRect().GetCenter(), 0.7);
        await Click(MouseButton.Left);
        await Wait(0.5);
        var knight = Card("knight5", owned: true);
        await Glide(knight, 0.6);
        await Wait(1.0);
        Beat("pointing at knight5");
        await DragOn(knight, page.Plate.GetGlobalRect().GetCenter(), from: false);
        Beat($"knight5 on: {Worn()}");
        await Wait(1.4);

        // Heavier guns for the Knight.
        await Glide(page.TabOf(HangarShelf.Weapons).GetGlobalRect().GetCenter(), 0.7);
        await Click(MouseButton.Left);
        await Wait(0.5);
        await DragOn(Card("PLASMA_CANNON2", owned: true), Slot(WiredSlot.Weapon(Mounting.Left)));
        Beat($"PLASMA_CANNON2 on: {Worn()}");
        await DragOn(Card("MULTI_LAUNCHER5", owned: true), Slot(WiredSlot.Weapon(Mounting.Top)));
        Beat($"MULTI_LAUNCHER5 on: {Worn()}");
        await Wait(0.6);

        // And a test fire.
        var testFire = page.FindChildren("*", "", true, false).OfType<ChipButton>().First(b => b.Text == Lang.T("Test fire").ToUpperInvariant());
        await Glide(testFire.GetGlobalRect().GetCenter(), 0.8);
        await Wait(0.2);
        await Click(MouseButton.Left);
        Beat("test fire");
        await Glide(page.Screen.GetGlobalRect().GetCenter() + new Vector2(300, 260), 1.2);
        await Wait(4);
        Beat("ends");
        GetTree().Quit();
    }

    /// <summary>A card picked up where it lies (after a look at it), carried over and let go.</summary>
    private async Task DragOn(Vector2 card, Vector2 onto, bool from = true)
    {
        if (from)
        {
            await Glide(card, 0.6);
            await Wait(0.5);
        }
        Button(MouseButton.Left, true);
        _down = true;
        await Frames(2);
        await Glide(card + (onto - card) * 0.08f, 0.15);
        await Glide(onto, 0.9);
        await Wait(0.45);
        Button(MouseButton.Left, false);
        _down = false;
        await Frames(2);
        await Wait(0.9);
    }

    /// <summary>The mouse moved as a hand moves it, easing in and out, a frame at a time.</summary>
    private async Task Glide(Vector2 to, double seconds)
    {
        var from = _mouse;
        // By the game's own time, which the movie writer steps a frame at a time.
        var gone = 0.0;
        while (true)
        {
            var u = (float)Math.Clamp(gone / Math.Max(seconds, 0.001), 0, 1);
            var at = from.Lerp(to, u * u * (3 - 2 * u));
            GetViewport().PushInput(new InputEventMouseMotion
            {
                Position = at,
                GlobalPosition = at,
                Relative = at - _mouse,
                ButtonMask = _down ? MouseButtonMask.Left : 0,
            });
            _mouse = at;
            if (_pointer is not null)
            {
                _pointer.Position = at;
                _pointer.Down = _down;
            }
            if (u >= 1)
                break;
            await Frames(1);
            gone += GetProcessDeltaTime();
        }
    }
}

/// <summary>The mouse pointer drawn into the picture, as the movie writer records none.</summary>
public partial class FilmPointer : Node2D
{
    private bool _down;

    public bool Down
    {
        get => _down;
        set
        {
            if (_down == value)
                return;
            _down = value;
            QueueRedraw();
        }
    }

    private static readonly Vector2[] Arrow = [new(0, 0), new(0, 25), new(6, 19.5f), new(10.5f, 29), new(15, 27), new(10.5f, 17.5f), new(18.5f, 17.5f)];

    public override void _Draw()
    {
        var scale = GetViewportRect().Size.Y / 1080f * 1.35f * (_down ? 0.9f : 1);
        var points = Arrow.Select(p => p * scale).ToArray();
        DrawColoredPolygon(points, new Color(0.97f, 0.97f, 0.95f));
        DrawPolyline([.. points, points[0]], new Color(0.05f, 0.05f, 0.05f), 1.6f * scale, true);
    }
}
