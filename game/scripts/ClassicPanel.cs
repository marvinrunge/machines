using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// The original's control panel down the left of the screen (MachInGameScreen), laid out as its 640 × 480
/// screen and scaled up: at the top the continent map (the race logo until a beacon stands) with the BMU button
/// and the navigator icons beside it; below, the selected machine or building, or the corral of several, then the
/// command buttons, then the build, production or research icons. What the mouse points at is described in the
/// prompt line beside the panel. The icons are the original's, from the player's game copy; the panel itself is
/// drawn plainly, and without a game copy the icons are words.
/// </summary>
public partial class ClassicPanel(Game game, Texture2D? planetMap) : Control
{
    /// <summary>The panel's size in original pixels (MachGui::controlPanelOutXPos, mapAreaHeight).</summary>
    public const int PanelWidth = 169, MapAreaHeight = 129;

    private const int IconSize = 38, ButtonWidth = 42, HealthBarHeight = 6;

    // A command button (MachCommandIcon): the original's 73 × 10 bitmap in a two-pixel border, two to a row with
    // five pixels between them (MachCommandIcons).
    private const int BarWidth = 77, BarHeight = 14, BarGap = 5;

    private sealed record Widget(Rect2 Rect, Action<Rect2> Draw, Action? Click = null, string? Prompt = null);

    private readonly List<Widget> _widgets = [];
    private float _scale = 1;
    private Widget? _hovered;
    private bool _showBmus = true;
    private bool _buildMenu;

    // The squadron bank (MachGuiSquadronBank): shown from the navigator to pick a squadron, or by the form
    // squadron command to fill one.
    private enum SquadronMode { None, Select, Form }
    private SquadronMode _squadrons;
    private string _selectionKey = "";

    /// <summary>A message for the prompt line when the mouse points at nothing in the panel (the cursor prompt).</summary>
    public string? Prompt { get; set; }

    /// <summary>What the command waiting for a click asks for; shown when there is no cursor prompt.</summary>
    public string? CommandPrompt { get; set; }

    /// <summary>The panel's width on screen, as far as it has slid out.</summary>
    public float ScreenWidth => _edge * _scale;

    // ---- sliding (MachInGameScreen's controlPanelXPos, MachGuiControlPanelOnOffBtn) --------------

    /// <summary>Where the panel's right edge stands when slid in: only its slider shows (controlPanelInXPos).</summary>
    private const int InEdge = 6;

    // Whether the player wants the panel out, and where its right edge is now, in the original's pixels.
    private bool _on = true;
    private float _edge = PanelWidth;
    private double _lastFrame;

    /// <summary>Hides or shows the panel (Alt and the left or right arrow; the slider at its edge).</summary>
    public void SlideOut(bool on) => _on = on;

    /// <summary>
    /// Some contexts bring the panel out even when it is put away: a navigator, the squadron bank, the build menu,
    /// a factory or lab, constructors (MachInGameScreen::displayControlPanel).
    /// </summary>
    private bool PanelNeeded()
    {
        if (_navigator != NavigatorKind.None || _squadrons != SquadronMode.None || _buildMenu)
            return true;
        var selected = game.Selection.Select(game.World.Find).OfType<Actor>().Where(a => a.Race == Game.Player).ToList();
        return selected.Count == 1 && selected[0] is Construction { Complete: true } building && building.Spec.Type is "FACTORY" or "HARDWARE_LAB"
            || selected.Count > 0 && selected.All(a => a is Machine { Spec.Type: "CONSTRUCTOR" });
    }

    /// <summary>
    /// Slides the panel towards where it should be: out at 20 pixels a frame of the original's 30, in at 15; it
    /// clicks out with ctrlpon.wav and in with ctrlpoff.wav.
    /// </summary>
    private void Slide()
    {
        var now = Time.GetTicksMsec() / 1000.0;
        var dt = _lastFrame == 0 ? 0 : (float)Math.Min(0.1, now - _lastFrame);
        _lastFrame = now;
        if (_on || PanelNeeded())
        {
            if (_edge < PanelWidth && _edge <= InEdge)
                game.Audio.PlayGui("gui/sounds/ctrlpon.wav");
            _edge = Math.Min(PanelWidth, _edge + 600 * dt);
        }
        else
        {
            if (_edge > InEdge && _edge >= PanelWidth)
                game.Audio.PlayGui("gui/sounds/ctrlpoff.wav");
            _edge = Math.Max(InEdge, _edge - 450 * dt);
        }
    }

    public float PixelScale => _scale;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    /// <summary>Rebuilds the panel for this frame.</summary>
    public void Refresh()
    {
        var viewport = GetViewportRect().Size;
        // The original's 480 lines fill the screen's height, in quarter steps; whole steps keep the icons sharp.
        _scale = Mathf.Max(1, Mathf.Floor(viewport.Y / 480f * 4) / 4);
        TextureFilter = _scale % 1 == 0 ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear;
        Slide();
        Position = new Vector2((_edge - PanelWidth) * _scale, 0);
        Size = new Vector2(PanelWidth * _scale, viewport.Y);

        var selectionKey = string.Join(',', game.Selection);
        if (selectionKey != _selectionKey)
        {
            _selectionKey = selectionKey;
            _buildMenu = false;
            _coverOpenedAt = null;
            _corralScrolled = 0;
            // A change of selection closes a navigator, unless the navigator made it with Ctrl (checkDismissNavigator).
            if (!_keepNavigator)
                _navigator = NavigatorKind.None;
            _keepNavigator = false;
            if (_squadrons == SquadronMode.Form)
                _squadrons = SquadronMode.None;
        }

        _widgets.Clear();
        _corralArea = null;
        AddCameraTab();
        AddSlider();
        AddMapArea();
        var y = MapAreaHeight;
        y = AddSelection(y);
        // With a navigator open, it takes the place of the commands and menus (MACHINE_NAVIGATION_MENU).
        if (_navigator != NavigatorKind.None && !game.Watching)
            AddNavigator();
        else
        {
            y = AddCommands(y + 2);
            AddContextIcons(y + 2);
        }
        if (_hovered is not null)
            _hovered = _widgets.FirstOrDefault(w => w.Rect == _hovered.Rect);
        QueueRedraw();
    }

    // ---- layout ---------------------------------------------------------------------------------

    private Rect2 R(float x, float y, float w, float h) => new(x * _scale, y * _scale, w * _scale, h * _scale);

    /// <summary>
    /// The slider down the panel's right edge (6 by 49 pixels, 8 down): the knob at the top while the panel is out,
    /// at the bottom while it is in, half way while held.
    /// </summary>
    private void AddSlider() =>
        _widgets.Add(new Widget(R(PanelWidth - 6, 8, 6, 49), rect =>
        {
            DrawBitmap(rect, "misc/slidhole.bmp", "");
            var held = _hovered?.Rect == rect && Input.IsMouseButtonPressed(MouseButton.Left);
            if (ClassicGui.Bitmap("misc/slider.bmp") is { } knob)
                DrawTextureRect(knob, new Rect2(rect.Position + new Vector2(0, held ? 12 : _on ? 0 : 24) * _scale,
                    new Vector2(knob.GetWidth(), knob.GetHeight()) * _scale), false);
        }, () => _on = !_on, _on ? "Put the control panel away (Alt and left arrow)" : "Bring the control panel out (Alt and right arrow)"));

    /// <summary>
    /// The tab beside the panel (MachGuiControlPanelAddOn, camtab.bmp, 30 by 66 pixels at the panel's full width):
    /// the way back to the menus and, below it, the switch between the zenith and the ground camera. It stays put
    /// while the panel slides.
    /// </summary>
    private void AddCameraTab()
    {
        if (game.InFirstPerson)
            return;
        var x = PanelWidth + (PanelWidth - _edge);
        _widgets.Add(new Widget(R(x, 0, 30, 66), rect => DrawBitmap(rect, "misc/camtab.bmp", "")));
        _widgets.Add(new Widget(R(x + 4, 0, 20, 20), rect =>
        {
            var held = _hovered?.Rect == rect && Input.IsMouseButtonPressed(MouseButton.Left);
            DrawBitmap(rect, held ? "misc/return2.bmp" : "misc/return1.bmp", "Menu");
        }, game.OpenMenu, "Return to the menus (Esc)"));
        var zenith = game.ZenithCamera;
        _widgets.Add(new Widget(R(x + 4, 22, 22, 22), rect =>
        {
            var held = _hovered?.Rect == rect && Input.IsMouseButtonPressed(MouseButton.Left);
            DrawBevel(rect, new Color(144 / 255f, 148 / 255f, 160 / 255f), new Color(232 / 255f, 232 / 255f, 232 / 255f), new Color(62 / 255f, 62 / 255f, 62 / 255f), held);
            var bitmap = zenith ? "misc/ground.bmp" : "misc/zenith.bmp";
            if (ClassicGui.Bitmap(bitmap) is { } icon)
                DrawTextureRect(icon, new Rect2(rect.Position + Vector2.One * (held ? 3 : 2) * _scale, new Vector2(icon.GetWidth(), icon.GetHeight()) * _scale), false);
            else
                DrawText(rect.Position + Vector2.One * 2 * _scale, zenith ? "Gnd" : "Zen", Colors.White);
        }, game.ToggleCamera, zenith ? "Ground camera (0 on the number pad)" : "Zenith camera (2 on the number pad)"));
    }

    /// <summary>The camera tab lies outside the panel's rectangle but takes the mouse all the same.</summary>
    public override bool _HasPoint(Vector2 point) =>
        new Rect2(Vector2.Zero, Size).HasPoint(point) || !game.InFirstPerson && R(PanelWidth + (PanelWidth - _edge), 0, 30, 66).HasPoint(point);

