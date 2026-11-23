using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// Package E's view of the escape's pack (plan stufe7 §4 E, design-escape §13 "View"): a member the hazards stagger (a
// container's blow, a plate, a jet) shudders, an amber light flickering on it as it sparks; one the jets set alight burns
// for a few seconds after (the cue "pack fire"), and the Thresher while it stands in a burning jet's band. The sim's
// stagger and fire are its own (StoryHazards.Staggered, StoryHazards.Burns); the view only shows them.
public partial class ActorView
{
    /// <summary>The world the escape's views read their machines' states from (set by Game.StoryEscape each frame of section 7).</summary>
    internal static GameWorld? EscapeWorld { get; set; }

    /// <summary>Until when (game time, seconds) a pack member burns after a jet caught it, by id (Game.StoryEscape's "pack fire").</summary>
    internal static readonly Dictionary<int, float> EscapeBurning = [];

    /// <summary>How long a pack member burns after a jet caught it, seconds.</summary>
    internal const float EscapeBurnSeconds = 3;

    private bool _escapeStaggered, _escapeBurns;
    private OmniLight3D? _escapeLamp;
    private GpuParticles3D? _escapeFire;
    private double _escapeClock;

    /// <summary>What the escape's hazards do to this machine at the sim's tick: staggered, burning.</summary>
    partial void StorySyncEscape(Actor actor)
    {
        if (EscapeWorld is not { Story: { Section: 7 } run } world || world.Find<Actor>(actor.Id) != actor || actor is not Machine machine)
        {
            _escapeStaggered = _escapeBurns = false;
            return;
        }
        var hazards = run.Hazards;
        _escapeStaggered = hazards.Staggered.TryGetValue(actor.Id, out var until) && until > world.Tick;
        _escapeBurns = EscapeBurning.TryGetValue(actor.Id, out var burns) && burns > world.Time
            || run.Pack.Members.TryGetValue(actor.Id, out var kind) && kind == PackKind.Thresher && InBurningJet(world, hazards, machine);
    }

    /// <summary>Whether the machine stands in the band of a jet that burns now.</summary>
    private static bool InBurningJet(GameWorld world, StoryHazards hazards, Machine machine)
    {
        if (world.Deck is not { } deck || hazards.JetsFrom < 0)
            return false;
        for (var jet = 1; jet <= 5; jet++)
            if (hazards.Burns(jet, world.Tick) && deck.Zone($"jet_j{jet}") is { } zone && GameWorld.Overlaps(zone, machine.Position, machine.Radius))
                return true;
        return false;
    }

    partial void StoryFrameEscape(double delta)
    {
        if (!_escapeStaggered && !_escapeBurns)
        {
            if (_escapeLamp is not null)
                _escapeLamp.Visible = false;
            if (_escapeFire is { Emitting: true })
                _escapeFire.Emitting = false;
            return;
        }
        _escapeClock += delta;
        if (_escapeLamp is null)
        {
            _escapeLamp = new OmniLight3D { Name = "EscapeLamp", OmniRange = 8, ShadowEnabled = false, Position = new Vector3(0, 3, 0) };
            AddChild(_escapeLamp);
        }
        var t = (float)_escapeClock;
        _escapeLamp.Visible = true;
        if (_escapeBurns)
            (_escapeLamp.LightColor, _escapeLamp.LightEnergy) = (new Color(1, 0.5f, 0.15f), 3 + Mathf.Sin(t * 19) + 0.5f * Mathf.Sin(t * 43));
        else
            // Staggered: an amber flicker, the sparks of a machine shaken out of its stride.
            (_escapeLamp.LightColor, _escapeLamp.LightEnergy) = (new Color(1, 0.7f, 0.25f), Mathf.Sin(t * 41) > 0.2f ? 2.5f : 0.3f);
        if (_escapeBurns && _escapeFire is null && Effects.DamageFire(2.5f, 4) is { } fire)
        {
            fire.Position = new Vector3(0, 1, 0);
            AddChild(fire);
            _escapeFire = fire;
        }
        if (_escapeFire is not null && _escapeFire.Emitting != _escapeBurns)
            _escapeFire.Emitting = _escapeBurns;
    }
}
