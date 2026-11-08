using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

public static partial class HangarText
{
    /// <summary>The word for a special wave; null for an ordinary one.</summary>
    public static string? Special(WiredSpecial special) => special switch
    {
        WiredSpecial.Night => Lang.T("Night"),
        WiredSpecial.Storm => Lang.T("Storm"),
        WiredSpecial.Spies => Lang.T("Spies"),
        WiredSpecial.Treason => Lang.T("Treason"),
        WiredSpecial.AirRaid => Lang.T("Air raid"),
        WiredSpecial.Siege => Lang.T("Siege"),
        WiredSpecial.IonStorm => Lang.T("Ion storm"),
        _ => null,
    };
}

/// <summary>
/// The screen of a Wired run, in the black bars of <see cref="FirstPersonScreen"/> and on plates rising out of them: the wave and
/// its kind, the enemies left or the countdown, the bank, the bodies left, the pod, the bosses, abilities, thrusters, chips
/// and the squad, and over the view what is called out, the way to the pod and what floats up from a pickup. It lies over
/// the goggles' filter, so it reads the same with them on, and everything it says comes from <see cref="WiredReadout"/>.
/// Beside the control panel, out of first person, it moves over. Wired is the remake's own addition.
/// </summary>
public partial class WiredHud : Control
{
    // Colours of the run's own, beside the menus'.
    private static readonly Color Danger = new(1f, 0.26f, 0.16f), Warning = new(1f, 0.7f, 0.2f), Ice = new(0.48f, 0.8f, 1f), Gold = new(1f, 0.84f, 0.36f);
    private static readonly Color Plating = new(0.006f, 0.028f, 0.012f, 0.9f), Sheen = new(0.03f, 0.085f, 0.04f, 0.9f);

    // Sizes in pixels of a window 1080 high: the bars' height, how far the plates rise above the lower one, the top plate.
    private const float Bar = 54, WavePlateWidth = 430, WavePlateHeight = 110, Rise = 96, Gap = 14;

    // The line along the bottom that tells what is pointed at, out of first person.
    private const float PromptLine = 36;

    private readonly Game _game;
    private readonly Hud _hud;
    private WiredReadout? _now;
    private Vector2 _size;
    private float _u = 1;
    private double _time;

    // What is shown as it follows the figures: the bank counting up and the bars' trails.
    private float _bank;
    private double _bankRose = -10, _bankFell = -10, _enemyFell = -10, _podStruck = -10;
    private float _podTrail = 1;
    private int _enemiesSeen;
    private bool _fresh = true;
    private int _lastTick = -1;
    private readonly Dictionary<int, float> _bossTrail = [];
    private readonly Dictionary<int, double> _bossStruck = [];
    private readonly Dictionary<int, double> _readyAt = [];
    private bool _dashWasReady = true;
    private double _dashReadyAt = -10;

    // The squad's orders as the strip's legend has them, with the signs the cards use.
    private static readonly (KeyMap.Act Act, EscortDoing Sign)[] Orders =
    [
        (KeyMap.Act.SquadFollow, EscortDoing.Following), (KeyMap.Act.SquadAttack, EscortDoing.Attacking),
        (KeyMap.Act.SquadHold, EscortDoing.Holding), (KeyMap.Act.SquadBack, EscortDoing.Moving),
    ];

    private KeyMap.Act? _orderGiven;
    private double _orderGivenAt = -10;

    /// <summary>Where the view starts, right of the control panel.</summary>
    public float Left { get; set; }

    /// <summary>Whether the hero is being driven: the plates sit in the bars then, and the view's call-outs make room for the sight.</summary>
    public bool Driving { get; set; }

    /// <summary>How far down the wave's plate reaches, the bosses' bars under it included: the messages go below.</summary>
    public float Hanging => (WavePlateHeight + (_now is { Bosses.Count: > 0 } ? 8 + 40 : 0) + Gap) * _u;

