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
    /// <summary>What lies under the sight: an enemy the weapons can reach or cannot tilt to (Miss), or a friend.</summary>
    public enum Sight { None, Enemy, Friend, Miss }

    // Frames and seconds per frame, from the .anm files; the start animation holds on its last frame.
    private static readonly Dictionary<string, (int Frames, float Seconds)> Animations = new()
    {
        ["ping"] = (7, 0.1f), ["targ"] = (8, 0.1f), ["targv"] = (2, 0.5f), ["targnv"] = (2, 0.5f),
    };

    // The eight ways an outline lies round a bitmap.
    private static readonly Vector2[] Round = [new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1)];

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
        get => _sight;
        set
        {
            if (value != _sight && value is Sight.Enemy or Sight.Friend)
                _audio?.PlayGui(value == Sight.Enemy ? "gui/sounds/attackon.wav" : "gui/sounds/friendon.wav");
            _sight = value;
        }
    }

    /// <summary>The aimed-at actor's health and armour, each 0 to 1, or null when nothing is aimed at.</summary>
    public (float Health, float Armour)? Target { get; set; }

    /// <summary>The driven machine's heading in degrees, for the compass under the sight.</summary>
    public float Heading { get; set; }

    /// <summary>In a Wired run, how far rounds can stray from the sight, in degrees: four marks stand that far out.</summary>
    public float? Spread { get; set; }

    /// <summary>
    /// Whether the bitmaps stand on a dark edge: the goggles of a run lay green over the view, on which the green of the sight
    /// and the compass are lost. In any other match they are shown as ever.
    /// </summary>
    public bool Outlined { get; set; }

    // What the marks show, following the spread; the driver's camera sees this many degrees from top to bottom.
    private float _spreadShown;
    private const float FieldOfView = 70;

    public override void _Process(double delta)
    {
        _time += delta;
        if (Spread is { } spread)
            _spreadShown = Mathf.Lerp(_spreadShown, spread, 1 - Mathf.Exp(-(float)delta * 12));
        if (Visible)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        var scale = Math.Max(1, (int)Math.Round(size.Y / 480f));
        var (name, frame) = Current();
        var centre = (size / 2).Floor();
        if (Spread is not null)
            DrawSpread(centre, size.Y);

        // MachGuiFirstPerson::displayHealthArmour: two upright bars beside the sight, filled from the bottom.
        if (Target is { } target)
        {
            Bar("health", target.Health, centre + new Vector2(-33, -9) * scale, scale);
            Bar("armour", target.Armour, centre + new Vector2(29, -9) * scale, scale);
        }

        // MachGuiFirstPerson::displayCompass: under the sight, once it has opened, a strip of the compass
        // bitmap 48 pixels wide around the heading (160 pixels go round once, north 38 pixels in).
        if (name != "ping" && Bitmap("compass", scale) is { } compass)
        {
            var start = 38 + Mathf.PosMod(Heading, 360) / 360 * 160 - 24;
            var strip = new Rect2(centre + new Vector2(-24, 26) * scale, new Vector2(48, compass.GetHeight() / (float)scale) * scale);
            var from = new Rect2(new Vector2(start, 0) * scale, new Vector2(48 * scale, compass.GetHeight()));
            if (Outlined)
                DrawRect(strip.Grow(scale), new Color(0, 0, 0, 0.6f));
            DrawTextureRectRegion(compass, strip, from);
        }

        if (Frame(name, frame, scale) is { } texture)
        {
            var at = (size - texture.GetSize()) / 2;
            if (Outlined)
            {
                foreach (var way in Round)
                    DrawTexture(texture, at + way * scale, new Color(0, 0, 0, 0.85f));
            }
            DrawTexture(texture, at);
        }
        else
        {
            var colour = _sight == Sight.Enemy ? new Color(1, 0.4f, 0.2f) : new Color(0.4f, 1, 0.3f);
            DrawLine(centre - new Vector2(12, 0), centre + new Vector2(12, 0), colour, 2);
            DrawLine(centre - new Vector2(0, 12), centre + new Vector2(0, 12), colour, 2);
        }
    }

    /// <summary>The marks at the edge of the cone a round flies in, on the screen as the driver's camera shows it.</summary>
    private void DrawSpread(Vector2 centre, float height)
    {
        var radius = Mathf.Tan(Mathf.DegToRad(_spreadShown)) / Mathf.Tan(Mathf.DegToRad(FieldOfView / 2)) * height / 2;
        var (length, line) = (height * 0.012f, Mathf.Max(height / 540, 1));
        var colour = _sight == Sight.Enemy ? new Color(1, 0.5f, 0.3f, 0.9f) : new Color(0.55f, 1, 0.45f, 0.75f);
        foreach (var way in new[] { Vector2.Up, Vector2.Right, Vector2.Down, Vector2.Left })
            DrawLine(centre + way * radius, centre + way * (radius + length), colour, line);
    }

    private (string Name, int Frame) Current()
    {
        if (_sight == Sight.Enemy)
            return ("targv", Loop("targv", _time));
        if (_sight is Sight.Friend or Sight.Miss)
            return ("targnv", Loop("targnv", _time));
        var (frames, seconds) = Animations["ping"];
        var opening = (int)((_time - _started) / seconds);
        return opening < frames ? ("ping", opening) : ("targ", Loop("targ", _time));
    }

    private void Bar(string name, float share, Vector2 topLeft, int scale)
    {
        if (Bitmap(name, scale) is not { } bar)
            return;
        var height = bar.GetHeight();
        var shown = Mathf.Clamp(share, 0, 1) * height;
        DrawTextureRectRegion(bar, new Rect2(topLeft + new Vector2(0, height - shown), new Vector2(bar.GetWidth(), shown)),
            new Rect2(0, height - shown, bar.GetWidth(), shown));
    }

    private static int Loop(string name, double time) => (int)(time / Animations[name].Seconds) % Animations[name].Frames;

    private Texture2D? Frame(string name, int frame, int scale) => Load($"{name}{frame + 1}", frame, scale);

    private Texture2D? Bitmap(string name, int scale) => Load(name, -1, scale);

    private Texture2D? Load(string file, int frame, int scale)
    {
        if (_frames.TryGetValue((file, frame, scale), out var cached))
            return cached;
        Texture2D? texture = null;
        if (ClassicGui.Bitmap($"fstpersn/cursor/{file}.bmp")?.GetImage() is { } image)
        {
            var (width, height) = (image.GetWidth() * scale, image.GetHeight() * scale);
            for (var grown = 1; grown < scale; grown *= 2)
                image = ClassicCursor.Scale2x(image);
            if (image.GetWidth() != width)
                image.Resize(width, height, Image.Interpolation.Lanczos);
            texture = ImageTexture.CreateFromImage(image);
        }
        _frames[(file, frame, scale)] = texture;
        return texture;
    }
}
