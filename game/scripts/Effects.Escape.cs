using Godot;

namespace Machines.Game;

// Package E's effects (plan stufe7 §4 E, design-escape §13 "View"): the gallery's jets (a band of the rendered fire across
// the gallery, the hiss's steam and its glow before it), the sparks a container throws as it scrapes along its rail, and the
// glow of the shaft under the footbridge. Built from the effects the game has (DamageFire, Plume, the spark showers); no new
// pictures.
public partial class Effects
{
    private static readonly Color JetSteam = new(0.86f, 0.88f, 0.9f, 0.5f), JetGlow = new(1, 0.48f, 0.14f), Scrape = new(1, 0.7f, 0.3f);

    /// <summary>A jet's phase as the view shows it (StoryHazards.JetPhase): off, warning (flicker, hiss, steam), burning.</summary>
    public enum JetLook
    {
        Off,
        Warn,
        Fire,
    }

    /// <summary>
    /// A jet's band of fire across the gallery (design-escape §3 C4), <paramref name="length"/> metres along x and
    /// <paramref name="depth"/> deep, its middle at the node's place on the floor; its nozzle's steam at <paramref name="nozzle"/>
    /// (in the band's frame). Dark until <see cref="ShowJet"/> lights it.
    /// </summary>
    public static Node3D JetBand(float length, float depth, Vector3 nozzle)
    {
        var band = new Node3D { Name = "JetBand" };
        var count = Math.Max(2, (int)MathF.Ceiling(length / 2.5f));
        for (var i = 0; i < count; i++)
        {
            var fire = DamageFire(Mathf.Min(depth, 3.2f), 6);
            fire.Name = $"Fire{i}";
            fire.Position = new Vector3(-length / 2 + (i + 0.5f) * length / count, 0.2f, 0);
            band.AddChild(fire);
        }
        // The hiss before it fires: steam out of the nozzle, rising and spreading.
        var steam = Plume(4, 1.4f, 3, 1.1f, JetSteam);
        steam.Name = "Steam";
        steam.Position = nozzle;
        band.AddChild(steam);
        band.AddChild(new OmniLight3D
        {
            Name = "Glow",
            LightColor = JetGlow,
            LightEnergy = 0,
            OmniRange = length + 8,
            Position = Vector3.Up * 3,
            Visible = false,
            DistanceFadeEnabled = true,
            DistanceFadeBegin = 60,
            DistanceFadeLength = 15,
            LightVolumetricFogEnergy = 0.6f,
        });
        return band;
    }

    /// <summary>
    /// A jet's band shown in a phase at <paramref name="time"/> seconds: off dark, warning with its steam and a red glow that
    /// flickers faster as it nears, burning with its flames and a bright, restless glow.
    /// </summary>
    public static void ShowJet(Node3D band, JetLook look, double time, float warned)
    {
        foreach (var child in band.GetChildren())
        {
            switch (child)
            {
                case GpuParticles3D particles:
                    var emitting = particles.Name.ToString().StartsWith("Fire", StringComparison.Ordinal) ? look == JetLook.Fire : look == JetLook.Warn;
                    if (particles.Emitting != emitting)
                        particles.Emitting = emitting;
                    break;
                case OmniLight3D glow:
                    var t = (float)time;
                    glow.Visible = look != JetLook.Off;
                    glow.LightColor = look == JetLook.Fire ? JetGlow : new Color(1, 0.18f, 0.06f);
                    // The warning's glow pulses faster the nearer the fire: the beat a player counts (design-escape §3 C4).
                    glow.LightEnergy = look switch
                    {
                        JetLook.Fire => 5 + 1.2f * Mathf.Sin(t * 23) + 0.6f * Mathf.Sin(t * 41 + 1),
                        JetLook.Warn => 0.6f + 1.4f * Mathf.Max(0, Mathf.Sin(t * (8 + 16 * warned))),
                        _ => 0,
                    };
                    break;
            }
        }
    }

    /// <summary>Sparks a container (or the rack) throws where its bottom scrapes along its rail, sprayed back from its way.</summary>
    public void ScrapeSparks(Vector3 at, Vector3 way, int amount = 10)
    {
        if (!GraphicsSettings.Effects)
            return;
        Sparks(at, Scrape, amount, (-way + Vector3.Up * 0.6f).Normalized(), spread: 50, speed: 6, lifetime: 0.9f, size: 0.35f);
    }

    /// <summary>The glow of the shaft under the footbridge: a deep orange light and a glowing floor far down, seen through the grating.</summary>
    public static Node3D ShaftGlow(Vector2 size, float depth)
    {
        var shaft = new Node3D { Name = "ShaftGlow" };
        shaft.AddChild(new MeshInstance3D
        {
            Name = "Embers",
            Mesh = new PlaneMesh { Size = size },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.2f, 0.05f, 0.02f),
                EmissionEnabled = true,
                Emission = new Color(1, 0.35f, 0.08f),
                EmissionEnergyMultiplier = 2.2f,
            },
            Position = Vector3.Down * depth,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        shaft.AddChild(new OmniLight3D
        {
            Name = "Glow",
            LightColor = new Color(1, 0.42f, 0.12f),
            LightEnergy = 4,
            OmniRange = depth + 14,
            OmniAttenuation = 0.6f,
            Position = Vector3.Down * (depth * 0.6f),
            ShadowEnabled = false,
            LightVolumetricFogEnergy = 1.2f,
        });
        // A haze rising out of the shaft, lit from below.
        var haze = Plume(6, 6, depth * 0.9f, 6, new Color(0.35f, 0.12f, 0.05f, 0.35f));
        haze.Name = "Haze";
        haze.Position = Vector3.Down * depth;
        haze.Emitting = true;
        shaft.AddChild(haze);
        return shaft;
    }
}
