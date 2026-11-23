using Godot;

namespace Machines.Game;

// Package E's sounds (plan stufe7 §4 E, design-escape §7): the loops that go with a thing of the escape while it moves or
// burns (a jet's roar, a container's scrape along its rail, the shutter's grind as it lowers), placed on that thing's node so
// they follow it, and faded out when it stops. The one-shots are played by their ids from game/assets/story/sfx
// (Game.StoryEscape); where the design names an id the folder has not, the nearest there is (EscapeSfx).
public partial class StorySounds
{
    /// <summary>
    /// A looping sound of the escape's on <paramref name="on"/> (it follows the node), faded in to <paramref name="db"/>; null
    /// when the sound is missing. <see cref="StopLoop"/> fades it out and frees it.
    /// </summary>
    public AudioStreamPlayer3D? LoopOn(Node3D on, string id, float db = 0, float pitch = 1)
    {
        if (StoryAudio.SfxPath(id) is not { } path || StoryAudio.Stream(path) is not { } stream)
        {
            audio.Trace($"sfx {id} missing");
            return null;
        }
        var player = new AudioStreamPlayer3D
        {
            Name = $"Loop-{id}",
            Stream = stream,
            Bus = GameAudio.EffectsBus,
            UnitSize = 10,
            MaxDistance = 140,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            PanningStrength = 1,
            VolumeDb = Silent,
            PitchScale = pitch,
        };
        on.AddChild(player);
        player.Play((float)(_random.Randf() * Math.Max(0, stream.GetLength() - 1)));
        player.CreateTween().TweenProperty(player, "volume_db", db, 0.3f);
        audio.Trace($"loop {id} on {on.Name}");
        return player;
    }

    /// <summary>Fades a loop of <see cref="LoopOn"/> out and frees it.</summary>
    public static void StopLoop(AudioStreamPlayer3D? player, float seconds = 0.5f)
    {
        if (player is null || !IsInstanceValid(player) || !player.IsInsideTree())
            return;
        var tween = player.CreateTween();
        tween.TweenProperty(player, "volume_db", Silent, seconds);
        tween.TweenCallback(Callable.From(player.QueueFree));
    }

    /// <summary>
    /// The sound id the escape plays for one the design names (design-escape §7): the design's own where the folder has it,
    /// else the nearest that is there (breach_tear, shutter_klaxon and gauge_sparks were never made).
    /// </summary>
    public static string EscapeSfx(string id) => id switch
    {
        "breach_tear" => "metal_groan_big",
        "shutter_klaxon" => "lockdown_klaxon",
        "gauge_sparks" => "sparks",
        _ => id,
    };
}
