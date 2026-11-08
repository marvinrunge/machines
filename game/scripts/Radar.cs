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

    // The original's machine pictures are this many of its pixels square.
    private const int PictureSize = 50;

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

    /// <summary>How wide the radar's corner is on the screen.</summary>
    public float ScreenWidth => (ClassicGui.Bitmap("fstpersn/radar/rmmap.bmp")?.GetWidth() ?? 0) * Math.Max(1, (int)Math.Round(GetViewportRect().Size.Y / 480f));

    /// <summary>How high the radar's backdrop stands on the screen.</summary>
    public float ScreenHeight => (ClassicGui.Bitmap("fstpersn/radar/rmmap.bmp")?.GetHeight() ?? 0) * Math.Max(1, (int)Math.Round(GetViewportRect().Size.Y / 480f));

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
        var range = (float)Math.Max(machine.Stats.ScannerRange, 1);
        var reach = ScannerRadius / range;
        // In a Wired run the dark takes away what the scanners no longer reach, at the same scale.
        if (world.Wired is not null)
            range = MathF.Max(world.ScannerReach(machine), 1);
        var turn = machine.Facing + Head + MathF.PI / 2;
        var (cos, sin) = (MathF.Cos(turn), MathF.Sin(turn));
        Vector2 Spot(NVector2 position)
        {
            var d = position - machine.Position;
            return corner + (ScannerCentre + new Vector2(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos) * reach) * scale;
        }
        Vector2? OnRadar(NVector2 position) => (position - machine.Position).LengthSquared() >= range * range ? null : Spot(position);
        foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Count > 0 && s.Ore > 0))
            if (OnRadar(site.Position) is { } at)
                Blit("fstpersn/radar/blips/ore.bmp", at, scale);
        foreach (var wreck in world.Debris)
            if (OnRadar(wreck.Position) is { } at)
                Blit("fstpersn/radar/blips/debris.bmp", at, scale);
        if (world.Wired is { } run)
            ShowRun(run, world, OnRadar, Spot, corner + ScannerCentre * scale, range * reach * scale, reach, scale);
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

        // The machine's own picture, and its health and armour; a hero of a run is photographed, as no bitmap shows it.
        var key = machine.Key;
        if (_game.HeroPicture(machine) is { } photo)
            DrawTextureRect(photo, new Rect2(corner + IconAt * scale, new Vector2(PictureSize, PictureSize) * scale), false);
        else
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

    /// <summary>What a Wired run adds: its crates, as a green diamond, and the coming ion strikes' circles.</summary>
    private void ShowRun(Machines.Sim.Survival.WiredRun run, GameWorld world, Func<NVector2, Vector2?> onRadar, Func<NVector2, Vector2> spot,
        Vector2 disc, float discRadius, float reach, int scale)
    {
        foreach (var crate in world.WiredPickups)
        {
            if (onRadar(crate.Position) is not { } at)
                continue;
            var r = 2.5f * scale;
            DrawColoredPolygon([at + new Vector2(0, -r), at + new Vector2(r, 0), at + new Vector2(0, r), at + new Vector2(-r, 0)], new Color(0.4f, 1, 0.65f));
        }
        // A circle counts as far as it lies in reach, though its middle may not.
        foreach (var strike in run.Strikes)
            Ring(spot(strike.Position), strike.Radius * reach * scale, disc, discRadius, new Color(1, 0.25f, 0.12f, 0.7f + 0.3f * Mathf.Sin((float)_time * 12)), scale);
    }

    /// <summary>A circle, as much of it as lies on the scanner's disc.</summary>
    private void Ring(Vector2 at, float radius, Vector2 disc, float discRadius, Color colour, float width)
    {
        const int Steps = 64;
        var line = new List<Vector2>();
        void Draw()
        {
            if (line.Count > 1)
                DrawPolyline([.. line], colour, width, true);
            line.Clear();
        }
        for (var i = 0; i <= Steps; i++)
        {
            var point = at + Vector2.FromAngle(i * Mathf.Tau / Steps) * radius;
            if (point.DistanceTo(disc) <= discRadius)
                line.Add(point);
            else
                Draw();
        }
        Draw();
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
