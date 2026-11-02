using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// The main menu's showpiece: a machine turning slowly on the big chip's screen, another every few seconds, in each
/// race's colours in turn. It is built as the game builds it (<see cref="ClassicAssets.RigFor"/>), so it shows the
/// look the player has chosen; without imported models the screen stays empty.
/// </summary>
public partial class MachineDisplay : MachineScreen
{
    private static readonly (string Id, string Name)[] Machines =
    [
        ("AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7", "Gorilla"),
        ("AGGRESSOR_5_KNIGHT_LRT_PLASMA_CANNON2_X3", "Knight"),
        ("ADMINISTRATOR_5_COMMANDER_LR_PULSE_CANNON_X2", "Commander"),
        ("AGGRESSOR_4_BALLISTA_LR_MULTI_LAUNCHER2_X2", "Ballista"),
        ("CONSTRUCTOR_5_BEHEMOTH", "Behemoth"),
        ("AGGRESSOR_3_ASSASSIN_LR_PLASMA_RIFLE_X2", "Reaper"),
        ("ADMINISTRATOR_2_BOSS_T_BEE_BOMB", "Boss"),
        ("APC_5", "APC"),
    ];

    private static readonly Race[] Races = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    private const float Shown = 9;      // seconds per machine

    private readonly ClassicAssets _assets;
    private int _next;
    private double _shownFor;

    /// <summary>The machine on the screen now, for the caption under it.</summary>
    public string Caption { get; private set; } = "";

    public event Action? Changed;

    /// <summary>
    /// A display of the machines <paramref name="assets"/> build, beginning with the <paramref name="first"/>; by
    /// chance without one. Two displays begun alike show the same machines together, as the first start's choice of
    /// look does in each look.
    /// </summary>
    public MachineDisplay(ClassicAssets assets, int? first = null)
    {
        _assets = assets;
        _next = first ?? (int)(GD.Randi() % Machines.Length);
    }

    /// <summary>A machine to begin with by chance, for displays shown side by side.</summary>
    public static int AnyFirst() => (int)(GD.Randi() % Machines.Length);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        base._Ready();
        ShowNext();
    }

    public override void _Process(double delta)
    {
        _shownFor += delta;
        Yaw += (float)delta * 0.35f;
        if (_shownFor >= Shown)
            ShowNext();
        base._Process(delta);
    }

    private void ShowNext()
    {
        _shownFor = 0;
        Place(null);
        for (var tries = 0; tries < Machines.Length && Turntable is null; tries++)
        {
            var (id, name) = Machines[_next % Machines.Length];
            var race = Races[_next / Machines.Length % Races.Length];
            _next = (_next + 1) % (Machines.Length * Races.Length);
            if (_assets.RigFor(id, race) is not { } rig)
                continue;
            Yaw = Mathf.DegToRad(-20);
            Place(RigView.Create(rig));
            Caption = $"{name}  ·  {race}";
        }
        Changed?.Invoke();
    }
}
