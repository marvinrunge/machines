using Godot;

namespace Machines.Game;

// Package W's lights (plan stufe7 §4 W, design-waves §4.1 and §4.11): when the armoury bolts shut and the Notabschaltung comes
// on, and again when section six loses its power at the end, the room's lamps dip and flicker back. Only the view: the sim's
// light groups (GameWorld.StoryLightState) stay as they are, and a group the story switches meanwhile keeps its new state.
public partial class DeckView
{
    private Tween? _dip;
    private string[] _dipped = [];

    /// <summary>
    /// The lamps of <paramref name="groups"/> fall to <paramref name="floor"/> of their light at once and flicker back to
    /// full over <paramref name="seconds"/>; a dip still running ends first. Groups this deck has not are left out.
    /// </summary>
    public void Dip(IEnumerable<string> groups, float seconds, float floor)
    {
        EndDip();
        _dipped = [.. groups.Where(_groups.ContainsKey).Distinct()];
        if (_dipped.Length == 0 || seconds <= 0)
            return;
        _dip = CreateTween();
        _dip.TweenMethod(Callable.From<float>(t => DipAt(t, seconds, floor)), 0f, seconds, seconds);
        _dip.TweenCallback(Callable.From(DipDone));
    }

    /// <summary>The dip's light at <paramref name="t"/> seconds: down at once, then back by stuttering steps, steady at the end.</summary>
    private void DipAt(float t, float seconds, float floor)
    {
        const float fall = 0.08f;
        if (t < fall)
        {
            DipLight(Mathf.Lerp(1, floor, t / fall));
            return;
        }
        // Back from a third of the way, the relays catching and dropping again the while (two beats, so it never looks even).
        var back = Mathf.SmoothStep(seconds * 0.3f, seconds * 0.95f, t);
        var caught = Mathf.Sin(t * 41) + Mathf.Sin(t * 23.7f + 1.3f) > 0.35f;
        var level = Mathf.Lerp(floor, 1, back);
        DipLight(caught || back >= 0.97f ? level : Mathf.Lerp(floor * 0.5f, level, back * back));
    }

    private void DipLight(float share)
    {
        foreach (var name in _dipped)
        {
            var group = _groups[name];
            foreach (var light in group.Ceiling)
                light.LightEnergy = LampEnergy * share;
            foreach (var light in group.Wall)
                light.LightEnergy = EmergencyEnergy * _gloom * share;
            group.Fixture.EmissionEnergyMultiplier = 3 * share;
            group.Glass.EmissionEnergyMultiplier = 4 * Mathf.Max(_gloom, 0.5f) * share;
        }
    }

    private void EndDip()
    {
        if (_dip is { } dip && dip.IsValid())
            dip.Kill();
        DipDone();
    }

    private void DipDone()
    {
        DipLight(1);
        _dipped = [];
        _dip = null;
    }
}
