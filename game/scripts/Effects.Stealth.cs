using Godot;

namespace Machines.Game;

// Package S's effects (design-stealth §9.4, §13 View): the dust a Thresher's plant shakes off the deck at his foot and, near
// him, out of the ceiling; the rattle running along a pipe from a struck valve to its far end; steam and sparks where a
// source has had its last bang. Grey and amber, never red (red is the enemy's colour).
public partial class Effects
{
    private static readonly Color CeilingGrit = new(0.62f, 0.6f, 0.56f), Steam = new(0.85f, 0.88f, 0.9f);

    /// <summary>A foot of his coming down: a low ring of dust round it (heavier for a front foot).</summary>
    public void PlantDust(Vector3 foot, float weight)
    {
        if (!GraphicsSettings.Effects)
            return;
        var size = 0.8f + 0.6f * Mathf.Clamp(weight, 0, 1.5f);
        Burst(foot + Vector3.Up * 0.15f, amount: (int)(8 + 8 * weight), lifetime: 1.2f, texture: Particle("smoke_04"), new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingRadius = size,
            EmissionRingInnerRadius = size * 0.6f,
            EmissionRingHeight = 0.1f,
            EmissionRingAxis = Vector3.Up,
            Direction = Vector3.Up,
            Spread = 80,
            InitialVelocityMin = 0.8f,
            InitialVelocityMax = 2.2f,
            Gravity = new Vector3(0, -0.5f, 0),
            DampingMin = 1.5f,
            DampingMax = 2.5f,
            ScaleMin = 1.2f,
            ScaleMax = 2.2f,
            ScaleCurve = Grow(0.5f),
            AngleMin = -180,
            AngleMax = 180,
            ColorRamp = Ramp(DustTint with { A = 0 }, DustTint with { A = 0.45f }, DustTint.Darkened(0.05f) with { A = 0 }),
        }, additive: false);
    }

    /// <summary>Grit sifting down from the deck above at <paramref name="at"/> (a spot near the listener under the ceiling): a thin fall, a few flakes.</summary>
    public void CeilingDust(Vector3 at, int amount)
    {
        if (!GraphicsSettings.Effects)
            return;
        Burst(at, amount: Math.Clamp(amount, 3, 24), lifetime: 2.2f, texture: Soft, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(1.5f, 0.1f, 1.5f),
            Direction = Vector3.Down,
            Spread = 12,
            InitialVelocityMin = 0.2f,
            InitialVelocityMax = 0.6f,
            Gravity = new Vector3(0, -2.5f, 0),
            ScaleMin = 0.08f,
            ScaleMax = 0.2f,
            ColorRamp = Ramp(CeilingGrit with { A = 0 }, CeilingGrit with { A = 0.8f }, CeilingGrit with { A = 0 }),
        }, additive: false);
    }

    /// <summary>
    /// A struck valve's bang running along its pipe under the ceiling from <paramref name="from"/> to <paramref name="to"/>
    /// (first along x, then along y, as the deck's pipes run): a puff of grit every few metres, one after the other, at the
    /// pipe's 0.8 s pace (StorySight.PipeDelay) end to end.
    /// </summary>
    public void PipeRattle(Vector3 from, Vector3 to, float seconds)
    {
        if (!GraphicsSettings.Effects)
            return;
        var corner = new Vector3(to.X, from.Y, from.Z);
        var (first, second) = (from.DistanceTo(corner), corner.DistanceTo(to));
        var length = Mathf.Max(first + second, 0.01f);
        var puffs = Math.Clamp((int)(length / 6), 3, 16);
        for (var i = 0; i <= puffs; i++)
        {
            var along = length * i / puffs;
            var at = along <= first ? from.Lerp(corner, first > 0 ? along / first : 1) : corner.Lerp(to, second > 0 ? (along - first) / second : 1);
            var timer = GetTree().CreateTimer(seconds * i / puffs);
            timer.Timeout += () => CeilingDust(at, 4);
        }
    }

    /// <summary>A source that has had its last bang (StorySight.SpentAfter): a hiss of steam and a few amber sparks from the burst joint.</summary>
    public void SpentSource(Vector3 at)
    {
        SparkShower(at + Vector3.Up * 1.5f, Vector3.Up, 18, new Color(1, 0.72f, 0.25f));
        if (!GraphicsSettings.Effects)
            return;
        Burst(at + Vector3.Up * 1.5f, amount: 14, lifetime: 2.5f, texture: Particle("smoke_04"), new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 25,
            InitialVelocityMin = 2,
            InitialVelocityMax = 4,
            Gravity = new Vector3(0, 0.4f, 0),
            DampingMin = 1,
            DampingMax = 2,
            ScaleMin = 1,
            ScaleMax = 2.5f,
            ScaleCurve = Grow(0.3f),
            ColorRamp = Ramp(Steam with { A = 0 }, Steam with { A = 0.5f }, Steam with { A = 0 }),
        }, additive: false);
    }
}
