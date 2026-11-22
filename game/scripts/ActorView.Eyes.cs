using Godot;

namespace Machines.Game;

/// <summary>How a machine's eyes look (<see cref="ActorView.EyesOf"/>).</summary>
public enum EyeLook
{
    /// <summary>As the model has them: glowing white.</summary>
    Lit,

    /// <summary>Switched off: black, unlit, and its lamp dark.</summary>
    Off,

    /// <summary>Corrupted, the trio's enemy: a red glow that glitches now and then, and a red lamp.</summary>
    Hostile,
}

// A machine's eyes on the story's deck: dark while it is switched off, coming on with a stutter when it wakes, and a corrupted red
// on the trio's enemies, with a red lamp to match. The eyes are the models' own glowing parts (the white circles on black of
// mach_b.bmp) and the glow sprites around them (flar2_bt.bmp); each view gives them materials of its own to dim and tint. The
// view's alone, and only where the story asks (EyesOf is null in every other match, where nothing here runs).
public partial class ActorView
{
    /// <summary>
    /// On the story's deck, how a machine's eyes look, by its actor's id (Game.StoryEyes): from the world's state, so every
    /// client shows the same. Null in every other match.
    /// </summary>
    public static Func<int, EyeLook>? EyesOf { get; set; }

    // The model the eyes were looked up for, its eye parts with their own colours, and its glow sprites with theirs.
    private Node? _eyesOf;
    private readonly List<(StandardMaterial3D Material, Color Albedo, Color Emission)> _eyeParts = [];
    private readonly List<(ShaderMaterial Material, Vector3 Colour)> _eyeGlows = [];

    // The model's other lights (a rifle's charge panels, lit strips), given materials of their own the first time it shows red.
    private List<(StandardMaterial3D Material, Color Emission)>? _otherLights;
    private List<(ShaderMaterial Material, Vector3 Colour)>? _otherGlows;

    // Whether the eyes are on, since when (the view's clock), whether they glow red, and how bright they were last shown.
    private bool _eyesOn;
    private double _eyesSince = double.MinValue;
    private bool _eyesRed;
    private float _eyesShown = -1;
    private CorruptGlow? _corrupt;

    /// <summary>The eyes of a corrupted machine: glowing red, a little orange at the core so they burn rather than blush.</summary>
    private static readonly Color HostileEye = new(1.6f, 0.12f, 0.05f);

    /// <summary>The glow round them.</summary>
    private static readonly Vector3 HostileGlow = new(1.25f, 0.1f, 0.04f);

    /// <summary>What a dead eye's texture is darkened to: black glass, a faint shine left.</summary>
    private const float DeadAlbedo = 0.05f;

    /// <summary>A corrupted machine's lamp: red, and brighter than a white one so the cone still reads in the dark.</summary>
    private static readonly Color HostileLamp = new(1f, 0.13f, 0.07f);

    private const float HostileLampBoost = 1.4f, SearchlightBoost = 1.15f;

    /// <summary>The eyes now: whether they are lit and how bright (0 to about 1.3), with the lamp's share to go with them.</summary>
    private (EyeLook Look, float Level) ShowEyes()
    {
        if (EyesOf is null || _model is null || IsBuilding)
            return (EyeLook.Lit, 1);
        var look = EyesOf(ActorId);
        var now = Time.GetTicksMsec() / 1000.0;
        if (_eyesOf != _model)
        {
            // A new model (the first, or a refit): its eyes as they stand, without a stutter.
            FindEyes();
            _eyesOn = look != EyeLook.Off;
            _eyesSince = double.MinValue;
            _eyesShown = -1;
            (_otherLights, _otherGlows) = (null, null);
        }
        var on = look != EyeLook.Off;
        if (on != _eyesOn)
            (_eyesOn, _eyesSince) = (on, now);
        if (on)
            _eyesRed = look == EyeLook.Hostile;
        var level = on ? Waking(now - _eyesSince) : Dying(now - _eyesSince);
        if (_eyesRed && level > 0)
            level *= (_corrupt ??= new CorruptGlow(ActorId)).At(now);
        if (!Mathf.IsEqualApprox(level, _eyesShown))
        {
            _eyesShown = level;
            foreach (var (material, albedo, emission) in _eyeParts)
            {
                var lit = Mathf.Clamp(level, 0, 1);
                material.Emission = (_eyesRed ? HostileEye : emission) * level;
                // Dark, the white circles are black glass; red, the glass itself is tinted.
                var glass = _eyesRed ? new Color(0.7f, 0.08f, 0.05f) : albedo;
                material.AlbedoColor = (glass * (DeadAlbedo + (1 - DeadAlbedo) * lit)) with { A = albedo.A };
            }
            foreach (var (material, colour) in _eyeGlows)
                material.SetShaderParameter("colour", (_eyesRed ? HostileGlow : colour) * level);
            if (_eyesRed && _otherLights is null)
                FindOtherLights();
            // Its other lights burn the same corrupted red, at their own brightness, so nothing green or white glows beside the eyes.
            foreach (var (material, emission) in _otherLights ?? [])
                material.Emission = _eyesRed ? HostileEye * (Brightness(emission) * 0.45f * Mathf.Clamp(level, 0, 1)) : emission;
            foreach (var (material, colour) in _otherGlows ?? [])
                material.SetShaderParameter("colour", _eyesRed ? HostileGlow * (Mathf.Max(colour.X, Mathf.Max(colour.Y, colour.Z)) * 0.6f * Mathf.Clamp(level, 0, 1)) : colour);
        }
        return (on ? look : EyeLook.Off, level);
    }

