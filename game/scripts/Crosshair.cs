using Godot;

namespace Machines.Game;

/// <summary>
/// The first-person crosshair of the original (MachGuiFirstPerson): it opens up when a machine is taken over
/// (<c>ping</c>), then turns slowly (<c>targ</c>); over an enemy it shows the attack sight (<c>targv</c>) and
/// over a friend the no-fire sight (<c>targnv</c>), each with its sound as it comes on. What the bitmaps of
/// <c>gui/fstpersn/cursor</c> show is drawn as shapes in the screen's own pixels, each of the original's pixels as many
/// wide as the screen's height has whole times 480, so it stays sharp at any size and needs no game copy. In a Wired
/// match brackets stand round it over an enemy a geo locator has located. A sighted driver of Wired (the Modern layout) sees
/// two marks: this sight where the gun points (<see cref="At"/>), with its colour, sounds and spread marks, and a dot in the
/// middle where the view looks (<see cref="Dot"/>), with the bars and brackets of what it rests on. Left unset, those draw
/// and sound as the original's single sight.
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

    /// <summary>
    /// What lies under the crosshair; an enemy or a friend coming into it plays its sound, for a sighted driver only while
    /// the gun is on the sight (<see cref="GunOn"/>).
    /// </summary>
    public Sight Aim
    {
        get => _sight;
        set
        {
            if (value != _sight && _gunOn)
                Sound(value);
            _sight = value;
        }
    }

    /// <summary>
    /// Whether the driven machine's gun points where the view looks; true unless a sighted driver's gun still turns. Coming
    /// on with an enemy or a friend in the sight plays that one's sound, so the sound says the gun is on it.
    /// </summary>
    public bool GunOn
    {
        get => _gunOn;
        set
        {
            if (value && !_gunOn)
                Sound(_sight);
            _gunOn = value;
        }
    }

    private bool _gunOn = true;

    private void Sound(Sight sight)
    {
        if (sight is Sight.Enemy or Sight.Friend)
            _audio?.PlayGui(sight == Sight.Enemy ? "gui/sounds/attackon.wav" : "gui/sounds/friendon.wav");
    }

    /// <summary>What the gun sight rests on and whether the gun is on: the gun goes off before the sight changes and on after it, so one sound at most plays.</summary>
    public void Show(Sight aim, bool gunOn)
    {
        if (!gunOn)
            GunOn = false;
        Aim = aim;
        GunOn = gunOn;
    }

    /// <summary>The original's single sight in the middle again, without a sound.</summary>
    public void Single()
    {
        (At, Clamped, Dot, SightHidden, _gunOn) = (null, false, false, false, true);
    }

    /// <summary>Where on the screen the sight stands, its bitmap and spread marks: null for the middle.</summary>
    public Vector2? At { get; set; }

    /// <summary>Whether the sight stands clamped to the edge of the screen, the gun pointing off it: the sight alone, at half strength.</summary>
    public bool Clamped { get; set; }

    /// <summary>Whether a dot marks the middle, where the view looks; the bars and brackets stand round it.</summary>
    public bool Dot { get; set; }

    /// <summary>Whether the sight is left out and the dot alone drawn (a constructor's towers are up: the triggers are theirs).</summary>
    public bool SightHidden { get; set; }

    /// <summary>The aimed-at actor's health and armour, each 0 to 1, or null when nothing is aimed at.</summary>
    public (float Health, float Armour)? Target { get; set; }

    /// <summary>In a Wired match, whether the enemy aimed at has been located by a geo locator's pulse: brackets stand round the sight.</summary>
    public bool Located { get; set; }

    /// <summary>The driven machine's heading in degrees, for the compass under the sight.</summary>
    public float Heading { get; set; }

    /// <summary>In a Wired run, how far rounds can stray from the sight, in degrees: four marks stand that far out.</summary>
    public float? Spread { get; set; }

    /// <summary>
    /// Whether the sight and the compass stand on a dark edge: the goggles of a run lay green over the view, on which the green of the sight
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
        var scale = HudScale.Of(size);
        var (name, frame) = Current();
        var centre = (size / 2).Floor();
        var sight = At is { } placed ? placed.Floor() : centre;
        var showSight = !(Dot && SightHidden);
        if (Spread is not null && showSight && !Clamped)
            DrawSpread(sight, size.Y);
        _s = scale;

        // Each part twice under the goggles: first its dark edge, then itself.
        foreach (var edge in Outlined ? new[] { true, false } : [false])
        {
            _edge = edge;
            // MachGuiFirstPerson::displayHealthArmour: two upright bars beside the sight, filled from the bottom.
            _strength = 1;
            if (Target is { } target)
            {
                Bar(HealthShades, target.Health, (centre + new Vector2(-33, -9) * scale).Round());
                Bar(ArmourShades, target.Armour, (centre + new Vector2(29, -9) * scale).Round());
            }
            // MachGuiFirstPerson::displayCompass: under the sight, once it has opened.
            if (name != "ping")
                DrawCompass((centre + new Vector2(-24, 26) * scale).Round());
            if (showSight)
            {
                _strength = Clamped ? 0.5f : 1;
                DrawSight(sight, name, frame);
            }
        }
        _edge = false;
        _strength = 1;
        if (Target is not null && Located)
            DrawLocated(centre, scale);

        // The dot where the view looks, over everything: white with a dark ring, six pixels across at 1080 lines.
        if (Dot)
        {
            var radius = 1.5f * scale;
            DrawCircle(centre, radius + 1, new Color(0, 0, 0, 0.9f));
            DrawCircle(centre, radius, new Color(1, 1, 1, 0.9f));
        }
    }

    // ---- the sight, drawn as shapes in the screen's own pixels ------------------------------------------------------
    // Measures are the original's pixels (its 68x74 bitmaps, laid with their pixel 34,37 on the sight's point), from that
    // point: a pixel's middle lies at .5, so 24.5 is the middle of the ring of pixels 24 out; each is _s screen pixels.

    // Screen pixels to the original's one; whether the dark edge under the goggles is being drawn; how strongly the sight shows.
    private float _s = 1;
    private bool _edge;
    private float _strength = 1;

    private static Color Rgb(int r, int g, int b) => Color.Color8((byte)r, (byte)g, (byte)b);

    // The bitmaps' colours: the turning sight's green and shade, the compass marks' green, the glint's pale tail, the compass.
    private static readonly Color Green = Rgb(90, 255, 0), GreenShade = Rgb(33, 99, 0), CompassGreen = Rgb(16, 148, 0),
        Pale = Rgb(214, 255, 198), Bright = Rgb(0, 206, 0), Lit = Rgb(41, 255, 16), Faint = Rgb(0, 156, 0);

    /// <summary>The outer arcs of a sight: their outer line, their inner line from the far end to the end with the hook, and the hook.</summary>
    private readonly record struct Ring(Color Outer, Color InnerFar, Color InnerHook, Color Hook);

    private static readonly Ring Turning = new(Green, GreenShade, GreenShade, Green);
    private static readonly Ring Attack = new(Rgb(255, 132, 0), Rgb(156, 0, 0), Rgb(255, 0, 0), Rgb(255, 49, 49));
    private static readonly Ring NoFire = new(Green, Rgb(239, 231, 0), Rgb(255, 173, 0), Rgb(255, 173, 0));

    // The target's bars (health.bmp, armour.bmp), their colour from top to bottom.
    private static readonly Color[] HealthShades = [Rgb(16, 255, 66), Rgb(0, 247, 49), Rgb(0, 206, 41), Rgb(0, 165, 33), Rgb(0, 140, 24)];
    private static readonly Color[] ArmourShades = [Rgb(16, 24, 255), Rgb(16, 16, 247), Rgb(0, 8, 214), Rgb(0, 0, 165), Rgb(0, 0, 140)];

    private Color Ink(Color colour) =>
        _edge ? new Color(0, 0, 0, 0.85f * _strength * colour.A) : new Color(colour, colour.A * _strength);

    private float Width(float width) => _edge ? width + 2 * _s : width;

    /// <summary>Whole pixels of the original from one corner to the other, in measures from the point; one of the original's pixels wider all round as an edge.</summary>
    private void Block(Vector2 point, float left, float top, float right, float bottom, Color colour)
    {
        var box = new Rect2(point + new Vector2(left, top) * _s, new Vector2(right - left, bottom - top) * _s);
        DrawRect(_edge ? box.Grow(_s) : box, Ink(colour));
    }

    /// <summary>
    /// An arc a pixel wide, its colour running from one end to the other, as far as it lies within <paramref name="clip"/>
    /// above and below the point (the opening sight shows only what lies between its two lines).
    /// </summary>
    private void Arc(Vector2 point, float radius, float from, float to, Color start, Color end, float clip = float.MaxValue)
    {
        const int Steps = 36;
        var (line, colours) = (new List<Vector2>(), new List<Color>());
        void Draw()
        {
            if (line.Count > 1)
                DrawPolylineColors([.. line], [.. colours], Width(_s), true);
            (line, colours) = (new List<Vector2>(), new List<Color>());
        }
        for (var i = 0; i <= Steps; i++)
        {
            var t = i / (float)Steps;
            var offset = Vector2.FromAngle(Mathf.Lerp(from, to, t)) * radius;
            if (Mathf.Abs(offset.Y) > clip)
            {
                Draw();
                continue;
            }
            line.Add(point + offset * _s);
            colours.Add(Ink(start.Lerp(end, t)));
        }
        Draw();
    }

    /// <summary>A closed outline a pixel wide through pixel middles.</summary>
    private void Outline(Vector2 point, Color colour, params Vector2[] corners)
    {
        var line = corners.Select(c => point + c * _s).ToList();
        line.Add(line[0]);
        DrawPolyline([.. line], Ink(colour), Width(_s), true);
    }

    /// <summary>
    /// The sight as the original's bitmap of that name and frame shows it: ping opens two lines from the middle and
    /// narrows them, uncovering the arcs; targ sends a glint round them; targv (attack) and targnv (no fire) add their
    /// inner marks, which blink white in their second frame, and the frames of the target's bars.
    /// </summary>
    private void DrawSight(Vector2 point, string name, int frame)
    {
        var ring = name switch { "targv" => Attack, "targnv" => NoFire, _ => Turning };
        // ping: how far the lines stand from the middle and how far they reach either way, frame by frame.
        float[] apart = [0.5f, 3.5f, 8.5f, 15.5f, 24.5f, 24.5f, 24.5f], reach = [25, 25, 25, 25, 25, 17.5f, 6.5f];
        var clip = name == "ping" ? apart[frame] - 0.5f : float.MaxValue;

        // The two arcs, upper left and lower right, each thickening inwards towards its end on the upright, where a hook turns in.
        foreach (var (from, to, up) in new[] { (Mathf.Pi, Mathf.Pi * 1.5f, true), (0f, Mathf.Pi / 2, false) })
        {
            Arc(point, 24.5f, from, to, ring.Outer, ring.Outer, clip);
            Arc(point, 23.5f, from, to, ring.InnerFar, ring.InnerHook, clip);
            Arc(point, 22.5f, to - 0.25f, to, new Color(ring.Hook, 0), ring.Hook, clip);
            if (clip >= 24)
            {
                if (up)
                    Block(point, -1, -25, 0, -22, ring.Hook);
                else
                    Block(point, 0, 22, 1, 25, ring.Hook);
            }
        }

        if (name is "ping" or "targ")
        {
            // Eight dots round the middle.
            foreach (var (x, y) in new[] { (1.5f, 5.5f), (5.5f, 1.5f) })
                foreach (var (sx, sy) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
                    if (y < clip)
                        Block(point, x * sx - 0.5f, y * sy - 0.5f, x * sx + 0.5f, y * sy + 0.5f, Green);
        }
        if (name == "ping")
        {
            var (gap, half) = (apart[frame], reach[frame]);
            Block(point, -half, -gap - 0.5f, half, -gap + 0.5f, Green);
            Block(point, -half, gap - 0.5f, half, gap + 0.5f, Green);
            return;
        }

        // The marks either side of the compass below.
        foreach (var x in new[] { -25, 24 })
            Block(point, x, 25, x + 1, 36, CompassGreen);

        if (name == "targ")
        {
            // A glint going round clockwise, an eighth of the way each frame: a white head, its tail pale, then green.
            var angle = -Mathf.Pi / 2 + frame * Mathf.Pi / 4;
            Arc(point, 25.5f, angle - 0.22f, angle, Green, Pale);
            if (!_edge)
                DrawCircle(point + Vector2.FromAngle(angle) * 25.5f * _s, 0.65f * _s, Ink(Colors.White), true, -1, true);
            return;
        }

        var blink = frame == 1;
        if (name == "targv")
        {
            // Two inner arcs across the outer ones, each with a short hook, a triangle in each of the arcs' quarters, and two corners.
            var inner = Rgb(156, 0, 0);
            Arc(point, 16.5f, -Mathf.Pi / 2, 0, inner, inner);
            Arc(point, 16.5f, Mathf.Pi / 2, Mathf.Pi, inner, inner);
            Block(point, 0, -17, 1, -15, inner);
            Block(point, -1, 15, 0, 17, inner);
            var mark = blink ? Colors.White : Rgb(231, 0, 0);
            Outline(point, mark, new(-9.5f, -14.5f), new(-9.5f, -10.5f), new(-13.5f, -10.5f));
            Outline(point, mark, new(9.5f, 14.5f), new(9.5f, 10.5f), new(13.5f, 10.5f));
            Corner(point, 4.5f, -4.5f, -1, 1, 2, Rgb(231, 0, 0));
            Corner(point, -4.5f, 4.5f, 1, -1, 2, Rgb(231, 0, 0));
        }
        else
        {
            var mark = blink ? Colors.White : Rgb(255, 206, 0);
            Outline(point, mark, new(11.5f, -17.5f), new(11.5f, -11.5f), new(17.5f, -11.5f));
            Outline(point, mark, new(-11.5f, 17.5f), new(-11.5f, 11.5f), new(-17.5f, 11.5f));
            Corner(point, -5.5f, -5.5f, 1, 1, 3, mark);
            Corner(point, 5.5f, 5.5f, -1, -1, 3, mark);
        }
        // The frames the target's bars stand in.
        var frameColour = name == "targv" ? Rgb(0, 255, 8) : Green;
        foreach (var left in new[] { -34, 28 })
        {
            Block(point, left, -10, left + 6, -9, frameColour);
            Block(point, left, 21, left + 6, 22, frameColour);
            Block(point, left, -9, left + 1, 21, frameColour);
            Block(point, left + 5, -9, left + 6, 21, frameColour);
        }
    }

    /// <summary>A corner of pixels at a pixel's middle, its two arms reaching that many pixels further, along x and along y.</summary>
    private void Corner(Vector2 point, float x, float y, int alongX, int alongY, int arm, Color colour)
    {
        Block(point, Mathf.Min(x, x + alongX * arm) - 0.5f, y - 0.5f, Mathf.Max(x, x + alongX * arm) + 0.5f, y + 0.5f, colour);
        Block(point, x - 0.5f, Mathf.Min(y, y + alongY * arm) - 0.5f, x + 0.5f, Mathf.Max(y, y + alongY * arm) + 0.5f, colour);
    }

    /// <summary>
    /// The compass strip under the sight (compass.bmp), 48 pixels wide round the heading, 160 going round once: a dot every
    /// two, a mark every ten and at every 45 degrees an arrow with the bearing under it, from north 38 pixels in. Marks
    /// fade out at the strip's ends, and a bearing as it reaches past them.
    /// </summary>
    private void DrawCompass(Vector2 topLeft)
    {
        const float Wide = 48, Turn = 160;
        if (Outlined && _edge)
            DrawRect(new Rect2(topLeft, new Vector2(Wide, 9) * _s).Grow(_s), new Color(0, 0, 0, 0.6f));
        // The strip's left end, in pixels east of north.
        var start = Mathf.PosMod(Heading, 360) / 360 * Turn - Wide / 2;
        var size = Mathf.RoundToInt(7 * _s);
        for (var mark = Mathf.CeilToInt(start / 2) * 2; mark < start + Wide + 2; mark += 2)
        {
            var x = mark - start;
            var fade = Mathf.Clamp(Mathf.Min(x, Wide - x) / 3, 0, 1);
            if (fade <= 0)
                continue;
            var at = topLeft + new Vector2(x, 0) * _s;
            var east = (int)Mathf.PosMod(mark, Turn);
            Color Faded(Color c) => new(c, c.A * fade);
            if (east % 20 == 0)
            {
                var head = new[] { at + new Vector2(-2.5f, 0) * _s, at + new Vector2(2.5f, 0) * _s, at + new Vector2(0, 3) * _s };
                var bearing = (east * 360 / (int)Turn).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var width = MenuTheme.Font.GetStringSize(bearing, HorizontalAlignment.Left, -1, size).X;
                var baseline = at + new Vector2(-width / 2, 9 * _s);
                // A bearing shows whole, fading out as it reaches past an end of the strip.
                var whole = Mathf.Clamp((Mathf.Min(x, Wide - x) - width / 2 / _s) / 3 + 1, 0, 1);
                Color Label(Color c) => new(c, c.A * whole);
                if (_edge)
                {
                    DrawPolyline([.. head, head[0]], Ink(Faded(Colors.Black)), 2 * _s, true);
                    DrawStringOutline(MenuTheme.Font, baseline, bearing, HorizontalAlignment.Left, -1, size, Mathf.RoundToInt(2 * _s) + 1, Ink(Label(Colors.Black)));
                }
                else
                {
                    DrawColoredPolygon(head, Ink(Faded(Bright)));
                    DrawLine(at, at + new Vector2(0, 3) * _s, Ink(Faded(Lit)), _s, true);
                    DrawString(MenuTheme.Font, baseline, bearing, HorizontalAlignment.Left, -1, size, Ink(Label(Bright)));
                }
            }
            else
            {
                var high = east % 10 == 0 ? 2 : 1;
                Block(at, -0.5f, 0, 0.5f, high, Faded(high == 2 ? Bright : Faint));
            }
        }
    }

    /// <summary>An upright bar the sight's own size (4 by 31), filled from the bottom, its colour running down as the bitmap's.</summary>
    private void Bar(Color[] shades, float share, Vector2 topLeft)
    {
        const float Wide = 4, High = 31;
        share = Mathf.Clamp(share, 0, 1);
        if (share <= 0)
            return;
        var top = Mathf.Round(High * (1 - share) * _s);
        Color Shade(float y)
        {
            var at = y / (High * _s) * (shades.Length - 1);
            var i = Math.Min((int)at, shades.Length - 2);
            return shades[i].Lerp(shades[i + 1], at - i);
        }
        var box = new Rect2(topLeft + new Vector2(0, top), new Vector2(Wide * _s, High * _s - top));
        if (_edge)
        {
            DrawRect(box.Grow(_s), Ink(Colors.Black));
            return;
        }
        DrawPolygon([box.Position, new Vector2(box.End.X, box.Position.Y), box.End, new Vector2(box.Position.X, box.End.Y)],
            [Ink(Shade(top)), Ink(Shade(top)), Ink(Shade(High * _s)), Ink(Shade(High * _s))]);
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

    /// <summary>Amber brackets at the four corners round the sight and its bars, as the HUD sets round a located enemy in the field.</summary>
    private void DrawLocated(Vector2 centre, float scale)
    {
        var colour = new Color(1, 0.7f, 0.2f, 0.75f + 0.25f * Mathf.Sin((float)_time * 8));
        var (half, arm) = (new Vector2(44, 26) * scale, 9f * scale);
        foreach (var (x, y) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            var corner = centre + new Vector2(x * half.X, y * half.Y);
            Vector2[] strokes = [corner - new Vector2(0, y * arm), corner, corner - new Vector2(x * arm, 0)];
            if (Outlined)
                DrawPolyline(strokes, new Color(0, 0, 0, 0.7f), scale + 2, true);
            DrawPolyline(strokes, colour, scale, true);
        }
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

    private static int Loop(string name, double time) => (int)(time / Animations[name].Seconds) % Animations[name].Frames;
}
