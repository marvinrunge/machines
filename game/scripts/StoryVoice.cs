using Godot;
using NVector3 = System.Numerics.Vector3;

namespace Machines.Game;

/// <summary>
/// The story's lines (plan 5.4): a line id becomes <c>res://assets/story/voice/&lt;lang&gt;/&lt;id&gt;.ogg</c> (English when the
/// player's language has none, only the subtitle when neither has), spoken in turn by <see cref="StoryLineQueue"/> (an
/// announcement goes ahead of the radio, the radio has its turn between announcements, stale radio is dropped). The PA speaks on the Announcer bus and from the 1 to 3 nearest
/// ship loudspeakers in the world, 30 to 80 ms apart, so the hall echoes and the voice can be placed; "Gefunden." comes
/// from the one loudspeaker beside the Locator, dry; Ike and Bob speak over the radio with a click; the update's whisper
/// muffles the world while it speaks. Unlike <see cref="GameAudio.Say"/> it speaks in a film (GameAudio.Film) too, so the
/// story's cutscenes keep their voices.
/// </summary>
public partial class StoryVoice(StoryAudio audio) : Node
{
    /// <summary>The loudspeakers' group: a prop loader puts each <c>pa_speaker</c> it places in it.</summary>
    public const string SpeakerGroup = "pa_speaker";

    // The 2D voice under the loudspeakers, when there are any; alone it is the whole voice.
    private const float UnderSpeakersDb = -7;

    private readonly StoryLineQueue _queue = new();
    private readonly List<(double At, string? Owner, Action Do)> _pending = [];
    private readonly List<AudioStreamPlayer3D> _speakers = [];
    private AudioStreamPlayer _flat = null!, _click = null!;
    private readonly Dictionary<string, (string? Path, double Seconds, string? Preset)> _resolved = [];
    private StoryLineCue? _speaking;

    public override void _Ready()
    {
        _flat = new AudioStreamPlayer { Name = "Line", Bus = StoryAudio.VoiceBus };
        _click = new AudioStreamPlayer { Name = "Click", Bus = StoryAudio.RadioBus };
        AddChild(_flat);
        AddChild(_click);
    }

    public override void _Process(double delta)
    {
        var now = audio.Clock;
        Apply(_queue.Update(now));
        // What is due runs after it is taken out: a subtitle's listener may say the next line at once.
        var due = _pending.Where(p => p.At <= now).ToList();
        if (due.Count == 0)
            return;
        _pending.RemoveAll(p => p.At <= now);
        foreach (var (_, _, action) in due)
            action();
    }

    public void Say(string id)
    {
        var (path, seconds, preset) = Resolve(id);
        var channel = StorySoundscape.Channel(id, preset);
        var voiced = path is not null;
        if (channel == StoryVoiceChannel.Text)
        {
            // HUD text: never spoken, never waits.
            var cue = new StoryLineCue(id, channel, StorySoundscape.UnvoicedSeconds, false, Lang.Code);
            audio.Trace($"line {id} text");
            StoryAudio.Raise(cue, true);
            _pending.Add((audio.Clock + cue.Seconds, null, () => StoryAudio.Raise(cue, false)));
            return;
        }
        var line = new StoryQueuedLine(id, channel, voiced ? seconds : StorySoundscape.UnvoicedSeconds, audio.Clock, voiced)
        {
            Gong = StorySoundscape.Gong(id, preset),
        };
        Apply(_queue.Add(line, audio.Clock));
    }

    /// <summary>
    /// A cutscene's line, said now, as its script times it, on its own channel (the PA's band and hall, the radio's), so speech
    /// ducks the world under it as in play; its subtitle comes with it for as long as its voice. An announcement or thought
    /// speaking is cut off; a Grunt's bark speaking is let finish and the line follows it (StoryLineQueue.Force). The lines
    /// waiting (a Grunt's bark as control comes back) are said after it.
    /// </summary>
    public void SayNow(string id)
    {
        var (path, seconds, preset) = Resolve(id);
        if (StorySoundscape.Channel(id, preset) is var channel && channel == StoryVoiceChannel.Text)
        {
            Say(id);
            return;
        }
        var voiced = path is not null;
        Apply(_queue.Force(new StoryQueuedLine(id, channel, voiced ? seconds : StorySoundscape.UnvoicedSeconds, audio.Clock, voiced), audio.Clock));
    }

    /// <summary>The waiting lines of an id are dropped.</summary>
    public void Withdraw(string id) => Apply(_queue.Withdraw(id));

