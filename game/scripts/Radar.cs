using Godot;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// The first-person radar of the original (MachGuiRadar), in the lower right corner: it switches on in ten frames
/// (<c>radar0</c> to <c>radar9</c>, half a second), then shows what lies within the driven machine's scanner range
/// as blips turned to its heading (machines, buildings, pods and missile emplacements in their race's colour, and the
/// ore anyone has found, and wreckage; in a Wired match no blip of a hero hiding in its shadow, and a located enemy's
/// blinking in a ring), the dome and arrow in its middle, the machine's picture, and its own health and armour
/// as two upright bars filled from the bottom, amber and then red as they run low; at a fifth of its health a warning
/// sounds. The bitmaps come from <c>gui/fstpersn/radar</c> of the player's game copy, drawn a texel to a screen pixel at a
/// whole number of pixels to each of the original's (<see cref="HudArt"/>: the enlargements of tools/hud-sharp where made,
/// in which the scanner's checkered glass is evenly half see-through, else the bitmaps blown up); the blips, the arrow and
/// the dome in the middle are drawn as shapes. Under them the remake lays the map of the ground, faintly, turned with the
/// view as the blips are: the continent map's picture, as the ground chosen draws it.
/// </summary>
public partial class Radar : Control
{
    private const int StartupFrames = 10;
    private const double StartupSeconds = 0.5;

    // Where the original draws on the radar, in its pixels from the backdrop's corner.
    private static readonly Vector2 ScannerCentre = new(102, 103), DomeAt = new(100, 101), ArrowAt = new(85, 85), IconAt = new(180, 30);
    private static readonly Vector2 HealthAt = new(216, 111), ArmourAt = new(234, 111);
    private const float ScannerRadius = 55;

    // The scanner's glass, a circle in the original's pixels, and how strongly the map shows through it; where the
    // driven machine stands on it, at the dome's middle, as the blips' bitmaps put the others half a blip off their spot.
    private static readonly Vector2 GlassCentre = new(106, 106), MapMiddle = new(105.5f, 106.5f);
    private const float GlassRadius = 58.5f, MapShows = 0.42f;
    private const int GlassSteps = 64;

    // The original's machine pictures are this many of its pixels square.
    private const int PictureSize = 50;

    private Game _game = null!;
    private GameAudio? _audio;
    private Texture2D? _planetMap;
    private double _time, _started;
    private bool _healthy;

    public static Radar Create(Game game, GameAudio? audio, Texture2D? planetMap = null) => new()
    {
        Name = "Radar",
        _game = game,
        _audio = audio,
        _planetMap = planetMap,
        MouseFilter = MouseFilterEnum.Ignore,
        TextureFilter = TextureFilterEnum.Linear,
        Visible = false,
    };

    /// <summary>How wide the radar's corner is on the screen.</summary>
    public float ScreenWidth => HudArt.Size(ClassicGui.Bitmap("fstpersn/radar/rmmap.bmp")?.GetWidth() ?? 0, HudScale.Of(GetViewportRect().Size));

    /// <summary>How high the radar's backdrop stands on the screen.</summary>
    public float ScreenHeight => HudArt.Size(ClassicGui.Bitmap("fstpersn/radar/rmmap.bmp")?.GetHeight() ?? 0, HudScale.Of(GetViewportRect().Size));

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

    // The art of the first moments in first person is made ready for the screen's scale as the match begins.
    public override void _Ready() => HudArt.Prepare(HudScale.Of(GetViewportRect().Size));

