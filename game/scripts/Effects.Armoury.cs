using Godot;

namespace Machines.Game;

// Package W's effects (plan stufe7 §4 W): the dust the heavy Grunt's charge kicks up, white and amber, never red (red is the
// enemy's colour, design-waves §6).
public partial class Effects
{
    private static readonly Color Dust = new(0.75f, 0.72f, 0.66f), Amber = new(1, 0.72f, 0.25f);

    /// <summary>Dust and grit kicked up by the heavy Grunt's wheels: low, outward, a little amber in it.</summary>
    public void ChargeDust(Vector3 at, int amount)
    {
        SparkShower(at + Vector3.Up * 0.4f, Vector3.Up, amount, Dust);
        SparkShower(at + Vector3.Up * 0.8f, Vector3.Up, amount / 3, Amber);
    }
}
