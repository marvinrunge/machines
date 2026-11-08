using Godot;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The first-person weapon panels at the top left (MachGuiFirstPerson::displayWeapons): left, top and right
/// weapon in that order, each dropping down in ten frames (gui/fstpersn/weapon/weap0..9) as first person begins,
/// one after another, then showing its icon and how far it has recharged. A weapon not selected (Tab) folds back up.
/// Where each weapon has its own trigger, the remake's own, a mark under each panel shows what fires it, and a place of
/// a hero's chassis without a gun has its panel, empty.
/// </summary>
public partial class WeaponDisplay : Control
{
    private const float DropTime = 0.7f, DropDelay = 0.4f;
    private const int DropFrames = 10;

    // MachGuiFirstPerson::loadWeaponBmps; multi-launchers fire other numbers' missiles.
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["ELECTRIC_CHARGE"] = "echarge", ["BOLTER"] = "bolter", ["HEAVY_BOLTER1"] = "hbolter", ["HEAVY_BOLTER2"] = "hbolter2",
        ["FLAME_THROWER1"] = "flame1", ["FLAME_THROWER2"] = "flame2", ["AUTO_CANNON"] = "autoc",
        ["SUPERCHARGE_ADVANCED"] = "heala", ["SUPERCHARGE_SUPER"] = "heals", ["PLASMA_CANNON1"] = "plscan1",
        ["PLASMA_CANNON2"] = "plscan2", ["PLASMA_RIFLE"] = "plsrif", ["PULSE_CANNON"] = "pulscan", ["PULSE_RIFLE"] = "pulsrif",
        ["LARGE_MISSILE"] = "misslrg", ["MULTI_LAUNCHER1"] = "miss6", ["MULTI_LAUNCHER2"] = "miss5", ["MULTI_LAUNCHER3"] = "miss1",
        ["MULTI_LAUNCHER4"] = "miss4", ["MULTI_LAUNCHER5"] = "miss2", ["MULTI_LAUNCHER6"] = "miss3", ["MULTI_LAUNCHER7"] = "miss7",
        ["VORTEX"] = "vortex", ["ORB_OF_TREACHERY"] = "treach", ["GORILLA_PUNCH"] = "punch", ["VIRUS"] = "virus",
        ["BEE_BOMB"] = "beebomb", ["WASP_LIGHT_STING"] = "wasplite", ["WASP_METAL_STING"] = "waspmis",
    };

    private readonly Dictionary<(string, int), Texture2D?> _bitmaps = [];
    private readonly Dictionary<Mounting, float> _drop = [];
    private List<(WeaponMount Weapon, bool Selected)> _weapons = [];
    private IReadOnlyList<GunPlace>? _places;

    /// <summary>A place for a gun: what is on it, if anything, and what fires it.</summary>
    public sealed record GunPlace(Mounting Mounting, WeaponMount? Weapon, IReadOnlyList<KeyMap.Act> Triggers);

    /// <summary>Whether the driver holds a trigger, which the game knows better than the system when the focus came and went.</summary>
    public Func<KeyMap.Act, bool> Held { get; set; } = KeyMap.Held;

    /// <summary>Where the marks under the panels end, in pixels from the top; 0 without any.</summary>
    public float Bottom { get; private set; }

    /// <summary>What the panels and the marks under them take of the screen; empty without any.</summary>
    public Rect2 Area { get; private set; }

    public static WeaponDisplay Create() => new() { Name = "Weapons", MouseFilter = MouseFilterEnum.Ignore, Visible = false };

    /// <summary>Starts the panels dropping down, one after another, as on taking over a machine.</summary>
    public void Start()
    {
        _drop.Clear();
        var order = 0;
        foreach (var mounting in new[] { Mounting.Left, Mounting.Top, Mounting.Right })
            _drop[mounting] = -DropDelay * order++ / DropTime * DropFrames;
    }

    /// <summary>The driven machine's weapons, and which of them are selected to fire.</summary>
    public void Show(IReadOnlyList<WeaponMount> weapons, int selected) =>
        _weapons = weapons.Select((w, i) => (w, (selected & (1 << i)) != 0)).ToList();

    /// <summary>With a trigger for each weapon, the machine's places for guns; null where Tab picks the weapons, which draws no marks.</summary>
    public void ShowTriggers(IReadOnlyList<GunPlace>? places) => _places = places;

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        void Drop(Mounting mounting, bool down)
        {
            var drop = _drop.GetValueOrDefault(mounting);
            var step = (float)delta / DropTime * DropFrames;
            _drop[mounting] = down ? Mathf.Min(DropFrames, drop + step) : Mathf.Max(0, drop - step);
        }
        foreach (var (weapon, selected) in _weapons)
            Drop(weapon.Mounting, selected);
        foreach (var place in _places ?? [])
            if (place.Weapon is null)
                Drop(place.Mounting, true);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var scale = Math.Max(1, (int)Math.Round(GetViewportRect().Size.Y / 480f));
        // The original's panels start below a border of 5 % of the screen.
        var at = new Vector2(1, (int)(GetViewportRect().Size.Y * 0.05f));
        var (start, end) = (at, at);
        Bottom = 0;
        foreach (var mounting in new[] { Mounting.Left, Mounting.Top, Mounting.Right })
        {
            var weapon = _weapons.FirstOrDefault(w => w.Weapon.Mounting == mounting).Weapon;
            var place = _places?.FirstOrDefault(p => p.Mounting == mounting);
            if (weapon is null && place is null)
                continue;
            var frame = (int)_drop.GetValueOrDefault(mounting);
            if (Bitmap("weapon", scale) is not { } background)
                continue;
            if (frame >= DropFrames)
            {
                DrawTexture(background, at);
                if (weapon is null)
                {
                    DrawRect(new Rect2(at + new Vector2(8, 33) * scale, new Vector2(background.GetWidth() - 16 * scale, 34 * scale)), new Color(0, 0, 0, 0.55f));
                    Say(at + new Vector2(background.GetWidth() / 2f, 54 * scale), Lang.T("Empty"), 9 * scale, MenuTheme.Text, 0.5f);
                }
                else if (Icons.TryGetValue(weapon.Spec.TypeName, out var icon) && Bitmap(icon, scale) is { } picture)
                    DrawTexture(picture, at + new Vector2(8, 33) * scale);
                if (weapon is not null && Bitmap("chrgey", scale) is { } charge)
                {
                    var width = charge.GetWidth() * weapon.ChargePercent / 100;
                    DrawTextureRectRegion(charge, new Rect2(at + new Vector2(9, 19) * scale, new Vector2(width, charge.GetHeight())),
                        new Rect2(0, 0, width, charge.GetHeight()));
                }
            }
            else if (Bitmap($"weap{Math.Max(0, frame)}", scale) is { } dropping)
            {
                DrawTexture(dropping, at);
            }
            if (place is not null)
                DrawTriggers(place, at + new Vector2(background.GetWidth() / 2f, background.GetHeight() + 3 * scale), scale);
            end = new Vector2(at.X + background.GetWidth(), Mathf.Max(end.Y, Mathf.Max(at.Y + background.GetHeight(), Bottom)));
            at.X += background.GetWidth() + 2 * scale;
        }
        Area = new Rect2(start, end - start);
    }

    /// <summary>What fires a place, side by side and centred on a point; it lights up while held.</summary>
    private void DrawTriggers(GunPlace place, Vector2 top, int scale)
    {
        var size = 8 * scale;
        var gap = 3 * scale;
        float Wide(KeyMap.Act act) => IsPlainButton(act) ? 12 * scale : MenuTheme.Font.GetStringSize(KeyMap.Name(act), HorizontalAlignment.Left, -1, size).X + 6 * scale;
        var x = top.X - (place.Triggers.Sum(Wide) + gap * (place.Triggers.Count - 1)) / 2;
        foreach (var act in place.Triggers)
        {
            var colour = place.Weapon is null ? MenuTheme.Dim : Held(act) ? Colors.White : MenuTheme.Glow;
            if (IsPlainButton(act))
                DrawMouse(KeyMap.Button(act)!.Value, new Rect2(x, top.Y, 12 * scale, 17 * scale), colour, scale);
            else
                DrawKeyCap(KeyMap.Name(act), new Rect2(x, top.Y + 2 * scale, Wide(act), 13 * scale), colour, size, scale);
            x += Wide(act) + gap;
        }
        Bottom = Mathf.Max(Bottom, top.Y + 19 * scale);
    }

    // The three a little mouse can show; the thumb buttons go by name.
    private static bool IsPlainButton(KeyMap.Act act) => KeyMap.Button(act) is MouseButton.Left or MouseButton.Right or MouseButton.Middle;

    /// <summary>A mouse seen from above with the one button filled in.</summary>
    private void DrawMouse(MouseButton button, Rect2 box, Color colour, int scale)
    {
        const int steps = 8;
        var radius = box.Size.X / 2;
        var (middle, waist) = (box.Position.X + radius, box.Position.Y + radius * 1.3f);
        Vector2 Arc(float centre, int step) => new Vector2(middle, centre) + Vector2.FromAngle(step * Mathf.Pi / steps) * radius;
        var (high, low) = (box.Position.Y + radius, box.End.Y - radius);
        var shell = Enumerable.Range(steps, steps + 1).Select(step => Arc(high, step)).Concat(Enumerable.Range(0, steps + 1).Select(step => Arc(low, step))).ToArray();
        DrawColoredPolygon(shell, new Color(0, 0, 0, 0.6f));
        if (button == MouseButton.Middle)
            DrawRect(new Rect2(middle - 1.5f * scale, box.Position.Y + 1.5f * scale, 3 * scale, 5 * scale), colour);
        else
        {
            var quarter = button == MouseButton.Left ? Enumerable.Range(steps, steps / 2 + 1) : Enumerable.Range(steps + steps / 2, steps / 2 + 1).Reverse();
            var edge = button == MouseButton.Left ? box.Position.X : box.End.X;
            DrawColoredPolygon([new Vector2(middle, waist), new Vector2(edge, waist), .. quarter.Select(step => Arc(high, step))], colour);
        }
        var line = new Color(colour, 0.8f);
        DrawPolyline([.. shell, shell[0]], line, scale, true);
        DrawLine(new Vector2(box.Position.X, waist), new Vector2(box.End.X, waist), line, scale);
        DrawLine(new Vector2(middle, box.Position.Y), new Vector2(middle, waist), line, scale);
    }

    private void DrawKeyCap(string key, Rect2 box, Color colour, int size, int scale)
    {
        DrawRect(box, new Color(0, 0, 0, 0.6f));
        DrawRect(box, new Color(colour, 0.75f), false, scale);
        Say(box.Position + new Vector2(box.Size.X / 2, box.Size.Y - 3.5f * scale), key, size, colour, 0.5f);
    }

    private void Say(Vector2 at, string text, int size, Color colour, float align)
    {
        var font = MenuTheme.Font;
        DrawString(font, at - new Vector2(font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X * align, 0), text, HorizontalAlignment.Left, -1, size, colour);
    }

    private Texture2D? Bitmap(string name, int scale)
    {
        if (_bitmaps.TryGetValue((name, scale), out var cached))
            return cached;
        Texture2D? texture = null;
        if (ClassicGui.Bitmap($"fstpersn/weapon/{name}.bmp")?.GetImage() is { } image)
        {
            var (width, height) = (image.GetWidth() * scale, image.GetHeight() * scale);
            for (var grown = 1; grown < scale; grown *= 2)
                image = ClassicCursor.Scale2x(image);
            if (image.GetWidth() != width)
                image.Resize(width, height, Image.Interpolation.Lanczos);
            texture = ImageTexture.CreateFromImage(image);
        }
        _bitmaps[(name, scale)] = texture;
        return texture;
    }
}
