using Godot;
using Machines.Net;
using Machines.Sim.Story;

namespace Machines.Game;

// The end of a story chapter (the remake's own): the trio got away, the bay's part of the chapter's film has played, and the
// film goes on outside on a map of its own (StoryFilm); after it the chapter's card and the menus. Nothing here runs in any
// other match.
public partial class Game
{
    /// <summary>How long after the trio got away the chapter's film goes on outside at the latest, seconds (the bay's part over or skipped).</summary>
    private const double StoryEndWait = 0.6;

    private bool _storyEnding, _storyFilmStarted;
    private double _storyEndedFor, _storyFilmWait;
    private ColorRect? _storyBlack;

    /// <summary>
    /// The chapter won (<see cref="StoryEnding.Escaped"/>): once the bay's part of its film is over (it ends black, and black
    /// it stays), the chapter is noted as done in the host's progress, the room left when it was played online, and its film
    /// goes on outside (<see cref="StoryFilm.Play"/>).
    /// </summary>
    private void FollowStoryEnd(double delta)
    {
        if (_storyEnding || Watching || World.Story is not { Ending: StoryEnding.Escaped } story || InCutscene)
            return;
        _storyEndedFor += delta;
        if (_storyEndedFor < StoryEndWait)
            return;
        _storyEnding = true;
        HoldStoryBlack();
        if (Me == story.Locator)
        {
            var progress = StoryProgress.Load();
            progress.Finished(story.Options.Chapter);
            progress.Save();
        }
        var online = Online;
        if (online)
        {
            SaveReplay();
            NetLink.Send(new LeaveRoom());
            NetLink.ForgetSeat();
        }
        if (StoryFilm.Of(story.Options.Chapter, online) is { } film)
            StoryFilm.Play(GetTree(), film);
        else
            LeaveStoryFilm(online);
        if (_sceneScripted)
            GD.Print($"scene: the chapter ends with its film{(online ? ", the room left" : "")}");
    }

    /// <summary>The view goes black and stays so: the bay's part of the chapter's film ended in black, and the outside comes up from it.</summary>
    private void HoldStoryBlack()
    {
        if (_storyBlack is not null)
            return;
        _storyBlack = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore, Name = "StoryBlack" };
        _storyBlack.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var layer = new CanvasLayer { Layer = 90, Name = "StoryBlackLayer" };
        layer.AddChild(_storyBlack);
        AddChild(layer);
    }

    /// <summary>Whether this match only films the end of a chapter (<see cref="MatchRequest.Film"/>): no panel, no keys but the film's and the card's.</summary>
    public bool FilmingStoryEnd => MatchRequest.Current.Film is not null;

    /// <summary>
    /// The film of a chapter's end on its own map: once the match shows, its cutscene plays the outside part, then the
    /// chapter's card (<see cref="StoryDoneCard"/>) stands until the player goes on to the menus.
    /// </summary>
    private void FollowStoryFilm(double delta)
    {
        if (_storyFilmStarted || Hidden || MatchRequest.Current.Film is not { } film)
            return;
        _hud.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Hidden;
        // Let the views settle first, as a cutscene asked for by a check does.
        _storyFilmWait += delta;
        if (_storyFilmWait < 0.5)
            return;
        _storyFilmStarted = true;
        FilmLimiter(true);
        if (_sceneScripted)
            GD.Print($"scene: the film {film.Cutscene} of {film.Chapter} begins");
        if (PlayCutscene(film.Cutscene, _ => ShowStoryDone(film)) is null)
            ShowStoryDone(film);
    }

    private void ShowStoryDone(StoryFilm film)
    {
        if (_sceneScripted)
            GD.Print($"scene: the chapter's card ({StoryDoneCard.Words(film.Chapter).Title})");
        var layer = new CanvasLayer { Layer = 95, Name = "StoryDoneLayer" };
        layer.AddChild(new StoryDoneCard(film, () => LeaveStoryFilm(film.Online)));
        AddChild(layer);
    }

    // The limiter on Master while a chapter's film plays outside, null while none is on.
    private static AudioEffectHardLimiter? _filmLimiter;

    /// <summary>
    /// A limiter on Master while a chapter's end is filmed outside (the music of the map and the pod's drop and impact come
    /// together there and clipped); off again when the film's match is left. Only the film's match has it: every other
    /// match sounds as before.
    /// </summary>
    private static void FilmLimiter(bool on)
    {
        var master = AudioServer.GetBusIndex("Master");
        if (_filmLimiter is { } old)
        {
            for (var i = AudioServer.GetBusEffectCount(master) - 1; i >= 0; i--)
                if (AudioServer.GetBusEffect(master, i) == old)
                    AudioServer.RemoveBusEffect(master, i);
            _filmLimiter = null;
        }
        if (!on)
            return;
        _filmLimiter = new AudioEffectHardLimiter { CeilingDb = -1, PreGainDb = -3, Release = 0.12f };
        AudioServer.AddBusEffect(master, _filmLimiter);
    }

    /// <summary>On from the chapter's end to the menus: the story's page, or the rooms when it was played online.</summary>
    private void LeaveStoryFilm(bool online)
    {
        if (_sceneScripted)
            GD.Print("scene: the story's end leaves for the menus");
        MenuScreens.StartPage = online ? "multiplayer" : "story";
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().ChangeSceneToFile("res://scenes/Menu.tscn");
    }
}