    private void AddMapArea()
    {
        var world = game.World;
        var mapRect = R(2, 1, 134, 127);
        var beacon = world.Constructions.Any(c => c.Race == Game.Player && c.Complete && c.IsType("BEACON"));
        _widgets.Add(new Widget(mapRect, rect => DrawContinentMap(rect, beacon), null,
            beacon ? "Left click to look there, right click to send the selection" : "Build a beacon to see the planet"));

        var x = 136;
        _widgets.Add(new Widget(R(x, 0, 20, 20), rect => DrawBitmap(rect, "navigate/bmus.bmp", "BMU"), () => _showBmus = !_showBmus,
            $"BMUs {world.Races[Game.Player].Bmu:F0} of {world.BmuCapacity(Game.Player)}"));
        var race = RaceFolder(Game.Player);
        _widgets.Add(new Widget(R(x, 19, 20, 29), rect => DrawBitmap(rect, $"navigate/{race}/machines.bmp", "M"),
            () => _navigator = _navigator == NavigatorKind.Machines ? NavigatorKind.None : NavigatorKind.Machines,
            "Machine navigator"));
        _widgets.Add(new Widget(R(x, 47, 20, 29), rect => DrawBitmap(rect, $"navigate/{race}/construc.bmp", "C"),
            () => _navigator = _navigator == NavigatorKind.Constructions ? NavigatorKind.None : NavigatorKind.Constructions,
            "Construction navigator"));

        // Below right on the map: what it shows (units, resources or both) and the ground under it on or off
        // (MachGuiMapModeButton, MachGuiTerrainOnOffButton, 14 by 11 pixels).
        if (beacon)
        {
            _widgets.Add(new Widget(R(2 + 134 - 27, 1 + 127 - 11, 14, 11), rect => DrawMapButton(rect, "map/mapm.bmp", false),
                () => _mapMode = _mapMode switch { MapMode.UnitsOnly => MapMode.ResourcesOnly, MapMode.ResourcesOnly => MapMode.All, _ => MapMode.UnitsOnly },
                _mapMode switch { MapMode.UnitsOnly => "Map shows machines and buildings", MapMode.ResourcesOnly => "Map shows resources", _ => "Map shows everything" }));
            _widgets.Add(new Widget(R(2 + 134 - 14, 1 + 127 - 11, 14, 11), rect => DrawMapButton(rect, "map/mapt.bmp", _terrainOn),
                () => _terrainOn = !_terrainOn, "Terrain on or off"));
        }
        _widgets.Add(new Widget(R(x, 75, 20, 29), rect => DrawBitmap(rect, $"navigate/{race}/squads.bmp", "S"),
            () => _squadrons = _squadrons == SquadronMode.None ? SquadronMode.Select : SquadronMode.None,
            "Squadrons: pick one (or its number; Ctrl and the number forms one)"));
    }

