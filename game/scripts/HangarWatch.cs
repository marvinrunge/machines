namespace Machines.Game;

/// <summary>What the hangar's screen has to do after a look at the sim.</summary>
public enum HangarMove
{
    Stay,
    TakeDown,

    /// <summary>The sim's hangar stands open with no screen over it: it is closed.</summary>
    Close,
}

/// <summary>
/// Keeps the hangar's screen in step with the sim, which hears of the player a tick or more later and has the last word. It
/// goes by how often the sim has opened the hangar (<see cref="Machines.Sim.Survival.WiredState.HangarOpens"/>) and not by how
/// that stands at one look: an opening and a closing may both come between two frames, and a closing can still be on its way
/// when the next screen goes up. Free of Godot, so the tests can compile it. Wired is the remake's own addition.
/// </summary>
public sealed class HangarWatch
{
    private int _awaited;
    private bool _closing;

    /// <summary>A screen goes up: the opening that belongs to it is the next one after <paramref name="opens"/> and those on their way.</summary>
    public void Opening(int opens)
    {
        _awaited = Math.Max(_awaited, opens) + 1;
        _closing = false;
    }

    /// <summary>The player took the screen down, and the closing is on its way.</summary>
    public void Closing() => _closing = true;

    /// <summary>The world said no to an opening.</summary>
    public void Refused() => _awaited--;

    /// <summary>Whether no opening is still on its way.</summary>
    public bool Answered(int opens) => opens >= _awaited;

    /// <summary>A look at the sim: whether a screen is up, how the hangar stands and how often it has been opened.</summary>
    public HangarMove Follow(bool screen, bool open, int opens)
    {
        if (screen)
            return Answered(opens) && !open ? HangarMove.TakeDown : HangarMove.Stay;
        if (!open)
        {
            _closing &= !Answered(opens);
            return HangarMove.Stay;
        }
        if (_closing)
            return HangarMove.Stay;
        _closing = true;
        return HangarMove.Close;
    }
}
