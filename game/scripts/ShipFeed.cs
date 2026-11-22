using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The ship's camera beside the core's hack (docs/STORY.md, Der Hack): plugged into the main computer, the Locator sees
/// what the ship's own cameras see, a grainy night-vision picture on a chip of its own to the right of the board. It shows
/// what the hack is buying time for: Ike and Bob pushing crates before doors J and I, the Commander's squad at the lift and
/// down the spine, hammering at door J (outside and in turn the crates shaking inside), going round by door W and the
/// service rooms, and shooting door I's crates away. Each picture is taken from the ceiling camera of the room the
/// subject is in that comes nearest a good distance from it, looking at it. The world's own 3D scene is drawn a second
/// time, small, only while the chip is up.
/// </summary>
public partial class ShipFeed : ChipPanel
{
    /// <summary>The picture's size in menu pixels (16:9).</summary>
    public static readonly Vector2 Picture = new(520, 292);

    /// <summary>A ceiling camera: the room it hangs in, where (deck metres) and how high.</summary>
    private readonly record struct Spot(string Room, string Name, float X, float Y, float Height);

    /// <summary>
    /// The ship's cameras round the core: over the chamber's doors J and I (looking in) and on its east and west walls, along
    /// the spine (one over door J) and at the lift, the service rooms and the antechamber.
    /// </summary>
    private static readonly Spot[] Spots =
    [
        new("R9", "Core J", 192, 295, 15), new("R9", "Core I", 190, 233, 15), new("R9", "Core E", 222, 264, 18), new("R9", "Core W", 162, 264, 18),
        new("R10", "Spine 1", 160, 318, 15), new("R10", "Spine J", 210, 318, 13), new("R10", "Spine 2", 250, 318, 15), new("R10", "Spine 3", 300, 318, 15),
        new("R10", "Spine 4", 370, 318, 15), new("R10", "Spine 5", 425, 302, 15), new("R10a", "Lift", 446, 302, 15),
        new("R11", "Service 1", 145, 292, 12), new("R11", "Service 2", 145, 250, 12), new("R11", "Service 3", 145, 217, 12),
        new("R8", "Antechamber W", 178, 214, 12), new("R8", "Antechamber E", 206, 214, 12),
    ];

    /// <summary>How far from its subject a camera's picture reads best, metres, and how wide a view round it the camera zooms to.</summary>
    private const float Best = 14, Span = 13;

    /// <summary>How long each of two alternating subjects is shown, seconds.</summary>
    private const float Alternate = 5;

    /// <summary>
    /// The camera's picture is taken afresh every so many frames only (a ship's camera's few pictures a second): drawing the
    /// deck a second time every frame cost the hack's frames about a quarter more.
    /// </summary>
    private const int EveryFrames = 3;

    private const string Shader = """
        shader_type canvas_item;
        uniform float time;
        uniform float gain = 2.6;
        void fragment() {
            vec3 c = texture(TEXTURE, UV).rgb;
            float l = dot(c, vec3(0.3, 0.55, 0.15));
            l = pow(clamp(l * gain, 0.0, 1.0), 0.8);
            float n = fract(sin(dot(UV * vec2(431.0, 197.0) + time * 7.0, vec2(12.9898, 78.233))) * 43758.5453);
            l = l * (0.86 + 0.14 * n) + 0.035;
            l *= 0.88 + 0.12 * sin(UV.y * 290.0 + time * 3.0);
            vec2 d = UV - 0.5;
            l *= 1.0 - dot(d, d) * 1.3;
            COLOR = vec4(l * 0.62, l * 1.0, l * 0.66, 1.0);
        }
        """;

    private readonly Game _game;
    private SubViewport _view = null!;
    private Camera3D _camera = null!;
    private TextureRect _screen = null!;
    private ShaderMaterial _look = null!;
    private Label _title = null!, _place = null!;
    private Spot? _spot;
    private double _time;
    private int _frame;

    public ShipFeed(Game game)
    {
        _game = game;
        Name = "ShipFeed";
        Padding = 12;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>The camera the picture is taken from, for automated checks; empty while none is.</summary>
    public string Camera => _spot?.Name ?? "";

    public override void _Ready()
    {
        base._Ready();
        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 6);
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _title = MenuTheme.Label(Lang.T("Ship camera").ToUpperInvariant(), 20, HackBoard.Danger);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _place = MenuTheme.Label("", 18, MenuTheme.Dim, HorizontalAlignment.Right);
        head.AddChild(_title);
        head.AddChild(_place);
        column.AddChild(head);
        _view = new SubViewport
        {
            Size = new Vector2I((int)Picture.X, (int)Picture.Y), OwnWorld3D = false, TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Msaa3D = Viewport.Msaa.Disabled,
        };
        _camera = new Camera3D { Fov = 68, Near = 0.3f, Far = 220, Current = true };
        _view.AddChild(_camera);
        AddChild(_view);
        _look = new ShaderMaterial { Shader = new Shader { Code = Shader } };
        _screen = new TextureRect
        {
            Texture = _view.GetTexture(), CustomMinimumSize = Picture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, Material = _look, MouseFilter = MouseFilterEnum.Ignore,
        };
        column.AddChild(_screen);
        AddChild(column);
        Visible = false;
    }

