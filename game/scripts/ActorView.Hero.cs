using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

// A Wired hero's model is put together from what it wears (see HeroRig). Only a hero gets here: every other machine has
// its rig by its name, once. Wired is the remake's own addition.
public partial class ActorView
{
    private int _refits;

    /// <summary>Whether a machine was refitted since this view last showed it.</summary>
    public bool Refitted(Machine machine) => machine.Refits != _refits;

    /// <summary>
    /// How a machine's legs keep pace with the ground: its walk is clocked by its top speed, a hero's by its share of the
    /// original's (<see cref="WiredMachines.GaitShare"/>), since a hero may be tuned faster than the walk of its model was
    /// made for – the crab runs 16 m/s on legs made for 6. Such a hero's walk may then run up to twice as fast as any
    /// other's, so that its legs run rather than skate; every other machine, a crab of a normal match too, as before.
    /// </summary>
    private void Gait(Machine machine, bool hero)
    {
        var share = hero ? WiredMachines.GaitShare(machine.Spec.TypeName, machine.Stats.HardwareLevel) : 1;
        _maxSpeed = machine.Stats.Speed * share;
        _fastestWalk = share < 1 ? 2 * RigView.FastestWalk : RigView.FastestWalk;
    }

    /// <summary>
    /// Shows what a hero is since the hangar refitted it: a new rig and all that hangs on its size. The view stays, so
    /// the selection, what burns and smokes on it and the driver's seat stay as well.
    /// </summary>
    public void Refit(Machine machine, HeroLoadout hero, ClassicAssets assets, GameAudio audio)
    {
        _refits = machine.Refits;
        var look = Game.Look(machine.ShownRace);
        if (_rigView is null || HeroRig.Compose(assets, look, hero) is not { } rig)
            return;
        var turned = _rigView.UpperBody;
        RemoveChild(_rigView);
        _rigView.QueueFree();
        var burning = _flamesLeft > 0;
        _topFlames.Clear();

        var (id, size) = ModelOf(machine);
        Size = Mathf.Max(size.X, size.Z);
        _rigView = RigView.Create(rig);
        _rigView.UpperBody = turned;
        Gait(machine, hero: true);
        if (machine.Stats.BodyTurnRate is > 0 and var bodyRate)
            _rigView.TurnRate = Mathf.DegToRad(bodyRate);
        AddChild(_rigView);
        _model = _rigView;
        Measure(machine, _rigView, _rigView.Transform);

        BuildMarker();
        if (_plate is not null)
            _plate.Mesh = assets.PaintedShadow(id, look);
        if (_lamp is not null)
            _lamp.Position = _lamp.Position with { X = Mathf.Max(0.5f, _fore) };
        if (_followsGround && _dust is null && GraphicsSettings.Effects)
            AddChild(_dust = Effects.DustTrail());
        else if (!_followsGround && _dust is not null)
        {
            _dust.QueueFree();
            _dust = null;
        }
        if (_dust is not null)
            _dust.Position = new Vector3(_aft * 0.6f, 0.3f, 0);
        _health.Position = new Vector3(0, size.Y + 2.5f, 0);
        _armour.Position = new Vector3(0, size.Y + 3.1f, 0);
        var top = new Vector3(_bounds.GetCenter().X, _bounds.End.Y * 0.9f, _bounds.GetCenter().Z);
        foreach (var emitter in new[] { _damageSmoke, _thickSmoke, _damageFire })
            if (emitter is not null)
                emitter.Position = top;

        if (_engine is not null)
        {
            RemoveChild(_engine.Player);
            _engine.Player.QueueFree();
        }
        _engine = audio.EngineFor(machine);
        if (_engine is not null)
            AddChild(_engine.Player);

        // What lay on the old rig goes on the new one.
        var darkness = _darkness;
        _darkness = 0;
        Darken(darkness);
        if (burning)
            LightTopFlames();
        if (_beam is not null)
            foreach (var mesh in Shape())
                mesh.MaterialOverride = _beam;
        if (_driven)
            SetLayers(this, DrivenLayer);
    }
}
