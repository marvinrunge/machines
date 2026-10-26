using Godot;
using Machines.Sim.Combat;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The first-person weapon panels at the top left (MachGuiFirstPerson::displayWeapons): left, top and right
/// weapon in that order, each dropping down in ten frames (gui/fstpersn/weapon/weap0..9) as first person begins,
/// one after another, then showing its icon and how far it has recharged. A weapon not selected (Tab) folds back up.
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

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        foreach (var (weapon, selected) in _weapons)
        {
            var drop = _drop.GetValueOrDefault(weapon.Mounting);
            var step = (float)delta / DropTime * DropFrames;
            _drop[weapon.Mounting] = selected ? Mathf.Min(DropFrames, drop + step) : Mathf.Max(0, drop - step);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var scale = Math.Max(1, (int)Math.Round(GetViewportRect().Size.Y / 480f));
        // The original's panels start below a border of 5 % of the screen.
        var at = new Vector2(1, (int)(GetViewportRect().Size.Y * 0.05f));
        foreach (var mounting in new[] { Mounting.Left, Mounting.Top, Mounting.Right })
        {
            if (_weapons.FirstOrDefault(w => w.Weapon.Mounting == mounting) is not { Weapon: { } weapon })
                continue;
            var frame = (int)_drop.GetValueOrDefault(mounting);
            if (Bitmap("weapon", scale) is not { } background)
                continue;
            if (frame >= DropFrames)
            {
                DrawTexture(background, at);
                if (Icons.TryGetValue(weapon.Spec.TypeName, out var icon) && Bitmap(icon, scale) is { } picture)
                    DrawTexture(picture, at + new Vector2(8, 33) * scale);
                if (Bitmap("chrgey", scale) is { } charge)
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
            at.X += background.GetWidth() + 2 * scale;
        }
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