    /// <summary>
    /// Shows the feed while the Locator hacks the core and something is worth seeing (the Grunts at the crates from the
    /// hack's start, the squad once it is sent), until the break-in; <paramref name="scale"/> is the menus' stage scale,
    /// so the picture is drawn at the window's own pixels.
    /// </summary>
    public void Follow(bool up, float scale, double delta)
    {
        var world = _game.World;
        var subject = up && world.Story is { } run && !run.Director.Has("break_in") && run.Director.Has("core_open") ? Subject(world, run) : null;
        if (subject is not { } at || Pick(world, at) is not { } spot)
        {
            if (Visible)
            {
                Visible = false;
                _view.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                _spot = null;
            }
            return;
        }
        if (!Visible)
        {
            _view.World3D = GetViewport().FindWorld3D();
            Visible = true;
            _frame = 0;
            SwitchOn(0);
        }
        var pixels = new Vector2I(Math.Max(64, (int)(Picture.X * scale)), Math.Max(36, (int)(Picture.Y * scale)));
        if (_view.Size != pixels)
            _view.Size = pixels;
        _spot = spot;
        _place.Text = Lang.T(spot.Name).ToUpperInvariant();
        _time += delta;
        _look.SetShaderParameter("time", (float)(_time % 1000));
        if (_frame++ % EveryFrames == 0)
            _view.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        var from = new Vector3(spot.X, ActorView.World(new System.Numerics.Vector2(spot.X, spot.Y)).Y + spot.Height, spot.Y);
        var toward = ActorView.World(at) + Vector3.Up * 2.5f;
        if (from.DistanceSquaredTo(toward) > 1)
        {
            _camera.LookAtFromPosition(from, toward, Vector3.Up);
            // The camera zooms so its subject fills the picture at any distance (within its lens).
            _camera.Fov = Mathf.Clamp(Mathf.RadToDeg(2 * Mathf.Atan(Span / 2 / from.DistanceTo(toward))), 22, 70);
        }
    }

    /// <summary>
    /// What the ship's camera follows: before the squad, Ike and Bob in turn at their doors; then the lead Reaper, and while
    /// it hammers at J or shoots at I, in turn the barricade's crates from inside the chamber.
    /// </summary>
    private System.Numerics.Vector2? Subject(GameWorld world, StoryRun run)
    {
        var director = run.Director;
        var turn = (int)(world.Time / Alternate) % 2;
        if (!director.Has("squad"))
        {
            // Ike's door J and Bob's door I in turn: the Grunt once a camera has it, until then its door, waiting for the crates.
            var which = run.Seats.Count > 2 ? 1 + turn : 1;
            var waiting = Crates(world, which == 1 ? "J" : "I");
            return which < run.Seats.Count && world.HeroOf(run.Seats[which]) is { IsDead: false } grunt && Room(world, grunt.Position) is not null
                ? grunt.Position : waiting;
        }
        var lead = director.Of(world, Prologue.ReaperRole).FirstOrDefault(m => !m.IsDead);
        var door = director.Has("shoot_i") ? "I" : director.Has("hammer_j") && !director.Has("go_round") ? "J" : null;
        if (door is not null && turn == 1 && Crates(world, door) is { } crates)
            return crates;
        return lead?.Position;
    }

    /// <summary>The middle of a door's barricade (its second slot).</summary>
    private static System.Numerics.Vector2? Crates(GameWorld world, string door) =>
        world.Deck?.Markers.FirstOrDefault(m => m.Name == $"barricade_{door}_2")?.Position;

    /// <summary>The room with ship's cameras a point is in, or null.</summary>
    private static string? Room(GameWorld world, System.Numerics.Vector2 at) =>
        world.Deck is { } deck ? Spots.Select(s => s.Room).Distinct().FirstOrDefault(r => deck.Zone(r)?.Contains(at) == true) : null;

    /// <summary>The camera of the subject's room whose distance from it comes nearest <see cref="Best"/>; the last one when the subject is in none.</summary>
    private Spot? Pick(GameWorld world, System.Numerics.Vector2 at)
    {
        if (Room(world, at) is not { } room)
            return _spot;
        return Spots.Where(s => s.Room == room)
            .OrderBy(s => MathF.Abs(System.Numerics.Vector2.Distance(new System.Numerics.Vector2(s.X, s.Y), at) - Best)).First();
    }
}
