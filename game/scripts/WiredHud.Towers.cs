using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

// A constructor hero's tower mode on a run's screen (Game.Towers): over the foot a strip of the catalogue's towers and
// pulling one down, the choice framed, those the chassis builds lit with their price and the others dim with the chassis
// that builds them first, and over it the keys on a plate of their own; under the sight what the place is wrong for, or
// what the tower chosen costs, and while pulling down is chosen the player's tower in the sight in brackets with what it
// brings back. The way to the pod keeps above the strip, or beside it where it would point down onto it (Pointer). Wired is the remake's own addition.
public partial class WiredHud
{
    private const float TowerCellWidth = 100, TowerCellHeight = 84, TowerCellGap = 6, TowerKeysLine = 36, TowerKeysSize = 17;

    // Where the top of the foot's plates was drawn last: the strip stands on it.
    private float _footTop;

    // Where the strip and its keys were drawn last: the way to the pod goes beside it (Pointer).
    private Rect2? _towerStrip;

    /// <summary>The foot of the strip: over the foot's plates, and over the tab of the squad's orders where the squad has one.</summary>
    private float TowerStripBottom => _footTop - (10 + (_now is { Squad.Count: > 0 } ? 30 : 0)) * _u;

    /// <summary>The top of the strip and the keys over it, while the towers are up: what is said over the foot goes above.</summary>
    private float? TowerStripTop => _game.Towers is null ? null : TowerStripBottom - (TowerCellHeight + TowerKeysLine) * _u;

    /// <summary>Where a refusal is said: over the foot, and above the towers' strip while it is up.</summary>
    private float RefusalAt => Mathf.Min(_size.Y - (Rise + 84) * _u, (TowerStripTop ?? float.MaxValue) - 48 * _u);

    private void DrawTowerMode()
    {
        if (_game.Towers is not { } towers)
            return;
        DrawTowerSight(towers);
        DrawTowerStrip(towers);
    }

    /// <summary>
    /// Under the sight, below its compass: the tower's name and price where it may stand, or why not; the tower to pull
    /// down in brackets, with what it brings back.
    /// </summary>
    private void DrawTowerSight(Game.TowerSight towers)
    {
        var choice = towers.Choices[towers.Chosen];
        // The sight's compass reaches 40 of its own pixels down, which grow with the window by whole steps (Crosshair).
        var under = towers.Sight + new Vector2(0, 44 * Math.Max(1, Mathf.RoundToInt(_size.Y / 480f)) + 6 * _u);
        if (choice.Tower is { } tower)
        {
            if (towers.Why is { } why)
                SightLabel(under, HangarText.Reason(why), Danger.Lerp(Colors.White, 0.2f));
            else
                SightLabel(under, $"{TowerName(tower)}  {Thousands(choice.Price)} BMU", new Color(0.45f, 1, 0.5f));
            return;
        }
        if (towers.Target is { } target && GetViewport().GetCamera3D() is { } camera && _game.ViewOf(target) is { } view && Framed(camera, view) is { } frame)
            Brackets(frame, towers.Why is null ? Gold : Danger);
        if (towers.Why is { } refused)
            SightLabel(under, HangarText.Reason(refused), towers.Target is null ? MenuTheme.Text : Danger.Lerp(Colors.White, 0.2f));
        else
            SightLabel(under, $"+{Thousands(towers.Refund)} BMU", Gold);
    }

    /// <summary>A line on a dark plate hung from a point, to read on sand as on snow.</summary>
    private void SightLabel(Vector2 top, string text, Color colour)
    {
        const float Size = 17;
        var box = new Rect2(top - new Vector2(Width(text, Size) / 2 + 12 * _u, 0), new Vector2(Width(text, Size) + 24 * _u, (Size + 12) * _u));
        Plate(box, new Color(Plating, 0.75f), new Color(colour, 0.6f), 6);
        Say(new Vector2(top.X, box.Position.Y + (Size + 3) * _u), text, Size, colour, 0.5f);
    }

    /// <summary>
    /// The strip of the choices over the foot, under the middle of the view as far as the radar leaves room, its cells
    /// narrower where the window is, with the keys over it.
    /// </summary>
    private void DrawTowerStrip(Game.TowerSight towers)
    {
        var count = towers.Choices.Count;
        var start = Left + 16 * _u;
        var end = _hud.Radar.Visible ? _size.X - _hud.Radar.ScreenWidth - Gap * _u : _size.X - 16 * _u;
        var cell = Mathf.Clamp(((end - start) / _u - (count - 1) * TowerCellGap) / count, 64, TowerCellWidth);
        var width = (count * cell + (count - 1) * TowerCellGap) * _u;
        var middle = Mathf.Max(start + width / 2, Mathf.Min(Left + (_size.X - Left) / 2, end - width / 2));
        var top = TowerStripBottom - TowerCellHeight * _u;
        var x = middle - width / 2;
        for (var i = 0; i < count; i++)
        {
            DrawTowerChoice(towers, i, new Rect2(new Vector2(x, top), new Vector2(cell, TowerCellHeight) * _u));
            x += (cell + TowerCellGap) * _u;
        }
        var chosen = towers.Choices[towers.Chosen];
        var keys = string.Join("    ", Lang.T("Wheel: choose"),
            chosen.Tower is null ? Lang.T("{0}: pull down", KeyMap.Name(KeyMap.Act.FireLeft)) : Lang.T("{0}: place", KeyMap.Name(KeyMap.Act.FireLeft)),
            Lang.T("{0}: put away", $"{KeyMap.Name(KeyMap.Act.FireRight)} / {KeyMap.Name(KeyMap.Act.Special)}"));
        // On a plate of their own, as wide as the words but never wider than the room beside the radar, so they read on sand.
        var room = (end - start) / _u - 24;
        var size = Shrunk(keys, TowerKeysSize, room, least: 13);
        var plate = Mathf.Min(Width(keys, size), room * _u) + 24 * _u;
        var height = (TowerKeysSize + 12) * _u;
        var box = new Rect2(new Vector2(Mathf.Clamp(middle - plate / 2, start, Mathf.Max(start, end - plate)), top - 6 * _u - height), new Vector2(plate, height));
        Plate(box, new Color(Plating, 0.8f), new Color(MenuTheme.Edge, 0.8f), 6);
        _towerStrip = box.Merge(new Rect2(new Vector2(middle - width / 2, top), new Vector2(width, TowerCellHeight * _u)));
        SayWithin(new Vector2(box.GetCenter().X, box.Position.Y + height / 2 + size * 0.36f * _u), keys, size, room, MenuTheme.Bright, least: 13);
    }

