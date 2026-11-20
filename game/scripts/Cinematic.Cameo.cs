using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Glimpses of Wired's own machines (Sarge, Old Iron, Thresher, Harvester, Widow, Colossus, Hornet, Marshal, Titan) in the
// background of the battles, to make the trailer's viewers curious. They belong to a side of their own that is at peace
// with everyone, painted in the colours of the army they stand with: nobody shoots at them and they shoot at nobody, so
// the battles play as they did without them (each film's log of blasts and kills stays the same).
public partial class Cinematic
{
    /// <summary>The side the glimpses belong to (no film has yellow players).</summary>
    private const Race Guest = Race.Yellow;

    /// <summary>A machine of Wired's in the background: where it stands, and where it walks to, from when.</summary>
    private sealed record Cameo(string Type, string Sub, int Level, string Combo, float X, float Y, float? ToX = null, float? ToY = null, float From = 0.5f, float Facing = 0);

    private readonly List<int> _cameos = [];

    /// <summary>Sets the glimpses down before the first tick, in the colours of <paramref name="look"/>.</summary>
    private void Cameos(Race look, params Cameo[] cast)
    {
        if (cast.Length == 0 || System.Environment.GetEnvironmentVariable("MACHINES_CINEMATIC_CAMEOS") == "0")
            return;
        var guest = new RaceState(Guest, Controller.Player);
        foreach (var race in _world.Races.Keys)
        {
            guest.Allies.Add(race);
            _world.Races[race].Allies.Add(Guest);
        }
        _world.Races[Guest] = guest;
        var request = MatchRequest.Current;
        var colours = request.Starts.Colours.ToDictionary(c => c.Key, c => c.Value);
        colours[Guest] = look;
        MatchRequest.Current = request with { Starts = request.Starts with { Colours = colours } };
        foreach (var cameo in cast)
        {
            var spot = _world.Nav.NearestFreePoint(new NVector2(cameo.X, cameo.Y), crossesWater: false) ?? new NVector2(cameo.X, cameo.Y);
            var machine = _world.AddMachine(Guest, spot, cameo.Type, cameo.Sub, cameo.Level, cameo.Combo is "" ? null : cameo.Combo);
            machine.Facing = cameo.Facing;
            // It never looks about for targets: every look rolls the world's dice, and the battle would play differently.
            machine.Alert.NextLook = float.MaxValue;
            _cameos.Add(machine.Id);
            GD.Print($"cinematic: cameo {cameo.Type} {cameo.Sub} {cameo.Level} #{machine.Id} at {spot.X:0},{spot.Y:0}");
            if (cameo is { ToX: { } x, ToY: { } y })
                At(cameo.From, () => _world.Issue(new MoveCommand(Guest, [machine.Id], new NVector2(x, y))));
        }
    }
}
