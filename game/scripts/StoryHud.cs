using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The screen of a story chapter, in the look of Wired's (<see cref="HudInk"/>) but with what the story asks for: the hero's
/// plate (the Locator is L-7 whoever plays it), the objective at the top with the way there through the deck (the next door
/// or corner marked in the view, not the straight line through the walls), the alarm and the countdown on a row of LEDs, the
/// other two of the trio on cards (following, waiting, drawing the hunters off, down as a scrap heap, still switched off), the
/// signature meter and the motion tracker in the radar's corner (the radar would show the hunters through the walls), what
/// can be done where the hero stands, calls across the view, and the card when the Locator is lost. At the chapter's start the
/// Locator boots in its clamp, system after system, its plates coming one by one through static (StoryHud.Boot). Everything it says comes from <see cref="StoryReadout"/>;
/// MACHINES_STORY_HUD puts it into a state for pictures (<see cref="Demo"/>).
/// </summary>
public partial class StoryHud : Control
{
    private readonly Game _game;
    private readonly Hud _hud;
    private readonly HudInk _ink;
    private StoryReadout? _now;
    private DeckRoute? _route;
    private Vector2 _size;
    private float _u = 1;
    private double _time;

    /// <summary>Where the view starts, right of the control panel (out of first person once the chapter is over).</summary>
    public float Left { get; set; }

    /// <summary>Whether the hero is being driven: the prompts and the orders' keys show then.</summary>
    public bool Driving { get; set; }

    /// <summary>How far down the weapons' panels of first person start: under the hero's plate.</summary>
    public float PanelsTop => (6 + 52 + 6) * _u;

    public StoryHud(Game game, Hud hud)
    {
        (_game, _hud) = (game, hud);
        Name = "StoryHud";
        MouseFilter = MouseFilterEnum.Ignore;
        _ink = new HudInk(this);
        _demo = Demo.FromEnvironment();
    }

    public override void _Ready() => SetAnchorsPreset(LayoutPreset.FullRect);

    public override void _Process(double delta)
    {
        _time += delta;
        _size = GetViewportRect().Size;
        _u = HudInk.ScaleFor(_size);
        _ink.U = _u;
        var world = _game.World;
        if (_route is null && world.Deck is { } deck)
            _route = new DeckRoute(deck);
        _now = StoryReadout.Of(world, Game.Me, _route);
        if (_now is { } now && _demo is not null)
            _now = now = _demo.Apply(now, world, this);
        if (_now is { } shown)
        {
            // In the thick of it (the alarm up or a hunter on the trio) a call is small and high, out of the way of the room.
            _busy = shown.Alarm || shown.Spotted;
            FollowCheckpoint(shown);
            FollowBoot(shown);
            FollowTracker(shown);
            FollowSprint(shown);
            if (_hud.Subtitles is { } subtitles && shown.Grunts.Count >= 2)
                (subtitles.IkeName, subtitles.BobName) = Names(shown);
        }
        FollowCalls();
        QueueRedraw();
    }

    // The checkpoint last seen: a later one is called out, and noted as the one to continue from in the progress of the
    // Locator's player, the host's (plan WP-K: in co-op the story is the host's; the others keep their own).
    private int? _checkpoint;

    private void FollowCheckpoint(StoryReadout now)
    {
        if (_checkpoint is { } last && now.Checkpoint > last && _demo is null && !_game.Watching)
        {
            CheckpointReached();
            if (now.IsLocator)
            {
                var progress = StoryProgress.Load();
                progress.Reached(now.Chapter, now.Checkpoint);
                progress.Save();
            }
        }
        _checkpoint = now.Checkpoint;
    }

    // The names of Ike's and Bob's seats as the cards have them, in seat order whoever plays.
    private (string Ike, string Bob) Names(StoryReadout now)
    {
        var story = _game.World.Story!;
        return (StoryReadout.NameOf(_game.World, story, story.Seats[1]), StoryReadout.NameOf(_game.World, story, story.Seats[2]));
    }

    public override void _Draw()
    {
        if (_size == Vector2.Zero || _now is not { } now)
            return;
        DrawBootView(now);
        DrawDanger(now);
        DrawLabels(now);
        if (Shows(BootPart.Way))
        {
            DrawWay(now);
            DrawSquad(now);
        }
        DrawFloats();
        if (Driving && now.Ending is null)
            DrawPrompts(now);
        DrawCall();
        if (Shows(BootPart.Hero))
            DrawHero(now);
        if (Shows(BootPart.Objective))
            DrawTop(now);
        if (Shows(BootPart.Grunts))
            DrawGrunts(now);
        if (Shows(BootPart.Tracker))
        {
            DrawTracker(now);
            DrawSignature(now);
        }
        DrawBoot(now);
        // The armoury's bar, boss bar, wreck marks and prompts (StoryHud.Armoury).
        DrawArmoury(now);
        DrawConsole();
        DrawHolds();
        DrawEnd(now);
        // Over everything: the Notabschaltung (StoryHud.Shutdown).
        DrawShutdown(now);
    }

