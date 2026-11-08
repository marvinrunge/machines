using Godot;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The hero's values in the hangar, and how a build being tried would change them: what rises in green, what falls in
/// red, and under them the Load and Power bars with what it would come to and how much of the bar is overload. Everything
/// shown comes from <see cref="HeroBuild.Resolve"/>. Wired is the remake's own addition.
/// </summary>
public partial class HangarValues : Control
{
    private const float BarHeight = 16;

    /// <summary>A line of values: its name, how to read it from a build and how to write it.</summary>
    private sealed record Row(string Name, Func<HeroBuild, float, float> Read, string Format, string Unit = "");

    private static readonly Row[] Rows =
    [
        new("Hit points", (b, _) => b.Stats.HitPoints, "0"),
        new("Armour", (b, _) => b.Stats.Armour, "0"),
        new("Repair", (b, _) => b.Stats.RepairRate, "0.0", "/s"),
        new("Speed", (b, _) => b.Stats.Speed, "0.0", " m/s"),
        new("Turn", (b, limit) => b.DrivenTurnRate(limit), "0", "°/s"),
        new("Accuracy", (b, _) => b.Stats.Accuracy ?? 100, "0"),
        new("Scanner", (b, _) => b.Stats.ScannerRange, "0", " m"),
        new("Damage/s", (b, _) => b.DamagePerSecond, "0.0"),
        // Whole metres as the first-person panel has them, which cuts the rest off.
        new("Range", (b, _) => MathF.Floor(b.Range), "0", " m"),
    ];

    private HeroBuild? _now, _then;

    /// <summary>The first-person limit a driven hull turns under; set before it enters the tree.</summary>
    public required float TurnLimit { get; init; }

    public override void _Ready() => SizeFlagsVertical = SizeFlags.ExpandFill;

    /// <summary>The hero as it is and, while something is tried, as it would be.</summary>
    public void Show(HeroBuild now, HeroBuild? then)
    {
        (_now, _then) = (now, then);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_now is null)
            return;
        var warnings = (_then ?? _now).Warnings;
        var warningRoom = 24 * Math.Min(warnings.Count, 3) + 6;
        var barsRoom = 2 * (BarHeight + 34);
        var rowHeight = Math.Min(38, (Size.Y - barsRoom - warningRoom) / Rows.Length);
        var size = (int)Math.Clamp(rowHeight * 0.6f, 15, 22);
        var y = 0f;
        foreach (var row in Rows)
        {
            DrawRow(row, y, rowHeight, size);
            y += rowHeight;
        }
        y += 8;
        DrawBar("Load", y, _now.Load, _now.LoadCapacity, _then?.Load, _then?.LoadCapacity, WiredBalance.MostLoad);
        y += BarHeight + 34;
        DrawBar("Power", y, _now.PowerDraw, _now.PowerSupply, _then?.PowerDraw, _then?.PowerSupply, 1.5f);
        y += BarHeight + 34;
        foreach (var warning in warnings.Take(3))
        {
            HangarText.Line(this, new Vector2(0, y + 16), Lang.T(warning), Size.X, 16, MenuTheme.Bright);
            y += 24;
        }
    }

    private void DrawRow(Row row, float y, float height, int size)
    {
        var limit = TurnLimit;
        var now = row.Read(_now!, limit);
        var baseline = y + height * 0.7f;
        var line = new Vector2(0, y + height - 1);
        DrawLine(line, line + new Vector2(Size.X, 0), new Color(MenuTheme.Edge, 0.35f), 1);
        HangarText.Line(this, new Vector2(0, baseline), HangarText.Word(row.Name), Size.X * 0.5f, size, MenuTheme.Text);

        var then = _then is null ? now : row.Read(_then, limit);
        var difference = then - now;
        var shown = Text(then, row);
        var valueRight = Size.X - 74;
        var changed = Math.Abs(difference) > 0.0005f && Text(difference, row) != Text(0, row);
        DrawString(MenuTheme.Font, new Vector2(0, baseline), shown, HorizontalAlignment.Right, valueRight, size,
            !changed ? MenuTheme.Bright : difference > 0 ? MenuTheme.Glow : HangarPage.Red);
        if (changed)
        {
            var sign = difference > 0 ? "+" : "-";
            DrawString(MenuTheme.Font, new Vector2(0, baseline), sign + Number(Math.Abs(difference), row), HorizontalAlignment.Right, Size.X, size - 2,
                difference > 0 ? MenuTheme.Glow : HangarPage.Red);
        }
    }

    private static string Text(float value, Row row) => Number(value, row) + row.Unit;

    private static string Number(float value, Row row)
    {
        var text = value.ToString(row.Format, System.Globalization.CultureInfo.InvariantCulture);
        return Lang.Code == "de" ? text.Replace('.', ',') : text;
    }

    /// <summary>A budget's bar: the share used, a ghost of what it would come to, and red from full on up to <paramref name="most"/>.</summary>
    private void DrawBar(string name, float y, int used, int all, int? usedThen, int? allThen, float most)
    {
        var (shownUsed, shownAll) = (usedThen ?? used, allThen ?? all);
        var text = $"{shownUsed} / {shownAll}";
        var over = shownUsed > shownAll;
        DrawString(MenuTheme.Font, new Vector2(0, y + 16), HangarText.Word(name), HorizontalAlignment.Left, Size.X * 0.5f, 18, MenuTheme.Text);
        DrawString(MenuTheme.Font, new Vector2(0, y + 16), text, HorizontalAlignment.Right, Size.X, 18, over ? HangarPage.Red : MenuTheme.Bright);

        var bar = new Rect2(0, y + 24, Size.X, BarHeight);
        DrawRect(bar, MenuTheme.Well);
        var full = bar.Size.X / most;
        var share = all <= 0 ? 0 : Math.Min((float)used / all, most);
        var then = shownAll <= 0 ? 0 : Math.Min((float)shownUsed / shownAll, most);
        var (low, high) = (Math.Min(share, then), Math.Max(share, then));
        DrawRect(new Rect2(bar.Position, new Vector2(low * full, BarHeight)), MenuTheme.Glow.Darkened(0.2f));
        if (high > low)
            DrawRect(new Rect2(bar.Position + new Vector2(low * full, 0), new Vector2((high - low) * full, BarHeight)), then > share ? new Color(MenuTheme.Bright, 0.85f) : new Color(HangarPage.Red, 0.55f));
        if (high > 1)
            DrawRect(new Rect2(bar.Position + new Vector2(full, 0), new Vector2((high - 1) * full, BarHeight)), new Color(HangarPage.Red, 0.85f));
        DrawLine(bar.Position + new Vector2(full, -2), bar.Position + new Vector2(full, BarHeight + 2), MenuTheme.Text, 2);
        DrawRect(bar, MenuTheme.Edge, false, 1);
    }
}
