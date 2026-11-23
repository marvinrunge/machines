using Godot;

namespace Machines.Game;

// Package S's warm-up (plan stufe7 §3): what section 2 shows first is drawn once behind the loading screen where the camera
// looks: a searchlight as the Thresher's (shadowed, thick in the haze), so its shadow and fog pipelines are compiled before his
// beam comes through window D, and his two aiming lasers' unshaded glow (ActorView.Stealth). His sounds are among the story's
// effects StoryAudio.Files has preloaded already.
public partial class Warmup
{
    /// <summary>This package's things to warm up, a step each (Warmup.Steps runs them in a story).</summary>
    private System.Collections.Generic.IEnumerable<bool> WarmStealth()
    {
        if (_world.Story is not { } run || run.Section > 2)
            yield break;
        var beam = new SpotLight3D
        {
            Name = "WarmSearchlight",
            SpotRange = 46,
            SpotAngle = 25,
            SpotAngleAttenuation = 0.8f,
            SpotAttenuation = 0.3f,
            ShadowEnabled = true,
            LightColor = new Color(0.85f, 0.92f, 1f),
            LightVolumetricFogEnergy = 0.8f,
            LightEnergy = 30,
            Position = _focus + Vector3.Up * 9,
            RotationDegrees = new Vector3(-30, 0, 0),
        };
        AddChild(beam);
        var laser = new MeshInstance3D
        {
            Name = "WarmLaser",
            Mesh = new BoxMesh { Size = new Vector3(0.035f, 0.035f, 4) },
            MaterialOverride = ActorView.LaserMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = _focus + Vector3.Up * 3,
        };
        AddChild(laser);
        // Two frames drawn with them, then they go (their pipelines stay compiled).
        yield return true;
        yield return true;
        beam.QueueFree();
        laser.QueueFree();
        yield return false;
    }
}