    /// <summary>
    /// The lamp of a corrupted machine shines red, brighter and thicker in the haze than a white one so its cone still reads in
    /// the dark; it dips a little when its eyes glitch dark. Any other keeps its white (a searchlight its cold white).
    /// </summary>
    private void HostileCone(SpotLight3D cone, bool hostile, float eyeLevel)
    {
        if (!hostile)
        {
            cone.LightColor = _searchlight ? SearchlightColour : HeadlampColour;
            cone.LightVolumetricFogEnergy = _searchlight ? SearchlightFog : HeadlampFog;
            return;
        }
        cone.LightColor = HostileLamp;
        // A searchlight's haze stays as thick as it was: red, it filled the whole view of whoever stood in it.
        cone.LightVolumetricFogEnergy = _searchlight ? SearchlightFog : HeadlampFog * 1.6f;
        cone.LightEnergy *= (_searchlight ? SearchlightBoost : HostileLampBoost) * Mathf.Lerp(0.7f, 1f, Mathf.Clamp(eyeLevel, 0, 1));
    }

    /// <summary>The eyes go dark at once (the machine is destroyed): the parts that fly off show no glow.</summary>
    private void EyesOut()
    {
        if (EyesOf is null || _eyesOf is null)
            return;
        foreach (var (material, albedo, _) in _eyeParts)
            (material.Emission, material.AlbedoColor) = (Colors.Black, (albedo * DeadAlbedo) with { A = albedo.A });
        foreach (var (material, _) in _eyeGlows)
            material.SetShaderParameter("colour", Vector3.Zero);
    }