    private int AddSelection(int y)
    {
        var world = game.World;
        var actors = game.Selection.Select(world.Find).OfType<Actor>().ToList();
        if (actors.Count == 0)
            return y;
        if (actors.Count == 1)
        {
            var actor = actors[0];
            var height = IconSize + 4 + HealthBarHeight;
            _widgets.Add(new Widget(R(1, y, ButtonWidth, height), rect => DrawActorButton(rect, actor), () => game.LookAt(actor.Position), Describe(actor)));
            var lines = Details(actor);
            _widgets.Add(new Widget(R(ButtonWidth + 2, y + 1, PanelWidth - ButtonWidth - 24, height), rect => DrawLines(rect, lines)));
            if (actor is Machine { Race: var r } driven && r == Game.Player && !game.Watching)
                _widgets.Add(new Widget(R(PanelWidth - 21, y, 20, 20), rect => DrawBitmap(rect, $"misc/{RaceFolder(r)}/inhead.bmp", "1st"),
                    game.EnterFirstPerson, "Drive it in first person (F)"));
            else if (actor is Construction { Garrisoned.Count: > 0 } garrison && garrison.Race == Game.Player && garrison.IsType("GARRISON"))
                _widgets.Add(new Widget(R(PanelWidth - 21, y, 20, 20), rect => DrawBitmap(rect, "misc/contents.bmp", "Out"),
                    () => game.Order(new StopCommand(Game.Player, garrison.Garrisoned.ToList())),
                    $"Let the {garrison.Garrisoned.Count} machines inside out"));
            return y + height;
        }

        // The corral: three columns of the selected machines; a click picks one out. When they do not all fit, it
        // scrolls between the original's scroll bars as the icon menus do (MachGuiCorral, a MachGuiScrollArea).
        var bottom = (int)(Size.Y / _scale) - 60;
        var rowHeight = IconSize + 4 + HealthBarHeight;
        var rows = Math.Max(1, Math.Min((actors.Count + 2) / 3, (bottom - y) / 2 / rowHeight));
        var scrolls = actors.Count > rows * 3;
        var last = Math.Max(0, (actors.Count + 2) / 3 * 3 - rows * 3);
        _corralScrolled = scrolls ? Math.Clamp(_corralScrolled, 0, last) : 0;
        var left = scrolls ? ScrollBarWidth + 1 : 14;
        var shown = actors.Skip(_corralScrolled).Take(rows * 3).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var actor = shown[i];
            _widgets.Add(new Widget(R(left + i % 3 * ButtonWidth, y + i / 3 * rowHeight, ButtonWidth, rowHeight), rect => DrawActorButton(rect, actor),
                () => game.Select([actor.Id]), Describe(actor)));
        }
        if (scrolls)
        {
            var (back, forward) = (_corralScrolled > 0, _corralScrolled < last);
            var height = Math.Min(ScrollButtonHeight, rows * rowHeight / 2);
            var right = left + 3 * ButtonWidth;
            void ScrollButton(int x, int top, string bitmap, bool lit, string prompt, Action act) =>
                _widgets.Add(new Widget(R(x, top, ScrollBarWidth, height), rect => DrawBitmap(rect, $"corral/{bitmap}{(lit ? "H" : "")}.bmp", bitmap[^1] == '2' ? "⇈" : "↑"),
                    lit ? act : null, lit ? prompt : null));
            ScrollButton(1, y, "scrlup2", back, "First row", () => _corralScrolled = 0);
            ScrollButton(1, y + height, "scrldn2", forward, "Last row", () => _corralScrolled = last);
            ScrollButton(right, y, "scrlup", back, "Scroll up", () => _corralScrolled = Math.Max(0, _corralScrolled - 3));
            ScrollButton(right, y + height, "scrldn", forward, "Scroll down", () => _corralScrolled = Math.Min(last, _corralScrolled + 3));
            _corralArea = R(1, y, right + ScrollBarWidth, rows * rowHeight);
        }
        return y + rows * rowHeight;
    }

    // How many of the selected the corral is scrolled past, a row of three at a time; where it lies, for the wheel.
    private int _corralScrolled;
    private Rect2? _corralArea;

    // Icon is the bitmap in gui/commands; a charging strike weapon lays Charged of its Overlay over it.
    private sealed record Bar(string Label, string Name, Action Act, string Icon, float? Charged = null, string? Overlay = null);

    // The self-destruct button (MachGuiSelfDestructCommandIcon): the first click slides its cover open, the second
    // starts the countdown. It shuts again when the selection changes.
    private double? _coverOpenedAt;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    private int AddCommands(int y)
    {
        var bars = Commands();
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            _widgets.Add(new Widget(R(2 + i % 2 * (BarWidth + BarGap), y + i / 2 * BarHeight, BarWidth, BarHeight), rect => DrawBar(rect, bar),
                () => { if (!game.RefusesOrders()) bar.Act(); }, bar.Name));
        }
        return y + (bars.Count + 1) / 2 * BarHeight;
    }

    /// <summary>
    /// The commands for the selection, in the original's order (MachInGameScreen::allCommands): self-destruct, move,
    /// stop, attack, construct, repair, capture, deconstruct, recycle, locate, mines, deploy, patrol, transport,
    /// scavenge, treachery, form squadron, heal, stand ground, assembly point, the strike weapons, initiative and
    /// camouflage.
    /// </summary>
    private List<Bar> Commands()
    {
        if (game.Watching)
            return [];
        var world = game.World;
        var bars = new List<Bar>();
        var selected = game.Selection.Select(world.Find).OfType<Actor>().Where(a => a.Race == Game.Player).ToList();
        var machines = selected.OfType<Machine>().ToList();
        var ids = machines.Select(m => m.Id).ToList();
        if (selected.Count > 0)
            bars.Add(SelfDestruct(selected));
        var constructors = machines.Where(m => m.Spec.Type == "CONSTRUCTOR").Select(m => m.Id).ToList();
        var spies = machines.Where(m => m.IsSpy).ToList();
        if (machines.Count > 0)
        {
            bars.Add(new Bar("Move", "Move: click where to", () => game.BeginTargeting("Click where to move; right click cancels", "move",
                (point, _) => game.Order(new MoveCommand(Game.Player, ids, point))), "commands/move.bmp"));
            bars.Add(new Bar("Stop", "Stop", () => game.Order(new StopCommand(Game.Player, ids)), "commands/stop.bmp"));
            if (machines.Any(m => m.CanFight))
                bars.Add(new Bar("Attack", "Attack: click the target", () => game.BeginTargeting("Click what to attack; right click cancels", "attack",
                    (_, target) =>
                    {
                        if (target is not null && world.AreEnemies(Game.Player, target.Race))
                            game.Order(new AttackCommand(Game.Player, ids.Where(id => world.Find<Machine>(id)?.CanFight == true).ToList(), target.Id));
                    }), "commands/attack.bmp"));
            if (constructors.Count > 0)
            {
                bars.Add(new Bar("Construct", "Construct: pick a building below", () => _buildMenu = !_buildMenu, "commands/const.bmp"));
                bars.Add(new Bar("Repair", "Repair: click a building", () => game.BeginTargeting("Click the building to repair; right click cancels", "repair",
                    (_, target) => game.Order(new RepairCommand(Game.Player, constructors, target?.Id ?? 0))), "commands/repair.bmp"));
                bars.Add(new Bar("Capture", "Capture: click an enemy building", () => game.BeginTargeting("Click the building to capture; right click cancels", "capture",
                    (_, target) => game.Order(new CaptureCommand(Game.Player, constructors, target?.Id ?? 0))), "commands/capture.bmp"));
                bars.Add(new Bar("Deconstruct", "Deconstruct: click a building", () => game.BeginTargeting("Click the building to take apart; right click cancels", "deconst",
                    (_, target) => game.Order(new DeconstructCommand(Game.Player, constructors, target?.Id ?? 0))), "commands/deconst.bmp"));
            }
            bars.Add(new Bar("Recycle", "Recycle at the nearest smelter: half the price back", () => game.Order(new RecycleCommand(Game.Player, ids)), "commands/recycle.bmp"));
            var locators = machines.Where(m => m.Spec.Type == "GEO_LOCATOR").Select(m => m.Id).ToList();
            if (locators.Count > 0)
                bars.Add(new Bar("Locate", "Locate: click where to search", () => game.BeginTargeting("Click where to search for minerals; right click cancels", "locat",
                    (point, _) => game.Order(new LocateCommand(Game.Player, locators, point))), "commands/locate.bmp"));
            if (spies.Any(s => s.Mines > 0))
                bars.Add(new Bar("Lay mines", $"Lay land mines (Shift for more points): {spies.Sum(s => s.Mines)} carried", () => game.BeginWaypoints(
                    "Click where to lay a mine, Shift for more; right click cancels", "landmin",
                    points => game.Order(new DropLandMinesCommand(Game.Player, spies.Where(s => s.Mines > 0).Select(s => s.Id).ToList(), points))),
                    "commands/dropmine.bmp"));
            if (spies.Count > 0)
                bars.Add(new Bar("Refill mines", "Refill land mines at the nearest garrison",
                    () => game.Order(new RefillLandMinesCommand(Game.Player, spies.Select(s => s.Id).ToList())), "commands/refill.bmp"));
            var apcs = machines.Where(m => m.IsType("APC") && m.Passengers.Count > 0).Select(m => m.Id).ToList();
            if (apcs.Count > 0)
                bars.Add(new Bar("Deploy", "Deploy: click where to let them out", () => game.BeginTargeting("Click where to let them out; right click cancels", "deploy",
                    (point, _) => game.Order(new DeployCommand(Game.Player, apcs, point))), "commands/deploy.bmp"));
            // Every machine patrols, holds its ground and has an initiative (MachGuiPatrolCommand, StandGround, Defcon).
            bars.Add(new Bar("Patrol", "Patrol (Shift for more points): it goes round them and back", () => game.BeginWaypoints(
                "Click where to patrol, Shift for more points; right click cancels", "patrol", points =>
                {
                    // Each patrols the points and back to where it stands now (the original adds its position last).
                    foreach (var machine in machines)
                        game.Order(new PatrolCommand(Game.Player, [machine.Id], [.. points, machine.Position]));
                }), "commands/patrol.bmp"));
            var carriers = machines.Where(m => m.Spec.Type == "RESOURCE_CARRIER" && !m.IsScavenger).Select(m => m.Id).ToList();
            if (carriers.Count > 0)
                bars.Add(new Bar("Transport", "Transport: click a mine, then a smelter or pod", () => game.BeginTargeting(
                    "Click the mine to fetch ore from; right click cancels", "pickup", (_, mine) =>
                    {
                        if (mine is not Construction { Spec.Type: "MINE" } from || from.Race != Game.Player)
                            return;
                        game.BeginTargeting("Click the smelter or pod to bring it to; right click cancels", "putdn", (_, store) =>
                        {
                            if (store is Construction { Spec.Type: "SMELTER" or "POD" } to && to.Race == Game.Player)
                                game.Order(new TransportCommand(Game.Player, carriers, from.Id, to.Id));
                        });
                    }), "commands/transprt.bmp"));
            var scavengers = machines.Where(m => m.IsScavenger).Select(m => m.Id).ToList();
            if (scavengers.Count > 0)
                bars.Add(new Bar("Scavenge", "Scavenge: click the wreckage to gather", () => game.BeginTargeting("Click the wreckage to gather; right click cancels", "scav",
                    (point, _) =>
                    {
                        if (game.DebrisNear(point) is { } wreck)
                            game.Order(new ScavengeCommand(Game.Player, scavengers, [wreck.Id]));
                    }), "commands/scavenge.bmp"));
            var traitors = machines.Where(m => m.Weapons.Any(w => w.IsTreachery)).Select(m => m.Id).ToList();
            if (traitors.Count > 0)
                bars.Add(new Bar("Treachery", "Treachery: click an enemy machine to turn it", () => game.BeginTargeting(
                    "Click the enemy machine to turn; right click cancels", "treach", (_, target) =>
                    {
                        if (target is Machine && world.AreEnemies(Game.Player, target.Race))
                            game.Order(new AttackCommand(Game.Player, traitors, target.Id));
                    }), "commands/treach.bmp"));
            bars.Add(new Bar("Form squadron", "Form squadron: click one of the squadrons below (or Ctrl and its number)",
                () => _squadrons = SquadronMode.Form, "commands/formsqad.bmp"));
            var healers = machines.Where(m => m.CanHeal).Select(m => m.Id).ToList();
            if (healers.Count > 0)
                bars.Add(new Bar("Heal", "Heal: click a machine", () => game.BeginTargeting("Click the machine to heal; right click cancels", "heal",
                    (_, target) => game.Order(new HealCommand(Game.Player, healers, target?.Id ?? 0))), "commands/heal.bmp"));
            bars.Add(new Bar("Stand ground", "Stand ground: shoot from where it is, never move to",
                () => game.Order(new StandGroundCommand(Game.Player, ids)), "commands/stand.bmp"));
        }
        // Factories send their new machines to an assembly point (B, as in the original).
        var factories = selected.OfType<Construction>().Where(c => c.Complete && c.IsType("FACTORY")).Select(c => c.Id).ToList();
        if (factories.Count > 0)
            bars.Add(new Bar("Assembly point", "Assembly point (B): click where new machines gather", game.BeginAssemblyPoint, "commands/assemble.bmp"));
        if (selected.Count == 1 && selected[0] is Construction { Complete: true } building
            && building.Weapons.FirstOrDefault(w => w.IsStrike) is { } strike)
        {
            // The strike weapons' buttons light up red from the left as they charge (MachGuiIonAttackCommandIcon).
            var icon = strike.Spec.TypeName == "NUCLEAR_MISSILE" ? "nukeattk" : "ionattk";
            var ready = strike.Reload <= 0;
            bars.Add(new Bar(icon == "nukeattk" ? "Nuke" : "Ion cannon",
                (icon == "nukeattk" ? "Launch the nuclear missile" : "Fire the ion cannon") + (ready ? "" : $": charging, {strike.ChargePercent:F0}%"),
                () => { if (ready) game.BeginStrike(building.Id); },
                $"commands/{icon}.bmp", strike.ChargePercent / 100f, icon == "nukeattk" ? "commands/nukeatkr.bmp" : "commands/ionattkr.bmp"));
        }
        if (machines.Count > 0)
        {
            // Its lights show which initiatives the selection has (defco1 low, defco2 normal, defco3 high DefCon).
            var (low, normal, high) = game.Initiative();
            var initiative = (low ? 1 : 0) + (normal ? 1 : 0) + (high ? 1 : 0) > 1 ? "mixed" : low ? "high" : normal ? "medium" : "low";
            bars.Add(new Bar($"Initiative {initiative}", $"Machine initiative: {initiative} (Tab to change)", game.CycleInitiative,
                $"commands/defco{(low ? "1" : "")}{(normal ? "2" : "")}{(high ? "3" : "")}.bmp"));
        }
        // Camouflage hides a spy from the other players' machines; there are only those in a network game.
        if (spies.Count > 0 && game.Online)
        {
            var hidden = spies.Count(s => s.Camouflaged);
            bars.Add(new Bar(hidden == spies.Count ? "Uncamouflage" : "Camouflage", "Camouflage: the enemy's machines pass it over",
                () => game.Order(new CamouflageCommand(Game.Player, spies.Select(s => s.Id).ToList())),
                hidden == 0 ? "commands/camooff.bmp" : hidden == spies.Count ? "commands/camoon.bmp" : "commands/camomix.bmp"));
        }
        return bars;
    }

    /// <summary>
    /// The self-destruct button: shut (selfdes1), a click slides its cover open (selfdes2 to 7, a frame every
    /// 0.05 s) and the next starts the five-second countdown, during which it flashes (selfdes7 and 8, four times a
    /// second) until a click calls it off.
    /// </summary>
    private Bar SelfDestruct(List<Actor> selected)
    {
        var world = game.World;
        var all = selected.Select(a => a.Id).ToList();
        if (selected.Select(a => a.SelfDestructAt).FirstOrDefault(t => t is not null) is { } at)
            return new Bar($"Destruct {Math.Max(0, at - world.Time):F0}", $"Self-destruct in {Math.Max(0, at - world.Time):F0} s: click to call it off",
                () => game.Order(new StopCommand(Game.Player, all)), (int)(Now * 4) % 2 == 1 ? "commands/selfdes8.bmp" : "commands/selfdes7.bmp");
        if (_coverOpenedAt is not { } opened)
            return new Bar("Self-destruct", "Self-destruct: click to open the cover", () => _coverOpenedAt = Now, "commands/selfdes1.bmp");
        var frame = Math.Min(6, 1 + (int)((Now - opened) / 0.05));
        return new Bar("Self-destruct", "Self-destruct: click again to blow up in five seconds",
            () => game.Order(new SelfDestructCommand(Game.Player, all)), $"commands/selfdes{frame + 1}.bmp");
    }

    /// <summary>The build menu of constructors, the production of a factory or the research of a lab.</summary>
    private void AddContextIcons(int y)
    {
        if (game.Watching)
            return;
        if (_squadrons != SquadronMode.None)
        {
            AddSquadronBank(y);
            return;
        }
        var world = game.World;
        var selected = game.Selection.Select(world.Find).OfType<Actor>().Where(a => a.Race == Game.Player).ToList();
        var icons = new List<(string Bitmap, string Short, string Prompt, Action Act)>();
        if (_buildMenu && selected.OfType<Machine>().Any(m => m.Spec.Type == "CONSTRUCTOR"))
        {
            foreach (var e in world.Buildable(Game.Player))
            {
                var cost = world.Data.Parm.Construction(e.SubType is null ? e.Type : $"{e.Type} {e.SubType}").Level(e.HardwareLevel).Cost;
                icons.Add(($"construc/{RaceFolder(Game.Player)}/{ConstructionPrefix(e.Type, e.SubType)}{e.HardwareLevel}.bmp",
                    $"{e.SubType ?? e.Type}"[..3] + e.HardwareLevel, $"{TypeName(e.Type, e.SubType)} level {e.HardwareLevel}: {cost} BMU",
                    () => game.BeginPlacement(e.Type, e.SubType, e.HardwareLevel)));
            }
        }
        else if (selected is [Construction { Complete: true } building])
        {
            var state = world.Races[Game.Player];
            if (building.IsType("FACTORY"))
            {
                foreach (var key in world.Data.FactoryOffer(building.Spec.SubType!, building.Stats.HardwareLevel).Where(state.Researched.Contains))
                    icons.Add((MachineBitmap(key, Game.Player), Short(key),
                        $"Build {KeyName(key)}: {ResearchTree.FactoryPrice(world.Data.ResearchTree.Get(key), world.Data.Parm)} BMU",
                        () => game.Issue(new BuildMachineCommand(Game.Player, building.Id, key))));
                if (building.ProductionQueue.Count > 0)
                    y = AddQueue(building, building.ProductionQueue, y,
                        key => building.ProductionSpent / Math.Max(1, ResearchTree.FactoryPrice(world.Data.ResearchTree.Get(key), world.Data.Parm)),
                        "Building", index => game.Issue(new CancelProductionCommand(Game.Player, building.Id, index)));
            }
            else if (building.IsType("HARDWARE_LAB"))
            {
                foreach (var item in world.Data.ResearchTree.Items.Where(i => world.WhyCannotResearch(Game.Player, i.Key, building) is null && !building.ResearchQueue.Contains(i.Key)))
                    icons.Add((MachineBitmap(item.Key, Game.Player), Short(item.Key),
                        $"Research {KeyName(item.Key)}: {world.ResearchCost(Game.Player, item)} RU, {item.BuildingCost} BMU",
                        () => game.Issue(new ResearchCommand(Game.Player, building.Id, item.Key))));
                if (building.ResearchQueue.Count > 0)
                    y = AddQueue(building, building.ResearchQueue, y, key =>
                    {
                        state.ResearchProgress.TryGetValue(key, out var progress);
                        return progress / Math.Max(1, world.ResearchCost(Game.Player, world.Data.ResearchTree.Get(key)));
                    }, "Researching", index => game.Issue(new CancelResearchCommand(Game.Player, building.Id, index)));
            }
        }
        var menu = _buildMenu ? "build" : string.Join(',', selected.Select(a => a.Id));
        if (menu != _iconMenu)
        {
            _iconMenu = menu;
            _scrolled = 0;
        }
        _iconArea = null;
        if (icons.Count == 0)
            return;

        // As the original's MachGuiScrollArea: the icons three to a row in the panel's height, between two bars of
        // scroll buttons, to the first and last row on the left and a row up or down on the right; a button lights
        // up while it can scroll. The mouse wheel scrolls a row too.
        const int columns = 3;
        var rows = Math.Max(1, ((int)(Size.Y / _scale) - 30 - y) / ButtonWidth);
        var visible = rows * columns;
        var last = Math.Max(0, (icons.Count + columns - 1) / columns * columns - visible);
        _scrolled = Math.Clamp(_scrolled, 0, last);
        var (back, forward) = (_scrolled > 0, _scrolled < last);
        for (var i = 0; i < visible && _scrolled + i < icons.Count; i++)
        {
            var icon = icons[_scrolled + i];
            _widgets.Add(new Widget(R(ScrollBarWidth + 1 + i % columns * ButtonWidth, y + i / columns * ButtonWidth, ButtonWidth, ButtonWidth),
                rect => DrawIcon(rect, icon.Bitmap, icon.Short), icon.Act, icon.Prompt));
        }
        _iconArea = R(1, y, 2 * ScrollBarWidth + columns * ButtonWidth, rows * ButtonWidth);

        var height = Math.Min(ScrollButtonHeight, rows * ButtonWidth / 2);
        var right = ScrollBarWidth + 1 + columns * ButtonWidth;
        void ScrollButton(int x, int top, string bitmap, bool lit, string prompt, Action act) =>
            _widgets.Add(new Widget(R(x, top, ScrollBarWidth, height), rect => DrawBitmap(rect, $"corral/{bitmap}{(lit ? "H" : "")}.bmp", bitmap[^1] == '2' ? "⇈" : "↑"),
                lit ? act : null, lit ? prompt : null));
        ScrollButton(1, y, "scrlup2", back, "First row", () => _scrolled = 0);
        ScrollButton(1, y + height, "scrldn2", forward, "Last row", () => _scrolled = last);
        ScrollButton(right, y, "scrlup", back, "Scroll up", () => ScrollIcons(-1));
        ScrollButton(right, y + height, "scrldn", forward, "Scroll down", () => ScrollIcons(1));
    }

    /// <summary>
    /// The squadron bank as the original laid it out (MachGuiSquadronBank): squadrons 1 to 9 and then 0 in two
    /// columns of five, each its number with the count of its members, and beside it the picture of its commander
    /// (an administrator among them), the static of a squadron without one, or black for an empty one. A click picks
    /// the squadron, or, forming one, makes the selection that squadron.
    /// </summary>
    private void AddSquadronBank(int y)
    {
        const int number = 29, admin = 40, height = 40, spacer = 22;
        var left = (PanelWidth - 2 * (number + admin) - spacer) / 2;
        var world = game.World;
        var forming = _squadrons == SquadronMode.Form;
        for (var slot = 0; slot < 10; slot++)
        {
            var squad = (slot + 1) % 10;
            var members = world.Machines.Where(m => m.Race == Game.Player && m.Squad == squad).ToList();
            var commander = members.FirstOrDefault(m => m.IsType("ADMINISTRATOR"));
            var x = left + slot / 5 * (number + admin + spacer);
            var top = y + slot % 5 * (height - 1);
            _widgets.Add(new Widget(R(x, top, number + admin, height), rect => DrawSquadron(rect, squad, members.Count, commander),
                () =>
                {
                    if (forming)
                    {
                        game.FormSquadron(squad);
                        _squadrons = SquadronMode.None;
                    }
                    else
                        game.SelectSquadron(squad);
                },
                (forming ? $"Form squadron {squad} from the selection" : $"Squadron {squad}: select it")
                + $" ({members.Count} machines{(commander is null ? "" : ", led by its administrator")})"));
        }
    }

    private void DrawSquadron(Rect2 rect, int squad, int members, Machine? commander)
    {
        var numberRect = new Rect2(rect.Position, new Vector2(29, 40) * _scale);
        DrawBitmap(numberRect, $"misc/squad{squad}.bmp", squad.ToString());
        // The count, right-aligned at the foot of the number (numsqd0 to numsqd9).
        var at = numberRect.End - new Vector2(4, 4 + 9) * _scale;
        foreach (var digit in members.ToString().Reverse())
        {
            if (ClassicGui.Bitmap($"misc/numsqd{digit}.bmp") is not { } picture)
            {
                DrawText(at - new Vector2(6 * _scale, 0), digit.ToString(), Colors.White);
                at.X -= 6 * _scale;
                continue;
            }
            at.X -= picture.GetWidth() * _scale;
            DrawTextureRect(picture, new Rect2(at, picture.GetSize() * _scale), false);
        }
        var adminRect = new Rect2(rect.Position + new Vector2(29, 0) * _scale, new Vector2(40, 40) * _scale);
        DrawRect(adminRect, Colors.Black);
        if (members == 0)
            return;
        var inner = new Rect2(adminRect.Position + Vector2.One * _scale, adminRect.Size - Vector2.One * 2 * _scale);
        if (commander is not null)
            DrawBitmap(inner, MachineBitmap(commander.Key, Game.Look(commander.Race)), "Adm");
        // No commander: the screen shows static, a new frame each time (nosquad1 to nosquad5).
        else if (ClassicGui.Bitmap($"misc/nosquad{Random.Shared.Next(1, 6)}.bmp") is { } noise)
            DrawTextureRect(noise, inner, false);
    }

    // The icon menu shown (the build menu, or the selected factory's or lab's) and how many of its icons are
    // scrolled past, a row of three at a time; where it lies on screen, for the mouse wheel.
    private string _iconMenu = "";
    private int _scrolled;
    private Rect2? _iconArea;

    /// <summary>The original's scroll bars: 17 pixels wide, each button 50 high (MachGuiScrollButton).</summary>
    private const int ScrollBarWidth = 17, ScrollButtonHeight = 50;

    /// <summary>Opens the self-destruct button's cover (for automated checks).</summary>
    public void OpenSelfDestructCover()
    {
        Refresh();
        _coverOpenedAt = Now;
    }

    /// <summary>Opens the squadron bank, to pick squadrons or (with <paramref name="forming"/>) to form one (for automated checks).</summary>
    public void OpenSquadrons(bool forming)
    {
        Refresh();
        _squadrons = forming ? SquadronMode.Form : SquadronMode.Select;
    }

    /// <summary>Opens the constructors' build menu, scrolled down by <paramref name="rows"/> (for automated checks).</summary>
    public void OpenBuildMenu(int rows)
    {
        Refresh();
        _buildMenu = true;
        _iconMenu = "build";
        _scrolled = rows * 3;
    }

    /// <summary>Scrolls the icon menu by rows (the end is held at the last full row by the next layout).</summary>
    private void ScrollIcons(int rows) => _scrolled = Math.Max(0, _scrolled + rows * 3);

    /// <summary>
    /// A factory's or lab's whole queue (MachGuiProductionBank), three to a row: the first in work with its
    /// progress, and any of them cancelled with a click. Returns where the rows end.
    /// </summary>
    private int AddQueue(Construction building, List<ResearchKey> queue, int y, Func<ResearchKey, float> progress, string doing, Action<int> cancel)
    {
        const int columns = 3;
        for (var i = 0; i < queue.Count; i++)
        {
            var key = queue[i];
            var index = i;
            float? share = i == 0 ? progress(key) : null;
            _widgets.Add(new Widget(R(14 + i % columns * ButtonWidth, y + i / columns * ButtonWidth, ButtonWidth, ButtonWidth),
                rect => DrawIcon(rect, MachineBitmap(key, Game.Player), Short(key), share),
                () => cancel(index),
                (i == 0 ? $"{doing} {KeyName(key)}: {share * 100:F0}%" : $"Queued: {KeyName(key)}") + " (click to cancel)"));
        }
        return y + (queue.Count + columns - 1) / columns * ButtonWidth + 4;
    }

    // ---- drawing --------------------------------------------------------------------------------

    public override void _Draw()
    {
        // The panel's own look; only the icons come from the original.
        DrawRect(new Rect2(Vector2.Zero, Size), Panel);
        DrawRect(new Rect2(Size.X - _scale, 0, _scale, Size.Y), Edge);
        DrawRect(new Rect2(0, MapAreaHeight * _scale - _scale, Size.X, _scale), Edge);
        foreach (var widget in _widgets)
            widget.Draw(widget.Rect);
        if (_hovered?.Rect is { } hover && _hovered.Click is not null)
            DrawRect(hover, new Color(1, 1, 1, 0.12f), filled: true);

        DrawPrompt();
    }

    // The prompt on show and when it came, for its wipe in from the left.
    private string? _promptShown;
    private double _promptSince;

    /// <summary>
    /// The prompt line beside the panel at the foot of the screen (MachPromptText): what the mouse points at, or
    /// failing that what the command in hand wants. Up to two lines in the original's prompt font with its shadow,
    /// wiped in from the left at 20 pixels a frame while the lamp beside it is lit.
    /// </summary>
    private void DrawPrompt()
    {
        var prompt = _hovered?.Prompt ?? Prompt ?? CommandPrompt;
        if (prompt != _promptShown)
            (_promptShown, _promptSince) = (prompt, Time.GetTicksMsec() / 1000.0);
        if (prompt is not { Length: > 0 })
            return;
        var origin = new Vector2((PanelWidth - 7) * _scale, Size.Y - 24 * _scale);
        var right = GetViewportRect().Size.X - Position.X;
        if (ClassicFont.Get("menu/promtfnt.bmp") is not { } font || ClassicFont.Get("menu/promdfnt.bmp") is not { } shadow)
        {
            var at = new Vector2(origin.X + 13 * _scale, Size.Y - 16 * _scale);
            var size = Font.GetStringSize(prompt, HorizontalAlignment.Left, -1, FontSize).X;
            DrawRect(new Rect2(at - new Vector2(4, 2) * _scale, new Vector2(size + 8 * _scale, 13 * _scale)), new Color(0, 0, 0, 0.55f));
            DrawText(at, prompt, Colors.White);
            return;
        }
        var lamp = ClassicGui.Bitmap("misc/tplight1.bmp");
        var lampWidth = lamp?.GetWidth() ?? 6;
        var textAt = origin + new Vector2(lampWidth + 1, 7) * _scale;
        var width = (int)((right - textAt.X) / _scale);
        var lines = font.Chop(prompt, width).Take(2).ToList();
        var wiped = (int)((Time.GetTicksMsec() / 1000.0 - _promptSince) * 600);
        var lit = wiped < width;
        if (ClassicGui.Bitmap(lit ? "misc/tplight2.bmp" : "misc/tplight1.bmp") is { } light)
            DrawTextureRect(light, new Rect2(origin, new Vector2(light.GetWidth(), light.GetHeight()) * _scale), false);
        var y = lines.Count == 1 ? shadow.Height / 2 : 0;
        foreach (var line in lines)
        {
            var at = textAt + new Vector2(0, y) * _scale;
            shadow.Draw(this, at + Vector2.One * _scale, line, _scale, Math.Min(wiped, width - 1));
            font.Draw(this, at, line, _scale, Math.Min(wiped, width));
            y += shadow.Height + 1;
        }
    }

    private static readonly Color Panel = new(0.11f, 0.12f, 0.14f, 0.96f), Edge = new(0.32f, 0.35f, 0.4f),
        Button = new(0.2f, 0.22f, 0.26f), ButtonEdge = new(0.42f, 0.46f, 0.52f), Accent = new(0.95f, 0.75f, 0.25f),
        DarkGrey = new(0.3f, 0.3f, 0.3f), LightGrey = new(0.7f, 0.7f, 0.7f);

    private static Font Font => ThemeDB.FallbackFont;

    private int FontSize => (int)(6.5f * _scale);

    // What the continent map shows (MachContinentMap::MapMode; it starts with units only) and whether it shows the
    // ground beneath them.
    private enum MapMode { UnitsOnly, ResourcesOnly, All }

    private MapMode _mapMode = MapMode.UnitsOnly;
    private bool _terrainOn = true;

    /// <summary>A map button: a very dark grey frame and a bevel, sunk while it is on.</summary>
    private void DrawMapButton(Rect2 rect, string bitmap, bool on)
    {
        DrawBevel(rect, new Color(0.18f, 0.18f, 0.18f), LightGrey, DarkGrey, on);
        DrawBitmap(new Rect2(rect.Position + Vector2.One * (on ? 3 : 2) * _scale, rect.Size - Vector2.One * 4 * _scale), bitmap, "");
    }

    private void DrawContinentMap(Rect2 rect, bool beacon)
    {
        var world = game.World;
        var logo = ClassicGui.Bitmap($"map/{RaceFolder(Game.Player)[0]}logo.bmp");
        if (beacon && !_terrainOn)
            DrawRect(rect, Colors.Black);
        else if (beacon && planetMap is not null)
            DrawTextureRect(planetMap, rect, false);
        else if (logo is not null)
            DrawTextureRect(logo, rect, false);
        else
            DrawRect(rect, new Color(0.15f, 0.15f, 0.18f));

        Vector2 ToMap(NVector2 p) => rect.Position + new Vector2(p.X / world.Planet.Surface.Width * rect.Size.X, p.Y / world.Planet.Surface.Depth * rect.Size.Y);
        if (beacon)
        {
            if (_mapMode != MapMode.UnitsOnly)
            {
                var ore = ClassicGui.Bitmap("map/ore.bmp");
                // Every found deposit, whoever found it, as the original's map showed every ore hologram.
                foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Count > 0 && s.Ore > 0))
                    DrawPixel(ToMap(site.Position), ore, new Color(1, 0.9f, 0.2f));
                var debris = ClassicGui.Bitmap("map/debris.bmp");
                foreach (var wreck in world.Debris)
                    DrawPixel(ToMap(wreck.Position), debris, new Color(0.5f, 0.5f, 0.5f));
            }
            foreach (var actor in _mapMode == MapMode.ResourcesOnly ? [] : world.Actors.Where(a => a is not Machine { Inside: not null }))
            {
                // A camouflaged spy shows in the colours of whoever looks at the map (MachContinentMap's CAMOUFLAGEDMACHINE).
                var shownRace = actor is Machine { Camouflaged: true } ? Game.Player : actor.Race;
                var letter = RaceFolder(shownRace)[0];
                var kind = actor is Machine ? "machpix" : actor is Construction c && c.IsType("POD") ? "podpix" : "conspix";
                DrawPixel(ToMap(actor.Position), ClassicGui.Bitmap($"map/{letter}{kind}.bmp"), ClassicAssets.RaceColour(Game.Look(shownRace)));
            }
            // Below a level 3 beacon the map shows only what the player's machines have uncovered.
            if (game.Fog is { } fog && fog.Beacon < 3)
                DrawTextureRect(fog.Mask, rect, false);
            if (GetViewport().GetCamera3D() is { } camera)
            {
                var at = ToMap(new NVector2(camera.GlobalPosition.X, camera.GlobalPosition.Z));
                if (ClassicGui.Bitmap("map/campos.bmp", blackIsClear: true) is { } marker)
                    DrawTextureRect(marker, new Rect2(at - new Vector2(2.5f, 2.5f) * _scale, new Vector2(5, 5) * _scale), false);
                else
                    DrawCircle(at, 2 * _scale, Colors.White);
            }
        }
        if (_showBmus)
        {
            var state = world.Races[Game.Player];
            var text = $"{state.Bmu:F0} / {world.BmuCapacity(Game.Player)} BMU";
            DrawRect(new Rect2(rect.Position, new Vector2(Font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize).X + 4 * _scale, 9 * _scale)), new Color(0, 0, 0, 0.6f));
            DrawText(rect.Position + new Vector2(2, 1) * _scale, text, Accent);
        }
    }

    private void DrawPixel(Vector2 at, Texture2D? pixel, Color fallback)
    {
        if (pixel is not null)
            DrawTextureRect(pixel, new Rect2(at - new Vector2(2, 2) * _scale, new Vector2(4, 4) * _scale), false);
        else
            DrawRect(new Rect2(at - Vector2.One * 1.5f * _scale, Vector2.One * 3 * _scale), fallback);
    }

    private void DrawBitmap(Rect2 rect, string path, string fallback)
    {
        if (ClassicGui.Bitmap(path) is { } texture)
        {
            DrawTextureRect(texture, rect, false);
            return;
        }
        DrawRect(rect, new Color(0.2f, 0.2f, 0.22f));
        DrawRect(rect, new Color(0.6f, 0.6f, 0.6f), filled: false);
        DrawText(rect.Position + new Vector2(2, 2) * _scale, fallback, Colors.White);
    }

    /// <summary>
    /// A command button as MachCommandIcon draws it: a black frame round a dark grey bevel round the bitmap, which
    /// sinks a pixel while the button is held. Without a game copy it shows its name instead.
    /// </summary>
    private void DrawBar(Rect2 rect, Bar bar)
    {
        DrawRect(rect, Colors.Black);
        DrawRect(rect.Grow(-_scale), DarkGrey);
        var held = _hovered?.Rect == rect && Input.IsMouseButtonPressed(MouseButton.Left);
        var inner = new Rect2(rect.Position + Vector2.One * (held ? 3 : 2) * _scale, new Vector2(BarWidth - 4, BarHeight - 4) * _scale);
        var share = Mathf.Clamp(bar.Charged ?? 1, 0, 1);
        if (ClassicGui.Bitmap(bar.Icon) is { } icon)
        {
            DrawTextureRect(icon, inner, false);
            // A strike weapon's charged share shows its lit bitmap, with a black line where the charge has got to.
            if (bar.Overlay is { } overlay && ClassicGui.Bitmap(overlay) is { } lit)
            {
                var width = inner.Size.X * share;
                DrawTextureRectRegion(lit, new Rect2(inner.Position, new Vector2(width, inner.Size.Y)), new Rect2(0, 0, lit.GetWidth() * share, lit.GetHeight()));
                DrawRect(new Rect2(inner.Position.X + width, inner.Position.Y, _scale, inner.Size.Y), Colors.Black);
            }
            return;
        }
        DrawRect(inner, Button);
        if (bar.Charged is not null && share < 1)
            DrawRect(new Rect2(inner.Position, new Vector2(inner.Size.X * share, inner.Size.Y)), new Color(0.55f, 0.25f, 0.15f));
        var size = Font.GetStringSize(bar.Label, HorizontalAlignment.Left, -1, FontSize);
        DrawText(inner.Position + new Vector2((inner.Size.X - size.X) / 2, (inner.Size.Y - 9 * _scale) / 2 + _scale), bar.Label, Colors.White);
    }

    private void DrawIcon(Rect2 rect, string path, string fallback, float? progress = null)
    {
        var inner = new Rect2(rect.Position + Vector2.One * 2 * _scale, Vector2.One * IconSize * _scale);
        DrawBitmap(inner, path, fallback);
        if (progress is { } share)
        {
            var bar = new Rect2(inner.Position.X, inner.End.Y - 3 * _scale, inner.Size.X, 3 * _scale);
            DrawRect(bar, Colors.Black);
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * Mathf.Clamp(share, 0, 1), bar.Size.Y)), new Color(0.3f, 0.9f, 0.3f));
        }
    }

    private void DrawActorButton(Rect2 rect, Actor actor)
    {
        var path = actor switch
        {
            Machine m => MachineBitmap(m.Key, Game.Look(m.Race)),
            Construction c => $"construc/{RaceFolder(Game.Look(c.Race))}/{ConstructionPrefix(c.Spec.Type, c.Spec.SubType)}{c.Stats.HardwareLevel}.bmp",
            _ => "",
        };
        var inner = new Rect2(rect.Position + Vector2.One * 2 * _scale, Vector2.One * IconSize * _scale);
        DrawBitmap(inner, path, actor is Machine m2 ? Short(m2.Key) : "Bld");
        // The health bar under the picture (MachGuiHealthBar): hit points above, armour below.
        var bar = new Rect2(inner.Position.X, inner.End.Y + 1 * _scale, inner.Size.X, (HealthBarHeight - 1) * _scale);
        DrawRect(bar, Colors.Black);
        var hp = actor.HitPoints / (float)Math.Max(1, actor.MaxHitPoints);
        var hpColour = hp > 0.66f ? new Color(0.2f, 0.85f, 0.2f) : hp > 0.33f ? new Color(0.95f, 0.85f, 0.1f) : new Color(0.9f, 0.15f, 0.1f);
        DrawRect(new Rect2(bar.Position + Vector2.One * _scale * 0.5f, new Vector2((bar.Size.X - _scale) * hp, 2 * _scale)), hpColour);
        var armour = actor.Armour / (float)Math.Max(1, actor.MaxArmour);
        DrawRect(new Rect2(bar.Position + new Vector2(0.5f, 3) * _scale, new Vector2((bar.Size.X - _scale) * armour, 1.5f * _scale)), new Color(0.35f, 0.6f, 1f));
    }

    private void DrawLines(Rect2 rect, IReadOnlyList<string> lines)
    {
        var lineHeight = 8.5f * _scale;
        var y = rect.Position.Y;
        for (var i = 0; i < lines.Count; i++)
        {
            if (y + lineHeight > rect.End.Y + lineHeight * 3)
                break;
            DrawText(new Vector2(rect.Position.X, y), lines[i], i == 0 ? Accent : Colors.White, rect.Size.X);
            y += lineHeight;
        }
    }

    /// <summary>A line of text with its top left at <paramref name="at"/>, cut short at <paramref name="width"/> pixels.</summary>
    private void DrawText(Vector2 at, string text, Color colour, float width = -1) =>
        DrawString(Font, at + new Vector2(0, 7 * _scale), text, HorizontalAlignment.Left, width, FontSize, colour,
            TextServer.JustificationFlag.None, TextServer.Direction.Auto, TextServer.Orientation.Horizontal);

    // ---- input ----------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseMotion motion:
                _hovered = _widgets.LastOrDefault(w => w.Rect.HasPoint(motion.Position));
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                var widget = _widgets.LastOrDefault(w => w.Rect.HasPoint(click.Position));
                // The control panel clicks as the original's did (igclick.wav on its buttons, icons and map).
                if (widget?.Click is { } act)
                {
                    game.Audio.PlayGui("gui/sounds/igclick.wav");
                    act();
                }
                else if (InMap(click.Position) is { } ground)
                {
                    game.Audio.PlayGui("gui/sounds/igclick.wav");
                    game.LookAt(ground);
                }
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right:
                if (InMap(right.Position) is { } target && game.SelectedMachines().Where(m => m.Race == Game.Player).Select(m => m.Id).ToList() is { Count: > 0 } ids)
                    game.Order(new MoveCommand(Game.Player, ids, target));
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel
                when _iconArea is { } area && area.HasPoint(wheel.Position):
                ScrollIcons(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel
                when _corralArea is { } corral && corral.HasPoint(wheel.Position):
                _corralScrolled = Math.Max(0, _corralScrolled + (wheel.ButtonIndex == MouseButton.WheelUp ? -3 : 3));
                AcceptEvent();
                break;
        }
    }

    /// <summary>The ground point under a click on the continent map, when the map shows the planet.</summary>
    private NVector2? InMap(Vector2 position)
    {
        var rect = R(2, 1, 134, 127);
        var world = game.World;
        if (!rect.HasPoint(position) || !world.Constructions.Any(c => c.Race == Game.Player && c.Complete && c.IsType("BEACON")))
            return null;
        var f = (position - rect.Position) / rect.Size;
        return new NVector2(f.X * world.Planet.Surface.Width, f.Y * world.Planet.Surface.Depth);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
            _hovered = null;
    }

    // ---- actions --------------------------------------------------------------------------------

    // ---- navigators (MachGuiMachineNavigator, MachGuiConstructionNavigator) ----------------------

    private enum NavigatorKind { None, Machines, Constructions }

    private NavigatorKind _navigator;

    // A navigator button picked with Ctrl keeps the navigator open through the change of selection it makes.
    private bool _keepNavigator;

    // Where Alt-clicking each button has got to in cycling the camera through its actors.
    private readonly Dictionary<string, int> _navigatorCycle = [];

    /// <summary>One button: what it stands for (no subtype for any), its bitmap and its name in the prompt.</summary>
    private sealed record NavButton(string Type, string? SubType, string Bitmap, string Name);

    // Each row: the type as a whole (all.bmp), then its kinds, as buildMachineIcons and buildConstructIcons lay them out.
    private static readonly NavButton[][] MachineRows =
    [
        [new("AGGRESSOR", null, "", "aggressors"), new("AGGRESSOR", "GRUNT", "nagg", "grunts"), new("AGGRESSOR", "ASSASSIN", "naga", "assassins"),
            new("AGGRESSOR", "BALLISTA", "nagb", "ballistas"), new("AGGRESSOR", "KNIGHT", "nagk", "knights"), new("AGGRESSOR", "NINJA", "nagm", "ninjas")],
        [new("ADMINISTRATOR", null, "", "administrators"), new("ADMINISTRATOR", "BOSS", "nadb", "bosses"),
            new("ADMINISTRATOR", "OVERSEER", "nado", "overseers"), new("ADMINISTRATOR", "COMMANDER", "nadc", "commanders")],
        [new("TECHNICIAN", null, "", "technicians"), new("TECHNICIAN", "LAB_TECH", "ntel", "lab techs"),
            new("TECHNICIAN", "TECH_BOY", "ntet", "tech boys"), new("TECHNICIAN", "BRAIN_BOX", "nteb", "brain boxes")],
        [new("CONSTRUCTOR", null, "", "constructors"), new("CONSTRUCTOR", "DOZER", "ncod", "dozers"),
            new("CONSTRUCTOR", "BUILDER", "ncob", "builders"), new("CONSTRUCTOR", "BEHEMOTH", "ncom", "behemoths")],
        [new("LOCATOR", null, "", "locators"), new("GEO_LOCATOR", null, "nlog", "geo locators"), new("SPY_LOCATOR", null, "nlos", "spy locators")],
        [new("CARRIER", null, "", "carriers"), new("RESOURCE_CARRIER", null, "ntrr", "resource carriers"), new("APC", null, "ntrp", "APCs")],
    ];

    private static readonly NavButton[][] ConstructionRows =
    [
        [new("PODS", null, "", "pods and beacons"), new("POD", null, "pod", "pods"), new("BEACON", null, "beacon", "beacons")],
        [new("MISSILE_EMPLACEMENT", null, "", "missile emplacements"), new("MISSILE_EMPLACEMENT", "TURRET", "turret", "turrets"),
            new("MISSILE_EMPLACEMENT", "SENTRY", "sentry", "sentries"), new("MISSILE_EMPLACEMENT", "LAUNCHER", "launche", "launchers"),
            new("MISSILE_EMPLACEMENT", "ICBM", "icbm", "ICBMs")],
        [new("FACTORY", null, "", "factories"), new("FACTORY", "CIVILIAN", "civfac", "civilian factories"), new("FACTORY", "MILITARY", "milfac", "military factories")],
        [new("HARDWARE_LAB", null, "", "labs"), new("HARDWARE_LAB", "CIVILIAN", "civlab", "civilian labs"), new("HARDWARE_LAB", "MILITARY", "millab", "military labs")],
        [new("SMELTERS", null, "", "smelters and mines"), new("SMELTER", null, "smelt", "smelters"), new("MINE", null, "mine", "mines")],
        [new("GARRISON", null, "", "garrisons"), new("GARRISON", null, "garris", "garrisons")],
    ];

    /// <summary>
    /// The player's actors a button stands for: of its type and kind, or of every kind in its row for the first.
    /// A machine in an APC is there as the APC (cycleThroughActors).
    /// </summary>
    private List<Actor> NavActors(NavButton[] row, NavButton button)
    {
        var kinds = button.Bitmap == "" ? row.Skip(1).ToArray() : [button];
        bool Is(Actor a, NavButton k) => a switch
        {
            Machine m => m.IsType(k.Type, k.SubType),
            Construction c => c.IsType(k.Type, k.SubType),
            _ => false,
        };
        return game.World.Actors.Where(a => a.Race == Game.Player && !a.IsDead && kinds.Any(k => Is(a, k))).OrderBy(a => a.Id).ToList();
    }

    /// <summary>
    /// A kind's button appears once its race may have that kind (MachLogResearchTree::activated) or owns one; it
    /// greys out while there are none.
    /// </summary>
    private bool NavSwitchedOn(NavButton button, int count)
    {
        if (count > 0)
            return true;
        var state = game.World.Races[Game.Player];
        return state.Available.Concat(state.Researched).Any(k => k.Type == button.Type && (button.SubType is null || k.SubType == button.SubType));
    }

    /// <summary>
    /// The navigator at the foot of the panel: six rows, each the type's button and then its kinds, 20 and 28 by 33
    /// pixels, with the number of each in its corner and the dismiss button bottom right. A click selects them all
    /// (and closes it), with Ctrl adds or takes them away (and keeps it open), with Alt looks at the next of them.
    /// </summary>
    private void AddNavigator()
    {
        var rows = _navigator == NavigatorKind.Machines ? MachineRows : ConstructionRows;
        var top = (int)(Size.Y / _scale) - 6 * 33;
        for (var r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            var x = 1;
            foreach (var button in row)
            {
                var actors = NavActors(row, button);
                var all = button.Bitmap == "";
                if (!all && !NavSwitchedOn(button, actors.Count))
                    continue;
                var width = all ? 20 : 28;
                var selected = actors.Count > 0 && actors.All(a => game.Selection.Contains(a.Id));
                var bitmap = all ? "navigate/all.bmp"
                    : $"navigate/{(button.Type is "POD" or "BEACON" or "MISSILE_EMPLACEMENT" or "FACTORY" or "HARDWARE_LAB" or "SMELTER" or "MINE" or "GARRISON" ? "construc" : "machines")}/{(actors.Count > 0 ? RaceFolder(Game.Player) : "grey")}/{button.Bitmap}.bmp";
                var key = $"{r}/{button.Type}/{button.SubType}/{button.Bitmap}";
                _widgets.Add(new Widget(R(x, top + r * 33, width, 33), rect => DrawNavButton(rect, bitmap, actors.Count, selected, all ? "All" : Short(button.Name)),
                    () => NavClick(actors, selected, key), $"Select all {button.Name} (Ctrl adds, Alt looks at the next)"));
                x += width;
            }
        }
        _widgets.Add(new Widget(R(1 + 5 * 28, top + 5 * 33, 28, 33), rect => DrawBitmap(rect, "navigate/nonav.bmp", "X"),
            () => _navigator = NavigatorKind.None, "Close the navigator (Esc)"));
    }

    private static string Short(string name) => name.Length <= 4 ? name : name[..4];

    private void NavClick(List<Actor> actors, bool selected, string key)
    {
        if (Input.IsKeyPressed(Key.Alt))
        {
            if (actors.Count == 0)
                return;
            _navigatorCycle.TryGetValue(key, out var next);
            var actor = actors[next % actors.Count];
            _navigatorCycle[key] = next + 1;
            game.LookAt(actor is Machine { Inside: { } apc } && game.World.Find<Machine>(apc) is { } carrier ? carrier.Position : actor.Position);
            return;
        }
        var ids = actors.Select(a => a.Id).ToList();
        if (Input.IsKeyPressed(Key.Ctrl))
        {
            _keepNavigator = true;
            game.Select(selected ? game.Selection.Except(ids).ToList() : game.Selection.Union(ids).ToList());
            return;
        }
        game.Select(ids);
        _navigator = NavigatorKind.None;
    }

    /// <summary>A navigator button (MachGuiNavButtonWithCounter): bevelled, green when its actors are selected, the count in navi digits.</summary>
    private void DrawNavButton(Rect2 rect, string bitmap, int count, bool selected, string fallback)
    {
        var held = _hovered?.Rect == rect && Input.IsMouseButtonPressed(MouseButton.Left);
        DrawBevel(rect, selected ? new Color(0, 0.8f, 0) : Colors.Black, LightGrey, DarkGrey, held);
        var inner = new Rect2(rect.Position + Vector2.One * (held ? 3 : 2) * _scale, rect.Size - Vector2.One * 4 * _scale);
        DrawBitmap(inner, bitmap, fallback);
        if (count > 0)
            DrawDigits(new Vector2(rect.End.X - 2 * _scale, rect.Position.Y + 25 * _scale), count, "navigate/navi");
    }

    /// <summary>
    /// A button's border (GuiFilledBorder, metrics 1, 1, 1): its frame, the highlight along the top and left, the
    /// shadow along the bottom and right, the two swapped while it is held down.
    /// </summary>
    private void DrawBevel(Rect2 rect, Color frame, Color highlight, Color shadow, bool held)
    {
        if (held)
            (highlight, shadow) = (shadow, highlight);
        var s = _scale;
        DrawRect(rect, frame);
        DrawRect(new Rect2(rect.Position + Vector2.One * s, rect.Size - Vector2.One * 2 * s), shadow);
        DrawRect(new Rect2(rect.Position + Vector2.One * s, new Vector2(rect.Size.X - 3 * s, s)), highlight);
        DrawRect(new Rect2(rect.Position + Vector2.One * s, new Vector2(s, rect.Size.Y - 3 * s)), highlight);
    }

    /// <summary>A number in the original's digit bitmaps, right-aligned at <paramref name="right"/> (MachGui::drawNumber).</summary>
    private void DrawDigits(Vector2 right, int number, string digits)
    {
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var x = right.X;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (ClassicGui.Bitmap($"{digits}{text[i]}.bmp") is not { } digit)
            {
                DrawText(new Vector2(x - 6 * _scale, right.Y), text[i].ToString(), Colors.White);
                x -= 6 * _scale;
                continue;
            }
            var size = new Vector2(digit.GetWidth(), digit.GetHeight()) * _scale;
            x -= size.X;
            DrawTextureRect(digit, new Rect2(new Vector2(x, right.Y), size), false);
        }
    }

    /// <summary>Opens the machine or construction navigator (for automated checks).</summary>
    public void OpenNavigator(bool machines)
    {
        Refresh();
        _navigator = machines ? NavigatorKind.Machines : NavigatorKind.Constructions;
    }

    /// <summary>Closes an open navigator; false if none was open (Esc then does what it otherwise does).</summary>
    public bool CloseNavigator()
    {
        if (_navigator == NavigatorKind.None)
            return false;
        _navigator = NavigatorKind.None;
        return true;
    }

    // ---- names ----------------------------------------------------------------------------------

    /// <summary>
    /// The prompt line for what the mouse points at in the world (MachInGameScreen::displayActorPromptText): its
    /// name, and for your own its weapons, hit and armour points, a carrier's load, a mine's ore left in its
    /// deposit and mined so far, and how far a building site has come.
    /// </summary>
    public string PromptFor(Actor actor)
    {
        var name = actor switch
        {
            Machine m when m.Race == Game.Player => KeyName(m.Key),
            Machine m => $"{TypeName(m.Spec.Type, m.Spec.SubType)} L{m.Stats.HardwareLevel}",
            Construction c => $"{TypeName(c.Spec.Type, c.Spec.SubType)} L{c.Stats.HardwareLevel}",
            _ => "",
        };
        if (actor.Race != Game.Player)
            return name;
        var parts = new List<string> { name };
        if (actor is not Construction { Complete: false })
            parts.Add($"HP {actor.HitPoints}  AP {actor.Armour}");
        switch (actor)
        {
            case Machine { Spec.Type: "RESOURCE_CARRIER", IsScavenger: false } carrier:
                parts.Add($"Ore {carrier.Ore}/{carrier.Stats.Capacity}");
                break;
            case Construction building:
                if (building.IsType("MINE"))
                {
                    parts.Add($"deposit {DepositOre(building):F0} BMU");
                    if (building.Complete)
                        parts.Add($"mined {building.Ore:F0}");
                }
                if (!building.Complete)
                    parts.Add($"constructed {100 * building.Built / Math.Max(1, building.Stats.Cost):F0}%");
                break;
        }
        return string.Join("   ", parts);
    }

    /// <summary>The prompt line over a found deposit's ore hologram: the BMUs of ore still in it.</summary>
    public static string DepositPrompt(MineralSiteState site) => $"Mineral Deposit   {site.Ore:F0} BMU";

    /// <summary>The ore left in a mine's deposit (none once it has run dry or when it has no site).</summary>
    private float DepositOre(Construction mine) => mine.MineralSite is { } id ? game.World.Sites[id].Ore : 0;

    private string Describe(Actor actor) => actor switch
    {
        Machine m => $"{KeyName(m.Key)}: {m.HitPoints}/{m.MaxHitPoints} HP, armour {m.Armour}/{m.MaxArmour}",
        Construction c => $"{TypeName(c.Spec.Type, c.Spec.SubType)} level {c.Stats.HardwareLevel}: {c.HitPoints}/{c.MaxHitPoints} HP",
        _ => "",
    };

    private List<string> Details(Actor actor)
    {
        var world = game.World;
        var lines = new List<string>();
        switch (actor)
        {
            case Machine m:
                lines.Add($"{TypeName(m.Spec.Type, m.Spec.SubType)} L{m.Stats.HardwareLevel}");
                if (m.Weapons.Count > 0)
                    lines.Add(string.Join(", ", m.Weapons.Select(w => Title(w.Spec.TypeName)).Distinct()));
                if (m.Spec.Type == "RESOURCE_CARRIER" && !m.IsScavenger)
                    lines.Add($"Ore {m.Ore}/{m.Stats.Capacity}");
                if (m.IsType("APC"))
                    lines.Add($"Carrying {world.CarriedVolume(m)}/{m.Stats.Capacity}");
                if (m.IsSpy)
                    lines.Add($"Mines {m.Mines}/{m.Stats.MineCapacity ?? 0}");
                if (m.Squad is { } squad && m.Race == Game.Player)
                    lines.Add($"Squadron {squad}");
                break;
            case Construction c:
                lines.Add($"{TypeName(c.Spec.Type, c.Spec.SubType)} L{c.Stats.HardwareLevel}");
                if (!c.Complete)
                    lines.Add($"Built {c.Built:F0}/{c.Stats.Cost}");
                if (c.IsType("MINE"))
                {
                    lines.Add($"Ore {c.Ore:F0}/{c.Stats.Capacity}");
                    // The deposit's ore left, as the original's single icon showed it (MachGuiCorralSingleIcon).
                    lines.Add($"Deposit {DepositOre(c):F0} BMU");
                }
                if (c.IsType("GARRISON"))
                    lines.Add($"Inside {c.Garrisoned.Count}/{GameWorld.GarrisonStations(c)}");
                if (c.IsType("HARDWARE_LAB"))
                    lines.Add($"Technicians {c.Technicians.Count}/{GameWorld.ResearchStations(c)}");
                break;
        }
        lines.Add($"HP {actor.HitPoints}/{actor.MaxHitPoints}");
        if (actor.SelfDestructAt is { } at)
            lines.Add($"Self-destruct {Math.Max(0, at - world.Time):F0} s");
        return lines;
    }

    private static string Title(string upper) =>
        string.Join(' ', upper.Split('_', ' ').Where(w => w.Length > 0).Select(w => w[..1] + w[1..].ToLowerInvariant()));

    private static string TypeName(string type, string? subType) => Title(subType is null ? type : $"{subType} {type}")
        .Replace("Missile Emplacement", "Emplacement");

    private static string KeyName(ResearchKey key) =>
        $"{TypeName(key.Type, key.SubType)} L{key.HardwareLevel}" + (key.WeaponCombo is { } combo ? $" ({Title(combo)})" : "");

    private static string Short(ResearchKey key) => (key.SubType ?? key.Type)[..3] + key.HardwareLevel;

    internal static string RaceFolder(Race race) => Game.Look(race).ToString().ToLowerInvariant();

    // ---- original bitmap names (MachActorBitmaps) -------------------------------------------------

    private static string MachineBitmap(ResearchKey key, Race race) =>
        $"machines/{RaceFolder(race)}/{MachinePrefix(key.Type, key.SubType)}{key.HardwareLevel}{WeaponSuffix(key.WeaponCombo)}.bmp";

    internal static string MachinePrefix(string type, string? subType) => (type, subType) switch
    {
        ("ADMINISTRATOR", "BOSS") => "adb",
        ("ADMINISTRATOR", "OVERSEER") => "ado",
        ("ADMINISTRATOR", "COMMANDER") => "adc",
        ("AGGRESSOR", "GRUNT") => "agg",
        ("AGGRESSOR", "ASSASSIN") => "aga",
        ("AGGRESSOR", "BALLISTA") => "agb",
        ("AGGRESSOR", "KNIGHT") => "agk",
        ("AGGRESSOR", "NINJA") => "agn",
        ("CONSTRUCTOR", "DOZER") => "cod",
        ("CONSTRUCTOR", "BUILDER") => "cob",
        ("CONSTRUCTOR", "BEHEMOTH") => "como",
        ("GEO_LOCATOR", _) => "log",
        ("SPY_LOCATOR", _) => "los",
        ("TECHNICIAN", "LAB_TECH") => "tel",
        ("TECHNICIAN", "TECH_BOY") => "tet",
        ("TECHNICIAN", "BRAIN_BOX") => "teb",
        ("RESOURCE_CARRIER", _) => "trr",
        ("APC", _) => "trp",
        _ => "bad",
    };

    private static string ConstructionPrefix(string type, string? subType) => (type, subType) switch
    {
        ("BEACON", _) => "bk",
        ("FACTORY", "CIVILIAN") => "fac",
        ("FACTORY", "MILITARY") => "fam",
        ("GARRISON", _) => "ga",
        ("HARDWARE_LAB", "CIVILIAN") => "cl",
        ("HARDWARE_LAB", "MILITARY") => "ml",
        ("POD", _) => "pd",
        ("MINE", _) => "mn",
        ("MISSILE_EMPLACEMENT", "TURRET") => "met",
        ("MISSILE_EMPLACEMENT", "SENTRY") => "mes",
        ("MISSILE_EMPLACEMENT", "LAUNCHER") => "mel",
        ("MISSILE_EMPLACEMENT", "ICBM") => "mei",
        ("SMELTER", _) => "sm",
        _ => "bad",
    };

    /// <summary>The weapon part of a machine's bitmap name (MachActorBitmaps::appendWeaponCombo).</summary>
    internal static string WeaponSuffix(string? combo) => combo switch
    {
        null => "",
        "L_BOLTER" or "R_BOLTER" => "bolt",
        "L_ELECTRIC_CHARGE" or "R_ELECTRIC_CHARGE" => "elec",
        "LR_PULSE_RIFLE_X2" or "LR_PULSE_CANNON_X2" => "puls",
        "L_PULSE_RIFLE_R_SUPERCHARGE_ADVANCED" or "L_PULSE_CANNON_R_SUPERCHARGE_SUPER" or "L_MULTI_LAUNCHER5_R_SUPERCHARGE_SUPER" => "schg",
        "LR_PLASMA_RIFLE_X2" or "L_PLASMA_RIFLE" or "LR_PLASMA_CANNON1_X2" or "LR_PLASMA_CANNON2_X2" or "LRT_PLASMA_CANNON2_X3" => "plsm",
        "L_MULTI_LAUNCHER5_R_TREACHERY" => "trch",
        "L_PULSE_CANNON_R_VIRUS" => "viru",
        "T_VORTEX" => "vort",
        "L_FLAME_THROWER2" or "T_FLAME_THROWER1" => "flm1",
        "LR_MULTI_LAUNCHER7_X2" or "LR_MULTI_LAUNCHER5_X2" or "LR_MULTI_LAUNCHER6_X2" or "LR_MULTI_LAUNCHER2_X2"
            or "LR_MULTI_LAUNCHER3_X2" or "LRT_MULTI_LAUNCHER4_X3" or "T_MULTI_LAUNCHER1" => "mult",
        "LR_HEAVY_BOLTER2_X2" => "hvy2",
        "LR_HEAVY_BOLTER1_X2" => "hvy1",
        "L_AUTO_CANNON" or "LR_AUTO_CANNON_X2" => "auto",
        "LR_LARGE_MISSILE_X2" => "lmis",
        "T_NUCLEAR_MISSILE" => "nuke",
        "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7" => "punc",
        "T_WASP_LIGHT_STING" => "lstg",
        "T_WASP_METAL_STING" => "mstg",
        "T_BEE_BOMB" => "bbmb",
        "T_ION_ORBITAL_CANNON" => "ion",
        _ => "",
    };
}