    /// <summary>
    /// One choice: its picture, its name, and its price where the chassis builds it (red while the world says it cannot be
    /// placed now), else the chassis that does; the choice the wheel has come to stands out in gold.
    /// </summary>
    private void DrawTowerChoice(Game.TowerSight towers, int index, Rect2 box)
    {
        var choice = towers.Choices[index];
        var chosen = index == towers.Chosen;
        var lit = choice.Builds;
        Plate(box, Plating, chosen ? Gold : lit ? MenuTheme.Edge : new Color(MenuTheme.Edge, 0.35f), 8);
        if (chosen)
            DrawRect(box.Grow(3 * _u), new Color(Gold, 0.45f), false, 2 * _u);
        var picture = new Rect2(box.Position + new Vector2(box.Size.X / 2 - 22 * _u, 6 * _u), new Vector2(44, 44) * _u);
        var path = choice.Tower is { } tower
            ? $"construc/{ClassicPanel.RaceFolder(Game.Player)}/{ClassicPanel.ConstructionPrefix(WiredTower.Type, tower.SubType)}{tower.Level}.bmp"
            : "commands/deconst.bmp";
        var bitmap = ClassicGui.Bitmap(path);
        // The picture's enlargement (tools/hud-sharp) at the box's size in whole screen pixels, else the bitmap stretched.
        var (corner, across) = (picture.Position.Round(), picture.Size.Round());
        if (bitmap is not null && HudArt.Sized(path, (int)across.X, (int)across.Y) is { } sharp)
            DrawTexture(sharp, corner, lit ? Colors.White : new Color(1, 1, 1, 0.3f));
        else if (bitmap is not null)
            DrawTextureRect(bitmap, picture, false, lit ? Colors.White : new Color(1, 1, 1, 0.3f));
        else
            Glyph(TowerGlyph, picture, lit ? MenuTheme.Glow : MenuTheme.Dim);
        var name = choice.Tower is { } named ? TowerName(named) : Lang.T("Scrap");
        var centre = box.Position.X + box.Size.X / 2;
        var room = box.Size.X / _u - 10;
        SayWithin(new Vector2(centre, box.Position.Y + 64 * _u), name.Caps(), 13, room, lit ? MenuTheme.Bright : MenuTheme.Dim, least: 12);
        var (line, colour) = choice switch
        {
            { Builds: false, OpensOn: { } opens } => (Lang.T("from {0}", opens), MenuTheme.Dim),
            { Builds: false } => ("", MenuTheme.Dim),
            { Tower: null } => (chosen && towers.Target is not null && towers.Why is null ? $"+{Thousands(towers.Refund)} BMU" : "", Gold),
            _ => ($"{Thousands(choice.Price)} BMU", choice.Why is null ? MenuTheme.Bright : Danger),
        };
        if (line.Length > 0)
            SayWithin(new Vector2(centre, box.Position.Y + 79 * _u), line, 13, room, colour, least: 12);
    }

    /// <summary>
    /// A line centred on a point within a width in the HUD's pixels: smaller as far as size <paramref name="least"/>, then
    /// narrower, so that a long name such as GESCHÜTZTURM 1 keeps its ends and its number in a small cell.
    /// </summary>
    private void SayWithin(Vector2 centre, string text, float size, float width, Color colour, float least = 10)
    {
        size = Shrunk(text, size, width, least);
        var wide = Width(text, size);
        if (wide <= width * _u)
        {
            Say(centre, text, size, colour, 0.5f);
            return;
        }
        DrawSetTransform(centre - new Vector2(width * _u / 2, 0), 0, new Vector2(width * _u / wide, 1));
        Say(Vector2.Zero, text, size, colour);
        DrawSetTransform(Vector2.Zero);
    }

    /// <summary>A tower's name as the game shows its emplacement: Turret 1, Sentry 3, Launcher 4.</summary>
    private static string TowerName(WiredTower tower) =>
        $"{Lang.T(Studio.Names.GetValueOrDefault(tower.SubType, tower.SubType))} {tower.Level}";
}
