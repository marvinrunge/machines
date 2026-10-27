using Godot;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// The first-person radar of the original (MachGuiRadar), in the lower right corner: it switches on in ten frames
/// (<c>radar0</c> to <c>radar9</c>, half a second), then shows what lies within the driven machine's scanner range
/// as blips turned to its heading (machines, buildings, pods and missile emplacements in their race's colour, and the
/// ore anyone has found, and wreckage), the dome and arrow in its middle, the machine's picture, and its own health and armour
/// as two upright bars filled from the bottom, amber and then red as they run low; at a fifth of its health a warning
/// sounds. The bitmaps come from <c>gui/fstpersn/radar</c> of the player's game copy, scaled up with the screen.
/// </summary>
public partial class Radar : Control
{
    private const int StartupFrames = 10;
    private const double StartupSeconds = 0.5;

    // Where the original draws on the radar, in its pixels from the backdrop's corner.
    private static readonly Vector2 ScannerCentre = new(102, 103), DomeAt = new(100, 101), ArrowAt = new(85, 85), IconAt = new(180, 30);
    private static readonly Vector2 HealthAt = new(216, 111), ArmourAt = new(234, 111);
    private const float ScannerRadius = 55;

    private Game _game = null!;
    private GameAudio? _audio;
    private double _time, _started;
    private bool _healthy;

    public static Radar Create(Game game, GameAudio? audio) => new()
    {
        Name = "Radar",
        _game = game,
        _audio = audio,
        MouseFilter = MouseFilterEnum.Ignore,
        TextureFilter = TextureFilterEnum.Nearest,
        Visible = false,
    };

    /// <summary>The machine being driven, whose surroundings the radar shows.</summary>
    public Machine? Driven { get; set; }

    /// <summary>
    /// How far the driven machine's upper body is turned from its hull, radians anticlockwise: the radar turns with
    /// the view, and its arrow points the way the hull goes (MachGuiRadar::displayMotionDirection).
    /// </summary>
    public float Head { get; set; }

    /// <summary>Switches the radar on from its first frame, as when the original embodies a machine.</summary>
    public void Start()
    {
        _started = _time;
        _healthy = true;
        _audio?.PlayGui("gui/sounds/radaron.wav");
    }

    public override void _Process(double delta)
    {
        _time += delta;
        if (Visible)
            QueueRedraw();
    }

    public override void _Draw()
    {
        if (Driven is not { } machine || ClassicGui.Bitmap("fstpersn/radar/rmmap.bmp") is not { } backdrop)
            return;
        var size = GetViewportRect().Size;
        var scale = Math.Max(1, (int)Math.Round(size.Y / 480f));
        var corner = size - backdrop.GetSize() * scale;

        // The start-up frames, right-aligned, until the radar is on.
        var frame = (int)((_time - _started) / StartupSeconds * StartupFrames);
        if (frame < StartupFrames)
        {
            if (ClassicGui.Bitmap($"fstpersn/radar/radar{frame}.bmp") is { } starting)
                DrawTextureRect(starting, new Rect2(new Vector2(size.X - starting.GetWidth() * scale, corner.Y), starting.GetSize() * scale), false);
            return;
        }

        DrawTextureRect(backdrop, new Rect2(corner, backdrop.GetSize() * scale), false);
        // Sixteen arrows round the compass, the first straight ahead.
        var arrow = (int)(Mathf.PosMod(-Mathf.RadToDeg(Head), 360) / 360 * 16) % 16;
        Blit($"fstpersn/radar/arrow/point{arrow + 1}.bmp", corner + ArrowAt * scale, scale);

        // Blips: each actor's place relative to the machine, turned so that ahead is up (the original turns by its
        // azimuth and a quarter more), within the scanner range drawn to the scanner's radius.
        var world = _game.World;
        var range = Math.Max(machine.Stats.ScannerRange, 1);
        var reach = ScannerRadius / range;
        var turn = machine.Facing + Head + MathF.PI / 2;
        var (cos, sin) = (MathF.Cos(turn), MathF.Sin(turn));
        Vector2? OnRadar(NVector2 position)
        {
            var d = position - machine.Position;
            if (d.LengthSquared() >= range * range)
                return null;
            return corner + (ScannerCentre + new Vector2(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos) * reach) * scale;
        }
        foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Count > 0 && s.Ore > 0))
            if (OnRadar(site.Position) is { } at)
                Blit("fstpersn/radar/blips/ore.bmp", at, scale);
        foreach (var wreck in world.Debris)
            if (OnRadar(wreck.Position) is { } at)
                Blit("fstpersn/radar/blips/debris.bmp", at, scale);
        foreach (var actor in world.Actors)
        {
            if (actor.Id == machine.Id || actor is Machine { Inside: not null } || OnRadar(actor.Position) is not { } at)
                continue;
            var letter = ClassicPanel.RaceFolder(actor is Machine { Camouflaged: true } ? Game.Player : actor.Race)[0];
            var kind = actor switch
            {
                Machine => "machpix",
                Construction c when c.IsType("POD") => "podpix",
                Construction c when c.IsType("MISSILE_EMPLACEMENT") => "misspix",
                _ => "conspix",
            };
            Blit($"fstpersn/radar/blips/{letter}{kind}.bmp", at, scale);
        }
        Blit("fstpersn/radar/dome.bmp", corner + DomeAt * scale, scale);

        // The machine's own picture, and its health and armour.
        var key = machine.Key;
        Blit($"fstpersn/machines/{ClassicPanel.MachinePrefix(key.Type, key.SubType)}{key.HardwareLevel}{ClassicPanel.WeaponSuffix(key.WeaponCombo)}.bmp",
            corner + IconAt * scale, scale);
        var health = machine.HitPoints / (float)Math.Max(1, machine.MaxHitPoints);
        var armour = machine.Armour / (float)Math.Max(1, machine.MaxArmour);
        Bar("rhealth", health, corner + HealthAt * scale, scale);
        Bar("rarmour", armour, corner + ArmourAt * scale, scale);
        if (_healthy && health <= 0.2f)
            _audio?.PlayGui("gui/sounds/hpcritic.wav");
        _healthy = health > 0.2f;
    }

    private void Blit(string path, Vector2 at, int scale)
    {
        if (ClassicGui.Bitmap(path) is { } bitmap)
            DrawTextureRect(bitmap, new Rect2(at, bitmap.GetSize() * scale), false);
    }

    /// <summary>An upright bar filled from the bottom; its second and third pictures (amber, red) below 1.7 and 1 third.</summary>
    private void Bar(string name, float share, Vector2 topLeft, int scale)
    {
        share = Mathf.Clamp(share, 0, 1);
        var picture = share <= 1 / 3f ? $"{name}3" : share <= 1.7f / 3 ? $"{name}2" : name;
        if (ClassicGui.Bitmap($"fstpersn/radar/{picture}.bmp") is not { } bar)
            return;
        var height = bar.GetHeight();
        var shown = share * height;
        DrawTextureRectRegion(bar, new Rect2(topLeft + new Vector2(0, height - shown) * scale, new Vector2(bar.GetWidth(), shown) * scale),
            new Rect2(0, height - shown, bar.GetWidth(), shown));
    }
}
