using Godot;

namespace Machines.Game;

// The trio's own faint glow in a story (user's wish after the first play test): a Grunt or L-7 whose lamp is off still gives
// a dim, short light around itself (its running lights, its warm motor), so the deck right about it is never pitch black.
// The view's only: the sim's sight goes by the lamp and the signature, not by this.
public partial class ActorView
{
    /// <summary>Whether a machine is one of the trio's heroes in a running story (Game.StoryStealth sets it each frame); null outside one.</summary>
    internal static Func<int, bool>? TrioMember { get; set; }

    /// <summary>The glow's reach (metres) and strength; about a tenth of a lamp, gone within a few metres.</summary>
    private const float GlowRange = 9, GlowEnergy = 0.35f;

    private OmniLight3D? _glow;

    partial void StoryFrameGlow(double delta)
    {
        var wanted = TrioMember?.Invoke(ActorId) == true && Visible && PlanetLook.Dark > 0.05f && _lamp is not { Visible: true };
        if (!wanted)
        {
            if (_glow is not null)
                _glow.Visible = false;
            return;
        }
        if (_glow is null)
        {
            _glow = new OmniLight3D
            {
                Name = "Glow",
                OmniRange = GlowRange,
                OmniAttenuation = 1.6f,
                LightColor = new Color(0.75f, 0.8f, 0.9f),
                ShadowEnabled = false,
                Position = Vector3.Up * 2.5f,
            };
            AddChild(_glow);
        }
        _glow.LightEnergy = GlowEnergy * PlanetLook.Dark;
        _glow.Visible = true;
    }
}
