using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

// The special of the hero's chassis in the foot of a run's or a battle's screen, from what the world shows of it
// (WiredSpecialView): the Colossus' anchor, the Marshal's rally, the spies' mines with the Phantom's shadow, the
// constructors' towers, the geo locators' pulse, the Widow's lurk and the Harvester's harvest. Old Iron's ram and the
// Hornet's afterburner are their dash, whose block takes their name. Over the view the enemies a pulse has located stand in
// brackets. Wired is the remake's own addition.
public partial class WiredHud
{
    private const float SpecialWidth = 170;

    /// <summary>The specials the special's key works: the constructors' takes up their towers.</summary>
    private const ChassisSpecial Keyed = ChassisSpecial.Anchor | ChassisSpecial.Field | ChassisSpecial.Mines | ChassisSpecial.Locate | ChassisSpecial.Towers;

    private bool _specialWasReady = true;
    private double _specialReadyAt = -10, _harvestAt = -10;
    private string _harvested = "";

    // Drawings on the 16 by 16 grid of the hangar's (HangarGlyphs): an anchor, a squad called in, a tower, a pulse going
    // out, a sickle, and an eye open and shut.
    private static readonly float[][] AnchorGlyph =
    [
        Ring(8, 3, 1.5f), [8, 4.5f, 8, 14.5f], [5, 7, 11, 7], [2, 9.5f, 3, 12, 5.5f, 14, 8, 14.5f, 10.5f, 14, 13, 12, 14, 9.5f],
        [1, 11, 2, 9.5f, 3.5f, 10.5f], [12.5f, 10.5f, 14, 9.5f, 15, 11],
    ];

    private static readonly float[][] RallyGlyph =
    [
        Ring(8, 8, 1.8f), [1, 1, 5, 5], [2.5f, 5, 5, 5, 5, 2.5f], [15, 1, 11, 5], [13.5f, 5, 11, 5, 11, 2.5f],
        [1, 15, 5, 11], [2.5f, 11, 5, 11, 5, 13.5f], [15, 15, 11, 11], [13.5f, 11, 11, 11, 11, 13.5f],
    ];

    private static readonly float[][] TowerGlyph = [[2, 15, 14, 15], [4, 15, 5.5f, 9, 10.5f, 9, 12, 15], [5.5f, 9, 5.5f, 5, 10.5f, 5, 10.5f, 9], [10.5f, 6.5f, 15, 4]];

    private static readonly float[][] PulseGlyph = [Ring(8, 8, 1.5f), Ring(8, 8, 4.2f), Ring(8, 8, 7), [8, 8, 13, 3]];

    private static readonly float[][] SickleGlyph = [[3, 14, 6.5f, 10.5f], [6.5f, 10.5f, 5, 6.5f, 6.5f, 3.5f, 10, 1.5f, 14, 2, 11, 3.5f, 8.5f, 6, 8, 9, 6.5f, 10.5f]];

    private static readonly float[][] EyeOpen = [[1, 8, 4, 4.5f, 8, 3.5f, 12, 4.5f, 15, 8, 12, 11.5f, 8, 12.5f, 4, 11.5f, 1, 8], Ring(8, 8, 2.5f)];

    private static readonly float[][] EyeShut = [[1, 7, 4, 9.5f, 8, 10.5f, 12, 9.5f, 15, 7], [3.5f, 9.5f, 2.5f, 12], [8, 10.5f, 8, 13], [12.5f, 9.5f, 13.5f, 12]];

    /// <summary>The name of a special that is its chassis' dash, for the dash's block; null for any other.</summary>
    private static string? DashName(WiredSpecialView? view) =>
        view is { } shown && (shown.Special & (ChassisSpecial.Plough | ChassisSpecial.Afterburner)) != 0 ? shown.Name : null;

    /// <summary>The special, where it has a block of its own beside the dash's.</summary>
    private static WiredSpecialView? OwnBlock(WiredSpecialView? view) => view is not null && DashName(view) is null ? view : null;

    /// <summary>The special's key coming back flashes its block, as an ability's does.</summary>
    private void FollowSpecial(WiredSpecialView? view)
    {
        var ready = view is { Ready: true };
        if (ready && !_specialWasReady)
            _specialReadyAt = _time;
        _specialWasReady = ready;
    }

    /// <summary>The hero harvested a kill: its block flashes with the armour it got back.</summary>
    public void Harvested(string gained)
    {
        _harvestAt = _time;
        _harvested = gained;
    }

