using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// Screen overlay: resources, messages, the selection panel with its command buttons, and the minimap.
/// </summary>
public partial class Hud : CanvasLayer
{
    private readonly Game _game;
    private readonly Texture2D? _map;
    private readonly List<(string Text, double Until)> _messages = [];

    private Label _resources = null!;
    private Label _messageLabel = null!;
    private Label _banner = null!;
    private Label _selectionLabel = null!;
    private GridContainer _buttons = null!;
    private Minimap _minimap = null!;
    private Control _dragBox = null!;
    private Label _crosshair = null!;
    private string _buttonsFor = "";

    public Hud(Game game, Texture2D? map)
    {
        _game = game;
        _map = map;
        Name = "Hud";
    }

    public Rect2? DragRect { get; set; }

    public bool FirstPerson
    {
        set => _crosshair.Visible = value;
    }

    public override void _Ready()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _resources = new Label { Position = new Vector2(12, 8) };
        _resources.AddThemeFontSizeOverride("font_size", 18);
        root.AddChild(_resources);

        _messageLabel = new Label { Position = new Vector2(12, 36) };
        root.AddChild(_messageLabel);

        _banner = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _banner.AddThemeFontSizeOverride("font_size", 64);
        _banner.SetAnchorsPreset(Control.LayoutPreset.Center);
        root.AddChild(_banner);

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        panel.Position = new Vector2(8, -8);
        panel.GrowVertical = Control.GrowDirection.Begin;
        panel.CustomMinimumSize = new Vector2(560, 0);
        root.AddChild(panel);
        var column = new VBoxContainer();
        panel.AddChild(column);
        _selectionLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(540, 0) };
        column.AddChild(_selectionLabel);
        _buttons = new GridContainer { Columns = 3 };
        column.AddChild(_buttons);

        _minimap = new Minimap(_game, _map) { CustomMinimumSize = new Vector2(220, 220) };
        _minimap.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _minimap.Position = new Vector2(-228, -228);
        root.AddChild(_minimap);

        _dragBox = new DragBox(this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _dragBox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_dragBox);

        _crosshair = new Label { Text = "+", Visible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _crosshair.AddThemeFontSizeOverride("font_size", 32);
        _crosshair.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_crosshair);

        var help = new Label
        {
            Text = "Left drag: select   Right click: move/attack/build/haul/locate   Ctrl+right: attack-move   F: first person   Q/E: rotate   P: pause",
            Modulate = new Color(1, 1, 1, 0.7f),
        };
        help.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        help.Position = new Vector2(-900, 8);
        root.AddChild(help);
    }

    public void Message(string text)
    {
        _messages.Add((text, Time.GetTicksMsec() / 1000.0 + 5));
        if (_messages.Count > 5)
            _messages.RemoveAt(0);
    }

    /// <summary>Shows a large message across the screen; null hides it.</summary>
    public void Banner(string? text)
    {
        _banner.Text = text ?? "";
        _banner.Visible = text is not null;
    }

    public void SelectionChanged() => _buttonsFor = "";

    /// <summary>Called every frame.</summary>
    public void Refresh()
    {
        var world = _game.World;
        var state = world.Races[Game.Player];
        var units = world.Machines.Count(m => m.Race == Game.Player);
        _resources.Text = $"BMU {state.Bmu:F0} / {world.BmuCapacity(Game.Player)}     Machines {units}     " +
                          $"Sites found {world.Sites.Count(s => s.DiscoveredBy.Contains(Game.Player))}     Score {state.Score.Gross}     " +
                          (world.TimeLeft is { } left ? $"Time left {TimeSpan.FromSeconds(left):h\\:mm\\:ss}" : $"Time {TimeSpan.FromSeconds(world.Time):h\\:mm\\:ss}") +
                          (_game.Paused ? "     PAUSED" : "");

        var now = Time.GetTicksMsec() / 1000.0;
        _messages.RemoveAll(m => m.Until < now);
        _messageLabel.Text = string.Join("\n", _messages.Select(m => m.Text));

        RefreshSelection();
        _minimap.QueueRedraw();
        _dragBox.QueueRedraw();
    }

    private void RefreshSelection()
    {
        var world = _game.World;
        var actors = _game.Selection.Select(world.Find).OfType<Actor>().ToList();
        if (actors.Count == 0)
        {
            _selectionLabel.Text = "Nothing selected";
            SetButtons("none", []);
            return;
        }

        if (actors.Count == 1)
        {
            var actor = actors[0];
            _selectionLabel.Text = Describe(actor);
            if (actor is Construction building && building.Race == Game.Player && building.Complete)
            {
                var (signature, buttons) =
                    building.IsType("FACTORY") ? ($"factory{building.Id}:{world.Races[Game.Player].Researched.Count}", FactoryButtons(building))
                    : building.IsType("HARDWARE_LAB") ? ($"lab{building.Id}:{world.Races[Game.Player].Researched.Count}:{building.ResearchQueue.Count}", LabButtons(building))
                    : building.Weapons.FirstOrDefault(w => w.IsStrike) is { } strike ? StrikeButton(building, strike)
                    : ($"building{building.Id}", new List<(string Label, Action Action)>());
                if (building.Garrisoned.Count > 0)
                    buttons.Add(($"Let out\n{building.Garrisoned.Count} machines",() => _game.Issue(new StopCommand(Game.Player, building.Garrisoned.ToList()))));
                buttons.Add(SelfDestructButton([building.Id], building.SelfDestructAt is not null));
                SetButtons($"{signature}:{building.SelfDestructAt is not null}:{building.Garrisoned.Count}", buttons);
                return;
            }
        }
        else
        {
            _selectionLabel.Text = string.Join(", ", actors.OfType<Machine>().GroupBy(m => MachineName(m)).Select(g => $"{g.Count()} x {g.Key}"));
        }

        var machines = _game.SelectedMachines().ToList();
        if (machines.Count == 0)
        {
            SetButtons("none", []);
            return;
        }

        var ids = machines.Select(m => m.Id).ToList();
        var counting = machines.Any(m => m.SelfDestructAt is not null);
        var common = new List<(string Label, Action Action)>
        {
            ("Recycle\nhalf price back", () => _game.Issue(new RecycleCommand(Game.Player, ids))),
            SelfDestructButton(ids, counting),
        };
        var apcs = machines.Where(m => m.IsType("APC")).ToList();
        var carried = apcs.Sum(m => m.Passengers.Count);
        if (carried > 0)
            common.Insert(0, ($"Deploy {carried}\nclick the point", () => _game.BeginTargeting("Left click where to let them out; right click cancels",
                (point, _) => _game.Issue(new DeployCommand(Game.Player, apcs.Select(a => a.Id).ToList(), point)))));
        var state = $"{counting}:{carried}";
        if (machines.Any(m => m.Spec.Type == "CONSTRUCTOR"))
        {
            common.Insert(0, ("Deconstruct\nclick a building", () => _game.BeginTargeting("Left click the building to take apart; right click cancels",
                (_, target) => _game.Issue(new DeconstructCommand(Game.Player, ids, target?.Id ?? 0)))));
            var buildable = world.Buildable(Game.Player);
            SetButtons($"build:{state}:" + string.Join(',', buildable), common.Concat(buildable.Select(e => (
                Label: $"{e.Type.Replace("MISSILE_EMPLACEMENT", "EMPLACEMENT")} {e.SubType} L{e.HardwareLevel}\n{world.Data.Parm.Construction(e.SubType is null ? e.Type : $"{e.Type} {e.SubType}").Level(e.HardwareLevel).Cost} BMU",
                Action: (Action)(() => _game.BeginPlacement(e.Type, e.SubType, e.HardwareLevel))))).ToList());
        }
        else
        {
            SetButtons($"machines:{state}:{string.Join(',', ids)}", common);
        }
    }

    private (string, List<(string Label, Action Action)>) StrikeButton(Construction building, WeaponMount strike)
    {
        var name = strike.Spec.TypeName == "NUCLEAR_MISSILE" ? "Launch nuclear missile" : "Fire ion cannon";
        var label = strike.Reload > 0 ? $"{name}\ncharging {strike.ChargePercent:F0}%" : $"{name}\nready";
        return ($"strike{building.Id}:{label}", [(label, () => _game.BeginStrike(building.Id))]);
    }

    /// <summary>Starts the 5-second self-destruct, or calls it off while it counts down.</summary>
    private (string Label, Action Action) SelfDestructButton(List<int> ids, bool counting) => counting
        ? ("Cancel\nself-destruct", () => _game.Issue(new StopCommand(Game.Player, ids)))
        : ("Self-destruct\nin 5 seconds", () => _game.Issue(new SelfDestructCommand(Game.Player, ids)));

    private List<(string Label, Action Action)> FactoryButtons(Construction factory)
    {
        var world = _game.World;
        var state = world.Races[Game.Player];
        return world.Data.FactoryOffer(factory.Spec.SubType!, factory.Stats.HardwareLevel)
            .Where(key => state.Researched.Contains(key))
            .Select(key => (
                Label: $"{KeyName(key)}\n{ResearchTree.FactoryPrice(world.Data.ResearchTree.Get(key), world.Data.Parm)} BMU",
                Action: (Action)(() => _game.Issue(new BuildMachineCommand(Game.Player, factory.Id, key)))))
            .ToList();
    }

    private List<(string Label, Action Action)> LabButtons(Construction lab)
    {
        var world = _game.World;
        return world.Data.ResearchTree.Items
            .Where(item => world.WhyCannotResearch(Game.Player, item.Key, lab) is null && !lab.ResearchQueue.Contains(item.Key))
            .Select(item => (
                Label: $"{KeyName(item.Key)}\n{world.ResearchCost(Game.Player, item)} RU, {item.BuildingCost} BMU",
                Action: (Action)(() => _game.Issue(new ResearchCommand(Game.Player, lab.Id, item.Key)))))
            .ToList();
    }

    private void SetButtons(string signature, List<(string Label, Action Action)> buttons)
    {
        if (signature == _buttonsFor)
            return;
        _buttonsFor = signature;
        foreach (var child in _buttons.GetChildren())
            child.QueueFree();
        foreach (var (label, action) in buttons)
        {
            var button = new Button { Text = label, CustomMinimumSize = new Vector2(176, 44), FocusMode = Control.FocusModeEnum.None };
            button.AddThemeFontSizeOverride("font_size", 12);
            button.Pressed += action;
            _buttons.AddChild(button);
        }
    }

    private string Describe(Actor actor)
    {
        var world = _game.World;
        var health = $"HP {actor.HitPoints}/{actor.MaxHitPoints}  Armour {actor.Armour}/{actor.MaxArmour}";
        if (actor.SelfDestructAt is { } at)
            health += $"   SELF-DESTRUCT IN {Math.Max(0, at - world.Time):F0} s";
        switch (actor)
        {
            case Machine m:
                var extra = m.Spec.Type == "RESOURCE_CARRIER" ? $"  Ore {m.Ore}/{m.Stats.Capacity}"
                    : m.IsType("APC") ? $"  Carrying {world.CarriedVolume(m)}/{m.Stats.Capacity} ({m.Passengers.Count} machines)" : "";
                var weapons = m.Weapons.Count > 0 ? "\nWeapons: " + string.Join(", ", m.Weapons.Select(w => w.Spec.TypeName)) : "";
                return $"{Game.Look(m.Race)} {MachineName(m)}  [{m.Order.GetType().Name.Replace("Order", "")}]\n{health}{extra}{weapons}";
            case Construction c:
                var lines = new List<string> { $"{Game.Look(c.Race)} {c.TypeName} level {c.Stats.HardwareLevel}", health };
                if (!c.Complete) lines.Add($"Built {c.Built:F0}/{c.Stats.Cost} BMU");
                if (c.IsType("MINE")) lines.Add($"Ore stored {c.Ore:F0}/{c.Stats.Capacity}" + (c.MineralSite is { } s ? $", site has {world.Sites[s].Ore:F0}" : ", not on a site"));
                if (c.ProductionQueue.Count > 0) lines.Add($"Building {KeyName(c.ProductionQueue[0])} ({c.ProductionSpent:F0} BMU)" + (c.ProductionQueue.Count > 1 ? $", +{c.ProductionQueue.Count - 1} queued" : ""));
                if (c.IsType("GARRISON")) lines.Add($"Machines inside {c.Garrisoned.Count}/{GameWorld.GarrisonStations(c)}, repairing {c.Stats.RepairRate} HP a minute");
                if (c.IsType("HARDWARE_LAB"))
                {
                    lines.Add($"Technicians {c.Technicians.Count}/{GameWorld.ResearchStations(c)}");
                    if (c.ResearchQueue.Count > 0)
                    {
                        var key = c.ResearchQueue[0];
                        world.Races[c.Race].ResearchProgress.TryGetValue(key, out var progress);
                        lines.Add($"Researching {KeyName(key)}: {progress:F0}/{world.ResearchCost(c.Race, world.Data.ResearchTree.Get(key))}");
                    }
                }
                return string.Join("\n", lines);
            default:
                return health;
        }
    }

    private static string MachineName(Machine m) => KeyName(m.Key);

    private static string KeyName(ResearchKey key) =>
        $"{key.SubType ?? key.Type} L{key.HardwareLevel}" + (key.WeaponCombo is { } combo ? $" {combo}" : "");

    /// <summary>The selection rectangle while dragging.</summary>
    private partial class DragBox(Hud hud) : Control
    {
        public override void _Draw()
        {
            if (hud.DragRect is { } rect)
                DrawRect(rect, new Color(0.3f, 1, 0.3f), filled: false, width: 1.5f);
        }
    }
}

