using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// The Locator's boot in its clamp at the prologue's start (Prologue.BootSteps): what each of the director's "boot <step>"
// cues sounds and looks like in the view (StoryHud.Boot writes the lines and draws the optics). Nothing here changes the
// world: the lamp's darkness and its test are the view's alone (the sim has the Locator's lamp on throughout).
public partial class Game
{
    // The lamp's test (by the engine's clock): when it began, null while it is off. When the next drop of coolant falls.
    private double? _bootLampFrom;
    private double _nextDrip;

    /// <summary>Skips the boot (Esc held, StoryHud): a Use command, so every peer and every replay skips it in the same tick.</summary>
    public void SkipStoryBoot()
    {
        if (World.Story is { } story && Me == story.Locator && story.Flags.Contains(StoryMarks.Boot))
            Issue(new StoryUseCommand(Player, StoryMarks.SkipBoot));
    }

    /// <summary>
    /// The Locator's lamp in the view while it boots: dark until the step "lamp", then it stutters three times and stays on.
    /// Null for any other machine and once the boot is over (the sim's rule, GameWorld.StoryLampOn, again).
    /// </summary>
    private bool? BootLamp(int id)
    {
        if (World.Story is not { } story || !story.Flags.Contains(StoryMarks.Boot) || World.HeroOf(story.Locator)?.Id != id)
            return null;
        if (_bootLampFrom is not { } from)
            return false;
        var t = Time.GetTicksMsec() / 1000.0 - from;
        return t is >= 0 and < 0.1 or >= 0.25 and < 0.32 or >= 0.5 and < 0.62 or >= 0.85;
    }

    /// <summary>A step of the boot (the director's "boot &lt;step&gt;"): its sounds, the camera's jolt, the lamp's test.</summary>
    private void BootCue(string step)
    {
        var hero = World.Story is { } story && World.HeroOf(story.Locator) is { } locator ? locator : null;
        var at = hero is null ? Vector3.Zero : ActorView.World(hero.Position) + Vector3.Up * 1.5f;
        var facing = hero is null ? 0 : hero.Facing;
        var ahead = new Vector3(MathF.Cos(facing), 0, MathF.Sin(facing));
        var side = new Vector3(-ahead.Z, 0, ahead.X);
        switch (step)
        {
            case "dark":
                // A moment of true silence, then the reactor's hum comes through the clamp, muffled until the optics.
                StoryAudio.Hush(1.5f);
                StoryAudio.SetMuffled("boot", true);
                _bootLampFrom = null;
                _nextDrip = World.Time + 4;
                break;
            case "relay":
                StoryAudio.Play("hack_click", at);
                break;
            case "start":
                StoryAudio.Play("hud_glitch");
                break;
            case "core":
                StoryAudio.Play("hud_glitch");
                StoryAudio.Play("hack_click", at);
                break;
            case "memory":
                StoryAudio.Play("tracker_tick");
                break;
            case "optics":
                StoryAudio.Play("hud_glitch");
                GetTree().CreateTimer(Prologue.OpticsClear).Timeout += () => StoryAudio.SetMuffled("boot", false);
                break;
            case "legs":
                // The legs pull once; the clamp creaks and holds.
                StoryAudio.Play("loc_servo", at);
                GetTree().CreateTimer(0.45).Timeout += () => StoryAudio.Play("loc_servo", at);
                GetTree().CreateTimer(0.7).Timeout += () => StoryAudio.Play("door_jammed", at);
                CameraRig.Shake(at, 1.2f, 0.6f);
                break;
            case "drive":
                StoryAudio.Play("loc_fan", at);
                break;
            case "screen":
                // The cracked wall screen across the bay flickers on: the core's decryption, a cold glow ahead in the dark.
                StoryAudio.Play("sparks", at + ahead * 6);
                BootScreen();
                break;
            case "edge":
                TearBootScreen(0.4);
                StoryAudio.Play("data_squeal");
                _screen.Interference(GameOptions.SoftScares ? 0.15f : 0.3f, 0.3f);
                break;
            case "update":
                TearBootScreen(0.4);
                StoryAudio.Play("hack_fail");
                StoryAudio.Play("hud_glitch");
                _screen.Interference(GameOptions.SoftScares ? 0.25f : 0.5f, 0.4f);
                break;
            case "radio":
                // Two faint signals in the bays either side; far down in the decks a single clang.
                StoryAudio.Play("tracker_tick", at + side * 8);
                GetTree().CreateTimer(0.4).Timeout += () => StoryAudio.Play("tracker_tick", at - side * 8);
                StoryAudio.Play("metal_groan_big", at - ahead * 60);
                break;
            case "lamp":
                StoryAudio.Play("searchlight_servo", at);
                StoryAudio.Play("sparks", at);
                _bootLampFrom = Time.GetTicksMsec() / 1000.0;
                break;
            case "lamp_off":
                // The test passed: the lamp's relay clicks home and it stays on.
                StoryAudio.Play("hack_click", at);
                break;
            case "done":
                // The boot over (or skipped: the thought speaking breaks off, and the call comes after).
                StoryAudio.SetMuffled("boot", false);
                _bootLampFrom = null;
                if (World.Story?.Flags.Contains(StoryMarks.BootSkip) == true)
                    StoryAudio.CutVoice();
                break;
        }
    }

    private ShaderMaterial? _bootScreenLook;

