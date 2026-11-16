using Godot;

namespace Machines.Game;

/// <summary>
/// The main menu's showpiece rehearsed where nobody sees it: a screen like <see cref="MachineDisplay"/>'s, see-through
/// and rendering all the time, that waits for <see cref="MachineDisplay.Preload"/> and then shows every machine the
/// display shows, in each race's colours, all at once, until the GPU has built the pipelines for them (none more for
/// fifteen frames, four seconds at most); then it is <see cref="Done"/> and leaves. Begun once as the game starts,
/// under the loading picture, so that the machines change on the menu's screen without halting it.
/// </summary>
public partial class MachineWarmup : MachineScreen
{
    // Frames without a new pipeline before the GPU counts as done, and the longest the rehearsal may take.
    private const int QuietFrames = 15;
    private const double MostSeconds = 4;

    private static bool _begun;

    private readonly ClassicAssets _assets;
    private readonly MachineDisplay.Loading _loading;
    private bool _placed;
    private int _quiet;
    private long _built;
    private double _shownFor;

    /// <summary>Whether the machines have been drawn and the GPU has done with them (or there was nothing to draw).</summary>
    public bool Done { get; private set; }

    /// <summary>
    /// How far the rehearsal has come, from 0 to 1, for the loading bar: the loading the first nine tenths, the drawing
    /// the rest, as its quiet frames count up.
    /// </summary>
    public float Progress => Done ? 1 : !_placed ? 0.9f * _loading.Share : 0.9f + 0.1f * Math.Max(_quiet / (float)QuietFrames, (float)(_shownFor / MostSeconds));

    private MachineWarmup(ClassicAssets assets)
    {
        _assets = assets;
        _loading = MachineDisplay.Preload(assets);
    }

    /// <summary>The rehearsal, the first time the menus open in this run of the game; null after.</summary>
    public static MachineWarmup? Begin(ClassicAssets assets)
    {
        if (_begun)
            return null;
        _begun = true;
        return new MachineWarmup(assets);
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        base._Ready();
        // Not seen, yet drawn: the screen's picture is clear, and its world renders though nothing shows it.
        SelfModulate = Colors.Transparent;
        Scene.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        Size = new Vector2(320, 240);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (Done)
            return;
        if (!_placed)
        {
            if (!_loading.Task.IsCompleted)
                return;
            _placed = true;
            Place(Lineup());
            _built = HitchLog.Compiled();
            if (Turntable is null)
                Finish();
            return;
        }
        _shownFor += delta;
        var built = HitchLog.Compiled();
        _quiet = built == _built ? _quiet + 1 : 0;
        _built = built;
        if (_quiet >= QuietFrames || _shownFor >= MostSeconds)
            Finish();
    }

    // Every machine side by side, in rows by race, near enough together for the camera to have them all in view.
    private Node3D? Lineup()
    {
        var lineup = new Node3D();
        var count = 0;
        foreach (var (id, race) in MachineDisplay.AllShown())
        {
            if (_assets.RigFor(id, race) is not { } rig)
                continue;
            var view = RigView.Create(rig);
            view.Position = new Vector3(count % 8 * 8, 0, count / 8 * 8);
            lineup.AddChild(view);
            count++;
        }
        if (count > 0)
            return lineup;
        lineup.Free();
        return null;
    }

    private void Finish()
    {
        Done = true;
        Place(null);
        QueueFree();
    }
}
