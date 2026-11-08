using Godot;
using Machines.Sim.Survival;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// What the HUD of a run says over the view: what is called out, what floats up, the way to the pod, the hangar's prompt and
// the state of the hero.
public partial class WiredHud
{
    /// <summary>What is called out across the view: a title, a line under it, and how long it stays.</summary>
    private sealed record Shout(string Title, string? Line, Color Colour, float Seconds);

    private readonly List<Shout> _calls = [];
    private double _callStarted;

    /// <summary>Something rising from a place in the world, as far as the world allows, with the moment it began.</summary>
    private sealed record Rising(Vector3 Place, string Text, Color Colour, double Since);

    private const double RiseSeconds = 1.5;

    private readonly List<Rising> _rising = [];

    private readonly List<(float Angle, double Since)> _hits = [];

    private string? _refused;
    private double _refusedAt = -10;

    // The plates the prompts took in the frame before, which the pointers keep clear of, and in this one.
    private List<Rect2> _prompted = [], _prompting = [];

    private static readonly Lazy<Texture2D> Edge = new(() => new GradientTexture2D
    {
        Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f),
        FillTo = new Vector2(1.02f, 0.5f),
        Width = 256,
        Height = 256,
        Gradient = new Gradient
        {
            Offsets = [0.55f, 1f],
            Colors = [new Color(1, 1, 1, 0), new Color(1, 1, 1, 1)],
        },
    });

    /// <summary>The wave has begun, with what its boss is called.</summary>
    public void WaveBegins(int wave, string? boss) =>
        Announce(Lang.T("Wave {0}", wave).ToUpperInvariant(), boss is null ? null : Lang.T("Boss: {0}", Lang.T(boss)), boss is null ? Gold : Danger, 3.2f);

    /// <summary>The wave just called out is a special one: the same call says so.</summary>
    public void WaveIsSpecial(WiredSpecial special)
    {
        var last = _calls.Count > 0 ? _calls[^1] : null;
        var line = Warned(special);
        if (last is null || line is null)
            return;
        _calls[^1] = last with { Line = line, Colour = Warning, Seconds = 4.2f };
    }

    /// <summary>What the player has to know of a special wave, in a line.</summary>
    private static string? Warned(WiredSpecial special) => special switch
    {
        WiredSpecial.Night => Lang.T("Night: the scanners reach half as far and aim suffers"),
        WiredSpecial.Storm => Lang.T("Storm: twice as many, fast and light"),
        WiredSpecial.Spies => Lang.T("Spies: mines round the pod and on the way"),
        WiredSpecial.Treason => Lang.T("Treason: a commander comes for your squad"),
        WiredSpecial.AirRaid => Lang.T("Air raid: they come by air only"),
        WiredSpecial.Siege => Lang.T("Siege: Ballistas shell the pod from afar, go out to them"),
        WiredSpecial.IonStorm => Lang.T("Ion storm: get out of the warning circles"),
        _ => null,
    };

    public void WaveCleared(int wave, string bonus) =>
        Announce(Lang.T("Wave {0} cleared", wave).ToUpperInvariant(), Lang.T("Bonus {0} BMU", bonus), MenuTheme.Glow, 3);

    public void BossAppears(string name) => Announce(Lang.T(name).ToUpperInvariant(), Lang.T("Boss on the field"), Danger, 3);

    public void BossCallsForHelp(string name) => Announce(Lang.T(name).ToUpperInvariant(), Lang.T("Below half: a call for help"), Warning, 2.4f);

    public void ChipChoiceWaits() => Announce(Lang.T("Boss down").ToUpperInvariant(), Lang.T("Choose a chip in the hangar"), Gold, 3.4f);

    public void RunEnds(WiredEnding ending) => Announce(Lang.T("Run over").ToUpperInvariant(), WiredEndScreen.Why(ending), Danger, 4);

    private void Announce(string title, string? line, Color colour, float seconds)
    {
        // A long queue would run behind what is going on.
        if (_calls.Count > 2)
            _calls.RemoveAt(1);
        _calls.Add(new Shout(title, line, colour, seconds));
        if (_calls.Count == 1)
            _callStarted = _time;
    }

    private void FollowCalls(double delta)
    {
        if (_calls.Count > 0 && _time - _callStarted > _calls[0].Seconds)
        {
            _calls.RemoveAt(0);
            _callStarted = _time;
        }
        _rising.RemoveAll(r => _time - r.Since > RiseSeconds);
        _hits.RemoveAll(h => _time - h.Since > 0.9);
    }

    /// <summary>Text rising from a place in the world, as a wreck's "+17 BMU".</summary>
    public void Float(Vector3 place, string text, Color? colour = null) => _rising.Add(new Rising(place, text, colour ?? Gold, _time));

    /// <summary>The hero was hit from there: an arc on that side of the sight.</summary>
    public void HitFrom(Vector3 place)
    {
        if (GetViewport().GetCamera3D() is not { } camera)
            return;
        var local = camera.GlobalTransform.AffineInverse() * place;
        _hits.Add((MathF.Atan2(local.X, -local.Z), _time));
        if (_hits.Count > 6)
            _hits.RemoveAt(0);
    }

    /// <summary>What was refused, in the player's language, on a line over the plates, with the sound for it.</summary>
    public void Refuse(string why)
    {
        _refused = why;
        _refusedAt = _time;
        _game.Audio.PlayGui("gui/sounds/clickbad.wav");
    }

    // ---- over the view ----------------------------------------------------------------------------

    /// <summary>The red at the edges of a hero nearly gone, and the arcs towards what hit it.</summary>
    private void DrawDanger(WiredReadout now)
    {
        if (_game.World.HeroOf(Game.Player) is { } hero && Driving)
        {
            var health = hero.HitPoints / (float)Math.Max(1, hero.MaxHitPoints);
            if (health < 0.35f)
            {
                var beat = 0.65f + 0.35f * MathF.Sin((float)_time * (health < 0.15f ? 9 : 5));
                DrawTextureRect(Edge.Value, new Rect2(Vector2.Zero, _size), false, new Color(Danger, (0.35f - health) / 0.35f * 0.8f * beat));
            }
        }
        var (centre, radius) = (_size / 2, 250 * _u);
        foreach (var (angle, since) in _hits)
        {
            var strength = Fading(since, 0.9);
            var from = angle - Mathf.Pi / 2 - 0.3f;
            DrawArc(centre, radius, from, from + 0.6f, 20, new Color(Danger, strength * 0.9f), 7 * _u, true);
        }
    }

    /// <summary>Where to go: the pod, by a mark on it or an arrow at the edge of the view, and in an ion strike's circle the way out.</summary>
    private void DrawMarkers(WiredReadout now)
    {
        if (GetViewport().GetCamera3D() is not { } camera)
            return;
        var world = _game.World;
        if (world.WiredPod(Game.Player) is { } pod && world.HeroOf(Game.Player) is not null)
        {
            var struck = Fading(_podStruck, 1.4) > 0;
            var strong = now.BetweenWaves || struck;
            var colour = struck ? Danger : now.BetweenWaves ? MenuTheme.Glow : MenuTheme.Text;
            var label = now.PodDistance is { } distance ? $"POD {Mathf.RoundToInt(distance)} m" : "POD";
            Pointer(camera, ActorView.World(pod.Position) + Vector3.Up * 14, colour, label, strong);
        }
        if (world.Wired is { } run && now.InStrike && world.HeroOf(Game.Player) is { } hero)
        {
            var strike = run.Strikes.Where(s => NVector2.Distance(s.Position, hero.Position) <= s.Radius)
                .OrderBy(s => s.At).First();
            var away = hero.Position - strike.Position;
            away = away.LengthSquared() < 1 ? NVector2.UnitX : NVector2.Normalize(away);
            Pointer(camera, ActorView.World(hero.Position + away * 200), Danger, Lang.T("Out!"), true);
        }
    }

    /// <summary>A mark on a place in view, or an arrow on the ring round the sight towards one that is not.</summary>
    private void Pointer(Camera3D camera, Vector3 place, Color colour, string label, bool strong)
    {
        var margin = 90 * _u;
        var area = new Rect2(new Vector2(Left + margin, margin * 1.4f), _size - new Vector2(Left + margin * 2, margin * 3.2f));
        var beat = strong ? 0.8f + 0.2f * MathF.Sin((float)_time * 6) : 0.75f;
        var tint = new Color(colour, beat);
        // What can be seen gets its mark right up to the edges of the view, short of the plates, the radar and the panels.
        var view = new Rect2(new Vector2(Left + 16 * _u, margin * 0.6f), new Vector2(_size.X - Left - 32 * _u, _size.Y - margin * 0.6f - (Rise + 16 + (Driving ? 0 : PromptLine)) * _u));
        var front = !camera.IsPositionBehind(place);
        var at = front ? camera.UnprojectPosition(place) : Vector2.Zero;
        var r = (strong ? 11 : 8) * _u;
        if (front && view.HasPoint(at) && !Kept(plates: false).Any(k => k.Grow(r + 26 * _u).HasPoint(at)))
        {
            DrawPolyline([at + new Vector2(0, -r), at + new Vector2(r, 0), at + new Vector2(0, r), at + new Vector2(-r, 0), at + new Vector2(0, -r)], tint, 2 * _u, true);
            Tag(at + new Vector2(0, -r - 14 * _u), label, tint);
            return;
        }
        var local = camera.GlobalTransform.AffineInverse() * place;
        var angle = MathF.Atan2(local.X, -local.Z);
        var middle = area.GetCenter();
        var way = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
        // Looking down on the field the way on the screen is the way to go; a driver looks along the ground, and goes by the bearing.
        if (!Driving && front)
            way = (at - middle).Normalized();
        var tip = middle + way * area.Size / 2;
        var size = (strong ? 24 : 17) * _u;
        var room = size + Width(label, 15) / 2 + 22 * _u;
        tip = Clear(tip, Kept(plates: true).Select(k => k.Grow(room)).ToList());
        var side = new Vector2(-way.Y, way.X);
        var back = tip - way * size;
        Vector2[] arrow = [tip, back + side * size * 0.6f, back + way * size * 0.3f, back - side * size * 0.6f];
        DrawColoredPolygon(arrow, tint);
        DrawPolyline([.. arrow, tip], new Color(Plating, 1), 2 * _u, true);
        Tag(back - way * (Width(label, 15) / 2 + 14 * _u) * new Vector2(1, 0.4f), label, tint);
    }

    /// <summary>What a pointer keeps clear of: the radar, the weapons' panels in first person and, with <paramref name="plates"/>, the prompts' plates of the frame before.</summary>
    private List<Rect2> Kept(bool plates)
    {
        var kept = new List<Rect2>();
        if (RadarCorner() is { } radar)
            kept.Add(radar);
        if (_hud.Weapons.Visible && _hud.Weapons.Area.HasArea())
            kept.Add(_hud.Weapons.Area);
        if (plates)
            kept.AddRange(_prompted);
        return kept;
    }

    /// <summary>
    /// A point in one of <paramref name="kept"/> moved out of it the shortest way that stays on the screen and, where it can,
    /// out of the others too.
    /// </summary>
    private Vector2 Clear(Vector2 point, List<Rect2> kept)
    {
        var screen = new Rect2(Vector2.Zero, _size);
        for (var moves = 0; moves < 4 && kept.FindIndex(k => k.HasPoint(point)) is >= 0 and var i; moves++)
        {
            var k = kept[i];
            Vector2[] ways = [new(k.Position.X - 1, point.Y), new(k.End.X, point.Y), new(point.X, k.Position.Y - 1), new(point.X, k.End.Y)];
            var exits = ways.Where(screen.HasPoint).ToList();
            if (exits.Count == 0)
                break;
            var free = exits.Where(p => !kept.Any(o => o.HasPoint(p))).ToList();
            var from = point;
            point = (free.Count > 0 ? free : exits).MinBy(p => p.DistanceSquaredTo(from));
        }
        return point;
    }

    /// <summary>The corner the radar takes, while it is there.</summary>
    private Rect2? RadarCorner() => _hud.Radar.Visible
        ? new Rect2(_size - new Vector2(_hud.Radar.ScreenWidth, _hud.Radar.ScreenHeight), new Vector2(_hud.Radar.ScreenWidth, _hud.Radar.ScreenHeight))
        : null;

    /// <summary>A pointer's word on a dark ground, so that it reads on sand as on snow, whole in the view.</summary>
    private void Tag(Vector2 middle, string label, Color tint)
    {
        var half = new Vector2(Width(label, 15) / 2 + 8 * _u, 12 * _u);
        middle = new Vector2(Mathf.Clamp(middle.X, Left + half.X, _size.X - half.X), Mathf.Clamp(middle.Y, half.Y, _size.Y - half.Y));
        DrawRect(new Rect2(middle - half, half * 2), new Color(Plating, 0.72f));
        Say(middle + new Vector2(0, 5.5f * _u), label, 15, new Color(tint, 1), 0.5f);
    }

    /// <summary>What rises from pickups: from the place while it is in view, otherwise from under the sight, each below those before it.</summary>
    private void DrawFloats()
    {
        var camera = GetViewport().GetCamera3D();
        var taken = new List<Rect2>();
        foreach (var rising in _rising)
        {
            var age = (float)((_time - rising.Since) / RiseSeconds);
            var start = _size / 2 + new Vector2(0, 150 * _u);
            if (camera is not null && !camera.IsPositionBehind(rising.Place) && camera.UnprojectPosition(rising.Place) is var seen
                && new Rect2(Vector2.Zero, _size).Grow(-60 * _u).HasPoint(seen))
                start = seen;
            var at = start + new Vector2(0, -100 * (1 - (1 - age) * (1 - age)) * _u);
            var width = Width(rising.Text, 30);
            var box = new Rect2(at + new Vector2(-width / 2, -26 * _u), new Vector2(width, 34 * _u));
            foreach (var other in taken.OrderBy(r => r.Position.Y))
            {
                if (other.Intersects(box))
                    box.Position = new Vector2(box.Position.X, other.End.Y);
            }
            taken.Add(box);
            var alpha = age < 0.7f ? 1 : (1 - age) / 0.3f;
            Say(new Vector2(at.X, box.Position.Y + 26 * _u), rising.Text, 30, new Color(rising.Colour, alpha), 0.5f, outline: 2);
        }
    }

    /// <summary>Under the sight: the hangar where it opens, the way there between waves, and the warning of a strike.</summary>
    private void DrawPrompts(WiredReadout now)
    {
        var middle = Left + (_size.X - Left) / 2;
        var y = _size.Y * 0.7f;
        if (_game.Watching)
        {
            // A replay only looks on: there is no hangar to go to.
        }
        else if (now.HangarHere)
        {
            var text = Lang.T("Open hangar ({0})", KeyMap.Name(KeyMap.Act.Hangar));
            Prompt(new Vector2(middle, y), text, now.ChipChoice ? Gold : MenuTheme.Glow, 26);
            if (now.ChipChoice)
                Prompt(new Vector2(middle, y + 56 * _u), Lang.T("A chip is waiting to be chosen"), Gold, 18);
        }
        else if (now.BetweenWaves && _game.World.HeroOf(Game.Player) is not null && now.Seconds > 0)
            Prompt(new Vector2(middle, y), Lang.T("Back to the pod for the hangar"), MenuTheme.Text, 20);

        if (now.StrikeIn is { } strike)
        {
            var left = Mathf.CeilToInt(strike);
            var at = new Vector2(middle, (WavePlateHeight + 90 + (now.Bosses.Count > 0 ? 46 : 0)) * _u);
            if (now.InStrike)
            {
                var beat = (MathF.Sin((float)_time * 16) + 1) / 2;
                Prompt(at, Lang.T("Ion strike in {0} s: get out of the circle", left), Danger.Lerp(Colors.White, beat * 0.5f), 28);
            }
            else
                Prompt(at, Lang.T("Ion strike in {0} s", left), Warning, 22);
        }

        if (_refused is { } refused && Fading(_refusedAt, 2.6) is > 0 and var alpha)
            Prompt(new Vector2(middle, _size.Y - (Rise + 84) * _u), refused, new Color(Danger.Lerp(Colors.White, 0.2f), Mathf.Min(alpha * 2, 1)), 20);
    }

    private void Prompt(Vector2 at, string text, Color colour, float size)
    {
        var width = Width(text, size) + 40 * _u;
        var height = (size + 20) * _u;
        var box = new Rect2(at - new Vector2(width / 2, 0), new Vector2(width, height));
        _prompting.Add(box);
        Plate(box, new Color(Plating, Plating.A * colour.A), colour, 10);
        Say(box.GetCenter() + new Vector2(0, size * 0.36f * _u), text, size, colour, 0.5f);
    }

    /// <summary>The call under way: a band across the view, its title and a line, coming in from the middle.</summary>
    private void DrawCall()
    {
        if (_calls.Count == 0)
            return;
        var call = _calls[0];
        var age = (float)(_time - _callStarted);
        var opening = Mathf.Clamp(age / 0.22f, 0, 1);
        var alpha = opening * Mathf.Clamp((call.Seconds - age) / 0.45f, 0, 1);
        var middle = Left + (_size.X - Left) / 2;
        var y = _size.Y * 0.28f;
        var width = (_size.X - Left) * 0.42f * (1 - (1 - opening) * (1 - opening));
        var height = (call.Line is null ? 92 : 128) * _u;
        // Dark across the middle and clear towards the ends, to read on any ground.
        var left = new Color(0, 0, 0, 0);
        var dark = new Color(0, 0, 0, 0.62f * alpha);
        DrawPolygon([new Vector2(middle - width, y), new Vector2(middle, y), new Vector2(middle, y + height), new Vector2(middle - width, y + height)], [left, dark, dark, left]);
        DrawPolygon([new Vector2(middle, y), new Vector2(middle + width, y), new Vector2(middle + width, y + height), new Vector2(middle, y + height)], [dark, left, left, dark]);
        var tint = new Color(call.Colour, alpha);
        DrawLine(new Vector2(middle - width, y), new Vector2(middle + width, y), tint, 2 * _u);
        DrawLine(new Vector2(middle - width, y + height), new Vector2(middle + width, y + height), tint, 2 * _u);
        Say(new Vector2(middle, y + 66 * _u), call.Title, 58, new Color(Colors.White, alpha), 0.5f, outline: 2);
        if (call.Line is { } line)
            Say(new Vector2(middle, y + 106 * _u), Fit(line, 25, (_size.X - Left) / _u * 0.8f), 25, tint, 0.5f, outline: 2);
    }

    /// <summary>The hero is down: over the static, how long until the next body and what is left.</summary>
    private void DrawDown(WiredReadout now)
    {
        if (now.DownSeconds is not { } seconds)
            return;
        var middle = Left + (_size.X - Left) / 2;
        var box = new Rect2(middle - 300 * _u, _size.Y * 0.5f - 96 * _u, 600 * _u, 250 * _u);
        var beat = 1 - (float)(seconds % 1);
        Plate(box, new Color(0, 0, 0, 0.82f), Danger, 16);
        Say(new Vector2(middle, box.Position.Y + 50 * _u), Lang.T("Body lost").ToUpperInvariant(), 36, Danger, 0.5f);
        Say(new Vector2(middle, box.Position.Y + 150 * _u), Mathf.CeilToInt(seconds).ToString(), 96 + beat * 10, Colors.White, 0.5f);
        Say(new Vector2(middle, box.Position.Y + 190 * _u), Lang.T("The next body stands at the pod"), 22, MenuTheme.Text, 0.5f);
        Say(new Vector2(middle, box.Position.Y + 224 * _u), Lang.T("Spare bodies left: {0}", now.SpareBodies), 19, MenuTheme.Dim, 0.5f);
    }
}