/// <summary>The planet overview with every actor as a dot; click to move the camera, right click to order.</summary>
public partial class Minimap(Game game, Texture2D? map) : Control
{
    public override void _Draw()
    {
        var world = game.World;
        var size = Size;
        if (map is not null)
            DrawTextureRect(map, new Rect2(Vector2.Zero, size), tile: false);
        else
            DrawRect(new Rect2(Vector2.Zero, size), new Color(0.6f, 0.5f, 0.35f));

        Vector2 ToMap(System.Numerics.Vector2 p) =>
            new(p.X / world.Planet.Surface.Width * size.X, p.Y / world.Planet.Surface.Depth * size.Y);

        foreach (var site in world.Sites.Where(s => s.DiscoveredBy.Contains(Game.Player)))
            DrawCircle(ToMap(site.Position), 2.5f, new Color(1, 0.9f, 0.2f));
        foreach (var actor in world.Actors.Where(a => a is not Machine { Inside: not null }))
            DrawCircle(ToMap(actor.Position), actor is Construction ? 3.5f : 2f, ClassicAssets.RaceColour(Game.Look(actor.Race)));

        if (GetViewport().GetCamera3D() is { } camera)
        {
            var focus = camera.GlobalPosition;
            DrawCircle(ToMap(new System.Numerics.Vector2(focus.X, focus.Z)), 3, Colors.White);
        }
        DrawRect(new Rect2(Vector2.Zero, size), Colors.Black, filled: false, width: 2);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
        {
            var world = game.World;
            var x = click.Position.X / Size.X * world.Planet.Surface.Width;
            var y = click.Position.Y / Size.Y * world.Planet.Surface.Depth;
            if (game.GetNode<CameraRig>("CameraRig") is { } rig)
                rig.Position = new Vector3(x, 0, y);
            AcceptEvent();
        }
    }
}