    /// <summary>
    /// The special's block: its name (with what it counts) over a drawing of it, where it stands beside that, and a bar along
    /// the foot for the wait or the work under way; the key's cap on the corner of what the key works.
    /// </summary>
    private void DrawSpecial(WiredSpecialView view, Vector2 at)
    {
        var box = new Rect2(at, new Vector2(SpecialWidth, Rise) * _u);
        var special = view.Special;
        var keyed = (special & Keyed) != 0;
        var flash = keyed ? Fading(_specialReadyAt, 0.6) : 0;
        var on = view.Phase is "Anchored" or "Lurking" or "Hidden";
        Plate(box, Plating, on ? Gold : keyed && view.Ready ? MenuTheme.Glow.Lerp(Colors.White, flash) : MenuTheme.Edge, 10);
        var mark = new Rect2(at + new Vector2(10, 30) * _u, new Vector2(48, 48) * _u);
        var text = at + new Vector2(68, 0) * _u;
        if ((special & ChassisSpecial.Towers) != 0)
            DrawTowers(view, box, mark, text);
        else if ((special & ChassisSpecial.Mines) != 0)
            DrawMines(view, box, mark, text);
        else if ((special & ChassisSpecial.Locate) != 0)
            DrawLocate(view, box, mark, text);
        else if ((special & ChassisSpecial.Anchor) != 0)
            DrawAnchor(view, box, mark, text);
        else if ((special & ChassisSpecial.Field) != 0)
            DrawRally(view, box, mark, text);
        else if ((special & ChassisSpecial.Lurk) != 0)
            DrawLurk(view, box, mark, text);
        else if ((special & ChassisSpecial.Harvest) != 0)
            DrawHarvest(view, box, mark, text);
        if (flash > 0)
            DrawRect(box.Grow(flash * 6 * _u), new Color(MenuTheme.Glow, flash * 0.7f), false, 2 * _u);
        Cap(box, keyed ? KeyOf(-2) : null);
    }

    /// <summary>The towers standing of the most the chassis lets stand, and the work on the nearest one under way.</summary>
    private void DrawTowers(WiredSpecialView view, Rect2 box, Rect2 mark, Vector2 text)
    {
        SpecialTitle(box, Lang.T("Towers {0}/{1}", view.Count, view.Most), view.Ready);
        var working = view.SecondsLeft > 0;
        Glyph(TowerGlyph, mark, working ? Gold : view.Ready ? MenuTheme.Glow : MenuTheme.Dim);
        Pips(text + new Vector2(0, 30) * _u, view.Count, view.Most, MenuTheme.Glow);
        if (!working)
            return;
        Say(text + new Vector2(0, 78) * _u, $"{Mathf.CeilToInt(view.SecondsLeft)} s", 15, Gold);
        SpecialBar(box, view.Share, Gold);
    }

    /// <summary>The mines carried of the most the chassis carries; on the Phantom an eye, shut while it is hidden, open with the seconds until it is again.</summary>
    private void DrawMines(WiredSpecialView view, Rect2 box, Rect2 mark, Vector2 text)
    {
        SpecialTitle(box, Lang.T("Mines {0}/{1}", view.Count, view.Most), view.Ready);
        Pips(text + new Vector2(0, 30) * _u, view.Count, view.Most, MenuTheme.Glow);
        if (view.Phase is not { } phase)
        {
            Glyph(HangarGlyphs.All.GetValueOrDefault("minefield"), mark, view.Ready ? MenuTheme.Glow : MenuTheme.Dim);
            SpecialBar(box, view.Share, view.Ready ? MenuTheme.Glow : MenuTheme.Dim);
            return;
        }
        var hidden = phase == "Hidden";
        Glyph(hidden ? EyeShut : EyeOpen, mark, hidden ? Gold : Warning);
        var word = hidden ? Lang.T(phase).Caps() : $"{Lang.T(phase).Caps()} {Tenths(view.SecondsLeft)} s";
        Say(text + new Vector2(0, 78) * _u, word, Shrunk(word, 15, SpecialWidth - 78), hidden ? Gold : Warning);
        SpecialBar(box, view.Share, hidden ? Gold : Warning);
    }