    private const string BootScreenShader = """
        shader_type spatial;
        render_mode unshaded, cull_disabled;
        uniform float since;
        uniform float tear = 0.0;
        float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
        void fragment() {
            float t = TIME - since;
            // It flickers on, then holds with a faint unsteadiness.
            float on = t < 0.15 ? 0.8 : t < 0.4 ? 0.05 : t < 0.5 ? 1.0 : t < 0.8 ? 0.2 : min(1.0, 0.5 + t * 0.5);
            vec2 uv = UV;
            // Rows of glyphs scrolling up: blocks of on and off cells, a crack across the glass.
            float row = floor((uv.y + t * 0.02) * 9.0);
            float col = floor(uv.x * 26.0);
            float glyph = step(0.42, hash(vec2(col, row + floor(t * 3.0) * step(0.8, hash(vec2(row, 7.0))))));
            float line = step(hash(vec2(row, 3.0)) * 0.9 + 0.1, uv.x) < 0.5 ? glyph : 0.0;
            float rowMask = step(0.35, fract((uv.y + t * 0.02) * 9.0)) * step(0.2, fract(uv.x * 26.0)) * step(0.08, uv.x) * step(uv.x, 0.92);
            float text = line * rowMask * step(0.3, uv.y);
            // The decryption's bar along the foot, creeping on.
            float bar = step(0.1, uv.y) * step(uv.y, 0.18) * step(0.08, uv.x) * step(uv.x, 0.08 + 0.84 * clamp(t / 60.0, 0.0, 0.37));
            float frame = step(0.1, uv.y) * step(uv.y, 0.18) * (step(uv.x, 0.085) + step(0.915, uv.x)) * step(0.075, uv.x) * step(uv.x, 0.925);
            float crack = smoothstep(0.012, 0.0, abs(uv.y - (0.72 - uv.x * 0.45 + sin(uv.x * 31.0) * 0.02)));
            float scan = 0.82 + 0.18 * sin(uv.y * 400.0 + TIME * 9.0);
            float noise = hash(uv * 300.0 + TIME) * 0.04;
            float level = (0.03 + text * 0.4 + bar * 0.75 + frame * 0.4 + noise) * scan * on * (1.0 - crack * 0.85);
            level *= 1.0 - tear * step(0.5, hash(vec2(floor(uv.y * 30.0), floor(TIME * 20.0))));
            ALBEDO = vec3(0.08, 0.75, 0.3) * level;
        }
        """;

    /// <summary>
    /// The cracked wall screen across the Locator's bay comes on (the boot's step "screen"): a glowing face over the screen's
    /// prop with the core's decryption running on it, and a little cold light round it, the first thing the optics see.
    /// </summary>
    private void BootScreen()
    {
        if (_deckView is null || World.Deck?.Markers.FirstOrDefault(m => m.Tags.Contains("screen")) is not { } marker)
            return;
        var facing = new Vector3(Mathf.Cos(Mathf.DegToRad(marker.Angle)), 0, Mathf.Sin(Mathf.DegToRad(marker.Angle)));
        var foot = ActorView.World(marker.Position);
        _bootScreenLook = new ShaderMaterial { Shader = new Shader { Code = BootScreenShader } };
        _bootScreenLook.SetShaderParameter("since", Time.GetTicksMsec() / 1000f);
        var face = new MeshInstance3D
        {
            Name = "BootScreen", Mesh = new QuadMesh { Size = new Vector2(BootScreenSize.X, BootScreenSize.Y) }, MaterialOverride = _bootScreenLook,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _deckView.AddChild(face);
        face.LookAtFromPosition(foot + Vector3.Up * BootScreenHigh + facing * BootScreenOut, foot + Vector3.Up * BootScreenHigh - facing, Vector3.Up);
        _deckView.AddChild(new OmniLight3D
        {
            Name = "BootScreenGlow", LightColor = new Color(0.4f, 1f, 0.65f), LightEnergy = 0.9f, OmniRange = 12, OmniAttenuation = 1.2f,
            Position = foot + Vector3.Up * BootScreenHigh + facing * 2.5f, ShadowEnabled = false,
        });
        TearBootScreen(0.4);
    }

    /// <summary>The boot screen's picture tears for a moment (the core's edge, the update failing).</summary>
    private void TearBootScreen(double seconds)
    {
        _bootScreenLook?.SetShaderParameter("tear", 1f);
        GetTree().CreateTimer(seconds).Timeout += () => _bootScreenLook?.SetShaderParameter("tear", 0f);
    }

    /// <summary>The screen's glowing face, metres, and how high its middle hangs.</summary>
    private static readonly Vector2 BootScreenSize = new(2.6f, 1.6f);

    /// <summary>How high the face's middle hangs, and how far before the wall (in front of the prop's dark glass).</summary>
    private const float BootScreenHigh = 3.25f, BootScreenOut = 0.95f;

    /// <summary>While the Locator boots: now and then a drop of coolant falls on the grating beside it.</summary>
    private void BootDrips()
    {
        if (World.Story is not { } story || !story.Flags.Contains(StoryMarks.Boot) || World.Time < _nextDrip
            || World.HeroOf(story.Locator) is not { } hero)
            return;
        _nextDrip = World.Time + 3 + GD.Randf() * 3;
        var side = GD.Randf() < 0.5f ? -1 : 1;
        var across = new Vector3(-MathF.Sin(hero.Facing), 0, MathF.Cos(hero.Facing));
        StoryAudio.Play("cooling_tick", ActorView.World(hero.Position) + across * side * (2 + GD.Randf() * 2));
    }
}
