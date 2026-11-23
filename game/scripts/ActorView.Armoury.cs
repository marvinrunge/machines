using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// Package W's view of the armoury's machines (plan stufe7 §4 W): the heavy Grunt's states on G's HeavyGruntRig (its headlamps
// flaring white as it revs, amber sparks and a flicker while it stands crashed with its radiator open) and an electric
// unit's crackle as it closes on the trio (a blue flicker before the strike). Warnings in white, amber and blue: red is the
// enemy's own colour (design-waves §6).
public partial class ActorView
{
    /// <summary>The world the armoury's views read their machines' states from (set by Game.StoryArmoury with each of its cues).</summary>
    internal static GameWorld? ArmouryWorld { get; set; }

    private enum ArmouryLook { None, Rev, Stunned, Crackle }

    private ArmouryLook _armouryLook;
    private OmniLight3D? _armouryLamp;
    private double _armouryClock;

    /// <summary>What the armoury makes of this machine at the sim's tick: the boss's phase, an electric unit near the trio.</summary>
    partial void StorySyncArmoury(Actor actor)
    {
        if (ArmouryWorld is not { Story: { Section: 6 } run } world || world.Find<Actor>(actor.Id) != actor)
        {
            _armouryLook = ArmouryLook.None;
            return;
        }
        var waves = run.Waves;
        _armouryLook = ArmouryLook.None;
        if (waves.Boss == actor.Id)
            _armouryLook = waves.Phase switch
            {
                StoryChargePhase.Telegraph or StoryChargePhase.Run => ArmouryLook.Rev,
                StoryChargePhase.Stunned => ArmouryLook.Stunned,
                _ => ArmouryLook.None,
            };
        else if (waves.Foes.TryGetValue(actor.Id, out var foe) && foe.Kind is StoryWaveKind.Zap or StoryWaveKind.TankZap && actor is Machine zap
            && run.Seats.Any(s => world.HeroOf(s) is { } hero && System.Numerics.Vector2.Distance(hero.Position, zap.Position) < 16))
            _armouryLook = ArmouryLook.Crackle;
    }

    partial void StoryFrameArmoury(double delta)
    {
        if (_armouryLook == ArmouryLook.None)
        {
            if (_armouryLamp is not null)
                _armouryLamp.Visible = false;
            return;
        }
        _armouryClock += delta;
        if (_armouryLamp is null)
        {
            _armouryLamp = new OmniLight3D { Name = "ArmouryLamp", OmniRange = 9, ShadowEnabled = false };
            AddChild(_armouryLamp);
        }
        _armouryLamp.Visible = true;
        var t = (float)_armouryClock;
        switch (_armouryLook)
        {
            case ArmouryLook.Rev:
                // The headlamps flare up white and pulse with the engine's rising roar, out in front.
                (_armouryLamp.LightColor, _armouryLamp.LightEnergy) = (new Color(1, 0.97f, 0.9f), 4 + 3 * Mathf.Abs(Mathf.Sin(t * 14)));
                _armouryLamp.Position = new Vector3(0, 3.5f, 0);
                break;
            case ArmouryLook.Stunned:
                (_armouryLamp.LightColor, _armouryLamp.LightEnergy) = (new Color(1, 0.7f, 0.25f), Mathf.Sin(t * 37) > 0.3f ? 3 : 0.4f);
                _armouryLamp.Position = new Vector3(0, 5, 0);
                break;
            default:
                (_armouryLamp.LightColor, _armouryLamp.LightEnergy) = (new Color(0.45f, 0.7f, 1), Mathf.Sin(t * 53) > 0 ? 2.5f : 0.3f);
                _armouryLamp.Position = new Vector3(0, 2.5f, 0);
                break;
        }
    }
}
