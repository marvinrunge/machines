using Godot;

namespace Machines.Game;

// Package W's held sounds (plan stufe7 §4 W, design-waves §8.2): loops in the world that last as long as something lasts
// and follow where it goes. The workbench's strip, mount and mend while the use key is held, the heavy Grunt's motor and its
// charge's scrape, the barracks lift's hum behind gate N, an electric unit's crackle. Game.StoryArmoury says each look which
// it wants (Keep); a loop no longer wanted fades out. A sound that does not loop by itself (the welding hiss) starts again
// as it ends.
public partial class StorySounds
{
    /// <summary>How fast a held sound comes in and goes, seconds.</summary>
    private const float HeldIn = 0.15f, HeldOut = 0.3f;

    private readonly Dictionary<string, AudioStreamPlayer3D> _held = new(StringComparer.Ordinal);

    // Held sounds fading out, by key: wanted again before they are gone, they come back where they were instead of starting
    // their recording again. And each player's running fade, so a new one replaces it rather than fighting it.
    private readonly Dictionary<string, AudioStreamPlayer3D> _fading = new(StringComparer.Ordinal);
    private readonly Dictionary<AudioStreamPlayer3D, Tween> _fades = [];

    /// <summary>A held sound: which effect, where it is now, how loud (dB) and at what pitch.</summary>
    public readonly record struct Held(string Id, Vector3 At, float Db = 0, float Pitch = 1);

    /// <summary>
    /// The held sounds whose keys start with <paramref name="prefix"/> are those of <paramref name="wanted"/> from now on:
    /// a new one starts, one still wanted moves to its place, the others fade out.
    /// </summary>
    public void Keep(string prefix, IReadOnlyDictionary<string, Held> wanted)
    {
        foreach (var key in _held.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !wanted.ContainsKey(k)).ToList())
            Let(key);
        foreach (var (key, sound) in wanted)
            Hold(key, sound);
    }

    /// <summary>Starts a held sound under <paramref name="key"/>, or moves the one playing there (its loudness and pitch too).</summary>
    public void Hold(string key, Held sound)
    {
        if (_fading.Remove(key, out var fading) && IsInstanceValid(fading))
        {
            _held[key] = fading;
            fading.SetMeta("db", Silent);
        }
        if (_held.TryGetValue(key, out var playing))
        {
            if (IsInstanceValid(playing))
            {
                playing.GlobalPosition = sound.At;
                playing.PitchScale = sound.Pitch;
                if (!Mathf.IsEqualApprox(playing.GetMeta("db", 0f).AsSingle(), sound.Db))
                {
                    playing.SetMeta("db", sound.Db);
                    Refade(playing).TweenProperty(playing, "volume_db", sound.Db, HeldIn);
                }
                return;
            }
            _held.Remove(key);
        }
        var resolved = StoryAudio.Sfx.Resolve(sound.Id, count => _random.RandiRange(0, count - 1));
        if (resolved is null || StoryAudio.SfxPath(resolved) is not { } path || StoryAudio.Stream(path) is not { } stream)
        {
            audio.Trace($"sfx {sound.Id} missing");
            return;
        }
        var player = new AudioStreamPlayer3D
        {
            Name = $"Held-{key.Replace(':', '-')}",
            Stream = stream,
            Bus = GameAudio.EffectsBus,
            UnitSize = 10,
            MaxDistance = 140,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            PanningStrength = 1,
            PitchScale = sound.Pitch,
            VolumeDb = Silent,
        };
        player.SetMeta("db", sound.Db);
        AddChild(player);
        player.GlobalPosition = sound.At;
        // A sound that does not loop by itself goes round again while it is held.
        player.Finished += () =>
        {
            if (IsInstanceValid(player) && _held.ContainsValue(player))
                player.Play();
        };
        player.Play();
        Refade(player).TweenProperty(player, "volume_db", sound.Db, HeldIn);
        _held[key] = player;
        audio.Trace($"sfx-held {resolved} on {key}");
    }

    /// <summary>The held sound under <paramref name="key"/> fades out and goes.</summary>
    public void Let(string key)
    {
        if (!_held.Remove(key, out var player) || !IsInstanceValid(player))
            return;
        _fading[key] = player;
        var tween = Refade(player);
        tween.TweenProperty(player, "volume_db", Silent, HeldOut);
        tween.TweenCallback(Callable.From(() =>
        {
            if (_fading.TryGetValue(key, out var gone) && gone == player)
                _fading.Remove(key);
            _fades.Remove(player);
            player.QueueFree();
        }));
        audio.Trace($"sfx-held off {key}");
    }

    /// <summary>A new fade of a held sound's loudness, in place of the one it may still be running.</summary>
    private Tween Refade(AudioStreamPlayer3D player)
    {
        if (_fades.Remove(player, out var running) && running.IsValid())
            running.Kill();
        var tween = player.CreateTween();
        _fades[player] = tween;
        return tween;
    }
}