    // ---- the hero and the objective -----------------------------------------------------------------

    /// <summary>The hero's name, its health and its armour, at the top left of the view.</summary>
    private void DrawHero(StoryReadout now)
    {
        var at = new Vector2(Left + 16 * _u, 6 * _u);
        var box = new Rect2(at, new Vector2(300, 52) * _u);
        var hurt = now.Health < 0.3f;
        _ink.Plate(box, HudInk.Plating, hurt ? HudInk.Danger : MenuTheme.Edge, 8);
        _ink.Glyph(HangarGlyphs.All.GetValueOrDefault(now.IsLocator ? "GEO_LOCATOR" : "GRUNT"), new Rect2(at + new Vector2(8, 6) * _u, new Vector2(40, 40) * _u), hurt ? HudInk.Danger : MenuTheme.Glow);
        _ink.Say(at + new Vector2(56, 23) * _u, _ink.Fit(now.HeroName.Caps(), 21, 230), 21, MenuTheme.Text);
        var health = new Rect2(at + new Vector2(56, 31) * _u, new Vector2(228, 8) * _u);
        _ink.Bar(health, now.Health, hurt ? HudInk.Danger : now.Health < 0.6f ? HudInk.Warning : MenuTheme.Glow);
        _ink.Thin(new Rect2(at + new Vector2(56, 43) * _u, new Vector2(228, 3) * _u), now.Armour, HudInk.Ice);
        DrawDrive(now, at + new Vector2(308, 0) * _u);
    }

    /// <summary>
    /// The drive's plate beside the hero's (<see cref="StoryDrive"/>): LIMITED while the maintenance limiter holds the sprint,
    /// then its heat, which a sprint fills and walking cools; overheated it flashes until it may sprint again.
    /// </summary>
    private void DrawDrive(StoryReadout now, Vector2 at)
    {
        var box = new Rect2(at, new Vector2(132, 52) * _u);
        var hot = now.DriveHot;
        var flash = hot && (int)(_time * 4) % 2 == 0;
        var colour = hot ? HudInk.Danger : now.DriveHeat > 0.66f ? HudInk.Warning : MenuTheme.Glow;
        _ink.Plate(box, HudInk.Plating, flash ? HudInk.Danger : MenuTheme.Edge, 8);
        _ink.Say(at + new Vector2(10, 18) * _u, Lang.T("Engine").Caps(), 13, MenuTheme.Dim);
        if (!now.SprintFree)
        {
            _ink.Say(at + new Vector2(10, 40) * _u, Lang.T("Limited").Caps(), 15, MenuTheme.Dim);
            return;
        }
        if (now.Sprinting)
            _ink.Say(at + new Vector2(122, 18) * _u, ">>", 13, HudInk.Gold, 1);
        else if (hot)
            _ink.Say(at + new Vector2(122, 18) * _u, Lang.T("Hot").Caps(), 13, flash ? HudInk.Danger : HudInk.Warning, 1);
        _ink.Leds(new Rect2(at + new Vector2(10, 28) * _u, new Vector2(112, 14) * _u), now.DriveHeat, colour, (float)_time, 12);
    }

    /// <summary>The objective at the top middle, and under it the alarm and the countdown.</summary>
    private void DrawTop(StoryReadout now)
    {
        var middle = Left + (_size.X - Left) / 2;
        var y = 6 * _u;
        if (now.Objective is { } objective)
        {
            var text = Lang.T(objective);
            var distance = now.WayLength ?? (now.ObjectiveDistance is { } d ? Mathf.RoundToInt(d) : (int?)null);
            var far = distance is { } metres ? $"{metres} m" : "";
            // The words, the distance and the margins round them: the words were fitted into less room than the card was
            // made for, and long objectives lost their last word.
            var farWidth = _ink.Width(far, 18) / _u;
            var width = Mathf.Clamp(_ink.Width(text, 22) / _u + farWidth + 56, 360, 900);
            var box = new Rect2(middle - width * _u / 2, y, width * _u, 58 * _u);
            _ink.Plate(_ink.Corners(box, 0, 0, 14, 14), HudInk.Plating, MenuTheme.Edge, 1.5f);
            _ink.Say(box.Position + new Vector2(16, 20) * _u, Lang.T("Objective").Caps(), 13, MenuTheme.Dim);
            _ink.Say(box.Position + new Vector2(16, 45) * _u, _ink.Fit(text, 22, width - 52 - farWidth), 22, MenuTheme.Text);
            _ink.Say(new Vector2(box.End.X - 16 * _u, box.Position.Y + 45 * _u), far, 18, HudInk.Gold, 1);
            y = box.End.Y + 6 * _u;
        }
        if (now.CountdownLeft is { } left)
        {
            var urgent = left <= 30;
            var beat = urgent ? 0.6f + 0.4f * MathF.Sin((float)_time * 10) : 1;
            var colour = urgent ? HudInk.Danger : HudInk.Warning;
            var box = new Rect2(middle - 220 * _u, y, 440 * _u, 64 * _u);
            _ink.Plate(box, HudInk.Plating, new Color(colour, beat), 10);
            var clock = $"{(int)left / 60}:{(int)left % 60:00}";
            _ink.Say(box.Position + new Vector2(16, 22) * _u, Lang.T("Atmospheric entry").Caps(), 13, MenuTheme.Dim);
            _ink.Say(new Vector2(box.End.X - 16 * _u, box.Position.Y + 48 * _u), clock, 36, new Color(colour.Lerp(Colors.White, 0.2f), 1), 1);
            _ink.Leds(new Rect2(box.Position + new Vector2(16, 30) * _u, new Vector2(300, 22) * _u), now.CountdownShare ?? 1, colour, (float)_time, 20);
            y = box.End.Y + 6 * _u;
        }
        if (now.Alarm)
        {
            var beat = 0.5f + 0.5f * MathF.Sin((float)_time * 7);
            var text = Lang.T("Alarm").Caps();
            var width = _ink.Width(text, 24) + 60 * _u;
            var box = new Rect2(middle - width / 2, y, width, 36 * _u);
            _ink.Plate(box, new Color(HudInk.Danger.Darkened(0.6f), 0.55f + 0.35f * beat), HudInk.Danger, 8);
            _ink.Say(new Vector2(middle, box.Position.Y + 27 * _u), text, 24, Colors.White.Lerp(HudInk.Danger, 0.3f * beat), 0.5f);
        }
    }

