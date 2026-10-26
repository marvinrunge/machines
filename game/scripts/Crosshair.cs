using Godot;

namespace Machines.Game;

/// <summary>
/// The first-person crosshair of the original (MachGuiFirstPerson): it opens up when a machine is taken over
/// (<c>ping</c>), then turns slowly (<c>targ</c>); over an enemy it shows the attack sight (<c>targv</c>) and
/// over a friend the no-fire sight (<c>targnv</c>), each with its sound as it comes on. The bitmaps come from
/// <c>gui/fstpersn/cursor</c> of the player's game copy, scaled up with the screen; without them a plain cross.
/// </summary>
public partial class Crosshair : Control
{
    public enum Sight { None, Enemy, Friend }

    // Frames and seconds per frame, from the .anm files; the start animation holds on its last frame.
    private static readonly Dictionary<string, (int Frames, float Seconds)> Animations = new()
    {
        ["ping"] = (7, 0.1f), ["targ"] = (8, 0.1f), ["targv"] = (2, 0.5f), ["targnv"] = (2, 0.5f),
    };

    private readonly Dictionary<(string, int, int), Texture2D?> _frames = [];
    private GameAudio? _audio;
    private double _time, _started;
    private Sight _sight;

    public static Crosshair Create(GameAudio? audio) => new()
    {
        Name = "Crosshair",
        _audio = audio,
        MouseFilter = MouseFilterEnum.Ignore,
        Visible = false,
    };

    /// <summary>Starts the opening animation, as when the original embodies a machine.</summary>
    public void Start()
    {
        _started = _time;
        _sight = Sight.None;
    }

    /// <summary>What lies under the crosshair; an enemy or a friend coming into it plays its sound.</summary>
    public Sight Aim
    {
        set
        {
            if (value != _sight && value != Sight.None)
                _audio?.PlayGui(value == Sight.Enemy ? "gui/sounds/attackon.wav" : "gui/sounds/friendon.wav");
            _sight = value;
        }
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (Visible)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        var scale = Math.Max(1, (int)Math.Round(size.Y / 480f));
        var (name, frame) = Current();
        if (Frame(name, frame, scale) is { } texture)
            DrawTexture(texture, (size - texture.GetSize()) / 2);
        else
        {
            var centre = size / 2;
            var colour = _sight == Sight.Enemy ? new Color(1, 0.4f, 0.2f) : new Color(0.4f, 1, 0.3f);
            DrawLine(centre - new Vector2(12, 0), centre + new Vector2(12, 0), colour, 2);
            DrawLine(centre - new Vector2(0, 12), centre + new Vector2(0, 12), colour, 2);
        }
    }

    private (string Name, int Frame) Current()
    {
        if (_sight == Sight.Enemy)
            return ("targv", Loop("targv", _time));
        if (_sight == Sight.Friend)
            return ("targnv", Loop("targnv", _time));
        var (frames, seconds) = Animations["ping"];
        var opening = (int)((_time - _started) / seconds);
        return opening < frames ? ("ping", opening) : ("targ", Loop("targ", _time));
    }

    private static int Loop(string name, double time) => (int)(time / Animations[name].Seconds) % Animations[name].Frames;

    private Texture2D? Frame(string name, int frame, int scale)
    {
        if (_frames.TryGetValue((name, frame, scale), out var cached))
            return cached;
        Texture2D? texture = null;
        if (ClassicGui.Bitmap($"fstpersn/cursor/{name}{frame + 1}.bmp")?.GetImage() is { } image)
        {
            var (width, height) = (image.GetWidth() * scale, image.GetHeight() * scale);
            for (var grown = 1; grown < scale; grown *= 2)
                image = ClassicCursor.Scale2x(image);
            if (image.GetWidth() != width)
                image.Resize(width, height, Image.Interpolation.Lanczos);
            texture = ImageTexture.CreateFromImage(image);
        }
        _frames[(name, frame, scale)] = texture;
        return texture;
    }
}