    public void Cut(bool clear, bool crackle, bool onlyAnnouncements = false)
    {
        var speaking = _queue.Current;
        Apply(_queue.Cut(audio.Clock, clear, onlyAnnouncements));
        if (crackle && speaking is { Channel: StoryVoiceChannel.Announcer or StoryVoiceChannel.Local })
            StoryAudio.Play("pa_cutoff");
        // A thought cut short (the boot skipped) breaks off in the HUD's crackle.
        else if (crackle && speaking is { Channel: StoryVoiceChannel.Inner })
            StoryAudio.Play("hud_glitch");
    }

    // A line's file in the player's language, else English; its length from the stream (the manifest's when it cannot tell).
    private (string? Path, double Seconds, string? Preset) Resolve(string id)
    {
        var key = $"{Lang.Code}/{id}";
        if (_resolved.TryGetValue(key, out var known))
            return known;
        var manifest = StoryAudio.VoiceFiles;
        string? path = null;
        double seconds = 0;
        foreach (var lang in Lang.Code == "en" ? new[] { "en" } : [Lang.Code, "en"])
        {
            var file = manifest.Find(id, lang) is { } entry && entry.Lang == lang ? entry : null;
            var candidate = $"{StoryAudio.VoiceFolder}/{file?.File ?? $"{lang}/{id}.ogg"}";
            if (!StoryAudio.Exists(candidate))
                continue;
            path = candidate;
            seconds = StoryAudio.Stream(candidate)?.GetLength() is > 0 and var length ? length : file?.Seconds ?? 0;
            break;
        }
        var result = (path, seconds > 0 ? seconds : StorySoundscape.UnvoicedSeconds, manifest.Preset(id));
        _resolved[key] = result;
        return result;
    }

    private void Apply(List<StoryQueueEvent> events)
    {
        foreach (var change in events)
        {
            var line = change.Line;
            switch (change.Change)
            {
                case StoryQueueChange.Started:
                    Start(line);
                    break;
                case StoryQueueChange.Ended:
                case StoryQueueChange.Interrupted:
                    Stop(line, change.Change == StoryQueueChange.Interrupted);
                    break;
                case StoryQueueChange.Dropped:
                    audio.Trace($"line {line.Id} dropped");
                    break;
            }
        }
    }

    private void Start(StoryQueuedLine line)
    {
        var path = line.Voiced ? Resolve(line.Id).Path : null;
        var cue = new StoryLineCue(line.Id, line.Channel, line.Seconds, path is not null, Lang.Code);
        _speaking = cue;
        var at = audio.Clock + line.Lead;
        if (line.Lead > 0)
            StoryAudio.Play("pa_gong");
        // The subtitle comes with the voice, after the gong.
        _pending.Add((at, line.Id, () =>
        {
            if (!ReferenceEquals(_speaking, cue))
                return;
            StoryAudio.Raise(cue, true);
            if (path is null || StoryAudio.Stream(path) is not { } stream)
            {
                audio.Trace($"line {line.Id} {Channel(line.Channel)} subtitle-only");
                return;
            }
            Speak(line, stream);
        }));
    }

    private void Speak(StoryQueuedLine line, AudioStream stream)
    {
        var listener = audio.Listener;
        switch (line.Channel)
        {
            case StoryVoiceChannel.Announcer:
            {
                var speakers = StorySoundscape.NearestSpeakers(Speakers(listener), N(listener));
                _flat.Bus = StoryAudio.AnnouncerBus;
                _flat.VolumeDb = speakers.Count > 0 ? UnderSpeakersDb : 0;
                _flat.Stream = stream;
                _flat.Play();
                for (var i = 0; i < speakers.Count; i++)
                {
                    var player = Speaker(i);
                    player.Bus = StoryAudio.AnnouncerBus;
                    player.Stream = stream;
                    player.GlobalPosition = G(speakers[i]);
                    _pending.Add((audio.Clock + StorySoundscape.SpeakerDelay(i), line.Id, () => player.Play()));
                }
                audio.Trace($"line {line.Id} announcer speakers={speakers.Count} {Lang.Code} {line.Seconds:0.00}s");
                break;
            }
            case StoryVoiceChannel.Local:
            {
                // The loudspeaker right beside the Locator, else one made up a step to its right.
                var near = StorySoundscape.NearestSpeakers(Speakers(listener), N(listener), 1, 6);
                var player = Speaker(0);
                player.Bus = StoryAudio.VoiceBus;
                player.Stream = stream;
                player.GlobalPosition = near.Count > 0 ? G(near[0]) : listener + Right() * 1.5f + Vector3.Up * 0.8f;
                player.Play();
                audio.Trace($"line {line.Id} local {(near.Count > 0 ? "speaker" : "beside")} {Lang.Code} {line.Seconds:0.00}s");
                break;
            }
            case StoryVoiceChannel.Radio:
                _flat.Bus = StoryAudio.RadioBus;
                _flat.VolumeDb = 0;
                _flat.Stream = stream;
                Click();
                _pending.Add((audio.Clock + 0.08, line.Id, () => _flat.Play()));
                audio.Trace($"line {line.Id} radio {Lang.Code} {line.Seconds:0.00}s");
                break;
            case StoryVoiceChannel.Inner:
                // The Locator's own thought: in the middle, close and dry; speech ducks the world and the music by itself.
                _flat.Bus = StoryAudio.VoiceBus;
                _flat.VolumeDb = 0;
                _flat.Stream = stream;
                _flat.Play();
                audio.Trace($"line {line.Id} inner {Lang.Code} {line.Seconds:0.00}s");
                break;
            case StoryVoiceChannel.Whisper:
                _flat.Bus = StoryAudio.VoiceBus;
                _flat.VolumeDb = 0;
                _flat.Stream = stream;
                _flat.Play();
                StoryAudio.SetMuffled("whisper", true);
                audio.Trace($"line {line.Id} whisper {Lang.Code} {line.Seconds:0.00}s");
                break;
        }
    }