    // ---- over the view ------------------------------------------------------------------------------

    private static readonly Lazy<Texture2D> Edge = new(() => new GradientTexture2D
    {
        Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f),
        FillTo = new Vector2(1.02f, 0.5f),
        Width = 256,
        Height = 256,
        Gradient = new Gradient { Offsets = [0.55f, 1f], Colors = [new Color(1, 1, 1, 0), new Color(1, 1, 1, 1)] },
    });

    /// <summary>The red at the edges of a hero nearly gone, and red at the edges while a hunter sees the trio.</summary>
    private void DrawDanger(StoryReadout now)
    {
        if (!Driving || now.Ending is not null)
            return;
        if (now.Health < 0.35f)
        {
            var beat = 0.65f + 0.35f * MathF.Sin((float)_time * (now.Health < 0.15f ? 9 : 5));
            DrawTextureRect(Edge.Value, new Rect2(Vector2.Zero, _size), false, new Color(HudInk.Danger, (0.35f - now.Health) / 0.35f * 0.8f * beat));
        }
        else if (now.Spotted)
            DrawTextureRect(Edge.Value, new Rect2(Vector2.Zero, _size), false, new Color(HudInk.Danger, 0.18f + 0.1f * MathF.Sin((float)_time * 6)));
    }

    /// <summary>The way: the next waypoint marked where it lies and the objective once it is in sight, by an arrow at the edge when out of view.</summary>
    private void DrawWay(StoryReadout now)
    {
        if (GetViewport().GetCamera3D() is not { } camera || _game.World.HeroOf(Game.Me) is not { } hero || now.ObjectivePlace is not { } goal || now.Ending is not null)
            return;
        if (now.Waypoints.Count > 0)
        {
            var next = now.Waypoints[0];
            var metres = Mathf.RoundToInt(System.Numerics.Vector2.Distance(next, hero.Position));
            Pointer(camera, ActorView.World(next) + Vector3.Up * 1.5f, MenuTheme.Glow, $"{metres} m", strong: false);
            return;
        }
        var distance = Mathf.RoundToInt(System.Numerics.Vector2.Distance(goal, hero.Position));
        Pointer(camera, ActorView.World(goal) + Vector3.Up * 2, HudInk.Gold, $"{Lang.T("Objective")}  {distance} m", strong: true);
    }

    /// <summary>Where the Commander's squad comes from while it is on its way to the core chamber: a red mark, or an arrow at the edge.</summary>
    private void DrawSquad(StoryReadout now)
    {
        if (now.Squad is not { } squad || now.Ending is not null || GetViewport().GetCamera3D() is not { } camera || _game.World.HeroOf(Game.Me) is not { } hero)
            return;
        var metres = Mathf.RoundToInt(System.Numerics.Vector2.Distance(squad, hero.Position));
        Pointer(camera, ActorView.World(squad) + Vector3.Up * 4, HudInk.Danger, $"{Lang.T("Squad")}  {metres} m", strong: false);
    }

    /// <summary>A mark on a place in view, or an arrow at the edge of the view towards one that is not (as Wired's pointers, <see cref="WiredHud"/>).</summary>
    private void Pointer(Camera3D camera, Vector3 place, Color colour, string label, bool strong)
    {
        var margin = 90 * _u;
        // Arrows at the edge keep above the subtitles at the foot of the view.
        var area = new Rect2(new Vector2(Left + margin, margin * 1.6f), new Vector2(_size.X - Left - margin * 2, _size.Y * 0.68f - margin * 1.6f));
        var view = new Rect2(new Vector2(Left + 16 * _u, margin * 0.9f), new Vector2(_size.X - Left - 32 * _u, _size.Y - margin * 0.9f - 250 * _u));
        var beat = strong ? 0.8f + 0.2f * MathF.Sin((float)_time * 6) : 0.75f;
        var tint = new Color(colour, beat);
        var front = !camera.IsPositionBehind(place);
        var at = front ? camera.UnprojectPosition(place) : Vector2.Zero;
        var r = (strong ? 11 : 8) * _u;
        var screen = new Rect2(Left, 0, _size.X - Left, _size.Y);
        if (front && view.HasPoint(at))
        {
            _ink.Mark(at, r, tint);
            _ink.Tag(at + new Vector2(0, -r - 14 * _u), label, tint, screen);
            return;
        }
        var local = camera.GlobalTransform.AffineInverse() * place;
        var angle = MathF.Atan2(local.X, -local.Z);
        var way = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
        var tip = area.GetCenter() + way * area.Size / 2;
        var size = (strong ? 24 : 17) * _u;
        var back = _ink.Arrow(tip, way, size, tint);
        _ink.Tag(back - way * (_ink.Width(label, 15) / 2 + 14 * _u) * new Vector2(1, 0.4f), label, tint, screen);
    }

    /// <summary>What can be done where the hero stands, under the sight: the nearest large, the rest smaller under it.</summary>
    private void DrawPrompts(StoryReadout now)
    {
        var middle = Left + (_size.X - Left) / 2;
        var y = _size.Y * 0.64f;
        var first = true;
        foreach (var prompt in now.Prompts.Take(3))
        {
            var box = _ink.Prompt(new Vector2(middle, y), PromptText(prompt), first ? MenuTheme.Glow : MenuTheme.Text, first ? 24 : 18);
            y = box.End.Y + 8 * _u;
            first = false;
        }
    }

    /// <summary>A prompt's words with the key that does it.</summary>
    public static string PromptText(StoryPrompt prompt)
    {
        var key = KeyMap.Name(KeyMap.Act.Use);
        return prompt.Kind switch
        {
            StoryPromptKind.Hack => Lang.T("Hack ({0})", key),
            StoryPromptKind.Take => Lang.T("Take ({0})", key),
            StoryPromptKind.Use => Lang.T("Use ({0})", key),
            StoryPromptKind.Hold => Lang.T("Hold ({0})", key),
            _ => Lang.T("Hold {0}: put {1} together", key, prompt.Target),
        };
    }

    // ---- the trio -------------------------------------------------------------------------------------

    private const float CardWidth = 156, CardHeight = 70, CardGap = 6;

    // The orders the Locator gives its Grunts, as the tab over their cards shows them.
    private static readonly (KeyMap.Act Act, GruntDoing Sign, string Word)[] Orders =
    [
        (KeyMap.Act.GruntFollow, GruntDoing.Following, "Follow"),
        (KeyMap.Act.GruntWait, GruntDoing.Waiting, "Wait"),
        (KeyMap.Act.GruntDistract, GruntDoing.Distracting, "Distract"),
    ];

    private KeyMap.Act? _orderGiven;
    private double _orderGivenAt = -10;

    /// <summary>An order just given to the Grunts lights up on the tab.</summary>
    public void OrderGiven(KeyMap.Act act) => (_orderGiven, _orderGivenAt) = (act, _time);

    /// <summary>The others of the trio on cards at the foot of the view on the left, with the orders' keys over them for the Locator.</summary>
    private void DrawGrunts(StoryReadout now)
    {
        if (now.Grunts.Count == 0)
            return;
        var width = 16 + now.Grunts.Count * (CardWidth + CardGap);
        var at = new Vector2(Left + 16 * _u, _size.Y - (CardHeight + 44) * _u);
        var box = new Rect2(at, new Vector2(width, CardHeight + 30) * _u);
        _ink.Plate(box, HudInk.Plating, MenuTheme.Edge, 10);
        _ink.Say(at + new Vector2(10, 18) * _u, Lang.T(now.IsLocator ? "Grunts" : "Trio").Caps(), 13, MenuTheme.Dim);
        if (Driving && now.IsLocator && now.Ending is null)
            DrawOrders(at);
        for (var i = 0; i < now.Grunts.Count; i++)
        {
            var grunt = now.Grunts[i];
            var card = new Rect2(at + new Vector2(8 + i * (CardWidth + CardGap), 24) * _u, new Vector2(CardWidth, CardHeight) * _u);
            DrawCard(grunt, card, now.IsLocator && i == 0 ? "GRUNT" : now.IsLocator ? "GRUNT" : i == 0 ? "GEO_LOCATOR" : "GRUNT");
        }
    }

    private void DrawCard(GruntReadout grunt, Rect2 card, string glyph)
    {
        var (colour, edge) = grunt.Doing switch
        {
            GruntDoing.Down => (HudInk.Danger, new Color(HudInk.Danger, 0.6f + 0.4f * MathF.Sin((float)_time * 6))),
            GruntDoing.Dormant => (MenuTheme.Dim, new Color(MenuTheme.Edge, 0.5f)),
            GruntDoing.Distracting => (HudInk.Gold, HudInk.Gold),
            _ => (grunt.Health < 0.3f ? HudInk.Danger : MenuTheme.Glow, grunt.Health < 0.3f ? HudInk.Danger : MenuTheme.Edge),
        };
        _ink.Plate(card, MenuTheme.Well, edge, 6);
        _ink.Glyph(HangarGlyphs.All.GetValueOrDefault(grunt.Doing == GruntDoing.Down ? "scrap" : glyph), new Rect2(card.Position + new Vector2(4, 4) * _u, new Vector2(34, 34) * _u), colour);
        _ink.Say(card.Position + new Vector2(44, 19) * _u, _ink.Fit(grunt.Name.Caps(), 15, CardWidth - 50), 15, grunt.Doing == GruntDoing.Dormant ? MenuTheme.Dim : MenuTheme.Text);
        DrawDoing(grunt.Doing, card.Position + new Vector2(52, 33) * _u);
        _ink.Say(card.Position + new Vector2(62, 38) * _u, _ink.Fit(Lang.T(DoingWord(grunt.Doing)).Caps(), 11, CardWidth - 66 - (grunt.Person ? 12 : 0)), 11, colour);
        if (grunt.Person)
            _ink.Say(new Vector2(card.End.X - 6 * _u, card.Position.Y + 38 * _u), "P", 11, MenuTheme.Dim, 1);
        var bar = new Rect2(card.Position + new Vector2(6, 48) * _u, new Vector2(CardWidth - 12, 7) * _u);
        _ink.Bar(bar, grunt.Health, grunt.Health < 0.3f ? HudInk.Danger : grunt.Health < 0.6f ? HudInk.Warning : MenuTheme.Glow);
        if (grunt.Distance is { } far)
            _ink.Say(new Vector2(card.End.X - 6 * _u, card.Position.Y + 66 * _u), $"{Mathf.RoundToInt(far)} m", 11, MenuTheme.Dim, 1);
    }

    /// <summary>What a card says a Grunt is about.</summary>
    public static string DoingWord(GruntDoing doing) => doing switch
    {
        GruntDoing.Following => "Following",
        GruntDoing.Waiting => "Waiting",
        GruntDoing.Moving => "On the way",
        GruntDoing.Distracting => "Distracting",
        GruntDoing.Down => "Knocked down",
        GruntDoing.Dormant => "Switched off",
        _ => "Standing",
    };

    /// <summary>The orders' keys on a tab over the cards, each with the sign the cards will show.</summary>
    private void DrawOrders(Vector2 at)
    {
        const float height = 26, sign = 20, gap = 8;
        float Wide(KeyMap.Act act) => Mathf.Max(_ink.Width(KeyMap.Name(act), 13) / _u + 8, 18);
        float Word(string word) => _ink.Width(Lang.T(word), 12) / _u;
        var width = Orders.Sum(order => Wide(order.Act) + sign + Word(order.Word) + gap * 2) + gap;
        var tab = new Rect2(at - new Vector2(0, height + 2) * _u, new Vector2(width, height) * _u);
        _ink.Plate(tab, HudInk.Plating, MenuTheme.Edge, 6);
        var x = tab.Position.X + gap * _u;
        foreach (var (act, doing, word) in Orders)
        {
            var flash = _orderGiven == act ? (float)Math.Clamp(1 - (_time - _orderGivenAt) / 1.2, 0, 1) : 0;
            var cap = new Rect2(x, tab.Position.Y + 4 * _u, Wide(act) * _u, 18 * _u);
            DrawRect(cap, MenuTheme.Well.Lerp(MenuTheme.Glow, flash * 0.7f));
            DrawRect(cap, MenuTheme.Edge.Lerp(Colors.White, flash), false, 1);
            _ink.Say(cap.Position + new Vector2(cap.Size.X / 2, 14 * _u), KeyMap.Name(act), 13, MenuTheme.Text.Lerp(Colors.White, flash), 0.5f);
            DrawDoing(doing, new Vector2(cap.End.X + sign / 2 * _u, tab.GetCenter().Y));
            _ink.Say(new Vector2(cap.End.X + sign * _u + 2 * _u, tab.Position.Y + 18 * _u), Lang.T(word), 12, MenuTheme.Dim);
            x = cap.End.X + (sign + Word(word) + gap * 2) * _u;
        }
    }

    /// <summary>A little sign of what a Grunt is about: an arrow behind the hero, a shield where it waits, a horn, a cross (at a scale and a strength for its label in the view).</summary>
    private void DrawDoing(GruntDoing doing, Vector2 at, float scale = 1, float alpha = 1)
    {
        var r = 7 * _u * scale;
        var line = 2 * _u * scale;
        Color Lit(Color colour) => new(colour, colour.A * alpha);
        switch (doing)
        {
            case GruntDoing.Following:
                DrawPolyline([at + new Vector2(-r, r * 0.5f), at + new Vector2(0, -r * 0.5f), at + new Vector2(r, r * 0.5f)], Lit(MenuTheme.Text), line, true);
                break;
            case GruntDoing.Waiting:
                DrawPolyline([at + new Vector2(-r, -r * 0.7f), at + new Vector2(r, -r * 0.7f), at + new Vector2(r, r * 0.1f), at + new Vector2(0, r), at + new Vector2(-r, r * 0.1f), at + new Vector2(-r, -r * 0.7f)], Lit(HudInk.Ice), line, true);
                break;
            case GruntDoing.Moving:
                DrawPolyline([at + new Vector2(-r * 0.5f, -r), at + new Vector2(r * 0.5f, 0), at + new Vector2(-r * 0.5f, r)], Lit(HudInk.Gold), line, true);
                break;
            case GruntDoing.Distracting:
                // A horn, sounding.
                DrawPolyline([at + new Vector2(-r, -r * 0.3f), at + new Vector2(-r * 0.3f, -r * 0.3f), at + new Vector2(r * 0.5f, -r), at + new Vector2(r * 0.5f, r), at + new Vector2(-r * 0.3f, r * 0.3f), at + new Vector2(-r, r * 0.3f), at + new Vector2(-r, -r * 0.3f)], Lit(HudInk.Gold), line, true);
                DrawArc(at + new Vector2(r * 0.5f, 0), r * 0.8f, -0.8f, 0.8f, 8, Lit(HudInk.Gold), 0.75f * line, true);
                break;
            case GruntDoing.Down:
                DrawLine(at - new Vector2(r, r), at + new Vector2(r, r), Lit(HudInk.Danger), line, true);
                DrawLine(at + new Vector2(-r, r), at + new Vector2(r, -r), Lit(HudInk.Danger), line, true);
                break;
            default:
                DrawCircle(at, 2.5f * _u * scale, Lit(MenuTheme.Dim));
                break;
        }
    }

    // ---- signature and motion tracker ------------------------------------------------------------------

    private const float TrackerRadius = 92;

    /// <summary>The tracker's middle, in the radar's corner at the foot of the view on the right.</summary>
    private Vector2 TrackerCentre => new(_size.X - (16 + TrackerRadius + 14) * _u, _size.Y - (16 + TrackerRadius + 24) * _u);

    // When the tracker pinged last and when it pings next, by the HUD's clock.
    private double _pinged = -10, _nextPing;

    /// <summary>The tracker pings, faster the nearer what moves is; it ticks aloud only while something moves within its reach.</summary>
    private void FollowTracker(StoryReadout now)
    {
        if (!Driving || now.Ending is not null || _time < _nextPing || !Shows(BootPart.Tracker))
            return;
        _pinged = _time;
        if (now.Contacts.Count == 0)
        {
            _nextPing = _time + 2.2;
            return;
        }
        var nearest = now.Contacts[0].Distance;
        _nextPing = _time + Mathf.Lerp(0.16f, 1.3f, Mathf.Clamp(nearest / StoryReadout.TrackerRange, 0, 1));
        _game.Audio.PlayGui("gui/sounds/navkey.wav");
    }

    /// <summary>
    /// The motion tracker: four rings out to its reach (15 m apart), the view's way up, and a blip for each of the ship's machines
    /// that moves within it, lit at each ping and fading after; the nearest's distance under it.
    /// </summary>
    private void DrawTracker(StoryReadout now)
    {
        var centre = TrackerCentre;
        var radius = TrackerRadius * _u;
        var box = new Rect2(centre - new Vector2(radius + 10 * _u, radius + 10 * _u), new Vector2(radius * 2 + 20 * _u, radius * 2 + 38 * _u));
        _ink.Plate(box, HudInk.Plating, MenuTheme.Edge, 12);
        var ping = (float)Math.Clamp((_time - _pinged) / 0.6, 0, 1);
        for (var ring = 1; ring <= 4; ring++)
            DrawArc(centre, radius * ring / 4, 0, Mathf.Tau, 48, new Color(MenuTheme.Glow, ring == 4 ? 0.5f : 0.22f), Mathf.Max(1, _u), true);
        // The ping going out from the middle.
        if (ping < 1)
            DrawArc(centre, radius * ping, 0, Mathf.Tau, 48, new Color(MenuTheme.Glow, 0.6f * (1 - ping)), 2 * _u, true);
        // Ahead: the view's way.
        DrawLine(centre, centre + new Vector2(0, -radius), new Color(MenuTheme.Glow, 0.3f), Mathf.Max(1, _u));
        DrawColoredPolygon([centre + new Vector2(0, -6) * _u, centre + new Vector2(5, 5) * _u, centre + new Vector2(-5, 5) * _u], MenuTheme.Glow);
        var camera = GetViewport().GetCamera3D();
        var lit = 1 - (float)Math.Clamp((_time - _pinged) / 1.1, 0, 1);
        foreach (var contact in now.Contacts)
        {
            var angle = camera is not null ? Bearing(camera, contact) : contact.Bearing;
            var reach = Mathf.Clamp(contact.Distance / StoryReadout.TrackerRange, 0, 1) * radius;
            var at = centre + new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * reach;
            DrawCircle(at, 9 * _u, new Color(MenuTheme.Glow, 0.15f * lit + 0.05f));
            DrawCircle(at, 4.5f * _u, new Color(MenuTheme.Glow.Lerp(Colors.White, 0.4f), 0.35f + 0.65f * lit));
        }
        var label = now.Contacts.Count > 0 ? $"{Mathf.RoundToInt(now.Contacts[0].Distance)} m" : "--";
        _ink.Say(new Vector2(box.Position.X + 10 * _u, box.End.Y - 9 * _u), Lang.T("Motion").Caps(), 12, MenuTheme.Dim);
        _ink.Say(new Vector2(box.End.X - 10 * _u, box.End.Y - 9 * _u), label, 15, now.Contacts.Count > 0 ? HudInk.Warning : MenuTheme.Dim, 1);
    }

    /// <summary>Which way a contact lies from where the view looks: 0 ahead, positive to the right.</summary>
    private static float Bearing(Camera3D camera, TrackerContact contact)
    {
        var place = ActorView.World(contact.Position);
        var local = camera.GlobalTransform.AffineInverse() * place;
        return MathF.Atan2(local.X, -local.Z);
    }

    /// <summary>How loud and visible the hero is, a bar over the tracker; the warning when a hunter sees it.</summary>
    private void DrawSignature(StoryReadout now)
    {
        var tracker = TrackerCentre;
        var right = tracker.X + (TrackerRadius + 10) * _u;
        var bottom = tracker.Y - (TrackerRadius + 18) * _u;
        var width = (TrackerRadius * 2 + 20) * _u;
        var box = new Rect2(right - width, bottom - 40 * _u, width, 40 * _u);
        var level = now.Signature;
        var colour = level < 0.3f ? MenuTheme.Glow : level < 0.7f ? HudInk.Warning : HudInk.Danger;
        _ink.Plate(box, HudInk.Plating, now.Spotted ? HudInk.Danger : MenuTheme.Edge, 8);
        _ink.Say(box.Position + new Vector2(10, 16) * _u, Lang.T("Signature").Caps(), 12, MenuTheme.Dim);
        // Ten cells, lit from the left.
        const int Cells = 10;
        var cells = new Rect2(box.Position + new Vector2(10, 22) * _u, new Vector2(width - 20 * _u, 10 * _u));
        var cell = (cells.Size.X - (Cells - 1) * 3 * _u) / Cells;
        for (var i = 0; i < Cells; i++)
        {
            var on = Mathf.Clamp(level * Cells - i, 0, 1);
            var rect = new Rect2(cells.Position.X + i * (cell + 3 * _u), cells.Position.Y, cell, cells.Size.Y);
            DrawRect(rect, new Color(0, 0, 0, 0.7f));
            if (on > 0)
                DrawRect(rect, new Color(colour, on));
        }
        if (now.Spotted)
        {
            var beat = 0.55f + 0.45f * MathF.Sin((float)_time * 12);
            _ink.Say(new Vector2(box.End.X, box.Position.Y - 8 * _u), Lang.T("Signature detected").Caps(), 18, new Color(HudInk.Danger, beat), 1, outline: 1);
        }
    }

    // ---- calls and floats ------------------------------------------------------------------------------

    private sealed record Shout(string Title, string? Line, Color Colour, float Seconds);

    private readonly List<Shout> _calls = [];
    private bool _busy;
    private double _callStarted;

    private sealed record Rising(string Text, Color Colour, double Since);

    private readonly List<Rising> _rising = [];

    /// <summary>
    /// A call across the view: a section's title, the alarm, a checkpoint reached. A newer call under the same title (the next
    /// objective) takes the place of one still waiting and cuts one showing short, so the card never shows a step already done.
    /// </summary>
    public void Announce(string title, string? line, Color colour, float seconds = 3)
    {
        _calls.RemoveAll(call => call.Title == title && !ReferenceEquals(call, _calls[0]));
        if (_calls.Count > 0 && _calls[0].Title == title && _calls[0].Line != line)
        {
            _calls.RemoveAt(0);
            _callStarted = _time;
        }
        if (_calls.Count > 2)
            _calls.RemoveAt(1);
        _calls.Add(new Shout(title, line, colour, seconds));
        if (_calls.Count == 1)
            _callStarted = _time;
    }

    /// <summary>A checkpoint is reached: called out briefly.</summary>
    public void CheckpointReached() => Announce(Lang.T("Checkpoint").Caps(), null, MenuTheme.Glow, 2.2f);

    /// <summary>Words rising from under the sight, as "+ Bolter" or a Grunt back online.</summary>
    public void Float(string text, Color? colour = null) => _rising.Add(new Rising(text, colour ?? HudInk.Gold, _time));

    private void FollowCalls()
    {
        if (_calls.Count > 0 && _time - _callStarted > _calls[0].Seconds)
        {
            _calls.RemoveAt(0);
            _callStarted = _time;
        }
        _rising.RemoveAll(r => _time - r.Since > 1.6);
    }

    private void DrawCall()
    {
        if (_calls.Count == 0)
            return;
        var call = _calls[0];
        var age = (float)(_time - _callStarted);
        var opening = Mathf.Clamp(age / 0.22f, 0, 1);
        var alpha = opening * Mathf.Clamp((call.Seconds - age) / 0.45f, 0, 1);
        var middle = Left + (_size.X - Left) / 2;
        var width = (_size.X - Left) * (_busy ? 0.24f : 0.42f) * (1 - (1 - opening) * (1 - opening));
        _ink.Call(middle, _size.Y * (_busy ? 0.19f : 0.28f), width, call.Title, call.Line, call.Colour, alpha,
            (_size.X - Left) / _u * (_busy ? 0.45f : 0.8f), _busy ? 0.5f : 1);
    }

    private void DrawFloats()
    {
        var y = 0f;
        foreach (var rising in _rising)
        {
            var age = (float)((_time - rising.Since) / 1.6);
            var at = new Vector2(Left + (_size.X - Left) / 2, _size.Y / 2 + 150 * _u - 100 * (1 - (1 - age) * (1 - age)) * _u + y);
            var alpha = age < 0.7f ? 1 : (1 - age) / 0.3f;
            _ink.Say(at, rising.Text, 30, new Color(rising.Colour, alpha), 0.5f, outline: 2);
            y += 36 * _u;
        }
    }

    // ---- the end -----------------------------------------------------------------------------------------

    /// <summary>What the card says when the chapter is lost: its heading, the line under it and the key's prompt (online: back to the room).</summary>
    public static (string Title, string Line, string Prompt) EndWords(StoryEnding ending, bool online, bool host) => (
        ending == StoryEnding.Crashed ? Lang.T("Crashed") : Lang.T("{0} lost", StoryReadout.LocatorName),
        ending == StoryEnding.Crashed ? Lang.T("The ship went down before the pod was away.") : Lang.T("Without the Locator there is no way out."),
        !online ? Lang.T("From the checkpoint ({0})", Lang.T("Space"))
            : host ? Lang.T("To the room, on from the checkpoint ({0})", Lang.T("Space"))
            : Lang.T("To the room, the host goes on from the checkpoint ({0})", Lang.T("Space")));

    /// <summary>The chapter is lost (the Locator fell, or the ship came down first): it goes on from its last checkpoint, by a key.</summary>
    private void DrawEnd(StoryReadout now)
    {
        if (now.Ending is not ({ } ending and (StoryEnding.LocatorLost or StoryEnding.Crashed)))
            return;
        var (title, line, prompt) = EndWords(ending, _game.Online, _game.StoryHost);
        var middle = Left + (_size.X - Left) / 2;
        var box = new Rect2(middle - 340 * _u, _size.Y * 0.5f - 120 * _u, 680 * _u, 240 * _u);
        _ink.Plate(box, new Color(0, 0, 0, 0.84f), HudInk.Danger, 16);
        _ink.Say(new Vector2(middle, box.Position.Y + 56 * _u), title.Caps(), 40, HudInk.Danger, 0.5f);
        _ink.Say(new Vector2(middle, box.Position.Y + 108 * _u), _ink.Fit(line, 22, 640), 22, MenuTheme.Text, 0.5f);
        var beat = 0.75f + 0.25f * MathF.Sin((float)_time * 4);
        _ink.Prompt(new Vector2(middle, box.Position.Y + 146 * _u), prompt, new Color(MenuTheme.Glow, beat), 24);
        _ink.Say(new Vector2(middle, box.End.Y - 14 * _u), Lang.T("Checkpoint {0}", now.Checkpoint), 15, MenuTheme.Dim, 0.5f);
    }

    public override void _Input(InputEvent @event)
    {
        if (BootInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_now is not { Ending: StoryEnding.LocatorLost or StoryEnding.Crashed } || _demo is not null || _game.Watching)
            return;
        if (@event is InputEventKey { Pressed: true, Echo: false } key && key.Keycode is Key.Space or Key.Enter or Key.KpEnter)
        {
            GetViewport().SetInputAsHandled();
            _game.FromCheckpoint();
        }
    }
}
