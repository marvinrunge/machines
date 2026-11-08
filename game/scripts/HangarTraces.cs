using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// The glowing traces over the hangar's screen, from each slot card to its place on the model, as the tracks of a board
/// run from pin to pin: a weapon's to its mount, the others to the body's skin (<see cref="HeroHull"/>). They are worked out
/// every frame, so they stay on the hero as it turns, and pulses of light run along them. A fresh fit sends a surge down its trace and a ring out from the mount. Wired is the remake's own
/// addition.
/// </summary>
public partial class HangarTraces : Control
{
    private const float SurgeTime = 0.8f;

    private readonly Dictionary<WiredSlot, float> _surges = [];
    private float _time;

    /// <summary>The cards with traces, the screen they end on. Set before it enters the tree.</summary>
    public required IReadOnlyList<SlotCard> Cards { get; init; }

    public required HangarScreen Screen { get; init; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        // After the screen: its model must have moved before the mounts are projected.
        ProcessPriority = 10;
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        foreach (var slot in _surges.Keys.ToList())
        {
            _surges[slot] -= (float)delta;
            if (_surges[slot] <= 0)
                _surges.Remove(slot);
        }
        QueueRedraw();
    }

    /// <summary>Light runs down a slot's trace.</summary>
    public void Surge(WiredSlot slot) => _surges[slot] = SurgeTime;

    /// <summary>The point of the model a slot's trace ends on.</summary>
    public Vector3? End(WiredSlot slot) => slot.Kind switch
    {
        WiredSlotKind.Weapon => Screen.MountPoint((Mounting)slot.Index),
        WiredSlotKind.Module or WiredSlotKind.Ability => Screen.HullPoint(slot)?.At,
        _ => null,
    };

    /// <summary>How much of a trace shows: one to the skin fades out as the hero turns that side away.</summary>
    private float Shown(WiredSlot slot) => slot.Kind is WiredSlotKind.Module or WiredSlotKind.Ability && Screen.HullPoint(slot) is { } spot
        ? Math.Clamp((spot.Facing + 0.1f) / 0.25f, 0, 1)
        : 1;

    public override void _Draw()
    {
        var phase = 0f;
        foreach (var card in Cards)
        {
            phase += 0.37f;
            if (!card.Visible || card.Absent || End(card.Slot) is not { } point)
                continue;
            var end = Screen.Project(point);
            if (end.X < 0 || end.Y < 0 || end.X > Size.X || end.Y > Size.Y)
                continue;
            var colour = card.NoteColour == HangarPage.Red && card.Note is not null ? HangarPage.Red : card.Empty ? MenuTheme.Bright : MenuTheme.Glow;
            var strength = (card.Empty ? 0.3f + 0.5f * card.Pulse : card.Pointed ? 1 : 0.62f) * Shown(card.Slot);
            var surge = _surges.GetValueOrDefault(card.Slot) / SurgeTime;
            if (strength > 0)
                Run(Route(card, end), colour, strength, phase, surge);
        }
    }

    /// <summary>From the card's nearest edge, straight, then at forty-five degrees, then straight in, as a board's tracks go.</summary>
    private Vector2[] Route(SlotCard card, Vector2 end)
    {
        var box = new Rect2(card.Position, card.Size);
        var side = end.X < box.Position.X || end.X > box.End.X;
        var start = side ? new Vector2(end.X < box.Position.X ? box.Position.X : box.End.X, box.GetCenter().Y)
            : new Vector2(box.GetCenter().X, end.Y < box.Position.Y ? box.Position.Y : box.End.Y);
        var away = end - start;
        var (dx, dy) = (Math.Abs(away.X), Math.Abs(away.Y));
        var (sx, sy) = (Math.Sign(away.X), Math.Sign(away.Y));
        if (side)
        {
            var slant = Math.Min(dx, dy);
            var run = (dx - slant) * 0.55f;
            var bend = start + new Vector2(sx * run, 0);
            return [start, bend, bend + new Vector2(sx * slant, sy * slant), end];
        }
        var fall = Math.Min(dx, dy);
        var down = (dy - fall) * 0.55f;
        var turn = start + new Vector2(0, sy * down);
        return [start, turn, turn + new Vector2(sx * fall, sy * fall), end];
    }

    private void Run(Vector2[] route, Color colour, float strength, float phase, float surge)
    {
        var width = 1 + surge * 2;
        DrawPolyline(route, new Color(colour, 0.07f * strength + 0.2f * surge), 9 * width, true);
        DrawPolyline(route, new Color(colour, 0.16f * strength + 0.3f * surge), 4.5f * width, true);
        DrawPolyline(route, new Color(colour, 0.8f * strength + 0.2f * surge), 1.6f * width, true);

        var length = 0f;
        for (var i = 1; i < route.Length; i++)
            length += route[i - 1].DistanceTo(route[i]);
        // Pulses run from the card to the model; a surge is a bright one all the way.
        for (var pulse = 0; pulse < 2; pulse++)
        {
            var t = (_time * 0.42f + phase + pulse * 0.5f) % 1;
            var at = Along(route, t * length);
            DrawCircle(at, 3.2f, new Color(colour, 0.85f * strength));
            DrawCircle(at, 7f, new Color(colour, 0.18f * strength));
        }
        if (surge > 0)
        {
            var at = Along(route, (1 - surge) * length);
            DrawCircle(at, 6, new Color(1, 1, 0.8f, 0.9f));
            DrawCircle(at, 15, new Color(colour, 0.35f));
        }

        var end = route[^1];
        var ring = 7 + 1.5f * Mathf.Sin(_time * 3 + phase * 7);
        DrawArc(end, ring, 0, Mathf.Tau, 28, new Color(colour, 0.85f * strength), 1.6f, true);
        DrawCircle(end, 2.6f, new Color(colour, strength));
        DrawCircle(route[0], 3.4f, new Color(colour, 0.9f * strength));
        if (surge > 0)
            DrawArc(end, 8 + (1 - surge) * 70, 0, Mathf.Tau, 48, new Color(1, 1, 0.75f, surge * 0.8f), 2.5f, true);
    }

    private static Vector2 Along(Vector2[] route, float distance)
    {
        for (var i = 1; i < route.Length; i++)
        {
            var part = route[i - 1].DistanceTo(route[i]);
            if (distance <= part)
                return part < 0.01f ? route[i] : route[i - 1].Lerp(route[i], distance / part);
            distance -= part;
        }
        return route[^1];
    }
}