    /// <summary>The pulse: its wait as an ability's, and the enemies it has located standing now.</summary>
    private void DrawLocate(WiredSpecialView view, Rect2 box, Rect2 mark, Vector2 text)
    {
        SpecialTitle(box, Lang.T(view.Name), view.Ready);
        Glyph(PulseGlyph, mark, view.Ready ? MenuTheme.Glow : MenuTheme.Dim);
        Cooling(view, mark);
        if (view.Count > 0)
        {
            var located = Lang.T("Located {0}", view.Count).Caps();
            Say(text + new Vector2(0, 60) * _u, located, Shrunk(located, 17, SpecialWidth - 78), Warning);
        }
        else if (view.Ready)
            Say(text + new Vector2(0, 60) * _u, Lang.T("Ready").Caps(), 17, MenuTheme.Glow);
        SpecialBar(box, view.Share, view.Ready ? MenuTheme.Glow : MenuTheme.Dim);
    }

    /// <summary>The Colossus' tracks: free, locking for two seconds, anchored, or coming free for two.</summary>
    private void DrawAnchor(WiredSpecialView view, Rect2 box, Rect2 mark, Vector2 text)
    {
        SpecialTitle(box, Lang.T(view.Name), view.Ready);
        var phase = view.Phase ?? "Free";
        var moving = phase is "Locking" or "Releasing";
        var colour = phase == "Anchored" ? Gold : moving ? Ice : view.Ready ? MenuTheme.Glow : MenuTheme.Dim;
        Glyph(AnchorGlyph, mark, moving ? Ice.Lerp(Colors.White, 0.5f + 0.5f * MathF.Sin((float)_time * 10)) : colour);
        var word = Lang.T(phase).Caps();
        Say(text + new Vector2(0, 56) * _u, word, Shrunk(word, 17, SpecialWidth - 78), colour);
        if (moving)
            Say(text + new Vector2(0, 76) * _u, $"{Tenths(view.SecondsLeft)} s", 14, MenuTheme.Text);
        SpecialBar(box, view.Share, colour);
    }

    /// <summary>The rally's wait as an ability's, and how many of the squad stand within the Marshal's reach.</summary>
    private void DrawRally(WiredSpecialView view, Rect2 box, Rect2 mark, Vector2 text)
    {
        SpecialTitle(box, Lang.T(view.Name), view.Ready);
        Glyph(RallyGlyph, mark, view.Ready ? MenuTheme.Glow : MenuTheme.Dim);
        Cooling(view, mark);
        if (view.Most == 0)
            Say(text + new Vector2(0, 60) * _u, Lang.T("No squad"), Shrunk(Lang.T("No squad"), 15, SpecialWidth - 78), MenuTheme.Dim);
        else
        {
            Say(text + new Vector2(0, 58) * _u, $"{view.Count}/{view.Most}", 24, view.Count > 0 ? MenuTheme.Bright : MenuTheme.Dim);
            Say(text + new Vector2(0, 76) * _u, Lang.T("in reach"), Shrunk(Lang.T("in reach"), 13, SpecialWidth - 78), MenuTheme.Dim);
        }
        SpecialBar(box, view.Share, view.Ready ? MenuTheme.Glow : MenuTheme.Dim);
    }

    /// <summary>The Widow's lurk light: filling while it stands still, lit once it lurks, out once its first shot is spent.</summary>
    private void DrawLurk(WiredSpecialView view, Rect2 box, Rect2 mark, Vector2 text)
    {
        var lurks = view.Phase == "Lurking";
        var spent = view.Phase == "Spent";
        SpecialTitle(box, Lang.T(view.Name), lurks);
        var (centre, radius) = (mark.GetCenter(), 17 * _u);
        if (lurks)
        {
            var pulse = 0.8f + 0.2f * MathF.Sin((float)_time * 6);
            DrawCircle(centre, radius + 6 * _u, new Color(Gold, 0.18f * pulse));
            DrawCircle(centre, radius, new Color(Gold, pulse));
            DrawArc(centre, radius, 0, Mathf.Tau, 32, Colors.White, 1.5f * _u, true);
        }
        else
        {
            DrawCircle(centre, radius, MenuTheme.Well);
            if (!spent && view.Share > 0)
                DrawArc(centre, radius - 2 * _u, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * Mathf.Clamp(view.Share, 0, 1), 32, Gold, 4 * _u, true);
            DrawArc(centre, radius, 0, Mathf.Tau, 32, spent ? MenuTheme.Dim : MenuTheme.Edge, 1.5f * _u, true);
        }
        var word = lurks ? Lang.T("Lurking") : spent ? Lang.T("Spent") : view.Share > 0 ? $"{Tenths(view.SecondsLeft)} s" : null;
        if (word is not null)
            Say(text + new Vector2(0, 60) * _u, word.Caps(), Shrunk(word.Caps(), 17, SpecialWidth - 78), lurks ? Gold : spent ? MenuTheme.Dim : MenuTheme.Text);
        SpecialBar(box, lurks ? 1 : spent ? 0 : view.Share, Gold);
    }