    public override void _Process(double delta)
    {
        _time += delta;
        if (Visible)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        var scale = HudScale.Of(size);
        // The HUD's size changed in the options, or the window's: the art for the new one is made ready.
        HudArt.Prepare(scale);
        if (Driven is not { } machine || Art("fstpersn/radar/rmmap.bmp", scale) is not { } backdrop)
            return;
        var corner = size - backdrop.GetSize();

        // The start-up frames, right-aligned, until the radar is on.
        var frame = (int)((_time - _started) / StartupSeconds * StartupFrames);
        if (frame < StartupFrames)
        {
            if (Art($"fstpersn/radar/radar{frame}.bmp", scale) is { } starting)
                DrawTexture(starting, new Vector2(size.X - starting.GetWidth(), corner.Y));
            return;
        }

        DrawTexture(backdrop, corner);
        // Sixteen arrows round the compass, the first straight ahead; each bitmap's arrow starts at its middle.
        var arrow = (int)(Mathf.PosMod(-Mathf.RadToDeg(Head), 360) / 360 * 16) % 16;
        DrawArrow(corner + (ArrowAt + new Vector2(20, 20.5f)) * scale, arrow * Mathf.Tau / 16, scale);

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
        DrawGround(corner, machine.Position, cos, sin, reach, scale);
        foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Count > 0 && s.Ore > 0))
            if (OnRadar(site.Position) is { } at)
                Blit("fstpersn/radar/blips/ore.bmp", at, scale);
        foreach (var wreck in world.Debris)
            if (OnRadar(wreck.Position) is { } at)
                Blit("fstpersn/radar/blips/debris.bmp", at, scale);
        if (world.Wired is { } run)
            ShowRun(run, world, OnRadar, Spot, corner + ScannerCentre * scale, range * reach * scale, reach, scale);
        // A Wired hero hiding in its shadow shows no blip; an enemy a geo locator's pulse has located blinks, in a ring.
        var blink = Mathf.PosMod((float)_time, 0.5f) < 0.3f;
        foreach (var actor in world.Actors)
        {
            if (actor.Id == machine.Id || actor is Machine { Inside: not null } || OnRadar(actor.Position) is not { } at || _game.InShadow(actor))
                continue;
            var letter = ClassicPanel.RaceFolder(actor is Machine { Camouflaged: true } ? Game.Player : actor.Race)[0];
            var kind = actor switch
            {
                Machine => "machpix",
                Construction c when c.IsType("POD") => "podpix",
                Construction c when c.IsType("MISSILE_EMPLACEMENT") => "misspix",
                _ => "conspix",
            };
            var blip = $"fstpersn/radar/blips/{letter}{kind}.bmp";
            if (!_game.LocatedFoe(actor))
                Blit(blip, at, scale);
            else
            {
                var middle = at.Round() + (ClassicGui.Bitmap(blip)?.GetSize() ?? Vector2.Zero) * scale / 2;
                DrawArc(middle, 4.5f * scale, 0, Mathf.Tau, 16, new Color(0, 0, 0, 0.7f), scale + 2, true);
                DrawArc(middle, 4.5f * scale, 0, Mathf.Tau, 16, new Color(1, 0.62f, 0.1f, blink ? 1 : 0.6f), scale, true);
                if (blink)
                    Blit(blip, at, scale);
            }
        }
        DrawDome(corner + (DomeAt + new Vector2(5.5f, 5.5f)) * scale, scale);

        // The machine's own picture, and its health and armour; a hero of a run is photographed, as no bitmap shows it. Any
        // other match shows the original's picture of the machine or, where the game copy has none, nothing, as the original.
        var key = machine.Key;
        // A photograph is larger than its box and shrinks smoothly: the radar's filter is linear, and all else it draws lies a texel to a pixel.
        if (_game.HeroPicture(machine) is { } photo)
            DrawTextureRect(photo, new Rect2(corner + IconAt * scale, new Vector2(PictureSize, PictureSize) * scale), false);
        else if (world.Wired is null && world.Battle is null)
            Blit($"fstpersn/machines/{ClassicPanel.MachinePrefix(key.Type, key.SubType)}{key.HardwareLevel}{ClassicPanel.WeaponSuffix(key.WeaponCombo)}.bmp",
                corner + IconAt * scale, scale);
        else if (PictureOf(key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo) is { } picture)
            Blit($"fstpersn/machines/{picture}", corner + IconAt * scale, scale);
        var health = machine.HitPoints / (float)Math.Max(1, machine.MaxHitPoints);
        var armour = machine.Armour / (float)Math.Max(1, machine.MaxArmour);
        Bar("rhealth", health, corner + HealthAt * scale, scale);
        Bar("rarmour", armour, corner + ArmourAt * scale, scale);
        if (_healthy && health <= 0.2f)
            _audio?.PlayGui("gui/sounds/hpcritic.wav");
        _healthy = health > 0.2f;
    }

    /// <summary>
    /// The map of the ground under the glass, faintly: a disc the size of the glass whose corners look up the continent
    /// map's picture where the ground under them lies, so that it turns and moves as the blips do.
    /// </summary>
    private void DrawGround(Vector2 corner, NVector2 position, float cos, float sin, float reach, float scale)
    {
        if ((MiniMaps.For(MatchRequest.Current.Scenario) ?? _planetMap) is not { } map)
            return;
        var surface = _game.World.Planet.Surface;
        var (middle, glass) = (corner + MapMiddle * scale, corner + GlassCentre * scale);
        var points = new Vector2[GlassSteps];
        var uvs = new Vector2[GlassSteps];
        for (var i = 0; i < GlassSteps; i++)
        {
            points[i] = glass + Vector2.FromAngle(i * Mathf.Tau / GlassSteps) * GlassRadius * scale;
            // Back from the radar to the ground: the blips' turn undone.
            var o = (points[i] - middle) / (reach * scale);
            var ground = new Vector2(position.X + o.X * cos - o.Y * sin, position.Y + o.X * sin + o.Y * cos);
            uvs[i] = new Vector2(ground.X / surface.Width, ground.Y / surface.Depth);
        }
        DrawPolygon(points, new[] { new Color(1, 1, 1, MapShows) }, uvs, map);
    }

    /// <summary>What a Wired run adds: its crates, as a green diamond, and the coming ion strikes' circles.</summary>
    private void ShowRun(Machines.Sim.Survival.WiredRun run, GameWorld world, Func<NVector2, Vector2?> onRadar, Func<NVector2, Vector2> spot,
        Vector2 disc, float discRadius, float reach, float scale)
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

    // The picture chosen for each name a machine's picture would have, by PictureOf.
    private static readonly Dictionary<string, string?> Pictures = [];

    /// <summary>
    /// The file in <c>gui/fstpersn/machines</c> that pictures a machine (MachActorBitmaps): of its kind, level and weapons
    /// where the original has it, else of its kind and level with other weapons, the unarmed first, else of the nearest level
    /// below, else above, so that a machine of a level the original never had (Wired's Phantom, Titan and its own grunts and
    /// assassins, a geo locator's hero) still shows its kind; null for a kind without a picture.
    /// </summary>
    private static string? PictureOf(string type, string? subType, int level, string? combo)
    {
        var (prefix, suffix) = (ClassicPanel.MachinePrefix(type, subType), ClassicPanel.WeaponSuffix(combo));
        var name = $"{prefix}{level}{suffix}";
        if (!ClassicGui.Available)
            return null;
        if (Pictures.TryGetValue(name, out var known))
            return known;
        var pictured = new List<(string File, int Level, string Suffix)>();
        foreach (var file in ClassicGui.Names("fstpersn/machines"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var digits = stem.Length > prefix.Length && stem.StartsWith(prefix, StringComparison.Ordinal)
                ? stem[prefix.Length..].TakeWhile(char.IsAsciiDigit).Count()
                : 0;
            if (digits > 0)
                pictured.Add((file, int.Parse(stem.AsSpan(prefix.Length, digits)), stem[(prefix.Length + digits)..]));
        }
        var chosen = pictured
            .OrderBy(p => p.Level == level ? 0 : p.Level < level ? 1 : 2)
            .ThenBy(p => Math.Abs(p.Level - level))
            .ThenBy(p => p.Suffix == suffix ? 0 : p.Suffix.Length == 0 ? 1 : 2)
            .ThenBy(p => p.File, StringComparer.Ordinal)
            .Select(p => p.File)
            .FirstOrDefault();
        Pictures[name] = chosen;
        return chosen;
    }

    private static Texture2D? Art(string path, float scale) => HudArt.Get(path, scale);

    private void Blit(string path, Vector2 at, float scale)
    {
        if (path.StartsWith("fstpersn/radar/blips/", StringComparison.Ordinal))
            DrawBlip(path, at.Round(), scale);
        else if (Art(path, scale) is { } bitmap)
            DrawTexture(bitmap, at.Round());
    }

    /// <summary>
    /// A blip as a shape its bitmap's size, in the bitmap's own colours: a machine's rounded plate with an M on it, a
    /// building's square plate with a C, a missile emplacement's with an E and a pod's round plate with a P, each in a
    /// brown rim and lit from the top left; ore a pale green heap and wreckage a dark one. Without the bitmap, none.
    /// </summary>
    private void DrawBlip(string path, Vector2 at, float scale)
    {
        if (ClassicGui.Bitmap(path)?.GetImage() is not { } image)
            return;
        var (width, height) = (image.GetWidth(), image.GetHeight());
        Vector2 P(float x, float y) => at + new Vector2(x, y) * scale;
        var line = Mathf.Max(1, scale * 0.75f);
        var name = Path.GetFileNameWithoutExtension(path);
        if (name is "ore" or "debris")
        {
            Vector2[] heap = name == "ore"
                ? [P(0.3f, 3.8f), P(3.5f, 0.3f), P(6.7f, 3.8f)]
                : [P(0.3f, 3.8f), P(1.8f, 1.6f), P(3.2f, 2.2f), P(4.4f, 0.3f), P(5.7f, 3.8f)];
            var (fill, edge) = name == "ore" ? (Color.Color8(181, 255, 173), Color.Color8(8, 181, 0)) : (Color.Color8(20, 20, 20), Color.Color8(110, 110, 110));
            DrawColoredPolygon(heap, fill);
            DrawPolyline([.. heap, heap[0]], edge, line, true);
            return;
        }
        if (name.Length < 2 || name[1..] is not ("machpix" or "conspix" or "misspix" or "podpix"))
        {
            if (Art(path, scale) is { } bitmap)
                DrawTexture(bitmap, at);
            return;
        }
        // The plate's lightest, darkest and middle colours, leaving out the rim, the letter and the see-through corners.
        var rim = Color.Color8(90, 49, 24);
        var (light, dark, sum, count) = (Colors.Black, Colors.White, new Color(0, 0, 0, 0), 0);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = image.GetPixel(x, y);
                if (c.A < 0.5f || c.R8 == c.G8 && c.G8 == c.B8 || c.R8 == rim.R8 && c.G8 == rim.G8 && c.B8 == rim.B8)
                    continue;
                if (c.Luminance > light.Luminance)
                    light = c;
                if (c.Luminance < dark.Luminance)
                    dark = c;
                (sum, count) = (sum + c, count + 1);
            }
        var mid = count == 0 ? light : sum / count;
        // Lit at the top left corner, in the plate's own colour across the middle, shaded at the bottom right.
        Color Lit(float t) => t < 0.5f ? light.Lerp(mid, t * 2) : mid.Lerp(dark, t * 2 - 1);
        var kind = name[1..];
        var round = kind switch { "machpix" => 2.2f, "podpix" => 3.6f, _ => 0.8f } * scale;
        var box = new Rect2(at, new Vector2(width, height) * scale);
        DrawColoredPolygon(Rounded(box, round), rim);
        var inner = Rounded(box.Grow(-scale), Mathf.Max(0, round - scale));
        DrawPolygon(inner, [.. inner.Select(p => Lit(Mathf.Clamp((p - box.Position).Dot(Vector2.One) / (box.Size.X + box.Size.Y), 0, 1)))]);
        var letter = kind switch { "machpix" => "M", "conspix" => "C", "misspix" => "E", _ => "P" };
        var size = (int)(6.5f * scale);
        var font = MenuTheme.Font;
        var across = font.GetStringSize(letter, HorizontalAlignment.Left, -1, size).X;
        var middle = box.GetCenter();
        DrawString(font, new Vector2(middle.X - across / 2, middle.Y + size * 0.36f), letter, HorizontalAlignment.Left, -1, size, Color.Color8(36, 36, 36));
    }

    /// <summary>The corners of a box with rounded corners, clockwise from the top left.</summary>
    private static Vector2[] Rounded(Rect2 box, float radius)
    {
        const int Steps = 4;
        radius = Mathf.Min(radius, Mathf.Min(box.Size.X, box.Size.Y) / 2);
        var points = new List<Vector2>();
        Vector2[] centres = [box.Position + new Vector2(radius, radius), new(box.End.X - radius, box.Position.Y + radius), box.End - new Vector2(radius, radius), new(box.Position.X + radius, box.End.Y - radius)];
        for (var corner = 0; corner < 4; corner++)
            for (var step = 0; step <= Steps; step++)
                points.Add(centres[corner] + Vector2.FromAngle(Mathf.Pi + (corner + step / (float)Steps) * Mathf.Pi / 2) * radius);
        return [.. points];
    }

    // The arrow's colours (gui/fstpersn/radar/arrow/point1.bmp): a shaft two pixels wide, lit on its left, under a head ten high.
    private static readonly Color ArrowLight = new(0.16f, 0.71f, 0.03f), ArrowShade = new(0.19f, 0.52f, 0.09f),
        ArrowHead = new(0.13f, 0.97f, 0), ArrowEdge = new(0.22f, 0.32f, 0.13f);

    /// <summary>The arrow out of the scanner's middle, turned clockwise from straight ahead (MachGuiRadar::displayMotionDirection).</summary>
    private void DrawArrow(Vector2 middle, float turn, float scale)
    {
        Vector2 At(float x, float y) => middle + new Vector2(x, y).Rotated(turn) * scale;
        DrawLine(At(-0.5f, 0), At(-0.5f, -10.5f), ArrowLight, scale, true);
        DrawLine(At(0.5f, 0), At(0.5f, -10.5f), ArrowShade, scale, true);
        Vector2[] head = [At(0, -20.5f), At(3, -10), At(-3, -10)];
        DrawColoredPolygon(head, ArrowHead);
        DrawPolyline([.. head, head[0]], ArrowEdge, Mathf.Max(1, scale * 0.6f), true);
    }

    /// <summary>
    /// The dome in the scanner's middle (gui/fstpersn/radar/dome.bmp): a grey ball eleven of the original's pixels across,
    /// lit from the top left, as discs of light shrinking towards its shine.
    /// </summary>
    private void DrawDome(Vector2 middle, float scale)
    {
        const int Discs = 24;
        var (dark, light) = (new Color(0.16f, 0.17f, 0.17f), new Color(0.80f, 0.82f, 0.82f));
        var radius = 5.5f * scale;
        DrawCircle(middle, radius + 0.5f, new Color(0.10f, 0.11f, 0.11f), true, -1, true);
        for (var disc = 0; disc < Discs; disc++)
        {
            var t = disc / (float)Discs;
            DrawCircle(middle + new Vector2(-0.35f, -0.4f) * radius * t, radius * (1 - t * 0.85f), dark.Lerp(light, Mathf.Sqrt(t)), true, -1, true);
        }
    }

    /// <summary>An upright bar filled from the bottom; its second and third pictures (amber, red) below 1.7 and 1 third.</summary>
    private void Bar(string name, float share, Vector2 topLeft, float scale)
    {
        share = Mathf.Clamp(share, 0, 1);
        var picture = share <= 1 / 3f ? $"{name}3" : share <= 1.7f / 3 ? $"{name}2" : name;
        if (Art($"fstpersn/radar/{picture}.bmp", scale) is not { } bar)
            return;
        var height = bar.GetHeight();
        var shown = Mathf.Round(share * height);
        topLeft = topLeft.Round();
        DrawTextureRectRegion(bar, new Rect2(topLeft + new Vector2(0, height - shown), new Vector2(bar.GetWidth(), shown)),
            new Rect2(0, height - shown, bar.GetWidth(), shown));
    }
}