    private void Stop(StoryQueuedLine line, bool interrupted)
    {
        var cue = _speaking;
        _speaking = null;
        _flat.Stop();
        foreach (var speaker in _speakers)
            speaker.Stop();
        // Loudspeakers still waiting for their few milliseconds do not start.
        _pending.RemoveAll(p => p.Owner == line.Id);
        if (line.Channel == StoryVoiceChannel.Radio)
            Click();
        if (line.Channel == StoryVoiceChannel.Whisper)
            StoryAudio.SetMuffled("whisper", false);
        audio.Trace($"line {line.Id} {(interrupted ? "cut" : "end")}");
        if (cue is not null && cue.Id == line.Id)
            StoryAudio.Raise(cue, false);
    }

    private void Click()
    {
        if (StoryAudio.SfxPath("hack_click") is { } path && StoryAudio.Stream(path) is { } click)
        {
            _click.Stream = click;
            _click.PitchScale = 0.6f;
            _click.Play();
        }
    }

    private AudioStreamPlayer3D Speaker(int index)
    {
        while (_speakers.Count <= index)
        {
            var player = new AudioStreamPlayer3D
            {
                Name = $"Speaker{_speakers.Count}",
                UnitSize = 14,
                MaxDistance = 150,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                PanningStrength = 1,
            };
            AddChild(player);
            _speakers.Add(player);
        }
        return _speakers[index];
    }

    /// <summary>
    /// The ship's loudspeakers: the props in <see cref="SpeakerGroup"/>, the deck's <c>pa_speaker</c> props and markers
    /// (on the ceiling of their room), and, when the room the listener is in has none, one under the middle of its ceiling.
    /// </summary>
    private IEnumerable<NVector3> Speakers(Vector3 listener)
    {
        var any = false;
        foreach (var node in GetTree().GetNodesInGroup(SpeakerGroup).OfType<Node3D>())
        {
            any = true;
            yield return N(node.GlobalPosition);
        }
        if (any || audio.Deck is not { } deck)
            yield break;
        NVector3 Ceiling(System.Numerics.Vector2 at)
        {
            var room = StorySoundscape.RoomAt(deck, at);
            var height = float.TryParse(room?.Tag("height"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 6;
            return new NVector3(at.X, Mathf.Max(2, height - 0.6f), at.Y);
        }
        foreach (var prop in deck.Props.Where(p => p.Type == SpeakerGroup))
        {
            any = true;
            yield return Ceiling(prop.Position);
        }
        foreach (var marker in deck.Markers.Where(m => m.Tags.Contains(SpeakerGroup)))
        {
            any = true;
            yield return Ceiling(marker.Position);
        }
        if (!any && StorySoundscape.RoomAt(deck, new(listener.X, listener.Z)) is { } here)
            yield return Ceiling((here.Min + here.Max) / 2);
    }

    private Vector3 Right() => GetViewport()?.GetCamera3D() is { } camera ? camera.GlobalBasis.X : Vector3.Right;

    private static NVector3 N(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 G(NVector3 v) => new(v.X, v.Y, v.Z);

    private static string Channel(StoryVoiceChannel channel) => channel.ToString().ToLowerInvariant();
}