    /// <summary>The Harvester's harvest, which has no key: it flashes with the armour a kill near by gave back.</summary>
    private void DrawHarvest(WiredSpecialView view, Rect2 box, Rect2 mark, Vector2 text)
    {
        var flash = Fading(_harvestAt, 1.6);
        SpecialTitle(box, Lang.T(view.Name), flash > 0);
        Glyph(SickleGlyph, mark, flash > 0 ? Gold.Lerp(Colors.White, flash * 0.6f) : MenuTheme.Glow);
        if (flash > 0)
        {
            // Armour already whole gains nothing: the flash says the harvest took, without a "+0".
            if (_harvested != "0")
                Say(text + new Vector2(0, 60) * _u, $"+{_harvested}", 26, new Color(Ice, Mathf.Min(1, flash * 2)));
            DrawRect(box.Grow(flash * 6 * _u), new Color(Gold, flash * 0.7f), false, 2 * _u);
        }
        else
            Say(text + new Vector2(0, 60) * _u, $"{WiredBalance.HarvestReach:0} m", 17, MenuTheme.Dim);
    }

    private void SpecialTitle(Rect2 box, string title, bool lit) =>
        Say(box.Position + new Vector2(10, 20) * _u, Fit(title.Caps(), 13, SpecialWidth - 50), 13, lit ? MenuTheme.Text : MenuTheme.Dim);

    /// <summary>A wait over the drawing, as an ability's: darkened as a hand going round, with the seconds left.</summary>
    private void Cooling(WiredSpecialView view, Rect2 mark)
    {
        if (view.SecondsLeft <= 0)
            return;
        Sweep(mark.Grow(4 * _u), view.Share);
        Say(mark.GetCenter() + new Vector2(0, 10) * _u, $"{Mathf.CeilToInt(view.SecondsLeft)}", 26, Colors.White, 0.5f, outline: 3);
    }