    public WiredHud(Game game, Hud hud)
    {
        (_game, _hud) = (game, hud);
        Name = "WiredHud";
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready() => SetAnchorsPreset(LayoutPreset.FullRect);

    public override void _Process(double delta)
    {
        _time += delta;
        _now = WiredReadout.Of(_game.World, Game.Player);
        _size = GetViewportRect().Size;
        _u = Mathf.Max(_size.Y / 1080f, 0.6f);
        if (_now is { } now)
            Follow(now, (float)delta);
        FollowCalls(delta);
        QueueRedraw();
    }

    /// <summary>What has changed since the last frame shows: the bank counts, bars leave a trail, the last seconds tick.</summary>
    private void Follow(WiredReadout now, float delta)
    {
        // What the first figures say is how things stand, at a start or from a save: nothing has happened yet.
        if (_fresh)
        {
            _fresh = false;
            (_bank, _podTrail, _enemiesSeen) = (now.Bank, now.Pod ?? 0, now.EnemiesLeft);
        }
        if (now.Bank > _bank + 0.5f && _bankRose < _time - 0.2)
            _bankRose = _time;
        if (now.Bank < _bank - 0.5f)
            _bankFell = _time;
        _bank = Mathf.MoveToward(_bank, now.Bank, Mathf.Max(Mathf.Abs(now.Bank - _bank) * 6, 30) * delta);

        if (now.BetweenWaves)
        {
            if (now.Seconds != _lastTick && now.Seconds is > 0 and <= 5)
                _game.Audio.PlayGui("gui/sounds/navkey.wav");
            _lastTick = now.Seconds;
        }
        else
        {
            if (now.EnemiesLeft < _enemiesSeen)
                _enemyFell = _time;
            _lastTick = -1;
        }
        _enemiesSeen = now.EnemiesLeft;

        var pod = now.Pod ?? 0;
        if (pod < _podTrail - 0.0005f)
            _podStruck = _time;
        _podTrail = Mathf.Max(pod, Mathf.MoveToward(_podTrail, pod, 0.25f * delta));

        foreach (var boss in now.Bosses)
        {
            var trail = _bossTrail.GetValueOrDefault(boss.Id, boss.Health);
            if (boss.Health < trail - 0.001f && _bossStruck.GetValueOrDefault(boss.Id) < _time - 0.12)
                _bossStruck[boss.Id] = _time;
            _bossTrail[boss.Id] = Mathf.Max(boss.Health, Mathf.MoveToward(trail, boss.Health, 0.3f * delta));
        }

        foreach (var ability in now.Abilities)
        {
            if (ability.Ready && !_readyAt.ContainsKey(ability.Slot))
                _readyAt[ability.Slot] = _time;
            else if (!ability.Ready)
                _readyAt.Remove(ability.Slot);
        }
        var dashReady = now.Dash is { Ready: true };
        if (dashReady && !_dashWasReady)
            _dashReadyAt = _time;
        _dashWasReady = dashReady;
    }

    public override void _Draw()
    {
        if (_now is not { } now || _size == Vector2.Zero)
            return;
        (_prompted, _prompting) = (_prompting, _prompted);
        _prompting.Clear();
        DrawDanger(now);
        DrawMarkers(now);
        DrawFloats();
        DrawPrompts(now);
        DrawCall();
        DrawHero(now);
        DrawBank(now);
        DrawWave(now);
        DrawBosses(now);
        DrawFoot(now);
        DrawDown(now);
    }

    // ---- the top bar ------------------------------------------------------------------------------

    /// <summary>The hero's name and the bodies left, at the top left of the view.</summary>
    private void DrawHero(WiredReadout now)
    {
        var options = _game.World.Wired!.Options;
        var at = new Vector2(Left + 16 * _u, 6 * _u);
        var box = new Rect2(at, new Vector2(330, 44) * _u);
        Plate(box, Plating, MenuTheme.Edge, 8);
        var name = Fit((options.HeroName is { Length: > 0 } given ? given : Lang.T("Hero")).ToUpperInvariant(), 21, 300);
        Say(at + new Vector2(12, 21) * _u, name, 21, MenuTheme.Text);
        var bodies = now.BodiesAtStart == 0 ? Lang.T("No spare body") : Lang.T("Spare bodies");
        var low = now.SpareBodies == 0 && now.DownSeconds is null;
        Say(at + new Vector2(12, 38) * _u, bodies, 14, low ? Warning : MenuTheme.Dim);
        var x = at.X + (12 * _u) + Width(bodies, 14) + 10 * _u;
        for (var i = 0; i < now.BodiesAtStart; i++)
        {
            var pip = new Rect2(new Vector2(x + i * 17 * _u, at.Y + 27 * _u), new Vector2(12, 12) * _u);
            if (i < now.SpareBodies)
                Plate(pip, MenuTheme.Glow, MenuTheme.Glow, 3);
            else
                Plate(pip, Plating, MenuTheme.Edge, 3);
        }
    }

    /// <summary>The bank, counting up to what it holds, and the score, at the top right.</summary>
    private void DrawBank(WiredReadout now)
    {
        var box = new Rect2(new Vector2(_size.X - 316 * _u, 6 * _u), new Vector2(300, 44) * _u);
        var rise = Fading(_bankRose, 0.5);
        var fall = Fading(_bankFell, 1.2);
        Plate(box, Plating, fall > 0 ? Danger : rise > 0 ? Gold : MenuTheme.Edge, 8);
        var right = box.End.X - 12 * _u;
        var colour = fall > 0 ? Danger : MenuTheme.Bright.Lerp(Colors.White, rise * 0.8f);
        Say(new Vector2(right, box.Position.Y + 24 * _u), $"{Thousands(Mathf.RoundToInt(_bank))} BMU", 24 + rise * 2, colour, 1);
        Say(new Vector2(right, box.Position.Y + 39 * _u), Lang.T("Score {0}", Thousands(now.Score)), 14, MenuTheme.Dim, 1);
    }

    /// <summary>The plate hanging from the top bar: what comes next and when between waves, what is left of it during one.</summary>
    private void DrawWave(WiredReadout now)
    {
        var middle = Left + (_size.X - Left) / 2;
        var (width, height) = (WavePlateWidth * _u, WavePlateHeight * _u);
        var box = new Rect2(middle - width / 2, 0, width, height);
        var boss = now.BossName is not null;
        var kind = HangarText.Special(now.Special);
        var accent = boss ? Danger : kind is not null ? Warning : now.BetweenWaves ? MenuTheme.Glow : Gold;
        var calm = now.BetweenWaves && now.Seconds > 5;
        var edge = calm ? MenuTheme.Edge : accent;
        var points = Corners(box, 0, 0, 22, 22);
        Plate(points, Plating, edge, 2);

        var title = (now.BetweenWaves ? Lang.T("Next wave {0}", now.Wave) : Lang.T("Wave {0}", now.Wave)).ToUpperInvariant();
        var tag = boss ? Lang.T("Boss: {0}", Lang.T(now.BossName!)) : kind;
        var (top, centre) = (24 * _u, new Vector2(middle, 0));
        if (tag is null)
            Say(centre + new Vector2(0, top), title, 21, MenuTheme.Text, 0.5f);
        else
        {
            // A long name brings the line down in size before it loses any of it.
            tag = tag.ToUpperInvariant();
            var size = 21f;
            while (size > 13 && Width(title, size) + Width(tag, size) + 24 * _u > 390 * _u)
                size--;
            tag = Fit(tag, size, 390 - Width(title, size) / _u - 24);
            var (first, second) = (Width(title, size), Width(tag, size));
            var left = middle - (first + 24 * _u + second) / 2;
            Say(new Vector2(left, top), title, size, MenuTheme.Text);
            DrawCircle(new Vector2(left + first + 12 * _u, top - 7 * _u), 3 * _u, accent);
            Say(new Vector2(left + first + 24 * _u, top), tag, size, accent);
        }

        if (now.BetweenWaves)
            DrawClock(now, middle, box, accent);
        else
            DrawEnemies(now, middle, box, accent);
    }

    private void DrawClock(WiredReadout now, float middle, Rect2 box, Color accent)
    {
        var line = box.End.Y - 8 * _u;
        if (now.Seconds == 0)
        {
            Say(new Vector2(middle, box.End.Y - 34 * _u), Lang.T("The wave is on its way").ToUpperInvariant(), 24, accent, 0.5f);
            return;
        }
        var last = now.Seconds <= 5;
        // The last seconds beat, each swelling and sinking back.
        var beat = last ? 1 - (float)(_time % 1) : 0;
        var colour = last ? Danger.Lerp(Colors.White, beat * 0.35f) : MenuTheme.Text;
        Say(new Vector2(middle, box.Position.Y + 79 * _u), $"{now.Seconds / 60}:{now.Seconds % 60:00}", 52 + beat * 8, colour, 0.5f, outline: last ? 3 : 0);
        Thin(new Rect2(box.Position.X + 26 * _u, line, box.Size.X - 52 * _u, 3 * _u), Mathf.Min(1, now.Seconds / (float)Math.Max(now.WaitLength, 1)), last ? Danger : accent);
    }

    private void DrawEnemies(WiredReadout now, float middle, Rect2 box, Color accent)
    {
        var fell = Fading(_enemyFell, 0.35);
        var number = now.EnemiesLeft.ToString();
        var label = Lang.T("enemies left");
        var (first, second) = (Width(number, 52), Width(label, 18));
        var left = middle - (first + 12 * _u + second) / 2;
        Say(new Vector2(left, box.Position.Y + 79 * _u), number, 52, MenuTheme.Text.Lerp(Colors.White, fell), outline: 0);
        Say(new Vector2(left + first + 12 * _u, box.Position.Y + 76 * _u), label, 18, MenuTheme.Text);
        Thin(new Rect2(box.Position.X + 26 * _u, box.End.Y - 8 * _u, box.Size.X - 52 * _u, 3 * _u), Mathf.Min(1, now.EnemiesLeft / (float)Math.Max(now.MostEnemies, 1)), accent);
    }

    /// <summary>The bosses' bars under the wave's plate, side by side when there are two.</summary>
    private void DrawBosses(WiredReadout now)
    {
        if (now.Bosses.Count == 0)
            return;
        var middle = Left + (_size.X - Left) / 2;
        var total = Mathf.Min(760 * _u, _size.X - Left - 40 * _u);
        var gap = 14 * _u;
        var width = (total - gap * (now.Bosses.Count - 1)) / now.Bosses.Count;
        for (var i = 0; i < now.Bosses.Count; i++)
        {
            var boss = now.Bosses[i];
            var struck = Fading(_bossStruck.GetValueOrDefault(boss.Id, -10), 0.16);
            var shake = struck > 0 ? new Vector2(MathF.Sin((float)_time * 90) * 3 * struck * _u, 0) : Vector2.Zero;
            var box = new Rect2(middle - total / 2 + i * (width + gap), (WavePlateHeight + 8) * _u, width, 40 * _u);
            box.Position += shake;
            Plate(box, Plating, Danger.Lerp(Colors.White, struck * 0.7f), 8);
            var bar = new Rect2(box.Position + new Vector2(10, 22) * _u, new Vector2(box.Size.X - 20 * _u, 11 * _u));
            DrawRect(bar, new Color(0.14f, 0.02f, 0.01f, 0.95f));
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * _bossTrail.GetValueOrDefault(boss.Id, boss.Health), bar.Size.Y)), new Color(1, 0.86f, 0.74f, 0.85f));
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * boss.Health, bar.Size.Y)), Danger);
            // Half way a call for help goes out: the mark says when.
            DrawRect(new Rect2(bar.Position + new Vector2(bar.Size.X * 0.5f - _u, 0), new Vector2(2 * _u, bar.Size.Y)), new Color(0, 0, 0, 0.75f));
            DrawRect(new Rect2(bar.Position + new Vector2(0, bar.Size.Y + _u), new Vector2(bar.Size.X * boss.Armour, 2 * _u)), Ice);
            Say(box.Position + new Vector2(12, 17) * _u, Fit(Lang.T(boss.Name).ToUpperInvariant(), 16, box.Size.X / _u - 80), 16, Colors.White);
            Say(box.Position + new Vector2(box.Size.X - 12 * _u, 17 * _u), $"{Mathf.CeilToInt(boss.Health * 100)} %", 15, MenuTheme.Text, 1);
        }
    }

    // ---- the plates out of the lower bar ----------------------------------------------------------

    private readonly record struct Block(float Width, Action<Vector2> Draw);

    /// <summary>
    /// The pod, the abilities, the chips and the squad, side by side left of the radar; what no longer fits there goes on a
    /// row above.
    /// </summary>
    private void DrawFoot(WiredReadout now)
    {
        var blocks = new List<Block> { new(Foot(300), at => DrawPod(now, at)) };
        foreach (var ability in now.Abilities)
            blocks.Add(new Block(Foot(Rise), at => DrawAbility(ability, at)));
        if (now.Dash is { } dash)
            blocks.Add(new Block(Foot(Rise), at => DrawDash(dash, at)));
        if (now.Chips.Count > 0)
            blocks.Add(new Block(Foot(ChipsWidth), at => DrawChips(now, at)));
        if (now.Squad.Count > 0)
            blocks.Add(new Block(Foot(SquadWidth(now)), at => DrawSquad(now, at)));

        // The radar's backdrop takes this much of the corner in first person; out of it the bars go across.
        var end = _hud.Radar.Visible ? _size.X - _hud.Radar.ScreenWidth - Gap * _u : _size.X - 16 * _u;
        var (x, y) = (Left + 16 * _u, _size.Y - (Rise + 8 + (Driving ? 0 : PromptLine)) * _u);
        foreach (var block in blocks)
        {
            if (x + block.Width > end && x > Left + 16 * _u)
                (x, y) = (Left + 16 * _u, y - (Rise + 8) * _u);
            block.Draw(new Vector2(x, y));
            x += block.Width + Gap * _u;
        }
    }

    private float Foot(float width) => width * _u;

    private void DrawPod(WiredReadout now, Vector2 at)
    {
        var box = new Rect2(at, new Vector2(300, Rise) * _u);
        var struck = now.Pod is null ? 0 : Fading(_podStruck, 1.4);
        var hurt = now.Pod is null or < 0.3f;
        var beat = struck > 0 ? (MathF.Sin((float)_time * 14) + 1) / 2 : 0;
        var edge = struck > 0 ? MenuTheme.Edge.Lerp(Danger, 0.4f + 0.6f * beat) : hurt ? Danger : MenuTheme.Edge;
        Plate(box, Plating, edge, 10);
        var glyph = new Rect2(at + new Vector2(10, 12) * _u, new Vector2(44, 44) * _u);
        Glyph(HangarGlyphs.All.GetValueOrDefault("POD") ?? PodGlyph, glyph, struck > 0 ? Danger : MenuTheme.Glow);
        var share = now.Pod ?? 0;
        var line = at + new Vector2(66, 30) * _u;
        Say(line, "POD", 20, MenuTheme.Text);
        Say(at + new Vector2(286, 30) * _u, now.Pod is null ? Lang.T("gone") : $"{Mathf.Max(1, Mathf.FloorToInt(share * 100))} %", 22, hurt ? Danger : MenuTheme.Bright, 1);
        var bar = new Rect2(at + new Vector2(66, 40) * _u, new Vector2(220, 15) * _u);
        DrawRect(bar, MenuTheme.Well);
        DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * _podTrail, bar.Size.Y)), new Color(1, 0.86f, 0.74f, 0.8f));
        DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * share, bar.Size.Y)), hurt ? Danger : share < 0.6f ? Warning : MenuTheme.Glow);
        DrawRect(bar, MenuTheme.Edge, false, 1);
        if (struck > 0)
            Say(at + new Vector2(66, 76) * _u, Lang.T("Under attack").ToUpperInvariant(), 16, Danger.Lerp(Colors.White, beat * 0.5f));
        else if (now.PodDistance is { } distance)
            Say(at + new Vector2(66, 76) * _u, Lang.T("{0} m away", Mathf.RoundToInt(distance)), 15, MenuTheme.Dim);
    }

    private void DrawAbility(AbilityReadout ability, Vector2 at)
    {
        var box = new Rect2(at, new Vector2(Rise, Rise) * _u);
        var flash = Fading(_readyAt.GetValueOrDefault(ability.Slot, -10), 0.6);
        Plate(box, Plating, ability.Ready ? MenuTheme.Glow.Lerp(Colors.White, flash) : MenuTheme.Edge, 10);
        var inner = box.Grow(-14 * _u);
        Glyph(HangarGlyphs.Of(WiredKind.Ability, ability.Catalogue), inner, ability.Ready ? MenuTheme.Glow : MenuTheme.Dim);
        if (!ability.Ready)
        {
            Sweep(box.Grow(-4 * _u), ability.Share);
            var left = ability.WavesLeft > 0 ? Lang.T("{0} W", ability.WavesLeft) : $"{Mathf.CeilToInt(ability.SecondsLeft)}";
            Say(box.GetCenter() + new Vector2(0, 11) * _u, left, 30, Colors.White, 0.5f, outline: 3);
        }
        if (flash > 0)
            DrawRect(box.Grow(flash * 6 * _u), new Color(MenuTheme.Glow, flash * 0.7f), false, 2 * _u);
        Cap(box, KeyOf(ability.Slot));
    }

    private void DrawDash(DashReadout dash, Vector2 at)
    {
        var box = new Rect2(at, new Vector2(Rise, Rise) * _u);
        var flash = Fading(_dashReadyAt, 0.6);
        var colour = dash.Firing ? Colors.White : dash.Ready ? Ice : MenuTheme.Dim;
        Plate(box, Plating, dash.Ready ? Ice.Lerp(Colors.White, flash) : MenuTheme.Edge, 10);
        // Three chevrons, one way.
        var inner = box.Grow(-22 * _u);
        for (var i = 0; i < 3; i++)
        {
            var x = inner.Position.X + inner.Size.X * (0.1f + 0.3f * i);
            var tip = inner.Size.X * 0.3f;
            DrawPolyline([new Vector2(x, inner.Position.Y), new Vector2(x + tip, inner.GetCenter().Y), new Vector2(x, inner.End.Y)], colour, 3 * _u, true);
        }
        if (!dash.Ready)
        {
            Sweep(box.Grow(-4 * _u), dash.Share);
            Say(box.GetCenter() + new Vector2(0, 11) * _u, $"{Mathf.CeilToInt(dash.SecondsLeft)}", 30, Colors.White, 0.5f, outline: 3);
        }
        if (flash > 0)
            DrawRect(box.Grow(flash * 6 * _u), new Color(Ice, flash * 0.7f), false, 2 * _u);
        Cap(box, KeyOf(-1));
    }

    private const float ChipSize = 30, ChipGap = 6;
    private const float ChipsWidth = 3 * ChipSize + 2 * ChipGap + 20;

    /// <summary>The six sockets, a light each: dim in the chip's colour, bright while it is at work.</summary>
    private void DrawChips(WiredReadout now, Vector2 at)
    {
        var box = new Rect2(at, new Vector2(ChipsWidth, Rise) * _u);
        Plate(box, Plating, MenuTheme.Edge, 10);
        Say(at + new Vector2(ChipsWidth / 2, 20) * _u, Lang.T("Chips").ToUpperInvariant(), 13, MenuTheme.Dim, 0.5f);
        for (var i = 0; i < WiredBalance.ChipSockets; i++)
        {
            var place = at + new Vector2(10 + i % 3 * (ChipSize + ChipGap), 26 + i / 3 * (ChipSize + ChipGap)) * _u;
            var socket = new Rect2(place, new Vector2(ChipSize, ChipSize) * _u);
            if (i >= now.Chips.Count)
            {
                Plate(socket, MenuTheme.Well, new Color(MenuTheme.Edge, 0.5f), 5);
                continue;
            }
            var chip = now.Chips[i];
            var colour = HangarText.FamilyColour(chip.Family);
            if (chip.Lit)
            {
                var pulse = 0.75f + 0.25f * MathF.Sin((float)_time * 8);
                Plate(socket.Grow(3 * _u), new Color(colour, 0.25f * pulse), new Color(colour, 0.6f), 7);
                Plate(socket, new Color(colour, pulse), Colors.White, 5);
            }
            else
                Plate(socket, new Color(colour, 0.3f), new Color(colour, 0.7f), 5);
        }
    }

    private const float CardWidth = 66, CardGap = 5;

    private static float SquadWidth(WiredReadout now) => 16 + Math.Max(now.SquadLimit, now.Squad.Count) * (CardWidth + CardGap);

    /// <summary>The escorts as cards: the kind of machine and its level, its health and what it is about; a free place stays empty.</summary>
    private void DrawSquad(WiredReadout now, Vector2 at)
    {
        var count = Math.Max(now.SquadLimit, now.Squad.Count);
        var box = new Rect2(at, new Vector2(SquadWidth(now), Rise) * _u);
        Plate(box, Plating, MenuTheme.Edge, 10);
        Say(at + new Vector2(10, 18) * _u, Lang.T("Squad {0}/{1}", now.Squad.Count, now.SquadLimit).ToUpperInvariant(), 13, MenuTheme.Dim);
        if (Driving)
            DrawOrders(at);
        for (var i = 0; i < count; i++)
        {
            var card = new Rect2(at + new Vector2(8 + i * (CardWidth + CardGap), 24) * _u, new Vector2(CardWidth, 64) * _u);
            if (i >= now.Squad.Count)
            {
                Plate(card, MenuTheme.Well, new Color(MenuTheme.Edge, 0.45f), 6);
                continue;
            }
            var escort = now.Squad[i];
            var hurt = escort.Health < 0.3f;
            Plate(card, MenuTheme.Well, hurt ? Danger : MenuTheme.Edge, 6);
            Glyph(HangarGlyphs.All.GetValueOrDefault(escort.Model), new Rect2(card.Position + new Vector2(4, 3) * _u, new Vector2(40, 40) * _u), hurt ? Danger : MenuTheme.Glow);
            Say(card.Position + new Vector2(CardWidth - 5, 17) * _u, escort.Level.ToString(), 14, MenuTheme.Text, 1);
            DrawDoing(escort.Doing, card.Position + new Vector2(CardWidth - 13, 33) * _u);
            var bar = new Rect2(card.Position + new Vector2(5, 50) * _u, new Vector2(CardWidth - 10, 8) * _u);
            DrawRect(bar, new Color(0, 0, 0, 0.8f));
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * escort.Health, bar.Size.Y)), hurt ? Danger : escort.Health < 0.6f ? Warning : MenuTheme.Glow);
        }
    }

    /// <summary>The order just given lights up on the strip's legend.</summary>
    public void OrderGiven(KeyMap.Act act)
    {
        _orderGiven = act;
        _orderGivenAt = _time;
    }

    /// <summary>The orders' keys on a tab over the strip, each with the sign the cards will show.</summary>
    private void DrawOrders(Vector2 at)
    {
        const float height = 26, sign = 20, gap = 8;
        float Wide(KeyMap.Act act) => Mathf.Max(Width(KeyMap.Name(act), 13) / _u + 8, 18);
        var width = Orders.Sum(order => Wide(order.Act) + sign + gap) + gap;
        var tab = new Rect2(at - new Vector2(0, height + 2) * _u, new Vector2(width, height) * _u);
        Plate(tab, Plating, MenuTheme.Edge, 6);
        var x = tab.Position.X + gap * _u;
        foreach (var (act, doing) in Orders)
        {
            var flash = _orderGiven == act ? Fading(_orderGivenAt, 1.2) : 0;
            var cap = new Rect2(x, tab.Position.Y + 4 * _u, Wide(act) * _u, 18 * _u);
            DrawRect(cap, MenuTheme.Well.Lerp(MenuTheme.Glow, flash * 0.7f));
            DrawRect(cap, MenuTheme.Edge.Lerp(Colors.White, flash), false, 1);
            Say(cap.Position + new Vector2(cap.Size.X / 2, 14 * _u), KeyMap.Name(act), 13, MenuTheme.Text.Lerp(Colors.White, flash), 0.5f);
            DrawDoing(doing, new Vector2(cap.End.X + sign / 2 * _u, tab.GetCenter().Y));
            x = cap.End.X + (sign + gap) * _u;
        }
    }

    /// <summary>A little sign of what an escort is about: an arrow behind the hero, a sight, a shield or a dash.</summary>
    private void DrawDoing(EscortDoing doing, Vector2 at)
    {
        var r = 8 * _u;
        switch (doing)
        {
            case EscortDoing.Following:
                DrawPolyline([at + new Vector2(-r, r * 0.5f), at + new Vector2(0, -r * 0.5f), at + new Vector2(r, r * 0.5f)], MenuTheme.Text, 2 * _u, true);
                break;
            case EscortDoing.Attacking:
                DrawArc(at, r * 0.8f, 0, Mathf.Tau, 16, Danger, 2 * _u, true);
                DrawLine(at - new Vector2(r, 0), at + new Vector2(r, 0), Danger, 2 * _u, true);
                DrawLine(at - new Vector2(0, r), at + new Vector2(0, r), Danger, 2 * _u, true);
                break;
            case EscortDoing.Holding:
                DrawPolyline([at + new Vector2(-r, -r * 0.7f), at + new Vector2(r, -r * 0.7f), at + new Vector2(r, r * 0.1f), at + new Vector2(0, r), at + new Vector2(-r, r * 0.1f), at + new Vector2(-r, -r * 0.7f)], Ice, 2 * _u, true);
                break;
            case EscortDoing.Moving:
                DrawPolyline([at + new Vector2(-r * 0.5f, -r), at + new Vector2(r * 0.5f, 0), at + new Vector2(-r * 0.5f, r)], Gold, 2 * _u, true);
                break;
            default:
                DrawCircle(at, 2.5f * _u, MenuTheme.Dim);
                break;
        }
    }

    // ---- drawing ----------------------------------------------------------------------------------

    private static readonly float[][] PodGlyph = [[2, 13, 4, 7, 8, 3, 12, 7, 14, 13, 2, 13], [6, 13, 6, 10, 10, 10, 10, 13]];

    /// <summary>What is left of a moment's flash, from 1 just after it to 0 when <paramref name="seconds"/> have gone.</summary>
    private float Fading(double since, double seconds) => (float)Math.Clamp(1 - (_time - since) / seconds, 0, 1);

    private static string Thousands(long figure) => figure.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');

    private int Px(float size) => Math.Max(9, Mathf.RoundToInt(size * _u));

    private float Width(string text, float size) => MenuTheme.Font.GetStringSize(text, HorizontalAlignment.Left, -1, Px(size)).X;

    /// <summary>Text from its baseline: <paramref name="align"/> says which part of it stands there, 0 its start, 0.5 its middle, 1 its end.</summary>
    private void Say(Vector2 at, string text, float size, Color colour, float align = 0, int outline = 0)
    {
        var px = Px(size);
        var font = MenuTheme.Font;
        var place = at - new Vector2(font.GetStringSize(text, HorizontalAlignment.Left, -1, px).X * align, 0);
        if (outline > 0)
            DrawStringOutline(font, place, text, HorizontalAlignment.Left, -1, px, Math.Max(2, Mathf.RoundToInt(outline * 2 * _u)), new Color(0, 0, 0, colour.A * 0.85f));
        DrawString(font, place, text, HorizontalAlignment.Left, -1, px, colour);
    }

    /// <summary>A text cut with dots to a width in the HUD's pixels, as a hero's name can be long.</summary>
    private string Fit(string text, float size, float width)
    {
        if (Width(text, size) <= width * _u)
            return text;
        var cut = text.Length;
        while (cut > 1 && Width(text[..cut] + "...", size) > width * _u)
            cut--;
        return text[..cut].TrimEnd() + "...";
    }

    /// <summary>A rectangle's outline with its corners cut, by how much each: top left, top right, bottom right, bottom left.</summary>
    private Vector2[] Corners(Rect2 box, float a, float b, float c, float d)
    {
        var (p, e) = (box.Position, box.End);
        var (ca, cb, cc, cd) = (a * _u, b * _u, c * _u, d * _u);
        return
        [
            new(p.X + ca, p.Y), new(e.X - cb, p.Y), new(e.X, p.Y + cb), new(e.X, e.Y - cc),
            new(e.X - cc, e.Y), new(p.X + cd, e.Y), new(p.X, e.Y - cd), new(p.X, p.Y + ca),
        ];
    }

    private void Plate(Rect2 box, Color fill, Color edge, float cut) => Plate(Corners(box, cut, 0, cut, 0), fill, edge, 1.5f);

    private void Plate(Vector2[] points, Color fill, Color edge, float line)
    {
        // A plating with a little sheen at its top; a light of one colour stays that colour.
        var top = points.Min(p => p.Y);
        var height = Mathf.Max(points.Max(p => p.Y) - top, 1);
        var plain = fill != Plating;
        var colours = points.Select(p => plain ? fill : Sheen.Lerp(Plating, Mathf.Clamp((p.Y - top) / height * 1.6f, 0, 1))).ToArray();
        DrawPolygon(points, colours);
        DrawPolyline([.. points, points[0]], edge, Mathf.Max(line * _u, 1), true);
    }

    private void Thin(Rect2 bar, float share, Color colour)
    {
        DrawRect(bar, new Color(colour, 0.18f));
        var width = bar.Size.X * Mathf.Clamp(share, 0, 1);
        DrawRect(new Rect2(bar.Position + new Vector2((bar.Size.X - width) / 2, 0), new Vector2(width, bar.Size.Y)), colour);
    }

    private void Glyph(float[][]? lines, Rect2 box, Color colour)
    {
        if (lines is null)
            return;
        var scale = box.Size / HangarGlyphs.Grid;
        foreach (var line in lines)
        {
            var points = new Vector2[line.Length / 2];
            for (var i = 0; i < points.Length; i++)
                points[i] = box.Position + new Vector2(line[i * 2], line[i * 2 + 1]) * scale;
            DrawPolyline(points, new Color(colour, 0.22f), 5 * _u, true);
            DrawPolyline(points, colour, Mathf.Max(2 * _u, 1), true);
        }
    }

    /// <summary>What of a slot is still cooling, darkened as a hand going round from the top.</summary>
    private void Sweep(Rect2 box, float share)
    {
        share = Mathf.Clamp(share, 0, 1);
        if (share >= 1)
            return;
        var (centre, half) = (box.GetCenter(), box.Size / 2);
        const int Steps = 40;
        var points = new List<Vector2> { centre };
        for (var i = 0; i <= Steps; i++)
        {
            var angle = Mathf.Tau * (share + (1 - share) * i / Steps);
            var way = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
            points.Add(centre + way * (1 / Mathf.Max(Mathf.Abs(way.X), Mathf.Abs(way.Y))) * half);
        }
        DrawColoredPolygon([.. points], new Color(0, 0, 0, 0.72f));
    }

    // The keys are shown while they work, in first person: the abilities' by their slots, -1 the thrusters'.
    private string? KeyOf(int slot) => !Driving ? null : KeyMap.Name(slot switch
    {
        0 => KeyMap.Act.Ability1,
        1 => KeyMap.Act.Ability2,
        _ => KeyMap.Act.Dash,
    });

    /// <summary>A key's cap on the corner of a slot.</summary>
    private void Cap(Rect2 box, string? key)
    {
        if (key is null)
            return;
        var width = Mathf.Max(Width(key, 15) + 10 * _u, 22 * _u);
        var cap = new Rect2(box.Position + new Vector2(box.Size.X - width - 3 * _u, 3 * _u), new Vector2(width, 21 * _u));
        DrawRect(cap, MenuTheme.Well);
        DrawRect(cap, MenuTheme.Edge, false, 1);
        Say(cap.Position + new Vector2(width / 2, 16 * _u), key, 15, MenuTheme.Text, 0.5f);
    }
}
