using Godot;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The keys of the strategy view (MachInGameScreen::doHandleKeyEvent, MachCameras, the navigators and each command's
/// processButtonEvent). The bare letters of the commands work as far as the layout leaves them free: all of them with
/// the options' Classic Keys, the rest of them with the remake's moves on WASD.
/// </summary>
public partial class Game
{
    // The camera's four places kept with Ctrl and F5 to F8, each the start until then; the start itself.
    private readonly CameraRig.View?[] _savedViews = new CameraRig.View?[4];
    private CameraRig.View? _startView;

    // Where Alt with A or S has got to among the player's machines and buildings (MachGuiNavigatorBase::viewNext).
    private int _nextMachine, _nextConstruction;

    private void OnKey(InputEventKey key)
    {
        // Once the match is over only Esc and Space work, and they cut the wait short (doHandleKeyEvent).
        if (MatchOver)
        {
            if (key.Keycode is Key.Escape or Key.Space)
                _endSkipped = true;
            return;
        }
        var (ctrl, shift, alt) = (key.CtrlPressed, key.ShiftPressed, key.AltPressed);

        // The keys that keep their use (KeyMap.Reserved) first, then those the player may have moved.
        switch (key.Keycode)
        {
            // Esc cancels what is in hand (a building to place, a command's target, a navigator), and otherwise
            // leads to the menus as in the original.
            case Key.Escape:
                if (_placing is not null) CancelPlacement();
                else if (_inHand is not null) _inHand = null;
                else if (!_hud.Panel.CloseNavigator()) OpenMenu();
                return;
            // Shift and A or S select all the player's machines or buildings, with Ctrl as well as the selection; Alt
            // and A or S look at the next of them (MachGuiNavigatorBase).
            case Key.A or Key.S when shift && !alt:
                SelectAll(key.Keycode == Key.A, keep: ctrl);
                return;
            case Key.A or Key.S when alt && !shift && !ctrl:
                ViewNext(key.Keycode == Key.A);
                return;
            case Key.S when ctrl:
                Order(new StopCommand(Player, [.. SelectedMachines().Select(m => m.Id), .. SelectedEmplacements().Select(c => c.Id)]));
                return;
            // Shift and Z or X select what is on screen of the machines or the buildings, with Ctrl as well as the
            // selection (MachWorldViewWindow).
            case Key.Z or Key.X when shift && !alt:
                SelectOnScreen(key.Keycode == Key.Z, keep: ctrl);
                return;
            // Shift and H goes back to where the camera started, Ctrl and Shift and H to where it was last moved to.
            case Key.H when shift && !alt:
                if ((ctrl ? _rig.LastView : _startView) is { } view)
                    ShowView(view);
                return;
            // Ctrl and F5 to F8 keep the camera's place, F5 to F8 go back to it (MachCameras).
            case >= Key.F5 and <= Key.F8 when !shift && !alt:
                var slot = (int)(key.Keycode - Key.F5);
                if (ctrl)
                    _savedViews[slot] = _rig.Snapshot;
                else if ((_savedViews[slot] ?? _startView) is { } saved)
                    ShowView(saved);
                return;
            // Ctrl, Shift and Backspace is the self-destruct button; Backspace cancels what is marked in a queue.
            case Key.Backspace when ctrl && shift:
                _hud.Panel.PressSelfDestruct();
                return;
            case Key.Backspace when !ctrl && !shift && !alt:
                _hud.Panel.CancelMarked();
                return;
            // Ctrl and a number forms the selection into that squadron, the number alone selects it
            // (MachGuiSquadronBank).
            case >= Key.Key0 and <= Key.Key9 when ctrl:
                FormSquadron((int)(key.Keycode - Key.Key0));
                return;
            case >= Key.Key0 and <= Key.Key9:
                SelectSquadron((int)(key.Keycode - Key.Key0));
                return;
            // F1 to F3 start a chat message to another player, F4 one to everyone (MachPromptText).
            case >= Key.F1 and <= Key.F4 when Online && !shift && !ctrl:
                _hud.Chat.Begin((int)(key.Keycode - Key.F1));
                return;
            // With Ctrl, F1 to F3 make that other player an ally or no longer one (MachPromptText).
            case >= Key.F1 and <= Key.F3 when Online && ctrl:
                ToggleAlliance((int)(key.Keycode - Key.F1));
                return;
            // Alt and the left arrow puts the control panel away, Alt and the right arrow brings it out.
            case Key.Left when alt:
                _hud.Panel.SlideOut(false);
                return;
            case Key.Right when alt:
                _hud.Panel.SlideOut(true);
                return;
            // Shift and M lays mines (MachGuiDropLandMineCommand).
            case Key.M when shift && !ctrl && !alt:
                _hud.Panel.Press(key.Keycode, shifted: true);
                return;
        }

        // Space turns the building a quarter, as in the original (MachGuiConstructCommand); R does too.
        if (_placing is not null && KeyMap.Is(key, KeyMap.Act.TurnBuilding))
            _placingOrientation = (_placingOrientation + 90) % 360;
        else if (KeyMap.Is(key, KeyMap.Act.FirstPerson))
            EnterFirstPerson();
        // Tab steps the initiative (MachGuiDefconCommand).
        else if (KeyMap.Is(key, KeyMap.Act.Initiative))
            CycleInitiative();
        else if (KeyMap.Is(key, KeyMap.Act.AssemblyPoint))
            BeginAssemblyPoint();
        else if (KeyMap.Is(key, KeyMap.Act.Pause))
            TogglePause();
        // F10 leads to the menus, as Esc does when there is nothing to cancel (MachInGameScreen's keys).
        else if (KeyMap.Is(key, KeyMap.Act.Menu))
            OpenMenu();
        else if (KeyMap.Is(key, KeyMap.Act.ZenithView))
            UseZenithCamera(true);
        else if (KeyMap.Is(key, KeyMap.Act.GroundView))
            UseZenithCamera(false);
        // A bare letter the layout leaves free is the command that has it, when the panel shows that command.
        else if (!ctrl && !shift && !alt && !KeyMap.Taken(key.Keycode, firstPerson: false))
            _hud.Panel.Press(key.Keycode);
    }

    private void TogglePause()
    {
        if (Online)
            _hud.Message("A multiplayer match cannot be paused");
        else
        {
            Paused = !Paused;
            _hud.Message(Paused ? "Paused" : "Running");
        }
    }

    // MachGuiNavigatorBase::selectAll: those that can fight first, and an open navigator stays open.
    private void SelectAll(bool machines, bool keep)
    {
        var all = World.Actors.Where(a => a.Race == Player && !a.IsDead && (machines ? a is Machine { Inside: null } : a is Construction))
            .OrderBy(a => a is Machine { CanFight: true } or Construction { Weapons.Count: > 0 } ? 0 : 1).Select(a => a.Id);
        _hud.Panel.KeepNavigator();
        Select(keep ? _selection.Union(all).ToList() : all.ToList());
    }

    private void ViewNext(bool machines)
    {
        var all = World.Actors.Where(a => a.Race == Player && !a.IsDead && (machines ? a is Machine : a is Construction)).OrderBy(a => a.Id).ToList();
        if (all.Count == 0)
            return;
        ref var next = ref machines ? ref _nextMachine : ref _nextConstruction;
        LookAtActor(all[next++ % all.Count]);
    }

    private void SelectOnScreen(bool machines, bool keep)
    {
        var screen = GetViewport().GetVisibleRect();
        var shown = InBand(screen).Where(a => machines ? a is Machine : a is Construction).Select(a => a.Id);
        Select(keep ? _selection.Union(shown).ToList() : shown.ToList());
    }

    // MachCameras::restoreCamera: the camera that was in use, where it was.
    private void ShowView(CameraRig.View view)
    {
        UseZenithCamera(!view.Ground);
        _rig.Show(view);
    }

    /// <summary>
    /// Looks at an actor, inside an APC at its APC (MachCameras::lookAt): the ground camera behind a machine, 25 metres
    /// back and 40 from a glider; a building, or the zenith camera, from above.
    /// </summary>
    public void LookAtActor(Actor actor)
    {
        if (actor is Machine { Inside: { } inside } && World.Find(inside) is { } carrier)
            actor = carrier;
        if (!ZenithCamera && actor is Machine machine)
        {
            LookAt(machine.Position);
            _rig.LookAlong(machine.Facing, machine.Stats.Locomotion == Machines.Import.Model.Locomotion.Glider ? 40 : 25);
            return;
        }
        UseZenithCamera(true);
        LookAt(actor.Position);
    }
}