    /// <summary>
    /// Gives the model's eyes materials of their own: the parts glowing white (the eyes' circles) and the white glow sprites,
    /// each surface overridden on this view's meshes alone. Marked, so the parts of a machine blown apart keep them (Debris).
    /// </summary>
    private void FindEyes()
    {
        _eyesOf = _model;
        _eyeParts.Clear();
        _eyeGlows.Clear();
        if (_model is null)
            return;
        var meshes = _model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>()
            .Append(_model as MeshInstance3D).OfType<MeshInstance3D>();
        foreach (var instance in meshes)
        {
            if (instance.Mesh is not { } mesh || instance.MaterialOverride is not null)
                continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var shared = instance.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s);
                if (shared is StandardMaterial3D { EmissionEnabled: true } part && IsWhite(part.Emission)
                    && part.GetMeta("texture", "").AsString() == EyeTexture)
                {
                    var own = (StandardMaterial3D)part.Duplicate();
                    own.SetMeta(EyeMark, true);
                    instance.SetSurfaceOverrideMaterial(s, own);
                    _eyeParts.Add((own, part.AlbedoColor, part.Emission));
                }
                else if (shared is ShaderMaterial glow && ClassicAssets.IsGlowSprite(glow)
                    && glow.GetShaderParameter("colour").AsVector3() is var colour && colour.X > 0.9f && colour.Y > 0.9f && colour.Z > 0.9f)
                {
                    var own = (ShaderMaterial)glow.Duplicate();
                    own.SetMeta(EyeMark, true);
                    instance.SetSurfaceOverrideMaterial(s, own);
                    _eyeGlows.Add((own, colour));
                }
            }
        }
    }

    /// <summary>
    /// Gives the model's other glowing parts materials of their own (its eyes aside): lit panels and strips, and glow sprites
    /// that are not the eyes' white, for a corrupted machine to show them red.
    /// </summary>
    private void FindOtherLights()
    {
        (_otherLights, _otherGlows) = ([], []);
        if (_model is null)
            return;
        var meshes = _model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>()
            .Append(_model as MeshInstance3D).OfType<MeshInstance3D>();
        foreach (var instance in meshes)
        {
            if (instance.Mesh is not { } mesh || instance.MaterialOverride is not null)
                continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                var shared = instance.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s);
                if (shared?.HasMeta(EyeMark) == true)
                    continue;
                if (shared is StandardMaterial3D { EmissionEnabled: true } part && Brightness(part.Emission) > 0.05f)
                {
                    var own = (StandardMaterial3D)part.Duplicate();
                    own.SetMeta(EyeMark, true);
                    instance.SetSurfaceOverrideMaterial(s, own);
                    _otherLights.Add((own, part.Emission));
                }
                else if (shared is ShaderMaterial glow && ClassicAssets.IsGlowSprite(glow) && glow.GetShaderParameter("colour").AsVector3() is var colour
                    && colour.LengthSquared() > 0.01f)
                {
                    var own = (ShaderMaterial)glow.Duplicate();
                    own.SetMeta(EyeMark, true);
                    instance.SetSurfaceOverrideMaterial(s, own);
                    _otherGlows.Add((own, colour));
                }
            }
        }
    }

    private static float Brightness(Color colour) => Mathf.Max(colour.R, Mathf.Max(colour.G, colour.B));

    /// <summary>The texture of the machines' eyes: black with white circles, glowing white.</summary>
    private const string EyeTexture = "mach_b.bmp";

    /// <summary>The meta an eye's own material carries, for Debris to keep it.</summary>
    public const string EyeMark = "eye";

    private static bool IsWhite(Color colour) => colour.R > 0.9f && colour.G > 0.9f && colour.B > 0.9f;

    /// <summary>Eyes coming on (woken, the optics starting): a stutter of short flashes, then they rise to full over a second and a half.</summary>
    private static float Waking(double t) => t switch
    {
        < 0.08 => 0.7f,
        < 0.3 => 0.04f,
        < 0.38 => 0.9f,
        < 0.62 => 0.08f,
        < 0.7 => 1f,
        < 0.8 => 0.25f,
        < 0.95 => 0.8f,
        < 1.05 => 0.15f,
        < 1.8 => 0.45f + 0.55f * (float)((t - 1.05) / 0.75),
        _ => 1f,
    };

    /// <summary>Eyes going out (switched off, downed): a last blink, then they fade to black in half a second.</summary>
    private static float Dying(double t) => t switch
    {
        < 0.1 => 1f,
        < 0.18 => 0.15f,
        < 0.26 => 0.7f,
        < 0.76 => 0.7f * (1 - (float)((t - 0.26) / 0.5)),
        _ => 0f,
    };
}

/// <summary>
/// The glow of a corrupted machine's eyes (the story's enemies): a steady red with a faint unsteady swell, broken every one to
/// four seconds, at irregular times, by a glitch: a few frames of stepping, too bright and too dark in turn, or a quick double
/// pulse. Each machine has its own rhythm (seeded by its actor's id). The view's alone.
/// </summary>
public sealed class CorruptGlow
{
    private readonly Random _random;
    private readonly double _phase;
    private double _next, _start, _length;
    private bool _pulse;
    private int _seed;

    public CorruptGlow(int seed)
    {
        _random = new Random(unchecked(seed * 104729 + 31));
        _phase = _random.NextDouble() * 10;
        _next = 0.5 + _random.NextDouble() * 3;
    }

    /// <summary>How bright the eyes glow at a time of the view's clock (seconds), as a share of their glow: about 0.1 to 1.4.</summary>
    public float At(double now)
    {
        if (now >= _next)
        {
            _start = now;
            _pulse = _random.NextDouble() < 0.35;
            _length = _pulse ? 0.32 : 0.08 + _random.NextDouble() * 0.16;
            _seed = _random.Next();
            _next = now + _length + 1 + _random.NextDouble() * 3;
        }
        var steady = 0.88f + 0.07f * (float)Math.Sin(now * 1.7 + _phase) + 0.03f * (float)Math.Sin(now * 11.3 + 2 * _phase);
        var into = now - _start;
        if (into < 0 || into >= _length)
            return steady;
        if (_pulse)
            // Two quick throbs, brighter than it should.
            return steady * (1 + 0.45f * (float)Math.Abs(Math.Sin(into / _length * 2 * Math.PI)));
        // Steps of about 30 ms, each a level picked from the glitch's own seed: dark, bright, dark ...
        var step = (int)(into / 0.03);
        var roll = unchecked((uint)_seed ^ (uint)step * 0x9E3779B9u) % 4;
        return roll switch { 0 => 0.12f, 1 => 1.35f, 2 => 0.35f, _ => 1.15f };
    }
}