    /// <summary>A thin bar along the foot of the block, filled to the share.</summary>
    private void SpecialBar(Rect2 box, float share, Color colour)
    {
        var bar = new Rect2(box.Position + new Vector2(10, 85) * _u, new Vector2(SpecialWidth - 20, 4) * _u);
        DrawRect(bar, MenuTheme.Well);
        DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * Mathf.Clamp(share, 0, 1), bar.Size.Y)), colour);
    }

    /// <summary>A light for each of <paramref name="most"/>, lit for each of <paramref name="count"/>, in up to two rows.</summary>
    private void Pips(Vector2 at, int count, int most, Color colour)
    {
        var row = most <= 5 ? Math.Max(most, 1) : (most + 1) / 2;
        for (var i = 0; i < most; i++)
        {
            var pip = new Rect2(at + new Vector2(i % row * 18, i / row * 15) * _u, new Vector2(13, 10) * _u);
            if (i < count)
                Plate(pip, colour, colour, 2);
            else
                Plate(pip, MenuTheme.Well, MenuTheme.Edge, 2);
        }
    }

    /// <summary>Seconds to a tenth, with the decimal mark of the player's language.</summary>
    private static string Tenths(float seconds) =>
        MathF.Max(0, seconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', Lang.Code == "de" ? ',' : '.');

    /// <summary>The size, at most <paramref name="size"/> and no less than <paramref name="least"/>, at which a text fits a width in the HUD's pixels.</summary>
    private float Shrunk(string text, float size, float width, float least = 10)
    {
        while (size > least && Width(text, size) > width * _u)
            size--;
        return size;
    }

    // ---- over the view ----------------------------------------------------------------------------

    /// <summary>How long the sweep of the player's own pulse runs round the edge of the view, seconds.</summary>
    private const float PulseSweep = 0.35f;

    // When the player's own pulse went out, by the HUD's clock.
    private double _pulsed = double.MinValue;

    /// <summary>The player's own geo locator sent out its pulse: the edge of the view lights up with it.</summary>
    public void Pulsed() => _pulsed = _time;

    /// <summary>
    /// In first person, for a third of a second after the player's pulse, amber along the edge of the view that runs in
    /// from it and fades, where from above the ring running out over the ground shows the pulse.
    /// </summary>
    private void DrawPulseSweep()
    {
        var share = (float)(_time - _pulsed) / PulseSweep;
        if (!Driving || share is < 0 or > 1)
            return;
        var depth = Mathf.Lerp(10, 140, Mathf.Sqrt(share)) * _u;
        var (edge, clear) = (new Color(Warning, 0.6f * (1 - share)), new Color(Warning, 0));
        var (left, top, right, bottom) = (Left, 0f, _size.X, _size.Y);
        Color[] fade = [edge, edge, clear, clear];
        DrawPolygon([new(left, top), new(right, top), new(right - depth, top + depth), new(left + depth, top + depth)], fade);
        DrawPolygon([new(right, bottom), new(left, bottom), new(left + depth, bottom - depth), new(right - depth, bottom - depth)], fade);
        DrawPolygon([new(left, bottom), new(left, top), new(left + depth, top + depth), new(left + depth, bottom - depth)], fade);
        DrawPolygon([new(right, top), new(right, bottom), new(right - depth, bottom - depth), new(right - depth, top + depth)], fade);
    }

    /// <summary>
    /// Each enemy a geo locator's pulse has located, in brackets round it where it stands in view, beating in its last two
    /// seconds. A located hero is marked at any distance: its shadow is gone while it is located.
    /// </summary>
    private void DrawLocated()
    {
        var world = _game.World;
        if (GetViewport().GetCamera3D() is not { } camera || world.Wired is not { Located.Count: > 0 } run)
            return;
        var screen = new Rect2(new Vector2(Left, 0), new Vector2(_size.X - Left, _size.Y));
        foreach (var (id, _) in run.Located)
        {
            if (world.Find<Machine>(id) is not { IsDead: false, Inside: null } machine || !_game.LocatedFoe(machine)
                || _game.ViewOf(id) is not { } view || !view.IsVisibleInTree() || Framed(camera, view) is not { } frame || !screen.Intersects(frame))
                continue;
            var left = world.LocatedFor(id);
            var beat = left < 2 ? 0.55f + 0.45f * MathF.Sin((float)_time * 14) : 1;
            Brackets(frame, new Color(Warning, 0.95f * beat));
        }
    }

    /// <summary>Where a view's box stands on the screen, a little larger, and never too small to be seen; null behind the camera.</summary>
    private Rect2? Framed(Camera3D camera, ActorView view)
    {
        if (camera.IsPositionBehind(view.Centre))
            return null;
        var bounds = view.Bounds;
        var (low, high) = (new Vector2(float.MaxValue, float.MaxValue), new Vector2(float.MinValue, float.MinValue));
        for (var i = 0; i < 8; i++)
        {
            var point = view.GlobalTransform * bounds.GetEndpoint(i);
            if (camera.IsPositionBehind(point))
                continue;
            var at = camera.UnprojectPosition(point);
            (low, high) = (new Vector2(Mathf.Min(low.X, at.X), Mathf.Min(low.Y, at.Y)), new Vector2(Mathf.Max(high.X, at.X), Mathf.Max(high.Y, at.Y)));
        }
        if (low.X > high.X)
            return null;
        var size = new Vector2(Mathf.Max(high.X - low.X, 26 * _u), Mathf.Max(high.Y - low.Y, 26 * _u)) + new Vector2(10, 10) * _u;
        return new Rect2((low + high) / 2 - size / 2, size);
    }

    /// <summary>Four corners of a frame, each two short strokes on a dark edge, so that they read on sand as on snow.</summary>
    private void Brackets(Rect2 frame, Color colour)
    {
        var arm = Mathf.Clamp(Mathf.Min(frame.Size.X, frame.Size.Y) * 0.3f, 6 * _u, 30 * _u);
        var width = Mathf.Max(2 * _u, 1);
        var (p, e) = (frame.Position, frame.End);
        foreach (var (corner, x, y) in new[] { (p, 1f, 1f), (new Vector2(e.X, p.Y), -1f, 1f), (e, -1f, -1f), (new Vector2(p.X, e.Y), 1f, -1f) })
        {
            Vector2[] strokes = [corner + new Vector2(0, y * arm), corner, corner + new Vector2(x * arm, 0)];
            DrawPolyline(strokes, new Color(Plating, colour.A * 0.8f), width + 2 * _u, true);
            DrawPolyline(strokes, colour, width, true);
        }
    }

    private static float[] Ring(float x, float y, float radius)
    {
        var points = new float[34];
        for (var i = 0; i <= 16; i++)
        {
            points[i * 2] = x + radius * MathF.Cos(i * MathF.Tau / 16);
            points[i * 2 + 1] = y + radius * MathF.Sin(i * MathF.Tau / 16);
        }
        return points;
    }
}
