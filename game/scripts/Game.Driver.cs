using Godot;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Game;

// What the run driver (WiredDriver, MACHINES_WIRED_DRIVE) sees of the match: the view, the sight and the hands, as a player
// sees them on the screen. All of it is read only; the driver plays through the input like anyone else.
public partial class Game
{
    /// <summary>The machine driven in first person, if any.</summary>
    public Machine? Driven => _driven;

    /// <summary>Whether the view waits on the static for a run's new body.</summary>
    public bool HeroDown => _heroDown;

    /// <summary>Where the view looks with the modern keys, radians anticlockwise in the original's axes.</summary>
    public float AimYaw => _aimYaw;

    /// <summary>How far up the view looks, radians.</summary>
    public float LookPitch => _lookPitch;

    /// <summary>Radians the view turns for a pixel the mouse moves.</summary>
    public static float LookStep => LookPerPixel;

    /// <summary>Where the first person camera stands.</summary>
    public Vector3 Eye => _firstPersonCamera.GlobalPosition;

    /// <summary>The middle of an actor's model as it is shown, if it is shown.</summary>
    public Vector3? CentreOf(int actor) => _views.TryGetValue(actor, out var view) ? view.Centre : null;

    /// <summary>The places for guns of a machine, with the triggers of each.</summary>
    public IReadOnlyList<WeaponDisplay.GunPlace> GunsOf(Machine machine) => GunPlaces(machine);

    /// <summary>Whether the game counts a trigger as held.</summary>
    public bool TriggerDown(Mounting trigger) => _hands.Down(trigger);

    /// <summary>What the crosshair rests on.</summary>
    public Crosshair.Sight CrosshairSight => _hud.Crosshair.Aim;

    /// <summary>The hangar's page while it is open.</summary>
    public HangarPage? HangarShown => HangarOpen ? _hangar!.Page : null;

    /// <summary>The menu over the match, while it is open.</summary>
    public InGameMenu? MenuShown => _menu is not null && IsInstanceValid(_menu) ? _menu : null;
}
