using Godot;

namespace Machines.Game;

/// <summary>
/// What lies over the driver's view (MachGuiFirstPerson, under the crosshair and gauges): black bars at the top and
/// bottom, which open from the middle of the screen as the machine is entered; static, which flickers on entry, comes
/// with one hit in twenty and swallows the view as the machine dies (RenDevice::addInterference); and the colour of the
/// night vision goggles (EnvPlanetEnvironment::nvgOn), which a hit may knock on or off until the static ends.
/// </summary>
public partial class FirstPersonScreen : CanvasLayer
{
    // STARTUP_SEQUENCE_TIME; the bars' share of the height once open; how long the view lasts after the machine.
    private const float Startup = 0.5f, Border = 0.05f, Dying = 0.5f;

    // The most bands of static RenDevice::addInterference lays on a frame.
    private const int MostBands = 3;

    private static readonly Lazy<Shader> ViewShader = new(() => new Shader
    {
        Code = $$"""
            shader_type canvas_item;
            uniform sampler2D screen : hint_screen_texture, filter_linear;
            uniform sampler2D noise : repeat_enable, filter_nearest;
            uniform vec3 filter_colour = vec3(1.0);
            uniform float border = 0.05;
            uniform vec4 bands[{{MostBands}}];
            uniform int count = 0;
            uniform vec4 noise_place = vec4(0.0, 0.0, 1.0, 1.0);
            void fragment() {
                vec3 colour = texture(screen, SCREEN_UV).rgb * filter_colour;
                vec3 grain = texture(noise, UV * noise_place.zw + noise_place.xy).rgb;
                for (int i = 0; i < count; i++) {
                    vec4 band = bands[i];
                    if (UV.y >= band.x && UV.y < band.y) {
                        float across = 1.0 - abs((UV.y - band.x) / (band.y - band.x) * 2.0 - 1.0);
                        colour = mix(colour, grain, mix(band.z, band.w, across));
                    }
                }
                COLOR = vec4(UV.y < border || UV.y > 1.0 - border ? vec3(0.0) : colour, 1.0);
            }
            """,
    });

    private PlanetLook _look = null!;
    private ShaderMaterial _material = null!;
    private bool _noise;

    private float _age, _level, _left;
    private bool _starting, _flickering, _goggles;
    private float? _dying;
    private Action? _gone;

    /// <summary>The machine is lost and the view is about to go: the controls no longer do anything.</summary>
    public bool Lost => _dying is not null;

    public static FirstPersonScreen Create(PlanetLook look, Texture2D? noise)
    {
        var screen = new FirstPersonScreen { Name = "FirstPersonScreen", Layer = 0, Visible = false, _look = look, _noise = noise is not null };
        screen._material = new ShaderMaterial { Shader = ViewShader.Value };
        if (noise is not null)
            screen._material.SetShaderParameter("noise", noise);
        var view = new ColorRect { Material = screen._material, MouseFilter = Control.MouseFilterEnum.Ignore };
        view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        screen.AddChild(view);
        screen.SetProcess(false);
        return screen;
    }

    /// <summary>The machine is entered: the bars open and the static flickers for half a second, goggles off.</summary>
    public void Enter()
    {
        (_age, _level, _left, _dying, _gone) = (0, 0, 0, null, null);
        (_starting, _flickering, _goggles) = (true, false, false);
        _look.NightVision = false;
        Visible = true;
        SetProcess(true);
        Draw();
    }

    public void Leave()
    {
        _look.NightVision = false;
        Visible = false;
        SetProcess(false);
    }

    /// <summary>The N key on a machine that has goggles.</summary>
    public void ToggleGoggles() => _look.NightVision = _goggles = !_look.NightVision;

    /// <summary>
    /// The machine is hit: one hit in twenty brings static for 0.1 to 0.5 s, and one of those in five, on a machine with
    /// goggles, switches them until the static ends. True when it did, which the driver hears.
    /// </summary>
    public bool Hit(bool hasGoggles)
    {
        if (!Visible || _starting || _flickering || _dying is not null || Random.Shared.Next(20) != 0)
            return false;
        _flickering = true;
        _left = Between(0.1f, 0.5f);
        _level = Random.Shared.Next(4, 8) / 10f;
        if (hasGoggles && Random.Shared.Next(5) == 0)
        {
            if (!_look.NightVision)
                _level = 0;
            _look.NightVision = !_look.NightVision;
        }
        return true;
    }

