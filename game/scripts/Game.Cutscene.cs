using Godot;

namespace Machines.Game;

// The story's live cutscenes in a match (Cutscene), and how a ship in distress leans the player's view (Tilt): the remake's
// own. Nothing here is asked for in any other match, and none of it touches the simulation.
public partial class Game
{
    private Cutscene? _cutscene;

    /// <summary>Whether a cutscene has the screen: the player's keys and mouse do nothing, and their machine stands.</summary>
    public bool InCutscene => _cutscene is not null && IsInstanceValid(_cutscene);

    /// <summary>
    /// Words a cutscene passes on for the story as its cues come (<c>event</c> in its script: "lights emergency",
    /// "lights off" ...), for the deck's lights and whatever else the story shows of them.
    /// </summary>
    public event Action<string>? CutsceneTold;

    /// <summary>
    /// Plays a cutscene by its script's name (data/planets/&lt;chapter&gt;/&lt;name&gt;.cut): its deck part on a story deck, its
    /// outside part elsewhere. <paramref name="finished"/> hears when the player has the view back (and whether they
    /// skipped). Null when there is no such script or part, or another cutscene plays.
    /// </summary>
    public Cutscene? PlayCutscene(string name, Action<bool>? finished = null)
    {
        if (InCutscene)
            return null;
        if (FilmCamera.ScriptPath(name) is not { } path)
        {
            GD.PrintErr($"cutscene: no script '{name}' (data/planets/*/{name}.cut)");
            return null;
        }
        var script = CutsceneScript.Load(path);
        if (script.Part(World.Deck is not null) is not { } part)
        {
            GD.PrintErr($"cutscene: '{name}' has no part to play {(World.Deck is not null ? "on a deck" : "outside")}");
            return null;
        }
        // What the Grunts were still waiting to say is stale once it plays (Ike's urging came as the pod dropped).
        foreach (var bark in Machines.Sim.Story.CompanionBarks.English.Keys)
            StoryAudio.Withdraw(bark);
        var (panel, screen) = (_hud.Visible, _screen.Visible);
        var stage = new Cutscene.Stage(
            _rig,
            () => _driven is not null ? _firstPersonCamera : null,
            _filmLook,
            shown =>
            {
                _hud.Visible = shown && panel;
                _screen.Visible = shown && screen;
            },
            // The machine the cutscene rode in shows again; the player's own is hidden from their eye again.
            () =>
            {
                if (_driven is { } driven && _views.TryGetValue(driven.Id, out var view))
                    view.SetDriven(true);
            },
            _driven?.Id,
            () => TiltNow,
            TiltTo,
            // Its lines play as the script times them, on the story's voice channels (the PA's band and hall; speech ducks
            // the world), and show as subtitles for as long as their voices (on their own layer, which the cutscene leaves
            // up): the announcement Erwischt turns on was heard and never read. Without the story's audio only the subtitle.
            Speak: id =>
            {
                if (StoryAudio.Current is { } audio)
                {
                    audio.Voice.SayNow(id);
                    return true;
                }
                ShowStoryLine(id, null);
                return false;
            },
            Tell: words => CutsceneTold?.Invoke(words));
        var cutscene = new Cutscene(World, id => _views.GetValueOrDefault(id), script, part, stage) { Models = SceneModel };
        cutscene.Finished += skipped =>
        {
            if (_cutscene == cutscene)
                _cutscene = null;
            finished?.Invoke(skipped);
        };
        _cutscene = cutscene;
        AddChild(cutscene);
        return cutscene;
    }

    /// <summary>
    /// A model of the original a cutscene shows by name: <c>pod</c>, or a machine's or building's model id. The story's
    /// pod (the escape pods in the bay, the one dropped and landed outside) is always the HD Pod, the player's own Meshy
    /// model with its four spikes on the turning mast (renders/meshy/buildings/pod-1-spike), whichever look is chosen.
    /// </summary>
    private Node3D? SceneModel(string name)
    {
        var id = name == "pod" ? "POD_1_T_ION_ORBITAL_CANNON" : name;
        var race = Look(Player);
        if (name == "pod" && (_podAssets ??= new ClassicAssets(Paths.Root, ModelLook.Hd)).RigFor(id, race) is { } pod)
            return RigView.Create(pod);
        if (_assets.RigFor(id, race) is { } rig)
            return RigView.Create(rig);
        return _assets.HasClassicModels ? new MeshInstance3D { Mesh = _assets.MeshFor(id, race, new Vector3(10, 8, 10)), Name = id } : null;
    }

    /// <summary>The HD look's assets for the story's pod alone (<see cref="SceneModel"/>), made when first needed.</summary>
    private ClassicAssets? _podAssets;

    // ---- the view leaning ----------------------------------------------------------------------------

    private CameraTilt _tiltFrom, _tiltTo;
    private double _tiltStart, _tiltEase;

    /// <summary>
    /// How the player's view leans (the camera only: the level and the panel stay level), as the ship lists on its way down;
    /// setting it leans the view at once (<see cref="TiltTo"/> eases).
    /// </summary>
    public CameraTilt Tilt
    {
        get => TiltNow;
        set
        {
            _tiltFrom = _tiltTo = value;
            _tiltEase = 0;
        }
    }

    /// <summary>Leans the view towards <paramref name="to"/> over <paramref name="seconds"/>.</summary>
    public void TiltTo(CameraTilt to, float seconds)
    {
        _tiltFrom = TiltNow;
        _tiltTo = to;
        _tiltStart = FrameClock.Now;
        _tiltEase = seconds;
    }

    private CameraTilt TiltNow => _tiltEase <= 0
        ? _tiltTo
        : CameraTilt.Lerp(_tiltFrom, _tiltTo, Mathf.Clamp((float)((FrameClock.Now - _tiltStart) / _tiltEase), 0, 1));

    /// <summary>The turn the lean gives a camera now, in its own frame (none while level).</summary>
    private Basis Leaning() => TiltNow is { Level: false } tilt ? tilt.At(FrameClock.Now) : Basis.Identity;

    // ---- for checks ------------------------------------------------------------------------------

    private bool _cutsceneAsked;
    private double _cutsceneWait;

    /// <summary>
    /// For checks: MACHINES_CUTSCENE=&lt;name&gt; plays that cutscene once the match shows; MACHINES_CUTSCENE_STILLS=
    /// &lt;folder&gt;;&lt;name&gt;;&lt;seconds&gt;,... plays it and saves the picture at those seconds of it as
    /// &lt;folder&gt;/&lt;name&gt;-&lt;part&gt;_&lt;seconds&gt;.png, then quits. With MACHINES_STORY=prolog the deck part plays,
    /// with MACHINES_MAP=prolog-abwurf,m_desert the outside part.
    /// </summary>
    private void CutsceneIfAsked(double delta)
    {
        if (_cutsceneAsked || Hidden)
            return;
        var stills = System.Environment.GetEnvironmentVariable("MACHINES_CUTSCENE_STILLS")?.Split(';');
        var name = stills is [_, var named, _] ? named : System.Environment.GetEnvironmentVariable("MACHINES_CUTSCENE");
        if (string.IsNullOrEmpty(name))
        {
            _cutsceneAsked = true;
            return;
        }
        // Let the views settle and the player be seated first.
        _cutsceneWait += delta;
        if (_cutsceneWait < 1.5)
            return;
        _cutsceneAsked = true;
        var cutscene = PlayCutscene(name);
        if (cutscene is null)
        {
            if (stills is not null)
                GetTree().Quit(1);
            return;
        }
        if (stills is [var folder, _, var times])
            cutscene.Stills(folder, times.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
