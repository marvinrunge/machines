using Godot;

namespace Machines.Game;

/// <summary>
/// The question the hangar is there for, under the hero's values: the next chassis and the next weapon side by side, what
/// each costs and brings, how far the bank goes for both, and what the good under the mouse leaves for the next wave.
/// Wired is the remake's own addition.
/// </summary>
public partial class HangarChoice : Control
{
    public const float Height = 226;

    private const int FactSize = 14;

    private ShopChoice? _choice;
    private int _wave, _bonus, _pointedPrice;
    private string? _pointed;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(0, Height);
    }

    /// <summary>What there is to weigh up, and <paramref name="pointed"/> if the mouse is on a good of that price.</summary>
    public void Show(ShopChoice choice, int wave, int bonus, string? pointed, int pointedPrice)
    {
        (_choice, _wave, _bonus, _pointed, _pointedPrice) = (choice, wave, bonus, pointed, pointedPrice);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_choice is not { } choice)
            return;
        DrawLine(Vector2.Zero, new Vector2(Size.X, 0), new Color(MenuTheme.Edge, 0.7f), 1);
        HangarText.Line(this, new Vector2(0, 26), Lang.T("Chassis or weapon?"), Size.X, 20, MenuTheme.Bright);

        var half = (Size.X - 12) / 2;
        Side(new Rect2(0, 34, half, 84), choice.Chassis is { } chassis ? HangarText.Name(chassis.Id) : Lang.T("Tree climbed"), choice.ChassisPrice,
            choice.Chassis is null ? null : choice.ChassisAffordable, choice.Full, Lines(choice.Step, half - 12));
        var weapon = choice.Weapon is { } id ? HangarText.Name(id) : Lang.T("No weapon on offer");
        Side(new Rect2(half + 12, 34, half, 84), weapon, choice.WeaponPrice, choice.Weapon is null ? null : choice.WeaponAffordable, choice.Full,
            choice.Weapon is null ? ("", "") : (Lang.T("{0} damage/s", (choice.WeaponGain >= 0 ? "+" : "") + Decimal(choice.WeaponGain)), Lang.T("in the best slot")));

        Bank(new Rect2(0, 126, Size.X, 14), choice);
        var both = choice.LeftWithBoth >= 0
            ? Lang.T("Both: {0} BMU, {1} left", choice.Both, choice.LeftWithBoth)
            : Lang.T("Both: {0} BMU, {1} short", choice.Both, -choice.LeftWithBoth);
        if (choice.Full)
            HangarText.Line(this, new Vector2(0, 162), Lang.T("Inventory full: sell something first"), Size.X, 18, HangarPage.Red);
        else if (choice.Chassis is not null && choice.Weapon is not null)
            HangarText.Line(this, new Vector2(0, 162), both, Size.X, 18, choice.LeftWithBoth >= 0 ? MenuTheme.Glow : HangarPage.Red);

        var left = choice.Bank - _pointedPrice;
        var text = _pointed is null ? Lang.T("Wave {0} finds {1} BMU in the bank", _wave, choice.Bank)
            : left >= 0 ? Lang.T("{0} leaves {1} BMU for wave {2}", _pointed, left, _wave)
            : Lang.T("{0} is {1} BMU short", _pointed, -left);
        HangarText.Line(this, new Vector2(0, 186), text, Size.X, 17, left < 0 ? HangarPage.Red : MenuTheme.Text);
        HangarText.Line(this, new Vector2(0, 208), Lang.T("Clearing it brings {0} BMU on top of its wrecks", _bonus), Size.X, 15, MenuTheme.Dim.Lightened(0.3f));
    }

    private static string Decimal(float value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', Lang.Code == "de" ? ',' : '.');

    private static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString();

    private static string Counted(int value, string one, string many) => Lang.T(Math.Abs(value) == 1 ? one : many, Signed(value));

    /// <summary>Everything a chassis changes that a card can say, the most telling first. What stays as it is goes unsaid.</summary>
    private static IEnumerable<string> Facts(ChassisGain step)
    {
        if (step.WeaponSlots != 0)
            yield return Counted(step.WeaponSlots, "{0} slot", "{0} slots");
        if (step.ModuleSlots != 0)
            yield return Counted(step.ModuleSlots, "{0} module", "{0} modules");
        foreach (var slot in step.Resized)
            yield return slot.Is > slot.Was ? $"{HangarText.Side(slot.Mounting)}: {HangarText.Size(slot.Is)}" : Lang.T("{0} only {1}", HangarText.Side(slot.Mounting), HangarText.Size(slot.Is).ToLowerInvariant());
        if (step.FallsOff > 0)
            yield return Lang.T(step.FallsOff == 1 ? "{0} falls off" : "{0} fall off", step.FallsOff);
        if (step.HitPoints != 0)
            yield return Counted(step.HitPoints, "{0} hit point", "{0} hit points");
        if (step.Armour != 0)
            yield return Lang.T("{0} armour", Signed(step.Armour));
        if (step.Carries != 0)
            yield return Lang.T("{0} load", Signed(step.Carries));
        if (step.Supplies != 0)
            yield return Lang.T("{0} power", Signed(step.Supplies));
    }

    /// <summary>What a chassis brings, as far as two short lines hold it: the rest it shows when pointed at.</summary>
    private static (string, string) Lines(ChassisGain? step, float width)
    {
        if (step is null)
            return ("", "");
        if (step.Same)
            return (Lang.T("No change"), "");
        var lines = new[] { "", "" };
        var at = 0;
        foreach (var fact in Facts(step))
        {
            var longer = $"{lines[at]} · {fact}";
            if (lines[at].Length == 0)
                lines[at] = fact;
            else if (MenuTheme.Font.GetStringSize(longer, HorizontalAlignment.Left, -1, FactSize).X <= width)
                lines[at] = longer;
            else if (++at < lines.Length)
                lines[at] = fact;
            else
                break;
        }
        return (lines[0], lines[1]);
    }

    /// <summary>One side of the question: green if it can be had now, red if the bank does not cover it, grey with nowhere to put it.</summary>
    private void Side(Rect2 box, string name, int price, bool? affordable, bool full, (string First, string Second) lines)
    {
        var tint = affordable is null ? MenuTheme.Dim : full ? MenuTheme.Dim.Lightened(0.3f) : affordable == true ? MenuTheme.Glow : HangarPage.Red;
        DrawRect(box, new Color(tint, 0.08f));
        DrawRect(box, new Color(tint, 0.6f), false, 1);
        var (x, width) = (box.Position.X + 6, box.Size.X - 12);
        var top = box.Position.Y;
        HangarText.Line(this, new Vector2(x, top + 20), name, width, 18, MenuTheme.Bright);
        if (affordable is not null)
            HangarText.Line(this, new Vector2(x, top + 41), $"{price} BMU", width, 19, tint);
        HangarText.Line(this, new Vector2(x, top + 59), lines.First, width, FactSize, MenuTheme.Text);
        HangarText.Line(this, new Vector2(x, top + 76), lines.Second, width, FactSize, MenuTheme.Text);
    }

    /// <summary>The bank as a bar with both prices laid on it: what sticks out past its end is what is missing.</summary>
    private void Bank(Rect2 bar, ShopChoice choice)
    {
        var whole = Math.Max(1, Math.Max(choice.Bank, choice.Both));
        var scale = bar.Size.X / whole;
        DrawRect(bar, MenuTheme.Well);
        DrawRect(new Rect2(bar.Position, new Vector2(choice.Bank * scale, bar.Size.Y)), MenuTheme.Glow.Darkened(0.45f));
        var at = bar.Position.X;
        foreach (var (price, colour) in new[] { (choice.Chassis is null ? 0 : choice.ChassisPrice, MenuTheme.Bright), (choice.Weapon is null ? 0 : choice.WeaponPrice, new Color(1f, 0.6f, 0.2f)) })
        {
            DrawRect(new Rect2(at, bar.Position.Y + 3, price * scale, bar.Size.Y - 6), colour);
            at += price * scale;
        }
        var end = bar.Position + new Vector2(choice.Bank * scale, 0);
        DrawLine(end - new Vector2(0, 2), end + new Vector2(0, bar.Size.Y + 2), MenuTheme.Text, 2);
        DrawRect(bar, MenuTheme.Edge, false, 1);
    }
}
