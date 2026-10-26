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

    private const int IconSize = 38, ButtonWidth = 42, BarWidth = 81, BarHeight = 12, HealthBarHeight = 6;

    private sealed record Widget(Rect2 Rect, Action<Rect2> Draw, Action? Click = null, string? Prompt = null);

    private readonly List<Widget> _widgets = [];
    private float _scale = 1;
    private Widget? _hovered;
    private bool _showBmus = true;
    private bool _buildMenu;
    private string _selectionKey = "";

    /// <summary>A message for the prompt line when the mouse points at nothing in the panel.</summary>
    public string? Prompt { get; set; }

    /// <summary>The panel's width on screen.</summary>
    public float ScreenWidth => PanelWidth * _scale;

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
        Position = Vector2.Zero;
        Size = new Vector2(PanelWidth * _scale, viewport.Y);

        var selectionKey = string.Join(',', game.Selection);
        if (selectionKey != _selectionKey)
        {
            _selectionKey = selectionKey;
            _buildMenu = false;
        }

        _widgets.Clear();
        AddMapArea();
        var y = MapAreaHeight;
        y = AddSelection(y);
        y = AddCommands(y + 2);
        AddContextIcons(y + 2);
        if (_hovered is not null)
            _hovered = _widgets.FirstOrDefault(w => w.Rect == _hovered.Rect);
        QueueRedraw();
    }

    // ---- layout ---------------------------------------------------------------------------------

    private Rect2 R(float x, float y, float w, float h) => new(x * _scale, y * _scale, w * _scale, h * _scale);

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
        _widgets.Add(new Widget(R(x, 19, 20, 29), rect => DrawBitmap(rect, $"navigate/{race}/machines.bmp", "M"), SelectAllMachines,
            "Machines: select them all"));
        _widgets.Add(new Widget(R(x, 47, 20, 29), rect => DrawBitmap(rect, $"navigate/{race}/construc.bmp", "C"), NextConstruction,
            "Constructions: go to the next one"));
        _widgets.Add(new Widget(R(x, 75, 20, 29), rect => DrawBitmap(rect, $"navigate/{race}/squads.bmp", "S"), null,
            "Squadrons"));
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
            else if (actor is Construction { Garrisoned.Count: > 0 } garrison && garrison.Race == Game.Player)
                _widgets.Add(new Widget(R(PanelWidth - 21, y, 20, 20), rect => DrawBitmap(rect, "misc/contents.bmp", "Out"),
                    () => game.Issue(new StopCommand(Game.Player, garrison.Garrisoned.ToList())),
                    $"Let the {garrison.Garrisoned.Count} machines inside out"));
            return y + height;
        }

        // The corral: three columns of the selected machines; a click picks one out.
        var bottom = (int)(Size.Y / _scale) - 60;
        var rowHeight = IconSize + 4 + HealthBarHeight;
        var rows = Math.Max(1, Math.Min((actors.Count + 2) / 3, (bottom - y) / 2 / rowHeight));
        var shown = actors.Take(rows * 3).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var actor = shown[i];
            _widgets.Add(new Widget(R(14 + i % 3 * ButtonWidth, y + i / 3 * rowHeight, ButtonWidth, rowHeight), rect => DrawActorButton(rect, actor),
                () => game.Select([actor.Id]), Describe(actor)));
        }
        if (actors.Count > shown.Count)
        {
            var more = actors.Count - shown.Count;
            _widgets.Add(new Widget(R(14, y + rows * rowHeight, 3 * ButtonWidth, 9), rect => DrawLines(rect, [$"and {more} more"])));
            return y + rows * rowHeight + 10;
        }
        return y + rows * rowHeight;
    }

    private sealed record Bar(string Label, string Name, Action Act, float? Charged = null);

    private int AddCommands(int y)
    {
        var bars = Commands();
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            _widgets.Add(new Widget(R(2 + i % 2 * (BarWidth + 3), y + i / 2 * (BarHeight + 2), BarWidth, BarHeight), rect => DrawBar(rect, bar), bar.Act, bar.Name));
        }
        return y + (bars.Count + 1) / 2 * (BarHeight + 2);
    }

    private List<Bar> Commands()
    {
        if (game.Watching)
            return [];
        var world = game.World;
        var bars = new List<Bar>();
        var selected = game.Selection.Select(world.Find).OfType<Actor>().Where(a => a.Race == Game.Player).ToList();
        var machines = selected.OfType<Machine>().ToList();
        var ids = machines.Select(m => m.Id).ToList();
        if (machines.Count > 0)
        {
            bars.Add(new Bar("Move", "Move: click where to", () => game.BeginTargeting("Click where to move; right click cancels", "move",
                (point, _) => game.Issue(new MoveCommand(Game.Player, ids, point)))));
            bars.Add(new Bar("Stop", "Stop", () => game.Issue(new StopCommand(Game.Player, ids))));
            if (machines.Any(m => m.CanFight))
                bars.Add(new Bar("Attack", "Attack: click the target", () => game.BeginTargeting("Click what to attack; right click cancels", "attack",
                    (_, target) =>
                    {
                        if (target is not null && world.AreEnemies(Game.Player, target.Race))
                            game.Issue(new AttackCommand(Game.Player, ids.Where(id => world.Find<Machine>(id)?.CanFight == true).ToList(), target.Id));
                    })));
            var constructors = machines.Where(m => m.Spec.Type == "CONSTRUCTOR").Select(m => m.Id).ToList();
            if (constructors.Count > 0)
            {
                bars.Add(new Bar("Construct", "Construct: pick a building below", () => _buildMenu = !_buildMenu));
                bars.Add(new Bar("Repair", "Repair: click a building", () => game.BeginTargeting("Click the building to repair; right click cancels", "repair",
                    (_, target) => game.Issue(new RepairCommand(Game.Player, constructors, target?.Id ?? 0)))));
                bars.Add(new Bar("Capture", "Capture: click an enemy building", () => game.BeginTargeting("Click the building to capture; right click cancels", "capture",
                    (_, target) => game.Issue(new CaptureCommand(Game.Player, constructors, target?.Id ?? 0)))));
                bars.Add(new Bar("Deconstruct", "Deconstruct: click a building", () => game.BeginTargeting("Click the building to take apart; right click cancels", "deconst",
                    (_, target) => game.Issue(new DeconstructCommand(Game.Player, constructors, target?.Id ?? 0)))));
            }
            var locators = machines.Where(m => m.Spec.Type == "GEO_LOCATOR").Select(m => m.Id).ToList();
            if (locators.Count > 0)
                bars.Add(new Bar("Locate", "Locate: click where to search", () => game.BeginTargeting("Click where to search for minerals; right click cancels", "locat",
                    (point, _) => game.Issue(new LocateCommand(Game.Player, locators, point)))));
            var healers = machines.Where(m => m.CanHeal).Select(m => m.Id).ToList();
            if (healers.Count > 0)
                bars.Add(new Bar("Heal", "Heal: click a machine", () => game.BeginTargeting("Click the machine to heal; right click cancels", "heal",
                    (_, target) => game.Issue(new HealCommand(Game.Player, healers, target?.Id ?? 0)))));
            var apcs = machines.Where(m => m.IsType("APC") && m.Passengers.Count > 0).Select(m => m.Id).ToList();
            if (apcs.Count > 0)
                bars.Add(new Bar("Deploy", "Deploy: click where to let them out", () => game.BeginTargeting("Click where to let them out; right click cancels", "deploy",
                    (point, _) => game.Issue(new DeployCommand(Game.Player, apcs, point)))));
            bars.Add(new Bar("Recycle", "Recycle at the nearest smelter: half the price back", () => game.Issue(new RecycleCommand(Game.Player, ids))));
        }
        if (selected.Count == 1 && selected[0] is Construction { Complete: true } building
            && building.Weapons.FirstOrDefault(w => w.IsStrike) is { } strike)
        {
            var nuke = strike.Spec.TypeName == "NUCLEAR_MISSILE";
            var ready = strike.Reload <= 0;
            bars.Add(new Bar(nuke ? "Nuke" : "Ion cannon",
                (nuke ? "Launch the nuclear missile" : "Fire the ion cannon") + (ready ? "" : $": charging, {strike.ChargePercent:F0}%"),
                () => { if (ready) game.BeginStrike(building.Id); },
                strike.ChargePercent / 100f));
        }
        // Factories send their new machines to an assembly point (B, as in the original).
        var factories = selected.OfType<Construction>().Where(c => c.Race == Game.Player && c.Complete && c.IsType("FACTORY")).Select(c => c.Id).ToList();
        if (factories.Count > 0)
            bars.Add(new Bar("Assembly point", "Assembly point (B): click where new machines gather", game.BeginAssemblyPoint));
        if (selected.Count > 0)
        {
            var all = selected.Select(a => a.Id).ToList();
            var countdown = selected.Select(a => a.SelfDestructAt).FirstOrDefault(t => t is not null);
            if (countdown is { } at)
            {
                bars.Add(new Bar($"Destruct {Math.Max(0, at - world.Time):F0}", "Cancel the self-destruct", () => game.Issue(new StopCommand(Game.Player, all))));
            }
            else
            {
                bars.Add(new Bar("Self-destruct", "Self-destruct in five seconds", () => game.Issue(new SelfDestructCommand(Game.Player, all))));
            }
        }
        return bars;
    }

    /// <summary>The build menu of constructors, the production of a factory or the research of a lab.</summary>
    private void AddContextIcons(int y)
    {
        if (game.Watching)
            return;
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
        var columns = 3;
        var maxRows = Math.Max(0, ((int)(Size.Y / _scale) - 30 - y) / ButtonWidth);
        for (var i = 0; i < icons.Count && i / columns < maxRows; i++)
        {
            var icon = icons[i];
            _widgets.Add(new Widget(R(14 + i % columns * ButtonWidth, y + i / columns * ButtonWidth, ButtonWidth, ButtonWidth),
                rect => DrawIcon(rect, icon.Bitmap, icon.Short), icon.Act, icon.Prompt));
        }
    }

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

        // The prompt line, beside the panel at the foot of the screen (MachPromptText).
        if ((_hovered?.Prompt ?? Prompt) is { Length: > 0 } prompt)
        {
            var at = new Vector2(ScreenWidth + 6 * _scale, Size.Y - 16 * _scale);
            var width = Font.GetStringSize(prompt, HorizontalAlignment.Left, -1, FontSize).X;
            DrawRect(new Rect2(at - new Vector2(4, 2) * _scale, new Vector2(width + 8 * _scale, 13 * _scale)), new Color(0, 0, 0, 0.55f));
            DrawText(at, prompt, Colors.White);
        }
    }

    private static readonly Color Panel = new(0.11f, 0.12f, 0.14f, 0.96f), Edge = new(0.32f, 0.35f, 0.4f),
        Button = new(0.2f, 0.22f, 0.26f), ButtonEdge = new(0.42f, 0.46f, 0.52f), Accent = new(0.95f, 0.75f, 0.25f);

    private static Font Font => ThemeDB.FallbackFont;

    private int FontSize => (int)(6.5f * _scale);

    private void DrawContinentMap(Rect2 rect, bool beacon)
    {
        var world = game.World;
        var logo = ClassicGui.Bitmap($"map/{RaceFolder(Game.Player)[0]}logo.bmp");
        if (beacon && planetMap is not null)
            DrawTextureRect(planetMap, rect, false);
        else if (logo is not null)
            DrawTextureRect(logo, rect, false);
        else
            DrawRect(rect, new Color(0.15f, 0.15f, 0.18f));

        Vector2 ToMap(NVector2 p) => rect.Position + new Vector2(p.X / world.Planet.Surface.Width * rect.Size.X, p.Y / world.Planet.Surface.Depth * rect.Size.Y);
        if (beacon)
        {
            var ore = ClassicGui.Bitmap("map/ore.bmp");
            foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Contains(Game.Player)))
                DrawPixel(ToMap(site.Position), ore, new Color(1, 0.9f, 0.2f));
            foreach (var actor in world.Actors.Where(a => a is not Machine { Inside: not null }))
            {
                var letter = RaceFolder(actor.Race)[0];
                var kind = actor is Machine ? "machpix" : actor is Construction c && c.IsType("POD") ? "podpix" : "conspix";
                DrawPixel(ToMap(actor.Position), ClassicGui.Bitmap($"map/{letter}{kind}.bmp"), ClassicAssets.RaceColour(Game.Look(actor.Race)));
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

    private void DrawBar(Rect2 rect, Bar bar)
    {
        DrawRect(rect, Button);
        // A charging strike weapon fills its bar from the left as it charges.
        if (bar.Charged is { } share && share < 1)
            DrawRect(new Rect2(rect.Position, new Vector2(rect.Size.X * share, rect.Size.Y)), new Color(0.55f, 0.25f, 0.15f));
        DrawRect(rect, ButtonEdge, filled: false, width: Mathf.Max(1, _scale / 2));
        var size = Font.GetStringSize(bar.Label, HorizontalAlignment.Left, -1, FontSize);
        DrawText(rect.Position + new Vector2((rect.Size.X - size.X) / 2, (rect.Size.Y - 9 * _scale) / 2 + _scale), bar.Label, Colors.White);
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
                if (widget?.Click is { } act)
                    act();
                else if (InMap(click.Position) is { } ground)
                    game.LookAt(ground);
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right:
                if (InMap(right.Position) is { } target && game.SelectedMachines().Where(m => m.Race == Game.Player).Select(m => m.Id).ToList() is { Count: > 0 } ids)
                    game.Issue(new MoveCommand(Game.Player, ids, target));
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

    private void SelectAllMachines() =>
        game.Select(game.World.Machines.Where(m => m.Race == Game.Player && m.Inside is null).Select(m => m.Id).ToList());

    private int _nextConstruction;

    private void NextConstruction()
    {
        var own = game.World.Constructions.Where(c => c.Race == Game.Player).OrderBy(c => c.Id).ToList();
        if (own.Count == 0)
            return;
        var building = own[_nextConstruction++ % own.Count];
        game.Select([building.Id]);
        game.LookAt(building.Position);
    }

    // ---- names ----------------------------------------------------------------------------------

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
                if (m.Spec.Type == "RESOURCE_CARRIER")
                    lines.Add($"Ore {m.Ore}/{m.Stats.Capacity}");
                if (m.IsType("APC"))
                    lines.Add($"Carrying {world.CarriedVolume(m)}/{m.Stats.Capacity}");
                break;
            case Construction c:
                lines.Add($"{TypeName(c.Spec.Type, c.Spec.SubType)} L{c.Stats.HardwareLevel}");
                if (!c.Complete)
                    lines.Add($"Built {c.Built:F0}/{c.Stats.Cost}");
                if (c.IsType("MINE"))
                    lines.Add($"Ore {c.Ore:F0}/{c.Stats.Capacity}");
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

    private static string RaceFolder(Race race) => Game.Look(race).ToString().ToLowerInvariant();

    // ---- original bitmap names (MachActorBitmaps) -------------------------------------------------

    private static string MachineBitmap(ResearchKey key, Race race) =>
        $"machines/{RaceFolder(race)}/{MachinePrefix(key.Type, key.SubType)}{key.HardwareLevel}{WeaponSuffix(key.WeaponCombo)}.bmp";

    private static string MachinePrefix(string type, string? subType) => (type, subType) switch
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
    private static string WeaponSuffix(string? combo) => combo switch
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