    /// <summary>The machine is gone or lost to the enemy: static rises over the view for half a second, then <paramref name="gone"/>.</summary>
    public void Die(Action gone)
    {
        if (_dying is not null)
            return;
        if (!Visible)
        {
            gone();
            return;
        }
        _dying = 0;
        _gone = gone;
    }

    public override void _Process(double delta)
    {
        var step = (float)delta;
        _age += step;
        if (_dying is { } dying)
        {
            _dying = dying + step;
            _level = Mathf.Min(dying + step + 0.1f, 1);
            if (_dying > Dying)
            {
                _gone?.Invoke();
                return;
            }
        }
        else if (_starting)
            Flicker(step);
        else if (_flickering && (_left -= step) <= 0)
        {
            _flickering = false;
            _level = 0;
            _look.NightVision = _goggles;
        }
        Draw();
    }

    // On entry static comes and goes, weaker each time, for as long as the bars take to open.
    private void Flicker(float step)
    {
        if (_age >= Startup)
        {
            _starting = _flickering = false;
            _level = 0;
            return;
        }
        if (_flickering && (_left -= step) > 0)
            return;
        _flickering = true;
        if (Random.Shared.Next(2) == 0)
        {
            _level = 0;
            _left = Between(0.05f, 0.15f);
        }
        else
        {
            _level = Mathf.Clamp((10 - (_age * 20 + Random.Shared.Next(-2, 3))) / 10, 0.1f, 1);
            _left = Between(0.05f, 0.4f);
        }
    }

    private void Draw()
    {
        var height = Mathf.Max(GetViewport().GetVisibleRect().Size.Y, 1);
        var border = _age >= Startup ? Border : Mathf.Max(Border, 0.5f - 10 / height - (0.5f - Border) * _age / Startup);
        var bands = new Vector4[MostBands];
        var count = _noise && _level > 0.001f ? Interference(bands) : 0;
        _material.SetShaderParameter("border", border);
        _material.SetShaderParameter("bands", bands);
        _material.SetShaderParameter("count", count);
        // The original gave each corner of the noise a random place in the picture: every frame it is another.
        _material.SetShaderParameter("noise_place", new Vector4(Between(0, 1), Between(0, 1), Between(100, 1000), Between(100, 1000)));
        var filter = _look.NightVision ? _look.NightVisionFilter : Colors.White;
        _material.SetShaderParameter("filter_colour", new Vector3(filter.R, filter.G, filter.B));
    }

    /// <summary>
    /// The bands of static on this frame, each from its top to its bottom (shares of the height) and from its alpha at
    /// the edges to its alpha in the middle (RenDevice::addInterference): light static leaves clear gaps between up to
    /// three bands, stronger static covers all, and from 0.8 on it is even. As in the original the bands are as high as
    /// their bottom edge is far down, as if that were their height.
    /// </summary>
    private int Interference(Vector4[] bands)
    {
        var level = _level;
        if (level < 0.2f && Random.Shared.Next(3) == 0)
            return 0;
        if (level >= 0.8f)
        {
            bands[0] = new Vector4(0, 1, level, level);
            return 1;
        }
        var count = 0;
        var (cover, least) = (Mathf.Min(1, level + 0.4f), Mathf.Max(0, level - 0.6f));
        var clear = 1 - cover;
        if (clear > 0)
        {
            var (covered, cleared, last) = (0f, 0f, -0.15f);
            for (var i = 0; i < MostBands && last < 1 && cover > covered; i++)
            {
                var (more, gap) = (Between(0, cover - covered), Between(0, Mathf.Max(0, clear - cleared)));
                covered += more;
                cleared += gap;
                var top = last + gap;
                last = Mathf.Min(1, top + more);
                bands[count++] = new Vector4(top, top + last, least, level);
            }
            return count;
        }
        var alpha = Mathf.Remap(level, 0.6f, 0.8f, least, level);
        var from = 0f;
        for (var i = 0; i < 2 && from < 1; i++)
        {
            var bottom = Mathf.Min(1, from + Between(0.2f, 0.4f));
            bands[count++] = new Vector4(from, from + bottom, alpha, level);
            from = bottom;
        }
        if (from < 1)
            bands[count++] = new Vector4(from, from + 1, alpha, level);
        return count;
    }

    private static float Between(float from, float to) => from + Random.Shared.NextSingle() * (to - from);
}
