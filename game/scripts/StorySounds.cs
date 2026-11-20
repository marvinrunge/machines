using Godot;

namespace Machines.Game;

/// <summary>
/// The story's sound effects and the rooms' ambience (plan 5.2, 5.4): one-shots by their id in
/// <c>game/assets/story/sfx/manifest.json</c> (a numbered family such as <c>thresher_step</c> plays one of its variants),
/// placed in the world or not; the ambience loops of the room the listener is in (<see cref="StorySoundscape.Ambience"/>)
/// or of the zone the director names, cross-faded as one walks, with the room's reverb on the World bus; and loops the
/// director lays over them (the alarm's klaxon, fire). Everything plays on the game's Effects bus, which sends into World
/// during a story, so the room's reverb, the muffling and the ducking take it as they take the machines' own sounds.
/// </summary>
public partial class StorySounds(StoryAudio audio) : Node
{
    private const int Voices = 16;
    private const float Fade = 1.5f, Silent = -60;

    private readonly List<Node> _voices = [];
    private readonly Dictionary<string, AudioStreamPlayer> _ambience = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioStreamPlayer> _layers = new(StringComparer.Ordinal);
    private readonly RandomNumberGenerator _random = new();
    private string? _pinned;
    private string? _room = "";

    public void Play(string id, Vector3? at)
    {
        var sound = StoryAudio.Sfx.Resolve(id, count => _random.RandiRange(0, count - 1));
        if (sound is null || StoryAudio.SfxPath(sound) is not { } path || StoryAudio.Stream(path) is not { } stream)
        {
            audio.Trace($"sfx {id} missing");
            return;
        }
        if (at is { } place)
        {
            var player = Voice<AudioStreamPlayer3D>();
            player.Stream = stream;
            player.UnitSize = 6;
            player.MaxDistance = 80;
            player.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance;
            player.PanningStrength = 1;
            player.GlobalPosition = place;
            player.Play();
            audio.Trace($"sfx {sound} at {place.X:0.#},{place.Y:0.#},{place.Z:0.#}");
        }
        else
        {
            var player = Voice<AudioStreamPlayer>();
            player.Stream = stream;
            player.Play();
            audio.Trace($"sfx {sound}");
        }
    }

    // A free voice of the kind, a new one while there are fewer than Voices, else the one playing longest.
    private T Voice<T>() where T : Node, new()
    {
        bool Playing(Node n) => n is AudioStreamPlayer f ? f.Playing : ((AudioStreamPlayer3D)n).Playing;
        if (_voices.OfType<T>().FirstOrDefault(v => !Playing(v)) is { } free)
            return free;
        if (_voices.Count >= Voices && _voices.OfType<T>().FirstOrDefault() is { } oldest)
        {
            _voices.Remove(oldest);
            _voices.Add(oldest);
            return oldest;
        }
        var player = new T();
        player.Name = $"Sfx{_voices.Count}";
        player.Set("bus", GameAudio.EffectsBus);
        AddChild(player);
        _voices.Add(player);
        return player;
    }

    /// <summary>The zone whose ambience plays: a room of the deck, <c>section:&lt;n&gt;</c>, or null to follow the listener.</summary>
    public void SetAmbience(string? zone)
    {
        _pinned = zone;
        if (zone is not null)
            Room(zone);
        else
            _room = "";
    }

    /// <summary>Follows the room the listener is in, unless the director has named one.</summary>
    public void Look(Vector3 listener, int section)
    {
        if (_pinned is not null)
            return;
        var room = StorySoundscape.RoomAt(audio.Deck, new(listener.X, listener.Z));
        Room(room?.Name ?? $"section:{section}");
    }

    private void Room(string zone)
    {
        if (zone == _room)
            return;
        _room = zone;
        var section = zone.StartsWith("section:", StringComparison.Ordinal) && int.TryParse(zone[8..], out var s) ? s : 0;
        var room = audio.Deck?.Zone(zone);
        var layers = StorySoundscape.Ambience(section > 0 ? null : zone, section > 0 ? section : StorySoundscape.Section(room));
        var wanted = layers.ToDictionary(l => l.Sound, StringComparer.Ordinal);
        foreach (var (sound, player) in _ambience.ToList())
            if (!wanted.ContainsKey(sound))
            {
                _ambience.Remove(sound);
                FadeOut(player);
            }
        foreach (var layer in layers)
        {
            if (_ambience.TryGetValue(layer.Sound, out var playing))
                FadeTo(playing, layer.Db);
            else if (Loop(layer, $"Ambience-{layer.Sound}") is { } player)
                _ambience[layer.Sound] = player;
        }
        audio.SetReverb(StorySoundscape.Reverb(room));
        audio.Trace($"ambience {zone} {string.Join('+', layers.Select(l => l.Sound))}");
    }

    /// <summary>A loop over the ambience, on or off.</summary>
    public void SetLayer(string id, bool on, float db)
    {
        if (_layers.Remove(id, out var playing))
        {
            if (on)
                _layers[id] = playing;
            else
                FadeOut(playing);
        }
        if (on)
        {
            if (_layers.TryGetValue(id, out var player))
                FadeTo(player, db);
            else if (Loop(new StoryLayer(id, db), $"Layer-{id}") is { } started)
                _layers[id] = started;
        }
        audio.Trace($"layer {id} {(on ? "on" : "off")}");
    }

    /// <summary>A looping player of a layer on <paramref name="bus"/>, fading in; null when the sound is missing.</summary>
    internal AudioStreamPlayer? Loop(StoryLayer layer, string name, string? bus = null, Node? parent = null)
    {
        if (StoryAudio.SfxPath(layer.Sound) is not { } path || StoryAudio.Stream(path) is not { } stream)
            return null;
        var player = new AudioStreamPlayer { Name = name, Stream = stream, Bus = bus ?? GameAudio.EffectsBus, VolumeDb = Silent, PitchScale = layer.Pitch };
        (parent ?? this).AddChild(player);
        // Loops start at a random point, so two rooms of the same hum do not start in step.
        player.Play((float)(_random.Randf() * Math.Max(0, stream.GetLength() - 1)));
        FadeTo(player, layer.Db);
        return player;
    }

    internal static void FadeTo(AudioStreamPlayer player, float db, float seconds = Fade)
    {
        var tween = player.CreateTween();
        tween.TweenProperty(player, "volume_db", db, seconds);
    }

    internal static void FadeOut(AudioStreamPlayer player, float seconds = Fade)
    {
        var tween = player.CreateTween();
        tween.TweenProperty(player, "volume_db", Silent, seconds);
        tween.TweenCallback(Callable.From(player.QueueFree));
    }
}
